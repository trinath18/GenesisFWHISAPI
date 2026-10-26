namespace Genesis.Core.Security;

/// <summary>
/// USR.USRAccess is a string of '0'/'1' flags, one position per legacy screen (1-based).
/// </summary>
public readonly record struct AccessString(string Value)
{
    public bool IsAllowed(int position) =>
        position >= 1 && position <= Value.Length && Value[position - 1] == '1';
}
