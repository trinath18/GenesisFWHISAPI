using Dapper;
using Genesis.Core.Data;
using Genesis.Core.Fees;
using Microsoft.Extensions.DependencyInjection;

namespace Genesis.Tests;

public class FeeMathTests
{
    private static readonly DateOnly Jan1 = new(2024, 1, 1), Dec31 = new(2024, 12, 31);

    [Fact]
    public void Period_days_are_inclusive()
    {
        Assert.Equal(366, FeeMath.PeriodDays(Jan1, Dec31));
        Assert.Equal(1, FeeMath.PeriodDays(Jan1, Jan1));
        Assert.Equal(365, FeeMath.PeriodDays(new(2023, 1, 1), new(2023, 12, 31)));
    }

    [Theory]
    [InlineData(2.675, 2.68)]
    [InlineData(2.674, 2.67)]
    [InlineData(0.005, 0.01)]
    public void Money_rounds_halves_away_from_zero_like_Format(double input, double expected) =>
        Assert.Equal((decimal)expected, FeeMath.Money((decimal)input));

    [Theory]
    [InlineData(2.5, 2)]
    [InlineData(3.5, 4)]
    [InlineData(0.5, 0)]
    [InlineData(100.27, 100)]
    public void Whole_rounding_is_bankers_rounding_like_VB_Round(double input, double expected) =>
        Assert.Equal((decimal)expected, FeeMath.WholeEven((decimal)input));

    [Fact]
    public void Mco_is_pro_rated_by_days_over_365_then_rounded_to_whole_only_when_the_plan_says_so()
    {
        // 366 days x 100 / 365 = 100.27
        Assert.Equal(100m, FeeMath.Mco(100m, new PlanFeeFlags(RoundUp: true, ProRate: true), Jan1, Dec31));
        Assert.Equal(100.27m, FeeMath.Mco(100m, new PlanFeeFlags(RoundUp: false, ProRate: true), Jan1, Dec31));
        Assert.Equal(100m, FeeMath.Mco(100m, new PlanFeeFlags(RoundUp: false, ProRate: false), Jan1, Dec31));
        Assert.Equal(0m, FeeMath.Mco(15m, new PlanFeeFlags(), Jan1, Jan1));
        Assert.Equal(0.04m, FeeMath.Mco(15m, new PlanFeeFlags(RoundUp: false), Jan1, Jan1));
    }

    [Fact]
    public void Discounts_apply_one_after_the_other_then_the_premium_is_pro_rated()
    {
        var (basic, premium) = FeeMath.Premium(1000m, new DiscountRates(12, 10), new(2023, 1, 1), new(2023, 12, 31));
        Assert.Equal(1000m, basic);
        Assert.Equal(792m, premium);
        Assert.Equal(1000m, FeeMath.Premium(1000m, new DiscountRates(), new(2023, 1, 1), new(2023, 12, 31)).Premium);
        Assert.Equal(495.89m, FeeMath.Premium(1000m, new DiscountRates(), new(2023, 1, 1), new(2023, 6, 30)).Premium);
    }
}

public class FeeServiceTests
{
    private sealed class FakeRates : IFeeRateSource
    {
        public (decimal, decimal?)? Premium = (1200m, 1500m);
        public (decimal, decimal)? Mco = (30m, 12m);
        public PlanFeeFlags Flags = new(RoundUp: false);
        public DiscountRates Discounts = new(10, 0);
        public Task<(decimal Amount, decimal? FemaleAmount)?> GetPremiumRateAsync(FeeRequest r, CancellationToken ct = default) => Task.FromResult(Premium is { } p ? ((decimal, decimal?)?)(p.Item1, p.Item2) : null);
        public Task<(decimal Adult, decimal Child)?> GetMcoRateAsync(FeeRequest r, CancellationToken ct = default) => Task.FromResult(Mco is { } m ? ((decimal, decimal)?)(m.Item1, m.Item2) : null);
        public Task<PlanFeeFlags> GetPlanFlagsAsync(FeeRequest r, CancellationToken ct = default) => Task.FromResult(Flags);
        public Task<DiscountRates> GetDiscountsAsync(string plan, CancellationToken ct = default) => Task.FromResult(Discounts);
    }

    private static FeeRequest Req(string sex = "M", string ac = "A", bool discounts = true) =>
        new("G", "N", "WM", "FH01", "05", ac, sex, new(2023, 1, 1), new(2023, 1, 1), new(2023, 12, 31), discounts);

    [Fact]
    public async Task Full_year_adult_male_with_discount()
    {
        var r = await new FeeService(new FakeRates()).CalculateAsync(Req());
        Assert.Equal((1200m, 1080m, 30m, 365), (r.BasicPremium, r.Premium, r.McoFee, r.PeriodDays));
    }

