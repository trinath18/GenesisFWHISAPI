using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Payments;

namespace GenesisFIS.Tests;

public class PaymentTrackingRuleTests
{
    private static PaymentTrackingLine L(string pv, int day, decimal paid, string hospital, string member, string bordx, string no = "ZZ1") =>
        new(pv, new DateOnly(2030, 5, day), paid, member, null, bordx, new DateOnly(2030, 4, 30), no, "1", hospital, "ZZ", "N", null, null, "F", 5, 2030);

    private static readonly PaymentTrackingLine[] Lines =
        [L("CF1", 1, 100, "HOS A", "M1", "B1", "ZZ1"), L("CF1", 1, 50, "HOS B", "M2", "B1", "ZZ2"), L("CF2", 2, 30, "HOS A", "M1", "B2", "ZZ3")];

    [Fact]
    public void The_grouped_reports_follow_the_legacy_stored_procedure()
    {
        Assert.Equal([("CF1", 2, 150m), ("CF2", 1, 30m)], PaymentTrackingReport.Group(PaymentTrackingReport.ByPv, Lines).Select(g => (g.Key, g.Cases, g.Paid)));
        Assert.Equal([("HOS A", 2, 130m), ("HOS B", 1, 50m)], PaymentTrackingReport.Group(PaymentTrackingReport.HospitalSummary, Lines).Select(g => (g.Key, g.Cases, g.Paid)));
        Assert.Equal(3, PaymentTrackingReport.Group(PaymentTrackingReport.ByHospital, Lines).Count); // by hospital and PV date
        Assert.Equal([("M1", 130m), ("M2", 50m)], PaymentTrackingReport.Group(PaymentTrackingReport.ByMember, Lines).Select(g => (g.Key, g.Paid)));
        Assert.Equal(["B1", "B2"], PaymentTrackingReport.Group(PaymentTrackingReport.ByBordx, Lines).Select(g => g.Key));
        Assert.Equal(["ZZ1-1", "ZZ2-1", "ZZ3-1"], PaymentTrackingReport.Group(PaymentTrackingReport.ByClaim, Lines).Select(g => g.Key));
        Assert.Empty(PaymentTrackingReport.Group(PaymentTrackingReport.General, Lines));
        Assert.Equal(PaymentTrackingReport.General, PaymentTrackingReport.Normalise("bogus"));
    }
}

