using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using static BveVehicleEditor.FormFieldHelper;

namespace BveVehicleEditor
{
    /// <summary>
    /// パラメーターファイルの入力フォーム（基本・運転台・定速運転装置・主制御装置）
    /// 下のテキストエディタが元になり、テキストが変わると少しあとにフォームを表示し直す
    /// フォームで値を変えると、下のテキストのその行を書き換える（Ctrl+Z で戻せる）
    /// </summary>
    public partial class ParameterForm : UserControl
    {
        // 運転台のセクション名（どちらか1つだけ書く）
        private const string CabSection = "Cab";
        private const string OneLeverCabSection = "OneLeverCab";

        // 定速運転装置のセクション名
        private const string ConstantSpeedSection = "ConstantSpeedControl";

        // 主制御装置のセクション名
        private const string MainCircuitSection = "MainCircuit";

        // 主制御装置の再粘着制御のセクション名（書かないと再粘着制御は行われない）
        // ページでは「NEW」の印があるが、ヘッダー 0.04 の公開車両（211-5000）でも使われているので、すべてのバージョンで使える扱い（ユーザー判断）
        private const string PowerReAdhesionSection = "PowerReAdhesion";

        // ブレーキ方式のセクション名（どれか1つだけ書く）。どれもなければ BVE5 / BVE6 は電気指令式 [Ecb] として扱う（ユーザー確認済み）
        private const string EcbSection = "Ecb";
        private const string SmeeSection = "Smee";
        private const string ClSection = "Cl";
        private static readonly string[] BrakeSystemSections = [EcbSection, SmeeSection, ClSection];

        // ブレーキ方式の欄の印（TextField.Section に入れる）。書くセクションは、そのとき選ばれている方式で決まる
        private const string BrakeSystemGroup = "*BrakeSystem";

        // 一部の方式でしか書けない項目と、書ける方式
        private static readonly Dictionary<string, string[]> BrakeKeysOnlyFor = new(StringComparer.OrdinalIgnoreCase)
        {
            // 電気指令式・電磁直通だけ
            ["PressureRates"] = [EcbSection, SmeeSection],
            // 電磁直通・自動空気だけ
            ["BpInitialPressure"] = [SmeeSection, ClSection],
        };

        // , で区切った数字の並びを書く項目（それぞれ 0 以上の数字）
        private static readonly HashSet<string> NumberListKeys = new(StringComparer.OrdinalIgnoreCase) { "PressureRates" };

        // 直通管の圧力調整弁のセクション名（ブレーキ方式が電磁直通 [Smee] のときだけ書く）
        private const string SapSection = "Sap";

        // 釣り合い空気溜めの圧力調整弁のセクション名（自動空気 [Cl] のときだけ書く）
        // Plan.md の調査メモ「Bvets Vehicle Parameters 2.00 から Er セクション使える」により、ヘッダーが 1.01 以下では使えない扱い
        private const string ErSection = "Er";

        // ブレーキ管の圧力調整弁のセクション名（電磁直通 [Smee] か自動空気 [Cl] のときに書く）
        private const string BpSection = "Bp";

        // 電空協調制御のセクション名（どちらか1つだけ書く）。どちらもなければ BVE は遅れ込め制御式 [AirSupplement] として扱う（ユーザー確認済み）
        private const string AirSupplementSection = "AirSupplement";
        private const string LockoutValveSection = "LockoutValve";
        private static readonly string[] CoordinationSections = [AirSupplementSection, LockoutValveSection];

        // 公式の一覧（https://bvets.net/jp/edit/formats/vehicle/params.html。2026-11-23 に読んだ）：セクション → 書けるキー（"" はセクションの前）
        // 一覧にない名前は書き間違いかもしれないので警告する（BveTextParser.FindUnknownNames）
        private static readonly string[] CabKeys = ["BrakeNotchCount", "PowerNotchCount", "HoldingSpeedNotchCount", "HoldingSpeedBrake", "AtsCancelNotch", "MotorBrakeNotch", "ReverserText", "BrakeText", "PowerText", "HoldingSpeedText"];
        private static readonly string[] BrakeSystemKeys = ["MaximumPressure", "PressureRates", "SapBcRatio", "SapBcOffset", "BpInitialPressure", "LeverDelay"];
        private static readonly string[] CoordinationKeys = ["MaximumPressure", "InitialPressure", "RegenerationLimit", "ShoeFriction"];
        private static readonly string[] BrakeCylinderKeys = ["ApplySpeed", "ReleaseSpeed", "VolumeRatio", "ApplyStart", "ApplyStop", "ReleaseStart", "ReleaseStop", "PistonArea"];
        private static readonly Dictionary<string, string[]> KnownKeys = new()
        {
            [""] = ["FirstCar", "LoadCompensating"],
            ["Cab"] = CabKeys,
            ["OneLeverCab"] = CabKeys,
            ["ConstantSpeedControl"] = ["Power", "Neutral", "Brake"],
            ["MainCircuit"] = ["RegenerationLimit", "RegenerationStartLimit", "LeverDelay", "BrakePriority", "SlipVelocityCoefficient"],
            ["PowerReAdhesion"] = ["SlipVelocity", "SlipAcceleration", "BalanceAcceleration", "HoldingTime", "ReferenceAcceleration", "ReferenceDeceleration", "CurrentDecrease", "CurrentIncrease"],
            ["Ecb"] = BrakeSystemKeys,
            ["Smee"] = BrakeSystemKeys,
            ["Cl"] = BrakeSystemKeys,
            ["Sap"] = ["ApplySpeed", "ReleaseSpeed", "VolumeRatio"],
            ["Er"] = ["ApplySpeed", "ReleaseSpeed", "VolumeRatio", "RapidReleaseSpeed"],
            ["Bp"] = ["ApplySpeed", "ReleaseSpeed", "VolumeRatio", "RapidReleaseSpeed"],
            ["AirSupplement"] = CoordinationKeys,
            ["LockoutValve"] = CoordinationKeys,
            ["Bc"] = BrakeCylinderKeys,
            ["BcServo"] = BrakeCylinderKeys,
            ["Brake"] = ["PistonArea", "ShoeFriction"],
            ["BrakeReAdhesion"] = ["SlipVelocity", "SlipDeceleration", "BalanceDeceleration", "HoldingTime", "ReferenceDeceleration", "BcReleaseSpeed", "BcApplySpeed"],
            ["Compressor"] = ["LowerPressure", "UpperPressure", "CompressionSpeed"],
            ["Dynamics"] = ["MotorcarWeight", "TrailerWeight", "MotorcarCount", "TrailerCount", "MotorcarInertiaFactor", "TrailerInertiaFactor", "CarLength", "RunningResistanceFactor", "CurveResistanceFactor"],
            ["Passengers"] = ["Capacity", "BodyWeight", "BoardingSpeed", "AlightingSpeed"],
            ["Door"] = ["CloseTime"],
            ["ViewPoint"] = ["X", "Y", "Z"],
        };

        // 電空協調制御の欄の印（TextField.Section に入れる）。書くセクションは、そのとき選ばれている方式で決まる
        private const string CoordinationGroup = "*Coordination";

        // 電空協調制御の画面に出す方式の名前
        private static string CoordinationDisplayName(string section) => section == AirSupplementSection ? Loc.T("Pm.001") : Loc.T("Pm.002");

        // ブレーキシリンダーの圧力調整弁のセクション名（どちらか1つだけ書く）
        // どちらもなければ BVE がどちらを使うかページに書かれていないので、仮に圧力比例制御式 [BcServo] として扱う（ユーザー判断）
        private const string BcSection = "BC";
        private const string BcServoSection = "BcServo";
        private static readonly string[] BrakeCylinderSections = [BcSection, BcServoSection];

        // ブレーキシリンダーの圧力調整弁の欄の印（TextField.Section に入れる）。書くセクションは、そのとき選ばれている方式で決まる
        private const string BrakeCylinderGroup = "*BrakeCylinder";

        // ブレーキシリンダーの圧力調整弁の画面に出す方式の名前
        private static string BrakeCylinderDisplayName(string section) => section == BcSection ? Loc.T("Pm.003") : Loc.T("Pm.004");

        // 基礎ブレーキ装置のセクション名
        // 受圧面積（PistonArea）は、ヘッダー 0.04 の公開車両（211-5000）でも使われているので、すべてのバージョンで使える扱い（ユーザー判断）
        // 制輪子の摩擦係数（ShoeFriction）は Plan.md の調査メモ「1.01 にはない」により、ヘッダーが 1.01 以下では使えない扱い
        private const string BrakeSection = "Brake";

        // 基礎ブレーキ装置の滑走再粘着制御のセクション名（書かないと再粘着制御は行われない）
        // ページで「NEW」の印、Plan.md の調査メモ「1.01 にはない」、見本もヘッダー 2.01 だけなので、ヘッダーが 1.01 以下では使えない扱い（ユーザー判断）
        private const string BrakeReAdhesionSection = "BrakeReAdhesion";

        // 空気圧縮機と元空気溜めのセクション名
        private const string CompressorSection = "Compressor";

        // 力学パラメーターのセクション名
        private const string DynamicsSection = "Dynamics";

        // 乗客のセクション名。ページでは「NEW」の印があるが、どのバージョンでも使える扱い（ユーザー判断）
        private const string PassengersSection = "Passengers";

        // 客室ドア（側引戸）のセクション名
        private const string DoorSection = "Door";

        // 運転士の視点のセクション名
        private const string ViewPointSection = "ViewPoint";

        // 制輪子の摩擦係数の式 μ = a × (1 + b × v) ÷ (1 + c × v) の係数 a, b, c の既定値（出典は DefaultValues と同じページ）
        private static readonly string[] ShoeFrictionDefaults = ["0.26", "0.01", "0.025"];

        // 速度ごとの μ の表に出す速度（km/h）。XAML の表の列と同じ順
        private static readonly int[] ShoeFrictionSpeeds = [0, 25, 50, 75, 100, 130];

        // 画面に出す方式の名前
        private static string BrakeSystemDisplayName(string section) => section switch
        {
            EcbSection => Loc.T("Pm.005"),
            SmeeSection => Loc.T("Pm.006"),
            _ => Loc.T("Pm.007"),
        };

        // マイナスも書ける数字の項目（それ以外の数字は 0 以上）
        private static readonly HashSet<string> NegativeAllowedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            // 範囲がページに書かれていない
            "SlipVelocityCoefficient",
            // 既定値が -2。電流を最小まで下げるときはマイナスの大きな値にする
            "BalanceAcceleration",
            // 滑走再粘着制御の点 B。電流を最小まで下げるときはマイナスの大きな値にする
            "BalanceDeceleration",
            // 運転士の視点の座標（既定値の X・Z がマイナス）
            "X",
            "Y",
            "Z",
        };

        // BveTs Vehicle Parameters 1.01 以前にはない項目（2.00 以降で増えた。2.00 は魚拓がなく不明なので使える扱い）
        private static readonly HashSet<string> KeysNotInOldVersion = new(StringComparer.OrdinalIgnoreCase)
        {
            "RegenerationStartLimit",
            "SlipVelocityCoefficient",
        };

