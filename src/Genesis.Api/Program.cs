using Genesis.Api.Features.Auth;
using Genesis.Api.Features.Cases;
using Genesis.Api.Features.Health;
using Genesis.Api.Features.Maintenance;
using Genesis.Api.Features.Membership;
using Genesis.Api.Features.Reports;
using Genesis.Api.Features.Tracking;
using Genesis.Api.Infrastructure;
using Genesis.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Filters;
using Serilog.Formatting.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console()
    // Audit trail of every write: its own append-only JSON file (one per day), separate from the application log.
    .WriteTo.Logger(audit => audit.Filter.ByIncludingOnly(Matching.FromSource("Genesis.Audit"))
        .WriteTo.File(new JsonFormatter(renderMessage: true), ctx.Configuration["Audit:Path"] ?? "logs/audit-.jsonl", rollingInterval: RollingInterval.Day, shared: true)));

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be set (>= 32 chars). Use user-secrets locally or an environment variable / secret store when deployed.");

builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IAuthorizationHandler, ScreenAccessHandler>();
builder.Services.AddInfrastructure();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new()
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = jwt.SigningKey,
        NameClaimType = System.Security.Claims.ClaimTypes.Name,
        ClockSkew = TimeSpan.FromMinutes(1)
    };
});
builder.Services.AddAuthorization();

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (origins.Length > 0)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
if (origins.Length > 0) app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseBusinessRuleErrors();

app.MapHealth();
app.MapAuth();
app.MapReports();
app.MapMembership();
app.MapCases();
app.MapTracking();
app.MapSummaries();
app.MapPanelHospitals();
app.MapLookups();
app.MapSuspensions();

app.Run();

public partial class Program;







