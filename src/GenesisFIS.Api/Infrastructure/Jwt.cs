using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GenesisFIS.Core.Users;
using Microsoft.IdentityModel.Tokens;

namespace GenesisFIS.Api.Infrastructure;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string Key { get; set; } = "";
    public int ExpiryMinutes { get; set; } = 480;

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(Key));
}

public static class GenesisClaims
{
    public const string Group = "group";
    public const string Department = "dept";
    public const string Access = "access";
    public const string MustChangePassword = "must_change_pwd";
}

public sealed class TokenService(JwtOptions options)
{
    public (string Token, DateTime ExpiresAt) Create(UserRecord user)
    {
        var expires = DateTime.UtcNow.AddMinutes(options.ExpiryMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Code),
            new(ClaimTypes.Name, user.Name),
            new(GenesisClaims.Group, user.GroupCode ?? ""),
            new(GenesisClaims.Department, user.DepartmentCode ?? ""),
            new(GenesisClaims.Access, user.Access ?? ""),
            new(GenesisClaims.MustChangePassword, user.MustChangePassword ? "true" : "false")
        };
        var token = new JwtSecurityToken(options.Issuer, options.Audience, claims,
            expires: expires,
            signingCredentials: new SigningCredentials(options.SigningKey, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
