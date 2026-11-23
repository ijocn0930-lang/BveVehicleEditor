using System.Globalization;

namespace BveVehicleEditor
{
    /// <summary>
    /// 力行の表（M 車 1 両あたり）を式から作る計算（電流・引張力テーブルの段階5。ユーザーと決めた式）
    /// 引張力（空車）：① 定トルク領域（0～v1）は F0 で一定、② 定電力領域（v1～v2）は F0 × v1 ÷ v、③ 特性領域（v2 より上）は F0 × v1 × v2^(k−1) ÷ v^k（k はノッチごと）
    ///   起動時の力 F0 ＝ 編成の重さ（慣性係数を含む）× 起動加速度 ÷ 3.6 ÷ M 車の両数（走行抵抗の分は足さない。ユーザーの判断）
    /// 引張力（満車）：空車の力 × 満車の倍率（乗客を足した重さの比）。「増やすのは ○ km/h まで」より上は空車と同じ
    /// 電流（空車）：ノッチごとの起動時の電流 I0 から、0～v2 は一定、v2 より上は I0 ×（v2 ÷ v）^(k−1)
    /// 電流（満車）：空車の電流 × 入れた倍率（増やす速度の上限は満車の力と同じ）
    /// 無負荷電流：0～v0 は入れた値で一定、v0 より上は 値 × v0 ÷ v（v0 が空ならノッチごとの v1）
    /// （サンプルの車両を調べて決めた。速度で変わる無負荷電流 14 個は、どれもこの形だった。抵抗制御・界磁制御の車両はこの式では作れない）
    /// ブレーキ（[Brake]。段階6）：引張力は 0～vs は 0、vs～va は直線で上げる、va～vb は一定、vb より上は ×（vb ÷ v）^k。最大の力は減速度から逆算。電流は力に比例。
    ///   満車・満車の電流・無負荷電流（v0 が空なら vb）は力行と同じ考え方（ブレーキの表 79 個を調べて決めた）
    /// </summary>
    internal static class TractionCalculator
    {
        /// <summary>
        /// 作る表の種類（ノッチのファイルの [Power] のキー）
        /// </summary>
        internal enum TableKind { Force, MaxForce, Current, MaxCurrent, NoLoadCurrent }

        /// <summary>
        /// 1 つのノッチの設定
        /// </summary>
        /// <param name="StartAcceleration">起動加速度 [km/h/s]</param>
        /// <param name="V1">定トルク領域の終わり（定電力領域の始まり）の速度 [km/h]</param>
        /// <param name="V2">定電力領域の終わり（特性領域の始まり）の速度 [km/h]</param>
        /// <param name="DecayExponent">特性領域の減衰の指数 k（ふつう 2。ノッチによって違うので、ノッチごとに持つ。ユーザーの判断）</param>
        /// <param name="StartCurrent">起動時の電流 [A]（低いノッチで力の比と合わない車両が多いので、ノッチごとに持つ。ユーザーの判断）。ブレーキでは最大の電流</param>
        /// <param name="Vs">ブレーキだけ：電気ブレーキが効き始める速度 [km/h]（これより下は力 0）</param>
        /// <param name="Va">ブレーキだけ：最大の力になる速度 [km/h]（vs～va は直線で上げる）</param>
        /// <remarks>ブレーキでは StartAcceleration を減速度 [km/h/s]、V2 を弱め始める速度 vb として使い、V1 は使わない</remarks>
        internal sealed record NotchSetting(double StartAcceleration, double V1, double V2, double DecayExponent, double StartCurrent, double Vs = 0, double Va = 0);

        /// <summary>
        /// 編成（パラメーターの [Dynamics] の値）
        /// </summary>
        internal sealed record TrainSpec(double MotorcarWeight, double TrailerWeight, double MotorcarCount, double TrailerCount, double MotorcarInertiaFactor, double TrailerInertiaFactor);

