namespace BveVehicleEditor
{
    /// <summary>
    /// 抵抗制御の発電ブレーキ（列が段ごとのブレーキの表）を式から作る計算（ResistanceCalculator の partial）。サンプル 5 つ（南海 6000・阪急 5300/6000・京成 3500・京急 6020）と比べて決めた式
    /// 発電ブレーキ：電動機が起こす電圧 κ × φ(x) × v ＝ 電流 ×（抵抗 ＋ r）。段 k の「x が 1 になる速度」を c_k とすると、x ÷ φ(x) ＝ v ÷ c_k、力 F ＝ Fl × φ(x) × x
    ///   x ＝ 電流 ÷ 限流値、φ は力行と同じ飽和の形（φ(1) ＝ 1）。段ごとに「速度 × 磁束 ÷ 電流」が一定（サンプルでばらつき 1～5%）
    /// 段の進み方：全ノッチで速い速度から下がり、電流が段の限流値まで下がったら次の段（抵抗が小さい段）へ（限流値 0 の段はすぐ進む）
    /// 段の切り替わる速度 v_k（段の限流値で切り替わる速度）は、今の表から写すか、いちばん速い段と最後の段の間を同じ比で並べる
    /// 最後の段より遅いところでは電流が保てなくなり、x ÷ φ(x) の下限 s ÷ (1 ＋ s) より遅いと 0（電気ブレーキが効かなくなる）
    /// 速度による電圧の下がり b：起こす電圧を φ(x) − b × v × x にする（x ＝ (v ÷ c_k) × (φ(x) − b × v × x)）。6 つのサンプルとも「速度 × 磁束 ÷ 電流」が速いほど大きく、
    ///   b を入れると 90～160 km/h のずれが、南海 6300 は力 3.3% → 1.2%・電流 2.6% → 0.8%、阪急 5300 は電流 5.9% → 0.8%、京成 3500 は力 7.7% → 5.9% になった（力は φ(x) のまま）
    ///   0～30 km/h は阪急 5300・阪急 6000・京成 3500 の電流が 0.4～1.8 ポイント悪くなった（もとから課題）
    /// 限流値より上の飽和の強さ（HighSaturation）：x ＞ 1 のときだけ電流の式の s を小さくして、電流を増えにくくする（力の φ(x) は s のまま）
    ///   阪急 5300 の 30～160 km/h の力が 3～6% → 1%、京成 3500 の 90～160 km/h の力が 5.9% → 3.9% になった。南海 6300 は効かない（速い段だけ電流が増えにくいため。課題）
    ///   b の効き方を速度の 2 乗・4 乗・6 乗にする案も試したが、南海の 90～160 km/h が 1.2% → 0.8% ほどで、欄を増やすほどではなかった
    /// </summary>
    internal static partial class ResistanceCalculator
    {
        /// <summary>
        /// ブレーキの計算に使う値の全部
        /// </summary>
        /// <param name="LimitCurrent">限流値 [A]（x ＝ 1 にする電流。段ごとの限流値が違うときは、いちばん多い値）</param>
        /// <param name="RatedForce">限流値のときの力 Fl [N]（M 車 1 両あたり）</param>
        /// <param name="Steps">段の数</param>
        /// <param name="FirstSpeed">いちばん速い段（段 1）が限流値まで下がる速度 [km/h]</param>
        /// <param name="LastSpeed">最後の段が限流値まで下がる速度 [km/h]（これより遅いと電気ブレーキが弱まる）</param>
        /// <param name="Saturation">飽和の強さ s</param>
        /// <param name="StepSpeeds">段ごとの切り替わる速度 [km/h]（速い順。null なら FirstSpeed～LastSpeed を同じ比で並べる）</param>
        /// <param name="StepLimits">段ごとの限流値 [A]（null ならどれも LimitCurrent。0 の段はすぐ進む）</param>
        /// <param name="SpeedDrop">速度による電圧の下がり b [1/(km/h)]（起こす電圧を φ(x) から φ(x) − b × v × x にする。0 なら下がらない）</param>
        /// <param name="HighSaturation">限流値より大きい電流のときの、電流の式の飽和の強さ（0 なら Saturation と同じ。小さいほど電流が増えにくい）</param>
        internal sealed record BrakeInput(
            double LimitCurrent, double RatedForce, int Steps, double FirstSpeed, double LastSpeed, double Saturation,
            double MaxSpeed, double SpeedStep, IReadOnlyList<double>? StepSpeeds = null, IReadOnlyList<double>? StepLimits = null,
            double SpeedDrop = 0, double HighSaturation = 0);

