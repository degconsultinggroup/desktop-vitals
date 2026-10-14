// Entry, command line, and settings files.
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
    class WatchRule
    {
        public string Label;
        public string Match;
        public bool ByExe;
        public string Check;      // a file, folder or http address that shows the bot is working
        public string Code;       // the bot's folder, opened in your editor on click
        public bool Ai;           // shown in the AI card: "| ai", or the match names a known AI program
        public string Kind;       // "| kind: Agent" overrides what the line is called in the AI card
        public string Raw;        // the line as written, so the review screen can change it in place
        public bool IsBot { get { return Check != null; } }

        static readonly string[] AiWords = { "claude", "openclaw", "codex", "gemini", "chatgpt", "openai", "anthropic", "grok", "cursor", "windsurf", "copilot", "aider", "ollama", "llama", "lm studio", "koboldcpp", "vllm", "perplexity", "meetily" };
        public static bool SoundsLikeAi(string s) { return AiWords.Any(w => s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0); }

        // watch.txt and review.txt live under %APPDATA%\DesktopVitals.
        public static string ConfigFile()
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopVitals", "watch.txt");
        }
        public static string ReviewFile()
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopVitals", "review.txt");
        }

        public static bool ConfigTooLarge;

        // File.ReadAllLines keeps BOM and UTF-16. Skip files over 1 MB.
        public static List<string> ReadLines(string file)
        {
            var list = new List<string>();
            if (!File.Exists(file)) return list;
            try
            {
                if (new FileInfo(file).Length > 1024 * 1024) { ConfigTooLarge = true; return list; }
                list.AddRange(File.ReadAllLines(file));
            }
            catch (Exception) { }
            return list;
        }

        public static Dictionary<string, string> ParseSettings(List<string> lines)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (!line.StartsWith("@") || eq < 2) continue;
                d[line.Substring(1, eq - 1).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        public static List<string> ParseMany(List<string> lines, string key)
        {
            var list = new List<string>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.StartsWith("@") && eq > 1 && line.Substring(1, eq - 1).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    list.Add(line.Substring(eq + 1).Trim());
            }
            return list;
        }

        public static List<WatchRule> ParseRules(List<string> lines)
        {
            var rules = new List<WatchRule>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("@")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var r = new WatchRule();
                r.Label = line.Substring(0, eq).Trim();
                var parts = line.Substring(eq + 1).Split('|');
                r.Match = parts[0].Trim();
                foreach (var part in parts.Skip(1))
                {
                    string x = part.Trim();
                    if (x.StartsWith("check:", StringComparison.OrdinalIgnoreCase)) r.Check = x.Substring(6).Trim();
                    else if (x.StartsWith("code:", StringComparison.OrdinalIgnoreCase)) r.Code = x.Substring(5).Trim();
                    else if (x.Equals("ai", StringComparison.OrdinalIgnoreCase)) r.Ai = true;
                    else if (x.StartsWith("kind:", StringComparison.OrdinalIgnoreCase)) r.Kind = Theme.Kinds.Keys.FirstOrDefault(k => k.Equals(x.Substring(5).Trim(), StringComparison.OrdinalIgnoreCase));
                }
                r.Raw = raw;
                if (r.Kind != null && Theme.AiKinds.Contains(r.Kind)) r.Ai = true;
                if (SoundsLikeAi(r.Label + " " + r.Match)) r.Ai = true;
                r.ByExe = r.Match.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (r.Match.Length > 0) rules.Add(r);
            }
            return rules;
        }

        public bool Hit(Proc p)
        {
            if (ByExe) return string.Equals(p.Exe, Match, StringComparison.OrdinalIgnoreCase);
            return p.Cmd.IndexOf(Match, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // Scrub secrets and user folder names for tips, vitals.txt, errors.log. Matching still uses raw Cmd.
    static class Privacy
    {
        static readonly Regex Secret = new Regex(@"(?i)(?:--?(?:api[_-]?key|token|access[_-]?token|secret|password|passwd|pwd)\b\s*[=:]?\s*)\S+|(?:(?:api[_-]?key|token|password|passwd|secret)\s*=\s*)\S+|\b(?:sk-[A-Za-z0-9_\-]{8,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9\-]{10,})\b|\bBearer\s+\S+|(?:https?|ftp)://[^/\s:@]+:[^/\s:@]+@[^\s]+", RegexOptions.Compiled);
        static readonly Regex UserDir = new Regex(@"(?i)([A-Za-z]:\\|\\\\)Users([/\\])[^/\\]+", RegexOptions.Compiled);
        static readonly Regex EnvAssign = new Regex(@"\b[A-Z][A-Z0-9_]{1,64}=[^\s""]+", RegexOptions.Compiled);
        static readonly Regex EnvPct = new Regex(@"%[A-Za-z_][A-Za-z0-9_]{0,64}%", RegexOptions.Compiled);
        static readonly Regex CmdLine = new Regex(@"(?i)(?:""?[A-Za-z]:\\[^""\r\n]{0,200}\.(?:exe|bat|cmd|ps1|com)""?(?:\s+/[cCkK]\s+""[^""]*"")?|\bcmd\.exe\b[^\r\n]*)", RegexOptions.Compiled);
        public static string Show(string s) { return string.IsNullOrEmpty(s) ? (s ?? "") : UserDir.Replace(Secret.Replace(s, "<redacted>"), "$1Users$2<user>"); }
        public static string Log(string s) { return string.IsNullOrEmpty(s) ? "" : CmdLine.Replace(EnvPct.Replace(EnvAssign.Replace(Show(s), "<env>"), "%<env>%"), "<command>"); }
    }

    static class ErrorLog
    {
        const string Ver = "1.0";
        const int Cap = 256 * 1024;

        public static void Write(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopVitals");
                Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, "errors.log");
                var sb = new StringBuilder();
                sb.Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append(" ver=").Append(Ver);
                sb.Append(" type=").Append(ex.GetType().FullName);
                sb.Append(" msg=").Append(Privacy.Log(ex.Message)).Append('\n');
                if (ex.StackTrace != null) sb.Append(Privacy.Log(ex.StackTrace)).Append('\n');
                sb.Append('\n');
                byte[] add = Encoding.UTF8.GetBytes(sb.ToString());
                byte[] prior = File.Exists(file) ? File.ReadAllBytes(file) : new byte[0];
                if (prior.Length + add.Length > Cap)
                {
                    int keep = Math.Max(0, prior.Length / 2);
                    var trimmed = new byte[keep + add.Length];
                    Buffer.BlockCopy(prior, prior.Length - keep, trimmed, 0, keep);
                    Buffer.BlockCopy(add, 0, trimmed, keep, add.Length);
                    File.WriteAllBytes(file, trimmed);
                }
                else
                {
                    using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
                        fs.Write(add, 0, add.Length);
                }
            }
            catch (Exception) { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { ErrorLog.Write(e.ExceptionObject as Exception); };
            int at = Array.FindIndex(args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                string dir = at + 1 < args.Length && !args[at + 1].StartsWith("--") ? args[at + 1] : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopVitals", "snapshot");
                var snapApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                snapApp.DispatcherUnhandledException += (s, e) => { ErrorLog.Write(e.Exception); e.Handled = true; };
                // "--review" pictures the review screen; "--accept-review" writes review.txt without touching watch.txt.
                bool review = args.Any(a => a.Equals("--review", StringComparison.OrdinalIgnoreCase));
                bool accept = args.Any(a => a.Equals("--accept-review", StringComparison.OrdinalIgnoreCase));
                var snapWin = new VitalsWindow(System.IO.Path.GetFullPath(dir), review, accept);
                // "--width 1200" pictures a widened window.
                int wAt = Array.FindIndex(args, a => a.Equals("--width", StringComparison.OrdinalIgnoreCase));
                double w;
                if (wAt >= 0 && wAt + 1 < args.Length && double.TryParse(args[wAt + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out w)) snapWin.Width = Math.Max(snapWin.MinWidth, w);
                snapApp.Run(snapWin);
                return;
            }
            bool created;
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DesktopVitals.Show", out created))
            {
                if (!created) { signal.Set(); return; }
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                // One bad reading (an odd process, a driver hiccup) should skip a refresh, not close the window.
                app.DispatcherUnhandledException += (s, e) => { ErrorLog.Write(e.Exception); e.Handled = true; };
                var win = new VitalsWindow();
                ThreadPool.RegisterWaitForSingleObject(signal, delegate { win.Dispatcher.BeginInvoke(new Action(win.Toggle)); }, null, -1, false);
                app.Run(win);
            }
        }
    }

}
