using Genesis.Core.Data;
using Genesis.Core.Maintenance;
using Genesis.Core.Writes;
using Genesis.Infrastructure.Data;
using static Genesis.Infrastructure.Data.RowReader;

namespace Genesis.Infrastructure.Maintenance;

internal sealed class ValidDateRangeRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IValidDateRangeRepository
{
    private const string Select = "SELECT TOP 1 ValidID, MBMEffStart, MBMEffEnd, MBMExpStart, MBMExpEnd, MBMDOBStart, MBMDOBEnd, MBMValidDateLastUpdateUser, MBMValidDateLastUpdateDate FROM MBMValidDateRange ORDER BY ValidID";

    private static DateOnly? Day(IDictionary<string, object?> r, string name) => D(r, name) is { } d ? DateOnly.FromDateTime(d) : null;

    private static ValidDateRanges Map(IDictionary<string, object?> r) =>
        new(Day(r, "MBMEffStart"), Day(r, "MBMEffEnd"), Day(r, "MBMExpStart"), Day(r, "MBMExpEnd"), Day(r, "MBMDOBStart"), Day(r, "MBMDOBEnd"),
            S(r, "MBMValidDateLastUpdateUser"), Day(r, "MBMValidDateLastUpdateDate"));

    public async Task<ValidDateRanges?> GetAsync(CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Main, Select, null, ct)).Select(Map).FirstOrDefault();

    public async Task<ValidDateRanges> SaveAsync(ValidDateRanges ranges, string user, CancellationToken ct = default)
    {
        if (ValidDateRangeRules.Validate(ranges) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);

        await using var session = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        var existing = (await session.QueryAsync(Select, null, ct)).FirstOrDefault();
        var args = new
        {
            id = existing is null ? 0 : I(existing, "ValidID") ?? 0,
            eff1 = ranges.EffectiveStart!.Value.ToDateTime(TimeOnly.MinValue), eff2 = ranges.EffectiveEnd!.Value.ToDateTime(TimeOnly.MinValue),
            exp1 = ranges.ExpiryStart!.Value.ToDateTime(TimeOnly.MinValue), exp2 = ranges.ExpiryEnd!.Value.ToDateTime(TimeOnly.MinValue),
            dob1 = ranges.BirthStart!.Value.ToDateTime(TimeOnly.MinValue), dob2 = ranges.BirthEnd!.Value.ToDateTime(TimeOnly.MinValue),
            user, today = DateTime.Today,
        };
        if (existing is null)
            await session.ExecuteAsync("""
                INSERT INTO MBMValidDateRange (MBMEffStart, MBMEffEnd, MBMExpStart, MBMExpEnd, MBMDOBStart, MBMDOBEnd, MBMValidDateLastUpdateUser, MBMValidDateLastUpdateDate)
                VALUES (@eff1, @eff2, @exp1, @exp2, @dob1, @dob2, @user, @today)
                """, args, ct);
        else
            await session.ExecuteAsync("""
                UPDATE MBMValidDateRange SET MBMEffStart = @eff1, MBMEffEnd = @eff2, MBMExpStart = @exp1, MBMExpEnd = @exp2, MBMDOBStart = @dob1, MBMDOBEnd = @dob2,
                       MBMValidDateLastUpdateUser = @user, MBMValidDateLastUpdateDate = @today
                WHERE ValidID = @id
                """, args, ct);
        var saved = Map((await session.QueryAsync(Select, null, ct)).First());
        await session.CommitAsync(ct);

        audit.Record(new AuditEntry("ValidDates.Save", "MBMValidDateRange", args.id.ToString(), user,
            new Dictionary<string, object?> { ["before"] = existing is null ? null : Map(existing), ["after"] = saved }));
        return saved;
    }
}

