using GenesisFIS.Core.Tracking;
using GenesisFIS.Infrastructure.Tracking;

namespace GenesisFIS.Tests;

/// <summary>Membership > Tracking (Simple): the listing and the summary share the legacy selection criteria and the legacy permission (position 6).</summary>
public class MemberSimpleTrackingTests
{
    private static readonly TrackingDefinition Listing = TrackingCatalog.Find("member-tracking-simple")!;
    private static readonly SummaryDefinition Summary = SummaryCatalog.Find("member-summary-simple")!;
    private static Dictionary<string, string> F(params (string, string)[] p) => p.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void Both_screens_read_the_simple_view_under_the_legacy_permission()
    {
        Assert.Equal(("VIEW_MBM_Search2_Simple", 6), (Listing.Source, Listing.ScreenPosition));
        Assert.Equal(("VIEW_MBM_Search2_Simple", 6), (Summary.Source, Summary.ScreenPosition));
        Assert.True(Listing.RequiresFilter);
        Assert.True(Summary.RequiresFilter);
    }

    [Fact]
    public void Summary_groups_by_the_chosen_columns_with_member_counts_and_fees()
    {
        var q = SummaryQueryBuilder.Build(Summary, new SummaryRequest(["payor", "renew", "effMonth"], ["members", "mco", "premium"], 20),
            F(("registered.from", "2021-01-01"), ("status", "A")));
        Assert.Contains("PAYCode AS [payor], MBMRenewFlag AS [renew], MONTH(MBMPayorEffDate) AS [effMonth]", q.Sql);
        Assert.Contains("COUNT(*) AS [members], SUM(MBMMCOFee) AS [mco], SUM(MBMPremium) AS [premium]", q.Sql);
        Assert.Contains("MBMRegDate >= @p", q.Sql);
        Assert.Contains("MBMStatus = @p", q.Sql);
        Assert.Contains("ORDER BY COUNT(*) DESC", q.Sql);
        Assert.Throws<TrackingFilterException>(() => SummaryQueryBuilder.Build(Summary, new SummaryRequest(["payor"], ["members"]), F()));
    }

    [Fact]
    public void Listing_offers_the_delay_and_amount_ranges_and_rejects_unlisted_choices()
    {
        var q = TrackingQueryBuilder.Build(Listing, F(("payor", "JE"), ("delayReg.from", "30"), ("sex", "F")), 10);
        Assert.Contains("DelayReg >= @p", q.Sql);
        Assert.Contains("MBMSex = @p", q.Sql);
        Assert.Throws<TrackingFilterException>(() => TrackingQueryBuilder.Build(Listing, F(("sex", "X")), 10));
        Assert.DoesNotContain(Summary.Filters, f => f.Key == "delayReg");
    }
}
