using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;
using GenesisFIS.Core.Writes;
using GenesisFIS.Infrastructure.Data;
using static GenesisFIS.Infrastructure.Data.RowReader;

namespace GenesisFIS.Infrastructure.Membership;

internal sealed class AdjustmentRepository(IDbConnectionFactory connections, IAuditLog audit) : SqlRepository(connections), IAdjustmentRepository
{
    private const string Select = """
        SELECT m.MBMNumber, m.MBMName, m.MBMPolicyNo, m.MBMIcBcPp, m.MBMDOB, m.MBMSex, m.MBMAddress1, m.MBMAddress2, m.MBMAddress3, m.CTYCode, m.STACode, m.MBMPostCode, m.RACCode,
               m.MBMStatus, m.INSCode, m.PLNCode, m.PAYCode, m.MBMPayorEffDate, m.MBMPayorExpDate, m.MBMBatchNo,
               t.NATCode, t.MBMSalutation, t.MBMEmail, t.MBMTelnoH, t.MBMTelNoM, t.MBMTelNoO
        FROM MBM m LEFT JOIN MBMTwo t ON t.MBMNumber = m.MBMNumber WHERE m.MBMNumber = @number
        """;

    private static DateOnly? Day(IDictionary<string, object?> r, string name) => D(r, name) is { } d ? DateOnly.FromDateTime(d) : null;

    private static AdjustmentMember Map(IDictionary<string, object?> r) => new(
        S(r, "MBMNumber") ?? "", S(r, "MBMName"), S(r, "MBMPolicyNo"), S(r, "MBMIcBcPp"), Day(r, "MBMDOB"), S(r, "MBMSex"), S(r, "MBMAddress1"), S(r, "MBMAddress2"), S(r, "MBMAddress3"),
        S(r, "CTYCode"), S(r, "STACode"), S(r, "MBMPostCode"), S(r, "RACCode"), S(r, "NATCode"), S(r, "MBMSalutation"), S(r, "MBMEmail"), S(r, "MBMTelnoH"), S(r, "MBMTelNoM"), S(r, "MBMTelNoO"),
        S(r, "MBMStatus"), S(r, "INSCode"), S(r, "PLNCode"), S(r, "PAYCode"), Day(r, "MBMPayorEffDate"), Day(r, "MBMPayorExpDate"), I(r, "MBMBatchNo"));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string RenewKey(string? payor, string? insurer, DateOnly? dob, string? name) => $"{payor?.Trim()}*{insurer?.Trim()}*{dob:yyyyMMdd}*{name?.Trim()}";

    public async Task<AdjustmentMember?> GetAsync(string memberNumber, CancellationToken ct = default)
    {
        if (!AdjustmentRules.IsValidMember(memberNumber)) throw new BusinessRuleException("Please enter a valid membership number.", field: "memberNumber");
        return (await QueryAsync(GenesisDb.Main, Select, new { number = memberNumber.Trim() }, ct)).Select(Map).FirstOrDefault();
    }

    public async Task<string?> NeighbourAsync(string memberNumber, bool next, CancellationToken ct = default)
    {
        if (!AdjustmentRules.IsValidMember(memberNumber)) throw new BusinessRuleException("Please enter a valid membership number.", field: "memberNumber");
        var sql = next ? "SELECT TOP 1 MBMNumber FROM MBM WHERE MBMNumber > @n ORDER BY MBMNumber" : "SELECT TOP 1 MBMNumber FROM MBM WHERE MBMNumber < @n ORDER BY MBMNumber DESC";
        return (await QueryAsync(GenesisDb.Main, sql, new { n = memberNumber.Trim() }, ct)).Select(r => S(r, "MBMNumber")?.Trim()).FirstOrDefault();
    }

