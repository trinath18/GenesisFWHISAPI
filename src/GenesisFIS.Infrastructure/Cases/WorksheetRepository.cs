using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>
/// Worksheet Enquiry (legacy frmWorksheetEnq) and Worksheet Registration (frmNewWorksheet2010): the calculation here, the save in WorksheetRepository.Save.cs.
/// The case, the next version and the invoices come from the invoice repository so both screens agree on them.
/// </summary>
internal sealed partial class WorksheetRepository(IDbConnectionFactory connections, ICaseInvoiceRepository invoices, IAuditLog audit) : SqlRepository(connections), IWorksheetRepository
{
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static readonly string[] Parts = ["ClaimLimit", "ClaimActual", "ClaimApprove", "ClaimHos", "ClaimMember"];
    private static readonly string[] LimitDays = ["CLMBoardDays", "CLMICUDays", "CLMCashDays", "CLMGurdianDays", "CLMINHOSPhyDays", "CLMKIDDCDays"];

    private static WorksheetInvoiceRef Ref(InvoiceEntry i) => new(i.Index, i.InvoiceNo, i.InvoiceDate, i.PayTo, i.Amount, i.PayAmount, i.InWorksheet);

    private static string Column(WorksheetLine l, bool combined) => combined && l.CombinedColumn is not null ? l.CombinedColumn : l.Column;

    public async Task<WorksheetEnquiry?> EnquiryAsync(string caseNumber, CancellationToken ct = default)
    {
        if (await invoices.GetAsync(caseNumber, ct) is not { } screen) return null;
        var no = screen.Case.CaseNumber;
        var combined = WorksheetCatalog.UsesCombinedRows(screen.Case.PlanCode);

        var parts = new Dictionary<string, Dictionary<int, IDictionary<string, object?>>>();
        foreach (var t in Parts)
            parts[t] = (await QueryAsync(GenesisDb.Worksheet, $"SELECT * FROM {t} WHERE CASNumber = @no", new { no }, ct))
                .GroupBy(r => I(r, "CLMVersion") ?? 0).ToDictionary(g => g.Key, g => g.First());

        var headers = await QueryAsync(GenesisDb.Worksheet, """
            SELECT ClaimVersion, ClaimStatus, CLMWSStatus, ClaimOpenDate, ClaimCloseDate, ClaimOpenBy, CLMTotalActual, CLMTotalApproved, CLMPayableByMedix2H, CLMPayableByMedix2M,
                   CLMExcess, CLMBalanceLimit, CLMPreviousBalance, CLMStayType, CLMCashType, BordxRefNo, DebitCreditRefNo, DebitCreditDate, CLMPayorAppStatus,
                   CLMFinalApprove, CLMLimitExceeded
            FROM Claim WHERE CASNumber = @no ORDER BY ClaimVersion
            """, new { no }, ct);

        var versions = headers.Select(h =>
        {
            var v = I(h, "ClaimVersion") ?? 0;
            string? Cell(string table, string column) => parts[table].TryGetValue(v, out var r) && r.ContainsKey(column) ? S(r, column) : null;
            var lines = WorksheetCatalog.Lines.Select(l =>
            {
                var c = Column(l, combined);
                return new WorksheetLineValue(l.Row, l.BenefitCode, l.Label, Cell("ClaimLimit", c), Cell("ClaimActual", c), Cell("ClaimApprove", c), Cell("ClaimHos", c), Cell("ClaimMember", c));
            }).ToList();
            var status = S(h, "CLMWSStatus");
            return new WorksheetVersion(v, S(h, "ClaimStatus"), status, status is null ? null : WorksheetCatalog.WorksheetStatuses.GetValueOrDefault(status), Day(h, "ClaimOpenDate"), Day(h, "ClaimCloseDate"),
                S(h, "ClaimOpenBy"), Dec(h, "CLMTotalActual"), Dec(h, "CLMTotalApproved"), Dec(h, "CLMPayableByMedix2H"), Dec(h, "CLMPayableByMedix2M"), Dec(h, "CLMExcess"),
                Dec(h, "CLMBalanceLimit"), Dec(h, "CLMPreviousBalance"), S(h, "CLMStayType"), S(h, "CLMCashType"), S(h, "BordxRefNo"), S(h, "DebitCreditRefNo"), Day(h, "DebitCreditDate"),
                S(h, "CLMPayorAppStatus"), LimitDays.ToDictionary(d => d, d => Cell("ClaimLimit", d)), lines,
                screen.Invoices.Where(i => i.Version == v).Select(Ref).ToList(), Dec(h, "CLMFinalApprove"), Dec(h, "CLMLimitExceeded"));
        }).ToList();
        return new WorksheetEnquiry(screen.Case, combined, versions);
    }

