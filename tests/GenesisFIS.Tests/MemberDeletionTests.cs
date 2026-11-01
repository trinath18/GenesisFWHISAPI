using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class DeletionRuleTests
{
    private static DeletionCriteria ByMember() => new("N", "WM", "M", null, null, null, "ZZDEL0001*01", "ZZDEL0001*99");

    [Fact]
    public void Valid_criteria_pass() => Assert.Null(DeletionRules.Validate(ByMember()));

    [Fact]
    public void Each_option_requires_its_own_inputs()
    {
        Assert.Equal("criteria", DeletionRules.Validate(null)!.Value.Field);
        Assert.Equal("health", DeletionRules.Validate(ByMember() with { Health = "" })!.Value.Field);
        Assert.Equal("payor", DeletionRules.Validate(ByMember() with { Payor = "WMX" })!.Value.Field);
        Assert.Equal("option", DeletionRules.Validate(ByMember() with { Option = "X" })!.Value.Field);
        Assert.Equal("memberFrom", DeletionRules.Validate(ByMember() with { MemberTo = "a'; --" })!.Value.Field);
        Assert.Equal("bordxFrom", DeletionRules.Validate(ByMember() with { Option = "D" })!.Value.Field);
        Assert.Equal("bordxTo", DeletionRules.Validate(ByMember() with { Option = "D", BordxFrom = new(2024, 2, 1), BordxTo = new(2024, 1, 1) })!.Value.Field);
        Assert.Equal("batchNo", DeletionRules.Validate(ByMember() with { Option = "B", BordxFrom = new(2024, 1, 1) })!.Value.Field);
        Assert.Null(DeletionRules.Validate(ByMember() with { Option = "B", BordxFrom = new(2024, 1, 1), BatchNo = 3 }));
    }

    [Fact]
    public void Blocked_reasons_name_what_is_in_the_way()
    {
        Assert.Null(new DeletionCandidate("X", null, null, null, null, null, 0, 0, 0).BlockedReason);
        Assert.Contains("2 case(s)", new DeletionCandidate("X", null, null, null, null, null, 0, 2, 0).BlockedReason);
        Assert.Contains("billing", new DeletionCandidate("X", null, null, null, null, null, 0, 0, 1).BlockedReason);
    }
}

