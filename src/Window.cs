// Glass window: one pane that draws a Board.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
        public static readonly Brush Green = Solid(0xFF, 0x34, 0xD3, 0x6A);
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

    class VitalsWindow : Window
    {
        static readonly string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopVitals");
        static readonly string[] Editor = FindEditor();

        readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background);
        readonly string snapshotDir;
        bool live = true;
        bool naming;
        TextBlock nameText, stripText, headlineText, stateLabel;
        TextBox nameEdit;
        StackPanel nameHost, list;
        Border switchTrack, pane;
        TranslateTransform knob;
        ScrollViewer scroll;

        public VitalsWindow() : this(null) { }

        // With a folder, the window renders off screen, writes vitals.txt and vitals.png, and closes.
        public VitalsWindow(string snapshotDir)
        {
            this.snapshotDir = snapshotDir;
            Title = "Desktop Vitals";
            Width = 440;
            SizeToContent = SizeToContent.Height;
            MaxHeight = snapshotDir == null ? SystemParameters.WorkArea.Height - 40 : double.PositiveInfinity;
            WindowStyle = WindowStyle.None;
            MinWidth = 340;
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
                    ShowBoard(Engine.Tick());
                    double sec = (DateTime.UtcNow - since).TotalSeconds;
                    if (sec < 2.5) return;
                    if (!Engine.TasksReady && sec < 12) return;
                    if (!Engine.ModelsReady && sec < 12) return;
                    done.Stop();
                    ShowBoard(Engine.Tick());
                    UpdateLayout();
                    WriteSnapshot();
                    Close();
                };
                Loaded += delegate { Engine.Prime(); done.Start(); };
            }

            timer.Interval = TimeSpan.FromMilliseconds(1500);
            timer.Tick += delegate { if (live && WindowState != WindowState.Minimized) ShowBoard(Engine.Tick()); };
            SourceInitialized += delegate { if (snapshotDir == null) ApplyGlass(); };
            if (snapshotDir == null) Loaded += delegate { Prime(); };
            StateChanged += delegate { UpdateRunning(); };
            Closing += delegate { if (snapshotDir == null) SavePlacement(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };
        }

        UIElement Build()
        {
            var root = new Grid();
            root.Background = snapshotDir == null && TransparencyOn()
                ? new SolidColorBrush(Color.FromArgb(0x52, 0x0C, 0x0E, 0x14))
                : new SolidColorBrush(Color.FromArgb(0xFF, 0x17, 0x1A, 0x21));

            var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            stack.Children.Add(BuildHeader());
            stripText = Theme.Label("", 12.5, Theme.Sub, FontWeights.Normal);
            stripText.TextWrapping = TextWrapping.Wrap;
            stripText.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(stripText);
            headlineText = Theme.Label("", 13.5, Theme.Sub, FontWeights.SemiBold);
            headlineText.TextWrapping = TextWrapping.Wrap;
            headlineText.Margin = new Thickness(0, 8, 0, 8);
            stack.Children.Add(headlineText);

            list = new StackPanel();
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
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            nameText = Theme.Label(Environment.MachineName, 19, Theme.Text, FontWeights.SemiBold);
            nameText.FontFamily = Theme.Display;
            nameText.Cursor = Cursors.Hand;
            nameText.MouseLeftButtonUp += delegate { BeginNameEdit(); };
            WindowChrome.SetIsHitTestVisibleInChrome(nameText, true);
            nameHost = new StackPanel();
            nameHost.Children.Add(nameText);
            g.Children.Add(nameHost);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(right, 1);
            var sw = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(10, 2, 6, 0) };
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
            right.Children.Add(CaptionButton("\uE921", "Minimize", delegate { WindowState = WindowState.Minimized; }));
            right.Children.Add(CaptionButton("\uE8BB", "Close", delegate { Close(); }));
            g.Children.Add(right);
            return g;
        }

        UIElement CaptionButton(string glyph, string tip, MouseButtonEventHandler click)
        {
            var b = new Border { Width = 28, Height = 30, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip };
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
            bool run = live && WindowState != WindowState.Minimized;
            if (run && !timer.IsEnabled) Prime();
            else if (!run && timer.IsEnabled)
            {
                timer.Stop();
                Engine.Reset();
                if (!live && stripText != null) stripText.Text = "Off";
                Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            }
        }

        void Prime()
        {
            if (!live || WindowState == WindowState.Minimized) return;
            Engine.Prime();
            var once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            once.Tick += delegate
            {
                once.Stop();
                if (!live || WindowState == WindowState.Minimized) return;
                ShowBoard(Engine.Tick());
                timer.Start();
            };
            once.Start();
        }

        void ShowBoard(Board board)
        {
            if (board == null || list == null) return;
            if (nameEdit == null && nameText != null)
                nameText.Text = string.IsNullOrEmpty(board.name) ? Environment.MachineName : board.name;
            if (stripText != null)
                stripText.Text = live ? BoardText.Strip(board.strip) : "Off";
            if (headlineText != null)
            {
                headlineText.Text = board.headline == null || board.headline.text == null ? "" : board.headline.text;
                headlineText.Foreground = board.headline != null && board.headline.needs_you ? Theme.AmberDot : Theme.Text;
            }
            list.Children.Clear();
            if (board.groups == null) return;
            foreach (Group g in board.groups)
            {
                var title = Theme.Label((g.title ?? "").ToUpperInvariant(), 11, Theme.Faint, FontWeights.SemiBold);
                title.Margin = new Thickness(0, 10, 0, 2);
                list.Children.Add(title);
                if (g.things == null) continue;
                foreach (Thing t in g.things) AddThing(t);
            }
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
                case "ended": return Theme.Faint;
                default: return Theme.Faint;
            }
        }

        void AddThing(Thing t)
        {
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = Theme.Label(BoardText.Dot(t.state), 13, DotBrush(t.state), FontWeights.Normal);
            dot.Margin = new Thickness(0, 0, 6, 0);
            var name = Theme.Label(BoardText.DisplayName(t), 13, Theme.Text, FontWeights.Normal);
            name.Cursor = Cursors.Hand;
            string folder = t.folder;
            name.MouseLeftButtonUp += delegate { OpenCode(folder); };
            line.Children.Add(dot);
            line.Children.Add(name);
            if (!string.IsNullOrEmpty(t.project))
            {
                var project = Theme.Label(t.project, 12, Theme.Sub, FontWeights.Normal);
                project.Margin = new Thickness(8, 0, 0, 0);
                line.Children.Add(project);
            }
            string status = BoardText.Phrase(t.state);
            if (!string.IsNullOrEmpty(t.since)) status += " \u00B7 " + t.since;
            var state = Theme.Label(status, 12, Theme.Sub, FontWeights.Normal);
            state.Margin = new Thickness(8, 0, 0, 0);
            line.Children.Add(state);
            if (t.ports != null)
            {
                foreach (int port in t.ports)
                {
                    int shown = port;
                    var link = Theme.Label(":" + shown.ToString(CultureInfo.InvariantCulture), 12, Theme.Link, FontWeights.Normal);
                    link.Margin = new Thickness(8, 0, 0, 0);
                    link.Cursor = Cursors.Hand;
                    link.MouseLeftButtonUp += delegate { OpenPort(shown.ToString(CultureInfo.InvariantCulture)); };
                    line.Children.Add(link);
                }
            }
            row.Children.Add(line);
            if (!string.IsNullOrEmpty(t.detail))
            {
                var detail = Theme.Label(t.detail, 12, Theme.Faint, FontWeights.Normal);
                detail.Margin = new Thickness(18, 0, 0, 0);
                row.Children.Add(detail);
            }
            if (!string.IsNullOrEmpty(t.why)) row.ToolTip = t.why;
            list.Children.Add(row);
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
            nameHost.Children.Clear();
            nameHost.Children.Add(nameText);
            naming = false;
        }

        void CancelName()
        {
            if (nameEdit == null) return;
            nameEdit = null;
            nameHost.Children.Clear();
            nameHost.Children.Add(nameText);
            naming = false;
        }

        static string[] FindEditor()
        {
            try
            {
                string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "cursor", "Cursor.exe");
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
                            string exe = Path.Combine(d, e[0]);
                            if (File.Exists(exe)) return new[] { exe, e[1] };
                            d = Path.GetDirectoryName(d);
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
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                string args = (File.Exists(path) ? "/select," : "") + QuotePath(path);
                using (Process.Start(new ProcessStartInfo(explorer, args) { UseShellExecute = false })) { }
            }
            catch (Exception) { }
        }

        static void OpenPort(string port)
        {
            try
            {
                using (Process.Start(new ProcessStartInfo("http://127.0.0.1:" + port) { UseShellExecute = true })) { }
            }
            catch (Exception) { }
        }

        void WriteSnapshot()
        {
            try
            {
                string dir = Path.GetFullPath(snapshotDir);
                Directory.CreateDirectory(dir);
                string txtPath = Path.Combine(dir, "vitals.txt");
                string pngPath = Path.Combine(dir, "vitals.png");
                ShowBoard(Engine.Current);
                UpdateLayout();
                string text = BoardText.Render(Engine.Current);
                File.WriteAllText(txtPath, text, new UTF8Encoding(false));
                if (string.Equals(Environment.GetEnvironmentVariable("DESKTOPVITALS_PRIVACY_TEST"), "1", StringComparison.Ordinal))
                    WritePrivacyProof(dir);

                var root = (FrameworkElement)Content;
                double scale = 2;
                double maxPx = 4000;
                if (root.ActualWidth * scale > maxPx) scale = maxPx / Math.Max(1, root.ActualWidth);
                if (root.ActualHeight * scale > maxPx) scale = Math.Min(scale, maxPx / Math.Max(1, root.ActualHeight));
                int bw = Math.Max(1, (int)Math.Ceiling(root.ActualWidth * scale));
                int bh = Math.Max(1, (int)Math.Ceiling(root.ActualHeight * scale));
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
            File.WriteAllText(Path.Combine(dir, "privacy.txt"), sb.ToString(), new UTF8Encoding(false));
        }

        void RestorePlacement()
        {
            try
            {
                string f = Path.Combine(StateDir, "window.txt");
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
                File.WriteAllText(Path.Combine(StateDir, "window.txt"),
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
