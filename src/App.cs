using System; using System.Collections.Generic; using System.Globalization; using System.IO;
using System.Runtime.Serialization.Json; using System.Text; using System.Text.RegularExpressions; using System.Threading;
using System.Windows;
namespace DesktopVitals {
    static class Redact {
        static readonly Regex Secret = new Regex(@"(?i)(?:--?(?:api[_-]?key|token|access[_-]?token|secret|password|passwd|pwd)\b\s*[=:]?\s*)\S+|(?:(?:api[_-]?key|token|password|passwd|secret)\s*=\s*)\S+|\b(?:sk-[A-Za-z0-9_\-]{8,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9\-]{10,})\b|\bBearer\s+\S+|(?:https?|ftp)://[^/\s:@]+:[^/\s:@]+@[^\s]+", RegexOptions.Compiled);
        static readonly Regex UserDir = new Regex(@"(?i)([A-Za-z]:\\|\\\\)Users([/\\])[^/\\]+", RegexOptions.Compiled);
        static readonly Regex EnvAssign = new Regex(@"\b[A-Z][A-Z0-9_]{1,64}=[^\s""]+", RegexOptions.Compiled);
        static readonly Regex EnvPct = new Regex(@"%[A-Za-z_][A-Za-z0-9_]{0,64}%", RegexOptions.Compiled);
        static readonly Regex CmdLine = new Regex(@"(?i)(?:""?[A-Za-z]:\\[^""\r\n]{0,200}\.(?:exe|bat|cmd|ps1|com)""?(?:\s+/[cCkK]\s+""[^""]*"")?|\bcmd\.exe\b[^\r\n]*)", RegexOptions.Compiled);
        public static string Show(string s) { return string.IsNullOrEmpty(s) ? (s ?? "") : UserDir.Replace(Secret.Replace(s, "<redacted>"), "$1Users$2<user>"); }
        public static string Log(string s) { return string.IsNullOrEmpty(s) ? "" : CmdLine.Replace(EnvPct.Replace(EnvAssign.Replace(Show(s), "<env>"), "%<env>%"), "<command>"); } }
    static class ErrorLog {
        const string Ver = "2.0"; const int Cap = 256 * 1024;
        public static void Write(Exception ex) {
            if (ex == null) return;
            try {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopVitals");
                Directory.CreateDirectory(dir); string file = Path.Combine(dir, "errors.log"); var sb = new StringBuilder();
                sb.Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append(" ver=").Append(Ver);
                sb.Append(" type=").Append(ex.GetType().FullName); sb.Append(" msg=").Append(Redact.Log(ex.Message)).Append('\n');
                if (ex.StackTrace != null) sb.Append(Redact.Log(ex.StackTrace)).Append('\n'); sb.Append('\n');
                byte[] add = Encoding.UTF8.GetBytes(sb.ToString());
                byte[] prior = File.Exists(file) ? File.ReadAllBytes(file) : new byte[0];
                if (prior.Length + add.Length > Cap) {
                    int keep = Math.Max(0, prior.Length / 2); var trimmed = new byte[keep + add.Length];
                    Buffer.BlockCopy(prior, prior.Length - keep, trimmed, 0, keep);
                    Buffer.BlockCopy(add, 0, trimmed, keep, add.Length); File.WriteAllBytes(file, trimmed); }
                else {
                    using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
                        fs.Write(add, 0, add.Length); } }
            catch (Exception) { } } }
    class PinRule {
        public string Id; public string Label; public string Match; public bool ByExe; }
    static class Prefs {
        public static string Name = "";
        public static readonly Dictionary<string, string> Rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static readonly HashSet<string> Hides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static readonly HashSet<string> PinIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static readonly List<PinRule> PinRules = new List<PinRule>(); public static bool Migrated;
        public static bool ImportNote; public static bool Readable = true; public static bool TooLarge;
        public static string Dir() {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopVitals"); }
        public static string FilePath() { return Path.Combine(Dir(), "prefs.txt"); }
        public static void Load() {
            Readable = true; TooLarge = false; Migrated = false; ImportNote = false; Name = ""; Rename.Clear(); Hides.Clear();
            PinIds.Clear(); PinRules.Clear(); string path = FilePath(); if (!File.Exists(path)) return;
            try {
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length > 1024 * 1024) { TooLarge = true; return; }
                for (int i = 0; i < bytes.Length; i++) if (bytes[i] == 0) { Readable = false; return; }
                string text = Encoding.UTF8.GetString(bytes);
                if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
                string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                foreach (string raw in lines) {
                    string line = raw.Trim(); int eq = line.IndexOf('='); if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(); string val = line.Substring(eq + 1);
                    if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) Name = val.Trim();
                    else if (key.Equals("rename", StringComparison.OrdinalIgnoreCase)) {
                        string[] parts = val.Split(new[] { '|' }, 2);
                        if (parts.Length == 2 && parts[0].Length > 0) Rename[parts[0]] = parts[1]; }
                    else if (key.Equals("hide", StringComparison.OrdinalIgnoreCase)) {
                        string hide = val.Trim(); if (hide.Length > 0) Hides.Add(hide); }
                    else if (key.Equals("pin", StringComparison.OrdinalIgnoreCase)) {
                        string id = val.Trim(); if (id.Length > 0) PinIds.Add(id); }
                    else if (key.Equals("pinmatch", StringComparison.OrdinalIgnoreCase)) {
                        string[] parts = val.Split(new[] { '|' }, 3);
                        if (parts.Length == 3 && parts[1].Trim().Length > 0 && parts[2].Length > 0) {
                            var rule = new PinRule(); rule.Label = parts[1].Trim(); rule.Match = parts[2];
                            rule.ByExe = parts[0].Trim() == "1" || rule.Match.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                            rule.Id = "pin:" + rule.Label; PinRules.Add(rule); } }
                    else if (key.Equals("migratedFromWatch", StringComparison.OrdinalIgnoreCase)) Migrated = val.Trim() == "1";
                    else if (key.Equals("importNote", StringComparison.OrdinalIgnoreCase)) ImportNote = val.Trim() == "1"; } }
            catch (Exception) { Readable = false; } }
        public static bool Save() {
            if (!Readable || TooLarge) return false;
            try {
                var sb = new StringBuilder(); if (!string.IsNullOrEmpty(Name)) sb.Append("name=").Append(Name).Append('\n');
                foreach (var kv in Rename) sb.Append("rename=").Append(kv.Key).Append('|').Append(kv.Value).Append('\n');
                foreach (string h in Hides) sb.Append("hide=").Append(h).Append('\n');
                foreach (string id in PinIds) sb.Append("pin=").Append(id).Append('\n');
                foreach (PinRule rule in PinRules)
                    sb.Append("pinmatch=").Append(rule.ByExe ? "1" : "0").Append('|').Append(rule.Label).Append('|').Append(rule.Match).Append('\n');
                if (Migrated) sb.Append("migratedFromWatch=1\n"); if (ImportNote) sb.Append("importNote=1\n");
                Directory.CreateDirectory(Dir()); File.WriteAllText(FilePath(), sb.ToString(), new UTF8Encoding(false));
                return true; }
            catch (Exception) { return false; } }
        public static void Migrate() {
            if (!Readable || TooLarge || Migrated) return;
            try {
                string watch = Path.Combine(Dir(), "watch.txt"); string review = Path.Combine(Dir(), "review.txt");
                bool haveWatch = File.Exists(watch); bool haveReview = File.Exists(review); if (!haveWatch && !haveReview) return;
                var lines = new List<string>(); bool huge = false; if (haveWatch && !ReadSource(watch, lines, ref huge)) return;
                if (haveReview && !ReadSource(review, lines, ref huge)) return; if (huge) return; bool imported = false;
                foreach (string raw in lines) {
                    string line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue; int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.StartsWith("@")) {
                        string key = line.Substring(1, eq - 1).Trim(); string val = line.Substring(eq + 1).Trim();
                        if (key.Equals("hide", StringComparison.OrdinalIgnoreCase) && val.Length > 0) {
                            Hides.Add(val); imported = true; }
                        continue; }
                    string label = line.Substring(0, eq).Trim(); string rest = line.Substring(eq + 1);
                    string[] parts = rest.Split('|'); string match = parts.Length == 0 ? "" : parts[0].Trim();
                    if (label.Length == 0 || match.Length == 0) continue; if (HavePin(label)) continue; var rule = new PinRule();
                    rule.Label = label; rule.Match = match;
                    rule.ByExe = match.EndsWith(".exe", StringComparison.OrdinalIgnoreCase); rule.Id = "pin:" + label;
                    PinRules.Add(rule); imported = true; }
                Migrated = true; ImportNote = imported; if (!Save()) return; Archive(watch); Archive(review); }
            catch (Exception) { } }
        static bool HavePin(string label) {
            foreach (PinRule rule in PinRules)
                if (rule.Label != null && rule.Label.Equals(label, StringComparison.OrdinalIgnoreCase)) return true;
            return false; }
        static bool ReadSource(string path, List<string> lines, ref bool huge) {
            try {
                var fi = new FileInfo(path);
                if (fi.Length > 1024 * 1024) { huge = true; return true; }
                byte[] bytes = File.ReadAllBytes(path); for (int i = 0; i < bytes.Length; i++) if (bytes[i] == 0) return false;
                string text = Encoding.UTF8.GetString(bytes);
                if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
                lines.AddRange(text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
                return true; }
            catch (Exception) { return false; } }
        static void Archive(string path) {
            try {
                if (!File.Exists(path)) return; string dest = path + ".migrated"; if (File.Exists(dest)) return;
                File.Move(path, dest); }
            catch (Exception) { } } }
    static class BoardJson {
        static readonly DataContractJsonSerializer Ser = new DataContractJsonSerializer(typeof(Board));
        public static Board Read(string path) {
            using (var fs = File.OpenRead(path))
                return (Board)Ser.ReadObject(fs); }
        public static string Write(Board board) {
            using (var ms = new MemoryStream()) {
                Ser.WriteObject(ms, Redacted(board)); return Encoding.UTF8.GetString(ms.ToArray()); } }
        static string Opt(string s) { return s == null ? null : Redact.Show(s); }
        static Board Redacted(Board src) {
            var b = new Board(); if (src == null) return b; b.name = Redact.Show(src.name); b.strip = CopyStrip(src.strip);
            b.headline = new Headline();
            if (src.headline != null) {
                b.headline.text = Redact.Show(src.headline.text); b.headline.needs_you = src.headline.needs_you; }
            if (src.groups == null) return b;
            foreach (Group g in src.groups) {
                var ng = new Group(); ng.title = Redact.Show(g.title); ng.kind = Redact.Show(g.kind);
                if (g.things != null)
                    foreach (Thing t in g.things) ng.things.Add(CopyThing(t));
                b.groups.Add(ng); }
            return b; }
        static Strip CopyStrip(Strip s) {
            var n = new Strip(); if (s == null) return n; n.cpu = s.cpu; n.ram_used_gb = s.ram_used_gb;
            n.ram_total_gb = s.ram_total_gb; n.gpu = s.gpu; n.gpu_temp = s.gpu_temp; n.vram_used_gb = s.vram_used_gb;
            n.vram_total_gb = s.vram_total_gb; n.disk_free_gb = s.disk_free_gb; n.disk_total_gb = s.disk_total_gb;
            n.gpu_name = Opt(s.gpu_name); n.watts = s.watts; if (s.threads != null) n.threads = new List<double>(s.threads);
            if (s.core_groups != null)
                foreach (CoreGroup g in s.core_groups) {
                    var cg = new CoreGroup();
                    if (g == null) { n.core_groups.Add(cg); continue; }
                    cg.kind = Redact.Show(g.kind); if (g.threads != null) cg.threads = new List<int>(g.threads);
                    n.core_groups.Add(cg); }
            return n; }
        static Thing CopyThing(Thing t) {
            var n = new Thing(); if (t == null) return n; n.id = Redact.Show(t.id); n.name = Redact.Show(t.name);
            n.kind = Redact.Show(t.kind); n.state = Redact.Show(t.state); n.project = Opt(t.project); n.branch = Opt(t.branch);
            n.folder = Opt(t.folder); if (t.ports != null) n.ports = new List<int>(t.ports); n.cpu = t.cpu; n.ram_mb = t.ram_mb;
            n.vram_mb = t.vram_mb; n.count = t.count; n.since = Redact.Show(t.since); n.detail = Opt(t.detail);
            n.why = Redact.Show(t.why); n.pinned = t.pinned; return n; } }
    static class Program {
        [STAThread]
        static void Main(string[] args) {
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { ErrorLog.Write(e.ExceptionObject as Exception); };
            try { Prefs.Load(); Prefs.Migrate(); }
            catch (Exception ex) { ErrorLog.Write(ex); }
            bool changed = ApplyPrefArgs(args); string fixturePath = ArgValue(args, "--fixture"); Board fixture = null;
            if (fixturePath != null) {
                try { fixture = BoardJson.Read(fixturePath); }
                catch (Exception ex) {
                    try { Console.Error.WriteLine("Fixture failed: " + ex.Message); } catch (Exception) { }
                    Environment.Exit(1); } }
            bool wantJson = Find(args, "--json") >= 0; int at = Find(args, "--snapshot");
            if (wantJson && at < 0) {
                Board board = fixture != null ? fixture : ReadLiveBoard(); string json = BoardJson.Write(board);
                string path = ArgValue(args, "--json");
                try {
                    if (!string.IsNullOrEmpty(path)) {
                        string full = Path.GetFullPath(path); string dir = Path.GetDirectoryName(full);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(full, json, Utf8); }
                    WriteOut(json); }
                catch (Exception) { Environment.Exit(1); }
                return; }
            int benchAt = Find(args, "--bench");
            if (benchAt >= 0 && at < 0) {
                int n = 21;
                if (benchAt + 1 < args.Length && !args[benchAt + 1].StartsWith("--")) {
                    int parsed;
                    if (int.TryParse(args[benchAt + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) n = parsed; }
                if (n < 5) n = 5; if (n > 40) n = 40;
                string benchPath = benchAt + 2 < args.Length && !args[benchAt + 2].StartsWith("--") ? args[benchAt + 2] : null;
                RunBench(n, benchPath); return; }
            if (at >= 0) {
                string dir = at + 1 < args.Length && !args[at + 1].StartsWith("--")
                    ? args[at + 1]
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopVitals", "snapshot");
                var snapApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                snapApp.DispatcherUnhandledException += delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) { ErrorLog.Write(e.Exception); e.Handled = true; };
                var snapWin = new VitalsWindow(Path.GetFullPath(dir)); if (fixture != null) snapWin.ShowFixture(fixture);
                int wAt = Find(args, "--width"); double w;
                if (wAt >= 0 && wAt + 1 < args.Length && double.TryParse(args[wAt + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out w))
                    snapWin.Width = Math.Max(snapWin.MinWidth, w);
                snapApp.Run(snapWin); return; }
            if (changed && fixture == null) return; bool created;
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DesktopVitals.Show", out created)) {
                if (!created) { signal.Set(); return; }
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) { ErrorLog.Write(e.Exception); e.Handled = true; };
                var win = new VitalsWindow(); if (fixture != null) win.ShowFixture(fixture);
                ThreadPool.RegisterWaitForSingleObject(signal, delegate { win.Dispatcher.BeginInvoke(new Action(win.Toggle)); }, null, -1, false);
                app.Run(win); } }
        static void RunBench(int n, string path) {
            Engine.Prime(); var start = DateTime.UtcNow;
            while (true) {
                Engine.Tick(); double sec = (DateTime.UtcNow - start).TotalSeconds; if (Engine.ScansComplete && sec >= 1) break;
                if (sec >= 20) break; Thread.Sleep(50); }
            Engine.Tick(); var samples = new double[n];
            for (int i = 0; i < n; i++) {
                Engine.Tick(); samples[i] = Engine.LastPassMs; }
            Array.Sort(samples); double p50 = samples[n / 2];
            string line = "p50_ms=" + p50.ToString("0.00", CultureInfo.InvariantCulture)
                + " n=" + n.ToString(CultureInfo.InvariantCulture)
                + " procs=" + Engine.LastProcs.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(path)) {
                string full = Path.GetFullPath(path); string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(full, line + "\n", new UTF8Encoding(false)); }
            try { WriteOut(line); }
            catch (Exception) { } }
        static readonly Encoding Utf8 = new UTF8Encoding(false);
        static void WriteOut(string text) {
            byte[] b = Utf8.GetBytes((text ?? "") + "\n"); Stream s = Console.OpenStandardOutput();
            s.Write(b, 0, b.Length); s.Flush(); }
        static Board ReadLiveBoard() {
            Engine.Prime(); var start = DateTime.UtcNow; Board board = null;
            while (true) {
                bool ready = Engine.TasksReady; board = Engine.Tick(); double sec = (DateTime.UtcNow - start).TotalSeconds; if (!ready && Engine.TasksReady) board = Engine.Tick();
                if (((ready || Engine.TasksReady) && sec >= 0.7) || sec >= 1.8) break; Thread.Sleep(120); }
            return board; }
        static string ArgValue(string[] args, string flag) {
            int at = Find(args, flag); if (at < 0 || at + 1 >= args.Length || args[at + 1].StartsWith("--")) return null;
            return args[at + 1]; }
        static int Find(string[] args, string flag) {
            if (args == null) return -1;
            for (int i = 0; i < args.Length; i++)
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) return i;
            return -1; }
        static bool ApplyPrefArgs(string[] args) {
            if (args == null || args.Length == 0) return false; bool changed = false;
            for (int i = 0; i < args.Length; i++) {
                string a = args[i];
                if (a.Equals("--name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && !args[i + 1].StartsWith("--")) {
                    Prefs.Name = args[++i]; changed = true; }
                else if (a.Equals("--rename", StringComparison.OrdinalIgnoreCase) && i + 2 < args.Length) {
                    string id = args[++i]; string name = args[++i];
                    if (id.Length > 0) { Prefs.Rename[id] = name; changed = true; } }
                else if (a.Equals("--hide", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && !args[i + 1].StartsWith("--")) {
                    Prefs.Hides.Add(args[++i]); changed = true; }
                else if (a.Equals("--pin", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && !args[i + 1].StartsWith("--")) {
                    Prefs.PinIds.Add(args[++i]); changed = true; }
                else if (a.Equals("--unpin", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && !args[i + 1].StartsWith("--")) {
                    string id = args[++i]; Prefs.PinIds.Remove(id);
                    for (int p = Prefs.PinRules.Count - 1; p >= 0; p--)
                        if (Prefs.PinRules[p].Id == id || string.Equals(Prefs.PinRules[p].Label, id, StringComparison.OrdinalIgnoreCase))
                            Prefs.PinRules.RemoveAt(p);
                    changed = true; } }
            if (changed) Prefs.Save(); return changed; } } }
