using System.Text;

namespace Genesis.Core.Security;

/// <summary>
/// Reproduces the VB6 Crypt() routine used for USR.USRPassword so existing users can sign in
/// while the database stays unchanged. The scheme is weak and reversible; it is kept only for
/// compatibility and should be replaced once the schema can change or SSO is introduced.
/// </summary>
public static class LegacyPasswordCipher
{
    private static readonly Encoding Ansi = CreateAnsi();

    public static string Encrypt(string plain)
    {
        var sb = new StringBuilder(plain.Length);
        for (var i = 1; i <= plain.Length; i++)
        {
            var value = Ansi.GetBytes(plain[i - 1].ToString())[0] + i * i;
            while (value > 255) value -= 255;
            sb.Append(Ansi.GetString([(byte)value]));
        }
        return sb.ToString().ToUpperInvariant();
    }

    public static bool Verify(string plain, string? stored) =>
        !string.IsNullOrEmpty(plain) && stored is not null &&
        string.Equals(Encrypt(plain), stored.ToUpperInvariant(), StringComparison.Ordinal);

    private static Encoding CreateAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }
}