        /// <summary>
        /// 満車の決め方
        /// </summary>
        /// <param name="Capacity">1 両の定員 [人]（パラメーターの [Passengers] の Capacity）</param>
        /// <param name="BodyWeight">乗客 1 人の体重 [kg]（[Passengers] の BodyWeight）</param>
        /// <param name="LoadPercent">乗車率 [%]（定員の何 % が乗った重さを満車とするか）</param>
        /// <param name="BoostUpToSpeed">満車で力・電流を増やすのは何 km/h までか（null なら全部の速度）</param>
        internal sealed record LoadSpec(double Capacity, double BodyWeight, double LoadPercent, double? BoostUpToSpeed);

        /// <summary>
        /// 計算に使う値の全部
        /// </summary>
        /// <param name="MaxSpeed">表の最後の速度 [km/h]</param>
        /// <param name="SpeedStep">速度の刻み [km/h]</param>
        /// <param name="FullCurrentRatio">満車の電流は空車の何倍か</param>
        /// <param name="NoLoadCurrent">無負荷電流 [A]（v0 までの値）</param>
        /// <param name="NoLoadDropSpeed">無負荷電流が下がり始める速度 v0 [km/h]（null なら力行はノッチごとの v1、ブレーキは vb）</param>
        /// <param name="IsBrake">ブレーキ（[Brake]）の表か（引張力と電流の形が力行と違う。段階6）</param>
        internal sealed record TractionInput(TrainSpec Train, double MaxSpeed, double SpeedStep, IReadOnlyList<NotchSetting> Notches, LoadSpec Load, double FullCurrentRatio, double NoLoadCurrent, double? NoLoadDropSpeed, bool IsBrake = false);

        /// <summary>
        /// パラメーターから読んだ値（ParameterForm.ReadTrainForTraction）
        /// </summary>
        /// <param name="Resistance">走行抵抗の係数（書かれていなければ null で、既定の式を使う）</param>
        /// <param name="Capacity">1 両の定員 [人]</param>
        /// <param name="BodyWeight">乗客 1 人の体重 [kg]</param>
        internal sealed record ParamValues(TrainSpec Train, int PowerNotchCount, ResistanceFactors? Resistance, double Capacity, double BodyWeight);

        /// <summary>
        /// 走行抵抗の係数（編成全体の走行抵抗 FR = a × v² ＋ b × v ＋ c [N]。v は km/h。パラメーターの [Dynamics] の RunningResistanceFactor）
        /// </summary>
        internal sealed record ResistanceFactors(double A, double B, double C);

        // 表の行が多くなりすぎないように（刻み 0.1 km/h で 500 km/h なら 5001 行）
        private const int MaxRowCount = 5001;

        /// <summary>
        /// 表の種類のグラフの縦の軸の名前
        /// </summary>
        internal static string ValueName(TableKind kind)
        {
            return kind is TableKind.Force or TableKind.MaxForce ? Loc.T("TC.001") : Loc.T("TC.002");
        }

        /// <summary>
        /// 表の種類の名前（画面に出す）
        /// </summary>
        internal static string KindName(TableKind kind)
        {
            return kind switch
            {
                TableKind.Force => Loc.T("TC.003"),
                TableKind.MaxForce => Loc.T("TC.004"),
                TableKind.Current => Loc.T("TC.005"),
                TableKind.MaxCurrent => Loc.T("TC.006"),
                _ => Loc.T("TC.007"),
            };
        }

        /// <summary>
        /// 走行抵抗の係数が書かれていないときに BVE が使う既定の係数（出典：https://bvets.net/jp/edit/formats/vehicle/exposit.html。ParameterForm と同じ式）
        /// </summary>
        internal static ResistanceFactors DefaultResistance(TrainSpec train)
        {
            return new(
                0.275 + 0.076 * (train.MotorcarCount + train.TrailerCount - 1),
                0.000242 * train.MotorcarCount * train.MotorcarWeight + 0.0000275 * train.TrailerCount * train.TrailerWeight,
                0.0162 * train.MotorcarCount * train.MotorcarWeight + 0.00765 * train.TrailerCount * train.TrailerWeight);
        }

