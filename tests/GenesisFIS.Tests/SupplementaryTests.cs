using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class SupplementaryRuleTests
{
    private static SupplementaryRequest Ok() => new("ZZSUP0001*01", "SPOUSE ONE", "MS", "IC-S1", new(1990, 1, 1), "F", "S", "none", null, null, new(2023, 7, 1), new(2023, 8, 1), 2);

    [Fact]
    public void Valid_request_passes() => Assert.Null(SupplementaryRules.Validate(Ok(), true));

    [Fact]
    public void Fields_and_limits_are_checked()
    {
        Assert.Equal("memberNumber", SupplementaryRules.Validate(Ok() with { MemberNumber = "a'b" }, false)!.Value.Field);
        Assert.Equal("name", SupplementaryRules.Validate(Ok() with { Name = " " }, false)!.Value.Field);
        Assert.Equal("name", SupplementaryRules.Validate(Ok() with { Name = new string('x', 61) }, false)!.Value.Field);
        Assert.Equal("icBcPp", SupplementaryRules.Validate(Ok() with { IcBcPp = "" }, false)!.Value.Field);
        Assert.Equal("relationship", SupplementaryRules.Validate(Ok() with { Relationship = "SP" }, false)!.Value.Field);
        Assert.Equal("sex", SupplementaryRules.Validate(Ok() with { Sex = "X" }, false)!.Value.Field);
        Assert.Equal("dateOfBirth", SupplementaryRules.Validate(Ok() with { DateOfBirth = new(2999, 1, 1) }, false)!.Value.Field);
        Assert.Equal("supplementaryPlan", SupplementaryRules.Validate(Ok() with { SupplementaryPlan = "TOOLONG" }, false)!.Value.Field);
        Assert.Equal("renewalDiscount", SupplementaryRules.Validate(Ok() with { RenewalDiscount = 101 }, false)!.Value.Field);
        Assert.Null(SupplementaryRules.Validate(Ok() with { BatchNo = null }, false));
        Assert.Equal("batchNo", SupplementaryRules.Validate(Ok() with { BatchNo = null }, true)!.Value.Field);
    }
}

