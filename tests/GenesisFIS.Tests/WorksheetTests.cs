using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using Microsoft.Data.SqlClient;

namespace GenesisFIS.Tests;

public class WorksheetCatalogTests
{
    [Fact]
    public void Grid_rows_are_numbered_once_and_format_like_the_legacy_grid()
    {
        Assert.Equal(Enumerable.Range(1, 29), WorksheetCatalog.Lines.Select(l => l.Row));
        Assert.Equal(("-", "1,234.50", ".50"), (WorksheetCatalog.Format(0), WorksheetCatalog.Format(1234.5m), WorksheetCatalog.Format(0.5m)));
        Assert.True(WorksheetCatalog.UsesCombinedRows("3AB1"));
        Assert.False(WorksheetCatalog.UsesCombinedRows("N001"));
    }

    /// <summary>
    /// The catalog's invoice-to-row mapping, checked against the worksheets the legacy system saved: for recent completed worksheets, the invoices
    /// of that version must add up to the stored actual (all invoices) and hospital (invoices paid to the hospital) amounts of every row.
    /// </summary>
    [DatabaseFact]
    public void Invoice_totals_per_row_match_the_saved_legacy_worksheets()
    {
        using var conn = new SqlConnection(TestHost.Config()["ConnectionStrings:Worksheet"]);
        conn.Open();
        var cases = conn.Query<(string Case, int Version)>("""
            SELECT TOP 400 c.CASNumber, c.ClaimVersion FROM Claim c
            WHERE c.ClaimStatus = 'C' AND EXISTS (SELECT 1 FROM ClaimInvoice i WHERE i.CASNumber = c.CASNumber AND i.INVWSversion = CAST(c.ClaimVersion AS nvarchar))
            ORDER BY c.ClaimOpenDate DESC
            """).ToList();
        if (cases.Count == 0) return;

        var checkedCells = 0; var mismatches = new List<string>();
        foreach (var (no, version) in cases)
        {
            var actual = conn.QuerySingle("SELECT * FROM ClaimActual WHERE CASNumber = @no AND CLMVersion = @version", new { no, version }) as IDictionary<string, object?>;
            var hos = conn.QuerySingleOrDefault("SELECT * FROM ClaimHos WHERE CASNumber = @no AND CLMVersion = @version", new { no, version }) as IDictionary<string, object?>;
            var sums = (IDictionary<string, object?>)conn.QuerySingle($"""
                SELECT {string.Join(", ", WorksheetCatalog.Lines.SelectMany(l => new[]
                {
                    $"SUM({string.Join(" + ", l.InvoiceColumns.Select(c => $"ISNULL(a.{c}, 0)"))}) AS a{l.Row}",
                    $"SUM({string.Join(" + ", (l.CombinedInvoiceColumns ?? l.InvoiceColumns).Select(c => $"ISNULL(a.{c}, 0)"))}) AS c{l.Row}",
                    $"SUM(CASE WHEN i.INVPayTo = 'H' THEN {string.Join(" + ", l.InvoiceColumns.Select(c => $"ISNULL(a.{c}, 0)"))} ELSE 0 END) AS h{l.Row}",
                }))}
                FROM ClaimInvoice i JOIN ClaimInvoiceActual a ON a.INVIndex = i.INVIndex WHERE i.CASNumber = @no AND i.INVWSversion = @v
                """, new { no, v = version.ToString(CultureInfo.InvariantCulture) });
            static decimal Parse(object? cell) => decimal.TryParse(Convert.ToString(cell)?.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0;
            foreach (var l in WorksheetCatalog.Lines)
            {
                if (!actual.ContainsKey(l.Column)) continue;
                var stored = Parse(actual[l.Column]);
                var matches = stored == Convert.ToDecimal(sums["a" + l.Row]) || l.CombinedColumn is not null && Parse(actual[l.CombinedColumn]) == Convert.ToDecimal(sums["c" + l.Row]);
                checkedCells++;
                if (!matches) mismatches.Add($"{no}-{version} row {l.Row} actual");
                if (hos is not null && l.CombinedColumn is null && Parse(hos[l.Column]) != Convert.ToDecimal(sums["h" + l.Row])) mismatches.Add($"{no}-{version} row {l.Row} hospital");
            }
        }
        // A few legacy worksheets were edited by hand after saving; anything beyond that means the mapping is wrong.
        Assert.True(mismatches.Count <= checkedCells / 200, $"{mismatches.Count} of {checkedCells} cells differ: {string.Join("; ", mismatches.Take(10))}");
    }
}

public class WorksheetTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Case = "ZZWKS00001";
    private HttpClient Client(params int[] positions) => factory.ClientFor(WriteApiFactory.Access(positions));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task SeedAsync(string plan = "N001")
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMAvailableLimit, MBMTopupInd)
                VALUES ('ZZWKS0001*01', '00', 'A', 'WORKSHEET PRINCIPAL', 'IC1', 'WM', 'N', 'F', @plan, 'POLWKS', 20000, 'N');
                INSERT INTO CAS (CASNumber, CASClaimAdmin, MBMNumber, MBMPatientCovID, CASHOSCode, CASDateAdmitted, CASDischargeDate, CASStayType)
                VALUES (@case, 'N', 'ZZWKS0001*01', '00', '00001', '2024-05-01', '2024-05-04', 'I');
                """, new { @case = Case, plan });
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        await ws.ExecuteAsync("""
            DELETE FROM ClaimInvoice WHERE CASNumber = @case; DELETE FROM Claim WHERE CASNumber = @case;
            DELETE FROM ClaimActual WHERE CASNumber = @case; DELETE FROM ClaimLimit WHERE CASNumber = @case; DELETE FROM ClaimApprove WHERE CASNumber = @case;
            DELETE FROM ClaimHos WHERE CASNumber = @case; DELETE FROM ClaimMember WHERE CASNumber = @case;
            """, new { @case = Case });
    }

    private static CaseInvoiceRequest Invoice(string no, string payTo, Dictionary<string, decimal?> amounts) =>
        new(no, new DateOnly(2024, 5, 4), payTo, "I", payTo == "H" ? "00001" : null, null, payTo == "M" ? "PM" : null, payTo == "M" ? new DateOnly(2024, 5, 6) : null, null,
            payTo == "H" ? 0m : null, 100m, null, amounts, null, null, null, null, null);

    [Fact]
    public async Task Enquiry_and_registration_have_their_own_legacy_permissions()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(64).GetAsync($"/api/worksheet-enquiry/{Case}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(13).GetAsync($"/api/worksheet-registration/{Case}/preview")).StatusCode);
    }

    [DatabaseFact]
    public async Task Preview_adds_up_the_waiting_invoices_per_row_and_the_hospital_share()
    {
        await SeedAsync();
        var inv = Client(22);
        Assert.Equal(HttpStatusCode.Created, (await inv.PostAsJsonAsync($"/api/case-invoices/{Case}", Invoice("H1", "H", new() { ["boardDays"] = 2, ["roomRate"] = 100, ["roomTax"] = 12, ["services"] = 300 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await inv.PostAsJsonAsync($"/api/case-invoices/{Case}", Invoice("M1", "M", new() { ["services"] = 50, ["cashDays"] = 2, ["cashRate"] = 30 }))).StatusCode);

        var p = (await Client(64).GetFromJsonAsync<WorksheetPreview>($"/api/worksheet-registration/{Case}/preview"))!;
        Assert.Equal((1, 2, false), (p.Version, p.Invoices.Count, p.CombinedRows));
        var rows = p.Calculation.Lines.ToDictionary(l => l.Row);
        Assert.Equal(("212.00", "212.00"), (rows[1].Actual, rows[1].Hospital));           // room & board includes its new tax
        Assert.Equal(("350.00", "300.00"), (rows[3].Actual, rows[3].Hospital));           // services: both invoices, hospital share only H
        Assert.Equal(("60.00", "-"), (rows[19].Actual, rows[19].Hospital));               // government hospital cash, member invoice
        Assert.Equal((622m, 512m), (p.Calculation.TotalActual, p.Calculation.TotalHospital));
        Assert.Equal(0m, p.Calculation.TotalApproved);                                     // plan N001 has no benefits defined
    }

    [DatabaseFact]
    public async Task Preview_applies_the_plan_benefits_and_typed_limits()
    {
        await SeedAsync("ZZW1");
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                INSERT INTO BNF (PLNCode, INSCode, HLTCode, BNFCode, BNFNoOfDays, BNFClaimAbleAmount, BNFRMPerDis, BNFEffDate)
                VALUES ('ZZW1', 'F', 'N', '101', 30, '160', NULL, '2020-01-01'), ('ZZW1', 'F', 'N', '103', NULL, NULL, 'FULL', '2020-01-01');
                """);
        Assert.Equal(HttpStatusCode.Created, (await Client(22).PostAsJsonAsync($"/api/case-invoices/{Case}", Invoice("H1", "H", new() { ["boardDays"] = 3, ["roomRate"] = 200, ["services"] = 900 }))).StatusCode);

        var p = (await Client(64).GetFromJsonAsync<WorksheetPreview>($"/api/worksheet-registration/{Case}/preview"))!;
        var rows = p.Calculation.Lines.ToDictionary(l => l.Row);
        Assert.Equal(("480.00", "600.00", "480.00", "(120.00)"), (rows[1].Limit, rows[1].Actual, rows[1].Approved, rows[1].Member));
        Assert.Equal(("FULL", "900.00"), (rows[3].Limit, rows[3].Approved));
        Assert.Equal((1380m, 1380m, -120m, true), (p.Calculation.TotalApproved, p.Calculation.PayableToHospital, p.Calculation.Excess, p.Claimable));

        var typed = await Client(64).PostAsJsonAsync($"/api/worksheet-registration/{Case}/calculate", new WorksheetCalculateRequest(new Dictionary<int, string> { [3] = "500" }));
        Assert.Equal(HttpStatusCode.OK, typed.StatusCode);
        Assert.Equal(980m, (await typed.Content.ReadFromJsonAsync<WorksheetPreview>())!.Calculation.TotalApproved);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(64).PostAsJsonAsync($"/api/worksheet-registration/{Case}/calculate", new WorksheetCalculateRequest(new Dictionary<int, string> { [3] = "lots" }))).StatusCode);

        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("UPDATE CAS SET CASClaimability = 'RR' WHERE CASNumber = @case", new { @case = Case });
        var rr = (await Client(64).GetFromJsonAsync<WorksheetPreview>($"/api/worksheet-registration/{Case}/preview"))!;
        Assert.Equal((false, 0m), (rr.Claimable, rr.Calculation.TotalApproved));
    }

    [DatabaseFact]
    public async Task Enquiry_shows_every_saved_version_as_the_legacy_grid()
    {
        await SeedAsync();
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            await ws.ExecuteAsync("""
                INSERT INTO Claim (CASNumber, ClaimVersion, ClaimStatus, CLMWSStatus, CLMTotalActual, CLMTotalApproved, CLMPayableByMedix2H, CLMPayableByMedix2M) VALUES (@case, 1, 'C', 'AA', 924, 900, 900, 0);
                INSERT INTO ClaimActual (CASNumber, CLMVersion, CLMBoardAmt, CLMServices) VALUES (@case, 1, '160.00', '764.00');
                INSERT INTO ClaimLimit (CASNumber, CLMVersion, CLMBoardAmt, CLMServices, CLMBoardDays) VALUES (@case, 1, '150.00', 'FULL', '1');
                INSERT INTO ClaimApprove (CASNumber, CLMVersion, CLMBoardAmt, CLMServices) VALUES (@case, 1, '150.00', '750.00');
                INSERT INTO ClaimHos (CASNumber, CLMVersion, CLMBoardAmt, CLMServices) VALUES (@case, 1, '150.00', '750.00');
                INSERT INTO ClaimMember (CASNumber, CLMVersion, CLMBoardAmt, CLMServices) VALUES (@case, 1, '10.00', '14.00');
                """, new { @case = Case });
        var e = (await Client(13).GetFromJsonAsync<WorksheetEnquiry>($"/api/worksheet-enquiry/{Case}"))!;
        var v = Assert.Single(e.Versions);
        Assert.Equal((1, "AA", "Accepted", 900m), (v.Version, v.WorksheetStatus, v.WorksheetStatusName, v.TotalApproved));
        var room = v.Lines.Single(l => l.Row == 1);
        Assert.Equal(("150.00", "160.00", "150.00", "150.00", "10.00"), (room.Limit, room.Actual, room.Approved, room.Hospital, room.Member));
        Assert.Equal("FULL", v.Lines.Single(l => l.Row == 3).Limit);
        Assert.Equal("1", v.LimitDays["CLMBoardDays"]);
        Assert.Equal(HttpStatusCode.NotFound, (await Client(13).GetAsync("/api/worksheet-enquiry/NOCASE999")).StatusCode);
    }
}
