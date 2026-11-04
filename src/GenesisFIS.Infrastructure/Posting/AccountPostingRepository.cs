using System.Globalization;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Posting;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Posting;

/// <summary>
/// A/C Export (legacy frmPosting* forms, stored procedure SUN_HIS_Posting). The legacy VIEW_SUN_Posting_* views join the HIS databases' cross
/// reference tables, which hold almost no FWHS cases, so the claims and invoices are read from the worksheet database and their case, member and plan
/// from the main database here. SUNPosting has no index on its keys, so it is read once per search and hash-joined.
/// </summary>
internal sealed class AccountPostingRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IAccountPostingRepository
{
    private const int Chunk = 1000;
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);

    public async Task<PostingOptions> OptionsAsync(string user, CancellationToken ct = default)
    {
        var hlt = (await QueryAsync(GenesisDb.Main, "SELECT HLTCode, HLTDescription FROM HLT ORDER BY HLTCode", null, ct))
            .Select(r => new PostingOption(S(r, "HLTCode")!.Trim(), S(r, "HLTDescription")?.Trim() ?? "")).ToList();
        var pay = (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PAYCompanyName, HLTCode FROM PAY WHERE ISNULL(PAYCode, '') <> '' ORDER BY PAYCode", null, ct))
            .Select(r => new PostingOption(S(r, "PAYCode")!.Trim(), S(r, "PAYCompanyName")?.Trim() ?? "")).ToList();
        var periods = (await QueryAsync(GenesisDb.Maintenance, "SELECT AccMonth, AccYear FROM AccPeriod", null, ct))
            .Select(r => PostingRules.Period(S(r, "AccMonth"), S(r, "AccYear"))).Where(p => PostingRules.ParsePeriod(p) is not null)
            .OrderBy(p => p[3..]).ThenBy(p => p[..2]).ToList();
        return new PostingOptions(hlt, pay, periods, await WindowAsync(user, ct));
    }

    private async Task<PostingWindow> WindowAsync(string user, CancellationToken ct)
    {
        var t = (await QueryAsync(GenesisDb.Maintenance, "SELECT StartTime, EndTime FROM TimeMain WHERE Process = 'POSTING'", null, ct)).FirstOrDefault();
        var special = await ScalarAsync<string>(GenesisDb.Maintenance, "SELECT SpecialAccess FROM USR WHERE USRCode = @user", new { user }, ct);
        TimeOnly? Time(string c) => t is not null && D(t, c) is { } d ? TimeOnly.FromDateTime(d) : null;
        return PostingWindow.For(TimeOnly.FromDateTime(DateTime.Now), Time("StartTime"), Time("EndTime"), special?.Trim() == "Y");
    }

    private async Task<List<IDictionary<string, object?>>> InChunksAsync(GenesisDb db, string sql, IEnumerable<string> keys, CancellationToken ct)
    {
        var all = new List<IDictionary<string, object?>>();
        foreach (var chunk in keys.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(Chunk))
            all.AddRange(await QueryAsync(db, sql, new { keys = chunk }, ct));
        return all;
    }

    /// <summary>The member side of a case: payor, health type, member and patient name (the covered person when the case is for one), product category.</summary>
    private sealed record Person(string Payor, string HealthType, string? MemberName, string? PatientName, string ProductCategory, string? Hospital);

    /// <summary>Loads the cases' members, covered persons and plans; cases of Mediholdings' own payor (MH) are left out, as in the legacy views.</summary>
    private async Task<Dictionary<string, Person>> PeopleAsync(IEnumerable<string> caseNumbers, CancellationToken ct)
    {
        var cases = (await InChunksAsync(GenesisDb.Main, "SELECT CASNumber, MBMNumber, MBMPatientCovID, CASHOSCode FROM CAS WHERE CASNumber IN @keys", caseNumbers, ct))
            .ToDictionary(r => S(r, "CASNumber")!.Trim(), StringComparer.OrdinalIgnoreCase);
        var members = (await InChunksAsync(GenesisDb.Main, "SELECT MBMNumber, PAYCode, HLTCode, MBMName FROM MBM WHERE MBMNumber IN @keys",
            cases.Values.Select(r => S(r, "MBMNumber")?.Trim()).OfType<string>(), ct)).GroupBy(r => S(r, "MBMNumber")!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var covered = (await InChunksAsync(GenesisDb.Main, "SELECT MBMCNumber, MBMCCoverID, MBMCName FROM MBMCoveredPersons WHERE MBMCNumber IN @keys",
            cases.Values.Where(r => S(r, "MBMPatientCovID")?.Trim() is { } c && c != "00").Select(r => S(r, "MBMNumber")!.Trim()), ct))
            .GroupBy(r => (S(r, "MBMCNumber")!.Trim().ToUpperInvariant(), S(r, "MBMCCoverID")?.Trim())).ToDictionary(g => g.Key, g => S(g.First(), "MBMCName")?.Trim());
        // Legacy GetProductCat: the first plan of the health type and payor with a product category; SM for Sihat Malaysia (health type S).
        var productCats = (await QueryAsync(GenesisDb.Main, "SELECT HLTCode, PAYCode, PLNProCat FROM PLN WHERE ISNULL(PLNProCat, '') <> ''", null, ct))
            .GroupBy(r => (S(r, "HLTCode")?.Trim() ?? "", S(r, "PAYCode")?.Trim() ?? "")).ToDictionary(g => g.Key, g => S(g.First(), "PLNProCat")!.Trim());

        var people = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);
        foreach (var (caseNo, c) in cases)
        {
            if (S(c, "MBMNumber")?.Trim() is not { } mbm || !members.TryGetValue(mbm, out var m)) continue;
            var payor = S(m, "PAYCode")?.Trim() ?? "";
            if (payor == "MH") continue;
            var hlt = S(m, "HLTCode")?.Trim() ?? "";
            var cov = S(c, "MBMPatientCovID")?.Trim();
            var name = S(m, "MBMName")?.Trim();
            people[caseNo] = new Person(payor, hlt, name, cov is null or "00" ? name : covered.GetValueOrDefault((mbm.ToUpperInvariant(), cov)) ?? name,
                hlt == "S" ? "SM" : productCats.GetValueOrDefault((hlt, payor)) ?? "", Blank(S(c, "CASHOSCode")));
        }
        return people;
    }

    private static bool Matches(Person p, string? payor, string? healthType) =>
        (Blank(payor) is not { } pay || p.Payor.Equals(pay, StringComparison.OrdinalIgnoreCase)) && (Blank(healthType) is not { } h || p.HealthType.Equals(h, StringComparison.OrdinalIgnoreCase));

    private static bool IsPosted(IDictionary<string, object?> r, string status) => S(r, status)?.Trim() == "1" && S(r, "PostStatus")?.Trim() == "1";

    /// <summary>Posted search: items with a posting in the post date and batch ranges. Unposted search: items with no posting at all.</summary>
    private static bool Wanted(IEnumerable<IDictionary<string, object?>> rows, string status, bool posted, DateOnly? postFrom, DateOnly? postTo, string? batchFrom, string? batchTo)
    {
        if (!posted) return !rows.Any(r => IsPosted(r, status));
        return rows.Where(r => IsPosted(r, status)).Any(r =>
            (postFrom is null || Day(r, "PostDate") >= postFrom) && (postTo is null || Day(r, "PostDate") <= postTo)
            && (Blank(batchFrom) is not { } bf || string.CompareOrdinal(S(r, "BatchNo")?.Trim(), bf) >= 0)
            && (Blank(batchTo) is not { } bt || string.CompareOrdinal(S(r, "BatchNo")?.Trim(), bt) <= 0));
    }

    /// <summary>The checks of the legacy CmdPost common to every form, and the chosen items of a fresh search.</summary>
    private async Task<((string Month, string Year) Period, List<PostingItem> Chosen)> PrepareAsync(string user, IReadOnlyList<string>? keyList, string? accountPeriod,
        Func<Task<PostingJournal>> search, CancellationToken ct)
    {
        var window = await WindowAsync(user, ct);
        if (!window.Open) throw new BusinessRuleException(window.Message ?? "Posting is closed now.");
        var keys = (keyList ?? []).Select(k => k.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0) throw new BusinessRuleException("Please select the record");
        if (PostingRules.ParsePeriod(accountPeriod) is not { } period) throw new BusinessRuleException("Please Keyin for Accounting Period ! ", field: "accountPeriod");
        if ((await ScalarAsync<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM AccPeriod WHERE AccMonth = @m AND AccYear = @y", new { m = period.Month, y = period.Year }, ct)) == 0)
            throw new BusinessRuleException($"Accounting period {accountPeriod} is not open (AccPeriod).", field: "accountPeriod");
        var chosen = (await search()).Items.Where(i => keys.Contains(i.Key)).ToList();
        if (chosen.Count != keys.Count) throw BusinessRuleException.Conflict("Some selected records are no longer waiting to be posted. Please search again.");
        if (chosen.FirstOrDefault(i => i.Problem is not null) is { } bad) throw new BusinessRuleException($"{bad.Key}: {bad.Problem}");
        return (period, chosen);
    }

    /// <summary>Legacy GenSUNPostingBatchNo: the last batch number plus one (1001 for the first), taken under lock.</summary>
    private static async Task<string> NextBatchAsync(IDbSession ws, CancellationToken ct)
    {
        var last = await ws.ScalarAsync<string?>("SELECT TOP 1 BatchNo FROM SunPostBatch WITH (UPDLOCK, HOLDLOCK) ORDER BY BatchIndex DESC", null, ct);
        var batchNo = (int.TryParse(last?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n + 1 : 1001).ToString(CultureInfo.InvariantCulture);
        if (batchNo.Length > 5) throw new BusinessRuleException("The SUN batch number has run out (SunPostBatch).");
        await ws.ExecuteAsync(last is null ? "INSERT INTO SunPostBatch (BatchNo) VALUES (@batchNo)" : "UPDATE SunPostBatch SET BatchNo = @batchNo", new { batchNo }, ct);
        return batchNo;
    }

    private static (string Case, string Version) SplitKey(string key) => (key[..key.LastIndexOf('/')], key[(key.LastIndexOf('/') + 1)..]);

    // ---- Invoice > Hospital (InvHosp) -------------------------------------------------------------------------------------------------------------

    public async Task<PostingJournal> SearchHospitalInvoicesAsync(HospitalInvoicePostingSearch search, CancellationToken ct = default)
    {
        if (PostingRules.Validate(search) is { } problem) throw new BusinessRuleException(problem);
        var s = search;
        var where = new List<string> { "i.INVPayTo = 'H'", "cl.CLMPayableByMedix2H <> 0" };
        void Add(bool when, string sql) { if (when) where.Add(sql); }
        Add(s.Bordxed, "ISNULL(i.INVBordxRefNo, '') <> ''");
        Add(!s.Bordxed, "ISNULL(i.INVBordxRefNo, '') = ''");
        Add(Blank(s.Hospital) is not null, "i.INVPayee = @hospital");
        Add(s.InvoiceDateFrom is not null, "i.INVEnteredDate >= @invFrom");
        Add(s.InvoiceDateTo is not null, "i.INVEnteredDate < @invTo");
        Add(Blank(s.InvoiceNoFrom) is not null, "i.InvNo >= @noFrom");
        Add(Blank(s.InvoiceNoTo) is not null, "i.InvNo <= @noTo");
        Add(s.BordxFrom is not null, "b.BordxDate >= @bFrom");
        Add(s.BordxTo is not null, "b.BordxDate < @bTo");
        var args = new
        {
            hospital = Blank(s.Hospital), invFrom = At(s.InvoiceDateFrom), invTo = At(s.InvoiceDateTo?.AddDays(1)), noFrom = Blank(s.InvoiceNoFrom), noTo = Blank(s.InvoiceNoTo),
            bFrom = At(s.BordxFrom), bTo = At(s.BordxTo?.AddDays(1)),
        };
        // The claims with a matching hospital invoice, then all their hospital invoices with any InvHosp posting.
        var rows = await QueryAsync(GenesisDb.Worksheet, $"""
            WITH c AS (SELECT DISTINCT i.CASNumber, i.INVWSversion, cl.CLMPayableByMedix2H
                       FROM ClaimInvoice i JOIN Claim cl ON cl.CASNumber = i.CASNumber AND CAST(cl.ClaimVersion AS nvarchar(10)) = i.INVWSversion
                            LEFT JOIN Bordx b ON b.BordxRefNo = i.INVBordxRefNo
                       WHERE {string.Join(" AND ", where)}),
                 p AS (SELECT INVIndex, InvHStatus, PostStatus, PostDate, AccMonth, AccYear, BatchNo FROM SUNPosting WHERE PostType = 'InvHosp')
            SELECT CAST(i.INVIndex AS bigint) AS INVIndex, i.InvNo, i.INVEnteredDate, i.CASNumber, i.INVWSversion, i.INVPayee, i.INVBordxRefNo, c.CLMPayableByMedix2H,
                   p.InvHStatus, p.PostStatus, p.PostDate, p.AccMonth, p.AccYear, p.BatchNo
            FROM c JOIN ClaimInvoice i ON i.CASNumber = c.CASNumber AND i.INVWSversion = c.INVWSversion AND i.INVPayTo = 'H'
                 LEFT HASH JOIN p ON p.INVIndex = CAST(CAST(i.INVIndex AS bigint) AS nvarchar(50))
            ORDER BY i.CASNumber, i.INVWSversion, i.INVIndex
            """, args, ct, timeoutSeconds: 300);

        // A claim is one item whatever its number of hospital invoices: the legacy view repeated the claim's payable on each invoice, which would post
        // it twice. Unposted: none of its hospital invoices is posted.
        var claims = rows.GroupBy(r => (Case: S(r, "CASNumber")!.Trim(), Version: S(r, "INVWSversion")?.Trim() ?? ""))
            .Where(g => Wanted(g, "InvHStatus", s.Posted, s.PostDateFrom, s.PostDateTo, s.BatchFrom, s.BatchTo)).ToList();
        var people = await PeopleAsync(claims.Select(g => g.Key.Case), ct);
        var hospitals = (await InChunksAsync(GenesisDb.Main, "SELECT HOSCode, HOSAccCode FROM HOS WHERE HOSCode IN @keys",
            claims.SelectMany(g => g).Select(r => S(r, "INVPayee")?.Trim()).OfType<string>(), ct)).GroupBy(r => S(r, "HOSCode")!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Blank(S(g.First(), "HOSAccCode")), StringComparer.OrdinalIgnoreCase);

        var items = new List<PostingItem>();
        var truncated = false;
        foreach (var claim in claims)
        {
            if (!people.TryGetValue(claim.Key.Case, out var person) || !Matches(person, s.Payor, s.HealthType)) continue;
            if (items.Count == PostingRules.MaxItems) { truncated = true; break; }
            // The first invoice gives the transaction reference and date; for a posted claim, its posting gives the period and batch.
            var first = s.Posted ? claim.First(r => IsPosted(r, "InvHStatus")) : claim.First();
            var payee = S(first, "INVPayee")?.Trim() ?? "";
            var hosAcc = hospitals.GetValueOrDefault(payee);
            var amount = Dec(first, "CLMPayableByMedix2H") ?? 0;
            var period = PostingRules.Period(S(first, "AccMonth"), S(first, "AccYear"));
            JournalLine Line(string acc, string dc) => new("RC", "", "MED", S(first, "InvNo")?.Trim() ?? "", period == "" ? null : period, Day(first, "INVEnteredDate"),
                PostingRules.Description(person.PatientName), acc, amount, dc, hosAcc is null ? null : hosAcc[1..], null, person.Payor, PostingRules.CaseRef(claim.Key.Case),
                person.ProductCategory, PostingRules.Last(S(first, "INVBordxRefNo"), 5), S(first, "BatchNo")?.Trim());
            var why = hosAcc is null ? $"Hospital {payee} has no account code (HOSAccCode)." : person.Payor == "" ? "The member has no payor." : null;
            items.Add(new PostingItem($"{claim.Key.Case}/{claim.Key.Version}", s.Posted, [Line("6000" + person.Payor, "D"), Line(hosAcc ?? "", "C")],
                claim.Select(r => S(r, "InvNo")?.Trim() ?? "").ToList(), why));
        }
        var total = items.Sum(i => i.Lines[0].Amount);
        return new PostingJournal(items, total, total, truncated);
    }

    public async Task<PostingResult> PostHospitalInvoicesAsync(HospitalInvoicePostRequest request, string user, CancellationToken ct = default)
    {
        var search = request.Search ?? throw new BusinessRuleException("Please search the records first.");
        if (search.InvoiceDateTo is null) throw new BusinessRuleException("Please Enter Invoice Date(To)", field: "invoiceDateTo");
        if (!search.Bordxed) throw new BusinessRuleException("Please Change Bordx Status", field: "bordxed");
        if (search.Posted) throw new BusinessRuleException("Record(s) has been posted ! ");
        var (period, chosen) = await PrepareAsync(user, request.Keys, request.AccountPeriod, () => SearchHospitalInvoicesAsync(search, ct), ct);

        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        var indexes = new List<string>();
        foreach (var item in chosen)
        {
            var (caseNo, version) = SplitKey(item.Key);
            var rows = await ws.QueryAsync("""
                SELECT CAST(CAST(i.INVIndex AS bigint) AS nvarchar(50)) AS Idx,
                       (SELECT COUNT(*) FROM SUNPosting p WITH (UPDLOCK, HOLDLOCK) WHERE p.INVIndex = CAST(CAST(i.INVIndex AS bigint) AS nvarchar(50)) AND p.PostType = 'InvHosp') AS Posted
                FROM ClaimInvoice i WHERE i.CASNumber = @caseNo AND i.INVWSversion = @version AND i.INVPayTo = 'H'
                """, new { caseNo, version }, ct);
            if (rows.Any(r => (I(r, "Posted") ?? 0) > 0)) throw BusinessRuleException.Conflict($"{item.Key} has been posted meanwhile. Please search again.");
            indexes.AddRange(rows.Select(r => S(r, "Idx")!));
        }
        var batchNo = await NextBatchAsync(ws, ct);
        // As legacy: keyed by the invoice only. CASNumber stays empty, because the member posting (and legacy reports) treat any SUNPosting row with
        // the case number and version as that claim's member posting.
        foreach (var index in indexes)
            await ws.ExecuteAsync("""
                INSERT INTO SUNPosting (INVIndex, InvHStatus, PostStatus, PostType, PostDate, PostBy, AccMonth, AccYear, BatchNo)
                VALUES (@index, '1', '1', 'InvHosp', CAST(GETDATE() AS date), @user, @month, @year, @batchNo)
                """, new { index, user, month = period.Month, year = period.Year, batchNo }, ct);
        await ws.CommitAsync(ct);

        audit.Record(new AuditEntry("Posting.InvoiceHospital", "SUNPosting", batchNo, user, new Dictionary<string, object?>
        {
            ["accountPeriod"] = request.AccountPeriod, ["items"] = chosen.Select(i => i.Key).ToList(), ["invoices"] = indexes.Count, ["amount"] = chosen.Sum(i => i.Lines[0].Amount),
        }));
        var posted = await SearchHospitalInvoicesAsync(new HospitalInvoicePostingSearch(Bordxed: true, Posted: true, BatchFrom: batchNo, BatchTo: batchNo), ct);
        return new PostingResult(batchNo, chosen.Count, indexes.Count, posted);
    }

    // ---- Invoice > Member (InvMBM) ----------------------------------------------------------------------------------------------------------------

    public async Task<PostingJournal> SearchMemberInvoicesAsync(MemberInvoicePostingSearch search, CancellationToken ct = default)
    {
        if (PostingRules.Validate(search) is { } problem) throw new BusinessRuleException(problem);
        var s = search;
        // Legacy VIEW_SUN_Posting_MBMInv: the claims paying the member (CLMPayableByMedix2M), positive or negative by Post Type, with their invoices'
        // bordereau; legacy compares the invoice "entered" date with the claim's close date.
        var where = new List<string> { s.Negative ? "cl.CLMPayableByMedix2M < 0" : "cl.CLMPayableByMedix2M > 0" };
        void Add(bool when, string sql) { if (when) where.Add(sql); }
        Add(s.Bordxed, "ISNULL(i.INVBordxRefNo, '') <> ''");
        Add(!s.Bordxed, "ISNULL(i.INVBordxRefNo, '') = ''");
        Add(s.ClaimDateFrom is not null, "cl.ClaimCloseDate >= @dFrom");
        Add(s.ClaimDateTo is not null, "cl.ClaimCloseDate < @dTo");
        Add(Blank(s.ClaimNoFrom) is not null, "cl.CASNumber >= @noFrom");
        Add(Blank(s.ClaimNoTo) is not null, "cl.CASNumber <= @noTo");
        Add(s.BordxFrom is not null, "b.BordxDate >= @bFrom");
        Add(s.BordxTo is not null, "b.BordxDate < @bTo");
        var args = new
        {
            dFrom = At(s.ClaimDateFrom), dTo = At(s.ClaimDateTo?.AddDays(1)), noFrom = Blank(s.ClaimNoFrom), noTo = Blank(s.ClaimNoTo),
            bFrom = At(s.BordxFrom), bTo = At(s.BordxTo?.AddDays(1)),
        };
        var rows = await QueryAsync(GenesisDb.Worksheet, $"""
            WITH c AS (SELECT cl.CASNumber, CAST(cl.ClaimVersion AS nvarchar(10)) AS Ver, cl.CLMPayableByMedix2M, cl.ClaimCloseDate, cl.CLMBordxRevInd, MAX(i.INVBordxRefNo) AS BordxRef
                       FROM Claim cl JOIN ClaimInvoice i ON i.CASNumber = cl.CASNumber AND i.INVWSversion = CAST(cl.ClaimVersion AS nvarchar(10))
                            LEFT JOIN Bordx b ON b.BordxRefNo = i.INVBordxRefNo
                       WHERE {string.Join(" AND ", where)}
                       GROUP BY cl.CASNumber, cl.ClaimVersion, cl.CLMPayableByMedix2M, cl.ClaimCloseDate, cl.CLMBordxRevInd),
                 p AS (SELECT CASNumber, ClmVersion, InvMStatus, PostStatus, PostDate, AccMonth, AccYear, BatchNo FROM SUNPosting WHERE PostType = 'InvMBM')
            SELECT c.CASNumber, c.Ver, c.CLMPayableByMedix2M, c.ClaimCloseDate, c.CLMBordxRevInd, c.BordxRef, p.InvMStatus, p.PostStatus, p.PostDate, p.AccMonth, p.AccYear, p.BatchNo
            FROM c LEFT HASH JOIN p ON p.CASNumber = c.CASNumber AND RTRIM(p.ClmVersion) = c.Ver
            ORDER BY c.CASNumber, c.Ver
            """, args, ct, timeoutSeconds: 300);

        var claims = rows.GroupBy(r => (Case: S(r, "CASNumber")!.Trim(), Version: S(r, "Ver")?.Trim() ?? ""))
            .Where(g => Wanted(g, "InvMStatus", s.Posted, s.PostDateFrom, s.PostDateTo, s.BatchFrom, s.BatchTo)).ToList();
        var people = await PeopleAsync(claims.Select(g => g.Key.Case), ct);

        var items = new List<PostingItem>();
        var truncated = false;
        foreach (var claim in claims)
        {
            if (!people.TryGetValue(claim.Key.Case, out var person) || !Matches(person, s.Payor, s.HealthType)) continue;
            if (items.Count == PostingRules.MaxItems) { truncated = true; break; }
            var first = s.Posted ? claim.First(r => IsPosted(r, "InvMStatus")) : claim.First();
            // Legacy MBMInvListing: Positive is journal MJC (credit 8200 + payor for the member, debit 6000 + payor); Negative is MJD with the sides swapped,
            // the amount turned positive and 6200 + payor when the bordereau was reversed.
            var amount = Math.Abs(Dec(first, "CLMPayableByMedix2M") ?? 0);
            var period = PostingRules.Period(S(first, "AccMonth"), S(first, "AccYear"));
            var reversed = S(first, "CLMBordxRevInd")?.Trim() == "Y";
            var transRef = $"{claim.Key.Case}-{claim.Key.Version}";
            JournalLine Line(string acc, string dc, string? name) => new(s.Negative ? "MJD" : "MJC", "", "MED", transRef, period == "" ? null : period, Day(first, "ClaimCloseDate"),
                PostingRules.Description(name), acc, amount, dc, "0R", null, person.Payor, PostingRules.CaseRef(claim.Key.Case), person.ProductCategory,
                PostingRules.Last(S(first, "BordxRef"), 5), S(first, "BatchNo")?.Trim());
            var member = Line((s.Negative && reversed ? "6200" : "8200") + person.Payor, s.Negative ? "D" : "C", person.MemberName);
            var payor = Line("6000" + person.Payor, s.Negative ? "C" : "D", person.PatientName);
            items.Add(new PostingItem($"{claim.Key.Case}/{claim.Key.Version}", s.Posted, [member, payor], [transRef], person.Payor == "" ? "The member has no payor." : null));
        }
        var total = items.Sum(i => i.Lines[0].Amount);
        return new PostingJournal(items, total, total, truncated);
    }

    public async Task<PostingResult> PostMemberInvoicesAsync(MemberInvoicePostRequest request, string user, CancellationToken ct = default)
    {
        var search = request.Search ?? throw new BusinessRuleException("Please search the records first.");
        if (search.ClaimDateTo is null) throw new BusinessRuleException("Please Enter Invoice Date(To)", field: "claimDateTo");
        if (!search.Bordxed) throw new BusinessRuleException("Please change Bordx Status", field: "bordxed");
        if (search.Posted) throw new BusinessRuleException("Record(s) has been posted ! ");
        var (period, chosen) = await PrepareAsync(user, request.Keys, request.AccountPeriod, () => SearchMemberInvoicesAsync(search, ct), ct);

        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        foreach (var item in chosen)
        {
            var (caseNo, version) = SplitKey(item.Key);
            if (await ws.ScalarAsync<int>("SELECT COUNT(*) FROM SUNPosting WITH (UPDLOCK, HOLDLOCK) WHERE CASNumber = @caseNo AND RTRIM(ClmVersion) = @version AND PostType = 'InvMBM'",
                    new { caseNo, version }, ct) > 0)
                throw BusinessRuleException.Conflict($"{item.Key} has been posted meanwhile. Please search again.");
        }
        var batchNo = await NextBatchAsync(ws, ct);
        foreach (var item in chosen)
        {
            var (caseNo, version) = SplitKey(item.Key);
            await ws.ExecuteAsync("""
                INSERT INTO SUNPosting (CASNumber, ClmVersion, InvMStatus, PostStatus, PostType, PostDate, PostBy, AccMonth, AccYear, BatchNo)
                VALUES (@caseNo, @version, '1', '1', 'InvMBM', CAST(GETDATE() AS date), @user, @month, @year, @batchNo)
                """, new { caseNo, version, user, month = period.Month, year = period.Year, batchNo }, ct);
        }
        await ws.CommitAsync(ct);

        audit.Record(new AuditEntry("Posting.InvoiceMember", "SUNPosting", batchNo, user, new Dictionary<string, object?>
        {
            ["accountPeriod"] = request.AccountPeriod, ["negative"] = search.Negative, ["items"] = chosen.Select(i => i.Key).ToList(), ["amount"] = chosen.Sum(i => i.Lines[0].Amount),
        }));
        var posted = await SearchMemberInvoicesAsync(new MemberInvoicePostingSearch(Bordxed: true, Posted: true, BatchFrom: batchNo, BatchTo: batchNo, Negative: search.Negative), ct);
        return new PostingResult(batchNo, chosen.Count, chosen.Count, posted);
    }

    // ---- Payment > Hospital (PayHosp) -------------------------------------------------------------------------------------------------------------

    public async Task<PostingJournal> SearchHospitalPaymentsAsync(HospitalPaymentPostingSearch search, CancellationToken ct = default)
    {
        if (PostingRules.Validate(search) is { } problem) throw new BusinessRuleException(problem);
        var s = search;
        var where = new List<string> { "ISNULL(PVHOSNo, '') <> ''" };
        void Add(bool when, string sql) { if (when) where.Add(sql); }
        Add(s.VoucherDateFrom is not null, "PVHOSDate >= @dFrom");
        Add(s.VoucherDateTo is not null, "PVHOSDate < @dTo");
        Add(Blank(s.VoucherNoFrom) is not null, "PVHOSNo >= @noFrom");
        Add(Blank(s.VoucherNoTo) is not null, "PVHOSNo <= @noTo");
        var vouchers = await QueryAsync(GenesisDb.Payment, $"""
            SELECT PVHOSNo, PVHOSDate, PVHOSChequeNo, PVHOSChequeAmt, PVStatus, CASNumber
            FROM Payment2HOS WHERE {string.Join(" AND ", where)} ORDER BY PVHOSNo, CASNumber
            """, new { dFrom = At(s.VoucherDateFrom), dTo = At(s.VoucherDateTo?.AddDays(1)), noFrom = Blank(s.VoucherNoFrom), noTo = Blank(s.VoucherNoTo) }, ct, timeoutSeconds: 300);
        var byVoucher = vouchers.GroupBy(r => S(r, "PVHOSNo")!.Trim(), StringComparer.OrdinalIgnoreCase).ToList();
        // SUNPosting has no index on TransRef: one read of the voucher number range (lookups in batches each scanned the table).
        var keys = byVoucher.Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
        var postings = (keys.Count == 0 ? [] : await QueryAsync(GenesisDb.Worksheet,
            "SELECT TransRef, PayHStatus, PostStatus, PostDate, AccMonth, AccYear, BatchNo FROM SUNPosting WHERE PostType = 'PayHosp' AND TransRef >= @lo AND TransRef <= @hi",
            new { lo = keys[0], hi = keys[^1] }, ct)).Where(r => S(r, "TransRef") is not null).ToLookup(r => S(r, "TransRef")!.Trim(), StringComparer.OrdinalIgnoreCase);
        var wanted = byVoucher.Where(g => Wanted(postings[g.Key], "PayHStatus", s.Posted, s.PostDateFrom, s.PostDateTo, s.BatchFrom, s.BatchTo)).ToList();
        var people = await PeopleAsync(wanted.SelectMany(g => g).Select(r => S(r, "CASNumber")?.Trim()).OfType<string>(), ct);
        var hospitals = (await InChunksAsync(GenesisDb.Main, "SELECT HOSCode, HOSAccCode FROM HOS WHERE HOSCode IN @keys",
            people.Values.Select(p => p.Hospital).OfType<string>(), ct)).GroupBy(r => S(r, "HOSCode")!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Blank(S(g.First(), "HOSAccCode")), StringComparer.OrdinalIgnoreCase);

        var items = new List<PostingItem>();
        var truncated = false;
        foreach (var voucher in wanted)
        {
            // A voucher can pay several cases; it is one item for the voucher amount (the legacy view repeated the amount for each payor of its cases).
            var cases = voucher.Select(r => S(r, "CASNumber")?.Trim()).OfType<string>().Select(c => people.GetValueOrDefault(c)).OfType<Person>().ToList();
            // Legacy view: Mediholdings (MH) and ZT payments are never posted.
            if (cases.Count == 0 || cases.Any(p => p.Payor is "MH" or "ZT") || !cases.Any(p => Matches(p, s.Payor, s.HealthType))) continue;
            if (items.Count == PostingRules.MaxItems) { truncated = true; break; }
            var first = voucher.First();
            var posting = s.Posted ? postings[voucher.Key].First(r => IsPosted(r, "PayHStatus")) : null;
            var person = cases[0];
            var hospital = cases.Select(p => p.Hospital).FirstOrDefault(h => h is not null) ?? "";
            var hosAcc = hospitals.GetValueOrDefault(hospital);
            var bank = PostingRules.BankAccount(voucher.Key);
            var amount = voucher.Max(r => Dec(r, "PVHOSChequeAmt") ?? 0);
            var period = posting is null ? "" : PostingRules.Period(S(posting, "AccMonth"), S(posting, "AccYear"));
            var description = $"{(Blank(S(first, "PVHOSChequeNo")) is { } chq ? chq + "-" : "")}{hospital}";
            JournalLine Line(string acc, string dc) => new("CP", "", "MED", voucher.Key, period == "" ? null : period, Day(first, "PVHOSDate"), description, acc, amount, dc,
                hosAcc is null ? null : hosAcc[1..], null, person.Payor, null, person.ProductCategory, null, posting is null ? null : S(posting, "BatchNo")?.Trim());
            var why = bank is null ? $"No bank account code for payment vouchers starting {voucher.Key[..Math.Min(2, voucher.Key.Length)]} (legacy: CA, CB, CC)."
                : hosAcc is null ? $"Hospital {hospital} has no account code (HOSAccCode)." : null;
            items.Add(new PostingItem(voucher.Key, s.Posted, [Line(bank ?? "", "C"), Line(hosAcc ?? "", "D")],
                voucher.Select(r => S(r, "CASNumber")?.Trim() ?? "").Distinct().ToList(), why));
        }
        var total = items.Sum(i => i.Lines[0].Amount);
        return new PostingJournal(items, total, total, truncated);
    }

    public async Task<PostingResult> PostHospitalPaymentsAsync(HospitalPaymentPostRequest request, string user, CancellationToken ct = default)
    {
        var search = request.Search ?? throw new BusinessRuleException("Please search the records first.");
        if (search.Posted) throw new BusinessRuleException("Record(s) has been posted ! ");
        var (period, chosen) = await PrepareAsync(user, request.Keys, request.AccountPeriod, () => SearchHospitalPaymentsAsync(search, ct), ct);

        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        foreach (var item in chosen)
            if (await ws.ScalarAsync<int>("SELECT COUNT(*) FROM SUNPosting WITH (UPDLOCK, HOLDLOCK) WHERE TransRef = @key AND PostType = 'PayHosp'", new { key = item.Key }, ct) > 0)
                throw BusinessRuleException.Conflict($"{item.Key} has been posted meanwhile. Please search again.");
        var batchNo = await NextBatchAsync(ws, ct);
        foreach (var item in chosen)
            await ws.ExecuteAsync("""
                INSERT INTO SUNPosting (TransRef, PayHStatus, PostStatus, PostType, PostDate, PostBy, AccMonth, AccYear, BatchNo)
                VALUES (@key, '1', '1', 'PayHosp', CAST(GETDATE() AS date), @user, @month, @year, @batchNo)
                """, new { key = item.Key, user, month = period.Month, year = period.Year, batchNo }, ct);
        await ws.CommitAsync(ct);

        audit.Record(new AuditEntry("Posting.PaymentHospital", "SUNPosting", batchNo, user, new Dictionary<string, object?>
        {
            ["accountPeriod"] = request.AccountPeriod, ["items"] = chosen.Select(i => i.Key).ToList(), ["amount"] = chosen.Sum(i => i.Lines[0].Amount),
        }));
        var posted = await SearchHospitalPaymentsAsync(new HospitalPaymentPostingSearch(Posted: true, BatchFrom: batchNo, BatchTo: batchNo), ct);
        return new PostingResult(batchNo, chosen.Count, chosen.Count, posted);
    }

    // ---- Receipt > Payor (RecPayor) ---------------------------------------------------------------------------------------------------------------

    public async Task<PostingJournal> SearchPayorReceiptsAsync(PayorReceiptPostingSearch search, CancellationToken ct = default)
    {
        if (PostingRules.Validate(search) is { } problem) throw new BusinessRuleException(problem);
        var s = search;
        var where = new List<string> { "ISNULL(ReceiptNo, '') <> ''", "ReceiptAmt <> 0" };
        void Add(bool when, string sql) { if (when) where.Add(sql); }
        Add(s.ReceiptDateFrom is not null, "ReceiptDate >= @dFrom");
        Add(s.ReceiptDateTo is not null, "ReceiptDate < @dTo");
        Add(Blank(s.ReceiptNoFrom) is not null, "ReceiptNo >= @noFrom");
        Add(Blank(s.ReceiptNoTo) is not null, "ReceiptNo <= @noTo");
        var receipts = await QueryAsync(GenesisDb.Payment, $"""
            SELECT ReceiptNo, ReceiptDate, ReceiptAmt, CASNumber FROM ReceiptfrPAY WHERE {string.Join(" AND ", where)} ORDER BY ReceiptNo, CASNumber
            """, new { dFrom = At(s.ReceiptDateFrom), dTo = At(s.ReceiptDateTo?.AddDays(1)), noFrom = Blank(s.ReceiptNoFrom), noTo = Blank(s.ReceiptNoTo) }, ct, timeoutSeconds: 300);
        var byReceipt = receipts.GroupBy(r => S(r, "ReceiptNo")!.Trim(), StringComparer.OrdinalIgnoreCase).ToList();
        var keys = byReceipt.Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
        var postings = (keys.Count == 0 ? [] : await QueryAsync(GenesisDb.Worksheet,
            "SELECT TransRef, RecPayStatus, PostStatus, PostDate, AccMonth, AccYear, BatchNo FROM SUNPosting WHERE PostType = 'RecPayor' AND TransRef >= @lo AND TransRef <= @hi",
            new { lo = keys[0], hi = keys[^1] }, ct)).Where(r => S(r, "TransRef") is not null).ToLookup(r => S(r, "TransRef")!.Trim(), StringComparer.OrdinalIgnoreCase);
        var wanted = byReceipt.Where(g => Wanted(postings[g.Key], "RecPayStatus", s.Posted, s.PostDateFrom, s.PostDateTo, s.BatchFrom, s.BatchTo)).ToList();
        var people = await PeopleAsync(wanted.SelectMany(g => g).Select(r => S(r, "CASNumber")?.Trim()).OfType<string>(), ct);
        var payorAccounts = (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PAYAccCode FROM PAY", null, ct)).Where(r => S(r, "PAYCode") is not null)
            .GroupBy(r => S(r, "PAYCode")!.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => Blank(S(g.First(), "PAYAccCode")), StringComparer.OrdinalIgnoreCase);

        var items = new List<PostingItem>();
        var truncated = false;
        foreach (var receipt in wanted)
        {
            // A receipt can cover several cases (the receipt amount is on each of them); it is one item for the receipt amount.
            var cases = receipt.Select(r => S(r, "CASNumber")?.Trim()).OfType<string>().Select(c => people.GetValueOrDefault(c)).OfType<Person>().ToList();
            // Legacy view: Mediholdings (MH) and ZT receipts are never posted.
            if (cases.Count == 0 || cases.Any(p => p.Payor is "MH" or "ZT") || !cases.Any(p => Matches(p, s.Payor, null))) continue;
            if (items.Count == PostingRules.MaxItems) { truncated = true; break; }
            var first = receipt.First();
            var posting = s.Posted ? postings[receipt.Key].First(r => IsPosted(r, "RecPayStatus")) : null;
            var person = cases.FirstOrDefault(p => Matches(p, s.Payor, null)) ?? cases[0];
            var bank = PostingRules.ReceiptBankAccount(receipt.Key);
            var payorAcc = payorAccounts.GetValueOrDefault(person.Payor);
            var amount = receipt.Max(r => Dec(r, "ReceiptAmt") ?? 0);
            var period = posting is null ? "" : PostingRules.Period(S(posting, "AccMonth"), S(posting, "AccYear"));
            JournalLine Line(string acc, string dc) => new("CR", "", "MED", receipt.Key, period == "" ? null : period, Day(first, "ReceiptDate"), person.Payor, acc, amount, dc,
                null, null, person.Payor, null, person.ProductCategory, null, posting is null ? null : S(posting, "BatchNo")?.Trim());
            var why = bank is null ? $"No bank account code for receipts starting {receipt.Key[..Math.Min(2, receipt.Key.Length)]} (legacy: RA, RB, RC)."
                : payorAcc is null ? $"Payor {person.Payor} has no account code (PAYAccCode)." : null;
            items.Add(new PostingItem(receipt.Key, s.Posted, [Line(bank ?? "", "D"), Line(payorAcc ?? "", "C")],
                receipt.Select(r => S(r, "CASNumber")?.Trim() ?? "").Distinct().ToList(), why));
        }
        var total = items.Sum(i => i.Lines[0].Amount);
        return new PostingJournal(items, total, total, truncated);
    }

    public async Task<PostingResult> PostPayorReceiptsAsync(PayorReceiptPostRequest request, string user, CancellationToken ct = default)
    {
        var search = request.Search ?? throw new BusinessRuleException("Please search the records first.");
        if (search.Posted) throw new BusinessRuleException("Record(s) has been posted ! ");
        var (period, chosen) = await PrepareAsync(user, request.Keys, request.AccountPeriod, () => SearchPayorReceiptsAsync(search, ct), ct);

        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        foreach (var item in chosen)
            if (await ws.ScalarAsync<int>("SELECT COUNT(*) FROM SUNPosting WITH (UPDLOCK, HOLDLOCK) WHERE TransRef = @key AND PostType = 'RecPayor'", new { key = item.Key }, ct) > 0)
                throw BusinessRuleException.Conflict($"{item.Key} has been posted meanwhile. Please search again.");
        var batchNo = await NextBatchAsync(ws, ct);
        // One row per receipt; the legacy form wrote one for each ticked line, so a receipt usually got two.
        foreach (var item in chosen)
            await ws.ExecuteAsync("""
                INSERT INTO SUNPosting (TransRef, RecPayStatus, PostStatus, PostType, PostDate, PostBy, AccMonth, AccYear, BatchNo)
                VALUES (@key, '1', '1', 'RecPayor', CAST(GETDATE() AS date), @user, @month, @year, @batchNo)
                """, new { key = item.Key, user, month = period.Month, year = period.Year, batchNo }, ct);
        await ws.CommitAsync(ct);

        audit.Record(new AuditEntry("Posting.ReceiptPayor", "SUNPosting", batchNo, user, new Dictionary<string, object?>
        {
            ["accountPeriod"] = request.AccountPeriod, ["items"] = chosen.Select(i => i.Key).ToList(), ["amount"] = chosen.Sum(i => i.Lines[0].Amount),
        }));
        var posted = await SearchPayorReceiptsAsync(new PayorReceiptPostingSearch(Posted: true, BatchFrom: batchNo, BatchTo: batchNo), ct);
        return new PostingResult(batchNo, chosen.Count, chosen.Count, posted);
    }

    // ---- Bordx > Payor (Bordx2PaySum) -------------------------------------------------------------------------------------------------------------

    public async Task<PostingJournal> SearchBordxPayorAsync(BordxPayorPostingSearch search, CancellationToken ct = default)
    {
        if (PostingRules.Validate(search) is { } problem) throw new BusinessRuleException(problem);
        var s = search;
        var where = new List<string> { "ISNULL(cl.BordxRefNo, '') <> ''" };
        void Add(bool when, string sql) { if (when) where.Add(sql); }
        Add(s.BordxDateFrom is not null, "b.BordxDate >= @dFrom");
        Add(s.BordxDateTo is not null, "b.BordxDate < @dTo");
        Add(Blank(s.BordxNoFrom) is not null, "cl.BordxRefNo >= @noFrom");
        Add(Blank(s.BordxNoTo) is not null, "cl.BordxRefNo <= @noTo");
        var claims = await QueryAsync(GenesisDb.Worksheet, $"""
            SELECT cl.BordxRefNo, b.BordxDate, cl.CASNumber, cl.ClaimVersion, cl.CLMBordxAmt, cl.CLMCaseMgmtFees, cl.CLMPayableByMedix2H
            FROM Claim cl JOIN Bordx b ON b.BordxRefNo = cl.BordxRefNo
            WHERE {string.Join(" AND ", where)} ORDER BY cl.BordxRefNo, cl.CASNumber, cl.ClaimVersion
            """, new { dFrom = At(s.BordxDateFrom), dTo = At(s.BordxDateTo?.AddDays(1)), noFrom = Blank(s.BordxNoFrom), noTo = Blank(s.BordxNoTo) }, ct, timeoutSeconds: 300);
        var byBordx = claims.GroupBy(r => S(r, "BordxRefNo")!.Trim(), StringComparer.OrdinalIgnoreCase).ToList();
        var keys = byBordx.Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
        var postings = (keys.Count == 0 ? [] : await QueryAsync(GenesisDb.Worksheet,
            "SELECT TransRef, BordxPayStatus, PostStatus, PostDate, AccMonth, AccYear, BatchNo FROM SUNPosting WHERE PostType = 'Bordx2PaySum' AND TransRef >= @lo AND TransRef <= @hi",
            new { lo = keys[0], hi = keys[^1] }, ct)).Where(r => S(r, "TransRef") is not null).ToLookup(r => S(r, "TransRef")!.Trim(), StringComparer.OrdinalIgnoreCase);
        var wanted = byBordx.Where(g => Wanted(postings[g.Key], "BordxPayStatus", s.Posted, s.PostDateFrom, s.PostDateTo, s.BatchFrom, s.BatchTo)).ToList();
        var people = await PeopleAsync(wanted.SelectMany(g => g).Select(r => S(r, "CASNumber")?.Trim()).OfType<string>(), ct);
        var payorAccounts = (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PAYAccCode FROM PAY", null, ct)).Where(r => S(r, "PAYCode") is not null)
            .GroupBy(r => S(r, "PAYCode")!.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => Blank(S(g.First(), "PAYAccCode")), StringComparer.OrdinalIgnoreCase);
        var hospitals = (await InChunksAsync(GenesisDb.Main, "SELECT HOSCode, HOSAccCode FROM HOS WHERE HOSCode IN @keys",
            people.Values.Select(p => p.Hospital).OfType<string>(), ct)).GroupBy(r => S(r, "HOSCode")!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Blank(S(g.First(), "HOSAccCode")), StringComparer.OrdinalIgnoreCase);

        var items = new List<PostingItem>();
        var truncated = false;
        foreach (var bordx in wanted)
        {
            // Legacy view: health type N (Sihat Malaysia goes to Bordx > MNRB); payors XW, MH and ZT are never posted.
            var rows = bordx.Select(r => (Row: r, Person: people.GetValueOrDefault(S(r, "CASNumber")?.Trim() ?? "")))
                .Where(x => x.Person is { HealthType: "N" } p && p.Payor is not ("XW" or "MH" or "ZT")).ToList();
            if (rows.Count == 0 || !rows.Any(x => Matches(x.Person!, s.Payor, null))) continue;
            if (items.Count == PostingRules.MaxItems) { truncated = true; break; }
            var posting = s.Posted ? postings[bordx.Key].First(r => IsPosted(r, "BordxPayStatus")) : null;
            var period = posting is null ? "" : PostingRules.Period(S(posting, "AccMonth"), S(posting, "AccYear"));
            var lines = new List<JournalLine>();
            string? why = null;
            foreach (var (row, person) in rows)
            {
                var p = person!;
                var caseNo = S(row, "CASNumber")!.Trim();
                var amount = Dec(row, "CLMBordxAmt") ?? 0;
                var fees = Dec(row, "CLMCaseMgmtFees") ?? 0;
                // The hospital column: the case's hospital account (blank when it has none) when the claim pays a hospital, "0R" (member) otherwise.
                var hospital = (Dec(row, "CLMPayableByMedix2H") ?? 0) == 0 ? "0R"
                    : p.Hospital is { } h && hospitals.GetValueOrDefault(h) is { Length: > 1 } acc ? acc[1..Math.Min(7, acc.Length)] : "";
                var payorAcc = payorAccounts.GetValueOrDefault(p.Payor);
                why ??= payorAcc is null ? $"Payor {p.Payor} has no account code (PAYAccCode)." : null;
                JournalLine Line(string accCode, decimal amt, string dc, string description) => new("BI", "", "MED", bordx.Key, period == "" ? null : period, Day(row, "BordxDate"),
                    PostingRules.Description(description), accCode, amt, dc, hospital, null, p.Payor, PostingRules.CaseRef(caseNo), p.ProductCategory,
                    bordx.Key.Length > 3 ? bordx.Key.Substring(3, Math.Min(5, bordx.Key.Length - 3)) : null, posting is null ? null : S(posting, "BatchNo")?.Trim());
                // Legacy BordxPayListing, per claim: debit the payor's account for the bordereau amount, credit 6000 + payor for it less the case
                // management fees and 1030 for the fees.
                lines.Add(Line(payorAcc ?? "", amount, "D", p.PatientName ?? ""));
                lines.Add(Line("6000" + p.Payor, amount - fees, "C", p.PatientName ?? ""));
                if (fees != 0) lines.Add(Line("1030", fees, "C", $"{caseNo}-{S(row, "ClaimVersion")?.Trim()}"));
            }
            items.Add(new PostingItem(bordx.Key, s.Posted, lines, rows.Select(x => S(x.Row, "CASNumber")?.Trim() ?? "").Distinct().ToList(), why));
        }
        return new PostingJournal(items, items.Sum(i => i.Lines.Where(l => l.DebitCredit == "D").Sum(l => l.Amount)),
            items.Sum(i => i.Lines.Where(l => l.DebitCredit == "C").Sum(l => l.Amount)), truncated);
    }

    public async Task<PostingResult> PostBordxPayorAsync(BordxPayorPostRequest request, string user, CancellationToken ct = default)
    {
        var search = request.Search ?? throw new BusinessRuleException("Please search the records first.");
        if (search.Posted) throw new BusinessRuleException("Record(s) has been posted ! ");
        var (period, chosen) = await PrepareAsync(user, request.Keys, request.AccountPeriod, () => SearchBordxPayorAsync(search, ct), ct);

        await using var ws = await Db.BeginSessionAsync(GenesisDb.Worksheet, ct);
        foreach (var item in chosen)
            if (await ws.ScalarAsync<int>("SELECT COUNT(*) FROM SUNPosting WITH (UPDLOCK, HOLDLOCK) WHERE TransRef = @key AND PostType = 'Bordx2PaySum'", new { key = item.Key }, ct) > 0)
                throw BusinessRuleException.Conflict($"{item.Key} has been posted meanwhile. Please search again.");
        var batchNo = await NextBatchAsync(ws, ct);
        foreach (var item in chosen)
            await ws.ExecuteAsync("""
                INSERT INTO SUNPosting (TransRef, BordxPayStatus, PostStatus, PostType, PostDate, PostBy, AccMonth, AccYear, BatchNo)
                VALUES (@key, '1', '1', 'Bordx2PaySum', CAST(GETDATE() AS date), @user, @month, @year, @batchNo)
                """, new { key = item.Key, user, month = period.Month, year = period.Year, batchNo }, ct);
        await ws.CommitAsync(ct);

        audit.Record(new AuditEntry("Posting.BordxPayor", "SUNPosting", batchNo, user, new Dictionary<string, object?>
        {
            ["accountPeriod"] = request.AccountPeriod, ["items"] = chosen.Select(i => i.Key).ToList(),
            ["amount"] = chosen.Sum(i => i.Lines.Where(l => l.DebitCredit == "D").Sum(l => l.Amount)),
        }));
        var posted = await SearchBordxPayorAsync(new BordxPayorPostingSearch(Posted: true, BatchFrom: batchNo, BatchTo: batchNo), ct);
        return new PostingResult(batchNo, chosen.Count, chosen.Count, posted);
    }
}
