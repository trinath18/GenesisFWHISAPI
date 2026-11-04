using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;

namespace GenesisFIS.Tests;

/// <summary>Claims file lines, field by field as the legacy form read them. Payor "ZZ" and "ZZ..." keys keep clear of real data; hospital 060100 has KodPTJ 060101.</summary>
internal static class SppaUploadSamples
{
    public const string FileName = "TEFWMCH900001.TXT";

    public static string Line(string fw = "ZZFW1", string esp = "ZZESP1", string bill = "ZZB-1", string admitted = "20240301", string discharged = "20240303",
        string amount = "1234.50", string payor = "ZZ", string lodged = "20240304") => string.Join("|",
        "T1", "060100", payor, "ZZPOL", fw, "ZZPOLNO", "20240101", "", "ZZP-" + fw, "ZZ WORKER " + fw, "", "", esp, admitted, discharged, "FEVER", bill, "20240303",
        amount, lodged, "LODGER", "A09", "ILL", "MRN1", "0", "0", "20240305");

    public static SppaUploadRequest File(params string[] lines) => new(FileName, string.Join("\r\n", lines) + "\r\n");
}

public class SppaClaimUploadRuleTests
{
    private static SppaUploadLine Parsed(string line)
    {
        var issues = new List<SppaUploadIssue>();
        var l = Assert.Single(SppaClaimUpload.Parse(line, issues));
        Assert.Empty(issues);
        return l;
    }

    [Fact]
    public void Only_a_CH_file_is_a_claims_file()
    {
        Assert.True(SppaClaimUpload.IsClaimsFile("TEFWMCH002345.TXT"));
        Assert.True(SppaClaimUpload.IsClaimsFile("etfwmch1.txt"));
        Assert.False(SppaClaimUpload.IsClaimsFile("ETFWMMW002812.TXT"));
        Assert.False(SppaClaimUpload.IsClaimsFile("CH.TXT"));
        Assert.Equal("Please Insert File", Assert.Single(SppaClaimUpload.CheckFile(new(null, "x"))).Message);
        Assert.Contains(SppaClaimUpload.CheckFile(new("TEFWMCH1.TXT", " ")), i => i.Message == "The file is empty");
        Assert.Contains(SppaClaimUpload.CheckFile(new("TEFWMMW1.TXT", "x")), i => i.Message.Contains("not an SPPA claims file"));
    }

    [Fact]
    public void Reads_the_fields_the_legacy_form_used()
    {
        var l = Parsed(SppaUploadSamples.Line());
        Assert.Equal(("060100", "ZZ", "ZZPOL", "ZZFW1", "ZZPOLNO"), (l.HospitalCode, l.PayorCode, l.PolicyUniqueNo, l.WorkerUniqueNo, l.PolicyNo));
        Assert.Equal((new DateTime(2024, 1, 1), new DateTime(2024, 3, 1), new DateTime(2024, 3, 3)), (l.PolicyEffDate, l.Admitted, l.Discharged));
        Assert.Equal(("ZZESP1", "ZZB-1", 1234.50m, "ILL", "MRN1"), (l.EspRefNo, l.BillNo, l.BilledAmount, l.NatureCode, l.MrnNo));
        Assert.Equal((new DateTime(2024, 3, 4), "LODGER", new DateTime(2024, 3, 5)), (l.BillLodgedDate, l.BillLodgedBy, l.LastDocDate));
        Assert.Null(Parsed(SppaUploadSamples.Line(lodged: "")).BillLodgedDate);
    }

    [Fact]
    public void Lines_the_legacy_form_failed_on_are_issues()
    {
        var issues = new List<SppaUploadIssue>();
        var lines = SppaClaimUpload.Parse(string.Join("\n", "A|B|C", SppaUploadSamples.Line(admitted: "2024XX01"), SppaUploadSamples.Line(amount: ""),
            SppaUploadSamples.Line(admitted: "20240305", discharged: "20240301"), SppaUploadSamples.Line()), issues);
        Assert.Single(lines);
        Assert.Equal(5, lines[0].Line);
        Assert.Equal([1, 2, 3, 4], issues.Select(i => i.Line));
        Assert.Contains("needs 27", issues[0].Message);
        Assert.Contains("Date Admitted '2024XX01'", issues[1].Message);
        Assert.Equal("Billed Amount Is Blank", issues[2].Message);
        Assert.Equal("Date Discharged is before Date Admitted", issues[3].Message);
    }

