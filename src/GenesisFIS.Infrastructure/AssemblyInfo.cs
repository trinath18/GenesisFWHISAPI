using System.Runtime.CompilerServices;

// Lets the tests check internal helpers directly (for example LegacyFilter, which guards the legacy dynamic-SQL procedures).
[assembly: InternalsVisibleTo("GenesisFIS.Tests")]
