namespace GenesisFIS.Core.Membership;

/// <summary>The fixed lists of the legacy Leaflet &amp; Card form (frmReportMain combo boxes).</summary>
public static class LeafletLists
{
    public const string SingleMember = "Single Member", MemberRange = "Members No Range", BordxBatch = "Bordereaux Batch", BordxRange = "Bordereaux Range";
    public const string PrintNew = "Print New", Reprint = "Reprint", PrintAll = "Print All";
    public const string ExportNew = "Export New", ReExport = "Re-Export", ExportAll = "Export All";
    public const string BySihat = "Sihat Malaysia", ByGroupCompany = "Group Company", ByDeptEmployee = "Dept and Employee No";

    public static readonly IReadOnlyList<string> Selections = [SingleMember, MemberRange, BordxBatch, BordxRange];
    public static readonly IReadOnlyList<string> Sequences = ["Membership No", "Policy No", "Name", "Group Company"];
    public static readonly IReadOnlyList<string> PrintOptions = [PrintNew, Reprint, PrintAll];
    public static readonly IReadOnlyList<string> ExportOptions = [ExportNew, ReExport, ExportAll];
    /// <summary>Leaflet Version / Card Version: Leaflet.rpt, Leaflet_GrpCo.rpt and Leaflet_GrpCo_EmpNo.rpt.</summary>
    public static readonly IReadOnlyList<string> Versions = [BySihat, ByGroupCompany, ByDeptEmployee];
}

public sealed record LeafletOptions(IReadOnlyList<string> Selections, IReadOnlyList<string> Sequences, IReadOnlyList<string> PrintOptions,
    IReadOnlyList<string> ExportOptions, IReadOnlyList<string> Versions, IReadOnlyList<LeafletPayor> Payors);

public sealed record LeafletPayor(string Code, string? Name);

/// <summary>
/// Search of the Leaflet and Card Export tabs. The Leaflet tab uses exactly one <see cref="Selection"/>; the Card Export tab has no selector and
/// combines every filter it is given (Selection null).
/// </summary>
public sealed record LeafletCriteria(
    string? Selection, string? MemberNo, string? MemberFrom, string? MemberTo, DateOnly? BordxDate, int? BatchNo, string? Payor,
    DateOnly? BordxFrom, DateOnly? BordxTo, string? Sequence);

/// <summary>A row of the lvwLeaflet / lvwCRD list. Printed is the MBMPrinting flag of the tab (leaflet printed, or card exported).</summary>
public sealed record LeafletRow(string PayCode, string MemberNumber, DateOnly? BordxDate, string? PolicyNo, string? HltCode, int? BatchNo, string? Name, bool Printed);

/// <summary>PrintingTracked is false when the MBMPrinting table is not in the database: nothing is shown as printed and nothing is recorded.</summary>
public sealed record LeafletPage(IReadOnlyList<LeafletRow> Rows, bool Truncated, bool PrintingTracked);

/// <summary>Preview (Card Export "Print Preview") produces the file without recording it in MBMPrinting.</summary>
public sealed record LeafletPrintRequest(LeafletCriteria? Criteria, string? Option, string? Version, bool Preview = false);

/// <summary>One leaflet (Leaflet.rpt over VIEW_LEAFLET): the principal plus any covered persons of the membership.</summary>
public sealed record LeafletDocument(
    string MemberNumber, string? PolicyNo, string? AgentCode, string? Name, string? Address1, string? Address2, string? Address3, string? PostCode,
    string? PayorName, string? InsCode, string? GroupCompany, string? Department, string? EmployeeNo, IReadOnlyList<LeafletCoveredPerson> Covered)
{
    /// <summary>The Leaflet.rpt formula: insurers F (family) and H (husband and wife) print the covered persons as "Supplimentary Card".</summary>
    public bool HasSupplementaryCards => InsCode is "F" or "H";
}

public sealed record LeafletCoveredPerson(string CoverId, string? Name, string? Relationship);

/// <summary>One line of the card export sheet (VIEW_CardPrinting): the principal as cover 00 and each covered person.</summary>
public sealed record CardExportLine(string MemberNumber, string CoverId, string? Name, string? IcBcPp, string? PolicyNo, string? PlanCode, DateOnly? PayorExpDate,
    string? GroupCompany, string? Department, string? EmployeeNo, DateOnly? BordxDate, int? BatchNo, string PayCode);

/// <summary>The rows to print or export plus whether the printing log was written.</summary>
public sealed record LeafletOutput<T>(IReadOnlyList<T> Items, int Members, bool Recorded);

public static class LeafletRules
{
    public const int MaxRows = 2000;

