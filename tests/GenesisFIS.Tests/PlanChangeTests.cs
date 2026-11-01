using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class LegacyAgeTests
{
    [Theory]
    [InlineData("2023-01-01", "2023-01-01", "-1D")]
    [InlineData("2023-01-01", "2024-01-01", "-1D")]
    [InlineData("2023-06-15", "2010-03-10", "0Y")]
    [InlineData("2023-06-15", "2010-08-10", "-1Y")]
    [InlineData("2023-06-15", "2010-03-20", "-1Y")]
    [InlineData("2023-06-15", "1980-12-31", "43Y")]
    [InlineData("2023-01-01", "2005-01-01", "18Y")]
    public void Age_text_reproduces_the_legacy_function_including_its_quirks(string eff, string dob, string expected) =>
        Assert.Equal(expected, LegacyAge.Text(DateOnly.Parse(eff), DateOnly.Parse(dob)));

    private static readonly AgeBandRow[] Rows =
    [
        new(1, "01", "CHILD", "-100D", "17Y", null), new(2, "05", "ADULT", "18Y", "65Y", null), new(3, "06", "SENIOR", "66Y", "99Y", null),
        new(9, "01", "CHILD", "-100D", "21Y", "ML11"), new(10, "02", "ADULT", "22Y", "70Y", "ML11"),
    ];

    [Fact]
    public void Plan_rows_win_and_plan_less_rows_are_the_fallback()
    {
        Assert.Equal("02", LegacyAge.Band("43Y", "ML11", Rows)!.Code);
        Assert.Equal("05", LegacyAge.Band("43Y", "OTHER", Rows)!.Code);
        Assert.Equal("06", LegacyAge.Band("70Y", "OTHER", Rows)!.Code);
        Assert.Equal("05", LegacyAge.Band("43Y", "", Rows)!.Code);
    }

    [Fact]
    public void Children_and_the_buggy_zero_and_minus_one_ages_fall_in_the_child_band()
    {
        Assert.Equal("01", LegacyAge.Band("0Y", "OTHER", Rows)!.Code);
        Assert.Equal("01", LegacyAge.Band("-1Y", "OTHER", Rows)!.Code);
        Assert.Equal("01", LegacyAge.Band("-1D", "OTHER", Rows)!.Code);
        Assert.Equal("01", LegacyAge.Band("17Y", "OTHER", Rows)!.Code);
    }

    [Fact]
    public void Out_of_range_ages_and_bad_text_find_no_band()
    {
        Assert.Null(LegacyAge.Band("120Y", "OTHER", Rows));
        Assert.Null(LegacyAge.Band("", "OTHER", Rows));
        Assert.Null(LegacyAge.Band("abcY", "OTHER", Rows));
    }

    [Fact]
    public void Only_band_01_is_a_child()
    {
        Assert.Equal("C", LegacyAge.AdultChild("01"));
        Assert.Equal("A", LegacyAge.AdultChild("05"));
    }
}

public class PlanChangeRuleTests
{
    private static PlanChangeRequest Ok() => new("ZZPLC0001*01", "ZZP2", new(2023, 7, 1), new(2023, 8, 1), 3);

    [Fact]
    public void Valid_request_passes() => Assert.Null(PlanChangeRules.Validate(Ok(), true));

    [Fact]
    public void Fields_are_checked()
    {
        Assert.Equal("memberNumber", PlanChangeRules.Validate(Ok() with { MemberNumber = "a'b" }, false)!.Value.Field);
        Assert.Equal("newPlan", PlanChangeRules.Validate(Ok() with { NewPlan = "" }, false)!.Value.Field);
        Assert.Equal("newPlan", PlanChangeRules.Validate(Ok() with { NewPlan = "A';--" }, false)!.Value.Field);
        Assert.Equal("effectiveDate", PlanChangeRules.Validate(Ok() with { EffectiveDate = null }, false)!.Value.Field);
        Assert.Null(PlanChangeRules.Validate(Ok() with { BatchNo = null }, false));
        Assert.Equal("batchNo", PlanChangeRules.Validate(Ok() with { BatchNo = null }, true)!.Value.Field);
    }
}

