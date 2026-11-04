using System.Net;
using System.Net.Http.Json;
using Dapper;
using GenesisFIS.Core.Data;
using GenesisFIS.Core.Security;
using GenesisFIS.Core.Users;

namespace GenesisFIS.Tests;

public class UserMaintenanceRuleTests
{
    private static UserSaveRequest R(string? password = "secret12") => new("ZZU1", "Test User", null, password, "ADM", "ME", "A", "N", "1");

    [Fact]
    public void The_catalog_has_one_entry_per_legacy_check_box()
    {
        Assert.Equal(Enumerable.Range(1, 178), UserMaintenanceRules.Positions.Select(p => p.Position));
        Assert.All(UserMaintenanceRules.Positions, p => Assert.False(string.IsNullOrWhiteSpace(p.Caption)));
        Assert.Equal("Maintenance", UserMaintenanceRules.Positions.Single(p => p.Position == 112).Module);
    }

    [Fact]
    public void Saves_are_checked_as_the_legacy_form_and_the_column_sizes()
    {
        Assert.Null(UserMaintenanceRules.Validate(R(), true));
        Assert.Equal("password", UserMaintenanceRules.Validate(R(null), true)?.Field);
        Assert.Null(UserMaintenanceRules.Validate(R(null), false));
        Assert.Equal("password", UserMaintenanceRules.Validate(R("short"), false)?.Field);
        Assert.Equal("password", UserMaintenanceRules.Validate(R("toolongpassword"), false)?.Field);
        Assert.Equal("status", UserMaintenanceRules.Validate(R() with { Status = "X" }, true)?.Field);
        Assert.Equal("authorised", UserMaintenanceRules.Validate(R() with { Authorised = null }, true)?.Field);
        Assert.Equal("code", UserMaintenanceRules.Validate(R() with { Code = "ABCDEFGHIJK" }, true)?.Field);
        Assert.Equal("access", UserMaintenanceRules.Validate(R() with { Access = "102" }, true)?.Field);
    }

    [Fact]
    public void The_access_string_is_padded_to_the_check_boxes_and_keeps_flags_beyond_them()
    {
        Assert.Equal("1" + new string('0', 177), UserMaintenanceRules.MergeAccess("1", null));
        Assert.Equal("01" + new string('0', 176) + "11", UserMaintenanceRules.MergeAccess("01", new string('0', 178) + "11"));
        Assert.Equal(2, UserMaintenanceRules.Granted("11"));
    }
}

public class UserMaintenanceTests(WriteApiFactory factory) : IClassFixture<WriteApiFactory>, IAsyncLifetime
{
    private const string Url = "/api/maintenance/users", Code = "ZZUSR01", Old = "ZZUSR02";
    private HttpClient Client(int position = 112) => factory.ClientFor(WriteApiFactory.Access(position));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => factory.ResetAsync();

    private static string Access(params int[] positions)
    {
        var a = new string('0', 178).ToCharArray();
        foreach (var p in positions) a[p - 1] = '1';
        return new string(a);
    }

    /// <summary>The signed-in test user, and an old user whose access string is 180 long (2 flags beyond the check boxes).</summary>
    private async Task SeedAsync()
    {
        await using var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        await maint.ExecuteAsync("""
            IF NOT EXISTS (SELECT 1 FROM USR WHERE USRCode = @test)
                INSERT INTO USR (USRCode, USRName, USRPassword, USRStatus, AuthorisedStatus, GRPCode, SRVCode, USRAccess) VALUES (@test, 'TEST USER', 'X', 'A', 'N', 'ADM', 'ME', @testAccess);
            INSERT INTO USR (USRCode, USRName, USRPassword, USRStatus, AuthorisedStatus, GRPCode, SRVCode, USRAccess) VALUES (@old, 'OLD USER', @pwd, 'A', 'N', 'ADM', 'ME', @oldAccess);
            """, new { test = WriteApiFactory.TestUser, testAccess = Access(112), old = Old, pwd = LegacyPasswordCipher.Encrypt("oldpass12"), oldAccess = Access(1) + "11" });
    }

