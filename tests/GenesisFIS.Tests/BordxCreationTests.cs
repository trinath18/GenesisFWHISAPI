using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Bordereaux;
using GenesisFIS.Core.Data;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GenesisFIS.Tests;

public class BordxRuleTests
{
    [Fact]
    public void Bordereau_amounts_numbers_and_checks_follow_the_legacy_form()
    {
        Assert.Equal(1050m, BordxRules.Amount("N", "H", 900, 0, 1000, 50));
        Assert.Equal(950m, BordxRules.Amount("Y", "H", 900, 0, 1000, 50));
        Assert.Equal(350m, BordxRules.Amount("Y", "M", 0, 300, 1000, 50));
        Assert.Equal("NZZ00042", BordxRules.BordxNo("N", "ZZ", 42));
        Assert.Equal(("C", "R", "R"), (BordxRules.Status("H"), BordxRules.Status("M"), BordxRules.Status(null)));
        Assert.Equal("Date of Discharge is Empty.", BordxRules.Problem(null, "HOS", "A00", "AA"));
        Assert.Equal("Hospital cannot be empty!", BordxRules.Problem(new DateOnly(2030, 1, 1), " ", "A00", "AA"));
        Assert.Equal("Final ICD Code cannot be empty!", BordxRules.Problem(new DateOnly(2030, 1, 1), "HOS", null, "AA"));
        Assert.Equal("Claimability is Not Decided!", BordxRules.Problem(new DateOnly(2030, 1, 1), "HOS", "A00", "NN"));
        Assert.Null(BordxRules.Problem(new DateOnly(2030, 1, 1), "HOS", "A00", "RR"));
        Assert.Equal((false, false, true, true), (BordxRules.Sent("0", "0"), BordxRules.Sent(null, " "), BordxRules.Sent("1", "0"), BordxRules.Sent("0", "T")));
        Assert.Null(BordxRules.ScanProblem(["claim form.TIF", "notes.txt"]));
        Assert.Equal("Please ensure scanned documents are in system!", BordxRules.ScanProblem(["notes.txt"]));
        Assert.Equal("Please ensure scanned documents are in system!", BordxRules.ScanProblem([]));
        Assert.StartsWith("~02/01/2030 10:00:00         BORDX DONE BY TEST -  NZZ00001", BordxRules.CaseRemark(new DateTime(2030, 1, 2, 10, 0, 0), "TEST", "NZZ00001"));
    }
}

