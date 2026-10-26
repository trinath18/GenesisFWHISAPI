using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Membership;

namespace Genesis.Tests;

public class ReinstatementRuleTests
{
    private static ReinstatementRequest Ok() => new("ZZREI0001*01", new DateOnly(2023, 8, 1), new DateOnly(2023, 7, 15), 5);

    [Fact]
    public void Valid_request_passes() => Assert.Null(ReinstatementRules.Validate(Ok()));

    [Fact]
    public void Each_field_is_required_and_checked()
    {
        Assert.Equal("memberNumber", ReinstatementRules.Validate(Ok() with { MemberNumber = "a'; --" })!.Value.Field);
        Assert.Equal("endorsementBordxDate", ReinstatementRules.Validate(Ok() with { EndorsementBordxDate = null })!.Value.Field);
        Assert.Equal("reinstateDate", ReinstatementRules.Validate(Ok() with { ReinstateDate = null })!.Value.Field);
        Assert.Equal("batchNo", ReinstatementRules.Validate(Ok() with { BatchNo = 0 })!.Value.Field);
        Assert.Equal("batchNo", ReinstatementRules.Validate(Ok() with { BatchNo = 100000 })!.Value.Field);
        Assert.Equal("reinstateDate", ReinstatementRules.Validate(Ok() with { ReinstateDate = new DateOnly(1800, 1, 1) })!.Value.Field);
    }
}