    private static async Task<T> Ok<T>(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static UserSaveRequest New(string? password = "newpass12") => new(Code, "NEW USER", "LG1", password, "ADM", "ME", "A", "Y", Access(1, 2, 64));

    [DatabaseFact]
    public async Task Creates_a_user_with_the_legacy_password_cipher_and_access_string()
    {
        await SeedAsync();
        var created = await Ok<UserDetail>(await Client().PostAsJsonAsync(Url, New()));
        Assert.Equal(("NEW USER", "A", "Y", "ADM", "ME", 178, true), (created.Name, created.Status, created.Authorised, created.Group, created.ServiceProvider, created.Access.Length, created.MustChangePassword));
        Assert.Equal(3, UserMaintenanceRules.Granted(created.Access));
        await using var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        var (pwd, db) = await maint.QuerySingleAsync<(string, string)>("SELECT USRPassword, DBCode FROM USR WHERE USRCode = @Code", new { Code });
        Assert.True(LegacyPasswordCipher.Verify("newpass12", pwd));
        Assert.Equal("FW", db);
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsJsonAsync(Url, New())).StatusCode);
        Assert.Contains((await Ok<List<UserSummary>>(await Client().GetAsync($"{Url}?search=ZZUSR"))), u => u.Code == Code);
        Assert.Contains(factory.Audit.Entries, e => e.Action == "User.Create" && e.Key == Code);
    }

    [DatabaseFact]
    public async Task Update_keeps_the_password_unless_one_is_typed_and_keeps_flags_beyond_the_check_boxes()
    {
        await SeedAsync();
        var updated = await Ok<UserDetail>(await Client().PutAsJsonAsync($"{Url}/{Old}", new UserSaveRequest(Old, "OLD USER 2", null, null, "ADM", "ME", "S", "N", Access(1, 5))));
        Assert.Equal(("OLD USER 2", "S", 180), (updated.Name, updated.Status, updated.Access.Length));
        Assert.EndsWith("11", updated.Access);
        await using var maint = await factory.Db.OpenAsync(GenesisDb.Maintenance);
        Assert.True(LegacyPasswordCipher.Verify("oldpass12", await maint.ExecuteScalarAsync<string>("SELECT USRPassword FROM USR WHERE USRCode = @Old", new { Old })));
        await Ok<UserDetail>(await Client().PutAsJsonAsync($"{Url}/{Old}", new UserSaveRequest(Old, "OLD USER 2", null, "changed12", "ADM", "ME", "A", "N", Access(1))));
        Assert.True(LegacyPasswordCipher.Verify("changed12", await maint.ExecuteScalarAsync<string>("SELECT USRPassword FROM USR WHERE USRCode = @Old", new { Old })));
        var audit = factory.Audit.Entries.First(e => e.Action == "User.Update");
        Assert.Equal([5], (List<int>)audit.Details["granted"]!);
        Assert.False((bool)audit.Details["passwordChanged"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PutAsJsonAsync($"{Url}/{Old}", new UserSaveRequest(Old, "X", null, null, "ZZZ", "ME", "A", "N", null))).StatusCode);
    }

    [DatabaseFact]
    public async Task Users_cannot_lock_themselves_out_and_deletes_are_archived()
    {
        await SeedAsync();
        var me = WriteApiFactory.TestUser;
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PutAsJsonAsync($"{Url}/{me}", new UserSaveRequest(me, "TEST USER", null, null, "ADM", "ME", "S", "N", Access(112)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PutAsJsonAsync($"{Url}/{me}", new UserSaveRequest(me, "TEST USER", null, null, "ADM", "ME", "A", "N", Access(1)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().DeleteAsync($"{Url}/{me}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Client().DeleteAsync($"{Url}/{Old}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"{Url}/{Old}")).StatusCode);
        await using var arc = await factory.Db.OpenAsync(GenesisDb.Archive);
        Assert.Equal(1, await arc.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM USRArchive WHERE USRCode = @Old AND USRName = 'OLD USER' AND USRLastUpdateUser = @me", new { Old, me }));
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(111).GetAsync($"{Url}/options")).StatusCode);
        var options = await Ok<UserMaintenanceOptions>(await Client().GetAsync($"{Url}/options"));
        Assert.Equal(178, options.Positions.Count);
        Assert.Contains(options.Groups, g => g.Code == "ADM");
    }
}