public class BordxCreationTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/bordx/insert-worksheet", A = "ZZBDX00001", B = "ZZBDX00002", C = "ZZBDX00003", M = "ZZBDX00004";
    private HttpClient Client(int position = 30) => factory.ClientFor(WriteApiFactory.Access(position));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    /// <summary>
    /// Closed claims of payor ZZ not on a bordereau: A (approved 1,000 + fee 50) and B (300), paid to the hospital; C has no final diagnosis; M is paid to
    /// the member. The ZZ bordereau sequence is set to 00041.
    /// </summary>
    private async Task SeedAsync()
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM PAY WHERE PAYCode = 'ZZ') INSERT INTO PAY (PAYCode, PAYCompanyName, HLTCode) VALUES ('ZZ', 'ZZ TEST PAYOR', 'N');
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMAvailableLimit, MBMTopupInd)
                VALUES ('ZZBDX0001*01', '00', 'A', 'BORDX MEMBER', 'IC8', 'ZZ', 'N', 'F', 'ZZ01', 'POLBDX', 20000, 'N');
                INSERT INTO CAS (CASNumber, CASClaimAdmin, MBMNumber, MBMPatientCovID, CASHOSCode, CASDateAdmitted, CASDischargeDate, CASClaimability, CASPending)
                VALUES (@A, 'N', 'ZZBDX0001*01', '00', '00001', '2030-03-01', '2030-03-04', 'AA', 'Y'), (@B, 'N', 'ZZBDX0001*01', '00', '00001', '2030-03-02', '2030-03-05', 'AA', 'Y'),
                       (@C, 'N', 'ZZBDX0001*01', '00', '00001', '2030-03-03', '2030-03-06', 'AA', 'Y'), (@M, 'N', 'ZZBDX0001*01', '00', '00001', '2030-03-03', '2030-03-06', 'AA', 'Y');
                INSERT INTO DIAFinal (CASNumber, ICDCode1) VALUES (@A, 'A00'), (@B, 'B00'), (@M, 'M00');
                """, new { A, B, C, M });
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        await ws.ExecuteAsync("""
            INSERT INTO Claim (CASNumber, ClaimVersion, ClaimStatus, ClaimBordxStatus, CLMTokenStatus, CLMCaseMgmtFees, CLMPrevPerDisLmt, CLMPayableByMedix2H, CLMPayableByMedix2M, CLMTotalApproved, CLMPending, ClaimCloseDate)
            VALUES (@A, 1, 'C', 0, 0, 50, 0, 1000, 0, 1000, 'Y', '2030-03-10'), (@B, 1, 'C', 0, 0, 0, 0, 300, 0, 300, 'Y', '2030-03-10'),
                   (@C, 1, 'C', 0, 0, 0, 0, 200, 0, 200, 'Y', '2030-03-10'), (@M, 1, 'C', 0, 0, 0, 0, 0, 250, 250, 'Y', '2030-03-10');
            INSERT INTO ClaimInvoice (INVWSversion, INVBordxed, INVWSed, InvNo, CASNumber, InvDate, INVEnteredDate, INVEnteredBy, INVPayee, INVPayTo, INVAmount, InvPrintStatus, INVDCMBMStatus)
            VALUES ('1', 0, 1, 'ZZBINV-A', @A, '2030-03-04', '2030-03-05', 'TEST', '00001', 'H', 1000, '0', '0'), ('1', 0, 1, 'ZZBINV-B', @B, '2030-03-05', '2030-03-05', 'TEST', '00001', 'H', 300, '0', '0'),
                   ('1', 0, 1, 'ZZBINV-C', @C, '2030-03-06', '2030-03-06', 'TEST', '00001', 'H', 200, '0', '0'), ('1', 0, 1, 'ZZBINV-M', @M, '2030-03-06', '2030-03-06', 'TEST', 'Member', 'M', 250, '0', '0');
            IF EXISTS (SELECT 1 FROM CLAIM_BordxSEQ WHERE PAYCode = 'ZZ') UPDATE CLAIM_BordxSEQ SET CLMBordxNo = '00041' WHERE PAYCode = 'ZZ'
            ELSE INSERT INTO CLAIM_BordxSEQ (HLTCode, PAYCode, CLMBordxNo) VALUES ('N', 'ZZ', '00041');
            """, new { A, B, C, M });
    }

    private static async Task<T> Ok<T>(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<PendingBordxClaim> PendingAsync(string no) =>
        Assert.Single((await Ok<PendingBordxListing>(await Client().PostAsJsonAsync($"{Url}/pending", new PendingBordxSearch(no)))).Claims);

    [DatabaseFact]
    public async Task Lists_the_closed_claims_waiting_for_a_bordereau_with_their_checks()
    {
        await SeedAsync();
        var a = await PendingAsync(A);
        Assert.Equal(("ZZ", "H", 1050m, "BORDX MEMBER", "A00", null as string), (a.Payor, a.PayTo, a.BordxAmount, a.Patient, a.Icd, a.Problem));
        Assert.NotNull(a.Hospital);
        Assert.Equal("Final ICD Code cannot be empty!", (await PendingAsync(C)).Problem);
        Assert.Equal("M", (await PendingAsync(M)).PayTo);
        var byDischarge = await Ok<PendingBordxListing>(await Client().PostAsJsonAsync($"{Url}/pending", new PendingBordxSearch(DischargedFrom: new DateOnly(2030, 3, 5), DischargedTo: new DateOnly(2030, 3, 5))));
        Assert.Contains(byDischarge.Claims, c => c.CaseNumber == B);
        Assert.DoesNotContain(byDischarge.Claims, c => c.CaseNumber == A);
    }

    [DatabaseFact]
    public async Task Where_scanned_documents_are_configured_a_claim_without_them_cannot_go_on_a_bordereau()
    {
        await SeedAsync();
        var root = Path.Combine(Path.GetTempPath(), "genesis-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, A));
        await File.WriteAllBytesAsync(Path.Combine(root, A, "claim.jpg"), [0xFF, 0xD8]);
        Directory.CreateDirectory(Path.Combine(root, B));
        await File.WriteAllTextAsync(Path.Combine(root, B, "note.txt"), "not a scan");
        try
        {
            using var app = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["FileStore:ScannedDocsRoot"] = root })));
            var (token, _) = app.Services.GetRequiredService<TokenService>().Create(new UserRecord(WriteApiFactory.TestUser, "Test User", null, "A", "ITD", "ITD", WriteApiFactory.Access(30), false));
            var client = app.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            async Task<PendingBordxClaim> Pending(string no) =>
                Assert.Single((await Ok<PendingBordxListing>(await client.PostAsJsonAsync($"{Url}/pending", new PendingBordxSearch(no)))).Claims);
            Assert.Null((await Pending(A)).Problem);
            Assert.Equal(BordxRules.NoScannedDocuments, (await Pending(B)).Problem);
            Assert.Equal("Final ICD Code cannot be empty!", (await Pending(C)).Problem);
            var refused = await client.PostAsJsonAsync(Url, new CreateBordxRequest(BordxOption.Multiple, [new(B, 1)]));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("scanned documents", await refused.Content.ReadAsStringAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [DatabaseFact]
    public async Task Multiple_claims_go_on_one_new_bordereau()
    {
        await SeedAsync();
        var r = await Ok<CreateBordxResult>(await Client().PostAsJsonAsync(Url, new CreateBordxRequest(BordxOption.Multiple, [new(A, 1), new(B, 1)])));
        var bordx = Assert.Single(r.Bordereaux);
        Assert.Equal(("NZZ00042", "ZZ", "C", 2, 1350m), (bordx.BordxNo, bordx.Payor, bordx.Status, bordx.Claims, bordx.Amount));
        Assert.All(r.Claims, c => Assert.Equal("NZZ00042", c.BordxNo));

        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        Assert.Equal((1350m, (short)2, "C", "TEST"), await ws.QuerySingleAsync<(decimal, short, string, string)>("SELECT BordxClaimAmt, BordxNoClaims, BordxStatus, BordxBy FROM Bordx WHERE BordxRefNo = 'NZZ00042'"));
        Assert.Equal(2, await ws.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Claim WHERE BordxRefNo = 'NZZ00042' AND ClaimBordxStatus = 1 AND CLMPending = 'N'"));
        Assert.Equal(2, await ws.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ClaimInvoice WHERE INVBordxRefNo = 'NZZ00042' AND INVBordxed = 1"));
        Assert.Equal("00042", (await ws.ExecuteScalarAsync<string>("SELECT CLMBordxNo FROM CLAIM_BordxSEQ WHERE PAYCode = 'ZZ'"))!.Trim());
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        Assert.Equal("N", await main.ExecuteScalarAsync<string>("SELECT CASPending FROM CAS WHERE CASNumber = @A", new { A }));
        Assert.Contains("BORDX DONE BY TEST -  NZZ00042", await main.ExecuteScalarAsync<string>("SELECT CASORemarks FROM CASRemarks WHERE CASNumber = @A", new { A }));

        // The claims are no longer pending.
        Assert.Empty((await Ok<PendingBordxListing>(await Client().PostAsJsonAsync($"{Url}/pending", new PendingBordxSearch(A)))).Claims);
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync(Url, new CreateBordxRequest(BordxOption.Multiple, [new(A, 1)]))).StatusCode);
    }

    [DatabaseFact]
    public async Task Single_gives_each_claim_its_own_bordereau_and_member_claims_are_status_R()
    {
        await SeedAsync();
        var r = await Ok<CreateBordxResult>(await Client().PostAsJsonAsync(Url, new CreateBordxRequest(BordxOption.Single, [new(B, 1), new(M, 1)])));
        Assert.Equal([("NZZ00042", "C", 300m), ("NZZ00043", "R", 250m)], r.Bordereaux.Select(b => (b.BordxNo, b.Status, b.Amount)));
    }

    private const string Revise = "/api/bordx/remove-worksheet";

    [DatabaseFact]
    public async Task Remove_worksheet_takes_claims_off_the_bordereau_and_back_to_the_pending_list()
    {
        await SeedAsync();
        await Ok<CreateBordxResult>(await Client().PostAsJsonAsync(Url, new CreateBordxRequest(BordxOption.Multiple, [new(A, 1), new(B, 1)])));
        var revise = factory.ClientFor(WriteApiFactory.Access(31));
        var bordx = await Ok<BordxRevise>(await revise.GetAsync($"{Revise}/NZZ00042"));
        Assert.Equal((2, 1350m, false), (bordx.NoOfClaims, bordx.Amount, bordx.Sent));
        var b = bordx.Claims.Single(c => c.CaseNumber == B);
        Assert.Equal(("ZZBDX0001*01", "BORDX MEMBER", 300m), (b.MemberNumber, b.Patient, b.BordxAmount));

        var after = await Ok<BordxRevise>(await revise.PostAsJsonAsync($"{Revise}/NZZ00042/remove", new RemoveFromBordxRequest([new(B, 1)])));
        Assert.Equal((1, 1050m, A), (after.NoOfClaims, after.Amount, Assert.Single(after.Claims).CaseNumber));
        Assert.Equal(B, (await PendingAsync(B)).CaseNumber);
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        Assert.Equal(1, await ws.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM BordxWSArchive WHERE CASNumber = @B AND BordxRefNo = 'NZZ00042' AND LastUpdateUser = 'TEST'", new { B }));
        Assert.Equal((false, ""), await ws.QuerySingleAsync<(bool, string)>("SELECT INVBordxed, INVBordxRefNo FROM ClaimInvoice WHERE CASNumber = @B", new { B }));
        Assert.Single(factory.Audit.Entries, e => e.Action == "Bordx.Remove");

        Assert.Equal(HttpStatusCode.BadRequest, (await revise.PostAsJsonAsync($"{Revise}/NZZ00042/remove", new RemoveFromBordxRequest([new(B, 1)]))).StatusCode);
        await ws.ExecuteAsync("UPDATE Bordx SET BordxPAYToStatus = '1' WHERE BordxRefNo = 'NZZ00042'");
        Assert.True((await Ok<BordxRevise>(await revise.GetAsync($"{Revise}/NZZ00042"))).Sent);
        var sent = await revise.PostAsJsonAsync($"{Revise}/NZZ00042/remove", new RemoveFromBordxRequest([new(A, 1)]));
        Assert.Equal(HttpStatusCode.Conflict, sent.StatusCode);
        Assert.Contains("sent out", await sent.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await revise.GetAsync($"{Revise}/NZZ99999"))!.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().GetAsync($"{Revise}/NZZ00042")).StatusCode);
    }

    [DatabaseFact]
    public async Task Claims_failing_a_check_or_mixing_pay_to_are_refused()
    {
        await SeedAsync();
        var noIcd = await Client().PostAsJsonAsync(Url, new CreateBordxRequest(BordxOption.Multiple, [new(A, 1), new(C, 1)]));
        Assert.Equal(HttpStatusCode.BadRequest, noIcd.StatusCode);
        Assert.Contains("Final ICD Code", await noIcd.Content.ReadAsStringAsync());
        var mixed = await Client().PostAsJsonAsync(Url, new CreateBordxRequest(BordxOption.Multiple, [new(A, 1), new(M, 1)]));
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, new CreateBordxRequest(BordxOption.Multiple, []))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(31).PostAsJsonAsync($"{Url}/pending", new PendingBordxSearch())).StatusCode);
        await using var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet);
        Assert.Equal("00041", (await ws.ExecuteScalarAsync<string>("SELECT CLMBordxNo FROM CLAIM_BordxSEQ WHERE PAYCode = 'ZZ'"))!.Trim());
    }
}