        // この版以下のヘッダーでは、上の項目は使えない
        private const decimal LastOldVersion = 1.01m;

        /// <summary>
        /// 数字・文字の欄1つ分
        /// </summary>
        /// <param name="Box">欄</param>
        /// <param name="Key">ファイルに書くキー</param>
        /// <param name="Section">書くセクション。null なら運転台（[Cab] / [OneLeverCab]。どちらもなければ既定の [Cab]）</param>
        /// <param name="Label">画面の名前（警告の文に使う）</param>
        private sealed record TextField(TextBox Box, string Key, string? Section, string LabelKey)
        {
            // 画面の名前（言語を切り替えてもすぐ変わるよう、使うたびに引く）
            public string Label => Loc.T(LabelKey);
        }

        // 書かれていないときに BVE が使う値（既定値）。書かれていない欄に「（既定: ○○）」と薄く出す
        // 出典：https://bvets.net/jp/edit/formats/vehicle/params.html（BveTs Vehicle Parameters 2.01 の表）
        // [Cab] も [OneLeverCab] もないときは [Cab] として扱われる（ユーザーが K_SEI3500R で確認済み）
        private static Dictionary<string, string> DefaultValues => new(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstCar"] = "T",
            ["LoadCompensating"] = "true",
            ["BrakeNotchCount"] = "8",
            ["PowerNotchCount"] = "5",
            ["HoldingSpeedNotchCount"] = "0",
            ["HoldingSpeedBrake"] = "false",
            ["AtsCancelNotch"] = "1",
            ["MotorBrakeNotch"] = "1",
            ["ReverserText"] = "B,N,F",
            ["PowerText"] = "P0,P1,P2,...",
            ["BrakeText"] = "B0,B1,B2,...,EB",
            ["HoldingSpeedText"] = "H0,H1,H2,...",
            // 定速運転装置。Power・Brake はほかのセクションにも同じ名前があるので「セクション名:キー」で区別する
            [$"{ConstantSpeedSection}:Power"] = "0",
            [$"{ConstantSpeedSection}:Neutral"] = "0",
            [$"{ConstantSpeedSection}:Brake"] = "0",
            // 主制御装置。RegenerationStartLimit の既定値は「RegenerationLimit + 5」なので、表示のたびに計算する（_computedDefaults）
            [$"{MainCircuitSection}:RegenerationLimit"] = "5",
            [$"{MainCircuitSection}:LeverDelay"] = "0.5",
            [$"{MainCircuitSection}:BrakePriority"] = "false",
            [$"{MainCircuitSection}:SlipVelocityCoefficient"] = "0",
            // 主制御装置の再粘着制御
            [$"{PowerReAdhesionSection}:SlipVelocity"] = "2",
            [$"{PowerReAdhesionSection}:SlipAcceleration"] = "10",
            [$"{PowerReAdhesionSection}:BalanceAcceleration"] = "-2",
            [$"{PowerReAdhesionSection}:HoldingTime"] = "2",
            [$"{PowerReAdhesionSection}:ReferenceAcceleration"] = "4",
            [$"{PowerReAdhesionSection}:ReferenceDeceleration"] = "5",
            [$"{PowerReAdhesionSection}:CurrentDecrease"] = "0",
            [$"{PowerReAdhesionSection}:CurrentIncrease"] = "0",
            // ブレーキ方式（[Ecb]/[Smee]/[Cl] 共通の表。LeverDelay は主制御装置にも同じ名前があるので区別する）
            [$"{BrakeSystemGroup}:MaximumPressure"] = "440000",
            [$"{BrakeSystemGroup}:PressureRates"] = Loc.T("Pm.008"),
            [$"{BrakeSystemGroup}:SapBcRatio"] = "0.94",
            [$"{BrakeSystemGroup}:SapBcOffset"] = "30000",
            [$"{BrakeSystemGroup}:BpInitialPressure"] = "490000",
            [$"{BrakeSystemGroup}:LeverDelay"] = "0.2",
            // 直通管の圧力調整弁（ApplySpeed などは [Er]・[Bp] にも同じ名前がある）
            [$"{SapSection}:ApplySpeed"] = "500",
            [$"{SapSection}:ReleaseSpeed"] = "500",
            [$"{SapSection}:VolumeRatio"] = "20",
            // 釣り合い空気溜めの圧力調整弁
            [$"{ErSection}:ApplySpeed"] = "500",
            [$"{ErSection}:ReleaseSpeed"] = "500",
            [$"{ErSection}:VolumeRatio"] = "20",
            [$"{ErSection}:RapidReleaseSpeed"] = "1000",
            // ブレーキ管の圧力調整弁
            [$"{BpSection}:ApplySpeed"] = "500",
            [$"{BpSection}:ReleaseSpeed"] = "500",
            [$"{BpSection}:VolumeRatio"] = "20",
            [$"{BpSection}:RapidReleaseSpeed"] = "1000",
            // 電空協調制御（MaximumPressure・RegenerationLimit はほかのセクションにも同じ名前がある）
            [$"{CoordinationGroup}:MaximumPressure"] = "440000",
            [$"{CoordinationGroup}:InitialPressure"] = "40000",
            [$"{CoordinationGroup}:RegenerationLimit"] = "5",
            [$"{CoordinationGroup}:ShoeFriction"] = "0.18",
            // ブレーキシリンダーの圧力調整弁（[BC]/[BcServo] 共通の表。PistonArea はページで [Brake] へ移ったため既定値なし）
            [$"{BrakeCylinderGroup}:ApplySpeed"] = "500",
            [$"{BrakeCylinderGroup}:ReleaseSpeed"] = "500",
            [$"{BrakeCylinderGroup}:VolumeRatio"] = "20",
            [$"{BrakeCylinderGroup}:ApplyStart"] = "20000",
            [$"{BrakeCylinderGroup}:ApplyStop"] = "0",
            [$"{BrakeCylinderGroup}:ReleaseStart"] = "20000",
            [$"{BrakeCylinderGroup}:ReleaseStop"] = "0",
            // 基礎ブレーキ装置（ShoeFriction の既定値は 3 つの欄に分けて ShoeFrictionDefaults に持つ）
            [$"{BrakeSection}:PistonArea"] = "0.4",
            // 基礎ブレーキ装置の滑走再粘着制御
            [$"{BrakeReAdhesionSection}:SlipVelocity"] = "10",
            [$"{BrakeReAdhesionSection}:SlipDeceleration"] = "20",
            [$"{BrakeReAdhesionSection}:BalanceDeceleration"] = "0",
            [$"{BrakeReAdhesionSection}:HoldingTime"] = "0",
            [$"{BrakeReAdhesionSection}:ReferenceDeceleration"] = "5",
            [$"{BrakeReAdhesionSection}:BcReleaseSpeed"] = "0",
            [$"{BrakeReAdhesionSection}:BcApplySpeed"] = "0",
            // 空気圧縮機（LowerPressure が始動、UpperPressure が停止の圧力。Plan.md のメモは逆だったが、ページに合わせる）
            [$"{CompressorSection}:LowerPressure"] = "700000",
            [$"{CompressorSection}:UpperPressure"] = "800000",
            [$"{CompressorSection}:CompressionSpeed"] = "5000",
            // 力学パラメーター（RunningResistanceFactor と CurveResistanceFactor の既定値は質量・両数から計算する。ShowDynamics）
            [$"{DynamicsSection}:MotorcarWeight"] = "31500",
            [$"{DynamicsSection}:TrailerWeight"] = "31500",
            [$"{DynamicsSection}:MotorcarCount"] = "5",
            [$"{DynamicsSection}:TrailerCount"] = "5",
            [$"{DynamicsSection}:MotorcarInertiaFactor"] = "0.1",
            [$"{DynamicsSection}:TrailerInertiaFactor"] = "0.05",
            [$"{DynamicsSection}:CarLength"] = "20",
            // 乗客
            [$"{PassengersSection}:Capacity"] = "150",
            [$"{PassengersSection}:BodyWeight"] = "65",
            [$"{PassengersSection}:BoardingSpeed"] = "3.15",
            [$"{PassengersSection}:AlightingSpeed"] = "6.3",
            // 客室ドア
            [$"{DoorSection}:CloseTime"] = "5",
            // 運転士の視点
            [$"{ViewPointSection}:X"] = "-1.0",
            [$"{ViewPointSection}:Y"] = "2.5",
            [$"{ViewPointSection}:Z"] = "-1.0",
        };

        // ほかの値から計算する既定値（例: 電気ブレーキ開始の最低速度 = 電気ブレーキを切る速度 + 5）。表示のたびに入れ直す
        private readonly Dictionary<string, string> _computedDefaults = new(StringComparer.OrdinalIgnoreCase);

        // 書かれていない項目に出す文字（例:「（既定: 8）」）
        private static string NotWrittenText(string key)
        {
            return DefaultValues.TryGetValue(key, out string? value) ? Loc.F("Pm.009", value) : Loc.T("Pm.010");
        }

        // 元になるテキストエディタ
        private TextEditor? _editor;

        // テキストを打つたびに読み直すと重いので、打ち終わって少し待ってから1回だけ表示し直す
        private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

        // 表示し直している最中か（このときのコンボボックスの変化は、ユーザーの操作ではないので書き込まない）
        private bool _isRefreshing;

        // 今テキストに書かれている運転台のセクション（Cab / OneLeverCab）。どちらもなければ null
        private string? _cabSection;

        // 運転台の欄に書き込むときのセクション。どちらも書かれていなければ、BVE の既定の [Cab]（セクションごと足す）
        private string CabSectionToWrite => _cabSection ?? CabSection;

        // 今テキストに書かれているブレーキ方式のセクション（Ecb / Smee / Cl。書かれているとおりの大文字小文字）。どれもなければ null
        private string? _brakeSection;

        // ブレーキ方式の欄に書き込むときのセクション。どれも書かれていなければ [Ecb]（セクションごと足す）
        private string BrakeSectionToWrite => _brakeSection ?? EcbSection;

        // 今のブレーキ方式（Ecb / Smee / Cl のどれか。書かれていなければ既定の Ecb）。[Sap] などの関連セクションで使う
        private string CurrentBrakeSystem => BrakeSystemSections.First(section => string.Equals(section, BrakeSectionToWrite, StringComparison.OrdinalIgnoreCase));

        // 今テキストに書かれている電空協調制御のセクション（AirSupplement / LockoutValve。書かれているとおり）。どちらもなければ null
        private string? _coordinationSection;

        // 電空協調制御の欄に書き込むときのセクション。どちらも書かれていなければ [AirSupplement]（セクションごと足す）
        private string CoordinationSectionToWrite => _coordinationSection ?? AirSupplementSection;

        // 今の電空協調制御の方式（AirSupplement / LockoutValve のどちらか。書かれていなければ既定の AirSupplement）
        private string CurrentCoordination => CoordinationSections.First(section => string.Equals(section, CoordinationSectionToWrite, StringComparison.OrdinalIgnoreCase));

        // 今テキストに書かれているブレーキシリンダーの圧力調整弁のセクション（BC / BcServo。書かれているとおり）。どちらもなければ null
        private string? _brakeCylinderSection;

