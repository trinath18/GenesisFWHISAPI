using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Membership;

namespace Genesis.Tests;

public class LifetimeLimitTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/adjustment/supplementary";
    private const string Member = "ZZLTL0001*01";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(2));
    private static SupplementaryRequest Req(string? guarantee, string name = "SPOUSE") =>
        new(Member, name, "MS", "IC-S1", new DateOnly(1990, 1, 1), "F", "S", null, null, null, new(2023, 7, 1), new(2023, 8, 1), 2, 0, 0, guarantee);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed(string status = "C", string bnfStatus = "C", bool principalLifetime = true, string? topup = null)
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, PLNLifeTimeStatus) VALUES ('ZZP1', 'N', 'ZZ', '2020-01-01', 'Y')");
        await main.ExecuteAsync("INSERT INTO AGE (AGECode, AGEDescription, AGEFrom, AGETo, HLTCode, PLNCode) VALUES ('05', 'ADULT', '18Y', '99Y', 'N', 'ZZP1')");
        await main.ExecuteAsync("INSERT INTO PRM (INSCode, HLTCode, PAYCode, PRMAmount, PRMAmountFemale, AGECode, PLNCode, PRMEffDate, SuppPrmAmt, SuppPrmStatus) VALUES ('F', 'N', 'ZZ', 1000, 1000, '05', 'ZZP1', '2022-01-01', 400, @s)", new { s = status });
        await main.ExecuteAsync("INSERT INTO MCO (HLTCode, INSCode, PLNCode, MCOAmountAdult, MCOAmountChild, MCOEffDate, SuppMCOStatus, MCOSuppAdultAmt, MCOSuppChildAmt) VALUES ('N', 'F', 'ZZP1', 30, 12, '2022-01-01', @s, 50, 20)", new { s = status });
        await main.ExecuteAsync("INSERT INTO AnnualLimit (PLNCode, INSCode, HLTCode, AnnLimit, SuppLimit, SuppLimitStatus, SuppLifeTimeLimit, AnnualEffDate, DisabilityLmt) VALUES ('ZZP1', 'F', 'N', 50000, 20000, @s, 90000, '2022-01-01', 0)", new { s = status });
        await main.ExecuteAsync("INSERT INTO BnfAnnLmt (HLTCode, PLNCode, INSCode, ALBEffDate, SuppALBStatus, KidDialysisLmt, CancerLmt, HomeNursLmt) VALUES ('N', 'ZZP1', 'F', '2022-01-01', @s, 1000, 2000, 300)", new { s = bnfStatus });
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMStatus, MBMExpiredStatus, MBMName, MBMIcBcPp, MBMPolicyNo, HLTCode, PAYCode, INSCode, PLNCode, MBMPayorEffDate, MBMPayorExpDate, MBMTopupNumber)
            VALUES (@n, 'A', 'N', 'PRINCIPAL', 'IC0', 'ZZPOL', 'N', 'ZZ', 'F', 'ZZP1', '2023-01-01', '2023-12-31', @topup)
            """, new { n = Member, topup });
        await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@n)", new { n = Member });
        if (principalLifetime) await main.ExecuteAsync("INSERT INTO MBMLifeTime (MBMNumber, MBMCovID, MBMLifeTimeLmt) VALUES (@n, '00', 100000)", new { n = topup ?? Member });
    }

    private async Task<T?> Scalar<T>(string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [DatabaseFact]
    public async Task A_lifetime_plan_requires_a_guarantee_renewal_choice()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync(Url, Req(null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("guarantee renewal", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req("2"))).StatusCode);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n", new { n = Member }));
    }

    [DatabaseFact]
    public async Task Guarantee_3_writes_benefit_limits_and_a_lifetime_limit_row()
    {
        await Seed();
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req("3"))).Content.ReadFromJsonAsync<SupplementaryQuote>())!;
        Assert.Equal((1000m, 2000m, 300m), (q.BenefitLimits!.Kidney, q.BenefitLimits.Cancer, q.BenefitLimits.HomeNursing));
        Assert.Equal((Member, 90000m), (q.LifetimeLimit!.MemberNumber, q.LifetimeLimit.Limit));

        Assert.Equal(HttpStatusCode.Created, (await Client().PostAsJsonAsync(Url, Req("3"))).StatusCode);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMBnfLmt WHERE MBMNumber = @n AND MBMCoveredID = '01' AND MBMKidneyAnnLmt = 1000 AND MBMKidneyBalLmt = 1000 AND MBMCancerAnnLmt = 2000 AND MBMCancerBalLmt = 2000 AND MBMHomeNCAnnLmt = 300 AND MBMHomeNCBalLmt = 300", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMLifeTime WHERE MBMNumber = @n AND MBMCovID = '01' AND MBMLifeTimeLmt = 90000", new { n = Member }));
    }

    [DatabaseFact]
    public async Task Guarantee_1_writes_benefit_limits_but_no_lifetime_row()
    {
        await Seed();
        Assert.Equal(HttpStatusCode.Created, (await Client().PostAsJsonAsync(Url, Req("1"))).StatusCode);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMBnfLmt WHERE MBMNumber = @n AND MBMCoveredID = '01'", new { n = Member }));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMLifeTime WHERE MBMNumber = @n AND MBMCovID = '01'", new { n = Member }));
    }

    [DatabaseFact]
    public async Task No_principal_lifetime_row_means_no_lifetime_limit_for_the_cover()
    {
        await Seed(principalLifetime: false);
        Assert.Equal(HttpStatusCode.Created, (await Client().PostAsJsonAsync(Url, Req("3"))).StatusCode);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMLifeTime WHERE MBMCovID = '01' AND MBMNumber = @n", new { n = Member }));
    }

    [DatabaseFact]
    public async Task The_lifetime_row_is_tracked_under_the_top_up_number_when_there_is_one()
    {
        await Seed(topup: "ZZTOP0001*01");
        Assert.Equal(HttpStatusCode.Created, (await Client().PostAsJsonAsync(Url, Req("3"))).StatusCode);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMLifeTime WHERE MBMNumber = 'ZZTOP0001*01' AND MBMCovID = '01' AND MBMLifeTimeLmt = 90000"));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMLifeTime WHERE MBMNumber = @n AND MBMCovID = '01'", new { n = Member }));
    }

    [DatabaseFact]
    public async Task Benefit_status_b_applies_to_cover_01_only_and_an_existing_lifetime_row_is_refused()
    {
        await Seed(bnfStatus: "B");
        Assert.Equal(HttpStatusCode.Created, (await Client().PostAsJsonAsync(Url, Req("1"))).StatusCode);
        var second = (await (await Client().PostAsJsonAsync(Url, Req("1", "PERSON TWO"))).Content.ReadFromJsonAsync<SupplementaryResult>())!.Quote;
        Assert.Equal(("02", null), (second.CoverId, second.BenefitLimits));

        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("INSERT INTO MBMLifeTime (MBMNumber, MBMCovID, MBMLifeTimeLmt) VALUES (@n, '03', 1)", new { n = Member });
        var dup = await Client().PostAsJsonAsync(Url, Req("3", "PERSON THREE"));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("already exists", await dup.Content.ReadAsStringAsync());
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n", new { n = Member }));
    }
}
