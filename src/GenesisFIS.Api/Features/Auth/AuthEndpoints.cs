using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Security;
using GenesisFIS.Core.Users;

namespace GenesisFIS.Api.Features.Auth;

public sealed record LoginRequest(string UserId, string Password);
public sealed record UserInfo(string UserId, string Name, string? Group, string? Department, string Access, bool MustChangePassword);
public sealed record LoginResponse(string Token, DateTime ExpiresAt, UserInfo User);

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", async (LoginRequest request, IUserRepository users, TokenService tokens, ILogger<LoginRequest> log, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrEmpty(request.Password))
                return Results.BadRequest(new { error = "User ID and password are required." });

            var user = await users.FindByCodeAsync(request.UserId.Trim().ToUpperInvariant(), ct);
            if (user is null || user.Status != "A" || !LegacyPasswordCipher.Verify(request.Password, user.PasswordHash))
            {
                log.LogWarning("Failed login for {UserId}", request.UserId);
                return Results.Json(new { error = "Invalid user ID or password." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var (token, expires) = tokens.Create(user);
            return Results.Ok(new LoginResponse(token, expires, ToInfo(user)));
        }).AllowAnonymous();

        group.MapGet("/me", (ClaimsPrincipal principal) => Results.Ok(new UserInfo(
            principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub") ?? "",
            principal.FindFirstValue(ClaimTypes.Name) ?? "",
            principal.FindFirstValue(GenesisClaims.Group),
            principal.FindFirstValue(GenesisClaims.Department),
            principal.FindFirstValue(GenesisClaims.Access) ?? "",
            principal.FindFirstValue(GenesisClaims.MustChangePassword) == "true"))).RequireAuthorization();
    }

    private static UserInfo ToInfo(UserRecord u) =>
        new(u.Code, u.Name, u.GroupCode, u.DepartmentCode, u.Access ?? "", u.MustChangePassword);
}
