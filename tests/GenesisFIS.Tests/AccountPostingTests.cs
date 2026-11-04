using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Posting;

namespace GenesisFIS.Tests;

public class PostingRuleTests
{
    [Fact]
    public void Posting_opens_after_the_start_and_end_times_unless_the_user_has_special_access()
    {
        TimeOnly? start = new TimeOnly(16, 0), end = new TimeOnly(9, 0);
        Assert.False(PostingWindow.For(new TimeOnly(15, 59), start, end, false).Open);
        Assert.Contains("available", PostingWindow.For(new TimeOnly(10, 0), start, end, false).Message);
        Assert.True(PostingWindow.For(new TimeOnly(16, 1), start, end, false).Open);
        Assert.True(PostingWindow.For(new TimeOnly(10, 0), start, end, true).Open);
        Assert.True(PostingWindow.For(new TimeOnly(10, 0), null, null, false).Open);
    }

    [Fact]
    public void Journal_fields_follow_the_legacy_form()
    {
        Assert.Equal(("05", "2024"), PostingRules.ParsePeriod("05/2024"));
        Assert.Null(PostingRules.ParsePeriod("13/2024"));
        Assert.Equal("00447", PostingRules.CaseRef("AI00447"));
        Assert.Equal("00175", PostingRules.Last("FWB0000175", 5));
        Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXY", PostingRules.Description("ABCDEFGHIJKLMNOPQRSTUVWXYZ"));
        Assert.Equal(("7400", "7410", "7420", null), (PostingRules.BankAccount("CA1"), PostingRules.BankAccount("cb2"), PostingRules.BankAccount("CC3"), PostingRules.BankAccount("CF4")));
        Assert.Equal(("7400", "7420", null), (PostingRules.ReceiptBankAccount("RA1"), PostingRules.ReceiptBankAccount("RC2"), PostingRules.ReceiptBankAccount("RF3")));
        Assert.NotNull(PostingRules.Validate(new HospitalInvoicePostingSearch()));
        Assert.Null(PostingRules.Validate(new HospitalInvoicePostingSearch(InvoiceDateTo: new DateOnly(2024, 1, 31))));
    }
}

