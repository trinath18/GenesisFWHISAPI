namespace Genesis.Core.Membership;

public sealed record CoveredPerson(
    string CoverId, string? Name, string? IcBcPp, DateTime? DateOfBirth, string? Sex, string? AdultChild,
    string? Relationship, string? Status, string? PlanCode, string? MemberType, decimal? AnnualLimit,
    decimal? AvailableLimit, DateTime? EffectiveDate, DateTime? ExpiryDate, DateTime? CancelDate,
    decimal? Premium, DateTime? BordxDate, int? BatchNo);

public sealed record BenefitItem(string Code, string? Description, int NoOfDays, string? ClaimableAmount, string? RmPerDisability);

public sealed record BenefitListing(string? PlanCode, DateTime? EffectiveDate, IReadOnlyList<BenefitItem> Items);

public sealed record MemberBenefits(BenefitListing Principal, IReadOnlyList<BenefitListing> Supplementary);

public sealed record MemberHistoryItem(
    string MemberNumber, string? CoverId, string? TopupInd, string? TopupNumber, string? PolicyNo,
    DateTime? PayorEffDate, DateTime? PayorExpDate, string? DataStatus, string? Status, DateTime? BordxDate);

public sealed record MemberHistory(IReadOnlyList<MemberHistoryItem> ByIc, IReadOnlyList<MemberHistoryItem> ByNameDob)
{
    public bool CountsDiffer => ByIc.Count != ByNameDob.Count;
}

public sealed record MemberCaseHistory(IReadOnlyList<MemberCase> ByIc, IReadOnlyList<MemberCase> ByNameDob)
{
    public bool CountsDiffer => ByIc.Count != ByNameDob.Count;
}

public sealed record MemberFees(
    decimal? Premium, decimal? McoFee, decimal? AccessFee, decimal? BasicPremium, decimal? BasicMco,
    decimal? PayorMco, decimal? BilledMco);

public sealed record NonDisclosureItem(
    string MemberNumber, string? CaseNumber, string? CoverId, DateTime? Admitted, DateTime? PreIllnessDate,
    string? PreIllnessCategory, string? PreIllnessInformedBy, DateTime? UnderlyingIllnessDate,
    string? UnderlyingIllnessCategory, string? UnderlyingIllnessInformedBy);

public sealed record PolicyTerm(string Name, string? Value);

public sealed record LifetimeLimitRow(
    string MemberNumber, string? CoverId, string? Name, decimal AvailableLimit, decimal Balance,
    int CurrentCases, int HistoryCases, decimal CurrentBordxAmount, decimal HistoryBordxAmount);

public sealed record TakeoverClaim(
    DateTime? EffectiveDate, DateTime? ExpiryDate, string? PatientName, string? PatientIc, DateTime? Admitted,
    DateTime? Discharged, string? Hospital, string? Diagnosis, string? Doctors, decimal? BillAmount,
    decimal? ApprovedAmount, decimal? ExcessAmount);

public sealed record PolicyWordingFile(string Name, long Size);

public sealed record PolicyWordingListing(string? Folder, bool StoreConfigured, IReadOnlyList<PolicyWordingFile> Files);

public sealed record PolicyWordingContent(Stream Content, string ContentType, string FileName);

/// <summary>Where scanned policy wording documents live (a share today; blob storage in the cloud).</summary>
public interface IPolicyWordingStore
{
    bool IsConfigured { get; }
    IReadOnlyList<PolicyWordingFile> List(string folder);
    PolicyWordingContent? Open(string folder, string fileName);
}

public interface IMembershipTabsRepository
{
    Task<IReadOnlyList<CoveredPerson>> GetCoveredPersonsAsync(string number, CancellationToken ct = default);
    Task<MemberBenefits?> GetBenefitsAsync(string number, CancellationToken ct = default);
    Task<MemberHistory?> GetMemberHistoryAsync(string number, CancellationToken ct = default);
    Task<MemberCaseHistory?> GetCaseHistoryAsync(string number, CancellationToken ct = default);
    Task<MemberFees?> GetFeesAsync(string number, CancellationToken ct = default);
    Task<IReadOnlyList<NonDisclosureItem>> GetNonDisclosureAsync(string number, CancellationToken ct = default);
    Task<IReadOnlyList<PolicyTerm>> GetPolicyTermsAsync(string number, CancellationToken ct = default);
    Task<IReadOnlyList<LifetimeLimitRow>> GetLifetimeLimitAsync(string number, CancellationToken ct = default);
    Task<IReadOnlyList<TakeoverClaim>> GetTakeoverClaimsAsync(string number, CancellationToken ct = default);
    Task<string?> GetPolicyWordingFolderAsync(string number, CancellationToken ct = default);
}
