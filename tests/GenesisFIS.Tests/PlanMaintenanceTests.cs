using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Maintenance;

namespace GenesisFIS.Tests;

public class PlanMaintenanceTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Test codes for the drop-down checks (inside the rolled-back test transaction). ZZP9 and ZZP8 carry a management fee, ZZP1 does not.</summary>
    private async Task SeedCodesAsync()
    {
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Main))
            await conn.ExecuteAsync("""
                INSERT INTO HLT (HLTCode, HLTDescription) VALUES ('ZZ', 'TEST HEALTH');
                INSERT INTO INS (INSCode, INSDescription) VALUES ('ZZ', 'TEST INSURED'), ('ZY', 'TEST INSURED 2');
                INSERT INTO PAY (PAYCode, PAYCompanyName) VALUES ('ZZ', 'TEST PAYOR');
                INSERT INTO AGE (AGECode, AGEDescription) VALUES ('05', 'TEST AGE');
                INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, PLNMgmtFee) VALUES ('ZZP1', 'ZZ', 'ZZ', '2023-01-01', 'N'), ('ZZP9', 'ZZ', 'ZZ', '2023-01-01', 'Y'), ('ZZP8', 'ZZ', 'ZZ', '2023-01-01', 'Y');
                """);
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Maintenance))
            await conn.ExecuteAsync("INSERT INTO ProductCategory (ProductCode, ProductName) VALUES ('ZZ', 'TEST PRODUCT'); INSERT INTO PolicyCategory (PolicyWording) VALUES ('ZZ TEST WORDING');");
    }

    /// <summary>Every field the legacy SavePLN required, filled with valid values.</summary>
    private static Dictionary<string, string> NewPlan() => new()
    {
        ["code"] = "ZZP2", ["description"] = "TEST PLAN", ["health"] = "ZZ", ["payor"] = "ZZ", ["group"] = "TEST GROUP", ["effective"] = "2024-01-01",
        ["topUp"] = "N", ["coPayment"] = "N", ["specialGPeriod"] = "N", ["nursing"] = "Y", ["mgmtFee"] = "N", ["startAge"] = "15", ["endAge"] = "60",
        ["productCategory"] = "ZZ", ["meals"] = "Y", ["mri"] = "Y", ["tax"] = "N", ["annLimitInd"] = "Y", ["premiumInd"] = "Y", ["mcoInd"] = "Y",
        ["lifeTime"] = "N", ["disIndicator"] = "N", ["wording"] = "ZZ TEST WORDING",
    };
    public Task DisposeAsync() => factory.ResetAsync();
    private HttpClient Client(int position) => factory.ClientFor(WriteApiFactory.Access(position));
    private static Dictionary<string, string> Premium(string amount = "1000", string effective = "2023-01-01", string version = "3") => new()
        { ["insured"] = "ZZ", ["health"] = "N", ["payor"] = "ZZ", ["plan"] = "ZZP1", ["age"] = "05", ["effective"] = effective, ["amount"] = amount, ["suppAmount"] = "400", ["suppStatus"] = "C", ["version"] = version };

    [Fact]
    public void Every_plan_tab_is_catalogued_with_its_legacy_position()
    {
        var plan = LookupCatalog.All.Where(d => d.Id.StartsWith("plan-")).ToDictionary(d => d.Id);
        Assert.Equal(10, plan.Count);
        Assert.Equal((80, 81, 82, 83, 84, 85), (plan["plan-health"].ScreenPosition, plan["plan-insured"].ScreenPosition, plan["plan-code"].ScreenPosition, plan["plan-premium"].ScreenPosition, plan["plan-annual-limit"].ScreenPosition, plan["plan-mco-fee"].ScreenPosition));
        Assert.False(plan["plan-code"].AllowDelete);
        Assert.Contains(plan["plan-code"].Fields, f => f.Key == "specialNote" && f.Multiline);
    }

    [DatabaseFact]
    public async Task Premium_rows_accept_dates_money_and_integers_stamp_the_user_and_can_be_edited_and_deleted()
    {
        await SeedCodesAsync();
        var client = Client(83); var url = "/api/lookups/plan-premium/rows";
        var created = await client.PostAsJsonAsync(url, Premium());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var row = (await created.Content.ReadFromJsonAsync<LookupRow>())!;
        Assert.Equal(("2023-01-01", "1000.00", "3", "C"), (row.Values["effective"], row.Values["amount"], row.Values["version"], row.Values["suppStatus"]));
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Main))
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PRM WHERE PRMCode = @k AND PRMLastUpdateUser = 'TEST' AND PRMEffDate = '2023-01-01' AND PRMAmount = 1000 AND PRMVersion = 3", new { k = int.Parse(row.Key) }));

        var edit = await client.PutAsJsonAsync($"{url}?key={row.Key}", Premium("1200.5", "2024-02-29", "4"));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(("2024-02-29", "1200.50"), ((await edit.Content.ReadFromJsonAsync<LookupRow>())!.Values["effective"], "1200.50"));
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{url}?key={row.Key}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{url}?key={row.Key}")).StatusCode);
    }

    [DatabaseFact]
    public async Task Case_management_fee_has_a_three_column_key_that_cannot_change_and_cannot_be_duplicated()
    {
        await SeedCodesAsync();
        var client = Client(85); var url = "/api/lookups/plan-case-mgmt-fee/rows";
        var body = new Dictionary<string, string> { ["health"] = "ZZ", ["insured"] = "ZY", ["plan"] = "ZZP9", ["fee"] = "12.5" };
        var created = await client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var row = (await created.Content.ReadFromJsonAsync<LookupRow>())!;
        Assert.Equal("ZZ|ZY|ZZP9", row.Key);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, body)).StatusCode);
        body["plan"] = "ZZP8";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, body)).StatusCode);

        var change = new Dictionary<string, string> { ["fee"] = "20" };
        var edit = await client.PutAsJsonAsync($"{url}?key={Uri.EscapeDataString(row.Key)}", change);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("20.00", (await edit.Content.ReadFromJsonAsync<LookupRow>())!.Values["fee"]);
        change["plan"] = "OTHR";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}?key={Uri.EscapeDataString(row.Key)}", change)).StatusCode);
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Maintenance))
        {
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM CaseMgmtFee WHERE MgmtHLTCode = 'ZZ' AND MgmtINSCode = 'ZY' AND MgmtPLNCode = 'ZZP9' AND MgmtFee = 20 AND MgmtLastUpdateUser = 'TEST'"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM CaseMgmtFee WHERE MgmtPLNCode = 'ZZP8' AND MgmtFee = 12.5"));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"{url}?key=ZZ")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{url}?key={Uri.EscapeDataString(row.Key)}")).StatusCode);
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Maintenance))
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM CaseMgmtFee WHERE MgmtHLTCode = 'ZZ'"));
    }

    [DatabaseFact]
    public async Task Invalid_dates_integers_and_amounts_are_refused()
    {
        await SeedCodesAsync();
        var client = Client(83); var url = "/api/lookups/plan-premium/rows";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, Premium(effective: "31/12/2023"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, Premium(effective: "2023-02-30"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, Premium(version: "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, Premium(amount: "-5"))).StatusCode);
        var noDate = Premium(); noDate["effective"] = "";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, noDate)).StatusCode);
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PRM WHERE PLNCode = 'ZZP1'"));
    }

    [DatabaseFact]
    public async Task The_plan_tab_cannot_delete_plans_and_other_positions_are_refused()
    {
        var plan = (await Client(82).GetFromJsonAsync<List<LookupRow>>("/api/lookups/plan-code/rows"))!;
        Assert.NotEmpty(plan);
        var del = await Client(82).DeleteAsync($"/api/lookups/plan-code/rows?key={plan[0].Key}");
        Assert.NotEqual(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(3).GetAsync("/api/lookups/plan-premium/rows")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(84).GetAsync("/api/lookups/plan-premium/rows")).StatusCode);
    }

    [DatabaseFact]
    public async Task Plan_code_edits_keep_the_special_note_and_validate_the_choices()
    {
        var rows = (await Client(82).GetFromJsonAsync<List<LookupRow>>("/api/lookups/plan-code/rows"))!;
        await SeedCodesAsync();
        var values = rows[0].Values.ToDictionary(k => k.Key, k => k.Value ?? "");
        // The restored plan predates several fields the legacy form made mandatory; fill the gaps as a user would.
        foreach (var (k, v) in NewPlan().Where(p => p.Key is not ("code" or "health" or "payor" or "group" or "effective" or "description")))
            if (values[k].Length == 0) values[k] = v;
        values["specialNote"] = "Rolled back note"; values["proRate"] = "Y";
        var ok = await Client(82).PutAsJsonAsync($"/api/lookups/plan-code/rows?key={rows[0].Key}", values);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Rolled back note", (await ok.Content.ReadFromJsonAsync<LookupRow>())!.Values["specialNote"]);
        values["proRate"] = "Q";
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(82).PutAsJsonAsync($"/api/lookups/plan-code/rows?key={rows[0].Key}", values)).StatusCode);
    }

    [Fact]
    public void Plan_drop_downs_use_the_legacy_code_tables()
    {
        var plan = LookupCatalog.Find("plan-code")!.Fields.ToDictionary(f => f.Key);
        Assert.Equal(("HLT", "PAY", "ProductCategory", "PolicyCategory"), (plan["health"].Source!.Table, plan["payor"].Source!.Table, plan["productCategory"].Source!.Table, plan["wording"].Source!.Table));
        Assert.All(new[] { "topUp", "coPayment", "specialGPeriod", "nursing", "mgmtFee", "meals", "mri", "tax", "annLimitInd", "premiumInd", "mcoInd", "lifeTime", "disIndicator" },
            k => Assert.True(plan[k].Required && plan[k].Choices!.SequenceEqual(["Y", "N"]), k));
        Assert.Equal("PLNMgmtFee = 'Y'", LookupCatalog.Find("plan-case-mgmt-fee")!.Fields.Single(f => f.Key == "plan").Source!.Filter);
        Assert.Equal("SuppMCOStatus", LookupCatalog.Find("plan-mco-fee")!.Fields.Single(f => f.Key == "suppStatus").Source!.Table);
    }

    [DatabaseFact]
    public async Task Drop_down_lists_come_from_the_code_tables()
    {
        await SeedCodesAsync();
        var payors = (await Client(82).GetFromJsonAsync<List<LookupOption>>("/api/lookups/plan-code/options/payor"))!;
        Assert.Contains(payors, o => o.Value == "ZZ" && o.Label == "TEST PAYOR");
        Assert.Contains((await Client(82).GetFromJsonAsync<List<LookupOption>>("/api/lookups/plan-code/options/wording"))!, o => o.Value == "ZZ TEST WORDING" && o.Label is null);
        Assert.Contains((await Client(82).GetFromJsonAsync<List<LookupOption>>("/api/lookups/plan-code/options/productCategory"))!, o => o.Value == "ZZ");

        // Case management fee only offers plans that have a management fee; codes are listed once even when a plan has several versions.
        var mgmtPlans = (await Client(85).GetFromJsonAsync<List<LookupOption>>("/api/lookups/plan-case-mgmt-fee/options/plan"))!;
        Assert.Contains(mgmtPlans, o => o.Value == "ZZP9");
        Assert.DoesNotContain(mgmtPlans, o => o.Value == "ZZP1");
        Assert.Equal(mgmtPlans.Count, mgmtPlans.Select(o => o.Value).Distinct().Count());

        Assert.Equal(HttpStatusCode.NotFound, (await Client(82).GetAsync("/api/lookups/plan-code/options/group")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(3).GetAsync("/api/lookups/plan-code/options/payor")).StatusCode);
    }

    [DatabaseFact]
    public async Task A_plan_needs_the_legacy_mandatory_fields_and_known_codes()
    {
        await SeedCodesAsync();
        var client = Client(82); var url = "/api/lookups/plan-code/rows";
        foreach (var (field, bad) in new[] { ("payor", "Q9"), ("health", "Q"), ("productCategory", "Q9"), ("wording", "NO SUCH WORDING"), ("meals", "") })
        {
            var body = NewPlan(); body[field] = bad;
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, body)).StatusCode);
        }
        var grace = NewPlan(); grace["specialGPeriod"] = "Y";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, grace)).StatusCode);
        grace["specialGPeriodDays"] = "30";
        var created = await client.PostAsJsonAsync(url, grace);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var row = (await created.Content.ReadFromJsonAsync<LookupRow>())!;
        Assert.Equal(("ZZ", "ZZ TEST WORDING", "30", "Y"), (row.Values["payor"], row.Values["wording"], row.Values["specialGPeriodDays"], row.Values["specialGPeriod"]));

        // A premium row refuses a plan that is not in PLN.
        var prm = Premium(); prm["plan"] = "QQQQ";
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(83).PostAsJsonAsync("/api/lookups/plan-premium/rows", prm)).StatusCode);
    }

    private static object Batch(Dictionary<string, string> values, int count, params object[] plans) => new { values, count, plans };

    private static Dictionary<string, string> Shared(string health)
    {
        var v = NewPlan(); v.Remove("code"); v.Remove("description"); v["health"] = health; return v;
    }

    [DatabaseFact]
    public async Task Health_type_N_generates_the_plan_codes_from_the_group_company_letter()
    {
        await SeedCodesAsync();
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Main))
            await conn.ExecuteAsync("INSERT INTO HLT (HLTCode, HLTDescription) SELECT 'N', 'NON' WHERE NOT EXISTS (SELECT 1 FROM HLT WHERE HLTCode = 'N'); DELETE FROM PLNSeq WHERE PLNSeqCode IN ('9', '8'); INSERT INTO PLNSeq (PLNSeqCode, PLNNoSeq) VALUES ('9', '041');");
        var values = Shared("N"); values["group"] = "9 TEST GROUP";
        var res = await Client(82).PostAsJsonAsync("/api/lookups/plan-code/batch", Batch(values, 3, new { description = "FIRST" }, new { description = "SECOND" }));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var rows = (await res.Content.ReadFromJsonAsync<List<LookupRow>>())!;
        Assert.Equal(["9042", "9043", "9044"], rows.Select(r => r.Values["code"]));
        Assert.Equal(["FIRST", "SECOND", null], rows.Select(r => r.Values["description"]));
        Assert.All(rows, r => Assert.Equal(("9 TEST GROUP", "ZZ TEST WORDING"), (r.Values["group"], r.Values["wording"])));

        // A letter with no counter yet starts at 001.
        values["group"] = "8 GROUP";
        rows = (await (await Client(82).PostAsJsonAsync("/api/lookups/plan-code/batch", Batch(values, 1))).Content.ReadFromJsonAsync<List<LookupRow>>())!;
        Assert.Equal("8001", rows.Single().Values["code"]);
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Main))
            Assert.Equal(("044", "001"), (await conn.ExecuteScalarAsync<string>("SELECT PLNNoSeq FROM PLNSeq WHERE PLNSeqCode = '9'"), await conn.ExecuteScalarAsync<string>("SELECT PLNNoSeq FROM PLNSeq WHERE PLNSeqCode = '8'")));

        Assert.Equal(HttpStatusCode.BadRequest, (await Client(82).PostAsJsonAsync("/api/lookups/plan-code/batch", Batch(values, 7))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(82).PostAsJsonAsync("/api/lookups/plan-code/batch", Batch(values, 1, new { code = "MINE" }))).StatusCode);
    }

    [DatabaseFact]
    public async Task Other_health_types_save_every_typed_code_in_one_transaction()
    {
        await SeedCodesAsync();
        var values = Shared("ZZ");
        var res = await Client(82).PostAsJsonAsync("/api/lookups/plan-code/batch", Batch(values, 0, new { code = "ZZA1", description = "A" }, new { code = "", description = "skipped" }, new { code = "ZZA2" }));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(["ZZA1", "ZZA2"], (await res.Content.ReadFromJsonAsync<List<LookupRow>>())!.Select(r => r.Values["code"]));

        // Same code on the same effective date is refused, and nothing of the batch is kept.
        var dup = await Client(82).PostAsJsonAsync("/api/lookups/plan-code/batch", Batch(values, 0, new { code = "ZZA3" }, new { code = "ZZA1" }));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        await using (var conn = await factory.Db.OpenAsync(GenesisDb.Main))
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PLN WHERE PLNCode = 'ZZA3'"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(82).PostAsJsonAsync("/api/lookups/plan-code/batch", Batch(values, 0, new { code = "ZZA4" }, new { code = "ZZA4" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(82).PostAsJsonAsync("/api/lookups/plan-code/batch", Batch(values, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(83).PostAsJsonAsync("/api/lookups/plan-code/batch", Batch(values, 0, new { code = "ZZA5" }))).StatusCode);
    }
}
