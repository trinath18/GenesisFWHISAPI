using System.Security.Claims;
using GenesisFIS.Core.Hospitals;
using GenesisFIS.Reports.Hospitals;

namespace GenesisFIS.Api.Features.Reports;

public static class ReportEndpoints
{
    public static void MapReports(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").WithTags("Reports").RequireAuthorization();

        group.MapGet("/hospitals", async (string? search, IHospitalRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(search, ct)));

        group.MapGet("/hospitals.pdf", async (string? search, ClaimsPrincipal user, IHospitalRepository repo, CancellationToken ct) =>
        {
            var rows = await repo.ListAsync(search, ct);
            return Results.File(HospitalListReport.ToPdf(rows, user.Identity?.Name ?? "unknown"), "application/pdf", "hospitals.pdf");
        });

        group.MapGet("/hospitals.xlsx", async (string? search, IHospitalRepository repo, CancellationToken ct) =>
        {
            var rows = await repo.ListAsync(search, ct);
            return Results.File(HospitalListReport.ToExcel(rows),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "hospitals.xlsx");
        });
    }
}
