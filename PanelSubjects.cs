using System.Text.RegularExpressions;

namespace BveVehicleEditor
{
    /// <summary>
    /// パネルの計器に表示する状態量（Subject = の値）の一覧と説明
    /// 出典：https://bvets.net/jp/edit/formats/vehicle/panel.html の subjectKey の表（表の順番どおり）
    /// </summary>
    internal static class PanelSubjects
    {
        /// <summary>
        /// 状態量1つ分
        /// </summary>
        /// <param name="Key">Subject に書く名前（番号付きのものは 0 番の例）</param>
        /// <param name="Type">型（bool / int / float）</param>
        /// <param name="Description">説明</param>
        public sealed record Subject(string Key, string Type, string Description);

        /// <summary>
        /// 一覧（「一覧から選ぶ」に出す順）。doorl・doorr・ats は番号付きで、ここでは 0 番を例として入れる
        /// </summary>
        public static Subject[] All =>
        [
            new("true", "bool", Loc.T("Ps.001")),
            new("kmph", "float", Loc.T("Ps.002")),
            new("kmphd0", "int", Loc.T("Ps.003")),
            new("kmphd1", "int", Loc.T("Ps.004")),
            new("kmphd2", "int", Loc.T("Ps.005")),
            new("bc", "float", Loc.T("Ps.006")),
            new("bcd1", "int", Loc.T("Ps.007")),
            new("bcd2", "int", Loc.T("Ps.008")),
            new("mr", "float", Loc.T("Ps.009")),
            new("mrd1", "int", Loc.T("Ps.010")),
            new("mrd2", "int", Loc.T("Ps.011")),
            new("sap", "float", Loc.T("Ps.012")),
            new("bp", "float", Loc.T("Ps.013")),
            new("er", "float", Loc.T("Ps.014")),
            new("am", "float", Loc.T("Ps.015")),
            new("amabs", "float", Loc.T("Ps.016")),
            new("door", "bool", Loc.T("Ps.017")),
            new("doorl0", "bool", Loc.T("Ps.018")),
            new("doorr0", "bool", Loc.T("Ps.019")),
            new("csc", "bool", Loc.T("Ps.020")),
            new("ats0", "int", Loc.T("Ps.021")),
            new("power", "int", Loc.T("Ps.022")),
            new("brake", "int", Loc.T("Ps.023")),
            new("hour", "float", Loc.T("Ps.024")),
            new("min", "float", Loc.T("Ps.025")),
            new("sec", "int", Loc.T("Ps.026")),
        ];

        // ブレーキ方式が電磁直通 [Smee] か自動空気 [Cl] でないと使えない状態量（Plan.md のユーザーのメモ）
        private static readonly string[] BrakePipeSubjects = ["sap", "bp", "er"];

        /// <summary>
        /// 状態量の説明（「速度 [km/h]（float）」など）。一覧にない名前なら null
        /// 番号付きのもの（doorl3・ats12 など）は、その番号の説明にする。大文字小文字は区別しない（ページに記載がないため）
        /// </summary>
        public static string? Describe(string key)
        {
            string name = key.Trim();
            Subject? subject = All.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
            if (subject != null) return $"{subject.Description}（{subject.Type}）";

            Match door = Regex.Match(name, @"^door([lr])(\d+)$", RegexOptions.IgnoreCase);
            if (door.Success)
            {
                string side = door.Groups[1].Value.Equals("l", StringComparison.OrdinalIgnoreCase) ? Loc.T("Ps.027") : Loc.T("Ps.028");
                return Loc.F("Ps.029", int.Parse(door.Groups[2].Value) + 1, side);
            }

            Match ats = Regex.Match(name, @"^ats(\d+)$", RegexOptions.IgnoreCase);
            if (ats.Success && int.TryParse(ats.Groups[1].Value, out int number) && number <= 255) return Loc.F("Ps.030", number);
            return null;
        }

        /// <summary>
        /// 状態量の名前がおかしければ、その理由の文。おかしくなければ null
        /// </summary>
        /// <param name="key">Subject に書かれている名前</param>
        /// <param name="brakeSystem">パラメーターのブレーキ方式（Ecb / Smee / Cl）。パラメーターを読み込めていなければ null（調べない）</param>
        /// <param name="brakeSystemName">画面に出すブレーキ方式の名前（「電気指令式 [Ecb]」など）</param>
        public static string? FindProblem(string key, string? brakeSystem, string? brakeSystemName)
        {
            string name = key.Trim();
            Match ats = Regex.Match(name, @"^ats(\d+)$", RegexOptions.IgnoreCase);
            if (ats.Success && (!int.TryParse(ats.Groups[1].Value, out int number) || number > 255)) return Loc.F("Ps.031", name);
            if (Describe(name) == null) return Loc.F("Ps.032", name);

            bool isBrakePipe = BrakePipeSubjects.Contains(name, StringComparer.OrdinalIgnoreCase);
            bool isPipeBrake = string.Equals(brakeSystem, "Smee", StringComparison.OrdinalIgnoreCase) || string.Equals(brakeSystem, "Cl", StringComparison.OrdinalIgnoreCase);
            if (!isBrakePipe || brakeSystem == null || isPipeBrake) return null;
            return Loc.F("Ps.033", name, brakeSystemName);
        }
    }
}
