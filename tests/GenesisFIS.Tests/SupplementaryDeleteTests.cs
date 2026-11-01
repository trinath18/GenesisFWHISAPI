using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class SupplementaryDeleteTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/adjustment/supplementary/delete";
    private const string Member = "ZZSDL0001*01";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(2));
    private static SupplementaryDeleteRequest Req(string cover = "01", string member = Member) => new(member, cover, new(2023, 7, 1), new(2023, 8, 1), 4);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed(string principalStatus = "A")
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMStatus, MBMExpiredStatus, MBMName, MBMIcBcPp, MBMPolicyNo, INSCode, PLNCode, MBMPayorExpDate)
            VALUES (@n, @status, 'N', 'PRINCIPAL', 'IC0', 'POL1', 'F', 'ZZP1', '2023-12-31')
            """, new { n = Member, status = principalStatus });
        await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@n)", new { n = Member });
        await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName, MBMCStatus) VALUES (@n, '01', 'SPOUSE', 'A'), (@n, '02', 'CHILD', 'A')", new { n = Member });
        await main.ExecuteAsync("INSERT INTO MBMCoveredPersonsTwo (MBMCNumber, MBMCCoverID, MBMCSalutation) VALUES (@n, '01', 'MS'), (@n, '02', 'MR')", new { n = Member });
        await main.ExecuteAsync("INSERT INTO MBMCoveredPersonsCLN (MBMCNumber, MBMCCoverID) VALUES (@n, '01')", new { n = Member });
        await main.ExecuteAsync("INSERT INTO MBMBnfLmt (MBMNumber, MBMCoveredID) VALUES (@n, '01')", new { n = Member });
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
    public async Task Quote_counts_what_would_be_deleted_and_changes_nothing()
    {
        await Seed();
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req())).Content.ReadFromJsonAsync<SupplementaryDeleteQuote>())!;
        Assert.Equal(("SPOUSE", 0, 0, 0, 1, 1), (q.Name, q.Cases, q.RefundRows, q.LifetimeRows, q.ClinicRows, q.BenefitLimitRows));
        Assert.Null(q.BlockedReason);
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Deleting_removes_only_that_cover_everywhere_audits_the_full_record_first_and_writes_history()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(4, (await res.Content.ReadFromJsonAsync<SupplementaryDeleteResult>())!.RowsDeleted);

        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCCoverID = '01'", new { n = Member }));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersonsTwo WHERE MBMCNumber = @n AND MBMCCoverID = '01'", new { n = Member }));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersonsCLN WHERE MBMCNumber = @n", new { n = Member }));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMBnfLmt WHERE MBMNumber = @n", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCCoverID = '02'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersonsTwo WHERE MBMCNumber = @n AND MBMCCoverID = '02'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMStatus = 'A'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @n AND MBMLastEndorseStatus = 'T' AND MBMENDtEffDate = '2023-07-01' AND MBMENDtBordxDate = '2023-08-01'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n AND MBMAmendEdtType = 'T' AND MBMName = 'PRINCIPAL' AND MBMPlanCode = 'ZZP1' AND MBMAEndtBatchNo = 4 AND MBMAmendUser = 'TEST'", new { n = Member }));

        Assert.Equal(["Member.DeleteSupplementaryIntent", "Member.DeleteSupplementary"], factory.Audit.Entries.Select(e => e.Action));
        var covered = (IReadOnlyList<IDictionary<string, object?>>)factory.Audit.Entries[0].Details["coveredPerson"]!;
        Assert.Equal("SPOUSE", covered[0]["MBMCName"]);
    }

    [DatabaseFact]
    public async Task Covers_with_cases_refunds_or_lifetime_limits_cannot_be_deleted()
    {
        await Seed();
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("INSERT INTO CAS (CASNumber, CASClaimAdmin, MBMNumber, MBMPatientCovID) VALUES ('ZZCASE0001', 'N', @n, '01')", new { n = Member });
            await main.ExecuteAsync("INSERT INTO MBMRefund (MBMNumber, MBMCovID, MBMMCORefListPrinted, MBMRefInvoiced) VALUES (@n, '02', 0, 'N')", new { n = Member });
        }
        var withCase = await Client().PostAsJsonAsync(Url, Req("01"));
        Assert.Equal(HttpStatusCode.Conflict, withCase.StatusCode);
        Assert.Contains("case", await withCase.Content.ReadAsStringAsync());
        var withRefund = await Client().PostAsJsonAsync(Url, Req("02"));
        Assert.Equal(HttpStatusCode.Conflict, withRefund.StatusCode);
        Assert.Contains("refund", await withRefund.Content.ReadAsStringAsync());

        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("DELETE FROM CAS WHERE CASNumber = 'ZZCASE0001'");
            await main.ExecuteAsync("INSERT INTO MBMLifeTime (MBMNumber, MBMCovID, MBMLifeTimeLmt) VALUES (@n, '01', 1000)", new { n = Member });
        }
        var withLife = await Client().PostAsJsonAsync(Url, Req("01"));
        Assert.Equal(HttpStatusCode.Conflict, withLife.StatusCode);
        Assert.Contains("lifetime", await withLife.Content.ReadAsStringAsync());
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n", new { n = Member }));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Unknown_members_covers_and_bad_input_are_refused()
    {
        await Seed();
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req("09"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req("01", "ZZNONE*01"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req("1"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req() with { BatchNo = null })).StatusCode);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("UPDATE MBM SET MBMStatus = 'C' WHERE MBMNumber = @n", new { n = Member });
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync(Url, Req())).StatusCode);
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }
}
