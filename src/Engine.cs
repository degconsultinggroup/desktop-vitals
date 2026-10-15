using System; using System.Collections.Generic; using System.Diagnostics; using System.Globalization; using System.IO;
using System.Linq; using System.Runtime.InteropServices; using System.Runtime.Serialization; using System.Text;
using System.Text.RegularExpressions; using System.Threading;
namespace DesktopVitals {
    class Proc {
        public int Pid, Parent, Session;
        public string Exe;        // image name, e.g. chrome.exe
        public long CpuTime;      // 100 ns units, user + kernel
        public long CreateTime;
        public long Private;      // private working set, what Task Manager calls Memory
        public long Committed;    // private bytes, held even when Windows pages an idle process out of RAM
        public long Io;           // read + write operation counts from the snapshot
        public long WriteBytes;  // WriteTransferCount, this process only
        public double Cpu;       // percent of the whole machine
        public string Cmd = ""; }
    static class Procs {
        static IntPtr buf = IntPtr.Zero; static int bufLen;
        public static List<Proc> Snapshot() {
            var list = new List<Proc>(400);
            if (bufLen == 0) { bufLen = 1 << 20; buf = Marshal.AllocHGlobal(bufLen); }
            for (int tries = 0; ; tries++) {
                int ret; int st = Native.NtQuerySystemInformation(5, buf, bufLen, out ret);
                if (st == Native.StatusLengthMismatch && tries < 4) {
                    Marshal.FreeHGlobal(buf); bufLen = Math.Max(ret + (1 << 16), bufLen * 2); buf = Marshal.AllocHGlobal(bufLen);
                    continue; }
                if (st != 0) return list; break; }
            int off = 0;
            while (true) {
                IntPtr p = buf + off; int pid = (int)Marshal.ReadIntPtr(p, 80).ToInt64();
                if (pid != 0) {
                    var pr = new Proc(); pr.Pid = pid; pr.Parent = (int)Marshal.ReadIntPtr(p, 88).ToInt64();
                    pr.Session = Marshal.ReadInt32(p, 100); pr.Private = Marshal.ReadInt64(p, 8);
                    pr.Committed = Marshal.ReadInt64(p, 200);   // PrivatePageCount
                    pr.Io = Marshal.ReadInt64(p, 208) + Marshal.ReadInt64(p, 216); pr.WriteBytes = Marshal.ReadInt64(p, 240);
                    pr.CreateTime = Marshal.ReadInt64(p, 32);
                    pr.CpuTime = Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48);
                    int nlen = Marshal.ReadInt16(p, 56) & 0xffff; IntPtr name = Marshal.ReadIntPtr(p, 64);
                    pr.Exe = name == IntPtr.Zero ? "System" : Marshal.PtrToStringUni(name, nlen / 2); list.Add(pr); }
                int next = Marshal.ReadInt32(p, 0); if (next == 0) break; off += next; }
            return list; }
        public static string CommandLine(int pid) {
            IntPtr h = Native.OpenProcess(Native.QueryLimited, false, pid); if (h == IntPtr.Zero) return ""; int len = 4096;
            IntPtr b = Marshal.AllocHGlobal(len);
            try {
                int ret; int st = Native.NtQueryInformationProcess(h, 60, b, len, out ret);
                if ((st == Native.StatusLengthMismatch || st == Native.StatusBufferTooSmall) && ret > len && ret <= 1024 * 1024) {
                    Marshal.FreeHGlobal(b); len = ret; b = Marshal.AllocHGlobal(len);
                    st = Native.NtQueryInformationProcess(h, 60, b, len, out ret); }
                if (st != 0) return ""; int l = Marshal.ReadInt16(b, 0) & 0xffff; IntPtr s = Marshal.ReadIntPtr(b, 8);
                return s == IntPtr.Zero ? "" : Marshal.PtrToStringUni(s, l / 2); }
            finally { Marshal.FreeHGlobal(b); Native.CloseHandle(h); } }
        public static string CurrentDirectory(int pid) {
            IntPtr h = Native.OpenProcess(Native.QueryLimited | 0x0010, false, pid);   // PROCESS_VM_READ
            if (h == IntPtr.Zero) return null; IntPtr pbi = Marshal.AllocHGlobal(48);
            try {
                int ret; if (Native.NtQueryInformationProcess(h, 0, pbi, 48, out ret) != 0) return null;
                IntPtr peb = Marshal.ReadIntPtr(pbi, 8); string cwd = ReadCwd(h, peb, true); if (cwd != null) return cwd;
                IntPtr wowBuf = Marshal.AllocHGlobal(IntPtr.Size);
                try {
                    if (Native.NtQueryInformationProcess(h, 26, wowBuf, IntPtr.Size, out ret) != 0) return null;
                    IntPtr wow = Marshal.ReadIntPtr(wowBuf); if (wow == IntPtr.Zero) return null; return ReadCwd(h, wow, false); }
                finally { Marshal.FreeHGlobal(wowBuf); } }
            catch (Exception) { return null; }
            finally { Marshal.FreeHGlobal(pbi); Native.CloseHandle(h); } }
        static string ReadCwd(IntPtr h, IntPtr peb, bool wide) {
            IntPtr parms = ReadPtr(h, peb + (wide ? 0x20 : 0x10), wide ? 8 : 4); if (parms == IntPtr.Zero) return null;
            int ptr = wide ? 8 : 4; IntPtr us = parms + (wide ? 0x38 : 0x24); byte[] hdr = new byte[ptr == 8 ? 16 : 8];
            IntPtr got; if (!Native.ReadProcessMemory(h, us, hdr, hdr.Length, out got)) return null;
            int len = BitConverter.ToUInt16(hdr, 0);
            long bufAddr = ptr == 8 ? BitConverter.ToInt64(hdr, 8) : BitConverter.ToUInt32(hdr, 4);
            if (len <= 0 || len > 32 * 1024 || bufAddr == 0) return null; byte[] chars = new byte[len];
            if (!Native.ReadProcessMemory(h, new IntPtr(bufAddr), chars, len, out got)) return null;
            return Encoding.Unicode.GetString(chars).TrimEnd('\\'); }
        static IntPtr ReadPtr(IntPtr h, IntPtr addr, int bytes) {
            byte[] b = new byte[bytes]; IntPtr got; if (!Native.ReadProcessMemory(h, addr, b, bytes, out got)) return IntPtr.Zero;
            long v = bytes == 8 ? BitConverter.ToInt64(b, 0) : BitConverter.ToUInt32(b, 0); return new IntPtr(v); }
        public static string ImagePath(int pid) {
            IntPtr h = Native.OpenProcess(Native.QueryLimited, false, pid); if (h == IntPtr.Zero) return null;
            try {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null; }
            finally { Native.CloseHandle(h); } } }
    static class Paths {
        static readonly string[] Exts = { ".py", ".pyw", ".js", ".mjs", ".cjs", ".ts", ".ps1", ".bat", ".cmd", ".vbs", ".rb", ".php", ".pl", ".jar", ".sh" };
        static readonly string[] Hosts = { "python", "pythonw", "py", "pyw", "node", "bun", "deno", "powershell", "pwsh", "wscript", "cscript", "cmd", "ruby", "php", "perl", "java", "javaw", "bash" };
        public static List<string> Split(string cmd) {
            var list = new List<string>(); var sb = new StringBuilder(); bool q = false; if (cmd == null) return list;
            foreach (char c in cmd) {
                if (c == '"') { q = !q; continue; }
                if ((c == ' ' || c == '\t') && !q) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Length = 0; } continue; }
                sb.Append(c); }
            if (sb.Length > 0) list.Add(sb.ToString()); return list; }
        public static bool ScriptFile(string path) {
            if (string.IsNullOrEmpty(path)) return false;
            try { if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false; } catch (Exception) { return false; }
            string ext;
            try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch (Exception) { return false; }
            foreach (string e in Exts) if (e == ext) return true; return false; }
        public static bool Interpreter(string exe) {
            string n = Path.GetFileNameWithoutExtension(exe ?? "").ToLowerInvariant(); int i = n.Length;
            while (i > 0 && (char.IsDigit(n[i - 1]) || n[i - 1] == '.')) i--; n = n.Substring(0, i);
            foreach (string h in Hosts) if (h == n) return true; return false; }
        public static bool SystemPath(string path) {
            if (string.IsNullOrEmpty(path)) return true;
            if (path.IndexOf("\\node_modules\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (path.IndexOf("\\resources\\app", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (path.IndexOf("\\site-packages\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] roots = {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Programs"),
                string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Microsoft"),
                Path.GetTempPath()
            };
            foreach (string r in roots) {
                if (string.IsNullOrEmpty(r)) continue;
                if (path.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true; }
            return false; }
        public static bool Noise(string name, string path) {
            string s = ((name ?? "") + "\n" + (path ?? "")).ToLowerInvariant();
            if (s.IndexOf("svchost") >= 0 || s.IndexOf("msedge") >= 0 || s.IndexOf("edgeupdate") >= 0) return true;
            if (s.IndexOf("\\microsoft\\edge") >= 0 || s.IndexOf("microsoft edge") >= 0 || s.IndexOf("googleupdate") >= 0) return true;
            if (s.IndexOf("dellsupport") >= 0 || s.IndexOf("lenovo") >= 0 || s.IndexOf("hpsupport") >= 0) return true;
            if (s.IndexOf("zoom") >= 0 && s.IndexOf("update") >= 0) return true;
            if ((s.IndexOf("asus") >= 0 && s.IndexOf("system") >= 0) || s.IndexOf("hostedcompute") >= 0 || s.IndexOf("hosted-compute") >= 0) return true; return false; }
        public static string Find(string exe, string cmd, string cwd) {
            if (!Interpreter(exe) || string.IsNullOrEmpty(cmd)) return null; var args = Split(cmd);
            for (int i = 1; i < args.Count; i++) {
                string a = args[i];
                if ((a == "-m" || a == "-M") && i + 1 < args.Count && (exe ?? "").StartsWith("py", StringComparison.OrdinalIgnoreCase)) {
                    if (string.IsNullOrEmpty(cwd) || SystemPath(cwd + "\\")) return null;
                    return "module:" + args[i + 1] + "|" + cwd; }
                if (a.StartsWith("-") || a.StartsWith("/")) continue; if (!ScriptFile(a)) continue; string full = a;
                if (!Path.IsPathRooted(full)) {
                    if (string.IsNullOrEmpty(cwd)) return null;
                    try { full = Path.GetFullPath(Path.Combine(cwd, a)); } catch (Exception) { return null; } }
                if (SystemPath(full) || !File.Exists(full)) return null; return full; }
            return null; } }
    class InstalledModel {
        public string Name, Path; public long Bytes; }
    static class ModelStore {
        static readonly Regex Shard = new Regex(@"[-_.]\d{1,6}-of-\d{1,6}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static List<InstalledModel> Scan() {
            var list = new List<InstalledModel>(); string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string hfHome = Environment.GetEnvironmentVariable("HF_HOME");
            string hf = Environment.GetEnvironmentVariable("HF_HUB_CACHE");
            if (string.IsNullOrEmpty(hf)) hf = Path.Combine(string.IsNullOrEmpty(hfHome) ? Path.Combine(home, ".cache", "huggingface") : hfHome, "hub");
            string ollama = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
            if (string.IsNullOrEmpty(ollama)) ollama = Path.Combine(home, ".ollama", "models");
            var roots = new List<string> { Path.Combine(home, "models"), Path.Combine(home, ".lmstudio", "models"), Path.Combine(home, ".cache", "lm-studio", "models"), hf, ollama };
            AddModelRoots(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), roots);
            AddModelRoots(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), roots);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int budget = 0; int deadline = Environment.TickCount + 2000;
            foreach (string r in roots) { if (Late(deadline) || budget > 4000) break; Walk(r, list, seen, 0, ref budget, deadline); } ModelScan.Items = list;
            if (!Late(deadline)) try { foreach (DriveInfo d in DriveInfo.GetDrives()) if (!Late(deadline) && budget <= 4000 && d.DriveType == DriveType.Fixed) Walk(Path.Combine(d.RootDirectory.FullName, "models"), list, seen, 0, ref budget, deadline); }
            catch (Exception) { } return list; }
        static bool Late(int deadline) { return unchecked(Environment.TickCount - deadline) > 0; }
        static void AddModelRoots(string parent, List<string> roots) {
            if (string.IsNullOrEmpty(parent)) return; try {
                int n = 0; foreach (string sub in Directory.GetDirectories(parent)) {
                    if (++n > 80) break; string m = Path.Combine(sub, "models"); if (Directory.Exists(m)) roots.Add(m); } }
            catch (Exception) { } }
        static void Walk(string dir, List<InstalledModel> list, HashSet<string> seen, int depth, ref int budget, int deadline) {
            if (depth > 4 || budget > 4000 || string.IsNullOrEmpty(dir) || Late(deadline)) return; DirectoryInfo d;
            try {
                d = new DirectoryInfo(dir); if (!d.Exists || (d.Attributes & FileAttributes.ReparsePoint) != 0) return; }
            catch (Exception) { return; }
            string full;
            try { full = d.FullName; } catch (Exception) { return; }
            if (!seen.Add(full)) return;
            try {
                foreach (FileInfo f in d.EnumerateFiles()) {
                    if (++budget > 4000 || Late(deadline)) return; if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    string ext = f.Extension.ToLowerInvariant();
                    bool known = ext == ".gguf" || ext == ".safetensors" || ext == ".ggml";
                    bool big = f.Length >= (20L << 20) && (ext == ".bin" || ext == ".pt" || ext == ".pth" || ext == ".onnx" || ext == ".ckpt" || ext.Length == 0);
                    if (!known && !big) continue; string low = f.Name.ToLowerInvariant(); if (low.IndexOf("mmproj") >= 0 || low.IndexOf("tokenizer") >= 0 || low.IndexOf("vocab") >= 0) continue;
                    string name = Shard.Replace(Path.GetFileNameWithoutExtension(f.Name), "");
                    if ((depth > 0 && !f.Directory.Name.Equals("models", StringComparison.OrdinalIgnoreCase)) || name.Length >= 32 || name.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase)) name = FolderName(f.Directory);
                    Add(list, Pretty(name), f.FullName, f.Length); }
                if (depth == 4) return; int subs = 0; foreach (DirectoryInfo sub in d.EnumerateDirectories()) {
                    if (budget > 4000 || ++subs > 400 || Late(deadline)) return; if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 || sub.Name.StartsWith(".") || sub.Name.Equals("packs", StringComparison.OrdinalIgnoreCase) || sub.Name.Equals("mtp", StringComparison.OrdinalIgnoreCase)) continue;
                    Walk(sub.FullName, list, seen, depth + 1, ref budget, deadline); } }
            catch (Exception) { } }
        static string FolderName(DirectoryInfo dir) {
            string local = null; for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent) {
                string n = dir.Name; if (n == "blobs" || n == "snapshots") continue; if (n.StartsWith("models--")) { n = n.Substring(8).Replace("--", "/"); int s = n.LastIndexOf('/'); return s >= 0 && s < n.Length - 1 ? n.Substring(s + 1) : n; }
                if (local == null && n.Length > 0) local = n; }
            return string.IsNullOrEmpty(local) ? "model" : local; }
        static string Pretty(string name) {
            if (string.IsNullOrEmpty(name)) return "model"; int at = name.IndexOf("__"); if (at > 0 && at < name.Length - 2) name = name.Substring(at + 2); return name; }
        static void Add(List<InstalledModel> list, string name, string path, long bytes) {
            foreach (InstalledModel m in list)
                if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) { m.Bytes += bytes; return; }
            list.Add(new InstalledModel { Name = name, Path = path, Bytes = bytes }); } }
    class TaskInfo {
        public string Name, Path, Script, Exe; public int State, Result, RepeatMin; public DateTime LastRun, NextRun;
        public bool Running { get { return State == 4; } }
        public bool NeverRan { get { return Result == 0x41303 || LastRun.Year < 2000; } }
        public bool Failed { get { return !Running && !NeverRan && Result != 0 && Result != 0x41306 && Result != 0x41301; } } }
    static class TaskList {
        public static List<TaskInfo> Items = new List<TaskInfo>(); public static int Generation;
        public static bool Ready { get { return finished; } }
        public static bool Failed; static bool busy; static volatile bool finished; static DateTime read = DateTime.MinValue;
        public static void Soon() { if (!busy) read = DateTime.MinValue.AddSeconds(1); }
        public static void Refresh(Action done) {
            if (busy) { if (!finished && (DateTime.Now - read).TotalSeconds > 3) finished = true; return; } if (Ready && (DateTime.Now - read).TotalSeconds < 30) return; busy = true; read = DateTime.Now;
            ThreadPool.QueueUserWorkItem(delegate {
                var found = new List<TaskInfo>(); bool failed = false;
                try {
                    dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); svc.Connect();
                    Walk(svc.GetFolder("\\"), found); }
                catch (Exception) { failed = true; }
                Items = found; Generation++; Failed = failed; read = DateTime.Now; finished = true; busy = false;
                if (done != null) done();
            }); }
        static void Walk(dynamic folder, List<TaskInfo> found) {
            if (found.Count > 300) return; string path = (string)folder.Path; if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase)) return;
            int n = 0; foreach (dynamic t in folder.GetTasks(0)) { if (++n > 200 || found.Count > 300) return;
                try {
                    if (!(bool)t.Enabled) continue;
                    var info = new TaskInfo { Name = Regex.Replace((string)t.Name, "-S-1-5-[0-9-]+$", ""), Path = (string)t.Path, State = (int)t.State, Result = (int)t.LastTaskResult, LastRun = (DateTime)t.LastRunTime, NextRun = (DateTime)t.NextRunTime };
                    bool mine = false;
                    foreach (dynamic a in t.Definition.Actions) {
                        if ((int)a.Type != 0) continue;
                        string exe = Environment.ExpandEnvironmentVariables(((string)a.Path ?? "").Trim('"'));
                        string args = Environment.ExpandEnvironmentVariables((string)a.Arguments ?? ""); info.Exe = exe;
                        foreach (string arg in Paths.Split(args))
                            if (Path.IsPathRooted(arg) && Paths.ScriptFile(arg) && !Paths.SystemPath(arg)) { info.Script = arg; break; }
                        if (info.Script == null && Paths.ScriptFile(exe) && !Paths.SystemPath(exe)) info.Script = exe;
                        if (info.Script != null || (exe.Length > 0 && Path.IsPathRooted(exe) && !Paths.SystemPath(exe))) mine = true; }
                    if (Paths.Noise(info.Name, info.Exe) || (info.Script == null && info.Name.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0)) mine = false;
                    if (mine) { NoteRepeat(t, info); found.Add(info); } }
                catch (Exception) { } }
            int folders = 0; foreach (dynamic sub in folder.GetFolders(0)) { if (++folders > 40 || found.Count > 300) return; Walk(sub, found); } }
        static void NoteRepeat(dynamic task, TaskInfo info) {
            try { foreach (dynamic tr in task.Definition.Triggers) { string iso = ""; try { iso = (string)tr.Repetition.Interval; } catch (Exception) { }
                int mins = IsoMin(iso); if (mins > 0 && (info.RepeatMin == 0 || mins < info.RepeatMin)) info.RepeatMin = mins; } } catch (Exception) { }
            if (info.RepeatMin == 0 && info.LastRun.Year >= 2000 && info.NextRun > info.LastRun) {
                double gap = (info.NextRun - info.LastRun).TotalMinutes; if (gap >= 0.5 && gap <= 30) info.RepeatMin = (int)Math.Round(gap); } }
        static int IsoMin(string iso) {
            if (string.IsNullOrEmpty(iso)) return 0; var rx = Regex.Match(iso, @"^PT(?:(\d+)H)?(?:(\d+)M)?", RegexOptions.IgnoreCase);
            if (!rx.Success) return 0; int h = 0, m = 0; if (rx.Groups[1].Success) int.TryParse(rx.Groups[1].Value, out h);
            if (rx.Groups[2].Success) int.TryParse(rx.Groups[2].Value, out m); int total = h * 60 + m; return total > 0 && total <= 30 ? total : 0; } }
    static class GpuCounters {
        public static string Name; public static double Util, UsedGb, TotalGb; public static bool Have, Failed, UtilReady;
        static Dictionary<string, long> prevRaw = new Dictionary<string, long>(); static long prevTs; static bool busy, tried;
        static DateTime read = DateTime.MinValue;
        static readonly Dictionary<string, string> perfNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static string PerfName(string english) {
            string have; if (perfNames.TryGetValue(english, out have)) return have; string local = english;
            try {
                using (var en = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\009"))
                using (var cur = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\CurrentLanguage")) {
                    string[] enList = en != null ? en.GetValue("Counter") as string[] : null;
                    string[] curList = cur != null ? cur.GetValue("Counter") as string[] : null;
                    if (enList != null && curList != null) {
                        string index = null;
                        for (int i = 0; i + 1 < enList.Length; i += 2)
                            if (string.Equals(enList[i + 1], english, StringComparison.OrdinalIgnoreCase)) { index = enList[i]; break; }
                        if (index != null)
                            for (int i = 0; i + 1 < curList.Length; i += 2)
                                if (curList[i] == index) { local = curList[i + 1]; break; } } } }
            catch (Exception) { }
            return perfNames[english] = local; }
        public static void Refresh() {
            if (busy || (DateTime.Now - read).TotalSeconds < 2) return; busy = true;
            ThreadPool.QueueUserWorkItem(delegate {
                try {
                    if (!tried) { tried = true; ReadAdapter(); }
                    var eng = new PerformanceCounterCategory(PerfName("GPU Engine")).ReadCategory()[PerfName("Utilization Percentage")];
                    var raw = new Dictionary<string, long>(); long ts = 0;
                    foreach (InstanceData d in eng.Values) {
                        raw[d.InstanceName] = d.RawValue; ts = Math.Max(ts, d.Sample.TimeStamp100nSec); }
                    if (prevTs > 0 && ts > prevTs) {
                        var byType = new Dictionary<string, double>();
                        foreach (var kv in raw) {
                            long before; if (!prevRaw.TryGetValue(kv.Key, out before) || kv.Value < before) continue;
                            var m = Regex.Match(kv.Key, "luid_(\\w+?_\\w+?)_phys.*engtype_(.+)$"); if (!m.Success) continue;
                            string k = m.Groups[1].Value + "/" + m.Groups[2].Value; double have; byType.TryGetValue(k, out have);
                            byType[k] = have + (kv.Value - before) * 100.0 / (ts - prevTs); }
                        Util = byType.Count == 0 ? 0 : Math.Min(100, byType.Values.Max()); UtilReady = true; }
                    prevRaw = raw; prevTs = ts;
                    var mem = new PerformanceCounterCategory(PerfName("GPU Adapter Memory")).ReadCategory()[PerfName("Dedicated Usage")];
                    long used = 0; foreach (InstanceData d in mem.Values) used = Math.Max(used, d.RawValue);
                    UsedGb = used / 1073741824.0; Have = Name != null || used > 0 || UtilReady; Failed = false; }
                catch (Exception) { Have = false; Failed = true; }
                read = DateTime.Now; busy = false;
            }); }
        static void ReadAdapter() {
            try {
                using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}")) {
                    if (cls == null) return;
                    foreach (string sub in cls.GetSubKeyNames()) {
                        try {
                            using (var k = cls.OpenSubKey(sub)) {
                                if (k == null) continue; object size = k.GetValue("HardwareInformation.qwMemorySize");
                                long bytes = size is long ? (long)size : size is byte[] && ((byte[])size).Length >= 8 ? BitConverter.ToInt64((byte[])size, 0) : 0;
                                if (bytes / 1073741824.0 <= TotalGb) continue; TotalGb = bytes / 1073741824.0;
                                Name = ((string)k.GetValue("DriverDesc") ?? "GPU").Replace("NVIDIA ", "").Replace("GeForce ", "").Replace("AMD ", "").Replace("Radeon(TM) ", "Radeon "); } }
                        catch (Exception) { } } } }
            catch (Exception) { } } }
    [DataContract]
    public class Board {
        [DataMember(Order = 1)] public string name = "";
        [DataMember(Order = 2)] public Strip strip = new Strip();
        [DataMember(Order = 3)] public Headline headline = new Headline();
        [DataMember(Order = 4)] public List<Group> groups = new List<Group>(); }
    [DataContract]
    public class Strip {
        [DataMember(Order = 1)] public double cpu;
        [DataMember(Order = 2)] public double ram_used_gb;
        [DataMember(Order = 3)] public double ram_total_gb;
        [DataMember(Order = 4, EmitDefaultValue = false)] public double? gpu;
        [DataMember(Order = 5, EmitDefaultValue = false)] public double? gpu_temp;
        [DataMember(Order = 6, EmitDefaultValue = false)] public double? vram_used_gb;
        [DataMember(Order = 7, EmitDefaultValue = false)] public double? vram_total_gb;
        [DataMember(Order = 8)] public double disk_free_gb;
        [DataMember(Order = 9)] public double disk_total_gb;
        [DataMember(Order = 10, EmitDefaultValue = false)] public string gpu_name;
        [DataMember(Order = 11, EmitDefaultValue = false)] public double? watts;
        [DataMember(Order = 12)] public List<double> threads = new List<double>();
        [DataMember(Order = 13)] public List<CoreGroup> core_groups = new List<CoreGroup>(); }
    [DataContract]
    public class CoreGroup {
        [DataMember(Order = 1)] public string kind = "core";
        [DataMember(Order = 2)] public List<int> threads = new List<int>(); }
    [DataContract]
    public class Headline {
        [DataMember(Order = 1)] public string text = "All good";
        [DataMember(Order = 2)] public bool needs_you; }
    [DataContract]
    public class Group {
        [DataMember(Order = 1)] public string title = "";
        [DataMember(Order = 2)] public string kind = "project";
        [DataMember(Order = 3)] public List<Thing> things = new List<Thing>(); }
    [DataContract]
    public class Thing {
        [DataMember(Order = 1)] public string id = "";
        [DataMember(Order = 2)] public string name = "";
        [DataMember(Order = 3)] public string kind = "other";
        [DataMember(Order = 4)] public string state = "idle";
        [DataMember(Order = 5, EmitDefaultValue = false)] public string project;
        [DataMember(Order = 6, EmitDefaultValue = false)] public string branch;
        [DataMember(Order = 7, EmitDefaultValue = false)] public string folder;
        [DataMember(Order = 8)] public List<int> ports = new List<int>();
        [DataMember(Order = 9)] public double cpu;
        [DataMember(Order = 10)] public double ram_mb;
        [DataMember(Order = 11)] public double vram_mb;
        [DataMember(Order = 12)] public int count = 1;
        [DataMember(Order = 13)] public string since = "";
        [DataMember(Order = 14, EmitDefaultValue = false)] public string detail;
        [DataMember(Order = 15)] public string why = "";
        [DataMember(Order = 16)] public bool pinned; }
    static class Vram {
        public static Dictionary<int, long> ByPid = new Dictionary<int, long>(); static bool busy;
        static DateTime read = DateTime.MinValue;
        public static void Refresh() {
            if (busy || (DateTime.Now - read).TotalSeconds < 5) return; busy = true;
            ThreadPool.QueueUserWorkItem(delegate {
                var next = new Dictionary<int, long>();
                try {
                    var data = new PerformanceCounterCategory(GpuCounters.PerfName("GPU Process Memory")).ReadCategory();
                    var usage = data[GpuCounters.PerfName("Local Usage")];
                    if (usage != null)
                        foreach (InstanceData d in usage.Values) {
                            var m = Regex.Match(d.InstanceName, "^pid_(\\d+)_"); if (!m.Success || d.RawValue <= 0) continue;
                            int pid = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); long have;
                            next.TryGetValue(pid, out have); next[pid] = have + d.RawValue; } }
                catch (Exception) { }
                ByPid = next; read = DateTime.Now; busy = false;
            }); } }
    static class ModelScan {
        public static List<InstalledModel> Items = new List<InstalledModel>(); public static volatile bool Ready;
        static bool busy; static DateTime read = DateTime.MinValue;
        public static void Refresh() {
            if (busy) { if (!Ready && (DateTime.Now - read).TotalSeconds > 3) Ready = true; return; } if (Ready && (DateTime.Now - read).TotalSeconds < 30) return; busy = true; read = DateTime.Now;
            ThreadPool.QueueUserWorkItem(delegate {
                var found = new List<InstalledModel>();
                try { found = ModelStore.Scan(); }
                catch (Exception) { }
                Items = found; read = DateTime.Now; Ready = true; busy = false;
            }); } }
    static class BoardText {
        public static string Dot(string state) {
            switch (state) {
                case "working":
                case "running": return "\u25CF";
                case "waiting": return "\u25D0";
                case "quiet": return "\u25CC";
                case "failed": return "\u2715";
                case "idle": return "\u25CB";
                case "ended": return "\u2713";
                default: return "\u25CB"; } }
        public static string Phrase(string state) {
            switch (state) {
                case "working": return "working";
                case "running": return "running";
                case "waiting": return "waiting on you";
                case "quiet": return "quiet";
                case "idle": return "idle";
                case "failed": return "failed";
                case "ended": return "ended";
                default: return state ?? ""; } }
        public static string DisplayName(Thing t) {
            if (t.count > 1 && (t.name == null || t.name.IndexOf(" models on disk", StringComparison.Ordinal) < 0))
                return (t.name ?? "") + " \u00D7" + t.count.ToString(CultureInfo.InvariantCulture);
            return t.name ?? ""; }
        public static string BarPct(double v) {
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0.05) return "0%";
            if (v < 10) return v.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            return v.ToString("0", CultureInfo.InvariantCulture) + "%"; }
        public static string GbAmount(double gb) {
            if (double.IsNaN(gb) || double.IsInfinity(gb) || gb < 0) gb = 0;
            return gb >= 100 ? gb.ToString("0", CultureInfo.InvariantCulture) : gb.ToString("0.0", CultureInfo.InvariantCulture); }
        public static string DiskLabel() {
            try {
                string root = Path.GetPathRoot(Environment.SystemDirectory);
                if (!string.IsNullOrEmpty(root)) return "Disk " + root.TrimEnd('\\'); }
            catch (Exception) { }
            return "Disk"; }
        public class Meter {
            public string Detail = ""; public string Value = ""; public double Fraction; }
        static double Clamp01(double v) {
            if (double.IsNaN(v) || v < 0) return 0; if (v > 1) return 1; return v; }
        static double ClampPct(double v) {
            if (double.IsNaN(v) || v < 0) return 0; if (v > 100) return 100; return v; }
        public static List<Meter> Meters(Strip s) {
            if (s == null) s = new Strip(); var list = new List<Meter>(); var cpu = new Meter(); double cpuPct = ClampPct(s.cpu);
            cpu.Value = BarPct(cpuPct); cpu.Fraction = cpuPct / 100.0; list.Add(cpu); var mem = new Meter();
            if (s.ram_total_gb > 0) {
                double used = s.ram_used_gb < 0 ? 0 : s.ram_used_gb;
                mem.Detail = GbAmount(used) + " of " + GbAmount(s.ram_total_gb) + " GB";
                mem.Fraction = Clamp01(used / s.ram_total_gb); mem.Value = BarPct(mem.Fraction * 100); }
            else mem.Detail = "not available"; list.Add(mem); var gpu = new Meter();
            bool haveGpu = s.gpu.HasValue || s.gpu_temp.HasValue || s.watts.HasValue || !string.IsNullOrEmpty(s.gpu_name) || s.vram_used_gb.HasValue || s.vram_total_gb.HasValue;
            if (!haveGpu) gpu.Detail = "not available";
            else {
                var bits = new List<string>(); if (!string.IsNullOrEmpty(s.gpu_name)) bits.Add(s.gpu_name);
                else if (s.gpu.HasValue) bits.Add("GPU");
                if (s.gpu_temp.HasValue) bits.Add(((int)Math.Round(s.gpu_temp.Value)).ToString(CultureInfo.InvariantCulture) + "\u00B0C");
                if (s.watts.HasValue && s.watts.Value >= 0) bits.Add(s.watts.Value.ToString("0", CultureInfo.InvariantCulture) + " W");
                gpu.Detail = bits.Count == 0 ? "not available" : string.Join(" \u00B7 ", bits.ToArray());
                if (s.gpu.HasValue) {
                    double g = ClampPct(s.gpu.Value); gpu.Value = BarPct(g); gpu.Fraction = g / 100.0; } }
            list.Add(gpu); var vram = new Meter();
            if (s.vram_total_gb.HasValue && s.vram_total_gb.Value > 0) {
                double u = s.vram_used_gb.HasValue && s.vram_used_gb.Value > 0 ? s.vram_used_gb.Value : 0;
                double t = s.vram_total_gb.Value;
                vram.Detail = u.ToString("0.0", CultureInfo.InvariantCulture) + " of " + t.ToString("0", CultureInfo.InvariantCulture) + " GB";
                vram.Fraction = Clamp01(u / t); vram.Value = BarPct(vram.Fraction * 100); }
            else if (s.vram_used_gb.HasValue && s.vram_used_gb.Value >= 0)
                vram.Detail = s.vram_used_gb.Value.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            else vram.Detail = "not available"; list.Add(vram); var disk = new Meter();
            if (s.disk_total_gb > 0) {
                double free = s.disk_free_gb < 0 ? 0 : s.disk_free_gb; double usedD = s.disk_total_gb - free;
                if (usedD < 0) usedD = 0; disk.Detail = GbAmount(free) + " GB free of " + GbAmount(s.disk_total_gb);
                disk.Fraction = Clamp01(usedD / s.disk_total_gb); disk.Value = BarPct(disk.Fraction * 100); }
            else disk.Detail = "not available"; list.Add(disk); return list; }
        public static List<KeyValuePair<int, int[]>> Cores(Strip s) {
            var list = new List<KeyValuePair<int, int[]>>(); if (s == null || s.core_groups == null) return list;
            foreach (CoreGroup g in s.core_groups) {
                if (g == null || g.threads == null || g.threads.Count == 0) continue; int eff = g.kind == "performance" ? 1 : 0;
                list.Add(new KeyValuePair<int, int[]>(eff, g.threads.ToArray())); }
            return list; }
        public static string CoreSig(Strip s) {
            if (s == null || s.core_groups == null) return ""; var sb = new StringBuilder();
            foreach (CoreGroup g in s.core_groups) {
                sb.Append(g == null || g.kind == null ? "" : g.kind).Append(':');
                if (g != null && g.threads != null)
                    foreach (int t in g.threads) sb.Append(t.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(';'); }
            return sb.ToString(); }
        public static string BusiestLine(Strip s) {
            var cores = Cores(s); if (cores.Count == 0 || s == null || s.threads == null || s.threads.Count == 0) return "";
            int top = 0; for (int i = 0; i < cores.Count; i++) if (cores[i].Key > top) top = cores[i].Key; bool hybrid = false;
            for (int i = 0; i < cores.Count; i++) if (cores[i].Key != top) hybrid = true; int bestCpu = -1; double best = -1;
            int prevEff = -1, coreNo = 0; string bestName = "";
            for (int i = 0; i < cores.Count; i++) {
                var c = cores[i]; if (c.Key != prevEff) coreNo = 0; coreNo++; prevEff = c.Key;
                string kind = !hybrid ? "core " : c.Key == top ? "performance core " : "efficiency core ";
                for (int t = 0; t < c.Value.Length; t++) {
                    int cpu = c.Value[t]; double pct = cpu >= 0 && cpu < s.threads.Count ? s.threads[cpu] : 0;
                    if (pct > best) {
                        best = pct; bestCpu = cpu;
                        bestName = "Thread " + cpu.ToString(CultureInfo.InvariantCulture) + "  \u00B7  " + kind + coreNo.ToString(CultureInfo.InvariantCulture); } } }
            if (bestCpu < 0) return ""; return "Busiest thread: " + bestName + " at " + BarPct(best); }
        public static string Render(Board b) {
            if (b == null) b = new Board(); var sb = new StringBuilder();
            sb.Append("Desktop Vitals  ").Append(b.name ?? "").Append("  ");
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
            string[] labels = new[] { "Processor", "Memory", "Graphics", "Video memory", DiskLabel() };
            var meters = Meters(b.strip);
            for (int i = 0; i < meters.Count && i < labels.Length; i++) {
                sb.Append(labels[i]).Append("  ").Append(meters[i].Value ?? "");
                if (!string.IsNullOrEmpty(meters[i].Detail)) sb.Append("  \u00B7  ").Append(meters[i].Detail); sb.Append('\n'); }
            string busy = BusiestLine(b.strip); if (busy.Length > 0) sb.Append(busy).Append('\n'); sb.Append('\n');
            sb.Append(b.headline == null || b.headline.text == null ? "All good" : b.headline.text).Append('\n');
            if (b.groups != null)
                foreach (Group g in b.groups) {
                    sb.Append('\n'); sb.Append(g.title ?? "").Append('\n'); if (g.things == null) continue;
                    foreach (Thing t in g.things) {
                        sb.Append("  ").Append(Dot(t.state)).Append(' ').Append(DisplayName(t));
                        if (!string.IsNullOrEmpty(t.project)) sb.Append("  ").Append(t.project);
                        sb.Append("  ").Append(Phrase(t.state));
                        if (!string.IsNullOrEmpty(t.since)) sb.Append(" \u00B7 ").Append(t.since); sb.Append('\n');
                        if (!string.IsNullOrEmpty(t.detail)) sb.Append("    ").Append(t.detail).Append('\n');
                        if (!string.IsNullOrEmpty(t.why)) sb.Append("    ").Append(t.why).Append('\n'); } }
            return sb.ToString(); } }
    static partial class Engine {
        public static Board Current = Empty();
        public static bool TasksReady { get { return TaskList.Ready; } }
        public static bool ModelsReady { get { return ModelScan.Ready; } }
        public static bool ScansComplete { get { return TaskList.Ready && ModelScan.Ready; } }
        public static double LastPassMs; public static int LastProcs;
        public static void AcknowledgeImport(Board board) {
            if (!Prefs.ImportNote || board == null || board.headline == null || board.headline.text == null) return;
            if (board.headline.text.IndexOf("Imported hides and pins", StringComparison.Ordinal) < 0) return;
            Prefs.ImportNote = false; Prefs.Save(); }
        static Dictionary<int, Proc> prevProcs = new Dictionary<int, Proc>();
        static readonly Dictionary<string, string> cmds = new Dictionary<string, string>();
        static readonly Dictionary<string, string> cwds = new Dictionary<string, string>();
        static long prevWall, cpuIdle, cpuKernel, cpuUser; static bool haveWall, haveCpu, logged;
        static long[] threadBusy, threadTotal, prevThreadBusy, prevThreadTotal; static bool haveThreads;
        static List<CoreGroup> cachedGroups;
        public static void Reset() {
            prevProcs = new Dictionary<int, Proc>(); cmds.Clear(); cwds.Clear(); haveWall = false; haveCpu = false;
            haveThreads = false; prevWall = cpuIdle = cpuKernel = cpuUser = 0;
            threadBusy = threadTotal = prevThreadBusy = prevThreadTotal = null; LiveBoard.Clear(); }
        public static void Prime() {
            SampleCpu(); SampleThreads(null);
            try { SampleProcs(Procs.Snapshot()); } catch (Exception) { }
            try { TaskList.Refresh(null); } catch (Exception) { }
            try { GpuCounters.Refresh(); } catch (Exception) { }
            try { Vram.Refresh(); } catch (Exception) { } }
        public static Board Tick() {
            try {
                Current = Build(); logged = false; }
            catch (Exception ex) {
                if (!logged) { ErrorLog.Write(ex); logged = true; } }
            return Current; }
        static Board Empty() {
            var b = new Board(); b.name = Environment.MachineName; b.headline = new Headline(); b.headline.text = "All good";
            return b; }
        static string KeyOf(Proc p) { return p.Pid.ToString(CultureInfo.InvariantCulture) + ":" + p.CreateTime.ToString(CultureInfo.InvariantCulture); }
        static string CmdOf(Proc p) {
            if (!string.IsNullOrEmpty(p.Cmd)) return p.Cmd; string key = KeyOf(p); string cmd;
            if (cmds.TryGetValue(key, out cmd)) { p.Cmd = cmd; return cmd; }
            try { cmd = Procs.CommandLine(p.Pid) ?? ""; }
            catch (Exception) { cmd = ""; }
            if (cmds.Count > 4000) cmds.Clear(); cmds[key] = cmd; p.Cmd = cmd; return cmd; }
        static string CwdOf(Proc p) {
            string key = KeyOf(p); string dir; if (cwds.TryGetValue(key, out dir)) return dir;
            try { dir = Procs.CurrentDirectory(p.Pid); }
            catch (Exception) { dir = null; }
            if (cwds.Count > 4000) cwds.Clear(); cwds[key] = dir; return dir; }
        static double SampleCpu() {
            long idle, kernel, user; if (!Native.GetSystemTimes(out idle, out kernel, out user)) return 0; double pct = 0;
            if (haveCpu) {
                long dIdle = idle - cpuIdle; long dTotal = (kernel - cpuKernel) + (user - cpuUser);
                if (dTotal > 0) {
                    pct = (dTotal - dIdle) * 100.0 / dTotal; if (pct < 0) pct = 0; if (pct > 100) pct = 100; } }
            cpuIdle = idle; cpuKernel = kernel; cpuUser = user; haveCpu = true; return pct; }
        static void SampleThreads(List<double> into) {
            int n = Math.Max(1, Environment.ProcessorCount);
            if (threadBusy == null || threadBusy.Length != n) {
                threadBusy = new long[n]; threadTotal = new long[n]; prevThreadBusy = new long[n]; prevThreadTotal = new long[n];
                haveThreads = false; }
            if (into != null) {
                into.Clear(); for (int i = 0; i < n; i++) into.Add(0); }
            try {
                if (!Native.ThreadTimes(threadBusy, threadTotal)) return; }
            catch (Exception) { return; }
            if (haveThreads && into != null) {
                for (int i = 0; i < n; i++) {
                    long dt = threadTotal[i] - prevThreadTotal[i]; double p = 0;
                    if (dt > 0) {
                        p = (threadBusy[i] - prevThreadBusy[i]) * 100.0 / dt; if (p < 0) p = 0; if (p > 100) p = 100; }
                    into[i] = p; } }
            long[] swap = prevThreadBusy; prevThreadBusy = threadBusy; threadBusy = swap; swap = prevThreadTotal;
            prevThreadTotal = threadTotal; threadTotal = swap; haveThreads = true; }
        static List<CoreGroup> CoreGroups() {
            if (cachedGroups != null) return cachedGroups; List<KeyValuePair<int, int[]>> cores;
            try { cores = Native.CoreLayout(); }
            catch (Exception) {
                cores = new List<KeyValuePair<int, int[]>>(); int n = Math.Max(1, Environment.ProcessorCount);
                for (int i = 0; i < n; i++) cores.Add(new KeyValuePair<int, int[]>(0, new[] { i })); }
            int top = 0; for (int i = 0; i < cores.Count; i++) if (cores[i].Key > top) top = cores[i].Key; bool hybrid = false;
            for (int i = 0; i < cores.Count; i++) if (cores[i].Key != top) hybrid = true; cachedGroups = new List<CoreGroup>();
            for (int i = 0; i < cores.Count; i++) {
                var g = new CoreGroup(); if (!hybrid) g.kind = "core"; else if (cores[i].Key == top) g.kind = "performance";
                else g.kind = "efficiency"; g.threads = new List<int>(cores[i].Value); cachedGroups.Add(g); }
            return cachedGroups; }
        static void SampleProcs(List<Proc> list) {
            long wall = (long)Native.GetTickCount64(); double wallMs = haveWall ? wall - prevWall : 0;
            int ncpu = Math.Max(1, Environment.ProcessorCount);
            foreach (Proc p in list) {
                Proc old;
                if (wallMs > 0 && prevProcs.TryGetValue(p.Pid, out old) && old.CreateTime == p.CreateTime) {
                    long d = p.CpuTime - old.CpuTime; if (d < 0) d = 0; p.Cpu = (d / 10000.0) / wallMs * 100.0 / ncpu; } }
            var next = new Dictionary<int, Proc>(); foreach (Proc p in list) next[p.Pid] = p; prevProcs = next; prevWall = wall;
            haveWall = true; }
        static string FormatSpan(TimeSpan span) {
            int sec = (int)span.TotalSeconds; if (sec < 0) sec = 0;
            if (sec < 60) return sec.ToString(CultureInfo.InvariantCulture) + "s";
            int days = (int)span.TotalDays; if (days >= 1) return days.ToString(CultureInfo.InvariantCulture) + "d"; int mins = (int)span.TotalMinutes;
            if (mins < 60) return mins.ToString(CultureInfo.InvariantCulture) + "m"; int h = mins / 60; int m = mins % 60;
            if (m == 0) return h.ToString(CultureInfo.InvariantCulture) + "h";
            return h.ToString(CultureInfo.InvariantCulture) + "h " + m.ToString(CultureInfo.InvariantCulture) + "m"; }
        static string Age(long fileTime) {
            if (fileTime <= 0) return "";
            try {
                TimeSpan span = DateTime.Now - DateTime.FromFileTime(fileTime); return FormatSpan(span); }
            catch (Exception) { return ""; } }
        static string SinceWhen(DateTime t) {
            if (t.Year < 2000) return "";
            try { return FormatSpan(DateTime.Now - t); }
            catch (Exception) { return ""; } }
        static string Cap(string s) {
            if (string.IsNullOrEmpty(s)) return ""; return s.Length <= 2000 ? s : s.Substring(0, 2000); }
        static bool Hidden(string id, string name) {
            if (!string.IsNullOrEmpty(id) && Prefs.Hides.Contains(id)) return true;
            if (!string.IsNullOrEmpty(name) && Prefs.Hides.Contains(name)) return true; return false; }
        static void Finish(Thing t) {
            if (t.ports == null) t.ports = new List<int>(); string renamed;
            if (!string.IsNullOrEmpty(t.id) && Prefs.Rename.TryGetValue(t.id, out renamed) && !string.IsNullOrEmpty(renamed)) {
                if (!Hidden(t.id, renamed)) t.name = renamed; }
            if (!string.IsNullOrEmpty(t.id) && Prefs.PinIds.Contains(t.id)) t.pinned = true; t.name = Redact.Show(t.name);
            t.project = Redact.Show(t.project); t.branch = Redact.Show(t.branch); t.detail = Redact.Show(t.detail);
            t.why = Cap(Redact.Show(t.why)); t.since = t.since ?? ""; if (t.count < 1) t.count = 1; }
        static long VramOf(int pid) {
            long bytes; var map = Vram.ByPid; if (map != null && map.TryGetValue(pid, out bytes)) return bytes; return 0; }
        static Board Build() {
            var clock = Stopwatch.StartNew();
            try { return BuildBoard(); }
            finally { LastPassMs = clock.Elapsed.TotalMilliseconds; } }
        static Board BuildBoard() {
            var board = Empty(); string named = Prefs.Name;
            board.name = Redact.Show(string.IsNullOrEmpty(named) ? Environment.MachineName : named);
            board.strip.cpu = SampleCpu();
            try {
                var mem = new Native.MEMSTAT(); mem.len = (uint)Marshal.SizeOf(typeof(Native.MEMSTAT));
                if (Native.GlobalMemoryStatusEx(ref mem) && mem.total > 0) {
                    board.strip.ram_total_gb = mem.total / 1073741824.0;
                    board.strip.ram_used_gb = (mem.total - mem.avail) / 1073741824.0; } }
            catch (Exception) { }
            try {
                string root = System.IO.Path.GetPathRoot(Environment.SystemDirectory); var drive = new DriveInfo(root);
                board.strip.disk_free_gb = drive.AvailableFreeSpace / 1073741824.0;
                board.strip.disk_total_gb = drive.TotalSize / 1073741824.0; }
            catch (Exception) { }
            board.strip.threads = new List<double>(); SampleThreads(board.strip.threads);
            board.strip.core_groups = new List<CoreGroup>(CoreGroups()); double util = -1, usedGb = -1, totalGb = -1, watts = -1;
            int temp = -1; bool nv = false;
            try { nv = Nvml.Read(out util, out usedGb, out totalGb, out temp, out watts); }
            catch (Exception) { nv = false; util = usedGb = totalGb = watts = -1; temp = -1; }
            if (nv && util >= 0 && watts >= -1) board.strip.gpu = util; if (nv && temp >= 0) board.strip.gpu_temp = temp;
            if (nv && watts >= 0) board.strip.watts = watts;
            if (nv && !string.IsNullOrEmpty(Nvml.Name)) board.strip.gpu_name = Redact.Show(Nvml.Name);
            if (nv && totalGb >= 0) { board.strip.vram_used_gb = usedGb < 0 ? 0 : usedGb; board.strip.vram_total_gb = totalGb; }
            if (!board.strip.gpu.HasValue || !board.strip.vram_total_gb.HasValue || string.IsNullOrEmpty(board.strip.gpu_name)) {
                try { GpuCounters.Refresh(); } catch (Exception) { }
                if (GpuCounters.Have) {
                    if (!board.strip.gpu.HasValue && GpuCounters.UtilReady) board.strip.gpu = GpuCounters.Util;
                    if (string.IsNullOrEmpty(board.strip.gpu_name) && !string.IsNullOrEmpty(GpuCounters.Name))
                        board.strip.gpu_name = Redact.Show(GpuCounters.Name);
                    if (!board.strip.vram_total_gb.HasValue && (GpuCounters.TotalGb > 0 || GpuCounters.UsedGb > 0)) {
                        board.strip.vram_used_gb = GpuCounters.UsedGb;
                        if (GpuCounters.TotalGb > 0) board.strip.vram_total_gb = GpuCounters.TotalGb; } } }
            try { Vram.Refresh(); } catch (Exception) { }
            var procs = new List<Proc>();
            try { procs = Procs.Snapshot(); SampleProcs(procs); }
            catch (Exception) { procs = new List<Proc>(); }
            List<Group> order;
            try { order = LiveBoard.Make(procs); }
            catch (Exception) { order = new List<Group>(); }
            int waiting = 0, taskFailed = 0, exitFailed = 0, quiet = 0;
            foreach (Group g in order)
                foreach (Thing t in g.things) {
                    int n = Math.Max(1, t.count); if (t.state == "waiting" && t.kind == "agent") waiting += n;
                    else if (t.state == "failed" && t.kind == "task") taskFailed += n;
                    else if (t.state == "failed") exitFailed += n; else if (t.state == "quiet" && t.kind == "bot") quiet += n; }
            if (waiting == 0 && taskFailed == 0 && exitFailed == 0 && quiet == 0) {
                board.headline.text = "All good"; board.headline.needs_you = false; }
            else {
                var bits = new List<string>();
                if (waiting > 0) bits.Add(waiting.ToString(CultureInfo.InvariantCulture) + (waiting == 1 ? " agent waiting on you" : " agents waiting on you"));
                if (taskFailed > 0) bits.Add(taskFailed.ToString(CultureInfo.InvariantCulture) + (taskFailed == 1 ? " task failed" : " tasks failed"));
                if (exitFailed > 0) bits.Add(exitFailed.ToString(CultureInfo.InvariantCulture) + " failed");
                if (quiet > 0) bits.Add(quiet.ToString(CultureInfo.InvariantCulture) + (quiet == 1 ? " bot quiet" : " bots quiet"));
                board.headline.text = string.Join(" \u00B7 ", bits.ToArray()); board.headline.needs_you = true; }
            board.headline.text = Redact.Show(board.headline.text); board.groups = order; return board; } } }
