using System.Security.Claims;
using Genesis.Api.Infrastructure;
using Genesis.Core.Cases;

namespace Genesis.Api.Features.Cases;

public static class CaseRegistrationEndpoints
{
    /// <summary>Legacy USRAccess position of Case > Case Registration.</summary>
    public const int Screen = 11;

    public static void MapCaseRegistration(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/case-registration").WithTags("Case Registration").RequireScreen(Screen);
        g.MapGet("/options", async (ICaseRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.OptionsAsync(ct)));
        g.MapGet("/hospitals", async (string? search, ICaseRegistrationRepository repo, CancellationToken ct) => Results.Ok(await repo.HospitalsAsync(search, ct)));
        g.MapGet("/member", async (string memberNumber, ICaseRegistrationRepository repo, CancellationToken ct) =>
            await repo.MemberAsync(memberNumber, ct) is { } m ? Results.Ok(m) : Results.NotFound(new { message = "No record found!" }));
        g.MapPost("/", async (CaseRegistrationRequest request, ClaimsPrincipal user, ICaseRegistrationRepository repo, CancellationToken ct) =>
        {
            var result = await repo.RegisterAsync(request, user.UserCode(), ct);
            return Results.Created("/api/cases/" + result.CaseNumber, result);
        });
    }
}

