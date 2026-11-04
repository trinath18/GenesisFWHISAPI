using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GenesisFIS.Reports.Membership;

/// <summary>
/// Address labels for Membership Tracking (Simple) > Print Label: A4 sheets of 3 x 7 labels, 63.5 x 38.1 mm (the common L7160 / J8160 layout).
/// The legacy form only listed the addresses; change the sizes here for other label stock.
/// </summary>
public static class MemberLabelSheet
{
    static MemberLabelSheet() => QuestPDF.Settings.License = LicenseType.Community;

    private const float LabelWidth = 63.5f, LabelHeight = 38.1f, TopMargin = 15.15f, SideMargin = 7.2f, Gap = 2.5f;

    public static byte[] ToPdf(IReadOnlyList<IReadOnlyList<string>> labels) => Document.Create(doc => doc.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.MarginVertical(TopMargin, Unit.Millimetre); page.MarginHorizontal(SideMargin, Unit.Millimetre);
        page.DefaultTextStyle(x => x.FontSize(9));
        page.Content().Table(t =>
        {
            t.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(LabelWidth, Unit.Millimetre); cd.ConstantColumn(Gap, Unit.Millimetre);
                cd.ConstantColumn(LabelWidth, Unit.Millimetre); cd.ConstantColumn(Gap, Unit.Millimetre);
                cd.ConstantColumn(LabelWidth, Unit.Millimetre);
            });
            for (var i = 0; i < labels.Count; i++)
            {
                if (i % 3 != 0) t.Cell().Height(LabelHeight, Unit.Millimetre);
                var label = labels[i];
                t.Cell().Height(LabelHeight, Unit.Millimetre).PaddingHorizontal(4, Unit.Millimetre).AlignMiddle().Column(c =>
                {
                    c.Item().Text(label[0]).Bold();
                    foreach (var line in label.Skip(1)) c.Item().Text(line);
                });
            }
        });
    })).GeneratePdf();
}
