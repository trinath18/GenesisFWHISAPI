using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Membership;

namespace Genesis.Tests;

public class RegistrationSupplementaryTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/registration";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(1));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static RegistrationSupplementary Spouse() => new("SPOUSE ONE", "MS", "ZZSP1", new(1990, 1, 1), "F", "S", "pollen", "none");
    private static RegistrationSupplementary Child() => new("CHILD TWO", null, "ZZSP2", new(2015, 6, 1), "M", "C", null, null);
    private static RegistrationRequest Req(string insurer = "F", params RegistrationSupplementary[] persons) =>
        new("N", "WM", new(2023, 2, 1), 3, "N", null, null, "N", "ZZ SUPP TEST", "MR", "ZZICS1", new(1980, 5, 6), "M", "01", "MY", "1 Road", null, null, "KL", "WP", "50000",
            null, null, null, null, insurer, "ZZP1", "ZZPOLS1", new(2023, 1, 1), new(2023, 12, 31), null, null, null, null, null, "A", null, null, null, null, null, null, persons);

    private async Task Seed(string status = "C")
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, PLNRoundUpStatus, PLNProRate) VALUES ('ZZP1', 'N', 'WM', '2020-01-01', 'N', 'Y')");
        await main.ExecuteAsync("INSERT INTO AGE (AGECode, AGEDescription, AGEFrom, AGETo, HLTCode, PLNCode) VALUES ('01', 'CHILD', '-100D', '17Y', 'N', 'ZZP1'), ('05', 'ADULT', '18Y', '99Y', 'N', 'ZZP1')");
        await main.ExecuteAsync("INSERT INTO PRM (INSCode, HLTCode, PAYCode, PRMAmount, PRMAmountFemale, AGECode, PLNCode, PRMEffDate, SuppPrmAmt, SuppPrmStatus) VALUES ('F', 'N', 'WM', 1000, 1000, '05', 'ZZP1', '2022-01-01', 400, @s), ('F', 'N', 'WM', 500, 500, '01', 'ZZP1', '2022-01-01', 100, @s)", new { s = status });
        await main.ExecuteAsync("INSERT INTO MCO (HLTCode, INSCode, PLNCode, MCOAmountAdult, MCOAmountChild, MCOEffDate, SuppMCOStatus, MCOSuppAdultAmt, MCOSuppChildAmt) VALUES ('N', 'F', 'ZZP1', 30, 12, '2022-01-01', @s, 50, 20)", new { s = status });
        await main.ExecuteAsync("INSERT INTO AnnualLimit (PLNCode, INSCode, HLTCode, AnnLimit, SuppLimit, SuppLimitStatus, AnnualEffDate, DisabilityLmt) VALUES ('ZZP1', 'F', 'N', 50000, 20000, @s, '2022-01-01', 0)", new { s = status });
    }

    private async Task<T?> Scalar<T>(string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public void Supplementary_persons_are_validated()
    {
        Assert.Equal("supplementary", RegistrationRules.Validate(Req("F", Spouse() with { Name = "" }), true)!.Value.Field);
        Assert.Equal("supplementary", RegistrationRules.Validate(Req("F", Spouse() with { IcBcPp = "" }), true)!.Value.Field);
        Assert.Equal("supplementary", RegistrationRules.Validate(Req("F", Spouse() with { Relationship = "SP" }), true)!.Value.Field);
        Assert.Equal("supplementary", RegistrationRules.Validate(Req("F", Spouse() with { Sex = "X" }), true)!.Value.Field);
        Assert.Equal("supplementary", RegistrationRules.Validate(Req("F", Spouse() with { DateOfBirth = null }), true)!.Value.Field);
        Assert.Null(RegistrationRules.Validate(Req("F", Spouse(), Child()), true));
    }

    [DatabaseFact]
    public async Task Quote_prices_each_covered_person_by_their_own_age_band()
    {
        await Seed();
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req("F", Spouse(), Child()))).Content.ReadFromJsonAsync<RegistrationQuote>())!;
        Assert.Equal(2, q.Supplementary!.Count);
        var (a, b) = (q.Supplementary[0], q.Supplementary[1]);
        Assert.Equal(("01", "05", "A", 20000m, 400m, 400m, 50m, 50m), (a.CoverId, a.AgeCode, a.AdultChild, a.AnnualLimit, a.BasicPremium, a.Premium, a.BasicMco, a.Mco));
        Assert.Equal(("02", "01", "C", 20000m, 100m, 100m, 20m, 20m), (b.CoverId, b.AgeCode, b.AdultChild, b.AnnualLimit, b.BasicPremium, b.Premium, b.BasicMco, b.Mco));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBM WHERE MBMName = 'ZZ SUPP TEST'"));
    }

    [DatabaseFact]
    public async Task Registering_writes_the_covered_persons_with_both_tables()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync(Url, Req("F", Spouse(), Child()));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var n = new { n = (await res.Content.ReadFromJsonAsync<RegistrationResult>())!.MemberNumber };
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCCoverID = '01' AND MBMCName = 'SPOUSE ONE' AND MBMCICBCPPNo = 'ZZSP1' AND MBMCRELCode = 'S' AND MBMCStatus = 'A' AND MBMCMemberType = 'C' AND MBMCAgeband = '05' AND MBMCAdultChild = 'A' AND MBMCPLNCode = 'ZZP1' AND MBMCAnnualLimit = 20000 AND MBMCAvailableLimit = 20000 AND MBMCBasicPremium = 400 AND MBMCPremium = 400 AND MBMCBasicMCO = 50 AND MBMCMCO = 50 AND MBMCBordxDate = '2023-02-01' AND MBMCEffDate = '2023-01-01' AND MBMCExpDate = '2023-12-31'", n));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCCoverID = '02' AND MBMCAdultChild = 'C' AND MBMCAgeband = '01' AND MBMCPremium = 100 AND MBMCMCO = 20", n));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersonsTwo WHERE MBMCNumber = @n AND MBMCCoverID = '01' AND MBMCSalutation = 'MS' AND MBMCAllergic = 'pollen' AND MBMCExclusion = 'none'", n));
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersonsTwo WHERE MBMCNumber = @n", n));
    }

    [DatabaseFact]
    public async Task Status_b_charges_only_cover_01()
    {
        await Seed("B");
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req("F", Spouse(), Child()))).Content.ReadFromJsonAsync<RegistrationQuote>())!;
        Assert.Equal((400m, 50m, 20000m), (q.Supplementary![0].Premium, q.Supplementary[0].Mco, q.Supplementary[0].AnnualLimit));
        Assert.Equal((0m, 0m, 0m), (q.Supplementary[1].Premium, q.Supplementary[1].Mco, q.Supplementary[1].AnnualLimit));
    }

    [DatabaseFact]
    public async Task Supplementary_persons_need_insured_f_or_h_and_create_nothing_when_refused()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync(Url, Req("G", Spouse()));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("F and H", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync($"{Url}/quote", Req("F"))).StatusCode);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBM WHERE MBMName = 'ZZ SUPP TEST'"));
    }
}
