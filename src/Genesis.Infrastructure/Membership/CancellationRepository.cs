using Genesis.Core.Data;
using Genesis.Core.Fees;
using Genesis.Core.Membership;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Membership;

internal sealed class CancellationRepository(IDbConnectionFactory connections, IAuditLog audit, IFeeRateSource rates) : SqlRepository(connections), ICancellationRepository
{
    private sealed record Loaded(string Number, string? Name, string? Policy, string? Ic, string? Status, string? Insurer, string? Health, string? Payor, string? Plan, DateOnly Eff, DateOnly Exp,
        DateTime? BordxDate, decimal Premium, decimal Mco, int Covered);

    private static DateOnly? Day(IDictionary<string, object?> r, string name) => D(r, name) is { } d ? DateOnly.FromDateTime(d) : null;
    private static DateTime At(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    private static async Task<Loaded> LoadAsync(IDbSession session, string number, CancellationToken ct)
    {
        var r = (await session.QueryAsync("""
            SELECT m.MBMNumber, m.MBMName, m.MBMPolicyNo, m.MBMIcBcPp, m.MBMStatus, m.INSCode, m.HLTCode, m.PAYCode, m.PLNCode, m.MBMPayorEffDate, m.MBMPayorExpDate, m.MBMBordxDate,
                   ISNULL(o.MBMPremium, 0) AS Premium, ISNULL(o.MBMMCOFee, 0) AS Mco,
                   (SELECT COUNT(*) FROM MBMCoveredPersons c WHERE c.MBMCNumber = m.MBMNumber) AS Covered
            FROM MBM m LEFT JOIN MBMOthers o ON o.MBMNumber = m.MBMNumber WHERE m.MBMNumber = @number
            """, new { number }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound($"Member '{number}' does not exist.");
        if (Day(r, "MBMPayorEffDate") is not { } eff || Day(r, "MBMPayorExpDate") is not { } exp)
            throw new BusinessRuleException("The member has no payor effective/expiry dates, so the refund cannot be calculated.");
        return new Loaded(S(r, "MBMNumber") ?? number, S(r, "MBMName"), S(r, "MBMPolicyNo"), S(r, "MBMIcBcPp"), S(r, "MBMStatus"), S(r, "INSCode"), S(r, "HLTCode"), S(r, "PAYCode"), S(r, "PLNCode"),
            eff, exp, D(r, "MBMBordxDate"), Dec(r, "Premium") ?? 0, Dec(r, "Mco") ?? 0, I(r, "Covered") ?? 0);
    }

    private async Task<CancellationQuote> BuildAsync(IDbSession session, Loaded m, DateOnly cancel, CancellationToken ct)
    {
        if (m.Status is "C" or "D") throw BusinessRuleException.Conflict(m.Status == "C" ? "This membership is already cancelled." : "This membership is deleted.");
        if (cancel < m.Eff) throw new BusinessRuleException($"The cancellation date cannot be before the payor effective date ({m.Eff:dd/MM/yyyy}).", field: "cancelDate");
        if (cancel > m.Exp) throw new BusinessRuleException($"The cancellation date cannot be after the payor expiry date ({m.Exp:dd/MM/yyyy}).", field: "cancelDate");

        var probe = new FeeRequest(m.Insurer?.Trim() ?? "", m.Health?.Trim() ?? "", m.Payor?.Trim() ?? "", m.Plan?.Trim() ?? "", "", "", null, m.Eff, m.Eff, m.Exp, false);
        var flags = await rates.GetPlanFlagsAsync(probe, ct);
        return new CancellationQuote(m.Number, m.Name, m.Status, m.Eff, m.Exp, cancel, m.Premium, m.Mco, RefundMath.Compute(m.Exp, cancel, m.Eff, m.Premium, m.Mco, flags.RoundUp), m.Covered);
    }

    /// <summary>For insurers F/H the legacy form writes extra refund rows for covered persons depending on the MCO table's supplementary status; only the default status A (no extra rows) is supported.</summary>
    private static async Task EnsureSupportedAsync(IDbSession session, Loaded m, DateOnly cancel, CancellationToken ct)
    {
        if (m.Insurer is not ("F" or "H")) return;
        var status = (await session.QueryAsync("""
            SELECT TOP 1 SuppMCOStatus FROM MCO WHERE HLTCode = @health AND INSCode = @ins AND MCOEffDate <= @date AND (PLNCode = @plan OR PLNCode IS NULL)
            ORDER BY CASE WHEN PLNCode = @plan THEN 0 ELSE 1 END, MCOEffDate DESC
            """, new { health = m.Health?.Trim(), ins = m.Insurer.Trim(), plan = m.Plan?.Trim(), date = At(cancel) }, ct)).Select(r => S(r, "SuppMCOStatus")).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(status) && status != "A")
            throw new BusinessRuleException($"This plan charges supplementary MCO (status {status}); the supplementary refund rules are not migrated yet. Please cancel it in the legacy application.");
    }

    public async Task<CancellationQuote> QuoteAsync(CancellationRequest request, CancellationToken ct = default)
    {
        if (CancellationRules.Validate(request, needEndorsement: false) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        await using var session = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        var m = await LoadAsync(session, request.MemberNumber!.Trim(), ct);
        return await BuildAsync(session, m, request.CancelDate!.Value, ct);
    }

    public async Task<CancellationResult> CancelAsync(CancellationRequest request, string user, CancellationToken ct = default)
    {
        if (CancellationRules.Validate(request, needEndorsement: true) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var number = request.MemberNumber!.Trim();
        var cancel = request.CancelDate!.Value;
        var bordx = At(request.EndorsementBordxDate!.Value);
        var today = DateTime.Today;
        CancellationQuote quote;

        await using (var session = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            var m = await LoadAsync(session, number, ct);
            quote = await BuildAsync(session, m, cancel, ct);
            await EnsureSupportedAsync(session, m, cancel, ct);

            await session.ExecuteAsync("UPDATE MBM SET MBMStatus = 'C', MBMFinalExpiryDate = @cancel WHERE MBMNumber = @number", new { number, cancel = At(cancel) }, ct);
            if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @number", new { number }, ct) == 0)
                await session.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@number)", new { number }, ct);
            await session.ExecuteAsync("UPDATE MBMTwo SET MBMLastEndorseStatus = 'C', MBMENDtEffDate = @cancel, MBMENDtBordxDate = @bordx, MBMCancelDate = @cancel WHERE MBMNumber = @number",
                new { number, cancel = At(cancel), bordx }, ct);
            await session.ExecuteAsync("""
                INSERT INTO MBMAmendHistory (MBMNumber, MBMPolicyNo, MBMIcBcPp, MBMName, MBMExpiryDate, MBMPlanCode, MBMAmendEdtType, MBMAEndtBordxDate, MBMAEndtEffDate, MBMAmendUser, MBMAmendDate, MBMAEndtBatchNo)
                VALUES (@number, @policy, @ic, @name, @expiry, @plan, 'C', @bordx, @cancel, @user, @today, @batch)
                """, new { number, policy = m.Policy, ic = m.Ic, name = m.Name, expiry = At(m.Exp), plan = m.Plan, bordx, cancel = At(cancel), user, today, batch = (short)request.BatchNo!.Value }, ct);

            // Covered persons that are not already cancelled follow the principal (legacy WriteAdjStatus2Cov).
            await session.ExecuteAsync("UPDATE MBMCoveredPersons SET MBMCStatus = 'C', MBMCCancelDate = @cancel WHERE MBMCNumber = @number AND ISNULL(MBMCStatus, '') NOT IN ('K', 'C')",
                new { number, cancel = At(cancel) }, ct);

            // Refund row (legacy proc Upload_MBMMcoRefund): member bordx date and the batch number typed on the screen.
            await session.ExecuteAsync("""
                INSERT INTO MBMRefund (MBMNumber, MBMCovID, MBMPREMEffective, MBMPremReduction, MBMMCOEffective, MBMMCORefund, PAYCode, MBMBordxDate, MBMBatchNO, HLTCode)
                VALUES (@number, '00', @pe, @pr, @me, @mr, @payor, @bordxDate, @batch, @health)
                """, new { number, pe = quote.Refund.PremiumEffective, pr = quote.Refund.PremiumReduction, me = quote.Refund.McoEffective, mr = quote.Refund.McoRefund,
                    payor = m.Payor?.Trim(), bordxDate = m.BordxDate, batch = (short)request.BatchNo!.Value, health = m.Health?.Trim() is { Length: > 0 } h ? h[..1] : null }, ct);
            await session.CommitAsync(ct);
        }

        var crossOk = true;
        try
        {
            await using var maint = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
            await maint.ExecuteAsync("UPDATE MBMCrossReference SET MBMStatus = 'C', MBMFinalExpiryDate = @cancel WHERE MBMNumber = @number", new { number, cancel = At(cancel) }, ct);
            await maint.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { crossOk = false; }

        audit.Record(new AuditEntry("Member.Cancel", "MBM", number, user, new Dictionary<string, object?>
            { ["quote"] = quote, ["bordx"] = request.EndorsementBordxDate, ["batch"] = request.BatchNo, ["crossReferenceUpdated"] = crossOk }));
        return new CancellationResult(quote, crossOk);
    }
}
