using System.Net;
using System.Net.Http.Json;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Fees;
using Genesis.Core.Membership;

namespace Genesis.Tests;

public class RefundMathTests
{
    [Fact]
    public void Premium_reduction_matches_a_real_2009_refund_row()
    {
        // MBMRefund of XWGA1376281*01: effective 2009-12-15, expiry 2010-12-14, premium 60 -> reduction 59.84, effective 0.16
        var r = RefundMath.Compute(new(2010, 12, 14), new(2009, 12, 15), new(2009, 12, 15), 60m, 9m, roundUp: false);
        Assert.Equal((0.16m, 59.84m), (r.PremiumEffective, r.PremiumReduction));
    }

    [Fact]
    public void Mco_refund_counts_the_cancel_date_as_covered_in_the_current_source()
    {
        // 2009 rows show 8.98 (older formula, one day shorter); the current VB6 source gives the full 9.00 here.
        Assert.Equal(9m, RefundMath.Compute(new(2010, 12, 14), new(2009, 12, 15), new(2009, 12, 15), 60m, 9m, false).McoRefund);
    }

    [Fact]
    public void Mid_period_cancellation_pro_rates_both_refunds()
    {
        var r = RefundMath.Compute(new(2023, 12, 31), new(2023, 7, 1), new(2023, 1, 1), 1200m, 30m, roundUp: false);
        Assert.Equal((598.36m, 601.64m, 14.88m, 15.12m), (r.PremiumEffective, r.PremiumReduction, r.McoEffective, r.McoRefund));
    }

    [Fact]
    public void Round_up_plans_round_every_result_to_a_whole_number_and_inputs_use_CInt()
    {
        var r = RefundMath.Compute(new(2023, 12, 31), new(2023, 7, 1), new(2023, 1, 1), 1200.4m, 30.4m, roundUp: true);
        Assert.Equal((598m, 602m, 15m, 15m), (r.PremiumEffective, r.PremiumReduction, r.McoEffective, r.McoRefund));
    }

    [Fact]
    public void Cancelling_on_or_after_expiry_refunds_no_premium_and_never_a_negative_mco()
    {
        var r = RefundMath.Compute(new(2023, 12, 31), new(2023, 12, 31), new(2023, 1, 1), 1200m, 30m, false);
        Assert.Equal((0m, 1200m), (r.PremiumReduction, r.PremiumEffective));
        Assert.Equal(0.08m, r.McoRefund);
        Assert.Equal(0m, RefundMath.McoRefund(new(2023, 12, 31), new(2024, 3, 1), new(2023, 1, 1), 30m));
    }
}

