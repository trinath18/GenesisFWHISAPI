using GenesisFIS.Core.Membership;

namespace GenesisFIS.Tests;

public class MalaysianStatesTests
{
    [Theory]
    [InlineData("10", "SELANGOR")]
    [InlineData("01", "JOHOR")]
    [InlineData("14", "KUALA LUMPUR")]
    [InlineData("7", "PENANG")]
    [InlineData(" 11 ", "TERENGGANU")]
    [InlineData("SELANGOR", "SELANGOR")]
    [InlineData("ZZNIL", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_group_company_state_code_shows_as_the_state_name(string? state, string? expected) => Assert.Equal(expected, MalaysianStates.Name(state));
}