        /// <summary>
        /// 速度 v [km/h] のときの、M 車 1 両あたりの走行抵抗 [N]（編成全体の走行抵抗 ÷ M 車の両数。引張力の表と比べるため）。M 車がなければ 0
        /// </summary>
        internal static double RunningResistancePerMotorcar(ResistanceFactors factors, TrainSpec train, double speed)
        {
            if (train.MotorcarCount <= 0) return 0;
            return (factors.A * speed * speed + factors.B * speed + factors.C) / train.MotorcarCount;
        }

        /// <summary>
        /// 慣性を含めた編成の重さ [kg]（M 車の重さ × 両数 × (1 ＋ 慣性係数) ＋ T 車の分）
        /// </summary>
        internal static double EquivalentMass(TrainSpec train)
        {
            return train.MotorcarWeight * train.MotorcarCount * (1 + train.MotorcarInertiaFactor)
                + train.TrailerWeight * train.TrailerCount * (1 + train.TrailerInertiaFactor);
        }

        /// <summary>
        /// 満車の力の倍率（(編成の重さ ＋ 乗客の重さ) ÷ 編成の重さ。乗客の重さ ＝ 定員 × 乗車率 × 体重 × 全両数。乗客には慣性係数を掛けない）
        /// 383 の満車の力（空車の 1.085 倍）が、この式の 1.086 倍とほぼ同じだった
        /// </summary>
        internal static double FullLoadRatio(TrainSpec train, LoadSpec load)
        {
            double empty = EquivalentMass(train);
            if (empty <= 0) return 1;
            double passengers = load.Capacity * load.LoadPercent / 100 * load.BodyWeight * (train.MotorcarCount + train.TrailerCount);
            return (empty + passengers) / empty;
        }

        /// <summary>
        /// 起動時の力 F0（M 車 1 両あたり [N]）。M 車がなければ 0
        /// </summary>
        internal static double StartForce(TrainSpec train, double startAcceleration)
        {
            if (train.MotorcarCount <= 0) return 0;
            return EquivalentMass(train) * startAcceleration / 3.6 / train.MotorcarCount;
        }

        /// <summary>
        /// 速度 v [km/h] のときの引張力（空車。M 車 1 両あたり [N]）
        /// </summary>
        internal static double Force(double startForce, NotchSetting notch, double speed)
        {
            if (speed <= notch.V1) return startForce;
            if (speed <= notch.V2) return startForce * notch.V1 / speed;
            return startForce * notch.V1 * Math.Pow(notch.V2, notch.DecayExponent - 1) / Math.Pow(speed, notch.DecayExponent);
        }

        /// <summary>
        /// 速度 v [km/h] のときの電流（空車 [A]）。0～v2 は起動時の電流で一定、v2 より上は（v2 ÷ v）^(k−1) で下げる（電力が下がる分）
        /// </summary>
        internal static double Current(NotchSetting notch, double speed)
        {
            if (speed <= notch.V2) return notch.StartCurrent;
            return notch.StartCurrent * Math.Pow(notch.V2 / speed, notch.DecayExponent - 1);
        }

        /// <summary>
        /// 速度 v [km/h] のときのブレーキの引張力（空車。M 車 1 両あたり [N]。段階6。サンプルの 79 個の表を調べて決めた形）
        /// 0～vs は 0、vs～va は 0 から最大の力まで直線、va～vb は一定、vb より上は 最大の力 ×（vb ÷ v）^k
        /// </summary>
        internal static double BrakeForce(double maxForce, NotchSetting notch, double speed)
        {
            if (speed < notch.Vs) return 0;
            // ここに来るのは speed ≧ vs のときなので、speed < va なら va > vs（0 で割らない）。vs = va なら va で一気に最大
            if (speed < notch.Va) return maxForce * (speed - notch.Vs) / (notch.Va - notch.Vs);
            if (speed <= notch.V2) return maxForce;
            return maxForce * Math.Pow(notch.V2 / speed, notch.DecayExponent);
        }