        // ブレーキシリンダーの圧力調整弁の欄に書き込むときのセクション。どちらも書かれていなければ [BcServo]（セクションごと足す）
        private string BrakeCylinderSectionToWrite => _brakeCylinderSection ?? BcServoSection;

        // 今のブレーキシリンダーの圧力調整弁の方式（BC / BcServo のどちらか。書かれていなければ仮の既定の BcServo）
        private string CurrentBrakeCylinder => BrakeCylinderSections.First(section => string.Equals(section, BrakeCylinderSectionToWrite, StringComparison.OrdinalIgnoreCase));

        // 欄を書き込むセクション（運転台・ブレーキ方式・電空協調制御・ブレーキシリンダーの圧力調整弁は、そのとき選ばれているセクション）
        private string SectionToWrite(TextField field) => field.Section switch
        {
            null => CabSectionToWrite,
            BrakeSystemGroup => BrakeSectionToWrite,
            CoordinationGroup => CoordinationSectionToWrite,
            BrakeCylinderGroup => BrakeCylinderSectionToWrite,
            _ => field.Section,
        };

        // 数字・文字の欄（運転台・主制御装置）
        private readonly TextField[] _textFields;

        // 「a, b, c」のように 1 行に複数の数字を書く項目を、数字ごとの欄に分けたもの
        private sealed record SplitField(TextBox[] Boxes, string Section, string Key);

        // 制輪子の摩擦係数（[Brake] ShoeFriction）の a, b, c の欄
        private readonly SplitField _shoeFriction;

        // 走行抵抗の係数（[Dynamics] RunningResistanceFactor）の a, b, c の欄
        private readonly SplitField _runningResistance;

        // 走行抵抗の係数の既定値（質量・両数から表示のたびに計算し直す）
        private string[] _runningResistanceDefaults = ["", "", ""];

        // 速度ごとの μ を出す文字（ShoeFrictionSpeeds と同じ順）
        private readonly TextBlock[] _shoeFrictionMuTexts;

        // 定速運転装置の選ぶ欄・既定値を出す文字・ファイルに書くキー・画面の名前の組
        private readonly (ComboBox Combo, TextBlock DefaultLabel, string Key, string LabelKey)[] _constantSpeedFields;

        // 欄に入ったときの値（Esc で戻す・変わっていなければ書き込まないため）。書かれていなかったら null
        private readonly Dictionary<TextBox, string?> _valuesOnFocus = [];