public class AccountPostingTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/posting/invoice-hospital", Case = "ZZPST00001";
    private static readonly DateOnly Day = new(2030, 1, 15);
    private HttpClient Client(int position = 169) => factory.ClientFor(WriteApiFactory.Access(position));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static HospitalInvoicePostingSearch Search(bool posted = false, string? batch = null) =>
        new(InvoiceDateFrom: Day, InvoiceDateTo: Day, Posted: posted, BatchFrom: batch, BatchTo: batch);

    /// <summary>A claim paying RM 900 to hospital 00001 through two hospital invoices, on a bordereau; posting open from midnight.</summary>
    private async Task SeedAsync(string? hospitalAccount = "H000001")
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMAvailableLimit, MBMTopupInd)
                VALUES ('ZZPST0001*01', '00', 'A', 'POSTING PATIENT WITH A VERY LONG NAME', 'IC1', 'ZZ', 'N', 'F', 'ZZ01', 'POLPST', 20000, 'N');
                INSERT INTO CAS (CASNumber, CASClaimAdmin, MBMNumber, MBMPatientCovID, CASHOSCode, CASDateAdmitted, CASDischargeDate, CASStayType, ModalityCode)
                VALUES (@case, 'N', 'ZZPST0001*01', '00', '00001', '2030-01-01', '2030-01-05', 'I', 'S');
                UPDATE HOS SET HOSAccCode = @acc WHERE HOSCode = '00001';
                """, new { @case = Case, acc = hospitalAccount });
        await using (var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance))
            await maint.ExecuteAsync("UPDATE TimeMain SET StartTime = '1899-12-30 00:00:00', EndTime = '1899-12-30 00:00:00' WHERE Process = 'POSTING'; IF NOT EXISTS (SELECT 1 FROM AccPeriod WHERE AccMonth = '01' AND AccYear = '2030') INSERT INTO AccPeriod (AccMonth, AccYear) VALUES ('01', '2030')");
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        await ws.ExecuteAsync("""
            INSERT INTO Bordx (BordxRefNo, BordxDate, BordxPAYToStatus, BordxPAYDOCFRStatus, BordxMNRBToStatus, BordxMNRBDOCFRStatus, BordxPrintedStatus, ACPeriodStatus, BordxSendtoACStatus)
            VALUES ('ZZB0099901', '2030-01-20', '0', '0', '0', '0', 0, 0, 0);
            INSERT INTO Claim (CASNumber, ClaimVersion, ClaimStatus, ClaimBordxStatus, CLMTokenStatus, CLMCaseMgmtFees, CLMPrevPerDisLmt, CLMPayableByMedix2H, CLMPayableByMedix2M,
                               ClaimCloseDate, BordxRefNo)
            VALUES (@case, 1, 'C', 1, 0, 0, 0, 900, 0, '2030-01-15', 'ZZB0099901');
            INSERT INTO ClaimInvoice (INVWSversion, INVBordxed, INVWSed, InvNo, CASNumber, InvDate, INVEnteredDate, INVEnteredBy, INVPayee, INVPayTo, INVAmount, INVBordxRefNo, InvPrintStatus, INVDCMBMStatus)
            VALUES ('1', 1, 1, 'ZZINV-A', @case, '2030-01-10', '2030-01-15', 'TEST', '00001', 'H', 600, 'ZZB0099901', '0', '0'),
                   ('1', 1, 1, 'ZZINV-B', @case, '2030-01-11', '2030-01-15', 'TEST', '00001', 'H', 300, 'ZZB0099901', '0', '0');
            """, new { @case = Case });
    }

    private async Task<PostingJournal> SearchAsync(HospitalInvoicePostingSearch s)
    {
        var res = await Client().PostAsJsonAsync($"{Url}/search", s);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PostingJournal>())!;
    }

    [DatabaseFact]
    public async Task A_claim_is_one_debit_and_credit_pair_however_many_hospital_invoices_it_has()
    {
        await SeedAsync();
        var j = await SearchAsync(Search());
        var item = Assert.Single(j.Items, i => i.Key == $"{Case}/1");
        Assert.Null(item.Problem);
        Assert.Equal(["ZZINV-A", "ZZINV-B"], item.References);
        var (d, c) = (item.Lines[0], item.Lines[1]);
        Assert.Equal(("RC", "MED", "ZZINV-A", "6000ZZ", 900m, "D"), (d.JournalType, d.Database, d.TransRef, d.AccountCode, d.Amount, d.DebitCredit));
        Assert.Equal(("H000001", 900m, "C", "000001"), (c.AccountCode, c.Amount, c.DebitCredit, c.Hospital));
        // The patient name is cut to 25 characters, the case to characters 3-7, the bordereau to its last five.
        Assert.Equal(("POSTING PATIENT WITH A VE", "PST00", "99901"), (d.Description, d.CaseNo, d.BordxRef));
    }

    [DatabaseFact]
    public async Task Posting_writes_a_batch_for_every_invoice_of_the_chosen_claims_and_they_drop_out_of_the_unposted_list()
    {
        await SeedAsync();
        var res = await Client().PostAsJsonAsync($"{Url}/post", new HospitalInvoicePostRequest(Search(), [$"{Case}/1"], "01/2030"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var posted = (await res.Content.ReadFromJsonAsync<PostingResult>())!;
        Assert.Equal((1, 2), (posted.Items, posted.Records));
        var item = Assert.Single(posted.Journal.Items, i => i.Key == $"{Case}/1");
        Assert.Equal(("01/2030", posted.BatchNo), (item.Lines[0].AccountPeriod, item.Lines[0].BatchNo));

        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
        {
            // Keyed by the invoice only, as legacy: a case number would make the member posting treat the claim as posted.
            var rows = (await ws.QueryAsync<(string PostType, string Status, string By, string Batch, string? Case)>(
                "SELECT p.PostType, p.InvHStatus, p.PostBy, RTRIM(p.BatchNo), p.CASNumber FROM SUNPosting p JOIN ClaimInvoice i ON p.INVIndex = CAST(CAST(i.INVIndex AS bigint) AS nvarchar(50)) WHERE i.CASNumber = @case",
                new { @case = Case })).ToList();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(("InvHosp", "1", "TEST", posted.BatchNo, (string?)null), r));
            Assert.Equal(posted.BatchNo, (await ws.ExecuteScalarAsync<string>("SELECT TOP 1 RTRIM(BatchNo) FROM SunPostBatch ORDER BY BatchIndex DESC")));
        }
        Assert.DoesNotContain((await SearchAsync(Search())).Items, i => i.Key == $"{Case}/1");
        Assert.Contains((await SearchAsync(Search(posted: true, batch: posted.BatchNo))).Items, i => i.Key == $"{Case}/1");
        Assert.Single(factory.Audit.Entries, e => e.Action == "Posting.InvoiceHospital");

        // Posting the same claim again is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync($"{Url}/post", new HospitalInvoicePostRequest(Search(), [$"{Case}/1"], "01/2030"))).StatusCode);
    }

    [DatabaseFact]
    public async Task Posting_is_refused_without_a_hospital_account_code_an_open_period_or_outside_posting_hours()
    {
        await SeedAsync(hospitalAccount: null);
        var item = Assert.Single((await SearchAsync(Search())).Items, i => i.Key == $"{Case}/1");
        Assert.Contains("no account code", item.Problem);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/post", new HospitalInvoicePostRequest(Search(), [$"{Case}/1"], "01/2030"))).StatusCode);

        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("UPDATE HOS SET HOSAccCode = 'H000001' WHERE HOSCode = '00001'");
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/post", new HospitalInvoicePostRequest(Search(), [$"{Case}/1"], "13/2030"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/post", new HospitalInvoicePostRequest(Search() with { InvoiceDateTo = null, InvoiceNoFrom = "ZZINV" }, [$"{Case}/1"], "01/2030"))).StatusCode);

        await using (var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance))
            await maint.ExecuteAsync("UPDATE TimeMain SET StartTime = '1899-12-30 23:59:59' WHERE Process = 'POSTING'");
        Assert.False((await Client().GetFromJsonAsync<PostingOptions>($"{Url}/options"))!.Window.Open);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/post", new HospitalInvoicePostRequest(Search(), [$"{Case}/1"], "01/2030"))).StatusCode);
    }

    [DatabaseFact]
    public async Task The_screen_needs_its_legacy_access_and_a_narrowed_search()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(170).GetAsync($"{Url}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/search", new HospitalInvoicePostingSearch())).StatusCode);
        var o = (await Client().GetFromJsonAsync<PostingOptions>($"{Url}/options"))!;
        Assert.Contains(o.HealthTypes, h => h.Code == "N");
        Assert.NotEmpty(o.AccountPeriods);
    }
}

public class MemberPostingTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/posting/invoice-member", Case = "ZZPST00002";
    private static readonly DateOnly Day = new(2030, 1, 15);
    private HttpClient Client(int position = 170) => factory.ClientFor(WriteApiFactory.Access(position, 169));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static MemberInvoicePostingSearch Search(bool negative = false, bool posted = false, string? batch = null) =>
        new(ClaimDateFrom: Day, ClaimDateTo: Day, Posted: posted, BatchFrom: batch, BatchTo: batch, Negative: negative);

    /// <summary>A claim paying the member (CLMPayableByMedix2M) and a hospital, with a member and a hospital invoice on a bordereau; posting open from midnight.</summary>
    private async Task SeedAsync(decimal toMember, string reversed = "N")
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMAvailableLimit, MBMTopupInd)
                VALUES ('ZZPST0002*01', '00', 'A', 'MEMBER POSTING', 'IC2', 'ZZ', 'N', 'F', 'ZZ01', 'POLPST2', 20000, 'N');
                INSERT INTO CAS (CASNumber, CASClaimAdmin, MBMNumber, MBMPatientCovID, CASHOSCode, CASDateAdmitted, CASDischargeDate, CASStayType, ModalityCode)
                VALUES (@case, 'N', 'ZZPST0002*01', '00', '00001', '2030-01-01', '2030-01-05', 'I', 'S');
                UPDATE HOS SET HOSAccCode = 'H000001' WHERE HOSCode = '00001';
                """, new { @case = Case });
        await using (var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance))
            await maint.ExecuteAsync("UPDATE TimeMain SET StartTime = '1899-12-30 00:00:00', EndTime = '1899-12-30 00:00:00' WHERE Process = 'POSTING'; IF NOT EXISTS (SELECT 1 FROM AccPeriod WHERE AccMonth = '01' AND AccYear = '2030') INSERT INTO AccPeriod (AccMonth, AccYear) VALUES ('01', '2030')");
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        await ws.ExecuteAsync("""
            INSERT INTO Bordx (BordxRefNo, BordxDate, BordxPAYToStatus, BordxPAYDOCFRStatus, BordxMNRBToStatus, BordxMNRBDOCFRStatus, BordxPrintedStatus, ACPeriodStatus, BordxSendtoACStatus)
            VALUES ('ZZB0099902', '2030-01-20', '0', '0', '0', '0', 0, 0, 0);
            INSERT INTO Claim (CASNumber, ClaimVersion, ClaimStatus, ClaimBordxStatus, CLMTokenStatus, CLMCaseMgmtFees, CLMPrevPerDisLmt, CLMPayableByMedix2H, CLMPayableByMedix2M,
                               ClaimCloseDate, CLMBordxRevInd, BordxRefNo)
            VALUES (@case, 1, 'C', 1, 0, 0, 0, 400, @toMember, '2030-01-15', @reversed, 'ZZB0099902');
            INSERT INTO ClaimInvoice (INVWSversion, INVBordxed, INVWSed, InvNo, CASNumber, InvDate, INVEnteredDate, INVEnteredBy, INVPayee, INVPayTo, INVAmount, INVBordxRefNo, InvPrintStatus, INVDCMBMStatus)
            VALUES ('1', 1, 1, 'ZZMBM-A', @case, '2030-01-10', '2030-01-15', 'TEST', 'Member', 'M', 250, 'ZZB0099902', '0', '0'),
                   ('1', 1, 1, 'ZZHOS-A', @case, '2030-01-10', '2030-01-15', 'TEST', '00001', 'H', 400, 'ZZB0099902', '0', '0');
            """, new { @case = Case, toMember, reversed });
    }

    private async Task<PostingJournal> SearchAsync(MemberInvoicePostingSearch s)
    {
        var res = await Client().PostAsJsonAsync($"{Url}/search", s);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PostingJournal>())!;
    }

    [DatabaseFact]
    public async Task A_positive_claim_is_an_MJC_journal_crediting_the_member_and_posts_by_case_and_version()
    {
        await SeedAsync(250);
        var item = Assert.Single((await SearchAsync(Search())).Items, i => i.Key == $"{Case}/1");
        Assert.Null(item.Problem);
        var (m, p) = (item.Lines[0], item.Lines[1]);
        Assert.Equal(("MJC", "ZZPST00002-1", "8200ZZ", 250m, "C", "0R"), (m.JournalType, m.TransRef, m.AccountCode, m.Amount, m.DebitCredit, m.Hospital));
        Assert.Equal(("6000ZZ", "D", new DateOnly(2030, 1, 15)), (p.AccountCode, p.DebitCredit, p.TransDate));
        Assert.DoesNotContain((await SearchAsync(Search(negative: true))).Items, i => i.Key == $"{Case}/1");

        // Posting the claim's hospital invoice first does not make it look posted to the member posting.
        var hosp = await Client().PostAsJsonAsync("/api/posting/invoice-hospital/post",
            new HospitalInvoicePostRequest(new HospitalInvoicePostingSearch(InvoiceDateFrom: Day, InvoiceDateTo: Day), [$"{Case}/1"], "01/2030"));
        Assert.Equal(HttpStatusCode.OK, hosp.StatusCode);
        Assert.Contains((await SearchAsync(Search())).Items, i => i.Key == $"{Case}/1");

        var res = await Client().PostAsJsonAsync($"{Url}/post", new MemberInvoicePostRequest(Search(), [$"{Case}/1"], "01/2030"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var posted = (await res.Content.ReadFromJsonAsync<PostingResult>())!;
        Assert.Equal("01/2030", Assert.Single(posted.Journal.Items).Lines[0].AccountPeriod);
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
        {
            var row = await ws.QuerySingleAsync<(string? Inv, string Status, string Batch)>(
                "SELECT INVIndex, InvMStatus, RTRIM(BatchNo) FROM SUNPosting WHERE CASNumber = @case AND RTRIM(ClmVersion) = '1' AND PostType = 'InvMBM'", new { @case = Case });
            Assert.Equal(((string?)null, "1", posted.BatchNo), row);
        }
        Assert.DoesNotContain((await SearchAsync(Search())).Items, i => i.Key == $"{Case}/1");
        Assert.Contains((await SearchAsync(Search(posted: true, batch: posted.BatchNo))).Items, i => i.Key == $"{Case}/1");
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync($"{Url}/post", new MemberInvoicePostRequest(Search(), [$"{Case}/1"], "01/2030"))).StatusCode);
    }

    [DatabaseFact]
    public async Task A_negative_claim_is_an_MJD_journal_with_the_sides_swapped_and_6200_when_the_bordereau_was_reversed()
    {
        await SeedAsync(-120, reversed: "Y");
        var item = Assert.Single((await SearchAsync(Search(negative: true))).Items, i => i.Key == $"{Case}/1");
        Assert.Equal(("MJD", "6200ZZ", 120m, "D"), (item.Lines[0].JournalType, item.Lines[0].AccountCode, item.Lines[0].Amount, item.Lines[0].DebitCredit));
        Assert.Equal(("6000ZZ", "C"), (item.Lines[1].AccountCode, item.Lines[1].DebitCredit));
        Assert.DoesNotContain((await SearchAsync(Search())).Items, i => i.Key == $"{Case}/1");
    }

    [DatabaseFact]
    public async Task The_member_screen_needs_its_own_access_and_a_narrowed_search()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(169)).GetAsync($"{Url}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/search", new MemberInvoicePostingSearch())).StatusCode);
    }
}

