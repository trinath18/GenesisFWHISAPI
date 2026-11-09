using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class AdjustmentRuleTests
{
    private static PrincipalAmendment Ok() => new("ZZADJ0001*02", "NEW NAME", "POL1", "IC123", new(1980, 5, 6), "F", "A1", null, null, "KL", "WP", "50000", "01", "MY", "MS", "a@b.c",
        "0312345", "0123456789", null, new(2024, 2, 1), new(2024, 1, 15), 4);

    [Fact]
    public void Valid_amendment_passes() => Assert.Null(AdjustmentRules.Validate(Ok()));

    [Fact]
    public void Required_fields_lengths_and_dates_are_checked()
    {
        Assert.Equal("memberNumber", AdjustmentRules.Validate(Ok() with { MemberNumber = "a'; --" })!.Value.Field);
        Assert.Equal("name", AdjustmentRules.Validate(Ok() with { Name = " " })!.Value.Field);
        Assert.Equal("name", AdjustmentRules.Validate(Ok() with { Name = new string('x', 61) })!.Value.Field);
        Assert.Equal("icBcPp", AdjustmentRules.Validate(Ok() with { IcBcPp = "" })!.Value.Field);
        Assert.Equal("postCode", AdjustmentRules.Validate(Ok() with { PostCode = "1234567" })!.Value.Field);
        Assert.Equal("sex", AdjustmentRules.Validate(Ok() with { Sex = "X" })!.Value.Field);
        Assert.Equal("dateOfBirth", AdjustmentRules.Validate(Ok() with { DateOfBirth = new(2999, 1, 1) })!.Value.Field);
        Assert.Equal("endorsementBordxDate", AdjustmentRules.Validate(Ok() with { EndorsementBordxDate = null })!.Value.Field);
        Assert.Equal("effectiveDate", AdjustmentRules.Validate(Ok() with { EffectiveDate = null })!.Value.Field);
        Assert.Equal("batchNo", AdjustmentRules.Validate(Ok() with { BatchNo = 40000 })!.Value.Field);
        Assert.Equal("address1", AdjustmentRules.Validate(Ok() with { Address1 = "a\tb" })!.Value.Field);
    }

    private static readonly MemberAddress Group = new("NO 9 JALAN", "TAMAN JAYA", null, "MELAKA", "04", "75350");

    [Fact]
    public void The_group_company_address_fills_a_member_without_an_address()
    {
        Assert.Equal(Group, AdjustmentRules.ShownAddress(new(null, " ", "NA", null, "NA", ""), Group));
        // A line of the member's own keeps all their lines; only a missing city, state or postcode comes from the group company.
        Assert.Equal(new MemberAddress("OWN 1", null, null, "MELAKA", "04", "75350"), AdjustmentRules.ShownAddress(new("OWN 1", "NA", null, "NA", null, "NA"), Group));
        Assert.Equal(new MemberAddress("OWN 1", null, null, "KL", "14", "50000"), AdjustmentRules.ShownAddress(new("OWN 1", null, null, "KL", "14", "50000"), null));
    }

    [Fact]
    public void A_long_group_address_sent_back_unchanged_does_not_fail_the_length_check()
    {
        var longLine = new string('x', 80);
        var shown = Group with { Address1 = longLine };
        Assert.Null(AdjustmentRules.Validate(AdjustmentRules.ForValidation(Ok() with { Address1 = longLine }, shown)));
        Assert.Equal("address1", AdjustmentRules.Validate(AdjustmentRules.ForValidation(Ok() with { Address1 = longLine + "y" }, shown))!.Value.Field);
    }
}

