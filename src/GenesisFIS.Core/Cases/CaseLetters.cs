namespace GenesisFIS.Core.Cases;

/// <summary>Guarantee letter (LG) request for a registered case.</summary>
public sealed record CaseLgRequest(decimal? Amount, string? Time, string? Remark);

public sealed record CaseLgResult(string LgNumber, int Sequence, string Letter, decimal Balance, decimal Amount);

public sealed record CaseRepudiationText(string Code, string? Category, string? CategoryDescription, string? Reason);

/// <summary>Everything a case letter prints, read from the saved case (replaces the Word bookmarks of the legacy templates).</summary>
public sealed record CaseLetterData(
    string Kind, string CaseNumber, string MemberNumber, string? PolicyNo, string PatientName, string? PatientIc, string PrincipalName, string? Address1, string? Address2, string? PostCode, string? City, string? State,
    string? Hospital, string? HospitalContact, string? HospitalFax, DateOnly? Admitted, DateOnly? Discharged, DateOnly? PolicyExpiry, string? Payor, string? PayorAddress1, string? PayorAddress2, string? PayorAddress3, string? PayorContact,
    string? AdmissionReason, string? LgNumber, decimal? LgAmount, string? RoomAndBoard, string? LgRemark, string IssuedBy, DateOnly Date,
    IReadOnlyList<CaseRepudiationText> Repudiation, string? Document, string? Personnel, string? Attention, string? Underlying);

/// <summary>What a draft letter is printed from: the member, patient, hospital and dates on the form, and the case number once the case is saved.</summary>
public sealed record CaseDraftLetterRequest(string? MemberNumber, string? PatientId, string? HospitalCode, string? HospitalName, DateOnly? Admitted, DateOnly? Discharged, string? CaseNumber);

public static class CaseLetterKinds
{
    public const string Lg = "lg", Btbg = "btbg", Repudiation = "repudiation", PolicyEnding = "policy-ending", Expired = "expired";
    public static readonly string[] All = [Lg, Btbg, Repudiation, PolicyEnding, Expired];

    /// <summary>
    /// Letters the legacy Case Registration form printed from what is on screen, before or after the case is saved: the document requests to the hospital on
    /// admission (Doc Req Adm, letter A) and on discharge (Doc Req Discharge, letter D), and the take-home drugs letter (T.Home Drugs, letter T).
    /// </summary>
    public const string DocRequestAdmission = "doc-request-admission", DocRequestDischarge = "doc-request-discharge", TakeHomeDrugs = "take-home-drugs";
    public static readonly string[] Drafts = [DocRequestAdmission, DocRequestDischarge, TakeHomeDrugs];

    /// <summary>Letters the legacy form printed straight after registration: policy ending within 45 days (P), expired policy (E) and the repudiation letter (R).</summary>
    public static IReadOnlyList<string> AfterRegistration(DateOnly today, DateOnly? expiry, bool repudiated)
    {
        var l = new List<string>();
        if (expiry is { } e) { var days = e.DayNumber - today.DayNumber; if (days is >= 0 and <= 45) l.Add(PolicyEnding); else if (days < 0) l.Add(Expired); }
        if (repudiated) l.Add(Repudiation);
        return l;
    }
}

public static class CaseLgRules
{
    public static (string Field, string Message)? Validate(CaseLgRequest r)
    {
        if (r.Amount is null) return ("amount", "Please Enter Reserve Amount");
        if (r.Amount <= 0 || r.Amount > 922337203685477m) return ("amount", "The LG amount must be more than zero.");
        if (string.IsNullOrWhiteSpace(r.Time)) return ("time", "Please Enter LG Time");
        if (!System.Text.RegularExpressions.Regex.IsMatch(r.Time.Trim(), @"^([01]\d|2[0-3]):[0-5]\d$")) return ("time", "Please enter the LG time as hh:mm.");
        if ((r.Remark?.Length ?? 0) > 100 || (r.Remark?.Any(char.IsControl) ?? false)) return ("remark", "The remark can be at most 100 characters.");
        return null;
    }
}
