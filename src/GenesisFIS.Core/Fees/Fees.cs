namespace GenesisFIS.Core.Fees;

/// <summary>Plan flags that steer the fee calculation (table PLN). Defaults match the legacy form: round up Y, pro-rata Y, gender premium N.</summary>
public sealed record PlanFeeFlags(bool RoundUp = true, bool ProRate = true, bool GenderPremium = false);

public sealed record DiscountRates(decimal Policy = 0, decimal Renewal = 0);

/// <summary>
/// What to price. <see cref="RateDate"/> picks the rate rows (effective on or before that date); the pro-rata period runs from
/// <see cref="PeriodFrom"/> to <see cref="PeriodTo"/> inclusive. Adjustment uses the payor effective date for both; reinstatement uses the lapsed date.
/// </summary>
public sealed record FeeRequest(
    string Insurer, string Health, string Payor, string Plan, string AgeCode, string AdultChild, string? Sex,
    DateOnly RateDate, DateOnly PeriodFrom, DateOnly PeriodTo, bool ApplyDiscounts);

public sealed record FeeResult(decimal BasicPremium, decimal Premium, decimal McoFee, PlanFeeFlags Flags, DiscountRates Discounts, int PeriodDays, decimal BasicMco = 0);

/// <summary>Raised when the rate tables have no row for the request (the legacy form showed "Age out of premium range" / "MCO information is not correct").</summary>
public sealed class FeeRateMissingException(string message) : Exception(message);

/// <summary>Reads rate rows from the PRM, MCO, PLN and DiscPlan tables.</summary>
public interface IFeeRateSource
{
    /// <summary>Latest PRM row effective on or before the date; null when none. Female amount is used only for plans with gender premium.</summary>
    Task<(decimal Amount, decimal? FemaleAmount)?> GetPremiumRateAsync(FeeRequest r, CancellationToken ct = default);

    /// <summary>Latest MCO row for the plan (else the plan-less row) effective on or before the date; null when none.</summary>
    Task<(decimal Adult, decimal Child)?> GetMcoRateAsync(FeeRequest r, CancellationToken ct = default);

    Task<PlanFeeFlags> GetPlanFlagsAsync(FeeRequest r, CancellationToken ct = default);
    Task<DiscountRates> GetDiscountsAsync(string plan, CancellationToken ct = default);
}

/// <summary>Pure arithmetic of the legacy fee functions (Medix.bas InsertMCOFees / InsertPremium and the form's rounding), on decimals.</summary>
public static class FeeMath
{
    public const int DaysInYear = 365;

    /// <summary>Inclusive number of days of the period: (to - from) + 1.</summary>
    public static int PeriodDays(DateOnly from, DateOnly to) => to.DayNumber - from.DayNumber + 1;

    /// <summary>Legacy Format(x, "#,##0.00"): two decimals, halves away from zero.</summary>
    public static decimal Money(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    /// <summary>Legacy VB Round(x): whole number, halves to even (banker's rounding).</summary>
    public static decimal WholeEven(decimal x) => Math.Round(x, 0, MidpointRounding.ToEven);

    /// <summary>InsertMCOFees: days x fee / 365.</summary>
    public static decimal ProRateMco(DateOnly from, DateOnly to, decimal fee) => PeriodDays(from, to) * fee / DaysInYear;

    /// <summary>InsertPremium: (days / 365) x premium.</summary>
    public static decimal ProRatePremium(DateOnly from, DateOnly to, decimal premium) => PeriodDays(from, to) / (decimal)DaysInYear * premium;

    /// <summary>Policy and renewal discounts are applied one after the other as percentages.</summary>
    public static decimal ApplyDiscounts(decimal premium, DiscountRates d)
    {
        if (d.Policy > 0) premium = premium * (100 - d.Policy) / 100;
        if (d.Renewal > 0) premium = premium * (100 - d.Renewal) / 100;
        return premium;
    }

    public static decimal Mco(decimal rate, PlanFeeFlags flags, DateOnly from, DateOnly to)
    {
        var fee = Money(rate);
        if (flags.ProRate) fee = Money(ProRateMco(from, to, fee));
        return flags.RoundUp ? WholeEven(fee) : fee;
    }

    public static (decimal Basic, decimal Premium) Premium(decimal rate, DiscountRates discounts, DateOnly from, DateOnly to)
    {
        var basic = Money(rate);
        return (basic, Money(ProRatePremium(from, to, Money(ApplyDiscounts(basic, discounts)))));
    }
}

/// <summary>Prices a member for a period. One implementation for Membership Adjustment, Reinstatement and anything else that quotes premium and MCO fee.</summary>
public sealed class FeeService(IFeeRateSource rates)
{
    public async Task<FeeResult> CalculateAsync(FeeRequest r, CancellationToken ct = default)
    {
        if (r.PeriodTo < r.PeriodFrom) throw new FeeRateMissingException("The period end date is before its start date.");
        var flags = await rates.GetPlanFlagsAsync(r, ct);

        var premiumRate = await rates.GetPremiumRateAsync(r, ct)
            ?? throw new FeeRateMissingException("Age out of premium range. Please check the premium table.");
        var rate = flags.GenderPremium && r.Sex == "F" && premiumRate.FemaleAmount is { } female ? female : premiumRate.Amount;
        var discounts = r.ApplyDiscounts ? await rates.GetDiscountsAsync(r.Plan, ct) : new DiscountRates();
        var (basic, premium) = FeeMath.Premium(rate, discounts, r.PeriodFrom, r.PeriodTo);

        decimal mco = 0, basicMco = 0;
        if (r.AdultChild is "A" or "C")
        {
            var mcoRate = await rates.GetMcoRateAsync(r, ct) ?? throw new FeeRateMissingException("MCO information is not correct. Please check the MCO table.");
            var mcoAmount = r.AdultChild == "A" ? mcoRate.Adult : mcoRate.Child;
            basicMco = FeeMath.Money(mcoAmount);
            mco = FeeMath.Mco(mcoAmount, flags, r.PeriodFrom, r.PeriodTo);
        }
        return new FeeResult(basic, premium, mco, flags, discounts, FeeMath.PeriodDays(r.PeriodFrom, r.PeriodTo), basicMco);
    }
}


