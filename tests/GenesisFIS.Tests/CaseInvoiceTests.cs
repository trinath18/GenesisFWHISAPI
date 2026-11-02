using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;

namespace GenesisFIS.Tests;

public class CaseInvoiceRuleTests
{
    private static CaseInvoiceRequest Hospital(Dictionary<string, decimal?>? amounts = null) =>
        new("INV1", new DateOnly(2024, 5, 2), "H", "I", "00001", null, null, null, null, 0m, 100m, null, amounts, null, null, null, null, null);

    [Fact]
    public void Totals_follow_the_legacy_form_formulas()
    {
        var (a, total) = CaseInvoiceCatalog.Compute(new Dictionary<string, decimal>
        {
            ["boardDays"] = 3, ["roomRate"] = 150, ["roomTax"] = 27, ["icuDays"] = 1, ["icuRate"] = 500, ["guardianDays"] = 2, ["guardianRate"] = 20,
            ["surgeonFee"] = 1000, ["surgeonWard"] = 100, ["anaesFee"] = 300, ["preXray"] = 50, ["preLab"] = 30, ["cashDays"] = 2, ["cashRate"] = 50,
            ["rbTaxOld"] = 5, ["icuTaxOld"] = 6, ["services"] = 200, ["grossRoomBoard"] = 999, ["accidentEmergency"] = 999, ["notGp"] = 999,
        });
        Assert.Equal((450m, 477m, 500m, 500m, 40m, 40m), (a["roomAmount"], a["roomTotal"], a["icuAmount"], a["icuTotal"], a["guardianAmount"], a["guardianTotal"]));
        Assert.Equal((1100m, 300m, 80m, 100m, 11m, 999m), (a["surgical"], a["anaesthetist"], a["preDiagnostic"], a["cashAllowance"], a["tax"], a["accidentPostAmount"]));
        // Room, ICU and guardian count once (through their totals), accident through A11 (accident + post-treatment); gross R&B and not-payable amounts are not part of the invoice amount.
        Assert.Equal(477m + 500m + 40m + 1100m + 300m + 80m + 100m + 11m + 200m + 999m, total);
    }

    [Fact]
    public void Hospital_sums_are_kept_only_for_plans_2_to_5_or_new_sm_plans()
    {
        var (a, _) = CaseInvoiceCatalog.Compute(new Dictionary<string, decimal> { ["surgeonFee"] = 100, ["preSpecialist"] = 10, ["physicianFee"] = 40, ["nsPostTreatment"] = 5 });
        Assert.Equal((110m, 45m), CaseInvoiceCatalog.HospitalSums(a, "3AB1", false));
        Assert.Equal((110m, 45m), CaseInvoiceCatalog.HospitalSums(a, "N001", true));
        Assert.Equal((0m, 0m), CaseInvoiceCatalog.HospitalSums(a, "N001", false));
    }

    [Fact]
    public void Validation_follows_the_legacy_save_checks()
    {
        var d = new DateOnly(2024, 5, 1);
        Assert.Null(CaseInvoiceRules.Validate(Hospital(), d));
        Assert.Equal("discharged", CaseInvoiceRules.Validate(Hospital(), null)!.Value.Field);
        Assert.Equal("excess", CaseInvoiceRules.Validate(Hospital() with { Excess = null }, d)!.Value.Field);
        Assert.Equal("hospitalName", CaseInvoiceRules.Validate(Hospital() with { Hospital = "GP" }, d)!.Value.Field);
        Assert.Null(CaseInvoiceRules.Validate(Hospital() with { Hospital = "GP", HospitalName = "KLINIK ABC" }, d));
        var member = Hospital() with { PayTo = "M", Hospital = null, Excess = null };
        Assert.Equal("receiptDate", CaseInvoiceRules.Validate(member, d)!.Value.Field);
        Assert.Equal("receiptMode", CaseInvoiceRules.Validate(member with { ReceiptDate = d }, d)!.Value.Field);
        Assert.Null(CaseInvoiceRules.Validate(member with { ReceiptDate = d, ReceiptMode = "PM" }, d));
        Assert.Equal("initialAmount", CaseInvoiceRules.Validate(Hospital() with { InitialAmount = null }, d)!.Value.Field);
        Assert.Equal("nope", CaseInvoiceRules.Validate(Hospital(new() { ["nope"] = 1 }), d)!.Value.Field);
        Assert.Equal("roomRate", CaseInvoiceRules.Validate(Hospital(new() { ["roomRate"] = -1 }), d)!.Value.Field);
        Assert.Equal("boardDays", CaseInvoiceRules.Validate(Hospital(new() { ["boardDays"] = 10000 }), d)!.Value.Field);
        Assert.Equal("chargedToMember", CaseInvoiceRules.Validate(Hospital() with { ChargedToMember = ["x"] }, d)!.Value.Field);
    }

