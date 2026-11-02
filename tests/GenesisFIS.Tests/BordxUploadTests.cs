using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

/// <summary>Sample lines of the six FWHS files, field by field as the legacy form read them. Payor "ZZ" and "ZZ..." keys keep clear of real data.</summary>
internal static class BordxSamples
{
    public static string Mp(string refNo = "ZZREF1", string name = "ZZ EMPLOYER", string address = "1 JALAN ZZ") => string.Join("|",
        "T1", "ZZ", refNo, "ZZPOL1", "20240101", "20240101", "20241231", "C", "EMP'1", name, address, "", "", "", "KL", "50000", "14", "01", "AG1",
        "0311", "", "0122", "a@b.c", "20000", "2", "100.50", "20", "N", "6", "1.20", "0", "20240101", "");

    public static string Mw(string fw = "ZZFW1", string name = "ZZ WORKER ONE", string renewal = "N", string cancelled = "N", string dob = "19900215") => string.Join("|",
        "T1", "ZZ", "ZZREF1", fw, "ZZPOL1", "20240101", "IDN", "ZZP-" + fw, "D1", name, "M", dob, "20000", "50", "10", renewal, cancelled, "P", "WP1", "", "20240101", "");

    public static string Np(string refNo = "ZZREF1") => string.Join("|",
        "T1", "ZZ", refNo, "ZZPOL1", "20240101", "20240101", "20241231", "C", "EMP1", "ZZ EMPLOYER", "1 JALAN ZZ", "", "", "", "KL", "50000", "14", "01", "AG1",
        "0311", "", "", "", "20000", "2", "100.50", "20", "6", "1.20", "0", "ZZB1", "20240105", "");

    public static string Ep() => string.Join("|",
        "T1", "ZZ", "ZZREF1", "ZZEND1", "20240201", "20240201", "ZZPOL1", "20240101", "20241231", "C", "EMP1", "ZZ EMPLOYER", "1 JALAN ZZ", "", "", "", "KL", "50000",
        "14", "01", "0311", "", "", "", "3", "", "5", "0.30", "", "", "ZZB2", "20240205", "20240205");

    public static string Ex() => string.Join("|", "T1", "ZZ", "ZZREF1", "ZZEND2", "20240301", "20240301", "ZZPOL1", "20240101", "-40", "-8", "-0.48", "0", "ZZB3", "20240305", "20240305");

    public static string Ew(string fw = "ZZFW1", string type = "D") => string.Join("|",
        "T1", "ZZ", "ZZREF1", fw, "ZZEND3", "20240401", "ZZPOL1", "20240401", type, "IDN", "ZZP-" + fw, "D1", "ZZ WORKER ONE", "M", "19900215", "",
        "-25", "-5", "", "P", "WP1", "", "ZZB4", "20240405", "20240405");

    public static BordxUploadFile File(string kind, params string[] lines) => new($"ETZZZ{kind}900001.TXT", string.Join("\r\n", lines) + "\r\n");
}

public class BordxParserTests
{
    private static (BordxParsedFile File, List<BordxIssue> Issues) Parse(string kind, params string[] lines)
    {
        var issues = new List<BordxIssue>();
        return (BordxParser.Parse(kind, $"ETZZZ{kind}1.TXT", string.Join("\n", lines), issues), issues);
    }

    [Fact]
    public void The_kind_is_the_two_letters_at_position_six()
    {
        Assert.Equal("MP", BordxKind.Of("ETFWMMP002812.TXT"));
        Assert.Equal("EW", BordxKind.Of("etfwmew002812.txt"));
        Assert.Null(BordxKind.Of("ETFWMXX002812.TXT"));
        Assert.Null(BordxKind.Of("MP.TXT"));
    }

