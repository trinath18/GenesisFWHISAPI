using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

internal sealed class MemberDeletionRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IMemberDeletionRepository
{
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);

    /// <summary>Same selection as the legacy form, with every value as a parameter. Dates are inclusive.</summary>
    private static (string Where, object Args) Selection(DeletionCriteria c)
    {
        var where = "m.HLTCode = @health AND m.PAYCode = @payor";
        switch (c.Option)
        {
            case DeletionOptions.BordxDate: where += " AND m.MBMBordxDate >= @from AND m.MBMBordxDate < @to"; break;
            case DeletionOptions.Batch: where += " AND m.MBMBordxDate >= @from AND m.MBMBordxDate < @to AND m.MBMBatchNo = @batch"; break;
            default: where += " AND m.MBMNumber >= @mf AND m.MBMNumber <= @mt"; break;
        }
        var to = c.Option == DeletionOptions.Batch ? c.BordxFrom : c.BordxTo;
        return (where, new
        {
            health = c.Health!.Trim(), payor = c.Payor!.Trim(), from = At(c.BordxFrom), to = At(to?.AddDays(1)), batch = c.BatchNo, mf = c.MemberFrom?.Trim(), mt = c.MemberTo?.Trim(),
        });
    }

    private static string CandidateSql(string where, string top) => $"""
        SELECT {top} m.MBMNumber, m.MBMName, m.MBMBordxDate, m.MBMBatchNo, m.MBMMemberType, m.INSCode,
               (SELECT COUNT(*) FROM MBMCoveredPersons c WHERE c.MBMCNumber = m.MBMNumber) AS Covered,
               (SELECT COUNT(*) FROM CAS x WHERE x.MBMNumber = m.MBMNumber) AS Cases,
               (SELECT COUNT(*) FROM MBMMCOInvoice i WHERE i.MBMNumber = m.MBMNumber)
             + (SELECT COUNT(*) FROM MBMRefund r WHERE r.MBMNumber = m.MBMNumber)
             + (SELECT COUNT(*) FROM MCOCreditNote n WHERE n.MBMNumber = m.MBMNumber) AS Billing
        FROM MBM m WHERE {where} ORDER BY m.MBMNumber
        """;

    private static DeletionCandidate Map(IDictionary<string, object?> r) => new(
        S(r, "MBMNumber") ?? "", S(r, "MBMName"), D(r, "MBMBordxDate") is { } d ? DateOnly.FromDateTime(d) : null, I(r, "MBMBatchNo"), S(r, "MBMMemberType"), S(r, "INSCode"),
        I(r, "Covered") ?? 0, I(r, "Cases") ?? 0, I(r, "Billing") ?? 0);

    public async Task<IReadOnlyList<HealthPlan>> ListHealthPlansAsync(CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Main, "SELECT HLTCode, HLTDescription FROM HLT ORDER BY HLTCode", null, ct)).Select(r => new HealthPlan(S(r, "HLTCode") ?? "", S(r, "HLTDescription"))).ToList();

    public async Task<DeletionPreview> PreviewAsync(DeletionCriteria criteria, CancellationToken ct = default)
    {
        if (DeletionRules.Validate(criteria) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var (where, args) = Selection(criteria);
        var rows = (await QueryAsync(GenesisDb.Main, CandidateSql(where, $"TOP ({DeletionRules.MaxMembers + 1})"), args, ct)).Select(Map).ToList();
        if (rows.Count > DeletionRules.MaxMembers)
            throw new BusinessRuleException($"More than {DeletionRules.MaxMembers} members match. Narrow the selection (a smaller bordx or member range).", field: "criteria");
        return new DeletionPreview(rows, rows.Count, rows.Where(r => r.Insurer is "F" or "H").Sum(r => r.CoveredPersons), rows.Count(r => r.BlockedReason is not null));
    }

    public async Task<DeletionResult> DeleteAsync(DeletionRequest request, string user, CancellationToken ct = default)
    {
        if (DeletionRules.Validate(request.Criteria) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        if (request.ExpectedMembers is null or <= 0) throw new BusinessRuleException("Please confirm the number of members to delete.", field: "expectedMembers");

        var preview = await PreviewAsync(request.Criteria!, ct);
        if (preview.Principals != request.ExpectedMembers)
            throw BusinessRuleException.Conflict($"The selection now matches {preview.Principals} member(s), not the {request.ExpectedMembers} you confirmed. Search again and re-confirm; nothing was deleted.");
        if (preview.Principals == 0) throw BusinessRuleException.NotFound("No Record Found");
        if (preview.Candidates.Where(c => c.BlockedReason is not null).Take(5).ToList() is { Count: > 0 } blocked)
            throw BusinessRuleException.Conflict("These members cannot be deleted: " + string.Join("; ", blocked.Select(b => $"{b.MemberNumber} {b.BlockedReason}")) +
                (preview.Blocked > blocked.Count ? $" (and {preview.Blocked - blocked.Count} more)" : "") + ". Nothing was deleted.");

        var numbers = preview.Candidates.Select(c => c.MemberNumber).ToList();
        var withSupplementary = preview.Candidates.Where(c => c.Insurer is "F" or "H").Select(c => c.MemberNumber).ToList();

        // 1. Read the complete records and put them in the audit log BEFORE anything is changed.
        IReadOnlyList<IDictionary<string, object?>> members, others, covered;
        await using (var read = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            members = await read.QueryAsync("SELECT * FROM MBM WHERE MBMNumber IN @numbers", new { numbers }, ct);
            others = await read.QueryAsync("SELECT * FROM MBMOthers WHERE MBMNumber IN @numbers", new { numbers }, ct);
            covered = withSupplementary.Count == 0 ? [] : await read.QueryAsync("SELECT * FROM MBMCoveredPersons WHERE MBMCNumber IN @numbers", new { numbers = withSupplementary }, ct);
            await read.CommitAsync(ct);
        }
        audit.Record(new AuditEntry("Member.DeleteIntent", "MBM", $"{numbers.Count} member(s)", user, new Dictionary<string, object?>
            { ["criteria"] = request.Criteria, ["members"] = members, ["others"] = others, ["coveredPersons"] = covered }));

        // 2. Archive (separate database). Re-runnable: an earlier archive row of the same member is replaced.
        await using (var archive = await Db.BeginSessionAsync(GenesisDb.Archive, ct))
        {
            var overrides = new Dictionary<string, object?> { ["MBMStatus"] = "D", ["MBMDataStatus"] = "D", ["MBMValueDate"] = DateTime.Today };
            await ArchiveAsync(archive, "MBMArchive", "MBMNumber", numbers, members, overrides, ct);
            await ArchiveAsync(archive, "MBMOthersArchive", "MBMNumber", numbers, others, null, ct);
            if (withSupplementary.Count > 0) await ArchiveAsync(archive, "MBMCoveredPersonsArchive", "MBMCNumber", withSupplementary, covered, null, ct);
            await archive.CommitAsync(ct);
        }

        // 3. Delete from the main database in one transaction.
        await using var main = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        await main.ExecuteAsync("DELETE FROM MBMOthers WHERE MBMNumber IN @numbers", new { numbers }, ct);
        var coveredDeleted = withSupplementary.Count == 0 ? 0 : await main.ExecuteAsync("DELETE FROM MBMCoveredPersons WHERE MBMCNumber IN @numbers", new { numbers = withSupplementary }, ct);
        var deleted = await main.ExecuteAsync("DELETE FROM MBM WHERE MBMNumber IN @numbers", new { numbers }, ct);
        if (deleted != numbers.Count) throw BusinessRuleException.Conflict("The members changed while deleting; nothing was deleted. Search again.");
        await main.CommitAsync(ct);

        var archived = members.Count + others.Count + covered.Count;
        audit.Record(new AuditEntry("Member.Delete", "MBM", $"{deleted} member(s)", user, new Dictionary<string, object?>
            { ["members"] = numbers, ["coveredPersonsDeleted"] = coveredDeleted, ["archived"] = archived }));
        return new DeletionResult(deleted, coveredDeleted, archived);
    }

    /// <summary>Copies every source column that also exists in the archive table (matched by name); existing archive rows of these keys are replaced.</summary>
    private static async Task ArchiveAsync(IDbSession archive, string table, string keyColumn, IReadOnlyList<string> keys,
        IReadOnlyList<IDictionary<string, object?>> rows, IDictionary<string, object?>? overrides, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var columns = (await archive.QueryAsync("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@table) AND is_identity = 0 AND is_computed = 0", new { table }, ct))
            .Select(r => S(r, "name")!).ToList();
        await archive.ExecuteAsync($"DELETE FROM {table} WHERE {keyColumn} IN @keys", new { keys }, ct);
        foreach (var row in rows)
        {
            var values = columns.Where(c => row.Keys.Contains(c, StringComparer.OrdinalIgnoreCase) || overrides?.ContainsKey(c) == true).ToList();
            var args = new Dictionary<string, object?>();
            for (var i = 0; i < values.Count; i++)
            {
                var c = values[i];
                args["p" + i] = overrides is not null && overrides.TryGetValue(c, out var o) ? o : Get(row, c) is DBNull ? null : Get(row, c);
            }
            await archive.ExecuteAsync($"INSERT INTO {table} ({string.Join(", ", values.Select(c => $"[{c}]"))}) VALUES ({string.Join(", ", values.Select((_, i) => "@p" + i))})", args, ct);
        }
    }
}


