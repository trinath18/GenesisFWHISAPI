using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

internal sealed class CardSendingRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), ICardSendingRepository
{
    private static DateOnly? Day(IDictionary<string, object?> r, string name) => D(r, name) is { } d ? DateOnly.FromDateTime(d) : null;
    private static DateTime? At(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);

    public async Task<CardSendingPage> SearchAsync(CardSendingCriteria c, CancellationToken ct = default)
    {
        if (CardSendingRules.ValidateSearch(c) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);

        // Dates are inclusive: the end date is compared as "before the next day" so times on that day are kept.
        const string sql = """
            SELECT TOP (@take) MBMNumber, MBMName, INSCode, MBMBordxDate, MBMBatchNo, MBMSendingMode, MBMCardDate
            FROM VIEW_CardSent_Principal
            WHERE (@payor IS NULL OR PAYCode = @payor)
              AND (@mf IS NULL OR MBMNumber >= @mf) AND (@mt IS NULL OR MBMNumber <= @mt)
              AND (@bf IS NULL OR MBMBordxDate >= @bf) AND (@bt IS NULL OR MBMBordxDate < @bt)
              AND (@nf IS NULL OR MBMBatchNo >= @nf) AND (@nt IS NULL OR MBMBatchNo <= @nt)
              AND (@sf IS NULL OR MBMCardDate >= @sf) AND (@st IS NULL OR MBMCardDate < @st)
              AND (@mode IS NULL OR MBMSendingMode = @mode)
            ORDER BY MBMNumber
            """;
        string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var principals = await QueryAsync(GenesisDb.Main, sql, new
        {
            take = CardSendingRules.MaxPrincipals + 1, payor = Blank(c.Payor), mf = Blank(c.MemberFrom), mt = Blank(c.MemberTo),
            bf = At(c.BordxFrom), bt = At(c.BordxTo?.AddDays(1)), nf = c.BatchFrom, nt = c.BatchTo, sf = At(c.SentFrom), st = At(c.SentTo?.AddDays(1)), mode = Blank(c.Mode),
        }, ct);

        var truncated = principals.Count > CardSendingRules.MaxPrincipals;
        var rows = new List<CardSendingRow>();
        int supplementary = 0;
        foreach (var p in principals.Take(CardSendingRules.MaxPrincipals))
        {
            var number = S(p, "MBMNumber") ?? "";
            rows.Add(new CardSendingRow(number, "00", S(p, "MBMName"), Day(p, "MBMBordxDate"), I(p, "MBMBatchNo"), S(p, "MBMSendingMode"), Day(p, "MBMCardDate")));
            // The legacy form only lists covered persons for insurers F and H.
            if (S(p, "INSCode") is "F" or "H")
            {
                var covered = await QueryAsync(GenesisDb.Main, """
                    SELECT c.MBMCCoverID, c.MBMCName, t.MBMSendingMode, t.MBMCardDate
                    FROM MBMCoveredPersons c INNER JOIN MBMCoveredPersonsTwo t ON t.MBMCNumber = c.MBMCNumber AND t.MBMCCoverID = c.MBMCCoverID
                    WHERE c.MBMCNumber = @number ORDER BY c.MBMCCoverID
                    """, new { number }, ct);
                foreach (var cp in covered)
                {
                    rows.Add(new CardSendingRow(number, S(cp, "MBMCCoverID") ?? "", S(cp, "MBMCName"), Day(p, "MBMBordxDate"), I(p, "MBMBatchNo"), S(cp, "MBMSendingMode"), Day(cp, "MBMCardDate")));
                    supplementary++;
                }
            }
        }
        return new CardSendingPage(rows, rows.Count - supplementary, supplementary, truncated);
    }

    public async Task<CardSendingResult> UpdateAsync(CardSendingUpdate u, string user, CancellationToken ct = default)
    {
        if (CardSendingRules.ValidateUpdate(u) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var date = u.Date!.Value.ToDateTime(TimeOnly.MinValue);
        var targets = u.Members!.Select(m => (Number: m.MemberNumber!.Trim(), Cover: m.CoverId!.Trim())).Distinct().ToList();

        await using var session = await Db.BeginSessionAsync(GenesisDb.Main, ct);
        foreach (var (number, cover) in targets)
        {
            var args = new { mode = u.Mode, date, number, cover };
            var changed = cover == "00"
                ? await session.ExecuteAsync("UPDATE MBMTwo SET MBMSendingMode = @mode, MBMCardDate = @date WHERE MBMNumber = @number", args, ct)
                : await session.ExecuteAsync("UPDATE MBMCoveredPersonsTwo SET MBMSendingMode = @mode, MBMCardDate = @date WHERE MBMCNumber = @number AND MBMCCoverID = @cover", args, ct);
            if (changed == 0) throw BusinessRuleException.NotFound($"Member '{number}' cover {cover} was not found; nothing was updated.");
        }
        await session.CommitAsync(ct);

        audit.Record(new AuditEntry("CardSending.Update", "MBMTwo/MBMCoveredPersonsTwo", $"{targets.Count} member(s)", user, new Dictionary<string, object?>
            { ["mode"] = u.Mode, ["date"] = u.Date, ["count"] = targets.Count, ["members"] = targets.Take(200).Select(t => $"{t.Number}/{t.Cover}").ToList() }));
        return new CardSendingResult(targets.Count);
    }
}
