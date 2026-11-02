using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;

namespace GenesisFIS.Tests;

public class WorksheetSaveRuleTests
{
    private static WorksheetSaveRequest Request(string? status = "AA") =>
        new(status, null, null, null, null, null, false, null, null, null, null, null, null, null, null, null, null, null);

    [Fact]
    public void A_status_is_needed_and_a_member_payment_needs_an_address()
    {
        Assert.Null(WorksheetSaveRules.Validate(Request(), memberPaid: false));
        Assert.Equal("worksheetStatus", WorksheetSaveRules.Validate(Request(null), false)!.Value.Field);
        Assert.Equal("worksheetStatus", WorksheetSaveRules.Validate(Request("ZZ"), false)!.Value.Field);
        Assert.Equal("address1", WorksheetSaveRules.Validate(Request(), true)!.Value.Field);
        var addressed = Request() with { Address1 = "1 JALAN", PostCode = "50000", City = "KL", State = "14" };
        Assert.Null(WorksheetSaveRules.Validate(addressed, true));
        Assert.Equal("accountNo", WorksheetSaveRules.Validate(addressed with { Bank = "MAYBANK" }, true)!.Value.Field);
        Assert.Equal("bank", WorksheetSaveRules.Validate(addressed with { AccountNo = "123" }, true)!.Value.Field);
        Assert.Equal("postCode", WorksheetSaveRules.Validate(addressed with { PostCode = "1234567" }, true)!.Value.Field);
    }
}

