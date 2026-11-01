using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

internal sealed class McoPendingRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IMcoPendingRepository
{
    private static DateOnly? Day(IDictionary<string, object?> r, string name) => D(r, name) is { } d ? DateOnly.FromDateTime(d) : null;
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<McoPendingPage> SearchAsync(McoPendingCriteria c, CancellationToken ct = default)
    {
        if (McoPendingRules.ValidateSearch(c) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);

        // Same shape as the legacy view (members joined to their pending row), reading only the FWHS maintenance database.
        const string sql = """
            SELECT TOP (@take) COALESCE(x.MBMNumber, p.MBMNumber) AS MBMNumber, COALESCE(p.MBMBordxDate, x.MBMBordxDate) AS BordxDate, p.MBMNewBordxDate, p.Status
            FROM MBMCrossReference x FULL OUTER JOIN MCOPending p ON p.MBMNumber = x.MBMNumber
            WHERE (@mf IS NULL OR COALESCE(x.MBMNumber, p.MBMNumber) >= @mf) AND (@mt IS NULL OR COALESCE(x.MBMNumber, p.MBMNumber) <= @mt)
              AND (@bf IS NULL OR x.MBMBordxDate >= @bf) AND (@bt IS NULL OR x.MBMBordxDate < @bt)
              AND (@status IS NULL OR p.Status = @status)
            ORDER BY COALESCE(x.MBMNumber, p.MBMNumber)
            """;
        var rows = await QueryAsync(GenesisDb.Maintenance, sql, new
        {
            take = McoPendingRules.MaxRows + 1, mf = Blank(c.MemberFrom), mt = Blank(c.MemberTo), bf = At(c.BordxFrom), bt = At(c.BordxTo?.AddDays(1)), status = Blank(c.Status),
        }, ct);
        return new McoPendingPage(
            rows.Take(McoPendingRules.MaxRows).Select(r => new McoPendingRow(S(r, "MBMNumber") ?? "", Day(r, "BordxDate"), Day(r, "MBMNewBordxDate"), S(r, "Status"))).ToList(),
            rows.Count > McoPendingRules.MaxRows);
    }

    public async Task<McoPendingResult> ChangeAsync(McoPendingChange change, string user, CancellationToken ct = default)
    {
        if (McoPendingRules.ValidateChange(change) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var members = change.Members!.Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int added = 0, released = 0, unchanged = 0;

        await using var session = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
        var existing = (await session.QueryAsync("SELECT MBMNumber, Status FROM MCOPending WHERE MBMNumber IN @members", new { members }, ct))
            .ToDictionary(r => S(r, "MBMNumber")!, r => S(r, "Status"), StringComparer.OrdinalIgnoreCase);
        if (members.FirstOrDefault(m => existing.TryGetValue(m, out var s) && s == McoPendingStatus.Billed) is { } billed)
            throw BusinessRuleException.Conflict($"'{billed}' has already been billed and cannot be changed; nothing was updated.");

        foreach (var member in members)
        {
            var isListed = existing.ContainsKey(member);
            if (change.Status == McoPendingStatus.Pending)
            {
                if (isListed) { unchanged++; continue; }
                var bordx = (await session.QueryAsync("SELECT MBMBordxDate FROM MBMCrossReference WHERE MBMNumber = @member", new { member }, ct)).FirstOrDefault();
                if (bordx is null) throw BusinessRuleException.NotFound($"Member '{member}' was not found; nothing was updated.");
                await session.ExecuteAsync("INSERT INTO MCOPending (MBMNumber, MBMBordxDate, Status, LastUpdateUser, LastUpdateDate) VALUES (@member, @bordx, 'P', @user, @today)",
                    new { member, bordx = D(bordx, "MBMBordxDate"), user, today = DateTime.Today }, ct);
                added++;
            }
            else if (isListed)
            {
                await session.ExecuteAsync("DELETE FROM MCOPending WHERE MBMNumber = @member AND Status = 'P'", new { member }, ct);
                released++;
            }
            else unchanged++;
        }
        await session.CommitAsync(ct);

        audit.Record(new AuditEntry("McoPending." + (change.Status == McoPendingStatus.Pending ? "Pend" : "Release"), "MCOPending", $"{members.Count} member(s)", user,
            new Dictionary<string, object?> { ["status"] = change.Status, ["added"] = added, ["released"] = released, ["unchanged"] = unchanged, ["members"] = members.Take(200).ToList() }));
        return new McoPendingResult(added, released, unchanged);
    }
}