        /// <summary>
        /// 速度 v [km/h] のときのブレーキの電流（空車 [A]）。最大の電流 × そのときの力 ÷ 最大の力（力に比例。ユーザーの判断。383 と同じ形）
        /// </summary>
        internal static double BrakeCurrent(NotchSetting notch, double speed)
        {
            return notch.StartCurrent * BrakeForce(1, notch, speed);
        }

        /// <summary>
        /// 速度 v [km/h] のときの無負荷電流 [A]。0～v0 は一定、v0 より上は 値 × v0 ÷ v（v0 が null なら力行はノッチの v1、ブレーキは vb）
        /// </summary>
        internal static double NoLoadCurrentAt(double noLoadCurrent, double? dropSpeed, NotchSetting notch, double speed, bool isBrake = false)
        {
            double v0 = dropSpeed ?? (isBrake ? notch.V2 : notch.V1);
            if (speed <= v0) return noLoadCurrent;
            return noLoadCurrent * v0 / speed;
        }

        /// <summary>
        /// 満車で増やす速度か（「増やすのは ○ km/h まで」が空なら全部の速度）
        /// </summary>
        internal static bool IsBoosted(LoadSpec load, double speed)
        {
            return load.BoostUpToSpeed == null || speed <= load.BoostUpToSpeed.Value;
        }

        /// <summary>
        /// 表の速度の列（0 から最高速度まで刻みごと。最高速度が刻みで割り切れなければ最後に最高速度を足す）
        /// </summary>
        internal static List<double> Speeds(double maxSpeed, double step)
        {
            List<double> speeds = [];
            for (int i = 0; i * step < maxSpeed - 1e-9 && speeds.Count < MaxRowCount; i++) speeds.Add(Math.Round(i * step, 6));
            speeds.Add(maxSpeed);
            return speeds;
        }

        /// <summary>
        /// 値に問題があれば、日本語の理由の一覧を返す（なければ空）。問題があるときは表を作らない
        /// 作る表の種類で使わない値は調べない（例：引張力（空車）では電流を調べない）
        /// </summary>
        internal static List<string> Validate(TractionInput input, TableKind kind)
        {
            List<string> problems = [];
            TrainSpec t = input.Train;
            bool usesCurrent = kind is TableKind.Current or TableKind.MaxCurrent;
            bool usesLoad = kind is TableKind.MaxForce or TableKind.MaxCurrent;

            if (t.MotorcarCount <= 0) problems.Add(Loc.T("TC.008"));
            if (t.MotorcarWeight <= 0) problems.Add(Loc.T("TC.009"));
            if (t.TrailerCount < 0 || t.TrailerWeight < 0) problems.Add(Loc.T("TC.010"));
            if (t.MotorcarInertiaFactor < 0 || t.TrailerInertiaFactor < 0) problems.Add(Loc.T("TC.011"));
            if (input.MaxSpeed <= 0) problems.Add(Loc.T("TC.012"));
            if (input.SpeedStep <= 0) problems.Add(Loc.T("TC.013"));
            else if (input.MaxSpeed / input.SpeedStep >= MaxRowCount) problems.Add(Loc.F("TC.014", MaxRowCount - 1));
            if (input.Notches.Count == 0) problems.Add(Loc.T("TC.015"));

            if (usesLoad)
            {
                if (input.Load.Capacity < 0 || input.Load.BodyWeight < 0) problems.Add(Loc.T("TC.016"));
                if (input.Load.LoadPercent < 0) problems.Add(Loc.T("TC.017"));
                if (input.Load.BoostUpToSpeed is <= 0) problems.Add(Loc.T("TC.018"));
            }
            if (kind == TableKind.MaxCurrent && input.FullCurrentRatio <= 0) problems.Add(Loc.T("TC.019"));
            if (kind == TableKind.NoLoadCurrent && input.NoLoadCurrent < 0) problems.Add(Loc.T("TC.020"));
            if (kind == TableKind.NoLoadCurrent && input.NoLoadDropSpeed is <= 0) problems.Add(Loc.T("TC.021"));

            for (int i = 0; i < input.Notches.Count; i++)
            {
                NotchSetting n = input.Notches[i];
                if (input.IsBrake)
                {
                    // ブレーキ：減速度・vs・va・vb（V2）・k・最大の電流
                    if (n.StartAcceleration < 0) problems.Add(Loc.F("TC.022", i + 1));
                    if (n.Vs < 0) problems.Add(Loc.F("TC.023", i + 1));
                    if (n.Va < n.Vs) problems.Add(Loc.F("TC.024", i + 1));
                    if (n.V2 < n.Va || n.V2 <= 0) problems.Add(Loc.F("TC.025", i + 1));
                    if (n.DecayExponent <= 0) problems.Add(Loc.F("TC.026", i + 1));
                    if (usesCurrent && n.StartCurrent < 0) problems.Add(Loc.F("TC.027", i + 1));
                    continue;
                }
                if (n.StartAcceleration < 0) problems.Add(Loc.F("TC.028", i + 1));
                if (n.V1 <= 0) problems.Add(Loc.F("TC.029", i + 1));
                if (n.V2 < n.V1) problems.Add(Loc.F("TC.030", i + 1));
                if (n.DecayExponent <= 0) problems.Add(Loc.F("TC.031", i + 1));
                if (usesCurrent && n.StartCurrent < 0) problems.Add(Loc.F("TC.032", i + 1));
            }
            return problems;
        }

