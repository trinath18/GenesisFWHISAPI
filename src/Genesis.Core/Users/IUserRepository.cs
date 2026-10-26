namespace Genesis.Core.Users;

public sealed record UserRecord(
    string Code,
    string Name,
    string? PasswordHash,
    string Status,
    string? DepartmentCode,
    string? GroupCode,
    string? Access,
    bool MustChangePassword);

public interface IUserRepository
{
    Task<UserRecord?> FindByCodeAsync(string code, CancellationToken ct = default);
}
