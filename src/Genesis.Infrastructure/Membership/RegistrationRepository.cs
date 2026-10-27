using Genesis.Core.Data;
using Genesis.Core.Fees;
using Genesis.Core.Membership;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Membership;

internal sealed class RegistrationRepository(IDbConnectionFactory connections, IAuditLog audit, FeeService fees) : SqlRepository(connections), IRegistrationRepository
{
    private static DateTime At(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
    private static string T(string? s) => s?.Trim() ?? "";
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string First(string? s, int n) => T(s).Length > n ? T(s)[..n] : T(s);

    public async Task<RegistrationOptions> OptionsAsync(CancellationToken ct = default) => new(
        (await QueryAsync(GenesisDb.Main, "SELECT HLTCode, HLTDescription FROM HLT ORDER BY HLTCode", null, ct)).Select(r => new HealthPlan(T(S(r, "HLTCode")), S(r, "HLTDescription"))).ToList(),
        (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PAYCompanyName, HLTCode FROM PAY ORDER BY PAYCode", null, ct)).Select(r => new RegistrationPayor(T(S(r, "PAYCode")), S(r, "PAYCompanyName"), T(S(r, "HLTCode")))).ToList());

    public async Task<int> LastBatchAsync(string payor, DateOnly bordxDate, CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Main, "SELECT TOP 1 BATSequence FROM BAT WHERE PAYCode = @payor AND BATBordxDate = @date AND BordxType = 'N' ORDER BY BATIndex DESC", new { payor = T(payor), date = At(bordxDate) }, ct))
            .Select(r => I(r, "BATSequence") ?? 0).FirstOrDefault();

    private async Task<(RegistrationQuote Quote, FeeResult Fees)> BuildAsync(IDbSession main, RegistrationRequest r, CancellationToken ct)
    {
        var health = First(r.HealthCode, 1); var payor = T(r.PayorCode); var ins = First(r.Insurer, 1); var plan = T(r.Plan);
        var eff = r.PayorEffectiveDate!.Value; var exp = r.PayorExpiryDate!.Value; var dob = r.DateOfBirth!.Value;
        var supp = r.Supplementary ?? [];
        if (supp.Count > 0 && ins is not ("F" or "H")) throw new BusinessRuleException("Only insured codes F and H take supplementary persons.", field: "insurer");

        var payRow = (await main.QueryAsync("SELECT PAYMBMRegStatus FROM PAY WHERE PAYCode = @payor", new { payor }, ct)).FirstOrDefault() ?? throw BusinessRuleException.NotFound($"Payor '{payor}' does not exist.");
        if (T(S(payRow, "PAYMBMRegStatus")) == "P") throw new BusinessRuleException("This payor numbers members by policy number, which the web registration does not support yet.", field: "payorCode");
        var planRow = (await main.QueryAsync("SELECT TOP 1 PLNLifeTimeStatus FROM PLN WHERE PLNCode = @plan AND HLTCode = @health AND (@health <> 'N' OR PAYCode = @payor) ORDER BY PLNEffDate DESC", new { plan, health, payor }, ct)).FirstOrDefault()
            ?? throw BusinessRuleException.NotFound($"Plan '{plan}' does not exist for this health plan and payor.");
        if (T(S(planRow, "PLNLifeTimeStatus")) == "Y") throw new BusinessRuleException("This plan has a lifetime limit; lifetime-limit registration is not supported yet. Please use the legacy application.", field: "plan");

        var ageText = LegacyAge.Text(eff, dob);
        var rows = (await main.QueryAsync("SELECT AGEIndex, AGECode, AGEDescription, AGEFrom, AGETo, PLNCode FROM AGE WHERE PLNCode = @plan OR PLNCode IS NULL", new { plan }, ct))
            .Select(x => new AgeBandRow(I(x, "AGEIndex") ?? 0, S(x, "AGECode") ?? "", S(x, "AGEDescription"), S(x, "AGEFrom"), S(x, "AGETo"), S(x, "PLNCode"))).ToList();
        var band = LegacyAge.Band(ageText, plan, rows) ?? throw new BusinessRuleException("Invalid age group: the age is out of the acceptable range.", field: "dateOfBirth");
        var ageCode = First(band.Code, 2); var adultChild = LegacyAge.AdultChild(ageCode);

        FeeResult f;
        try { f = await fees.CalculateAsync(new FeeRequest(ins, health, payor, plan, ageCode, adultChild, r.Sex, eff, eff, exp, ApplyDiscounts: true), ct); }
        catch (FeeRateMissingException ex) { throw new BusinessRuleException(ex.Message); }

        var limit = (await main.QueryAsync("SELECT TOP 1 AnnLimit FROM AnnualLimit WHERE INSCode = @ins AND PLNCode = @plan AND HLTCode = @health AND AnnualEffDate <= @eff ORDER BY AnnualEffDate DESC", new { ins, plan, health, eff = At(eff) }, ct))
            .Select(x => Dec(x, "AnnLimit") ?? 0).FirstOrDefault();
        var suppStatus = (await main.QueryAsync("SELECT TOP 1 SuppLimitStatus FROM AnnualLimit WHERE PLNCode = @plan AND HLTCode = @health AND INSCode = @ins", new { plan, health, ins }, ct)).Select(x => T(S(x, "SuppLimitStatus"))).FirstOrDefault();
        var memberType = string.IsNullOrEmpty(suppStatus) || suppStatus == "A" ? "P" : "Q";

        var dup = (await QueryAsync(GenesisDb.Maintenance, "SELECT TOP 1 MBMNumber FROM MBMCrossReference WHERE MBMIcBcPp = @ic ORDER BY MBMPayorEffDate DESC, MBMNumber DESC", new { ic = T(r.IcBcPp) }, ct)).Select(x => T(S(x, "MBMNumber"))).FirstOrDefault();
        var suppQuotes = await PriceSupplementaryAsync(main, supp, health, payor, ins, plan, eff, exp, ct);
        return (new RegistrationQuote(ageText, ageCode, adultChild, f.BasicPremium, f.Premium, f.BasicMco, f.McoFee, limit, memberType, string.IsNullOrEmpty(dup) ? null : dup, eff, exp, suppQuotes), f);
    }

    /// <summary>Legacy saveCoverPersons: each covered person is priced on the principal's payor period using the plan's supplementary limit, MCO and premium statuses.</summary>
    private async Task<IReadOnlyList<RegistrationSupplementaryQuote>> PriceSupplementaryAsync(IDbSession main, IReadOnlyList<RegistrationSupplementary> persons,
        string health, string payor, string ins, string principalPlan, DateOnly payorEff, DateOnly payorExp, CancellationToken ct)
    {
        var list = new List<RegistrationSupplementaryQuote>();
        var hasDiscPlan = persons.Count > 0 && (await QueryAsync(GenesisDb.Maintenance, "SELECT COUNT(*) AS N FROM DiscPlan WHERE PLNCode = @plan", new { plan = principalPlan }, ct)).Select(x => I(x, "N") ?? 0).First() > 0;
        for (var i = 0; i < persons.Count; i++)
        {
            var s = persons[i]; var coverId = (i + 1).ToString("00");
            var plan = Blank(s.SupplementaryPlan) ?? principalPlan; var eff = s.EffectiveDate ?? payorEff; var exp = s.ExpiryDate ?? payorExp;
            if (exp < eff) throw new BusinessRuleException($"The cover expiry of {T(s.Name)} is before its effective date.", field: "supplementary");

            var ageText = LegacyAge.Text(eff, s.DateOfBirth!.Value);
            var rows = (await main.QueryAsync("SELECT AGEIndex, AGECode, AGEDescription, AGEFrom, AGETo, PLNCode FROM AGE WHERE PLNCode = @plan OR PLNCode IS NULL", new { plan = principalPlan }, ct))
                .Select(x => new AgeBandRow(I(x, "AGEIndex") ?? 0, S(x, "AGECode") ?? "", S(x, "AGEDescription"), S(x, "AGEFrom"), S(x, "AGETo"), S(x, "PLNCode"))).ToList();
            var band = LegacyAge.Band(ageText, principalPlan, rows) ?? throw new BusinessRuleException($"The age of {T(s.Name)} is out of the acceptable range.", field: "supplementary");
            var ageCode = First(band.Code, 2); var adultChild = LegacyAge.AdultChild(ageCode);

            var planRow = (await main.QueryAsync("SELECT TOP 1 PLNLifeTimeStatus, PLNRoundUpStatus, PLNProRate FROM PLN WHERE PLNCode = @plan AND HLTCode = @health AND PLNEffDate <= @eff AND (@health = 'S' OR PAYCode = @payor) ORDER BY PLNEffDate DESC",
                new { plan, health, eff = At(payorEff), payor }, ct)).FirstOrDefault();
            if (planRow is not null && T(S(planRow, "PLNLifeTimeStatus")) == "Y") throw new BusinessRuleException("A lifetime-limit plan is not supported yet. Please use the legacy application.", field: "supplementary");
            var roundUp = planRow is null || T(S(planRow, "PLNRoundUpStatus")) != "N"; var proRate = planRow is null || T(S(planRow, "PLNProRate")) != "N";

            var lmt = (await main.QueryAsync("SELECT TOP 1 SuppLimitStatus, SuppLimit FROM AnnualLimit WHERE PLNCode = @plan AND HLTCode = @health AND INSCode = @ins AND AnnualEffDate <= @eff ORDER BY AnnualEffDate DESC", new { plan, health, ins, eff = At(payorEff) }, ct)).FirstOrDefault();
            var limitStatus = lmt is not null && T(S(lmt, "SuppLimitStatus")) is { Length: > 0 } ls ? ls : "A";
            var annual = (limitStatus == "B" && coverId == "01") || limitStatus == "C" ? Dec(lmt!, "SuppLimit") ?? 0 : 0;

            var mcoRow = (await main.QueryAsync("""
                SELECT TOP 1 SuppMCOStatus, CovIDChrg, MCOSuppAdultAmt, MCOSuppChildAmt FROM MCO WHERE HLTCode = @health AND INSCode = @ins AND MCOEffDate <= @eff AND (PLNCode = @plan OR PLNCode IS NULL)
                ORDER BY CASE WHEN PLNCode = @plan THEN 0 ELSE 1 END, MCOEffDate DESC
                """, new { health, ins, eff = At(payorEff), plan }, ct)).FirstOrDefault();
            var mcoStatus = mcoRow is not null && T(S(mcoRow, "SuppMCOStatus")) is { Length: > 0 } ms ? ms : "A"; var chrgFrom = mcoRow is null ? "" : T(S(mcoRow, "CovIDChrg"));
            decimal basicMco = 0, mco = 0;
            if (mcoRow is not null && ((mcoStatus == "B" && coverId == "01") || mcoStatus == "C" || (mcoStatus == "D" && string.CompareOrdinal(coverId, chrgFrom) >= 0)))
            {
                basicMco = FeeMath.Money((adultChild == "C" ? Dec(mcoRow, "MCOSuppChildAmt") : Dec(mcoRow, "MCOSuppAdultAmt")) ?? 0);
                mco = FeeMath.Mco(basicMco, new PlanFeeFlags(roundUp, proRate), eff, exp);
            }

            decimal basicPrem = 0, prem = 0;
            var prm = (await main.QueryAsync("""
                SELECT TOP 1 SuppPrmStatus, SuppPrmAmt FROM PRM WHERE PLNCode = @plan AND AGECode = @age AND INSCode = @ins AND HLTCode = @health AND PRMEffDate <= @eff AND (@health = 'S' OR PAYCode = @payor)
                ORDER BY PRMEffDate DESC
                """, new { plan, age = ageCode, ins, health, eff = At(payorEff), payor }, ct)).FirstOrDefault();
            if (prm is not null)
            {
                var ps = T(S(prm, "SuppPrmStatus")) is { Length: > 0 } x ? x : "A";
                if ((ps == "B" && coverId == "01") || ps == "C")
                {
                    basicPrem = Dec(prm, "SuppPrmAmt") ?? 0;
                    prem = FeeMath.ProRatePremium(eff, exp, FeeMath.Money(basicPrem));
                    if (hasDiscPlan) prem = FeeMath.ApplyDiscounts(prem, new DiscountRates(Policy: s.PolicyDiscount ?? 0, Renewal: s.RenewalDiscount ?? 0));
                }
            }
            list.Add(new RegistrationSupplementaryQuote(coverId, T(s.Name), ageText, ageCode, adultChild, plan, eff, exp, annual, basicPrem, prem, basicMco, mco));
        }
        return list;
    }

    public async Task<RegistrationQuote> QuoteAsync(RegistrationRequest request, CancellationToken ct = default)
    {
        if (RegistrationRules.Validate(request, full: true) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        await using var main = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        return (await BuildAsync(main, request, ct)).Quote;
    }

    /// <summary>Legacy MBMCheckDigits counter: 01..99 wrapping to 01.</summary>
    private static async Task<string> NextCheckDigitsAsync(IDbSession main, CancellationToken ct)
    {
        var current = (await main.QueryAsync("SELECT TOP 1 MBMChkDigits FROM MBMCheckDigits WITH (UPDLOCK, HOLDLOCK)", null, ct)).Select(r => T(S(r, "MBMChkDigits"))).FirstOrDefault();
        if (current is null) throw new BusinessRuleException("The check-digit counter table (MBMCheckDigits) is empty.");
        var next = current != "99" && int.TryParse(current, out var n) ? (n + 1).ToString("00") : "01";
        await main.ExecuteAsync("UPDATE MBMCheckDigits SET MBMChkDigits = @next", new { next }, ct);
        return next;
    }

    /// <summary>Legacy Update2MBMSEQ05: per-payor counter, letter + 5 digits ("A00001"), rolling the letter after 99999.</summary>
    private static async Task<string> NextPayorSequenceAsync(IDbSession maint, string payor, CancellationToken ct)
    {
        var current = (await maint.QueryAsync("SELECT TOP 1 SEQNumber FROM MBMSEQ05 WITH (UPDLOCK, HOLDLOCK) WHERE PAYCode = @payor", new { payor }, ct)).Select(r => T(S(r, "SEQNumber"))).FirstOrDefault();
        if (current is null) { await maint.ExecuteAsync("INSERT INTO MBMSEQ05 (PAYCode, SEQNumber) VALUES (@payor, 'A00001')", new { payor }, ct); return "A00001"; }
        var letter = current[0]; var no = int.Parse(current[1..]);
        var next = no != 99999 ? $"{letter}{no + 1:00000}" : $"{(char)(letter + 1)}00001";
        await maint.ExecuteAsync("UPDATE MBMSEQ05 SET SEQNumber = @next WHERE PAYCode = @payor", new { payor, next }, ct);
        return next;
    }

    /// <summary>
    /// Legacy GenerateMBMNo05: number = payor + insured + sequence (letter, 5 digits, 2 check digits) + "*" + renewal id. The sequence is remembered per
    /// payor/insured/birth date/name key (MBMSEQRenew05) so a renewal of the same person keeps the sequence and bumps the renewal id.
    /// </summary>
    private static async Task<string> GenerateNumberAsync(IDbSession main, IDbSession maint, string payor, string ins, DateOnly dob, string name, string renewal, CancellationToken ct)
    {
        var key = $"{payor}*{ins}*{dob:yyyyMMdd}*{name.Trim().Replace("'", "''")}";
        var isNew = renewal == "N";
        var seqNo = isNew ? await NextPayorSequenceAsync(maint, payor, ct) + await NextCheckDigitsAsync(main, ct) : "";
        var row = (await maint.QueryAsync("SELECT TOP 1 INSCode, RewSEQNo, RewSEQID FROM MBMSEQRenew05 WHERE RewMBMSEQKey = @key", new { key }, ct)).FirstOrDefault();
        var id = "01"; string insPart = ins;

        if (row is not null)
        {
            if (isNew)
            {
                var hisNo = $"{seqNo}*{T(S(row, "RewSEQID"))}";
                if (await maint.ScalarAsync<int>("SELECT COUNT(*) FROM MBMSEQRenewHistory WHERE MBMKey = @key", new { key }, ct) == 0)
                    await maint.ExecuteAsync("INSERT INTO MBMSEQRenewHistory (MBMKey, SEQNo) VALUES (@key, @hisNo)", new { key, hisNo }, ct);
                else
                    await maint.ExecuteAsync("UPDATE MBMSEQRenewHistory SET SEQNo = SEQNo + '~' + @hisNo WHERE MBMKey = @key", new { key, hisNo }, ct);
                await maint.ExecuteAsync("UPDATE MBMSEQRenew05 SET RewSEQID = '01', INSCode = @ins, RewSEQNo = @seqNo WHERE RewMBMSEQKey = @key", new { key, ins, seqNo }, ct);
            }
            else
            {
                seqNo = T(S(row, "RewSEQNo")); insPart = T(S(row, "INSCode"));
                id = (int.Parse(T(S(row, "RewSEQID"))) + 1).ToString("00");
                await maint.ExecuteAsync("UPDATE MBMSEQRenew05 SET RewSEQID = @id, RewSEQNo = @seqNo WHERE RewMBMSEQKey = @key", new { key, id, seqNo }, ct);
            }
        }
        else
        {
            if (!isNew) seqNo = await NextPayorSequenceAsync(maint, payor, ct) + await NextCheckDigitsAsync(main, ct);
            await maint.ExecuteAsync("INSERT INTO MBMSEQRenew05 (RewMBMSEQKey, INSCode, RewSEQNo, RewSEQID) VALUES (@key, @ins, @seqNo, '01')", new { key, ins, seqNo }, ct);
        }
        return $"{payor}{insPart}{seqNo}*{id}";
    }

    public async Task<RegistrationResult> RegisterAsync(RegistrationRequest r, string user, CancellationToken ct = default)
    {
        if (RegistrationRules.Validate(r, full: true) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var health = First(r.HealthCode, 1); var payor = T(r.PayorCode); var ins = First(r.Insurer, 1); var plan = T(r.Plan);
        var name = T(r.Name); var dob = r.DateOfBirth!.Value; var today = DateTime.Today;
        var renewal = T(r.Renewal);
        RegistrationQuote q; string number;

        await using (var main = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            var (quote, f) = await BuildAsync(main, r, ct);
            q = quote;
            if (renewal == "N" && quote.DuplicateMemberNo is not null && false) throw BusinessRuleException.Conflict("duplicate");

            await using (var maint = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct))
            {
                number = await GenerateNumberAsync(main, maint, payor, ins, dob, name, renewal, ct);
                await maint.CommitAsync(ct);
            }

            var eff = At(q.PayorEffectiveDate); var exp = At(q.PayorExpiryDate); var bordx = At(r.BordxDate!.Value);
            await main.ExecuteAsync("""
                INSERT INTO MBM (PAYCode, HLTCode, MBMNumber, MBMPolicyNo, MBMCOVID, MBMIcBcPp, MBMName, MBMDOB, MBMSex, RACCode, MBMStatus, MBMDataStatus, MBMMemberType, MBMAddress1, MBMAddress2, MBMAddress3,
                    CTYCode, MBMPostCode, STACode, MBMExpiredStatus, MBMValueDate, MBMPayorEffDate, MBMPayorEXPDate, MBMTakeOver, MBMRenewFlag, MBMBordxDate, MBMBatchNo, PLNCode, INSCode, AgeCode,
                    MBMAdultChild, MBMAvailableLimit, HSClnInd, MBMTopupNumber, MBMTopupInd)
                VALUES (@payor, @health, @number, @policy, '00', @ic, @name, @dob, @sex, @race, 'A', 'A', @type, @a1, @a2, @a3, @city, @post, @state, 'N', @today, @eff, @exp, @takeover, @renewal, @bordx, @batch,
                    @plan, @ins, @age, @ac, @limit, 'H', '', 'N')
                """, new { payor, health, number, policy = T(r.PolicyNo), ic = T(r.IcBcPp), name, dob = At(dob), sex = r.Sex, race = Blank(r.Race), type = q.MemberType, a1 = T(r.Address1), a2 = Blank(r.Address2), a3 = Blank(r.Address3),
                    city = Blank(r.City), post = T(r.PostCode), state = T(r.State), today, eff, exp, takeover = First(r.Takeover, 1), renewal, bordx, batch = r.BatchNo!.Value, plan, ins, age = q.AgeCode, ac = q.AdultChild, limit = q.AnnualLimit }, ct);
            await main.ExecuteAsync("""
                INSERT INTO MBMTwo (MBMNumber, MBMSalutation, MBMTelnoH, MBMTelNoM, MBMTelNoO, MBMEmail, NATCode, SRVCodeList, LGNCode, MBMRegUser, MBMLastUpdateUser, MBMUploadStatus)
                VALUES (@number, @sal, @th, @tm, @to, @email, @nat, 'ME', NULL, @user, @user, 'M')
                """, new { number, sal = Blank(r.Salutation), th = Blank(r.TelHome), tm = Blank(r.TelMobile), to = Blank(r.TelOffice), email = Blank(r.Email), nat = Blank(r.Nationality), user }, ct);
            await main.ExecuteAsync("""
                INSERT INTO MBMOthers (MBMNumber, MBMGroupCompany, MBMEmployeeNo, MBMAgentCode, MBMBranch, MBMPremium, MBMMCOFee, MBMBasicMCO, MBMBasicPrem, MBMDepartment, MBMInstallment, RELCODE, MBMUndExcess,
                    MBMPrevPolNo, MBMPrevMemNo, MBMPolicyDisc, MBMRenewalDisc, MBMPolCondition, MBMGuaRenewal, MBMENDtRefNo, MCOVoidStatus, MBMInvoiceNo)
                VALUES (@number, @grp, @emp, @agent, @branch, @premium, @mco, @bmco, @bprem, @dept, @inst, 'P', 0, @prevPol, @prevMem, @pd, @rd, '', '', '', '', '')
                """, new { number, grp = Blank(r.GroupCompany), emp = Blank(r.EmployeeNo), agent = Blank(r.AgentCode), branch = Blank(r.Branch), premium = q.Premium, mco = q.McoFee, bmco = q.BasicMco, bprem = q.BasicPremium,
                    dept = Blank(r.Department), inst = Blank(r.InstallmentMode), prevPol = Blank(r.PreviousPolicyNo), prevMem = Blank(r.PreviousMemberNo), pd = (int)f.Discounts.Policy, rd = (int)f.Discounts.Renewal }, ct);
            await main.ExecuteAsync("""
                INSERT INTO MBMOthersTwo (MBMNumber, MBMOccupation, MBMWorkNature, MBMMaritalStatus, MBMAllergic, MBMExclusion, MBMRemarks, MBMPayorPremGross, MBMPayorPremNet, MBMPayorMCOGross, MBMPayorMCONet,
                    MBMRBAmt, MBMFamDiscAmt, MBMRenewDiscAmt, MBMLoadingPrem)
                VALUES (@number, @occ, @work, @marital, @allergic, @exclusion, @remarks, 0, 0, 0, 0, 0, 0, 0, 0)
                """, new { number, occ = Blank(r.Occupation), work = Blank(r.WorkNature), marital = Blank(r.MaritalStatus), allergic = Blank(r.Allergies), exclusion = Blank(r.Exclusion), remarks = Blank(r.Remarks) }, ct);

            for (var i = 0; i < (q.Supplementary?.Count ?? 0); i++)
            {
                var sq = q.Supplementary![i]; var s = r.Supplementary![i];
                await main.ExecuteAsync("""
                    INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCAgeband, MBMCStatus, MBMCBordxDate, MBMCBatchNo, MBMCName, MBMCICBCPPNo, MBMCDOB, MBMCSex, MBMCAdultChild, MBMCRELCode, MBMCMemberType,
                        MBMCRegDate, MBMCPLNCode, MBMCEffDate, MBMCExpDate, MBMCAnnualLimit, MBMCAvailableLimit, MBMCBasicPremium, MBMCPremium, MBMCBasicMCO, MBMCMCO)
                    VALUES (@number, @cover, @age, 'A', @bordx, @batch, @name, @ic, @dob, @sex, @ac, @rel, 'C', @today, @plan, @effd, @expd, @limit, @limit, @bp, @prem, @bm, @mco)
                    """, new { number, cover = sq.CoverId, age = sq.AgeCode, bordx, batch = r.BatchNo!.Value.ToString(), name = sq.Name, ic = T(s.IcBcPp), dob = At(s.DateOfBirth!.Value), sex = s.Sex, ac = sq.AdultChild,
                        rel = T(s.Relationship), today, plan = sq.Plan, effd = At(sq.EffectiveDate), expd = At(sq.ExpiryDate), limit = sq.AnnualLimit, bp = sq.BasicPremium, prem = sq.Premium, bm = sq.BasicMco, mco = sq.Mco }, ct);
                await main.ExecuteAsync("""
                    INSERT INTO MBMCoveredPersonsTwo (MBMCNumber, MBMCCoverID, MBMCSalutation, MBMCAllergic, MBMCExclusion, MBMCRenewalDisc, MBMCPolicyDisc)
                    VALUES (@number, @cover, @sal, @allergic, @exclusion, @renew, @policy)
                    """, new { number, cover = sq.CoverId, sal = Blank(s.Salutation), allergic = Blank(s.Allergies), exclusion = Blank(s.Exclusion), renew = s.RenewalDiscount ?? 0, policy = s.PolicyDiscount ?? 0 }, ct);
            }

            // SaveBatch: one BAT row per payor, bordx date and type N; its sequence is the batch number just used.
            if (await main.ScalarAsync<int>("SELECT COUNT(*) FROM BAT WHERE PAYCode = @payor AND BATBordxDate = @bordx AND BordxType = 'N'", new { payor, bordx }, ct) == 0)
                await main.ExecuteAsync("INSERT INTO BAT (PAYCode, BATBordxDate, BATSequence, BordxType) VALUES (@payor, @bordx, @batch, 'N')", new { payor, bordx, batch = r.BatchNo!.Value }, ct);
            else
                await main.ExecuteAsync("UPDATE BAT SET BATSequence = @batch WHERE PAYCode = @payor AND BATBordxDate = @bordx AND BordxType = 'N'", new { payor, bordx, batch = r.BatchNo!.Value }, ct);
            await main.CommitAsync(ct);
        }

        // The cross reference lives in the maintenance database and cannot share the transaction; the screen warns when it fails.
        var xref = true;
        try
        {
            await using var maint = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
            await maint.ExecuteAsync("""
                INSERT INTO MBMCrossReference (MBMNumber, MBMCOVID, MBMPolicyNo, MBMIcBcPp, MBMName, MBMPayorEffDate, MBMPayorExpDate, AGECode, INSCode, PLNCode, MBMBordxDate, MBMTopupNumber, MBMTopupInd,
                    MBMDataStatus, MBMStatus, MBMDOB, HLTCode, PAYCode, MBMBatchNo)
                VALUES (@number, '00', @policy, @ic, @name, @eff, @exp, @age, @ins, @plan, @bordx, '', 'N', 'A', 'A', @dob, @health, @payor, @batch)
                """, new { number, policy = T(r.PolicyNo), ic = T(r.IcBcPp), name, eff = At(q.PayorEffectiveDate), exp = At(q.PayorExpiryDate), age = q.AgeCode, ins, plan, bordx = At(r.BordxDate!.Value), dob = At(dob), health, payor, batch = r.BatchNo!.Value }, ct);
            await maint.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { xref = false; }

        audit.Record(new AuditEntry("Member.Register", "MBM", number, user, new Dictionary<string, object?>
            { ["request"] = r, ["quote"] = q, ["renewal"] = renewal, ["crossReferenceSaved"] = xref }));
        return new RegistrationResult(number, q, xref);
    }
}

