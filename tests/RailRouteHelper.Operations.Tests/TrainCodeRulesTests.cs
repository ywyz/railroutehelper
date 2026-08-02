using RailRouteHelper.Core;

namespace RailRouteHelper.Operations.Tests;

public sealed class TrainCodeRulesTests
{
    [Theory]
    [InlineData("0G2524", "G2524")]
    [InlineData(" 0g2524 次", "G2524")]
    [InlineData("通G2524", "G2524")]
    [InlineData("DJ8598", "DJ8598")]
    public void NormalizeLookupCode_removes_only_map_prefixes(string input, string expected)
    {
        Assert.Equal(expected, TrainCodeRules.NormalizeLookupCode(input));
    }

    [Theory]
    [InlineData("G6642G6641", "G6642", "G6641")]
    [InlineData("0G6642G6641", "0G6642", "G6641")]
    [InlineData("DJ8598G3401", "DJ8598", "G3401")]
    [InlineData("0G1703/G1704", "0G1703", "G1704")]
    [InlineData(" 0y2 / y1 次", "0Y2", "0Y1")]
    public void TrySplitCompositeCode_supports_map_variants(
        string input,
        string expectedFirst,
        string expectedSecond)
    {
        Assert.True(TrainCodeRules.TrySplitCompositeCode(input, out string? first, out string? second));
        Assert.Equal(expectedFirst, first);
        Assert.Equal(expectedSecond, second);
    }

    [Theory]
    [InlineData("0G6642G6641", false, "0G6642")]
    [InlineData("0G6642G6641", true, "G6641")]
    [InlineData("DJ8598G3401", false, "DJ8598")]
    [InlineData("DJ8598G3401", true, "G3401")]
    [InlineData("0G1703/G1704", false, "0G1703")]
    [InlineData("0G1703/G1704", true, "G1704")]
    [InlineData("0Y2/Y1", false, "0Y2")]
    [InlineData("0Y2/Y1", true, "0Y1")]
    public void SelectActiveCode_switches_after_stop(string input, bool secondLeg, string expected)
    {
        Assert.Equal(expected, TrainCodeRules.SelectActiveCode(input, secondLeg));
    }

    [Theory]
    [InlineData("G2524")]
    [InlineData("0G2524")]
    [InlineData("DJ8598")]
    public void TrySplitCompositeCode_rejects_single_codes(string input)
    {
        Assert.False(TrainCodeRules.TrySplitCompositeCode(input, out _, out _));
    }
}
