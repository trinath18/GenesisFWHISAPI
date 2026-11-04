using GenesisFIS.Core.Bordereaux;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Tracking;
using GenesisFIS.Core.Writes;
using GenesisFIS.Reports.Cases;
using GenesisFIS.Reports.Membership;

namespace GenesisFIS.Api.Features.Tracking;

/// <summary>Reads a column of a tracking result by its catalog column name.</summary>
internal static class TrackingRows
{
    public static Func<object?[], string?> Reader(TrackingResult result, string column)
    {
        var i = result.Columns.ToList().FindIndex(c => c.Key == column);
        if (i < 0) throw new InvalidOperationException($"The listing has no column {column}.");
        return row => row[i]?.ToString();
    }
}

/// <summary>Membership Tracking (Simple) > Print Label: an address label for each member found that has an address.</summary>
internal sealed class MemberLabelPrinter(ITrackingService service) : ITrackingPrinter
{
    public string Key => "member-labels";

    public async Task<TrackingPrintFile> PrintAsync(TrackingDefinition definition, IReadOnlyDictionary<string, string> filters, string user, CancellationToken ct = default)
    {
        var result = await service.SearchAsync(definition, filters, TrackingDefinition.PrintMaxRows, ct);
        if (result.Truncated) throw new BusinessRuleException($"More than {TrackingDefinition.PrintMaxRows:N0} members found: narrow the search before printing labels.");
        var col = new[] { "MBMName", "MBMAddress1", "MBMAddress2", "MBMAddress3", "MBMPostCode", "CTYCode", "STACode" }.Select(c => TrackingRows.Reader(result, c)).ToArray();
        var labels = result.Rows.Select(r => MemberLabels.Lines(col[0](r), col[1](r), col[2](r), col[3](r), col[4](r), col[5](r), col[6](r))).OfType<IReadOnlyList<string>>().ToList();
        if (labels.Count == 0) throw new BusinessRuleException("None of the members found has an address.");
        return new TrackingPrintFile(MemberLabelSheet.ToPdf(labels), $"member-labels-{DateTime.Now:yyyyMMdd-HHmm}.pdf");
    }
}

/// <summary>Claim Bordx > Print Bordx: the bordereaux the listing finds, each with its cover and claim list.</summary>
internal sealed class BordxDetailsPrinter(ITrackingService service, IBordxPrintRepository repo) : ITrackingPrinter
{
    public string Key => "bordx-details";

    public async Task<TrackingPrintFile> PrintAsync(TrackingDefinition definition, IReadOnlyDictionary<string, string> filters, string user, CancellationToken ct = default)
    {
        var result = await service.SearchAsync(definition, filters, TrackingDefinition.PrintMaxRows, ct);
        var bordx = TrackingRows.Reader(result, "BordxRefNo");
        var numbers = result.Rows.Select(r => bordx(r)?.Trim()).OfType<string>().Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (result.Truncated || numbers.Count > BordxPrintRules.MaxBordereaux)
            throw new BusinessRuleException($"Too many bordereaux found: print at most {BordxPrintRules.MaxBordereaux} at a time (narrow the Bordx ref or date).");
        var bordereaux = await repo.LoadAsync(numbers, ct);
        if (bordereaux.Count == 0) throw BusinessRuleException.NotFound("No bordereau found to print.");
        var name = bordereaux.Count == 1 ? bordereaux[0].BordxNo : $"{bordereaux[0].BordxNo}-{bordereaux[^1].BordxNo}";
        return new TrackingPrintFile(BordxPrintDocument.ToPdf(bordereaux, user), $"Bordx-{name}.pdf");
    }
}
