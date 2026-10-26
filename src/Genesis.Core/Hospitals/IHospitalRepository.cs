namespace Genesis.Core.Hospitals;

public sealed record HospitalListItem(
    string Code,
    string Name,
    string? Town,
    string? State,
    string? Telephone,
    string? Email,
    string? Status);

public interface IHospitalRepository
{
    Task<IReadOnlyList<HospitalListItem>> ListAsync(string? search, CancellationToken ct = default);
}