    [Fact]
    public void File_checks_need_a_known_kind_once_and_content()
    {
        Assert.Equal("Please Insert File", Assert.Single(BordxUploadRules.CheckFiles(new BordxUploadRequest([]))).Message);
        Assert.Contains(BordxUploadRules.CheckFiles(new BordxUploadRequest([new("WRONG.TXT", "x")])), i => i.Message == "Wrong File Name-WRONG.TXT" && i.Blocking);
        Assert.Contains(BordxUploadRules.CheckFiles(new BordxUploadRequest([BordxSamples.File("MP", "x"), BordxSamples.File("MP", "y")])), i => i.Message.StartsWith("Only one MP"));
        Assert.Contains(BordxUploadRules.CheckFiles(new BordxUploadRequest([new("ETZZZNP1.TXT", " ")])), i => i.Message == "The file is empty");
        Assert.Empty(BordxUploadRules.CheckFiles(new BordxUploadRequest([BordxSamples.File("MP", BordxSamples.Mp()), BordxSamples.File("EW", BordxSamples.Ew())])));
    }

    [Fact]
    public void Lines_are_read_field_by_field_with_legacy_conversions()
    {
        var (f, issues) = Parse("MP", BordxSamples.Mp(), "", BordxSamples.Mp("ZZREF2"));
        Assert.Empty(issues);
        Assert.Equal(2, f.Policies.Count);
        var p = f.Policies[0];
        Assert.Equal((1, "ZZ", "ZZREF1", "EMP1", 100.50m, 2), (p.Line, p.PayorCode, p.InsurerRefNo, p.EmployerCode, p.Premium, p.Workers));
        Assert.Equal(new DateTime(2024, 12, 31), p.ExpDate);
        Assert.Equal(BordxParser.BlankDate, p.Updated);
        Assert.Equal(3, f.Policies[1].Line);

        var w = Parse("MW", BordxSamples.Mw(renewal: "Y", cancelled: "Y")).File.Workers[0];
        Assert.Equal(("R", "C", new DateTime(1990, 2, 15)), (w.Renewal, w.Status, w.Dob));
        Assert.Equal(("N", "A"), (Parse("MW", BordxSamples.Mw()).File.Workers[0].Renewal, Parse("MW", BordxSamples.Mw()).File.Workers[0].Status));
    }

    [Fact]
    public void Endorsement_amounts_left_blank_are_zero()
    {
        var (f, issues) = Parse("EP", BordxSamples.Ep());
        Assert.Empty(issues);
        Assert.Equal((0m, 5m, 0m), (f.Policies[0].Premium, f.Policies[0].McoFee, f.Policies[0].TpcaTax));
    }

    [Fact]
    public void Short_lines_and_bad_values_block_the_upload()
    {
        var (_, issues) = Parse("EX", "T1|ZZ|ZZREF1");
        Assert.True(Assert.Single(issues).Blocking);

        var bad = BordxSamples.Mw(dob: "1990-2-15").Split('|'); bad[13] = "abc";
        (_, issues) = Parse("MW", string.Join("|", bad));
        Assert.All(issues, i => Assert.True(i.Blocking));
        Assert.Contains(issues, i => i.Message.StartsWith("Foreign Worker's DOB") && i.Line == 1 && i.PolicyNo == "ZZPOL1");
        Assert.Contains(issues, i => i.Message.StartsWith("Premium 'abc'"));
    }

    [Fact]
    public void The_legacy_verify_messages_are_listed_but_do_not_block()
    {
        var (_, issues) = Parse("MP", BordxSamples.Mp(name: "", address: ""));
        Assert.Equal(["Employer Name Is Blank", "Address Is Blank"], issues.Select(i => i.Message));
        Assert.All(issues, i => Assert.False(i.Blocking));
    }
}

