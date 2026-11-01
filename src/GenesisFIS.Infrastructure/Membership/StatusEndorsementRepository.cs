using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

internal sealed class StatusEndorsementRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IStatusEndorsementRepository
{
    private static DateTime At(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    public async Task<StatusEndorsementResult> ApplyAsync(StatusEndorsementRequest request, string user, CancellationToken ct = default)
    {
        if (StatusEndorsementRules.Validate(request) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var number = request.MemberNumber!.Trim();
        var type = request.Type!;
        var status = StatusEndorsements.NewStatus(type)!;
        var eff = At(request.EffectiveDate!.Value);
        var bordx = At(request.EndorsementBordxDate!.Value);
        var today = DateTime.Today;
        string previous;
        int covered;

        await using (var session = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            var m = (await session.QueryAsync("SELECT MBMName, MBMPolicyNo, MBMIcBcPp, MBMStatus, PLNCode, MBMPayorExpDate FROM MBM WHERE MBMNumber = @number", new { number }, ct)).FirstOrDefault()
                ?? throw BusinessRuleException.NotFound($"Member '{number}' does not exist.");
            previous = S(m, "MBMStatus")?.Trim() ?? "";
            if (previous == "D") throw BusinessRuleException.Conflict("This policy is already deleted.");
            if (type == StatusEndorsements.Suspend && previous is "S" or "C")
                throw BusinessRuleException.Conflict(previous == "S" ? "This membership is already suspended." : "A cancelled membership cannot be suspended.");

            // Write2MBMnHistory: member status, last endorsement, history row. Cancellation date and final expiry are untouched for X and D.
            await session.ExecuteAsync("UPDATE MBM SET MBMStatus = @status WHERE MBMNumber = @number", new { number, status }, ct);
            if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @number", new { number }, ct) == 0)
                await session.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@number)", new { number }, ct);
            await session.ExecuteAsync("UPDATE MBMTwo SET MBMLastEndorseStatus = @type, MBMENDtEffDate = @eff, MBMENDtBordxDate = @bordx WHERE MBMNumber = @number", new { number, type, eff, bordx }, ct);
            await session.ExecuteAsync("""
                INSERT INTO MBMAmendHistory (MBMNumber, MBMPolicyNo, MBMIcBcPp, MBMName, MBMExpiryDate, MBMPlanCode, MBMAmendEdtType, MBMAEndtBordxDate, MBMAEndtEffDate, MBMAmendUser, MBMAmendDate, MBMAEndtBatchNo)
                VALUES (@number, @policy, @ic, @name, @expiry, @plan, @type, @bordx, @eff, @user, @today, @batch)
                """, new { number, policy = S(m, "MBMPolicyNo"), ic = S(m, "MBMIcBcPp"), name = S(m, "MBMName"), expiry = D(m, "MBMPayorExpDate"), plan = S(m, "PLNCode"), type, bordx, eff, user, today, batch = (short)request.BatchNo!.Value }, ct);

            // Every covered person follows the principal (the legacy form sets the status for all listed covered persons).
            covered = await session.ExecuteAsync("UPDATE MBMCoveredPersons SET MBMCStatus = @status WHERE MBMCNumber = @number", new { number, status }, ct);
            await session.CommitAsync(ct);
        }

        var crossOk = true;
        try
        {
            await using var maint = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
            await maint.ExecuteAsync("UPDATE MBMCrossReference SET MBMStatus = @status WHERE MBMNumber = @number", new { number, status }, ct);
            await maint.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { crossOk = false; }

        audit.Record(new AuditEntry(type == StatusEndorsements.Suspend ? "Member.Suspend" : "Member.DeletePolicy", "MBM", number, user, new Dictionary<string, object?>
            { ["previousStatus"] = previous, ["newStatus"] = status, ["effective"] = request.EffectiveDate, ["bordx"] = request.EndorsementBordxDate, ["batch"] = request.BatchNo, ["coveredPersons"] = covered, ["crossReferenceUpdated"] = crossOk }));
        return new StatusEndorsementResult(number, type, previous, status, covered, crossOk);
    }
}
