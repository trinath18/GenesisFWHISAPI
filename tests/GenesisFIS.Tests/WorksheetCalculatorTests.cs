using Dapper;
using GenesisFIS.Core.Cases;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GenesisFIS.Tests;

public class WorksheetCalculatorTests
{
    private static readonly WorksheetBenefit[] Fh01 =
    [
        new("101", 30, "160", null), new("102", 15, "360", null), new("103", null, null, "FULL"), new("203", null, null, "FULL"), new("402", null, null, "FULL"), new("801", null, "120", "FULL"),
    ];

    private static WorksheetInput Input(Dictionary<int, decimal> actual, Dictionary<int, decimal>? hospital = null, WorksheetDays? days = null, bool member = false, decimal? balance = null,
        bool claimable = true, WorksheetDays? same = null, Dictionary<int, string>? overrides = null) =>
        new(actual, hospital ?? actual, days ?? WorksheetDays.None, Fh01, same, claimable, member, balance, overrides);

    [Fact]
    public void Legacy_grid_text_reads_and_writes_like_vb_format()
    {
        Assert.Equal(("-", "1,234.50", "0.50", "(120.00)"), (WorksheetCalculator.Money(0), WorksheetCalculator.Money(1234.5m), WorksheetCalculator.Money(0.5m), WorksheetCalculator.Money(-120)));
        Assert.Equal((1234.5m, -120m, 0.5m), (WorksheetCalculator.Parse("1,234.50"), WorksheetCalculator.Parse("(120.00)"), WorksheetCalculator.Parse(".50")));
        Assert.Null(WorksheetCalculator.Parse("FULL")); Assert.Null(WorksheetCalculator.Parse("-"));
        Assert.Equal(("FULL", "500.00", null), (WorksheetCalculator.NormaliseLimit("full"), WorksheetCalculator.NormaliseLimit("500"), WorksheetCalculator.NormaliseLimit("abc")));
    }

    [Fact]
    public void Approve_follows_the_legacy_get_min()
    {
        Assert.Equal((100m, 80m, 0m, 50m, 0m), (WorksheetCalculator.Approve(100, "FULL"), WorksheetCalculator.Approve(100, "80.00"), WorksheetCalculator.Approve(100, "-"),
            WorksheetCalculator.Approve(50, "(120.00)"), WorksheetCalculator.Approve(-200, "-")));
        Assert.Equal(-120m, WorksheetCalculator.Approve(-200, "(120.00)"));
    }

    [Fact]
    public void Room_and_icu_are_limited_by_days_and_daily_rate()
    {
        var c = WorksheetCalculator.Calculate(Input(new() { [1] = 6000, [2] = 1000, [3] = 700, [21] = 120 }, days: new(40, 2, 0, 0, 0)));
        var row = c.Lines.ToDictionary(l => l.Row);
        Assert.Equal(("4,800.00", "4,800.00"), (row[1].Limit, row[1].Approved));   // 30 days x 160
        Assert.Equal(("720.00", "720.00", "(280.00)"), (row[2].Limit, row[2].Approved, row[2].Member)); // 2 days x 360; the member owes the rest
        Assert.Equal(("FULL", "700.00"), (row[3].Limit, row[3].Approved));
        Assert.Equal(("-", "-"), (row[4].Limit, row[4].Approved));                  // no benefit for the theatre
        Assert.Equal((30m, 2m), (c.DaysAllowed.Board, c.DaysAllowed.Icu));
        Assert.Equal((7820m, 6340m, 6340m, -1480m, 0m), (c.TotalActual, c.TotalApproved, c.PayableToHospital, c.Excess, c.PayableToMember));
    }