public class MemberDeletionTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/deletion";
    private const string Member = "ZZDEL0001*01";
    private const string Other = "ZZDEL0002*01";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(5));

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static DeletionCriteria Range(string from = Member, string to = Other) => new("N", "WM", "M", null, null, null, from, to);

    private async Task Seed(string number, string insurer = "G", bool withCovered = false)
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMStatus, MBMExpiredStatus, MBMName, MBMIcBcPp, HLTCode, PAYCode, INSCode, MBMMemberType, MBMBordxDate, MBMBatchNo, MBMPolicyNo)
            VALUES (@n, 'A', 'N', 'TEST MEMBER', 'IC123', 'N', 'WM', @ins, 'P', '2099-01-05', 7, 'POL1')
            """, new { n = number, ins = insurer });
        await main.ExecuteAsync("INSERT INTO MBMOthers (MBMNumber, MBMAgentCode, MBMGroupCompany) VALUES (@n, 'AG1', 'TEST GROUP')", new { n = number });
        await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@n)", new { n = number });
        if (withCovered)
            await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName, MBMCMemberType) VALUES (@n, '01', 'TEST SPOUSE', 'S')", new { n = number });
    }

    private async Task<int> Count(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<int>(sql, args);
    }

    [Fact]
    public async Task Requires_authentication_and_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"{Url}/preview", Range())).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{Url}/preview", Range())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Url, new DeletionRequest(Range(), 1))).StatusCode);
    }

    [DatabaseFact]
    public async Task Preview_lists_members_with_counts_and_changes_nothing()
    {
        await Seed(Member, "F", withCovered: true);
        var p = await (await Client().PostAsJsonAsync($"{Url}/preview", Range())).Content.ReadFromJsonAsync<DeletionPreview>();
        var c = Assert.Single(p!.Candidates);
        Assert.Equal((Member, "TEST MEMBER", 1, 0, 0), (c.MemberNumber, c.Name, c.CoveredPersons, c.Cases, c.BillingRecords));
        Assert.Equal(new DateOnly(2099, 1, 5), c.BordxDate);
        Assert.Equal((1, 1, 0), (p.Principals, p.CoveredPersons, p.Blocked));
        Assert.Equal(1, await Count(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Members_with_cases_are_flagged_and_cannot_be_deleted()
    {
        var real = "WMGA6639886*01";
        var criteria = new DeletionCriteria("N", "WM", "M", null, null, null, real, real);
        var preview = await (await Client().PostAsJsonAsync($"{Url}/preview", criteria)).Content.ReadFromJsonAsync<DeletionPreview>();
        Assert.Equal(1, preview!.Blocked);
        Assert.Contains("case", Assert.Single(preview.Candidates).BlockedReason);

        var res = await Client().PostAsJsonAsync(Url, new DeletionRequest(criteria, 1));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("cannot be deleted", await res.Content.ReadAsStringAsync());
        Assert.Equal(1, await Count(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n", new { n = real }));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Delete_archives_then_removes_only_mbm_others_and_covered_persons_with_the_full_record_audited_first()
    {
        await Seed(Member, "F", withCovered: true);
        var res = await Client().PostAsJsonAsync(Url, new DeletionRequest(Range(), 1));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(new DeletionResult(1, 1, 3), await res.Content.ReadFromJsonAsync<DeletionResult>());

        Assert.Equal(0, await Count(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n", new { n = Member }));
        Assert.Equal(0, await Count(GenesisDb.Main, "SELECT COUNT(*) FROM MBMOthers WHERE MBMNumber = @n", new { n = Member }));
        Assert.Equal(0, await Count(GenesisDb.Main, "SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n", new { n = Member }));
        Assert.Equal(1, await Count(GenesisDb.Main, "SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @n", new { n = Member }));

        Assert.Equal(1, await Count(GenesisDb.Archive, "SELECT COUNT(*) FROM MBMArchive WHERE MBMNumber = @n AND MBMStatus = 'D' AND MBMDataStatus = 'D' AND MBMName = 'TEST MEMBER' AND MBMValueDate = CAST(GETDATE() AS date)", new { n = Member }));
        Assert.Equal(1, await Count(GenesisDb.Archive, "SELECT COUNT(*) FROM MBMOthersArchive WHERE MBMNumber = @n AND MBMAgentCode = 'AG1'", new { n = Member }));
        Assert.Equal(1, await Count(GenesisDb.Archive, "SELECT COUNT(*) FROM MBMCoveredPersonsArchive WHERE MBMCNumber = @n AND MBMCName = 'TEST SPOUSE'", new { n = Member }));

        Assert.Equal(["Member.DeleteIntent", "Member.Delete"], factory.Audit.Entries.Select(e => e.Action));
        var intent = factory.Audit.Entries[0];
        Assert.Equal("TEST", intent.User);
        var members = (IReadOnlyList<IDictionary<string, object?>>)intent.Details["members"]!;
        Assert.Equal("TEST MEMBER", members[0]["MBMName"]);
        Assert.Single((IReadOnlyList<IDictionary<string, object?>>)intent.Details["coveredPersons"]!);
    }

    [DatabaseFact]
    public async Task Non_FH_members_keep_their_covered_person_rows_like_the_legacy_form_and_rearchiving_is_safe()
    {
        await Seed(Member, "G", withCovered: true);
        await using (var arc = await factory.Db.OpenAsync(GenesisDb.Archive))
            await arc.ExecuteAsync("INSERT INTO MBMArchive (MBMNumber, MBMName) VALUES (@n, 'OLD ARCHIVE ROW')", new { n = Member });

        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync(Url, new DeletionRequest(Range(), 1))).StatusCode);
        Assert.Equal(1, await Count(GenesisDb.Main, "SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n", new { n = Member }));
        Assert.Equal(1, await Count(GenesisDb.Archive, "SELECT COUNT(*) FROM MBMArchive WHERE MBMNumber = @n AND MBMName = 'TEST MEMBER'", new { n = Member }));
    }

    [DatabaseFact]
    public async Task A_wrong_confirmed_count_deletes_nothing()
    {
        await Seed(Member); await Seed(Other);
        var res = await Client().PostAsJsonAsync(Url, new DeletionRequest(Range(), 1));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("not the 1 you confirmed", await res.Content.ReadAsStringAsync());
        Assert.Equal(2, await Count(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber IN (@a, @b)", new { a = Member, b = Other }));
        Assert.Equal(0, await Count(GenesisDb.Archive, "SELECT COUNT(*) FROM MBMArchive WHERE MBMNumber IN (@a, @b)", new { a = Member, b = Other }));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, new DeletionRequest(Range(), null))).StatusCode);
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task One_blocked_member_stops_the_whole_batch()
    {
        await Seed(Member);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("INSERT INTO MBMRefund (MBMNumber, MBMCovID, MBMMCORefListPrinted, MBMRefInvoiced) VALUES (@n, '00', 0, 'N')", new { n = Member });
        var res = await Client().PostAsJsonAsync(Url, new DeletionRequest(Range(), 1));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("billing", await res.Content.ReadAsStringAsync());
        Assert.Equal(1, await Count(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n", new { n = Member }));
    }

    [DatabaseFact]
    public async Task Selection_by_bordx_date_and_batch_and_the_size_limit_work()
    {
        await Seed(Member);
        var byBatch = new DeletionCriteria("N", "WM", "B", new(2099, 1, 5), null, 7, null, null);
        Assert.Equal(1, (await (await Client().PostAsJsonAsync($"{Url}/preview", byBatch)).Content.ReadFromJsonAsync<DeletionPreview>())!.Principals);
        Assert.Equal(0, (await (await Client().PostAsJsonAsync($"{Url}/preview", byBatch with { BatchNo = 8 })).Content.ReadFromJsonAsync<DeletionPreview>())!.Principals);
        var byDate = new DeletionCriteria("N", "WM", "D", new(2099, 1, 1), new(2099, 1, 31), null, null, null);
        Assert.Equal(1, (await (await Client().PostAsJsonAsync($"{Url}/preview", byDate)).Content.ReadFromJsonAsync<DeletionPreview>())!.Principals);

        var wide = await Client().PostAsJsonAsync($"{Url}/preview", new DeletionCriteria("N", "WM", "D", new(1900, 1, 1), new(2100, 1, 1), null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, wide.StatusCode);
        Assert.Contains("Narrow", await wide.Content.ReadAsStringAsync());
        // Confirming 1 member when nothing matches is a mismatch: refused, nothing changes.\n        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync(Url, new DeletionRequest(Range("ZZNONE0001*01", "ZZNONE0001*99"), 1))).StatusCode);
    }

    [DatabaseFact]
    public async Task Injection_text_in_criteria_is_rejected_or_harmless()
    {
        var res = await Client().PostAsJsonAsync($"{Url}/preview", new DeletionCriteria("N' OR '1'='1", "WM", "M", null, null, null, Member, Other));
        Assert.True(res.StatusCode is HttpStatusCode.BadRequest || (await res.Content.ReadFromJsonAsync<DeletionPreview>())!.Principals == 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/preview", Range(to: "x'; DELETE FROM MBM;--"))).StatusCode);
    }
}

