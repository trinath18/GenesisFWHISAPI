using GenesisFIS.Core.Data;
using GenesisFIS.Core.Payments;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Payments;

/// <summary>
/// Payment > Payment Tracking (legacy FrmPaymentTracking, stored procedure PaymentTracking on VIEW_PaymentHOS_Tracking). The vouchers are read from
/// Payment2HOS; the member, hospital, payor and bordereau come from the main and worksheet databases by case number (the legacy view joined the HIS
/// databases, empty for FWHS). Filters on those columns first pick the case numbers in their own database, so a search never reads every voucher.
/// </summary>
internal sealed class PaymentTrackingRepository(IDbConnectionFactory connections) : SqlRepository(connections), IPaymentTrackingRepository
{
    private const int Chunk = 1000;
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static (string, string) Key(string no, string? version) => (no.Trim().ToUpperInvariant(), version?.Trim() ?? "");

    public async Task<PaymentTrackingOptions> OptionsAsync(CancellationToken ct = default)
    {
        var payors = (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PAYCompanyName FROM PAY WHERE ISNULL(PAYCode, '') <> '' ORDER BY PAYCode", null, ct))
            .Select(r => new PaymentTrackingOption(S(r, "PAYCode")!, S(r, "PAYCompanyName") ?? "")).ToList();
        var hospitals = (await QueryAsync(GenesisDb.Main, "SELECT HOSCode, HOSName FROM HOS WHERE ISNULL(HOSName, '') <> '' ORDER BY HOSName", null, ct))
            .Select(r => new PaymentTrackingOption(S(r, "HOSCode")!, S(r, "HOSName")!)).ToList();
        return new PaymentTrackingOptions(payors, hospitals);
    }

    private async Task<List<IDictionary<string, object?>>> InChunksAsync(GenesisDb db, string sql, IEnumerable<string> keys, CancellationToken ct, object? extra = null)
    {
        var all = new List<IDictionary<string, object?>>();
        foreach (var chunk in keys.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(Chunk))
        {
            var args = new Dictionary<string, object?> { ["keys"] = chunk };
            if (extra is Dictionary<string, object?> more) foreach (var (k, v) in more) args[k] = v;
            all.AddRange(await QueryAsync(db, sql, args, ct));
        }
        return all;
    }

    private static void Range(List<string> where, Dictionary<string, object?> args, string column, string name, object? from, object? to, bool dates = false)
    {
        if (from is not null) { args[name + "From"] = from; where.Add($"{column} >= @{name}From"); }
        if (to is not null) { args[name + "To"] = to; where.Add(dates ? $"{column} < DATEADD(day, 1, @{name}To)" : $"{column} <= @{name}To"); }
    }

    public async Task<PaymentTrackingResult> SearchAsync(PaymentTrackingSearch s, CancellationToken ct = default)
    {
        var report = PaymentTrackingReport.Normalise(s.Report);
        var max = report == PaymentTrackingReport.General ? PaymentTrackingReport.MaxLines : PaymentTrackingReport.MaxGrouped;

        // Case numbers picked by the member / hospital filters (main database) and the bordereau / DN-CN filters (worksheet database).
        HashSet<string>? keys = null;
        void Narrow(IEnumerable<string?> found) { var set = found.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase); if (keys is null) keys = set; else keys.IntersectWith(set); }
        var memberNo = Blank(s.MemberNo); var memberName = Blank(s.MemberName); var hospital = Blank(s.Hospital);
        if (memberName is not null)
        {
            var named = (await QueryAsync(GenesisDb.Main, "SELECT TOP 5001 MBMNumber FROM MBM WHERE MBMName LIKE @name", new { name = "%" + memberName.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%" }, ct))
                .Select(r => S(r, "MBMNumber")).OfType<string>().ToList();
            Narrow((await InChunksAsync(GenesisDb.Main, "SELECT CASNumber FROM CAS WHERE MBMNumber IN @keys", named, ct)).Select(r => S(r, "CASNumber")));
        }
        if (memberNo is not null || hospital is not null)
        {
            var mw = new List<string>(); var ma = new Dictionary<string, object?>();
            if (memberNo is not null) { ma["member"] = memberNo; mw.Add("MBMNumber = @member"); }
            if (hospital is not null) { ma["hos"] = hospital; mw.Add("CASHOSCode = @hos"); }
            Narrow((await QueryAsync(GenesisDb.Main, $"SELECT CASNumber FROM CAS WHERE {string.Join(" AND ", mw)}", ma, ct)).Select(r => S(r, "CASNumber")));
        }
        var ww = new List<string>(); var wa = new Dictionary<string, object?>();
        Range(ww, wa, "c.BordxRefNo", "bordx", Blank(s.BordxNoFrom), Blank(s.BordxNoTo));
        Range(ww, wa, "b.BordxDate", "bordxDate", At(s.BordxDateFrom), At(s.BordxDateTo), true);
        Range(ww, wa, "c.DebitCreditRefNo", "dncn", Blank(s.DnCnFrom), Blank(s.DnCnTo));
        if (ww.Count > 0)
            Narrow((await QueryAsync(GenesisDb.Worksheet, $"SELECT DISTINCT c.CASNumber FROM Claim c LEFT JOIN Bordx b ON b.BordxRefNo = c.BordxRefNo WHERE {string.Join(" AND ", ww)}", wa, ct))
                .Select(r => S(r, "CASNumber")));

        var pw = new List<string>(); var pa = new Dictionary<string, object?> { ["take"] = max + 1 };
        Range(pw, pa, "PVHOSNo", "pv", Blank(s.PvNoFrom), Blank(s.PvNoTo));
        Range(pw, pa, "PVHOSDate", "pvDate", At(s.PvDateFrom), At(s.PvDateTo), true);
        Range(pw, pa, "HOSPaidAmt", "amount", s.AmountFrom, s.AmountTo);
        Range(pw, pa, "CASNumber", "claim", Blank(s.ClaimNoFrom), Blank(s.ClaimNoTo));
        Range(pw, pa, "TRY_CONVERT(int, HOSAccPeriodMth)", "month", s.AccountMonthFrom, s.AccountMonthTo);
        Range(pw, pa, "TRY_CONVERT(int, HOSAccPeriodYr)", "year", s.AccountYearFrom, s.AccountYearTo);
        if (Blank(s.Status) is { } status) { pa["status"] = status; pw.Add("PVPayHOSStatus = @status"); }
        var narrowing = pw.Count - (Blank(s.Status) is null ? 0 : 1);
        if (Blank(s.Payor) is { } payor) { pa["payor"] = payor; pw.Add("LEFT(CASNumber, 2) = @payor"); narrowing++; }
        if (Blank(s.HealthType) is { } hlt)
        {
            pa["hltPayors"] = (await QueryAsync(GenesisDb.Main, "SELECT PAYCode FROM PAY WHERE HLTCode = @hlt", new { hlt }, ct)).Select(r => S(r, "PAYCode")).OfType<string>().ToArray();
            pw.Add("LEFT(CASNumber, 2) IN @hltPayors");
        }
        if (keys is null && narrowing == 0)
            throw new BusinessRuleException("Please narrow the search (PV, claim, member, hospital, payor, bordereau, amount or account period).", field: "search");

        var sql = $"""
            SELECT TOP (@take) PVHOSNo, PVHOSDate, HOSPaidAmt, CASNumber, ClaimVersion, HOSRemark, PVPayHOSStatus, HOSAccPeriodMth, HOSAccPeriodYr
            FROM Payment2HOS WHERE {(pw.Count == 0 ? "1 = 1" : string.Join(" AND ", pw))} {(keys is null ? "" : "AND CASNumber IN @keys")}
            ORDER BY PVHOSDate, PVHOSNo, CASNumber
            """;
        var vouchers = keys is null ? await QueryAsync(GenesisDb.Payment, sql, pa, ct) : await InChunksAsync(GenesisDb.Payment, sql, keys, ct, pa);
        var truncated = vouchers.Count > max;
        vouchers = vouchers.Take(max).ToList();

        var cases = vouchers.Select(r => S(r, "CASNumber")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var claims = (await InChunksAsync(GenesisDb.Worksheet, "SELECT c.CASNumber, c.ClaimVersion, c.BordxRefNo, c.DebitCreditRefNo, b.BordxDate FROM Claim c LEFT JOIN Bordx b ON b.BordxRefNo = c.BordxRefNo WHERE c.CASNumber IN @keys", cases, ct))
            .GroupBy(r => Key(S(r, "CASNumber")!, S(r, "ClaimVersion"))).ToDictionary(g => g.Key, g => g.First());
        var cas = (await InChunksAsync(GenesisDb.Main, "SELECT CASNumber, MBMNumber, CASHOSCode FROM CAS WHERE CASNumber IN @keys", cases, ct))
            .GroupBy(r => S(r, "CASNumber")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var members = (await InChunksAsync(GenesisDb.Main, "SELECT MBMNumber, MBMName, PAYCode, HLTCode FROM MBM WHERE MBMNumber IN @keys", cas.Values.Select(r => S(r, "MBMNumber")).OfType<string>(), ct))
            .GroupBy(r => S(r, "MBMNumber")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var hospitals = (await InChunksAsync(GenesisDb.Main, "SELECT HOSCode, HOSName FROM HOS WHERE HOSCode IN @keys", cas.Values.Select(r => S(r, "CASHOSCode")).OfType<string>(), ct))
            .GroupBy(r => S(r, "HOSCode")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => S(g.First(), "HOSName"), StringComparer.OrdinalIgnoreCase);

        var lines = new List<PaymentTrackingLine>();
        foreach (var v in vouchers)
        {
            var no = S(v, "CASNumber")!; var version = S(v, "ClaimVersion") ?? "";
            claims.TryGetValue(Key(no, version), out var claim);
            cas.TryGetValue(no, out var cs);
            var mbm = cs is not null && S(cs, "MBMNumber") is { } m ? members.GetValueOrDefault(m) : null;
            var hosCode = cs is null ? null : S(cs, "CASHOSCode");
            lines.Add(new PaymentTrackingLine(S(v, "PVHOSNo"), Day(v, "PVHOSDate"), Dec(v, "HOSPaidAmt") ?? 0, cs is null ? null : S(cs, "MBMNumber"), mbm is null ? null : S(mbm, "MBMName"),
                claim is null ? null : S(claim, "BordxRefNo"), claim is null ? null : Day(claim, "BordxDate"), no, version, hosCode is null ? null : hospitals.GetValueOrDefault(hosCode),
                (mbm is null ? null : S(mbm, "PAYCode")) ?? (no.Length >= 2 ? no[..2] : null), mbm is null ? null : S(mbm, "HLTCode"), claim is null ? null : S(claim, "DebitCreditRefNo"),
                S(v, "HOSRemark"), S(v, "PVPayHOSStatus"), int.TryParse(S(v, "HOSAccPeriodMth"), out var mth) ? mth : null, int.TryParse(S(v, "HOSAccPeriodYr"), out var yr) ? yr : null));
        }
        lines = lines.OrderBy(l => l.PvDate).ThenBy(l => l.PvNo).ThenBy(l => l.CaseNumber).ToList();
        var groups = PaymentTrackingReport.Group(report, lines);
        return new PaymentTrackingResult(report, report == PaymentTrackingReport.General ? lines : [], groups, lines.Count, lines.Sum(l => l.Paid), truncated);
    }
}
