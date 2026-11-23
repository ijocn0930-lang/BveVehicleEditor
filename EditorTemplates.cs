namespace BveVehicleEditor
{
    /// <summary>
    /// テキストエリアの右クリックメニュー「定型文を入れる」の中身（タブごとに書けるキー・セクション名）
    /// すでに書かれているもの（どれか 1 つしか書けない組の片方があるときも）は、理由を付けて入れられないようにする（Plan.md「すでにある場合は挿入しない」）
    /// 入れるのは MainWindow（カーソルのある行の下に 1 行）
    /// </summary>
    internal static class EditorTemplates
    {
        /// <summary>どのタブのテキストか</summary>
        internal enum TabKind { Vehicle, Parameters, Panel, Notch, MotorNoise, Sound }

        /// <summary>
        /// 1 つの定型文（メニューの文字・入れる文字・入れられない理由（入れられるなら null））
        /// </summary>
        internal sealed record Template(string Label, string Text, string? DisabledReason);

        // パラメーターのセクションと説明（ParameterForm で扱っているもの。説明は言語で変わるので、使うたびに作る）
        private static (string Name, string Label)[] ParameterSections =>
        [
            ("Cab", Loc.T("Tpl.001")),
            ("OneLeverCab", Loc.T("Tpl.002")),
            ("ConstantSpeedControl", Loc.T("Tpl.003")),
            ("MainCircuit", Loc.T("Tpl.004")),
            ("PowerReAdhesion", Loc.T("Tpl.005")),
            ("Ecb", Loc.T("Tpl.006")),
            ("Smee", Loc.T("Tpl.007")),
            ("Cl", Loc.T("Tpl.008")),
            ("Sap", Loc.T("Tpl.009")),
            ("Er", Loc.T("Tpl.010")),
            ("Bp", Loc.T("Tpl.011")),
            ("AirSupplement", Loc.T("Tpl.012")),
            ("LockoutValve", Loc.T("Tpl.013")),
            ("BC", Loc.T("Tpl.014")),
            ("BcServo", Loc.T("Tpl.015")),
            ("Brake", Loc.T("Tpl.016")),
            ("BrakeReAdhesion", Loc.T("Tpl.017")),
            ("Compressor", Loc.T("Tpl.018")),
            ("Dynamics", Loc.T("Tpl.019")),
            ("Passengers", Loc.T("Tpl.020")),
            ("Door", Loc.T("Tpl.021")),
            ("ViewPoint", Loc.T("Tpl.022")),
        ];

        // パラメーターで、どれか 1 つしか書けないセクションの組
        private static readonly string[][] ExclusiveGroups =
        [
            ["Cab", "OneLeverCab"],
            ["Ecb", "Smee", "Cl"],
            ["AirSupplement", "LockoutValve"],
            ["BC", "BcServo"],
        ];

        // ノッチ・モーター音の [Power]・[Brake] の中のキー
        private static readonly string[] NotchKeys = ["Params", "Force", "MaxForce", "Current", "MaxCurrent", "NoLoadCurrent"];
        private static readonly string[] MotorNoiseKeys = ["Volume", "Frequency"];
        private static readonly string[] PowerBrakeSections = ["Power", "Brake"];

        /// <summary>
        /// そのタブで入れられる定型文の一覧
        /// </summary>
        /// <param name="kind">タブ</param>
        /// <param name="text">今のテキスト</param>
        /// <param name="caretLineNumber">カーソルのある行の番号（1 から。ノッチ・モーター音のキーは、カーソルがあるセクションに入れる）</param>
        public static List<Template> Build(TabKind kind, string text, int caretLineNumber)
        {
            List<BveTextLine> lines = BveTextParser.Parse(text);
            HashSet<string> sections = new(lines.Where(line => line.Kind == BveLineKind.Section && line.Section != null).Select(line => line.Section!.Trim()), StringComparer.OrdinalIgnoreCase);
            return kind switch
            {
                TabKind.Vehicle => VehicleTemplates(text, lines),
                TabKind.Parameters => ParameterTemplates(sections),
                TabKind.Panel => [.. PanelForm.AddableSections.Select(item => SectionTemplate(item.Name, item.Description,
                    item.Name.Equals("This", StringComparison.OrdinalIgnoreCase) && sections.Contains(item.Name) ? Loc.T("Tpl.023") : null))],
                TabKind.Notch => PowerBrakeTemplates(lines, sections, caretLineNumber, NotchKeys),
                TabKind.MotorNoise => PowerBrakeTemplates(lines, sections, caretLineNumber, MotorNoiseKeys),
                TabKind.Sound => [.. SoundForm.SectionNames.Select(item => SectionTemplate(item.Name, item.Label, SoundForm.IsSectionWritten(lines, item.Name) ? Loc.T("Tpl.024") : null))],
                _ => [],
            };
        }

        // 車両ファイル：ファイルのパスのキー（2.00 より前のヘッダーなら Ats32/Ats64 の代わりに Ats）
        private static List<Template> VehicleTemplates(string text, List<BveTextLine> lines)
        {
            (decimal? version, _) = VehicleFileParser.ReadVersion(text);
            string[] atsKeys = version < 2.00m ? ["Ats"] : ["Ats32", "Ats64"];
            string[] keys = ["PerformanceCurve", "Parameters", "Panel", "Sound", "MotorNoise", .. atsKeys];
            HashSet<string> written = new(lines.Where(line => line.Kind == BveLineKind.KeyValue && line.Key != null).Select(line => line.Key!), StringComparer.OrdinalIgnoreCase);
            return [.. keys.Select(key => KeyTemplate(key, written.Contains(key) ? Loc.T("Tpl.025") : null))];
        }

        // パラメーター：セクション名（どれか 1 つしか書けない組の片方があれば、もう片方も入れられない）
        private static List<Template> ParameterTemplates(HashSet<string> sections)
        {
            List<Template> templates = [];
            foreach ((string name, string label) in ParameterSections)
            {
                string? reason = sections.Contains(name) ? Loc.T("Tpl.026") : null;
                string[]? group = ExclusiveGroups.FirstOrDefault(item => item.Contains(name, StringComparer.OrdinalIgnoreCase));
                string? other = group?.FirstOrDefault(item => !item.Equals(name, StringComparison.OrdinalIgnoreCase) && sections.Contains(item));
                reason ??= other != null ? Loc.F("Tpl.027", other) : null;
                templates.Add(SectionTemplate(name, label, reason));
            }
            return templates;
        }

        // ノッチ・モーター音：[Power]・[Brake] と、カーソルのあるセクションの中のキー（[Power]・[Brake] の外ではキーを入れられない）
        private static List<Template> PowerBrakeTemplates(List<BveTextLine> lines, HashSet<string> sections, int caretLineNumber, string[] keys)
        {
            List<Template> templates = [.. PowerBrakeSections.Select(name => SectionTemplate(name, name == "Power" ? Loc.T("Tpl.028") : Loc.T("Tpl.029"), sections.Contains(name) ? Loc.T("Tpl.030") : null))];
            string? caretSection = lines.LastOrDefault(line => line.LineNumber <= caretLineNumber && line.Section != null)?.Section?.Trim();
            bool inPowerBrake = caretSection != null && PowerBrakeSections.Contains(caretSection, StringComparer.OrdinalIgnoreCase);
            HashSet<string> written = new(lines.Where(line => line.Kind == BveLineKind.KeyValue && line.Key != null && string.Equals(line.Section?.Trim(), caretSection, StringComparison.OrdinalIgnoreCase)).Select(line => line.Key!), StringComparer.OrdinalIgnoreCase);
            foreach (string key in keys)
            {
                string? reason = !inPowerBrake ? Loc.T("Tpl.031") : written.Contains(key) ? Loc.F("Tpl.032", caretSection) : null;
                templates.Add(KeyTemplate(key, reason));
            }
            return templates;
        }

        // 「[名前]」の行
        private static Template SectionTemplate(string name, string label, string? reason) => new($"[{name}]　{label}", $"[{name}]", reason);

        // 「キー = 」の行（続けて値を打てるように、= のあとに空白を 1 つ）
        private static Template KeyTemplate(string key, string? reason) => new($"{key} =", $"{key} = ", reason);
    }
}