    public async Task<string?> GetMgmtNoteAsync(string memberNumber, CancellationToken ct = default) =>
        (await QueryAsync(GenesisDb.Main, "SELECT MBMMmgtNotes FROM MBMTwo WHERE MBMNumber = @n", new { n = memberNumber.Trim() }, ct)).Select(r => S(r, "MBMMmgtNotes")).FirstOrDefault();

    public async Task SaveMgmtNoteAsync(string memberNumber, string? note, string user, CancellationToken ct = default)
    {
        if (!AdjustmentRules.IsValidMember(memberNumber)) throw new BusinessRuleException("Please enter a valid membership number.", field: "memberNumber");
        if ((note?.Length ?? 0) > 8000) throw new BusinessRuleException("The management note can be at most 8000 characters.", field: "note");
        var number = memberNumber.Trim();
        string? before;
        await using (var session = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM MBM WHERE MBMNumber = @number", new { number }, ct) == 0) throw BusinessRuleException.NotFound($"Member '{number}' does not exist.");
            before = (await session.QueryAsync("SELECT MBMMmgtNotes FROM MBMTwo WHERE MBMNumber = @number", new { number }, ct)).Select(r => S(r, "MBMMmgtNotes")).FirstOrDefault();
            if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @number", new { number }, ct) == 0)
                await session.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@number)", new { number }, ct);
            await session.ExecuteAsync("UPDATE MBMTwo SET MBMMmgtNotes = @note WHERE MBMNumber = @number", new { number, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() }, ct);
            await session.CommitAsync(ct);
        }
        audit.Record(new AuditEntry("Member.MgmtNote", "MBMTwo", number, user, new Dictionary<string, object?> { ["before"] = before, ["after"] = note }));
    }

    public async Task<AmendmentResult> AmendAsync(PrincipalAmendment a, string user, CancellationToken ct = default)
    {
        if (AdjustmentRules.Validate(a) is { } problem) throw new BusinessRuleException(problem.Message, field: problem.Field);
        var number = a.MemberNumber!.Trim();
        var name = a.Name!.Trim();
        var dob = a.DateOfBirth!.Value.ToDateTime(TimeOnly.MinValue);
        var today = DateTime.Today;
        var nameOrDobChanged = false;
        AdjustmentMember before, after;

        await using (var session = await Db.BeginSessionAsync(GenesisDb.Main, ct))
        {
            before = (await session.QueryAsync(Select, new { number }, ct)).Select(Map).FirstOrDefault() ?? throw BusinessRuleException.NotFound($"Member '{number}' does not exist.");
            nameOrDobChanged = !string.Equals(before.Name?.Trim(), name, StringComparison.Ordinal) || before.DateOfBirth != a.DateOfBirth;

            await session.ExecuteAsync("""
                UPDATE MBM SET MBMName = @name, MBMPolicyNo = @policy, MBMIcBcPp = @ic, MBMDOB = @dob, MBMSex = @sex, MBMAddress1 = @a1, MBMAddress2 = @a2, MBMAddress3 = @a3,
                       CTYCode = @city, STACode = @state, MBMPostCode = @post, RACCode = @race WHERE MBMNumber = @number
                """, new { number, name, policy = Blank(a.PolicyNo), ic = a.IcBcPp!.Trim(), dob, sex = a.Sex, a1 = Blank(a.Address1), a2 = Blank(a.Address2), a3 = Blank(a.Address3),
                    city = Blank(a.City), state = Blank(a.State), post = Blank(a.PostCode), race = Blank(a.Race) }, ct);

            // The same person across renewal years (MBMNumber prefix before the '*') shares name and date of birth, as in the legacy form.
            if (nameOrDobChanged && number.Contains('*'))
                await session.ExecuteAsync("UPDATE MBM SET MBMName = @name, MBMDOB = @dob WHERE MBMNumber LIKE @prefix AND MBMNumber <> @number",
                    new { name, dob, number, prefix = number[..(number.IndexOf('*') + 1)].Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%" }, ct);

            if (await session.ScalarAsync<int>("SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @number", new { number }, ct) == 0)
                await session.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber) VALUES (@number)", new { number }, ct);
            await session.ExecuteAsync("""
                UPDATE MBMTwo SET NATCode = @nat, MBMSalutation = @sal, MBMEmail = @email, MBMTelnoH = @th, MBMTelNoM = @tm, MBMTelNoO = @to,
                       MBMLastEndorseStatus = 'A', MBMENDtEffDate = @eff, MBMENDtBordxDate = @bordx, MBMLastAdjustmentDate = @today, MBMLastUpdateUser = @user
                WHERE MBMNumber = @number
                """, new { number, nat = Blank(a.Nationality), sal = Blank(a.Salutation), email = Blank(a.Email), th = Blank(a.TelHome), tm = Blank(a.TelMobile), to = Blank(a.TelOffice),
                    eff = a.EffectiveDate!.Value.ToDateTime(TimeOnly.MinValue), bordx = a.EndorsementBordxDate!.Value.ToDateTime(TimeOnly.MinValue), today, user }, ct);

            await session.ExecuteAsync("""
                INSERT INTO MBMAmendHistory (MBMNumber, MBMPolicyNo, MBMIcBcPp, MBMName, MBMExpiryDate, MBMPlanCode, MBMAmendEdtType, MBMAEndtBordxDate, MBMAEndtEffDate, MBMAmendUser, MBMAmendDate, MBMAEndtBatchNo)
                VALUES (@number, @policy, @ic, @name, @expiry, @plan, 'A', @bordx, @eff, @user, @today, @batch)
                """, new { number, policy = Blank(a.PolicyNo), ic = a.IcBcPp!.Trim(), name, expiry = before.PayorExpiryDate?.ToDateTime(TimeOnly.MinValue), plan = before.Plan,
                    bordx = a.EndorsementBordxDate!.Value.ToDateTime(TimeOnly.MinValue), eff = a.EffectiveDate!.Value.ToDateTime(TimeOnly.MinValue), user, today, batch = (short)a.BatchNo!.Value }, ct);

            after = (await session.QueryAsync(Select, new { number }, ct)).Select(Map).First();
            await session.CommitAsync(ct);
        }

        // Copies in the maintenance database (cross reference, renewal sequence key) cannot share the transaction; they are idempotent, so a failure is reported and can be repeated.
        bool crossOk = true, renewOk = true;
        try
        {
            await using var maint = await Db.BeginSessionAsync(GenesisDb.Maintenance, ct);
            await maint.ExecuteAsync("UPDATE MBMCrossReference SET MBMName = @name, MBMPolicyNo = @policy, MBMIcBcPp = @ic, MBMDOB = @dob WHERE MBMNumber = @number",
                new { number, name, policy = Blank(a.PolicyNo), ic = a.IcBcPp!.Trim(), dob }, ct);
            if (nameOrDobChanged && !string.IsNullOrWhiteSpace(before.Plan) && !string.IsNullOrWhiteSpace(before.Insurer))
                await maint.ExecuteAsync("UPDATE MBMSEQRenew05 SET RewMBMSEQKey = @newKey WHERE RewMBMSEQKey = @oldKey",
                    new { newKey = RenewKey(before.Payor, before.Insurer, a.DateOfBirth, name), oldKey = RenewKey(before.Payor, before.Insurer, before.DateOfBirth, before.Name) }, ct);
            await maint.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            crossOk = renewOk = false;
        }

        audit.Record(new AuditEntry("Member.Amend", "MBM", number, user, new Dictionary<string, object?>
            { ["before"] = before, ["after"] = after, ["endorsementBordx"] = a.EndorsementBordxDate, ["effective"] = a.EffectiveDate, ["batch"] = a.BatchNo, ["crossReferenceUpdated"] = crossOk, ["renewalKeyUpdated"] = renewOk }));
        return new AmendmentResult(after, crossOk, renewOk);
    }
}

