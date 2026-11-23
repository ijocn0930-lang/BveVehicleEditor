namespace BveVehicleEditor
{
    /// <summary>
    /// 抵抗制御（直並列・弱め界磁）の力行の表（列が進段ごと）を式から作る計算。サンプル 6 つ（京成 3500・能勢電 1500・京急 2610/8000・南海 6000・211 系）と比べて決めた式
    /// 電動機（直巻）：速度 v ＝（電圧 − x ×（抵抗 ＋ r））÷（κ × φ(α × x)）、力 F ＝ A × Fl × φ(α × x) × x − L × Fl ×（v ÷ vs）²
    ///   x ＝ 電流 ÷ 限流値、φ(x) ＝ x(1 ＋ s) ÷ (x ＋ s)（鉄心の飽和。φ(1) ＝ 1）、α は界磁の電流の割合（全界磁で 1）
    ///   電圧は並列のとき 1・直列のとき 0.5、抵抗と r は「限流値のときの電圧降下が電圧の何割か」で表す
    ///   κ・A は「全並列・全界磁・抵抗 0 で、限流値のとき速度 vs・力 Fl」になるように決める
    /// 段：起動の段（速度 0 で電流が 0.55 倍から 1.1 倍まで上がる）→ 直列 → 並列 → 弱め界磁
    ///   直列・並列の区間の中は同じ跳ね上がりの倍率で進み、区間の最後の段でちょうど抵抗 0 になるように倍率を決める
    ///   電流が限流値まで下がったら次の段へ進む（弱め界磁の段は限流値を別に持てる）
    /// </summary>
    internal static partial class ResistanceCalculator
    {
        /// <summary>
        /// 電動機の形（車両ごとに違う。「今の表に合わせる」Fit で選べる）
        /// </summary>
        /// <param name="Saturation">飽和の強さ s（小さいほど飽和が強い。既定 1）</param>
        /// <param name="InternalResistance">電動機の中の抵抗 r（限流値のときの電圧降下が電圧の何割か。既定 0.2）</param>
        /// <param name="Loss">損失 L（vs のときの損失が Fl の何割か。速度の 2 乗で増える。既定 0）</param>
        /// <param name="WeakSaturation">弱め界磁の段だけに使う飽和の強さ（0 なら Saturation と同じ）</param>
        /// <param name="WeakResistance">弱め界磁の段だけに使う中の抵抗（0 なら InternalResistance と同じ）</param>
        /// <remarks>京成 3500 のサンプルは、弱め界磁に入るときの跳ね上がりが大きく（WF2 で式 362 A・表 437 A）、1 組の s・r では低い速度と弱め界磁の両方は合わなかった。
        /// 弱め界磁だけ s・r を変えられるようにした（2026-11-22。ユーザーが見つけた）</remarks>
        /// <param name="WeakForceExponent">弱め界磁の段の「力」だけに使う磁束の形：限流値での磁束 ×（電流 ÷ 限流値）^q の q（0 なら飽和の形のまま）。速度と電流の関係は変えず、力の線の形だけを変える
        /// 南海 6000 のサンプルの最後の弱め界磁の段は、磁束（力 ÷ 電流）が電流にほぼ比例していて、飽和の形では特性領域の力が合わなかった（2026-11-22。ユーザーが見つけた）</param>
        internal sealed record MotorShape(double Saturation, double InternalResistance, double Loss, double WeakSaturation = 0, double WeakResistance = 0, double WeakForceExponent = 0)
        {
            internal static MotorShape Default { get; } = new(1.0, 0.2, 0.0);

            /// <summary>弱め界磁の段で使う飽和の強さ</summary>
            internal double WeakS => WeakSaturation > 0 ? WeakSaturation : Saturation;

            /// <summary>弱め界磁の段で使う中の抵抗</summary>
            internal double WeakR => WeakResistance > 0 ? WeakResistance : InternalResistance;
        }

        /// <summary>
        /// 計算に使う値の全部
        /// </summary>
        /// <param name="LimitCurrent">限流値 [A]（進段パラメーターの CurrentLimitingValue）</param>
        /// <param name="RatedSpeed">全並列・全界磁の段で、電流が限流値まで下がる速度 vs [km/h]</param>
        /// <param name="RatedForce">そのときの力 Fl [N]（M 車 1 両あたり）</param>
        /// <param name="StartSteps">起動の段の数（直列の段の数に含む）</param>
        /// <param name="SeriesSteps">直列の段の数（直並列のつなぎ替えがなければ、全部の抵抗の段の数）</param>
        /// <param name="ParallelSteps">並列の段の数（0 なら直並列のつなぎ替えなし）</param>
        /// <param name="FieldRatios">弱め界磁の段ごとの界磁の割合（界磁の電流の割合。0 より大きく 1 以下）</param>
        /// <param name="WeakLimitCurrent">弱め界磁の段の限流値 [A]</param>
        /// <param name="StepSpeeds">段ごとの切り替わる速度 [km/h] の並び（起動の段の次から、直列・並列の段の順。その段の電流が限流値まで下がる速度）
        /// あれば、段がちょうどその速度で限流値まで下がる抵抗にする（今の表の段の並びに合わせるため。区間の最後の段は抵抗 0 のまま）。null なら区間の中は同じ跳ね上がりの倍率</param>
        internal sealed record ResistanceInput(
            double LimitCurrent, double RatedSpeed, double RatedForce,
            int StartSteps, int SeriesSteps, int ParallelSteps,
            IReadOnlyList<double> FieldRatios, double WeakLimitCurrent,
            MotorShape Shape, double MaxSpeed, double SpeedStep,
            IReadOnlyList<double>? StepSpeeds = null, double SeriesVoltage = 0.5);
        // SeriesVoltage：直列のときの電圧（並列を 1 として。ふつう 0.5）。南海 6000 のサンプルは直列が全界磁の速度の 0.66 倍まで続き、0.5 では
        // 直並列のつなぎ替えの位置と跳ね上がりが合わなかった（2026-11-22。ユーザーが見つけた）。「今の表に合わせる」で、今の表の直列が終わる速度から決める

        /// <summary>
        /// 段ごとの切り替わる速度の数（起動の段の次から、直列・並列の段の数）
        /// </summary>
        internal static int SwitchSpeedCount(ResistanceInput input) => Math.Max(0, input.SeriesSteps - input.StartSteps) + Math.Max(0, input.ParallelSteps);

        /// <summary>
        /// 1 つの段（電圧・抵抗は割合、Field は界磁の割合、LimitRatio は限流値の倍率）
        /// </summary>
        internal sealed record StepInfo(string Kind, double Voltage, double Resistance, double Field, double LimitRatio);

        /// <summary>
        /// 作った段と、区間ごとの跳ね上がりの倍率（区間がなければ null）
        /// </summary>
        internal sealed record StepPlan(IReadOnlyList<StepInfo> Steps, double? SeriesJump, double? ParallelJump);

        /// <summary>
        /// 今の表を読み取った結果
        /// </summary>
        /// <param name="FluxRatios">弱め界磁の段ごとの磁束の比（限流値での力の比）。界磁の割合には FieldFromFluxRatio で直す</param>
        /// <param name="EnvelopeForce">全ノッチで走ったときの力 [N]（速度ごと）</param>
        /// <param name="EnvelopeCurrent">全ノッチで走ったときの電流 [A]（速度ごと）</param>
        /// <param name="SwitchSpeeds">段（列）ごとの切り替わる速度 [km/h]（その段の電流が限流値まで下がる速度。限流値 0 の段・読めない段は NaN）</param>
        internal sealed record TableAnalysis(double RatedSpeed, double RatedForce, IReadOnlyList<double> FluxRatios, IReadOnlyList<double> Speeds, IReadOnlyList<double> EnvelopeForce, IReadOnlyList<double> EnvelopeCurrent, IReadOnlyList<double> SwitchSpeeds);

        /// <summary>
        /// 進段パラメーターから読んだ段の並び
        /// </summary>
        /// <param name="SeriesSteps">直列（つなぎ替えがなければ全部の抵抗）の段の数</param>
        /// <param name="ParallelSteps">並列の段の数（0 ならつなぎ替えなし）</param>
        /// <param name="WeakSteps">弱め界磁の段の数</param>
        /// <param name="Limits">段ごとの限流値（空車）[A]。書かれていない段は 0</param>
        /// <param name="FromHeader">見出し（S1・P1・WF1 など）から読めたか（false なら列の数だけ分かる）</param>
        internal sealed record StepLayout(int SeriesSteps, int ParallelSteps, int WeakSteps, IReadOnlyList<double> Limits, bool FromHeader);

        // 今の表の「全ノッチで走ったとき」を調べる速度の間隔 [km/h]（合わせる計算が遅くならないように 0.5）
        private const double EnvelopeSpeedStep = 0.5;

        // 段の数・弱め界磁の段の数の上限（表の列が多くなりすぎないように）
        private const int MaxStepCount = 300;

        // ===== 電動機の式 =====

        /// <summary>
        /// 磁束 φ(x)（φ(1) ＝ 1）
        /// </summary>
        internal static double Flux(double saturation, double x) => x * (1 + saturation) / (x + saturation);

        /// <summary>
        /// 界磁の割合 field の段の、電流 x（限流値の倍率）での磁束 φ(α × x)（弱め界磁の段は弱め界磁用の飽和の強さ）
        /// </summary>
        internal static double StepFlux(MotorShape shape, double field, double x) => Flux(field < 1 ? shape.WeakS : shape.Saturation, field * x);

        /// <summary>
        /// 力に使う磁束。全界磁の段は StepFlux と同じ。弱め界磁の段は、力の指数 q があれば「限流値での磁束 × x^q」（限流値 x ＝ 1 では StepFlux と同じ値）
        /// </summary>
        internal static double ForceFlux(MotorShape shape, double field, double x)
        {
            if (field >= 1 || shape.WeakForceExponent <= 0) return StepFlux(shape, field, x);
            return StepFlux(shape, field, 1) * Math.Pow(x, shape.WeakForceExponent);
        }

        // 段の中の抵抗（弱め界磁の段は弱め界磁用）
        private static double StepInternalResistance(MotorShape shape, StepInfo step) => step.Field < 1 ? shape.WeakR : shape.InternalResistance;

        /// <summary>
        /// 限流値まで下がる速度の比から決めた磁束の比を、界磁の電流の割合に直す
        /// 全界磁の段の速度 ＝（1 − r）÷（κ × 1）、弱め界磁の段の速度 ＝（1 − 弱め界磁の r）÷（κ × φ(α)）なので、φ(α) ＝ 比 ×（1 − 弱め界磁の r）÷（1 − r）を解く
        /// </summary>
        internal static double FieldFromFluxRatio(MotorShape shape, double fluxRatio)
        {
            double target = fluxRatio * (1 - shape.WeakR) / (1 - shape.InternalResistance);
            if (target >= 1) return 1;
            double s = shape.WeakS;
            double field = target * s / (1 + s - target);
            return Math.Clamp(field, 0.05, 1.0);
        }

        // 係数 κ（全並列・全界磁・抵抗 0 で、限流値のとき速度 vs）
        private static double Kappa(ResistanceInput input) => (1 - input.Shape.InternalResistance) / input.RatedSpeed;

        // 電流が x（限流値の倍率）のときの速度
        private static double SpeedAt(ResistanceInput input, StepInfo step, double x)
        {
            return (step.Voltage - x * (step.Resistance + StepInternalResistance(input.Shape, step))) / (Kappa(input) * StepFlux(input.Shape, step.Field, x));
        }

        /// <summary>
        /// 速度 v のときの電流（限流値の倍率）。速度が高すぎて流れなければ 0
        /// </summary>
        internal static double CurrentRatioAt(ResistanceInput input, StepInfo step, double speed)
        {
            double lo = 1e-4, hi = step.Voltage / (step.Resistance + StepInternalResistance(input.Shape, step));
            if (SpeedAt(input, step, lo) <= speed) return 0;
            for (int n = 0; n < 60; n++)
            {
                double mid = (lo + hi) / 2;
                if (SpeedAt(input, step, mid) > speed) lo = mid; else hi = mid;
            }
            return (lo + hi) / 2;
        }

        /// <summary>
        /// 速度 v のときの電流 [A]
        /// </summary>
        internal static double CurrentAt(ResistanceInput input, StepInfo step, double speed) => CurrentRatioAt(input, step, speed) * input.LimitCurrent;

        /// <summary>
        /// 電流 x（限流値の倍率）・速度 v のときの力 [N]（損失で負になれば 0）
        /// </summary>
        internal static double ForceAt(ResistanceInput input, StepInfo step, double x, double speed)
        {
            MotorShape m = input.Shape;
            double ratio = speed / input.RatedSpeed;
            double force = (1 + m.Loss) * input.RatedForce * ForceFlux(m, step.Field, x) * x - m.Loss * input.RatedForce * ratio * ratio;
            return Math.Max(0, force);
        }

        // ===== 段を作る =====

        // 速度 v で電流が target（限流値の倍率）になる抵抗（負のまま返す）
        private static double RawResistance(ResistanceInput input, double speed, double target, double voltage)
        {
            return (voltage - Kappa(input) * Flux(input.Shape.Saturation, target) * speed) / target - input.Shape.InternalResistance;
        }

        // 1 つの区間の抵抗：入る速度 entry から count 段で、最後の段の抵抗がちょうど 0 になる倍率を探す
        // speeds（段ごとの切り替わる速度）があれば、段 k がちょうどその速度で限流値まで下がる抵抗にする（最後の段は抵抗 0）。jump は跳ね上がりの平均
        private static List<double> StageResistances(ResistanceInput input, double voltage, double entry, int count, IReadOnlyList<double>? speeds, out double jump)
        {
            jump = 1;
            List<double> result = [];
            if (count <= 0) return result;
            if (speeds != null && speeds.Count == count)
            {
                for (int k = 0; k < count - 1; k++) result.Add(Math.Max(0, RawResistance(input, speeds[k], 1, voltage)));
                result.Add(0);
                double previous = entry, total = 0;
                foreach (double resistance in result)
                {
                    StepInfo step = new("", voltage, resistance, 1, 1);
                    total += CurrentRatioAt(input, step, previous);
                    previous = Math.Max(previous, SpeedAt(input, step, 1));
                }
                jump = total / count;
                return result;
            }
            double lo = 1.0001, hi = 3;
            for (int n = 0; n < 60; n++)
            {
                double j = (lo + hi) / 2, speed = entry, last = 0;
                List<double> list = [];
                for (int k = 0; k < count; k++)
                {
                    last = RawResistance(input, speed, j, voltage);
                    list.Add(Math.Max(0, last));
                    if (last <= 0) break;
                    speed = SpeedAt(input, new("", voltage, last, 1, 1), 1);
                }
                // 最後の段まで抵抗が残る → 倍率を大きく、途中で 0 になる → 小さく
                if (list.Count == count && last > 0) lo = j; else hi = j;
                result = list;
                jump = j;
            }
            while (result.Count < count) result.Add(0);
            result[count - 1] = 0;
            return result;
        }

        /// <summary>
        /// 段を作る（値に問題があれば段は空）
        /// </summary>
        internal static StepPlan BuildSteps(ResistanceInput input)
        {
            if (Validate(input).Count > 0) return new([], null, null);

            List<StepInfo> steps = [];
            double r = input.Shape.InternalResistance;
            double voltage = input.ParallelSteps > 0 ? input.SeriesVoltage : 1.0;
            // 起動の段（速度 0 で電流が 0.55 倍から 1.1 倍まで上がる）
            for (int k = 1; k <= input.StartSteps; k++)
            {
                double c = input.StartSteps == 1 ? 1.1 : 0.55 + 0.55 * (k - 1) / (input.StartSteps - 1);
                steps.Add(new("起動", voltage, voltage / c - r, 1, 1));
            }

            double entry = steps.Count > 0 ? Math.Max(0, SpeedAt(input, steps[^1], 1)) : 0;
            double? seriesJump = null, parallelJump = null;
            int seriesCount = input.SeriesSteps - input.StartSteps;
            if (seriesCount > 0)
            {
                List<double>? stageSpeeds = input.StepSpeeds?.Take(seriesCount).ToList();
                List<double> resistances = StageResistances(input, voltage, entry, seriesCount, stageSpeeds, out double jump);
                foreach (double resistance in resistances) steps.Add(new(input.ParallelSteps > 0 ? "直列" : "抵抗", voltage, resistance, 1, 1));
                seriesJump = jump;
            }
            if (input.ParallelSteps > 0)
            {
                entry = Math.Max(0, SpeedAt(input, steps[^1], 1));
                List<double>? stageSpeeds = input.StepSpeeds?.Skip(Math.Max(0, seriesCount)).ToList();
                List<double> resistances = StageResistances(input, 1, entry, input.ParallelSteps, stageSpeeds, out double jump);
                foreach (double resistance in resistances) steps.Add(new("並列", 1, resistance, 1, 1));
                parallelJump = jump;
            }
            double weakLimit = input.WeakLimitCurrent / input.LimitCurrent;
            foreach (double field in input.FieldRatios) steps.Add(new("弱め界磁", 1, 0, field, weakLimit));
            return new(steps, seriesJump, parallelJump);
        }

        // ===== チェックと表 =====

        /// <summary>
        /// 値に問題があれば、日本語の理由の一覧を返す（なければ空）。問題があるときは段も表も作らない
        /// </summary>
        internal static List<string> Validate(ResistanceInput input)
        {
            List<string> problems = [];
            if (input.LimitCurrent <= 0) problems.Add(Loc.T("RC.006"));
            if (input.RatedSpeed <= 0) problems.Add(Loc.T("RC.007"));
            if (input.RatedForce <= 0) problems.Add(Loc.T("RC.008"));
            if (input.StartSteps < 0) problems.Add(Loc.T("RC.009"));
            if (input.SeriesSteps < 1) problems.Add(Loc.T("RC.010"));
            if (input.StartSteps > input.SeriesSteps) problems.Add(Loc.T("RC.011"));
            if (input.ParallelSteps < 0) problems.Add(Loc.T("RC.012"));
            if (input.SeriesSteps + Math.Max(0, input.ParallelSteps) + input.FieldRatios.Count > MaxStepCount) problems.Add(Loc.F("RC.013", MaxStepCount));
            for (int i = 0; i < input.FieldRatios.Count; i++)
            {
                double field = input.FieldRatios[i];
                if (field <= 0 || field > 1) problems.Add(Loc.F("RC.014", i + 1));
            }
            if (input.FieldRatios.Count > 0 && input.WeakLimitCurrent <= 0) problems.Add(Loc.T("RC.015"));
            if (input.Shape.Saturation <= 0) problems.Add(Loc.T("RC.016"));
            if (input.Shape.InternalResistance <= 0 || input.Shape.InternalResistance >= 0.5) problems.Add(Loc.T("RC.017"));
            if (input.Shape.Loss < 0) problems.Add(Loc.T("RC.018"));
            if (input.Shape.WeakSaturation < 0) problems.Add(Loc.T("RC.019"));
            if (input.Shape.WeakResistance < 0 || input.Shape.WeakResistance >= 0.5) problems.Add(Loc.T("RC.020"));
            if (input.Shape.WeakForceExponent < 0 || input.Shape.WeakForceExponent > 3) problems.Add(Loc.T("RC.021"));
            if (input.MaxSpeed <= 0) problems.Add(Loc.T("RC.022"));
            if (input.SpeedStep <= 0) problems.Add(Loc.T("RC.023"));
            if (input.ParallelSteps > 0 && (input.SeriesVoltage < MinSeriesVoltage || input.SeriesVoltage > MaxSeriesVoltage)) problems.Add(Loc.F("RC.024", MinSeriesVoltage, MaxSeriesVoltage));
            if (input.StepSpeeds != null)
            {
                int count = SwitchSpeedCount(input);
                if (input.StepSpeeds.Count != count) problems.Add(Loc.F("RC.025", input.StepSpeeds.Count, count));
                else if (input.StepSpeeds.Any(speed => speed < 0) || input.StepSpeeds.Zip(input.StepSpeeds.Skip(1), (a, b) => b < a).Any(down => down)) problems.Add(Loc.T("RC.026"));
            }
            return problems;
        }

        /// <summary>
        /// 表の行を作る（1 列目が速度、2 列目から段ごとの値）。作るのは引張力（Force）と電流（Current）だけ。値に問題があれば空
        /// </summary>
        internal static List<string[]> BuildRows(ResistanceInput input, TractionCalculator.TableKind kind)
        {
            if (kind is not (TractionCalculator.TableKind.Force or TractionCalculator.TableKind.Current)) return [];
            StepPlan plan = BuildSteps(input);
            if (plan.Steps.Count == 0) return [];

            List<string[]> rows = [];
            foreach (double speed in TractionCalculator.Speeds(input.MaxSpeed, input.SpeedStep))
            {
                string[] cells = new string[plan.Steps.Count + 1];
                cells[0] = TractionCalculator.Format(speed);
                for (int k = 0; k < plan.Steps.Count; k++)
                {
                    double x = CurrentRatioAt(input, plan.Steps[k], speed);
                    double value = kind == TractionCalculator.TableKind.Force ? ForceAt(input, plan.Steps[k], x, speed) : x * input.LimitCurrent;
                    cells[k + 1] = TractionCalculator.Format(value);
                }
                rows.Add(cells);
            }
            return rows;
        }

        /// <summary>
        /// 全ノッチで加速したときに使われる力 [N] と電流 [A]（速度ごと。電流が段の限流値まで下がったら次の段へ進む）
        /// </summary>
        internal static (double[] Force, double[] Current) Envelope(ResistanceInput input, StepPlan plan, IReadOnlyList<double> speeds)
        {
            double[] force = new double[speeds.Count], current = new double[speeds.Count];
            if (plan.Steps.Count == 0) return (force, current);
            int k = 0;
            for (int i = 0; i < speeds.Count; i++)
            {
                double x = CurrentRatioAt(input, plan.Steps[k], speeds[i]);
                while (x <= plan.Steps[k].LimitRatio && k < plan.Steps.Count - 1) x = CurrentRatioAt(input, plan.Steps[++k], speeds[i]);
                current[i] = x * input.LimitCurrent;
                force[i] = ForceAt(input, plan.Steps[k], x, speeds[i]);
            }
            return (force, current);
        }

        // ===== 今の表を読み取る・合わせる =====

        /// <summary>
        /// 今の表（引張力と電流。列は進段ごと）を読み取る。弱め界磁の段は後ろから weakSteps 個とする
        /// </summary>
        /// <param name="speeds">表の速度（行ごと）</param>
        /// <param name="force">引張力 [行][段]（数字でないセルは NaN）</param>
        /// <param name="current">電流 [行][段]</param>
        /// <param name="limits">段ごとの限流値 [A]（0 の段は電流を見ずにすぐ進む）</param>
        /// <returns>読み取れなければ null</returns>
        internal static TableAnalysis? AnalyzeTable(IReadOnlyList<double> speeds, IReadOnlyList<double[]> force, IReadOnlyList<double[]> current, IReadOnlyList<double> limits, int weakSteps)
        {
            int rowCount = Math.Min(speeds.Count, Math.Min(force.Count, current.Count));
            if (rowCount < 3 || weakSteps < 0) return null;
            int columns = Math.Min(force[0].Length, current[0].Length);
            if (columns <= weakSteps) return null;

            int fullColumn = columns - weakSteps - 1;
            (double Speed, double Force)? full = Crossing(speeds, force, current, rowCount, fullColumn, LimitOf(limits, fullColumn));
            if (full == null || full.Value.Force <= 0) return null;

            // 弱め界磁の段の磁束の比：同じ電流まで下がる速度の比（FluxFromSpeed が false なら力の比）。限流値が違えば、その電流で全界磁の段と比べる
            List<double> ratios = [];
            for (int k = fullColumn + 1; k < columns; k++)
            {
                double limit = LimitOf(limits, k);
                (double Speed, double Force)? weak = Crossing(speeds, force, current, rowCount, k, limit);
                if (weak == null) return null;
                (double Speed, double Force)? fullAtLimit = Crossing(speeds, force, current, rowCount, fullColumn, limit);
                double ratio = FluxFromSpeed
                    ? (fullAtLimit?.Speed ?? full.Value.Speed) / weak.Value.Speed
                    : weak.Value.Force / (fullAtLimit?.Force ?? full.Value.Force);
                ratios.Add(Math.Clamp(ratio, 0.05, 1.0));
            }

            // 全ノッチで走ったときの力と電流（限流値 0 の段はすぐ進む）
            // 0.5 km/h ごとに、行と行の間をまっすぐにつないだ値で段を進める（BVE と同じ）。行のある速度だけで決めると、
            // 行が粗い表（京成 3500 は 2 km/h ごと）では、行の間で段が進んだときののこぎりの山が抜け落ちて平らになる（ユーザーが見つけた）
            List<double> envSpeeds = [], envForce = [], envCurrent = [];
            int step = 0, row = 1;
            for (double v = EnvelopeSpeedStep; v <= speeds[rowCount - 1] + 1e-9; v += EnvelopeSpeedStep)
            {
                while (row < rowCount - 1 && speeds[row] < v) row++;
                double t = speeds[row] == speeds[row - 1] ? 1 : Math.Clamp((v - speeds[row - 1]) / (speeds[row] - speeds[row - 1]), 0, 1);
                double CurrentOf(int column) => current[row - 1][column] + (current[row][column] - current[row - 1][column]) * t;
                while (step < columns - 1)
                {
                    double limit = step < limits.Count ? limits[step] : 0;
                    if (limit > 0 && CurrentOf(step) > limit) break;
                    step++;
                }
                double f = force[row - 1][step] + (force[row][step] - force[row - 1][step]) * t;
                double c = CurrentOf(step);
                if (double.IsNaN(f) || double.IsNaN(c)) continue;
                envSpeeds.Add(Math.Round(v, 6));
                envForce.Add(f);
                envCurrent.Add(c);
            }
            // 段ごとの切り替わる速度（その段の電流が限流値まで下がる速度。限流値 0 の段・読めない段は NaN）
            double[] switchSpeeds = [.. Enumerable.Range(0, columns).Select(k =>
            {
                double limit = k < limits.Count ? limits[k] : 0;
                return limit > 0 && Crossing(speeds, force, current, rowCount, k, limit) is { } hit ? hit.Speed : double.NaN;
            })];
            return new(full.Value.Speed, full.Value.Force, ratios, envSpeeds, envForce, envCurrent, switchSpeeds);
        }

        // 段の限流値（0 や書かれていなければ、最初の正の限流値を使う）
        private static double LimitOf(IReadOnlyList<double> limits, int column)
        {
            if (column < limits.Count && limits[column] > 0) return limits[column];
            return limits.FirstOrDefault(l => l > 0);
        }

        // 列 column の電流が limit を下回る速度と、そのときの力（線形補間）
        private static (double Speed, double Force)? Crossing(IReadOnlyList<double> speeds, IReadOnlyList<double[]> force, IReadOnlyList<double[]> current, int rowCount, int column, double limit)
        {
            if (limit <= 0) return null;
            for (int n = 1; n < rowCount; n++)
            {
                double c0 = current[n - 1][column], c1 = current[n][column];
                if (!(speeds[n] > 0 && c0 > limit && c0 < 1e6 && c1 > 0 && c1 <= limit)) continue;
                double t = (c0 - limit) / (c0 - c1);
                double f = force[n - 1][column] + (force[n][column] - force[n - 1][column]) * t;
                if (double.IsNaN(f) || f >= 1e7) return null;
                return (speeds[n - 1] + (speeds[n] - speeds[n - 1]) * t, f);
            }
            return null;
        }

        // 列 column で電流が target のときの力（見つからなければ null）
        private static double? ForceOfColumnAtCurrent(IReadOnlyList<double> speeds, IReadOnlyList<double[]> force, IReadOnlyList<double[]> current, int rowCount, int column, double target)
        {
            (double Speed, double Force)? hit = Crossing(speeds, force, current, rowCount, column, target);
            return hit?.Force;
        }

        // ===== 進段パラメーター・表の文字を読む =====

        /// <summary>
        /// 進段パラメーターのテキストから、段の並び（直列・並列・弱め界磁の数）と段ごとの限流値（空車）を読む
        /// 見出しの「#」の行（S1・S1a・P1・WF1 など）で数える。見出しの空のセルは、前の段と同じ種類として数える（211 系の「WF1,,WF2,,」）
        /// 並列（P）がなく、直列（S）の途中に限流値 0 の段（つなぎ替え）があれば、そこまでを直列、その先を並列とする（南海 6000）
        /// </summary>
        /// <param name="columns">段の数（表の列の数。分からなければ 0 で、限流値の行の長さを使う）</param>
        internal static StepLayout ReadStepLayout(string paramsText, int columns)
        {
            string[] lines = paramsText.Replace("\r\n", "\n").Split('\n');
            string[]? limitCells = lines.Select(line => line.Split(','))
                .FirstOrDefault(cells => cells[0].Trim().Equals("CurrentLimitingValue_Empty", StringComparison.OrdinalIgnoreCase) || cells[0].Trim().Equals("CutOffCurrent_Empty", StringComparison.OrdinalIgnoreCase));
            if (columns <= 0) columns = limitCells == null ? 0 : limitCells.Skip(1).Count(cell => cell.Trim().Length > 0);
            double[] limits = new double[Math.Max(0, columns)];
            for (int k = 0; limitCells != null && k < columns && k + 1 < limitCells.Length; k++)
            {
                if (double.TryParse(limitCells[k + 1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)) limits[k] = value;
            }

            // 見出しの行：「#」で始まり、S・P・WF と数字の名前が 2 つ以上ある行
            string[]? header = lines.Where(line => line.TrimStart().StartsWith('#')).Select(line => line.Split(','))
                .FirstOrDefault(cells => cells.Skip(1).Count(cell => StepKindOf(cell) != null) >= 2);
            if (header == null) return new(columns, 0, 0, limits, false);

            int series = 0, parallel = 0, weak = 0;
            string last = "S";
            for (int k = 0; k < columns; k++)
            {
                string? kind = k + 1 < header.Length ? StepKindOf(header[k + 1]) : null;
                if (kind != null) last = kind;
                if (last == "P") parallel++;
                else if (last == "WF") weak++;
                else series++;
            }

            // 並列がなく、直列の途中に限流値 0 の段があれば、最後の限流値 0 の段までを直列とする
            if (parallel == 0)
            {
                int lastZero = -1;
                for (int k = 1; k < series - 1; k++)
                {
                    if (limits[k] == 0) lastZero = k;
                }
                if (lastZero > 0)
                {
                    parallel = series - (lastZero + 1);
                    series = lastZero + 1;
                }
            }
            return new(series, parallel, weak, limits, true);
        }

        // 見出しのセルの段の種類（S・P・WF。S1a のような名前も受け付ける）。段の名前でなければ null
        private static string? StepKindOf(string cell)
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(cell.Trim(), @"^(WF|S|P)\d+[a-zA-Z]?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }

        /// <summary>
        /// 表の行（1 列目が速度、2 列目から段ごとの値の文字）を数字に直す。速度が数字でない行は飛ばし、数字でないセルは NaN
        /// </summary>
        internal static (List<double> Speeds, List<double[]> Values) ParseRows(IEnumerable<string[]> rows)
        {
            List<double> speeds = [];
            List<double[]> values = [];
            foreach (string[] cells in rows)
            {
                if (cells.Length < 2 || !TryParseNumber(cells[0], out double speed)) continue;
                speeds.Add(speed);
                values.Add([.. cells.Skip(1).Select(cell => TryParseNumber(cell, out double v) ? v : double.NaN)]);
            }
            // 段の数（列の数）を最初の行に合わせる（短い行は NaN で埋める）
            int columns = values.Count > 0 ? values.Max(row => row.Length) : 0;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i].Length == columns) continue;
                double[] padded = [.. Enumerable.Repeat(double.NaN, columns)];
                values[i].CopyTo(padded, 0);
                values[i] = padded;
            }
            return (speeds, values);
        }

        // 数字として読む（前後の空白は無視）
        private static bool TryParseNumber(string text, out double value)
        {
            return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// 今の表の包絡線に一番合う電動機の形（s・r・L）を探す。弱め界磁の界磁の割合は、磁束の比から s ごとに直す
        /// </summary>
        /// <param name="input">段の数・限流値など（Shape と FieldRatios は使わず、探した値で置き換える）</param>
        /// <returns>一番合う形、全界磁の力の倍率（到達時間が合うように決めた値。1 なら今の表の値のまま）、今の表の段ごとの切り替わる速度を写したほうが合ったか、そのときのずれ</returns>
        /// <remarks>切り替わる速度を写すと京成 3500・能勢電・京急 2610・211 系はよく合い（京成の 0～30 km/h 9.0% → 3.6%）、南海 6000・京急 8000 は悪くなった。そのため両方を試して合うほうを選ぶ（2026-11-22）</remarks>
        internal static (MotorShape Shape, double ForceScale, bool UseSwitchSpeeds, double Error) Fit(ResistanceInput input, TableAnalysis table)
        {
            double[] saturations = [0.2, 0.3, 0.5, 0.7, 1.0, 1.5, 2.0, 3.0, 5.0];
            double[] resistances = [0.03, 0.06, 0.1, 0.15, 0.2, 0.3];
            double[] losses = [0, 0.005, 0.01, 0.02, 0.03];
            bool canUseSpeeds = TableSwitchSpeeds(input with { StartSteps = 0 }, table) != null;
            MotorShape best = input.Shape;
            double bestScale = 1, bestError = double.MaxValue;
            bool bestUseSpeeds = false;
            foreach (bool useSpeeds in canUseSpeeds ? new[] { false, true } : [false])
            {
                foreach (double s in saturations)
                {
                    foreach (double r in resistances)
                    {
                        foreach (double l in losses)
                        {
                            MotorShape shape = new(s, r, l);
                            double scale = ForceScaleFor(WithShape(input, table, shape, 1, useSpeeds), table);
                            double error = EnvelopeError(WithShape(input, table, shape, scale, useSpeeds), table);
                            if (error >= bestError) continue;
                            bestError = error;
                            best = shape;
                            bestScale = scale;
                            bestUseSpeeds = useSpeeds;
                        }
                    }
                }
            }

            // 2 段目：弱め界磁の段だけに使う飽和の強さと中の抵抗を選ぶ（全界磁の段までは変わらない。合わなければ全界磁と同じのまま）
            if (table.FluxRatios.Count == 0) return (best, bestScale, bestUseSpeeds, bestError);
            MotorShape main = best;
            double[] weakSaturations = [0.05, 0.1, .. saturations];
            foreach (double weakS in weakSaturations)
            {
                foreach (double weakR in resistances)
                {
                    MotorShape shape = main with { WeakSaturation = weakS, WeakResistance = weakR };
                    double scale = ForceScaleFor(WithShape(input, table, shape, 1, bestUseSpeeds), table);
                    double error = EnvelopeError(WithShape(input, table, shape, scale, bestUseSpeeds), table);
                    if (error >= bestError) continue;
                    bestError = error;
                    best = shape;
                    bestScale = scale;
                }
            }

            // 3 段目：弱め界磁の段の「力」だけに使う磁束の指数 q を選ぶ（速度と電流は変わらない。合わなければ飽和の形のまま）
            MotorShape weak = best;
            foreach (double exponent in new[] { 0.4, 0.6, 0.8, 1.0, 1.2, 1.5 })
            {
                MotorShape shape = weak with { WeakForceExponent = exponent };
                double scale = ForceScaleFor(WithShape(input, table, shape, 1, bestUseSpeeds), table);
                double error = EnvelopeError(WithShape(input, table, shape, scale, bestUseSpeeds), table);
                if (error >= bestError) continue;
                bestError = error;
                best = shape;
                bestScale = scale;
            }
            return (best, bestScale, bestUseSpeeds, bestError);
        }

        /// <summary>
        /// 今の表の段ごとの切り替わる速度を、起動の段の次から直列・並列の段の数だけ取り出す（段の数が表の列と合わなければ null）
        /// 読めない段（限流値 0 のつなぎ替えの段など）は 1 つ前の段と同じ速度（すぐ進む）。前の段より遅い速度は前の段と同じにする
        /// </summary>
        internal static List<double>? TableSwitchSpeeds(ResistanceInput input, TableAnalysis table)
        {
            int count = SwitchSpeedCount(input);
            if (count == 0 || input.StartSteps + count + table.FluxRatios.Count != table.SwitchSpeeds.Count) return null;
            double previous = input.StartSteps > 0 && !double.IsNaN(table.SwitchSpeeds[input.StartSteps - 1]) ? table.SwitchSpeeds[input.StartSteps - 1] : 0;
            List<double> result = [];
            foreach (double speed in table.SwitchSpeeds.Skip(input.StartSteps).Take(count))
            {
                if (!double.IsNaN(speed)) previous = Math.Max(previous, speed);
                result.Add(Math.Round(previous, 2));
            }
            return result;
        }

        // 全界磁の力の倍率の範囲（今の表の値から大きく離さない）
        private const double MinForceScale = 0.9;
        private const double MaxForceScale = 1.15;

        /// <summary>
        /// 到達時間が今の表と合うような、全界磁の力の倍率（到達時間の比の平均。力を c 倍すると時間は 1/c 倍になる。段の進み方は電流で決まるので変わらない）
        /// 段の中の跳ね上がりを同じ倍率にしているため、式の力はどのサンプルでも今の表より少し低めに出る（0→60 km/h が 2～5% 遅い）。その偏りを直す
        /// </summary>
        internal static double ForceScaleFor(ResistanceInput input, TableAnalysis table)
        {
            StepPlan plan = BuildSteps(input);
            if (plan.Steps.Count == 0 || table.Speeds.Count == 0) return 1;
            (double[] force, _) = Envelope(input, plan, table.Speeds);
            List<double> ratios = TimeRatios(force, table, input.RatedForce);
            return ratios.Count == 0 ? 1 : Math.Clamp(ratios.Average(), MinForceScale, MaxForceScale);
        }

        /// <summary>
        /// 形を置き換え、弱め界磁の界磁の割合を表の磁束の比から直した入力を作る（全界磁の力は今の表の値 × forceScale）
        /// useSwitchSpeeds なら、今の表の段ごとの切り替わる速度も写す（写せなければ写さない）
        /// </summary>
        internal static ResistanceInput WithShape(ResistanceInput input, TableAnalysis table, MotorShape shape, double forceScale = 1, bool useSwitchSpeeds = false)
        {
            List<double> fields = [.. table.FluxRatios.Select(ratio => FieldFromFluxRatio(shape, ratio))];
            // 切り替わる速度を写すときは、起動の段も同じように写す（起動の段の数は 0 にして、はじめの段から写す）
            // 決まった形の起動の段（速度 0 で 0.55～1.1 倍）では、南海 6000 の 0～2 km/h の形が合わなかった（2026-11-22。ユーザーが見つけた）
            ResistanceInput fromStart = input with { StartSteps = 0 };
            List<double>? speeds = useSwitchSpeeds ? TableSwitchSpeeds(fromStart, table) : null;
            return input with
            {
                Shape = shape, FieldRatios = fields, RatedSpeed = table.RatedSpeed, RatedForce = table.RatedForce * forceScale,
                StartSteps = speeds != null ? 0 : input.StartSteps,
                StepSpeeds = speeds,
                SeriesVoltage = TableSeriesVoltage(input, table, shape) ?? input.SeriesVoltage,
            };
        }

        // 直列のときの電圧の割合の範囲
        private const double MinSeriesVoltage = 0.3;
        private const double MaxSeriesVoltage = 0.9;

        /// <summary>
        /// 今の表の直列が終わる速度（直列の最後の段が限流値まで下がる速度）から、直列のときの電圧の割合を決める（読めなければ null）
        /// 直列の最後の段は抵抗 0 なので、その速度 ＝（電圧 − r）÷ κ、κ ＝（1 − r）÷ vs から、電圧 ＝ r ＋ 速度 ÷ vs ×（1 − r）
        /// </summary>
        internal static double? TableSeriesVoltage(ResistanceInput input, TableAnalysis table, MotorShape shape)
        {
            if (input.ParallelSteps <= 0 || input.SeriesSteps < 1 || input.SeriesSteps > table.SwitchSpeeds.Count || table.RatedSpeed <= 0) return null;
            // 直列の最後の段（限流値 0 で読めなければ、その前の読める段）
            double end = double.NaN;
            for (int k = input.SeriesSteps - 1; k >= 0 && double.IsNaN(end); k--) end = table.SwitchSpeeds[k];
            if (double.IsNaN(end) || end <= 0) return null;
            double r = shape.InternalResistance;
            return Math.Round(Math.Clamp(r + end / table.RatedSpeed * (1 - r), MinSeriesVoltage, MaxSeriesVoltage), 3);
        }

        /// <summary>
        /// 今の表の起動の段の数（全界磁の速度の 6% までに限流値まで下がる、はじめのほうの段の数。読めない段も数える）
        /// 京成 3500 は 2（2 km/h まで）、南海 6000 は 4（2 km/h まで）、能勢電 1500 は 5（1 km/h まで）だった
        /// </summary>
        internal static int TableStartSteps(TableAnalysis table)
        {
            int count = 0;
            foreach (double speed in table.SwitchSpeeds)
            {
                if (!double.IsNaN(speed) && speed > StartStepSpeedRatio * table.RatedSpeed) break;
                count++;
            }
            return count;
        }

        // 起動の段とみなす、限流値まで下がる速度の上限（全界磁の速度に対する割合）
        private const double StartStepSpeedRatio = 0.06;

        /// <summary>
        /// 弱め界磁の段の磁束の比を、限流値まで下がる速度の比から決めるか（false なら力の比）
        /// 直巻電動機なら同じ電流で「力 × 速度」は同じなので、どちらでも同じになるはずだが、京成 3500 のサンプルは WF1 だけ力が 1 割大きく書かれていた
        /// サンプル 6 つで比べ、速度の比のほうが 30～90 km/h の力のずれが小さかった（京成 8.7% → 6.5%、能勢電 5.1% → 2.9% など。2026-11-22。ユーザーが見つけた）
        /// </summary>
        internal static bool FluxFromSpeed { get; set; } = true;

        /// <summary>
        /// 合わせるときの、到達時間のずれの重さ（速度ごとの力・電流のずれに足す）
        /// サンプル 6 つで 0・1・3・10 を試し、全界磁の力の倍率と合わせて 1 で到達時間のずれがだいたい ±2% になった（2026-11-22）
        /// </summary>
        internal static double TimeWeight { get; set; } = 1;

        /// <summary>
        /// 表の包絡線とのずれ（力と電流の相対誤差の平均の和。力は Fl の 5%、電流は限流値の 5% を下限にして割る）
        /// ＋ 到達時間のずれ（0 → 20・40・60… km/h の到達時間の比と 1 の差の平均）× TimeWeight
        /// </summary>
        internal static double EnvelopeError(ResistanceInput input, TableAnalysis table)
        {
            StepPlan plan = BuildSteps(input);
            if (plan.Steps.Count == 0 || table.Speeds.Count == 0) return double.MaxValue;
            (double[] force, double[] current) = Envelope(input, plan, table.Speeds);
            double total = 0;
            for (int i = 0; i < table.Speeds.Count; i++)
            {
                total += Math.Abs(force[i] - table.EnvelopeForce[i]) / Math.Max(Math.Abs(table.EnvelopeForce[i]), 0.05 * input.RatedForce);
                total += Math.Abs(current[i] - table.EnvelopeCurrent[i]) / Math.Max(table.EnvelopeCurrent[i], 0.05 * input.LimitCurrent);
            }
            double error = total / table.Speeds.Count;
            if (TimeWeight <= 0) return error;
            List<double> ratios = TimeRatios(force, table, input.RatedForce);
            return ratios.Count == 0 ? error : error + TimeWeight * ratios.Average(ratio => Math.Abs(ratio - 1));
        }

        /// <summary>
        /// 全ノッチで加速したときの、0 → 20・40・60… km/h の到達時間の比（式 ÷ 今の表）。時間は「Δv ÷ 力」を足し合わせたもの（走行抵抗は入れない）
        /// 力が小さすぎるところ（Fl の 5% 未満）は 5% として数える（走行抵抗につり合う速度より上で時間が大きくなりすぎないように）
        /// </summary>
        internal static List<double> TimeRatios(IReadOnlyList<double> force, TableAnalysis table, double ratedForce)
        {
            List<double> ratios = [];
            double floor = 0.05 * ratedForce, model = 0, sample = 0, next = 20;
            for (int i = 1; i < table.Speeds.Count; i++)
            {
                double dv = table.Speeds[i] - table.Speeds[i - 1];
                model += dv / Math.Max(force[i], floor);
                sample += dv / Math.Max(table.EnvelopeForce[i], floor);
                if (table.Speeds[i] < next - 1e-9) continue;
                ratios.Add(model / sample);
                next += 20;
            }
            return ratios;
        }
    }
}