public class BordxUploadTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/bordx-upload";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(8));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private async Task<T?> Scalar<T>(string sql, object? args = null, GenesisDb db = GenesisDb.Main)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    private static BordxUploadRequest All() => new([
        BordxSamples.File("MP", BordxSamples.Mp(), BordxSamples.Mp("ZZREF2")), BordxSamples.File("MW", BordxSamples.Mw(), BordxSamples.Mw("ZZFW2", "ZZ WORKER TWO")),
        BordxSamples.File("NP", BordxSamples.Np()), BordxSamples.File("EP", BordxSamples.Ep()), BordxSamples.File("EX", BordxSamples.Ex()), BordxSamples.File("EW", BordxSamples.Ew())]);

    [Fact]
    public async Task Needs_the_upload_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"{Url}/preview", All())).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(1));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{Url}/preview", All())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Url, All())).StatusCode);
    }

    [DatabaseFact]
    public async Task Preview_counts_inserts_and_updates_and_writes_nothing()
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("INSERT INTO GroupCompanyFW (CoGRPPaycode, CoInsurerRefNo, CoPolicyNo, CoGRPName) VALUES ('ZZ', 'ZZREF2', 'ZZOLD', 'OLD')");

        var p = (await (await Client().PostAsJsonAsync($"{Url}/preview", All())).Content.ReadFromJsonAsync<BordxUploadPreview>())!;
        Assert.True(p.CanUpload, string.Join("; ", p.Issues.Select(i => i.Message)));
        Assert.Empty(p.Issues);
        Assert.Equal(["MP", "MW", "NP", "EP", "EX", "EW"], p.Files.Select(f => f.Kind));
        Assert.Equal((2, 1, 1), (p.Files[0].Records, p.Files[0].Inserts, p.Files[0].Updates));
        Assert.Equal((2, 0), (p.Files[1].Inserts, p.Files[1].Updates));
        Assert.Equal((1, 1, 0), (p.Files[5].Inserts, p.Files[5].Updates, p.Files[5].Skipped));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM FWHSTPAFee WHERE CoInsurerRefNo = 'ZZREF1'"));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM MBM WHERE PAYCode = 'ZZ' AND MBMFWUniqueNo LIKE 'ZZFW%'"));
    }

    [DatabaseFact]
    public async Task Upload_writes_all_six_files_through_the_legacy_procedures()
    {
        var res = await Client().PostAsJsonAsync(Url, All());
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var r = (await res.Content.ReadFromJsonAsync<BordxUploadResult>())!;
        Assert.Equal(2, r.NewMemberNumbers.Count);
        Assert.All(r.NewMemberNumbers, n => Assert.Matches(@"^ZZG[A-Z]\d{7}\*01$", n));

        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM GroupCompanyFW WHERE CoGRPPaycode = 'ZZ' AND CoGRPLastUpdateUser = 'TEST' AND CoSPPAFilename = 'ETZZZMP900001.TXT' AND CoEmployerCd = 'EMP1' AND CoTotalPremium = 100.50"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM FWHSTPAFee WHERE CoSPPAFilename = 'ETZZZNP900001.TXT' AND CoPAYBatchNo = 'ZZB1' AND CoPAYDateSubmission = '2024-01-05' AND CoESPRecUpdateDate = '1900-01-01'"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM FWHSTPAFee WHERE CoSPPAFilename = 'ETZZZEP900001.TXT' AND CoEndorseNo = 'ZZEND1' AND CoTotalPremium = 0 AND CoTotalMCOFee = 5"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM FWHSTPAFee WHERE CoSPPAFilename = 'ETZZZEX900001.TXT' AND CoEndorseNo = 'ZZEND2' AND CoTotalPremium = -40"));

        var first = r.NewMemberNumbers[0];
        Assert.Equal(1, await Scalar<int>("""
            SELECT COUNT(*) FROM MBM WHERE MBMNumber = @first AND PAYCode = 'ZZ' AND HLTCode = 'N' AND MBMCOVID = '00' AND MBMIcBcPp = 'ZZP-ZZFW1' AND PLNCode = 'FH01' AND INSCode = 'G'
              AND MBMPayorEffDate = '2024-01-01' AND MBMPayorEXPDate = '2024-01-01' AND MBMRenewFlag = 'N' AND MBMBatchNo = 1 AND MBMAvailableLimit = 20000
              AND MBMInsurerUniqueNo = 'ZZREF1' AND MBMFWUniqueNo = 'ZZFW1' AND MBMWorkPermitExpiry = '1900-01-01'
            """, new { first }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBMOthers WHERE MBMNumber = @first AND MBMPremium = 50 AND MBMBasicMCO = 10 AND RELCODE = 'P'", new { first }));

        // The EW deletion of the worker the MW file just added: the member is cancelled and the endorsement row written.
        Assert.Equal("C", await Scalar<string>("SELECT MBMStatus FROM MBM WHERE MBMNumber = @first", new { first }));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM FWHSEndorsementMBM WHERE MBMNumber = @first AND MBMEndorsementNo = 'ZZEND3' AND MBMEndorseType = 'D' AND MBMPremium = -25", new { first }));
        Assert.Equal("A", await Scalar<string>("SELECT MBMStatus FROM MBM WHERE MBMNumber = @n", new { n = r.NewMemberNumbers[1] }));
        Assert.Single(factory.Audit.Entries, e => e.Action == "Membership.UploadBordx");
    }

    [DatabaseFact]
    public async Task An_existing_worker_is_updated_and_keeps_its_number()
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("""
                INSERT INTO MBM (PAYCode, HLTCode, MBMNumber, MBMPolicyNo, MBMCOVID, MBMIcBcPp, MBMName, MBMStatus, MBMInsurerUniqueNo, MBMFWUniqueNo)
                VALUES ('ZZ', 'N', 'ZZGZ0000001*01', 'OLDPOL', '00', 'OLD', 'OLD NAME', 'C', 'ZZREF1', 'ZZFW1')
                """);
        var res = await Client().PostAsJsonAsync(Url, new BordxUploadRequest([BordxSamples.File("MW", BordxSamples.Mw(renewal: "Y"))]));
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Assert.Empty((await res.Content.ReadFromJsonAsync<BordxUploadResult>())!.NewMemberNumbers);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBM WHERE MBMNumber = 'ZZGZ0000001*01' AND MBMName = 'ZZ WORKER ONE' AND MBMPolicyNo = 'ZZPOL1' AND MBMStatus = 'A' AND MBMRenewFlag = 'R'"));
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM MBM WHERE PAYCode = 'ZZ' AND MBMFWUniqueNo = 'ZZFW1'"));
    }

    [DatabaseFact]
    public async Task A_file_already_uploaded_or_a_bad_line_blocks_the_upload()
    {
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("INSERT INTO FWHSTPAFee (CoInsurerRefNo, CoGRPPaycode, CoSPPAFilename) VALUES ('ZZOLD', 'ZZ', 'ETZZZNP900001.TXT')");
        var request = new BordxUploadRequest([BordxSamples.File("NP", BordxSamples.Np())]);
        var p = (await (await Client().PostAsJsonAsync($"{Url}/preview", request)).Content.ReadFromJsonAsync<BordxUploadPreview>())!;
        Assert.False(p.CanUpload);
        Assert.Contains(p.Issues, i => i.Message.StartsWith("Duplicate record(s) found!") && i.Blocking);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync(Url, request)).StatusCode);
        Assert.Equal(1, await Scalar<int>("SELECT COUNT(*) FROM FWHSTPAFee WHERE CoSPPAFilename = 'ETZZZNP900001.TXT'"));

        var res = await Client().PostAsJsonAsync(Url, new BordxUploadRequest([BordxSamples.File("MP", BordxSamples.Mp(), "T1|ZZ|ZZREF9")]));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("line 2", await res.Content.ReadAsStringAsync());
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM GroupCompanyFW WHERE CoGRPPaycode = 'ZZ' AND CoInsurerRefNo = 'ZZREF1'"));
    }

    [DatabaseFact]
    public async Task An_endorsement_for_an_unknown_worker_is_listed_and_skipped()
    {
        var request = new BordxUploadRequest([BordxSamples.File("EW", BordxSamples.Ew("ZZNOBODY"))]);
        var p = (await (await Client().PostAsJsonAsync($"{Url}/preview", request)).Content.ReadFromJsonAsync<BordxUploadPreview>())!;
        Assert.True(p.CanUpload, string.Join("; ", p.Issues.Select(i => i.Message)));
        Assert.False(Assert.Single(p.Issues).Blocking);
        Assert.Equal(1, p.Files[0].Skipped);
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync(Url, request)).StatusCode);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM FWHSEndorsementMBM WHERE MBMEndorsementNo = 'ZZEND3'"));
    }
}
