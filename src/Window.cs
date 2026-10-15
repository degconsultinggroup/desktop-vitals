// Glass window: one pane that draws a Board.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace DesktopVitals
{
    static class Theme
    {
        public static readonly Brush Text = Solid(0xFF, 0xF3, 0xF5, 0xF8);
        public static readonly Brush Sub = Solid(0x9E, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Faint = Solid(0x5C, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Card = Gradient(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), false);
        public static readonly Brush Sheen = Gradient(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), false);
        public static readonly Brush Hover = Solid(0x1A, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Track = Solid(0x1C, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Green = Solid(0xFF, 0x34, 0xD3, 0x6A);
        public static readonly Brush Blue = Gradient(Color.FromRgb(0x5E, 0x9B, 0xFF), Color.FromRgb(0x6F, 0xE0, 0xFF), true);
        public static readonly Brush Amber = Gradient(Color.FromRgb(0xFF, 0x9F, 0x2E), Color.FromRgb(0xFF, 0xCF, 0x5C), true);
        public static readonly Brush Red = Gradient(Color.FromRgb(0xFF, 0x45, 0x5A), Color.FromRgb(0xFF, 0x7E, 0x6B), true);
        public static readonly Brush BlueV = Gradient(Color.FromRgb(0x6F, 0xE0, 0xFF), Color.FromRgb(0x5E, 0x9B, 0xFF), false);
        public static readonly Brush AmberV = Gradient(Color.FromRgb(0xFF, 0xCF, 0x5C), Color.FromRgb(0xFF, 0x9F, 0x2E), false);
        public static readonly Brush RedV = Gradient(Color.FromRgb(0xFF, 0x7E, 0x6B), Color.FromRgb(0xFF, 0x45, 0x5A), false);
        public static readonly Brush Link = Solid(0xFF, 0x6F, 0xC8, 0xFF);
        public static readonly Brush AmberDot = Solid(0xFF, 0xFF, 0xB5, 0x47);
        public static readonly Brush FailedDot = Solid(0xFF, 0xFF, 0x5C, 0x5C);
        public static readonly Brush Off = Solid(0x33, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Edge = Rim();
        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI");

        // Light catching a glass edge: bright at the top-left, gone along the sides, a faint glint at the bottom-right.
        static Brush Rim()
        {
            var br = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            br.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 0));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), 0.28));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF), 0.62));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF), 1));
            br.Freeze();
            return br;
        }

        static Brush Solid(byte a, byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b)); br.Freeze(); return br;
        }

        static Brush Gradient(Color a, Color b, bool horizontal)
        {
            var br = new LinearGradientBrush(a, b, horizontal ? new Point(0, 0) : new Point(0, 0), horizontal ? new Point(1, 0) : new Point(0, 1)); br.Freeze(); return br;
        }

        public static TextBlock Label(string s, double size, Brush color, FontWeight weight)
        {
            var t = new TextBlock { Text = s, FontSize = size, Foreground = color, FontWeight = weight, FontFamily = Font, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            return t;
        }
    }

    // One thin column per processor thread, grouped by core: performance cores first, then efficiency cores.
    class ThreadStrip : StackPanel
    {
        readonly Dictionary<int, ScaleTransform> fills = new Dictionary<int, ScaleTransform>();
        readonly Dictionary<int, Border> bars = new Dictionary<int, Border>();
        readonly Dictionary<int, string> names = new Dictionary<int, string>();
        readonly TextBlock busiestLine;

        public ThreadStrip(List<KeyValuePair<int, int[]>> cores)
        {
            Margin = new Thickness(0, 2, 0, 6);
            busiestLine = Theme.Label("", 11.5, Theme.Faint, FontWeights.Normal);
            busiestLine.Margin = new Thickness(0, 4, 0, 0);
            if (cores == null) cores = new List<KeyValuePair<int, int[]>>();
            int top = cores.Count == 0 ? 0 : cores.Max(c => c.Key);
            bool hybrid = cores.Any(c => c.Key != top);
            int nThreads = cores.Sum(c => c.Value.Length);
            if (nThreads == 0)
            {
                Visibility = Visibility.Collapsed;
                return;
            }
            int pThreads = cores.Where(c => c.Key == top).Sum(c => c.Value.Length);
            int eThreads = cores.Where(c => c.Key != top).Sum(c => c.Value.Length);
            string text = nThreads.ToString(CultureInfo.InvariantCulture) + " threads";
            if (hybrid) text += "  \u00B7  " + pThreads.ToString(CultureInfo.InvariantCulture) + " performance, " + eThreads.ToString(CultureInfo.InvariantCulture) + " efficiency";
            var caption = Theme.Label(text, 11.5, Theme.Faint, FontWeights.Normal);
            caption.Margin = new Thickness(0, 0, 0, 5);
            Children.Add(caption);

            var row = new Grid { Height = 36 };
            int col = 0, prevEff = -1, coreNo = 0;
            foreach (var c in cores)
            {
                double gapBefore = col == 0 ? 0 : c.Key != prevEff ? 10 : 4;
                if (c.Key != prevEff) coreNo = 0;
                coreNo++;
                prevEff = c.Key;
                for (int t = 0; t < c.Value.Length; t++)
                {
                    int cpu = c.Value[t];
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var cell = new Grid { Margin = new Thickness(t == 0 ? gapBefore : 1.5, 0, 0, 0), Background = Brushes.Transparent };
                    cell.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Theme.Track });
                    var sc = new ScaleTransform(1, 0);
                    var f = new Border { CornerRadius = new CornerRadius(2), Background = Theme.BlueV, RenderTransform = sc, RenderTransformOrigin = new Point(0.5, 1) };
                    cell.Children.Add(f);
                    names[cpu] = "Thread " + cpu.ToString(CultureInfo.InvariantCulture) + "  \u00B7  " + (!hybrid ? "core " : c.Key == top ? "performance core " : "efficiency core ") + coreNo.ToString(CultureInfo.InvariantCulture);
                    cell.ToolTip = names[cpu];
                    Grid.SetColumn(cell, col++);
                    row.Children.Add(cell);
                    fills[cpu] = sc;
                    bars[cpu] = f;
                }
            }
            Children.Add(row);
            Children.Add(busiestLine);
        }

        public void SetBusiest(string line)
        {
            if (busiestLine == null) return;
            if (line == null) line = "";
            if (busiestLine.Text != line) busiestLine.Text = line;
        }

        public void Set(int cpu, double frac, bool instant)
        {
            ScaleTransform sc;
            if (!fills.TryGetValue(cpu, out sc)) return;
            if (double.IsNaN(frac)) frac = 0;
            frac = Math.Max(0, Math.Min(1, frac));
            Brush b = frac >= 0.9 ? Theme.RedV : frac >= 0.75 ? Theme.AmberV : Theme.BlueV;
            if (bars[cpu].Background != b) bars[cpu].Background = b;
            string tip = names[cpu] + "  \u00B7  " + BoardText.BarPct(frac * 100);
            ((FrameworkElement)bars[cpu].Parent).ToolTip = tip;
            if (instant)
            {
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                sc.ScaleY = frac;
                return;
            }
            var a = new DoubleAnimation(frac, TimeSpan.FromMilliseconds(500));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    // A thin bar that eases to its new length.
    class Bar : Grid
    {
        readonly Border fill;
        readonly ScaleTransform scale = new ScaleTransform(0, 1);
        double current = -1;

        public Bar()
        {
            Height = 5;
            var track = new Border();
            track.CornerRadius = new CornerRadius(2.5);
            track.Background = Theme.Track;
            fill = new Border();
            fill.CornerRadius = new CornerRadius(2.5);
            fill.Background = Theme.Blue;
            fill.RenderTransform = scale;
            fill.RenderTransformOrigin = new Point(0, 0.5);
            Children.Add(track);
            Children.Add(fill);
        }

        public void Set(double frac, bool instant)
        {
            if (double.IsNaN(frac)) frac = 0;
            frac = Math.Max(0, Math.Min(1, frac));
            Brush b = frac >= 0.9 ? Theme.Red : frac >= 0.75 ? Theme.Amber : Theme.Blue;
            if (fill.Background != b) fill.Background = b;
            if (!instant && Math.Abs(frac - current) < 0.003) return;
            current = frac;
            if (instant)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.ScaleX = frac;
                return;
            }
            var a = new DoubleAnimation(frac, TimeSpan.FromMilliseconds(500));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        }
    }

    // Label, detail, value, then a bar.
    class StatRow : StackPanel
    {
        readonly TextBlock detail, value;
        readonly Bar bar = new Bar();

        public StatRow(string label)
        {
            Margin = new Thickness(0, 6, 0, 6);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = Theme.Label(label, 13, Theme.Text, FontWeights.SemiBold);
            detail = Theme.Label("", 12, Theme.Sub, FontWeights.Normal);
            detail.Margin = new Thickness(8, 1, 8, 0);
            value = Theme.Label("", 13, Theme.Text, FontWeights.SemiBold);
            Grid.SetColumn(detail, 1);
            Grid.SetColumn(value, 2);
            g.Children.Add(l);
            g.Children.Add(detail);
            g.Children.Add(value);
            bar.Margin = new Thickness(0, 7, 0, 0);
            Children.Add(g);
            Children.Add(bar);
        }

        public void Set(string d, string v, double frac, bool instant)
        {
            if (d == null) d = "";
            if (v == null) v = "";
            if (detail.Text != d) detail.Text = d;
            if (value.Text != v) value.Text = v;
            bar.Set(frac, instant);
        }
    }
    class VitalsWindow : Window
    {
        static readonly string StateDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopVitals");
        static readonly string[] Editor = FindEditor();
        readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background);
        readonly Dictionary<string, string> seen = new Dictionary<string, string>();
        readonly Dictionary<string, string> knownNames = new Dictionary<string, string>();
        readonly HashSet<string> expanded = new HashSet<string>();
        readonly List<Entry> lines = new List<Entry>();
        readonly string snapshotDir;
        bool live = true;
        bool fixtureMode;
        Board fixtureBoard;
        Board lastBoard;
        bool naming;
        string threadSig;
        string listSig;
        TextBlock nameText, headlineText, stateLabel, nameMark;
        StatRow cpuRow, memRow, gpuRow, vramRow, diskRow;
        ThreadStrip threadStrip;
        TextBox nameEdit, rowEdit;
        StackPanel nameHost, nameLine, system;
        Grid list;
        Border switchTrack, pane, gearBtn;
        TranslateTransform knob;
        ScrollViewer scroll;
        Popup menu;
        public VitalsWindow() : this(null) { }
        public void ShowFixture(Board board)
        {
            fixtureMode = true;
            fixtureBoard = board ?? new Board();
            Engine.Current = fixtureBoard;
        }
        public VitalsWindow(string snapshotDir)
        {
            this.snapshotDir = snapshotDir;
            Title = "Desktop Vitals";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            MaxHeight = snapshotDir == null ? SystemParameters.WorkArea.Height - 40 : double.PositiveInfinity;
            WindowStyle = WindowStyle.None;
            MinWidth = 400;
            MinHeight = 160;
            FontFamily = Theme.Font;
            Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            var chrome = new WindowChrome { CaptionHeight = 52, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false };
            WindowChrome.SetWindowChrome(this, chrome);
            Content = Build();
            if (snapshotDir == null) RestorePlacement();
            else
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -20000;
                Top = 0;
                ShowActivated = false;
                ShowInTaskbar = false;
                var since = DateTime.UtcNow;
                var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                done.Tick += delegate
                {
                    if (fixtureMode)
                    {
                        ShowBoard(fixtureBoard);
                        Engine.Current = fixtureBoard;
                        UpdateLayout();
                        if ((DateTime.UtcNow - since).TotalSeconds < 0.4 && ActualHeight < 1) return;
                        done.Stop();
                        WriteSnapshot();
                        Close();
                        return;
                    }
                    ShowBoard(Engine.Tick());
                    double sec = (DateTime.UtcNow - since).TotalSeconds;
                    if (sec < 2.5 || (!Engine.ScansComplete && sec < 45)) return;
                    done.Stop();
                    Board ready = Engine.Tick();
                    ShowBoard(ready);
                    UpdateLayout();
                    WriteSnapshot();
                    Engine.AcknowledgeImport(ready);
                    Close();
                };
                Loaded += delegate { if (!fixtureMode) Engine.Prime(); done.Start(); };
            }
            timer.Interval = TimeSpan.FromMilliseconds(1500);
            timer.Tick += delegate { if (live && !fixtureMode && WindowState != WindowState.Minimized) Refresh(); };
            SourceInitialized += delegate { if (snapshotDir == null) ApplyGlass(); };
            if (snapshotDir == null) Loaded += delegate { if (fixtureMode) ShowBoard(fixtureBoard); else Prime(); };
            StateChanged += delegate { UpdateRunning(); };
            Closing += delegate { if (snapshotDir == null) SavePlacement(); };
            SizeChanged += delegate { FitListWidth(); };
            PreviewMouseDown += delegate(object s, MouseButtonEventArgs e) { if (menu != null && menu.IsOpen && !InsideMenu(e.OriginalSource)) menu.IsOpen = false; };
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key != Key.Escape) return;
                if (menu != null && menu.IsOpen) { menu.IsOpen = false; e.Handled = true; return; }
                if (nameEdit != null || rowEdit != null) return;
                Close();
            };
        }
        UIElement Build()
        {
            var root = new Grid();
            root.Background = snapshotDir == null && TransparencyOn()
                ? new SolidColorBrush(Color.FromArgb(0x52, 0x0C, 0x0E, 0x14))
                : new SolidColorBrush(Color.FromArgb(0xFF, 0x17, 0x1A, 0x21));
            var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            stack.Children.Add(BuildHeader());
            cpuRow = new StatRow("Processor");
            memRow = new StatRow("Memory");
            gpuRow = new StatRow("Graphics");
            vramRow = new StatRow("Video memory");
            diskRow = new StatRow(BoardText.DiskLabel());
            threadStrip = new ThreadStrip(new List<KeyValuePair<int, int[]>>());
            system = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (UIElement row in new UIElement[] { cpuRow, threadStrip, memRow, gpuRow, vramRow, diskRow }) system.Children.Add(row);
            stack.Children.Add(system);
            headlineText = Theme.Label("", 15, Theme.Text, FontWeights.Normal);
            headlineText.TextWrapping = TextWrapping.Wrap;
            headlineText.Margin = new Thickness(0, 12, 0, 4);
            stack.Children.Add(headlineText);
            list = new Grid();
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
            scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list };
            scroll.VerticalScrollBarVisibility = snapshotDir == null ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
            if (snapshotDir == null) scroll.MaxHeight = 640;
            stack.Children.Add(scroll);
            var sheen = new Border { Height = 46, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(19, 19, 0, 0), Background = Theme.Sheen, IsHitTestVisible = false };
            pane = new Border { Margin = new Thickness(8), Background = Theme.Card, BorderBrush = Theme.Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Child = new Grid { Children = { sheen, stack } } };
            root.Children.Add(pane);
            return root;
        }
        UIElement BuildHeader()
        {
            var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nameText = Theme.Label(Environment.MachineName, 19, Theme.Text, FontWeights.SemiBold);
            nameText.FontFamily = Theme.Display;
            nameText.MaxWidth = 280;
            HookClick(nameText, delegate { BeginNameEdit(); });
            nameMark = Theme.Label("\u270E", 13, Theme.Faint, FontWeights.Normal);
            nameMark.Margin = new Thickness(6, 1, 0, 0);
            nameMark.Visibility = Visibility.Collapsed;
            HookClick(nameMark, delegate { BeginNameEdit(); });
            nameLine = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            nameLine.Children.Add(nameText);
            nameLine.Children.Add(nameMark);
            nameLine.MouseEnter += delegate { nameMark.Visibility = Visibility.Visible; };
            nameLine.MouseLeave += delegate { nameMark.Visibility = Visibility.Collapsed; };
            nameHost = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { nameLine } };
            g.Children.Add(nameHost);
            stateLabel = Theme.Label("Live", 12, Theme.Sub, FontWeights.Normal);
            stateLabel.Margin = new Thickness(0, 0, 8, 0);
            stateLabel.Width = 32;
            switchTrack = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(11), Background = Theme.Green, VerticalAlignment = VerticalAlignment.Center };
            knob = new TranslateTransform(18, 0);
            switchTrack.Child = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0), RenderTransform = knob };
            var sw = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            sw.Children.Add(stateLabel);
            sw.Children.Add(switchTrack);
            sw.MouseLeftButtonUp += delegate { SetLive(!live); };
            WindowChrome.SetIsHitTestVisibleInChrome(sw, true);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(right, 1);
            right.Children.Add(sw);
            gearBtn = CaptionButton("\uE713", null, delegate { OpenGear(); });
            right.Children.Add(gearBtn);
            right.Children.Add(CaptionButton("\uE921", "Minimize", delegate { WindowState = WindowState.Minimized; }));
            right.Children.Add(CaptionButton("\uE8BB", "Close", delegate { Close(); }));
            g.Children.Add(right);
            return g;
        }
        static void HookClick(TextBlock tb, MouseButtonEventHandler click)
        {
            tb.Cursor = Cursors.Hand;
            tb.MouseLeftButtonUp += click;
            WindowChrome.SetIsHitTestVisibleInChrome(tb, true);
        }
        Border CaptionButton(string glyph, string tip, MouseButtonEventHandler click)
        {
            var t = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = Theme.Sub, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { Width = 28, Height = 30, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = t };
            if (!string.IsNullOrEmpty(tip)) b.ToolTip = tip;
            b.MouseEnter += delegate { b.Background = Theme.Hover; t.Foreground = Theme.Text; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; t.Foreground = Theme.Sub; };
            b.MouseLeftButtonUp += click;
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            return b;
        }
        static bool TransparencyOn()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("EnableTransparency");
                    return v == null || Convert.ToInt32(v) != 0;
                }
            }
            catch (Exception) { return true; }
        }
        void ApplyGlass()
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;
            int on = 1;
            Native.DwmSetWindowAttribute(h, 20, ref on, 4);
            int round = 2;
            Native.DwmSetWindowAttribute(h, 33, ref round, 4);
            var m = new Native.MARGINS { l = -1, r = -1, t = -1, b = -1 };
            Native.DwmExtendFrameIntoClientArea(h, ref m);
            if (!TransparencyOn()) return;
            int acrylic = 3;
            Native.DwmSetWindowAttribute(h, 38, ref acrylic, 4);
        }
        void SetLive(bool on)
        {
            live = on;
            var a = new DoubleAnimation(on ? 18 : 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            knob.BeginAnimation(TranslateTransform.XProperty, a);
            switchTrack.Background = on ? Theme.Green : Theme.Off;
            stateLabel.Text = on ? "Live" : "Off";
            if (pane != null) pane.Opacity = on ? 1 : 0.45;
            UpdateRunning();
            if (on) ShowBoard(Engine.Current);
        }
        void UpdateRunning()
        {
            if (fixtureMode) return;
            bool run = live && WindowState != WindowState.Minimized;
            if (run && !timer.IsEnabled) Prime();
            else if (!run && timer.IsEnabled)
            {
                timer.Stop();
                Engine.Reset();
                Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            }
        }
        void Prime()
        {
            if (fixtureMode || !live || WindowState == WindowState.Minimized) return;
            Engine.Prime();
            var once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            once.Tick += delegate { once.Stop(); if (live && WindowState != WindowState.Minimized) Refresh(); };
            once.Start();
        }
        void Refresh() { Board board = Engine.Tick(); ShowBoard(board); Engine.AcknowledgeImport(board); }
        void ShowBoard(Board board)
        {
            if (board == null || list == null) return;
            lastBoard = board;
            if (nameEdit == null && nameText != null)
                nameText.Text = Redact.Show(string.IsNullOrEmpty(board.name) ? Environment.MachineName : board.name);
            bool instant = snapshotDir != null;
            ShowSystem(board, instant);
            PaintHeadline(board);
            if (rowEdit == null) ShowList(board, instant);
        }
        void PaintHeadline(Board board)
        {
            string text = board.headline == null || board.headline.text == null ? "" : board.headline.text;
            text = Redact.Show(text);
            bool need = board.headline != null && board.headline.needs_you && text.Length > 0;
            headlineText.Text = text;
            headlineText.Foreground = need ? Theme.AmberDot : Theme.Text;
            headlineText.FontWeight = need ? FontWeights.SemiBold : FontWeights.Normal;
            headlineText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        void ShowSystem(Board board, bool instant)
        {
            if (cpuRow == null) return;
            Strip strip = board == null ? null : board.strip;
            var meters = BoardText.Meters(strip);
            ShowMeter(cpuRow, meters, 0, false, instant);
            ShowMeter(memRow, meters, 1, false, instant);
            ShowMeter(gpuRow, meters, 2, true, instant);
            ShowMeter(vramRow, meters, 3, true, instant);
            ShowMeter(diskRow, meters, 4, false, instant);
            string sig = BoardText.CoreSig(strip);
            if (sig != threadSig)
            {
                var next = new ThreadStrip(BoardText.Cores(strip));
                int at = system == null || threadStrip == null ? -1 : system.Children.IndexOf(threadStrip);
                if (at >= 0) { system.Children.RemoveAt(at); system.Children.Insert(at, next); }
                threadStrip = next;
                threadSig = sig;
            }
            if (threadStrip == null) return;
            var cores = BoardText.Cores(strip);
            if (strip != null && strip.threads != null)
                foreach (var c in cores)
                    for (int t = 0; t < c.Value.Length; t++)
                    {
                        int cpu = c.Value[t];
                        threadStrip.Set(cpu, (cpu >= 0 && cpu < strip.threads.Count ? strip.threads[cpu] : 0) / 100.0, instant);
                    }
            threadStrip.SetBusiest(BoardText.BusiestLine(strip));
        }
        static void ShowMeter(StatRow row, List<BoardText.Meter> meters, int index, bool optional, bool instant)
        {
            if (row == null) return;
            BoardText.Meter m = meters != null && index >= 0 && index < meters.Count ? meters[index] : null;
            bool hide = m == null || (optional && m.Detail == "not available");
            row.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            if (!hide) row.Set(m.Detail, m.Value, m.Fraction, instant);
        }
        void FitListWidth()
        {
            if (list == null) return;
            double w = (ActualWidth > 1 ? ActualWidth : Width) - 50;
            if (w >= 180 && (double.IsNaN(list.Width) || Math.Abs(list.Width - w) > 0.5)) list.Width = w;
        }
        void ShowList(Board board, bool instant)
        {
            FitListWidth();
            var next = Plan(board);
            var sb = new StringBuilder();
            foreach (Entry e in next) sb.Append(e.Key).Append('\n');
            string sig = sb.ToString();
            if (sig == listSig && lines.Count == next.Count)
            {
                for (int i = 0; i < next.Count; i++) { lines[i].Thing = next[i].Thing; lines[i].Title = next[i].Title; lines[i].Group = next[i].Group; lines[i].Open = next[i].Open; Paint(lines[i], instant); }
                return;
            }
            double y = scroll == null ? 0 : scroll.VerticalOffset;
            list.Children.Clear();
            list.RowDefinitions.Clear();
            lines.Clear();
            listSig = sig;
            foreach (Entry e in next) lines.Add(Make(e, instant));
            if (scroll == null) return;
            scroll.UpdateLayout();
            scroll.ScrollToVerticalOffset(y);
        }
        List<Entry> Plan(Board board)
        {
            var planned = new List<Entry>();
            if (board == null || board.groups == null) return planned;
            foreach (Group g in board.groups)
            {
                if (g == null || g.things == null) continue;
                var top = new List<Thing>();
                var kids = new Dictionary<string, List<Thing>>();
                Thing roll = null;
                foreach (Thing t in g.things)
                {
                    if (t == null || IsHidden(t)) continue;
                    if (roll != null && IsChild(roll, t))
                    {
                        List<Thing> bucket;
                        if (!kids.TryGetValue(roll.id, out bucket)) kids[roll.id] = bucket = new List<Thing>();
                        bucket.Add(t);
                        continue;
                    }
                    roll = IsRollup(t) ? t : null;
                    top.Add(t);
                }
                if (top.Count == 0) continue;
                string title = Redact.Show((g.title ?? "").ToUpperInvariant());
                planned.Add(new Entry { Section = true, Title = title, Key = "t:" + title + ":" + (g.kind ?? "") });
                foreach (Thing t in top)
                {
                    bool disk = IsRollup(t);
                    bool open = disk && !string.IsNullOrEmpty(t.id) && expanded.Contains(t.id);
                    string id = string.IsNullOrEmpty(t.id) ? (t.name ?? "") : t.id;
                    planned.Add(new Entry { Thing = t, Roll = disk, Open = open, Group = g.kind, Title = g.title, Key = (disk ? "r:" : "n:") + id + (open ? ":o" : "") });
                    if (!open) continue;
                    List<Thing> bucket;
                    if (!string.IsNullOrEmpty(t.id) && kids.TryGetValue(t.id, out bucket))
                    {
                        foreach (Thing c in bucket) planned.Add(new Entry { Thing = c, Indent = 1, Group = g.kind, Title = g.title, Key = "c:" + (c.id ?? "") });
                        continue;
                    }
                    string[] parts = (t.why ?? "").Split(new[] { " \u00B7 " }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    for (int i = 0; i < parts.Length; i++)
                        planned.Add(new Entry { Title = parts[i], Indent = 1, Key = "w:" + id + ":" + i.ToString(CultureInfo.InvariantCulture) });
                }
            }
            return planned;
        }
        Entry Make(Entry e, bool instant)
        {
            int row = list.RowDefinitions.Count;
            list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (e.Section)
            {
                e.Name = Theme.Label(e.Title ?? "", 11.5, Theme.Faint, FontWeights.SemiBold);
                e.Name.Margin = new Thickness(2, row == 0 ? 4 : 8, 0, 2);
                Put(e.Name, row, 0, 3);
                return e;
            }
            var wash = new SolidColorBrush(Colors.Transparent);
            e.Wash = wash;
            Put(new Border { Background = wash, IsHitTestVisible = false, CornerRadius = new CornerRadius(8), Margin = new Thickness(-6, 0, -4, 0) }, row, 0, 3);
            bool quiet = e.Roll || e.Thing == null || e.Indent > 0;
            e.Dot = Theme.Label("", 13, Theme.Sub, FontWeights.Normal);
            e.Dot.HorizontalAlignment = HorizontalAlignment.Center;
            e.Dot.Width = 16;
            if (e.Roll || e.Thing == null) e.Dot.Visibility = Visibility.Collapsed;
            e.Name = Theme.Label("", e.Roll ? 12.5 : (quiet ? 12 : 13.5), quiet ? Theme.Faint : Theme.Text, FontWeights.Medium);
            e.Spin = new RotateTransform(0);
            e.Mark = Theme.Label("\u203A", 14, Theme.Sub, FontWeights.SemiBold);
            e.Mark.Margin = new Thickness(8, 0, 0, 0);
            e.Mark.Visibility = e.Roll ? Visibility.Visible : Visibility.Collapsed;
            e.Mark.RenderTransform = e.Spin;
            e.Mark.RenderTransformOrigin = new Point(0.5, 0.5);
            e.Host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { e.Name, e.Mark } };
            if (e.Roll) e.Host.Cursor = Cursors.Hand;
            e.Note = Theme.Label("", 12.5, Theme.Faint, FontWeights.Normal);
            e.Note.HorizontalAlignment = HorizontalAlignment.Right;
            e.Note.TextAlignment = TextAlignment.Right;
            Put(e.Dot, row, 0, 1);
            Put(e.Host, row, 1, 1);
            Put(e.Note, row, 2, 1);
            int below = list.RowDefinitions.Count;
            list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            e.Detail = new TextBlock { FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Faint, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            Typography.SetNumeralAlignment(e.Detail, FontNumeralAlignment.Tabular);
            Put(e.Detail, below, 1, 2);
            Wire(e);
            Paint(e, instant);
            return e;
        }
        void Put(UIElement el, int row, int col, int span)
        {
            Grid.SetRow(el, row);
            Grid.SetColumn(el, col);
            if (span > 1) Grid.SetColumnSpan(el, span);
            list.Children.Add(el);
        }
        void Wire(Entry e)
        {
            MouseButtonEventHandler menuClick = delegate(object s, MouseButtonEventArgs ev) { ev.Handled = true; OpenLine(e); };
            e.Host.MouseRightButtonUp += menuClick;
            e.Dot.MouseRightButtonUp += menuClick;
            e.Note.MouseRightButtonUp += menuClick;
            e.Detail.MouseRightButtonUp += menuClick;
            e.Host.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs ev)
            {
                if (rowEdit != null) return;
                if (e.Roll) { ev.Handled = true; if (e.Thing != null) ToggleRoll(e.Thing.id); return; }
                if (FromLink(ev.OriginalSource) || e.Thing == null || string.IsNullOrEmpty(e.Thing.folder)) return;
                ev.Handled = true;
                OpenCode(e.Thing.folder);
            };
        }
        void Paint(Entry e, bool instant)
        {
            if (e.Section)
            {
                if (e.Name != null && e.Name.Text != (e.Title ?? "")) e.Name.Text = e.Title ?? "";
                return;
            }
            if (e.Thing == null)
            {
                string only = Redact.Show(e.Title ?? "");
                if (e.Name.Text != only) e.Name.Text = only;
                e.Host.Margin = new Thickness(e.Indent * 16, 1, 8, 1);
                return;
            }
            Thing t = e.Thing;
            Remember(t);
            string why = Redact.Show(t.why ?? "");
            if (e.Roll)
            {
                string label = ShownName(t);
                if (!string.IsNullOrEmpty(t.detail)) label += "  \u00B7  " + t.detail;
                label = Redact.Show(label);
                if (e.Name.Text != label) e.Name.Text = label;
                Tip(e.Name, why);
                e.Host.Margin = new Thickness(0, 2, 8, 2);
                Spin(e, instant);
                return;
            }
            bool pin = Pinned(t);
            double inner = double.IsNaN(list.Width) ? 480 : list.Width;
            e.Name.MaxWidth = Math.Max(96, inner - 202 - e.Indent * 16);
            e.Name.FontWeight = pin ? FontWeights.SemiBold : FontWeights.Medium;
            e.Name.Cursor = string.IsNullOrEmpty(t.folder) ? Cursors.Arrow : Cursors.Hand;
            var mark = new StringBuilder();
            mark.Append(pin ? "1|" : "0|").Append(ShownName(t)).Append('|').Append(t.project ?? "").Append('|').Append(t.detail ?? "").Append('|').Append(RightText(t)).Append('|');
            if (t.ports != null) foreach (int p in t.ports) mark.Append(p.ToString(CultureInfo.InvariantCulture)).Append(',');
            if (e.Sig != mark.ToString()) { e.Sig = mark.ToString(); Fill(e, t, pin); }
            string right = Redact.Show(RightText(t));
            if (e.Note.Text != right) e.Note.Text = right;
            bool word = t.state == "waiting" || t.state == "quiet" || t.state == "failed";
            e.Note.Foreground = word ? DotBrush(t.state) : Theme.Faint;
            e.Dot.Text = BoardText.Dot(t.state);
            e.Dot.Foreground = DotBrush(t.state);
            int bot = e.Detail.Visibility == Visibility.Visible ? 0 : 2;
            e.Dot.Margin = new Thickness(0, 2, 0, bot);
            e.Host.Margin = new Thickness(e.Indent * 16, 2, 8, bot);
            e.Note.Margin = new Thickness(6, 2, 0, bot);
            Tip(e.Dot, why); Tip(e.Name, why); Tip(e.Note, why); Tip(e.Detail, why);
            NoteState(e, t, instant);
        }
        void Spin(Entry e, bool instant)
        {
            if (e.Spin == null || Math.Abs(e.Spin.Angle - (e.Open ? 90 : 0)) < 1) return;
            if (!instant && e.Open)
            {
                e.Spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 90, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                return;
            }
            e.Spin.BeginAnimation(RotateTransform.AngleProperty, null);
            e.Spin.Angle = e.Open ? 90 : 0;
        }
        static void Fill(Entry e, Thing t, bool pin)
        {
            e.Name.Inlines.Clear();
            var name = new Run(Redact.Show(ShownName(t))) { Foreground = e.Indent > 0 ? Theme.Faint : Theme.Text, FontWeight = pin ? FontWeights.SemiBold : FontWeights.Medium };
            e.Name.Inlines.Add(name);
            string proj = Redact.Show(BoardText.Aside(t, e.Group, e.Title));
            if (proj.Length > 0) e.Name.Inlines.Add(new Run("  " + proj) { Foreground = Theme.Sub, FontWeight = FontWeights.Normal });
            string story = Story(t);
            if (story.Length == 0) ShowPorts(e.Name, t, " \u00B7 ");
            e.Detail.Inlines.Clear();
            if (story.Length == 0)
            {
                e.Detail.Visibility = Visibility.Collapsed;
                e.Detail.Margin = new Thickness(0);
                return;
            }
            e.Detail.Inlines.Add(new Run(Redact.Show(story)));
            ShowPorts(e.Detail, t, "  \u00B7  ");
            e.Detail.Visibility = Visibility.Visible;
            e.Detail.Margin = new Thickness(e.Indent * 16, 0, 8, 3);
        }
        static void ShowPorts(TextBlock tb, Thing t, string lead)
        {
            var show = new List<int>(); if (t != null && t.ports != null) foreach (int p in t.ports) if (p != 9229 && p != 9222) show.Add(p);
            if (show.Count == 0 && t != null && t.ports != null) foreach (int p in t.ports) show.Add(p);
            show.Sort(); int extra = show.Count > 3 ? show.Count - 3 : 0; int n = extra > 0 ? 3 : show.Count;
            for (int i = 0; i < n; i++) AddPort(tb, show[i], lead);
            if (extra > 0) tb.Inlines.Add(new Run(lead + "+" + extra.ToString(CultureInfo.InvariantCulture)) { Foreground = Theme.Faint });
        }
        static void AddPort(TextBlock tb, int port, string lead)
        {
            tb.Inlines.Add(new Run(lead) { Foreground = Theme.Faint });
            string shown = port.ToString(CultureInfo.InvariantCulture);
            var link = new Hyperlink(new Run(":" + shown)) { Foreground = Theme.Link, TextDecorations = new TextDecorationCollection() };
            link.Click += delegate { OpenPort(shown); };
            tb.Inlines.Add(link);
        }
        void NoteState(Entry e, Thing t, bool instant)
        {
            if (e == null || t == null || string.IsNullOrEmpty(t.id) || e.Wash == null) return;
            string prev;
            bool known = seen.TryGetValue(t.id, out prev);
            string now = t.state ?? "";
            seen[t.id] = now;
            if (!known || prev == now || instant || e.Flashing) return;
            e.Flashing = true;
            var src = DotBrush(now) as SolidColorBrush;
            Color c = src == null ? Colors.White : src.Color;
            var a = new ColorAnimation(Color.FromArgb(0x48, c.R, c.G, c.B), Colors.Transparent, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            a.Completed += delegate { e.Flashing = false; if (e.Wash == null) return; e.Wash.BeginAnimation(SolidColorBrush.ColorProperty, null); e.Wash.Color = Colors.Transparent; };
            e.Wash.BeginAnimation(SolidColorBrush.ColorProperty, a);
        }
        void ToggleRoll(string id)
        {
            if (string.IsNullOrEmpty(id) || snapshotDir != null) return;
            if (!expanded.Add(id)) expanded.Remove(id);
            if (lastBoard != null) ShowList(lastBoard, false);
        }
        void OpenGear()
        {
            if (snapshotDir != null) return;
            if (menu != null && menu.IsOpen && menu.PlacementTarget == gearBtn) { menu.IsOpen = false; return; }
            ShowMenu(gearBtn, PlacementMode.Bottom, GearRows());
        }
        List<MenuRow> GearRows()
        {
            var rows = new List<MenuRow>();
            rows.Add(new MenuRow { Label = "Hidden", Header = true });
            var ids = new List<string>(Prefs.Hides);
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string id in ids) rows.Add(Item(Remembered(id), delegate { Unhide(id); }));
            rows.Add(Item("Frontier Deployments", OpenFrontier));
            rows.Add(new MenuRow { Label = ErrorLog.Ver, Dim = true });
            return rows;
        }
        void OpenLine(Entry e)
        {
            if (e == null || e.Thing == null || snapshotDir != null) return;
            Thing t = e.Thing;
            Action rename = string.IsNullOrEmpty(t.id) ? null : (Action)delegate { BeginRename(e); };
            Action hide = string.IsNullOrEmpty(t.id) ? null : (Action)delegate { HideThing(t); };
            Action pin = string.IsNullOrEmpty(t.id) ? null : (Action)delegate { PinThing(t); };
            ShowMenu(this, PlacementMode.MousePoint, new List<MenuRow> { Item("Rename", rename), Item("Hide", hide), Item(Pinned(t) ? "Unpin" : "Pin", pin) });
        }
        static MenuRow Item(string label, Action act)
        {
            return new MenuRow { Label = label, Act = act };
        }
        void ShowMenu(UIElement target, PlacementMode how, List<MenuRow> rows)
        {
            if (menu != null) menu.IsOpen = false;
            var panel = new StackPanel { Margin = new Thickness(6) };
            foreach (MenuRow row in rows)
            {
                if (row == null || string.IsNullOrEmpty(row.Label)) continue;
                MenuRow pick = row;
                bool quiet = pick.Header || pick.Dim || pick.Act == null;
                var item = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, pick.Header ? 3 : 6, 16, pick.Header ? 3 : 6), Child = Theme.Label(pick.Label, pick.Header ? 11 : 13, quiet ? Theme.Faint : Theme.Text, pick.Header ? FontWeights.SemiBold : FontWeights.Normal) };
                if (pick.Label == "Frontier Deployments") item.Margin = new Thickness(0, 6, 0, 0);
                if (pick.Act != null)
                {
                    item.Cursor = Cursors.Hand;
                    item.MouseEnter += delegate { item.Background = Theme.Hover; };
                    item.MouseLeave += delegate { item.Background = Brushes.Transparent; };
                    item.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs ev) { ev.Handled = true; if (menu != null) menu.IsOpen = false; pick.Act(); };
                }
                panel.Children.Add(item);
            }
            menu = new Popup { AllowsTransparency = true, StaysOpen = true, PopupAnimation = PopupAnimation.Fade, Placement = how, VerticalOffset = how == PlacementMode.Bottom ? 4 : 0 };
            menu.PlacementTarget = how == PlacementMode.MousePoint ? (UIElement)this : (target ?? this);
            menu.Child = new Border { MinWidth = 208, Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x14, 0x16, 0x1E)), BorderBrush = Theme.Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = panel };
            menu.IsOpen = true;
        }
        bool InsideMenu(object src)
        {
            var d = src as DependencyObject;
            while (d != null)
            {
                if (menu != null && (d == menu.Child || d == menu.PlacementTarget)) return true;
                DependencyObject next = d is Visual ? VisualTreeHelper.GetParent(d) : null;
                if (next == null) next = LogicalTreeHelper.GetParent(d);
                d = next;
            }
            return false;
        }
        void Unhide(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            Prefs.Hides.Remove(id);
            Keep();
        }
        void HideThing(Thing t)
        {
            if (t == null || string.IsNullOrEmpty(t.id) || snapshotDir != null) return;
            Prefs.Hides.Add(t.id);
            knownNames[t.id] = ShownName(t);
            Keep();
        }
        void PinThing(Thing t)
        {
            if (t == null || string.IsNullOrEmpty(t.id) || snapshotDir != null) return;
            if (Prefs.PinIds.Contains(t.id)) Prefs.PinIds.Remove(t.id);
            else Prefs.PinIds.Add(t.id);
            t.pinned = Prefs.PinIds.Contains(t.id);
            Keep();
        }
        void Keep()
        {
            Prefs.Save();
            if (lastBoard != null) ShowBoard(lastBoard);
        }
        string Remembered(string id)
        {
            string n;
            if (!string.IsNullOrEmpty(id) && Prefs.Rename.TryGetValue(id, out n) && !string.IsNullOrEmpty(n)) return n;
            if (!string.IsNullOrEmpty(id) && knownNames.TryGetValue(id, out n) && !string.IsNullOrEmpty(n)) return n;
            return id ?? "";
        }
        void Remember(Thing t)
        {
            if (t != null && !string.IsNullOrEmpty(t.id)) knownNames[t.id] = ShownName(t);
        }
        void BeginRename(Entry e)
        {
            if (e == null || e.Name == null || e.Host == null || rowEdit != null || snapshotDir != null) return;
            if (e.Thing == null || string.IsNullOrEmpty(e.Thing.id)) return;
            var box = Box(ShownName(e.Thing), e.Name.FontSize, e.Name.FontWeight, Theme.Font, 80);
            e.Name.Visibility = Visibility.Collapsed;
            e.Host.Children.Insert(0, box);
            rowEdit = box;
            box.KeyDown += delegate(object s, KeyEventArgs ev)
            {
                if (ev.Key == Key.Enter) { ev.Handled = true; FinishRename(e, true); }
                else if (ev.Key == Key.Escape) { ev.Handled = true; FinishRename(e, false); }
            };
            box.LostKeyboardFocus += delegate { FinishRename(e, true); };
            box.Focus();
            box.SelectAll();
        }
        void FinishRename(Entry e, bool save)
        {
            if (rowEdit == null || e == null || e.Host == null) return;
            TextBox box = rowEdit;
            rowEdit = null;
            string n = (box.Text ?? "").Trim();
            if (n.Length > 80) n = n.Substring(0, 80);
            if (save && n.Length > 0 && e.Thing != null && !string.IsNullOrEmpty(e.Thing.id)) { Prefs.Rename[e.Thing.id] = n; Prefs.Save(); knownNames[e.Thing.id] = n; }
            if (e.Host.Children.Contains(box)) e.Host.Children.Remove(box);
            e.Name.Visibility = Visibility.Visible;
            e.Sig = null;
            if (e.Thing != null) Fill(e, e.Thing, Pinned(e.Thing));
        }
        static TextBox Box(string text, double size, FontWeight weight, FontFamily font, double min)
        {
            return new TextBox { Text = text, FontFamily = font, FontSize = size, FontWeight = weight, Foreground = Theme.Text, CaretBrush = Theme.Text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinWidth = min };
        }
        void BeginNameEdit()
        {
            if (naming || snapshotDir != null || nameHost == null) return;
            naming = true;
            nameEdit = Box(string.IsNullOrEmpty(Prefs.Name) ? nameText.Text : Prefs.Name, 19, FontWeights.SemiBold, Theme.Display, 120);
            nameHost.Children.Clear();
            nameHost.Children.Add(nameEdit);
            nameEdit.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { e.Handled = true; CommitName(); }
                else if (e.Key == Key.Escape) { e.Handled = true; CancelName(); }
            };
            nameEdit.LostKeyboardFocus += delegate { CommitName(); };
            nameEdit.Focus();
            nameEdit.SelectAll();
        }
        void CommitName()
        {
            if (nameEdit == null) return;
            string n = (nameEdit.Text ?? "").Trim();
            nameEdit = null;
            if (n.Length > 80) n = n.Substring(0, 80);
            if (n.Length == 0) n = Environment.MachineName;
            Prefs.Name = n;
            Prefs.Save();
            nameText.Text = Redact.Show(n);
            ShowName();
            naming = false;
        }
        void CancelName()
        {
            if (nameEdit == null) return;
            nameEdit = null;
            ShowName();
            naming = false;
        }
        void ShowName()
        {
            if (nameHost == null || nameLine == null) return;
            nameHost.Children.Clear();
            nameHost.Children.Add(nameLine);
        }
        static string[] FindEditor()
        {
            try
            {
                string local = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "cursor", "Cursor.exe");
                if (File.Exists(local)) return new[] { local, "Cursor" };
            }
            catch (Exception) { }
            string[] path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
            foreach (var e in new[] { new[] { "Cursor.exe", "Cursor" }, new[] { "Code.exe", "VS Code" } })
                foreach (string raw in path)
                {
                    string d = raw.Trim().TrimEnd('\\');
                    if (d.Length == 0) continue;
                    try
                    {
                        for (int up = 0; up < 4 && d != null; up++)
                        {
                            string exe = System.IO.Path.Combine(d, e[0]);
                            if (File.Exists(exe)) return new[] { exe, e[1] };
                            d = System.IO.Path.GetDirectoryName(d);
                        }
                    }
                    catch (Exception) { }
                }
            return null;
        }
        static bool SafePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('"') >= 0 || path.IndexOf('\n') >= 0 || path.IndexOf('\r') >= 0 || path.IndexOf('%') >= 0) return false;
            try { return File.Exists(path) || Directory.Exists(path); }
            catch (Exception) { return false; }
        }
        static string QuotePath(string path) { return "\"" + path.TrimEnd('\\') + "\""; }
        static void OpenCode(string path)
        {
            if (!SafePath(path)) return;
            try
            {
                if (Editor != null)
                {
                    using (Process.Start(new ProcessStartInfo(Editor[0], QuotePath(path)) { UseShellExecute = false })) { }
                    return;
                }
                OpenExplorer(path);
            }
            catch (Exception) { }
        }
        static void OpenExplorer(string path)
        {
            if (!SafePath(path)) return;
            try
            {
                string explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                string args = (File.Exists(path) ? "/select," : "") + QuotePath(path);
                using (Process.Start(new ProcessStartInfo(explorer, args) { UseShellExecute = false })) { }
            }
            catch (Exception) { }
        }
        static void OpenPort(string port)
        {
            try { using (Process.Start(new ProcessStartInfo("http://127.0.0.1:" + port) { UseShellExecute = true })) { } }
            catch (Exception) { }
        }
        static void OpenFrontier()
        {
            try { using (Process.Start(new ProcessStartInfo("https://frontierdeployments.com") { UseShellExecute = true })) { } }
            catch (Exception) { }
        }
        void FitSnapshotHeight()
        {
            if (pane == null) return;
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null) { chrome.CaptionHeight = 0; chrome.ResizeBorderThickness = new Thickness(0); }
            double inner = Math.Max(1, Width - pane.Margin.Left - pane.Margin.Right);
            pane.Measure(new Size(inner, double.PositiveInfinity));
            double h = pane.DesiredSize.Height + pane.Margin.Top + pane.Margin.Bottom + pane.Margin.Bottom;
            if (h < 1) h = MinHeight;
            SizeToContent = SizeToContent.Manual;
            MinHeight = 0;
            MaxHeight = double.PositiveInfinity;
            Height = Math.Ceiling(h);
            UpdateLayout();
        }
        void WriteSnapshot()
        {
            try
            {
                string dir = System.IO.Path.GetFullPath(snapshotDir);
                Directory.CreateDirectory(dir);
                ShowBoard(Engine.Current);
                UpdateLayout();
                FitSnapshotHeight();
                string text = BoardText.Render(Engine.Current);
                File.WriteAllText(System.IO.Path.Combine(dir, "vitals.txt"), text, new UTF8Encoding(false));
                if (string.Equals(Environment.GetEnvironmentVariable("DESKTOPVITALS_PRIVACY_TEST"), "1", StringComparison.Ordinal))
                    WritePrivacyProof(dir);
                var root = (FrameworkElement)Content;
                double renderW = Math.Max(1, root.ActualWidth);
                root.Measure(new Size(renderW, double.PositiveInfinity));
                double renderH = Math.Max(root.ActualHeight, Math.Max(Height, root.DesiredSize.Height));
                root.Arrange(new Rect(0, 0, renderW, renderH));
                double scale = Math.Min(2, Math.Min(4000 / renderW, 4000 / Math.Max(1, renderH)));
                int bw = Math.Max(1, (int)Math.Ceiling(renderW * scale));
                int bh = Math.Max(1, (int)Math.Ceiling(renderH * scale));
                var bmp = new RenderTargetBitmap(bw, bh, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(System.IO.Path.Combine(dir, "vitals.png"))) enc.Save(fs);
                try { Console.Out.Write(text); Console.Out.Flush(); } catch (Exception) { }
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine("Snapshot failed: " + ex.Message); } catch (Exception) { }
                Environment.Exit(1);
            }
        }
        void WritePrivacyProof(string dir)
        {
            const string sample = @"C:\Users\Sam\x.ps1 --api-key sk-dvsmokeTESTKEY0001 --token ghp_dvsmokeTESTTOKEN0123456789 password=dvsmokePass99 Bearer dvsmokeBearerTok99 API_KEY=dvsmokeEnv99";
            var sb = new StringBuilder();
            sb.Append("show=").Append(Redact.Show(sample)).Append('\n');
            sb.Append("log=").Append(Redact.Log(sample)).Append('\n');
            Board board = Engine.Current;
            if (board != null && board.groups != null)
                foreach (Group g in board.groups)
                    if (g.things != null)
                        foreach (Thing t in g.things)
                            if (!string.IsNullOrEmpty(t.why)) sb.Append("why=").Append(t.why).Append('\n');
            File.WriteAllText(System.IO.Path.Combine(dir, "privacy.txt"), sb.ToString(), new UTF8Encoding(false));
        }
        void RestorePlacement()
        {
            try
            {
                string f = System.IO.Path.Combine(StateDir, "window.txt");
                if (!File.Exists(f)) return;
                string[] parts = File.ReadAllText(f).Split(',');
                if (parts.Length < 3) return;
                double left = double.Parse(parts[0], CultureInfo.InvariantCulture);
                double top = double.Parse(parts[1], CultureInfo.InvariantCulture);
                var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (!vs.Contains(new Point(left + 40, top + 20))) return;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                if (parts.Length > 3) Width = Math.Max(MinWidth, double.Parse(parts[3], CultureInfo.InvariantCulture));
            }
            catch (Exception) { }
        }
        void SavePlacement()
        {
            try
            {
                Directory.CreateDirectory(StateDir);
                var r = RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                File.WriteAllText(System.IO.Path.Combine(StateDir, "window.txt"), string.Join(",", new[] { r.Left.ToString(CultureInfo.InvariantCulture), r.Top.ToString(CultureInfo.InvariantCulture), r.Height.ToString(CultureInfo.InvariantCulture), r.Width.ToString(CultureInfo.InvariantCulture) }));
            }
            catch (Exception) { }
        }
        public void Toggle()
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            if (WindowState != WindowState.Minimized && Native.GetForegroundWindow() == h) { WindowState = WindowState.Minimized; return; }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }
        static bool IsHidden(Thing t)
        {
            return t == null || (!string.IsNullOrEmpty(t.id) && Prefs.Hides.Contains(t.id)) || (!string.IsNullOrEmpty(t.name) && Prefs.Hides.Contains(t.name));
        }
        static bool IsRollup(Thing t)
        {
            return t != null && !string.IsNullOrEmpty(t.name) && t.name.IndexOf(" on disk", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        static bool IsChild(Thing roll, Thing t)
        {
            return roll != null && t != null && !string.IsNullOrEmpty(roll.id) && !string.IsNullOrEmpty(t.id) && t.id.StartsWith(roll.id + ":", StringComparison.Ordinal);
        }
        static string ShownName(Thing t)
        {
            if (t == null) return "";
            string renamed;
            string baseName = t.name ?? "";
            if (!string.IsNullOrEmpty(t.id) && Prefs.Rename.TryGetValue(t.id, out renamed) && !string.IsNullOrEmpty(renamed)) baseName = renamed;
            if (t.count > 1 && baseName.IndexOf(" on disk", StringComparison.OrdinalIgnoreCase) < 0)
                return baseName + " \u00D7" + t.count.ToString(CultureInfo.InvariantCulture);
            return baseName;
        }
        static string RightText(Thing t)
        {
            if (t == null) return "";
            string state = t.state ?? "";
            if (state == "ended") return "";
            if (state == "waiting" || state == "quiet" || state == "failed")
                return BoardText.Phrase(state) + (string.IsNullOrEmpty(t.since) ? "" : " \u00B7 " + t.since);
            if (!string.IsNullOrEmpty(t.since)) return t.since;
            if (t.cpu >= 5) return t.cpu.ToString("0", CultureInfo.InvariantCulture) + "%";
            if (t.vram_mb >= 200) return Megs(t.vram_mb) + " VRAM";
            if (t.ram_mb >= 200) return Megs(t.ram_mb);
            return "";
        }
        static string Story(Thing t)
        {
            string detail = t.detail ?? "";
            string right = RightText(t);
            if (detail.Length == 0 || right.Length == 0) return detail;
            int shown = Figure(right);
            string[] parts = detail.Split(new[] { " \u00B7 " }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new StringBuilder();
            foreach (string raw in parts)
            {
                string part = raw.Trim();
                if (shown != 0 && Figure(part) == shown) continue;
                if (kept.Length > 0) kept.Append(" \u00B7 ");
                kept.Append(part);
            }
            return kept.ToString();
        }
        static int Figure(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (s.EndsWith("%", StringComparison.Ordinal))
            {
                double ignored;
                if (double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Number, CultureInfo.InvariantCulture, out ignored) && ignored >= 0) return 3;
                return 0;
            }
            bool vram = s.EndsWith(" VRAM", StringComparison.OrdinalIgnoreCase);
            string body = vram ? s.Substring(0, s.Length - 5).Trim() : s;
            if (body.EndsWith(" GB", StringComparison.OrdinalIgnoreCase) || body.EndsWith(" MB", StringComparison.OrdinalIgnoreCase))
                return vram ? 2 : 1;
            return 0;
        }
        static string Megs(double mb)
        {
            if (mb >= 10240) return (mb / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " GB";
            if (mb >= 1024) return (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return ((int)Math.Round(mb)).ToString(CultureInfo.InvariantCulture) + " MB";
        }
        static bool FromLink(object src)
        {
            var d = src as DependencyObject;
            while (d != null)
            {
                if (d is Hyperlink) return true;
                DependencyObject next = LogicalTreeHelper.GetParent(d);
                if (next == null && d is Visual) next = VisualTreeHelper.GetParent(d);
                if (next == d) break;
                d = next;
            }
            return false;
        }
        static void Tip(FrameworkElement el, string why)
        {
            if (el == null) return;
            string cur = el.ToolTip as string;
            if (cur == why || (string.IsNullOrEmpty(cur) && string.IsNullOrEmpty(why))) return;
            el.ToolTip = string.IsNullOrEmpty(why) ? null : why;
        }
        static Brush DotBrush(string state)
        {
            switch (state)
            {
                case "working": case "running": return Theme.Green;
                case "waiting": case "quiet": return Theme.AmberDot;
                case "failed": return Theme.FailedDot;
                default: return Theme.Sub;
            }
        }
        static bool Pinned(Thing t)
        {
            return t != null && (t.pinned || (!string.IsNullOrEmpty(t.id) && Prefs.PinIds.Contains(t.id)));
        }
        class Entry
        {
            public string Key, Title, Sig, Group;
            public Thing Thing;
            public bool Section, Roll, Open, Flashing;
            public int Indent;
            public TextBlock Dot, Name, Note, Detail, Mark;
            public StackPanel Host;
            public RotateTransform Spin;
            public SolidColorBrush Wash;
        }
        class MenuRow
        {
            public string Label;
            public bool Header;
            public bool Dim;
            public Action Act;
        }
    }
}
