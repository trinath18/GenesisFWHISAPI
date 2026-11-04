namespace GenesisFIS.Core.Bordereaux;

/// <summary>
/// Claim Bordx > Print Bordx (legacy FrmPrintBordx.DetailPrint, Excel template Template\Claim\BordxDetailsTemplate.xlt; the Crystal summaries
/// ClaimBordx_Summ_*.rpt were for the HIS payors). Neither the template nor the reports are available, so the printout is a drafted layout with the
/// fields the template was filled with: a cover with the payor and the bordereau totals, then the claims.
/// </summary>
public sealed record BordxPrintClaim(
    int No, string? PolicyNo, string? Patient, string? IcBcPp, string CaseNumber, int Version, DateOnly? Admitted, DateOnly? Discharged, string? Hospital,
    string? PayTo, string? Diagnosis, string? Mrn, string? ClaimId, decimal HospitalCharges, decimal MedicalReport, decimal Total);

public sealed record BordxPrintPayor(string Code, string? Name, string? Address1, string? Address2, string? Address3, string? Contact, string? Phone, string? Fax);

public sealed record BordxPrint(string BordxNo, DateOnly? BordxDate, BordxPrintPayor Payor, IReadOnlyList<BordxPrintClaim> Claims)
{
    public decimal HospitalCharges => Claims.Sum(c => c.HospitalCharges);
    public decimal MedicalReport => Claims.Sum(c => c.MedicalReport);
    public decimal Total => Claims.Sum(c => c.Total);
}

public static class BordxPrintRules
{
    /// <summary>Most bordereaux one printout holds.</summary>
    public const int MaxBordereaux = 200;

    /// <summary>Legacy: a government hospital claim (CASPAYType "C") is paid to "PENGARAH &lt;hospital&gt;", otherwise to the claim's pay-to.</summary>
    public static string? PayTo(string? casPayType, string? hospital, string? claimPayTo) =>
        casPayType?.Trim() == "C" ? $"PENGARAH {hospital?.Trim()}".TrimEnd() : claimPayTo?.Trim();

    /// <summary>Legacy amounts: the total is what the claim pays the hospital (CLMPayableByMedix2H); hospital charges = that less the medical report fee.</summary>
    public static (decimal HospitalCharges, decimal MedicalReport, decimal Total) Amounts(decimal payableToHospital, decimal medical) =>
        (payableToHospital - medical, medical, payableToHospital);
}

public interface IBordxPrintRepository
{
    /// <summary>The bordereaux with their payor and claims (View_PrintBordx, one line per claim, in case order); unknown numbers are left out.</summary>
    Task<IReadOnlyList<BordxPrint>> LoadAsync(IReadOnlyCollection<string> bordxNos, CancellationToken ct = default);
}