        /// <summary>
        /// 今のブレーキの表を読み取った結果
        /// </summary>
        /// <param name="LimitCurrent">いちばん多い限流値 [A]</param>
        /// <param name="RatedForce">段が限流値まで下がるときの力の中央値 [N]</param>
        /// <param name="SwitchSpeeds">段ごとの切り替わる速度 [km/h]（段の限流値まで下がる速度。読めない段は NaN）</param>
        /// <param name="Limits">段ごとの限流値 [A]</param>
        /// <param name="Speeds">全ノッチで走ったときを調べた速度（遅い順）</param>
        internal sealed record BrakeTableAnalysis(double LimitCurrent, double RatedForce, IReadOnlyList<double> SwitchSpeeds, IReadOnlyList<double> Limits,
            IReadOnlyList<double> Speeds, IReadOnlyList<double> EnvelopeForce, IReadOnlyList<double> EnvelopeCurrent);

        // ===== 段 =====

        /// <summary>
        /// 段ごとの切り替わる速度（速い順。StepSpeeds があればそれ、なければ FirstSpeed～LastSpeed を同じ比で並べる）
        /// </summary>
        internal static double[] BrakeSwitchSpeeds(BrakeInput input)
        {
            if (input.StepSpeeds != null && input.StepSpeeds.Count == input.Steps) return [.. input.StepSpeeds];
            if (input.Steps == 1) return [input.FirstSpeed];
            return [.. Enumerable.Range(0, input.Steps).Select(k => input.FirstSpeed * Math.Pow(input.LastSpeed / input.FirstSpeed, (double)k / (input.Steps - 1)))];
        }

        // 段の限流値 [A]
        private static double BrakeStepLimit(BrakeInput input, int k) => input.StepLimits != null && k < input.StepLimits.Count ? input.StepLimits[k] : input.LimitCurrent;

        /// <summary>
        /// 段ごとの「x が 1 になる速度」c_k。段の限流値で切り替わる速度 v_k から直す（x_k ＝ 段の限流値 ÷ 限流値、q ＝ x_k ÷ φ(x_k)）
        ///   x ÷ φ(x) ＝ a ÷ (1 ＋ a × b × v)（a ＝ v ÷ c_k）なので、c_k ＝ v_k × (1 − q × b × v_k) ÷ q
        /// 京成 3500 は速い段だけ限流値が 160・210 A（ほかは 275 A）で、これを直さないと 90 km/h より上が 56% ずれた
        /// 速度による電圧の下がりが大きすぎて電圧が残らない段は 0（ValidateBrake で知らせる）
        /// </summary>
        internal static double[] BrakeCoefficients(BrakeInput input)
        {
            double[] speeds = BrakeSwitchSpeeds(input);
            return [.. speeds.Select((v, k) =>
            {
                double limit = BrakeStepLimit(input, k);
                double xk = limit > 0 ? limit / input.LimitCurrent : 1;
                double s = xk > 1 ? HighSaturationOf(input) : input.Saturation;
                double q = (xk + s) / (1 + s);
                return Math.Max(0, v * (1 - q * input.SpeedDrop * v) / q);
            })];
        }

        // 限流値より大きい電流のときの飽和の強さ（0 なら s と同じ）
        private static double HighSaturationOf(BrakeInput input) => input.HighSaturation > 0 ? input.HighSaturation : input.Saturation;

        /// <summary>
        /// x ÷ φ(x) ＝ t を解く（t ＝ ratio ÷ (1 ＋ ratio × drop)、ratio ＝ v ÷ c_k、drop ＝ b × v。b ＝ 0 なら t ＝ ratio）
        /// x ÷ φ(x) ＝ (x ＋ s) ÷ (1 ＋ s) なので x ＝ t × (1 ＋ s) − s。t が s ÷ (1 ＋ s) 以下なら電流 0
        /// 電圧の下がりは電流に比例させる（x ＝ a × (φ(x) − b × v × x)）。電流によらず引くと、段の線が途中で 0 から 170 A に跳ねた（南海 6300）
        /// x ＞ 1（限流値より大きい電流）のときだけ、電流の式の s を highSaturation にする（t ＝ 1 で x ＝ 1 なのでつながる。力の φ(x) は s のまま）
        /// </summary>
        internal static double BrakeCurrentRatio(double ratio, double saturation, double drop = 0, double highSaturation = 0)
        {
            double target = ratio / (1 + ratio * drop);
            if (target <= saturation / (1 + saturation)) return 0;
            double s = target > 1 && highSaturation > 0 ? highSaturation : saturation;
            return target * (1 + s) - s;
        }

