using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

internal sealed class FwhsInvoiceRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IFwhsInvoiceRepository
{
    private const int Max = 2000;
    private static string T(string? s) => s?.Trim() ?? "";
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d ? DateOnly.FromDateTime(d) : null;

    /// <summary>Which rows a "new" run covers: billing mode I uses the policy date (new business) and the endorsement date (endorsements); other payors use the submission date for both.</summary>
    private static string NewScope(bool policyDateMode) => policyDateMode
        ? "((f.CoEndorseNo IS NULL AND f.CoPolicyDate >= @from AND f.CoPolicyDate < DATEADD(day, 1, @to)) OR (f.CoEndorseNo IS NOT NULL AND f.CoEndorseDate >= @from AND f.CoEndorseDate < DATEADD(day, 1, @to)))"
        : "(f.CoPAYDateSubmission >= @from AND f.CoPAYDateSubmission < DATEADD(day, 1, @to))";

    private async Task<(string Name, string Billing, bool MedixTax)> PayorAsync(IDbSession? session, string payor, CancellationToken ct)
    {
        const string sql = "SELECT PAYCompanyName, PAYBillingMode, PAYMedixTax FROM PAY WHERE PAYCode = @payor";
        var rows = session is null ? await QueryAsync(GenesisDb.Main, sql, new { payor }, ct) : await session.QueryAsync(sql, new { payor }, ct);
        var r = rows.FirstOrDefault() ?? throw BusinessRuleException.NotFound($"Payor '{payor}' does not exist.");
        return (T(S(r, "PAYCompanyName")), T(S(r, "PAYBillingMode")), T(S(r, "PAYMedixTax")) == "Y");
    }

    private static string Select(string tax, bool medixTax) => $"""
        SELECT TOP ({Max + 1}) CASE WHEN f.CoEndorseNo IS NULL THEN 'New business' ELSE 'Endorsement' END AS Kind, f.CoPolicyNo, f.CoPolicyDate, f.CoEndorseDate, f.CoPAYDateSubmission, f.CoPAYBatchNo,
               f.CoInvoiceNumber, f.CoInvoiceDate, f.CoInvoiceBy, SUM(f.CoTotalFW) AS Workers, SUM(f.CoTotalMCOFee) AS Fee, SUM(f.CoTPCATax) AS PcaTax, SUM(ISNULL(f.GSTAmount, 0)) AS Gst
        FROM FWHSTPAFee f
        """;

    private static FwhsInvoiceListing Build(string name, string billing, bool medixTax, string tax, List<IDictionary<string, object?>> data, bool previewing)
    {
        var truncated = data.Count > Max; if (truncated) data = data.Take(Max).ToList();
        var rows = data.Select(r =>
        {
            var fee = Dec(r, "Fee") ?? 0;
            var taxAmount = tax == FwhsTax.None ? (medixTax ? Dec(r, "PcaTax") ?? 0 : 0) : previewing && T(S(r, "CoInvoiceNumber")).Length == 0 ? Math.Round(fee * FwhsTax.Rate(tax), 2) : Dec(r, "Gst") ?? 0;
            var date = billing == "I" ? (S(r, "Kind") == "Endorsement" ? Day(r, "CoEndorseDate") : Day(r, "CoPolicyDate")) : Day(r, "CoPAYDateSubmission");
            return new FwhsInvoiceRow(S(r, "Kind") ?? "", T(S(r, "CoPolicyNo")), date, T(S(r, "CoPAYBatchNo")), I(r, "Workers") ?? 0, fee, taxAmount, fee + taxAmount, T(S(r, "CoInvoiceNumber")), Day(r, "CoInvoiceDate"), T(S(r, "CoInvoiceBy")));
        }).ToList();
        return new FwhsInvoiceListing(name, billing, medixTax, rows, rows.Sum(x => x.Workers), rows.Sum(x => x.Fee), rows.Sum(x => x.Tax), rows.Sum(x => x.Total), truncated);
    }

