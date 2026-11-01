using GenesisFIS.Core.Cases;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>
/// Case > Non-Disclousre Info (frmRECNonDisclosure). The legacy form listed SearchMBMCovPersonsProc and saved through updatable recordsets on MBMTwo /
/// MBMCoveredPersonsTwo; this reads and writes the same columns with parameterised SQL.
/// </summary>
internal sealed class CaseNonDisclosureRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ICaseNonDisclosureRepository
{
    private static DateOnly? Day(IDictionary<string, object?> r, string c) => D(r, c) is { } d && d.Year > 1900 ? DateOnly.FromDateTime(d) : null;
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Number(string? s)
    {
        if (!MemberNumber.IsValid(s)) throw new BusinessRuleException("Please Enter Membership No", field: "memberNumber");
        return s!.Trim();
    }

    public async Task<NonDisclosureView?> GetAsync(string memberNumber, CancellationToken ct = default)
    {
        var number = Number(memberNumber);
        var p = (await QueryAsync(GenesisDb.Main, """
            SELECT m.MBMNumber, m.MBMName, m.MBMPolicyNo, m.MBMPayorEffDate, m.MBMPayorExpDate, m.INSCode,
                   t.MBMNumber AS TwoNumber, t.MBMPreIllDate, t.MBMPreIllCAT, t.MBMPreIllInfBy, t.MBMUndIllDate, t.MBMUndIllCAT, t.MBMUndIllInfBy
            FROM MBM m LEFT JOIN MBMTwo t ON t.MBMNumber = m.MBMNumber
            WHERE m.MBMNumber = @number
            """, new { number }, ct)).FirstOrDefault();
        if (p is null) return null;
        var persons = new List<NonDisclosurePerson>
        {
            new(S(p, "MBMNumber")!, "00", S(p, "MBMName"), S(p, "MBMPolicyNo"), Day(p, "MBMPayorEffDate"), Day(p, "MBMPayorExpDate"),
                Day(p, "MBMPreIllDate"), S(p, "MBMPreIllCAT"), S(p, "MBMPreIllInfBy"), Day(p, "MBMUndIllDate"), S(p, "MBMUndIllCAT"), S(p, "MBMUndIllInfBy"), S(p, "TwoNumber") is not null),
        };
        // Like the legacy form, covered persons are listed only for family (F) and husband-and-wife (H) memberships.
        if (S(p, "INSCode") is "F" or "H")
        {
            var rows = await QueryAsync(GenesisDb.Main, """
                SELECT c.MBMCNumber, c.MBMCCoverID, c.MBMCName, c.MBMCEffDate, c.MBMCExpDate,
                       t.MBMCNumber AS TwoNumber, t.MBMCPreIllDate, t.MBMCPreIllCAT, t.MBMCPreInfBy, t.MBMCUndIllDate, t.MBMCUndIllCAT, t.MBMCUndInfBy
                FROM MBMCoveredPersons c LEFT JOIN MBMCoveredPersonsTwo t ON t.MBMCNumber = c.MBMCNumber AND t.MBMCCoverID = c.MBMCCoverID
                WHERE c.MBMCNumber = @number AND c.MBMCCoverID <> '00'
                ORDER BY c.MBMCCoverID
                """, new { number }, ct);
            persons.AddRange(rows.Select(r => new NonDisclosurePerson(
                S(r, "MBMCNumber")!, S(r, "MBMCCoverID")!, S(r, "MBMCName"), S(p, "MBMPolicyNo"), Day(r, "MBMCEffDate") ?? Day(p, "MBMPayorEffDate"), Day(r, "MBMCExpDate") ?? Day(p, "MBMPayorExpDate"),
                Day(r, "MBMCPreIllDate"), S(r, "MBMCPreIllCAT"), S(r, "MBMCPreInfBy"), Day(r, "MBMCUndIllDate"), S(r, "MBMCUndIllCAT"), S(r, "MBMCUndInfBy"), S(r, "TwoNumber") is not null)));
        }
        return new NonDisclosureView(number, S(p, "MBMName"), persons);
    }

    public async Task<string?> NeighbourAsync(string memberNumber, bool next, CancellationToken ct = default)
    {
        var number = Number(memberNumber);
        var sql = next ? "SELECT TOP 1 MBMNumber FROM MBM WHERE MBMNumber > @number ORDER BY MBMNumber" : "SELECT TOP 1 MBMNumber FROM MBM WHERE MBMNumber < @number ORDER BY MBMNumber DESC";
        return (await QueryAsync(GenesisDb.Main, sql, new { number }, ct)).Select(r => S(r, "MBMNumber")).FirstOrDefault();
    }

    public async Task UpdateAsync(string memberNumber, string coverId, NonDisclosureRequest r, string user, CancellationToken ct = default)
    {
        var number = Number(memberNumber);
        var cover = coverId?.Trim() ?? "";
        if (cover.Length is 0 or > 5 || !cover.All(char.IsLetterOrDigit)) throw new BusinessRuleException("Please Select a record", field: "coverId");
        if (NonDisclosureRules.Validate(r, DateOnly.FromDateTime(DateTime.Today)) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var view = await GetAsync(number, ct) ?? throw BusinessRuleException.NotFound("No record found!");
        var before = view.Persons.FirstOrDefault(x => x.CoverId == cover) ?? throw BusinessRuleException.NotFound("Please Select a record");
        // The legacy form could only update an existing detail row (MBMTwo / MBMCoveredPersonsTwo); it never created one.
        if (!before.HasDetailRecord) throw new BusinessRuleException(cover == "00" ? "This member has no MBMTwo record to update." : "This covered person has no MBMCoveredPersonsTwo record to update.");
        var args = new
        {
            number, cover, preDate = At(r.PreExistingDate), preCat = Blank(r.PreExistingCategory) ?? "", preBy = Blank(r.PreExistingInformedBy) ?? "",
            undDate = At(r.UnderlyingDate), undCat = Blank(r.UnderlyingCategory) ?? "", undBy = Blank(r.UnderlyingInformedBy) ?? "",
        };
        await using (var s = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            var n = cover == "00"
                ? await s.ExecuteAsync("""
                    UPDATE MBMTwo SET MBMPreIllDate = @preDate, MBMPreIllCAT = @preCat, MBMPreIllInfBy = @preBy, MBMUndIllDate = @undDate, MBMUndIllCAT = @undCat, MBMUndIllInfBy = @undBy
                    WHERE MBMNumber = @number
                    """, args, ct)
                : await s.ExecuteAsync("""
                    UPDATE MBMCoveredPersonsTwo SET MBMCPreIllDate = @preDate, MBMCPreIllCAT = @preCat, MBMCPreInfBy = @preBy, MBMCUndIllDate = @undDate, MBMCUndIllCAT = @undCat, MBMCUndInfBy = @undBy
                    WHERE MBMCNumber = @number AND MBMCCoverID = @cover
                    """, args, ct);
            if (n == 0) throw BusinessRuleException.NotFound("Please Select a record");
            await s.CommitAsync(ct);
        }
        audit.Record(new AuditEntry("Member.NonDisclosure", cover == "00" ? "MBMTwo" : "MBMCoveredPersonsTwo", $"{number}-{cover}", user,
            new Dictionary<string, object?> { ["before"] = before, ["after"] = r }));
    }
}
