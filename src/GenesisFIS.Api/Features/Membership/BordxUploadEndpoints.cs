using System.Security.Claims;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Api.Features.Membership;

public static class BordxUploadEndpoints
{
    /// <summary>Legacy USRAccess position of Membership > Upload Bordx (mnuUpload: CheckAccess(8)).</summary>
    public const int Screen = 8;

    public static void MapBordxUpload(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/membership/bordx-upload").WithTags("Membership: Upload Bordx").RequireScreen(Screen);

        g.MapPost("/preview", async (BordxUploadRequest request, IBordxUploadRepository repo, CancellationToken ct) => Results.Ok(await repo.PreviewAsync(request, ct)));
        g.MapPost("/", async (BordxUploadRequest request, ClaimsPrincipal user, IBordxUploadRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.UploadAsync(request, user.UserCode(), ct)));
    }
}