    private const string GroupBy = " GROUP BY f.CoEndorseNo, f.CoPolicyNo, f.CoPolicyDate, f.CoEndorseDate, f.CoPAYDateSubmission, f.CoPAYBatchNo, f.CoInvoiceNumber, f.CoInvoiceDate, f.CoInvoiceBy ORDER BY f.CoPolicyNo, f.CoPAYBatchNo";

    public async Task<FwhsInvoiceListing> ListAsync(FwhsInvoiceFilter f, CancellationToken ct = default)
    {
        if (FwhsInvoiceRules.Validate(f) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var (payor, tax) = (T(f.Payor), f.Tax ?? FwhsTax.None);
        var (name, billing, medixTax) = await PayorAsync(null, payor, ct);
        var sql = Select(tax, medixTax) + " WHERE f.CoGRPPaycode = @payor AND " + (f.Mode == "reprint" ? "f.CoInvoiceNumber = @invoice" : $"(f.CoInvoiceNumber IS NULL OR f.CoInvoiceNumber = '') AND {NewScope(billing == "I")}") + GroupBy;
        var data = (await QueryAsync(GenesisDb.Main, sql, new { payor, invoice = T(f.InvoiceNo), from = At(f.From), to = At(f.To) }, ct)).ToList();
        return Build(name, billing, medixTax, tax, data, previewing: f.Mode == "new");
    }

    public async Task<FwhsInvoiceResult> InvoiceAsync(FwhsInvoiceFilter f, string user, CancellationToken ct = default)
    {
        if (f.Mode != "new") throw new BusinessRuleException("An invoice number is only generated for Print New.", field: "mode");
        if (FwhsInvoiceRules.Validate(f) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var (payor, tax) = (T(f.Payor), f.Tax ?? FwhsTax.None);
        string invoiceNo; int updated;
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            var (_, billing, _) = await PayorAsync(s, payor, ct);
            var scope = NewScope(billing == "I");
            var pending = await s.ScalarAsync<int>($"SELECT COUNT(*) FROM FWHSTPAFee f WHERE f.CoGRPPaycode = @payor AND (f.CoInvoiceNumber IS NULL OR f.CoInvoiceNumber = '') AND {scope}", new { payor, from = At(f.From), to = At(f.To) }, ct);
            if (pending == 0) throw new BusinessRuleException("There are no uninvoiced fees for this payor and date range.");

            var current = (await s.QueryAsync("SELECT TOP 1 MCOInvoice_No FROM MBMMCOINVQ WITH (UPDLOCK, HOLDLOCK)", null, ct)).Select(r => T(S(r, "MCOInvoice_No"))).FirstOrDefault();
            string number;
            if (current is null) { number = "10001"; await s.ExecuteAsync("INSERT INTO MBMMCOINVQ (MCOInvoice_No) VALUES (@number)", new { number }, ct); }
            else
            {
                number = (int.Parse(current) + 1).ToString("00000");
                await s.ExecuteAsync("UPDATE MBMMCOINVQ SET MCOInvoice_No = @number", new { number }, ct);
            }
            invoiceNo = "FWMA" + number;

            var gst = tax == FwhsTax.None ? "" : ", f.GSTAmount = f.CoTotalMCOFee * @rate";
            updated = await s.ExecuteAsync($"UPDATE f SET f.CoInvoiceDate = @today, f.CoInvoiceNumber = @invoiceNo, f.CoInvoiceBy = @user{gst} FROM FWHSTPAFee f WHERE f.CoGRPPaycode = @payor AND (f.CoInvoiceNumber IS NULL OR f.CoInvoiceNumber = '') AND {scope}",
                new { payor, from = At(f.From), to = At(f.To), today = DateTime.Today, invoiceNo, user, rate = FwhsTax.Rate(tax) }, ct);
            await s.CommitAsync(ct);
        }
        audit.Record(new AuditEntry("Mco.FwhsInvoice", "FWHSTPAFee", invoiceNo, user, new Dictionary<string, object?> { ["payor"] = payor, ["from"] = f.From, ["to"] = f.To, ["tax"] = tax, ["rows"] = updated }));
        var listing = await ListAsync(new FwhsInvoiceFilter(payor, "reprint", null, null, invoiceNo, tax), ct);
        return new FwhsInvoiceResult(invoiceNo, updated, listing);
    }
}