        public ParameterForm()
        {
            InitializeComponent();
            _refreshTimer.Tick += (s, e) =>
            {
                _refreshTimer.Stop();
                Refresh();
            };

            _textFields =
            [
                // 運転台（Section = null）
                new(TxtBrakeNotchCount, "BrakeNotchCount", null, "Pm.011"),
                new(TxtPowerNotchCount, "PowerNotchCount", null, "Pm.012"),
                new(TxtHoldingSpeedNotchCount, "HoldingSpeedNotchCount", null, "Pm.013"),
                new(TxtAtsCancelNotch, "AtsCancelNotch", null, "Pm.014"),
                new(TxtMotorBrakeNotch, "MotorBrakeNotch", null, "Pm.015"),
                new(TxtReverserText, "ReverserText", null, "Pm.016"),
                new(TxtPowerText, "PowerText", null, "Pm.017"),
                new(TxtBrakeText, "BrakeText", null, "Pm.018"),
                new(TxtHoldingSpeedText, "HoldingSpeedText", null, "Pm.019"),
                // 主制御装置
                new(TxtRegenerationLimit, "RegenerationLimit", MainCircuitSection, "Pm.020"),
                new(TxtRegenerationStartLimit, "RegenerationStartLimit", MainCircuitSection, "Pm.021"),
                new(TxtLeverDelay, "LeverDelay", MainCircuitSection, "Pm.022"),
                new(TxtSlipVelocityCoefficient, "SlipVelocityCoefficient", MainCircuitSection, "Pm.023"),
                // 主制御装置の再粘着制御
                new(TxtSlipVelocity, "SlipVelocity", PowerReAdhesionSection, "Pm.024"),
                new(TxtSlipAcceleration, "SlipAcceleration", PowerReAdhesionSection, "Pm.025"),
                new(TxtBalanceAcceleration, "BalanceAcceleration", PowerReAdhesionSection, "Pm.026"),
                new(TxtHoldingTime, "HoldingTime", PowerReAdhesionSection, "Pm.027"),
                new(TxtReferenceAcceleration, "ReferenceAcceleration", PowerReAdhesionSection, "Pm.028"),
                new(TxtReferenceDeceleration, "ReferenceDeceleration", PowerReAdhesionSection, "Pm.029"),
                new(TxtCurrentDecrease, "CurrentDecrease", PowerReAdhesionSection, "Pm.030"),
                new(TxtCurrentIncrease, "CurrentIncrease", PowerReAdhesionSection, "Pm.031"),
                // ブレーキ方式（書くセクションは選ばれている方式）
                new(TxtMaximumPressure, "MaximumPressure", BrakeSystemGroup, "Pm.032"),
                new(TxtPressureRates, "PressureRates", BrakeSystemGroup, "Pm.033"),
                new(TxtSapBcRatio, "SapBcRatio", BrakeSystemGroup, "Pm.034"),
                new(TxtSapBcOffset, "SapBcOffset", BrakeSystemGroup, "Pm.035"),
                new(TxtBpInitialPressure, "BpInitialPressure", BrakeSystemGroup, "Pm.036"),
                new(TxtBrakeLeverDelay, "LeverDelay", BrakeSystemGroup, "Pm.037"),
                // 直通管の圧力調整弁（電磁直通だけ）
                new(TxtSapApplySpeed, "ApplySpeed", SapSection, "Pm.038"),
                new(TxtSapReleaseSpeed, "ReleaseSpeed", SapSection, "Pm.039"),
                new(TxtSapVolumeRatio, "VolumeRatio", SapSection, "Pm.040"),
                // 釣り合い空気溜めの圧力調整弁（自動空気だけ）
                new(TxtErApplySpeed, "ApplySpeed", ErSection, "Pm.041"),
                new(TxtErReleaseSpeed, "ReleaseSpeed", ErSection, "Pm.042"),
                new(TxtErVolumeRatio, "VolumeRatio", ErSection, "Pm.043"),
                new(TxtErRapidReleaseSpeed, "RapidReleaseSpeed", ErSection, "Pm.044"),
                // ブレーキ管の圧力調整弁（電磁直通・自動空気）
                new(TxtBpApplySpeed, "ApplySpeed", BpSection, "Pm.045"),
                new(TxtBpReleaseSpeed, "ReleaseSpeed", BpSection, "Pm.046"),
                new(TxtBpVolumeRatio, "VolumeRatio", BpSection, "Pm.047"),
                new(TxtBpRapidReleaseSpeed, "RapidReleaseSpeed", BpSection, "Pm.048"),
                // 電空協調制御（書くセクションは選ばれている方式）
                new(TxtCoordinationMaximumPressure, "MaximumPressure", CoordinationGroup, "Pm.049"),
                new(TxtCoordinationInitialPressure, "InitialPressure", CoordinationGroup, "Pm.050"),
                new(TxtCoordinationRegenerationLimit, "RegenerationLimit", CoordinationGroup, "Pm.051"),
                new(TxtCoordinationShoeFriction, "ShoeFriction", CoordinationGroup, "Pm.052"),
                // ブレーキシリンダーの圧力調整弁（書くセクションは選ばれている方式）
                new(TxtBcApplySpeed, "ApplySpeed", BrakeCylinderGroup, "Pm.053"),
                new(TxtBcReleaseSpeed, "ReleaseSpeed", BrakeCylinderGroup, "Pm.054"),
                new(TxtBcVolumeRatio, "VolumeRatio", BrakeCylinderGroup, "Pm.055"),
                new(TxtBcApplyStart, "ApplyStart", BrakeCylinderGroup, "Pm.056"),
                new(TxtBcApplyStop, "ApplyStop", BrakeCylinderGroup, "Pm.057"),
                new(TxtBcReleaseStart, "ReleaseStart", BrakeCylinderGroup, "Pm.058"),
                new(TxtBcReleaseStop, "ReleaseStop", BrakeCylinderGroup, "Pm.059"),
                new(TxtBcPistonArea, "PistonArea", BrakeCylinderGroup, "Pm.060"),
                // 基礎ブレーキ装置（制輪子の摩擦係数は 3 つの欄に分かれるので、ここには入れない）
                new(TxtBrakePistonArea, "PistonArea", BrakeSection, "Pm.061"),
                // 基礎ブレーキ装置の滑走再粘着制御
                new(TxtBrakeSlipVelocity, "SlipVelocity", BrakeReAdhesionSection, "Pm.062"),
                new(TxtBrakeSlipDeceleration, "SlipDeceleration", BrakeReAdhesionSection, "Pm.063"),
                new(TxtBrakeBalanceDeceleration, "BalanceDeceleration", BrakeReAdhesionSection, "Pm.064"),
                new(TxtBrakeHoldingTime, "HoldingTime", BrakeReAdhesionSection, "Pm.065"),
                new(TxtBrakeReferenceDeceleration, "ReferenceDeceleration", BrakeReAdhesionSection, "Pm.066"),
                new(TxtBrakeBcReleaseSpeed, "BcReleaseSpeed", BrakeReAdhesionSection, "Pm.067"),
                new(TxtBrakeBcApplySpeed, "BcApplySpeed", BrakeReAdhesionSection, "Pm.068"),
                // 空気圧縮機
                new(TxtCompressorLowerPressure, "LowerPressure", CompressorSection, "Pm.069"),
                new(TxtCompressorUpperPressure, "UpperPressure", CompressorSection, "Pm.070"),
                new(TxtCompressionSpeed, "CompressionSpeed", CompressorSection, "Pm.071"),
                // 力学パラメーター（走行抵抗の係数は 3 つの欄に分かれるので、ここには入れない）
                new(TxtMotorcarWeight, "MotorcarWeight", DynamicsSection, "Pm.072"),
                new(TxtTrailerWeight, "TrailerWeight", DynamicsSection, "Pm.073"),
                new(TxtMotorcarCount, "MotorcarCount", DynamicsSection, "Pm.074"),
                new(TxtTrailerCount, "TrailerCount", DynamicsSection, "Pm.075"),
                new(TxtMotorcarInertiaFactor, "MotorcarInertiaFactor", DynamicsSection, "Pm.076"),
                new(TxtTrailerInertiaFactor, "TrailerInertiaFactor", DynamicsSection, "Pm.077"),
                new(TxtCarLength, "CarLength", DynamicsSection, "Pm.078"),
                new(TxtCurveResistanceFactor, "CurveResistanceFactor", DynamicsSection, "Pm.079"),
                // 乗客
                new(TxtPassengerCapacity, "Capacity", PassengersSection, "Pm.080"),
                new(TxtPassengerBodyWeight, "BodyWeight", PassengersSection, "Pm.081"),
                new(TxtBoardingSpeed, "BoardingSpeed", PassengersSection, "Pm.082"),
                new(TxtAlightingSpeed, "AlightingSpeed", PassengersSection, "Pm.083"),
                // 客室ドア
                new(TxtDoorCloseTime, "CloseTime", DoorSection, "Pm.084"),
                // 運転士の視点
                new(TxtViewPointX, "X", ViewPointSection, "Pm.085"),
                new(TxtViewPointY, "Y", ViewPointSection, "Pm.086"),
                new(TxtViewPointZ, "Z", ViewPointSection, "Pm.087"),
            ];

            _shoeFriction = new([TxtShoeFrictionA, TxtShoeFrictionB, TxtShoeFrictionC], BrakeSection, "ShoeFriction");
            _runningResistance = new([TxtRunningResistanceA, TxtRunningResistanceB, TxtRunningResistanceC], DynamicsSection, "RunningResistanceFactor");
            _shoeFrictionMuTexts = [TxtMu0, TxtMu25, TxtMu50, TxtMu75, TxtMu100, TxtMu130];

            _constantSpeedFields =
            [
                (CmbConstantSpeedPower, TxtConstantSpeedPowerDefault, "Power", "Pm.088"),
                (CmbConstantSpeedNeutral, TxtConstantSpeedNeutralDefault, "Neutral", "Pm.089"),
                (CmbConstantSpeedBrake, TxtConstantSpeedBrakeDefault, "Brake", "Pm.090"),
            ];

            // 数字・文字の欄：入ったら「（未記入）」を消す、出たら・Enter で書き込む、Esc で取り消す
            foreach (TextField field in _textFields)
            {
                field.Box.GotKeyboardFocus += (s, e) => BeginEdit(field.Box);
                field.Box.LostKeyboardFocus += (s, e) => CommitText(field, isLeaving: true);
                field.Box.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter) CommitText(field, isLeaving: false);
                    if (e.Key == Key.Escape) CancelEdit(field);
                };
            }

            // 「a, b, c」の欄も同じ動き（書き込むときは欄をまとめて「a, b, c」の1行にする）
            foreach (SplitField splitField in new[] { _shoeFriction, _runningResistance })
            {
                for (int i = 0; i < splitField.Boxes.Length; i++)
                {
                    int index = i;
                    TextBox box = splitField.Boxes[i];
                    box.GotKeyboardFocus += (s, e) => BeginEdit(box);
                    box.LostKeyboardFocus += (s, e) => CommitSplitField(splitField, index, isLeaving: true);
                    box.KeyDown += (s, e) =>
                    {
                        if (e.Key == Key.Enter) CommitSplitField(splitField, index, isLeaving: false);
                        if (e.Key == Key.Escape) CancelSplitField(splitField, index);
                    };
                }
            }
        }

        /// <summary>
        /// 元になるテキストエディタをつなぐ。テキストが変わったら表示し直す
        /// </summary>
        public void Attach(TextEditor editor)
        {
            _editor = editor;
            editor.TextChanged += (s, e) =>
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            };
            Refresh();
        }

        /// <summary>
        /// テキストを読み直して、フォームに表示する
        /// </summary>
        public void Refresh()
        {
            if (_editor == null) return;

            _isRefreshing = true;
            try
            {
                ShowValues(BveTextParser.Parse(_editor.Text));
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        // 読み取った行をフォームの各欄に出す
        private void ShowValues(List<BveTextLine> lines)
        {
            // パラメーターファイルを読み込めていない（テキストが空）ときは、フォームでは変えられないようにする
            bool canEdit = lines.Count > 0;
            CmbFirstCar.IsEnabled = canEdit;
            ChkLoadCompensating.IsEnabled = canEdit;

            // 同じセクションが 2 回以上書かれていれば、フォームの一番上に知らせる
            List<string> duplicates = BveTextParser.DescribeDuplicateSections(lines);
            TxtParamNotice.Text = string.Join("\n", duplicates);
            TxtParamNotice.Visibility = duplicates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // 公式の一覧にないセクション・キー（書き間違いかもしれない）を、フォームの一番上に警告する
            ShowWarnings(TxtParamWarnings, BveTextParser.FindUnknownNames(lines, KnownKeys));

            // 基本（ファイルの先頭、セクションの前に書く項目）
            string? firstCar = FindValue(lines, null, "FirstCar");
            CmbFirstCar.SelectedIndex = firstCar?.ToUpperInvariant() switch
            {
                "M" => 0,
                "T" => 1,
                _ => -1,
            };
            // 書かれていない（読めない値も含む）ときは、選ぶ欄の横に既定値を出す
            TxtFirstCarDefault.Text = NotWrittenText("FirstCar");
            TxtFirstCarDefault.Visibility = canEdit && CmbFirstCar.SelectedIndex < 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowBool(ChkLoadCompensating, FindValue(lines, null, "LoadCompensating"), Loc.T("Pm.091"), "LoadCompensating");

            // 運転台（[Cab] か [OneLeverCab] のどちらか。どちらもなければ BVE は既定の [Cab] として扱う）
            (string? cabSection, bool isBothWritten) = FindCabSection(lines);
            _cabSection = cabSection;
            RdoCab.IsChecked = CabSectionToWrite == CabSection && canEdit;
            RdoOneLeverCab.IsChecked = cabSection == OneLeverCabSection;

            // ハンドルは、両方書かれているとき以外は切り替えられる（どちらもなければ選ぶとセクションを足す）
            RdoCab.IsEnabled = canEdit && !isBothWritten;
            RdoOneLeverCab.IsEnabled = canEdit && !isBothWritten;

            // 中の項目は、両方書かれているとき以外は変えられる（どちらもないときは [Cab] ごと足して書き込む）
            bool canEditCab = canEdit && !isBothWritten;
            foreach (TextField field in _textFields.Where(field => field.Section == null))
            {
                field.Box.IsEnabled = canEditCab;
                // 打っている最中の欄は書き換えない
                if (field.Box.IsKeyboardFocusWithin) continue;
                ShowText(field.Box, FindCabValue(lines, cabSection, field.Key), PlaceholderOf(field));
            }
            ChkHoldingSpeedBrake.IsEnabled = canEditCab;
            ShowBool(ChkHoldingSpeedBrake, FindCabValue(lines, cabSection, "HoldingSpeedBrake"), Loc.T("Pm.092"), "HoldingSpeedBrake");

            // ノッチ数とテキストの数などが合わなければ、運転台の枠の一番上に警告を出す
            List<string> problems = FindCabProblems(lines, cabSection);
            ShowWarnings(TxtCabWarnings, problems);

            ShowConstantSpeed(lines, canEdit);
            ShowMainCircuit(lines, canEdit);
            ShowPowerReAdhesion(lines, canEdit);
            ShowBrakeSystem(lines, canEdit);
            // ブレーキ方式で使い分けるセクション（今の方式が決まってから出す）
            (decimal? version, string? versionText) = ReadHeaderVersion(lines);
            bool isOldVersion = version != null && version <= LastOldVersion;
            ShowBrakeSubSection(lines, canEdit, SapSection, [SmeeSection], TxtSapNotice, TxtSapWarnings, null);
            ShowBrakeSubSection(lines, canEdit, ErSection, [ClSection], TxtErNotice, TxtErWarnings, isOldVersion ? versionText : null);
            ShowBrakeSubSection(lines, canEdit, BpSection, [SmeeSection, ClSection], TxtBpNotice, TxtBpWarnings, null);
            ShowCoordination(lines, canEdit);
            ShowBrakeCylinder(lines, canEdit, version, versionText);
            ShowBrake(lines, canEdit, isOldVersion, versionText);
            ShowBrakeReAdhesion(lines, canEdit, isOldVersion, versionText);
            ShowCompressor(lines, canEdit);
            ShowDynamics(lines, canEdit);
            ShowWarnings(TxtPassengersWarnings, ShowNumberFields(lines, PassengersSection, PassengersSection, canEdit, field => false, field => string.Empty));
            ShowWarnings(TxtDoorWarnings, ShowNumberFields(lines, DoorSection, DoorSection, canEdit, field => false, field => string.Empty));
            ShowWarnings(TxtViewPointWarnings, ShowNumberFields(lines, ViewPointSection, ViewPointSection, canEdit, field => false, field => string.Empty));
        }

        // 空気圧縮機を出す。数字でなければ警告。始動の圧力が停止の圧力以上なら警告
        // 片方が書かれていなければ既定値で比べる（ユーザー判断）。両方とも既定値なら比べない
        private void ShowCompressor(List<BveTextLine> lines, bool canEdit)
        {
            List<string> problems = ShowNumberFields(lines, CompressorSection, CompressorSection, canEdit, field => false, field => string.Empty);

            CountValue? lower = ReadNumberForCheck(lines, CompressorSection, "LowerPressure");
            CountValue? upper = ReadNumberForCheck(lines, CompressorSection, "UpperPressure");
            bool canCompare = lower != null && upper != null && !(lower.Value.IsDefault && upper.Value.IsDefault);
            if (canCompare && lower!.Value.Value >= upper!.Value.Value) problems.Add(Loc.F("Pm.093", DescribeCount(Loc.T("Pm2.001"), lower.Value), DescribeCount(Loc.T("Pm2.002"), upper.Value)));
            ShowWarnings(TxtCompressorWarnings, problems);
        }

        // 比べるために数字を読む。書かれていなければ既定値。数字でなければ null（その警告は ShowNumberFields が出すので、ここでは比べない）
        private static CountValue? ReadNumberForCheck(List<BveTextLine> lines, string section, string key)
        {
            string? value = FindValue(lines, section, key);
            if (value == null) return new(double.Parse(DefaultValues[$"{section}:{key}"], System.Globalization.CultureInfo.InvariantCulture), IsDefault: true);
            return TryReadNumber(value, allowNegative: false, out double number) ? new(number, IsDefault: false) : null;
        }

        // 基礎ブレーキ装置の滑走再粘着制御を出す。セクションがあるときだけ「使う」にチェックして欄を使えるようにする
        // ヘッダーが 1.01 以下では、チェックも欄も灰色にして知らせ、書かれていれば警告
        private void ShowBrakeReAdhesion(List<BveTextLine> lines, bool canEdit, bool isOldVersion, string? versionText)
        {
            bool isWritten = BveTextParser.HasSection(lines, BrakeReAdhesionSection);

            ChkBrakeReAdhesionEnabled.IsChecked = isWritten;
            ChkBrakeReAdhesionEnabled.IsEnabled = canEdit && !isOldVersion;

            List<string> problems = ShowNumberFields(lines, BrakeReAdhesionSection, BrakeReAdhesionSection, canEdit && isWritten && !isOldVersion, field => false, field => string.Empty);
            if (isWritten && isOldVersion) problems = [Loc.F("Pm.094", BrakeReAdhesionSection, versionText)];

            TxtBrakeReAdhesionNotice.Text = Loc.F("Pm.095", versionText, BrakeReAdhesionSection);
            TxtBrakeReAdhesionNotice.Visibility = canEdit && isOldVersion ? Visibility.Visible : Visibility.Collapsed;
            ShowWarnings(TxtBrakeReAdhesionWarnings, problems);
        }

        // 「滑走再粘着制御を使う」をクリックしたとき：チェックしたらセクションを足す。外したら確認してからセクションごと消す
        private void ChkBrakeReAdhesionEnabled_Click(object sender, RoutedEventArgs e)
        {
            ToggleOptionalSection(ChkBrakeReAdhesionEnabled, BrakeReAdhesionSection, Loc.T("Pm.096"));
        }

        // 基礎ブレーキ装置 [Brake] を出す。受圧面積はどのバージョンでも使える
        // 制輪子の摩擦係数は「a, b, c」を 3 つの欄に分けて出し、速度ごとの μ を計算して表にする。ヘッダーが 1.01 以下では灰色
        private void ShowBrake(List<BveTextLine> lines, bool canEdit, bool isOldVersion, string? versionText)
        {
            List<string> problems = ShowNumberFields(lines, BrakeSection, BrakeSection, canEdit, field => false, field => string.Empty);

            string? shoeFriction = FindValue(lines, BrakeSection, "ShoeFriction");
            string?[] parts = ShowSplitField(lines, _shoeFriction, canEdit && !isOldVersion);

            if (shoeFriction != null && isOldVersion) problems.Add(Loc.F("Pm.097", versionText));
            else if (shoeFriction != null && !IsValidSplitValue(shoeFriction, _shoeFriction.Boxes.Length)) problems.Add(Loc.F("Pm.098", shoeFriction));

            // 速度ごとの μ。読めない・書かれていない係数は既定値で計算する。使えないバージョンでは出さない
            double[] coefficients = parts.Select((part, i) => TryReadNumber(part, allowNegative: true, out double number) ? number : double.Parse(ShoeFrictionDefaults[i], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            for (int i = 0; i < ShoeFrictionSpeeds.Length; i++)
            {
                double speed = ShoeFrictionSpeeds[i];
                double denominator = 1 + coefficients[2] * speed;
                bool canCalculate = canEdit && !isOldVersion && denominator != 0;
                _shoeFrictionMuTexts[i].Text = canCalculate ? (coefficients[0] * (1 + coefficients[1] * speed) / denominator).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "—";
            }

            TxtBrakeNotice.Text = Loc.F("Pm.099", versionText);
            TxtBrakeNotice.Visibility = canEdit && isOldVersion ? Visibility.Visible : Visibility.Collapsed;
            ShowWarnings(TxtBrakeWarnings, problems);
        }

        // 力学パラメーター [Dynamics] を出す。数字でなければ警告
        // 走行抵抗の係数（a, b, c）と曲線抵抗の係数（d）は、書かれていなければ今の質量・両数から計算した既定値を出す
        private void ShowDynamics(List<BveTextLine> lines, bool canEdit)
        {
            // 計算に使う質量・両数（書かれていない・読めないときは既定値）
            double motorcarWeight = ReadNumberOrDefault(lines, DynamicsSection, "MotorcarWeight");
            double trailerWeight = ReadNumberOrDefault(lines, DynamicsSection, "TrailerWeight");
            double motorcarCount = ReadNumberOrDefault(lines, DynamicsSection, "MotorcarCount");
            double trailerCount = ReadNumberOrDefault(lines, DynamicsSection, "TrailerCount");

            // 既定値の式（出典：https://bvets.net/jp/edit/formats/vehicle/exposit.html）
            _runningResistanceDefaults =
            [
                FormatNumber(0.275 + 0.076 * (motorcarCount + trailerCount - 1)),
                FormatNumber(0.000242 * motorcarCount * motorcarWeight + 0.0000275 * trailerCount * trailerWeight),
                FormatNumber(0.0162 * motorcarCount * motorcarWeight + 0.00765 * trailerCount * trailerWeight),
            ];
            _computedDefaults[$"{DynamicsSection}:CurveResistanceFactor"] = FormatNumber(9.80665 * 0.6 * (motorcarCount * motorcarWeight + trailerCount * trailerWeight));

            List<string> problems = ShowNumberFields(lines, DynamicsSection, DynamicsSection, canEdit, field => false, field => string.Empty);

            string? runningResistance = FindValue(lines, DynamicsSection, "RunningResistanceFactor");
            ShowSplitField(lines, _runningResistance, canEdit);
            if (runningResistance != null && !IsValidSplitValue(runningResistance, _runningResistance.Boxes.Length)) problems.Add(Loc.F("Pm.100", runningResistance));
            ShowWarnings(TxtDynamicsWarnings, problems);
        }

        /// <summary>
        /// 表を式から作るウィンドウ用に、[Dynamics] の重さ・両数・慣性係数と走行抵抗の係数、[Passengers] の定員と体重、運転台の力行ノッチ数を読む
        /// 書かれていない・読めないときは既定値（[Cab] も [OneLeverCab] もなければ既定の [Cab]）
        /// 走行抵抗の係数は、書かれていない・「a, b, c」の 3 つの数字でなければ null（呼ぶ側で既定の式を使う）
        /// </summary>
        internal static TractionCalculator.ParamValues ReadTrainForTraction(string paramText)
        {
            List<BveTextLine> lines = BveTextParser.Parse(paramText);
            TractionCalculator.TrainSpec train = new(
                ReadNumberOrDefault(lines, DynamicsSection, "MotorcarWeight"),
                ReadNumberOrDefault(lines, DynamicsSection, "TrailerWeight"),
                ReadNumberOrDefault(lines, DynamicsSection, "MotorcarCount"),
                ReadNumberOrDefault(lines, DynamicsSection, "TrailerCount"),
                ReadNumberOrDefault(lines, DynamicsSection, "MotorcarInertiaFactor"),
                ReadNumberOrDefault(lines, DynamicsSection, "TrailerInertiaFactor"));

            string cab = BveTextParser.HasSection(lines, OneLeverCabSection) && !BveTextParser.HasSection(lines, CabSection) ? OneLeverCabSection : CabSection;
            int powerNotchCount = TryReadNumber(FindValue(lines, cab, "PowerNotchCount"), allowNegative: false, out double count) && count >= 1
                ? (int)count
                : int.Parse(DefaultValues["PowerNotchCount"], System.Globalization.CultureInfo.InvariantCulture);

            // 走行抵抗の係数「a, b, c」（3 つとも数字のときだけ使う。マイナスは BVE の書き方として受け付ける）
            TractionCalculator.ResistanceFactors? resistance = null;
            string[]? parts = FindValue(lines, DynamicsSection, "RunningResistanceFactor")?.Split(',');
            if (parts is { Length: 3 }
                && TryReadNumber(parts[0], allowNegative: true, out double a)
                && TryReadNumber(parts[1], allowNegative: true, out double b)
                && TryReadNumber(parts[2], allowNegative: true, out double c))
            {
                resistance = new(a, b, c);
            }

            // 満車の重さに使う、1 両の定員と乗客 1 人の体重
            double capacity = ReadNumberOrDefault(lines, PassengersSection, "Capacity");
            double bodyWeight = ReadNumberOrDefault(lines, PassengersSection, "BodyWeight");
            return new(train, powerNotchCount, resistance, capacity, bodyWeight);
        }

        // セクションの数字を読む。書かれていない・読めないときは DefaultValues の既定値
        private static double ReadNumberOrDefault(List<BveTextLine> lines, string section, string key)
        {
            if (TryReadNumber(FindValue(lines, section, key), allowNegative: false, out double value)) return value;
            return double.Parse(DefaultValues[$"{section}:{key}"], System.Globalization.CultureInfo.InvariantCulture);
        }

        // 計算した既定値を画面に出す形にする（小数は 6 けたまで。欄を1つ入れたときにほかの欄へそのまま書くので、丸めすぎない）
        private static string FormatNumber(double value)
        {
            return value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        // 「a, b, c」のような欄の既定値（制輪子の摩擦係数は決まった値、走行抵抗の係数は質量・両数から計算した値）
        private string[] SplitDefaultsOf(SplitField field) => field == _shoeFriction ? ShoeFrictionDefaults : _runningResistanceDefaults;

        // 「a, b, c」のような欄の i 番目に出す既定値の文字
        private string SplitPlaceholderOf(SplitField field, int index) => Loc.F("Pm.101", SplitDefaultsOf(field)[index]);

        // 「a, b, c」のような欄を出す。分けた値を返す（書かれていない・空のところは null）
        private string?[] ShowSplitField(List<BveTextLine> lines, SplitField field, bool canEdit)
        {
            string?[] parts = SplitValues(FindValue(lines, field.Section, field.Key), field.Boxes.Length);
            for (int i = 0; i < field.Boxes.Length; i++)
            {
                TextBox box = field.Boxes[i];
                box.IsEnabled = canEdit;
                if (!box.IsKeyboardFocusWithin) ShowText(box, parts[i], SplitPlaceholderOf(field, i));
            }
            return parts;
        }

        // 「a, b, c」を count 個に分ける。書かれていない・空のところは null
        private static string?[] SplitValues(string? value, int count)
        {
            string[] items = value?.Split(',').Select(item => item.Trim()).ToArray() ?? [];
            return [.. Enumerable.Range(0, count).Select(i => i < items.Length && items[i].Length > 0 ? items[i] : null)];
        }

        // 「a, b, c」がちょうど count 個の数字か
        private static bool IsValidSplitValue(string value, int count)
        {
            string[] items = value.Split(',');
            return items.Length == count && items.All(item => TryReadNumber(item.Trim(), allowNegative: true, out _));
        }

        // 「a, b, c」のような欄から出たとき・Enter：欄をまとめて「a, b, c」の1行に書き込む
        // ほかの欄が書かれていなければ、そこは既定値で埋める（BVE はそろっていないと読めないため）
        // isLeaving：欄から出たとき true、Enter のとき false（欄に入ったまま）
        private void CommitSplitField(SplitField field, int index, bool isLeaving)
        {
            TextBox box = field.Boxes[index];
            if (_isRefreshing || _editor == null || !_valuesOnFocus.TryGetValue(box, out string? original)) return;
            // 「（既定: ○○）」を出しているだけなら、何も打っていないので書き込まない
            if (IsShowingPlaceholder(box)) return;

            string value = box.Text.Trim();
            string placeholder = SplitPlaceholderOf(field, index);
            if (value.Length == 0)
            {
                RestoreEmptyBox(box, original, placeholder, isLeaving);
                return;
            }
            if (value == original) return;

            // 「,」は係数の区切り、「;」と「#」はコメントの始まりなので、1つの欄には入れられない
            if (value.Contains(',') || BveTextParser.FindCommentIndex(value) >= 0)
            {
                MessageBox.Show(Loc.T("Pm.102"), Loc.T("Pm.103"), MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowText(box, original, placeholder);
                return;
            }

            string?[] parts = SplitValues(FindValue(BveTextParser.Parse(_editor.Text), field.Section, field.Key), field.Boxes.Length);
            parts[index] = value;
            string[] defaults = SplitDefaultsOf(field);
            WriteValue(field.Section, field.Key, string.Join(", ", parts.Select((part, i) => part ?? defaults[i])));
            _valuesOnFocus[box] = value;
        }

        // 「a, b, c」のような欄で Esc：欄に入ったときの値に戻す
        private void CancelSplitField(SplitField field, int index)
        {
            TextBox box = field.Boxes[index];
            if (!_valuesOnFocus.TryGetValue(box, out string? original)) return;
            // 書かれていなかった欄なら空に戻す（欄に入ったままなので「（既定: ○○）」は出さない）
            RestoreEmptyBox(box, original, SplitPlaceholderOf(field, index), isLeaving: false);
        }

        // ブレーキシリンダーの圧力調整弁（[BC]/[BcServo] のどちらか1つ）を出す
        // どちらもなければ仮に [BcServo]。両方あれば方式は切り替えられない
        // 受圧面積（PistonArea）はヘッダーが 1.01 以下のときだけ書ける（2.00 以降は [Brake] へ移った）。ヘッダーが読めないときは書ける扱い
        private void ShowBrakeCylinder(List<BveTextLine> lines, bool canEdit, decimal? version, string? versionText)
        {
            List<string> writtenSections = FindWrittenSections(lines, BrakeCylinderSections);
            bool isMultiple = writtenSections.Count > 1;
            _brakeCylinderSection = writtenSections.FirstOrDefault();
            bool isPistonAreaMoved = version != null && version > LastOldVersion;

            string current = CurrentBrakeCylinder;
            RdoBc.IsChecked = canEdit && current == BcSection;
            RdoBcServo.IsChecked = canEdit && current == BcServoSection;
            RdoBc.IsEnabled = canEdit && !isMultiple;
            RdoBcServo.IsEnabled = canEdit && !isMultiple;

            List<string> notices = [];
            if (canEdit && writtenSections.Count == 0) notices.Add(Loc.T("Pm.104"));
            if (isMultiple) notices.Add(Loc.F("Pm.105", string.Join(Loc.T("Pm2.003"), writtenSections.Select(section => $"[{section}]")), _brakeCylinderSection));
            if (canEdit && isPistonAreaMoved) notices.Add(Loc.F("Pm.106", versionText));
            TxtBrakeCylinderNotice.Text = string.Join("\n", notices);
            TxtBrakeCylinderNotice.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            List<string> problems = ShowNumberFields(lines, BrakeCylinderGroup, _brakeCylinderSection, canEdit && !isMultiple,
                field => field.Key == "PistonArea" && isPistonAreaMoved,
                field => Loc.F("Pm.107", field.Label, field.Key, versionText, _brakeCylinderSection));
            ShowWarnings(TxtBrakeCylinderWarnings, problems);
        }

        // ブレーキシリンダーの圧力調整弁の方式を選んだとき：今の方式のセクション名を書き換える（どちらもなければ最後に足す）
        private void BrakeCylinderRadio_Click(object sender, RoutedEventArgs e)
        {
            if (_editor == null) return;

            string newSection = sender == RdoBc ? BcSection : BcServoSection;
            if (string.Equals(_brakeCylinderSection, newSection, StringComparison.OrdinalIgnoreCase)) return;
            // どちらも書かれていないときに仮の既定の [BcServo] を選んだだけなら、何も書かない
            if (_brakeCylinderSection == null && newSection == BcServoSection) return;

            EditorTextHelper.ReplaceChangedText(_editor, BveTextParser.RenameSection(_editor.Text, _brakeCylinderSection, newSection));
            OperationHistory.Add($"パラメーター入力: ブレーキシリンダーの圧力調整弁 [{_brakeCylinderSection ?? "なし"}] → [{newSection}]");
        }

        // ブレーキ方式で使い分けるセクション（[Sap]・[Er]・[Bp]）を出す。今の方式が usedFor のどれかのときだけ欄が使える
        // ほかの方式のときは灰色にして知らせ、セクションが書かれていれば警告
        // oldVersionText：ヘッダーが古くてこのセクションを使えないときのバージョン（使えるなら null）
        private void ShowBrakeSubSection(List<BveTextLine> lines, bool canEdit, string section, string[] usedFor,
            TextBlock notice, TextBlock warnings, string? oldVersionText)
        {
            string current = CurrentBrakeSystem;
            bool isUsedByMethod = usedFor.Contains(current);
            bool isOldVersion = oldVersionText != null;
            bool isWritten = BveTextParser.HasSection(lines, section);
            string usedForText = string.Join(Loc.T("Pm.108"), usedFor.Select(BrakeSystemDisplayName));

            List<string> problems = ShowNumberFields(lines, section, section, canEdit && isUsedByMethod && !isOldVersion, field => false, field => string.Empty);

            // 使えないのに書かれているときは、欄ごとではなく1つにまとめて警告
            if (isWritten && isOldVersion) problems = [Loc.F("Pm.109", section, oldVersionText)];
            else if (isWritten && !isUsedByMethod) problems.Insert(0, Loc.F("Pm.110", section, usedForText, BrakeSystemDisplayName(current)));

            string noticeText = string.Empty;
            if (!isUsedByMethod) noticeText = Loc.F("Pm.111", BrakeSystemDisplayName(current), usedForText);
            if (isOldVersion) noticeText = Loc.F("Pm.112", oldVersionText, section);
            notice.Text = noticeText;
            notice.Visibility = canEdit && noticeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowWarnings(warnings, problems);
        }

        // ブレーキ方式（[Ecb]/[Smee]/[Cl] のどれか1つ）を出す
        // どれもなければ [Ecb] として扱う。2つ以上あれば方式は切り替えられない。その方式で書けない欄は灰色
        private void ShowBrakeSystem(List<BveTextLine> lines, bool canEdit)
        {
            // 書かれている方式（上から順。書かれているとおりの大文字小文字）
            List<string> writtenSections = FindWrittenSections(lines, BrakeSystemSections);
            bool isMultiple = writtenSections.Count > 1;
            _brakeSection = writtenSections.FirstOrDefault();

            // 選ばれている方式（どれもなければ既定の [Ecb]）
            string current = CurrentBrakeSystem;
            RdoBrakeEcb.IsChecked = canEdit && current == EcbSection;
            RdoBrakeSmee.IsChecked = canEdit && current == SmeeSection;
            RdoBrakeCl.IsChecked = canEdit && current == ClSection;
            foreach (RadioButton radio in new[] { RdoBrakeEcb, RdoBrakeSmee, RdoBrakeCl })
            {
                radio.IsEnabled = canEdit && !isMultiple;
            }

            string notice = string.Empty;
            if (canEdit && writtenSections.Count == 0) notice = Loc.T("Pm.113");
            if (isMultiple) notice = Loc.F("Pm.114", writtenSections.Count, string.Join(Loc.T("Pm2.004"), writtenSections.Select(section => $"[{section}]")), _brakeSection);
            TxtBrakeSystemNotice.Text = notice;
            TxtBrakeSystemNotice.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // その方式で書けない欄は灰色。書かれていれば警告
            List<string> problems = ShowNumberFields(lines, BrakeSystemGroup, _brakeSection, canEdit && !isMultiple,
                field => BrakeKeysOnlyFor.TryGetValue(field.Key, out string[]? allowed) && !allowed.Contains(current),
                field => Loc.F("Pm.115", field.Label, field.Key, BrakeSystemDisplayName(current), string.Join(Loc.T("Pm2.005"), BrakeKeysOnlyFor[field.Key].Select(BrakeSystemDisplayName))));
            ShowWarnings(TxtBrakeSystemWarnings, problems);
        }

        // 「どれか1つだけ書く」セクションのうち、書かれているものを上から順に返す（書かれているとおりの大文字小文字。同じものは1つ）
        /// <summary>
        /// パラメーターのテキストから、ブレーキ方式（Ecb / Smee / Cl）と画面に出す名前を読む（パネルの Subject のチェックで使う）
        /// どれも書かれていなければ BVE の既定の Ecb。テキストが空（読み込めていない）なら null
        /// </summary>
        internal static (string Section, string DisplayName)? ReadBrakeSystem(string paramText)
        {
            List<BveTextLine> lines = BveTextParser.Parse(paramText);
            if (lines.Count == 0) return null;

            string? written = FindWrittenSections(lines, BrakeSystemSections).FirstOrDefault();
            string section = BrakeSystemSections.First(name => string.Equals(name, written ?? EcbSection, StringComparison.OrdinalIgnoreCase));
            return (section, BrakeSystemDisplayName(section));
        }

        private static List<string> FindWrittenSections(List<BveTextLine> lines, string[] sectionNames)
        {
            return lines
                .Where(line => line.Kind == BveLineKind.Section && sectionNames.Contains(line.Section, StringComparer.OrdinalIgnoreCase))
                .Select(line => line.Section!)
                .DistinctBy(section => section.ToUpperInvariant())
                .ToList();
        }

        // 電空協調制御（[AirSupplement]/[LockoutValve] のどちらか1つ）を出す
        // どちらもなければ BVE の既定の [AirSupplement]。両方あれば方式は切り替えられない。制輪子の摩擦係数は遅れ込め制御式だけ
        private void ShowCoordination(List<BveTextLine> lines, bool canEdit)
        {
            List<string> writtenSections = FindWrittenSections(lines, CoordinationSections);
            bool isMultiple = writtenSections.Count > 1;
            _coordinationSection = writtenSections.FirstOrDefault();

            string current = CurrentCoordination;
            RdoAirSupplement.IsChecked = canEdit && current == AirSupplementSection;
            RdoLockoutValve.IsChecked = canEdit && current == LockoutValveSection;
            RdoAirSupplement.IsEnabled = canEdit && !isMultiple;
            RdoLockoutValve.IsEnabled = canEdit && !isMultiple;

            string notice = string.Empty;
            if (canEdit && writtenSections.Count == 0) notice = Loc.T("Pm.116");
            if (isMultiple) notice = Loc.F("Pm.117", string.Join(Loc.T("Pm2.006"), writtenSections.Select(section => $"[{section}]")), _coordinationSection);
            TxtCoordinationNotice.Text = notice;
            TxtCoordinationNotice.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // 制輪子の摩擦係数は遅れ込め制御式だけ。締切電磁弁式では灰色、書かれていれば警告
            List<string> problems = ShowNumberFields(lines, CoordinationGroup, _coordinationSection, canEdit && !isMultiple,
                field => field.Key == "ShoeFriction" && current != AirSupplementSection,
                field => Loc.F("Pm.118", field.Label, field.Key, CoordinationDisplayName(current)));
            ShowWarnings(TxtCoordinationWarnings, problems);
        }

        // 電空協調制御の方式を選んだとき：今の方式のセクション名を書き換える（どちらもなければ最後に足す）
        private void CoordinationRadio_Click(object sender, RoutedEventArgs e)
        {
            if (_editor == null) return;

            string newSection = sender == RdoLockoutValve ? LockoutValveSection : AirSupplementSection;
            if (string.Equals(_coordinationSection, newSection, StringComparison.OrdinalIgnoreCase)) return;
            // どちらも書かれていないときに既定の遅れ込め制御式を選んだだけなら、何も書かない
            if (_coordinationSection == null && newSection == AirSupplementSection) return;

            EditorTextHelper.ReplaceChangedText(_editor, BveTextParser.RenameSection(_editor.Text, _coordinationSection, newSection));
            OperationHistory.Add($"パラメーター入力: 電空協調制御 [{_coordinationSection ?? "なし"}] → [{newSection}]");
        }

        // ブレーキ方式を選んだとき：今の方式のセクション名を書き換える（どれもなければ最後に足す）
        private void BrakeSystemRadio_Click(object sender, RoutedEventArgs e)
        {
            if (_editor == null) return;

            string newSection = sender == RdoBrakeSmee ? SmeeSection : sender == RdoBrakeCl ? ClSection : EcbSection;
            if (string.Equals(_brakeSection, newSection, StringComparison.OrdinalIgnoreCase)) return;
            // どれも書かれていないときに既定の [Ecb] を選んだだけなら、何も書かない
            if (_brakeSection == null && newSection == EcbSection) return;

            EditorTextHelper.ReplaceChangedText(_editor, BveTextParser.RenameSection(_editor.Text, _brakeSection, newSection));
            OperationHistory.Add($"パラメーター入力: ブレーキ方式 [{_brakeSection ?? "なし"}] → [{newSection}]");
        }

        // 主制御装置を出す。書かれていなければ既定値、ヘッダーが古ければ使えない項目を灰色に、数字でなければ警告
        private void ShowMainCircuit(List<BveTextLine> lines, bool canEdit)
        {
            (decimal? version, string? versionText) = ReadHeaderVersion(lines);
            bool isOldVersion = version != null && version <= LastOldVersion;

            // 電気ブレーキ開始の最低速度の既定値は「電気ブレーキを切る速度 + 5」（切る速度が書かれていなければ既定の 5 を使う）
            string? limit = FindValue(lines, MainCircuitSection, "RegenerationLimit");
            double limitValue = TryReadNumber(limit, allowNegative: false, out double written) ? written : 5;
            _computedDefaults[$"{MainCircuitSection}:RegenerationStartLimit"] = (limitValue + 5).ToString(System.Globalization.CultureInfo.InvariantCulture);

            List<string> problems = ShowNumberFields(lines, MainCircuitSection, MainCircuitSection, canEdit,
                field => isOldVersion && KeysNotInOldVersion.Contains(field.Key),
                field => Loc.F("Pm.119", field.Label, field.Key, versionText));

            ChkBrakePriority.IsEnabled = canEdit;
            ShowBool(ChkBrakePriority, FindValue(lines, MainCircuitSection, "BrakePriority"), Loc.T("Pm.120"), $"{MainCircuitSection}:BrakePriority");

            TxtMainCircuitNotice.Text = Loc.F("Pm.121", versionText);
            TxtMainCircuitNotice.Visibility = isOldVersion ? Visibility.Visible : Visibility.Collapsed;
            ShowWarnings(TxtMainCircuitWarnings, problems);
        }

        // 主制御装置の再粘着制御を出す。セクションがあるときだけ「使う」にチェックして欄を使えるようにする（どのバージョンでも使える）
        private void ShowPowerReAdhesion(List<BveTextLine> lines, bool canEdit)
        {
            bool isWritten = BveTextParser.HasSection(lines, PowerReAdhesionSection);

            ChkPowerReAdhesionEnabled.IsChecked = isWritten;
            ChkPowerReAdhesionEnabled.IsEnabled = canEdit;

            // 欄はセクションがあるときだけ使える（ないときは灰色のまま既定値を見せる）
            List<string> problems = ShowNumberFields(lines, PowerReAdhesionSection, PowerReAdhesionSection, canEdit && isWritten, field => false, field => string.Empty);
            ShowWarnings(TxtPowerReAdhesionWarnings, problems);
        }

        // 数字の欄を出す。書かれていなければ既定値。使えない欄（isUnavailable）は灰色にして、書かれていれば unavailableMessage の警告
        // 数字でなければ警告（NegativeAllowedKeys 以外は 0 以上。NumberListKeys は , で区切った数字の並び）。警告の文を返す
        // fieldGroup：どの欄か（TextField.Section）。readSection：値を読むセクション（null なら書かれていないとみなす）
        private List<string> ShowNumberFields(List<BveTextLine> lines, string fieldGroup, string? readSection, bool canEdit,
            Func<TextField, bool> isUnavailable, Func<TextField, string> unavailableMessage)
        {
            List<string> problems = [];
            foreach (TextField field in _textFields.Where(field => field.Section == fieldGroup))
            {
                bool unavailable = isUnavailable(field);
                field.Box.IsEnabled = canEdit && !unavailable;

                string? value = readSection == null ? null : FindValue(lines, readSection, field.Key);
                if (!field.Box.IsKeyboardFocusWithin) ShowText(field.Box, value, PlaceholderOf(field));
                if (value == null) continue;

                if (unavailable)
                {
                    problems.Add(unavailableMessage(field));
                    continue;
                }

                // , で区切った数字の並び（PressureRates など）
                if (NumberListKeys.Contains(field.Key))
                {
                    string? badItem = value.Split(',').Select(item => item.Trim()).FirstOrDefault(item => !TryReadNumber(item, allowNegative: false, out _));
                    if (badItem != null) problems.Add(Loc.F("Pm.122", field.Label, badItem));
                    continue;
                }

                bool allowNegative = NegativeAllowedKeys.Contains(field.Key);
                if (TryReadNumber(value, allowNegative, out _)) continue;
                problems.Add(allowNegative
                    ? Loc.F("Pm.123", field.Label, value)
                    : Loc.F("Pm.124", field.Label, value));
            }
            return problems;
        }

        // 「再粘着制御を使う」をクリックしたとき：チェックしたらセクションを足す。外したら確認してからセクションごと消す
        private void ChkPowerReAdhesionEnabled_Click(object sender, RoutedEventArgs e)
        {
            ToggleOptionalSection(ChkPowerReAdhesionEnabled, PowerReAdhesionSection, Loc.T("Pm.125"));
        }

        // 「使う」チェックで足す・消すセクションの共通の処理（Ctrl+Z で戻せる）
        // チェックしたらセクションを足す。外したら、中の値の数と effect（消すとどうなるか）を見せて確認してから、セクションごと消す
        private void ToggleOptionalSection(CheckBox check, string section, string effect)
        {
            if (_editor == null) return;

            if (check.IsChecked == true)
            {
                EditorTextHelper.ReplaceChangedText(_editor, BveTextParser.RenameSection(_editor.Text, null, section));
                OperationHistory.Add($"パラメーター入力: [{section}] を足した");
                return;
            }

            int valueCount = BveTextParser.Parse(_editor.Text)
                .Count(line => line.Kind == BveLineKind.KeyValue && string.Equals(line.Section, section, StringComparison.OrdinalIgnoreCase));
            MessageBoxResult answer = MessageBox.Show(
                Loc.F("Pm.126", section, valueCount, effect),
                Loc.T("Pm.127"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                // チェックを元に戻す
                Refresh();
                return;
            }

            EditorTextHelper.ReplaceChangedText(_editor, BveTextParser.RemoveSection(_editor.Text, section));
            OperationHistory.Add($"パラメーター入力: [{section}] を消した（値 {valueCount} 件）");
        }

        // ヘッダー（1行目の「BveTs Vehicle Parameters 2.01」など）のバージョン番号を読む。読めなければ null
        private static (decimal? Version, string? Text) ReadHeaderVersion(List<BveTextLine> lines)
        {
            BveTextLine? header = lines.FirstOrDefault();
            if (header == null || header.Kind != BveLineKind.Other) return (null, null);

            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(header.Value, @"(\d+)\.(\d+)");
            if (!match.Success) return (null, null);
            return (decimal.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture), match.Value);
        }

        // 数字（小数も可）として読めるか。allowNegative が false ならマイナスはだめ
        private static bool TryReadNumber(string? value, bool allowNegative, out double number)
        {
            System.Globalization.NumberStyles styles = allowNegative
                ? System.Globalization.NumberStyles.Float
                : System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite;
            return double.TryParse(value, styles, System.Globalization.CultureInfo.InvariantCulture, out number);
        }

        // 欄の既定値の書き方（「セクション名:キー」で一覧を引く。運転台は「キー」だけ）
        private static string DefaultKeyOf(TextField field)
        {
            return field.Section == null ? field.Key : $"{field.Section}:{field.Key}";
        }

        // 書かれていない欄に出す文字（計算した既定値があればそちらを使う）
        private string PlaceholderOf(TextField field)
        {
            string defaultKey = DefaultKeyOf(field);
            if (_computedDefaults.TryGetValue(defaultKey, out string? computed)) return Loc.F("Pm.128", computed);
            return NotWrittenText(defaultKey);
        }

        // ブレーキ優先をクリックしたとき：[MainCircuit] の BrakePriority を true / false に書き換える
        private void ChkBrakePriority_Click(object sender, RoutedEventArgs e)
        {
            WriteValue(MainCircuitSection, "BrakePriority", ChkBrakePriority.IsChecked == true ? "true" : "false");
        }

        // 定速運転装置（0～3）を選ぶ欄に出す。書かれていなければ横に既定値、0～3 以外なら枠の上に警告
        private void ShowConstantSpeed(List<BveTextLine> lines, bool canEdit)
        {
            List<string> problems = [];
            foreach ((ComboBox combo, TextBlock defaultLabel, string key, string labelKey) in _constantSpeedFields)
            {
                string label = Loc.T(labelKey);
                combo.IsEnabled = canEdit;
                string? value = FindValue(lines, ConstantSpeedSection, key);
                bool isValid = int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) && index <= 3;
                combo.SelectedIndex = isValid ? index : -1;

                defaultLabel.Text = NotWrittenText($"{ConstantSpeedSection}:{key}");
                defaultLabel.Visibility = canEdit && value == null ? Visibility.Visible : Visibility.Collapsed;
                if (value != null && !isValid) problems.Add(Loc.F("Pm.129", label, key, value));
            }
            ShowWarnings(TxtConstantSpeedWarnings, problems);
        }

        // 警告の一覧を出す（なければ隠す）
        private static void ShowWarnings(TextBlock warningText, List<string> problems)
        {
            warningText.Text = string.Join("\n", problems.Select(problem => "⚠ " + problem));
            warningText.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 定速運転装置を選んだとき：[ConstantSpeedControl] の Power / Neutral / Brake を書き換える（0～3）
        private void ConstantSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshing || sender is not ComboBox combo || combo.Tag is not string key || combo.SelectedIndex < 0) return;
            WriteValue(ConstantSpeedSection, key, combo.SelectedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 運転台の数の決まりに合わないものを探す（どれも「以下」なら OK。ユーザー確認済み）
        /// ・ブレーキ / 力行 / 抑速のノッチ数 ≦ それぞれのテキストの数（超えると BVE で「インデックスが境界外」）
        /// ・ATS キャンセルノッチ・モーターブレーキノッチ ≦ ブレーキノッチ数
        /// ・ノッチ数などは 0 以上の整数
        /// 片方が書かれていなければ、BVE が使う既定値で比べる（ユーザー判断）。両方とも既定値なら比べない
        /// ノッチのテキストは、書かれていなければ BVE がノッチ数に合わせて作るので比べない
        /// </summary>
        private static List<string> FindCabProblems(List<BveTextLine> lines, string? cabSection)
        {
            List<string> problems = [];
            // 運転台のセクションがなければ、どれも書かれていない（既定値どうしなので比べない）
            if (cabSection == null) return problems;

            CountValue? brakeCount = ReadCount(lines, cabSection, "BrakeNotchCount", Loc.T("Pm.130"), problems);
            CountValue? powerCount = ReadCount(lines, cabSection, "PowerNotchCount", Loc.T("Pm.131"), problems);
            CountValue? holdingCount = ReadCount(lines, cabSection, "HoldingSpeedNotchCount", Loc.T("Pm.132"), problems);
            CountValue? atsCancel = ReadCount(lines, cabSection, "AtsCancelNotch", Loc.T("Pm.133"), problems);
            CountValue? motorBrake = ReadCount(lines, cabSection, "MotorBrakeNotch", Loc.T("Pm.134"), problems);

            CheckTextCount(brakeCount, FindValue(lines, cabSection, "BrakeText"), Loc.T("Pm.135"), Loc.T("Pm.136"), problems);
            CheckTextCount(powerCount, FindValue(lines, cabSection, "PowerText"), Loc.T("Pm.137"), Loc.T("Pm.138"), problems);
            CheckTextCount(holdingCount, FindValue(lines, cabSection, "HoldingSpeedText"), Loc.T("Pm.139"), Loc.T("Pm.140"), problems);

            CheckNotOverBrakeCount(atsCancel, brakeCount, Loc.T("Pm.141"), problems);
            CheckNotOverBrakeCount(motorBrake, brakeCount, Loc.T("Pm.142"), problems);

            // レバーサーのテキストは、最後に「,」があるとエラーにはならないが文字が壊れる（ユーザーが BVE で確認。Plan.md の # その他）
            // ノッチのテキストは「…,非常,」のように最後に「,」を付けても壊れないので、レバーサーだけ調べる
            string? reverserText = FindValue(lines, cabSection, "ReverserText");
            if (reverserText != null && reverserText.TrimEnd().EndsWith(',')) problems.Add(Loc.F("Pm.143", reverserText));
            return problems;
        }

        /// <summary>
        /// 比べるための数
        /// </summary>
        /// <param name="Value">数</param>
        /// <param name="IsDefault">書かれていないので既定値を使ったか</param>
        private readonly record struct CountValue(double Value, bool IsDefault);

        // 警告の文に出す「名前（値）」。既定値なら「名前（書かれていないので既定の 8）」
        private static string DescribeCount(string label, CountValue count)
        {
            string value = FormatNumber(count.Value);
            return count.IsDefault ? Loc.F("Pm.144", label, value) : $"{label}（{value}）";
        }

        // ノッチ数などを整数で読む。書かれていなければ既定値。0 以上の整数でなければ problems に足して null（比べない）
        private static CountValue? ReadCount(List<BveTextLine> lines, string section, string key, string label, List<string> problems)
        {
            string? value = FindValue(lines, section, key);
            if (value == null) return new(double.Parse(DefaultValues[key], System.Globalization.CultureInfo.InvariantCulture), IsDefault: true);
            if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int count)) return new(count, IsDefault: false);

            problems.Add(Loc.F("Pm.145", label, value));
            return null;
        }

        // ノッチ数が、テキストの数（, で区切った数）以下か。テキストが書かれていなければ比べない
        private static void CheckTextCount(CountValue? notchCount, string? text, string countLabel, string textLabel, List<string> problems)
        {
            if (notchCount == null || text == null) return;

            int textCount = CountTextItems(text);
            if (notchCount.Value.Value <= textCount) return;
            problems.Add(Loc.F("Pm.146", DescribeCount(countLabel, notchCount.Value), textLabel, textCount));
        }

        // ATS キャンセルノッチ・モーターブレーキノッチが、ブレーキノッチ数以下か。両方とも既定値なら比べない
        private static void CheckNotOverBrakeCount(CountValue? notch, CountValue? brakeCount, string label, List<string> problems)
        {
            if (notch == null || brakeCount == null) return;
            if (notch.Value.IsDefault && brakeCount.Value.IsDefault) return;
            if (notch.Value.Value <= brakeCount.Value.Value) return;
            problems.Add(Loc.F("Pm.147", DescribeCount(label, notch.Value), DescribeCount(Loc.T("Pm2.007"), brakeCount.Value)));
        }

        // テキストの数を数える（「切,1,2」なら 3 個）。最後の「,」の後ろの空っぽは数えない（「…,非常,」も同じ数）
        private static int CountTextItems(string text)
        {
            string trimmed = text.TrimEnd(',', ' ', '\t');
            return trimmed.Length == 0 ? 0 : trimmed.Split(',').Length;
        }

        // 先頭車を選んだとき：FirstCar の行を書き換える
        private void CmbFirstCar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshing) return;

            string? value = CmbFirstCar.SelectedIndex switch
            {
                0 => "M",
                1 => "T",
                _ => null,
            };
            if (value == null) return;
            WriteValue(null, "FirstCar", value);
        }

        // 応荷重制御をクリックしたとき：LoadCompensating の行を true / false に書き換える
        private void ChkLoadCompensating_Click(object sender, RoutedEventArgs e)
        {
            WriteValue(null, "LoadCompensating", ChkLoadCompensating.IsChecked == true ? "true" : "false");
        }

        // ハンドルを選んだとき：[Cab] / [OneLeverCab] のセクション名を書き換える（なければ最後に足す）
        private void CabRadio_Click(object sender, RoutedEventArgs e)
        {
            if (_editor == null) return;

            string newSection = sender == RdoCab ? CabSection : OneLeverCabSection;
            if (_cabSection == newSection) return;

            string newText = BveTextParser.RenameSection(_editor.Text, _cabSection, newSection);
            EditorTextHelper.ReplaceChangedText(_editor, newText);
            OperationHistory.Add($"パラメーター入力: ハンドル [{_cabSection ?? "なし"}] → [{newSection}]");
        }

        // 抑速ブレーキをクリックしたとき：HoldingSpeedBrake の行を true / false に書き換える
        // （運転台のセクションがなければ、既定の [Cab] ごと足して書き込む）
        private void ChkHoldingSpeedBrake_Click(object sender, RoutedEventArgs e)
        {
            WriteValue(CabSectionToWrite, "HoldingSpeedBrake", ChkHoldingSpeedBrake.IsChecked == true ? "true" : "false");
        }

        // 欄に入ったとき：今の値を覚え、「（未記入）」なら消して打てるようにする
        // （ShowText・IsShowingPlaceholder・RestoreEmptyBox は FormFieldHelper.cs にある、パネルの入力フォームと共通の処理）
        private void BeginEdit(TextBox box)
        {
            string text = box.Text;
            bool isNotWritten = ClearPlaceholder(box);
            _valuesOnFocus[box] = isNotWritten ? null : text;
        }

        // 欄から出たとき・Enter：変わっていれば下のテキストへ書き込む
        // （運転台の欄で運転台のセクションがなければ、既定の [Cab] ごと足して書き込む）
        // isLeaving：欄から出たとき true、Enter のとき false（欄に入ったまま）
        private void CommitText(TextField field, bool isLeaving)
        {
            TextBox box = field.Box;
            if (_isRefreshing || !_valuesOnFocus.TryGetValue(box, out string? original)) return;
            // 「（既定: ○○）」を出しているだけなら、何も打っていないので書き込まない
            if (IsShowingPlaceholder(box)) return;

            string value = box.Text.Trim();

            // 空にしたときは書き込まない（行は消さない）。元の表示に戻す
            if (value.Length == 0)
            {
                RestoreEmptyBox(box, original, PlaceholderOf(field), isLeaving);
                return;
            }
            if (value == original) return;

            // ; と # はコメントの始まりなので、値に入れると BVE がそこから後ろを読まない
            if (BveTextParser.FindCommentIndex(value) >= 0)
            {
                MessageBox.Show(Loc.T("Pm.148"), Loc.T("Pm.149"), MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowText(box, original, PlaceholderOf(field));
                return;
            }

            WriteValue(SectionToWrite(field), field.Key, value);
            _valuesOnFocus[box] = value;
        }

        // Esc：欄に入ったときの値に戻す
        private void CancelEdit(TextField field)
        {
            if (!_valuesOnFocus.TryGetValue(field.Box, out string? original)) return;
            // 書かれていなかった欄なら空に戻す（欄に入ったままなので「（既定: ○○）」は出さない）
            RestoreEmptyBox(field.Box, original, PlaceholderOf(field), isLeaving: false);
        }

        // 下のテキストの「キー = 値」を書き換える（なければ足す）。変わった部分だけ置き換えるので Ctrl+Z で戻せる
        // 書き換えるとテキストが変わり、少しあとにフォームも表示し直される
        private void WriteValue(string? section, string key, string value)
        {
            if (_editor == null) return;

            string newText = BveTextParser.SetValue(_editor.Text, section, key, value);
            EditorTextHelper.ReplaceChangedText(_editor, newText);
            OperationHistory.Add($"パラメーター入力: {(section == null ? string.Empty : $"[{section}] ")}{key} = {value}");
        }

        // 運転台のセクションを決める。両方あれば上にあるほう。どちらもなければ null
        // 両方ある・どちらもないときは、知らせる文を出す
        private (string? Section, bool IsBothWritten) FindCabSection(List<BveTextLine> lines)
        {
            List<string> cabSections = lines
                .Where(line => line.Kind == BveLineKind.Section
                    && (string.Equals(line.Section, CabSection, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(line.Section, OneLeverCabSection, StringComparison.OrdinalIgnoreCase)))
                .Select(line => string.Equals(line.Section, CabSection, StringComparison.OrdinalIgnoreCase) ? CabSection : OneLeverCabSection)
                .Distinct()
                .ToList();

            TxtCabNotice.Visibility = Visibility.Collapsed;
            if (cabSections.Count == 1) return (cabSections[0], false);

            if (cabSections.Count == 0)
            {
                // 何も読み込んでいない（テキストが空）ときは知らせない
                if (lines.Count > 0) ShowCabNotice(Loc.T("Pm.150"));
                return (null, false);
            }

            ShowCabNotice(Loc.F("Pm.151", cabSections[0]));
            return (cabSections[0], true);
        }

        private void ShowCabNotice(string message)
        {
            TxtCabNotice.Text = message;
            TxtCabNotice.Visibility = Visibility.Visible;
        }

        // セクション（null ならファイルの先頭）の中から、キーの値を探す。同じキーが何回もあれば最後のもの
        private static string? FindValue(List<BveTextLine> lines, string? section, string key)
        {
            return lines.LastOrDefault(line => line.Kind == BveLineKind.KeyValue
                && string.Equals(line.Section, section, StringComparison.OrdinalIgnoreCase)
                && string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
        }

        // 運転台の中のキーの値を探す。運転台のセクションがどちらもなければ、書かれていないとみなす
        // （セクションの前に同じキーが書かれていても、運転台の値ではないので使わない）
        private static string? FindCabValue(List<BveTextLine> lines, string? cabSection, string key)
        {
            return cabSection == null ? null : FindValue(lines, cabSection, key);
        }


        // true / false の値をチェックボックスに出す。書かれていない・読めない値なら「（既定: ○○）」（チェックは中間の印）
        private static void ShowBool(CheckBox box, string? value, string label, string key)
        {
            bool? isChecked = value?.ToLowerInvariant() switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            };
            box.IsChecked = isChecked;
            box.Content = isChecked == null ? $"{label} {NotWrittenText(key)}" : label;
        }
    }
}
