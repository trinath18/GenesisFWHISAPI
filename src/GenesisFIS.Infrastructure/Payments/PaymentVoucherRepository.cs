using GenesisFIS.Core.Data;
using GenesisFIS.Core.Payments;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Payments;

/// <summary>
/// The data of a payment voucher printout. Legacy read View_Print_PV (procedure Print_PaymentVoucher, payment database joining the worksheet and main
/// databases) with one row per claim invoice; here each database is read on its own and joined by case and version.
/// </summary>
internal sealed class PaymentVoucherRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IPaymentVoucherRepository
{
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static int Version(IDictionary<string, object?> r, string c) => int.TryParse(S(r, c)?.Trim(), out var v) ? v : 0;
    private static (string, int) Key(IDictionary<string, object?> r, string no, string version) => ((S(r, no) ?? "").Trim().ToUpperInvariant(), Version(r, version));
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<VoucherData?> LoadAsync(string pvNo, string printedBy, CancellationToken ct = default)
    {
        pvNo = pvNo.Trim();
        var pays = await QueryAsync(GenesisDb.Payment, """
            SELECT CASNumber, ClaimVersion, PVHOSDate, PVHOSChequeDate, PVBank, PVPayee, PVHOSChequeNo, PVHOSChequeNoMR, HOSPaidAmt
            FROM Payment2HOS WHERE PVHOSNo = @pvNo ORDER BY CASNumber, ClaimVersion
            """, new { pvNo }, ct);
        if (pays.Count == 0) return null;
        var keys = pays.Select(r => S(r, "CASNumber")!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var claims = (await QueryAsync(GenesisDb.Worksheet, "SELECT CASNumber, ClaimVersion, ClaimID, BordxRefNo, CLMBordxAmt FROM Claim WHERE CASNumber IN @keys", new { keys }, ct))
            .GroupBy(r => Key(r, "CASNumber", "ClaimVersion")).ToDictionary(g => g.Key, g => g.First());
        var approved = (await QueryAsync(GenesisDb.Worksheet, "SELECT CASNumber, CLMVersion, CLMMedical FROM ClaimApprove WHERE CASNumber IN @keys", new { keys }, ct))
            .GroupBy(r => Key(r, "CASNumber", "CLMVersion")).ToDictionary(g => g.Key, g => g.Sum(r => Dec(r, "CLMMedical") ?? 0));
        var invoices = (await QueryAsync(GenesisDb.Worksheet, """
            SELECT i.CASNumber, i.INVWSversion, i.InvNo, i.InvDate, ISNULL((SELECT SUM(a.CLMMedical) FROM ClaimInvoiceActual a WHERE a.INVIndex = i.INVIndex), 0) AS Medical
            FROM ClaimInvoice i WHERE i.CASNumber IN @keys ORDER BY i.InvDate, i.INVIndex
            """, new { keys }, ct)).ToLookup(r => Key(r, "CASNumber", "INVWSversion"));
        var receipts = (await QueryAsync(GenesisDb.Payment, "SELECT CASNumber, ClaimVersion, ReceiptNo, ReceiptDate FROM ReceiptfrPAY WHERE CASNumber IN @keys", new { keys }, ct))
            .GroupBy(r => Key(r, "CASNumber", "ClaimVersion")).ToDictionary(g => g.Key, g => g.OrderBy(r => D(r, "ReceiptDate")).Select(r => Clean(S(r, "ReceiptNo"))).LastOrDefault(x => x is not null));
        var cases = (await QueryAsync(GenesisDb.Main, "SELECT c.CASNumber, c.CASMRNNo, c.CASHOSCode, c.ClaimID, m.MBMName FROM CAS c LEFT JOIN MBM m ON m.MBMNumber = c.MBMNumber WHERE c.CASNumber IN @keys",
                new { keys }, ct))
            .GroupBy(r => S(r, "CASNumber")!.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var hosCode = cases.Values.Select(r => Clean(S(r, "CASHOSCode"))).FirstOrDefault(x => x is not null);
        var hos = hosCode is null ? null
            : (await QueryAsync(GenesisDb.Main, "SELECT HOSName, HOSAdd1, HOSAdd2, HOSAdd3 FROM HOS WHERE HOSCode = @hosCode", new { hosCode }, ct)).FirstOrDefault();

        var list = new List<VoucherClaim>();
        foreach (var p in pays)
        {
            var key = Key(p, "CASNumber", "ClaimVersion");
            claims.TryGetValue(key, out var c);
            cases.TryGetValue(key.Item1, out var cs);
            list.Add(new VoucherClaim(S(p, "CASNumber")!.Trim(), key.Item2, (c is null ? null : Clean(S(c, "ClaimID"))) ?? (cs is null ? null : Clean(S(cs, "ClaimID"))), cs is null ? null : Clean(S(cs, "CASMRNNo")),
                cs is null ? null : Clean(S(cs, "MBMName")), c is null ? null : Clean(S(c, "BordxRefNo")), receipts.GetValueOrDefault(key),
                Clean(S(p, "PVHOSChequeNo")), Clean(S(p, "PVHOSChequeNoMR")), c is null ? 0 : Dec(c, "CLMBordxAmt") ?? 0, approved.GetValueOrDefault(key),
                Dec(p, "HOSPaidAmt") ?? 0,
                invoices[key].Select(i => new VoucherInvoice(Clean(S(i, "InvNo")), Day(i, "InvDate"), Dec(i, "Medical") ?? 0)).ToList()));
        }
        var first = pays[0];
        return new VoucherData(pvNo, Day(first, "PVHOSDate"), Day(first, "PVHOSChequeDate"), Clean(S(first, "PVBank")), Clean(S(first, "PVPayee")),
            hos is null ? null : Clean(S(hos, "HOSName")), hos is null ? null : Clean(S(hos, "HOSAdd1")), hos is null ? null : Clean(S(hos, "HOSAdd2")),
            hos is null ? null : Clean(S(hos, "HOSAdd3")), list, DateOnly.FromDateTime(DateTime.Today), printedBy);
    }

    // ---- Cheques (legacy cmdPrintChq / frmPrintChequeRange, VIEW_Payment2HOS_Chq) ----

    /// <summary>Most cheques a range lists or prints at once.</summary>
    internal const int MaxCheques = 2_000;

    private const string ChequeColumns = """
        PVHOSNo, CASNumber, PVHOSChequeNo, PVHOSChequeDate, PVHOSChequeAmt, PVPayee, PVAmtWording, PVAmtWordingTwo, PVChequeStatus
        """;

    public async Task<IReadOnlyList<ChequeData>> ChequesAsync(string pvNo, CancellationToken ct = default)
    {
        var rows = await QueryAsync(GenesisDb.Payment, $"SELECT {ChequeColumns} FROM Payment2HOS WHERE PVHOSNo = @pvNo", new { pvNo = pvNo.Trim() }, ct);
        return await ToChequesAsync(rows, ct);
    }

    public async Task<IReadOnlyList<ChequeAccount>> ChequeAccountsAsync(CancellationToken ct = default)
    {
        var banks = await BanksAsync(ct);
        return (await QueryAsync(GenesisDb.Payment, "SELECT LEFT(PVHOSNo, 2) AS Acc, COUNT(DISTINCT PVHOSNo) AS N FROM Payment2HOS WHERE ISNULL(PVHOSNo, '') <> '' GROUP BY LEFT(PVHOSNo, 2) ORDER BY 1", null, ct))
            .Select(r => new ChequeAccount(S(r, "Acc")!.Trim().ToUpperInvariant(), banks.GetValueOrDefault(S(r, "Acc")!.Trim().ToUpperInvariant()), I(r, "N") ?? 0)).ToList();
    }

    public async Task<ChequeRangeListing> ChequeRangeAsync(ChequeRangeRequest request, CancellationToken ct = default)
    {
        if (ChequeRules.RangeProblem(request) is { } problem) throw new BusinessRuleException(problem);
        var cheques = await ToChequesAsync(await QueryAsync(GenesisDb.Payment, RangeSql, RangeArgs(request), ct), ct);
        var expected = ChequeRules.SeriesCount(request.ChequeNoFrom!, request.ChequeNoTo!);
        return new ChequeRangeListing(cheques, expected, ChequeRules.ListingProblem(cheques.Count, expected));
    }

    public async Task<IReadOnlyList<ChequeData>> PrintChequeRangeAsync(ChequeRangeRequest request, string user, CancellationToken ct = default)
    {
        if (ChequeRules.RangeProblem(request) is { } problem) throw new BusinessRuleException(problem);
        var args = RangeArgs(request);
        await using var pay = await Db.BeginSessionAsync(GenesisDb.Payment, ct);
        var rows = await pay.QueryAsync(RangeSql.Replace("FROM Payment2HOS", "FROM Payment2HOS WITH (UPDLOCK)", StringComparison.Ordinal), args, ct);
        var cheques = await ToChequesAsync(rows, ct);
        if (ChequeRules.ListingProblem(cheques.Count, ChequeRules.SeriesCount(request.ChequeNoFrom!, request.ChequeNoTo!)) is { } listing)
            throw new BusinessRuleException(listing);
        if (cheques.FirstOrDefault(c => ChequeRules.Problem(c) is not null) is { } bad)
            throw new BusinessRuleException($"Cheque {bad.ChequeNo}: {ChequeRules.Problem(bad)}");
        // Legacy ChequePrintingHOS: the range is marked printed after the cheques went to the printer (and drops out of the listing).
        var marked = await pay.ExecuteAsync("""
            UPDATE Payment2HOS SET PVChequeStatus = 'Y'
            WHERE LEFT(PVHOSNo, 2) = @acc AND PVHOSChequeNo >= @from AND PVHOSChequeNo <= @to AND ISNULL(PVChequeStatus, '') = ''
            """, args, ct);
        await pay.CommitAsync(ct);
        audit.Record(new AuditEntry("Payment.Hospital.PrintChequeRange", "Payment2HOS", $"{args.acc} {args.from}-{args.to}", user,
            new Dictionary<string, object?> { ["cheques"] = cheques.Count, ["rows"] = marked }));
        return cheques.Select(c => c with { Printed = true }).ToList();
    }

    private static readonly string RangeSql = $"""
        SELECT TOP ({MaxCheques * 5}) {ChequeColumns} FROM Payment2HOS
        WHERE LEFT(PVHOSNo, 2) = @acc AND PVHOSChequeNo >= @from AND PVHOSChequeNo <= @to AND ISNULL(PVChequeStatus, '') = ''
        ORDER BY PVHOSChequeNo, PVHOSNo
        """;

    /// <summary>The range as Dapper parameters (@acc, @from, @to).</summary>
    private sealed record RangeKey(string acc, string from, string to);

    private static RangeKey RangeArgs(ChequeRangeRequest r) => new(r.AccountType!.Trim().ToUpperInvariant(), r.ChequeNoFrom!.Trim(), r.ChequeNoTo!.Trim());

    private async Task<Dictionary<string, string?>> BanksAsync(CancellationToken ct) =>
        (await QueryAsync(GenesisDb.Payment, "SELECT PymtType, ChequeNo FROM ChequeType", null, ct))
            .Where(r => Clean(S(r, "PymtType")) is not null).GroupBy(r => S(r, "PymtType")!.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => Clean(S(g.First(), "ChequeNo")));

    /// <summary>
    /// Groups voucher rows into cheques as VIEW_Payment2HOS_Chq did (cheque no, date, amount, payee, wording). A voucher without a payee pays its
    /// hospital, so the payee defaults to the hospital of its first case; the wording defaults to the amount in words.
    /// </summary>
    private async Task<IReadOnlyList<ChequeData>> ToChequesAsync(IReadOnlyList<IDictionary<string, object?>> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var banks = await BanksAsync(ct);
        var cases = rows.Select(r => S(r, "CASNumber")?.Trim()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hospitals = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in cases.Chunk(1_000))
            foreach (var r in await QueryAsync(GenesisDb.Main, "SELECT c.CASNumber, h.HOSName FROM CAS c LEFT JOIN HOS h ON h.HOSCode = c.CASHOSCode WHERE c.CASNumber IN @chunk", new { chunk }, ct))
                hospitals[S(r, "CASNumber")!.Trim()] = Clean(S(r, "HOSName"));
        return rows
            .GroupBy(r => (No: Clean(S(r, "PVHOSChequeNo")) ?? "", Date: Day(r, "PVHOSChequeDate"), Amount: Dec(r, "PVHOSChequeAmt") ?? 0, Payee: Clean(S(r, "PVPayee")),
                W1: Clean(S(r, "PVAmtWording")), W2: Clean(S(r, "PVAmtWordingTwo")), Printed: Clean(S(r, "PVChequeStatus")) == "Y"))
            .Take(MaxCheques)
            .Select(g =>
            {
                var pvs = g.Select(r => S(r, "PVHOSNo")!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
                var acc = pvs[0].Length >= 2 ? pvs[0][..2].ToUpperInvariant() : pvs[0];
                var payee = g.Key.Payee ?? g.Select(r => hospitals.GetValueOrDefault(S(r, "CASNumber")?.Trim() ?? "")).FirstOrDefault(x => x is not null);
                var (w1, w2) = ChequeRules.Wording(g.Key.W1, g.Key.W2, g.Key.Amount);
                return new ChequeData(g.Key.No, g.Key.Date, g.Key.Amount, payee, w1, w2, acc, string.Join(", ", pvs), banks.GetValueOrDefault(acc), g.Key.Printed);
            })
            .ToList();
    }
}
