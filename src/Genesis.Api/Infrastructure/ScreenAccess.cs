using Genesis.Core.Security;
using Microsoft.AspNetCore.Authorization;

namespace Genesis.Api.Infrastructure;

/// <summary>Requires the legacy USRAccess flag at a 1-based screen position.</summary>
public sealed record ScreenAccessRequirement(int Position) : IAuthorizationRequirement;

public sealed class ScreenAccessHandler : AuthorizationHandler<ScreenAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScreenAccessRequirement requirement)
    {
        var access = new AccessString(context.User.FindFirst(GenesisClaims.Access)?.Value ?? "");
        if (access.IsAllowed(requirement.Position)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public static class ScreenAccessExtensions
{
    public static TBuilder RequireScreen<TBuilder>(this TBuilder builder, int position) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy.AddRequirements(new ScreenAccessRequirement(position)));
}
