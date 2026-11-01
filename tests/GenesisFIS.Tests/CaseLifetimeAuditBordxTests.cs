using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;

namespace GenesisFIS.Tests;

public class CaseAuditRuleTests
{
    private static CaseAuditRequest Ok() => new("O", "A", "INV1", new DateOnly(2024, 1, 2), 100m, null, null, null, "09:30", "MS LEE", null, 0m, null, null, null, null, "note");

    [Fact]
    public void Audit_review_needs_status_and_discount_type_like_the_legacy_form()
    {
        Assert.Null(CaseAuditRules.Validate(Ok()));
        Assert.Equal("discountType", CaseAuditRules.Validate(Ok() with { DiscountType = "X" })!.Value.Field);
        Assert.Equal("status", CaseAuditRules.Validate(Ok() with { Status = null })!.Value.Field);
        Assert.Equal("visitTime", CaseAuditRules.Validate(Ok() with { VisitTime = "25:00" })!.Value.Field);
        Assert.Equal("amount", CaseAuditRules.Validate(Ok() with { InvoiceAmount = -1 })!.Value.Field);
        Assert.Equal("invoiceDate", CaseAuditRules.Validate(Ok() with { InvoiceDate = new DateOnly(1980, 1, 1) })!.Value.Field);
        Assert.Equal("reviewAudit", CaseAuditRules.Validate(Ok() with { ReviewAudit = new string('x', 4001) })!.Value.Field);
    }
}