public class ReinstatementTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/reinstatement";
    private const string Member = "ZZREI0001*01";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(1));
    private static ReinstatementRequest Req(string member = Member) => new(member, new DateOnly(2023, 8, 1), new DateOnly(2023, 7, 15), 5);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed(string status = "C", string insurer = "G", bool covered = false, bool lapsed = true)
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO PRM (INSCode, HLTCode, PAYCode, PRMAmount, PRMAmountFemale, AGECode, PLNCode, PRMEffDate) VALUES (@ins, 'N', 'ZZ', 1000, 1200, '05', 'ZZPL', '2022-01-01')", new { ins = insurer });
        await main.ExecuteAsync("INSERT INTO MCO (HLTCode, INSCode, PLNCode, MCOAmountAdult, MCOAmountChild, MCOEffDate) VALUES ('N', @ins, 'ZZPL', 30, 12, '2022-01-01')", new { ins = insurer });
        await main.ExecuteAsync("INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, PLNRoundUpStatus, PLNProRate, PLNPremGenderStatus) VALUES ('ZZPL', 'N', 'ZZ', '2020-01-01', 'N', 'Y', 'N')");
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMStatus, MBMExpiredStatus, MBMName, MBMIcBcPp, MBMPolicyNo, HLTCode, PAYCode, INSCode, PLNCode, AGECode, MBMSex, MBMAdultChild, MBMMemberType, MBMPayorEffDate, MBMPayorExpDate, MBMFinalExpiryDate)
            VALUES (@n, @status, 'N', 'REINSTATE TEST', 'IC9', 'POL9', 'N', 'ZZ', @ins, 'ZZPL', '05', 'M', 'A', 'P', '2023-01-01', '2023-12-31', '2023-06-30')
            """, new { n = Member, status, ins = insurer });
        await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber, MBMCancelDate) VALUES (@n, @d)", new { n = Member, d = lapsed ? new DateTime(2023, 7, 1) : (DateTime?)null });
        if (covered)
            await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName, MBMCAdultChild, MBMCAgeband, MBMCSex, MBMCStatus, MBMCMemberType) VALUES (@n, '01', 'SPOUSE', 'C', '05', 'F', 'C', 'S')", new { n = Member });
        await using var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        await maint.ExecuteAsync("INSERT INTO MBMCrossReference (MBMNumber, MBMStatus, MBMFinalExpiryDate) VALUES (@n, @status, '2023-06-30')", new { n = Member, status });
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Requires_authentication_and_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}?memberNumber=X")).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}?memberNumber=X")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Url, Req())).StatusCode);
    }

    [DatabaseFact]
    public async Task Member_lookup_shows_status_dates_and_whether_it_can_be_reinstated()
    {
        await Seed();
        var m = await Client().GetFromJsonAsync<ReinstatementMember>($"{Url}?memberNumber={Uri.EscapeDataString(Member)}");
        Assert.Equal(("REINSTATE TEST", "C", new DateOnly(2023, 7, 1), new DateOnly(2023, 12, 31)), (m!.Name, m.Status, m.LapsedDate, m.ExpiryDate));
        Assert.True(m.CanReinstate);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"{Url}?memberNumber=ZZNOPE*01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync($"{Url}?memberNumber=a'b")).StatusCode);
    }

    [DatabaseFact]
    public async Task Quote_uses_the_lapsed_date_for_rates_and_pro_rata_to_expiry_without_discounts()
    {
        await Seed();
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req())).Content.ReadFromJsonAsync<ReinstatementQuote>())!;
        // 2023-07-01 .. 2023-12-31 = 184 days: 1000 x 184/365 = 504.11 ; MCO 30 x 184/365 = 15.12 (plan does not round up)
        var line = Assert.Single(q.Lines);
        Assert.Equal(("00", 504.11m, 15.12m), (line.CoverId, line.Premium, line.McoFee));
        Assert.Equal((504.11m, 15.12m), (q.TotalPremium, q.TotalMcoFee));
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMReinstatement WHERE MBMNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Reinstating_writes_all_tables_and_the_cross_reference_and_audits()
    {
        await Seed();
        var res = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var r = (await res.Content.ReadFromJsonAsync<ReinstatementResult>())!;
        Assert.Equal((1, 504.11m, 15.12m, true), (r.LinesWritten, r.TotalPremium, r.TotalMcoFee, r.CrossReferenceUpdated));

        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMStatus = 'A' AND MBMFinalExpiryDate = '2023-12-31'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, """
            SELECT COUNT(*) FROM MBMReinstatement WHERE MBMNumber = @n AND MBMCoverID = '00' AND MBMPremium = 504.11 AND MBMMCOFee = 15.12 AND MBMLapsedDate = '2023-07-01'
              AND MBMReinstateDate = '2023-07-15' AND MBMEndtBordxDate = '2023-08-01' AND MBMEndtBordxBatch = '5' AND LastUpdateUSR = 'TEST' AND LastUpdateDate = CAST(GETDATE() AS date)
            """, new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n AND MBMAmendEdtType = 'R' AND MBMAmendUser = 'TEST' AND MBMAEndtBatchNo = 5 AND MBMAEndtEffDate = '2023-07-15' AND MBMPlanCode = 'ZZPL'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM MBMCrossReference WHERE MBMNumber = @n AND MBMStatus = 'A' AND MBMFinalExpiryDate = '2023-12-31'", new { n = Member }));
        Assert.Equal("Member.Reinstate", Assert.Single(factory.Audit.Entries).Action);

        var again = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("active", await again.Content.ReadAsStringAsync());
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMReinstatement WHERE MBMNumber = @n", new { n = Member }));
    }

    [DatabaseFact]
    public async Task Supplementary_covered_persons_are_priced_and_reinstated_for_insurer_F()
    {
        await Seed(insurer: "F", covered: true);
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req())).Content.ReadFromJsonAsync<ReinstatementQuote>())!;
        Assert.Equal(["00", "01"], q.Lines.Select(l => l.CoverId));
        Assert.Equal(("01", 504.11m, 6.05m), (q.Lines[1].CoverId, q.Lines[1].Premium, q.Lines[1].McoFee));   // child MCO 12 x 184/365

        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync(Url, Req())).StatusCode);
        Assert.Equal(2, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMReinstatement WHERE MBMNumber = @n", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCStatus = 'A'", new { n = Member }));
    }

    [DatabaseFact]
    public async Task Active_members_missing_lapsed_dates_and_missing_rates_are_refused_without_changes()
    {
        await Seed(status: "A");
        var active = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMReinstatement WHERE MBMNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task A_member_without_a_lapsed_date_or_rates_cannot_be_priced()
    {
        await Seed(lapsed: false);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/quote", Req())).StatusCode);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("UPDATE MBMTwo SET MBMCancelDate = '2023-07-01' WHERE MBMNumber = @n", new { n = Member });
            await main.ExecuteAsync("DELETE FROM PRM WHERE PLNCode = 'ZZPL'");
        }
        var res = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("premium", await res.Content.ReadAsStringAsync());
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMStatus = 'C'", new { n = Member }));
    }
}