    private static bool Has(string? s) => !string.IsNullOrWhiteSpace(s);

    /// <summary>Returns the first problem with a search, or null. Mirrors the messages of cmdLLSearch_Click.</summary>
    public static (string Field, string Message)? Validate(LeafletCriteria c)
    {
        foreach (var (field, value) in new[] { ("memberNo", c.MemberNo), ("memberFrom", c.MemberFrom), ("memberTo", c.MemberTo) })
            if (Has(value) && !CardSendingRules.IsValidMemberNumber(value!.Trim())) return (field, "Invalid member number.");
        if (Has(c.Payor) && c.Payor!.Trim().Length != 2) return ("payor", "The payor code must be 2 characters.");
        if (Has(c.Sequence) && !LeafletLists.Sequences.Contains(c.Sequence)) return ("sequence", "Please Enter Sequence !");
        if (c.BatchNo is < 0) return ("batchNo", "Invalid batch number.");
        if (c.BordxFrom > c.BordxTo) return ("bordxTo", "The bordereaux end date cannot be before the start date.");
        if (Has(c.MemberFrom) && Has(c.MemberTo) && string.CompareOrdinal(c.MemberFrom!.Trim(), c.MemberTo!.Trim()) > 0) return ("memberTo", "The member range end cannot be before the start.");

        switch (c.Selection)
        {
            case LeafletLists.SingleMember when !Has(c.MemberNo):
                return ("memberNo", "Please Enter Member No !");
            case LeafletLists.MemberRange when !Has(c.MemberFrom) || !Has(c.MemberTo):
                return ("memberFrom", "Please Enter Members No range !");
            case LeafletLists.BordxBatch when c.BordxDate is null || c.BatchNo is null || !Has(c.Payor):
                return ("bordxDate", "Please Enter Bordereaux Date, Batch No and Payor !");
            case LeafletLists.BordxRange when c.BordxFrom is null || c.BordxTo is null || !Has(c.Payor):
                return ("bordxFrom", "Please Enter Bordereaux Range and Payor!");
            case null:
                // Card Export: any mix of filters, but at least one that narrows the 14M members (a payor alone does not).
                if (!Has(c.MemberNo) && !Has(c.MemberFrom) && !Has(c.MemberTo) && c.BordxDate is null && c.BordxFrom is null && c.BordxTo is null)
                    return ("memberNo", "Please enter a member, a member range or a bordereaux date.");
                break;
            case var s when !LeafletLists.Selections.Contains(s):
                return ("selection", "Unknown criteria selection.");
        }
        return null;
    }

    /// <summary>Which rows a print option takes, and the legacy message when it takes none.</summary>
    public static (bool? Printed, string? Problem) Filter(string? option, IReadOnlyList<LeafletRow> rows) => option switch
    {
        LeafletLists.PrintNew or LeafletLists.ExportNew => rows.Any(r => !r.Printed) ? (false, null) : (false, "This report already printed, please select 'Reprint'"),
        LeafletLists.Reprint or LeafletLists.ReExport => rows.Any(r => r.Printed) ? (true, null) : (true, "Please select 'Print' option for this report"),
        LeafletLists.PrintAll or LeafletLists.ExportAll => (null, rows.Count == 0 ? "Record not found !" : null),
        _ => (null, "Please Enter Print Option !"),
    };

    public static string? ValidateVersion(string? version) => version is not null && LeafletLists.Versions.Contains(version) ? null : "Please Enter Leaflet Version !";
}

/// <summary>
/// Legacy form: Membership &gt; Leaflet &amp; Card (frmReportMain). Reads MBM / MBMOthers / MBMCoveredPersons / PAY (VIEW_LEAFLET, VIEW_CardPrinting);
/// records printing in MBMPrinting (MBMLeafletPrinted / MBMExpListed) when that table exists.
/// </summary>
public interface ILeafletRepository
{
    Task<IReadOnlyList<LeafletPayor>> PayorsAsync(CancellationToken ct = default);

    Task<LeafletPage> SearchAsync(LeafletCriteria criteria, bool cardExport, CancellationToken ct = default);

    /// <summary>The leaflets the print option selects, in the chosen sequence; marks them printed (one MBMPrinting row per member).</summary>
    Task<LeafletOutput<LeafletDocument>> PrintLeafletsAsync(LeafletPrintRequest request, string user, CancellationToken ct = default);

    /// <summary>The card lines of the members the export option selects; marks those members exported.</summary>
    Task<LeafletOutput<CardExportLine>> ExportCardsAsync(LeafletPrintRequest request, string user, CancellationToken ct = default);
}