        /// <summary>
        /// 段 k（c_k）の、速度 v のときの電流（限流値の倍率）と力 [N]
        /// </summary>
        internal static (double X, double Force) BrakeStepAt(BrakeInput input, double coefficient, double speed)
        {
            double x = coefficient > 0 ? BrakeCurrentRatio(speed / coefficient, input.Saturation, input.SpeedDrop * speed, input.HighSaturation) : 0;
            return (x, input.RatedForce * Flux(input.Saturation, x) * x);
        }

        // ===== チェックと表 =====

        /// <summary>
        /// 値に問題があれば、日本語の理由の一覧を返す（なければ空）
        /// </summary>
        internal static List<string> ValidateBrake(BrakeInput input)
        {
            List<string> problems = [];
            if (input.LimitCurrent <= 0) problems.Add(Loc.T("RCB.001"));
            if (input.RatedForce <= 0) problems.Add(Loc.T("RCB.002"));
            if (input.Steps < 1 || input.Steps > MaxStepCount) problems.Add(Loc.F("RCB.003", MaxStepCount));
            if (input.StepSpeeds == null)
            {
                if (input.FirstSpeed <= 0 || input.LastSpeed <= 0) problems.Add(Loc.T("RCB.004"));
                else if (input.LastSpeed > input.FirstSpeed) problems.Add(Loc.T("RCB.005"));
            }
            else if (input.StepSpeeds.Count != input.Steps) problems.Add(Loc.F("RCB.006", input.StepSpeeds.Count, input.Steps));
            else if (input.StepSpeeds.Any(v => v <= 0) || input.StepSpeeds.Zip(input.StepSpeeds.Skip(1), (a, b) => b > a).Any(up => up)) problems.Add(Loc.T("RCB.007"));
            if (input.StepLimits != null && input.StepLimits.Any(l => l < 0)) problems.Add(Loc.T("RCB.008"));
            if (input.Saturation <= 0) problems.Add(Loc.T("RCB.009"));
            if (input.HighSaturation < 0) problems.Add(Loc.T("RCB.010"));
            if (input.SpeedDrop < 0) problems.Add(Loc.T("RCB.011"));
            else if (problems.Count == 0 && BrakeCoefficients(input).Any(c => c <= 0)) problems.Add(Loc.T("RCB.012"));
            if (input.MaxSpeed <= 0) problems.Add(Loc.T("RCB.013"));
            if (input.SpeedStep <= 0) problems.Add(Loc.T("RCB.014"));
            return problems;
        }

        /// <summary>
        /// ブレーキの表の行を作る（1 列目が速度、2 列目から段ごとの値）。引張力（Force）と電流（Current）だけ。値に問題があれば空
        /// </summary>
        internal static List<string[]> BuildBrakeRows(BrakeInput input, TractionCalculator.TableKind kind)
        {
            if (kind is not (TractionCalculator.TableKind.Force or TractionCalculator.TableKind.Current) || ValidateBrake(input).Count > 0) return [];
            double[] coefficients = BrakeCoefficients(input);
            List<string[]> rows = [];
            foreach (double speed in TractionCalculator.Speeds(input.MaxSpeed, input.SpeedStep))
            {
                string[] cells = new string[input.Steps + 1];
                cells[0] = TractionCalculator.Format(speed);
                for (int k = 0; k < input.Steps; k++)
                {
                    (double x, double force) = BrakeStepAt(input, coefficients[k], speed);
                    cells[k + 1] = TractionCalculator.Format(kind == TractionCalculator.TableKind.Force ? force : x * input.LimitCurrent);
                }
                rows.Add(cells);
            }
            return rows;
        }