public class CancellationTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/adjustment/cancellation";
    private const string Member = "ZZCAN0001*01";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(2));
    private static CancellationRequest Req(string m = Member, DateOnly? cancel = null) => new(m, cancel ?? new DateOnly(2023, 7, 1), new DateOnly(2023, 8, 1), 6);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task Seed(string status = "A", string insurer = "G", string roundUp = "N", bool covered = false, string? suppMcoStatus = null)
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, PLNRoundUpStatus, PLNProRate, PLNPremGenderStatus) VALUES ('ZZPL', 'N', 'ZZ', '2020-01-01', @r, 'Y', 'N')", new { r = roundUp });
        if (suppMcoStatus is not null)
            await main.ExecuteAsync("INSERT INTO MCO (HLTCode, INSCode, PLNCode, MCOAmountAdult, MCOAmountChild, MCOEffDate, SuppMCOStatus) VALUES ('N', @ins, 'ZZPL', 30, 12, '2022-01-01', @s)", new { ins = insurer, s = suppMcoStatus });
        await main.ExecuteAsync("""
            INSERT INTO MBM (MBMNumber, MBMStatus, MBMExpiredStatus, MBMName, MBMIcBcPp, MBMPolicyNo, HLTCode, PAYCode, INSCode, PLNCode, MBMPayorEffDate, MBMPayorExpDate, MBMBordxDate)
            VALUES (@n, @status, 'N', 'CANCEL TEST', 'IC1', 'POL1', 'N', 'ZZ', @ins, 'ZZPL', '2023-01-01', '2023-12-31', '2023-02-02')
            """, new { n = Member, status, ins = insurer });
        await main.ExecuteAsync("INSERT INTO MBMOthers (MBMNumber, MBMPremium, MBMMCOFee) VALUES (@n, 1200, 30)", new { n = Member });
        await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@n)", new { n = Member });
        if (covered) await main.ExecuteAsync("INSERT INTO MBMCoveredPersons (MBMCNumber, MBMCCoverID, MBMCName, MBMCStatus) VALUES (@n, '01', 'SPOUSE', 'A')", new { n = Member });
        await using var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        await maint.ExecuteAsync("INSERT INTO MBMCrossReference (MBMNumber, MBMStatus) VALUES (@n, @status)", new { n = Member, status });
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Requires_authentication_and_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"{Url}/quote", Req())).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{Url}/quote", Req())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Url, Req())).StatusCode);
    }

    [DatabaseFact]
    public async Task Quote_shows_the_refund_without_changing_anything()
    {
        await Seed();
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req())).Content.ReadFromJsonAsync<CancellationQuote>())!;
        Assert.Equal((1200m, 30m, 601.64m, 15.12m), (q.Premium, q.McoFee, q.Refund.PremiumReduction, q.Refund.McoRefund));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMStatus = 'A'", new { n = Member }));
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Cancelling_writes_status_history_cancel_date_covered_persons_refund_and_cross_reference()
    {
        await Seed(covered: true);
        var res = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await res.Content.ReadFromJsonAsync<CancellationResult>())!.CrossReferenceUpdated);

        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMStatus = 'C' AND MBMFinalExpiryDate = '2023-07-01'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @n AND MBMLastEndorseStatus = 'C' AND MBMCancelDate = '2023-07-01' AND MBMENDtEffDate = '2023-07-01' AND MBMENDtBordxDate = '2023-08-01'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n AND MBMAmendEdtType = 'C' AND MBMAEndtBatchNo = 6 AND MBMAmendUser = 'TEST' AND MBMAEndtEffDate = '2023-07-01' AND MBMExpiryDate = '2023-12-31'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMCoveredPersons WHERE MBMCNumber = @n AND MBMCStatus = 'C' AND MBMCCancelDate = '2023-07-01'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n AND MBMCovID = '00' AND MBMPremReduction = 601.64 AND MBMPremEffective = 598.36 AND MBMMCORefund = 15.12 AND MBMMCOEffective = 14.88 AND PAYCode = 'ZZ' AND MBMBordxDate = '2023-02-02' AND MBMBatchNo = 6 AND HLTCode = 'N'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM MBMCrossReference WHERE MBMNumber = @n AND MBMStatus = 'C' AND MBMFinalExpiryDate = '2023-07-01'", new { n = Member }));
        Assert.Equal("Member.Cancel", Assert.Single(factory.Audit.Entries).Action);

        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync(Url, Req())).StatusCode);
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n", new { n = Member }));
    }

    [DatabaseFact]
    public async Task Round_up_plans_store_whole_number_refunds()
    {
        await Seed(roundUp: "Y");
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync(Url, Req())).StatusCode);
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n AND MBMPremReduction = 602 AND MBMMCORefund = 15", new { n = Member }));
    }

    [DatabaseFact]
    public async Task Dates_status_and_unsupported_supplementary_rules_are_refused_without_changes()
    {
        await Seed(insurer: "F", suppMcoStatus: "C");
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req(cancel: new(2022, 12, 31)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req(cancel: new(2024, 1, 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req("ZZNONE*01"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req() with { BatchNo = null })).StatusCode);
        var unsupported = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Contains("not migrated", await unsupported.Content.ReadAsStringAsync());
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMStatus = 'A'", new { n = Member }));
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMRefund WHERE MBMNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }
}
