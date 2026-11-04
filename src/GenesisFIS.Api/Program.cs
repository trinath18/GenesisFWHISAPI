using GenesisFIS.Api.Features.Auth;
using GenesisFIS.Api.Features.Cases;
using GenesisFIS.Api.Features.Health;
using GenesisFIS.Api.Features.Maintenance;
using GenesisFIS.Api.Features.Membership;
using GenesisFIS.Api.Features.Bordereaux;
using GenesisFIS.Api.Features.Payments;
using GenesisFIS.Api.Features.Posting;
using GenesisFIS.Api.Features.Reports;
using GenesisFIS.Api.Features.Tracking;
using GenesisFIS.Api.Infrastructure;
using GenesisFIS.Core.Tracking;
using GenesisFIS.Infrastructure;
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
builder.Services.AddScoped<ITrackingPrinter, MemberLabelPrinter>();
builder.Services.AddScoped<ITrackingPrinter, BordxDetailsPrinter>();
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
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition", "X-Records", "X-Batch", "X-Saved-To", "X-Totals", "X-Files")));

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
app.MapCaseRegistration();
app.MapCaseAdjustment();
app.MapCaseInvoices();
app.MapWorksheets();
app.MapCaseTransfers();
app.MapLifetimeAuditBordx();
app.MapCaseMenu();
app.MapTracking();
app.MapSummaries();
app.MapPanelHospitals();
app.MapLookups();
app.MapSuspensions();
app.MapCardSending();
app.MapLeaflet();
app.MapMcoPending();
app.MapMemberDeletion();
app.MapReinstatement();
app.MapAdjustment();
app.MapRegistration();
app.MapBenefits();
app.MapIcdSelection();
app.MapMcoFees();
app.MapBordxUpload();
app.MapAccountPosting();
app.MapPayorReceipts();
app.MapHospitalPayments();
app.MapPaymentTracking();
app.MapBordxCreation();
app.MapCreditDebitNotes();
app.MapUserMaintenance();
app.MapSppaExport();
app.MapSppaUpload();

app.Run();

public partial class Program;




