    public async Task<WorksheetPreview?> PreviewAsync(string caseNumber, IReadOnlyDictionary<int, string>? limitOverrides = null, CancellationToken ct = default)
    {
        if (await invoices.GetAsync(caseNumber, ct) is not { } screen) return null;
        var input = (await LoadAsync(screen.Case.CaseNumber, screen.NextVersion, true, ct))!;
        if (limitOverrides is { Count: > 0 }) ValidateOverrides(limitOverrides, input.Value.Input.Claimable);
        var calc = WorksheetCalculator.Calculate(input.Value.Input with { LimitOverrides = limitOverrides });
        var pending = screen.Invoices.Where(i => i.Version == screen.NextVersion && !i.InWorksheet).Select(Ref).ToList();
        return new WorksheetPreview(screen.Case, screen.NextVersion, WorksheetCatalog.UsesCombinedRows(screen.Case.PlanCode), pending, input.Value.Claimability,
            input.Value.Input.Claimable, input.Value.Input.SameLimitDays is not null, calc);
    }

    private static void ValidateOverrides(IReadOnlyDictionary<int, string> limitOverrides, bool claimable)
    {
        if (!claimable) throw new BusinessRuleException("Limits cannot be changed: the case is not claimable.", field: "limitOverrides");
        foreach (var (row, text) in limitOverrides)
            if (row is < 1 or > 29 || WorksheetCalculator.NormaliseLimit(text) is null)
                throw new BusinessRuleException($"Row {row}: enter an amount or FULL as the limit.", field: "limitOverrides");
    }

    public async Task<WorksheetInput?> InputAsync(string caseNumber, int version, bool openOnly, CancellationToken ct = default) =>
        CaseNumber.IsValid(caseNumber) ? (await LoadAsync(caseNumber.Trim(), version, openOnly, ct))?.Input : null;