        /// <summary>
        /// 全ノッチで速い速度から下がったときの力 [N] と電流 [A]（speeds の並びのまま返す。電流が段の限流値まで下がったら次の段へ。限流値 0 の段はすぐ進む）
        /// </summary>
        internal static (double[] Force, double[] Current) BrakeEnvelope(BrakeInput input, IReadOnlyList<double> speeds)
        {
            double[] force = new double[speeds.Count], current = new double[speeds.Count];
            if (ValidateBrake(input).Count > 0) return (force, current);
            double[] coefficients = BrakeCoefficients(input);
            int step = 0;
            foreach (int i in Enumerable.Range(0, speeds.Count).OrderByDescending(i => speeds[i]))
            {
                (double x, double f) = BrakeStepAt(input, coefficients[step], speeds[i]);
                while (step < input.Steps - 1)
                {
                    double limit = BrakeStepLimit(input, step);
                    if (limit > 0 && x * input.LimitCurrent > limit) break;
                    (x, f) = BrakeStepAt(input, coefficients[++step], speeds[i]);
                }
                current[i] = x * input.LimitCurrent;
                force[i] = f;
            }
            return (force, current);
        }

        // ===== 今の表を読み取る・合わせる =====

        /// <summary>
        /// 今のブレーキの表（引張力と電流。列は段ごと）を読み取る（読み取れなければ null）
        /// </summary>
        /// <param name="limits">段ごとの限流値 [A]（0 の段はすぐ進む）</param>
        internal static BrakeTableAnalysis? AnalyzeBrakeTable(IReadOnlyList<double> speeds, IReadOnlyList<double[]> force, IReadOnlyList<double[]> current, IReadOnlyList<double> limits)
        {
            int rowCount = Math.Min(speeds.Count, Math.Min(force.Count, current.Count));
            if (rowCount < 3) return null;
            int columns = Math.Min(force[0].Length, current[0].Length);
            if (columns < 1) return null;
            double[] stepLimits = [.. Enumerable.Range(0, columns).Select(k => k < limits.Count ? limits[k] : 0)];
            double baseLimit = MostCommon(stepLimits.Where(l => l > 0));
            if (baseLimit <= 0) return null;

            // 段ごとの切り替わる速度（速い側から、電流が段の限流値をはじめて下回るところ。行の間はつなぐ）と、そのときの力
            double[] switchSpeeds = new double[columns];
            List<double> forcesAtLimit = [];
            for (int k = 0; k < columns; k++)
            {
                switchSpeeds[k] = double.NaN;
                double limit = stepLimits[k];
                if (limit <= 0) continue;
                for (int n = rowCount - 1; n > 0; n--)
                {
                    double c1 = current[n][k], c0 = current[n - 1][k];
                    if (!(c1 >= limit && c0 < limit && c1 < 1e6)) continue;
                    double t = (limit - c0) / (c1 - c0);
                    switchSpeeds[k] = speeds[n - 1] + (speeds[n] - speeds[n - 1]) * t;
                    double f = force[n - 1][k] + (force[n][k] - force[n - 1][k]) * t;
                    if (limit == baseLimit && f > 0 && f < 1e7) forcesAtLimit.Add(f);
                    break;
                }
            }
            if (forcesAtLimit.Count == 0 || switchSpeeds.All(double.IsNaN)) return null;
            double ratedForce = forcesAtLimit.Order().ElementAt(forcesAtLimit.Count / 2);

            // 全ノッチで速い速度から下がったとき（0.5 km/h ごと。行の間はつなぐ）。遅い順に並べて返す
            List<(double Speed, double Force, double Current)> envelope = [];
            int step = 0, row = rowCount - 1;
            for (double v = Math.Floor(speeds[rowCount - 1] / EnvelopeSpeedStep) * EnvelopeSpeedStep; v >= EnvelopeSpeedStep - 1e-9; v -= EnvelopeSpeedStep)
            {
                while (row > 1 && speeds[row - 1] > v) row--;
                double t = speeds[row] == speeds[row - 1] ? 0 : Math.Clamp((v - speeds[row - 1]) / (speeds[row] - speeds[row - 1]), 0, 1);
                double CurrentOf(int k) => current[row - 1][k] + (current[row][k] - current[row - 1][k]) * t;
                while (step < columns - 1 && (stepLimits[step] <= 0 || !(CurrentOf(step) > stepLimits[step]))) step++;
                double f = force[row - 1][step] + (force[row][step] - force[row - 1][step]) * t;
                double c = CurrentOf(step);
                if (double.IsNaN(f) || double.IsNaN(c) || f > 1e7) continue;
                envelope.Add((Math.Round(v, 6), Math.Max(0, f), Math.Max(0, c)));
            }
            envelope.Reverse();
            return new(baseLimit, ratedForce, switchSpeeds, stepLimits,
                [.. envelope.Select(e => e.Speed)], [.. envelope.Select(e => e.Force)], [.. envelope.Select(e => e.Current)]);
        }