    [Fact]
    public void Room_and_board_is_76_a_day_counting_both_ends()
    {
        var l = Parsed(SppaUploadSamples.Line());
        Assert.Equal(3, SppaClaimUpload.RoomBoardDays(l));
        Assert.Equal(228m, SppaClaimUpload.RoomBoardLimit(l));
    }

    [Fact]
    public void The_claim_id_is_42_kodptj_and_the_bill_number_right_aligned()
    {
        // Real FWHS rows: JE23896 (bill 2122408B) and BS07314 (bill 2123577B) at hospital 060100.
        Assert.Equal("42060101122408B", SppaClaimUpload.ClaimId("060101", "JE", "2122408B"));
        Assert.Equal("4206010100002123577B", SppaClaimUpload.ClaimId("060101", "BS", "2123577B"));
        Assert.Equal("4206010100000INV1234", SppaClaimUpload.ClaimId("060101", "MA", "INV-12/3 4"));
        Assert.Equal("420601012345678", SppaClaimUpload.ClaimId("060101", "KU", "9912345678"));
        Assert.Equal("", SppaClaimUpload.ClaimId("", "BS", "2123577B"));
    }

    [Fact]
    public void The_balance_limit_takes_off_the_last_bordereau_amount_read()
    {
        Assert.Equal(10_000m, SppaClaimUpload.BalanceLimit(new DateTime(2016, 7, 31), []));
        Assert.Equal(20_000m, SppaClaimUpload.BalanceLimit(new DateTime(2016, 8, 1), []));
        Assert.Equal(19_300m, SppaClaimUpload.BalanceLimit(new DateTime(2020, 1, 1), [new("A1", 1, 500), new("A2", 1, 700)]));
        Assert.Equal(20_000m, SppaClaimUpload.BalanceLimit(new DateTime(2020, 1, 1), [new("A1", 1, 500), new("A2", 1, 0)]));
        Assert.Equal(7_500m, SppaClaimUpload.BalanceLimit(new DateTime(2015, 1, 1), [new("A1", 2, 2_500)]));
    }

    [Fact]
    public void Plan_registers_an_admission_once_and_adds_its_other_bills()
    {
        var issues = new List<SppaUploadIssue>();
        var lines = SppaClaimUpload.Parse(string.Join("\n",
            SppaUploadSamples.Line(),
            SppaUploadSamples.Line(bill: "ZZB-2"),
            SppaUploadSamples.Line(bill: "ZZB-2"),
            SppaUploadSamples.Line(fw: "ZZNOBODY", esp: "ZZESP9"),
            SppaUploadSamples.Line(fw: "ZZFW2", esp: "ZZESP2"),
            SppaUploadSamples.Line(fw: "ZZFW3", esp: "ZZESP3")), issues);
        Assert.Empty(issues);
        var rows = SppaClaimUpload.Plan(lines,
            l => l.WorkerUniqueNo == "ZZNOBODY" ? null : "M-" + l.WorkerUniqueNo,
            l => l.WorkerUniqueNo is "ZZFW2" or "ZZFW3" ? "ZZ00009" : null,
            l => l.WorkerUniqueNo == "ZZFW3");
        Assert.Equal([SppaUploadAction.NewCase, SppaUploadAction.Invoice, SppaUploadAction.Skipped, SppaUploadAction.Skipped, SppaUploadAction.Skipped, SppaUploadAction.Skipped],
            rows.Select(r => r.Action));
        Assert.Equal("Case of line 1", rows[1].Message);
        Assert.Contains("already in the file", rows[2].Message);
        Assert.StartsWith("Member not found", rows[3].Message);
        Assert.Equal("The admission is already registered as case ZZ00009; add the bill in Invoice Processing", rows[4].Message);
        Assert.Equal("Already uploaded: bill ZZB-1 is on case ZZ00009", rows[5].Message);
        Assert.Equal("M-ZZFW1", rows[0].MemberNumber);
    }
}