public class AdjustmentTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/membership/adjustment";
    private const string Member = "ZZADJ0001*02";
    private const string Earlier = "ZZADJ0001*01";
    private HttpClient Client() => factory.ClientFor(WriteApiFactory.Access(2));

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static PrincipalAmendment Amend(string member = Member) => new(member, "  NEW NAME  ", "POL-NEW", "IC-NEW", new(1980, 5, 6), "F", "1 Road", "", null, "KL", "WP", "50000", "01", "MY",
        "MS", "x@y.z", "0312345", "0123456789", "", new(2024, 2, 1), new(2024, 1, 15), 4);

    private async Task Seed()
    {
        await using var main = await factory.Db.OpenAsync(GenesisDb.Main);
        foreach (var n in new[] { Earlier, Member })
        {
            await main.ExecuteAsync("INSERT INTO MBM (MBMNumber, MBMStatus, MBMExpiredStatus, MBMName, MBMDOB, MBMIcBcPp, MBMPolicyNo, PAYCode, INSCode, PLNCode, MBMSex, MBMPayorExpDate) VALUES (@n, 'A', 'N', 'OLD NAME', '1979-01-01', 'IC-OLD', 'POL-OLD', 'ZZ', 'G', 'ZZPL', 'M', '2024-12-31')", new { n });
            await main.ExecuteAsync("INSERT INTO MBMTwo (MBMNumber, MBMEmail) VALUES (@n, 'old@x.y')", new { n });
        }
        await using var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        await maint.ExecuteAsync("INSERT INTO MBMCrossReference (MBMNumber, MBMName, MBMDOB, MBMIcBcPp) VALUES (@n, 'OLD NAME', '1979-01-01', 'IC-OLD')", new { n = Member });
        await maint.ExecuteAsync("INSERT INTO MBMSEQRenew05 (RewMBMSEQKey, INSCode, RewSEQNo, RewSEQID) VALUES ('ZZ*G*19790101*OLD NAME', 'G', 'ZZTEST01', '01')");
    }

    private async Task<T?> Scalar<T>(GenesisDb db, string sql, object? args = null)
    {
        await using var conn = await factory.Db.OpenAsync(db);
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    [Fact]
    public async Task Requires_authentication_and_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}?memberNumber=X")).StatusCode);
        var other = factory.ClientFor(WriteApiFactory.Access(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}?memberNumber=X")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync($"{Url}/amendment", Amend())).StatusCode);
    }

    [DatabaseFact]
    public async Task Member_lookup_returns_details_or_404()
    {
        await Seed();
        var m = await Client().GetFromJsonAsync<AdjustmentMember>($"{Url}?memberNumber={Uri.EscapeDataString(Member)}");
        Assert.Equal(("OLD NAME", "IC-OLD", new DateOnly(1979, 1, 1), "old@x.y"), (m!.Name, m.IcBcPp, m.DateOfBirth, m.Email));
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"{Url}?memberNumber=ZZNONE*01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync($"{Url}?memberNumber=a'b")).StatusCode);
    }

    [DatabaseFact]
    public async Task Amendment_updates_member_contact_history_and_copies_and_audits()
    {
        await Seed();
        var res = await Client().PutAsJsonAsync($"{Url}/amendment", Amend());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var r = (await res.Content.ReadFromJsonAsync<AmendmentResult>())!;
        Assert.True(r.CrossReferenceUpdated && r.RenewalKeyUpdated);
        Assert.Equal(("NEW NAME", "F", "1 Road"), (r.Member.Name, r.Member.Sex, r.Member.Address1));

        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMName = 'NEW NAME' AND MBMIcBcPp = 'IC-NEW' AND MBMPolicyNo = 'POL-NEW' AND MBMDOB = '1980-05-06' AND MBMAddress2 IS NULL", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @n AND MBMEmail = 'x@y.z' AND MBMTelNoO IS NULL AND MBMLastEndorseStatus = 'A' AND MBMLastUpdateUser = 'TEST' AND MBMENDtEffDate = '2024-01-15' AND MBMENDtBordxDate = '2024-02-01'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n AND MBMAmendEdtType = 'A' AND MBMName = 'NEW NAME' AND MBMAEndtBatchNo = 4 AND MBMAmendUser = 'TEST' AND MBMPlanCode = 'ZZPL' AND MBMExpiryDate = '2024-12-31'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMName = 'NEW NAME' AND MBMDOB = '1980-05-06'", new { n = Earlier }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM MBMCrossReference WHERE MBMNumber = @n AND MBMName = 'NEW NAME' AND MBMIcBcPp = 'IC-NEW' AND MBMDOB = '1980-05-06'", new { n = Member }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM MBMSEQRenew05 WHERE RewMBMSEQKey = 'ZZ*G*19800506*NEW NAME'"));
        Assert.Equal(0, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM MBMSEQRenew05 WHERE RewMBMSEQKey = 'ZZ*G*19790101*OLD NAME'"));
        var entry = Assert.Single(factory.Audit.Entries);
        Assert.Equal(("Member.Amend", "TEST"), (entry.Action, entry.User));
    }

    [DatabaseFact]
    public async Task Changing_only_contact_details_leaves_name_dob_renewal_key_and_other_years_alone()
    {
        await Seed();
        var a = Amend() with { Name = "OLD NAME", DateOfBirth = new(1979, 1, 1), Email = "changed@x.y" };
        Assert.Equal(HttpStatusCode.OK, (await Client().PutAsJsonAsync($"{Url}/amendment", a)).StatusCode);
        Assert.Equal(1, await Scalar<int>(GenesisDb.Maintenance, "SELECT COUNT(*) FROM MBMSEQRenew05 WHERE RewMBMSEQKey = 'ZZ*G*19790101*OLD NAME'"));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMName = 'OLD NAME' AND MBMDOB = '1979-01-01'", new { n = Earlier }));
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMTwo WHERE MBMNumber = @n AND MBMEmail = 'changed@x.y'", new { n = Member }));
    }

    [DatabaseFact]
    public async Task Invalid_or_unknown_members_change_nothing()
    {
        await Seed();
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PutAsJsonAsync($"{Url}/amendment", Amend("ZZNONE*01"))).StatusCode);
        var bad = await Client().PutAsJsonAsync($"{Url}/amendment", Amend() with { Sex = "Q" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("sex", await bad.Content.ReadAsStringAsync());
        Assert.Equal(1, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber = @n AND MBMName = 'OLD NAME'", new { n = Member }));
        Assert.Equal(0, await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBMAmendHistory WHERE MBMNumber = @n", new { n = Member }));
        Assert.Empty(factory.Audit.Entries);
    }

    [DatabaseFact]
    public async Task Quotes_and_injection_text_are_stored_literally()
    {
        await Seed();
        var evil = "O'BRIEN'; DELETE FROM MBM;--";
        Assert.Equal(HttpStatusCode.OK, (await Client().PutAsJsonAsync($"{Url}/amendment", Amend() with { Name = evil.Length > 60 ? "O'BRIEN" : evil })).StatusCode);
        Assert.True(await Scalar<int>(GenesisDb.Main, "SELECT COUNT(*) FROM MBM WHERE MBMNumber IN (@a, @b)", new { a = Member, b = Earlier }) == 2);
    }

    [DatabaseFact]
    public async Task The_screen_shows_the_group_company_address_and_expiry_and_saving_keeps_mbm_unless_changed()
    {
        await Seed();
        var longLine = new string('A', 80);
        await using (var main = await factory.Db.OpenAsync(GenesisDb.Main))
        {
            await main.ExecuteAsync("UPDATE MBM SET MBMInsurerUniqueNo = 'ZZGRPREF' WHERE MBMNumber = @Member", new { Member });
            await main.ExecuteAsync("""
                INSERT INTO GroupCompanyFW (CoInsurerRefNo, CoGRPPaycode, CoGRPAdd1, CoGRPAdd2, CoGRPCity, CoGRPPCode, CoGRPPState, CoPolicyExpDate)
                VALUES ('ZZGRPREF', 'ZZ', @longLine, 'TAMAN JAYA', 'MELAKA', '75350', '04', '2025-06-30')
                """, new { longLine });
        }
        var m = await Client().GetFromJsonAsync<AdjustmentMember>($"{Url}?memberNumber={Uri.EscapeDataString(Member)}");
        // The group company state code (04) shows as its name.
        Assert.Equal((longLine, "TAMAN JAYA", "MELAKA", "MELAKA", "75350"), (m!.Address1, m.Address2, m.City, m.State, m.PostCode));
        Assert.Equal(new DateOnly(2025, 6, 30), m.PayorExpiryDate);

        // Saving what the screen showed leaves MBM's own (empty) address alone, however long the group company's is.
        var same = Amend() with { Address1 = m.Address1, Address2 = m.Address2, Address3 = m.Address3, City = m.City, State = m.State, PostCode = m.PostCode };
        var res = await Client().PutAsJsonAsync($"{Url}/amendment", same);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Null(await Scalar<string>(GenesisDb.Main, "SELECT MBMAddress1 FROM MBM WHERE MBMNumber = @Member", new { Member }));
        Assert.Equal(new DateTime(2025, 6, 30), await Scalar<DateTime>(GenesisDb.Main, "SELECT TOP 1 MBMExpiryDate FROM MBMAmendHistory WHERE MBMNumber = @Member ORDER BY MBMAmendID DESC", new { Member }));

        // A changed line is stored, and stays on screen.
        var changed = await Client().PutAsJsonAsync($"{Url}/amendment", same with { Address1 = "1 NEW ROAD" });
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        Assert.Equal("1 NEW ROAD", await Scalar<string>(GenesisDb.Main, "SELECT MBMAddress1 FROM MBM WHERE MBMNumber = @Member", new { Member }));
        Assert.Equal("1 NEW ROAD", (await Client().GetFromJsonAsync<AdjustmentMember>($"{Url}?memberNumber={Uri.EscapeDataString(Member)}"))!.Address1);
    }
}
