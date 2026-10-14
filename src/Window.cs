// Glass window and the list it draws.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
    // A friendly name and icon per exe, looked up once.
    class AppIdentity
    {
        public string Title;
        public ImageSource Icon;
        static readonly Dictionary<string, AppIdentity> cache = new Dictionary<string, AppIdentity>(StringComparer.OrdinalIgnoreCase);

        public static AppIdentity For(string exe, IEnumerable<int> pids)
        {
            AppIdentity id;
            if (cache.TryGetValue(exe, out id)) return id;
            id = new AppIdentity();
            id.Title = System.IO.Path.GetFileNameWithoutExtension(exe);
            string path = null;
            foreach (int pid in pids.Take(3)) { path = Procs.ImagePath(pid); if (path != null) break; }
            if (path != null)
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    string d = (vi.FileDescription ?? "").Trim();
                    if (d.Length > 1 && d.Length < 40 && !d.Contains(".exe")) id.Title = d;
                }
                catch (Exception) { }
                try
                {
                    using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(path))
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        id.Icon = src;
                    }
                }
                catch (Exception) { }
            }
            cache[exe] = id;
            return id;
        }
    }

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
        public static readonly Brush Link = Solid(0xFF, 0x6F, 0xC8, 0xFF);
        public static readonly Brush AmberDot = Solid(0xFF, 0xFF, 0xB5, 0x47);
        public static readonly Brush Off = Solid(0x33, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Blue = Gradient(Color.FromRgb(0x5E, 0x9B, 0xFF), Color.FromRgb(0x6F, 0xE0, 0xFF), true);
        public static readonly Brush Amber = Gradient(Color.FromRgb(0xFF, 0x9F, 0x2E), Color.FromRgb(0xFF, 0xCF, 0x5C), true);
        public static readonly Brush Red = Gradient(Color.FromRgb(0xFF, 0x45, 0x5A), Color.FromRgb(0xFF, 0x7E, 0x6B), true);
        public static readonly Brush BlueV = Gradient(Color.FromRgb(0x6F, 0xE0, 0xFF), Color.FromRgb(0x5E, 0x9B, 0xFF), false);
        public static readonly Brush AmberV = Gradient(Color.FromRgb(0xFF, 0xCF, 0x5C), Color.FromRgb(0xFF, 0x9F, 0x2E), false);
        public static readonly Brush RedV = Gradient(Color.FromRgb(0xFF, 0x7E, 0x6B), Color.FromRgb(0xFF, 0x45, 0x5A), false);
        public static readonly Brush Edge = Rim();

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
        // One color per kind of thing, kept apart from the green / amber / grey status dots.
        public static readonly Dictionary<string, Color> Kinds = new Dictionary<string, Color>
        {
            { "Model", Color.FromRgb(0xC3, 0x9B, 0xFF) },
            { "Agent", Color.FromRgb(0x5F, 0xD8, 0xE8) },
            { "App", Color.FromRgb(0x8F, 0xB2, 0xFF) },
            { "Bot", Color.FromRgb(0xFF, 0x8A, 0xC4) },
            { "Watching", Color.FromRgb(0xC8, 0xCD, 0xD6) },
            { "Script", Color.FromRgb(0xFF, 0xB8, 0x8C) },
            { "Task", Color.FromRgb(0xA9, 0xC4, 0xD8) },
            { "Start", Color.FromRgb(0x9F, 0xE8, 0xB4) },
        };

        // What a kind is called on screen. "Watching" stays the word in watch.txt; on screen it reads Pinned.
        public static string Say(string kind) { return kind == "Watching" ? "Pinned" : kind; }
        public static readonly string[] AiKinds = { "Bot", "Agent", "App", "Model", "Watching" };

        public static Border Tag(string kind)
        {
            var b = new Border { Child = Label("", 10.5, Sub, FontWeights.SemiBold), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 1), VerticalAlignment = VerticalAlignment.Center };
            Paint(b, kind);
            return b;
        }

        public static void Paint(Border tag, string kind)
        {
            Color c = Kinds[kind];
            var t = (TextBlock)tag.Child;
            t.Text = Say(kind);
            t.Foreground = new SolidColorBrush(c);
            tag.Background = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
        }
        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI");

        static Brush Solid(byte a, byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b)); br.Freeze(); return br;
        }
        static Brush Gradient(Color a, Color b, bool horizontal)
        {
            var br = new LinearGradientBrush(a, b, horizontal ? new Point(0, 0) : new Point(0, 0), horizontal ? new Point(1, 0) : new Point(0, 1));
            br.Freeze(); return br;
        }

        public static TextBlock Label(string s, double size, Brush color, FontWeight weight)
        {
            var t = new TextBlock();
            t.Text = s; t.FontSize = size; t.Foreground = color; t.FontWeight = weight; t.FontFamily = Font;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.VerticalAlignment = VerticalAlignment.Center;
            System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            return t;
        }

        public static string N(double v, string fmt) { return v.ToString(fmt, CultureInfo.InvariantCulture); }
        public static string Gb(double bytes) { double gb = bytes / 1073741824.0; return gb >= 100 ? N(gb, "0") : N(gb, "0.0"); }
        public static string Mem(long bytes) { double mb = bytes / 1048576.0; return mb >= 1024 ? N(mb / 1024, "0.0") + " GB" : N(mb, "0") + " MB"; }
        public static string Pct(double v) { return v < 0.05 ? "0%" : v < 10 ? N(v, "0.0") + "%" : N(v, "0") + "%"; }
    }

    // A thin bar that eases to its new length.
    // One thin column per processor thread, grouped by core: performance cores first, then efficiency cores.

    class ThreadStrip : StackPanel
    {
        readonly Dictionary<int, ScaleTransform> fills = new Dictionary<int, ScaleTransform>();
        readonly Dictionary<int, Border> bars = new Dictionary<int, Border>();
        readonly Dictionary<int, string> names = new Dictionary<int, string>();

        public ThreadStrip(List<KeyValuePair<int, int[]>> cores)
        {
            Margin = new Thickness(0, 2, 0, 6);
            int top = cores.Count == 0 ? 0 : cores.Max(c => c.Key);
            bool hybrid = cores.Any(c => c.Key != top);
            int pThreads = cores.Where(c => c.Key == top).Sum(c => c.Value.Length);
            int eThreads = cores.Where(c => c.Key != top).Sum(c => c.Value.Length);
            string text = cores.Sum(c => c.Value.Length) + " threads";
            if (hybrid) text += "  \u00B7  " + pThreads + " performance, " + eThreads + " efficiency";
            var caption = Theme.Label(text, 11.5, Theme.Faint, FontWeights.Normal);
            caption.Margin = new Thickness(0, 0, 0, 5);
            Children.Add(caption);

            var row = new Grid { Height = 28 };
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
                    names[cpu] = "Thread " + cpu + "  \u00B7  " + (!hybrid ? "core " : c.Key == top ? "performance core " : "efficiency core ") + coreNo;
                    cell.ToolTip = names[cpu];
                    Grid.SetColumn(cell, col++);
                    row.Children.Add(cell);
                    fills[cpu] = sc; bars[cpu] = f;
                }
            }
            Children.Add(row);
        }

        public readonly Dictionary<int, double> Last = new Dictionary<int, double>();

        public string Busiest()
        {
            if (Last.Count == 0) return "";
            var top = Last.OrderByDescending(kv => kv.Value).First();
            return names[top.Key] + " at " + Theme.Pct(top.Value * 100);
        }

        public void Set(int cpu, double frac)
        {
            ScaleTransform sc;
            if (!fills.TryGetValue(cpu, out sc)) return;
            frac = Math.Max(0, Math.Min(1, frac));
            Last[cpu] = frac;
            Brush b = frac >= 0.9 ? Theme.RedV : frac >= 0.75 ? Theme.AmberV : Theme.BlueV;
            if (bars[cpu].Background != b) bars[cpu].Background = b;
            ((FrameworkElement)bars[cpu].Parent).ToolTip = names[cpu] + "  \u00B7  " + Theme.Pct(frac * 100);
            var a = new DoubleAnimation(frac, TimeSpan.FromMilliseconds(500));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

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

        public void Set(double frac)
        {
            if (double.IsNaN(frac)) frac = 0;
            frac = Math.Max(0, Math.Min(1, frac));
            Brush b = frac >= 0.9 ? Theme.Red : frac >= 0.75 ? Theme.Amber : Theme.Blue;
            if (fill.Background != b) fill.Background = b;
            if (Math.Abs(frac - current) < 0.003) return;
            current = frac;
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
        readonly string label;

        public string Summary { get { return label + "  " + value.Text + (detail.Text.Length > 0 ? "  \u00B7  " + detail.Text : ""); } }

        public StatRow(string label)
        {
            this.label = label;
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
            g.Children.Add(l); g.Children.Add(detail); g.Children.Add(value);
            bar.Margin = new Thickness(0, 7, 0, 0);
            Children.Add(g);
            Children.Add(bar);
        }

        public void Set(string d, string v, double frac)
        {
            if (detail.Text != d) detail.Text = d;
            if (value.Text != v) value.Text = v;
            bar.Set(frac);
        }
    }

    // One line per app or watched item: icon, name, count, CPU, memory.

    class ProcRow : Border
    {
        readonly Image icon = new Image();
        readonly Ellipse dot = new Ellipse();
        readonly TextBlock name, count, cpu, mem, kindText;
        readonly Border kindTag = new Border();
        public string Kind;         // set on rows in the AI card: Model, Agent, App, Bot, Watching
        public int[] Pids = new int[0];
        public string Title = "";
        public int State = -1;
        public WatchRule Bot;
        public string Script;       // script key for rows in Scripts
        public TaskInfo Task;
        public Func<ProcRow, string> HintFor;
        bool hovering;
        string countText = "";

        public string Summary
        {
            get
            {
                var parts = new[] { count.Text, cpu.Text, mem.Text }.Where(x => x.Length > 0);
                string mark = State == 1 ? "[ok] " : State == 2 ? "[QUIET] " : State == 0 ? "[DOWN] " : "";
                if (Kind != null) mark += Kind + ": ";
                return mark + name.Text + "  " + string.Join("  \u00B7  ", parts);
            }
        }

        public ProcRow()
        {
            Padding = new Thickness(8, 4, 8, 4);
            Margin = new Thickness(-8, 0, -8, 0);
            CornerRadius = new CornerRadius(10);
            Background = Brushes.Transparent;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            icon.Width = icon.Height = 16;
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            dot.Width = dot.Height = 7;
            dot.HorizontalAlignment = HorizontalAlignment.Left;
            dot.Margin = new Thickness(4, 0, 0, 0);
            dot.Visibility = Visibility.Collapsed;
            var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
            name = Theme.Label("", 13, Theme.Text, FontWeights.Normal);
            count = Theme.Label("", 12, Theme.Faint, FontWeights.Normal);
            count.Margin = new Thickness(6, 1, 0, 0);
            kindText = Theme.Label("", 10.5, Theme.Sub, FontWeights.SemiBold);
            kindTag.Child = kindText;
            kindTag.CornerRadius = new CornerRadius(8);
            kindTag.Padding = new Thickness(7, 0, 7, 1);
            kindTag.Margin = new Thickness(7, 1, 0, 0);
            kindTag.VerticalAlignment = VerticalAlignment.Center;
            kindTag.Visibility = Visibility.Collapsed;
            nameLine.Children.Add(name); nameLine.Children.Add(kindTag); nameLine.Children.Add(count);
            cpu = Theme.Label("", 12.5, Theme.Text, FontWeights.Normal);
            cpu.HorizontalAlignment = HorizontalAlignment.Right;
            mem = Theme.Label("", 12.5, Theme.Sub, FontWeights.Normal);
            mem.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(nameLine, 1); Grid.SetColumn(cpu, 2); Grid.SetColumn(mem, 3);
            g.Children.Add(icon); g.Children.Add(dot); g.Children.Add(nameLine); g.Children.Add(cpu); g.Children.Add(mem);
            Child = g;
            MouseEnter += delegate
            {
                Background = Theme.Hover;
                string hint = HintFor == null ? "" : HintFor(this);
                if (hint.Length == 0) return;
                hovering = true;
                count.Text = hint;
                count.Foreground = Theme.Link;
            };
            MouseLeave += delegate
            {
                Background = Brushes.Transparent;
                hovering = false;
                count.Text = countText;
                count.Foreground = Theme.Faint;
            };
        }

        public void SetApp(ImageSource img, string title, int n, double cpuPct, long bytes, string tip)
        {
            Title = title;
            dot.Visibility = Visibility.Collapsed;
            icon.Visibility = Visibility.Visible;
            if (icon.Source != img) icon.Source = img;
            Fill(title, n > 1 ? "\u00D7" + n : "", Theme.Pct(cpuPct), Theme.Mem(bytes), Theme.Text, tip);
        }

        public void SetWatched(string title, bool running, int n, double cpuPct, long bytes, string tip)
        {
            Title = title;
            icon.Visibility = Visibility.Collapsed;
            dot.Visibility = Visibility.Visible;
            dot.Fill = running ? Theme.Green : Theme.Off;
            if (running) Fill(title, n > 1 ? "\u00D7" + n : "", Theme.Pct(cpuPct), Theme.Mem(bytes), Theme.Text, tip);
            else Fill(title, "", "", "not running", Theme.Faint, null);
        }

        public void SetPlain(string title, Brush dotColor, string detail, string a, string b, string tip)
        {
            Title = title;
            icon.Visibility = Visibility.Collapsed;
            dot.Visibility = Visibility.Visible;
            dot.Fill = dotColor;
            Fill(title, detail, a, b, Theme.Text, tip);
            if (mem.Foreground != Theme.Sub) mem.Foreground = Theme.Sub;
        }

        // state: 0 not running, 1 working, 2 running but quiet or failing.
        public void SetBot(string title, int state, string detail, string cpuText, string right, string tip)
        {
            Title = title;
            State = state;
            icon.Visibility = Visibility.Collapsed;
            dot.Visibility = Visibility.Visible;
            dot.Fill = state == 1 ? Theme.Green : state == 2 ? Theme.AmberDot : Theme.Off;
            Fill(title, detail, cpuText, right, state == 0 ? Theme.Faint : Theme.Text, tip);
            var want = state == 2 ? Theme.AmberDot : Theme.Sub;
            if (mem.Foreground != want) mem.Foreground = want;
        }

        // A line in the AI card. state: 0 not running, 1 running or working, 2 running but quiet or failing.
        public void SetAi(string title, string kind, int state, string detail, string cpuText, string memText, string tip)
        {
            Title = title;
            State = kind == "Bot" ? state : -1;
            icon.Visibility = Visibility.Collapsed;
            dot.Visibility = Visibility.Visible;
            dot.Fill = state == 1 ? Theme.Green : state == 2 ? Theme.AmberDot : Theme.Off;
            if (Kind != kind)
            {
                Kind = kind;
                Color c = Theme.Kinds[kind];
                kindText.Text = Theme.Say(kind);
                kindText.Foreground = new SolidColorBrush(c);
                kindTag.Background = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
                kindTag.Visibility = Visibility.Visible;
            }
            Fill(title, detail, cpuText, memText, state == 0 ? Theme.Faint : Theme.Text, tip);
            var want = state == 2 ? Theme.AmberDot : Theme.Sub;
            if (mem.Foreground != want) mem.Foreground = want;
        }

        void Fill(string t, string c, string cp, string m, Brush color, string tip)
        {
            if (name.Text != t) name.Text = t;
            if (name.Foreground != color) name.Foreground = color;
            countText = c;
            if (!hovering && count.Text != c) count.Text = c;
            if (cpu.Text != cp) cpu.Text = cp;
            if (mem.Text != m) mem.Text = m;
            string safe = tip == null ? null : Privacy.Show(tip);
            if (!object.Equals(ToolTip, safe)) ToolTip = safe;
        }
    }

    // Shortest-column card flow; columns at most MaxCol, centered.

    class CardFlow : Panel
    {
        const double MinCol = 360, MaxCol = 540, Gap = 12;
        double colW, left;
        int cols;

        void Plan(double width)
        {
            if (double.IsInfinity(width) || width <= 0) width = MinCol;
            cols = Math.Max(1, (int)((width + Gap) / (MinCol + Gap)));
            int shown = InternalChildren.Cast<UIElement>().Count(c => c.Visibility != Visibility.Collapsed);
            cols = Math.Max(1, Math.Min(cols, shown));
            colW = Math.Min(MaxCol, (width - Gap * (cols - 1)) / cols);
            left = Math.Max(0, (width - (colW * cols + Gap * (cols - 1))) / 2);
        }

        // Where each card goes: its column and top, in the order the cards were added.
        List<Point> Place(Func<UIElement, double> height, out double tallest)
        {
            var tops = new double[cols];
            var spots = new List<Point>();
            foreach (UIElement c in InternalChildren)
            {
                if (c.Visibility == Visibility.Collapsed) { spots.Add(new Point(-1, 0)); continue; }
                int col = 0;
                for (int i = 1; i < cols; i++) if (tops[i] < tops[col] - 1) col = i;
                spots.Add(new Point(col, tops[col]));
                tops[col] += height(c) + Gap;
            }
            tallest = Math.Max(0, tops.Max() - Gap);
            return spots;
        }

        protected override Size MeasureOverride(Size available)
        {
            Plan(available.Width);
            foreach (UIElement c in InternalChildren) c.Measure(new Size(colW, double.PositiveInfinity));
            double tallest;
            Place(c => c.DesiredSize.Height, out tallest);
            return new Size(double.IsInfinity(available.Width) ? colW : available.Width, tallest);
        }

        protected override Size ArrangeOverride(Size final)
        {
            Plan(final.Width);
            double tallest;
            var spots = Place(c => c.DesiredSize.Height, out tallest);
            for (int i = 0; i < InternalChildren.Count; i++)
            {
                var c = InternalChildren[i];
                if (spots[i].X < 0) { c.Arrange(new Rect(0, 0, 0, 0)); continue; }
                c.Arrange(new Rect(left + spots[i].X * (colW + Gap), spots[i].Y, colW, c.DesiredSize.Height));
            }
            return final;
        }
    }

    class Group
    {
        public string Key;
        public List<Proc> Items = new List<Proc>();
        public double Cpu;
        public long Mem;
        public int[] Pids { get { return Items.Select(p => p.Pid).ToArray(); } }
    }

    // One line on the review screen.

    class ReviewItem
    {
        public string Name, Kind, OrigKind, Detail, Match, Section;
        public bool On = true;
        public WatchRule Rule;
        public string[] Kinds;      // what clicking the tag cycles through; null when the kind is fixed
    }

    // One line in the AI card: a model, agent or app found running, or an AI line from your settings.

    class AiLine
    {
        public string Kind, Name;
        public List<Proc> Procs = new List<Proc>();
        public WatchRule Rule;
        public InstalledModel Model;   // a model on disk; Procs are what has it loaded
        public bool GpuOnly;           // a script holding video memory with no model found for it
    }

    class VitalsWindow : Window
    {
        const int TopCount = 9;
        static readonly string StateDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopVitals");
        static readonly string[] ModelExes = { "llama-server.exe", "llama-cli.exe", "ollama.exe", "ollama_llama_server.exe", "koboldcpp.exe", "lms.exe", "LM Studio.exe" };

        readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background);
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly int cores = Environment.ProcessorCount;
        List<WatchRule> rules, botRules, aiRules;
        HashSet<string> hidden;                 // "@hide = <name>": found items you unticked in the review
        Dictionary<string, string> kindOf;      // "@kind = <name> | Agent": found items you re-tagged
        readonly Dictionary<string, BotStatus> botStatus = new Dictionary<string, BotStatus>();
        Dictionary<int, Proc> prev = new Dictionary<int, Proc>();
        readonly Dictionary<long, string> cmdCache = new Dictionary<long, string>();
        long prevTicks, prevIdle, prevKernel, prevUser;
        double cpuTotal;
        int tickCount;
        bool live = true;
        bool sortByMemory;
        string diskDetail = "Reading disk";
        double diskFrac;
        string diskValue = "";

        TextBlock subtitle, stateLabel;
        Border switchTrack;
        TranslateTransform knob;
        CardFlow body;
        StackPanel aiList, botList, watchList, topList;
        StatRow cpuRow, memRow, gpuRow, vramRow, diskRow;
        ThreadStrip threadStrip;
        long[] threadBusy, threadTotal, prevThreadBusy, prevThreadTotal;
        readonly List<ProcRow> aiRows = new List<ProcRow>();
        readonly List<ProcRow> watchRows = new List<ProcRow>();
        readonly List<ProcRow> botRows = new List<ProcRow>();
        readonly List<ProcRow> topRows = new List<ProcRow>();
        readonly List<ProcRow> scriptRows = new List<ProcRow>();
        readonly List<ProcRow> taskRows = new List<ProcRow>();
        StackPanel scriptList, taskList;
        Border aiCard, scriptsCard, tasksCard, startCard;
        StackPanel startList;
        readonly List<ProcRow> startRows = new List<ProcRow>();
        readonly Dictionary<long, string[]> aiCache = new Dictionary<long, string[]>();
        static readonly List<string> KindOrder = new List<string> { "Bot", "Agent", "App", "Model", "Watching" };
        List<AiLine> lastAi = new List<AiLine>();
        List<KeyValuePair<string, List<Proc>>> lastScripts = new List<KeyValuePair<string, List<Proc>>>();
        List<Proc> lastProcs = new List<Proc>();
        Grid rootGrid;
        ScrollViewer dashScroll;
        UIElement reviewView;
        TextBlock reviewError;
        bool needReview;
        bool acceptReview;
        bool configSkipped;
        static readonly string[] AiChoices = { "Agent", "App", "Model", "Bot", "Watching" };
        static readonly string[] ScriptChoices = { "Script", "Agent", "Bot" };
        static readonly string[] WatchChoices = { "Watching", "App", "Agent", "Bot", "Model" };
        readonly Dictionary<long, string> scriptCache = new Dictionary<long, string>();
        List<InstalledModel> installed = new List<InstalledModel>();
        DateTime modelsRead = DateTime.MinValue;
        bool modelsBusy;
        HashSet<string> scriptFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // where running scripts live, as of the last model scan
        DateTime configTime;   // watch.txt's last change, to read it again when it changes
        readonly Dictionary<long, string> useCache = new Dictionary<long, string>();      // command line + configs it names
        readonly Dictionary<long, string> sourceCache = new Dictionary<long, string>();   // script source, for GPU users
        Dictionary<string, string> settings;
        static readonly string DiskRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory);

        readonly string snapshotDir;

        public VitalsWindow() : this(null, false, false) { }

        // With a folder, the window renders off screen, writes vitals.txt and vitals.png there, and closes.
        public VitalsWindow(string snapshotDir, bool review) : this(snapshotDir, review, false) { }

        public VitalsWindow(string snapshotDir, bool review, bool acceptReview)
        {
            this.snapshotDir = snapshotDir;
            this.acceptReview = acceptReview;
            LoadConfig();
            // First run: once the first readings are in, show what was found and ask if it's right.
            needReview = review || (snapshotDir == null && !settings.ContainsKey("reviewed"));
            Title = "Desktop Vitals";
            Width = 400;
            SizeToContent = SizeToContent.Height;
            MaxHeight = SystemParameters.WorkArea.Height - 40;
            WindowStyle = WindowStyle.None;
            MinWidth = 340;
            MinHeight = 360;
            FontFamily = Theme.Font;
            Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            var chrome = new WindowChrome();
            chrome.CaptionHeight = 58;
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
                Left = -20000; Top = 0;
                MaxHeight = double.PositiveInfinity;
                ShowActivated = false;
                ShowInTaskbar = false;
                // Wait until Task Scheduler and the model scan finish (or 12 s), so vitals.txt is not empty of both.
                var since = DateTime.UtcNow;
                var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                done.Tick += delegate
                {
                    double sec = (DateTime.UtcNow - since).TotalSeconds;
                    bool models = (!modelsBusy && modelsRead != DateTime.MinValue) || sec >= 12;
                    bool tasks = TaskList.Ready || sec >= 12;
                    if (sec < 2.5 || !models || !tasks) return;
                    done.Stop();
                    // Snapshot can finish at 2.5s; open review before the PNG so --review is not a board shot.
                    if (needReview) { needReview = false; ShowReview(); }
                    if (acceptReview) { acceptReview = false; WriteAcceptedReview(); }
                    if (reviewView != null) { UpdateLayout(); }
                    WriteSnapshot();
                    Close();
                };
                Loaded += delegate { done.Start(); };
            }

            timer.Interval = TimeSpan.FromMilliseconds(1500);
            timer.Tick += delegate { Tick(); };
            SourceInitialized += delegate { if (snapshotDir == null) ApplyGlass(); };
            Loaded += delegate { Prime(); };
            StateChanged += delegate { UpdateRunning(); };
            Closing += delegate { if (snapshotDir == null) SavePlacement(); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        void LoadConfig()
        {
            WatchRule.ConfigTooLarge = false;
            var watch = WatchRule.ReadLines(WatchRule.ConfigFile());
            var review = WatchRule.ReadLines(WatchRule.ReviewFile());
            configSkipped = WatchRule.ConfigTooLarge;
            var all = WatchRule.ParseRules(watch);
            rules = all.Where(r => !r.IsBot && !r.Ai).ToList();
            botRules = all.Where(r => r.IsBot && !r.Ai).ToList();
            aiRules = all.Where(r => r.Ai).ToList();
            settings = WatchRule.ParseSettings(watch);
            foreach (var kv in WatchRule.ParseSettings(review)) settings[kv.Key] = kv.Value;
            hidden = new HashSet<string>(WatchRule.ParseMany(watch, "hide"), StringComparer.OrdinalIgnoreCase);
            foreach (string h in WatchRule.ParseMany(review, "hide")) hidden.Add(h);
            kindOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action<List<string>> takeKinds = delegate(List<string> lines)
            {
                foreach (string v in WatchRule.ParseMany(lines, "kind"))
                {
                    int bar = v.LastIndexOf('|');
                    if (bar <= 0) continue;
                    string k = Theme.Kinds.Keys.FirstOrDefault(x => x.Equals(v.Substring(bar + 1).Trim(), StringComparison.OrdinalIgnoreCase));
                    if (k != null) kindOf[v.Substring(0, bar).Trim()] = k;
                }
            };
            takeKinds(watch);
            takeKinds(review);
        }

        // ---------- layout ----------

        UIElement Build()
        {
            var root = rootGrid = new Grid();
            root.Background = snapshotDir == null && TransparencyOn() ? new SolidColorBrush(Color.FromArgb(0x52, 0x0C, 0x0E, 0x14)) : new SolidColorBrush(Color.FromArgb(0xFF, 0x17, 0x1A, 0x21));
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            root.Children.Add(BuildHeader());

            body = new CardFlow { Margin = new Thickness(14, 2, 14, 14) };
            var scroll = dashScroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            scroll.Content = body;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            cpuRow = new StatRow("Processor");
            memRow = new StatRow("Memory");
            gpuRow = new StatRow("Graphics");
            vramRow = new StatRow("Video memory");
            diskRow = new StatRow("Disk " + DiskRoot.TrimEnd('\\'));
            threadStrip = new ThreadStrip(Native.CoreLayout());
            threadBusy = new long[cores]; threadTotal = new long[cores];
            prevThreadBusy = new long[cores]; prevThreadTotal = new long[cores];
            body.Children.Add(Card("System", null, cpuRow, threadStrip, memRow, gpuRow, vramRow, diskRow));

            aiList = new StackPanel();
            aiCard = Card("AI", Columns(Head("CPU"), Head("Memory")), aiList);
            aiCard.Visibility = Visibility.Collapsed;
            body.Children.Add(aiCard);

            botList = new StackPanel();
            if (botRules.Count > 0) body.Children.Add(Card("Bot", Columns(Head("CPU"), Head("Last work")), botList));
            scriptList = new StackPanel();
            scriptsCard = Card("Script", Columns(Head("CPU"), Head("Memory")), scriptList);
            scriptsCard.Visibility = Visibility.Collapsed;
            body.Children.Add(scriptsCard);

            startList = new StackPanel();
            startCard = Card("Start", Columns(Head("CPU"), Head("Memory")), startList);
            startCard.Visibility = Visibility.Collapsed;
            body.Children.Add(startCard);

            taskList = new StackPanel();
            tasksCard = Card("Task", Columns(Head("Result"), Head("Last run")), taskList);
            tasksCard.Visibility = Visibility.Collapsed;
            body.Children.Add(tasksCard);

            watchList = new StackPanel();
            if (rules.Count > 0) body.Children.Add(Card("Watching", Columns(Head("CPU"), Head("Memory")), watchList));

            topList = new StackPanel();
            // Click a column label to sort by it. The active one is bright and marked with an arrow.
            var cpuHead = Head("CPU \u2193");
            var memHead = Head("Memory");
            Action showSort = delegate
            {
                cpuHead.Text = sortByMemory ? "CPU" : "CPU \u2193";
                memHead.Text = sortByMemory ? "Memory \u2193" : "Memory";
                cpuHead.Foreground = sortByMemory ? Theme.Faint : Theme.Text;
                memHead.Foreground = sortByMemory ? Theme.Text : Theme.Faint;
            };
            foreach (var h in new[] { cpuHead, memHead })
            {
                bool byMem = h == memHead;
                h.Cursor = Cursors.Hand;
                h.ToolTip = byMem ? "Sort by memory" : "Sort by CPU";
                h.MouseLeftButtonUp += delegate { sortByMemory = byMem; showSort(); if (live) Tick(); };
            }
            showSort();
            body.Children.Add(Card("Top apps", Columns(cpuHead, memHead), topList));
            return root;
        }

        UIElement BuildHeader()
        {
            var g = new Grid { Margin = new Thickness(18, 14, 12, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titles = new StackPanel();
            var title = Theme.Label(Environment.MachineName, 19, Theme.Text, FontWeights.SemiBold);
            title.FontFamily = Theme.Display;
            subtitle = Theme.Label("", 12, Theme.Sub, FontWeights.Normal);
            titles.Children.Add(title);
            titles.Children.Add(subtitle);
            g.Children.Add(titles);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(right, 1);

            // The on/off switch. Off stops every reading until it is turned back on.
            var sw = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(10, 4, 6, 0) };
            stateLabel = Theme.Label("Live", 12, Theme.Sub, FontWeights.Normal);
            stateLabel.Margin = new Thickness(0, 0, 8, 0);
            stateLabel.Width = 26;
            switchTrack = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(11), Background = Theme.Green, VerticalAlignment = VerticalAlignment.Center };
            var k = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
            knob = new TranslateTransform(20, 0);
            k.RenderTransform = knob;
            switchTrack.Child = new Grid { Margin = new Thickness(0), Children = { k }, VerticalAlignment = VerticalAlignment.Center };
            k.Margin = new Thickness(0, 0, 0, 0);
            sw.Children.Add(stateLabel);
            sw.Children.Add(switchTrack);
            sw.ToolTip = "Turn readings on or off";
            sw.MouseLeftButtonUp += delegate { SetLive(!live); };
            WindowChrome.SetIsHitTestVisibleInChrome(sw, true);
            right.Children.Add(sw);

            right.Children.Add(CaptionButton("\uE72C", "Look again now: new scripts, models, tasks and settings", delegate { RefreshAll(); }));
            right.Children.Add(CaptionButton("\uE713", "Review what's shown", delegate { ShowReview(); }));
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

        static TextBlock Head(string text)
        {
            var t = Theme.Label(text, 12, Theme.Faint, FontWeights.Normal);
            t.HorizontalAlignment = HorizontalAlignment.Right;
            return t;
        }

        // Sits over a list's two number columns, which are 56 and 84 wide in ProcRow.
        static Grid Columns(TextBlock a, TextBlock b)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            Grid.SetColumn(a, 1); Grid.SetColumn(b, 2);
            g.Children.Add(a); g.Children.Add(b);
            return g;
        }

        Border Card(string heading, UIElement extra, params UIElement[] rows)
        {
            var stack = new StackPanel();
            var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // Cards that hold one kind of thing are headed by that kind's colored tag, the same tag AI lines carry.
            string[] plural = { "Bot", "Bots", "Script", "Scripts", "Task", "Scheduled", "Start", "Starts with Windows", "Watching", "Pinned" };
            int at = Array.IndexOf(plural, heading);
            if (at >= 0 && at % 2 == 0)
            {
                var tag = Theme.Tag(heading);
                ((TextBlock)tag.Child).Text = plural[at + 1];
                tag.HorizontalAlignment = HorizontalAlignment.Left;
                head.Children.Add(tag);
            }
            else head.Children.Add(Theme.Label(heading, 12, Theme.Sub, FontWeights.SemiBold));
            if (extra is Grid) { Grid.SetColumnSpan(extra, 2); head.Children.Add(extra); }
            else if (extra != null) { Grid.SetColumn(extra, 1); head.Children.Add(extra); }
            stack.Children.Add(head);
            foreach (var r in rows) stack.Children.Add(r);
            stack.Margin = new Thickness(16, 13, 16, 12);
            // Glass: a clear pane that thins out toward the bottom, a sheen across its top, and a rim that catches
            // light on the top-left edge and again faintly at the bottom-right.
            var sheen = new Border { Height = 46, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(19, 19, 0, 0), Background = Theme.Sheen, IsHitTestVisible = false };
            return new Border
            {
                Background = Theme.Card,
                BorderBrush = Theme.Edge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20),
                Child = new Grid { Children = { sheen, stack } }
            };
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
            int on = 1; Native.DwmSetWindowAttribute(h, 20, ref on, 4);
            int round = 2; Native.DwmSetWindowAttribute(h, 33, ref round, 4);
            var m = new Native.MARGINS { l = -1, r = -1, t = -1, b = -1 };
            Native.DwmExtendFrameIntoClientArea(h, ref m);
            if (!TransparencyOn()) return;
            int acrylic = 3;
            Native.DwmSetWindowAttribute(h, 38, ref acrylic, 4);
        }

        // ---------- on / off ----------

        void SetLive(bool on)
        {
            live = on;
            var a = new DoubleAnimation(on ? 20 : 2, TimeSpan.FromMilliseconds(180));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            knob.BeginAnimation(TranslateTransform.XProperty, a);
            switchTrack.Background = on ? Theme.Green : Theme.Off;
            stateLabel.Text = on ? "Live" : "Off";
            body.Opacity = on ? 1 : 0.4;
            UpdateRunning();
        }

        // Readings run only while the switch is on and the window is showing.
        void UpdateRunning()
        {
            bool run = live && WindowState != WindowState.Minimized;
            if (run && !timer.IsEnabled) Prime();
            else if (!run && timer.IsEnabled)
            {
                timer.Stop();
                prev.Clear();
                cmdCache.Clear();
                prevKernel = 0;
                if (!live) subtitle.Text = "Off \u00B7 not collecting" + (configSkipped ? " \u00B7 settings file too large" : "");
                Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            }
        }

        // CPU needs two samples, so take one now and draw a moment later.
        void Prime()
        {
            if (!live || WindowState == WindowState.Minimized) return;
            Sample();
            var once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            once.Tick += delegate { once.Stop(); Tick(); timer.Start(); };
            once.Start();
        }

        // ---------- readings ----------

        List<Proc> Sample()
        {
            var procs = Procs.Snapshot();
            long now = clock.Elapsed.Ticks;
            double span = Math.Max(1, now - prevTicks) * cores;
            var next = new Dictionary<int, Proc>(procs.Count);
            foreach (var p in procs)
            {
                Proc old;
                if (prev.TryGetValue(p.Pid, out old) && old.CreateTime == p.CreateTime)
                    p.Cpu = Math.Max(0, (p.CpuTime - old.CpuTime) / span * 100);
                long key = ((long)p.Pid << 32) ^ p.CreateTime;
                string cmd;
                if (!cmdCache.TryGetValue(key, out cmd)) { cmd = Procs.CommandLine(p.Pid); cmdCache[key] = cmd; }
                p.Cmd = cmd;
                next[p.Pid] = p;
            }
            if (cmdCache.Count > procs.Count * 3)
            {
                var keep = new HashSet<long>(procs.Select(p => ((long)p.Pid << 32) ^ p.CreateTime));
                foreach (var k in cmdCache.Keys.Where(k => !keep.Contains(k)).ToList()) cmdCache.Remove(k);
            }
            prev = next;
            prevTicks = now;

            long idle, kernel, user;
            if (Native.GetSystemTimes(out idle, out kernel, out user))
            {
                long di = idle - prevIdle, dt = (kernel - prevKernel) + (user - prevUser);
                if (prevKernel != 0 && dt > 0) cpuTotal = Math.Max(0, Math.Min(100, 100.0 * (dt - di) / dt));
                prevIdle = idle; prevKernel = kernel; prevUser = user;
            }
            return procs;
        }

        void Tick()
        {
            if (!live) return;
            var procs = Sample();
            tickCount++;

            // System
            cpuRow.Set(SensorDetail(), Theme.Pct(cpuTotal), cpuTotal / 100);
            if (Native.ThreadTimes(threadBusy, threadTotal))
            {
                for (int i = 0; i < cores; i++)
                {
                    long dt = threadTotal[i] - prevThreadTotal[i];
                    if (prevThreadTotal[i] != 0 && dt > 0) threadStrip.Set(i, (double)(threadBusy[i] - prevThreadBusy[i]) / dt);
                }
                var swap = prevThreadBusy; prevThreadBusy = threadBusy; threadBusy = swap;
                swap = prevThreadTotal; prevThreadTotal = threadTotal; threadTotal = swap;
            }

            var ms = new Native.MEMSTAT { len = (uint)Marshal.SizeOf(typeof(Native.MEMSTAT)) };
            if (!Native.GlobalMemoryStatusEx(ref ms) || ms.total == 0) memRow.Set("not available", "", 0);
            else
            {
                double used = ms.total - ms.avail;
                memRow.Set(Theme.Gb(used) + " of " + Theme.Gb(ms.total) + " GB", Theme.Pct(100 * used / ms.total), used / ms.total);
            }

            double gu, vu, vt, gw; int gt;
            if (Nvml.Read(out gu, out vu, out vt, out gt, out gw))
            {
                var bits = new List<string> { Nvml.Name ?? "GPU" };
                if (gt >= 0) bits.Add(gt + "\u00B0C");
                if (gw >= 0) bits.Add(Theme.N(gw, "0") + " W");
                gpuRow.Set(string.Join(" \u00B7 ", bits), gu >= 0 ? Theme.Pct(gu) : "", gu >= 0 ? gu / 100 : 0);
                if (vt > 0) vramRow.Set(Theme.N(vu, "0.0") + " of " + Theme.N(vt, "0") + " GB", Theme.Pct(100 * vu / vt), vu / vt);
                else if (vu >= 0) vramRow.Set(Theme.N(vu, "0.0") + " GB", "", 0);
                else vramRow.Set("not available", "", 0);
            }
            else
            {
                GpuCounters.Refresh();
                if (GpuCounters.Have)
                {
                    gpuRow.Set(GpuCounters.Name ?? "GPU", GpuCounters.UtilReady ? Theme.Pct(GpuCounters.Util) : "", GpuCounters.UtilReady ? GpuCounters.Util / 100 : 0);
                    double t = GpuCounters.TotalGb, u = GpuCounters.UsedGb;
                    vramRow.Set(t > 0 ? Theme.N(u, "0.0") + " of " + Theme.N(t, "0") + " GB" : Theme.N(u, "0.0") + " GB", t > 0 ? Theme.Pct(100 * u / t) : "", t > 0 ? u / t : 0);
                }
                else
                {
                    string msg = GpuCounters.Failed ? "GPU readings not available on this system" : "Reading GPU";
                    gpuRow.Set(msg, "", 0);
                    vramRow.Set(msg, "", 0);
                }
            }

            if (tickCount % 20 == 1)
            {
                try
                {
                    var d = new DriveInfo(DiskRoot);
                    double usedD = d.TotalSize - d.AvailableFreeSpace;
                    diskDetail = Theme.Gb(d.AvailableFreeSpace) + " GB free of " + Theme.Gb(d.TotalSize);
                    diskValue = Theme.Pct(100 * usedD / d.TotalSize);
                    diskFrac = usedD / d.TotalSize;
                }
                catch (Exception) { diskDetail = "not available"; }
            }
            diskRow.Set(diskDetail, diskValue, diskFrac);

            TimeSpan up = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            string upText = up.TotalDays >= 1 ? string.Format("{0}d {1}h", (int)up.TotalDays, up.Hours) : string.Format("{0}h {1}m", up.Hours, up.Minutes);
            subtitle.Text = "Up " + upText + " \u00B7 " + procs.Count + " processes" + (configSkipped ? " \u00B7 settings file too large" : "");

            // AI: local models, coding agents, AI apps, and the bots and watched lines that are AI.
            lastProcs = procs;
            ReloadIfChanged();
            ReadVram();
            ScanModels(procs);
            lastAi = FindAi(procs);
            var ai = lastAi.Where(l => !hidden.Contains(l.Name)).ToList();
            aiCard.Visibility = ai.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EnsureRows(aiList, aiRows, ai.Count);
            for (int i = 0; i < ai.Count; i++) ShowAi(aiRows[i], ai[i]);

            // Watching
            var shownWatch = rules.Where(r => !hidden.Contains(r.Label)).ToList();
            EnsureRows(watchList, watchRows, shownWatch.Count);
            for (int i = 0; i < shownWatch.Count; i++)
            {
                var hits = procs.Where(shownWatch[i].Hit).ToList();
                watchRows[i].SetWatched(shownWatch[i].Label, hits.Count > 0, hits.Count, hits.Sum(h => h.Cpu), hits.Sum(h => h.Private),
                    hits.Count == 0 ? null : CmdTip(hits));
                watchRows[i].Pids = hits.Select(h => h.Pid).ToArray();
            }

            // Bots
            var shownBots = botRules.Where(r => !hidden.Contains(r.Label)).ToList();
            EnsureRows(botList, botRows, shownBots.Count);
            for (int i = 0; i < shownBots.Count; i++)
            {
                var r = shownBots[i];
                var hits = procs.Where(r.Hit).ToList();
                botRows[i].Pids = hits.Select(h => h.Pid).ToArray();
                botRows[i].Bot = r;
                int state; string detail, right, tip;
                BotView(r, hits, out state, out detail, out right, out tip);
                botRows[i].SetBot(r.Label, state, detail, state == 0 ? "" : Theme.Pct(hits.Sum(h => h.Cpu)), right, tip);
            }

            // Scripts: anything an interpreter is running from a script file of yours, one line per script.
            var claimed = new HashSet<int>(botRules.SelectMany(r => procs.Where(r.Hit)).Select(p => p.Pid));
            // A script that has a model loaded still shows here; the model's line only says who is using it.
            foreach (var l in lastAi) if (l.Model == null && !l.GpuOnly) foreach (var p in l.Procs) claimed.Add(p.Pid);
            var byScript = new Dictionary<string, List<Proc>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in procs)
            {
                if (claimed.Contains(p.Pid)) continue;
                string script = ScriptOf(p);
                if (script == null) continue;
                List<Proc> l;
                if (!byScript.TryGetValue(script, out l)) byScript[script] = l = new List<Proc>();
                l.Add(p);
            }
            if (scriptCache.Count > procs.Count * 3)
            {
                var keep = new HashSet<long>(procs.Select(p => ((long)p.Pid << 32) ^ p.CreateTime));
                foreach (var k in scriptCache.Keys.Where(k => !keep.Contains(k)).ToList()) scriptCache.Remove(k);
            }
            lastScripts = byScript.OrderBy(kv => Scripts.Label(kv.Key), StringComparer.OrdinalIgnoreCase).ToList();
            var scripts = lastScripts.Where(kv => !hidden.Contains(Scripts.Label(kv.Key))).Take(12).ToList();
            scriptsCard.Visibility = scripts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EnsureRows(scriptList, scriptRows, scripts.Count);
            for (int i = 0; i < scripts.Count; i++)
            {
                var items = scripts[i].Value;
                TimeSpan sUp = DateTime.UtcNow - DateTime.FromFileTimeUtc(items.Min(h => h.CreateTime));
                string tip = (scripts[i].Key.StartsWith("module:") ? Scripts.Folder(scripts[i].Key) : scripts[i].Key) + "\n\n" + CmdTip(items);
                scriptRows[i].Script = scripts[i].Key;
                scriptRows[i].Pids = items.Select(h => h.Pid).ToArray();
                scriptRows[i].SetPlain(Scripts.Label(scripts[i].Key), Theme.Green, Scripts.Language(items[0].Exe) + " \u00B7 up " + Ago(sUp),
                    Theme.Pct(items.Sum(h => h.Cpu)), Theme.Mem(items.Sum(h => h.Private)), tip);
            }

            // Starts with Windows: Startup folder, Startup apps in the registry, and tasks that only run at sign-in.
            TaskList.Refresh(null);
            StartupList.Refresh();
            var starts = StartupList.Items.Concat(TaskList.Items.Where(t => t.StartsWithWindows)
                    .Select(t => new StartItem { Name = t.Name, Where = "sign-in task", Exe = t.Exe, Script = t.Script, Task = t }))
                .Where(s => !hidden.Contains(s.Name))
                .Select(s => new { Item = s, Hits = StartProcs(s, procs) })
                .OrderByDescending(s => s.Hits.Count > 0 || (s.Item.Task != null && s.Item.Task.Running)).ThenBy(s => s.Item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(12).ToList();
            startCard.Visibility = starts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EnsureRows(startList, startRows, starts.Count);
            for (int i = 0; i < starts.Count; i++)
            {
                var s = starts[i].Item;
                var hits = starts[i].Hits;
                bool running = hits.Count > 0 || (s.Task != null && s.Task.Running);
                string tip = s.Where + "\n" + (s.Script ?? s.Exe ?? "") + (hits.Count == 0 ? "" : "\n\n" + CmdTip(hits));
                startRows[i].Task = s.Task;
                startRows[i].Script = s.Script;
                startRows[i].Pids = hits.Select(h => h.Pid).ToArray();
                if (!running) startRows[i].SetPlain(s.Name, Theme.Off, s.Where, "", "not running", tip);
                else
                {
                    string since = hits.Count == 0 ? "" : " · up " + Ago(DateTime.UtcNow - DateTime.FromFileTimeUtc(hits.Min(h => h.CreateTime)));
                    startRows[i].SetPlain(s.Name, Theme.Green, s.Where + since, hits.Count == 0 ? "" : Theme.Pct(hits.Sum(h => h.Cpu)), hits.Count == 0 ? "running" : Theme.Mem(hits.Sum(h => h.Private)), tip);
                }
            }

            // Scheduled: only tasks that run at set times. A bot's restart-every-few-minutes task is the bot's own
            // business and shows in Bots; a task with no trigger is started by something else (the temperature sensor).
            var bots = botRules.Concat(aiRules.Where(r => r.IsBot)).ToList();
            var tasks = TaskList.Items.Where(t => t.Timed && !hidden.Contains(t.Name))
                .Where(t => !bots.Any(r => (t.Action ?? "").IndexOf(r.Match, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderByDescending(t => t.Running).ThenByDescending(t => t.Failed)
                .ThenBy(t => t.NextRun > DateTime.Now ? t.NextRun : DateTime.MaxValue).Take(8).ToList();
            if (!TaskList.Ready)
            {
                tasksCard.Visibility = Visibility.Visible;
                EnsureRows(taskList, taskRows, 1);
                taskRows[0].Task = null;
                taskRows[0].Pids = new int[0];
                taskRows[0].SetPlain("Reading Task Scheduler", Theme.Off, "", "", "", "");
            }
            else if (TaskList.Failed && tasks.Count == 0)
            {
                tasksCard.Visibility = Visibility.Visible;
                EnsureRows(taskList, taskRows, 1);
                taskRows[0].Task = null;
                taskRows[0].Pids = new int[0];
                taskRows[0].SetPlain("Task Scheduler not available", Theme.Off, "", "", "", "");
            }
            else
            {
                tasksCard.Visibility = tasks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                EnsureRows(taskList, taskRows, tasks.Count);
                for (int i = 0; i < tasks.Count; i++)
                {
                    var t = tasks[i];
                    string result = t.Running ? "running" : t.NeverRan ? "n/a" : t.Result == 0x41306 ? "stopped" : t.Failed ? "failed" : "OK";
                    string last = t.NeverRan ? "never" : Short(DateTime.Now - t.LastRun) + " ago";
                    string next = t.NextRun > DateTime.Now ? "next in " + Ago(t.NextRun - DateTime.Now) : "";
                    string tip = t.Path + "\n" + Trim(t.Action ?? "", 160) + (t.Failed ? "\nLast result: 0x" + t.Result.ToString("X") : "");
                    taskRows[i].Task = t;
                    taskRows[i].SetPlain(t.Name, t.Running ? Theme.Green : t.Failed ? Theme.AmberDot : Theme.Off, next, result, last, tip);
                }
            }

            // Top apps, grouped by exe
            var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in procs)
            {
                Group g;
                if (!groups.TryGetValue(p.Exe, out g)) { g = new Group { Key = p.Exe }; groups[p.Exe] = g; }
                g.Items.Add(p); g.Cpu += p.Cpu; g.Mem += p.Private;
            }
            var top = (sortByMemory
                ? groups.Values.OrderByDescending(g => g.Mem)
                : groups.Values.OrderByDescending(g => Math.Round(g.Cpu, 1)).ThenByDescending(g => g.Mem)).Take(TopCount).ToList();
            EnsureRows(topList, topRows, top.Count);
            for (int i = 0; i < top.Count; i++)
            {
                var g = top[i];
                var id = AppIdentity.For(g.Key, g.Pids);
                topRows[i].SetApp(id.Icon, id.Title, g.Items.Count, g.Cpu, g.Mem, g.Key + "\n" + g.Items.Count + (g.Items.Count == 1 ? " process" : " processes"));
                topRows[i].Pids = g.Pids;
            }

            // Interactive: board paints first (tick 1-2), review on tick 3. Snapshot --review: open on tick 2 so the PNG catches it.
            if (needReview && tickCount >= (snapshotDir != null ? 2 : 3)) { needReview = false; ShowReview(); }
        }

        static string Trim(string s, int n) { return s.Length <= n ? s : s.Substring(0, n) + "\u2026"; }
        static string CmdTip(IEnumerable<Proc> hits) { return string.Join("\n", hits.Take(12).Select(h => h.Pid + "  " + Trim(h.Cmd.Length > 0 ? h.Cmd : h.Exe, 140))); }

        // Processes a startup entry started: its script under an interpreter, or its program by name.
        static List<Proc> StartProcs(StartItem s, List<Proc> procs)
        {
            if (s.Script != null)
            {
                string file = System.IO.Path.GetFileName(s.Script);
                return procs.Where(p => p.Cmd.IndexOf(file, StringComparison.OrdinalIgnoreCase) >= 0 && Scripts.IsInterpreter(p.Exe)).ToList();
            }
            string exe;
            try { exe = System.IO.Path.GetFileName(s.Exe ?? ""); } catch (Exception) { return new List<Proc>(); }
            if (exe.Length == 0 || Scripts.IsInterpreter(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return new List<Proc>();
            return procs.Where(p => string.Equals(p.Exe, exe, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // compact: 45s, 12m, 13h, 2d. otherwise: 13h 05m, 2d 3h.
        static string Short(TimeSpan t) { return SpanText(t, true); }
        static string Ago(TimeSpan t) { return SpanText(t, false); }
        static string SpanText(TimeSpan t, bool compact)
        {
            if (t.TotalSeconds < 60) return Math.Max(0, (int)t.TotalSeconds) + "s";
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + "m";
            if (t.TotalHours < 24) return compact ? (int)t.TotalHours + "h" : (int)t.TotalHours + "h " + t.Minutes.ToString("00") + "m";
            return compact ? (int)t.TotalDays + "d" : (int)t.TotalDays + "d " + t.Hours + "h";
        }

        // A bot's state from its check: 0 not running, 1 working, 2 quiet or failing. right is when it last showed work.
        void BotView(WatchRule r, List<Proc> hits, out int state, out string detail, out string right, out string tip)
        {
            BotStatus st;
            if (!botStatus.TryGetValue(r.Label, out st)) botStatus[r.Label] = st = new BotStatus();
            st.Refresh(r);
            tip = hits.Count == 0 ? null : CmdTip(hits);
            if (r.Code != null) tip = tip == null ? OpenInEditor : OpenInEditor + "\n\n" + tip;
            state = 0; detail = ""; right = "not running";
            if (hits.Count == 0) return;

            TimeSpan botUp = st.Uptime ?? (DateTime.UtcNow - DateTime.FromFileTimeUtc(hits.Max(h => h.CreateTime)));
            var bits = new List<string> { "up " + Ago(botUp) };
            if (st.Problem != null) bits.Add(st.Problem);
            double quiet = st.LastSign.HasValue ? (DateTime.Now - st.LastSign.Value).TotalSeconds : -1;
            bool stale = st.Read != DateTime.MinValue && (quiet < 0 || quiet > 300);
            right = st.Read == DateTime.MinValue ? "" : quiet < 0 ? "no sign" : quiet < 60 ? "just now" : Short(TimeSpan.FromSeconds(quiet)) + " ago";
            state = stale || st.Problem != null ? 2 : 1;
            detail = string.Join(" \u00B7 ", bits);
        }

        // What AI program a process is, as { kind, name }, or null. Looked up once per process.
        string[] AiOf(Proc p)
        {
            long key = ((long)p.Pid << 32) ^ p.CreateTime;
            string[] id;
            if (aiCache.TryGetValue(key, out id)) return id;
            id = Identify(p);
            if (aiCache.Count > 4000) aiCache.Clear();
            aiCache[key] = id;
            return id;
        }

        static string[] Identify(Proc p)
        {
            string model = ModelLabel(p);
            if (model != null) return new[] { "Model", model };
            string exe = (p.Exe ?? "").ToLowerInvariant(), cmd = p.Cmd ?? "";
            Func<string, bool> has = w => cmd.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0;
            if (exe == "claude.exe")
            {
                // The Claude desktop app and Claude Code share a file name; the desktop app lives in its own install folder.
                string path = Procs.ImagePath(p.Pid) ?? "";
                bool desktop = path.IndexOf("AnthropicClaude", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("\\Programs\\Claude\\", StringComparison.OrdinalIgnoreCase) >= 0;
                return desktop ? new[] { "App", "Claude" } : new[] { "Agent", "Claude Code" };
            }
            if (has("openclaw")) return new[] { "Agent", "OpenClaw" };
            if (exe == "codex.exe" || (has("@openai") && has("codex"))) return new[] { "Agent", "Codex" };
            if (exe == "gemini.exe" || has("gemini-cli")) return new[] { "Agent", "Gemini CLI" };
            if (has("@anthropic-ai") && has("claude-code")) return new[] { "Agent", "Claude Code" };
            if (exe == "opencode.exe") return new[] { "Agent", "opencode" };
            if (exe == "goose.exe") return new[] { "Agent", "Goose" };
            if (exe.StartsWith("python") && Regex.IsMatch(cmd, "[\\\\/ ]aider(\\.exe)?[\" ]|-m aider", RegexOptions.IgnoreCase)) return new[] { "Agent", "Aider" };
            if (exe == "cursor.exe") return new[] { "App", "Cursor" };
            if (exe == "chatgpt.exe") return new[] { "App", "ChatGPT" };
            if (exe == "windsurf.exe") return new[] { "App", "Windsurf" };
            if (exe == "perplexity.exe") return new[] { "App", "Perplexity" };
            if (exe == "meetily.exe") return new[] { "App", "Meetily" };
            if (exe == "copilot.exe") return new[] { "App", "Copilot" };
            if (exe.Contains("grok") && exe.EndsWith(".exe")) return new[] { "App", System.IO.Path.GetFileNameWithoutExtension(p.Exe) };
            return null;
        }

        // Everything AI on the machine, running first, grouped by kind.
        List<AiLine> FindAi(List<Proc> procs)
        {
            var lines = new List<AiLine>();
            var taken = new HashSet<int>();
            foreach (var r in aiRules.Where(r => r.IsBot))
            {
                var hits = procs.Where(r.Hit).ToList();
                foreach (var h in hits) taken.Add(h.Pid);
                lines.Add(new AiLine { Kind = r.Kind ?? "Bot", Name = r.Label, Procs = hits, Rule = r });
            }
            var found = new Dictionary<string, AiLine>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in procs)
            {
                if (taken.Contains(p.Pid)) continue;
                var id = AiOf(p);
                if (id == null) continue;
                AiLine l;
                if (!found.TryGetValue(id[1], out l)) found[id[1]] = l = new AiLine { Kind = id[0], Name = id[1] };
                l.Procs.Add(p);
            }
            // Your own AI lines: a name for something found, a "not running" line, or something found only by your match.
            foreach (var r in aiRules.Where(r => !r.IsBot))
            {
                var hits = procs.Where(p => !taken.Contains(p.Pid) && r.Hit(p)).ToList();
                var pids = new HashSet<int>(hits.Select(h => h.Pid));
                var over = found.Values.Where(l => l.Procs.Any(p => pids.Contains(p.Pid))).ToList();
                if (hits.Count == 0)
                {
                    var guess = Identify(new Proc { Exe = r.ByExe ? r.Match : "", Cmd = r.Match });
                    lines.Add(new AiLine { Kind = r.Kind ?? (guess == null ? "Watching" : guess[0]), Name = r.Label, Rule = r });
                }
                else if (over.Count == 1 && over[0].Rule == null && over[0].Procs.Count == hits.Count)
                {
                    // "Claude Code" says more than "Claude", so a found name that already starts with yours stays.
                    if (!over[0].Name.StartsWith(r.Label, StringComparison.OrdinalIgnoreCase)) over[0].Name = r.Label;
                    over[0].Rule = r;
                    if (r.Kind != null) over[0].Kind = r.Kind;
                }
                else if (over.Count == 0)
                {
                    lines.Add(new AiLine { Kind = r.Kind ?? "Watching", Name = r.Label, Procs = hits, Rule = r });
                    foreach (int pid in pids) taken.Add(pid);
                }
            }
            lines.AddRange(found.Values);
            AddModels(lines, procs);
            foreach (var l in lines)
            {
                string k;
                if (kindOf.TryGetValue(l.Name, out k)) l.Kind = k;
            }
            return lines.OrderBy(l => l.Procs.Count == 0).ThenBy(l => KindOrder.IndexOf(l.Kind))
                .ThenByDescending(l => l.Model == null ? DateTime.MinValue : l.Model.Added).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The models on disk, looked for again every minute off the UI thread, and at once when a script starts
        // from a folder not seen before, so a new model shows up without waiting.
        void ScanModels(List<Proc> procs)
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in procs)
            {
                string script = ScriptOf(p);
                string dir = script == null ? null : Scripts.Folder(script);
                if (dir != null) folders.Add(dir);
            }
            if (folders.Any(f => !scriptFolders.Contains(f))) modelsRead = DateTime.MinValue;
            if (modelsBusy || (DateTime.Now - modelsRead).TotalSeconds < 60) return;
            modelsBusy = true;
            scriptFolders = folders;
            var extra = WatchRule.ParseMany(WatchRule.ReadLines(WatchRule.ConfigFile()), "models");
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<InstalledModel> found;
                try { found = ModelStore.Scan(extra, folders); } catch (Exception) { found = new List<InstalledModel>(); }
                Dispatcher.BeginInvoke(new Action(delegate { installed = found; modelsRead = DateTime.Now; modelsBusy = false; }));
            });
        }

        // Your settings, read again when watch.txt changes (the review screen, or you editing it).
        void ReloadIfChanged()
        {
            DateTime t;
            try { t = File.GetLastWriteTimeUtc(WatchRule.ConfigFile()); } catch (Exception) { return; }
            if (t == configTime) return;
            configTime = t;
            LoadConfig();
        }

        // The refresh button: read settings, models and scheduled tasks again now, and look at every process afresh.
        void RefreshAll()
        {
            configTime = DateTime.MinValue;
            ReloadIfChanged();
            aiCache.Clear(); scriptCache.Clear(); useCache.Clear(); sourceCache.Clear(); cmdCache.Clear();
            scriptFolders.Clear();
            modelsRead = DateTime.MinValue;
            TaskList.Soon();
            StartupList.Soon();
            if (live && WindowState != WindowState.Minimized) Tick();
        }

        string ScriptOf(Proc p)
        {
            long key = ((long)p.Pid << 32) ^ p.CreateTime;
            string script;
            if (!scriptCache.TryGetValue(key, out script))
            {
                int pid = p.Pid;
                script = Scripts.Find(p, () => Procs.CurrentDirectory(pid));
                scriptCache[key] = script;
            }
            return script;
        }

        long VramOf(Proc p) { long b; return vramByPid.TryGetValue(p.Pid, out b) ? b : 0; }

        void AddModels(List<AiLine> lines, List<Proc> procs)
        {
            var models = installed;
            var mine = models.Select(m => new AiLine { Kind = "Model", Name = m.Name, Model = m }).ToList();
            var using_ = new HashSet<int>();
            foreach (var p in procs)
            {
                long key = ((long)p.Pid << 32) ^ p.CreateTime;
                string use;
                if (!useCache.TryGetValue(key, out use)) { use = p.Cmd.Length == 0 ? "" : ModelStore.CommandText(p); useCache[key] = use; }
                // A script big enough to hold a model, on the GPU or running it on the CPU (speech, small ONNX models).
                string src = "", home = null;
                // Committed, not the working set: an idle model gets paged out but is still loaded.
                if (VramOf(p) >= 256L << 20 || (p.Committed >= 256L << 20 && Scripts.IsInterpreter(p.Exe)))
                {
                    string script = ScriptOf(p);
                    if (!sourceCache.TryGetValue(key, out src))
                        src = sourceCache[key] = script == null || script.StartsWith("module:") ? "" : ModelStore.ScriptText(script);
                    string dir = script == null ? null : Scripts.Folder(script);
                    if (dir != null) home = ModelStore.Norm(dir).TrimEnd('\\') + "\\";
                }
                if (use.Length == 0 && src.Length == 0 && home == null) continue;
                foreach (var l in mine)
                {
                    var m = l.Model;
                    // ... or a model kept in the script's own folder (push-to-talk\model).
                    if (m.PathKeys.Any(k => Has(use, k)) || (home != null && m.PathKeys.Any(k => k.StartsWith(home, StringComparison.Ordinal)))
                        || (src.Length > 0 && (m.PathKeys.Any(k => Has(src, k)) || m.NameKeys.Any(k => Has(src, k)))))
                    {
                        l.Procs.Add(p);
                        using_.Add(p.Pid);
                    }
                }
            }
            foreach (var d in new[] { useCache, sourceCache })
                if (d.Count > procs.Count * 3)
                {
                    var keep = new HashSet<long>(procs.Select(p => ((long)p.Pid << 32) ^ p.CreateTime));
                    foreach (var k in d.Keys.Where(k => !keep.Contains(k)).ToList()) d.Remove(k);
                }
            // A model server found by name says less than the installed model it serves.
            lines.RemoveAll(l => l.Kind == "Model" && l.Rule == null && l.Procs.Count > 0 && l.Procs.All(p => using_.Contains(p.Pid)));
            lines.AddRange(mine);
            var shown = new HashSet<int>(lines.SelectMany(l => l.Procs).Select(p => p.Pid));
            foreach (var p in procs)
            {
                if (shown.Contains(p.Pid) || VramOf(p) < 1L << 30) continue;
                if (!Scripts.IsInterpreter(p.Exe) && !ModelExes.Any(x => string.Equals(x, p.Exe, StringComparison.OrdinalIgnoreCase))) continue;
                string script = ScriptOf(p);
                lines.Add(new AiLine { Kind = "Model", Name = script != null ? Scripts.Label(script) : AppIdentity.For(p.Exe, new[] { p.Pid }).Title, Procs = new List<Proc> { p }, GpuOnly = true });
            }
        }

        // text contains key as a whole path or name, not as part of a longer one.
        static bool Has(string text, string key)
        {
            for (int at = text.IndexOf(key, StringComparison.Ordinal); at >= 0; at = text.IndexOf(key, at + 1, StringComparison.Ordinal))
            {
                int end = at + key.Length;
                bool before = at == 0 || !IsNameChar(text[at - 1]);
                bool after = end == text.Length || !IsNameChar(text[end]);
                if (before && after) return true;
            }
            return false;
        }

        static bool IsNameChar(char c) { return char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'; }

        static string AddedText(DateTime when)
        {
            int days = (DateTime.Today - when.Date).Days;
            if (days <= 0) return "new today";
            if (days == 1) return "added yesterday";
            return "added " + when.ToString("MMM d", CultureInfo.InvariantCulture);
        }

        // A model's line: who has it loaded and how much video memory that holds, or its size and when it arrived.
        void ShowModel(ProcRow row, AiLine l)
        {
            var m = l.Model;
            string about = m == null ? "" : m.Path + "\n" + Theme.Mem(m.Bytes) + " on disk \u00B7 " + m.Files + (m.Files == 1 ? " weights file" : " weights files") + " \u00B7 " + AddedText(m.Added);
            if (l.Procs.Count == 0)
            {
                string added = AddedText(m.Added);
                row.SetAi(l.Name, l.Kind, 0, added == "new today" ? added : Theme.Mem(m.Bytes), "", "not loaded", about);
                return;
            }
            long vram = l.Procs.Sum(p => VramOf(p));
            var user = l.Procs.OrderByDescending(p => VramOf(p)).First();
            string script = ScriptOf(user);
            string port = l.Procs.Select(p => ArgAfter(Scripts.SplitArgs(p.Cmd), "--port")).FirstOrDefault(x => x != null);
            string who = port != null ? "serving :" + port : script != null ? Scripts.Label(script) : AppIdentity.For(user.Exe, new[] { user.Pid }).Title;
            if (l.GpuOnly) who = "model not found";
            // What a model costs is mostly video memory, so that is what its right-hand column shows when it has any.
            bool onGpu = vram >= 32L << 20;
            string tip = (about.Length > 0 ? about + "\n\n" : "") + (vram > 0 ? Theme.Mem(vram) + " of video memory\n" : "") + CmdTip(l.Procs);
            row.SetAi(l.Name, l.Kind, 1, who, Theme.Pct(l.Procs.Sum(h => h.Cpu)), onGpu ? Theme.Mem(vram) + " GPU" : Theme.Mem(l.Procs.Sum(h => h.Private)), tip);
        }

        void ShowAi(ProcRow row, AiLine l)
        {
            row.Pids = l.Procs.Select(p => p.Pid).ToArray();
            row.Bot = l.Rule != null && l.Rule.IsBot ? l.Rule : null;
            double cpu = l.Procs.Sum(h => h.Cpu);
            long mem = l.Procs.Sum(h => h.Private);
            if (row.Bot != null)
            {
                int state; string detail, right, btip;
                BotView(l.Rule, l.Procs, out state, out detail, out right, out btip);
                if (state != 0 && right.Length > 0)
                {
                    btip = detail + (btip == null ? "" : "\n\n" + btip);
                    detail = right == "no sign" ? "no sign of work" : "worked " + right;
                }
                row.SetAi(l.Name, l.Kind, state, detail, state == 0 ? "" : Theme.Pct(cpu), state == 0 ? "not running" : Theme.Mem(mem), btip);
                return;
            }
            if (l.Model != null || l.GpuOnly) { ShowModel(row, l); return; }
            if (l.Procs.Count == 0) { row.SetAi(l.Name, l.Kind, 0, "", "", "not running", null); return; }

            TimeSpan up = DateTime.UtcNow - DateTime.FromFileTimeUtc(l.Procs.Min(h => h.CreateTime));
            string text = "up " + Ago(up);
            string tip;
            // A command-line agent runs one process per session; say how many, and which folder each works in.
            bool sessions = l.Kind == "Agent" && !Scripts.IsInterpreter(l.Procs[0].Exe) && l.Procs.All(p => p.Exe == l.Procs[0].Exe);
            if (sessions)
            {
                if (l.Procs.Count > 1) text = l.Procs.Count + " sessions";
                tip = string.Join("\n", l.Procs.Take(12).Select(h => h.Pid + "  " + (Procs.CurrentDirectory(h.Pid) ?? "folder unknown")));
            }
            else
            {
                if (l.Procs.Count > 1) text = "\u00D7" + l.Procs.Count + " \u00B7 " + text;
                tip = CmdTip(l.Procs);
            }
            row.SetAi(l.Name, l.Kind, 1, text, Theme.Pct(cpu), Theme.Mem(mem), tip);
        }

        string SensorDetail()
        {
            string file;
            if (!settings.TryGetValue("temperature-file", out file)) return "";
            try
            {
                if (!File.Exists(file)) return "temperature sensor off";
                string json;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > 64 * 1024) return "temperature not readable";
                    var buf = new char[64 * 1024];
                    using (var sr = new StreamReader(fs))
                    {
                        int n = sr.Read(buf, 0, buf.Length);
                        json = new string(buf, 0, n);
                    }
                }
                var ts = Regex.Match(json, "\"ts\"\\s*:\\s*\"([^\"]+)\"");
                DateTimeOffset when;
                if (!ts.Success || !DateTimeOffset.TryParse(ts.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out when)) return "temperature not readable";
                if ((DateTimeOffset.Now - when).TotalSeconds > 60) return "temperature stale";
                var t = Regex.Match(json, "\"cpu_temp_c\"\\s*:\\s*([0-9.]+)");
                var w = Regex.Match(json, "\"cpu_pkg_power_w\"\\s*:\\s*([0-9.]+)");
                var parts = new List<string>();
                if (t.Success) parts.Add(Theme.N(double.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture), "0") + "\u00B0C");
                if (w.Success) parts.Add(Theme.N(double.Parse(w.Groups[1].Value, CultureInfo.InvariantCulture), "0") + " W");
                return parts.Count == 0 ? "temperature not readable" : string.Join(" \u00B7 ", parts);
            }
            catch (Exception) { return "temperature not readable"; }
        }

        // Local model servers: llama.cpp, Ollama, LM Studio, koboldcpp, vLLM.
        static string ModelLabel(Proc p)
        {
            string exe = p.Exe;
            bool known = ModelExes.Any(x => string.Equals(x, exe, StringComparison.OrdinalIgnoreCase));
            bool pyServer = exe.StartsWith("python", StringComparison.OrdinalIgnoreCase)
                && (p.Cmd.IndexOf("vllm", StringComparison.OrdinalIgnoreCase) >= 0 || p.Cmd.IndexOf("llama_cpp.server", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!known && !pyServer) return null;
            var args = Scripts.SplitArgs(p.Cmd);
            string model = ArgAfter(args, "-m", "--model", "-hf", "--hf-repo");
            string port = ArgAfter(args, "--port");
            if (exe.StartsWith("ollama", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Any(a => a == "serve")) return "Ollama server";
                if (args.Any(a => a == "runner")) return "Ollama model";
                return null; // the CLI client
            }
            if (exe.Equals("LM Studio.exe", StringComparison.OrdinalIgnoreCase)) return args.Count > 1 ? null : "LM Studio";
            string name = model == null ? System.IO.Path.GetFileNameWithoutExtension(exe) : System.IO.Path.GetFileNameWithoutExtension(model.TrimEnd('/', '\\'));
            if (model != null && model.Contains("/") && !model.Contains("\\")) name = model.Substring(model.LastIndexOf('/') + 1);
            if (exe.StartsWith("llama-server", StringComparison.OrdinalIgnoreCase) && port == null) port = "8080";
            return port == null ? name : name + "  :" + port;
        }

        static string ArgAfter(List<string> args, params string[] flags)
        {
            for (int i = 0; i < args.Count; i++)
            {
                foreach (var f in flags)
                {
                    if (args[i] == f && i + 1 < args.Count) return args[i + 1];
                    if (args[i].StartsWith(f + "=")) return args[i].Substring(f.Length + 1);
                }
            }
            return null;
        }

        void EnsureRows(StackPanel host, List<ProcRow> rows, int n)
        {
            while (rows.Count < n)
            {
                var r = new ProcRow();
                var row = r;
                r.Cursor = Cursors.Hand;
                r.MouseLeftButtonUp += delegate { GoTo(row); };
                r.HintFor = Hint;
                rows.Add(r);
                host.Children.Add(r);
            }
            for (int i = 0; i < rows.Count; i++) rows[i].Visibility = i < n ? Visibility.Visible : Visibility.Collapsed;
        }

        // Only "| code:" paths open in the editor. Everything else opens File Explorer.
        static readonly string[] Editor = FindEditor();
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
        static string OpenInEditor { get { return Editor == null ? "Show in File Explorer" : "Open in " + Editor[1]; } }

        // Paths from other processes or settings: show or open only when they look like real files/folders.
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

        // User-typed "| code:" folder or file: open in Cursor/VS Code when found.
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

        // Process, task, or command-line paths: select the file in Explorer, or open the folder.
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

        string Hint(ProcRow row)
        {
            if (row.Task != null) return row.Task.Script != null ? "Show in File Explorer" : "";
            if (row.Bot != null && row.Bot.Code != null) return OpenInEditor;
            if (row.Pids.Length == 0) return "";
            if (row.Script != null || ScriptOf(row.Pids) != null) return "Show in File Explorer";
            if (row.Pids.Select(Procs.ImagePath).Any(x => !string.IsNullOrEmpty(x))) return "Show in File Explorer";
            return "";
        }

        // The one script file (.py, .js, .ps1 ...) these processes are running, if they all run the same one.
        static string ScriptOf(int[] pids)
        {
            string found = null;
            foreach (int pid in pids)
            {
                var m = Regex.Match(Procs.CommandLine(pid) ?? "", "\"?([A-Za-z]:\\\\[^\"]+?\\.(?:py|pyw|js|mjs|cjs|ts|ps1))\"?(?:\\s|$)", RegexOptions.IgnoreCase);
                if (!m.Success || !File.Exists(m.Groups[1].Value)) continue;
                if (found != null && !string.Equals(found, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) return null;
                found = m.Groups[1].Value;
            }
            return found;
        }

        void GoTo(ProcRow row)
        {
            if (row.Task != null)
            {
                if (row.Task.Script != null) OpenExplorer(row.Task.Script);
                return;
            }
            if (row.Bot != null && row.Bot.Code != null) { OpenBot(row.Bot); return; }
            if (row.Pids.Length == 0) return;
            if (row.Script != null)
            {
                bool module = row.Script.StartsWith("module:");
                OpenExplorer(module ? Scripts.Folder(row.Script) : row.Script);
                return;
            }
            string script = ScriptOf(row.Pids);
            if (script != null) { OpenExplorer(script); return; }
            OpenExplorer(row.Pids.Select(Procs.ImagePath).FirstOrDefault(x => !string.IsNullOrEmpty(x)));
        }

        void OpenBot(WatchRule bot) { OpenCode(bot.Code); }

        Dictionary<int, long> vramByPid = new Dictionary<int, long>();
        bool vramBusy;
        DateTime vramRead = DateTime.MinValue;

        // Dedicated video memory per process, from Windows' "GPU Process Memory" counters. Read off the UI thread every 5 s.
        void ReadVram()
        {
            if (!vramBusy && (DateTime.Now - vramRead).TotalSeconds >= 5)
            {
                vramBusy = true;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var next = new Dictionary<int, long>();
                    try
                    {
                        var data = new PerformanceCounterCategory(GpuCounters.PerfName("GPU Process Memory")).ReadCategory();
                        var usage = data[GpuCounters.PerfName("Local Usage")];
                        if (usage != null)
                            foreach (InstanceData d in usage.Values)
                            {
                                var m = Regex.Match(d.InstanceName, "^pid_(\\d+)_");
                                if (!m.Success || d.RawValue <= 0) continue;
                                int pid = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                                long have;
                                next.TryGetValue(pid, out have);
                                next[pid] = have + d.RawValue;
                            }
                    }
                    catch (Exception) { }
                    Dispatcher.BeginInvoke(new Action(delegate { vramByPid = next; vramRead = DateTime.Now; vramBusy = false; }));
                });
            }
        }

        // ---------- review: "here's what I found, is it right?" ----------

        List<ReviewItem> ReviewItems()
        {
            var items = new List<ReviewItem>();
            foreach (var l in lastAi)
            {
                items.Add(new ReviewItem { Section = "AI", Name = l.Name, Kind = l.Kind, Rule = l.Rule, On = !hidden.Contains(l.Name), Kinds = AiChoices, Detail = l.Model != null ? (l.Procs.Count == 0 ? "installed" : "in use") : l.Procs.Count == 0 ? "not running" : "running" });
            }
            foreach (var r in botRules)
            {
                var hits = lastProcs.Where(r.Hit).ToList();
                items.Add(new ReviewItem { Section = "Bot", Name = r.Label, Kind = "Bot", Rule = r, On = !hidden.Contains(r.Label), Detail = hits.Count == 0 ? "not running" : "running" });
            }
            foreach (var kv in lastScripts)
            {
                string label = Scripts.Label(kv.Key);
                string match = kv.Key.StartsWith("module:") ? "-m " + kv.Key.Substring(7).Split('|')[0] : kv.Key;
                items.Add(new ReviewItem { Section = "Script", Name = label, Kind = "Script", Match = match, On = !hidden.Contains(label), Kinds = ScriptChoices, Detail = Scripts.Language(kv.Value[0].Exe) });
            }
            foreach (var t in TaskList.Items.Where(t => !t.OnDemand).OrderByDescending(t => t.Running).ThenByDescending(t => t.LastRun))
                items.Add(new ReviewItem { Section = t.StartsWithWindows ? "Start" : "Task", Name = t.Name, Kind = "Task", On = !hidden.Contains(t.Name), Detail = t.Running ? "running" : t.NeverRan ? "never ran" : t.Failed ? "failed" : "last ran " + Short(DateTime.Now - t.LastRun) + " ago" });
            foreach (var r in rules)
            {
                var hits = lastProcs.Where(r.Hit).ToList();
                string kind = r.Kind ?? "Watching";
                string k;
                if (kindOf.TryGetValue(r.Label, out k)) kind = k;
                items.Add(new ReviewItem { Section = "Watching", Name = r.Label, Kind = kind, Rule = r, On = !hidden.Contains(r.Label), Kinds = WatchChoices, Detail = hits.Count == 0 ? "not running" : "running" });
            }
            foreach (var i in items) i.OrigKind = i.Kind;
            return items;
        }

        void ShowReview()
        {
            if (reviewView != null || rootGrid == null) return;
            var items = ReviewItems();

            var page = new Grid();
            page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var stack = new StackPanel { Margin = new Thickness(14, 2, 14, 6) };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = stack };
            page.Children.Add(scroll);

            var title = Theme.Label("Here's what I found", 15, Theme.Text, FontWeights.SemiBold);
            title.Margin = new Thickness(4, 0, 0, 10);
            stack.Children.Add(title);

            foreach (var sec in new[] { "AI", "Bot", "Script", "Start", "Task", "Watching" })
            {
                var these = items.Where(i => i.Section == sec).ToList();
                if (these.Count == 0) continue;
                var secCard = Card(sec, null, these.Select(ReviewRow).ToArray());
                secCard.Margin = new Thickness(0, 0, 0, 12);
                stack.Children.Add(secCard);
            }

            var foot = new Grid { Margin = new Thickness(18, 4, 14, 14) };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            var edit = Theme.Label("Edit settings file", 12, Theme.Link, FontWeights.Normal);
            edit.Cursor = Cursors.Hand;
            edit.ToolTip = "For things that aren't open now, or a bot's health check";
            edit.MouseLeftButtonUp += delegate { OpenSettingsFile(); };
            left.Children.Add(edit);
            reviewError = Theme.Label("", 12, Theme.AmberDot, FontWeights.Normal);
            left.Children.Add(reviewError);
            foot.Children.Add(left);
            var ok = new Border { Background = Theme.Green, CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 6, 16, 7), Cursor = Cursors.Hand };
            ok.Child = Theme.Label("Looks good", 13, new SolidColorBrush(Color.FromRgb(0x06, 0x24, 0x12)), FontWeights.SemiBold);
            ok.MouseLeftButtonUp += delegate { SaveReview(items); };
            Grid.SetColumn(ok, 1);
            foot.Children.Add(ok);
            Grid.SetRow(foot, 1);
            page.Children.Add(foot);

            reviewView = page;
            Grid.SetRow(page, 1);
            rootGrid.Children.Add(page);
            dashScroll.Visibility = Visibility.Collapsed;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        }

        UIElement ReviewRow(ReviewItem it)
        {
            var row = new Border { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(-6, 0, -6, 0), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var check = new TextBlock { Text = "\uE73E", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var box = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5), HorizontalAlignment = HorizontalAlignment.Left, Child = check, ToolTip = "Show on the board" };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var name = Theme.Label(it.Name, 13, Theme.Text, FontWeights.Normal);
            var tag = Theme.Tag(it.Kind);
            tag.Margin = new Thickness(7, 1, 0, 0);
            var detail = Theme.Label(it.Detail ?? "", 12, Theme.Faint, FontWeights.Normal);
            detail.Margin = new Thickness(7, 1, 0, 0);
            line.Children.Add(name);
            if (it.Kinds != null) line.Children.Add(tag);   // a card of one fixed kind already says what its lines are
            line.Children.Add(detail);
            Grid.SetColumn(line, 1);
            g.Children.Add(box); g.Children.Add(line);
            row.Child = g;
            row.ToolTip = it.On ? "Click to hide from the board" : "Click to show on the board";

            Action show = delegate
            {
                box.Background = it.On ? Theme.Link : Brushes.Transparent;
                box.BorderBrush = it.On ? Theme.Link : Theme.Sub;
                check.Visibility = it.On ? Visibility.Visible : Visibility.Hidden;
                name.Foreground = it.On ? Theme.Text : Theme.Faint;
                tag.Opacity = it.On ? 1 : 0.5;
                row.ToolTip = it.On ? "Click to hide from the board" : "Click to show on the board";
            };
            show();
            row.MouseEnter += delegate { row.Background = Theme.Hover; };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            row.MouseLeftButtonUp += delegate { it.On = !it.On; show(); };
            if (it.Kinds != null)
            {
                tag.Cursor = Cursors.Hand;
                tag.ToolTip = "Click to change kind: " + string.Join(", ", it.Kinds);
                tag.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    int at = Array.IndexOf(it.Kinds, it.Kind);
                    it.Kind = it.Kinds[(at + 1) % it.Kinds.Length];
                    Theme.Paint(tag, it.Kind);
                    if (!it.On) { it.On = true; show(); }
                };
            }
            return row;
        }

        // Writes "@hide", "@kind", and "@reviewed" to review.txt only. Never rewrites watch.txt.
        bool TryWriteReview(List<ReviewItem> items)
        {
            try
            {
                string file = WatchRule.ReviewFile();
                var lines = new List<string>();
                if (items != null)
                {
                    foreach (var it in items)
                    {
                        if (!it.On) lines.Add("@hide = " + it.Name);
                        else if (it.Kind != it.OrigKind) lines.Add("@kind = " + it.Name + " | " + it.Kind);
                    }
                }
                lines.Add("@reviewed = " + DateTime.Now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                File.WriteAllLines(file, lines, new UTF8Encoding(false));
                return true;
            }
            catch (Exception) { return false; }
        }

        // Snapshot --accept-review: always leave a review.txt with at least @reviewed.
        void WriteAcceptedReview()
        {
            List<ReviewItem> items = null;
            try { items = ReviewItems(); } catch (Exception) { }
            if (TryWriteReview(items)) return;
            try
            {
                string file = WatchRule.ReviewFile();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                File.WriteAllLines(file, new[] { "@reviewed = " + DateTime.Now.ToString("yyyy-MM-dd") }, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        void SaveReview(List<ReviewItem> items)
        {
            if (!TryWriteReview(items))
            {
                if (reviewError != null) reviewError.Text = "Could not save settings";
                return;
            }
            LoadConfig();
            foreach (var l in new[] { aiRows, botRows, scriptRows, startRows, taskRows, watchRows, topRows }) l.Clear();
            reviewView = null;
            reviewError = null;
            Content = Build();
            SetLive(live);
            if (live) Prime();
        }

        void OpenSettingsFile()
        {
            try
            {
                string file = WatchRule.ConfigFile();
                if (!File.Exists(file))
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                    File.WriteAllText(file, "# Desktop Vitals settings for this PC. See watch.example.txt in the app folder for the format.\r\n", new UTF8Encoding(false));
                }
                using (Process.Start(new ProcessStartInfo("notepad.exe", "\"" + file + "\"") { UseShellExecute = true })) { }
            }
            catch (Exception) { }
        }

        // ---------- snapshot for other programs (scripts, AI agents) ----------

        void WriteSnapshot()
        {
            try
            {
                string dir = System.IO.Path.GetFullPath(snapshotDir);
                Directory.CreateDirectory(dir);
                string txtPath = System.IO.Path.Combine(dir, "vitals.txt");
                string pngPath = System.IO.Path.Combine(dir, "vitals.png");

                var sb = new StringBuilder();
                sb.AppendLine("Desktop Vitals  " + Environment.MachineName + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "  \u00B7  " + subtitle.Text);
                sb.AppendLine();
                foreach (var r in new[] { cpuRow, memRow, gpuRow, vramRow, diskRow }) sb.AppendLine(r.Summary);
                string busy = threadStrip.Busiest();
                if (busy.Length > 0) sb.AppendLine("Busiest thread: " + busy);
                Section(sb, "AI", aiRows, "");
                Section(sb, "Bots", botRows, "");
                Section(sb, "Scripts", scriptRows, "none running");
                Section(sb, "Starts with Windows", startRows, "");
                Section(sb, "Scheduled", taskRows, "");
                Section(sb, "Pinned", watchRows, "");
                Section(sb, "Top apps", topRows, "");
                string text = Privacy.Show(sb.ToString());
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

        static void Section(StringBuilder sb, string title, List<ProcRow> rows, string empty)
        {
            var shown = rows.Where(r => r.Visibility == Visibility.Visible).ToList();
            if (shown.Count == 0 && empty.Length == 0) return;
            sb.AppendLine();
            sb.AppendLine(title + (shown.Count == 0 ? ": " + empty : ""));
            foreach (var r in shown) sb.AppendLine("  " + r.Summary);
        }

        void WritePrivacyProof(string dir)
        {
            const string sample = @"C:\Users\Sam\x.ps1 --api-key sk-dvsmokeTESTKEY0001 --token ghp_dvsmokeTESTTOKEN0123456789 password=dvsmokePass99 Bearer dvsmokeBearerTok99 API_KEY=dvsmokeEnv99";
            var tips = aiRows.Concat(botRows).Concat(scriptRows).Concat(startRows).Concat(taskRows).Concat(watchRows).Concat(topRows)
                .Where(r => r.Visibility == Visibility.Visible && r.ToolTip != null).Select(r => "tip=" + r.ToolTip);
            File.WriteAllText(System.IO.Path.Combine(dir, "privacy.txt"),
                "show=" + Privacy.Show(sample) + "\nlog=" + Privacy.Log(sample) + "\n" + string.Join("\n", tips) + "\n", new UTF8Encoding(false));
        }

        // ---------- window position ----------

        void RestorePlacement()
        {
            try
            {
                string f = System.IO.Path.Combine(StateDir, "window.txt");
                if (!File.Exists(f)) return;
                var v = File.ReadAllText(f).Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                if (v.Length < 3) return;
                var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (!vs.Contains(new Point(v[0] + 40, v[1] + 20))) return;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = v[0]; Top = v[1];
                if (v.Length > 3) Width = Math.Max(MinWidth, v[3]);
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
                    string.Join(",", new[] { r.Left, r.Top, r.Height, r.Width }.Select(d => d.ToString(CultureInfo.InvariantCulture))));
            }
            catch (Exception) { }
        }

        // Ctrl+Alt+P: raise window, or minimize if already frontmost.
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
            Topmost = true; Topmost = false;
            Focus();
        }
    }

}