    /// <summary>
    /// Builds the calculator input of one version. <paramref name="amend"/> recalculates a saved version (Worksheet Adjustment): all its invoices count, and the
    /// same-limit days and annual balance it already used are given back, so it is not limited by itself.
    /// </summary>
    private async Task<(WorksheetInput Input, string? Claimability)?> LoadAsync(string no, int version, bool openOnly, CancellationToken ct, bool amend = false)
    {
        var c = (await QueryAsync(GenesisDb.Main, """
            SELECT c.MBMNumber, c.MBMPatientCovID, c.CASClaimability, c.LINKCASNo, c.CASDateAdmitted, m.PLNCode, m.INSCode, m.HLTCode, m.MBMMemberType, m.MBMPayorEffDate, cp.MBMCPLNCode,
                   (SELECT TOP 1 p.AnnLmtIND FROM PLN p WHERE p.PLNCode = m.PLNCode AND p.HLTCode = m.HLTCode ORDER BY p.PLNEffDate DESC) AS AnnLmtIND
            FROM CAS c JOIN MBM m ON m.MBMNumber = c.MBMNumber LEFT JOIN MBMCoveredPersons cp ON cp.MBMCNumber = c.MBMNumber AND cp.MBMCCoverID = c.MBMPatientCovID
            WHERE c.CASNumber = @no
            """, new { no }, ct)).FirstOrDefault();
        if (c is null) return null;
        string T(string col) => S(c, col)?.Trim() ?? "";
        var patient = T("MBMPatientCovID");
        // Legacy display_benefit: a covered person with a plan of their own uses it, everyone else the member's plan.
        var plan = patient != "00" && T("MBMCPLNCode").Length > 0 ? T("MBMCPLNCode") : T("PLNCode");
        var admitted = D(c, "CASDateAdmitted") ?? DateTime.Today;
        var benefits = (await QueryAsync(GenesisDb.Main, """
            SELECT BNFCode, BNFNoOfDays, BNFClaimAbleAmount, BNFRMPerDis, BNFEffDate FROM BNF
            WHERE PLNCode = @plan AND INSCode = @ins AND HLTCode = @health ORDER BY BNFCode, CASE WHEN BNFEffDate <= @admitted THEN 0 ELSE 1 END, BNFEffDate DESC
            """, new { plan, ins = T("INSCode"), health = T("HLTCode"), admitted }, ct))
            .GroupBy(r => S(r, "BNFCode")!.Trim()).Select(g => g.First())
            .Select(r => new WorksheetBenefit(S(r, "BNFCode")!.Trim(), I(r, "BNFNoOfDays"), S(r, "BNFClaimAbleAmount"), S(r, "BNFRMPerDis"))).ToList();

        var combined = WorksheetCatalog.UsesCombinedRows(T("PLNCode"));
        string Sum(WorksheetLine l, bool hospitalOnly)
        {
            var cols = combined && l.CombinedInvoiceColumns is not null ? l.CombinedInvoiceColumns : l.InvoiceColumns;
            var sum = string.Join(" + ", cols.Select(x => $"ISNULL(a.{x}, 0)"));
            return $"SUM({(hospitalOnly ? $"CASE WHEN i.INVPayTo = 'H' THEN {sum} ELSE 0 END" : sum)}) AS [{(hospitalOnly ? "h" : "a")}{l.Row}]";
        }
        var row = (await QueryAsync(GenesisDb.Worksheet, $"""
            SELECT {string.Join(", ", WorksheetCatalog.Lines.SelectMany(l => new[] { Sum(l, false), Sum(l, true) }))},
                   SUM(ISNULL(o.CLMBoardDay, 0)) AS dBoard, SUM(ISNULL(o.CLMICUDay, 0)) AS dIcu, SUM(ISNULL(o.CLMCashDay, 0)) AS dCash, SUM(ISNULL(o.CLMLodgerday, 0)) AS dGuardian,
                   SUM(ISNULL(o.CLMKidneyMnth, 0)) AS dKidney, SUM(CASE WHEN i.INVPayTo = 'M' THEN 1 ELSE 0 END) AS memberInvoices
            FROM ClaimInvoice i JOIN ClaimInvoiceActual a ON a.INVIndex = i.INVIndex LEFT JOIN ClaimInvoiceActualOthers o ON o.INVIndex = i.INVIndex
            WHERE i.CASNumber = @no AND i.INVWSversion = @version{(openOnly ? " AND ISNULL(i.INVWSed, 0) = 0" : "")}
            """, new { no, version = version.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct)).First();
        decimal V(string k) => Dec(row, k) ?? 0;

        // Legacy getDatatoWS: the days left on the linked case's ClaimSameLimit row (or this case's, for a later version) replace the plan's day caps.
        var sameCase = T("LINKCASNo").Length > 0 ? T("LINKCASNo") : no;
        var same = (await QueryAsync(GenesisDb.Worksheet, "SELECT CLMBoardDays, CLMICUDays, CLMCashDays, CLMGurdianDays, CLMKIDDCDays FROM ClaimSameLimit WHERE CASNumber = @sameCase", new { sameCase }, ct))
            .Select(r => new WorksheetDays(WorksheetCalculator.Parse(S(r, "CLMBoardDays")) ?? 0, WorksheetCalculator.Parse(S(r, "CLMICUDays")) ?? 0, WorksheetCalculator.Parse(S(r, "CLMCashDays")) ?? 0,
                WorksheetCalculator.Parse(S(r, "CLMGurdianDays")) ?? 0, WorksheetCalculator.Parse(S(r, "CLMKIDDCDays")) ?? 0)).FirstOrDefault();

        decimal? balance = T("AnnLmtIND") == "Y"
            ? await AnnualBalance.ForAsync((db, sql, args) => QueryAsync(db, sql, args, ct), T("MBMNumber"), patient, T("PLNCode"), T("INSCode"), S(c, "MBMMemberType"), D(c, "MBMPayorEffDate") ?? admitted)
            : null;
        if (amend)
        {
            var saved = (await QueryAsync(GenesisDb.Worksheet, """
                SELECT c.CLMTotalApproved, l.CLMBoardDays, l.CLMICUDays, l.CLMCashDays, l.CLMGurdianDays, l.CLMKIDDCDays
                FROM Claim c LEFT JOIN ClaimLimit l ON l.CASNumber = c.CASNumber AND l.CLMVersion = c.ClaimVersion WHERE c.CASNumber = @no AND c.ClaimVersion = @version
                """, new { no, version }, ct)).FirstOrDefault();
            if (saved is not null)
            {
                decimal Days(string col) => WorksheetCalculator.Parse(S(saved, col)) ?? 0;
                if (balance is not null) balance += Dec(saved, "CLMTotalApproved") ?? 0;
                if (same is not null)
                    same = new WorksheetDays(same.Board + Days("CLMBoardDays"), same.Icu + Days("CLMICUDays"), same.Cash + Days("CLMCashDays"), same.Guardian + Days("CLMGurdianDays"),
                        same.KidneyMonths + Days("CLMKIDDCDays"));
            }
        }
        var claimability = S(c, "CASClaimability")?.Trim();
        return (new WorksheetInput(
            WorksheetCatalog.Lines.ToDictionary(l => l.Row, l => V("a" + l.Row)), WorksheetCatalog.Lines.ToDictionary(l => l.Row, l => V("h" + l.Row)),
            new WorksheetDays(V("dBoard"), V("dIcu"), V("dCash"), V("dGuardian"), V("dKidney")), benefits, same,
            !WorksheetCalculator.NotClaimable.Contains(claimability ?? ""), V("memberInvoices") > 0, balance), claimability);
    }
}
