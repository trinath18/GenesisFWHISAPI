using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GenesisFIS.Core.Writes;

namespace GenesisFIS.Api.Infrastructure;

public static class WriteSupport
{
    /// <summary>The legacy user code ("Logon") that write flows stamp into LastUpdateUSR-style columns and the audit log.</summary>
    public static string UserCode(this ClaimsPrincipal user) =>
        user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new InvalidOperationException("The signed-in user has no user code.");

    /// <summary>Maps <see cref="BusinessRuleException"/> to 400 / 404 / 409 JSON so every write endpoint reports problems the same way.</summary>
    public static IApplicationBuilder UseBusinessRuleErrors(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        try
        {
            await next();
        }
        catch (BusinessRuleException ex) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = ex.Kind switch { RuleFailure.NotFound => 404, RuleFailure.Conflict => 409, _ => 400 };
            await context.Response.WriteAsJsonAsync(new { error = ex.Message, field = ex.Field });
        }
    });
}
