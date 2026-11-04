using GenesisFIS.Core.Data;
using GenesisFIS.Core.Payments;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Cases;

/// <summary>Appends a line to a case's remarks the way the legacy saves did (CASRemarks, CASRemarksTwo, CASRemarkThree columns in turn).</summary>
internal static class CaseRemarkLog
{
    /// <summary>Appends <paramref name="line"/> to the first remark column with room (see <see cref="PayorReceiptRules.AppendRemark"/>), creating the row when needed.</summary>
    public static async Task AppendAsync(IDbSession main, string no, string line, CancellationToken ct)
    {
        var r1 = (await main.QueryAsync("SELECT CASORemarks, CASORemarksTwo FROM CASRemarks WITH (UPDLOCK) WHERE CASNumber = @no", new { no }, ct)).FirstOrDefault();
        var r2 = (await main.QueryAsync("SELECT CASORemarks, CASORemarksTwo FROM CASRemarksTwo WITH (UPDLOCK) WHERE CASNumber = @no", new { no }, ct)).FirstOrDefault();
        var r3 = (await main.QueryAsync("SELECT CASRemarks, CASRemarksFour, CASRemarksFive FROM CASRemarkThree WITH (UPDLOCK) WHERE CASNumber = @no", new { no }, ct)).FirstOrDefault();
        string? G(IDictionary<string, object?>? r, string c) => r is null ? null : Get(r, c) as string;
        var (slot, text) = PayorReceiptRules.AppendRemark(
            [G(r1, "CASORemarks"), G(r1, "CASORemarksTwo"), G(r2, "CASORemarks"), G(r2, "CASORemarksTwo"), G(r3, "CASRemarks"), G(r3, "CASRemarksFour"), G(r3, "CASRemarksFive")], line);
        var (table, column, exists) = slot switch
        {
            0 => ("CASRemarks", "CASORemarks", r1 is not null), 1 => ("CASRemarks", "CASORemarksTwo", r1 is not null),
            2 => ("CASRemarksTwo", "CASORemarks", r2 is not null), 3 => ("CASRemarksTwo", "CASORemarksTwo", r2 is not null),
            4 => ("CASRemarkThree", "CASRemarks", r3 is not null), 5 => ("CASRemarkThree", "CASRemarksFour", r3 is not null),
            _ => ("CASRemarkThree", "CASRemarksFive", r3 is not null),
        };
        if (exists) await main.ExecuteAsync($"UPDATE {table} SET {column} = @text WHERE CASNumber = @no", new { no, text }, ct);
        else await main.ExecuteAsync($"INSERT INTO {table} (CASNumber, {column}) VALUES (@no, @text)", new { no, text }, ct);
    }
}
