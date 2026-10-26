using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Membership;

namespace Genesis.Tests;

public class SupplementaryCancelRuleTests
{
    private static SupplementaryCancelRequest Ok() => new("ZZSCN0001*01", "01", new(2023, 7, 1), new(2023, 8, 1), 3);

    [Fact]
    public void Valid_request_passes() => Assert.Null(SupplementaryCancelRules.Validate(Ok(), true));

    [Fact]
    public void Fields_are_checked()
    {
        Assert.Equal("memberNumber", SupplementaryCancelRules.Validate(Ok() with { MemberNumber = "a'b" }, false)!.Value.Field);
        Assert.Equal("coverId", SupplementaryCancelRules.Validate(Ok() with { CoverId = "1" }, false)!.Value.Field);
        Assert.Equal("effectiveDate", SupplementaryCancelRules.Validate(Ok() with { EffectiveDate = null }, false)!.Value.Field);
        Assert.Null(SupplementaryCancelRules.Validate(Ok() with { BatchNo = null }, false));
        Assert.Equal("batchNo", SupplementaryCancelRules.Validate(Ok() with { BatchNo = null }, true)!.Value.Field);
    }
}

public class SupplementaryCancelTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/adjustment/supplementary/cancel";
    private const string Member = "ZZSCN0001*01";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(2));
    private static SupplementaryCancelRequest Req(string cover = "01", string member = Member) => new(member, cover, new(2023, 7, 1), new(2023, 8, 1), 3);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed(string suppStatus = "C", string coverStatus = "A")
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, PLNRoundUpStatus) VALUES ('ZZP1', 'N', 'ZZ', '2020-01-01', 'N')");
        await main.ExecuteAsync("INSERT INTO MCO (HLTCode, INSCode, PLNCode, MCOAmountAdult, MCOAmountChild, MCOEffDate, SuppMCOStatus) VALUES ('N', 'F', 'ZZP1', 30, 12, '2022-01-01', @s)", new { s = suppStatus });
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMStatus, MBMExpiredStatus, MBMName, MBMIcBcPp, MBMPolicyNo, HLTCode, PAYCode, INSCode, PLNCode, MBMPayorEffDate, MBMPayorExpDate, MBMBordxDate)
            VALUES (@n, 'A', 'N', 'PRINCIPAL', 'IC0', 'POL1', 'N', 'ZZ', 'F', 'ZZP1', '2023-01-01', '2023-12-31', '2023-02-02')
            """, new { n = Member });
        await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@n)", new { n = Member });
        await main.ExecuteAsync("""
            INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName, MBMCICBCPPNo, MBMCStatus, MBMCPLNCode, MBMCEffDate, MBMCExpDate, MBMCPremium, MBMCMCO)
            VALUES (@n, '01', 'SPOUSE', 'IC-S1', @cs, 'ZZP1', '2023-01-01', '2023-12-31', 360, 50), (@n, '02', 'CHILD', 'IC-S2', 'A', 'ZZP1', '2023-01-01', '2023-12-31', 360, 50)
            """, new { n = Member, cs = coverStatus });
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
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(3)).PostAsJsonAsync(Url, Req())).StatusCode);
    }

    [DatabaseFact]
    public async Task Quote_shows_the_refund_without_whole_number_inputs_and_changes_nothing()
    {
        await Seed();
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req())).Content.ReadFromJsonAsync<SupplementaryCancelQuote>())!;
        // 183/365 x 360 = 180.49 ; 184/365 x 50 = 25.21 (no CInt on the covered person's own values)
        Assert.Equal((true, 180.49m, 179.51m, 25.21m, 24.79m), (q.RefundWritten, q.Refund.PremiumReduction, q.Refund.PremiumEffective, q.Refund.McoRefund, q.Refund.McoEffective));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCCoverID = '01' AND MBMCStatus = 'A'", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Cancelling_marks_only_that_cover_writes_history_and_refund_and_leaves_the_principal_alone()
    {
        await Seed();
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync(Url, Req())).StatusCode);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCCoverID = '01' AND MBMCStatus = 'C' AND MBMCCancelDate = '2023-07-01'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCCoverID = '02' AND MBMCStatus = 'A' AND MBMCCancelDate IS NULL", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMStatus = 'A'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @n AND MBMLastEndorseStatus = 'K' AND MBMENDtEffDate = '2023-07-01' AND MBMENDtBordxDate = '2023-08-01' AND MBMCancelDate IS NULL", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n AND MBMAmendEdtType = 'K' AND MBMName = 'SPOUSE' AND MBMIcBcPp = 'IC-S1' AND MBMPlanCode = 'ZZP1' AND MBMAEndtBatchNo = 3 AND MBMAmendUser = 'TEST'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n AND MBMCovID = '01' AND MBMPremReduction = 180.49 AND MBMMCORefund = 25.21 AND PAYCode = 'ZZ' AND MBMBordxDate = '2023-02-02' AND MBMBatchNo = 3 AND HLTCode = 'N'", new { n = Member }));
        Assert.Equal("Member.CancelSupplementary", Assert.Single(factory.Audit.Entries).Action);
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync(Url, Req())).StatusCode);
    }

    [DatabaseFact]
    public async Task Refund_rows_follow_the_supplementary_mco_status()
    {
        await Seed(suppStatus: "A");
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync(Url, Req())).StatusCode);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCCoverID = '01' AND MBMCStatus = 'C'", new { n = Member }));
    }

    [DatabaseFact]
    public async Task Status_b_refunds_only_cover_01()
    {
        await Seed(suppStatus: "B");
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync(Url, Req("02"))).StatusCode);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n", new { n = Member }));
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync(Url, Req("01"))).StatusCode);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n AND MBMCovID = '01'", new { n = Member }));
    }

    [DatabaseFact]
    public async Task Invalid_requests_are_refused_without_changes()
    {
        await Seed(coverStatus: "C");
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync(Url, Req())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req("09"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req("01", "ZZNONE*01"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req("02") with { EffectiveDate = new(2024, 6, 1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req("02") with { BatchNo = null })).StatusCode);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }
}
