using Genesis.Core.Membership;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Genesis.Reports.Membership;

/// <summary>Replaces the Crystal report RptMbmEnquiry1.rpt (member enquiry summary).</summary>
public static class MemberEnquiryReport
{
    static MemberEnquiryReport() => QuestPDF.Settings.License = LicenseType.Community;

    private static string Date(DateTime? d) => d?.ToString("dd/MM/yyyy") ?? "";
    private static string Money(decimal? v) => v?.ToString("#,##0.00") ?? "";

    public static byte[] ToPdf(MemberDetail m, IReadOnlyList<MemberCase> cases, string generatedBy) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(28);
            page.DefaultTextStyle(t => t.FontSize(9));
            page.Header().Column(c =>
            {
                c.Item().Text("Member Enquiry").FontSize(16).SemiBold();
                c.Item().Text($"{m.Number} - {m.Name}").FontSize(11);
                c.Item().Text($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by {generatedBy}").FontColor(Colors.Grey.Darken1);
            });
            page.Content().PaddingVertical(8).Column(col =>
            {
                col.Spacing(10);
                col.Item().Element(c => Section(c, "Member", [
                    ("IC / BC / Passport", m.IcBcPp), ("Policy No", m.PolicyNo), ("Date of Birth", Date(m.DateOfBirth)), ("Sex", m.Sex),
                    ("Adult / Child", m.AdultChild), ("Member Type", m.MemberType), ("Status", m.Status), ("Data Status", m.DataStatus),
                    ("Expired", m.ExpiredStatus), ("Renewal", m.RenewFlag)]));
                col.Item().Element(c => Section(c, "Policy", [
                    ("Insurer", m.InsurerCode), ("Plan", m.PlanCode), ("Health Code", m.HealthCode), ("Payor", m.PayorCode),
                    ("Payor Effective", Date(m.PayorEffDate)), ("Payor Expiry", Date(m.PayorExpDate)), ("Final Expiry", Date(m.FinalExpiryDate)),
                    ("Bordx Date", Date(m.BordxDate)), ("Batch No", m.BatchNo), ("Value Date", Date(m.ValueDate)),
                    ("Available Annual Limit", m.AnnualLimitApplies ? Money(m.AvailableLimit) : "FULL")]));
                col.Item().Element(c => Section(c, "Group and address", [
                    ("Group Company", m.GroupCompany), ("Address", string.Join(", ", new[] { m.Address1, m.Address2, m.Address3, m.Address4 }.Where(x => !string.IsNullOrWhiteSpace(x)))),
                    ("City", m.City), ("Post Code", m.PostCode), ("State", m.State)]));
                if (!string.IsNullOrWhiteSpace(m.PlanSpecialNote))
                    col.Item().Element(c => Section(c, "Plan special note", [("", m.PlanSpecialNote)]));
                col.Item().Text($"Current cases ({cases.Count})").SemiBold();
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c => { c.ConstantColumn(60); c.RelativeColumn(2); c.ConstantColumn(55); c.ConstantColumn(55); c.RelativeColumn(3); c.ConstantColumn(30); });
                    t.Header(h => { foreach (var x in new[] { "Case No", "Patient", "Admitted", "Discharged", "Diagnosis", "Status" }) h.Cell().Background(Colors.Grey.Lighten3).Padding(2).Text(x).SemiBold(); });
                    foreach (var c in cases)
                        foreach (var v in new[] { c.CaseNumber, c.PatientName, Date(c.Admitted), Date(c.Discharged), c.Diagnosis, c.Status })
                            t.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(2).Text(v ?? "");
                });
            });
            page.Footer().AlignCenter().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        })).GeneratePdf();

    private static void Section(IContainer c, string title, (string Label, string? Value)[] fields) =>
        c.Column(col =>
        {
            col.Item().Text(title).SemiBold().FontSize(10);
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(d => { d.ConstantColumn(130); d.RelativeColumn(); d.ConstantColumn(130); d.RelativeColumn(); });
                foreach (var (label, value) in fields)
                {
                    t.Cell().Padding(2).Text(label).FontColor(Colors.Grey.Darken1);
                    t.Cell().Padding(2).Text(value ?? "");
                }
            });
        });
}
