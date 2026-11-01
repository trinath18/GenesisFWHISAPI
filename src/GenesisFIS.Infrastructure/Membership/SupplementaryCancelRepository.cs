using GenesisFIS.Core.Data;
using GenesisFIS.Core.Fees;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

internal sealed class SupplementaryCancelRepository(IDbConnectionFactory connections, IAuditLog audit, IFeeRateSource rates) : SqlRepository(connections), ISupplementaryCancelRepository
{
    private sealed record Loaded(string Number, string? Name, string? Policy, string? Status, string? Insurer, string? Health, string? Payor, string? Plan, DateOnly Eff, DateOnly Exp, DateTime? BordxDate,
        string CoverId, string? CoverName, string? CoverIc, string? CoverStatus, string? CoverPlan, DateOnly CoverEff, DateOnly CoverExp, decimal Premium, decimal Mco);

    private static DateOnly? Day(IDictionary<string, object?> r, string name) => D(r, name) is { } d ? DateOnly.FromDateTime(d) : null;
    private static DateTime At(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
    private static string T(string? s) => s?.Trim() ?? "";

    private static async Task<Loaded> LoadAsync(IDbSession session, string number, string cover, CancellationToken ct)
    {
        var m = (await session.QueryAsync("SELECT MBMNumber, MBMName, MBMPolicyNo, MBMStatus, INSCode, HLTCode, PAYCode, PLNCode, MBMPayorEffDate, MBMPayorExpDate, MBMBordxDate FROM MBM WHERE MBMNumber = @number",
            new { number }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound($"Member '{number}' does not exist.");
        if (Day(m, "MBMPayorEffDate") is not { } eff || Day(m, "MBMPayorExpDate") is not { } exp) throw new BusinessRuleException("The member has no payor effective/expiry dates.");
        var c = (await session.QueryAsync("""
            SELECT MBMCCoverID, MBMCName, MBMCICBCPPNo, MBMCStatus, MBMCPLNCode, MBMCEffDate, MBMCExpDate, ISNULL(MBMCPremium, 0) AS Premium, ISNULL(MBMCMCO, 0) AS Mco
            FROM MBMCoveredPersons WHERE MBMCNumber = @number AND MBMCCoverID = @cover
            """, new { number, cover }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound($"Cover {cover} of member '{number}' does not exist.");
        return new Loaded(S(m, "MBMNumber") ?? number, S(m, "MBMName"), S(m, "MBMPolicyNo"), S(m, "MBMStatus"), S(m, "INSCode"), S(m, "HLTCode"), S(m, "PAYCode"), S(m, "PLNCode"), eff, exp, D(m, "MBMBordxDate"),
            cover, S(c, "MBMCName"), S(c, "MBMCICBCPPNo"), S(c, "MBMCStatus"), S(c, "MBMCPLNCode"), Day(c, "MBMCEffDate") ?? eff, Day(c, "MBMCExpDate") ?? exp, Dec(c, "Premium") ?? 0, Dec(c, "Mco") ?? 0);
    }

    private async Task<SupplementaryCancelQuote> BuildAsync(IDbSession session, Loaded m, DateOnly cancel, CancellationToken ct)
    {
        if (m.Status is "C" or "D") throw BusinessRuleException.Conflict("The membership is cancelled or deleted.");
        if (T(m.CoverStatus) is "C" or "K" or "D") throw BusinessRuleException.Conflict($"Cover {m.CoverId} is already cancelled or deleted.");
        if (cancel < m.CoverEff || cancel > m.CoverExp) throw new BusinessRuleException($"The cancellation date must be within the cover period ({m.CoverEff:dd/MM/yyyy} - {m.CoverExp:dd/MM/yyyy}).", field: "effectiveDate");

        // MBMSuppMCORefundCal: the supplementary MCO status of the cover's plan decides whether a refund row is written at all.
        var plan = T(m.CoverPlan) is { Length: > 0 } cp ? cp : T(m.Plan);
        var status = (await session.QueryAsync("""
            SELECT TOP 1 SuppMCOStatus FROM MCO WHERE HLTCode = @health AND INSCode = @ins AND MCOEffDate <= @eff AND (PLNCode = @plan OR PLNCode IS NULL)
            ORDER BY CASE WHEN PLNCode = @plan THEN 0 ELSE 1 END, MCOEffDate DESC
            """, new { health = T(m.Health).Length > 1 ? T(m.Health)[..1] : T(m.Health), ins = T(m.Insurer), eff = At(m.CoverEff), plan }, ct)).Select(r => T(S(r, "SuppMCOStatus"))).FirstOrDefault();
        var mcoStatus = string.IsNullOrEmpty(status) ? "A" : status;
        var write = (mcoStatus == "B" && m.CoverId == "01") || mcoStatus is "C" or "D";

        var flags = await rates.GetPlanFlagsAsync(new FeeRequest(T(m.Insurer), T(m.Health), T(m.Payor), T(m.Plan), "", "", null, m.Eff, m.Eff, m.Exp, false), ct);
        var refund = write ? RefundMath.Compute(m.CoverExp, cancel, m.CoverEff, m.Premium, m.Mco, flags.RoundUp, wholeInputs: false) : new RefundAmounts(0, 0, 0, 0);
        return new SupplementaryCancelQuote(m.Number, m.CoverId, m.CoverName, m.CoverStatus, m.CoverEff, m.CoverExp, cancel, m.Premium, m.Mco, mcoStatus, write, refund);
    }

    public async Task<SupplementaryCancelQuote> QuoteAsync(SupplementaryCancelRequest request, CancellationToken ct = default)
    {
        if (SupplementaryCancelRules.Validate(request, needEndorsement: false) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        await using var session = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        return await BuildAsync(session, await LoadAsync(session, request.MemberNumber!.Trim(), request.CoverId!.Trim(), ct), request.EffectiveDate!.Value, ct);
    }

    public async Task<SupplementaryCancelResult> CancelAsync(SupplementaryCancelRequest request, string user, CancellationToken ct = default)
    {
        if (SupplementaryCancelRules.Validate(request, needEndorsement: true) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var number = request.MemberNumber!.Trim();
        var cover = request.CoverId!.Trim();
        var cancel = request.EffectiveDate!.Value;
        var bordx = At(request.EndorsementBordxDate!.Value);
        var today = DateTime.Today;
        SupplementaryCancelQuote q;

        await using (var session = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            var m = await LoadAsync(session, number, cover, ct);
            q = await BuildAsync(session, m, cancel, ct);

            // Write2MBMnHistory("K"): the principal's status is not touched; last endorsement K and a history row about the covered person.
            if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @number", new { number }, ct) == 0)
                await session.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@number)", new { number }, ct);
            await session.ExecuteAsync("UPDATE MBMTwo SET MBMLastEndorseStatus = 'K', MBMENDtEffDate = @eff, MBMENDtBordxDate = @bordx WHERE MBMNumber = @number", new { number, eff = At(cancel), bordx }, ct);
            await session.ExecuteAsync("""
                INSERT INTO MBMAmendHistory (MBMNumber, MBMPolicyNo, MBMIcBcPp, MBMName, MBMExpiryDate, MBMPlanCode, MBMAmendEdtType, MBMAEndtBordxDate, MBMAEndtEffDate, MBMAmendUser, MBMAmendDate, MBMAEndtBatchNo)
                VALUES (@number, @policy, @ic, @name, @expiry, @plan, 'K', @bordx, @eff, @user, @today, @batch)
                """, new { number, policy = m.Policy, ic = m.CoverIc, name = m.CoverName, expiry = At(m.CoverExp), plan = T(m.CoverPlan) is { Length: > 0 } cp ? cp : m.Plan, bordx, eff = At(cancel), user, today, batch = (short)request.BatchNo!.Value }, ct);

            if (q.RefundWritten)
                await session.ExecuteAsync("""
                    INSERT INTO MBMRefund (MBMNumber, MBMCovID, MBMPREMEffective, MBMPremReduction, MBMMCOEffective, MBMMCORefund, PAYCode, MBMBordxDate, MBMBatchNO, HLTCode)
                    VALUES (@number, @cover, @pe, @pr, @me, @mr, @payor, @bordxDate, @batch, @health)
                    """, new { number, cover, pe = q.Refund.PremiumEffective, pr = q.Refund.PremiumReduction, me = q.Refund.McoEffective, mr = q.Refund.McoRefund, payor = T(m.Payor),
                        bordxDate = m.BordxDate, batch = (short)request.BatchNo!.Value, health = T(m.Health) is { Length: > 0 } h ? h[..1] : null }, ct);

            // WriteAdjStatus2Cov("C", "K", cover): only this cover, unless it is already K or C.
            await session.ExecuteAsync("UPDATE MBMCoveredPersons SET MBMCStatus = 'C', MBMCCancelDate = @cancel WHERE MBMCNumber = @number AND MBMCCoverID = @cover", new { number, cover, cancel = At(cancel) }, ct);
            await session.CommitAsync(ct);
        }

        audit.Record(new AuditEntry("Member.CancelSupplementary", "MBMCoveredPersons", $"{number}/{cover}", user, new Dictionary<string, object?>
            { ["quote"] = q, ["bordx"] = request.EndorsementBordxDate, ["batch"] = request.BatchNo }));
        return new SupplementaryCancelResult(q);
    }
}