public class HospitalPaymentPostingTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/posting/payment-hospital";
    private static readonly DateOnly Day = new(2030, 2, 10);
    private HttpClient Client(int position = 173) => factory.ClientFor(WriteApiFactory.Access(position));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static HospitalPaymentPostingSearch Search(bool posted = false, string? batch = null) =>
        new(VoucherDateFrom: Day, VoucherDateTo: Day, Posted: posted, BatchFrom: batch, BatchTo: batch);

    /// <summary>Voucher CAZZ00001 pays two cases at hospital 00001 (one cheque), CFZZ00002 pays one; posting open from midnight.</summary>
    private async Task SeedAsync()
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMAvailableLimit, MBMTopupInd)
                VALUES ('ZZPST0003*01', '00', 'A', 'PAYMENT POSTING', 'IC3', 'ZZ', 'N', 'F', 'ZZ01', 'POLPST3', 20000, 'N');
                INSERT INTO CAS (CASNumber, CASClaimAdmin, MBMNumber, MBMPatientCovID, CASHOSCode, CASDateAdmitted, CASDischargeDate, CASStayType, ModalityCode)
                VALUES ('ZZPST00003', 'N', 'ZZPST0003*01', '00', '00001', '2030-01-01', '2030-01-05', 'I', 'S'),
                       ('ZZPST00004', 'N', 'ZZPST0003*01', '00', '00001', '2030-01-06', '2030-01-08', 'I', 'S');
                UPDATE HOS SET HOSAccCode = 'H000001' WHERE HOSCode = '00001';
                """);
        await using (var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance))
            await maint.ExecuteAsync("UPDATE TimeMain SET StartTime = '1899-12-30 00:00:00', EndTime = '1899-12-30 00:00:00' WHERE Process = 'POSTING'; IF NOT EXISTS (SELECT 1 FROM AccPeriod WHERE AccMonth = '02' AND AccYear = '2030') INSERT INTO AccPeriod (AccMonth, AccYear) VALUES ('02', '2030')");
        await using var pay = await factory.Db.OpenAsync(GenesisDb.Payment);
        await pay.ExecuteAsync("""
            INSERT INTO Payment2HOS (PVHOSNo, PVHOSDate, PVHOSChequeNo, PVHOSChequeAmt, CASNumber, ClaimVersion, PVStatus)
            VALUES ('CAZZ00001', '2030-02-10', 'CHQ1', 1500, 'ZZPST00003', '1', 'F'), ('CAZZ00001', '2030-02-10', 'CHQ1', 1500, 'ZZPST00004', '1', 'F'),
                   ('CFZZ00002', '2030-02-10', 'CHQ2', 700, 'ZZPST00003', '2', 'F');
            """);
    }

    private async Task<PostingJournal> SearchAsync(HospitalPaymentPostingSearch s)
    {
        var res = await Client().PostAsJsonAsync($"{Url}/search", s);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PostingJournal>())!;
    }

    [DatabaseFact]
    public async Task A_voucher_is_one_CP_journal_crediting_the_bank_and_debiting_the_hospital_and_posts_by_voucher_number()
    {
        await SeedAsync();
        var j = await SearchAsync(Search());
        var ca = Assert.Single(j.Items, i => i.Key == "CAZZ00001");
        Assert.Null(ca.Problem);
        Assert.Equal(["ZZPST00003", "ZZPST00004"], ca.References);
        Assert.Equal(("CP", "7400", 1500m, "C", "CHQ1-00001"), (ca.Lines[0].JournalType, ca.Lines[0].AccountCode, ca.Lines[0].Amount, ca.Lines[0].DebitCredit, ca.Lines[0].Description));
        Assert.Equal(("H000001", "D", "000001"), (ca.Lines[1].AccountCode, ca.Lines[1].DebitCredit, ca.Lines[1].Hospital));
        // FWHS vouchers start CF, for which the legacy form had no bank account.
        Assert.Contains("CF", Assert.Single(j.Items, i => i.Key == "CFZZ00002").Problem);

        var res = await Client().PostAsJsonAsync($"{Url}/post", new HospitalPaymentPostRequest(Search(), ["CAZZ00001"], "02/2030"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var posted = (await res.Content.ReadFromJsonAsync<PostingResult>())!;
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            Assert.Equal(("1", posted.BatchNo), await ws.QuerySingleAsync<(string, string)>(
                "SELECT PayHStatus, RTRIM(BatchNo) FROM SUNPosting WHERE TransRef = 'CAZZ00001' AND PostType = 'PayHosp'"));
        Assert.DoesNotContain((await SearchAsync(Search())).Items, i => i.Key == "CAZZ00001");
        Assert.Contains((await SearchAsync(Search(posted: true, batch: posted.BatchNo))).Items, i => i.Key == "CAZZ00001");
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/post", new HospitalPaymentPostRequest(Search(), ["CFZZ00002"], "02/2030"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(169)).GetAsync($"{Url}/options")).StatusCode);
    }
}

public class PayorReceiptPostingTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/posting/receipt-payor";
    private static readonly DateOnly Day = new(2030, 3, 10);
    private HttpClient Client(int position = 172) => factory.ClientFor(WriteApiFactory.Access(position));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static PayorReceiptPostingSearch Search(bool posted = false, string? batch = null) =>
        new(ReceiptDateFrom: Day, ReceiptDateTo: Day, Posted: posted, BatchFrom: batch, BatchTo: batch);

    /// <summary>Receipt RAZZ00001 from payor ZZ covers two cases (amount on each row), RFZZ00002 one; posting open from midnight.</summary>
    private async Task SeedAsync(string? payorAccount = "P00ZZ")
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM PAY WHERE PAYCode = 'ZZ') INSERT INTO PAY (PAYCode, PAYCompanyName, HLTCode) VALUES ('ZZ', 'ZZ TEST PAYOR', 'N');
                UPDATE PAY SET PAYAccCode = @acc WHERE PAYCode = 'ZZ';
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMAvailableLimit, MBMTopupInd)
                VALUES ('ZZPST0005*01', '00', 'A', 'RECEIPT POSTING', 'IC5', 'ZZ', 'N', 'F', 'ZZ01', 'POLPST5', 20000, 'N');
                INSERT INTO CAS (CASNumber, CASClaimAdmin, MBMNumber, MBMPatientCovID, CASHOSCode, CASDateAdmitted, CASDischargeDate, CASStayType, ModalityCode)
                VALUES ('ZZPST00005', 'N', 'ZZPST0005*01', '00', '00001', '2030-01-01', '2030-01-05', 'I', 'S'),
                       ('ZZPST00006', 'N', 'ZZPST0005*01', '00', '00001', '2030-01-06', '2030-01-08', 'I', 'S');
                """, new { acc = payorAccount });
        await using (var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance))
            await maint.ExecuteAsync("UPDATE TimeMain SET StartTime = '1899-12-30 00:00:00', EndTime = '1899-12-30 00:00:00' WHERE Process = 'POSTING'; IF NOT EXISTS (SELECT 1 FROM AccPeriod WHERE AccMonth = '03' AND AccYear = '2030') INSERT INTO AccPeriod (AccMonth, AccYear) VALUES ('03', '2030')");
        await using var pay = await factory.Db.OpenAsync(GenesisDb.Payment);
        await pay.ExecuteAsync("""
            INSERT INTO ReceiptfrPAY (ReceiptNo, ReceiptDate, ReceiptAmt, CASNumber, ClaimVersion) VALUES ('RAZZ00001', '2030-03-10', 900, 'ZZPST00005', '1'),
                   ('RAZZ00001', '2030-03-10', 900, 'ZZPST00006', '1'), ('RFZZ00002', '2030-03-10', 300, 'ZZPST00005', '2');
            """);
    }

    private async Task<PostingJournal> SearchAsync(PayorReceiptPostingSearch s)
    {
        var res = await Client().PostAsJsonAsync($"{Url}/search", s);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PostingJournal>())!;
    }

    [DatabaseFact]
    public async Task A_receipt_is_one_CR_journal_debiting_the_bank_and_crediting_the_payor_and_posts_once_by_receipt_number()
    {
        await SeedAsync();
        var j = await SearchAsync(Search());
        var ra = Assert.Single(j.Items, i => i.Key == "RAZZ00001");
        Assert.Null(ra.Problem);
        Assert.Equal(("CR", "7400", 900m, "D", "ZZ"), (ra.Lines[0].JournalType, ra.Lines[0].AccountCode, ra.Lines[0].Amount, ra.Lines[0].DebitCredit, ra.Lines[0].Description));
        Assert.Equal(("P00ZZ", "C"), (ra.Lines[1].AccountCode, ra.Lines[1].DebitCredit));
        Assert.Contains("RF", Assert.Single(j.Items, i => i.Key == "RFZZ00002").Problem);

        var res = await Client().PostAsJsonAsync($"{Url}/post", new PayorReceiptPostRequest(Search(), ["RAZZ00001"], "03/2030"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var posted = (await res.Content.ReadFromJsonAsync<PostingResult>())!;
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            Assert.Equal(("1", posted.BatchNo), await ws.QuerySingleAsync<(string, string)>(
                "SELECT RecPayStatus, RTRIM(BatchNo) FROM SUNPosting WHERE TransRef = 'RAZZ00001' AND PostType = 'RecPayor'"));
        Assert.DoesNotContain((await SearchAsync(Search())).Items, i => i.Key == "RAZZ00001");
        Assert.Contains((await SearchAsync(Search(posted: true, batch: posted.BatchNo))).Items, i => i.Key == "RAZZ00001");
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(173)).GetAsync($"{Url}/options")).StatusCode);
    }

    [DatabaseFact]
    public async Task A_payor_without_an_account_code_cannot_be_posted()
    {
        await SeedAsync(payorAccount: null);
        Assert.Contains("PAYAccCode", Assert.Single((await SearchAsync(Search())).Items, i => i.Key == "RAZZ00001").Problem);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/post", new PayorReceiptPostRequest(Search(), ["RAZZ00001"], "03/2030"))).StatusCode);
    }
}

