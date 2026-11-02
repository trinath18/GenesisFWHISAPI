using System.Globalization;

namespace GenesisFIS.Core.Cases;

/// <summary>One BNF row of the patient's plan: days cap, per-day / claimable amount and the per-disability limit, all as the legacy text ("FULL" or a number).</summary>
public sealed record WorksheetBenefit(string Code, int? Days, string? ClaimableAmount, string? PerDisability);

/// <summary>Days (months for kidney dialysis) entered on the invoices, or the days the limit allows.</summary>
public sealed record WorksheetDays(decimal Board, decimal Icu, decimal Cash, decimal Guardian, decimal KidneyMonths)
{
    public static readonly WorksheetDays None = new(0, 0, 0, 0, 0);
}

/// <summary>
/// Everything the legacy getDatatoWS / display_benefit / getFinalTotal read for one worksheet. <see cref="SameLimitDays"/> is the ClaimSameLimit row of the
/// linked case (days left), when there is one. <see cref="AnnualBalance"/> is null when the plan has no annual limit. <see cref="LimitOverrides"/> are the
/// limits a claims officer typed into the grid (legacy FGrid_Click): a number (negative to deduct) or FULL.
/// </summary>
public sealed record WorksheetInput(
    IReadOnlyDictionary<int, decimal> Actual, IReadOnlyDictionary<int, decimal> Hospital, WorksheetDays Days, IReadOnlyList<WorksheetBenefit> Benefits,
    WorksheetDays? SameLimitDays, bool Claimable, bool HasMemberInvoices, decimal? AnnualBalance, IReadOnlyDictionary<int, string>? LimitOverrides = null);

public sealed record WorksheetCalculation(
    IReadOnlyList<WorksheetLineValue> Lines, WorksheetDays DaysAllowed, decimal TotalActual, decimal TotalApproved, decimal FinalApproved, bool LimitExceeded,
    decimal TotalHospital, decimal PayableToHospital, decimal PayableToMember, decimal Excess, decimal? AnnualBalance,
    IReadOnlyDictionary<int, string>? DaysAtLimit = null);

/// <summary>
/// The worksheet arithmetic of frmNewWorksheet2010 for the plans in use (codes not starting with 2-5, no co-payment, no top-up, no lifetime or take-over limit):
/// limits from the plan's benefits, approved = the lesser of actual and limit (legacy GetMin), member column = approved - hospital (legacy DueToFromMBM),
/// the annual balance cap and the payable split. Checked against the saved legacy worksheets by WorksheetCalculatorParityTests.
/// </summary>
public static class WorksheetCalculator
{
    public const string Full = "FULL";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Legacy grid money text: "-" for zero, "1,234.00", negatives in brackets.</summary>
    public static string Money(decimal v) => v == 0 ? "-" : v < 0 ? "(" + (-v).ToString("#,0.00", Inv) + ")" : v.ToString("#,0.00", Inv);

    /// <summary>Reads a legacy grid cell: "1,234.00", "(120.00)", ".50"; anything else (blank, "-", FULL) is null.</summary>
    public static decimal? Parse(string? text)
    {
        var t = text?.Trim().Replace(",", "");
        if (string.IsNullOrEmpty(t)) return null;
        var negative = t.StartsWith('(') && t.EndsWith(')');
        if (negative) t = t[1..^1];
        return decimal.TryParse(t, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, Inv, out var d) ? (negative ? -d : d) : null;
    }

    /// <summary>A limit a user may type: a number or FULL. Returns the normalised text, or null when it is neither.</summary>
    public static string? NormaliseLimit(string? text) =>
        string.Equals(text?.Trim(), Full, StringComparison.OrdinalIgnoreCase) ? Full : Parse(text) is { } d ? Money(d) is "-" ? "0.00" : Money(d) : null;

    /// <summary>Legacy GetMin(actual, limit): a non-negative limit caps the actual, a negative one is a floor, FULL approves the actual, no limit approves nothing.</summary>
    public static decimal Approve(decimal actual, string? limit)
    {
        if (string.Equals(limit?.Trim(), Full, StringComparison.OrdinalIgnoreCase)) return actual;
        if (Parse(limit) is not { } y) return 0;
        return y >= 0 ? Math.Min(actual, y) : Math.Max(actual, y);
    }

    /// <summary>
    /// Legacy FGrid_Click: a limit the officer types is applied as GetMin(typed, actual), the arguments the other way round from the grid's own
    /// calculation, so a negative amount on an empty row deducts it (e.g. "Others" -250) and FULL approves the actual.
    /// </summary>
    public static decimal ApproveTyped(decimal actual, string typed)
    {
        if (string.Equals(typed.Trim(), Full, StringComparison.OrdinalIgnoreCase)) return actual;
        var t = Parse(typed) ?? 0;
        return actual >= 0 ? Math.Min(t, actual) : Math.Max(t, actual);
    }

    private static decimal Num(string? s) => Parse(s) ?? 0;