public class SupplementaryTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/adjustment/supplementary";
    private const string Member = "ZZSUP0001*01";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(2));
    private static SupplementaryRequest Req(DateOnly? dob = null, string name = "SPOUSE ONE", decimal? renew = 10) =>
        new(Member, name, "MS", "IC-S1", dob ?? new DateOnly(1990, 1, 1), "F", "S", "pollen", "none", null, new(2023, 7, 1), new(2023, 8, 1), 2, renew, 0);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed(string suppStatus = "C", string insurer = "F", string status = "A", int? ageLimit = null, string lifetime = "")
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, PlnAgeLimit, PLNLifeTimeStatus) VALUES ('ZZP1', 'N', 'ZZ', '2020-01-01', @al, @lt)", new { al = ageLimit, lt = lifetime });
        await main.ExecuteAsync("INSERT INTO AGE (AGECode, AGEDescription, AGEFrom, AGETo, HLTCode, PLNCode) VALUES ('01', 'CHILD', '-100D', '17Y', 'N', 'ZZP1'), ('05', 'ADULT', '18Y', '99Y', 'N', 'ZZP1')");
        await main.ExecuteAsync("INSERT INTO PRM (INSCode, HLTCode, PAYCode, PRMAmount, PRMAmountFemale, AGECode, PLNCode, PRMEffDate, SuppPrmAmt, SuppPrmStatus) VALUES ('F', 'N', 'ZZ', 1000, 1000, '05', 'ZZP1', '2022-01-01', 400, @s)", new { s = suppStatus });
        await main.ExecuteAsync("INSERT INTO MCO (HLTCode, INSCode, PLNCode, MCOAmountAdult, MCOAmountChild, MCOEffDate, SuppMCOStatus, MCOSuppAdultAmt, MCOSuppChildAmt) VALUES ('N', 'F', 'ZZP1', 30, 12, '2022-01-01', @s, 50, 20)", new { s = suppStatus });
        await main.ExecuteAsync("INSERT INTO AnnualLimit (PLNCode, INSCode, HLTCode, AnnLimit, SuppLimit, SuppLimitStatus, AnnualEffDate, DisabilityLmt) VALUES ('ZZP1', 'F', 'N', 50000, 20000, @s, '2022-01-01', 0)", new { s = suppStatus });
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMStatus, MBMExpiredStatus, MBMName, MBMIcBcPp, MBMPolicyNo, HLTCode, PAYCode, INSCode, PLNCode, MBMPayorEffDate, MBMPayorExpDate)
            VALUES (@n, @status, 'N', 'PRINCIPAL', 'IC0', 'POL1', 'N', 'ZZ', @ins, 'ZZP1', '2023-01-01', '2023-12-31')
            """, new { n = Member, status, ins = insurer });
        await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@n)", new { n = Member });
        await using var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        await maint.ExecuteAsync("INSERT INTO DiscPlan (PLNCode, PolicyDisc, RenewalDisc) VALUES ('ZZP1', 12, 10)");
    }

    private async Task<T?> Scalar<T>(string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
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
    public async Task Quote_prices_the_adult_with_annual_limit_discount_and_supplementary_mco_without_changing_anything()
    {
        await Seed();
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req())).Content.ReadFromJsonAsync<SupplementaryQuote>())!;
        Assert.Equal(("01", "33Y", "05", "A", 20000m, 400m, 360m, 50m, 50m), (q.CoverId, q.AgeText, q.AgeCode, q.AdultChild, q.AnnualLimit, q.BasicPremium, q.Premium, q.BasicMco, q.Mco));
        Assert.Equal(new DateOnly(2023, 12, 31), q.ExpiryDate);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Adding_writes_both_covered_person_tables_the_endorsement_and_history()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(1, await Scalar<int>("""
            SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCCoverID = '01' AND MBMCName = 'SPOUSE ONE' AND MBMCICBCPPNo = 'IC-S1' AND MBMCDOB = '1990-01-01' AND MBMCSex = 'F'
              AND MBMCAdultChild = 'A' AND MBMCAgeband = '05' AND MBMCRELCode = 'S' AND MBMCMemberType = 'C' AND MBMCStatus = 'A' AND MBMCPLNCode = 'ZZP1' AND MBMCBordxDate = '2023-08-01' AND MBMCBatchNo = '2'
              AND MBMCEffDate = '2023-01-01' AND MBMCExpDate = '2023-12-31' AND MBMCAnnualLimit = 20000 AND MBMCAvailableLimit = 20000 AND MBMCBasicPremium = 400 AND MBMCPremium = 360
              AND MBMCBasicMCO = 50 AND MBMCMCO = 50 AND MBMCRegDate = CAST(GETDATE() AS date)
            """, new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersonsTwo WHERE MBMCNumber = @n AND MBMCCoverID = '01' AND MBMCSalutation = 'MS' AND MBMCAllergic = 'pollen' AND MBMCExclusion = 'none' AND MBMCRenewalDisc = 10 AND MBMCPolicyDisc = 0", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @n AND MBMLastEndorseStatus = 'S' AND MBMENDtEffDate = '2023-07-01' AND MBMENDtBordxDate = '2023-08-01'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n AND MBMAmendEdtType = 'S' AND MBMAEndtBatchNo = 2 AND MBMAmendUser = 'TEST'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMStatus = 'A'", new { n = Member }));
        Assert.Equal("Member.AddSupplementary", Assert.Single(factory.Audit.Entries).Action);

        var second = await (await Client().PostAsJsonAsync(Url, Req(name: "CHILD TWO", dob: new(2015, 6, 1)))).Content.ReadFromJsonAsync<SupplementaryResult>();
        Assert.Equal(("02", "01", "C", 20m), (second!.Quote.CoverId, second.Quote.AgeCode, second.Quote.AdultChild, second.Quote.Mco));
    }

    [DatabaseFact]
    public async Task Status_b_charges_only_the_first_supplementary_person()
    {
        await Seed(suppStatus: "B");
        Assert.Equal(HttpStatusCode.Created, (await Client().PostAsJsonAsync(Url, Req())).StatusCode);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCCoverID = '01' AND MBMCPremium = 360 AND MBMCMCO = 50 AND MBMCAnnualLimit = 20000", new { n = Member }));
        var second = (await (await Client().PostAsJsonAsync(Url, Req(name: "PERSON TWO"))).Content.ReadFromJsonAsync<SupplementaryResult>())!.Quote;
        Assert.Equal(("02", 0m, 0m, 0m, 0m), (second.CoverId, second.Premium, second.BasicPremium, second.Mco, second.AnnualLimit));
    }

    [DatabaseFact]
    public async Task Status_a_means_supplementary_persons_are_free_and_age_limit_ends_cover_on_the_birthday()
    {
        await Seed(suppStatus: "A", ageLimit: 33);
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req(dob: new(1990, 5, 6)))).Content.ReadFromJsonAsync<SupplementaryQuote>())!;
        Assert.Equal((0m, 0m, 0m), (q.Premium, q.Mco, q.AnnualLimit));
        Assert.Equal(new DateOnly(2023, 5, 6), q.ExpiryDate);
    }

    [DatabaseFact]
    public async Task Age_limit_pro_rates_a_charged_person_to_the_birthday()
    {
        await Seed(ageLimit: 33);
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req(dob: new(1990, 5, 6), renew: 0))).Content.ReadFromJsonAsync<SupplementaryQuote>())!;
        Assert.Equal((new DateOnly(2023, 5, 6), 138.08m, 17m), (q.ExpiryDate, Math.Round(q.Premium, 2), q.Mco));   // 126 days: 400 x 126/365, 50 x 126/365 = 17.26 -> 17
    }

    [DatabaseFact]
    public async Task Unsupported_or_invalid_additions_are_refused_without_changes()
    {
        await Seed(insurer: "G");
        var g = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.BadRequest, g.StatusCode);
        Assert.Contains("does not have supplementary", await g.Content.ReadAsStringAsync());
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("UPDATE MBM SET INSCode = 'F' WHERE MBMNumber = @n", new { n = Member });
            await main.ExecuteAsync("UPDATE PLN SET PLNLifeTimeStatus = 'Y' WHERE PLNCode = 'ZZP1'");
        }
        var life = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.BadRequest, life.StatusCode);
        Assert.Contains("lifetime", await life.Content.ReadAsStringAsync());
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("UPDATE PLN SET PLNLifeTimeStatus = '' WHERE PLNCode = 'ZZP1'");
            await main.ExecuteAsync("UPDATE MBM SET MBMStatus = 'C' WHERE MBMNumber = @n", new { n = Member });
        }
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync(Url, Req())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req() with { MemberNumber = "ZZNONE*01" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req(dob: new(1890, 1, 1)))).StatusCode);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }
}