    [Fact]
    public void Same_limit_days_override_the_plan_cap_and_claims_officers_can_type_limits()
    {
        var same = WorksheetCalculator.Calculate(Input(new() { [1] = 1600 }, days: new(10, 0, 0, 0, 0), same: new(4, 0, 0, 0, 0)));
        Assert.Equal("640.00", same.Lines[0].Limit);
        var typed = WorksheetCalculator.Calculate(Input(new() { [1] = 1600, [3] = 900 }, days: new(10, 0, 0, 0, 0), overrides: new() { [3] = "500", [1] = "full" }));
        Assert.Equal(("FULL", "1,600.00", "500.00", "500.00"), (typed.Lines[0].Limit, typed.Lines[0].Approved, typed.Lines[2].Limit, typed.Lines[2].Approved));
        // A negative amount typed on an empty row is a deduction (legacy FGrid_Click).
        var deduct = WorksheetCalculator.Calculate(Input(new() { [3] = 900 }, overrides: new() { [25] = "-250" }));
        Assert.Equal(("(250.00)", "(250.00)", 650m), (deduct.Lines[24].Limit, deduct.Lines[24].Approved, deduct.TotalApproved));
    }

    [Fact]
    public void Repudiated_cases_get_nothing_and_the_annual_balance_caps_the_final_amount()
    {
        var rr = WorksheetCalculator.Calculate(Input(new() { [3] = 900 }, claimable: false));
        Assert.Equal((0m, "-"), (rr.TotalApproved, rr.Lines[2].Limit));
        var capped = WorksheetCalculator.Calculate(Input(new() { [3] = 25000 }, balance: 20000));
        Assert.Equal((25000m, 20000m, true, 20000m, -5000m), (capped.TotalApproved, capped.FinalApproved, capped.LimitExceeded, capped.PayableToHospital, capped.Excess));
    }

    [Fact]
    public void With_member_invoices_the_hospital_gets_its_bill_and_the_member_the_rest()
    {
        var c = WorksheetCalculator.Calculate(Input(new() { [3] = 1000, [21] = 120 }, hospital: new() { [3] = 600 }, member: true));
        Assert.Equal((600m, 520m, 0m), (c.PayableToHospital, c.PayableToMember, c.Excess));
    }
}

/// <summary>
/// The calculator checked against the worksheets the legacy system saved. Each recent completed worksheet is recalculated from its own invoices through the
/// production loader; cases with a same-limit carry-over are left out because ClaimSameLimit only keeps the days left after the last save.
/// </summary>
public class WorksheetCalculatorParityTests(WriteApiFactory factory, ITestOutputHelper output) : IClassFixture<WriteApiFactory>
{
    private static decimal N(string? s) => WorksheetCalculator.Parse(s) ?? 0;
    private static bool SameLimit(string? a, string? b) =>
        string.Equals(a?.Trim(), "FULL", StringComparison.OrdinalIgnoreCase) || string.Equals(b?.Trim(), "FULL", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase) : N(a) == N(b);

