using System;
using System.Text;

namespace RailRouteAssistantDesktop
{
    internal static class StationNameFormatter
    {
        /// <summary>混合语言站名只保留中文；纯英文地图没有中文可取时原样返回。</summary>
        public static string ChineseOrOriginal(string station)
        {
            if (string.IsNullOrWhiteSpace(station)) return station;

            int firstChinese = -1;
            int lastChinese = -1;
            for (int i = 0; i < station.Length; i++)
            {
                if (!IsChinese(station[i])) continue;
                if (firstChinese < 0) firstChinese = i;
                lastChinese = i;
            }
            if (firstChinese < 0) return station.Trim();

            // 保留紧贴中文站名的编号（如“7号线”），但丢弃 Station_01 这类英文标识。
            int start = firstChinese;
            while (start > 0 && char.IsDigit(station[start - 1])) start--;
            int end = lastChinese;
            while (end + 1 < station.Length && char.IsDigit(station[end + 1])) end++;

            var result = new StringBuilder(end - start + 1);
            for (int i = start; i <= end; i++)
            {
                char c = station[i];
                if (IsChinese(c) || char.IsDigit(c) || c == '（' || c == '）')
                    result.Append(c);
            }
            return result.ToString().Trim();
        }

        private static bool IsChinese(char c) =>
            (c >= '\u3400' && c <= '\u4dbf') ||
            (c >= '\u4e00' && c <= '\u9fff');
    }
}