    [Fact]
    public void Every_field_key_is_unique()
    {
        Assert.Equal(CaseInvoiceCatalog.Fields.Count, CaseInvoiceCatalog.Fields.Select(f => f.Key).Distinct().Count());
        Assert.Equal(CaseInvoiceCatalog.Fields.Count, CaseInvoiceCatalog.Fields.Select(f => (f.Table, f.Column)).Distinct().Count());
    }
}

public class CaseInvoiceTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/case-invoices", Case = "ZZINV00001";
    private HttpClient Client(int position = 22) => factory.ClientFor(WriteApiFactory.Access(position));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task SeedAsync(bool discharged = true, int closedVersions = 0)
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("""
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMAvailableLimit, MBMTopupInd)
                VALUES ('ZZINV0001*01', '00', 'A', 'INVOICE PRINCIPAL', 'IC1', 'WM', 'N', 'F', '2ZZ1', 'POLINV', 20000, 'N');
                INSERT INTO CAS (CASNumber, CASClaimAdmin, MBMNumber, MBMPatientCovID, CASHOSCode, CASDateAdmitted, CASDischargeDate, CASStayType, ModalityCode)
                VALUES (@case, 'N', 'ZZINV0001*01', '00', '00001', '2024-05-01', @dod, 'I', 'S');
                """, new { @case = Case, dod = discharged ? new DateTime(2024, 5, 4) : (DateTime?)null });
        }
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        await ws.ExecuteAsync("DELETE FROM ClaimInvoice WHERE CASNumber = @case; DELETE FROM ClaimInvoiceRemarks WHERE CASNumber = @case; DELETE FROM Claim WHERE CASNumber = @case", new { @case = Case });
        for (var v = 1; v <= closedVersions; v++)
            await ws.ExecuteAsync("INSERT INTO Claim (CASNumber, ClaimVersion, ClaimStatus) VALUES (@case, @v, 'C')", new { @case = Case, v });
    }

    private static CaseInvoiceRequest Request(string no = "INV-001") => new(no, new DateOnly(2024, 5, 4), "H", "I", "00001", null, null, null, null, 50m, 1500m, "IRM1",
        new Dictionary<string, decimal?> { ["boardDays"] = 3, ["roomRate"] = 150, ["surgeonFee"] = 1000, ["preSpecialist"] = 20, ["notGp"] = 15 },
        ["phoneByMember"], "TOWEL", null, null, "GP NOT COVERED");

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var conn = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        return await conn.ExecuteScalarAsync<T>(sql, new { @case = Case });
    }

    [Fact]
    public async Task Needs_the_invoice_entry_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(12).GetAsync($"{Url}/{Case}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(12).PostAsJsonAsync($"{Url}/{Case}", Request())).StatusCode);
    }

    [DatabaseFact]
    public async Task Options_list_stay_types_receipt_modes_and_the_amount_catalog()
    {
        var o = (await Client().GetFromJsonAsync<CaseInvoiceOptions>($"{Url}/options"))!;
        Assert.Contains(o.StayTypes, s => s.Code == "I");
        Assert.DoesNotContain(o.StayTypes, s => s.Code is "C" or "R");
        Assert.Contains(o.ReceiptModes, s => s.Code == "PM");
        Assert.Equal(CaseInvoiceCatalog.Fields.Count, o.Fields.Count);
        // The UI tells computed and not-covered boxes apart by name.
        var json = await Client().GetStringAsync($"{Url}/options");
        Assert.Contains("\"kind\":\"Computed\"", json);
        Assert.Contains("\"table\":\"NotCovered\"", json);
    }

    [DatabaseFact]
    public async Task A_new_invoice_goes_to_the_next_worksheet_version_with_all_its_amounts()
    {
        await SeedAsync(closedVersions: 2);
        var screen = (await Client().GetFromJsonAsync<CaseInvoiceScreen>($"{Url}/{Case}"))!;
        Assert.Equal((3, "INVOICE PRINCIPAL", new DateOnly(2024, 5, 4)), (screen.NextVersion, screen.Case.MemberName, screen.Case.Discharged));
        Assert.Equal(("INVOICE PRINCIPAL", "S"), (screen.Case.PatientName, screen.Case.Modality));   // the principal is the patient; a surgical case
        Assert.Empty(screen.Invoices);

        var res = await Client().PostAsJsonAsync($"{Url}/{Case}", Request());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var inv = (await res.Content.ReadFromJsonAsync<InvoiceEntry>())!;
        Assert.Equal((3, 1470m, 50m, 1420m, "H", "TEST"), (inv.Version, inv.Amount, inv.Excess, inv.PayAmount, inv.PayTo, inv.EnteredBy));
        Assert.Equal(50m, inv.PaidByMember);   // legacy "Exc Paid"
        Assert.Equal((450m, 450m, 1000m, 15m), (inv.Amounts["roomAmount"], inv.Amounts["roomTotal"], inv.Amounts["surgical"], inv.Amounts["notGp"]));
        Assert.Equal(["phoneByMember"], inv.ChargedToMember);
        Assert.Equal(("TOWEL", "GP NOT COVERED"), (inv.OthersDescription, inv.NotPayableRemark));

        // Plan 2ZZ1 starts with 2, so the surgical hospital sum is kept; cashless to a hospital is pay type C.
        Assert.Equal(1020m, await Scalar<decimal>($"SELECT a.CLMCHospSum FROM ClaimInvoiceActual a WHERE a.INVIndex = {inv.Index}"));
        Assert.Equal(("C", "3", "Y"), (await Scalar<string>($"SELECT INVPAYType FROM ClaimInvoice WHERE INVIndex = {inv.Index}"),
            (await Scalar<string>($"SELECT INVWSversion FROM ClaimInvoice WHERE INVIndex = {inv.Index}"))!.Trim(), await Scalar<string>($"SELECT CLMTelChargesMBM FROM ClaimInvoiceActualOthers WHERE INVIndex = {inv.Index}")));
        Assert.Single((await Client().GetFromJsonAsync<CaseInvoiceScreen>($"{Url}/{Case}"))!.Invoices);
        Assert.Contains(factory.Audit.Entries, e => e.Action == "Case.CreateInvoice");
    }

    [DatabaseFact]
    public async Task Duplicate_numbers_missing_discharge_and_unknown_codes_are_refused()
    {
        await SeedAsync();
        Assert.Equal(HttpStatusCode.Created, (await Client().PostAsJsonAsync($"{Url}/{Case}", Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync($"{Url}/{Case}", Request())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/{Case}", Request("INV-2") with { Hospital = "NOPE" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/{Case}", Request("INV-2") with { StayType = "C" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/{Case}", Request("INV-2") with { PayTo = "M", ReceiptDate = new DateOnly(2024, 5, 5), ReceiptMode = "ZZ" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync($"{Url}/NOCASE999", Request())).StatusCode);

        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("UPDATE CAS SET CASDischargeDate = NULL WHERE CASNumber = @case", new { @case = Case });
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/{Case}", Request("INV-3"))).StatusCode);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM ClaimInvoice WHERE CASNumber = @case"));
    }

    [DatabaseFact]
    public async Task A_member_invoice_is_paid_in_full_and_can_be_edited_and_removed()
    {
        await SeedAsync();
        var member = Request() with { PayTo = "M", Hospital = null, Excess = 99m, ReceiptDate = new DateOnly(2024, 5, 6), ReceiptMode = "PM" };
        var inv = (await (await Client().PostAsJsonAsync($"{Url}/{Case}", member)).Content.ReadFromJsonAsync<InvoiceEntry>())!;
        Assert.Equal(("Member", 0m, 1470m), (inv.Payee, inv.Excess, inv.PayAmount));
        Assert.Equal("R", await Scalar<string>($"SELECT INVPAYType FROM ClaimInvoice WHERE INVIndex = {inv.Index}"));

        var edited = member with { Amounts = new Dictionary<string, decimal?> { ["ambulance"] = 80 }, ChargedToMember = [] };
        var res = await Client().PutAsJsonAsync($"{Url}/{Case}/{inv.Index}", edited);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var after = (await res.Content.ReadFromJsonAsync<InvoiceEntry>())!;
        Assert.Equal((80m, 0m, 0m), (after.Amount, after.Amounts["roomTotal"], after.Amounts["notGp"]));
        Assert.Empty(after.ChargedToMember);

        Assert.Equal(HttpStatusCode.OK, (await Client().DeleteAsync($"{Url}/{Case}/{inv.Index}")).StatusCode);
        Assert.Equal(0, await Scalar<int>($"SELECT (SELECT COUNT(*) FROM ClaimInvoice WHERE INVIndex = {inv.Index}) + (SELECT COUNT(*) FROM ClaimInvoiceActual WHERE INVIndex = {inv.Index}) + (SELECT COUNT(*) FROM ClaimInvoiceActualOthers WHERE INVIndex = {inv.Index}) + (SELECT COUNT(*) FROM ClaimInvoiceNotCover WHERE INVIndex = {inv.Index})"));
        Assert.Equal(["Case.CreateInvoice", "Case.UpdateInvoice", "Case.DeleteInvoice"], factory.Audit.Entries.Select(e => e.Action));
    }

    [DatabaseFact]
    public async Task An_invoice_in_a_worksheet_can_be_changed_until_its_bordereau_but_never_removed()
    {
        await SeedAsync();
        var inv = (await (await Client().PostAsJsonAsync($"{Url}/{Case}", Request())).Content.ReadFromJsonAsync<InvoiceEntry>())!;
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            await ws.ExecuteAsync($"UPDATE ClaimInvoice SET INVWSed = 1 WHERE INVIndex = {inv.Index}");
        Assert.Equal(HttpStatusCode.OK, (await Client().PutAsJsonAsync($"{Url}/{Case}/{inv.Index}", Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client().DeleteAsync($"{Url}/{Case}/{inv.Index}")).StatusCode);
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            await ws.ExecuteAsync($"UPDATE ClaimInvoice SET INVBordxed = 1 WHERE INVIndex = {inv.Index}");
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PutAsJsonAsync($"{Url}/{Case}/{inv.Index}", Request())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().DeleteAsync($"{Url}/OTHERCASE1/{inv.Index}")).StatusCode);
    }

    [DatabaseFact]
    public async Task The_case_remark_is_saved_once_per_case()
    {
        await SeedAsync();
        Assert.Equal(HttpStatusCode.OK, (await Client().PutAsJsonAsync($"{Url}/{Case}/remarks", new CaseInvoiceRemarksRequest("first"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().PutAsJsonAsync($"{Url}/{Case}/remarks", new CaseInvoiceRemarksRequest("second"))).StatusCode);
        Assert.Equal("second", (await Client().GetFromJsonAsync<CaseInvoiceScreen>($"{Url}/{Case}"))!.Remarks);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM ClaimInvoiceRemarks WHERE CASNumber = @case"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PutAsJsonAsync($"{Url}/{Case}/remarks", new CaseInvoiceRemarksRequest(new string('x', 501)))).StatusCode);
    }

    private static InvoiceCaseProcedures Procedures() => new(
        ["DR SURGEON", null, null, null, null], ["DR GAS"], ["MEDICAL MGMT"], [new CaseInvestigationInput("BLOOD TEST", "20010")],
        [new InvoiceProcedure("APPENDICECTOMY", "25130", 1500m, 300m)], [new InvoiceProcedure(null, null, null), new InvoiceProcedure("SCOPE", "64302", 800m)]);

    [DatabaseFact]
    public async Task The_procedure_entry_is_saved_with_the_invoice_and_shown_with_its_mma_rates()
    {
        await SeedAsync();
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("INSERT INTO CASOthers (CASNumber, CASOPROCRequired3) VALUES (@case, 'OLD'); INSERT INTO CASOthersTwo (CASNumber, CASAttendDoctor2) VALUES (@case, 'OLD DR')", new { @case = Case });

        // Without the procedure entry the case is left alone.
        var res = await Client().PostAsJsonAsync($"{Url}/{Case}", Request());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var screen = (await Client().GetFromJsonAsync<CaseInvoiceScreen>($"{Url}/{Case}"))!;
        Assert.Equal(("OLD", "OLD DR"), (screen.Procedures!.Surgical![2].Text, screen.Procedures.AttendingDoctors![1]));
        Assert.Equal(6, screen.Procedures.NonSurgical!.Count);

        // With it, every slot is written (legacy SaveCASOthers), and the MMA and 13th schedule rates come back with the codes.
        Assert.Equal(HttpStatusCode.Created, (await Client().PostAsJsonAsync($"{Url}/{Case}", Request("INV-002") with { Procedures = Procedures() })).StatusCode);
        screen = (await Client().GetFromJsonAsync<CaseInvoiceScreen>($"{Url}/{Case}"))!;
        var p = screen.Procedures!;
        Assert.Equal(("APPENDICECTOMY", "25130", 1500m, 300m), (p.Surgical![0].Text, p.Surgical[0].Code, p.Surgical[0].ActualFee, p.Surgical[0].ActualAnaesFee));
        Assert.Null(p.Surgical[2].Text);
        Assert.Equal(("SCOPE", "64302", 800m), (p.NonSurgical![1].Text, p.NonSurgical[1].Code, p.NonSurgical[1].ActualFee));
        Assert.Equal(("DR SURGEON", null, "DR GAS", "MEDICAL MGMT"), (p.AttendingDoctors![0], p.AttendingDoctors[1], p.Anaesthetists![0], p.MedicalManagement![0]));
        Assert.Equal(("BLOOD TEST", "20010"), (p.Investigations![0].Done, p.Investigations[0].Code));
        Assert.Equal((1370m, 275m, 1370m, 275m), (screen.Rates!["25130"].Surgeon, screen.Rates["25130"].Anaesthetist, screen.Rates["25130"].Surgeon13th, screen.Rates["25130"].Anaesthetist13th));
        Assert.Equal(875m, screen.Rates["64302"].Surgeon13th);

        var tooLong = Procedures() with { Anaesthetists = [new string('x', 31)] };
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/{Case}", Request("INV-003") with { Procedures = tooLong })).StatusCode);
    }

    [DatabaseFact]
    public async Task Opcs_and_investigation_codes_can_be_searched_and_their_rates_looked_up()
    {
        var all = (await Client().GetFromJsonAsync<List<MmaCode>>($"{Url}/mma?code=2513"))!;
        Assert.Contains(all, c => c.Code == "25130");
        var inv = (await Client().GetFromJsonAsync<List<MmaCode>>($"{Url}/mma?code=2001&investigations=true"))!;
        Assert.Contains(inv, c => c.Code == "20010");
        Assert.DoesNotContain((await Client().GetFromJsonAsync<List<MmaCode>>($"{Url}/mma?code=2513&investigations=true"))!, c => c.Code == "25130");
        Assert.Equal(1370m, (await Client().GetFromJsonAsync<MmaRate>($"{Url}/mma/25130"))!.Surgeon);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"{Url}/mma/NOPE9")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(12).GetAsync($"{Url}/mma?code=1")).StatusCode);
    }
}