    [DatabaseFact]
    public async Task Recalculated_worksheets_match_the_saved_legacy_amounts()
    {
        using var conn = new SqlConnection(TestHost.Config()["ConnectionStrings:Worksheet"]);
        conn.Open();
        var claims = conn.Query("""
            SELECT TOP 300 c.CASNumber, c.ClaimVersion, c.CLMWSStatus, c.CLMTotalApproved, c.CLMPayableByMedix2H, c.CLMPayableByMedix2M, c.CLMExcess FROM Claim c
            WHERE c.ClaimStatus = 'C' AND NOT EXISTS (SELECT 1 FROM ClaimSameLimit s WHERE s.CASNumber = c.CASNumber)
              AND EXISTS (SELECT 1 FROM ClaimInvoice i WHERE i.CASNumber = c.CASNumber AND i.INVWSversion = CAST(c.ClaimVersion AS nvarchar))
            ORDER BY c.ClaimOpenDate DESC
            """).Cast<IDictionary<string, object?>>().ToList();
        if (claims.Count == 0) return;

        using var scope = factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorksheetRepository>();
        int cells = 0, limitOk = 0, approvedOk = 0, memberOk = 0, totals = 0, totalOk = 0, payOk = 0, payChecked = 0;
        var misses = new List<string>();
        foreach (var c in claims)
        {
            var no = ((string)c["CASNumber"]!).Trim(); var v = (int)c["ClaimVersion"]!;
            if (await repo.InputAsync(no, v, openOnly: false) is not { } input) continue;
            var limit = conn.QuerySingle("SELECT * FROM ClaimLimit WHERE CASNumber = @no AND CLMVersion = @v", new { no, v }) as IDictionary<string, object?>;
            var approve = conn.QuerySingle("SELECT * FROM ClaimApprove WHERE CASNumber = @no AND CLMVersion = @v", new { no, v }) as IDictionary<string, object?>;
            var member = conn.QuerySingle("SELECT * FROM ClaimMember WHERE CASNumber = @no AND CLMVersion = @v", new { no, v }) as IDictionary<string, object?>;
            string? Cell(IDictionary<string, object?> r, WorksheetLine l) => r.TryGetValue(l.Column, out var x) ? Convert.ToString(x) : null;

            // The case's claimability can change after the worksheet is saved; the worksheet status records what it was then.
            input = input with { AnnualBalance = null, Claimable = !WorksheetCalculator.NotClaimable.Contains(Convert.ToString(c["CLMWSStatus"])?.Trim() ?? "") };
            var ours = WorksheetCalculator.Calculate(input);
            // Feed the stored limits back in (as the officer's typed limits) so approved and member amounts are compared on the same limits.
            var typed = WorksheetCatalog.Lines.Where(l => Cell(limit, l) is { } s && WorksheetCalculator.NormaliseLimit(s) is not null && !SameLimit(s, ours.Lines[l.Row - 1].Limit))
                .ToDictionary(l => l.Row, l => Cell(limit, l)!);
            var replay = input.Claimable ? WorksheetCalculator.Calculate(input with { LimitOverrides = typed }) : ours;
            foreach (var l in WorksheetCatalog.Lines.Where(l => limit.ContainsKey(l.Column)))
            {
                cells++;
                // On a row with nothing charged the limit text does not matter (some old worksheets stored 0.00 where the plan now says FULL).
                if (SameLimit(Cell(limit, l), ours.Lines[l.Row - 1].Limit) || input.Actual[l.Row] == 0 && N(Cell(approve, l)) == 0) limitOk++; else if (misses.Count < 15) misses.Add($"{no}-{v} r{l.Row} limit {Cell(limit, l)} vs {ours.Lines[l.Row - 1].Limit}");
                if (N(Cell(approve, l)) == N(replay.Lines[l.Row - 1].Approved)) approvedOk++;
                if (N(Cell(member, l)) == N(replay.Lines[l.Row - 1].Member)) memberOk++;
            }
            totals++;
            if ((decimal)c["CLMTotalApproved"]! == replay.TotalApproved) totalOk++;
            else if (misses.Count < 40) misses.Add($"{no}-{v} total {c["CLMTotalApproved"]} vs {replay.TotalApproved} (claimable {input.Claimable}; rows {string.Join(",", WorksheetCatalog.Lines.Where(l => N(Cell(approve, l)) != N(replay.Lines[l.Row - 1].Approved)).Select(l => $"r{l.Row} {Cell(approve, l)}/{replay.Lines[l.Row - 1].Approved}"))})");
            if ((decimal)c["CLMTotalApproved"]! == replay.FinalApproved)
            {
                payChecked++;
                if ((decimal)c["CLMPayableByMedix2H"]! == replay.PayableToHospital && (decimal)c["CLMPayableByMedix2M"]! == replay.PayableToMember) payOk++;
            }
        }
        output.WriteLine($"worksheets {totals}: limits {limitOk}/{cells}, approved {approvedOk}/{cells}, member {memberOk}/{cells}, total approved {totalOk}/{totals}, payables {payOk}/{payChecked}");
        foreach (var m in misses) output.WriteLine(m);
        Assert.True(limitOk >= cells * 0.98, $"limits {limitOk}/{cells}: {string.Join("; ", misses)}");
        Assert.True(approvedOk >= cells * 0.999, $"approved {approvedOk}/{cells}");
        Assert.True(memberOk >= cells * 0.999, $"member {memberOk}/{cells}");
        Assert.True(totalOk >= totals * 0.99, $"total approved {totalOk}/{totals}");
        Assert.True(payOk >= payChecked * 0.99, $"payables {payOk}/{payChecked}");
    }
}
