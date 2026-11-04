using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Cases;

namespace GenesisFIS.Api.Features.Cases;

public static class SppaUploadEndpoints
{
    /// <summary>Case > Claims ASCII (From SPPA) is checked against the Case ASCII position (CheckAccess(18)).</summary>
    public const int Screen = CaseMenuEndpoints.AsciiScreen;

    public static void MapSppaUpload(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/case-ascii/sppa-upload").WithTags("Claim / Case ASCII").RequireScreen(Screen);

        g.MapPost("/preview", async (SppaUploadRequest request, ISppaClaimUploadRepository repo, CancellationToken ct) => Results.Ok(await repo.PreviewAsync(request, ct)));
        g.MapPost("/", async (SppaUploadRequest request, ClaimsPrincipal user, ISppaClaimUploadRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.UploadAsync(request, user.UserCode(), ct)));
    }
}
