using System.Globalization;
using System.Text.RegularExpressions;

namespace BveVehicleEditor
{
    /// <summary>
    /// 進段パラメーターファイル（Params.csv など）の項目の一覧と説明、チェック
    /// 1 列目が項目名、2 列目からはノッチごとの値
    /// （https://bvets.net/jp/edit/formats/vehicle/performtable.html#params と、ユーザーが調べたことから）
    /// </summary>
    internal static class PerformanceParams
    {
        /// <summary>
        /// 進段パラメーターの項目
        /// </summary>
        /// <param name="Key">項目名（大文字小文字は区別しない）</param>
        /// <param name="Unit">単位（なければ空）</param>
        /// <param name="Description">説明</param>
        /// <param name="MinVersion">使えるようになったヘッダーのバージョン（どのバージョンでも使えるなら null）</param>
        /// <param name="SameAs">同じ働きの別の名前（どちらかが書かれていれば「書かれていない」と知らせない）</param>
        internal sealed record ParamKey(string Key, string Unit, string Description, decimal? MinVersion = null, string? SameAs = null);

        // 項目の一覧（公式サイトの表の順）
        internal static ParamKey[] Keys =>
        [
            new("BreakerDelay_On", Loc.T("PP.001"), Loc.T("PP.002")),
            new("BreakerDelay_Off", Loc.T("PP.003"), Loc.T("PP.004")),
            new("ResetTime", Loc.T("PP.005"), Loc.T("PP.006"), MinVersion: 2.00m),
            new("CurrentReducingTime", Loc.T("PP.007"), Loc.T("PP.008")),
            new("JerkRegulation_Up", "A/s", Loc.T("PP.009")),
            new("JerkRegulation_Down", "A/s", Loc.T("PP.010")),
            new("RequiredNotch_Up", "", Loc.T("PP.011")),
            new("RequiredNotch_Down", "", Loc.T("PP.012")),
            new("StepDelay_Up", Loc.T("PP.013"), Loc.T("PP.014")),
            new("StepDelay_Down", Loc.T("PP.015"), Loc.T("PP.016")),
            new("CurrentLimitingValue_Empty", "A", Loc.T("PP.017"), SameAs: "CutOffCurrent_Empty"),
            new("CurrentLimitingValue_Full", "A", Loc.T("PP.018"), SameAs: "CutOffCurrent_Full"),
            // 公式サイトにはないが、サンプル（315 のブレーキ）で使われている。どのバージョンかは分からない（Plan.md のメモ）
            new("CutOffCurrent_Empty", "A", Loc.T("PP.019"), SameAs: "CurrentLimitingValue_Empty"),
            new("CutOffCurrent_Full", "A", Loc.T("PP.020"), SameAs: "CurrentLimitingValue_Full"),
        ];

        /// <summary>
        /// 項目名から項目を探す（前後の空白と大文字小文字は気にしない）。一覧になければ null
        /// </summary>
        internal static ParamKey? Find(string name)
        {
            string trimmed = name.Trim();
            return Keys.FirstOrDefault(key => string.Equals(key.Key, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// ヘッダー（例：BveTs Vehicle Performance Table 2.01:shift_jis,,,）からバージョン番号を読む。読めなければ null
        /// </summary>
        internal static decimal? ReadVersion(string headerLine)
        {
            Match match = Regex.Match(headerLine, @"(\d+\.\d+)");
            if (!match.Success) return null;
            return decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal version) ? version : null;
        }

        /// <summary>
        /// 表の「単位・説明」の列に出す文字。項目名が空なら空、一覧になければ警告の文字
        /// </summary>
        internal static string Describe(string name, decimal? version)
        {
            if (name.Trim().Length == 0) return string.Empty;
            ParamKey? key = Find(name);
            if (key == null) return Loc.T("PP.021");

            string unit = key.Unit.Length == 0 ? string.Empty : $"[{key.Unit}] ";
            string tooNew = IsTooNew(key, version) ? Loc.F("PP.022", key.MinVersion) : string.Empty;
            return $"{unit}{key.Description}{tooNew}";
        }

        /// <summary>
        /// ヘッダーのバージョンでは、まだ使えない項目か（バージョンが読めなければ false）
        /// </summary>
        internal static bool IsTooNew(ParamKey key, decimal? version)
        {
            return key.MinVersion != null && version != null && version < key.MinVersion;
        }

        /// <summary>
        /// 書かれていない項目の名前（ヘッダーのバージョンで使えない項目と、同じ働きの別の名前が書かれている項目は入れない）
        /// </summary>
        internal static List<string> FindMissing(IEnumerable<string> writtenNames, decimal? version)
        {
            HashSet<string> written = new(writtenNames.Select(name => name.Trim()), StringComparer.OrdinalIgnoreCase);
            return [.. Keys
                // CutOffCurrent は公式サイトにない別の名前なので、「書かれていない」とは知らせない
                .Where(key => !key.Key.StartsWith("CutOffCurrent", StringComparison.Ordinal))
                .Where(key => !IsTooNew(key, version))
                .Where(key => !written.Contains(key.Key) && (key.SameAs == null || !written.Contains(key.SameAs)))
                .Select(key => key.Key)];
        }
    }
}
