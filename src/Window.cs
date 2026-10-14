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
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        static Brush Gradient(Color a, Color b, bool horizontal)
        {
            var br = new LinearGradientBrush(a, b, horizontal ? new Point(0, 0) : new Point(0, 0), horizontal ? new Point(1, 0) : new Point(0, 1));
            br.Freeze();
            return br;
        }

        public static TextBlock Label(string s, double size, Brush color, FontWeight weight)
        {
            var t = new TextBlock();
            t.Text = s;
            t.FontSize = size;
            t.Foreground = color;
            t.FontWeight = weight;
            t.FontFamily = Font;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.VerticalAlignment = VerticalAlignment.Center;
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
        readonly List<View> views = new List<View>();
        readonly string snapshotDir;
        bool live = true;
        bool fixtureMode;
        Board fixtureBoard;
        Board lastBoard;
        bool naming;
        string threadSig;
        string listSig;
        int rowCursor;
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

        // Draw this board instead of reading the machine.
        public void ShowFixture(Board board)
        {
            fixtureMode = true;
            fixtureBoard = board ?? new Board();
            Engine.Current = fixtureBoard;
        }

        // With a folder, the window renders off screen, writes vitals.txt and vitals.png, and closes.
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

            var chrome = new WindowChrome();
            chrome.CaptionHeight = 52;
            chrome.GlassFrameThickness = new Thickness(-1);
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.CornerRadius = new CornerRadius(0);
            chrome.UseAeroCaptionButtons = false;
            WindowChrome.SetWindowChrome(this, chrome);

            Content = Build();
            if (snapshotDir == null) RestorePlacement();
            else
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -20000;
                Top = 0;
                MaxHeight = double.PositiveInfinity;
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
                    if (sec < 2.5) return;
                    if (!Engine.ScansComplete && sec < 45) return;
                    done.Stop();
                    Board ready = Engine.Tick();
                    ShowBoard(ready);
                    UpdateLayout();
                    WriteSnapshot();
                    Engine.AcknowledgeImport(ready);
                    Close();
                };
                Loaded += delegate
                {
                    if (!fixtureMode) Engine.Prime();
                    done.Start();
                };
            }

            timer.Interval = TimeSpan.FromMilliseconds(1500);
            timer.Tick += delegate
            {
                if (!live || fixtureMode || WindowState == WindowState.Minimized) return;
                Board board = Engine.Tick();
                ShowBoard(board);
                Engine.AcknowledgeImport(board);
            };
            SourceInitialized += delegate { if (snapshotDir == null) ApplyGlass(); };
            if (snapshotDir == null) Loaded += delegate { if (fixtureMode) ShowBoard(fixtureBoard); else Prime(); };
            StateChanged += delegate { UpdateRunning(); };
            Closing += delegate { if (snapshotDir == null) SavePlacement(); };
            SizeChanged += delegate { FitListWidth(); };
            PreviewMouseDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (menu == null || !menu.IsOpen) return;
                if (InsideMenu(e.OriginalSource)) return;
                menu.IsOpen = false;
            };
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
            system.Children.Add(cpuRow);
            system.Children.Add(threadStrip);
            system.Children.Add(memRow);
            system.Children.Add(gpuRow);
            system.Children.Add(vramRow);
            system.Children.Add(diskRow);
            stack.Children.Add(system);
            headlineText = Theme.Label("", 15, Theme.Text, FontWeights.Normal);
            headlineText.TextWrapping = TextWrapping.Wrap;
            headlineText.Margin = new Thickness(0, 12, 0, 4);
            stack.Children.Add(headlineText);

            // Dot, then the name with its project beside it, then the right-hand note.
            list = new Grid();
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
            scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = snapshotDir == null ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Content = list;
            if (snapshotDir == null) scroll.MaxHeight = 640;
            stack.Children.Add(scroll);

            // Glass: a clear pane that thins out toward the bottom, a sheen across its top, and a rim that catches light.
            var sheen = new Border { Height = 46, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(19, 19, 0, 0), Background = Theme.Sheen, IsHitTestVisible = false };
            pane = new Border
            {
                Margin = new Thickness(8),
                Background = Theme.Card,
                BorderBrush = Theme.Edge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20),
                Child = new Grid { Children = { sheen, stack } }
            };
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
            nameText.Cursor = Cursors.Hand;
            nameText.MaxWidth = 280;
            nameText.MouseLeftButtonUp += delegate { BeginNameEdit(); };
            WindowChrome.SetIsHitTestVisibleInChrome(nameText, true);
            nameMark = Theme.Label("\u270E", 13, Theme.Faint, FontWeights.Normal);
            nameMark.Margin = new Thickness(6, 1, 0, 0);
            nameMark.Cursor = Cursors.Hand;
            nameMark.Visibility = Visibility.Collapsed;
            nameMark.MouseLeftButtonUp += delegate { BeginNameEdit(); };
            WindowChrome.SetIsHitTestVisibleInChrome(nameMark, true);
            nameLine = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            nameLine.Children.Add(nameText);
            nameLine.Children.Add(nameMark);
            nameLine.MouseEnter += delegate { if (nameMark != null) nameMark.Visibility = Visibility.Visible; };
            nameLine.MouseLeave += delegate { if (nameMark != null) nameMark.Visibility = Visibility.Collapsed; };
            nameHost = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameHost.Children.Add(nameLine);
            g.Children.Add(nameHost);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(right, 1);
            var sw = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            stateLabel = Theme.Label("Live", 12, Theme.Sub, FontWeights.Normal);
            stateLabel.Margin = new Thickness(0, 0, 8, 0);
            stateLabel.Width = 32;
            switchTrack = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(11), Background = Theme.Green, VerticalAlignment = VerticalAlignment.Center };
            var k = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
            knob = new TranslateTransform(18, 0);
            k.RenderTransform = knob;
            switchTrack.Child = k;
            sw.Children.Add(stateLabel);
            sw.Children.Add(switchTrack);
            sw.MouseLeftButtonUp += delegate { SetLive(!live); };
            WindowChrome.SetIsHitTestVisibleInChrome(sw, true);
            right.Children.Add(sw);
            gearBtn = CaptionButton("\uE713", null, delegate { OpenGear(); });
            right.Children.Add(gearBtn);
            right.Children.Add(CaptionButton("\uE921", "Minimize", delegate { WindowState = WindowState.Minimized; }));
            right.Children.Add(CaptionButton("\uE8BB", "Close", delegate { Close(); }));
            g.Children.Add(right);
            return g;
        }

        Border CaptionButton(string glyph, string tip, MouseButtonEventHandler click)
        {
            var b = new Border { Width = 28, Height = 30, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            if (!string.IsNullOrEmpty(tip)) b.ToolTip = tip;
            var t = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = Theme.Sub, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            b.Child = t;
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

        // Dark frame, rounded corners, and Windows 11 acrylic: a frosted blur of whatever is behind the window.
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
            var a = new DoubleAnimation(on ? 18 : 0, TimeSpan.FromMilliseconds(180));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            knob.BeginAnimation(TranslateTransform.XProperty, a);
            switchTrack.Background = on ? Theme.Green : Theme.Off;
            stateLabel.Text = on ? "Live" : "Off";
            if (pane != null) pane.Opacity = on ? 1 : 0.45;
            UpdateRunning();
            if (on) ShowBoard(Engine.Current);
        }

        // Readings run only while the switch is on and the window is showing.
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
            once.Tick += delegate
            {
                once.Stop();
                if (!live || WindowState == WindowState.Minimized) return;
                Board board = Engine.Tick();
                ShowBoard(board);
                Engine.AcknowledgeImport(board);
                timer.Start();
            };
            once.Start();
        }

        void ShowBoard(Board board)
        {
            if (board == null || list == null) return;
            lastBoard = board;
            if (nameEdit == null && nameText != null)
                nameText.Text = Redact.Show(string.IsNullOrEmpty(board.name) ? Environment.MachineName : board.name);
            bool instant = snapshotDir != null;
            ShowSystem(board, instant);
            PaintHeadline(board);
            if (rowEdit != null) return;
            ShowList(board, instant);
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
            // No graphics adapter: drop the rows. A "not available" bar is a blank.
            ShowMeter(gpuRow, meters, 2, true, instant);
            ShowMeter(vramRow, meters, 3, true, instant);
            ShowMeter(diskRow, meters, 4, false, instant);

            string sig = BoardText.CoreSig(strip);
            if (threadStrip == null || sig != threadSig)
            {
                var next = new ThreadStrip(BoardText.Cores(strip));
                if (system != null && threadStrip != null)
                {
                    int at = system.Children.IndexOf(threadStrip);
                    if (at >= 0)
                    {
                        system.Children.RemoveAt(at);
                        system.Children.Insert(at, next);
                    }
                }
                threadStrip = next;
                threadSig = sig;
            }
            if (threadStrip == null) return;
            var cores = BoardText.Cores(strip);
            if (strip != null && strip.threads != null)
            {
                foreach (var c in cores)
                    for (int t = 0; t < c.Value.Length; t++)
                    {
                        int cpu = c.Value[t];
                        double pct = cpu >= 0 && cpu < strip.threads.Count ? strip.threads[cpu] : 0;
                        threadStrip.Set(cpu, pct / 100.0, instant);
                    }
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
            double w = ActualWidth > 1 ? ActualWidth : Width;
            w -= 50;
            if (w < 180) return;
            if (double.IsNaN(list.Width) || Math.Abs(list.Width - w) > 0.5) list.Width = w;
        }

        void ShowList(Board board, bool instant)
        {
            FitListWidth();
            var slots = BuildSlots(board);
            string sig = SigOf(slots);
            if (sig == listSig && views.Count == slots.Count)
            {
                for (int i = 0; i < slots.Count; i++) Paint(views[i], slots[i], instant);
                return;
            }
            double y = scroll == null ? 0 : scroll.VerticalOffset;
            list.Children.Clear();
            list.RowDefinitions.Clear();
            views.Clear();
            listSig = sig;
            foreach (Slot s in slots) views.Add(BuildView(s, instant));
            if (scroll != null)
            {
                scroll.UpdateLayout();
                scroll.ScrollToVerticalOffset(y);
            }
        }

        static string SigOf(List<Slot> slots)
        {
            var sb = new StringBuilder();
            foreach (Slot s in slots) sb.Append(s.Key).Append('\n');
            return sb.ToString();
        }

        List<Slot> BuildSlots(Board board)
        {
            var slots = new List<Slot>();
            if (board == null || board.groups == null) return slots;
            foreach (Group g in board.groups)
            {
                if (g == null || g.things == null) continue;
                var rows = new List<Thing>();
                foreach (Thing t in g.things) if (t != null && !IsHidden(t)) rows.Add(t);
                var top = new List<Thing>();
                var kids = new Dictionary<string, List<Thing>>();
                Thing roll = null;
                foreach (Thing t in rows)
                {
                    if (roll != null && IsChild(roll, t))
                    {
                        List<Thing> bucket;
                        if (!kids.TryGetValue(roll.id, out bucket))
                        {
                            bucket = new List<Thing>();
                            kids[roll.id] = bucket;
                        }
                        bucket.Add(t);
                        continue;
                    }
                    roll = IsRollup(t) ? t : null;
                    top.Add(t);
                }
                if (top.Count == 0) continue;
                var title = new Slot();
                title.Kind = "title";
                title.Text = Redact.Show((g.title ?? "").ToUpperInvariant());
                title.Key = "t:" + title.Text + ":" + (g.kind ?? "");
                slots.Add(title);
                foreach (Thing t in top)
                {
                    bool rollup = IsRollup(t);
                    bool open = rollup && !string.IsNullOrEmpty(t.id) && expanded.Contains(t.id);
                    var s = new Slot();
                    s.Kind = rollup ? "roll" : "row";
                    s.Thing = t;
                    s.Open = open;
                    s.Key = s.Kind + ":" + (string.IsNullOrEmpty(t.id) ? (t.name ?? "") : t.id) + (open ? ":open" : "");
                    slots.Add(s);
                    if (!open) continue;
                    List<Thing> bucket;
                    if (!string.IsNullOrEmpty(t.id) && kids.TryGetValue(t.id, out bucket))
                    {
                        foreach (Thing c in bucket)
                        {
                            var cs = new Slot();
                            cs.Kind = "child";
                            cs.Thing = c;
                            cs.Key = "c:" + (c.id ?? "");
                            slots.Add(cs);
                        }
                    }
                    else
                    {
                        string[] parts = SplitNames(t.why);
                        for (int i = 0; i < parts.Length; i++)
                        {
                            var cs = new Slot();
                            cs.Kind = "child";
                            cs.Text = parts[i];
                            cs.Key = "w:" + (t.id ?? "") + ":" + i.ToString(CultureInfo.InvariantCulture);
                            slots.Add(cs);
                        }
                    }
                }
            }
            return slots;
        }

        View BuildView(Slot s, bool instant)
        {
            var v = new View();
            if (s.Kind == "title") BuildTitle(v, s);
            else if (s.Kind == "roll") BuildRoll(v);
            else if (s.Kind == "child") BuildChild(v);
            else BuildDataRow(v);
            Paint(v, s, instant);
            return v;
        }

        void NewRow()
        {
            rowCursor = list.RowDefinitions.Count;
            list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        void Add(UIElement el, int col, int span)
        {
            Grid.SetRow(el, rowCursor);
            Grid.SetColumn(el, col);
            if (span > 1) Grid.SetColumnSpan(el, span);
            list.Children.Add(el);
        }

        void BuildTitle(View v, Slot s)
        {
            NewRow();
            var t = Theme.Label(s.Text ?? "", 11.5, Theme.Faint, FontWeights.SemiBold);
            t.Margin = new Thickness(2, rowCursor == 0 ? 6 : 13, 0, 4);
            v.Name = t;
            Add(t, 0, 3);
        }

        void BuildDataRow(View v)
        {
            NewRow();
            var wash = new SolidColorBrush(Colors.Transparent);
            var flash = new Border { Background = wash, IsHitTestVisible = false, CornerRadius = new CornerRadius(8), Margin = new Thickness(-6, 1, -4, 1) };
            v.Wash = wash;
            Add(flash, 0, 3);
            v.Dot = Theme.Label("", 13, Theme.Sub, FontWeights.Normal);
            v.Dot.HorizontalAlignment = HorizontalAlignment.Center;
            v.Dot.Width = 16;
            v.Name = Theme.Label("", 13.5, Theme.Text, FontWeights.Medium);
            v.Name.TextWrapping = TextWrapping.NoWrap;
            v.State = Theme.Label("", 12.5, Theme.Faint, FontWeights.Normal);
            v.State.HorizontalAlignment = HorizontalAlignment.Right;
            v.State.TextAlignment = TextAlignment.Right;
            v.State.TextWrapping = TextWrapping.NoWrap;
            Add(v.Dot, 0, 1);
            Add(v.Name, 1, 1);
            Add(v.State, 2, 1);
            HookLine(v, v.Dot, false);
            HookLine(v, v.Name, true);
            HookLine(v, v.State, false);
            NewRow();
            v.Detail = new TextBlock();
            v.Detail.FontFamily = Theme.Font;
            v.Detail.FontSize = 12;
            v.Detail.Foreground = Theme.Faint;
            v.Detail.TextWrapping = TextWrapping.Wrap;
            v.Detail.Visibility = Visibility.Collapsed;
            Typography.SetNumeralAlignment(v.Detail, FontNumeralAlignment.Tabular);
            Add(v.Detail, 1, 2);
            HookLine(v, v.Detail, false);
        }

        void BuildRoll(View v)
        {
            NewRow();
            var line = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 3) };
            v.Name = Theme.Label("", 12.5, Theme.Faint, FontWeights.Normal);
            v.Chevron = Theme.Label("\u203A", 14, Theme.Sub, FontWeights.SemiBold);
            v.Chevron.Margin = new Thickness(8, 0, 0, 0);
            v.Chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            v.Spin = new RotateTransform(0);
            v.Chevron.RenderTransform = v.Spin;
            line.Children.Add(v.Name);
            line.Children.Add(v.Chevron);
            Add(line, 1, 2);
            line.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (v.Thing != null) ToggleRoll(v.Thing.id);
            };
            line.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                OpenLine(v);
            };
        }

        void BuildChild(View v)
        {
            NewRow();
            v.Name = Theme.Label("", 12, Theme.Faint, FontWeights.Normal);
            v.Name.Margin = new Thickness(16, 0, 0, 1);
            Add(v.Name, 1, 2);
            HookLine(v, v.Name, true);
        }

        void Paint(View v, Slot s, bool instant)
        {
            if (v == null || s == null) return;
            if (s.Kind == "title")
            {
                if (v.Name != null && v.Name.Text != (s.Text ?? "")) v.Name.Text = s.Text ?? "";
                return;
            }
            if (s.Kind == "roll") { PaintRoll(v, s, instant); return; }
            if (s.Kind == "child") { PaintChild(v, s); return; }
            PaintRow(v, s, instant);
        }

        void PaintRow(View v, Slot s, bool instant)
        {
            Thing t = s.Thing;
            if (t == null || v.Name == null) return;
            v.Thing = t;
            Remember(t);
            bool pin = t.pinned || (!string.IsNullOrEmpty(t.id) && Prefs.PinIds.Contains(t.id));
            v.Name.FontWeight = pin ? FontWeights.SemiBold : FontWeights.Medium;
            double inner = double.IsNaN(list.Width) ? 480 : list.Width;
            v.Name.MaxWidth = Math.Max(96, inner - 170);
            v.Name.Cursor = string.IsNullOrEmpty(t.folder) ? Cursors.Arrow : Cursors.Hand;
            string nsig = NameSig(t, pin);
            if (v.NameSig != nsig)
            {
                v.NameSig = nsig;
                FillName(v.Name, t, pin);
            }
            string right = Redact.Show(RightText(t));
            if (v.State.Text != right) v.State.Text = right;
            v.State.Foreground = WordState(t.state) ? StateInk(t.state) : Theme.Faint;
            v.Dot.Text = BoardText.Dot(t.state);
            v.Dot.Foreground = DotBrush(t.state);
            string why = Redact.Show(t.why ?? "");
            string dsig = DetailSig(t);
            if (v.DetailSig != dsig)
            {
                v.DetailSig = dsig;
                FillDetail(v.Detail, t);
            }
            bool any = v.Detail.Visibility == Visibility.Visible;
            v.Dot.Margin = new Thickness(0, 4, 0, any ? 0 : 4);
            v.Name.Margin = new Thickness(0, 4, 8, any ? 0 : 4);
            v.State.Margin = new Thickness(6, 4, 0, any ? 0 : 4);
            Tip(v.Dot, why);
            Tip(v.Name, why);
            Tip(v.State, why);
            Tip(v.Detail, why);
            NoteState(v, t, instant);
        }

        void PaintRoll(View v, Slot s, bool instant)
        {
            Thing t = s.Thing;
            if (t == null || v.Name == null) return;
            v.Thing = t;
            Remember(t);
            string label = ShownName(t);
            if (!string.IsNullOrEmpty(t.detail)) label += "  \u00B7  " + t.detail;
            label = Redact.Show(label);
            if (v.Name.Text != label) v.Name.Text = label;
            Tip(v.Name, Redact.Show(t.why ?? ""));
            double want = s.Open ? 90 : 0;
            if (v.Spin == null || Math.Abs(v.Spin.Angle - want) < 1) return;
            if (!instant && s.Open)
            {
                var a = new DoubleAnimation(0, 90, TimeSpan.FromMilliseconds(160));
                a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                v.Spin.BeginAnimation(RotateTransform.AngleProperty, a);
                return;
            }
            v.Spin.BeginAnimation(RotateTransform.AngleProperty, null);
            v.Spin.Angle = want;
        }

        void PaintChild(View v, Slot s)
        {
            if (v.Name == null) return;
            v.Thing = s.Thing;
            Remember(s.Thing);
            string label = s.Text;
            if (string.IsNullOrEmpty(label) && s.Thing != null) label = ShownName(s.Thing);
            label = Redact.Show(label ?? "");
            if (v.Name.Text != label) v.Name.Text = label;
            if (s.Thing == null) return;
            v.Name.Cursor = string.IsNullOrEmpty(s.Thing.folder) ? Cursors.Arrow : Cursors.Hand;
            Tip(v.Name, Redact.Show(s.Thing.why ?? ""));
        }

        static void FillName(TextBlock tb, Thing t, bool pin)
        {
            tb.Inlines.Clear();
            var name = new Run(Redact.Show(ShownName(t)));
            name.Foreground = Theme.Text;
            name.FontWeight = pin ? FontWeights.SemiBold : FontWeights.Medium;
            tb.Inlines.Add(name);
            string proj = Redact.Show(t.project ?? "");
            if (proj.Length > 0)
            {
                var tail = new Run("  " + proj);
                tail.Foreground = Theme.Sub;
                tail.FontWeight = FontWeights.Normal;
                tb.Inlines.Add(tail);
            }
            if (!string.IsNullOrEmpty(t.detail) || t.ports == null) return;
            foreach (int port in t.ports) AddPort(tb, port, " \u00B7 ");
        }

        static void AddPort(TextBlock tb, int port, string lead)
        {
            if (!string.IsNullOrEmpty(lead))
            {
                var gap = new Run(lead);
                gap.Foreground = Theme.Faint;
                tb.Inlines.Add(gap);
            }
            string shown = port.ToString(CultureInfo.InvariantCulture);
            var link = new Hyperlink(new Run(":" + shown));
            link.Foreground = Theme.Link;
            link.TextDecorations = new TextDecorationCollection();
            link.Click += delegate { OpenPort(shown); };
            tb.Inlines.Add(link);
        }

        void FillDetail(TextBlock tb, Thing t)
        {
            tb.Inlines.Clear();
            if (string.IsNullOrEmpty(t.detail))
            {
                tb.Visibility = Visibility.Collapsed;
                tb.Margin = new Thickness(0);
                return;
            }
            tb.Inlines.Add(new Run(Redact.Show(t.detail)));
            if (t.ports != null)
                foreach (int port in t.ports) AddPort(tb, port, "  \u00B7  ");
            tb.Visibility = Visibility.Visible;
            tb.Margin = new Thickness(0, 1, 8, 5);
        }

        void NoteState(View v, Thing t, bool instant)
        {
            if (v == null || t == null || string.IsNullOrEmpty(t.id)) return;
            string prev;
            bool known = seen.TryGetValue(t.id, out prev);
            string now = t.state ?? "";
            seen[t.id] = now;
            if (!known || prev == now || instant) return;
            Flash(v, now);
        }

        void Flash(View v, string state)
        {
            if (v.Wash == null || v.Flashing) return;
            v.Flashing = true;
            Color c = FlashColor(state);
            var a = new ColorAnimation(Color.FromArgb(0x48, c.R, c.G, c.B), Colors.Transparent, TimeSpan.FromMilliseconds(900));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            a.FillBehavior = FillBehavior.Stop;
            a.Completed += delegate
            {
                v.Flashing = false;
                if (v.Wash == null) return;
                v.Wash.BeginAnimation(SolidColorBrush.ColorProperty, null);
                v.Wash.Color = Colors.Transparent;
            };
            v.Wash.BeginAnimation(SolidColorBrush.ColorProperty, a);
        }

        void HookLine(View v, FrameworkElement el, bool opens)
        {
            if (el == null) return;
            el.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                OpenLine(v);
            };
            if (!opens) return;
            el.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (FromLink(e.OriginalSource)) return;
                if (v.Thing == null || string.IsNullOrEmpty(v.Thing.folder)) return;
                e.Handled = true;
                OpenCode(v.Thing.folder);
            };
        }

        void ToggleRoll(string id)
        {
            if (string.IsNullOrEmpty(id) || snapshotDir != null) return;
            if (expanded.Contains(id)) expanded.Remove(id);
            else expanded.Add(id);
            if (lastBoard != null) ShowList(lastBoard, false);
        }

        void OpenGear()
        {
            if (snapshotDir != null) return;
            if (menu != null && menu.IsOpen && menu.PlacementTarget == gearBtn)
            {
                menu.IsOpen = false;
                return;
            }
            ShowMenu(gearBtn, PlacementMode.Bottom, GearRows());
        }

        List<MenuRow> GearRows()
        {
            var rows = new List<MenuRow>();
            var head = new MenuRow();
            head.Label = "Hidden";
            head.Header = true;
            rows.Add(head);
            var ids = new List<string>(Prefs.Hides);
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string id in ids)
            {
                string captured = id;
                rows.Add(Item(Remembered(captured), delegate { Unhide(captured); }));
            }
            rows.Add(Item("Frontier Deployments", OpenFrontier));
            var ver = new MenuRow();
            ver.Label = ErrorLog.Ver;
            ver.Dim = true;
            rows.Add(ver);
            return rows;
        }

        void OpenLine(View v)
        {
            if (v == null || v.Thing == null || snapshotDir != null) return;
            Thing t = v.Thing;
            bool pinned = t.pinned || (!string.IsNullOrEmpty(t.id) && Prefs.PinIds.Contains(t.id));
            var rows = new List<MenuRow>();
            rows.Add(Item("Rename", string.IsNullOrEmpty(t.id) ? null : (Action)delegate { BeginRename(v); }));
            rows.Add(Item("Hide", string.IsNullOrEmpty(t.id) ? null : (Action)delegate { HideThing(t); }));
            rows.Add(Item(pinned ? "Unpin" : "Pin", string.IsNullOrEmpty(t.id) ? null : (Action)delegate { PinThing(t); }));
            ShowMenu(this, PlacementMode.MousePoint, rows);
        }

        static MenuRow Item(string label, Action act)
        {
            var r = new MenuRow();
            r.Label = label;
            r.Act = act;
            return r;
        }

        void ShowMenu(UIElement target, PlacementMode how, List<MenuRow> rows)
        {
            if (menu != null) menu.IsOpen = false;
            var panel = new StackPanel { Margin = new Thickness(6, 6, 6, 6) };
            foreach (MenuRow row in rows)
            {
                if (row == null || string.IsNullOrEmpty(row.Label)) continue;
                MenuRow pick = row;
                bool quiet = pick.Header || pick.Dim || pick.Act == null;
                var label = Theme.Label(pick.Label, pick.Header ? 11 : 13, quiet ? Theme.Faint : Theme.Text, pick.Header ? FontWeights.SemiBold : FontWeights.Normal);
                var item = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, pick.Header ? 3 : 6, 16, pick.Header ? 3 : 6), HorizontalAlignment = HorizontalAlignment.Stretch };
                if (pick.Label == "Frontier Deployments") item.Margin = new Thickness(0, 6, 0, 0);
                item.Child = label;
                if (pick.Act != null)
                {
                    item.Cursor = Cursors.Hand;
                    item.MouseEnter += delegate { item.Background = Theme.Hover; };
                    item.MouseLeave += delegate { item.Background = Brushes.Transparent; };
                    item.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                    {
                        e.Handled = true;
                        if (menu != null) menu.IsOpen = false;
                        pick.Act();
                    };
                }
                panel.Children.Add(item);
            }
            var box = new Border
            {
                MinWidth = 208,
                Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x14, 0x16, 0x1E)),
                BorderBrush = Theme.Edge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = panel
            };
            menu = new Popup();
            menu.AllowsTransparency = true;
            menu.StaysOpen = true;
            menu.PopupAnimation = PopupAnimation.Fade;
            menu.Placement = how;
            menu.PlacementTarget = how == PlacementMode.MousePoint ? (UIElement)this : (target ?? this);
            menu.VerticalOffset = how == PlacementMode.Bottom ? 4 : 0;
            menu.Child = box;
            menu.IsOpen = true;
        }

        bool InsideMenu(object src)
        {
            var d = src as DependencyObject;
            while (d != null)
            {
                if (menu != null && (d == menu.Child || d == menu.PlacementTarget)) return true;
                DependencyObject next = null;
                if (d is Visual) next = VisualTreeHelper.GetParent(d);
                if (next == null) next = LogicalTreeHelper.GetParent(d);
                d = next;
            }
            return false;
        }

        void Unhide(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            Prefs.Hides.Remove(id);
            Prefs.Save();
            if (lastBoard != null) ShowBoard(lastBoard);
        }

        void HideThing(Thing t)
        {
            if (t == null || string.IsNullOrEmpty(t.id) || snapshotDir != null) return;
            Prefs.Hides.Add(t.id);
            knownNames[t.id] = ShownName(t);
            Prefs.Save();
            if (lastBoard != null) ShowBoard(lastBoard);
        }

        void PinThing(Thing t)
        {
            if (t == null || string.IsNullOrEmpty(t.id) || snapshotDir != null) return;
            if (Prefs.PinIds.Contains(t.id)) Prefs.PinIds.Remove(t.id);
            else Prefs.PinIds.Add(t.id);
            t.pinned = Prefs.PinIds.Contains(t.id);
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
            if (t == null || string.IsNullOrEmpty(t.id)) return;
            knownNames[t.id] = ShownName(t);
        }

        void BeginRename(View v)
        {
            if (v == null || v.Name == null || rowEdit != null || snapshotDir != null || list == null) return;
            Thing t = v.Thing;
            if (t == null || string.IsNullOrEmpty(t.id)) return;
            var box = new TextBox();
            box.Text = ShownName(t);
            box.FontFamily = Theme.Font;
            box.FontSize = v.Name.FontSize;
            box.FontWeight = v.Name.FontWeight;
            box.Foreground = Theme.Text;
            box.CaretBrush = Theme.Text;
            box.Background = Brushes.Transparent;
            box.BorderThickness = new Thickness(0);
            box.Padding = new Thickness(0);
            box.MinWidth = 80;
            int row = Grid.GetRow(v.Name);
            int col = Grid.GetColumn(v.Name);
            list.Children.Remove(v.Name);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col);
            list.Children.Add(box);
            rowEdit = box;
            box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { e.Handled = true; FinishRename(v, true); }
                else if (e.Key == Key.Escape) { e.Handled = true; FinishRename(v, false); }
            };
            box.LostKeyboardFocus += delegate { FinishRename(v, true); };
            box.Focus();
            box.SelectAll();
        }

        void FinishRename(View v, bool save)
        {
            if (rowEdit == null || v == null || v.Name == null || list == null) return;
            TextBox box = rowEdit;
            rowEdit = null;
            string n = (box.Text ?? "").Trim();
            if (n.Length > 80) n = n.Substring(0, 80);
            if (save && n.Length > 0 && v.Thing != null && !string.IsNullOrEmpty(v.Thing.id))
            {
                Prefs.Rename[v.Thing.id] = n;
                Prefs.Save();
                knownNames[v.Thing.id] = n;
            }
            int row = Grid.GetRow(box);
            int col = Grid.GetColumn(box);
            if (list.Children.Contains(box)) list.Children.Remove(box);
            Grid.SetRow(v.Name, row);
            Grid.SetColumn(v.Name, col);
            if (!list.Children.Contains(v.Name)) list.Children.Add(v.Name);
            bool pin = v.Thing != null && (v.Thing.pinned || (!string.IsNullOrEmpty(v.Thing.id) && Prefs.PinIds.Contains(v.Thing.id)));
            v.NameSig = null;
            if (v.Thing != null) FillName(v.Name, v.Thing, pin);
        }

        void BeginNameEdit()
        {
            if (naming || snapshotDir != null || nameHost == null) return;
            naming = true;
            nameEdit = new TextBox();
            nameEdit.Text = string.IsNullOrEmpty(Prefs.Name) ? nameText.Text : Prefs.Name;
            nameEdit.FontFamily = Theme.Display;
            nameEdit.FontSize = 19;
            nameEdit.FontWeight = FontWeights.SemiBold;
            nameEdit.Foreground = Theme.Text;
            nameEdit.CaretBrush = Theme.Text;
            nameEdit.Background = Brushes.Transparent;
            nameEdit.BorderThickness = new Thickness(0);
            nameEdit.Padding = new Thickness(0);
            nameEdit.MinWidth = 120;
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
            TextBox box = nameEdit;
            nameEdit = null;
            string n = (box.Text ?? "").Trim();
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
            foreach (var e in new[] { new[] { "Cursor.exe", "Cursor" }, new[] { "Code.exe", "VS Code" } })
                foreach (string raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
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
            if (string.IsNullOrEmpty(path)) return false;
            if (path.IndexOf('"') >= 0 || path.IndexOf('\n') >= 0 || path.IndexOf('\r') >= 0 || path.IndexOf('%') >= 0) return false;
            try { return File.Exists(path) || Directory.Exists(path); }
            catch (Exception) { return false; }
        }

        static string QuotePath(string path)
        {
            return "\"" + path.TrimEnd('\\') + "\"";
        }

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

        // The chrome inset was shorter than the pane, so the last line was cut off.
        void FitSnapshotHeight()
        {
            if (pane == null) return;
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                chrome.CaptionHeight = 0;
                chrome.ResizeBorderThickness = new Thickness(0);
            }
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
                string txtPath = System.IO.Path.Combine(dir, "vitals.txt");
                string pngPath = System.IO.Path.Combine(dir, "vitals.png");
                ShowBoard(Engine.Current);
                UpdateLayout();
                FitSnapshotHeight();
                string text = BoardText.Render(Engine.Current);
                File.WriteAllText(txtPath, text, new UTF8Encoding(false));
                if (string.Equals(Environment.GetEnvironmentVariable("DESKTOPVITALS_PRIVACY_TEST"), "1", StringComparison.Ordinal))
                    WritePrivacyProof(dir);

                var root = (FrameworkElement)Content;
                double renderW = Math.Max(1, root.ActualWidth);
                root.Measure(new Size(renderW, double.PositiveInfinity));
                double renderH = Math.Max(root.ActualHeight, Math.Max(Height, root.DesiredSize.Height));
                root.Arrange(new Rect(0, 0, renderW, renderH));
                double scale = 2;
                double maxPx = 4000;
                if (renderW * scale > maxPx) scale = maxPx / renderW;
                if (renderH * scale > maxPx) scale = Math.Min(scale, maxPx / Math.Max(1, renderH));
                int bw = Math.Max(1, (int)Math.Ceiling(renderW * scale));
                int bh = Math.Max(1, (int)Math.Ceiling(renderH * scale));
                var bmp = new RenderTargetBitmap(bw, bh, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(pngPath)) enc.Save(fs);
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
                File.WriteAllText(System.IO.Path.Combine(StateDir, "window.txt"),
                    PlacementFormat.Join(new[] { r.Left, r.Top, r.Height, r.Width }));
            }
            catch (Exception) { }
        }

        public void Toggle()
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            if (WindowState != WindowState.Minimized && Native.GetForegroundWindow() == h)
            {
                WindowState = WindowState.Minimized;
                return;
            }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        static bool IsHidden(Thing t)
        {
            if (t == null) return true;
            if (!string.IsNullOrEmpty(t.id) && Prefs.Hides.Contains(t.id)) return true;
            if (!string.IsNullOrEmpty(t.name) && Prefs.Hides.Contains(t.name)) return true;
            return false;
        }

        static bool IsRollup(Thing t)
        {
            return t != null && !string.IsNullOrEmpty(t.name) && t.name.IndexOf(" on disk", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsChild(Thing roll, Thing t)
        {
            if (roll == null || t == null || string.IsNullOrEmpty(roll.id) || string.IsNullOrEmpty(t.id)) return false;
            return t.id.StartsWith(roll.id + ":", StringComparison.Ordinal);
        }

        static string[] SplitNames(string why)
        {
            if (string.IsNullOrEmpty(why)) return new string[0];
            string[] parts = why.Split(new[] { " \u00B7 " }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return new string[0];
            return parts;
        }

        static string ShownName(Thing t)
        {
            if (t == null) return "";
            string renamed;
            string baseName = t.name ?? "";
            if (!string.IsNullOrEmpty(t.id) && Prefs.Rename.TryGetValue(t.id, out renamed) && !string.IsNullOrEmpty(renamed))
                baseName = renamed;
            if (t.count > 1 && baseName.IndexOf(" on disk", StringComparison.OrdinalIgnoreCase) < 0)
                return baseName + " \u00D7" + t.count.ToString(CultureInfo.InvariantCulture);
            return baseName;
        }

        // A word only when the person is needed. Otherwise a number, or nothing.
        static string RightText(Thing t)
        {
            if (t == null) return "";
            string state = t.state ?? "";
            if (state == "ended") return "";
            if (WordState(state)) return BoardText.Phrase(state);
            if (!string.IsNullOrEmpty(t.since)) return t.since;
            if (t.cpu >= 5) return t.cpu.ToString("0", CultureInfo.InvariantCulture) + "%";
            if (t.vram_mb >= 200) return Megs(t.vram_mb) + " VRAM";
            if (t.ram_mb >= 200) return Megs(t.ram_mb);
            return "";
        }

        static bool WordState(string state)
        {
            return state == "waiting" || state == "quiet" || state == "failed";
        }

        static string NameSig(Thing t, bool pin)
        {
            var sb = new StringBuilder();
            sb.Append(pin ? "1" : "0").Append('|');
            sb.Append(ShownName(t)).Append('|');
            sb.Append(t.project ?? "").Append('|');
            sb.Append(t.detail ?? "").Append('|');
            if (string.IsNullOrEmpty(t.detail) && t.ports != null)
                foreach (int p in t.ports) sb.Append(p.ToString(CultureInfo.InvariantCulture)).Append(',');
            return sb.ToString();
        }

        static string Megs(double mb)
        {
            if (mb >= 10240) return (mb / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " GB";
            if (mb >= 1024) return (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return ((int)Math.Round(mb)).ToString(CultureInfo.InvariantCulture) + " MB";
        }

        static string DetailSig(Thing t)
        {
            var sb = new StringBuilder();
            sb.Append(t.detail ?? "").Append('|');
            if (!string.IsNullOrEmpty(t.detail) && t.ports != null)
                foreach (int p in t.ports) sb.Append(p.ToString(CultureInfo.InvariantCulture)).Append(',');
            return sb.ToString();
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
            if (cur == why) return;
            if (string.IsNullOrEmpty(cur) && string.IsNullOrEmpty(why)) return;
            el.ToolTip = string.IsNullOrEmpty(why) ? null : why;
        }

        static Brush DotBrush(string state)
        {
            switch (state)
            {
                case "working":
                case "running": return Theme.Green;
                case "waiting":
                case "quiet": return Theme.AmberDot;
                case "failed": return Theme.FailedDot;
                case "idle":
                case "ended": return Theme.Sub;
                default: return Theme.Sub;
            }
        }

        static Brush StateInk(string state)
        {
            switch (state)
            {
                case "waiting":
                case "quiet": return Theme.AmberDot;
                case "failed": return Theme.FailedDot;
                default: return Theme.Sub;
            }
        }

        static Color FlashColor(string state)
        {
            switch (state)
            {
                case "waiting":
                case "quiet": return Color.FromRgb(0xFF, 0xB5, 0x47);
                case "failed": return Color.FromRgb(0xFF, 0x5C, 0x5C);
                case "working":
                case "running": return Color.FromRgb(0x34, 0xD3, 0x6A);
                default: return Color.FromRgb(0xFF, 0xFF, 0xFF);
            }
        }

        class Slot
        {
            public string Kind;
            public string Key;
            public string Text;
            public Thing Thing;
            public bool Open;
        }

        class View
        {
            public TextBlock Dot, Name, State, Detail, Chevron;
            public RotateTransform Spin;
            public SolidColorBrush Wash;
            public bool Flashing;
            public string NameSig, DetailSig;
            public Thing Thing;
        }

        class MenuRow
        {
            public string Label;
            public bool Header;
            public bool Dim;
            public Action Act;
        }
    }

    static class PlacementFormat
    {
        public static string Join(double[] values)
        {
            var parts = new string[values.Length];
            for (int i = 0; i < values.Length; i++) parts[i] = values[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }
    }
}