public class WorksheetSaveTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Case = "ZZWKS00002", Member = "ZZWKS0002*01", Url = "/api/worksheet-registration/" + Case;
    private HttpClient Client(int position) => factory.ClientFor(WriteApiFactory.Access(position));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static WorksheetSaveRequest Save(decimal? expected = null) =>
        new("AA", expected, null, "CHECKED", null, null, false, null, null, null, null, null, null, null, null, null, null, null);

    /// <summary>Plan ZZW1 like the plan in use (FH01): room & board 160 a day for up to 30 days, hospital supplies in full, an annual limit of 20,000.</summary>
    private async Task SeedAsync(bool diagnosis = true)
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("""
                INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, AnnLmtIND) VALUES ('ZZW1', 'N', 'WM', '2020-01-01', 'Y');
                INSERT INTO AnnualLimit (PLNCode, INSCode, HLTCode, AnnLimit, SuppLimit, SuppLimitStatus, AnnualEffDate, PAYCode) VALUES ('ZZW1', 'F', 'N', 20000, 0, 'A', '2020-01-01', 'WM');
                INSERT INTO BNF (PLNCode, INSCode, HLTCode, BNFCode, BNFNoOfDays, BNFClaimAbleAmount, BNFRMPerDis, BNFEffDate)
                VALUES ('ZZW1', 'F', 'N', '101', 30, '160', NULL, '2020-01-01'), ('ZZW1', 'F', 'N', '103', NULL, NULL, 'FULL', '2020-01-01');
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMAvailableLimit, MBMTopupInd, MBMPayorEffDate)
                VALUES (@member, '00', 'A', 'SAVE PRINCIPAL', 'IC2', 'WM', 'N', 'F', 'ZZW1', 'POLSAVE', 20000, 'N', '2024-01-01');
                INSERT INTO CAS (CASNumber, CASClaimAdmin, CASClaimability, MBMNumber, MBMPatientCovID, CASHOSCode, CASDateAdmitted, CASDischargeDate, CASStayType, CASReserveAmount, CASPending)
                VALUES (@case, 'N', 'AA', @member, '00', '00001', '2024-05-01', '2024-05-04', 'I', 5000, 'Y');
                """, new { @case = Case, member = Member });
            if (diagnosis) await main.ExecuteAsync("INSERT INTO DIAFinal (CASNumber, ICDCode1) VALUES (@case, 'A00')", new { @case = Case });
        }
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        await ws.ExecuteAsync("""
            DELETE FROM ClaimInvoice WHERE CASNumber = @case; DELETE FROM Claim WHERE CASNumber = @case; DELETE FROM ClaimSameLimit WHERE CASNumber = @case;
            DELETE FROM ClaimActual WHERE CASNumber = @case; DELETE FROM ClaimLimit WHERE CASNumber = @case; DELETE FROM ClaimApprove WHERE CASNumber = @case;
            DELETE FROM ClaimHos WHERE CASNumber = @case; DELETE FROM ClaimMember WHERE CASNumber = @case; DELETE FROM ClaimOthers WHERE CASNumber = @case;
            DELETE FROM ClaimPayMBMStatus WHERE CASNumber = @case; DELETE FROM CAS_Excess WHERE CASNumber = @case; DELETE FROM EXC_SEQ WHERE CASNumber = @case;
            """, new { @case = Case });
    }

    /// <summary>A hospital invoice: 3 days at 200 (the plan pays 160 a day) and 900 of supplies; the member's excess of 120 is taken off what the hospital is paid.</summary>
    private async Task<int> HospitalInvoiceAsync(string no = "H1", decimal excess = 120)
    {
        var req = new CaseInvoiceRequest(no, new DateOnly(2024, 5, 4), "H", "I", "00001", null, null, null, null, excess, 1500m, null,
            new Dictionary<string, decimal?> { ["boardDays"] = 3, ["roomRate"] = 200, ["services"] = 900 }, null, null, null, null, null);
        var res = await Client(22).PostAsJsonAsync($"/api/case-invoices/{Case}", req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<InvoiceEntry>())!.Index;
    }

    private async Task<T?> Ws<T>(string sql)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        return await conn.ExecuteScalarAsync<T>(sql, new { @case = Case });
    }

    private async Task<T?> Main<T>(string sql)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Main);
        return await conn.ExecuteScalarAsync<T>(sql, new { @case = Case, member = Member });
    }

    [Fact]
    public async Task Saving_needs_the_worksheet_registration_permission()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(22).PostAsJsonAsync(Url, Save())).StatusCode);
    }

    [DatabaseFact]
    public async Task A_cashless_worksheet_is_saved_with_every_table_and_updates_the_case_and_balance()
    {
        await SeedAsync();
        var invoice = await HospitalInvoiceAsync();
        var res = await Client(64).PostAsJsonAsync(Url, Save(expected: 1380));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var saved = (await res.Content.ReadFromJsonAsync<WorksheetSaveResult>())!;
        Assert.Equal(($"{Case}-1", 1380m, 1380m, 0m, -120m, (string?)null, 18620m), (saved.WorksheetNumber, saved.FinalApproved, saved.PayableToHospital, saved.PayableToMember, saved.Excess, saved.DebitCreditNote, saved.BalanceAfter));

        Assert.Equal(1, await Ws<int>("SELECT COUNT(*) FROM Claim WHERE CASNumber = @case AND ClaimVersion = 1 AND ClaimStatus = 'C' AND CLMWSStatus = 'AA' AND CLMCashType = 'C' AND ExcessCollectedBy = 'H' AND CLMFinalApprove = 1380 AND CLMBordxAmt = 1380 AND CLMExcess = -120 AND CLMPreviousBalance = 20000 AND CLMBalanceLimit = 18620 AND ClaimOpenBy = 'TEST'"));
        Assert.Equal("480.00", (await Ws<string>("SELECT CLMBoardAmt FROM ClaimLimit WHERE CASNumber = @case AND CLMVersion = 1"))!.Trim());
        Assert.Equal("3", (await Ws<string>("SELECT CLMBoardDays FROM ClaimLimit WHERE CASNumber = @case AND CLMVersion = 1"))!.Trim());
        Assert.Equal("600.00", (await Ws<string>("SELECT CLMBoardAmt FROM ClaimActual WHERE CASNumber = @case AND CLMVersion = 1"))!.Trim());
        Assert.Equal("480.00", (await Ws<string>("SELECT CLMBoardAmt FROM ClaimApprove WHERE CASNumber = @case AND CLMVersion = 1"))!.Trim());
        Assert.Equal("(120.00)", (await Ws<string>("SELECT CLMBoardAmt FROM ClaimMember WHERE CASNumber = @case AND CLMVersion = 1"))!.Trim());
        Assert.Equal(1, await Ws<int>("SELECT COUNT(*) FROM ClaimHos WHERE CASNumber = @case AND CLMVersion = 1"));
        Assert.Equal(1, await Ws<int>("SELECT COUNT(*) FROM ClaimPayMBMStatus WHERE CASNumber = @case AND CLMVersion = 1 AND CLMBoardAmt = 'N'"));
        Assert.Equal("CHECKED", (await Ws<string>("SELECT CLMWSRemarks FROM ClaimOthers WHERE CASNumber = @case AND CLaimVersion = 1"))!.Trim());
        Assert.Equal("27", (await Ws<string>("SELECT CLMBoardDays FROM ClaimSameLimit WHERE CASNumber = @case"))!.Trim());   // 30 days less the 3 used
        Assert.True(await Ws<bool>($"SELECT INVWSed FROM ClaimInvoice WHERE INVIndex = {invoice}"));
        // The hospital billed 120 of room & board beyond the plan: an excess record for the excess letter (legacy saveExcessLetter).
        Assert.Equal($"E{Case}-1", saved.ExcessNumber);
        Assert.Equal(1, await Ws<int>("SELECT COUNT(*) FROM CAS_Excess WHERE CASNumber = @case AND EXCNumber = 'E' + @case + '-1' AND ExcessAmt = 120 AND EXCBoardAmt = '120' AND EXCServices = '0' AND INVNo = 'H1' AND INVAmt = 1500 AND EXCIssuedBy = 'TEST'"));
        Assert.Equal(1, await Ws<int>("SELECT EXCSequence FROM EXC_SEQ WHERE CASNumber = @case"));

        Assert.Equal((0m, "N", 18620m), (await Main<decimal>("SELECT CASReserveAmount FROM CAS WHERE CASNumber = @case"), (await Main<string>("SELECT CASPending FROM CAS WHERE CASNumber = @case"))!.Trim(),
            await Main<decimal>("SELECT MBMAvailableLimit FROM MBM WHERE MBMNumber = @member")));
        Assert.Contains(factory.Audit.Entries, e => e.Action == "Worksheet.Save" && e.Key == $"{Case}-1");

        // The enquiry now shows it, and the invoice can no longer be changed.
        var enquiry = (await Client(13).GetFromJsonAsync<WorksheetEnquiry>($"/api/worksheet-enquiry/{Case}"))!;
        Assert.Equal(("480.00", "(120.00)"), (enquiry.Versions.Single().Lines[0].Approved, enquiry.Versions.Single().Lines[0].Member));
        Assert.Equal(HttpStatusCode.Conflict, (await Client(22).DeleteAsync($"/api/case-invoices/{Case}/{invoice}")).StatusCode);

        // The next invoice goes to version 2, limited to the 27 room & board days left.
        await HospitalInvoiceAsync("H2", excess: 0);
        var next = (await Client(64).GetFromJsonAsync<WorksheetPreview>($"{Url}/preview"))!;
        Assert.Equal((2, true), (next.Version, next.SameLimit));
    }

    [DatabaseFact]
    public async Task The_legacy_checks_refuse_an_incomplete_worksheet()
    {
        await SeedAsync(diagnosis: false);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(64).PostAsJsonAsync(Url, Save())).StatusCode);   // no invoice
        await HospitalInvoiceAsync(excess: 0);
        var noDiagnosis = await Client(64).PostAsJsonAsync(Url, Save());
        Assert.Equal(HttpStatusCode.BadRequest, noDiagnosis.StatusCode);
        Assert.Contains("Final Diagnosis", await noDiagnosis.Content.ReadAsStringAsync());

        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("INSERT INTO DIAFinal (CASNumber, ICDCode1) VALUES (@case, 'A00')", new { @case = Case });
        // The invoice says the hospital is paid 1,500 but the worksheet approves 1,380: the excess on the invoice must be corrected first.
        var payable = await Client(64).PostAsJsonAsync(Url, Save());
        Assert.Equal(HttpStatusCode.BadRequest, payable.StatusCode);
        Assert.Contains("Payable to Hospital", await payable.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(64).PostAsJsonAsync(Url, Save() with { WorksheetStatus = null })).StatusCode);
        Assert.Equal(0, await Ws<int>("SELECT COUNT(*) FROM Claim WHERE CASNumber = @case"));
    }

    [DatabaseFact]
    public async Task A_stale_total_is_refused_and_a_saved_version_cannot_be_saved_twice()
    {
        await SeedAsync();
        await HospitalInvoiceAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await Client(64).PostAsJsonAsync(Url, Save(expected: 999))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Client(64).PostAsJsonAsync(Url, Save(expected: 1380))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(64).PostAsJsonAsync(Url, Save())).StatusCode);   // nothing left to put in a worksheet
        Assert.Equal(1, await Ws<int>("SELECT COUNT(*) FROM Claim WHERE CASNumber = @case"));
    }

    [DatabaseFact]
    public async Task Worksheet_adjustment_recalculates_and_rewrites_the_latest_version()
    {
        await SeedAsync();
        var invoice = await HospitalInvoiceAsync();
        Assert.Equal(HttpStatusCode.Created, (await Client(64).PostAsJsonAsync(Url, Save(expected: 1380))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(64).GetAsync($"/api/worksheet-adjustment/{Case}")).StatusCode);

        // Unchanged, the recalculation matches what was saved and the saved details come back to start from.
        var view = (await Client(65).GetFromJsonAsync<WorksheetAdjustmentView>($"/api/worksheet-adjustment/{Case}"))!;
        Assert.Equal((1, true, 1380m, 1380m), (view.Version, view.CanAmend, view.Saved.TotalApproved, view.Recalculated.Calculation.FinalApproved));
        Assert.Equal(("AA", "CHECKED"), (view.Details.WorksheetStatus, view.Details.Remarks));
        Assert.Equal("480.00", view.Recalculated.Calculation.Lines[0].Limit);   // its own 3 days are given back, so it is not capped by itself

        // The hospital corrects the bill: 2 days instead of 3, and the member's excess becomes 80.
        var corrected = new CaseInvoiceRequest("H1", new DateOnly(2024, 5, 4), "H", "I", "00001", null, null, null, null, 80m, 1500m, null,
            new Dictionary<string, decimal?> { ["boardDays"] = 2, ["roomRate"] = 200, ["services"] = 900 }, null, null, null, null, null);
        Assert.Equal(HttpStatusCode.OK, (await Client(22).PutAsJsonAsync($"/api/case-invoices/{Case}/{invoice}", corrected)).StatusCode);
        var amend = await Client(65).PutAsJsonAsync($"/api/worksheet-adjustment/{Case}/1", Save(expected: 1220) with { Remarks = "BILL CORRECTED" });
        Assert.Equal(HttpStatusCode.OK, amend.StatusCode);
        var amended = (await amend.Content.ReadFromJsonAsync<WorksheetSaveResult>())!;
        Assert.Equal(($"{Case}-1", 1220m, -80m, 18780m), (amended.WorksheetNumber, amended.FinalApproved, amended.Excess, amended.BalanceAfter));

        Assert.Equal(1, await Ws<int>("SELECT COUNT(*) FROM Claim WHERE CASNumber = @case"));
        Assert.Equal(1, await Ws<int>("SELECT COUNT(*) FROM Claim WHERE CASNumber = @case AND ClaimVersion = 1 AND CLMTotalApproved = 1220 AND CLMBordxAmt = 1220 AND CLMPreviousBalance = 20000 AND CLMBalanceLimit = 18780 AND ClaimOpenBy = 'TEST'"));
        Assert.Equal(("320.00", "(80.00)"), ((await Ws<string>("SELECT CLMBoardAmt FROM ClaimApprove WHERE CASNumber = @case AND CLMVersion = 1"))!.Trim(),
            (await Ws<string>("SELECT CLMBoardAmt FROM ClaimMember WHERE CASNumber = @case AND CLMVersion = 1"))!.Trim()));
        Assert.Equal(1, await Ws<int>("SELECT COUNT(*) FROM ClaimApprove WHERE CASNumber = @case"));
        Assert.Equal("BILL CORRECTED", (await Ws<string>("SELECT CLMWSRemarks FROM ClaimOthers WHERE CASNumber = @case"))!.Trim());
        Assert.Equal("28", (await Ws<string>("SELECT CLMBoardDays FROM ClaimSameLimit WHERE CASNumber = @case"))!.Trim());   // 30 less the 2 days now used
        Assert.Equal($"E{Case}-1", amended.ExcessNumber);   // the excess record is rewritten, not repeated
        Assert.Equal(1, await Ws<int>("SELECT COUNT(*) FROM CAS_Excess WHERE CASNumber = @case AND ExcessAmt = 80 AND EXCBoardAmt = '80' AND EXCUpdateLogon = 'TEST'"));
        Assert.Equal(1, await Ws<int>("SELECT COUNT(*) FROM CAS_Excess WHERE CASNumber = @case"));
        Assert.Equal(18780m, await Main<decimal>("SELECT MBMAvailableLimit FROM MBM WHERE MBMNumber = @member"));
        Assert.Contains(factory.Audit.Entries, e => e.Action == "Worksheet.Amend");

        // Once on a bordereau the worksheet is closed to amendment.
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            await ws.ExecuteAsync("UPDATE Claim SET ClaimBordxStatus = 1, BordxRefNo = 'B0001' WHERE CASNumber = @case", new { @case = Case });
        var closed = (await Client(65).GetFromJsonAsync<WorksheetAdjustmentView>($"/api/worksheet-adjustment/{Case}"))!;
        Assert.False(closed.CanAmend);
        Assert.Equal(HttpStatusCode.Conflict, (await Client(65).PutAsJsonAsync($"/api/worksheet-adjustment/{Case}/1", Save())).StatusCode);
    }

    [DatabaseFact]
    public async Task Only_the_latest_version_can_be_amended()
    {
        await SeedAsync();
        await HospitalInvoiceAsync();
        Assert.Equal(HttpStatusCode.Created, (await Client(64).PostAsJsonAsync(Url, Save())).StatusCode);
        await HospitalInvoiceAsync("H2");   // a follow-up bill: version 2
        Assert.Equal(HttpStatusCode.Created, (await Client(64).PostAsJsonAsync(Url, Save())).StatusCode);

        var first = (await Client(65).GetFromJsonAsync<WorksheetAdjustmentView>($"/api/worksheet-adjustment/{Case}?version=1"))!;
        Assert.Equal([1, 2], first.Versions);
        Assert.False(first.CanAmend);
        Assert.Equal(HttpStatusCode.Conflict, (await Client(65).PutAsJsonAsync($"/api/worksheet-adjustment/{Case}/1", Save())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client(65).GetAsync($"/api/worksheet-adjustment/{Case}?version=7")).StatusCode);
    }

    [DatabaseFact]
    public async Task A_member_reimbursement_gets_a_credit_note_and_the_payee_details()
    {
        await SeedAsync();
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            await ws.ExecuteAsync("DELETE FROM CRSeq WHERE Payor = 'WM'; INSERT INTO CRSeq (CRSeq, Payor) VALUES ('00041', 'WM')");
        var req = new CaseInvoiceRequest("M1", new DateOnly(2024, 5, 4), "M", "I", null, null, "PM", new DateOnly(2024, 5, 6), null, null, 900m, null,
            new Dictionary<string, decimal?> { ["services"] = 900 }, null, null, null, null, null);
        Assert.Equal(HttpStatusCode.Created, (await Client(22).PostAsJsonAsync($"/api/case-invoices/{Case}", req)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await Client(64).PostAsJsonAsync(Url, Save())).StatusCode);   // the member's address is needed
        var res = await Client(64).PostAsJsonAsync(Url, Save() with { PayTo = "ZETA TEST SDN BHD", Address1 = "1 JALAN UJIAN", PostCode = "50000", City = "KUALA LUMPUR", State = "14" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var saved = (await res.Content.ReadFromJsonAsync<WorksheetSaveResult>())!;
        Assert.Equal((900m, 0m, "CWM00042"), (saved.PayableToMember, saved.PayableToHospital, saved.DebitCreditNote));
        Assert.Equal(1, await Ws<int>("SELECT COUNT(*) FROM Claim WHERE CASNumber = @case AND CLMCashType = 'R' AND ExcessCollectedBy = 'M' AND CLMPayTo = 'ZETA TEST SDN BHD' AND DebitCreditRefNo = 'CWM00042' AND CLMReimburse = 900"));
        Assert.Equal("KUALA LUMPUR", (await Ws<string>("SELECT MBMCity FROM ClaimOthers WHERE CASNumber = @case"))!.Trim());
        Assert.Null(saved.ExcessNumber);   // a reimbursement has no hospital excess
        Assert.Equal(0, await Ws<int>("SELECT COUNT(*) FROM CAS_Excess WHERE CASNumber = @case"));

        // The credit note and the cheque letter go with it.
        var docs = await Client(13).GetFromJsonAsync<DocumentList>($"/api/worksheet-enquiry/{Case}/1/documents");
        Assert.Equal(["credit-note", "cheque-letter", "worksheet"], docs!.Available);
        Assert.Equal(("CWM00042", 900m), (docs.DebitCreditNote, docs.NoteAmount));
        var note = await Client(13).GetAsync($"/api/worksheet-enquiry/{Case}/1/documents/credit-note");
        Assert.Equal(("application/pdf", "credit-note-" + Case + "-1.pdf"), (note.Content.Headers.ContentType!.MediaType, note.Content.Headers.ContentDisposition!.FileName));
        Assert.Equal(HttpStatusCode.NotFound, (await Client(13).GetAsync($"/api/worksheet-enquiry/{Case}/1/documents/excess-letter")).StatusCode);
    }

    [DatabaseFact]
    public async Task The_legacy_form_panels_have_their_lists_details_and_the_days_at_limit_column()
    {
        await SeedAsync();
        await HospitalInvoiceAsync();
        var options = (await Client(64).GetFromJsonAsync<WorksheetOptions>("/api/worksheet-registration/options"))!;
        Assert.Equal(15, options.Statuses.Count);
        Assert.Equal(["BY POST", "BANK-IN"], options.PaymentModes);
        Assert.NotEmpty(options.Banks);
        Assert.NotEmpty(options.States);

        var detail = (await Client(64).GetFromJsonAsync<WorksheetCaseDetail>($"/api/worksheet-registration/{Case}/1/details"))!;
        Assert.Equal(("SAVE PRINCIPAL", "SAVE PRINCIPAL", "A00", 1), (detail.MemberName, detail.PatientName, detail.IcdCodes, detail.Invoices.Count));
        Assert.Equal(new DateOnly(2024, 5, 4), detail.LastDocDate);
        Assert.Contains(detail.NotCovered, n => n.Label == "GP fee" && n.Amount == 0);
        Assert.Equal(HttpStatusCode.NotFound, (await Client(64).GetAsync("/api/worksheet-registration/NOSUCHCASE/1/details")).StatusCode);

        // Room & board: 160 a day, 3 days of the plan's 30.
        var preview = (await Client(64).GetFromJsonAsync<WorksheetPreview>($"{Url}/preview"))!;
        Assert.Equal("@160.00 (3/30)", preview.Calculation.DaysAtLimit![1]);

        // A bank account must have the bank's number of digits (legacy checkAccNo); the GP and last document date are kept.
        var bank = options.Banks.First(b => b.AccountDigits is > 0);
        var wrong = await Client(64).PostAsJsonAsync(Url, Save(expected: 1380) with { Bank = bank.Name, AccountNo = "1" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("account number", await wrong.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await Client(64).PostAsJsonAsync(Url, Save(expected: 1380) with { GpName = "DR TEST", LastDocDate = new DateOnly(2024, 5, 10) })).StatusCode);
        Assert.Equal(1, await Ws<int>("SELECT COUNT(*) FROM Claim WHERE CASNumber = @case AND CLMGPName = 'DR TEST' AND CLMLastDocDate = '2024-05-10'"));
        var view = (await Client(65).GetFromJsonAsync<WorksheetAdjustmentView>($"/api/worksheet-adjustment/{Case}"))!;
        Assert.Equal(("DR TEST", new DateOnly(2024, 5, 10)), (view.Details.GpName, view.Details.LastDocDate));
        // The enquiry shows what the version was saved with.
        var savedDetail = (await Client(13).GetFromJsonAsync<WorksheetCaseDetail>($"/api/worksheet-enquiry/{Case}/1/details"))!;
        Assert.Equal(("AA", "CHECKED", "DR TEST"), (savedDetail.Saved!.WorksheetStatus, savedDetail.Saved.Remarks, savedDetail.Saved.GpName));
        Assert.Equal(1380m, (await Client(13).GetFromJsonAsync<WorksheetEnquiry>($"/api/worksheet-enquiry/{Case}"))!.Versions.Single().FinalApproved);
    }

    private sealed record DocumentList(string[] Available, string? DebitCreditNote, string? ExcessNumber, decimal NoteAmount);

    [DatabaseFact]
    public async Task The_excess_letter_and_export_come_from_each_worksheet_screen()
    {
        await SeedAsync();
        await HospitalInvoiceAsync();
        Assert.Equal(HttpStatusCode.Created, (await Client(64).PostAsJsonAsync(Url, Save(expected: 1380))).StatusCode);

        foreach (var (position, path) in new[] { (13, "worksheet-enquiry"), (64, "worksheet-registration"), (65, "worksheet-adjustment") })
        {
            var docs = await Client(position).GetFromJsonAsync<DocumentList>($"/api/{path}/{Case}/1/documents");
            Assert.Equal(["excess-letter", "worksheet"], docs!.Available);
            Assert.Equal($"E{Case}-1", docs.ExcessNumber);
        }
        var letter = await Client(64).GetAsync($"/api/worksheet-registration/{Case}/1/documents/excess-letter");
        Assert.Equal(HttpStatusCode.OK, letter.StatusCode);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(await letter.Content.ReadAsByteArrayAsync(), 0, 4));
        var export = await Client(13).GetAsync($"/api/worksheet-enquiry/{Case}/1/documents/worksheet");
        Assert.Equal("worksheet-" + Case + "-1.xlsx", export.Content.Headers.ContentDisposition!.FileName);
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await export.Content.ReadAsByteArrayAsync()));
        Assert.Contains(wb.Worksheet(1).CellsUsed(), c => c.GetString() == "Room & Board");

        Assert.Equal(HttpStatusCode.NotFound, (await Client(13).GetAsync($"/api/worksheet-enquiry/{Case}/1/documents/credit-note")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client(13).GetAsync($"/api/worksheet-enquiry/{Case}/9/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(22).GetAsync($"/api/worksheet-enquiry/{Case}/1/documents")).StatusCode);
    }
}
