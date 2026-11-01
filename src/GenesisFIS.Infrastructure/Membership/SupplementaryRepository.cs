using GenesisFIS.Core.Data;
using GenesisFIS.Core.Fees;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

internal sealed class SupplementaryRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ISupplementaryRepository
{
    private sealed record Principal(string Number, string? Name, string? Policy, string? Ic, string? Status, string? Insurer, string? Health, string? Payor, string? Plan, DateOnly Eff, DateOnly Exp);

    private static DateOnly? Day(IDictionary<string, object?> r, string name) => D(r, name) is { } d ? DateOnly.FromDateTime(d) : null;
    private static DateTime At(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
    private static string T(string? s, int n = int.MaxValue) => (s?.Trim() ?? "") is { } t && t.Length > n ? t[..n] : s?.Trim() ?? "";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Legacy GetSuppExpDate: when the person reaches the plan's age limit during the period, cover ends on their birthday.</summary>
    private static DateOnly SuppExpiry(DateOnly dob, int ageLimit, DateOnly exp)
    {
        if (ageLimit == 0 || exp.Year - dob.Year != ageLimit) return exp;
        var birthday = new DateOnly(exp.Year, dob.Month, Math.Min(dob.Day, DateTime.DaysInMonth(exp.Year, dob.Month)));
        return birthday <= exp ? birthday : exp;
    }

    private async Task<(SupplementaryQuote Quote, Principal Member, int CoverNo)> BuildAsync(IDbSession session, SupplementaryRequest req, CancellationToken ct)
    {
        var number = req.MemberNumber!.Trim();
        var r = (await session.QueryAsync("""
            SELECT MBMNumber, MBMName, MBMPolicyNo, MBMIcBcPp, MBMStatus, INSCode, HLTCode, PAYCode, PLNCode, MBMPayorEffDate, MBMPayorExpDate FROM MBM WHERE MBMNumber = @number
            """, new { number }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound($"Member '{number}' does not exist.");
        if (Day(r, "MBMPayorEffDate") is not { } eff || Day(r, "MBMPayorExpDate") is not { } exp) throw new BusinessRuleException("The member has no payor effective/expiry dates.");
        var m = new Principal(S(r, "MBMNumber") ?? number, S(r, "MBMName"), S(r, "MBMPolicyNo"), S(r, "MBMIcBcPp"), S(r, "MBMStatus"), S(r, "INSCode"), S(r, "HLTCode"), S(r, "PAYCode"), S(r, "PLNCode"), eff, exp);
        if (m.Status?.Trim() is "C" or "D") throw BusinessRuleException.Conflict(m.Status.Trim() == "C" ? "A cancelled membership cannot take a supplementary person." : "This policy is deleted.");
        if (T(m.Insurer, 1) is not ("F" or "H")) throw new BusinessRuleException("This plan does not have supplementary persons (only insurer F and H plans do).");

        var ins = T(m.Insurer, 1);
        var health = T(m.Health, 1);
        var principalPlan = T(m.Plan, 4);
        var suppPlan = Blank(req.SupplementaryPlan) ?? principalPlan;
        var dob = req.DateOfBirth!.Value;
        var suppEff = m.Eff;

        var maxId = (await session.QueryAsync("SELECT MBMCCoverID FROM MBMCoveredPersons WHERE MBMCNumber = @number", new { number }, ct))
            .Select(x => int.TryParse(S(x, "MBMCCoverID"), out var n) ? n : 0).DefaultIfEmpty(0).Max();
        var coverNo = maxId + 1;
        var coverId = coverNo.ToString("00");

        var ageText = LegacyAge.Text(m.Eff, dob);
        var bandRows = (await session.QueryAsync("SELECT AGEIndex, AGECode, AGEDescription, AGEFrom, AGETo, PLNCode FROM AGE WHERE PLNCode = @plan OR PLNCode IS NULL", new { plan = principalPlan }, ct))
            .Select(x => new AgeBandRow(I(x, "AGEIndex") ?? 0, S(x, "AGECode") ?? "", S(x, "AGEDescription"), S(x, "AGEFrom"), S(x, "AGETo"), S(x, "PLNCode"))).ToList();
        var band = LegacyAge.Band(ageText, principalPlan, bandRows) ?? throw new BusinessRuleException("The age is out of the acceptable range (no age band found).", field: "dateOfBirth");
        var ageCode = T(band.Code, 2);
        var adultChild = LegacyAge.AdultChild(ageCode);

        var planRow = (await session.QueryAsync("SELECT PLNLifeTimeStatus, PlnAgeLimit FROM PLN WHERE PLNCode = @plan", new { plan = suppPlan }, ct)).FirstOrDefault();
        if (planRow is null) throw BusinessRuleException.NotFound($"Plan '{suppPlan}' does not exist.");
        var lifetimePlan = string.Equals(T(S(planRow, "PLNLifeTimeStatus")), "Y", StringComparison.OrdinalIgnoreCase);
        var guarantee = Blank(req.GuaranteeRenewal) ?? "";
        if (lifetimePlan && guarantee.Length == 0)
            throw new BusinessRuleException("This plan has a lifetime limit: please choose guarantee renewal 1 (no lifetime limit) or 3 (with lifetime limit).", field: "guaranteeRenewal");
        var suppExp = SuppExpiry(dob, (int)(Dec(planRow, "PlnAgeLimit") ?? 0), m.Exp);

        // Annual limit
        var lmt = (await session.QueryAsync("SELECT TOP 1 SuppLimitStatus, SuppLimit FROM AnnualLimit WHERE PLNCode = @plan ORDER BY AnnualEffDate DESC", new { plan = suppPlan }, ct)).FirstOrDefault();
        var limitStatus = lmt is not null && T(S(lmt, "SuppLimitStatus")) is { Length: > 0 } ls ? ls : "A";
        var annual = (limitStatus == "B" && coverId == "01") || limitStatus == "C" ? Dec(lmt!, "SuppLimit") ?? 0 : 0;

        // Supplementary MCO status and charge
        var mcoStatus = "A"; var chrgFrom = "";
        var mcoRow = (await session.QueryAsync("""
            SELECT TOP 1 SuppMCOStatus, CovIDChrg FROM MCO WHERE HLTCode = @health AND INSCode = @ins AND MCOEffDate <= @eff AND (PLNCode = @plan OR PLNCode IS NULL)
            ORDER BY CASE WHEN PLNCode = @plan THEN 0 ELSE 1 END, MCOEffDate DESC
            """, new { health = T(m.Health), ins = T(m.Insurer), eff = At(m.Eff), plan = suppPlan }, ct)).FirstOrDefault();
        if (mcoRow is not null) { if (T(S(mcoRow, "SuppMCOStatus")) is { Length: > 0 } s1) mcoStatus = s1; chrgFrom = T(S(mcoRow, "CovIDChrg")); }
        decimal basicMco = 0, mco = 0;
        if ((mcoStatus == "B" && coverId == "01") || mcoStatus == "C" || (mcoStatus == "D" && string.CompareOrdinal(coverId, chrgFrom) >= 0))
        {
            var amt = (await session.QueryAsync("""
                SELECT TOP 1 MCOSuppAdultAmt, MCOSuppChildAmt FROM MCO WHERE INSCode = @ins AND HLTCode = @health AND MCOEffDate <= @eff AND (PLNCode = @plan OR PLNCode IS NULL)
                ORDER BY CASE WHEN PLNCode = @plan THEN 0 ELSE 1 END, MCOEffDate DESC
                """, new { ins = T(m.Insurer), health = T(m.Health), eff = At(suppEff), plan = suppPlan }, ct)).FirstOrDefault();
            basicMco = FeeMath.Money(amt is null ? 0 : (adultChild == "C" ? Dec(amt, "MCOSuppChildAmt") : Dec(amt, "MCOSuppAdultAmt")) ?? 0);
            mco = FeeMath.WholeEven(FeeMath.ProRateMco(suppEff, suppExp, basicMco));
        }

        // Supplementary premium
        decimal basicPrem = 0, prem = 0; var premStatus = "A";
        var prm = (await session.QueryAsync("SELECT TOP 1 SuppPrmStatus, SuppPrmAmt FROM PRM WHERE PLNCode = @plan AND AGECode = @age AND INSCode = @ins ORDER BY PRMEffDate DESC",
            new { plan = suppPlan, age = ageCode, ins }, ct)).FirstOrDefault();
        if (prm is not null)
        {
            premStatus = T(S(prm, "SuppPrmStatus")) is { Length: > 0 } ps ? ps : "A";
            if ((premStatus == "B" && coverId == "01") || premStatus == "C")
            {
                basicPrem = Dec(prm, "SuppPrmAmt") ?? 0;
                var rate = (await session.QueryAsync("""
                    SELECT TOP 1 SuppPrmAmt FROM PRM WHERE INSCode = @ins AND HLTCode = @health AND PLNCode = @plan AND AGECode = @age AND PRMEffDate <= @eff AND (@health = 'S' OR PAYCode = @payor)
                    ORDER BY PRMEffDate DESC
                    """, new { ins, health, plan = principalPlan, age = ageCode, eff = At(suppEff), payor = T(m.Payor, 2) }, ct)).FirstOrDefault();
                if (rate is null || Dec(rate, "SuppPrmAmt") is not { } sp) throw new BusinessRuleException("Age out of premium range. Please check the premium table.");
                var p = FeeMath.ProRatePremium(suppEff, suppExp, FeeMath.Money(sp));
                var hasDiscPlan = (await QueryAsync(GenesisDb.Maintenance, "SELECT COUNT(*) AS N FROM DiscPlan WHERE PLNCode = @plan", new { plan = principalPlan }, ct)).Select(x => I(x, "N") ?? 0).First() > 0;
                if (hasDiscPlan) p = FeeMath.ApplyDiscounts(p, new DiscountRates(Policy: req.PolicyDiscount ?? 0, Renewal: req.RenewalDiscount ?? 0));
                prem = p;
            }
        }

        // Benefit and lifetime limits (legacy GetBenefitLimit / PrincipalLifeTimeChecking / SaveMBMBnfAnnLmt / SaveMBMLifeTimeLmt): only for lifetime-limit plans.
        BenefitLimitRow? benefit = null;
        SupplementaryLifetimeRow? lifetime = null;
        if (lifetimePlan)
        {
            var bnf = (await session.QueryAsync("""
                SELECT TOP 1 SuppALBStatus, KidDialysisLmt, CancerLmt, HomeNursLmt FROM BnfAnnLmt WHERE INSCode = @ins AND PLNCode = @plan AND HLTCode = @health AND ALBEffDate <= @eff ORDER BY ALBEffDate DESC
                """, new { ins = T(m.Insurer), plan = T(m.Plan), health = T(m.Health), eff = At(m.Eff) }, ct)).FirstOrDefault();
            if (bnf is not null)
            {
                var bnfStatus = T(S(bnf, "SuppALBStatus")) is { Length: > 0 } bs ? bs : "A";
                if ((bnfStatus == "B" && coverId == "01") || bnfStatus == "C")
                    benefit = new BenefitLimitRow(Dec(bnf, "KidDialysisLmt") ?? 0, Dec(bnf, "CancerLmt") ?? 0, Dec(bnf, "HomeNursLmt") ?? 0, bnfStatus);
            }

            // The member whose lifetime rows are used: the top-up number of the latest active policy with the same policy no, payor and insurer.
            var prevNo = m.Number;
            var latest = (await session.QueryAsync("SELECT TOP 1 MBMTopupNumber FROM MBM WHERE MBMPolicyNo = @policy AND MBMStatus = 'A' AND PAYCode = @payor AND INSCode = @ins ORDER BY MBMPayorEffDate DESC",
                new { policy = T(m.Policy), payor = T(m.Payor), ins = T(m.Insurer) }, ct)).FirstOrDefault();
            if (latest is not null && T(S(latest, "MBMTopupNumber")) is { Length: > 0 } topup) prevNo = topup;

            var principalLifetime = await session.ScalarAsync<int>("SELECT COUNT(*) FROM MBMLifeTime WHERE MBMNumber = @prev AND MBMCovID = '00'", new { prev = prevNo }, ct) > 0;
            if (guarantee == "3" && principalLifetime && ((limitStatus == "B" && coverId == "01") || limitStatus == "C"))
            {
                if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM MBMLifeTime WHERE MBMNumber = @prev AND MBMCovID = @cover", new { prev = prevNo, cover = coverId }, ct) > 0)
                    throw BusinessRuleException.Conflict($"A lifetime limit already exists for cover {coverId}; it cannot be added twice.");
                var ltl = (await session.QueryAsync("""
                    SELECT TOP 1 SuppLifeTimeLimit FROM AnnualLimit WHERE INSCode = @ins AND PLNCode = @plan AND HLTCode = @health AND AnnualEffDate <= @eff ORDER BY AnnualEffDate DESC
                    """, new { ins = T(m.Insurer), plan = T(m.Plan), health = T(m.Health), eff = At(m.Eff) }, ct)).Select(x => Dec(x, "SuppLifeTimeLimit") ?? 0).FirstOrDefault();
                // Legacy: limit = lifetime limit - (annual limit - next limit), where next = annual limit when guarantee renewal is 3 (always the case here), so nothing is deducted.
                lifetime = new SupplementaryLifetimeRow(prevNo, ltl);
            }
        }

        var quote = new SupplementaryQuote(m.Number, coverId, req.Name!.Trim(), ageText, ageCode, adultChild, suppPlan, suppEff, suppExp, annual, basicPrem, prem, basicMco, mco, limitStatus, mcoStatus, premStatus, benefit, lifetime);
        return (quote, m, coverNo);
    }

    public async Task<SupplementaryQuote> QuoteAsync(SupplementaryRequest request, CancellationToken ct = default)
    {
        if (SupplementaryRules.Validate(request, needEndorsement: false) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        await using var session = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        return (await BuildAsync(session, request, ct)).Quote;
    }

    public async Task<SupplementaryResult> AddAsync(SupplementaryRequest request, string user, CancellationToken ct = default)
    {
        if (SupplementaryRules.Validate(request, needEndorsement: true) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var number = request.MemberNumber!.Trim();
        var today = DateTime.Today;
        var bordx = At(request.EndorsementBordxDate!.Value);
        var eff = At(request.EffectiveDate!.Value);
        SupplementaryQuote q;

        await using (var session = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            var (quote, m, _) = await BuildAsync(session, request, ct);
            q = quote;
            await session.ExecuteAsync("""
                INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCAgeband, MBMCStatus, MBMCBordxDate, MBMCBatchNo, MBMCName, MBMCICBCPPNo, MBMCDOB, MBMCSex, MBMCAdultChild, MBMCRELCode,
                    MBMCMemberType, MBMCRegDate, MBMCPLNCode, MBMCEffDate, MBMCExpDate, MBMCAnnualLimit, MBMCAvailableLimit, MBMCBasicPremium, MBMCPremium, MBMCBasicMCO, MBMCMCO)
                VALUES (@number, @cover, @age, @status, @bordx, @batch, @name, @ic, @dob, @sex, @ac, @rel, 'C', @today, @plan, @effd, @expd, @limit, @limit, @bp, @prem, @bm, @mco)
                """, new { number, cover = q.CoverId, age = q.AgeCode, status = T(m.Status, 1), bordx, batch = request.BatchNo!.Value.ToString(), name = q.Name, ic = request.IcBcPp!.Trim(),
                    dob = At(request.DateOfBirth!.Value), sex = request.Sex, ac = q.AdultChild, rel = request.Relationship!.Trim(), today, plan = q.Plan, effd = At(q.EffectiveDate), expd = At(q.ExpiryDate),
                    limit = q.AnnualLimit, bp = q.BasicPremium, prem = q.Premium, bm = q.BasicMco, mco = q.Mco }, ct);
            await session.ExecuteAsync("""
                INSERT INTO MBMCoveredPersonsTwo (MBMCNumber, MBMCCoverID, MBMCSalutation, MBMCAllergic, MBMCExclusion, MBMCPayorPremNet, MBMCPayorMCOGross, MBMCPayorMCONet, MBMCLoadingPrem,
                    MBMCFamDiscAmt, MBMCRenewDiscAmt, MBMCUndExcess, MBMCRenewalDisc, MBMCPolicyDisc)
                VALUES (@number, @cover, @sal, @allergic, @exclusion, 0, 0, 0, 0, 0, 0, 0, @renew, @policy)
                """, new { number, cover = q.CoverId, sal = Blank(request.Salutation), allergic = Blank(request.Allergies), exclusion = Blank(request.Exclusion), renew = request.RenewalDiscount ?? 0, policy = request.PolicyDiscount ?? 0 }, ct);

            if (q.BenefitLimits is { } b)
                await session.ExecuteAsync("INSERT INTO MBMBnfLmt (MBMNumber, MBMCoveredID, MBMKidneyAnnLmt, MBMKidneyBalLmt, MBMCancerAnnLmt, MBMCancerBalLmt, MBMHomeNCAnnLmt, MBMHomeNCBalLmt) VALUES (@number, @cover, @k, @k, @c, @c, @h, @h)",
                    new { number, cover = q.CoverId, k = b.Kidney, c = b.Cancer, h = b.HomeNursing }, ct);
            if (q.LifetimeLimit is { } lt)
                await session.ExecuteAsync("INSERT INTO MBMLifeTime (MBMNumber, MBMCovID, MBMLifeTimeLmt) VALUES (@prev, @cover, @limit)", new { prev = lt.MemberNumber, cover = q.CoverId, limit = lt.Limit }, ct);

            // Write2MBMnHistory with the member's current status: status unchanged, last endorsement S, history row.
            if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @number", new { number }, ct) == 0)
                await session.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@number)", new { number }, ct);
            await session.ExecuteAsync("UPDATE MBMTwo SET MBMLastEndorseStatus = 'S', MBMENDtEffDate = @eff, MBMENDtBordxDate = @bordx WHERE MBMNumber = @number", new { number, eff, bordx }, ct);
            await session.ExecuteAsync("""
                INSERT INTO MBMAmendHistory (MBMNumber, MBMPolicyNo, MBMIcBcPp, MBMName, MBMExpiryDate, MBMPlanCode, MBMAmendEdtType, MBMAEndtBordxDate, MBMAEndtEffDate, MBMAmendUser, MBMAmendDate, MBMAEndtBatchNo)
                VALUES (@number, @policy, @ic, @name, @expiry, @plan, 'S', @bordx, @eff, @user, @today, @batch)
                """, new { number, policy = m.Policy, ic = m.Ic, name = m.Name, expiry = At(m.Exp), plan = m.Plan, bordx, eff, user, today, batch = (short)request.BatchNo!.Value }, ct);
            await session.CommitAsync(ct);
        }

        audit.Record(new AuditEntry("Member.AddSupplementary", "MBMCoveredPersons", $"{number}/{q.CoverId}", user, new Dictionary<string, object?>
            { ["quote"] = q, ["bordx"] = request.EndorsementBordxDate, ["effective"] = request.EffectiveDate, ["batch"] = request.BatchNo }));
        return new SupplementaryResult(q);
    }
}