public class PlanChangeTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/adjustment/plan-change";
    private const string Member = "ZZPLC0001*01";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(2));
    private static PlanChangeRequest Req(string plan = "ZZP2", bool mco = true, bool limit = false) => new(Member, plan, new(2023, 7, 1), new(2023, 8, 1), 3, mco, limit);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed(string status = "A", bool covered = false, bool withBand = true)
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, PLNRoundUpStatus, PLNProRate, PLNPremGenderStatus) VALUES ('ZZP1', 'N', 'ZZ', '2020-01-01', 'N', 'Y', 'N'), ('ZZP2', 'N', 'ZZ', '2020-01-01', 'N', 'Y', 'N')");
        if (withBand) await main.ExecuteAsync("INSERT INTO AGE (AGECode, AGEDescription, AGEFrom, AGETo, HLTCode, PLNCode) VALUES ('01', 'CHILD', '-100D', '17Y', 'N', 'ZZP2'), ('05', 'ADULT', '18Y', '99Y', 'N', 'ZZP2')");
        await main.ExecuteAsync("INSERT INTO PRM (INSCode, HLTCode, PAYCode, PRMAmount, PRMAmountFemale, AGECode, PLNCode, PRMEffDate) VALUES ('G', 'N', 'ZZ', 1000, 1000, '05', 'ZZP2', '2022-01-01')");
        await main.ExecuteAsync("INSERT INTO MCO (HLTCode, INSCode, PLNCode, MCOAmountAdult, MCOAmountChild, MCOEffDate) VALUES ('N', 'G', 'ZZP2', 30, 12, '2022-01-01')");
        await main.ExecuteAsync("INSERT INTO AnnualLimit (PLNCode, INSCode, HLTCode, AnnLimit, AnnualEffDate, DisabilityLmt) VALUES ('ZZP2', 'G', 'N', 50000, '2022-01-01', 0)");
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMStatus, MBMExpiredStatus, MBMName, MBMIcBcPp, MBMPolicyNo, HLTCode, PAYCode, INSCode, PLNCode, AGECode, MBMSex, MBMDOB, MBMPayorEffDate, MBMPayorExpDate, MBMBordxDate, MBMAvailableLimit)
            VALUES (@n, @status, 'N', 'PLAN TEST', 'IC1', 'POL1', 'N', 'ZZ', 'G', 'ZZP1', '05', 'M', '1980-05-06', '2023-01-01', '2023-12-31', '2023-02-02', 10000)
            """, new { n = Member, status });
        await main.ExecuteAsync("INSERT INTO MBMOthers (MBMNumber, MBMPremium, MBMMCOFee, MBMBasicPrem) VALUES (@n, 900, 20, 900)", new { n = Member });
        await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@n)", new { n = Member });
        if (covered) await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID) VALUES (@n, '01')", new { n = Member });
        await using var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        await maint.ExecuteAsync("INSERT INTO DiscPlan (PLNCode, PolicyDisc, RenewalDisc) VALUES ('ZZP2', 12, 10)");
        await maint.ExecuteAsync("INSERT INTO MBMCrossReference (MBMNumber, MBMStatus, PLNCode) VALUES (@n, @status, 'ZZP1')", new { n = Member, status });
        await maint.ExecuteAsync("INSERT INTO MCOPending (MBMNumber, Status) VALUES (@n, 'P')", new { n = Member });
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Requires_authentication_and_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"{Url}/quote", Req())).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{Url}/quote", Req())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Url, Req())).StatusCode);
    }

    [DatabaseFact]
    public async Task Quote_finds_the_age_band_prices_the_new_plan_and_the_refund_on_the_new_fees_without_changing_anything()
    {
        await Seed();
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req())).Content.ReadFromJsonAsync<PlanChangeQuote>())!;
        Assert.Equal(("43Y", "05", "A", 1000m, 792m, 30m), (q.AgeText, q.AgeCode, q.AdultChild, q.BasicPremium, q.Premium, q.McoFee));
        Assert.Equal((900m, 20m, true), (q.CurrentPremium, q.CurrentMcoFee, q.McoFeeDiffers));
        // refund from 2023-07-01 on the NEW fees: 183/365 x 792 = 397.08, 184/365 x 30 = 15.12
        Assert.Equal((394.92m, 397.08m, 14.88m, 15.12m), (q.Refund.PremiumEffective, q.Refund.PremiumReduction, q.Refund.McoEffective, q.Refund.McoRefund));
        Assert.Equal((10000m, 50000m, true), (q.CurrentAnnualLimit, q.NewAnnualLimit, q.AnnualLimitDiffers));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND PLNCode = 'ZZP1' AND MBMStatus = 'A'", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Changing_the_plan_writes_everything_the_legacy_form_wrote()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync(Url, Req(limit: true));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await res.Content.ReadFromJsonAsync<PlanChangeResult>())!.CrossReferenceUpdated);

        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND PLNCode = 'ZZP2' AND AGECode = '05' AND MBMStatus = 'P' AND MBMAvailableLimit = 50000", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMOthers WHERE MBMNumber = @n AND MBMPremium = 792 AND MBMBasicPrem = 1000 AND MBMMCOFee = 30", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @n AND MBMLastEndorseStatus = 'P' AND MBMENDtEffDate = '2023-07-01' AND MBMENDtBordxDate = '2023-08-01' AND MBMLastUpdateUser = 'TEST'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n AND MBMAmendEdtType = 'P' AND MBMPlanCode = 'ZZP2' AND MBMAEndtBatchNo = 3 AND MBMExpiryDate = '2023-12-31'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n AND MBMCovID = '00' AND MBMPremReduction = 397.08 AND MBMPremEffective = 394.92 AND MBMMCORefund = 15.12 AND MBMMCOEffective = 14.88 AND MBMBordxDate = '2023-02-02' AND MBMBatchNo = 3", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM MBMCrossReference WHERE MBMNumber = @n AND PLNCode = 'ZZP2' AND MBMStatus = 'P'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM MCOPending WHERE MBMNumber = @n AND MBMNewBordxDate = '2023-02-02'", new { n = Member }));
        Assert.Equal("Member.PlanChange", Assert.Single(factory.Audit.Entries).Action);
    }

    [DatabaseFact]
    public async Task Declining_the_mco_fee_and_annual_limit_keeps_the_old_values_and_the_refund_uses_the_old_mco()
    {
        await Seed();
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync(Url, Req(mco: false, limit: false))).StatusCode);
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMOthers WHERE MBMNumber = @n AND MBMMCOFee = 20 AND MBMPremium = 792", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMAvailableLimit = 10000", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n AND MBMMCORefund = 10.08", new { n = Member }));
    }

    [DatabaseFact]
    public async Task Unsupported_or_invalid_changes_are_refused_without_changes()
    {
        await Seed(covered: true);
        var withCovered = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.BadRequest, withCovered.StatusCode);
        Assert.Contains("covered persons", await withCovered.Content.ReadAsStringAsync());
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("DELETE FROM MBMCoveredPersons WHERE MBMCNumber = @n", new { n = Member });

        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req("ZZP1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req("NOPLAN"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req() with { EffectiveDate = new(2024, 6, 1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req() with { BatchNo = null })).StatusCode);
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND PLNCode = 'ZZP1' AND MBMStatus = 'A'", new { n = Member }));
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Missing_age_band_or_rates_are_reported_and_cancelled_members_refused()
    {
        await Seed(withBand: false);
        await using (var dob = await factory.Db.OpenAsync(GenesisDb.Main))
            await dob.ExecuteAsync("UPDATE MBM SET MBMDOB = '1890-01-01' WHERE MBMNumber = @n", new { n = Member });
        var noBand = await Client().PostAsJsonAsync($"{Url}/quote", Req());
        Assert.Equal(HttpStatusCode.BadRequest, noBand.StatusCode);
        Assert.Contains("age band", await noBand.Content.ReadAsStringAsync());
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("INSERT INTO AGE (AGECode, AGEDescription, AGEFrom, AGETo, HLTCode, PLNCode) VALUES ('05', 'ADULT', '18Y', '99Y', 'N', 'ZZP2')");
            await main.ExecuteAsync("UPDATE MBM SET MBMDOB = '1980-05-06' WHERE MBMNumber = @n", new { n = Member });
            await main.ExecuteAsync("DELETE FROM PRM WHERE PLNCode = 'ZZP2'");
        }
        var noRate = await Client().PostAsJsonAsync($"{Url}/quote", Req());
        Assert.Equal(HttpStatusCode.BadRequest, noRate.StatusCode);
        Assert.Contains("premium", await noRate.Content.ReadAsStringAsync());
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("UPDATE MBM SET MBMStatus = 'C' WHERE MBMNumber = @n", new { n = Member });
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync($"{Url}/quote", Req())).StatusCode);
    }
}

