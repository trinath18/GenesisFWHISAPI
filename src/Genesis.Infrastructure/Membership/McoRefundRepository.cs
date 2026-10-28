using Genesis.Core.Data;
using Genesis.Core.Membership;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Membership;

internal sealed class McoRefundRepository(IDbConnectionFactory connections) : SqlRepository(connections), IMcoRefundRepository
{
    private const int Max = 2000;
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d ? DateOnly.FromDateTime(d) : null;
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static McoRefundRow Map(IDictionary<string, object?> r)
    {
        var effEnd = Day(r, "MBMENDtEffDate"); var payEff = Day(r, "MBMPayorEffDate"); var payExp = Day(r, "MBMPayorExpDate");
        var fee = Dec(r, "MCOFee"); var refund = Dec(r, "MCORefund");
        return new McoRefundRow(S(r, "MBMNumber")?.Trim() ?? "", S(r, "CoverId")?.Trim() ?? "", S(r, "MBMPolicyNo")?.Trim(), S(r, "MBMName")?.Trim(), S(r, "MBMIcBcPp")?.Trim(), S(r, "MBMStatus")?.Trim(),
            Day(r, "MBMBordxDate"), payEff, payExp, payEff is { } a && payExp is { } b ? b.DayNumber - a.DayNumber : null, effEnd, Day(r, "MBMENDtBordxDate"),
            payEff is { } c && effEnd is { } d ? d.DayNumber - c.DayNumber : null, payEff is { } e && effEnd is { } f ? (f.Year - e.Year) * 12 + f.Month - e.Month : null,
            fee, fee is not null && refund is not null ? Math.Round(fee.Value - refund.Value, 2) : null, refund is null ? null : Math.Round(refund.Value, 2), I(r, "TotalCase") ?? 0,
            Blank(S(r, "CRNote")), Day(r, "CRNoteDate"), Blank(S(r, "InvoiceNo")), Day(r, "InvoiceDate"));
    }

    public async Task<IReadOnlyList<McoPayor>> PayorsAsync(CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PAYCompanyName, MBMCancelStatus FROM PAY ORDER BY PAYCode", null, ct))
            .Select(r => new McoPayor(S(r, "PAYCode")?.Trim() ?? "", S(r, "PAYCompanyName")?.Trim(), string.Equals(S(r, "MBMCancelStatus")?.Trim(), "Y", StringComparison.OrdinalIgnoreCase))).ToList();

    public async Task<McoRefundListing> ListAsync(McoRefundFilter f, CancellationToken ct = default)
    {
        if (McoRefundRules.Validate(f) is { } p) throw new BusinessRuleException(p.Message, field: p.Field);
        var payor = f.Payor!.Trim();
        var cancelledOnly = (await QueryAsync(GenesisDb.Main, "SELECT MBMCancelStatus FROM PAY WHERE PAYCode = @payor", new { payor }, ct)).Select(r => S(r, "MBMCancelStatus")?.Trim() == "Y").FirstOrDefault();
        var column = f.DateBy == "effective" ? "t.MBMENDtEffDate" : "t.MBMENDtBordxDate";
        var sql = $"""
            SELECT TOP ({Max + 1}) m.MBMNumber, m.MBMCOVID AS CoverId, m.MBMPolicyNo, m.MBMName, m.MBMIcBcPp, m.MBMStatus, m.MBMBordxDate, m.MBMPayorEffDate, m.MBMPayorExpDate, t.MBMENDtEffDate, t.MBMENDtBordxDate,
                   o.MBMMCOFee AS MCOFee, r.MBMMCORefund AS MCORefund, o.MBMCRNote AS CRNote, o.MBMCRNoteDate AS CRNoteDate, o.MBMInvoiceNo AS InvoiceNo, o.MBMInvoiceDate AS InvoiceDate,
                   (SELECT COUNT(*) FROM CAS c WHERE c.MBMNumber = m.MBMNumber AND c.MBMPatientCovID = m.MBMCOVID) AS TotalCase
            FROM MBM m JOIN MBMTwo t ON t.MBMNumber = m.MBMNumber
                 LEFT JOIN MBMRefund r ON r.MBMNumber = m.MBMNumber AND r.MBMCovID = m.MBMCOVID
                 LEFT JOIN MBMOthers o ON o.MBMNumber = m.MBMNumber
            WHERE m.PAYCode = @payor AND (@from IS NULL OR {column} >= @from) AND (@to IS NULL OR {column} < DATEADD(day, 1, @to))
              AND (@cancelledOnly = 0 OR (m.MBMStatus = 'C' AND DATEDIFF(day, t.MBMENDtEffDate, t.MBMENDtBordxDate) <= 30))
              AND ((@printed = 0 AND (o.MBMCRNote IS NULL OR o.MBMCRNote = '')) OR (@printed = 1 AND o.MBMCRNote <> ''))
            ORDER BY m.MBMNumber
            """;
        var rows = (await QueryAsync(GenesisDb.Main, sql, new { payor, from = f.From?.ToDateTime(TimeOnly.MinValue), to = f.To?.ToDateTime(TimeOnly.MinValue), cancelledOnly = cancelledOnly ? 1 : 0, printed = f.PrintStatus == "Y" ? 1 : 0 }, ct)).Select(Map).ToList();
        var truncated = rows.Count > Max; if (truncated) rows = rows.Take(Max).ToList();
        return new McoRefundListing(rows, rows.Sum(x => x.McoRefund ?? 0), truncated, cancelledOnly);
    }

    public async Task<IReadOnlyList<McoRefundRow>> SupplementaryAsync(string memberNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberNumber) || memberNumber.Trim().Length > 30 || memberNumber.Any(char.IsControl)) throw new BusinessRuleException("Please enter a valid membership number.", field: "memberNumber");
        const string sql = """
            SELECT m.MBMNumber, cp.MBMCCoverID AS CoverId, m.MBMPolicyNo, cp.MBMCName AS MBMName, cp.MBMCICBCPPNo AS MBMIcBcPp, cp.MBMCStatus AS MBMStatus, m.MBMBordxDate, m.MBMPayorEffDate, m.MBMPayorExpDate,
                   t.MBMENDtEffDate, t.MBMENDtBordxDate, cp.MBMCMCO AS MCOFee, r.MBMMCORefund AS MCORefund, ct.MBMCRNote AS CRNote, ct.MBMCRNoteDate AS CRNoteDate, cp.MBMInvoiceNo AS InvoiceNo, cp.MBMInvoiceDate AS InvoiceDate,
                   (SELECT COUNT(*) FROM CAS c WHERE c.MBMNumber = cp.MBMCNumber AND c.MBMPatientCovID = cp.MBMCCoverID) AS TotalCase
            FROM MBMCoveredPersons cp JOIN MBM m ON m.MBMNumber = cp.MBMCNumber LEFT JOIN MBMTwo t ON t.MBMNumber = m.MBMNumber
                 LEFT JOIN MBMCoveredPersonsTwo ct ON ct.MBMCNumber = cp.MBMCNumber AND ct.MBMCCoverID = cp.MBMCCoverID
                 LEFT JOIN MBMRefund r ON r.MBMNumber = cp.MBMCNumber AND r.MBMCovID = cp.MBMCCoverID
            WHERE cp.MBMCNumber = @memberNumber AND cp.MBMCStatus = 'C' ORDER BY cp.MBMCCoverID
            """;
        return (await QueryAsync(GenesisDb.Main, sql, new { memberNumber = memberNumber.Trim() }, ct)).Select(Map).ToList();
    }
}