        /// <summary>
        /// 表の行を作る（1 列目が速度、2 列目からノッチごとの値）。値に問題があれば空
        /// </summary>
        internal static List<string[]> BuildRows(TractionInput input, TableKind kind)
        {
            if (Validate(input, kind).Count > 0) return [];

            double[] startForces = [.. input.Notches.Select(n => StartForce(input.Train, n.StartAcceleration))];
            double forceRatio = FullLoadRatio(input.Train, input.Load);
            List<string[]> rows = [];
            foreach (double speed in Speeds(input.MaxSpeed, input.SpeedStep))
            {
                bool boosted = IsBoosted(input.Load, speed);
                string[] cells = new string[input.Notches.Count + 1];
                cells[0] = Format(speed);
                for (int i = 0; i < input.Notches.Count; i++)
                {
                    NotchSetting notch = input.Notches[i];
                    // 空車の力と電流（ブレーキは形が違う。最大の力は減速度から、力行の起動時の力と同じ式で逆算する）
                    double force = input.IsBrake ? BrakeForce(startForces[i], notch, speed) : Force(startForces[i], notch, speed);
                    double current = input.IsBrake ? BrakeCurrent(notch, speed) : Current(notch, speed);
                    double value = kind switch
                    {
                        TableKind.Force => force,
                        TableKind.MaxForce => force * (boosted ? forceRatio : 1),
                        TableKind.Current => current,
                        TableKind.MaxCurrent => current * (boosted ? input.FullCurrentRatio : 1),
                        _ => NoLoadCurrentAt(input.NoLoadCurrent, input.NoLoadDropSpeed, notch, speed, input.IsBrake),
                    };
                    cells[i + 1] = Format(value);
                }
                rows.Add(cells);
            }
            return rows;
        }

        /// <summary>
        /// 最高ノッチの値から、ノッチの表の最初の値を作る（ノッチ n の起動加速度と起動時の電流は n ÷ ノッチ数 の割合。v1・v2・k は同じ）
        /// </summary>
        internal static List<NotchSetting> DefaultNotches(int notchCount, NotchSetting top)
        {
            int count = Math.Max(1, notchCount);
            return [.. Enumerable.Range(1, count).Select(n => top with
            {
                StartAcceleration = Math.Round(top.StartAcceleration * n / count, 3),
                StartCurrent = Math.Round(top.StartCurrent * n / count, 3),
            })];
        }

        // 表に書く数字（小数は 3 けたまで）
        internal static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
