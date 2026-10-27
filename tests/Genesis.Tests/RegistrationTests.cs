using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Membership;

namespace Genesis.Tests;

public class RegistrationRuleTests
{
    private static RegistrationRequest Ok() => new("N", "WM", new(2023, 2, 1), 3, "N", null, null, "N", "NEW MEMBER", "MR", "IC-NEW", new(1980, 5, 6), "M", "01", "MY", "1 Road", null, null, "KL", "WP", "50000",
        "a@b.c", "0312345", "0123456789", null, "G", "ZZP1", "POL1", new(2023, 1, 1), new(2023, 12, 31), null, null, null, null, null, "A", null, null, null, null, null, null);

    [Fact]
    public void Valid_request_passes() => Assert.Null(RegistrationRules.Validate(Ok(), true));

    [Fact]
    public void Header_only_validation_checks_the_first_screen_fields()
    {
        Assert.Null(RegistrationRules.Validate(Ok() with { Name = null }, false));
        Assert.Equal("healthCode", RegistrationRules.Validate(Ok() with { HealthCode = "" }, false)!.Value.Field);
        Assert.Equal("payorCode", RegistrationRules.Validate(Ok() with { PayorCode = "W" }, false)!.Value.Field);
        Assert.Equal("bordxDate", RegistrationRules.Validate(Ok() with { BordxDate = null }, false)!.Value.Field);
        Assert.Equal("batchNo", RegistrationRules.Validate(Ok() with { BatchNo = null }, false)!.Value.Field);
    }

    [Fact]
    public void Full_validation_names_the_field()
    {
        Assert.Equal("renewal", RegistrationRules.Validate(Ok() with { Renewal = "X" }, true)!.Value.Field);
        Assert.Equal("previousMemberNo", RegistrationRules.Validate(Ok() with { Renewal = "R" }, true)!.Value.Field);
        Assert.Null(RegistrationRules.Validate(Ok() with { Renewal = "R", PreviousPolicyNo = "OLDPOL" }, true));
        Assert.Equal("name", RegistrationRules.Validate(Ok() with { Name = " " }, true)!.Value.Field);
        Assert.Equal("icBcPp", RegistrationRules.Validate(Ok() with { IcBcPp = "" }, true)!.Value.Field);
        Assert.Equal("sex", RegistrationRules.Validate(Ok() with { Sex = "Q" }, true)!.Value.Field);
        Assert.Equal("dateOfBirth", RegistrationRules.Validate(Ok() with { DateOfBirth = new(2999, 1, 1) }, true)!.Value.Field);
        Assert.Equal("address1", RegistrationRules.Validate(Ok() with { Address1 = "" }, true)!.Value.Field);
        Assert.Equal("postCode", RegistrationRules.Validate(Ok() with { PostCode = "1234567" }, true)!.Value.Field);
        Assert.Equal("plan", RegistrationRules.Validate(Ok() with { Plan = "" }, true)!.Value.Field);
        Assert.Equal("insurer", RegistrationRules.Validate(Ok() with { Insurer = "GG" }, true)!.Value.Field);
        Assert.Equal("payorExpiryDate", RegistrationRules.Validate(Ok() with { PayorExpiryDate = new(2022, 1, 1) }, true)!.Value.Field);
        Assert.Equal("installmentMode", RegistrationRules.Validate(Ok() with { InstallmentMode = "X" }, true)!.Value.Field);
    }
}