public class PaymentTrackingTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/payments/tracking", Pv = "CFZZT0001", A = "ZZTRK00001", B = "ZZTRK00002", Bordx = "NZZTR00001";
    private HttpClient Client(int position = 58) => factory.ClientFor(WriteApiFactory.Access(position));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    /// <summary>One voucher paying hospital 00001 for two claims of one member (RM 1,000 and RM 300); claim B has no hospital amount (still listed).</summary>
    private async Task SeedAsync()
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                INSERT INTO MBM (MBMNumber, MBMCOVID, MBMStatus, MBMName, MBMIcBcPp, PAYCode, HLTCode, INSCode, PLNCode, MBMPolicyNo, MBMAvailableLimit, MBMTopupInd)
                VALUES ('ZZTRK0001*01', '00', 'A', 'TRACKING MEMBER', 'IC9', 'ZZ', 'N', 'F', 'ZZ01', 'POLTRK', 20000, 'N');
                INSERT INTO CAS (CASNumber, CASClaimAdmin, MBMNumber, MBMPatientCovID, CASHOSCode) VALUES (@A, 'N', 'ZZTRK0001*01', '00', '00001'), (@B, 'N', 'ZZTRK0001*01', '00', '00001');
                """, new { A, B });
        await using (var ws = await factory.Db.OpenAsync(GenesisDb.Worksheet))
            await ws.ExecuteAsync("""
                INSERT INTO Bordx (BordxRefNo, BordxDate, BordxPAYCode, BordxPAYToStatus, BordxPAYDOCFRStatus, BordxMNRBToStatus, BordxMNRBDOCFRStatus, BordxPrintedStatus, ACPeriodStatus, BordxSendtoACStatus)
                VALUES (@Bordx, '2030-04-20', 'ZZ', '0', '0', '0', '0', 0, 0, 0);
                INSERT INTO Claim (CASNumber, ClaimVersion, ClaimStatus, ClaimBordxStatus, CLMTokenStatus, CLMCaseMgmtFees, CLMPrevPerDisLmt, CLMPayableByMedix2H, CLMPayableByMedix2M, CLMBordxAmt, BordxRefNo, DebitCreditRefNo)
                VALUES (@A, 1, 'C', 1, 0, 0, 0, 1000, 0, 1000, @Bordx, 'DNZZ1'), (@B, 1, 'C', 1, 0, 0, 0, 0, 0, 300, @Bordx, 'DNZZ2');
                """, new { A, B, Bordx });
        await using var pay = await factory.Db.OpenAsync(GenesisDb.Payment);
        await pay.ExecuteAsync("""
            INSERT INTO Payment2HOS (PVHOSNo, CASNumber, ClaimVersion, PVHOSDate, PVPayHOSStatus, PVStatus, HOSPaidAmt, HOSRemark, HOSAccPeriodMth, HOSAccPeriodYr)
            VALUES (@Pv, @A, '1', '2030-05-01', 'F', 'F', 1000, 'paid', '05', '2030'), (@Pv, @B, '1', '2030-05-01', 'P', 'F', 300, null, '05', '2030');
            """, new { Pv, A, B });
    }

    private async Task<PaymentTrackingResult> SearchAsync(PaymentTrackingSearch s)
    {
        var res = await Client().PostAsJsonAsync($"{Url}/search", s);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<PaymentTrackingResult>())!;
    }

    [DatabaseFact]
    public async Task Lists_the_vouchers_with_their_member_hospital_and_bordereau()
    {
        await SeedAsync();
        var r = await SearchAsync(new PaymentTrackingSearch(PvNoFrom: Pv, PvNoTo: Pv));
        Assert.Equal((2, 1300m), (r.Count, r.Total));
        var a = r.Lines.Single(l => l.CaseNumber == A);
        Assert.Equal(("ZZTRK0001*01", "TRACKING MEMBER", Bordx, "DNZZ1", "ZZ", "N", 5, 2030, "paid"),
            (a.MemberNumber, a.MemberName, a.BordxNo, a.DnCn, a.Payor, a.HealthType, a.AccountMonth!.Value, a.AccountYear!.Value, a.Remark));
        Assert.NotNull(a.Hospital);

        Assert.Equal([A], (await SearchAsync(new PaymentTrackingSearch(PvNoFrom: Pv, PvNoTo: Pv, AmountFrom: 500))).Lines.Select(l => l.CaseNumber));
        Assert.Equal([B], (await SearchAsync(new PaymentTrackingSearch(DnCnFrom: "DNZZ2", DnCnTo: "DNZZ2"))).Lines.Select(l => l.CaseNumber));
        Assert.Equal(2, (await SearchAsync(new PaymentTrackingSearch(MemberName: "TRACKING MEMB"))).Count);
        Assert.Equal([B], (await SearchAsync(new PaymentTrackingSearch(MemberNo: "ZZTRK0001*01", Status: "P"))).Lines.Select(l => l.CaseNumber));
        Assert.Equal(2, (await SearchAsync(new PaymentTrackingSearch(BordxNoFrom: Bordx, BordxNoTo: Bordx))).Count);
    }

    [DatabaseFact]
    public async Task Grouped_reports_return_groups_and_the_grand_total()
    {
        await SeedAsync();
        var byPv = await SearchAsync(new PaymentTrackingSearch(PaymentTrackingReport.ByPv, ClaimNoFrom: A, ClaimNoTo: B));
        Assert.Empty(byPv.Lines);
        var g = Assert.Single(byPv.Groups);
        Assert.Equal((Pv, new DateOnly(2030, 5, 1), 2, 1300m), (g.Key, g.Date!.Value, g.Cases, g.Paid));
        Assert.Equal(("ZZTRK0001*01", 2), (await SearchAsync(new PaymentTrackingSearch(PaymentTrackingReport.ByMember, ClaimNoFrom: A, ClaimNoTo: B))).Groups.Select(x => (x.Key, x.Cases)).Single());
    }

    [DatabaseFact]
    public async Task A_search_must_be_narrowed_and_needs_the_screen()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync($"{Url}/search", new PaymentTrackingSearch(Status: "F"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(55).GetAsync($"{Url}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync($"{Url}/options")).StatusCode);
    }
}