[Collection("CaseRegistration")]
public class CaseLifetimeAuditBordxTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private HttpClient Cli(params int[] positions) => factory.ClientFor(WriteApiFactory.Access(positions.Length == 0 ? new[] { 11, 16, 154 } : positions));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Member(string number, string topup, int yearsAgo)
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMPayorEffDate, MBMPayorExpDate, MBMAvailableLimit, MBMTopupInd, MBMTopupNumber)
            VALUES (@number, '00', 'A', 'LIFETIME TEST', 'IC1', 'WM', 'N', 'F', 'ZZP1', 'POLLT1', DATEADD(year, -@yearsAgo, CAST(GETDATE() AS date)), DATEADD(day, 364, DATEADD(year, -@yearsAgo, CAST(GETDATE() AS date))), 10000, 'N', @topup)
            """, new { number, topup, yearsAgo });
        await main.ExecuteAsync("INSERT INTO MBMOthers (MBMNumber) VALUES (@number)", new { number });
        await using var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        await maint.ExecuteAsync("INSERT INTO MBMCrossReference (MBMNumber, MBMTopupNumber) VALUES (@number, @topup)", new { number, topup });
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    private async Task<string> RegisteredCase(string member)
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMPayorEffDate, MBMPayorExpDate, MBMAvailableLimit, MBMTopupInd)
                VALUES (@member, '00', 'A', 'AUDIT TEST', 'IC1', 'WM', 'N', 'F', 'ZZP1', 'POLAR1', DATEADD(day, -200, CAST(GETDATE() AS date)), DATEADD(day, 165, CAST(GETDATE() AS date)), 9000, 'N')
                """, new { member });
        var req = new CaseRegistrationRequest(member, "00", "AA", "00001", null, DateOnly.FromDateTime(DateTime.Today).AddDays(-3), null, "N", 5000m, 0m, null, "C", "I", "S", "ILL", null, "N", "N", null, null, "x", null, 2024, null, "N", 0m, null, null, "35", "Y", null, null, "N", "N", "N", "N", ["r"], [new("A00", "c", null)], null, false, false, false);
        var res = await Cli(11).PostAsJsonAsync("/api/case-registration", req);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<CaseRegistrationResult>())!.CaseNumber;
    }

    [Fact]
    public async Task Each_screen_needs_its_legacy_position()
    {
        var none = factory.ClientFor(WriteApiFactory.Access(13));
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/api/lifetime-limit?memberNumber=X&coverId=00")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/api/case-audit-review/WM00001")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/api/case-send-to-bordx/options")).StatusCode);
    }

    [DatabaseFact]
    public async Task Lifetime_limit_is_three_times_the_annual_limit_and_only_once()
    {
        const string topup = "TZLT00001";
        await Member("WMZLT01*01", topup, 2); await Member("WMZLT01*02", topup, 1); await Member("WMZLT01*03", topup, 0);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("INSERT INTO AnnualLimit (PLNCode, INSCode, HLTCode, PAYCode, AnnualEffDate, AnnLimit, DisabilityLmt) VALUES ('ZZP1', 'F', 'N', 'WM', '2000-01-01', 12000, 0)");
        var view = (await Cli().GetFromJsonAsync<LifetimeLimitView>("/api/lifetime-limit?memberNumber=WMZLT01*03&coverId=00", Json))!;
        Assert.Equal(3, view.Memberships.Count); Assert.True(view.Qualified); Assert.False(view.Allocated); Assert.False(view.HasClaimsHistory);
        var res = await Cli().PostAsync("/api/lifetime-limit?memberNumber=WMZLT01*03&coverId=00", null);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Assert.Equal(36000m, await Scalar<decimal>(GenesisDb.Main, "SELECT MBMLifeTimeLmt FROM MBMLifeTime WHERE MBMNumber = @topup AND MBMCovID = '00'", new { topup }));
        Assert.Equal("3", (await Scalar<string>(GenesisDb.Main, "SELECT MBMGuaRenewal FROM MBMOthers WHERE MBMNumber = 'WMZLT01*03'"))?.Trim());
        Assert.Equal(HttpStatusCode.BadRequest, (await Cli().PostAsync("/api/lifetime-limit?memberNumber=WMZLT01*03&coverId=00", null)).StatusCode);
        Assert.Equal("Member.LifetimeLimit", factory.Audit.Entries.Last().Action);
    }

    [DatabaseFact]
    public async Task Lifetime_limit_is_refused_with_fewer_than_three_memberships()
    {
        await Member("WMZLT02*01", "TZLT00002", 1); await Member("WMZLT02*02", "TZLT00002", 0);
        Assert.False((await Cli().GetFromJsonAsync<LifetimeLimitView>("/api/lifetime-limit?memberNumber=WMZLT02*02&coverId=00", Json))!.Qualified);
        var res = await Cli().PostAsync("/api/lifetime-limit?memberNumber=WMZLT02*02&coverId=00", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("not qualified", await res.Content.ReadAsStringAsync());
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMLifeTime WHERE MBMNumber = 'TZLT00002'"));
    }

    [DatabaseFact]
    public async Task Audit_review_is_added_then_updated_and_stamps_the_cross_reference()
    {
        var no = await RegisteredCase("WMZAR01*01");
        var first = new CaseAuditRequest("O", "A", "INV-1", DateOnly.FromDateTime(DateTime.Today), 1200m, null, null, DateOnly.FromDateTime(DateTime.Today), "10:30", "MS LEE", "visit", 50m, null, DateOnly.FromDateTime(DateTime.Today), null, null, "first");
        var res = await Cli(16).PutAsJsonAsync($"/api/case-audit-review/{no}", first);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CASAuditReview WHERE CASNumber = @no AND CASARStatus = 'O' AND CASDiscType = 'A' AND CASLastUpdateUser = 'TEST'", new { no }));
        Assert.Equal("O", (await Scalar<string>(GenesisDb.Maintenance, "SELECT CASARStatus FROM CASCrossReference WHERE CASNumber = @no", new { no }))?.Trim());
        var view = (await Cli(16).GetFromJsonAsync<CaseAuditView>($"/api/case-audit-review/{no}", Json))!;
        Assert.Equal("INV-1", view.Audit!.InvoiceNo); Assert.Equal("10:30", view.Audit.VisitTime); Assert.Equal(DateOnly.FromDateTime(DateTime.Today), view.Audit.VisitDate);
        res = await Cli(16).PutAsJsonAsync($"/api/case-audit-review/{no}", first with { Status = "C", DiscountType = "B", ReviewAudit = "second" });
        Assert.True((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updated").GetBoolean());
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM CASAuditReview WHERE CASNumber = @no", new { no }));
        Assert.Equal("C", (await Scalar<string>(GenesisDb.Maintenance, "SELECT CASARStatus FROM CASCrossReference WHERE CASNumber = @no", new { no }))?.Trim());
        Assert.Equal(HttpStatusCode.BadRequest, (await Cli(16).PutAsJsonAsync($"/api/case-audit-review/{no}", first with { DiscountType = null })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Cli(16).GetAsync("/api/case-audit-review/NOCASE01")).StatusCode);
    }

    [DatabaseFact]
    public async Task Send_to_bordx_passes_ticked_versions_in_a_daily_batch_and_amend_can_take_them_back()
    {
        var no = await RegisteredCase("WMZBX01*01");
        await using (var w = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            await w.ExecuteAsync("DELETE FROM Claim WHERE CASNumber = @no; INSERT INTO Claim (CASNumber, ClaimVersion, CLMTotalApproved, CLMWSStatus) VALUES (@no, 1, 500, 'AA')", new { no });
        var search = await Cli(154).PostAsJsonAsync("/api/case-send-to-bordx/search", new { caseNoFrom = no, exactCase = true });
        Assert.True(search.StatusCode == HttpStatusCode.OK, await search.Content.ReadAsStringAsync());
        var row = Assert.Single((await search.Content.ReadFromJsonAsync<BordxSearchResult>(Json))!.Rows);
        Assert.False(row.Passed); Assert.Equal(1, row.Version);
        var before = await Scalar<int?>(GenesisDb.Maintenance, "SELECT PASSSeq FROM CASPASSSeq WHERE DPO = CAST(GETDATE() AS date) AND PASSBy = 'TEST'") ?? 0;
        var pass = await Cli(154).PostAsJsonAsync("/api/case-send-to-bordx/pass", new { items = new[] { new { caseNumber = no, version = 1 } } });
        Assert.True(pass.StatusCode == HttpStatusCode.OK, await pass.Content.ReadAsStringAsync());
        Assert.Equal((before + 1).ToString(), (await pass.Content.ReadFromJsonAsync<BordxPassResult>(Json))!.Batch);
        Assert.Equal(1, await Scalar<int>(GenesisDb.Worksheet, "SELECT COUNT(*) FROM ClaimOthersTwo WHERE CASNumber = @no AND ClaimVersion = 1 AND CASClaimAdmin = '1' AND CASUserToken = 'TEST' AND CAST(CASTokenDate AS date) = CAST(GETDATE() AS date)", new { no }));
        Assert.True(Assert.Single((await (await Cli(154).PostAsJsonAsync("/api/case-send-to-bordx/search", new { caseNoFrom = no, exactCase = true })).Content.ReadFromJsonAsync<BordxSearchResult>(Json))!.Rows).Passed);
        Assert.Equal(HttpStatusCode.OK, (await Cli(154).PutAsJsonAsync($"/api/case-send-to-bordx/{no}/1", new { dpo = (DateOnly?)null })).StatusCode);
        Assert.Equal("0", (await Scalar<string>(GenesisDb.Worksheet, "SELECT CASClaimAdmin FROM ClaimOthersTwo WHERE CASNumber = @no AND ClaimVersion = 1", new { no }))?.Trim());
        Assert.Equal(HttpStatusCode.BadRequest, (await Cli(154).PutAsJsonAsync($"/api/case-send-to-bordx/{no}/1", new { dpo = DateOnly.FromDateTime(DateTime.Today) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Cli(154).PostAsJsonAsync("/api/case-send-to-bordx/pass", new { items = new[] { new { caseNumber = no, version = 9 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Cli(154).PostAsJsonAsync("/api/case-send-to-bordx/search", new { })).StatusCode);
    }
}