public class RegistrationTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/registration";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(1));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static RegistrationRequest Req(string renewal = "N", string insurer = "G", string plan = "ZZP1", string name = "ZZ REG TEST", string ic = "ZZIC1") =>
        new("N", "WM", new(2023, 2, 1), 3, renewal, null, renewal == "R" ? "OLDPOL" : null, "N", name, "MR", ic, new(1980, 5, 6), "M", "01", "MY", "1 Road", null, null, "KL", "WP", "50000",
            "a@b.c", "0312345", "0123456789", null, insurer, plan, "ZZPOL1", new(2023, 1, 1), new(2023, 12, 31), "GROUP", "E1", "AG1", "DEPT", "BR", "A", "01", "001", "M", "none", null, "note");

    private async Task Seed(string lifetime = "")
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, PLNRoundUpStatus, PLNProRate, PLNLifeTimeStatus) VALUES ('ZZP1', 'N', 'WM', '2020-01-01', 'N', 'Y', @lt)", new { lt = lifetime });
        await main.ExecuteAsync("INSERT INTO AGE (AGECode, AGEDescription, AGEFrom, AGETo, HLTCode, PLNCode) VALUES ('01', 'CHILD', '-100D', '17Y', 'N', 'ZZP1'), ('05', 'ADULT', '18Y', '99Y', 'N', 'ZZP1')");
        await main.ExecuteAsync("INSERT INTO PRM (INSCode, HLTCode, PAYCode, PRMAmount, PRMAmountFemale, AGECode, PLNCode, PRMEffDate) VALUES ('G', 'N', 'WM', 1000, 1000, '05', 'ZZP1', '2022-01-01')");
        await main.ExecuteAsync("INSERT INTO MCO (HLTCode, INSCode, PLNCode, MCOAmountAdult, MCOAmountChild, MCOEffDate) VALUES ('N', 'G', 'ZZP1', 30, 12, '2022-01-01')");
        await main.ExecuteAsync("INSERT INTO AnnualLimit (PLNCode, INSCode, HLTCode, AnnLimit, SuppLimit, SuppLimitStatus, AnnualEffDate, DisabilityLmt) VALUES ('ZZP1', 'G', 'N', 50000, 0, 'A', '2022-01-01', 0)");
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Requires_authentication_and_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}/options")).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Url, Req())).StatusCode);
    }

    [DatabaseFact]
    public async Task Options_list_health_plans_and_payors_and_last_batch_reads_bat()
    {
        var o = (await Client().GetFromJsonAsync<RegistrationOptions>($"{Url}/options"))!;
        Assert.Contains(o.HealthPlans, h => h.Code == "N");
        Assert.Contains(o.Payors, p => p.Code == "WM");
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("INSERT INTO BAT (PAYCode, BATBordxDate, BATSequence, BordxType) VALUES ('ZB', '2023-02-01', 7, 'N')");
        var last = await Client().GetFromJsonAsync<System.Text.Json.JsonElement>($"{Url}/last-batch?payor=ZB&bordxDate=2023-02-01");
        Assert.Equal(7, last.GetProperty("lastBatch").GetInt32());
        Assert.Equal(0, (await Client().GetFromJsonAsync<System.Text.Json.JsonElement>($"{Url}/last-batch?payor=ZB&bordxDate=2020-01-01")).GetProperty("lastBatch").GetInt32());
    }

    [DatabaseFact]
    public async Task Quote_finds_the_age_band_prices_the_plan_and_changes_nothing()
    {
        await Seed();
        var seqBefore = await Scalar<string>(GenesisDb.Maintenance, "SELECT SEQNumber FROM MBMSEQ05 WHERE PAYCode = 'WM'");
        var chkBefore = await Scalar<string>(GenesisDb.Main, "SELECT MBMChkDigits FROM MBMCheckDigits");
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req())).Content.ReadFromJsonAsync<RegistrationQuote>())!;
        Assert.Equal(("43Y", "05", "A", 1000m, 1000m, 30m, 30m, 50000m, "P"), (q.AgeText, q.AgeCode, q.AdultChild, q.BasicPremium, q.Premium, q.BasicMco, q.McoFee, q.AnnualLimit, q.MemberType));
        Assert.Equal(seqBefore, await Scalar<string>(GenesisDb.Maintenance, "SELECT SEQNumber FROM MBMSEQ05 WHERE PAYCode = 'WM'"));
        Assert.Equal(chkBefore, await Scalar<string>(GenesisDb.Main, "SELECT MBMChkDigits FROM MBMCheckDigits"));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Registering_a_new_member_generates_the_number_and_writes_every_table()
    {
        await Seed();
        var seqBefore = (await Scalar<string>(GenesisDb.Maintenance, "SELECT SEQNumber FROM MBMSEQ05 WHERE PAYCode = 'WM'"))!.Trim();
        var chkBefore = int.Parse((await Scalar<string>(GenesisDb.Main, "SELECT MBMChkDigits FROM MBMCheckDigits"))!);
        var res = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var r = (await res.Content.ReadFromJsonAsync<RegistrationResult>())!;
        Assert.Matches(new Regex(@"^WMG[A-Z]\d{5}\d{2}\*01$"), r.MemberNumber);
        Assert.True(r.CrossReferenceSaved);

        var seqAfter = (await Scalar<string>(GenesisDb.Maintenance, "SELECT SEQNumber FROM MBMSEQ05 WHERE PAYCode = 'WM'"))!.Trim();
        var expectedSeq = seqBefore[1..] == "99999" ? $"{(char)(seqBefore[0] + 1)}00001" : $"{seqBefore[0]}{int.Parse(seqBefore[1..]) + 1:00000}";
        Assert.Equal(expectedSeq, seqAfter);
        var chkAfter = (await Scalar<string>(GenesisDb.Main, "SELECT MBMChkDigits FROM MBMCheckDigits"))!;
        Assert.Equal((chkBefore == 99 ? 1 : chkBefore + 1).ToString("00"), chkAfter);
        Assert.Equal($"WMG{seqAfter}{chkAfter}*01", r.MemberNumber);

        var n = new { n = r.MemberNumber };
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMStatus = 'A' AND MBMDataStatus = 'A' AND MBMMemberType = 'P' AND PLNCode = 'ZZP1' AND AgeCode = '05' AND MBMAdultChild = 'A' AND MBMBatchNo = 3 AND MBMBordxDate = '2023-02-01' AND MBMAvailableLimit = 50000 AND HSClnInd = 'H' AND MBMCOVID = '00' AND MBMRenewFlag = 'N' AND MBMName = 'ZZ REG TEST'", n));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @n AND MBMRegUser = 'TEST' AND MBMUploadStatus = 'M' AND MBMEmail = 'a@b.c' AND NATCode = 'MY'", n));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMOthers WHERE MBMNumber = @n AND MBMPremium = 1000 AND MBMMCOFee = 30 AND MBMBasicPrem = 1000 AND MBMBasicMCO = 30 AND RELCODE = 'P' AND MBMAgentCode = 'AG1'", n));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMOthersTwo WHERE MBMNumber = @n AND MBMOccupation = '01' AND MBMRemarks = 'note'", n));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM BAT WHERE PAYCode = 'WM' AND BATBordxDate = '2023-02-01' AND BordxType = 'N' AND BATSequence = 3"));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM MBMCrossReference WHERE MBMNumber = @n AND MBMStatus = 'A' AND PLNCode = 'ZZP1' AND MBMBatchNo = 3", n));
        Assert.Equal("Member.Register", Assert.Single(factory.Audit.Entries).Action);
    }

    [DatabaseFact]
    public async Task A_renewal_of_the_same_person_keeps_the_sequence_and_bumps_the_renewal_id_and_a_new_registration_gets_a_fresh_sequence()
    {
        await Seed();
        var first = (await (await Client().PostAsJsonAsync(Url, Req())).Content.ReadFromJsonAsync<RegistrationResult>())!.MemberNumber;
        var renewed = (await (await Client().PostAsJsonAsync(Url, Req("R"))).Content.ReadFromJsonAsync<RegistrationResult>())!.MemberNumber;
        Assert.Equal(first[..^3], renewed[..^3]);
        Assert.EndsWith("*02", renewed);

        var again = (await (await Client().PostAsJsonAsync(Url, Req("N"))).Content.ReadFromJsonAsync<RegistrationResult>())!.MemberNumber;
        Assert.NotEqual(first[..^3], again[..^3]);
        Assert.EndsWith("*01", again);
        var key = "WM*G*19800506*ZZ REG TEST";
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM MBMSEQRenewHistory WHERE MBMKey = @key AND SEQNo LIKE '%*02'", new { key }));
        Assert.Equal(again[3..^3], await Scalar<string>(GenesisDb.Maintenance, "SELECT RewSEQNo FROM MBMSEQRenew05 WHERE RewMBMSEQKey = @key", new { key }));
    }

    [DatabaseFact]
    public async Task A_renewal_with_no_earlier_registration_gets_a_new_sequence_and_the_check_digits_wrap_after_99()
    {
        await Seed();
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main)) await main.ExecuteAsync("UPDATE MBMCheckDigits SET MBMChkDigits = '99'");
        var number = (await (await Client().PostAsJsonAsync(Url, Req("R", name: "ZZ RENEW FIRST"))).Content.ReadFromJsonAsync<RegistrationResult>())!.MemberNumber;
        Assert.Equal("01", number.Substring(number.Length - 5, 2));
        Assert.EndsWith("*01", number);
        Assert.Equal("01", await Scalar<string>(GenesisDb.Main, "SELECT MBMChkDigits FROM MBMCheckDigits"));
    }

    [DatabaseFact]
    public async Task Duplicate_ic_is_reported_as_a_warning_and_does_not_block()
    {
        await Seed();
        await using (var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance))
            await maint.ExecuteAsync("INSERT INTO MBMCrossReference (MBMNumber, MBMIcBcPp, MBMStatus) VALUES ('ZZDUP0001*01', 'ZZICDUP', 'A')");
        var q = (await (await Client().PostAsJsonAsync($"{Url}/quote", Req(ic: "ZZICDUP"))).Content.ReadFromJsonAsync<RegistrationQuote>())!;
        Assert.Equal("ZZDUP0001*01", q.DuplicateMemberNo);
        Assert.Equal(HttpStatusCode.Created, (await Client().PostAsJsonAsync(Url, Req(ic: "ZZICDUP"))).StatusCode);
    }

    [DatabaseFact]
    public async Task Unsupported_or_invalid_registrations_are_refused_without_consuming_numbers()
    {
        await Seed();
        var chkBefore = await Scalar<string>(GenesisDb.Main, "SELECT MBMChkDigits FROM MBMCheckDigits");
        var f = await Client().PostAsJsonAsync(Url, Req(insurer: "F"));
        Assert.Equal(HttpStatusCode.BadRequest, f.StatusCode);
        Assert.Contains("supplementary", await f.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync(Url, Req(plan: "NOPE"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req() with { Name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, Req() with { DateOfBirth = new(1890, 1, 1) })).StatusCode);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main)) await main.ExecuteAsync("UPDATE PLN SET PLNLifeTimeStatus = 'Y' WHERE PLNCode = 'ZZP1'");
        var life = await Client().PostAsJsonAsync(Url, Req());
        Assert.Equal(HttpStatusCode.BadRequest, life.StatusCode);
        Assert.Contains("lifetime", await life.Content.ReadAsStringAsync());
        Assert.Equal(chkBefore, await Scalar<string>(GenesisDb.Main, "SELECT MBMChkDigits FROM MBMCheckDigits"));
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMName = 'ZZ REG TEST'"));
        Assert.Empty(factory.Audit.Entries);
    }
}

