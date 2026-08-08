using System.Text;
using System.Text.RegularExpressions;

namespace RailRouteHelper.Core;

/// <summary>
/// 游戏地图车次编号与铁路公开车次编号之间的统一转换规则。
/// </summary>
public static class TrainCodeRules
{
    private static readonly Regex TrainCodeTokenPattern = new(
        @"(?:0)?[A-Z]{1,2}\d+",
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

        // 部分地图在车号后附加中文括注，例如沈阳枢纽的“Z212(技停不办客)”。
        // 12306 和离线车次库只认纯车号，查询前去掉括注及括注外的中文。
        normalized = StripChineseAnnotation(normalized);

        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    /// <summary>
    /// 去掉车次编号中的中文括注后缀，例如 "Z212(技停不办客)" → "Z212"。
    /// 同时移除括注外的中文，保留前导“通”和英数字号。供查询和非入图播报使用。
    /// </summary>
    public static string? StripChineseAnnotation(string? code)
    {
        if (string.IsNullOrEmpty(code)) return code;

        int paren = code.IndexOfAny(new[] { '(', '（' });
        if (paren >= 0)
            code = code.Substring(0, paren);

        var sb = new StringBuilder();
        foreach (char c in code)
        {
            // CJK Unified Ideographs 范围内的中文统一表意文字一律移除。
            if (c >= 0x4E00 && c <= 0x9FFF) continue;
            sb.Append(c);
        }

        string result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    /// <summary>拆分地图车号中的所有连续运行段。</summary>
    /// <remarks>
    /// 支持相邻、斜杠以及三段以上形式，例如 G6642G6641、0G1703/G1704、
    /// G3220G3219G1792。无法完整解析时返回只含原车号的一项，避免截断未知编号。
    /// </remarks>
    public static IReadOnlyList<string> SplitCompositeCodes(string? code)
    {
        string? normalized = NormalizeDisplayCode(code);
        if (string.IsNullOrEmpty(normalized)) return Array.Empty<string>();

        // 中文括注只说明地图运行要求，不属于车号主体。
        int annotation = normalized.IndexOfAny(new[] { '(', '（' });
        string body = annotation >= 0 ? normalized.Substring(0, annotation) : normalized;
        bool throughPrefix = body.Length > 1 && body[0] == '通';
        if (throughPrefix) body = body.Substring(1);

        MatchCollection matches = TrainCodeTokenPattern.Matches(body);
        if (matches.Count == 0) return new[] { normalized };

        // 除车号之间允许的斜杠外，整个主体必须都被正则消费。
        string consumed = string.Concat(matches.Cast<Match>().Select(match => match.Value));
        if (!string.Equals(body.Replace("/", "", StringComparison.Ordinal), consumed, StringComparison.Ordinal))
            return new[] { normalized };

        var result = matches.Cast<Match>().Select(match => match.Value).ToList();
        if (throughPrefix) result[0] = "通" + result[0];

        // 秦皇岛地图的 Y 字头是地图内游车编号，后续段会省略共同的地图前导 0。
        if (result[0].StartsWith("0Y", StringComparison.Ordinal))
        {
            for (int index = 1; index < result.Count; index++)
            {
                if (result[index].StartsWith("Y", StringComparison.Ordinal))
                    result[index] = "0" + result[index];
            }
        }

        return result;
    }

    /// <summary>
    /// 兼容旧调用方的两段拆分接口。三段以上车号请使用 <see cref="SplitCompositeCodes"/>。
    /// </summary>
    public static bool TrySplitCompositeCode(string? code, out string? firstLeg, out string? secondLeg)
    {
        firstLeg = null;
        secondLeg = null;
        IReadOnlyList<string> parts = SplitCompositeCodes(code);
        if (parts.Count != 2) return false;
        firstLeg = parts[0];
        secondLeg = parts[1];
        return true;
    }

    /// <summary>按当前运行阶段选择复合车次的活动编号。</summary>
    public static string? SelectActiveCode(string? code, bool secondLegActive)
    {
        return TrySplitCompositeCode(code, out string? firstLeg, out string? secondLeg)
            ? (secondLegActive ? secondLeg : firstLeg)
            : NormalizeDisplayCode(code);
    }

    /// <summary>按从零开始的运行段序号选择活动车号。</summary>
    public static string? SelectActiveCode(string? code, int activeLegIndex)
    {
        IReadOnlyList<string> parts = SplitCompositeCodes(code);
        if (parts.Count == 0) return null;
        int index = Math.Clamp(activeLegIndex, 0, parts.Count - 1);
        return parts[index];
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
