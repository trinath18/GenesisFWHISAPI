using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

internal sealed class LeafletRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ILeafletRepository
{
    private const int Chunk = 500;
    private static DateOnly? Day(IDictionary<string, object?> r, string name) => D(r, name) is { } d ? DateOnly.FromDateTime(d) : null;
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The MBMPrinting flag column of each tab (UpdatePrintingFile "LEAFLET" and "CRD").</summary>
    private static (string Flag, string Date) Columns(bool cardExport) => cardExport ? ("MBMExpListed", "MBMExpListDate") : ("MBMLeafletPrinted", "MBMLeafletPrintedDate");

    private Task<bool> PrintingTrackedAsync(CancellationToken ct) =>
        ScalarAsync<bool>(GenesisDb.Main, "SELECT CAST(CASE WHEN OBJECT_ID('dbo.MBMPrinting', 'U') IS NULL THEN 0 ELSE 1 END AS bit)", null, ct);

    public async Task<IReadOnlyList<LeafletPayor>> PayorsAsync(CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Main, "SELECT PAYCode, PAYCompanyName FROM PAY ORDER BY PAYCode", null, ct)).Select(r => new LeafletPayor(S(r, "PAYCode") ?? "", S(r, "PAYCompanyName"))).ToList();

    public async Task<LeafletPage> SearchAsync(LeafletCriteria c, bool cardExport, CancellationToken ct = default)
    {
        if (LeafletRules.Validate(c) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var tracked = await PrintingTrackedAsync(ct);
        var rows = await ListAsync(c, cardExport, tracked, LeafletRules.MaxRows + 1, ct);
        return new LeafletPage(rows.Take(LeafletRules.MaxRows).ToList(), rows.Count > LeafletRules.MaxRows, tracked);
    }

    private async Task<List<LeafletRow>> ListAsync(LeafletCriteria c, bool cardExport, bool tracked, int take, CancellationToken ct)
    {
        // Only the fields of the chosen selection apply (cboLLCriteriaSel hides the others); the Card Export tab combines them all.
        bool Uses(params string[] selections) => c.Selection is null || selections.Contains(c.Selection);
        var single = Uses(LeafletLists.SingleMember);
        var range = Uses(LeafletLists.MemberRange);
        var batch = Uses(LeafletLists.BordxBatch);
        var bordxRange = Uses(LeafletLists.BordxRange);
        var payor = Uses(LeafletLists.BordxBatch, LeafletLists.BordxRange);

        var order = c.Sequence switch
        {
            "Policy No" => "m.MBMPolicyNo, m.MBMNumber",
            "Name" => "m.MBMName, m.MBMNumber",
            "Group Company" => "o.MBMGroupCompany, m.MBMNumber",
            _ => "m.MBMNumber",
        };
        var (flag, _) = Columns(cardExport);
        var printed = tracked
            ? $"""
               OUTER APPLY (SELECT TOP (1) CASE WHEN p.{flag} = 1 THEN 1 ELSE 0 END AS Printed FROM MBMPrinting p
                            WHERE p.MBMBordxDate = m.MBMBordxDate AND p.PAYCode = m.PAYCode AND p.HLTCode = m.HLTCode AND p.MBMBatchNo = m.MBMBatchNo AND p.MBMNumber = m.MBMNumber
                            ORDER BY CASE WHEN p.{flag} = 1 THEN 0 ELSE 1 END) pr
               """
            : "OUTER APPLY (SELECT 0 AS Printed) pr";
        // VIEW_LEAFLET drops deleted members, so the leaflet list does too; VIEW_CardPrinting (card export) does not filter on status.
        var status = cardExport ? "" : "AND m.MBMDataStatus <> N'D' AND m.MBMStatus <> N'D'";
        var sql = $"""
            SELECT TOP (@take) m.PAYCode, m.MBMNumber, m.MBMBordxDate, m.MBMPolicyNo, m.HLTCode, m.MBMBatchNo, m.MBMName, pr.Printed
            FROM MBM m LEFT JOIN MBMOthers o ON o.MBMNumber = m.MBMNumber
            {printed}
            WHERE 1 = 1 {status}
              AND (@no IS NULL OR m.MBMNumber = @no)
              AND (@mf IS NULL OR m.MBMNumber >= @mf) AND (@mt IS NULL OR m.MBMNumber <= @mt)
              AND (@day IS NULL OR (m.MBMBordxDate >= @day AND m.MBMBordxDate < @dayEnd))
              AND (@batch IS NULL OR m.MBMBatchNo = @batch)
              AND (@bf IS NULL OR m.MBMBordxDate >= @bf) AND (@bt IS NULL OR m.MBMBordxDate < @bt)
              AND (@payor IS NULL OR m.PAYCode = @payor)
            ORDER BY {order}
            OPTION (RECOMPILE)
            """;
        var list = await QueryAsync(GenesisDb.Main, sql, new
        {
            take,
            no = single ? Blank(c.MemberNo) : null,
            mf = range ? Blank(c.MemberFrom) : null, mt = range ? Blank(c.MemberTo) : null,
            day = batch ? At(c.BordxDate) : null, dayEnd = batch ? At(c.BordxDate?.AddDays(1)) : null, batch = batch ? c.BatchNo : null,
            bf = bordxRange ? At(c.BordxFrom) : null, bt = bordxRange ? At(c.BordxTo?.AddDays(1)) : null,
            payor = payor ? Blank(c.Payor)?.ToUpperInvariant() : null,
        }, ct);
        return list.Select(r => new LeafletRow(S(r, "PAYCode") ?? "", S(r, "MBMNumber") ?? "", Day(r, "MBMBordxDate"), S(r, "MBMPolicyNo"), S(r, "HLTCode"),
            I(r, "MBMBatchNo"), S(r, "MBMName"), I(r, "Printed") == 1)).ToList();
    }

    /// <summary>The rows a print or export request selects, or the legacy message why there are none.</summary>
    private async Task<(List<LeafletRow> Rows, bool Tracked)> SelectAsync(LeafletPrintRequest request, bool cardExport, CancellationToken ct)
    {
        var c = request.Criteria ?? throw new BusinessRuleException("Please enter the search criteria.", field: "criteria");
        if (LeafletRules.Validate(c) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        if (LeafletRules.ValidateVersion(request.Version) is { } version) throw new BusinessRuleException(version, field: "version");
        var options = cardExport ? LeafletLists.ExportOptions : LeafletLists.PrintOptions;
        if (request.Option is null || !options.Contains(request.Option)) throw new BusinessRuleException("Please Enter Print Option !", field: "option");

        var tracked = await PrintingTrackedAsync(ct);
        var rows = await ListAsync(c, cardExport, tracked, LeafletRules.MaxRows + 1, ct);
        if (rows.Count == 0) throw BusinessRuleException.NotFound("Record not found !");
        if (rows.Count > LeafletRules.MaxRows) throw new BusinessRuleException($"More than {LeafletRules.MaxRows} members match; narrow the criteria and print in parts.", field: "criteria");
        var (printed, message) = LeafletRules.Filter(request.Option, rows);
        if (message is not null) throw new BusinessRuleException(message, field: "option");
        return (printed is null ? rows : rows.Where(r => r.Printed == printed).ToList(), tracked);
    }

    public async Task<LeafletOutput<LeafletDocument>> PrintLeafletsAsync(LeafletPrintRequest request, string user, CancellationToken ct = default)
    {
        var (rows, tracked) = await SelectAsync(request, cardExport: false, ct);
        var details = new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        var covered = new Dictionary<string, List<LeafletCoveredPerson>>(StringComparer.OrdinalIgnoreCase);
        foreach (var numbers in rows.Select(r => r.MemberNumber).Distinct().Chunk(Chunk))
        {
            foreach (var r in await QueryAsync(GenesisDb.Main, """
                SELECT m.MBMNumber, m.MBMPolicyNo, m.MBMName, m.MBMAddress1, m.MBMAddress2, m.MBMAddress3, m.MBMPostCode, m.INSCode, p.PAYCompanyName,
                       o.MBMAgentCode, o.MBMGroupCompany, o.MBMDepartment, o.MBMEmployeeNo
                FROM MBM m INNER JOIN PAY p ON p.PAYCode = m.PAYCode LEFT JOIN MBMOthers o ON o.MBMNumber = m.MBMNumber
                WHERE m.MBMNumber IN @numbers
                """, new { numbers }, ct))
                details[S(r, "MBMNumber") ?? ""] = r;
            foreach (var r in await QueryAsync(GenesisDb.Main,
                "SELECT MBMCNumber, MBMCCoverID, MBMCName, MBMCRELCode FROM MBMCoveredPersons WHERE MBMCNumber IN @numbers AND MBMCCoverID <> '00' ORDER BY MBMCNumber, MBMCCoverID",
                new { numbers }, ct))
            {
                var key = S(r, "MBMCNumber") ?? "";
                if (!covered.TryGetValue(key, out var list)) covered[key] = list = [];
                list.Add(new LeafletCoveredPerson(S(r, "MBMCCoverID") ?? "", S(r, "MBMCName"), S(r, "MBMCRELCode")));
            }
        }

        // Keep the search sequence; a member without a payor row is not in VIEW_LEAFLET and is skipped, as the report would.
        var documents = rows.Select(r => r.MemberNumber).Distinct(StringComparer.OrdinalIgnoreCase).Where(details.ContainsKey).Select(n =>
        {
            var d = details[n];
            return new LeafletDocument(n, S(d, "MBMPolicyNo"), S(d, "MBMAgentCode"), S(d, "MBMName"), S(d, "MBMAddress1"), S(d, "MBMAddress2"), S(d, "MBMAddress3"),
                S(d, "MBMPostCode"), S(d, "PAYCompanyName"), S(d, "INSCode"), S(d, "MBMGroupCompany"), S(d, "MBMDepartment"), S(d, "MBMEmployeeNo"),
                covered.TryGetValue(n, out var cp) ? cp : []);
        }).ToList();
        var printedRows = rows.Where(r => details.ContainsKey(r.MemberNumber)).ToList();
        var recorded = tracked && !request.Preview && await RecordAsync(printedRows, cardExport: false, ct);
        Audit("Leaflet.Print", request, printedRows, recorded, user);
        return new LeafletOutput<LeafletDocument>(documents, documents.Count, recorded);
    }

    public async Task<LeafletOutput<CardExportLine>> ExportCardsAsync(LeafletPrintRequest request, string user, CancellationToken ct = default)
    {
        var (rows, tracked) = await SelectAsync(request, cardExport: true, ct);
        var byNumber = rows.GroupBy(r => r.MemberNumber, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var lines = new List<CardExportLine>();
        foreach (var numbers in byNumber.Keys.Chunk(Chunk))
        {
            // VIEW_CardPrinting without its full-table UNION: the principal as cover 00, then each covered person.
            var found = await QueryAsync(GenesisDb.Main, """
                SELECT m.MBMNumber, '00' AS CoverID, m.MBMName AS Name, m.MBMIcBcPp AS IcBcPp, m.MBMPolicyNo, m.PLNCode, m.MBMPayorExpDate,
                       o.MBMGroupCompany, o.MBMDepartment, o.MBMEmployeeNo, m.MBMBordxDate, m.MBMBatchNo, m.PAYCode
                FROM MBM m LEFT JOIN MBMOthers o ON o.MBMNumber = m.MBMNumber WHERE m.MBMNumber IN @numbers
                UNION ALL
                SELECT m.MBMNumber, c.MBMCCoverID, c.MBMCName, c.MBMCICBCPPNo, m.MBMPolicyNo, m.PLNCode, m.MBMPayorExpDate,
                       o.MBMGroupCompany, o.MBMDepartment, o.MBMEmployeeNo, m.MBMBordxDate, m.MBMBatchNo, m.PAYCode
                FROM MBM m INNER JOIN MBMCoveredPersons c ON c.MBMCNumber = m.MBMNumber LEFT JOIN MBMOthers o ON o.MBMNumber = m.MBMNumber
                WHERE m.MBMNumber IN @numbers AND c.MBMCCoverID <> '00'
                """, new { numbers }, ct);
            lines.AddRange(found.Select(r => new CardExportLine(S(r, "MBMNumber") ?? "", S(r, "CoverID") ?? "", S(r, "Name"), S(r, "IcBcPp"), S(r, "MBMPolicyNo"),
                S(r, "PLNCode"), Day(r, "MBMPayorExpDate"), S(r, "MBMGroupCompany"), S(r, "MBMDepartment"), S(r, "MBMEmployeeNo"), Day(r, "MBMBordxDate"), I(r, "MBMBatchNo"), S(r, "PAYCode") ?? "")));
        }
        // The legacy export ordered by PAYCode, bordx date, HLTCode, batch, member and cover; keep the chosen sequence for members instead.
        var rank = rows.Select((r, i) => (r.MemberNumber, i)).GroupBy(x => x.MemberNumber, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        var ordered = lines.OrderBy(l => rank[l.MemberNumber]).ThenBy(l => l.CoverId, StringComparer.Ordinal).ToList();
        var recorded = tracked && !request.Preview && await RecordAsync(rows, cardExport: true, ct);
        Audit("CardExport.Export", request, rows, recorded, user);
        return new LeafletOutput<CardExportLine>(ordered, byNumber.Count, recorded);
    }

    /// <summary>UpdatePrintingFile: one MBMPrinting row per bordx date, batch, payor, HLT code and member; sets the tab's flag and today's date.</summary>
    private async Task<bool> RecordAsync(IReadOnlyList<LeafletRow> rows, bool cardExport, CancellationToken ct)
    {
        var (flag, date) = Columns(cardExport);
        var today = DateTime.Today;
        await using var session = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        foreach (var r in rows.DistinctBy(r => (r.BordxDate, r.BatchNo, r.PayCode, r.HltCode, r.MemberNumber)))
        {
            var args = new { day = At(r.BordxDate), batch = r.BatchNo, pay = r.PayCode, hlt = r.HltCode, number = r.MemberNumber, today };
            var changed = await session.ExecuteAsync($"""
                UPDATE MBMPrinting SET {flag} = 1, {date} = @today
                WHERE MBMBordxDate = @day AND MBMBatchNo = @batch AND PAYCode = @pay AND HLTCode = @hlt AND MBMNumber = @number
                """, args, ct);
            if (changed == 0)
                await session.ExecuteAsync($"INSERT INTO MBMPrinting (MBMBordxDate, MBMBatchNo, PAYCode, HLTCode, MBMNumber, {flag}, {date}) VALUES (@day, @batch, @pay, @hlt, @number, 1, @today)", args, ct);
        }
        await session.CommitAsync(ct);
        return true;
    }

    private void Audit(string action, LeafletPrintRequest request, IReadOnlyList<LeafletRow> rows, bool recorded, string user) =>
        audit.Record(new AuditEntry(action, "MBMPrinting", $"{rows.Count} member(s)", user, new Dictionary<string, object?>
        {
            ["option"] = request.Option, ["preview"] = request.Preview, ["version"] = request.Version, ["selection"] = request.Criteria?.Selection, ["recorded"] = recorded,
            ["members"] = rows.Take(200).Select(r => r.MemberNumber).ToList(),
        }));
}