        // いちばん多い値（同じ数なら大きいほう）
        private static double MostCommon(IEnumerable<double> values)
        {
            return values.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).Select(g => g.Key).FirstOrDefault();
        }

        /// <summary>
        /// 今の表の段ごとの切り替わる速度を、段の数だけ取り出す（段の数が表の列と合わなければ null）。読めない段は 1 つ前（速い側）の段と同じ速度
        /// 最初に読める段より速い段（表の最高速度まで限流値に届かない段）は、読める 2 つの段の比で速い側へ伸ばす
        /// 南海 6300 は段 1・2 が読めず、段 3 と同じ速度にすると 155 km/h より上で電流が 9% 少なかった
        /// </summary>
        internal static List<double>? TableBrakeSwitchSpeeds(BrakeInput input, BrakeTableAnalysis table)
        {
            if (input.Steps != table.SwitchSpeeds.Count) return null;
            int first = table.SwitchSpeeds.ToList().FindIndex(v => !double.IsNaN(v));
            if (first < 0 || table.SwitchSpeeds[first] <= 0) return null;
            double previous = table.SwitchSpeeds[first];
            int second = table.SwitchSpeeds.ToList().FindIndex(first + 1, v => !double.IsNaN(v) && v > 0 && v < previous);
            double ratio = second < 0 ? 1 : Math.Pow(previous / table.SwitchSpeeds[second], 1.0 / (second - first));
            List<double> result = [];
            // 読めない段は表の最高速度まで限流値に届かないので、表の最高速度より遅くはしない（京急 6020 は伸ばすと 126.8 km/h になり、130 km/h で電流が 15% 少なかった）
            double topSpeed = table.Speeds.Count > 0 ? table.Speeds[^1] : previous;
            for (int k = 0; k < first; k++) result.Add(Math.Round(Math.Max(previous * Math.Pow(ratio, first - k), topSpeed * Math.Pow(ratio, first - 1 - k)), 2));
            for (int k = first; k < table.SwitchSpeeds.Count; k++)
            {
                double speed = table.SwitchSpeeds[k];
                if (!double.IsNaN(speed)) previous = Math.Min(previous, speed);
                result.Add(Math.Round(previous, 2));
            }
            return result;
        }

        /// <summary>
        /// 今の表から読んだ値で入力を作る（限流値・限流値のときの力 × forceScale・いちばん速い段と最後の段の速度・段ごとの限流値。useSwitchSpeeds なら切り替わる速度も写す）
        /// </summary>
        internal static BrakeInput WithBrakeTable(BrakeInput input, BrakeTableAnalysis table, double saturation, double forceScale = 1, bool useSwitchSpeeds = false, double speedDrop = 0, double highSaturation = 0)
        {
            List<double> read = [.. table.SwitchSpeeds.Where(v => !double.IsNaN(v))];
            List<double>? speeds = useSwitchSpeeds ? TableBrakeSwitchSpeeds(input, table) : null;
            bool sameSteps = input.Steps == table.Limits.Count;
            return input with
            {
                LimitCurrent = table.LimitCurrent, RatedForce = table.RatedForce * forceScale, Saturation = saturation, SpeedDrop = speedDrop, HighSaturation = highSaturation,
                FirstSpeed = read.Count > 0 ? Math.Round(read.Max(), 2) : input.FirstSpeed,
                LastSpeed = read.Count > 0 ? Math.Round(read.Min(), 2) : input.LastSpeed,
                StepSpeeds = speeds,
                StepLimits = sameSteps ? table.Limits : input.StepLimits,
            };
        }

        /// <summary>
        /// 今の表の全ノッチとのずれ（力と電流の相対誤差の平均の和。力は Fl の 5%、電流は限流値の 5% を下限にして割る）
        /// </summary>
        internal static double BrakeEnvelopeError(BrakeInput input, BrakeTableAnalysis table)
        {
            if (ValidateBrake(input).Count > 0 || table.Speeds.Count == 0) return double.MaxValue;
            (double[] force, double[] current) = BrakeEnvelope(input, table.Speeds);
            double total = 0;
            for (int i = 0; i < table.Speeds.Count; i++)
            {
                total += Math.Abs(force[i] - table.EnvelopeForce[i]) / Math.Max(table.EnvelopeForce[i], 0.05 * input.RatedForce);
                total += Math.Abs(current[i] - table.EnvelopeCurrent[i]) / Math.Max(table.EnvelopeCurrent[i], 0.05 * input.LimitCurrent);
            }
            return total / table.Speeds.Count;
        }