public class BordxPayorPostingTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/posting/bordx-payor", Bordx = "ZZB0099907";
    private static readonly DateOnly Day = new(2030, 4, 10);
    private HttpClient Client(int position = 176) => factory.ClientFor(WriteApiFactory.Access(position));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static BordxPayorPostingSearch Search(bool posted = false, string? batch = null) =>
        new(BordxDateFrom: Day, BordxDateTo: Day, Posted: posted, BatchFrom: batch, BatchTo: batch);

    /// <summary>A bordereau with two claims of payor ZZ: one paying a hospital (RM 1,000 with RM 50 case management fees), one paying the member (RM 300).</summary>
    private async Task SeedAsync(string? payorAccount = "P00ZZ")
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM PAY WHERE PAYCode = 'ZZ') INSERT INTO PAY (PAYCode, PAYCompanyName, HLTCode) VALUES ('ZZ', 'ZZ TEST PAYOR', 'N');
                UPDATE PAY SET PAYAccCode = @acc WHERE PAYCode = 'ZZ';
                UPDATE HOS SET HOSAccCode = 'H000001' WHERE HOSCode = '00001';
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMAvailableLimit, MBMTopupInd)
                VALUES ('ZZPST0007*01', '00', 'A', 'BORDX POSTING', 'IC7', 'ZZ', 'N', 'F', 'ZZ01', 'POLPST7', 20000, 'N');
                INSERT INTO CAS (CASNumber, CASClaimAdmin, MBMNumber, MBMPatientCovID, CASHOSCode, CASDateAdmitted, CASDischargeDate, CASStayType, ModalityCode)
                VALUES ('ZZPST00007', 'N', 'ZZPST0007*01', '00', '00001', '2030-03-01', '2030-03-05', 'I', 'S'),
                       ('ZZPST00008', 'N', 'ZZPST0007*01', '00', '00001', '2030-03-06', '2030-03-08', 'I', 'S');
                """, new { acc = payorAccount });
        await using (var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance))
            await maint.ExecuteAsync("UPDATE TimeMain SET StartTime = '1899-12-30 00:00:00', EndTime = '1899-12-30 00:00:00' WHERE Process = 'POSTING'; IF NOT EXISTS (SELECT 1 FROM AccPeriod WHERE AccMonth = '04' AND AccYear = '2030') INSERT INTO AccPeriod (AccMonth, AccYear) VALUES ('04', '2030')");
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        await ws.ExecuteAsync("""
            INSERT INTO Bordx (BordxRefNo, BordxDate, BordxPAYToStatus, BordxPAYDOCFRStatus, BordxMNRBToStatus, BordxMNRBDOCFRStatus, BordxPrintedStatus, ACPeriodStatus, BordxSendtoACStatus)
            VALUES (@bordx, '2030-04-10', '0', '0', '0', '0', 0, 0, 0);
            INSERT INTO Claim (CASNumber, ClaimVersion, ClaimStatus, ClaimBordxStatus, CLMTokenStatus, CLMCaseMgmtFees, CLMPrevPerDisLmt, CLMPayableByMedix2H, CLMPayableByMedix2M, CLMBordxAmt, BordxRefNo)
            VALUES ('ZZPST00007', 1, 'C', 1, 0, 50, 0, 950, 0, 1000, @bordx), ('ZZPST00008', 1, 'C', 1, 0, 0, 0, 0, 300, 300, @bordx);
            """, new { bordx = Bordx });
    }

    private async Task<PostingJournal> SearchAsync(BordxPayorPostingSearch s)
    {
        var res = await Client().PostAsJsonAsync($"{Url}/search", s);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PostingJournal>())!;
    }

    [DatabaseFact]
    public async Task A_bordereau_is_a_BI_journal_debiting_the_payor_and_crediting_6000_and_1030_for_each_claim()
    {
        await SeedAsync();
        var item = Assert.Single((await SearchAsync(Search())).Items, i => i.Key == Bordx);
        Assert.Null(item.Problem);
        Assert.Equal(["ZZPST00007", "ZZPST00008"], item.References);
        Assert.Equal(
            [("P00ZZ", 1000m, "D", "000001"), ("6000ZZ", 950m, "C", "000001"), ("1030", 50m, "C", "000001"), ("P00ZZ", 300m, "D", "0R"), ("6000ZZ", 300m, "C", "0R")],
            item.Lines.Select(l => (l.AccountCode, l.Amount, l.DebitCredit, l.Hospital)).ToList());
        Assert.Equal(("BI", "ZZPST00007-1", "00999"), (item.Lines[2].JournalType, item.Lines[2].Description, item.Lines[2].BordxRef));

        var res = await Client().PostAsJsonAsync($"{Url}/post", new BordxPayorPostRequest(Search(), [Bordx], "04/2030"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var posted = (await res.Content.ReadFromJsonAsync<PostingResult>())!;
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            Assert.Equal(("1", posted.BatchNo), await ws.QuerySingleAsync<(string, string)>(
                "SELECT BordxPayStatus, RTRIM(BatchNo) FROM SUNPosting WHERE TransRef = @bordx AND PostType = 'Bordx2PaySum'", new { bordx = Bordx }));
        Assert.DoesNotContain((await SearchAsync(Search())).Items, i => i.Key == Bordx);
        Assert.Contains((await SearchAsync(Search(posted: true, batch: posted.BatchNo))).Items, i => i.Key == Bordx);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(WriteApiFactory.Access(172)).GetAsync($"{Url}/options")).StatusCode);
    }

    [DatabaseFact]
    public async Task A_payor_without_an_account_code_cannot_be_posted()
    {
        await SeedAsync(payorAccount: null);
        Assert.Contains("PAYAccCode", Assert.Single((await SearchAsync(Search())).Items, i => i.Key == Bordx).Problem);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/post", new BordxPayorPostRequest(Search(), [Bordx], "04/2030"))).StatusCode);
    }
}
