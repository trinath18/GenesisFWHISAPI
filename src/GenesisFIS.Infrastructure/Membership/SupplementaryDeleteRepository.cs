using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

internal sealed class SupplementaryDeleteRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ISupplementaryDeleteRepository
{
    private static DateTime At(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    private sealed record Principal(string? Name, string? Policy, string? Ic, string? Plan, string? Status, string? TopupNumber, DateTime? Expiry);

    private static (string Field, string Message)? Validate(SupplementaryDeleteRequest r, bool needEndorsement)
    {
        if (!AdjustmentRules.IsValidMember(r.MemberNumber)) return ("memberNumber", "Please enter a valid membership number.");
        if (!CardSendingRules.IsValidCoverId(r.CoverId)) return ("coverId", "Please enter a valid cover id (2 digits).");
        if (!needEndorsement) return null;
        var min = new DateOnly(1900, 1, 1);
        if (r.EffectiveDate is null || r.EffectiveDate < min) return ("effectiveDate", "Please enter the endorsement effective date.");
        if (r.EndorsementBordxDate is null || r.EndorsementBordxDate < min) return ("endorsementBordxDate", "Please enter the endorsement bordx date.");
        if (r.BatchNo is null or < 0 or > 32767) return ("batchNo", "Please enter a batch number between 0 and 32767.");
        return null;
    }

    private static async Task<(Principal P, SupplementaryDeleteQuote Q)> LoadAsync(IDbSession session, string number, string cover, CancellationToken ct)
    {
        var m = (await session.QueryAsync("SELECT MBMName, MBMPolicyNo, MBMIcBcPp, PLNCode, MBMStatus, MBMTopupNumber, MBMPayorExpDate FROM MBM WHERE MBMNumber = @number", new { number }, ct)).FirstOrDefault()
            ?? throw BusinessRuleException.NotFound($"Member '{number}' does not exist.");
        var c = (await session.QueryAsync("SELECT MBMCName, MBMCStatus FROM MBMCoveredPersons WHERE MBMCNumber = @number AND MBMCCoverID = @cover", new { number, cover }, ct)).FirstOrDefault()
            ?? throw BusinessRuleException.NotFound($"Cover {cover} of member '{number}' does not exist.");
        var p = new Principal(S(m, "MBMName"), S(m, "MBMPolicyNo"), S(m, "MBMIcBcPp"), S(m, "PLNCode"), S(m, "MBMStatus"), S(m, "MBMTopupNumber")?.Trim(), D(m, "MBMPayorExpDate"));
        async Task<int> Count(string sql, object args) => await session.ScalarAsync<int>(sql, args, ct);
        var topup = string.IsNullOrWhiteSpace(p.TopupNumber) ? number : p.TopupNumber;
        var q = new SupplementaryDeleteQuote(number, cover, S(c, "MBMCName"), S(c, "MBMCStatus")?.Trim(),
            Cases: await Count("SELECT COUNT(*) FROM CAS WHERE MBMNumber = @number AND MBMPatientCovID = @cover", new { number, cover }),
            RefundRows: await Count("SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @number AND MBMCovID = @cover", new { number, cover }),
            LifetimeRows: await Count("SELECT COUNT(*) FROM MBMLifeTime WHERE (MBMNumber = @number OR MBMNumber = @topup) AND MBMCovID = @cover", new { number, topup, cover }),
            ClinicRows: await Count("SELECT COUNT(*) FROM MBMCoveredPersonsCLN WHERE MBMCNumber = @number AND MBMCCoverID = @cover", new { number, cover }),
            BenefitLimitRows: await Count("SELECT COUNT(*) FROM MBMBnfLmt WHERE MBMNumber = @number AND MBMCoveredID = @cover", new { number, cover }));
        return (p, q);
    }

    public async Task<SupplementaryDeleteQuote> QuoteAsync(SupplementaryDeleteRequest request, CancellationToken ct = default)
    {
        if (Validate(request, needEndorsement: false) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        await using var session = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        return (await LoadAsync(session, request.MemberNumber!.Trim(), request.CoverId!.Trim(), ct)).Q;
    }

    public async Task<SupplementaryDeleteResult> DeleteAsync(SupplementaryDeleteRequest request, string user, CancellationToken ct = default)
    {
        if (Validate(request, needEndorsement: true) is { } v) throw new BusinessRuleException(v.Message, field: v.Field);
        var number = request.MemberNumber!.Trim();
        var cover = request.CoverId!.Trim();
        var eff = At(request.EffectiveDate!.Value);
        var bordx = At(request.EndorsementBordxDate!.Value);
        var today = DateTime.Today;
        SupplementaryDeleteQuote q;
        int deleted;

        await using (var session = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            var (p, quote) = await LoadAsync(session, number, cover, ct);
            q = quote;
            if (p.Status is "C" or "D") throw BusinessRuleException.Conflict("The membership is cancelled or deleted.");
            if (q.BlockedReason is { } reason) throw BusinessRuleException.Conflict($"Cover {cover} cannot be deleted: it {reason}. Cancel it instead; nothing was deleted.");

            // The complete records go to the audit log before anything is changed.
            var one = await session.QueryAsync("SELECT * FROM MBMCoveredPersons WHERE MBMCNumber = @number AND MBMCCoverID = @cover", new { number, cover }, ct);
            var two = await session.QueryAsync("SELECT * FROM MBMCoveredPersonsTwo WHERE MBMCNumber = @number AND MBMCCoverID = @cover", new { number, cover }, ct);
            var cln = await session.QueryAsync("SELECT * FROM MBMCoveredPersonsCLN WHERE MBMCNumber = @number AND MBMCCoverID = @cover", new { number, cover }, ct);
            var bnf = await session.QueryAsync("SELECT * FROM MBMBnfLmt WHERE MBMNumber = @number AND MBMCoveredID = @cover", new { number, cover }, ct);
            audit.Record(new AuditEntry("Member.DeleteSupplementaryIntent", "MBMCoveredPersons", $"{number}/{cover}", user, new Dictionary<string, object?>
                { ["coveredPerson"] = one, ["coveredPersonTwo"] = two, ["clinic"] = cln, ["benefitLimits"] = bnf }));

            // Write2MBMnHistory(current status): status unchanged, last endorsement T, history row about the principal.
            if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @number", new { number }, ct) == 0)
                await session.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@number)", new { number }, ct);
            await session.ExecuteAsync("UPDATE MBMTwo SET MBMLastEndorseStatus = 'T', MBMENDtEffDate = @eff, MBMENDtBordxDate = @bordx WHERE MBMNumber = @number", new { number, eff, bordx }, ct);
            await session.ExecuteAsync("""
                INSERT INTO MBMAmendHistory (MBMNumber, MBMPolicyNo, MBMIcBcPp, MBMName, MBMExpiryDate, MBMPlanCode, MBMAmendEdtType, MBMAEndtBordxDate, MBMAEndtEffDate, MBMAmendUser, MBMAmendDate, MBMAEndtBatchNo)
                VALUES (@number, @policy, @ic, @name, @expiry, @plan, 'T', @bordx, @eff, @user, @today, @batch)
                """, new { number, policy = p.Policy, ic = p.Ic, name = p.Name, expiry = p.Expiry, plan = p.Plan, bordx, eff, user, today, batch = (short)request.BatchNo!.Value }, ct);

            // DeleteSupplementary: the covered person and its dependent rows.
            deleted = await session.ExecuteAsync("DELETE FROM MBMCoveredPersons WHERE MBMCNumber = @number AND MBMCCoverID = @cover", new { number, cover }, ct);
            deleted += await session.ExecuteAsync("DELETE FROM MBMCoveredPersonsTwo WHERE MBMCNumber = @number AND MBMCCoverID = @cover", new { number, cover }, ct);
            deleted += await session.ExecuteAsync("DELETE FROM MBMCoveredPersonsCLN WHERE MBMCNumber = @number AND MBMCCoverID = @cover", new { number, cover }, ct);
            deleted += await session.ExecuteAsync("DELETE FROM MBMBnfLmt WHERE MBMNumber = @number AND MBMCoveredID = @cover", new { number, cover }, ct);
            await session.CommitAsync(ct);
        }

        audit.Record(new AuditEntry("Member.DeleteSupplementary", "MBMCoveredPersons", $"{number}/{cover}", user, new Dictionary<string, object?>
            { ["quote"] = q, ["rowsDeleted"] = deleted, ["bordx"] = request.EndorsementBordxDate, ["effective"] = request.EffectiveDate, ["batch"] = request.BatchNo }));
        return new SupplementaryDeleteResult(q, deleted);
    }
}
