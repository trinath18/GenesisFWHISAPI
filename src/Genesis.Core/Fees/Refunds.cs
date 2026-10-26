namespace Genesis.Core.Fees;

public sealed record RefundAmounts(decimal PremiumEffective, decimal PremiumReduction, decimal McoEffective, decimal McoRefund);

/// <summary>
/// Refund arithmetic of the legacy form (Medix.bas MCORefund / MCOPremiumReduction and the form's SaveMBMRefund). Shared by cancellation, plan change and supplementary cancellation.
/// The 2009 rows in your MBMRefund table confirm the premium formula (e.g. 364/365 x 60 = 59.84); their MCO refunds were computed with an older formula one day shorter,
/// the current source (used here) counts the cancel date as covered.
/// </summary>
public static class RefundMath
{
    /// <summary>MCORefund: (expiry - cancel + 1) / (expiry - effective + 1) x MCO fee, never negative.</summary>
    public static decimal McoRefund(DateOnly expiry, DateOnly cancel, DateOnly effective, decimal mco)
    {
        var total = expiry.DayNumber - effective.DayNumber + 1;
        return total <= 0 ? 0 : Math.Max(0, (expiry.DayNumber - cancel.DayNumber + 1) / (decimal)total * mco);
    }

    /// <summary>MCOPremiumReduction: only when the cancel date is before expiry: (expiry - cancel) / (expiry - effective + 1) x premium.</summary>
    public static decimal PremiumReduction(DateOnly expiry, DateOnly cancel, DateOnly effective, decimal premium)
    {
        var total = expiry.DayNumber - effective.DayNumber + 1;
        return cancel >= expiry || total <= 0 ? 0 : (expiry.DayNumber - cancel.DayNumber) / (decimal)total * premium;
    }

    /// <summary>
    /// The legacy form passes the stored premium and MCO fee through CInt (whole numbers, halves to even) before refunding, formats every value to two decimals
    /// and, for plans with round-up, rounds each result to a whole number.
    /// </summary>
    public static RefundAmounts Compute(DateOnly expiry, DateOnly cancel, DateOnly effective, decimal premium, decimal mco, bool roundUp)
    {
        premium = FeeMath.WholeEven(premium);
        mco = FeeMath.WholeEven(mco);
        decimal R(decimal v) => roundUp ? FeeMath.WholeEven(v) : v;

        var mcoRefund = R(FeeMath.Money(McoRefund(expiry, cancel, effective, mco)));
        var reduction = R(FeeMath.Money(PremiumReduction(expiry, cancel, effective, premium)));
        var premiumEffective = R(FeeMath.Money(premium - reduction));
        var mcoEffective = R(FeeMath.Money(mco - mcoRefund));
        return new RefundAmounts(premiumEffective, reduction, mcoEffective, mcoRefund);
    }
}