    /// <summary>The limit of one row from the plan's benefits (legacy display_benefit + the day caps of getDatatoWS), and the days it allows.</summary>
    private static (string Limit, decimal Days) DefaultLimit(WorksheetLine line, IReadOnlyDictionary<string, WorksheetBenefit> benefits, WorksheetDays entered, WorksheetDays? same)
    {
        if (line.BenefitCode is null || !benefits.TryGetValue(line.BenefitCode, out var b)) return ("-", 0);
        (string, decimal) PerDay(decimal used, decimal? sameDays, string? rate)
        {
            // Days left on the linked case replace the plan's cap; without either the benefit has no day limit.
            var cap = same is not null ? sameDays ?? 0 : b.Days is > 0 ? b.Days.Value : used;
            var days = Math.Min(used, cap);
            if (string.Equals(rate?.Trim(), Full, StringComparison.OrdinalIgnoreCase)) return (Full, days);
            return (Money(days * Num(rate)), days);
        }
        return line.BenefitCode switch
        {
            "101" => PerDay(entered.Board, same?.Board, b.ClaimableAmount),
            "102" => PerDay(entered.Icu, same?.Icu, b.ClaimableAmount),
            "601" => PerDay(entered.Cash, same?.Cash, b.ClaimableAmount),
            "701" => PerDay(entered.Guardian, same?.Guardian, b.ClaimableAmount),
            "404" when b.Days is > 0 => PerDay(entered.KidneyMonths, same?.KidneyMonths, b.PerDisability),
            _ => string.Equals(b.PerDisability?.Trim(), Full, StringComparison.OrdinalIgnoreCase) ? (Full, 0)
                : Num(b.PerDisability) is var amount && amount != 0 ? (Money(amount), 0) : ("-", 0),
        };
    }

    /// <summary>
    /// The legacy "Days/Mths@Limit(MAX)" column: a per-day benefit shows its rate and the days allowed of the most allowed (the days left on the linked case when
    /// there is one), e.g. "@160.00 (3/30)"; another benefit with a claimable amount shows "@amount".
    /// </summary>
    private static string? DaysAtLimit(WorksheetLine line, IReadOnlyDictionary<string, WorksheetBenefit> benefits, decimal allowed, WorksheetDays? same)
    {
        if (line.BenefitCode is not { } code || !benefits.TryGetValue(code, out var b)) return null;
        static string Rate(string? r) => string.Equals(r?.Trim(), Full, StringComparison.OrdinalIgnoreCase) ? Full : (Parse(r) ?? 0).ToString("#,0.00", Inv);
        decimal? sameDays = code switch { "101" => same?.Board, "102" => same?.Icu, "601" => same?.Cash, "701" => same?.Guardian, "404" => same?.KidneyMonths, _ => null };
        var perDay = code is "101" or "102" or "601" or "701" || code == "404" && b.Days is > 0;
        if (perDay)
        {
            var max = same is not null ? sameDays ?? 0 : b.Days ?? 0;
            return $"@{Rate(code == "404" ? b.PerDisability : b.ClaimableAmount)} ({allowed.ToString("0.##", Inv)}/{max.ToString("0.##", Inv)})";
        }
        return string.IsNullOrWhiteSpace(b.ClaimableAmount) ? null : "@" + Rate(b.ClaimableAmount);
    }

    public static WorksheetCalculation Calculate(WorksheetInput input)
    {
        var benefits = input.Benefits.GroupBy(b => b.Code.Trim()).ToDictionary(g => g.Key, g => g.First());
        var days = new Dictionary<string, decimal>();
        var lines = new List<WorksheetLineValue>();
        var daysAtLimit = new Dictionary<int, string>();
        decimal totalActual = 0, totalApproved = 0, totalHospital = 0;
        foreach (var line in WorksheetCatalog.Lines)
        {
            var actual = input.Actual.GetValueOrDefault(line.Row);
            var hospital = input.Hospital.GetValueOrDefault(line.Row);
            string limit;
            decimal approved;
            if (!input.Claimable) { limit = "-"; approved = 0; }   // Repudiated, cancelled and undecided cases get no benefits (legacy display_benefit).
            else
            {
                var (l, d) = DefaultLimit(line, benefits, input.Days, input.SameLimitDays);
                if (line.BenefitCode is { } code) days[code] = d;
                if (DaysAtLimit(line, benefits, d, input.SameLimitDays) is { } text) daysAtLimit[line.Row] = text;
                if (input.LimitOverrides?.TryGetValue(line.Row, out var typed) == true && NormaliseLimit(typed) is { } t) { limit = t; approved = ApproveTyped(actual, t); }
                else { limit = l; approved = Approve(actual, l); }
            }
            totalActual += actual; totalApproved += approved; totalHospital += hospital;
            lines.Add(new WorksheetLineValue(line.Row, line.BenefitCode, line.Label, limit, Money(actual), Money(approved), Money(hospital), Money(approved - hospital)));
        }

        var exceeded = input.AnnualBalance is { } balance && totalApproved > balance;
        var final = exceeded ? input.AnnualBalance!.Value : totalApproved;
        // Cashless only: the hospital is paid the approved amount and the member settles the rest with the hospital (negative excess).
        // With member invoices: the hospital gets its bill and the member the remainder of the approved amount.
        var (payHospital, payMember, excess) = input.HasMemberInvoices ? (totalHospital, final - totalHospital, 0m) : (final, 0m, final - totalHospital);
        return new WorksheetCalculation(lines,
            new WorksheetDays(days.GetValueOrDefault("101"), days.GetValueOrDefault("102"), days.GetValueOrDefault("601"), days.GetValueOrDefault("701"), days.GetValueOrDefault("404")),
            totalActual, totalApproved, final, exceeded, totalHospital, payHospital, payMember, excess, input.AnnualBalance, daysAtLimit);
    }

    /// <summary>Case claimability codes for which the legacy worksheet shows no benefits.</summary>
    public static readonly IReadOnlyList<string> NotClaimable = ["RR", "CC", "NN", "NR", "PR"];
}
