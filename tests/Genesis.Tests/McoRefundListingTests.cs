using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Membership;

namespace Genesis.Tests;

public class McoRefundRuleTests
{
    private static McoRefundFilter Ok() => new("WM", "effective", new(2023, 1, 1), new(2023, 12, 31), "N");

    [Fact]
    public void Valid_filter_passes() => Assert.Null(McoRefundRules.Validate(Ok()));

    [Fact]
    public void Each_missing_criterion_is_named()
    {
        Assert.Equal("payor", McoRefundRules.Validate(Ok() with { Payor = "" })!.Value.Field);
        Assert.Equal("dateBy", McoRefundRules.Validate(Ok() with { DateBy = null })!.Value.Field);
        Assert.Equal("from", McoRefundRules.Validate(Ok() with { From = null, To = null })!.Value.Field);
        Assert.Equal("to", McoRefundRules.Validate(Ok() with { To = new(2022, 1, 1) })!.Value.Field);
        Assert.Equal("printStatus", McoRefundRules.Validate(Ok() with { PrintStatus = "X" })!.Value.Field);
        Assert.Null(McoRefundRules.Validate(Ok() with { To = null }));
    }
}

public class McoRefundListingTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/mco/refund-listing";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(21));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed(string cancelStatus)
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("UPDATE PAY SET MBMCancelStatus = @s WHERE PAYCode = 'WM'", new { s = cancelStatus });
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, MBMPolicyNo, PAYCode, INSCode, MBMPayorEffDate, MBMPayorExpDate, MBMBordxDate) VALUES
              ('ZZMCO0001*01', '00', 'C', 'CANCELLED ONE', 'IC1', 'P1', 'WM', 'F', '2023-01-01', '2023-12-31', '2023-01-05'),
              ('ZZMCO0002*01', '00', 'C', 'LATE ENTRY', 'IC2', 'P2', 'WM', 'F', '2023-01-01', '2023-12-31', '2023-01-05'),
              ('ZZMCO0003*01', '00', 'A', 'STILL ACTIVE', 'IC3', 'P3', 'WM', 'F', '2023-01-01', '2023-12-31', '2023-01-05'),
              ('ZZMCO0004*01', '00', 'C', 'WITH CR NOTE', 'IC4', 'P4', 'WM', 'F', '2023-01-01', '2023-12-31', '2023-01-05')
            """);
        await main.ExecuteAsync("""
            INSERT INTO MBMTwo (MBMNumber, MBMENDtEffDate, MBMENDtBordxDate) VALUES
              ('ZZMCO0001*01', '2023-03-01', '2023-03-10'), ('ZZMCO0002*01', '2023-03-01', '2023-05-15'), ('ZZMCO0003*01', '2023-03-01', '2023-03-10'), ('ZZMCO0004*01', '2023-03-02', '2023-03-12')
            """);
        await main.ExecuteAsync("""
            INSERT INTO MBMOthers (MBMNumber, MBMMCOFee, MBMCRNote, MBMCRNoteDate, MBMInvoiceNo) VALUES
              ('ZZMCO0001*01', 100, NULL, NULL, 'INV1'), ('ZZMCO0002*01', 100, NULL, NULL, NULL), ('ZZMCO0003*01', 100, NULL, NULL, NULL), ('ZZMCO0004*01', 50, 'CR0001', '2023-04-01', NULL)
            """);
        await main.ExecuteAsync("INSERT INTO MBMRefund (MBMNumber, MBMCovID, MBMMCORefund, MBMMCORefListPrinted, MBMRefInvoiced) VALUES ('ZZMCO0001*01', '00', 40, 0, 'N'), ('ZZMCO0002*01', '00', 30, 0, 'N'), ('ZZMCO0004*01', '00', 20, 0, 'N')");
        await main.ExecuteAsync("INSERT INTO CAS (CASNumber, CASClaimAdmin, MBMNumber, MBMPatientCovID) VALUES ('ZZMCOCAS01', 'N', 'ZZMCO0001*01', '00')");
    }

    [Fact]
    public async Task Needs_the_refund_listing_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}/payors")).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}/payors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}?payor=WM&dateBy=effective&from=2023-01-01&printStatus=N")).StatusCode);
    }

    [DatabaseFact]
    public async Task Payors_include_the_cancel_status_flag()
    {
        var payors = (await Client().GetFromJsonAsync<List<McoPayor>>($"{Url}/payors"))!;
        Assert.Contains(payors, p => p.Code == "WM");
    }

    [DatabaseFact]
    public async Task Cancel_status_payors_list_only_cancelled_members_entered_within_30_days()
    {
        await Seed("Y");
        var l = (await Client().GetFromJsonAsync<McoRefundListing>($"{Url}?payor=WM&dateBy=effective&from=2023-03-01&to=2023-03-31&printStatus=N"))!;
        Assert.True(l.CancelledOnly);
        var row = Assert.Single(l.Rows);
        Assert.Equal(("ZZMCO0001*01", "C", 100m, 40m, 60m, 1, 59, 2), (row.MemberNumber, row.Status, row.McoFee, row.McoRefund, row.McoUtilised, row.TotalCases, row.DaysUtilised, row.MonthsUtilised));
        Assert.Equal(("INV1", new DateOnly(2023, 3, 10)), (row.InvoiceNo, row.EndorsementBordx));
        Assert.Equal(40m, l.TotalRefund);
    }

    [DatabaseFact]
    public async Task Other_payors_list_every_member_with_an_endorsement_and_the_print_status_selects_credit_notes()
    {
        await Seed("N");
        var all = (await Client().GetFromJsonAsync<McoRefundListing>($"{Url}?payor=WM&dateBy=effective&from=2023-03-01&to=2023-03-31&printStatus=N"))!;
        Assert.False(all.CancelledOnly);
        Assert.Equal(["ZZMCO0001*01", "ZZMCO0002*01", "ZZMCO0003*01"], all.Rows.Select(r => r.MemberNumber).Where(n => n.StartsWith("ZZMCO")));
        Assert.Equal(70m, all.Rows.Where(r => r.MemberNumber.StartsWith("ZZMCO")).Sum(r => r.McoRefund ?? 0));
        var printed = (await Client().GetFromJsonAsync<McoRefundListing>($"{Url}?payor=WM&dateBy=effective&from=2023-03-01&to=2023-03-31&printStatus=Y"))!;
        var row = Assert.Single(printed.Rows.Where(r => r.MemberNumber.StartsWith("ZZMCO")));
        Assert.Equal(("ZZMCO0004*01", "CR0001", new DateOnly(2023, 4, 1)), (row.MemberNumber, row.CreditNote, row.CreditNoteDate));
        var byBordx = (await Client().GetFromJsonAsync<McoRefundListing>($"{Url}?payor=WM&dateBy=bordx&from=2023-05-01&to=2023-05-31&printStatus=N"))!;
        Assert.Equal("ZZMCO0002*01", Assert.Single(byBordx.Rows.Where(r => r.MemberNumber.StartsWith("ZZMCO"))).MemberNumber);
    }

    [DatabaseFact]
    public async Task Supplementary_persons_and_bad_criteria()
    {
        await Seed("Y");
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName, MBMCICBCPPNo, MBMCStatus, MBMCMCO) VALUES ('ZZMCO0001*01', '01', 'SPOUSE', 'ICS', 'C', 30), ('ZZMCO0001*01', '02', 'CHILD', 'ICC', 'A', 20)");
            await main.ExecuteAsync("INSERT INTO MBMRefund (MBMNumber, MBMCovID, MBMMCORefund, MBMMCORefListPrinted, MBMRefInvoiced) VALUES ('ZZMCO0001*01', '01', 12, 0, 'N')");
        }
        var s = (await Client().GetFromJsonAsync<List<McoRefundRow>>($"{Url}/supplementary?memberNumber=ZZMCO0001*01"))!;
        var one = Assert.Single(s);
        Assert.Equal(("01", "SPOUSE", 30m, 12m, 18m), (one.CoverId, one.Name, one.McoFee, one.McoRefund, one.McoUtilised));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync($"{Url}?payor=WM&dateBy=effective&printStatus=N")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync($"{Url}?dateBy=effective&from=2023-01-01&printStatus=N")).StatusCode);
    }
}
