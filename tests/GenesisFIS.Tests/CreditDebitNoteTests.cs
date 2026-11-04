using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;

namespace GenesisFIS.Tests;

public class CreditDebitNoteRuleTests
{
    [Fact]
    public void A_claim_paying_the_member_gets_a_credit_note_and_one_recovering_from_the_member_a_debit_note()
    {
        Assert.Equal(("Credit", "Debit", null as string), (CreditDebitNoteRules.Kind(null, 10), CreditDebitNoteRules.Kind(null, -10), CreditDebitNoteRules.Kind(null, 0)));
        Assert.Equal("Debit", CreditDebitNoteRules.Kind("DLP00001", 10));
        Assert.Contains("already has note CLP1", CreditDebitNoteRules.Problem("CLP1", 10));
        Assert.Contains("pays nothing", CreditDebitNoteRules.Problem(null, 0));
        Assert.Null(CreditDebitNoteRules.Problem(" ", -5));
    }
}

public class CreditDebitNoteTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/worksheets/credit-debit-note", Paid = "ZZCDN00001", Owed = "ZZCDN00002", None = "ZZCDN00003", Done = "ZZCDN00004";
    private HttpClient Client(int position = 65) => factory.ClientFor(WriteApiFactory.Access(position));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    /// <summary>Claims of payor ZZ: one paying the member 250, one recovering 40 from the member, one paying only the hospital, one with a note already.</summary>
    private async Task SeedAsync()
    {
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        await ws.ExecuteAsync("""
            INSERT INTO Claim (CASNumber, ClaimVersion, ClaimStatus, ClaimBordxStatus, CLMTokenStatus, CLMCaseMgmtFees, CLMPrevPerDisLmt, CLMPayableByMedix2H, CLMPayableByMedix2M, CLMTotalActual, CLMBordxAmt, DebitCreditRefNo, ClaimCloseDate)
            VALUES (@Paid, 1, 'C', 0, 0, 0, 0, 0, 250, 260, 250, NULL, '2030-06-01'), (@Owed, 1, 'C', 0, 0, 0, 0, 500, -40, 540, 500, '', '2030-06-02'),
                   (@None, 1, 'C', 0, 0, 0, 0, 300, 0, 300, 300, NULL, '2030-06-03'), (@Done, 1, 'C', 0, 0, 0, 0, 0, 90, 90, 90, 'CZZ00007', '2030-06-04');
            IF EXISTS (SELECT 1 FROM CRSeq WHERE Payor = 'ZZ') UPDATE CRSeq SET CRSeq = '00010' WHERE Payor = 'ZZ' ELSE INSERT INTO CRSeq (CRSeq, Payor) VALUES ('00010', 'ZZ');
            DELETE FROM DRSeq WHERE Payor = 'ZZ';
            """, new { Paid, Owed, None, Done });
    }

    private static async Task<T> Ok<T>(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    [DatabaseFact]
    public async Task Generates_a_credit_or_debit_note_numbered_by_payor()
    {
        await SeedAsync();
        var found = await Ok<ClaimNote>(await Client().GetAsync($"{Url}/{Paid}/1"));
        Assert.Equal((250m, 260m, null as string, null as string), (found.PayableToMember, found.Actual, found.NoteNo, found.Problem));

        var credit = await Ok<ClaimNote>(await Client().PostAsync($"{Url}/{Paid}/1", null));
        Assert.Equal(("CZZ00011", "Credit", DateOnly.FromDateTime(DateTime.Today)), (credit.NoteNo, credit.Kind, credit.NoteDate!.Value));
        var debit = await Ok<ClaimNote>(await Client().PostAsync($"{Url}/{Owed}/1", null));
        Assert.Equal(("DZZ00001", "Debit"), (debit.NoteNo, debit.Kind));
        Assert.Equal(2, factory.Audit.Entries.Count(e => e.Action == "Claim.CreditDebitNote"));

        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsync($"{Url}/{Paid}/1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsync($"{Url}/{Done}/1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsync($"{Url}/{None}/1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"{Url}/ZZCDN99999/1")).StatusCode);
    }

    [DatabaseFact]
    public async Task Lists_the_claims_still_missing_a_note()
    {
        await SeedAsync();
        var before = await Ok<ClaimNoteListing>(await Client().GetAsync($"{Url}/missing"));
        Assert.Contains(before.Claims, c => c.CaseNumber == Paid);
        Assert.Contains(before.Claims, c => c.CaseNumber == Owed);
        Assert.DoesNotContain(before.Claims, c => c.CaseNumber is None or Done);
        await Ok<ClaimNote>(await Client().PostAsync($"{Url}/{Paid}/1", null));
        Assert.DoesNotContain((await Ok<ClaimNoteListing>(await Client().GetAsync($"{Url}/missing"))).Claims, c => c.CaseNumber == Paid);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(66).GetAsync($"{Url}/missing")).StatusCode);
    }
}
