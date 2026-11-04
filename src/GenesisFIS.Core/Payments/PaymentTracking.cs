namespace GenesisFIS.Core.Payments;

/// <summary>
/// Payment > Payment Tracking (legacy FrmPaymentTracking, USRAccess 58), report type Hospital: the hospital payment vouchers (Payment2HOS) with their claim,
/// member, hospital and bordereau, listed or grouped as the legacy "Report" choices. The legacy views (VIEW_PaymentHOS_Tracking and the member
/// VIEW_Payment_Tracking) join the HIS databases, so on FWHS every member, hospital, payor and bordereau column was empty; here the FWHS tables are read.
/// Report type Member is not migrated: member payments (Payment2MBM) are empty in FWHS.
/// </summary>
public sealed record PaymentTrackingSearch(
    string? Report = null, string? MemberNo = null, string? MemberName = null, string? HealthType = null, string? Payor = null, string? ClaimNoFrom = null,
    string? ClaimNoTo = null, string? Hospital = null, string? PvNoFrom = null, string? PvNoTo = null, DateOnly? PvDateFrom = null, DateOnly? PvDateTo = null,
    string? DnCnFrom = null, string? DnCnTo = null, decimal? AmountFrom = null, decimal? AmountTo = null, string? BordxNoFrom = null, string? BordxNoTo = null,
    DateOnly? BordxDateFrom = null, DateOnly? BordxDateTo = null, int? AccountMonthFrom = null, int? AccountMonthTo = null, int? AccountYearFrom = null,
    int? AccountYearTo = null, string? Status = null);

/// <summary>One hospital payment voucher line (legacy "General Listing").</summary>
public sealed record PaymentTrackingLine(
    string? PvNo, DateOnly? PvDate, decimal Paid, string? MemberNumber, string? MemberName, string? BordxNo, DateOnly? BordxDate, string CaseNumber, string Version,
    string? Hospital, string? Payor, string? HealthType, string? DnCn, string? Remark, string? Status, int? AccountMonth, int? AccountYear);

/// <summary>One row of a grouped report: the group's key (and date, where the legacy report grouped by one), its number of cases and amount paid.</summary>
public sealed record PaymentTrackingGroup(string? Key, DateOnly? Date, int Cases, decimal Paid);

public sealed record PaymentTrackingResult(string Report, IReadOnlyList<PaymentTrackingLine> Lines, IReadOnlyList<PaymentTrackingGroup> Groups, int Count, decimal Total, bool Truncated);

public sealed record PaymentTrackingOption(string Code, string Name);

public sealed record PaymentTrackingOptions(IReadOnlyList<PaymentTrackingOption> Payors, IReadOnlyList<PaymentTrackingOption> Hospitals);

/// <summary>The legacy "Report" choices (stored procedure PaymentTracking cases 1-8) and how they group.</summary>
public static class PaymentTrackingReport
{
    public const string General = "general";
    public const string ByBordx = "bordx";
    public const string ByClaim = "claim";
    public const string ByHospital = "hospital";
    public const string ByMember = "member";
    public const string ByPayor = "payor";
    public const string ByPv = "pv";
    public const string HospitalSummary = "hospital-summary";

    public static readonly IReadOnlyList<string> All = [General, ByBordx, ByClaim, ByHospital, ByMember, ByPayor, ByPv, HospitalSummary];

    /// <summary>A general listing returns at most this many lines; a grouped report groups at most <see cref="MaxGrouped"/> vouchers.</summary>
    public const int MaxLines = 5000;
    public const int MaxGrouped = 50_000;

    public static string Normalise(string? report) => All.FirstOrDefault(r => string.Equals(r, report?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? General;

    /// <summary>Groups the lines as the legacy report did (By Hospital and By Payor also by PV date, By Bordx by bordereau date, By PV No by PV date).</summary>
    public static IReadOnlyList<PaymentTrackingGroup> Group(string report, IEnumerable<PaymentTrackingLine> lines)
    {
        (string? Key, DateOnly? Date) KeyOf(PaymentTrackingLine l) => report switch
        {
            ByBordx => (l.BordxNo, l.BordxDate),
            ByClaim => ($"{l.CaseNumber}-{l.Version}", null),
            ByHospital => (l.Hospital, l.PvDate),
            ByMember => (l.MemberNumber, null),
            ByPayor => (l.Payor, l.PvDate),
            ByPv => (l.PvNo, l.PvDate),
            HospitalSummary => (l.Hospital, null),
            _ => (null, null),
        };
        if (report == General) return [];
        return lines.GroupBy(KeyOf).Select(g => new PaymentTrackingGroup(g.Key.Key, g.Key.Date, g.Count(), g.Sum(l => l.Paid)))
            .OrderBy(g => g.Key ?? "").ThenBy(g => g.Date).ToList();
    }
}

public interface IPaymentTrackingRepository
{
    Task<PaymentTrackingOptions> OptionsAsync(CancellationToken ct = default);
    Task<PaymentTrackingResult> SearchAsync(PaymentTrackingSearch search, CancellationToken ct = default);
}
