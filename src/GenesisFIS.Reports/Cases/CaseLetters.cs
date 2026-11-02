using GenesisFIS.Core.Cases;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GenesisFIS.Reports.Cases;

/// <summary>
/// Replaces the Word letter templates of the legacy Case forms (New LG-2.DOT, BTBG.DOT, REPLetter.DOT and the policy ending / expired letters).
/// The layout and wording here are a drafted standard letter that prints the same fields the legacy bookmarks filled; swap in your approved wording if it differs.
/// </summary>
public static class CaseLetters
{
    static CaseLetters() => QuestPDF.Settings.License = LicenseType.Community;

    private static string D(DateOnly? d) => d?.ToString("dd/MM/yyyy") ?? "-";
    private static string V(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s;

    public static string Title(string kind) => kind switch
    {
        CaseLetterKinds.Lg => "LETTER OF GUARANTEE",
        CaseLetterKinds.Btbg => "BACK-TO-BACK LETTER OF GUARANTEE",
        CaseLetterKinds.Repudiation => "NOTICE OF REPUDIATION",
        CaseLetterKinds.PolicyEnding => "POLICY ENDING NOTICE",
        CaseLetterKinds.DocRequestAdmission => "REQUEST FOR DOCUMENTS - ADMISSION",
        CaseLetterKinds.DocRequestDischarge => "REQUEST FOR DOCUMENTS - DISCHARGE",
        CaseLetterKinds.TakeHomeDrugs => "TAKE HOME DRUGS",
        _ => "POLICY EXPIRED NOTICE"
    };

    public static byte[] ToPdf(CaseLetterData d) => Document.Create(doc => doc.Page(page =>
    {
        page.Size(PageSizes.A4); page.Margin(40); page.DefaultTextStyle(x => x.FontSize(10));
        page.Header().Column(c =>
        {
            c.Item().Text(Title(d.Kind)).FontSize(15).Bold();
            c.Item().Text($"Date: {D(d.Date)}    Case No: {d.CaseNumber}" + (d.LgNumber is null ? "" : $"    Ref: {d.LgNumber}")).FontColor(Colors.Grey.Darken2);
            c.Item().PaddingBottom(8).LineHorizontal(1);
        });
        page.Content().Column(c =>
        {
            c.Spacing(8);
            switch (d.Kind)
            {
                case CaseLetterKinds.Lg or CaseLetterKinds.Btbg:
                    c.Item().Text($"To: {V(d.Hospital)}\nAttention: {V(d.HospitalContact)}    Fax: {V(d.HospitalFax)}");
                    Fields(c, ("Patient", d.PatientName), ("Member No", d.MemberNumber), ("Policy No", d.PolicyNo), ("Date of admission", D(d.Admitted)), ("Room and board", d.RoomAndBoard), ("Guaranteed amount (RM)", d.LgAmount?.ToString("N2")), ("Remark", d.LgRemark));
                    c.Item().Text(d.Kind == CaseLetterKinds.Btbg
                        ? "We confirm the above admission is covered on a back-to-back basis, subject to the terms, conditions and limits of the policy. Please submit your bill to us on discharge."
                        : "We guarantee payment of the eligible hospital charges for the above admission up to the amount stated, subject to the terms, conditions, exclusions and limits of the policy.");
                    c.Item().Text("This guarantee is issued by " + d.IssuedBy + " and is valid for this admission only.").FontColor(Colors.Grey.Darken2);
                    break;
                case CaseLetterKinds.Repudiation:
                    c.Item().Text($"{V(d.PrincipalName)}\n{string.Join(", ", new[] { d.Address1, d.Address2, d.PostCode, d.City, d.State }.Where(x => !string.IsNullOrWhiteSpace(x)))}");
                    Fields(c, ("Insured", d.PrincipalName), ("Patient", d.PatientName), ("Member No", d.MemberNumber), ("Policy No", d.PolicyNo), ("Policy expiry", D(d.PolicyExpiry)), ("Hospital", d.Hospital), ("Date of admission", D(d.Admitted)), ("Payor", d.Payor), ("Contact", d.PayorContact));
                    c.Item().Text($"We regret to inform you that the claim for the above admission cannot be accepted. We return {V(d.Document)} to {V(d.Personnel)}. {V(d.Attention)}.");
                    foreach (var r in d.Repudiation)
                        c.Item().Border(0.5f).Padding(6).Column(x => { x.Item().Text($"{r.Code} - {V(r.Category)}").Bold(); if (r.CategoryDescription is not null) x.Item().Text(r.CategoryDescription); if (r.Reason is not null) x.Item().Text("Reason: " + r.Reason); });
                    if (d.Underlying is not null) c.Item().Text("Underlying: " + d.Underlying);
                    break;
                case CaseLetterKinds.PolicyEnding:
                    c.Item().Text($"{V(d.Payor)}\n{string.Join(", ", new[] { d.PayorAddress1, d.PayorAddress2, d.PayorAddress3 }.Where(x => !string.IsNullOrWhiteSpace(x)))}");
                    Fields(c, ("Patient", d.PatientName), ("Member No", d.MemberNumber), ("Policy No", d.PolicyNo), ("Policy expiry", D(d.PolicyExpiry)), ("Hospital", d.Hospital), ("Admitted", D(d.Admitted)), ("Discharged", D(d.Discharged)), ("Diagnosis / reason", d.AdmissionReason));
                    c.Item().Text($"The policy of the above patient ends in {d.PolicyExpiry?.ToString("MMMM yyyy") ?? "-"}. Please confirm whether the policy will be renewed so that the admission can be processed.");
                    break;
                case CaseLetterKinds.DocRequestAdmission or CaseLetterKinds.DocRequestDischarge:
                    c.Item().Text($"To: {V(d.Hospital)}\nAttention: {V(d.HospitalContact)}    Fax: {V(d.HospitalFax)}");
                    Fields(c, ("Patient", d.PatientName), ("IC No", d.PatientIc), ("Member No", d.MemberNumber), ("Date of admission", D(d.Admitted)), ("Date of discharge", D(d.Discharged)));
                    c.Item().Text(d.Kind == CaseLetterKinds.DocRequestAdmission
                        ? "To process the above admission, please send us the admission report, the doctor's notes and the estimated bill."
                        : "To process the above claim, please send us the final bill, the discharge summary and the itemised charges.");
                    c.Item().Text("Requested by " + d.IssuedBy).FontColor(Colors.Grey.Darken2);
                    break;
                case CaseLetterKinds.TakeHomeDrugs:
                    Fields(c, ("Patient", d.PatientName), ("Member No", d.MemberNumber), ("Policy No", d.PolicyNo), ("Date of admission", D(d.Admitted)), ("Date of discharge", D(d.Discharged)));
                    c.Item().Text("Take-home drugs supplied on discharge are covered only as provided by the policy; any amount not covered is to be settled by the member with the hospital.");
                    c.Item().Text("Prepared by " + d.IssuedBy).FontColor(Colors.Grey.Darken2);
                    break;
                default:
                    c.Item().Text($"{V(d.Payor)}\n{string.Join(", ", new[] { d.PayorAddress1, d.PayorAddress2, d.PayorAddress3 }.Where(x => !string.IsNullOrWhiteSpace(x)))}");
                    Fields(c, ("Patient", d.PatientName), ("Member No", d.MemberNumber), ("Policy No", d.PolicyNo), ("Policy expired on", D(d.PolicyExpiry)), ("Hospital", d.Hospital), ("Admitted", D(d.Admitted)), ("Discharged", D(d.Discharged)), ("Diagnosis / reason", d.AdmissionReason));
                    c.Item().Text("The policy of the above patient had expired before this admission. Please advise whether the admission is to be covered.");
                    break;
            }
        });
        page.Footer().Text("Generated by Genesis FIS").FontSize(8).FontColor(Colors.Grey.Medium);
    })).GeneratePdf();

    private static void Fields(ColumnDescriptor c, params (string Label, string? Value)[] rows)
    {
        foreach (var (label, value) in rows)
            c.Item().Row(r => { r.ConstantItem(140).Text(label).FontColor(Colors.Grey.Darken2); r.RelativeItem().Text(V(value)); });
    }
}
