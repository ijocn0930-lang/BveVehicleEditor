using System.Windows;
using System.Windows.Media;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace BveVehicleEditor
{
    /// <summary>
    /// 速度ごとの値（引張力・電流）の折れ線グラフを作る共通の部品（部品は OxyPlot。2026-11-22 に OxyPlot に決めた。部品を変えるときは、ここを書き直す）
    /// 表のウィンドウの「グラフ」のタブと、「引張力を式から作る」ウィンドウのプレビューで使う
    /// </summary>
    internal static class ChartBuilder
    {
        // 線の色（隣り合うノッチを見分けやすい 10 色。11 本目からは同じ色を破線・点線で描き分ける）
        private static readonly OxyColor[] LineColors =
        [
            OxyColor.FromRgb(0x1F, 0x77, 0xB4), OxyColor.FromRgb(0xFF, 0x7F, 0x0E), OxyColor.FromRgb(0x2C, 0xA0, 0x2C),
            OxyColor.FromRgb(0xD6, 0x27, 0x28), OxyColor.FromRgb(0x94, 0x67, 0xBD), OxyColor.FromRgb(0x8C, 0x56, 0x4B),
            OxyColor.FromRgb(0xE3, 0x77, 0xC2), OxyColor.FromRgb(0x7F, 0x7F, 0x7F), OxyColor.FromRgb(0xBC, 0xBD, 0x22),
            OxyColor.FromRgb(0x17, 0xBE, 0xCF),
        ];

        // 11 本目から使う線の形（色が一回りするごとに変える）
        private static readonly LineStyle[] LineStyles = [LineStyle.Solid, LineStyle.Dash, LineStyle.Dot, LineStyle.DashDot];

        /// <summary>
        /// 軸と凡例だけの空のグラフを作る（横は速度 [km/h]、縦は valueName）。色は今のテーマ（ライト・ダーク）に合わせる
        /// </summary>
        internal static PlotModel CreateModel(string? valueName, FrameworkElement themeSource)
        {
            bool dark = IsDarkTheme(themeSource);
            OxyColor text = dark ? OxyColor.FromRgb(0xE0, 0xE0, 0xE0) : OxyColor.FromRgb(0x1A, 0x1A, 0x1A);
            OxyColor grid = dark ? OxyColor.FromRgb(0x44, 0x44, 0x44) : OxyColor.FromRgb(0xE0, 0xE0, 0xE0);

            var model = new PlotModel { TextColor = text, PlotAreaBorderColor = grid, Background = OxyColors.Transparent };
            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom, Title = Loc.T("Ch.001"), Unit = "km/h",
                MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = grid, AxislineColor = text, TicklineColor = text,
            });
            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left, Title = valueName,
                MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = grid, AxislineColor = text, TicklineColor = text,
            });
            model.Legends.Add(new Legend
            {
                LegendPosition = LegendPosition.RightTop, LegendPlacement = LegendPlacement.Outside,
                LegendTextColor = text, LegendBackground = OxyColors.Transparent, LegendBorder = OxyColors.Transparent,
            });
            return model;
        }

        /// <summary>
        /// 線を 1 本足す（index は 0 から。色と線の形を決めるのに使う）。点がなければ足さずに false
        /// </summary>
        internal static bool AddLine(PlotModel model, string title, int index, IEnumerable<DataPoint> points)
        {
            LineSeries line = new()
            {
                Title = title,
                Color = LineColors[index % LineColors.Length],
                LineStyle = LineStyles[index / LineColors.Length % LineStyles.Length],
                StrokeThickness = 1.5,
                TrackerFormatString = Loc.T("Ch.002"),
            };
            line.Points.AddRange(points);
            if (line.Points.Count == 0) return false;
            model.Series.Add(line);
            return true;
        }

        // とても大きい値とみなす、中央値に対する倍率と、そのとき軸の上限に使う値の範囲（中央値の何倍までか）
        private const double OutlierRatio = 20;
        private const double AxisRangeRatio = 10;

        /// <summary>
        /// 縦の軸の上限を、とても大きい値を除いて決める（使わない速度に 3.8 × 10¹⁸ などを入れた抵抗制御の表で、軸が伸びて線がつぶれた。ユーザーが見つけた）
        /// 値の中央値の 20 倍より大きい値があるときだけ、上限を「中央値の 10 倍までの値の最大 × 1.1」にし、軸の名前に書き足す。なければ何もしない（ふつうの表は変わらない）
        /// サンプル：南海 6000 の引張力 中央値 15,152・最大 3.8 × 10¹⁸ → 上限 約 167,000。ふつうの表は中央値の 10 倍より大きい値がなかった
        /// </summary>
        /// <returns>上限を決めたか</returns>
        internal static bool LimitValueAxis(PlotModel model, IEnumerable<double> values)
        {
            double[] positive = [.. values.Where(value => value > 0 && double.IsFinite(value)).Order()];
            if (positive.Length == 0 || model.Axes.Count < 2) return false;
            double median = positive[positive.Length / 2];
            if (positive[^1] <= OutlierRatio * median) return false;

            double top = positive.Where(value => value <= AxisRangeRatio * median).DefaultIfEmpty(median).Max();
            Axis axis = model.Axes[1];
            axis.Maximum = top * 1.1;
            axis.Title = Loc.F("Ch.003", axis.Title);
            return true;
        }

        // 凡例にいつも出す線の印（目安の線・目立たせる線）。印のない線（ノッチ・段ごとの線）は、多すぎると凡例に出さない
        private const string KeepInLegend = "凡例に出す";

        // 凡例をグラフの下に横に並べる、グラフの幅（これより狭いと右に縦に並べるとグラフが狭くなる）
        private const double NarrowWidth = 700;

        // 凡例に出すノッチ・段ごとの線の数の上限（広いとき・狭いとき）
        private const int MaxLegendLinesWide = 20;
        private const int MaxLegendLinesNarrow = 8;

        // 大きさが変わったら凡例を並べ直すようにしたグラフ（同じグラフに何度もつながないように覚える）
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OxyPlot.Wpf.PlotView, object> _attached = [];

        /// <summary>
        /// グラフを置き、凡例の並べ方を幅と線の数に合わせる（ApplyLegendLayout）。大きさが変わったときも並べ直す
        /// 南海 6000 の 54 段を幅 480 で開くと、右の凡例が場所を取りすぎてグラフがとても狭くなった（ユーザーが見つけた）
        /// </summary>
        internal static void Show(OxyPlot.Wpf.PlotView view, PlotModel model)
        {
            view.Model = model;
            if (!_attached.TryGetValue(view, out _))
            {
                _attached.Add(view, new object());
                view.SizeChanged += (s, e) => ApplyLegendLayout(view);
            }
            ApplyLegendLayout(view);
        }

        /// <summary>
        /// 凡例の並べ方：幅が狭いときはグラフの下に横に、広いときは右に縦に並べる
        /// ノッチ・段ごとの線が上限より多ければ凡例に出さず（目安の線・目立たせる線だけ出す）、グラフの上にそのことを書く
        /// </summary>
        internal static void ApplyLegendLayout(OxyPlot.Wpf.PlotView view)
        {
            PlotModel? model = view.Model;
            if (model == null || model.Legends.Count == 0 || model.Legends[0] is not Legend legend) return;

            bool narrow = view.ActualWidth > 0 && view.ActualWidth < NarrowWidth;
            legend.LegendPosition = narrow ? LegendPosition.BottomCenter : LegendPosition.RightTop;
            legend.LegendOrientation = narrow ? LegendOrientation.Horizontal : LegendOrientation.Vertical;

            List<LineSeries> plain = [.. model.Series.OfType<LineSeries>().Where(line => !Equals(line.Tag, KeepInLegend))];
            bool tooMany = plain.Count > (narrow ? MaxLegendLinesNarrow : MaxLegendLinesWide);
            foreach (LineSeries line in plain) line.RenderInLegend = !tooMany;
            model.Subtitle = tooMany ? Loc.F("Ch.005", plain.Count) : null;
            model.SubtitleFontSize = 11;
            model.InvalidatePlot(false);
        }

        /// <summary>
        /// 目安の線（走行抵抗など）を灰色の点線で 1 本足す。ノッチの線と見分けやすいように色と形を変える
        /// </summary>
        internal static void AddReferenceLine(PlotModel model, string title, IEnumerable<DataPoint> points)
        {
            LineSeries line = new()
            {
                Title = title,
                Tag = KeepInLegend,
                Color = OxyColor.FromRgb(0x90, 0x90, 0x90),
                LineStyle = LineStyle.Dot,
                StrokeThickness = 2,
                TrackerFormatString = Loc.T("Ch.006"),
            };
            line.Points.AddRange(points);
            model.Series.Add(line);
        }

        /// <summary>
        /// 目立たせる線（全ノッチで走ったときの線など）を太く 1 本足す。色は文字の色（ライトは黒、ダークは白）、dashed なら破線
        /// </summary>
        internal static void AddEmphasisLine(PlotModel model, string title, IEnumerable<DataPoint> points, FrameworkElement themeSource, bool dashed = false)
        {
            bool dark = IsDarkTheme(themeSource);
            LineSeries line = new()
            {
                Title = title,
                Tag = KeepInLegend,
                Color = dark ? OxyColor.FromRgb(0xF0, 0xF0, 0xF0) : OxyColor.FromRgb(0x10, 0x10, 0x10),
                LineStyle = dashed ? LineStyle.Dash : LineStyle.Solid,
                StrokeThickness = 3,
                TrackerFormatString = Loc.T("Ch.007"),
            };
            line.Points.AddRange(points);
            model.Series.Add(line);
        }

        /// <summary>
        /// マウスの操作：乗せただけで近い点の値を出す（左クリックは要らない）。左ダブルクリックで元の大きさに戻す
        /// ホイールで拡大・縮小、右ボタンのドラッグで移動は OxyPlot の標準のまま
        /// </summary>
        internal static PlotController CreateController()
        {
            var controller = new PlotController();
            controller.UnbindMouseDown(OxyMouseButton.Left);
            controller.BindMouseEnter(PlotCommands.HoverSnapTrack);
            controller.BindMouseDown(OxyMouseButton.Left, OxyModifierKeys.None, 2, PlotCommands.ResetAt);
            return controller;
        }

        /// <summary>
        /// マウスを乗せたときの吹き出しと縦横の線の色を、今のテーマ（ライト・ダーク）に合わせる
        /// OxyPlot の標準の吹き出しは背景が薄い黄色のままで、文字の色だけアプリのダークの明るい色を引き継ぐため、ダークでは読めなかった（ユーザーが見つけた）
        /// </summary>
        internal static void ApplyTrackerStyle(OxyPlot.Wpf.PlotView view)
        {
            bool dark = IsDarkTheme(view);
            Brush background = new SolidColorBrush(dark ? Color.FromRgb(0x2D, 0x2D, 0x2D) : Color.FromRgb(0xFF, 0xFF, 0xFF));
            Brush border = new SolidColorBrush(dark ? Color.FromRgb(0x6A, 0x6A, 0x6A) : Color.FromRgb(0xB0, 0xB0, 0xB0));
            Brush text = new SolidColorBrush(dark ? Color.FromRgb(0xF0, 0xF0, 0xF0) : Color.FromRgb(0x1A, 0x1A, 0x1A));
            Brush line = new SolidColorBrush(dark ? Color.FromRgb(0xA0, 0xA0, 0xA0) : Color.FromRgb(0x70, 0x70, 0x70));

            // 吹き出し（TrackerControl）の中に、点の説明の文字を 1 つ置く形（OxyPlot の標準の形と同じ並び）
            var tracker = new FrameworkElementFactory(typeof(OxyPlot.Wpf.TrackerControl));
            tracker.SetBinding(OxyPlot.Wpf.TrackerControl.PositionProperty, new System.Windows.Data.Binding("Position"));
            tracker.SetBinding(OxyPlot.Wpf.TrackerControl.LineExtentsProperty, new System.Windows.Data.Binding("PlotModel.PlotArea"));
            tracker.SetValue(System.Windows.Controls.Control.BackgroundProperty, background);
            tracker.SetValue(System.Windows.Controls.Control.BorderBrushProperty, border);
            tracker.SetValue(System.Windows.Controls.Control.ForegroundProperty, text);
            tracker.SetValue(OxyPlot.Wpf.TrackerControl.LineStrokeProperty, line);

            var label = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            label.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding());
            label.SetValue(FrameworkElement.MarginProperty, new Thickness(7));
            label.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, text);
            // 文字の大きさが変わったら、吹き出しの枠を決め直させる（OxyPlot は出す位置が変わったときしか枠を決め直さず、
            // 短い名前の線のあとに長い名前の線へ吹き出しを出すと、文字が枠からはみ出した。ユーザーが見つけた）
            label.AddHandler(FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(RefreshTrackerBorder));
            tracker.AppendChild(label);

            view.DefaultTrackerTemplate = new System.Windows.Controls.ControlTemplate { VisualTree = tracker };
        }

        // 吹き出しの文字の大きさが変わったとき：外側の TrackerControl の出す位置を一度ずらして戻し、今の文字の大きさで枠を描き直させる
        private static void RefreshTrackerBorder(object sender, SizeChangedEventArgs e)
        {
            DependencyObject? node = sender as DependencyObject;
            while (node != null && node is not OxyPlot.Wpf.TrackerControl) node = VisualTreeHelper.GetParent(node);
            if (node is not OxyPlot.Wpf.TrackerControl tracker) return;
            // SetCurrentValue で置き直す（ふつうに代入すると、出す位置のバインディングが消えて、吹き出しが前の点から動かなくなった）
            ScreenPoint position = tracker.Position;
            tracker.SetCurrentValue(OxyPlot.Wpf.TrackerControl.PositionProperty, new ScreenPoint(position.X + 0.5, position.Y));
            tracker.SetCurrentValue(OxyPlot.Wpf.TrackerControl.PositionProperty, position);
        }

        /// <summary>
        /// 今のテーマがダークか（文字の色が明るければダーク。システムに合わせているときも、今の見た目で決まる）
        /// </summary>
        internal static bool IsDarkTheme(FrameworkElement source)
        {
            if (source.TryFindResource("TextFillColorPrimaryBrush") is not SolidColorBrush brush) return false;
            Color c = brush.Color;
            return (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) > 128;
        }
    }
}