        /// <summary>
        /// 全ノッチの力の合計が今の表と同じになる、限流値のときの力の倍率（ブレーキで止まるまでの距離をそろえる。0.9～1.15）
        /// 阪急 5300 は式のほうがどの速度でも 5～10% 強めだった
        /// </summary>
        internal static double BrakeForceScale(BrakeInput input, BrakeTableAnalysis table)
        {
            (double[] force, _) = BrakeEnvelope(input, table.Speeds);
            double model = force.Sum(), sample = table.EnvelopeForce.Sum();
            return model <= 0 ? 1 : Math.Clamp(sample / model, MinForceScale, MaxForceScale);
        }

        /// <summary>
        /// 今の表に一番合う飽和の強さ s と、今の表の切り替わる速度を写すか、限流値のときの力の倍率を探す
        /// 試算では、写すほうが南海 6000・京急 6020・阪急 6000・京成 3500 で 30～160 km/h のずれ 1～4% だった（同じ比で並べると 2～6 倍）
        /// </summary>
        internal static (double Saturation, double ForceScale, bool UseSwitchSpeeds, double SpeedDrop, double HighSaturation, double Error) FitBrake(BrakeInput input, BrakeTableAnalysis table)
        {
            // 1 段目：s・b・写すか・力の倍率（限流値より上も s のまま）
            var first = FitBrakeShape(input, table);

            // 2 段目：s と写すかは決めたまま、限流値より上の飽和の強さと b・力の倍率を選び直す（0 は s と同じ）
            double[] highs = [0, 0.05, 0.1, 0.2, 0.3, 0.5];
            double[] drops = [0, 0.0005, 0.001, 0.0015, 0.002, 0.003, 0.004, 0.005, 0.006, 0.008, 0.01];
            (double High, double Drop, double Scale, double Error) best = (0, first.SpeedDrop, first.ForceScale, first.Error);
            foreach (double high in highs.Where(h => h == 0 || h < first.Saturation))
            {
                foreach (double drop in drops)
                {
                    double sumScale = BrakeForceScale(WithBrakeTable(input, table, first.Saturation, 1, first.UseSwitchSpeeds, drop, high), table);
                    foreach (double scale in new[] { 1.0, sumScale })
                    {
                        double error = BrakeEnvelopeError(WithBrakeTable(input, table, first.Saturation, scale, first.UseSwitchSpeeds, drop, high), table);
                        if (error < best.Error) best = (high, drop, scale, error);
                    }
                }
            }
            return (first.Saturation, best.Scale, first.UseSwitchSpeeds, best.Drop, best.High, best.Error);
        }

        // FitBrake の 1 段目
        private static (double Saturation, double ForceScale, bool UseSwitchSpeeds, double SpeedDrop, double Error) FitBrakeShape(BrakeInput input, BrakeTableAnalysis table)
        {
            double[] saturations = [0.05, 0.1, 0.2, 0.3, 0.5, 0.7, 1.0, 1.5, 2.0, 3.0, 5.0];
            double[] drops = [0, 0.0005, 0.001, 0.0015, 0.002, 0.003, 0.004, 0.005, 0.006, 0.008, 0.01];
            bool canUseSpeeds = TableBrakeSwitchSpeeds(input, table) != null;
            (double S, double Scale, bool Speeds, double Drop, double Error) best = (input.Saturation, 1, false, 0, double.MaxValue);
            foreach (bool useSpeeds in canUseSpeeds ? new[] { false, true } : [false])
            {
                foreach (double s in saturations)
                {
                    foreach (double drop in drops)
                    {
                        // 力の倍率は、1（今の表のまま）と、力の合計から決めた値の、ずれの小さいほう（合計だけで決めると京成 3500 の 90 km/h より上が 4.0% → 7.7% に悪くなった）
                        double sumScale = BrakeForceScale(WithBrakeTable(input, table, s, 1, useSpeeds, drop), table);
                        foreach (double scale in new[] { 1.0, sumScale })
                        {
                            double error = BrakeEnvelopeError(WithBrakeTable(input, table, s, scale, useSpeeds, drop), table);
                            if (error < best.Error) best = (s, scale, useSpeeds, drop, error);
                        }
                    }
                }
            }
            return best;
        }
    }
}