public class SppaClaimUploadTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/case-ascii/sppa-upload";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(18));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task<T?> Scalar<T>(string sql, object? args = null, GenesisDb db = GenesisDb.Main)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    private async Task Member(string fw, string number)
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("""
            INSERT INTO MBM (PAYCode, HLTCode, MBMNumber, MBMPolicyNo, MBMCOVID, MBMIcBcPp, MBMName, MBMStatus, MBMInsurerUniqueNo, MBMFWUniqueNo, MBMPayorEffDate)
            VALUES ('ZZ', 'N', @number, 'ZZPOLNO', '00', 'ZZP', 'ZZ WORKER', 'A', 'ZZPOL', @fw, '2024-01-01')
            """, new { number, fw });
    }

    private static SppaUploadRequest File() => SppaUploadSamples.File(
        SppaUploadSamples.Line(), SppaUploadSamples.Line(bill: "ZZB-2", amount: "100"), SppaUploadSamples.Line(fw: "ZZNOBODY", esp: "ZZESP9"));

    [Fact]
    public async Task Needs_the_case_ascii_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"{Url}/preview", File())).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(1));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{Url}/preview", File())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Url, File())).StatusCode);
    }

    [DatabaseFact]
    public async Task Preview_says_what_each_line_does_and_writes_nothing()
    {
        await Member("ZZFW1", "ZZGZ0000001*01");
        var res = await Client().PostAsJsonAsync($"{Url}/preview", File());
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var p = (await res.Content.ReadFromJsonAsync<SppaUploadPreview>())!;
        Assert.True(p.CanUpload);
        Assert.Equal([SppaUploadAction.NewCase, SppaUploadAction.Invoice, SppaUploadAction.Skipped], p.Rows.Select(r => r.Action));
        Assert.Equal((1, 2, 1), (p.NewCases, p.Invoices, p.Skipped));
        Assert.Equal("ZZGZ0000001*01", p.Rows[0].MemberNumber);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM CAS WHERE CASESPRefNo = 'ZZESP1'"));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM CAQ WHERE PAYCode = 'ZZ'", db: GenesisDb.Maintenance));

        var bad = (await (await Client().PostAsJsonAsync($"{Url}/preview", new SppaUploadRequest("TEFWMMW1.TXT", "x"))).Content.ReadFromJsonAsync<SppaUploadPreview>())!;
        Assert.False(bad.CanUpload);
    }

    [DatabaseFact]
    public async Task Upload_registers_the_case_worksheet_and_invoices_as_the_legacy_form()
    {
        await Member("ZZFW1", "ZZGZ0000001*01");
        // An earlier claim of the member with RM 3,000 on a bordereau: the new worksheet gets 20,000 - 3,000.
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("INSERT INTO CAS (CasNumber, MBMNumber, MBMPatientCovID, CASStatus, CASDateAdmitted, CASESPRefNo) VALUES ('ZZ99999', 'ZZGZ0000001*01', '00', 'C', '2024-02-01', 'ZZOLD')");
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            await ws.ExecuteAsync("INSERT INTO Claim (CASNumber, ClaimVersion, CLMBordxAmt) VALUES ('ZZ99999', 1, 3000)");

        var res = await Client().PostAsJsonAsync(Url, File());
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var r = (await res.Content.ReadFromJsonAsync<SppaUploadResult>())!;
        Assert.Equal((1, 2, 1), (r.NewCases, r.Invoices, r.Skipped));
        Assert.Equal(["ZZ00001", "ZZ00001", null], r.Rows.Select(x => x.CaseNumber));
        Assert.Equal("00001", (await Scalar<string>("SELECT CAQNumber FROM CAQ WHERE PAYCode = 'ZZ'", db: GenesisDb.Maintenance))?.Trim());

        Assert.Equal(1, await Scalar<int>("""
            SELECT COUNT(*) FROM CAS WHERE CasNumber = 'ZZ00001' AND MBMNumber = 'ZZGZ0000001*01' AND MBMPatientCovID = '00' AND CASStatus = 'O' AND CASClaimability = 'AA'
              AND RTRIM(CASStayType) = 'I' AND RTRIM(CASPAYType) = 'C' AND ClaimNatureCode = 'ILL' AND CaseOpenBy = 'TEST' AND CaseRegDate = CAST(GETDATE() AS date)
              AND CASDateAdmitted = '2024-03-01' AND CASDischargeDate = '2024-03-03' AND CASHOSCode = '060100' AND CASPayorPolUnqNo = 'ZZPOL' AND CASPayorFWUnqNo = 'ZZFW1'
              AND CASPayorPolNo = 'ZZPOLNO' AND CASMRNNo = 'MRN1' AND CASBillNo = 'ZZB-1' AND CASBillDate = '2024-03-03' AND CASBillAmount = 1234.50
              AND CASBillRptDate = '2024-03-04' AND CASBillRptBy = 'LODGER' AND CASESPRefNo = 'ZZESP1' AND ClaimID = '4206010100000000ZZB1'
            """));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM DIAFirst WHERE CasNumber = 'ZZ00001'"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM DIAFinal WHERE CasNumber = 'ZZ00001'"));

        Assert.Equal(1, await Scalar<int>("""
            SELECT COUNT(*) FROM Claim WHERE CASNumber = 'ZZ00001' AND ClaimVersion = 1 AND ClaimStatus = 'O' AND RTRIM(CLMWSStatus) = 'O' AND ClaimOpenBy = 'TEST'
              AND ClaimBordxStatus = '0' AND CLMBordxWSStatus = 'N' AND CLMBordxWSASCII = 'N' AND LimitCollectedBy = 'N' AND ExcessCollectedBy = 'H' AND CLMCashType = 'C'
              AND CLMStayType = 'I' AND CLMExGratia = 'N' AND CLMLastDocDate = '2024-03-05' AND CLMBalanceLimit = 0 AND CLMPreviousBalance = 17000 AND CLMPreviousNewLimit = 17000
              AND ClaimCloseDate = CAST(GETDATE() AS date) AND CLMGPName = '0' AND ClaimID = '4206010100000000ZZB1'
            """, db: GenesisDb.Worksheet));
        Assert.Equal(1, await Scalar<int>("""
            SELECT COUNT(*) FROM ClaimLimit WHERE CasNumber = 'ZZ00001' AND CLMversion = 1 AND CLMBoardAmt = '228' AND CLMBoardDays = '3' AND CLMServices = 'FULL'
              AND CLMSurgicalAmt = 'FULL' AND CLMAmbulance = 'FULL' AND CLMMedical = 'FULL' AND CLMICUAmt = '0'
            """, db: GenesisDb.Worksheet));
        foreach (var table in new[] { "ClaimActual", "ClaimApprove", "ClaimHos", "ClaimMember" })
            Assert.Equal(1, await Scalar<int>($"SELECT COUNT(*) FROM {table} WHERE CasNumber = 'ZZ00001' AND CLMversion = 1", db: GenesisDb.Worksheet));

        Assert.Equal(2, await Scalar<int>("""
            SELECT COUNT(*) FROM ClaimInvoice WHERE CasNumber = 'ZZ00001' AND INVWSversion = '1' AND INVBordxed = 0 AND INVWSed = 1 AND INVEnteredBy = 'TEST' AND INVPayee = '060100'
              AND INVStayType = 'I' AND INVPAYType = 'H' AND InvPayTo = 'H' AND CLMINVRecMode = 'HS' AND InvDate = '2024-03-03' AND CASESPRefNo = 'ZZESP1'
            """, db: GenesisDb.Worksheet));
        Assert.Equal(100m, await Scalar<decimal>("SELECT INVAmount FROM ClaimInvoice WHERE CasNumber = 'ZZ00001' AND INVNo = 'ZZB-2'", db: GenesisDb.Worksheet));
        foreach (var table in new[] { "ClaimInvoiceActual", "ClaimInvoiceActualOthers", "ClaimInvoiceNotCover" })
            Assert.Equal(2, await Scalar<int>($"SELECT COUNT(*) FROM {table} WHERE INVIndex IN (SELECT INVIndex FROM ClaimInvoice WHERE CasNumber = 'ZZ00001')", db: GenesisDb.Worksheet));
        Assert.Single(factory.Audit.Entries, e => e.Action == "Claim.SppaUpload");

        // The same file again: everything is already there.
        var again = await Client().PostAsJsonAsync(Url, File());
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("No line of the file can be uploaded.", await again.Content.ReadAsStringAsync());
        var p = (await (await Client().PostAsJsonAsync($"{Url}/preview", File())).Content.ReadFromJsonAsync<SppaUploadPreview>())!;
        Assert.Equal("Already uploaded: bill ZZB-1 is on case ZZ00001", p.Rows[0].Message);
    }
}
