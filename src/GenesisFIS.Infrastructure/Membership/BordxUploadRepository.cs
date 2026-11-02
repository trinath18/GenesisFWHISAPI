using System.Xml;
using System.Xml.Linq;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

/// <summary>Membership > Upload Bordx (FrmNewMBMEndt2012): the FWHS master and MCO billing files, written through the same procedures as the legacy form.</summary>
internal sealed class BordxUploadRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IBordxUploadRepository
{
    private static DateTime D(DateTime? d) => d ?? BordxParser.BlankDate;

    /// <summary>What is already in the database for the keys of the files; read once per file instead of once per line (MBM has no index on the worker keys).</summary>
    private sealed class Lookups
    {
        public HashSet<(string, string)> Employers { get; } = [];
        public Dictionary<(string, string, string), string> Workers { get; } = [];
        public Dictionary<(string, string, string), List<string>> EndorsedWorkers { get; } = [];
        public HashSet<string> Endorsed { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Analysis(List<BordxParsedFile> Files, List<BordxIssue> Issues, List<BordxFileSummary> Summaries, Lookups Lookups)
    {
        public bool CanUpload => Files.Count > 0 && !Issues.Any(i => i.Blocking);
    }

    private static string Clean(string s) => new(s.Where(XmlConvert.IsXmlChar).ToArray());

    private static string Keys(IEnumerable<string[]> keys) =>
        new XElement("r", keys.Distinct(new KeyComparer()).Select(k => new XElement("k", k.Select((v, i) => new XAttribute("a" + i, Clean(v)))))).ToString(SaveOptions.DisableFormatting);

    private sealed class KeyComparer : IEqualityComparer<string[]>
    {
        public bool Equals(string[]? x, string[]? y) => x!.SequenceEqual(y!);
        public int GetHashCode(string[] k) => string.Join("|", k).GetHashCode();
    }

    /// <summary>MBM rows for (payor, FW unique no, insurer unique no) keys: one hash join instead of one scan of MBM per line.</summary>
    private static async Task<IReadOnlyList<IDictionary<string, object?>>> MembersAsync(IDbSession main, IEnumerable<BordxWorkerRow> rows, CancellationToken ct) =>
        await main.QueryAsync("""
            DECLARE @x xml = @keys;
            DECLARE @k TABLE (p nvarchar(5), f nvarchar(50), i nvarchar(50));
            INSERT @k SELECT DISTINCT n.value('@a0', 'nvarchar(5)'), n.value('@a1', 'nvarchar(50)'), n.value('@a2', 'nvarchar(50)') FROM @x.nodes('/r/k') t(n);
            SELECT m.MBMNumber, m.PAYCode, m.MBMFWUniqueNo, m.MBMInsurerUniqueNo
            FROM MBM m INNER HASH JOIN @k k ON m.PAYCode = k.p AND m.MBMFWUniqueNo = k.f AND m.MBMInsurerUniqueNo = k.i
            ORDER BY m.MBMNumber
            """, new { keys = Keys(rows.Select(r => new[] { r.PayorCode, r.FwUniqueNo, r.InsurerUniqueNo })) }, ct);

    private static (string, string, string) WorkerKey(BordxWorkerRow r) => (r.PayorCode.ToUpperInvariant(), r.FwUniqueNo.ToUpperInvariant(), r.InsurerUniqueNo.ToUpperInvariant());
    private static (string, string, string) WorkerKey(IDictionary<string, object?> r) => ((S(r, "PAYCode") ?? "").ToUpperInvariant(), (S(r, "MBMFWUniqueNo") ?? "").ToUpperInvariant(), (S(r, "MBMInsurerUniqueNo") ?? "").ToUpperInvariant());

    /// <summary>MBM rows of the EW workers (all of them: a deletion cancels each) and which of their numbers already have an endorsement row.</summary>
    private static async Task LoadEndorsedWorkersAsync(IDbSession main, BordxParsedFile ew, Lookups look, CancellationToken ct)
    {
        look.EndorsedWorkers.Clear(); look.Endorsed.Clear();
        foreach (var r in await MembersAsync(main, ew.Workers, ct))
        {
            var key = WorkerKey(r);
            if (!look.EndorsedWorkers.TryGetValue(key, out var list)) look.EndorsedWorkers[key] = list = [];
            list.Add(S(r, "MBMNumber") ?? "");
        }
        var numbers = look.EndorsedWorkers.Values.Select(l => l[^1]).Distinct().ToList();
        foreach (var chunk in numbers.Chunk(2000))
            foreach (var r in await main.QueryAsync("SELECT MBMNumber FROM FWHSEndorsementMBM WHERE MBMNumber IN @chunk", new { chunk }, ct))
                look.Endorsed.Add(S(r, "MBMNumber") ?? "");
    }

    private static async Task<Analysis> AnalyseAsync(IDbSession main, BordxUploadRequest request, CancellationToken ct)
    {
        var issues = BordxUploadRules.CheckFiles(request);
        var files = new List<BordxParsedFile>();
        if (issues.Any(i => i.Blocking)) return new Analysis(files, issues, [], new Lookups());

        foreach (var f in (request.Files ?? []).OrderBy(f => BordxKind.All.ToList().IndexOf(BordxKind.Of(f.Name!.Trim())!)))
            files.Add(BordxParser.Parse(BordxKind.Of(f.Name!.Trim())!, f.Name!.Trim(), f.Content!, issues));

        // Legacy Verify: a file name already in FWHSTPAFee means the files were uploaded before.
        var names = files.Select(f => f.FileName).ToList();
        foreach (var dup in await main.QueryAsync("SELECT DISTINCT CoSPPAFilename FROM FWHSTPAFee WHERE CoSPPAFilename IN @names", new { names }, ct))
            issues.Add(new BordxIssue(0, null, null, "Duplicate record(s) found! The file was already uploaded.", S(dup, "CoSPPAFilename") ?? "", true));

        var look = new Lookups();
        if (files.FirstOrDefault(f => f.Kind == BordxKind.Mp) is { Policies.Count: > 0 } mp)
            foreach (var r in await main.QueryAsync("""
                DECLARE @x xml = @keys;
                SELECT g.CoGRPPaycode, g.CoInsurerRefNo FROM GroupCompanyFW g
                JOIN (SELECT DISTINCT n.value('@a0', 'nvarchar(5)') AS p, n.value('@a1', 'nvarchar(50)') AS i FROM @x.nodes('/r/k') t(n)) k ON g.CoGRPPaycode = k.p AND g.CoInsurerRefNo = k.i
                """, new { keys = Keys(mp.Policies.Select(p => new[] { p.PayorCode, p.InsurerRefNo })) }, ct))
                look.Employers.Add(((S(r, "CoGRPPaycode") ?? "").ToUpperInvariant(), (S(r, "CoInsurerRefNo") ?? "").ToUpperInvariant()));
        if (files.FirstOrDefault(f => f.Kind == BordxKind.Mw) is { Workers.Count: > 0 } mw)
            foreach (var r in await MembersAsync(main, mw.Workers, ct)) look.Workers[WorkerKey(r)] = S(r, "MBMNumber") ?? "";
        if (files.FirstOrDefault(f => f.Kind == BordxKind.Ew) is { Workers.Count: > 0 } ew)
        {
            await LoadEndorsedWorkersAsync(main, ew, look, ct);
            // Workers the MW file of this upload adds are in MBM by the time the EW file is read (legacy order); count them as found.
            foreach (var w in files.FirstOrDefault(f => f.Kind == BordxKind.Mw)?.Workers ?? [])
                if (!look.Workers.ContainsKey(WorkerKey(w))) look.EndorsedWorkers.TryAdd(WorkerKey(w), ["new:" + w.Line]);
        }

        var summaries = new List<BordxFileSummary>();
        foreach (var f in files)
        {
            int ins = 0, upd = 0, skip = 0;
            switch (f.Kind)
            {
                case BordxKind.Mp:
                    var employers = new HashSet<(string, string)>(look.Employers);
                    foreach (var p in f.Policies) if (employers.Add((p.PayorCode.ToUpperInvariant(), p.InsurerRefNo.ToUpperInvariant()))) ins++; else upd++;
                    break;
                case BordxKind.Mw:
                    var workers = new HashSet<(string, string, string)>(look.Workers.Keys);
                    foreach (var w in f.Workers) if (workers.Add(WorkerKey(w))) ins++; else upd++;
                    break;
                case BordxKind.Ew:
                    var endorsed = new HashSet<string>(look.Endorsed, StringComparer.OrdinalIgnoreCase);
                    foreach (var w in f.Workers)
                    {
                        if (!look.EndorsedWorkers.TryGetValue(WorkerKey(w), out var nos))
                        {
                            issues.Add(new BordxIssue(w.Line, w.PolicyNo, w.Name, "Foreign worker not found in membership; the endorsement is skipped", f.FileName, false));
                            skip++; continue;
                        }
                        if (w.EndorseType == "D") upd++;
                        if (endorsed.Add(nos[^1])) ins++; else skip++;
                    }
                    break;
                default: ins = f.Records; break;
            }
            summaries.Add(new BordxFileSummary(f.Kind, f.FileName, f.Records, ins, upd, skip));
        }
        return new Analysis(files, issues, summaries, look);
    }

    public async Task<BordxUploadPreview> PreviewAsync(BordxUploadRequest request, CancellationToken ct = default)
    {
        // A session only so the reads share one connection; it is disposed without commit and writes nothing.
        await using var main = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        var a = await AnalyseAsync(main, request, ct);
        return new BordxUploadPreview(a.Summaries, a.Issues, a.CanUpload);
    }

    private static Task<int> ExecAsync(IDbSession s, string proc, CancellationToken ct, params (string Name, object? Value)[] ps)
    {
        var args = new DynamicParameters();
        foreach (var (name, value) in ps) args.Add(name, value);
        return s.ExecuteAsync($"EXEC dbo.{proc} " + string.Join(", ", ps.Select(p => $"@{p.Name} = @{p.Name}")), args, ct);
    }

    /// <summary>The policy columns shared by Upload_FW_Policy2012, Update_FW_Policy2012 and Upload_FW_TPAFee2012 / Upload_FW_EndtTPAFee2012.</summary>
    private static (string, object?)[] PolicyArgs(BordxPolicyRow p, string user, DateTime today) =>
    [
        ("CoPolicyEffDate", D(p.EffDate)), ("CoPolicyExpDate", D(p.ExpDate)), ("CoEmployerType", p.EmployerType), ("CoEmployerCd", p.EmployerCode),
        ("CoGRPName", p.Name), ("CoGRPAdd1", p.Address1), ("CoGRPAdd2", p.Address2), ("CoGRPAdd3", p.Address3), ("CoGRPAdd4", p.Address4),
        ("CoGRPCity", p.City), ("CoGRPPCode", p.PostCode), ("CoGRPPState", p.State), ("CoNatureBiz", p.BizNature), ("CoAgentCode", p.AgentCode),
        ("CoGRPOffPhone", p.OfficeTel), ("CoGRPPhone", p.HomeTel), ("CoGRPMobilePhone", p.MobileTel), ("CoGRPEmail", p.Email),
        ("CoMaxClaimLmtAmt", p.ClaimLimit), ("CoTotalFW", p.Workers), ("CoTotalPremium", p.Premium), ("CoTotalMCOFee", p.McoFee),
        ("CoGRPLastUpdateUser", user), ("CoGRPLastUpdateDate", today), ("CoPremiumTax", p.PremiumTax), ("CoTPCATax", p.TpcaTax), ("CoDiscount", p.Discount),
    ];

    public async Task<BordxUploadResult> UploadAsync(BordxUploadRequest request, string user, CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var created = new List<string>();
        await using var main = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        await using var maint = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
        var a = await AnalyseAsync(main, request, ct);
        if (!a.CanUpload)
        {
            var first = a.Issues.FirstOrDefault(i => i.Blocking);
            throw new BusinessRuleException(first is null ? "Please Insert File" : first.Line > 0 ? $"{first.FileName} line {first.Line}: {first.Message}" : first.Message);
        }

        foreach (var f in a.Files)
        {
            var file = ("CoSPPAFilename", (object?)f.FileName);
            switch (f.Kind)
            {
                case BordxKind.Mp:
                    foreach (var p in f.Policies)
                    {
                        var common = PolicyArgs(p, user, today).Prepend(("CoPolicyDate", (object?)D(p.PolicyDate)))
                            .Concat([("CoPolStatus", p.PolicyStatus), file, ("CoPolicyNo", p.PolicyNo), ("CoInsurerRefNo", p.InsurerRefNo), ("CoGRPPaycode", p.PayorCode),
                                ("CoESPRecCreatedDate", D(p.Created)), ("CoESPRecUpdateDate", D(p.Updated))]).ToArray();
                        var exists = !a.Lookups.Employers.Add((p.PayorCode.ToUpperInvariant(), p.InsurerRefNo.ToUpperInvariant()));
                        await ExecAsync(main, exists ? "Update_FW_Policy2012" : "Upload_FW_Policy2012", ct, common);
                    }
                    break;

                case BordxKind.Mw:
                    foreach (var w in f.Workers)
                    {
                        var key = WorkerKey(w);
                        (string, object?)[] person = [("MBMIcBcPp", w.PassportNo), ("MBMName", w.Name), ("MBMDOB", D(w.Dob)), ("MBMSex", w.Gender), ("MBMPayorEffDate", D(w.EffDate)),
                            ("MBMPayorEXPDate", D(w.EffDate)), ("AgeCode", ""), ("MBMAdultChild", "A"), ("MBMOccupation", w.Occupation), ("MBMNationalityCd", w.Nationality),
                            ("MBMRenewFlag", w.Renewal), ("MBMPolicyNo", w.PolicyNo), ("PAYCode", w.PayorCode), ("MBMFWUniqueNo", w.FwUniqueNo), ("MBMInsurerUniqueNo", w.InsurerUniqueNo),
                            ("MBMInsuredFor", w.InsuredFor), ("MBMWorkPermitNo", w.WorkPermitNo), ("MBMWorkPermitExpiry", D(w.WorkPermitExpiry)),
                            ("MBMESPRecCreatedDate", D(w.Created)), ("MBMESPRecUpdateDate", D(w.Updated))];
                        if (a.Lookups.Workers.TryGetValue(key, out var existing))
                        {
                            // Legacy Update_MBM_FWHS2012 call: status always 'A', expiry = effective date.
                            await ExecAsync(main, "Update_MBM_FWHS2012", ct, [.. person, ("MBMStatus", "A"), ("MBMNumber", existing)]);
                            continue;
                        }
                        var number = await RegistrationRepository.GenerateNumberAsync(main, maint, w.PayorCode, "G", DateOnly.FromDateTime(D(w.Dob)), w.Name, w.Renewal, ct);
                        await ExecAsync(main, "Upload_MBM_FWHS2012", ct, [.. person, ("MBMStatus", w.Status), ("HLTCode", "N"), ("MBMNumber", number), ("MBMCOVID", "00"),
                            ("MBMDataStatus", "A"), ("MBMMemberType", "P"), ("MBMExpiredStatus", "N"), ("MBMValueDate", today), ("MBMBordxDate", today), ("MBMBatchNo", 1),
                            ("PLNCode", "FH01"), ("INSCode", "G"), ("MBMAvailableLimit", w.ClaimLimit), ("HSClnInd", "H")]);
                        await ExecAsync(main, "Upload_FWHS_MBMOthers", ct, ("MBMNumber", number), ("MBMAgentCode", ""), ("MBMPremium", w.Premium), ("MBMBasicPrem", w.Premium),
                            ("MBMBasicMCO", w.McoFee), ("MBMMCOFee", w.McoFee), ("RELCODE", "P"), ("MBMGroupCompany", ""));
                        a.Lookups.Workers[key] = number;
                        created.Add(number);
                    }
                    break;

                case BordxKind.Np:
                    foreach (var p in f.Policies)
                        await ExecAsync(main, "Upload_FW_TPAFee2012", ct, [("CoGRPPaycode", p.PayorCode), ("CoInsurerRefNo", p.InsurerRefNo), ("CoPolicyNo", p.PolicyNo),
                            ("CoPolicyDate", D(p.PolicyDate)), .. PolicyArgs(p, user, today), file, ("CoPAYBatchNo", p.BatchNo), ("CoPAYDateSubmission", D(p.Submission)),
                            ("CoESPRecUpdateDate", D(p.Updated))]);
                    break;

                case BordxKind.Ep:
                    foreach (var p in f.Policies)
                        await ExecAsync(main, "Upload_FW_EndtTPAFee2012", ct, [("CoGRPPaycode", p.PayorCode), ("CoInsurerRefNo", p.InsurerRefNo), ("CoPolicyNo", p.PolicyNo),
                            .. PolicyArgs(p, user, today), ("CoEndorseNo", p.EndorseNo), ("CoEndorseDate", D(p.EndorseDate)), ("CoEndorseEffDate", D(p.EndorseEffDate)), file,
                            ("CoPAYBatchNo", p.BatchNo), ("CoPAYDateSubmission", D(p.Submission)), ("CoESPRecUpdateDate", D(p.Updated))]);
                    break;

                case BordxKind.Ex:
                    foreach (var p in f.Policies)
                        await ExecAsync(main, "Upload_FW_CANTPAFee2012", ct, ("CoGRPPaycode", p.PayorCode), ("CoInsurerRefNo", p.InsurerRefNo), ("CoPolicyNo", p.PolicyNo),
                            ("CoPolicyEffDate", D(p.EffDate)), ("CoTotalPremium", p.Premium), ("CoTotalMCOFee", p.McoFee), ("CoGRPLastUpdateUser", user), ("CoGRPLastUpdateDate", today),
                            ("CoPremiumTax", p.PremiumTax), ("CoTPCATax", p.TpcaTax), ("CoEndorseNo", p.EndorseNo), ("CoEndorseDate", D(p.EndorseDate)),
                            ("CoEndorseEffDate", D(p.EndorseEffDate)), file, ("CoPAYBatchNo", p.BatchNo), ("CoPAYDateSubmission", D(p.Submission)), ("CoESPRecUpdateDate", D(p.Updated)));
                    break;

                case BordxKind.Ew:
                    // Read again inside the transaction: the MW file above may have added the workers.
                    await LoadEndorsedWorkersAsync(main, f, a.Lookups, ct);
                    foreach (var w in f.Workers)
                    {
                        if (!a.Lookups.EndorsedWorkers.TryGetValue(WorkerKey(w), out var numbers)) continue;
                        // A deletion endorsement cancels every MBM row of the worker; one FWHSEndorsementMBM row per member number (its primary key).
                        if (w.EndorseType == "D")
                            await main.ExecuteAsync("UPDATE MBM SET MBMStatus = 'C' WHERE MBMNumber IN @numbers", new { numbers }, ct);
                        var number = numbers[^1];
                        if (!a.Lookups.Endorsed.Add(number)) continue;
                        await ExecAsync(main, "Upload_MBMEndt_FWHS2012", ct, ("MBMNumber", number), ("MBMInsurerUniqueNo", w.InsurerUniqueNo), ("MBMFWUniqueNo", w.FwUniqueNo),
                            ("MBMEndorsementNo", w.EndorseNo), ("MBMEndorsementDate", D(w.EndorseDate)), ("MBMPolicyNo", w.PolicyNo), ("MBMPayorEffDate", D(w.EffDate)),
                            ("MBMEndorseType", w.EndorseType), ("MBMNationalityCd", w.Nationality), ("MBMIcBcPp", w.PassportNo), ("MBMOccupation", w.Occupation),
                            ("MBMName", w.Name), ("MBMSex", w.Gender), ("MBMDOB", D(w.Dob)), ("MBMPremium", w.Premium), ("MBMMCOFee", w.McoFee), ("MBMValueDate", today),
                            ("MBMInsuredFor", w.InsuredFor), ("MBMWorkPermitNo", w.WorkPermitNo), ("MBMWorkPermitExpiry", D(w.WorkPermitExpiry)),
                            ("MBMPayBatchNo", w.BatchNo), ("MBMPAYDateSubmission", D(w.BatchDate)), ("MBMESPRecUpdateDate", D(w.Updated)));
                    }
                    break;
            }
        }

        // Member numbers come from the maintenance sequences: commit those first so a failure afterwards only leaves a gap, never a reused number.
        await maint.CommitAsync(ct);
        await main.CommitAsync(ct);

        audit.Record(new AuditEntry("Membership.UploadBordx", "FWHS", string.Join(",", a.Files.Select(f => f.FileName)), user, new Dictionary<string, object?>
            { ["files"] = a.Summaries, ["newMembers"] = created.Count }));
        return new BordxUploadResult(a.Summaries, created);
    }
}
