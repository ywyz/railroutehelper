using System.Text.RegularExpressions;

namespace RailRouteHelper.Core;

/// <summary>
/// 游戏地图车次编号与铁路公开车次编号之间的统一转换规则。
/// </summary>
public static class TrainCodeRules
{
    private static readonly Regex CompositeCodePattern = new(
        @"^((?:0)?[A-Z]{1,2}\d+)([A-Z]{1,2}\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SlashCompositeCodePattern = new(
        @"^((?:0)?[A-Z]{1,2}\d+)/((?:0)?[A-Z]{1,2}\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// 生成用于 12306 和离线车次库查询的编号。显示及播报仍保留地图原始编号。
    /// </summary>
    public static string? NormalizeLookupCode(string? code)
    {
        string? normalized = NormalizeDisplayCode(code);
        if (string.IsNullOrEmpty(normalized)) return null;

        // 部分地图用“通”表示通过列车，或用前导 0 区分地图内同名列车。
        if (normalized.Length > 1 && normalized[0] == '通')
            normalized = normalized.Substring(1);
        if (normalized.Length > 2 && normalized[0] == '0' && char.IsLetter(normalized[1]))
            normalized = normalized.Substring(1);

        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    /// <summary>
    /// 拆分地图复合车次，例如 G6642G6641、DJ8598G3401、0G1703/G1704、0Y2/Y1。
    /// </summary>
    public static bool TrySplitCompositeCode(string? code, out string? firstLeg, out string? secondLeg)
    {
        firstLeg = null;
        secondLeg = null;
        string? normalized = NormalizeDisplayCode(code);
        if (string.IsNullOrEmpty(normalized)) return false;

        Match slashMatch = SlashCompositeCodePattern.Match(normalized);
        if (slashMatch.Success)
        {
            firstLeg = slashMatch.Groups[1].Value;
            secondLeg = slashMatch.Groups[2].Value;

            // 秦皇岛地图的 Y 字头是地图内游车编号，斜杠后会省略共同的地图前导 0。
            // 普通国铁车次（如 0G1703/G1704）不继承这个 0。
            if (firstLeg.StartsWith("0Y", StringComparison.Ordinal) &&
                secondLeg.StartsWith("Y", StringComparison.Ordinal))
                secondLeg = "0" + secondLeg;
            return true;
        }

        Match match = CompositeCodePattern.Match(normalized);
        if (!match.Success) return false;
        firstLeg = match.Groups[1].Value;
        secondLeg = match.Groups[2].Value;
        return true;
    }

    /// <summary>按当前运行阶段选择复合车次的活动编号。</summary>
    public static string? SelectActiveCode(string? code, bool secondLegActive)
    {
        return TrySplitCompositeCode(code, out string? firstLeg, out string? secondLeg)
            ? (secondLegActive ? secondLeg : firstLeg)
            : NormalizeDisplayCode(code);
    }

    private static string? NormalizeDisplayCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        string normalized = new(code
            .Where(character => !char.IsWhiteSpace(character) && character != '次')
            .ToArray());
        normalized = normalized.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
