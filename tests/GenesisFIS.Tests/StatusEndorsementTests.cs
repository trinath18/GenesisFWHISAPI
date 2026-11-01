using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class StatusEndorsementRuleTests
{
    private static StatusEndorsementRequest Ok() => new("ZZSTA0001*01", "X", new(2023, 7, 1), new(2023, 8, 1), 2);

    [Fact]
    public void Valid_request_passes_and_types_map_to_statuses()
    {
        Assert.Null(StatusEndorsementRules.Validate(Ok()));
        Assert.Equal("S", StatusEndorsements.NewStatus("X"));
        Assert.Equal("D", StatusEndorsements.NewStatus("D"));
        Assert.Null(StatusEndorsements.NewStatus("C"));
    }

    [Fact]
    public void Fields_are_checked()
    {
        Assert.Equal("memberNumber", StatusEndorsementRules.Validate(Ok() with { MemberNumber = "a'b" })!.Value.Field);
        Assert.Equal("type", StatusEndorsementRules.Validate(Ok() with { Type = "C" })!.Value.Field);
        Assert.Equal("effectiveDate", StatusEndorsementRules.Validate(Ok() with { EffectiveDate = null })!.Value.Field);
        Assert.Equal("endorsementBordxDate", StatusEndorsementRules.Validate(Ok() with { EndorsementBordxDate = null })!.Value.Field);
        Assert.Equal("batchNo", StatusEndorsementRules.Validate(Ok() with { BatchNo = -1 })!.Value.Field);
    }
}

public class StatusEndorsementTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/adjustment/status-endorsement";
    private const string Member = "ZZSTA0001*01";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(2));
    private static StatusEndorsementRequest Req(string type = "X", string member = Member) => new(member, type, new(2023, 7, 1), new(2023, 8, 1), 2);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed(string status = "A")
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO MBM (MBMNumber, MBMStatus, MBMExpiredStatus, MBMName, MBMIcBcPp, MBMPolicyNo, PLNCode, MBMPayorExpDate, MBMFinalExpiryDate) VALUES (@n, @status, 'N', 'STATUS TEST', 'IC1', 'POL1', 'ZZPL', '2023-12-31', '2023-12-31')", new { n = Member, status });
        await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@n)", new { n = Member });
        await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCStatus) VALUES (@n, '01', 'A'), (@n, '02', 'C')", new { n = Member });
        await using var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        await maint.ExecuteAsync("INSERT INTO MBMCrossReference (MBMNumber, MBMStatus) VALUES (@n, @status)", new { n = Member, status });
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Requires_authentication_and_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(Url, Req())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(3)).PostAsJsonAsync(Url, Req())).StatusCode);
    }

    [DatabaseFact]
    public async Task Suspending_sets_status_s_everywhere_and_records_history_without_touching_dates_or_refunds()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync(Url, Req("X"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var r = (await res.Content.ReadFromJsonAsync<StatusEndorsementResult>())!;
        Assert.Equal(("A", "S", 2, true), (r.PreviousStatus, r.NewStatus, r.CoveredPersonsUpdated, r.CrossReferenceUpdated));

        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMStatus = 'S' AND MBMFinalExpiryDate = '2023-12-31'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @n AND MBMLastEndorseStatus = 'X' AND MBMENDtEffDate = '2023-07-01' AND MBMENDtBordxDate = '2023-08-01' AND MBMCancelDate IS NULL", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n AND MBMAmendEdtType = 'X' AND MBMAEndtBatchNo = 2 AND MBMAmendUser = 'TEST' AND MBMPlanCode = 'ZZPL'", new { n = Member }));
        Assert.Equal(2, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCStatus = 'S'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM MBMCrossReference WHERE MBMNumber = @n AND MBMStatus = 'S'", new { n = Member }));
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n", new { n = Member }));
        Assert.Equal("Member.Suspend", Assert.Single(factory.Audit.Entries).Action);
    }

    [DatabaseFact]
    public async Task Deleting_the_policy_sets_status_d_and_a_suspended_member_can_be_deleted()
    {
        await Seed(status: "S");
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync(Url, Req("D"))).StatusCode);
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMStatus = 'D'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n AND MBMAmendEdtType = 'D'", new { n = Member }));
        Assert.Equal(2, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCStatus = 'D'", new { n = Member }));
        Assert.Equal("Member.DeletePolicy", Assert.Single(factory.Audit.Entries).Action);
    }

    [DatabaseFact]
    public async Task Repeats_wrong_states_and_bad_input_are_refused_without_changes()
    {
        await Seed(status: "S");
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync(Url, Req("X"))).StatusCode);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("UPDATE MBM SET MBMStatus = 'C' WHERE MBMNumber = @n", new { n = Member });
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync(Url, Req("X"))).StatusCode);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("UPDATE MBM SET MBMStatus = 'D' WHERE MBMNumber = @n", new { n = Member });
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync(Url, Req("D"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req("X", "ZZNONE*01"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req("C"))).StatusCode);
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }
}