    [Fact]
    public async Task Gender_premium_picks_the_female_rate_only_when_the_plan_asks_for_it()
    {
        var plain = await new FeeService(new FakeRates()).CalculateAsync(Req("F"));
        Assert.Equal(1200m, plain.BasicPremium);
        var gendered = await new FeeService(new FakeRates { Flags = new(RoundUp: false, GenderPremium: true) }).CalculateAsync(Req("F"));
        Assert.Equal(1500m, gendered.BasicPremium);
        Assert.Equal(1200m, (await new FeeService(new FakeRates { Flags = new(GenderPremium: true) }).CalculateAsync(Req("M"))).BasicPremium);
    }

    [Fact]
    public async Task Child_uses_the_child_mco_and_discounts_can_be_switched_off()
    {
        var r = await new FeeService(new FakeRates()).CalculateAsync(Req(ac: "C", discounts: false));
        Assert.Equal((12m, 1200m), (r.McoFee, r.Premium));
    }

    [Fact]
    public async Task Missing_rates_are_reported_with_the_legacy_wording()
    {
        var ex1 = await Assert.ThrowsAsync<FeeRateMissingException>(() => new FeeService(new FakeRates { Premium = null }).CalculateAsync(Req()));
        Assert.Contains("premium", ex1.Message);
        var ex2 = await Assert.ThrowsAsync<FeeRateMissingException>(() => new FeeService(new FakeRates { Mco = null }).CalculateAsync(Req()));
        Assert.Contains("MCO", ex2.Message);
        Assert.Equal(0m, (await new FeeService(new FakeRates { Mco = null }).CalculateAsync(Req(ac: ""))).McoFee);
        await Assert.ThrowsAsync<FeeRateMissingException>(() => new FeeService(new FakeRates()).CalculateAsync(Req() with { PeriodTo = new(2022, 1, 1) }));
    }
}

public class FeeRateSourceTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private FeeService Fees() => factory.Services.CreateScope().ServiceProvider.GetRequiredService<FeeService>();

    private async Task Seed()
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        await main.ExecuteAsync("""
            INSERT INTO PRM (INSCode, HLTCode, PAYCode, PRMAmount, PRMAmountFemale, AGECode, PLNCode, PRMEffDate) VALUES
              ('G', 'N', 'ZZ', 800, 900, '05', 'ZZPL', '2019-01-01'),
              ('G', 'N', 'ZZ', 1000, 1200, '05', 'ZZPL', '2022-01-01'),
              ('G', 'N', 'QQ', 5, 5, '05', 'ZZPL', '2022-01-01')
            """);
        await main.ExecuteAsync("""
            INSERT INTO MCO (HLTCode, INSCode, PLNCode, MCOAmountAdult, MCOAmountChild, MCOEffDate) VALUES
              ('N', 'G', NULL, 10, 5, '2000-01-01'),
              ('N', 'G', 'ZZPL', 30, 12, '2022-01-01')
            """);
        await main.ExecuteAsync("INSERT INTO PLN (PLNCode, HLTCode, PAYCode, PLNEffDate, PLNRoundUpStatus, PLNProRate, PLNPremGenderStatus) VALUES ('ZZPL', 'N', 'ZZ', '2020-01-01', 'N', 'Y', 'Y')");
        await using var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        await maint.ExecuteAsync("INSERT INTO DiscPlan (PLNCode, PolicyDisc, RenewalDisc) VALUES ('ZZPL', 12, 10)");
    }

    private static FeeRequest Req(DateOnly rate, string sex = "M", string plan = "ZZPL") =>
        new("G", "N", "ZZ", plan, "05", "A", sex, rate, new(2023, 1, 1), new(2023, 12, 31), true);

    [DatabaseFact]
    public async Task Rates_come_from_the_latest_row_effective_on_the_rate_date_for_the_right_payor()
    {
        await Seed();
        var r = await Fees().CalculateAsync(Req(new(2023, 1, 1)));
        Assert.Equal((1000m, 792m, 30m), (r.BasicPremium, r.Premium, r.McoFee));
        Assert.Equal(new DiscountRates(12, 10), r.Discounts);
        Assert.True(r.Flags.GenderPremium && !r.Flags.RoundUp);

        Assert.Equal(800m, (await Fees().CalculateAsync(Req(new(2021, 6, 1)))).BasicPremium);
        Assert.Equal(1200m, (await Fees().CalculateAsync(Req(new(2023, 1, 1), "F"))).BasicPremium);
    }

    [DatabaseFact]
    public async Task Mco_falls_back_to_the_plan_less_row_and_missing_rates_are_reported()
    {
        await Seed();
        var fallback = await Fees().CalculateAsync(Req(new(2023, 1, 1), plan: "ZZPL") with { Plan = "ZZPL" });
        Assert.Equal(30m, fallback.McoFee);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
            await main.ExecuteAsync("DELETE FROM MCO WHERE PLNCode = 'ZZPL'");
        Assert.Equal(10m, (await Fees().CalculateAsync(Req(new(2023, 1, 1)))).McoFee);

        await Assert.ThrowsAsync<FeeRateMissingException>(() => Fees().CalculateAsync(Req(new(2018, 1, 1))));
        await Assert.ThrowsAsync<FeeRateMissingException>(() => Fees().CalculateAsync(Req(new(2023, 1, 1), plan: "NOPE")));
    }
}

