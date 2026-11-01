using GenesisFIS.Core.Data;
using GenesisFIS.Core.Fees;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

internal sealed class ReinstatementRepository(IDbConnectionFactory connections, IAuditLog audit, FeeService fees) : SqlRepository(connections), IReinstatementRepository
{
    private static DateOnly? Day(IDictionary<string, object?> r, string name) => D(r, name) is { } d ? DateOnly.FromDateTime(d) : null;
    private static DateTime At(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    public async Task<ReinstatementMember?> GetAsync(string memberNumber, CancellationToken ct = default)
    {
        if (!ReinstatementRules.IsValidMember(memberNumber)) throw new BusinessRuleException("Please enter a valid membership number.", field: "memberNumber");
        var number = memberNumber.Trim();
        var rows = await QueryAsync(GenesisDb.Main, """
            SELECT m.MBMNumber, m.MBMName, m.MBMPolicyNo, m.MBMStatus, m.MBMPayorEffDate, m.MBMPayorExpDate, m.MBMFinalExpiryDate, m.INSCode, m.PLNCode, m.HLTCode, m.PAYCode,
                   m.AGECode, m.MBMSex, m.MBMAdultChild, m.MBMIcBcPp, t.MBMCancelDate
            FROM MBM m LEFT JOIN MBMTwo t ON t.MBMNumber = m.MBMNumber WHERE m.MBMNumber = @number
            """, new { number }, ct);
        if (rows.Count == 0) return null;
        var r = rows[0];
        var insurer = S(r, "INSCode");
        IReadOnlyList<ReinstatementCovered> covered = [];
        if (insurer is "F" or "H")
            covered = (await QueryAsync(GenesisDb.Main, "SELECT MBMCCoverID, MBMCName, MBMCAdultChild, MBMCAgeband, MBMCSex FROM MBMCoveredPersons WHERE MBMCNumber = @number ORDER BY MBMCCoverID", new { number }, ct))
                .Select(c => new ReinstatementCovered(S(c, "MBMCCoverID") ?? "", S(c, "MBMCName"), S(c, "MBMCAdultChild"), S(c, "MBMCAgeband"), S(c, "MBMCSex"))).ToList();
        return new ReinstatementMember(S(r, "MBMNumber") ?? number, S(r, "MBMName"), S(r, "MBMPolicyNo"), S(r, "MBMStatus"), Day(r, "MBMPayorEffDate"), Day(r, "MBMPayorExpDate"),
            Day(r, "MBMFinalExpiryDate"), Day(r, "MBMCancelDate"), insurer, S(r, "PLNCode"), S(r, "HLTCode"), S(r, "PAYCode"), S(r, "AGECode"), S(r, "MBMSex"), S(r, "MBMAdultChild"),
            S(r, "MBMIcBcPp"), covered);
    }

    private async Task<(ReinstatementMember Member, List<ReinstatementLine> Lines)> PriceAsync(ReinstatementRequest request, CancellationToken ct)
    {
        if (ReinstatementRules.Validate(request) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var member = await GetAsync(request.MemberNumber!, ct) ?? throw BusinessRuleException.NotFound($"Member '{request.MemberNumber}' does not exist.");
        if (member.Status != "C") throw BusinessRuleException.Conflict("Membership is active! Only cancelled memberships can be reinstated.");
        if (member.LapsedDate is null) throw new BusinessRuleException("This member has no lapsed (cancel) date, so the fees cannot be calculated.");
        if (member.ExpiryDate is null) throw new BusinessRuleException("This member has no payor expiry date, so the fees cannot be calculated.");
        if (member.LapsedDate > member.ExpiryDate) throw new BusinessRuleException("The lapsed date is after the payor expiry date; the fees cannot be calculated.");

        var lapsed = member.LapsedDate.Value;
        var expiry = member.ExpiryDate.Value;
        async Task<ReinstatementLine> Price(string cover, string? ageCode, string? adultChild, string? sex)
        {
            try
            {
                var r = await fees.CalculateAsync(new FeeRequest(member.Insurer?.Trim() ?? "", member.Health?.Trim() ?? "", member.Payor?.Trim() ?? "", member.Plan?.Trim() ?? "",
                    ageCode?.Trim() ?? "", adultChild?.Trim() ?? "", sex?.Trim(), lapsed, lapsed, expiry, ApplyDiscounts: false), ct);
                return new ReinstatementLine(cover, r.Premium, r.McoFee);
            }
            catch (FeeRateMissingException ex)
            {
                throw new BusinessRuleException($"Cover {cover}: {ex.Message}");
            }
        }

        var lines = new List<ReinstatementLine> { await Price("00", member.AgeCode, member.AdultChild, member.Sex) };
        if (member.HasSupplementary)
            foreach (var c in member.CoveredPersons) lines.Add(await Price(c.CoverId, c.AgeBand, c.AdultChild, c.Sex));
        return (member, lines);
    }

    public async Task<ReinstatementQuote> QuoteAsync(ReinstatementRequest request, CancellationToken ct = default)
    {
        var (member, lines) = await PriceAsync(request, ct);
        return new ReinstatementQuote(member, lines, lines.Sum(l => l.Premium), lines.Sum(l => l.McoFee));
    }

    public async Task<ReinstatementResult> ReinstateAsync(ReinstatementRequest request, string user, CancellationToken ct = default)
    {
        var (member, lines) = await PriceAsync(request, ct);
        var number = member.MemberNumber;
        var expiry = At(member.ExpiryDate!.Value);
        var bordxDate = At(request.EndorsementBordxDate!.Value);
        var lapsedDate = At(member.LapsedDate!.Value);
        var reinstateDate = At(request.ReinstateDate!.Value);
        var batch = request.BatchNo!.Value.ToString();
        var today = DateTime.Today;

        await using (var session = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            // Re-check inside the transaction: another user may have reinstated the member since the quote.
            var status = (await session.QueryAsync("SELECT MBMStatus FROM MBM WHERE MBMNumber = @number", new { number }, ct)).Select(r => S(r, "MBMStatus")).FirstOrDefault();
            if (status != "C") throw BusinessRuleException.Conflict("Membership is active! Only cancelled memberships can be reinstated.");

            foreach (var line in lines)
                await session.ExecuteAsync("""
                    INSERT INTO MBMReinstatement (MBMNumber, MBMCoverID, MBMEndtBordxDate, MBMMCOFee, MBMPremium, MBMLapsedDate, MBMReinstateDate, LastUpdateUSR, LastUpdateDate, MBMEndtBordxBatch)
                    VALUES (@number, @cover, @bordx, @mco, @premium, @lapsed, @reinstate, @user, @today, @batch)
                    """, new { number, cover = line.CoverId, bordx = bordxDate, mco = line.McoFee, premium = line.Premium, lapsed = lapsedDate, reinstate = reinstateDate, user, today, batch }, ct);

            await session.ExecuteAsync("UPDATE MBM SET MBMStatus = 'A', MBMFinalExpiryDate = @expiry WHERE MBMNumber = @number", new { number, expiry }, ct);
            if (member.HasSupplementary)
                await session.ExecuteAsync("UPDATE MBMCoveredPersons SET MBMCStatus = 'A' WHERE MBMCNumber = @number", new { number }, ct);

            await session.ExecuteAsync("""
                INSERT INTO MBMAmendHistory (MBMNumber, MBMName, MBMIcBcPp, MBMPlanCode, MBMPolicyNo, MBMExpiryDate, MBMAmendUser, MBMAmendEdtType, MBMAmendDate, MBMAEndtBordxDate, MBMAEndtBatchNo, MBMAEndtEffDate)
                VALUES (@number, @name, @ic, @plan, @policy, @expiry, @user, 'R', @today, @bordx, @batch, @reinstate)
                """, new { number, name = member.Name, ic = member.IcNo, plan = member.Plan, policy = member.PolicyNo, expiry, user, today, bordx = bordxDate, batch = (short)request.BatchNo!.Value, reinstate = reinstateDate }, ct);
            await session.CommitAsync(ct);
        }

        // MBMCrossReference is a copy in the maintenance database; it cannot share the transaction. It is idempotent, so a failure is reported and can be repeated.
        var crossReferenceUpdated = true;
        try
        {
            await using var maint = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
            await maint.ExecuteAsync("UPDATE MBMCrossReference SET MBMStatus = 'A', MBMFinalExpiryDate = @expiry WHERE MBMNumber = @number", new { number, expiry }, ct);
            await maint.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            crossReferenceUpdated = false;
        }

        audit.Record(new AuditEntry("Member.Reinstate", "MBM", number, user, new Dictionary<string, object?>
        {
            ["lapsed"] = member.LapsedDate, ["reinstate"] = request.ReinstateDate, ["bordx"] = request.EndorsementBordxDate, ["batch"] = request.BatchNo, ["expiry"] = member.ExpiryDate,
            ["lines"] = lines, ["crossReferenceUpdated"] = crossReferenceUpdated,
        }));
        return new ReinstatementResult(lines.Count, lines.Sum(l => l.Premium), lines.Sum(l => l.McoFee), crossReferenceUpdated);
    }
}

