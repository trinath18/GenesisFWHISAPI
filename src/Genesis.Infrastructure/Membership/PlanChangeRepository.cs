using Genesis.Core.Data;
using Genesis.Core.Fees;
using Genesis.Core.Membership;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Membership;

internal sealed class PlanChangeRepository(IDbConnectionFactory connections, IAuditLog audit, FeeService fees) : SqlRepository(connections), IPlanChangeRepository
{
    private sealed record Loaded(string Number, string? Name, string? Policy, string? Ic, string? Status, string? Insurer, string? Health, string? Payor, string? Plan, string? Sex,
        DateOnly Dob, DateOnly Eff, DateOnly Exp, DateTime? BordxDate, decimal Premium, decimal Mco, decimal? AvailableLimit, int Covered);

    private static DateOnly? Day(IDictionary<string, object?> r, string name) => D(r, name) is { } d ? DateOnly.FromDateTime(d) : null;
    private static DateTime At(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
    private static string First(string? s, int n) => (s?.Trim() ?? "") is { } t && t.Length > n ? t[..n] : s?.Trim() ?? "";

    private static async Task<Loaded> LoadAsync(IDbSession session, string number, CancellationToken ct)
    {
        var r = (await session.QueryAsync("""
            SELECT m.MBMNumber, m.MBMName, m.MBMPolicyNo, m.MBMIcBcPp, m.MBMStatus, m.INSCode, m.HLTCode, m.PAYCode, m.PLNCode, m.MBMSex, m.MBMDOB, m.MBMPayorEffDate, m.MBMPayorExpDate,
                   m.MBMBordxDate, m.MBMAvailableLimit, ISNULL(o.MBMPremium, 0) AS Premium, ISNULL(o.MBMMCOFee, 0) AS Mco,
                   (SELECT COUNT(*) FROM MBMCoveredPersons c WHERE c.MBMCNumber = m.MBMNumber) AS Covered
            FROM MBM m LEFT JOIN MBMOthers o ON o.MBMNumber = m.MBMNumber WHERE m.MBMNumber = @number
            """, new { number }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound($"Member '{number}' does not exist.");
        if (Day(r, "MBMPayorEffDate") is not { } eff || Day(r, "MBMPayorExpDate") is not { } exp)
            throw new BusinessRuleException("The member has no payor effective/expiry dates, so the plan cannot be priced.");
        if (Day(r, "MBMDOB") is not { } dob) throw new BusinessRuleException("The member has no date of birth, so the age band cannot be found.");
        return new Loaded(S(r, "MBMNumber") ?? number, S(r, "MBMName"), S(r, "MBMPolicyNo"), S(r, "MBMIcBcPp"), S(r, "MBMStatus"), S(r, "INSCode"), S(r, "HLTCode"), S(r, "PAYCode"), S(r, "PLNCode"),
            S(r, "MBMSex"), dob, eff, exp, D(r, "MBMBordxDate"), Dec(r, "Premium") ?? 0, Dec(r, "Mco") ?? 0, Dec(r, "MBMAvailableLimit"), I(r, "Covered") ?? 0);
    }

    private async Task<PlanChangeQuote> BuildAsync(IDbSession session, Loaded m, PlanChangeRequest req, CancellationToken ct)
    {
        var plan = req.NewPlan!.Trim();
        var changeDate = req.EffectiveDate!.Value;
        if (m.Status is "C" or "D") throw BusinessRuleException.Conflict(m.Status == "C" ? "This membership is cancelled." : "This membership is deleted.");
        if (string.Equals(m.Plan?.Trim(), plan, StringComparison.OrdinalIgnoreCase)) throw new BusinessRuleException("The member is already on this plan.", field: "newPlan");
        if (m.Covered > 0) throw new BusinessRuleException("This member has covered persons; changing their plan needs the supplementary fee rules, which are not migrated yet. Please use the legacy application.");
        if (changeDate < m.Eff || changeDate > m.Exp) throw new BusinessRuleException($"The effective date must be within the payor period ({m.Eff:dd/MM/yyyy} - {m.Exp:dd/MM/yyyy}).", field: "effectiveDate");

        var health = First(m.Health, 1);
        if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM PLN WHERE PLNCode = @plan AND HLTCode = @health AND (@health <> 'N' OR PAYCode = @payor)", new { plan, health, payor = First(m.Payor, 2) }, ct) == 0)
            throw BusinessRuleException.NotFound($"Plan '{plan}' does not exist for this member's health plan.");

        // 1. Age band of the NEW plan on the payor effective date (legacy MemberCallAge).
        var ageText = LegacyAge.Text(m.Eff, m.Dob);
        var bandRows = (await session.QueryAsync("SELECT AGEIndex, AGECode, AGEDescription, AGEFrom, AGETo, PLNCode FROM AGE WHERE PLNCode = @plan OR PLNCode IS NULL", new { plan }, ct))
            .Select(r => new AgeBandRow(I(r, "AGEIndex") ?? 0, S(r, "AGECode") ?? "", S(r, "AGEDescription"), S(r, "AGEFrom"), S(r, "AGETo"), S(r, "PLNCode"))).ToList();
        var band = LegacyAge.Band(ageText, plan, bandRows) ?? throw new BusinessRuleException("Please enter a valid date, or the age is out of the acceptable range (no age band found).");
        var ageCode = First(band.Code, 2);
        var adultChild = LegacyAge.AdultChild(ageCode);

        // 2. Fees of the new plan (legacy MemberCallFees): rates and pro-rata on the payor period, discounts applied.
        FeeResult f;
        try
        {
            f = await fees.CalculateAsync(new FeeRequest(First(m.Insurer, 1), health, First(m.Payor, 2), plan, ageCode, adultChild, First(m.Sex, 1), m.Eff, m.Eff, m.Exp, ApplyDiscounts: true), ct);
        }
        catch (FeeRateMissingException ex) { throw new BusinessRuleException(ex.Message); }

        // 3. Refund of the legacy form (MBMMCORefundCal): computed on the NEW premium and MCO fee, from the endorsement date.
        var mcoStored = req.UpdateMcoFee ? f.McoFee : m.Mco;
        var refund = RefundMath.Compute(m.Exp, changeDate, m.Eff, f.Premium, mcoStored, f.Flags.RoundUp);

        var newLimit = (await session.QueryAsync("SELECT TOP 1 AnnLimit FROM AnnualLimit WHERE INSCode = @ins AND PLNCode = @plan AND HLTCode = @health AND AnnualEffDate <= @eff ORDER BY AnnualEffDate DESC",
            new { ins = First(m.Insurer, 1), plan, health, eff = At(m.Eff) }, ct)).Select(r => Dec(r, "AnnLimit")).FirstOrDefault();
        return new PlanChangeQuote(m.Number, m.Name, m.Status, m.Plan?.Trim(), plan, ageText, ageCode, adultChild, f.BasicPremium, f.Premium, f.McoFee, m.Premium, m.Mco,
            FeeMath.Money(f.McoFee) != FeeMath.Money(m.Mco), m.AvailableLimit, newLimit, newLimit != (m.AvailableLimit ?? 0), m.Eff, m.Exp, refund);
    }

    public async Task<PlanChangeQuote> QuoteAsync(PlanChangeRequest request, CancellationToken ct = default)
    {
        if (PlanChangeRules.Validate(request, needEndorsement: false) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        await using var session = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        return await BuildAsync(session, await LoadAsync(session, request.MemberNumber!.Trim(), ct), request, ct);
    }

    public async Task<PlanChangeResult> ChangeAsync(PlanChangeRequest request, string user, CancellationToken ct = default)
    {
        if (PlanChangeRules.Validate(request, needEndorsement: true) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var number = request.MemberNumber!.Trim();
        var bordx = At(request.EndorsementBordxDate!.Value);
        var changeDate = At(request.EffectiveDate!.Value);
        var today = DateTime.Today;
        PlanChangeQuote quote;
        DateTime? memberBordx;

        await using (var session = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            var m = await LoadAsync(session, number, ct);
            quote = await BuildAsync(session, m, request, ct);
            memberBordx = m.BordxDate;
            if (request.UpdateAnnualLimit && quote.NewAnnualLimit is null) throw new BusinessRuleException("There is no annual limit for the new plan in the maintenance table.", field: "updateAnnualLimit");
            var mcoStored = request.UpdateMcoFee ? quote.McoFee : m.Mco;

            // Write2MBMnHistory("P") + SavePrincipalAdjustment: status P, new plan and age band, optionally the new annual limit.
            await session.ExecuteAsync("UPDATE MBM SET PLNCode = @plan, AGECode = @age, MBMStatus = 'P', MBMAvailableLimit = CASE WHEN @limit IS NULL THEN MBMAvailableLimit ELSE @limit END WHERE MBMNumber = @number",
                new { number, plan = quote.NewPlan, age = quote.AgeCode, limit = request.UpdateAnnualLimit ? quote.NewAnnualLimit : null }, ct);
            if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @number", new { number }, ct) == 0)
                await session.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@number)", new { number }, ct);
            await session.ExecuteAsync("UPDATE MBMTwo SET MBMLastEndorseStatus = 'P', MBMENDtEffDate = @eff, MBMENDtBordxDate = @bordx, MBMLastAdjustmentDate = @today, MBMLastUpdateUser = @user WHERE MBMNumber = @number",
                new { number, eff = changeDate, bordx, today, user }, ct);
            await session.ExecuteAsync("""
                INSERT INTO MBMAmendHistory (MBMNumber, MBMPolicyNo, MBMIcBcPp, MBMName, MBMExpiryDate, MBMPlanCode, MBMAmendEdtType, MBMAEndtBordxDate, MBMAEndtEffDate, MBMAmendUser, MBMAmendDate, MBMAEndtBatchNo)
                VALUES (@number, @policy, @ic, @name, @expiry, @plan, 'P', @bordx, @eff, @user, @today, @batch)
                """, new { number, policy = m.Policy, ic = m.Ic, name = m.Name, expiry = At(m.Exp), plan = quote.NewPlan, bordx, eff = changeDate, user, today, batch = (short)request.BatchNo!.Value }, ct);

            if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM MBMOthers WHERE MBMNumber = @number", new { number }, ct) == 0)
                await session.ExecuteAsync("INSERT INTO MBMOthers (MBMNumber, RELCode) VALUES (@number, 'P')", new { number }, ct);
            await session.ExecuteAsync("UPDATE MBMOthers SET MBMMCOFee = @mco, MBMBasicPrem = @basic, MBMPremium = @premium WHERE MBMNumber = @number",
                new { number, mco = mcoStored, basic = quote.BasicPremium, premium = quote.Premium }, ct);

            // MBMMCORefundCal -> SaveMBMRefund (proc Upload_MBMMcoRefund).
            await session.ExecuteAsync("""
                INSERT INTO MBMRefund (MBMNumber, MBMCovID, MBMPREMEffective, MBMPremReduction, MBMMCOEffective, MBMMCORefund, PAYCode, MBMBordxDate, MBMBatchNO, HLTCode)
                VALUES (@number, '00', @pe, @pr, @me, @mr, @payor, @bordxDate, @batch, @health)
                """, new { number, pe = quote.Refund.PremiumEffective, pr = quote.Refund.PremiumReduction, me = quote.Refund.McoEffective, mr = quote.Refund.McoRefund,
                    payor = m.Payor?.Trim(), bordxDate = m.BordxDate, batch = (short)request.BatchNo!.Value, health = First(m.Health, 1) is { Length: > 0 } h ? h : null }, ct);
            await session.CommitAsync(ct);
        }

        var crossOk = true;
        try
        {
            await using var maint = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
            await maint.ExecuteAsync("UPDATE MBMCrossReference SET PLNCode = @plan, MBMStatus = 'P' WHERE MBMNumber = @number", new { number, plan = quote.NewPlan }, ct);
            await maint.ExecuteAsync("UPDATE MCOPending SET MBMNewBordxDate = @bordx WHERE MBMNumber = @number", new { number, bordx = memberBordx }, ct);
            await maint.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { crossOk = false; }

        audit.Record(new AuditEntry("Member.PlanChange", "MBM", number, user, new Dictionary<string, object?>
            { ["quote"] = quote, ["bordx"] = request.EndorsementBordxDate, ["batch"] = request.BatchNo, ["updateMcoFee"] = request.UpdateMcoFee, ["updateAnnualLimit"] = request.UpdateAnnualLimit, ["crossReferenceUpdated"] = crossOk }));
        return new PlanChangeResult(quote, crossOk);
    }
}
