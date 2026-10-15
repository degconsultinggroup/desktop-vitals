// Facts 1-5: runs as you, which project, what it is doing, mapped weights, agent progress.
using System; using System.Collections.Generic; using System.Globalization; using System.IO; using System.Runtime.InteropServices;
using System.Security.Cryptography; using System.Text; using System.Text.RegularExpressions; namespace DesktopVitals {
    static partial class Engine {
        static class LiveBoard {
            const long ModelVram = 1L << 30; const int TailCap = 16 * 1024;
            static readonly Regex Shard = new Regex(@"-\d{5}-of-\d{5}(?=\.[A-Za-z0-9]+$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            static readonly Dictionary<int, Memo> memos = new Dictionary<int, Memo>();
            static readonly Dictionary<string, string> projects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            static readonly Dictionary<string, long> tails = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            static readonly List<ExitNote> exits = new List<ExitNote>(); static Dictionary<string, string> dos; class Memo {
                public long Create, LastIo, LastCpu, CpuBucket, GitStamp;
                public string Cmd = "", Cwd, Image, Script, Leaf = "", LaunchKind, LaunchName, TaskName, TaskPath, ProjectFull, Marker, Agent, Weight, AgentModel, Kind = "script", RecentWrite, LogFile, GitHead = "";
                public bool Yours, Orphan, Plain, TaskHit, Mapped; public int TaskGen = -1, CadenceSec;
                public DateTime LastActive, WriteChecked, LastWrite, TaskNext; public IntPtr Handle; public Act Action; public GitSnap Git; }
            class Act { public string Kind, File, Model; public DateTime When; public bool Open; }
            class GitSnap { public string Branch, Worktree, Main; public int Commits, Changed; }
            class ExitNote { public string Name, Project, Folder, Kind; public int Code; public DateTime When; public bool Failed; }
            public static void Clear() {
                foreach (var kv in memos) if (kv.Value.Handle != IntPtr.Zero) Native.CloseHandle(kv.Value.Handle); memos.Clear(); tails.Clear(); }
            public static List<Group> Make(List<Proc> procs) {
                LastProcs = procs == null ? 0 : procs.Count; var byPid = new Dictionary<int, Proc>();
                if (procs != null) foreach (Proc p in procs) byPid[p.Pid] = p; var listeners = Listeners(); var now = DateTime.UtcNow;
                if (procs != null)
                    foreach (Proc p in procs) {
                        Memo m = Touch(p, byPid); int gen = TaskList.Generation; if (m.TaskGen != gen) { m.TaskGen = gen; MatchTask(p, m); }
                        NoteActivity(m, p, now); }
                if (procs != null) CatchUp(procs); NoteExits(byPid, now); try { TaskList.Refresh(null); } catch (Exception) { }
                try { ModelScan.Refresh(); } catch (Exception) { } var used = new HashSet<int>(); var things = new List<Thing>(); if (procs != null) {
                    Agents(procs, byPid, listeners, things, used); foreach (Proc p in procs) {
                        if (used.Contains(p.Pid)) continue; Memo m = memo(p); if (!IsRoot(p, m, byPid)) continue;
                        Thing t = WorkThing(p, m, byPid, listeners, used); if (t == null) continue; things.Add(t); } }
                Tasks(things); Models(things); Ended(things); Pins(things); return Order(things); }
            static Memo Touch(Proc p, Dictionary<int, Proc> byPid) {
                Memo m; if (memos.TryGetValue(p.Pid, out m) && m.Create == p.CreateTime) return m;
                if (m != null) Forget(p.Pid, m, DateTime.UtcNow); m = new Memo(); m.Create = p.CreateTime;
                m.LastActive = DateTime.UtcNow; m.LastCpu = p.CpuTime; m.LastIo = p.Io; memos[p.Pid] = m; int who = Who(p); m.Yours = who == 1;
                if (!Infra(p.Exe) && (m.Yours || (who < 0 && Paths.Interpreter(p.Exe)))) {
                    try { m.Image = Procs.ImagePath(p.Pid); } catch (Exception) { } if (!StockImage(m) || Paths.Interpreter(p.Exe)) {
                        try { m.Cmd = Procs.CommandLine(p.Pid) ?? ""; } catch (Exception) { m.Cmd = ""; }
                        try { m.Cwd = Procs.CurrentDirectory(p.Pid); } catch (Exception) { }
                        p.Cmd = m.Cmd; m.Script = Paths.Find(p.Exe, m.Cmd, m.Cwd); if (who < 0 && ProfileScript(m.Script)) m.Yours = true; } }
                if (m.Yours) m.Agent = AgentOf(p.Exe, m.Cmd, p.Pid, p.CreateTime); if (m.Yours && m.Agent != null && string.IsNullOrEmpty(m.Cwd)) {
                    try { if (string.IsNullOrEmpty(m.Cmd)) m.Cmd = Procs.CommandLine(p.Pid) ?? ""; } catch (Exception) { }
                    try { m.Cwd = Procs.CurrentDirectory(p.Pid); } catch (Exception) { }
                    p.Cmd = m.Cmd; if (string.IsNullOrEmpty(m.Script)) m.Script = Paths.Find(p.Exe, m.Cmd, m.Cwd); }
                if (m.Yours) Launch(p, m, byPid); if (m.Yours && !Kept(m)) { m.Yours = false; m.Agent = null; } MapOnce(p, m);
                m.Plain = m.Yours && m.Agent == null && string.IsNullOrEmpty(m.Weight) && string.IsNullOrEmpty(m.Script) && !m.TaskHit && !m.Orphan;
                m.Leaf = LeafName(p, m); if (m.Yours && !m.Plain) Hold(m, p.Pid); return m; }
            static void MapOnce(Proc p, Memo m) {
                if (!m.Yours || m.Mapped || Infra(p.Exe)) return; if (WantMap(p, m)) {
                    m.Weight = Weights(p.Pid); if (string.IsNullOrEmpty(m.Weight)) m.Weight = WeightArg(m.Cmd);
                    if (!string.IsNullOrEmpty(m.Weight)) m.Plain = false; }
                m.Mapped = true; }
            static void CatchUp(List<Proc> procs) {
                foreach (Proc p in procs) { Memo m = memo(p); if (m != null && m.Yours && !m.Mapped) MapOnce(p, m); } }
            static void Hold(Memo m, int pid) {
                if (m.Handle == IntPtr.Zero) m.Handle = Native.OpenProcess(Native.QueryLimited, false, pid); }
            static void NoteActivity(Memo m, Proc p, DateTime now) {
                long dCpu = p.CpuTime - m.LastCpu; if (dCpu < 0) dCpu = 0; m.CpuBucket += dCpu;
                if (p.Io != m.LastIo || m.CpuBucket >= 5000000) { m.LastActive = now; m.CpuBucket = 0; }
                m.LastCpu = p.CpuTime; m.LastIo = p.Io; if (m.Yours && !string.IsNullOrEmpty(m.Script)) NoteWrite(m, p, now); }
            static void NoteWrite(Memo m, Proc p, DateTime now) {
                if ((now - m.WriteChecked).TotalSeconds < 0.35) return; m.WriteChecked = now; string dir = null; string file = ScriptFile(m.Script);
                try { if (!string.IsNullOrEmpty(file)) dir = Path.GetDirectoryName(file); } catch (Exception) { }
                if (string.IsNullOrEmpty(dir)) dir = m.Cwd; if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || Forbidden(dir)) return;
                DateTime born = p.CreateTime > 0 ? DateTime.FromFileTime(p.CreateTime) : DateTime.MinValue;
                DateTime best = DateTime.MinValue; string name = null; try {
                    int n = 0; foreach (string f in Directory.EnumerateFiles(dir)) {
                        if (++n > 40) break; DateTime w; try { w = File.GetLastWriteTime(f); } catch (Exception) { continue; }
                        if (w >= born && w > best && (DateTime.Now - w).TotalSeconds <= 90) { best = w; name = Path.GetFileName(f); } } }
                catch (Exception) { return; } if (best == DateTime.MinValue) return; if (m.LastWrite != DateTime.MinValue) {
                    double d = (best - m.LastWrite).TotalSeconds; if (d >= 0.3 && d < 180) m.CadenceSec = (int)Math.Max(1, Math.Round(d)); }
                m.LastWrite = best; if (!string.IsNullOrEmpty(name)) m.RecentWrite = name; }
            static void Launch(Proc p, Memo m, Dictionary<int, Proc> byPid) {
                Proc cur = p; var guard = new HashSet<int>(); for (int n = 0; n < 40 && guard.Add(cur.Pid); n++) {
                    Proc parent; if (!byPid.TryGetValue(cur.Parent, out parent)) { m.Orphan = true; break; }
                    if (parent.CreateTime == 0 || cur.CreateTime == 0 || parent.CreateTime >= cur.CreateTime) { m.Orphan = true; break; }
                    string role = Role(parent.Exe); string b = Base(parent.Exe);
                    if ((b == "svchost" || b == "taskeng" || b == "taskhostw") && m.LaunchKind == null) { m.LaunchKind = "task"; m.LaunchName = "Task Scheduler"; }
                    else if ((role == "terminal" || role == "editor") && m.LaunchKind == null) { m.LaunchKind = role; m.LaunchName = RoleLabel(parent.Exe); }
                    else if (role == "explorer" && m.LaunchKind == null) { m.LaunchKind = "explorer"; m.LaunchName = "Explorer"; }
                    else if (Container(parent.Exe) && m.LaunchKind == null) { m.LaunchKind = "terminal"; m.LaunchName = RoleLabel(parent.Exe); }
                    cur = parent; }
                AssignProject(p, m); }
            static string mineSid; static int mineSession = -2; static int Who(Proc p) {
                if (p == null) return 0; if (mineSid == null) {
                    try { mineSid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value ?? ""; } catch (Exception) { mineSid = ""; }
                    try { mineSession = System.Diagnostics.Process.GetCurrentProcess().SessionId; } catch (Exception) { mineSession = -1; } }
                // This process's session, including 0. Not the console session.
                if (mineSession >= 0 && p.Session != mineSession) return 0;
                if (mineSid.Length == 0) return -1; string sid = UserSid(p.Pid); if (sid == null) return -1;
                return string.Equals(sid, mineSid, StringComparison.OrdinalIgnoreCase) ? 1 : 0; }
            static bool ProfileScript(string script) {
                if (string.IsNullOrEmpty(script)) return false; if (script.StartsWith("module:", StringComparison.Ordinal)) {
                    int bar = script.IndexOf('|'); script = bar < 0 ? "" : script.Substring(bar + 1); }
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return !string.IsNullOrEmpty(script) && !string.IsNullOrEmpty(home) && AtOrUnder(script, home) && !Forbidden(script); }
            static string UserSid(int pid) {
                IntPtr h = Native.OpenProcess(Native.QueryLimited | Native.QueryInfo, false, pid);
                if (h == IntPtr.Zero) h = Native.OpenProcess(Native.QueryLimited, false, pid); if (h == IntPtr.Zero) return null; try {
                    IntPtr tok; if (!Native.OpenProcessToken(h, Native.TokenQuery, out tok)) return null; try {
                        int need = 0; Native.GetTokenInformation(tok, 1, IntPtr.Zero, 0, out need); if (need < 8) return null;
                        IntPtr buf = Marshal.AllocHGlobal(need); try {
                            if (!Native.GetTokenInformation(tok, 1, buf, need, out need)) return null; IntPtr psid = Marshal.ReadIntPtr(buf), str;
                            if (!Native.ConvertSidToStringSid(psid, out str) || str == IntPtr.Zero) return null;
                            try { return Marshal.PtrToStringUni(str); } finally { Native.LocalFree(str); } }
                        finally { Marshal.FreeHGlobal(buf); } }
                    finally { Native.CloseHandle(tok); } }
                finally { Native.CloseHandle(h); } }
            static bool Kept(Memo m) {
                if (!StockImage(m)) return true; if (!string.IsNullOrEmpty(m.Script)) return true; return !string.IsNullOrEmpty(m.ProjectFull); }
            static bool StockImage(Memo m) {
                string path = m == null ? null : m.Image; if (string.IsNullOrEmpty(path)) return false;
                if (path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) return true; return Forbidden(path); }
            static bool Forbidden(string path) {
                if (string.IsNullOrEmpty(path)) return true;
                if (path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0 || UnderWindows(path)) return true;
                if (AtOrUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))) return true;
                if (AtOrUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))) return true;
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local) && (AtOrUnder(path, Path.Combine(local, "Programs")) || AtOrUnder(path, Path.Combine(local, "Temp")))) return true;
                string temp = Path.GetTempPath(); return !string.IsNullOrEmpty(temp) && AtOrUnder(path, temp); }
            static bool WantMap(Proc p, Memo m) {
                if (SkipMap(p.Exe)) return false; if (!string.IsNullOrEmpty(WeightArg(m.Cmd))) return true; string b = Base(p.Exe);
                if (b.IndexOf("ollama", StringComparison.Ordinal) >= 0 || b.IndexOf("llama", StringComparison.Ordinal) >= 0) return true;
                string cmd = m.Cmd ?? "";
                if (cmd.IndexOf(".gguf", StringComparison.OrdinalIgnoreCase) >= 0 || cmd.IndexOf(".safetensors", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return NearWeights(m); }
            static bool NearWeights(Memo m) {
                string dir = null; string file = ScriptFile(m.Script);
                try { if (!string.IsNullOrEmpty(file)) dir = Path.GetDirectoryName(file); } catch (Exception) { }
                if (string.IsNullOrEmpty(dir)) dir = m.Cwd; if (string.IsNullOrEmpty(dir) || Forbidden(dir)) return false; try {
                    foreach (string sub in new[] { dir, Path.Combine(dir, "models") }) {
                        if (!Directory.Exists(sub)) continue; int n = 0; foreach (string f in Directory.EnumerateFiles(sub)) {
                            if (++n > 40) break; string e = Path.GetExtension(f).ToLowerInvariant();
                            if (e == ".gguf" || e == ".safetensors" || e == ".ggml") return true; } } }
                catch (Exception) { } return false; }
            static bool SameScript(Memo a, Memo b) {
                return a != null && b != null && !string.IsNullOrEmpty(a.Script) && string.Equals(a.Script, b.Script, StringComparison.OrdinalIgnoreCase); }
            static bool SameStem(Memo a, Memo b) {
                string fa = a == null ? null : ScriptFile(a.Script), fb = b == null ? null : ScriptFile(b.Script);
                if (string.IsNullOrEmpty(fa) || string.IsNullOrEmpty(fb)) return false; string xa, ya, da, db; try {
                    xa = Path.GetFileNameWithoutExtension(fa); ya = Path.GetFileNameWithoutExtension(fb);
                    da = Path.GetDirectoryName(fa); db = Path.GetDirectoryName(fb); }
                catch (Exception) { return false; }
                return xa != null && xa.Length >= 3 && string.Equals(xa, ya, StringComparison.OrdinalIgnoreCase) && string.Equals(da, db, StringComparison.OrdinalIgnoreCase); }
            static bool DifferentProject(Memo a, Memo b) {
                return a != null && b != null && !string.IsNullOrEmpty(a.ProjectFull) && !string.IsNullOrEmpty(b.ProjectFull) && !string.Equals(a.ProjectFull, b.ProjectFull, StringComparison.OrdinalIgnoreCase); }
            static bool Owned(Proc p, Memo m, Dictionary<int, Proc> byPid) {
                Proc cur = p; var guard = new HashSet<int>(); for (int n = 0; n < 40 && guard.Add(cur.Pid); n++) {
                    Proc parent; if (!byPid.TryGetValue(cur.Parent, out parent)) return false;
                    if (parent.CreateTime == 0 || cur.CreateTime == 0 || parent.CreateTime >= cur.CreateTime) return false;
                    Memo pm = memo(parent); if (pm == null || !pm.Yours) return false; if (SameScript(m, pm) || SameStem(m, pm)) return true;
                    // A different script stays its own line. A runner with no script is not its owner.
                    if (!string.IsNullOrEmpty(m.Script)) { cur = parent; continue; }
                    if (Container(parent.Exe)) { cur = parent; continue; } if (m.Agent != null || DifferentProject(m, pm)) return false;
                    if (!string.IsNullOrEmpty(pm.Script)) return false; return true; }
                return false; }
            static bool IsRoot(Proc p, Memo m, Dictionary<int, Proc> byPid) {
                if (p == null || m == null || !m.Yours || Infra(p.Exe)) return false;
                if (Container(p.Exe) && string.IsNullOrEmpty(m.Script)) return false; if (m.Agent != null) return false; return !Owned(p, m, byPid); }
            static void Collect(Proc p, Dictionary<int, Proc> byPid, List<Proc> members, HashSet<int> used) {
                if (!used.Add(p.Pid)) return; members.Add(p); foreach (var kv in byPid) {
                    Proc c = kv.Value; if (c.Parent != p.Pid || c.CreateTime <= p.CreateTime) continue;
                    Memo cm = memo(c); if (cm != null && (cm.Agent != null || IsRoot(c, cm, byPid))) continue; Collect(c, byPid, members, used); } }
            static bool Container(string exe) { return Shell(exe) || Infra(exe); }

            static bool MatchTask(Proc p, Memo m) {
                foreach (TaskInfo t in TaskList.Items) {
                    if (t == null) continue; bool hit = CmdHas(m.Cmd, t.Script);
                    if (!hit && Paths.ScriptFile(t.Exe ?? "")) hit = CmdHas(m.Cmd, t.Exe);
                    if (!hit && !Paths.Interpreter(t.Exe ?? "")) hit = SamePath(m.Image, t.Exe);
                    if (!hit && !string.IsNullOrEmpty(t.Script) && !string.IsNullOrEmpty(m.ProjectFull)) {
                        string taskMarker; string taskRoot = FindProject(t.Script, out taskMarker);
                        hit = !string.IsNullOrEmpty(taskRoot) && string.Equals(taskRoot, m.ProjectFull, StringComparison.OrdinalIgnoreCase); }
                    if (!hit) continue;
                    m.TaskHit = true; m.TaskName = t.Name; m.TaskPath = string.IsNullOrEmpty(t.Path) ? t.Name : t.Path; m.TaskNext = t.NextRun;
                    if (m.LaunchKind == null) { m.LaunchKind = "task"; m.LaunchName = "Task Scheduler"; }
                    if (string.IsNullOrEmpty(m.ProjectFull) && !string.IsNullOrEmpty(t.Script)) AssignProjectPath(m, t.Script); return true; }
                return false; }
            static void AssignProject(Proc p, Memo m) {
                string marker = null; string root = null; if (m.Agent == null) root = FindProject(ScriptFile(m.Script), out marker);
                if (root == null) root = FindProject(m.Cwd, out marker);
                if (root == null && m.Agent != null) root = FindProject(ScriptFile(m.Script), out marker);
                if (root == null) root = FindProject(m.Image, out marker); m.ProjectFull = root; m.Marker = root == null ? null : marker; }
            static void AssignProjectPath(Memo m, string path) {
                string marker; string root = FindProject(path, out marker); if (string.IsNullOrEmpty(root)) return;
                m.ProjectFull = root; m.Marker = marker; }
            static string FindProject(string start, out string marker) {
                marker = null; if (string.IsNullOrEmpty(start) || Forbidden(start)) return null; string dir = start;
                try { if (File.Exists(start) || !Directory.Exists(start)) dir = Path.GetDirectoryName(start); }
                catch (Exception) { return null; } if (string.IsNullOrEmpty(dir) || Forbidden(dir)) return null; string cached;
                if (projects.TryGetValue(dir, out cached)) {
                    if (cached.Length == 0) return null; int bar = cached.IndexOf('|');
                    marker = bar < 0 ? null : cached.Substring(bar + 1); return bar < 0 ? cached : cached.Substring(0, bar); }
                string walk = dir, found = null, foundMarker = null, weak = null, weakMarker = null;
                for (int n = 0; n < 40 && !string.IsNullOrEmpty(walk) && found == null; n++) {
                    if (Forbidden(walk)) break; try {
                        if (Directory.Exists(Path.Combine(walk, ".git")) || File.Exists(Path.Combine(walk, ".git"))) { found = walk; foundMarker = ".git"; }
                        else if (weak == null) {
                            foreach (string name in new[] { "package.json", "pyproject.toml", "Cargo.toml", "go.mod" })
                                if (File.Exists(Path.Combine(walk, name))) { weak = walk; weakMarker = name; break; }
                            if (weak == null && Directory.Exists(walk) && Directory.GetFiles(walk, "*.sln").Length > 0) { weak = walk; weakMarker = "*.sln"; } } }
                    catch (Exception) { } DirectoryInfo parent = null;
                    try { parent = Directory.GetParent(walk); } catch (Exception) { parent = null; }
                    if (parent == null) break; walk = parent.FullName; }
                if (found == null) { found = weak; foundMarker = weakMarker; }
                if (!string.IsNullOrEmpty(found) && foundMarker != ".git" && string.Equals(NameOf(found), "workspace", StringComparison.OrdinalIgnoreCase)) { found = null; foundMarker = null; }
                projects[dir] = found == null ? "" : found + "|" + (foundMarker ?? ""); marker = foundMarker; return found; }
            static void Agents(List<Proc> procs, Dictionary<int, Proc> byPid, Dictionary<int, List<int>> listeners, List<Thing> things, HashSet<int> used) {
                var groups = new Dictionary<string, Thing>(StringComparer.OrdinalIgnoreCase); foreach (Proc p in procs) {
                    Memo m = memo(p); if (m == null || m.Agent == null || !m.Yours || ChildOfAgent(p, procs)) continue;
                    used.Add(p.Pid); Act act = LogOf(m, p); AdoptCwd(m); GitSnap git = GitOf(m); string child = LeafChild(p.Pid, byPid);
                    string repo = git != null && !string.IsNullOrEmpty(git.Main) ? git.Main : m.ProjectFull;
                    string repoName = NameOf(repo); if (string.IsNullOrEmpty(repoName)) repoName = "no project";
                    string key = m.Agent + "\n" + (repo ?? ""); Thing t; if (!groups.TryGetValue(key, out t)) {
                        t = new Thing(); t.id = "agent:" + Slug(m.Agent) + ":" + Slug(repoName); t.name = m.Agent;
                        t.kind = "agent"; t.project = repoName; t.folder = repo ?? m.Cwd; t.since = Age(p.CreateTime);
                        if (git != null) t.branch = git.Branch; groups[key] = t; things.Add(t); }
                    t.count++; t.cpu += p.Cpu; t.ram_mb += p.Private / 1048576.0; t.vram_mb += VramOf(p.Pid) / 1048576.0;
                    AddPorts(p.Pid, byPid, listeners, t.ports, new HashSet<int>());
                    string state = AgentState(m, p, act, child); if (Rank(state) > Rank(t.state)) t.state = state;
                    string detail = AgentDetail(m, act, git, child); if (string.IsNullOrEmpty(t.detail) || state == "waiting") t.detail = detail;
                    if (string.IsNullOrEmpty(t.branch) && git != null) t.branch = git.Branch;
                    if (string.IsNullOrEmpty(t.why)) t.why = !string.IsNullOrEmpty(m.AgentModel) ? "model " + m.AgentModel : "agent";
                    if (t.since.Length == 0) t.since = Age(p.CreateTime); m.Kind = "agent"; m.Leaf = m.Agent; }
                for (int i = things.Count - 1; i >= 0; i--) {
                    if (things[i].kind != "agent") continue; if (things[i].count < 1) things[i].count = 1;
                    if (Hidden(things[i].id, things[i].name)) { things.RemoveAt(i); continue; } Finish(things[i]); } }
            static void AdoptCwd(Memo m) {
                if (m == null || string.IsNullOrEmpty(m.LogFile)) return; if (!string.IsNullOrEmpty(m.ProjectFull) && !Forbidden(m.Cwd)) return;
                string lcwd = JsonString(ReadText(m.LogFile, 800), "\"cwd\":\"", 0); if (string.IsNullOrEmpty(lcwd) || Forbidden(lcwd)) return;
                if (string.IsNullOrEmpty(m.Cwd) || Forbidden(m.Cwd)) m.Cwd = lcwd;
                if (string.IsNullOrEmpty(m.ProjectFull)) AssignProjectPath(m, lcwd); }
            static void AddPorts(int pid, Dictionary<int, Proc> byPid, Dictionary<int, List<int>> listeners, List<int> ports, HashSet<int> seen) {
                if (ports == null || !seen.Add(pid)) return; List<int> lp;
                if (listeners.TryGetValue(pid, out lp)) foreach (int port in lp) if (!ports.Contains(port)) ports.Add(port);
                foreach (var kv in byPid) {
                    Proc c = kv.Value; if (c.Parent != pid) continue; Memo cm = memo(c);
                    if (cm != null && cm.Agent != null) continue; AddPorts(c.Pid, byPid, listeners, ports, seen); } }
            static bool ChildOfAgent(Proc p, List<Proc> procs) {
                int id = p.Parent; var guard = new HashSet<int>();
                while (id != 0 && guard.Add(id)) {
                    Proc up = null; foreach (Proc q in procs) if (q.Pid == id) { up = q; break; }
                    if (up == null || up.CreateTime >= p.CreateTime) return false; Memo m = memo(up);
                    if (m != null && m.Agent != null) return true; id = up.Parent; }
                return false; }
            static string AgentState(Memo m, Proc p, Act act, string child) {
                if (act != null && act.Open && string.IsNullOrEmpty(child)) return "waiting"; if (!string.IsNullOrEmpty(child)) return "working";
                if (act != null && act.Kind == "answering" && string.IsNullOrEmpty(child)) return "waiting";
                double quiet = (DateTime.UtcNow - m.LastActive).TotalMinutes;
                if (AgeMin(p) >= 5 && quiet >= 5) return string.IsNullOrEmpty(m.ProjectFull) ? "idle" : "quiet"; return "working"; }
            static string AgentDetail(Memo m, Act act, GitSnap git, string child) {
                var bits = new List<string>(); if (act != null && !string.IsNullOrEmpty(act.Kind)) {
                    string bit = act.Kind; if (!string.IsNullOrEmpty(act.File)) bit += " " + act.File; if (act.When != DateTime.MinValue) {
                        string age = FormatSpan(DateTime.UtcNow - act.When.ToUniversalTime()); if (age.Length > 0) bit += " \u00B7 " + age; }
                    bits.Add(bit); }
                if (git != null && git.Commits > 0) bits.Add(git.Commits.ToString(CultureInfo.InvariantCulture) + (git.Commits == 1 ? " commit" : " commits"));
                if (git != null && git.Changed > 0) bits.Add(git.Changed.ToString(CultureInfo.InvariantCulture) + (git.Changed == 1 ? " file changed" : " files changed"));
                if (bits.Count == 0 && git != null && !string.IsNullOrEmpty(git.Branch)) bits.Add("branch " + git.Branch);
                if (!string.IsNullOrEmpty(child)) bits.Add("running " + child);
                else if (bits.Count == 0 && git != null && !string.IsNullOrEmpty(git.Worktree)) bits.Add(git.Worktree);
                return bits.Count == 0 ? null : string.Join(" \u00B7 ", bits.ToArray()); }
            static Thing WorkThing(Proc p, Memo m, Dictionary<int, Proc> byPid, Dictionary<int, List<int>> listeners, HashSet<int> used) {
                var members = new List<Proc>(); Collect(p, byPid, members, used); var ports = new List<int>(); long vram = 0; double cpu = 0, ram = 0;
                foreach (Proc c in members) {
                    cpu += c.Cpu; ram += c.Private / 1048576.0; vram += VramOf(c.Pid); List<int> lp;
                    if (listeners.TryGetValue(c.Pid, out lp)) foreach (int port in lp) if (!ports.Contains(port)) ports.Add(port); }
                vram /= 1048576; if (string.IsNullOrEmpty(m.Weight) && vram >= (ModelVram / 1048576)) m.Weight = m.AgentModel;
                string kind = "script"; if (!string.IsNullOrEmpty(m.Weight)) kind = "model";
                else if (ports.Count > 0) kind = "server";
                else if (m.Orphan || m.CadenceSec > 0 || (AgeMin(p) > 10 && !string.IsNullOrEmpty(m.RecentWrite)) || (m.TaskHit && AgeMin(p) > 10)) kind = "bot";
                else if (string.IsNullOrEmpty(m.Script)) kind = "app";
                bool quiet = ports.Count == 0 && m.CadenceSec == 0 && string.IsNullOrEmpty(m.RecentWrite) && AgeMin(p) >= 5 && (DateTime.UtcNow - m.LastActive).TotalMinutes >= 5;
                var t = new Thing(); t.kind = kind; t.state = quiet ? "quiet" : "running"; t.name = kind == "model" ? m.Weight : LeafName(p, m);
                t.id = IdOf(m, p, kind, t.name); t.project = ProjectLabel(m); t.folder = FolderOf(m); t.cpu = cpu; t.ram_mb = ram;
                t.vram_mb = vram; t.ports = ports; t.since = Age(p.CreateTime); t.count = 1; t.why = Why(m, ports); t.detail = DetailOf(m, kind, p.Exe);
                m.Kind = kind; m.Leaf = t.name; if (Hidden(t.id, t.name)) return null; Finish(t); return t; }
            static string DetailOf(Memo m, string kind, string exe) {
                if (kind == "model") return "loaded by " + (string.IsNullOrEmpty(exe) ? "process" : exe); var bits = new List<string>();
                if (m.CadenceSec > 0) bits.Add("works every " + m.CadenceSec.ToString(CultureInfo.InvariantCulture) + "s");
                else if (!string.IsNullOrEmpty(m.RecentWrite)) bits.Add("wrote " + m.RecentWrite);
                else if (m.Orphan && !m.TaskHit) bits.Add("works on its own"); if (m.TaskHit && !string.IsNullOrEmpty(m.TaskName)) {
                    string next = m.TaskNext.Year >= 2000 && m.TaskNext > DateTime.Now ? " \u00B7 next " + m.TaskNext.ToString("HH:mm", CultureInfo.InvariantCulture) : "";
                    bits.Add("task: " + m.TaskName + next); }
                return bits.Count == 0 ? null : string.Join(" \u00B7 ", bits.ToArray()); }

            static void Tasks(List<Thing> things) {
                foreach (TaskInfo task in TaskList.Items) {
                    if (task == null || TaskLive(task, things)) continue; var t = new Thing();
                    t.id = "task:" + (string.IsNullOrEmpty(task.Path) ? task.Name : task.Path); t.name = task.Name ?? "";
                    t.kind = "task"; t.folder = task.Script; string marker; string root = FindProject(task.Script ?? task.Exe, out marker);
                    t.project = root != null ? Path.GetFileName(root.TrimEnd('\\')) : null;
                    if (task.Running) t.state = "running"; else if (task.Failed) t.state = "failed";
                    else if (task.NeverRan) t.state = "idle"; else if (task.Result == 0) t.state = "ended"; else t.state = "idle";
                    t.since = SinceWhen(task.LastRun); t.why = task.Failed ? "last result was not success" : "scheduled task";
                    if (task.Failed && task.LastRun.Year >= 2000) {
                        string next = task.NextRun > DateTime.Now ? " \u00B7 next in " + FormatSpan(task.NextRun - DateTime.Now) : "";
                        t.detail = "failed " + task.LastRun.ToString("HH:mm", CultureInfo.InvariantCulture) + next; }
                    if (Hidden(t.id, t.name)) continue; Finish(t); things.Add(t); } }
            static bool TaskLive(TaskInfo task, List<Thing> things) {
                foreach (Thing t in things) {
                    if (t == null || t.kind == "task" || t.kind == "agent" || t.kind == "model") continue;
                    if (!string.IsNullOrEmpty(task.Name) && t.detail != null && t.detail.IndexOf(task.Name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    if (!string.IsNullOrEmpty(task.Script) && t.id != null && t.id.IndexOf(task.Script, StringComparison.OrdinalIgnoreCase) >= 0) return true; }
                return false; }
            static void Models(List<Thing> things) {
                var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Thing t in things) if (t.kind == "model" && t.state == "running" && !string.IsNullOrEmpty(t.name)) loaded.Add(t.name);
                var rest = new List<InstalledModel>(); var items = ModelScan.Items; if (items != null)
                    foreach (InstalledModel m in items)
                        if (m != null && !string.IsNullOrEmpty(m.Name) && !loaded.Contains(m.Name)) rest.Add(m);
                if (rest.Count == 0) return; var line = new Thing(); line.kind = "model"; line.state = "idle"; line.count = 1; if (rest.Count == 1) {
                    line.id = "model:" + Slug(rest[0].Name); line.name = rest[0].Name; line.folder = rest[0].Path;
                    line.why = rest[0].Name; if (rest[0].Bytes > 0) line.detail = SizeText(rest[0].Bytes); }
                else {
                    line.id = "models:rest";
                    line.name = (loaded.Count > 0 ? rest.Count.ToString(CultureInfo.InvariantCulture) + " more on disk" : rest.Count.ToString(CultureInfo.InvariantCulture) + " models on disk");
                    long bytes = 0; var names = new StringBuilder(); foreach (InstalledModel m in rest) {
                        bytes += m.Bytes; if (names.Length > 0) names.Append(" \u00B7 "); names.Append(m.Name); if (names.Length > 2000) break; }
                    line.detail = SizeText(bytes); line.why = names.ToString(); }
                if (Hidden(line.id, line.name)) return; Finish(line); things.Add(line); }
            static void Ended(List<Thing> things) {
                var now = DateTime.UtcNow; for (int i = exits.Count - 1; i >= 0; i--) if ((now - exits[i].When).TotalHours > 1) exits.RemoveAt(i);
                foreach (ExitNote e in exits) {
                    var t = new Thing(); t.id = "ended:" + Slug(e.Name) + ":" + Slug(e.Project ?? ""); t.name = e.Name ?? "";
                    t.kind = string.IsNullOrEmpty(e.Kind) ? "script" : e.Kind; t.state = e.Failed ? "failed" : "ended";
                    t.project = e.Project; t.folder = e.Folder; t.since = FormatSpan(now - e.When);
                    string code = e.Code < 0 ? "exit" : "exit " + e.Code.ToString(CultureInfo.InvariantCulture);
                    t.detail = code + " \u00B7 " + t.since + " ago"; t.why = e.Failed ? "exit was not success" : "exit 0";
                    if (Hidden(t.id, t.name)) continue; Finish(t); things.Add(t); } }
            static void Pins(List<Thing> things) {
                foreach (Thing t in things) if (!string.IsNullOrEmpty(t.id) && Prefs.PinIds.Contains(t.id)) t.pinned = true;
                foreach (PinRule rule in Prefs.PinRules) {
                    if (rule == null) continue; foreach (Thing t in things)
                        if (string.Equals(t.id, rule.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(t.name, rule.Label, StringComparison.OrdinalIgnoreCase))
                            t.pinned = true; } }
            static List<Group> Order(List<Thing> things) {
                var agents = new List<Thing>(); var models = new List<Thing>(); var other = new List<Thing>();
                var ended = new List<Thing>(); var by = new Dictionary<string, List<Thing>>(StringComparer.OrdinalIgnoreCase);
                var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (Thing t in things) {
                    if (t.kind == "agent") agents.Add(t); else if (t.kind == "model") models.Add(t);
                    else if (t.id != null && t.id.StartsWith("ended:", StringComparison.Ordinal)) ended.Add(t);
                    else if ((t.kind == "app" || t.kind == "task") && string.IsNullOrEmpty(t.project)) other.Add(t);
                    else {
                        string title = GroupTitle(t); string key = title.ToLowerInvariant(); List<Thing> list;
                        if (!by.TryGetValue(key, out list)) { by[key] = list = new List<Thing>(); titles[key] = title; } list.Add(t); } }
                agents.Sort(delegate(Thing a, Thing b) { int c = Rank(b.state) - Rank(a.state); return c != 0 ? c : string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase); });
                var order = new List<Group>(); AddGroup(order, "AGENTS", "agents", agents); var keys = new List<string>(by.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase); foreach (string key in keys) {
                    by[key].Sort(delegate(Thing a, Thing b) { return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase); });
                    AddGroup(order, titles[key], "project", by[key]); }
                AddGroup(order, "MODELS", "models", models); AddGroup(order, "OTHER", "other", other);
                AddGroup(order, "ENDED IN THE LAST HOUR", "ended", ended); return order; }
            static void AddGroup(List<Group> order, string title, string kind, List<Thing> things) {
                if (things == null || things.Count == 0) return; var g = new Group(); g.title = Redact.Show(title ?? "");
                g.kind = kind; g.things = things; order.Add(g); }
            static string GroupTitle(Thing t) {
                if (!string.IsNullOrEmpty(t.project) && t.project != "task" && !BadName(t.project)) return t.project;
                if (!string.IsNullOrEmpty(t.folder) && !Forbidden(t.folder)) {
                    try {
                        string dir = Directory.Exists(t.folder) ? t.folder : Path.GetDirectoryName(t.folder);
                        if (!string.IsNullOrEmpty(dir) && !Forbidden(dir)) {
                            string name = Path.GetFileName(dir.TrimEnd('\\')); if (!string.IsNullOrEmpty(name) && !BadName(name)) return name; } }
                    catch (Exception) { } }
                return "Other"; }
            static bool BadName(string name) {
                return name.Equals("Windows", StringComparison.OrdinalIgnoreCase) || name.Equals("system32", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("SysWOW64", StringComparison.OrdinalIgnoreCase) || name.Equals("Scripts", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("workspace", StringComparison.OrdinalIgnoreCase); }
            static int Rank(string state) {
                if (state == "waiting") return 5; if (state == "failed") return 4;
                if (state == "working" || state == "running") return 3; if (state == "quiet") return 2; if (state == "idle") return 1; return 0; }
            static void NoteExits(Dictionary<int, Proc> live, DateTime now) {
                var gone = new List<int>(); foreach (var kv in memos) {
                    Proc p; if (live.TryGetValue(kv.Key, out p) && p.CreateTime == kv.Value.Create) continue; gone.Add(kv.Key); }
                foreach (int pid in gone) { Memo m; if (memos.TryGetValue(pid, out m)) Forget(pid, m, now); } }
            static void Forget(int pid, Memo m, DateTime now) {
                if (m.Yours && !m.Plain && !string.IsNullOrEmpty(m.Leaf)) {
                    int code = -1; uint c;
                    if (m.Handle != IntPtr.Zero && Native.GetExitCodeProcess(m.Handle, out c) && c != 259) code = unchecked((int)c);
                    exits.Add(new ExitNote { Name = m.Leaf, Project = NameOf(m.ProjectFull), Folder = FolderOf(m), Kind = m.Kind, Code = code, Failed = code > 0, When = now }); }
                if (m.Handle != IntPtr.Zero) Native.CloseHandle(m.Handle); memos.Remove(pid); }
            static Memo memo(Proc p) {
                Memo m; if (p == null || !memos.TryGetValue(p.Pid, out m) || m.Create != p.CreateTime) return null; return m; }
            static string IdOf(Memo m, Proc p, string kind, string name) {
                if (kind == "model") return "model:" + Slug(m.Weight); if (!string.IsNullOrEmpty(m.Script)) return kind + ":" + m.Script;
                return kind + ":" + p.Pid.ToString(CultureInfo.InvariantCulture); }
            static string ProjectLabel(Memo m) {
                string repo = NameOf(m.ProjectFull); if (!string.IsNullOrEmpty(repo)) return repo; return null; }
            static string FolderOf(Memo m) {
                if (!string.IsNullOrEmpty(m.ProjectFull)) return m.ProjectFull;
                if (!string.IsNullOrEmpty(m.Script)) return ScriptFile(m.Script) ?? m.Cwd; return m.Cwd; }
            static string Why(Memo m, List<int> ports) {
                var bits = new List<string>(); if (!string.IsNullOrEmpty(m.LaunchName)) bits.Add("launcher=" + m.LaunchName);
                else if (m.Orphan) bits.Add("parent exited"); if (!string.IsNullOrEmpty(m.ProjectFull)) bits.Add("project=" + (m.Marker ?? "marker"));
                if (!string.IsNullOrEmpty(m.Weight)) bits.Add("weights " + m.Weight);
                if (ports != null && ports.Count > 0) bits.Add("listens " + ports[0].ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(m.RecentWrite)) bits.Add("wrote " + m.RecentWrite);
                return bits.Count == 0 ? "yours" : string.Join("; ", bits.ToArray()); }
            static string LeafName(Proc p, Memo m) {
                string script = ScriptName(m.Script); return !string.IsNullOrEmpty(script) ? script : PrettyExe(p.Exe); }
            static string ScriptName(string script) {
                if (string.IsNullOrEmpty(script)) return null; if (script.StartsWith("module:", StringComparison.Ordinal)) {
                    string rest = script.Substring(7); int bar = rest.IndexOf('|'); return bar < 0 ? rest : rest.Substring(0, bar); }
                try { return Path.GetFileName(script); } catch (Exception) { return script; } }
            static string ScriptFile(string script) {
                if (string.IsNullOrEmpty(script) || script.StartsWith("module:", StringComparison.Ordinal)) return null; return script; }
            static string AgentOf(string exe, string cmd, int pid, long create) {
                string model = Arg(cmd, "--model"); if (IsWeightPath(model)) model = null;
                if (!string.IsNullOrEmpty(model) && Has(cmd, "--worktree")) return "Cursor agent";
                string blob = ((exe ?? "") + "\n" + (cmd ?? "")).ToLowerInvariant();
                if (blob.IndexOf("claude-code", StringComparison.Ordinal) >= 0 || blob.IndexOf("\\claude\\", StringComparison.Ordinal) >= 0 || blob.IndexOf("/claude/", StringComparison.Ordinal) >= 0) return "Claude Code";
                if (blob.IndexOf("codex-cli", StringComparison.Ordinal) >= 0 || blob.IndexOf("\\codex\\", StringComparison.Ordinal) >= 0 || blob.IndexOf("\\codex.exe", StringComparison.Ordinal) >= 0) return "Codex";
                if (blob.IndexOf("cursor-agent", StringComparison.Ordinal) >= 0) return "Cursor agent"; var args = Paths.Split(cmd);
                for (int i = 1; i < args.Count; i++) {
                    string a = args[i]; string named = null;
                    if (a.StartsWith("--agent=", StringComparison.OrdinalIgnoreCase)) named = PrettyAgent(a.Substring(8));
                    else if (a.Equals("--agent", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) named = PrettyAgent(args[++i]);
                    else named = PrettyAgent(Path.GetFileNameWithoutExtension(a.Trim('"'))); if (named != null) return named; }
                string fromExe = PrettyAgent(Path.GetFileNameWithoutExtension(exe ?? "")); if (fromExe != null) return fromExe;
                string reg = Path.Combine(AgentHome(), ".claude", "sessions", pid.ToString(CultureInfo.InvariantCulture) + ".json");
                if (File.Exists(reg)) {
                    long ps; string procStart = JsonString(ReadText(reg, 800), "\"procStart\":\"", 0);
                    if (string.IsNullOrEmpty(procStart) || !long.TryParse(procStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out ps)) return "Claude Code";
                    long d = ps - create; if (d < 0) d = -d; if (d <= 20000000) return "Claude Code"; }
                if (!string.IsNullOrEmpty(model)) return Path.GetFileNameWithoutExtension(exe ?? "node"); return null; }
            static string PrettyAgent(string token) {
                if (string.IsNullOrEmpty(token)) return null;
                if (token.Equals("claude", StringComparison.OrdinalIgnoreCase) || token.Equals("claude-code", StringComparison.OrdinalIgnoreCase)) return "Claude Code";
                if (token.Equals("codex", StringComparison.OrdinalIgnoreCase)) return "Codex";
                if (token.Equals("cursor-agent", StringComparison.OrdinalIgnoreCase)) return "Cursor agent"; return null; }
            static bool IsWeightPath(string path) {
                if (string.IsNullOrEmpty(path)) return false; string ext = "";
                try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch (Exception) { return false; }
                return ext == ".gguf" || ext == ".safetensors" || ext == ".bin" || ext == ".pt" || ext == ".pth"; }
            static string WeightArg(string cmd) {
                var args = Paths.Split(cmd); for (int i = 1; i < args.Count; i++) {
                    string a = args[i]; string path = null;
                    if (a.StartsWith("--model=", StringComparison.OrdinalIgnoreCase)) path = a.Substring(8).Trim('"');
                    else if ((a.Equals("--model", StringComparison.OrdinalIgnoreCase) || a.Equals("-m", StringComparison.OrdinalIgnoreCase) || a.Equals("--model-path", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Count)
                        path = args[++i].Trim('"');
                    if (!IsWeightPath(path)) continue; return Path.GetFileNameWithoutExtension(Shard.Replace(Path.GetFileName(path), "")); }
                return null; }
            static Act LogOf(Memo m, Proc p) {
                try {
                    string home = AgentHome(); if (m.Agent == "Claude Code") Claude(home, m, p);
                    else if (m.Agent == "Codex") Codex(home, m, p); else if (m.Agent == "Cursor agent") Cursor(home, m, p);
                    if (m.Action != null && m.AgentModel == null && m.Action.Model != null) m.AgentModel = m.Action.Model;
                    if (m.AgentModel == null) m.AgentModel = Arg(m.Cmd, "--model"); if (IsWeightPath(m.AgentModel)) m.AgentModel = null; }
                catch (Exception) { } return m.Action; }
            static string AgentHome() {
                string h = Environment.GetEnvironmentVariable("DESKTOPVITALS_HOME");
                return string.IsNullOrEmpty(h) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : h; }
            static void Claude(string home, Memo m, Proc p) {
                if (m.LogFile == null) {
                    string file = null;
                    string reg = Path.Combine(home, ".claude", "sessions", p.Pid.ToString(CultureInfo.InvariantCulture) + ".json");
                    string text = ReadText(reg, 8000); if (!string.IsNullOrEmpty(text)) {
                        long ps; string procStart = JsonString(text, "\"procStart\":\"", 0);
                        if (!string.IsNullOrEmpty(procStart) && long.TryParse(procStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out ps)) {
                            long d = ps - p.CreateTime; if (d < 0) d = -d; if (d > 20000000) text = null; }
                        if (text != null) {
                            string sid = JsonString(text, "\"sessionId\":\"", 0); string cwd = JsonString(text, "\"cwd\":\"", 0);
                            if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(cwd)) file = Path.Combine(home, ".claude", "projects", Encode(cwd), sid + ".jsonl"); } }
                    if (string.IsNullOrEmpty(file) || !File.Exists(file)) file = ClaudeByCwd(home, m.Cwd, p.CreateTime); m.LogFile = file ?? ""; }
                Apply(m, m.LogFile, true); }
            static string ClaudeByCwd(string home, string cwd, long create) {
                if (string.IsNullOrEmpty(cwd)) return null; string dir = Path.Combine(home, ".claude", "projects", Encode(cwd));
                if (!Directory.Exists(dir)) return null; string best = null; DateTime bestTime = DateTime.MinValue; string[] files;
                try { files = Directory.GetFiles(dir, "*.jsonl"); } catch (Exception) { return null; } foreach (string f in files) {
                    string got = JsonString(ReadText(f, 1500), "\"cwd\":\"", 0);
                    if (!string.IsNullOrEmpty(got) && !string.Equals(got, cwd, StringComparison.OrdinalIgnoreCase)) continue; DateTime w;
                    try { w = File.GetLastWriteTimeUtc(f); } catch (Exception) { continue; }
                    if (w < DateTime.FromFileTimeUtc(create).AddMinutes(-1) || w < bestTime) continue; bestTime = w; best = f; }
                return best; }
            static void Codex(string home, Memo m, Proc p) {
                if (string.IsNullOrEmpty(m.Cwd)) return;
                if (m.LogFile == null) m.LogFile = CodexFile(home, m.Cwd, p.CreateTime) ?? ""; Apply(m, m.LogFile, false); }
            static string CodexFile(string home, string cwd, long create) {
                string root = Path.Combine(home, ".codex", "sessions"); if (!Directory.Exists(root) || string.IsNullOrEmpty(cwd)) return null; string[] files;
                try { files = Directory.GetFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories); } catch (Exception) { return null; }
                string best = null; long bestSkew = long.MaxValue; DateTime start = DateTime.FromFileTimeUtc(create); int n = 0;
                foreach (string path in files) {
                    if (++n > 200) break; string fcwd = JsonString(ReadText(path, 800), "\"cwd\":\"", 0);
                    if (!SamePlace(fcwd, cwd)) continue; DateTime born = CodexBorn(path);
                    long skew = born == DateTime.MinValue ? 6 * 3600 : (long)Math.Abs((born - start).TotalSeconds);
                    if (skew > 12 * 3600) continue; if (skew < bestSkew) { bestSkew = skew; best = path; } }
                return best; }
            static bool SamePlace(string a, string b) {
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
                try { a = Path.GetFullPath(a).TrimEnd('\\'); b = Path.GetFullPath(b).TrimEnd('\\'); } catch (Exception) { return false; }
                return a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase); }
            static DateTime CodexBorn(string path) {
                string name = Path.GetFileName(path);
                if (name == null || name.Length < 27 || !name.StartsWith("rollout-", StringComparison.Ordinal)) return DateTime.MinValue; DateTime dt;
                if (DateTime.TryParseExact(name.Substring(8, 19), "yyyy-MM-ddTHH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
                    return dt.ToUniversalTime();
                return DateTime.MinValue; }
            static void Cursor(string home, Memo m, Proc p) {
                if (string.IsNullOrEmpty(m.Cwd)) return; if (m.LogFile == null) m.LogFile = CursorDb(home, m.Cwd, p.CreateTime) ?? "";
                if (m.LogFile.Length == 0 || !File.Exists(m.LogFile)) return; Act act = CursorAct(m.LogFile); if (act != null) m.Action = act; }
            static string CursorDb(string home, string cwd, long create) {
                string dir = Path.Combine(home, ".cursor", "chats", Md5Hex(cwd)); if (!Directory.Exists(dir)) return null; string[] chats;
                try { chats = Directory.GetDirectories(dir); } catch (Exception) { return null; } string best = null; long bestSkew = long.MaxValue;
                long startMs = (long)(DateTime.FromFileTimeUtc(create) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                foreach (string chat in chats) {
                    string meta = ReadText(Path.Combine(chat, "meta.json"), 4000);
                    if (string.IsNullOrEmpty(meta) || !string.Equals(JsonString(meta, "\"cwd\":\"", 0), cwd, StringComparison.OrdinalIgnoreCase)) continue;
                    long cms = JsonLong(meta, "\"createdAtMs\":"); if (cms == 0) continue; long skew = cms - startMs; if (skew < 0) skew = -skew;
                    if (skew < bestSkew) { bestSkew = skew; best = Path.Combine(chat, "store.db"); } }
                return best; }
            static void Apply(Memo m, string path, bool claude) {
                string text = Tail(path); if (string.IsNullOrEmpty(text)) return; foreach (string line in text.Split('\n')) {
                    Act hit = claude ? ParseClaude(line) : ParseCodex(line); if (hit == null) continue;
                    if (hit.Model != null && m.AgentModel == null) m.AgentModel = hit.Model; if (hit.Kind != null) m.Action = hit; } }
            static string Tail(string path) {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null; long offset; if (!tails.TryGetValue(path, out offset)) offset = 0;
                try {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                        long end = fs.Length; long start = offset; if (end < start) start = 0;
                        if (end - start > TailCap) start = end - TailCap; if (start < 0) start = 0;
                        if (end <= start) { tails[path] = end; return null; }
                        fs.Seek(start, SeekOrigin.Begin); int n = (int)(end - start); byte[] buf = new byte[n];
                        int got = fs.Read(buf, 0, n); tails[path] = end; string text = Encoding.UTF8.GetString(buf, 0, got); if (start > offset) {
                            int nl = text.IndexOf('\n'); if (nl >= 0) text = text.Substring(nl + 1); }
                        return text; } }
                catch (Exception) { return null; } }
            static Act ParseClaude(string line) {
                if (string.IsNullOrEmpty(line)) return null; var hit = new Act(); hit.When = Stamp(JsonString(line, "\"timestamp\":\"", 0));
                hit.Model = JsonString(line, "\"requestedModel\":\"", 0);
                bool tool = line.IndexOf("\"type\":\"tool_use\"", StringComparison.Ordinal) >= 0;
                if (line.IndexOf("\"type\":\"tool_result\"", StringComparison.Ordinal) >= 0 && !tool) { hit.Kind = "tool"; return hit; } if (tool) {
                    string name = JsonString(line, "\"name\":\"", Math.Max(0, line.IndexOf("\"type\":\"tool_use\"", StringComparison.Ordinal)));
                    hit.Kind = KindFor(name); hit.File = FileFrom(line); hit.Open = true; return hit; }
                if (line.IndexOf("\"type\":\"thinking\"", StringComparison.Ordinal) >= 0 && line.IndexOf("\"type\":\"text\"", StringComparison.Ordinal) < 0) { hit.Kind = "thinking"; return hit; }
                if (line.IndexOf("\"type\":\"text\"", StringComparison.Ordinal) >= 0 && line.IndexOf("\"stop_reason\":\"end_turn\"", StringComparison.Ordinal) >= 0) { hit.Kind = "answering"; return hit; }
                return hit.Model == null ? null : hit; }
            static Act ParseCodex(string line) {
                if (string.IsNullOrEmpty(line)) return null; var hit = new Act(); hit.When = Stamp(JsonString(line, "\"timestamp\":\"", 0));
                if (line.IndexOf("\"type\":\"turn_context\"", StringComparison.Ordinal) >= 0) hit.Model = JsonString(line, "\"model\":\"", 0);
                if (line.IndexOf("\"type\":\"FileChange\"", StringComparison.Ordinal) >= 0) {
                    hit.Kind = "editing"; int at = line.IndexOf("\"changes\":{", StringComparison.Ordinal);
                    if (at >= 0) hit.File = BaseName(JsonString(line, "\"", at + "\"changes\":{".Length)); return hit; }
                if (line.IndexOf("\"type\":\"CommandExecution\"", StringComparison.Ordinal) >= 0) { hit.Kind = "shell"; return hit; }
                if (line.IndexOf("\"type\":\"Reasoning\"", StringComparison.Ordinal) >= 0) { hit.Kind = "thinking"; return hit; }
                if (line.IndexOf("\"type\":\"AgentMessage\"", StringComparison.Ordinal) >= 0) { hit.Kind = "answering"; return hit; }
                return hit.Model == null ? null : hit; }
            static Act CursorAct(string db) {
                IntPtr conn;
                try { if (Native.sqlite3_open_v2("file:" + db.Replace('\\', '/') + "?mode=ro", out conn, Native.SqliteOpenReadonly | Native.SqliteOpenUri, IntPtr.Zero) != 0) return null; }
                catch (Exception) { return null; } try {
                    Native.sqlite3_busy_timeout(conn, 0); byte[] meta = SqlBlob(conn, "SELECT value FROM meta WHERE key='0'", null);
                    string root = meta == null ? null : HexJson(meta, "latestRootBlobId");
                    byte[] blob = string.IsNullOrEmpty(root) ? null : SqlBlob(conn, "SELECT data FROM blobs WHERE id=?", root);
                    if (blob == null) return null; var ids = new List<string>(); for (int i = 0; i + 34 <= blob.Length; i++) {
                        if (blob[i] != 0x0a || blob[i + 1] != 0x20) continue; ids.Add(Hex(blob, i + 2, 32)); i += 33; }
                    for (int n = ids.Count - 1; n >= 0 && n >= ids.Count - 12; n--) {
                        byte[] kind = SqlBlob(conn, "SELECT substr(data,1,1) FROM blobs WHERE id=?", ids[n]);
                        if (kind == null || kind.Length == 0 || kind[0] != (byte)'{') continue;
                        byte[] body = SqlBlob(conn, "SELECT substr(data,1,4096) FROM blobs WHERE id=?", ids[n]);
                        if (body == null || body.Length == 0) continue; string head = Encoding.UTF8.GetString(body); string end = "";
                        if (head.IndexOf("\"path\":\"", StringComparison.Ordinal) < 0) {
                            byte[] tail = SqlBlob(conn, "SELECT substr(data, max(1, length(data)-1023), 1024) FROM blobs WHERE id=?", ids[n]);
                            if (tail != null) end = Encoding.UTF8.GetString(tail); }
                        Act hit = ParseCursor(head, end); if (hit != null && hit.Kind != null) {
                            try { hit.When = File.GetLastWriteTimeUtc(db); } catch (Exception) { hit.When = DateTime.UtcNow; } return hit; } } }
                catch (Exception) { }
                finally { try { Native.sqlite3_close(conn); } catch (Exception) { } } return null; }
            static Act ParseCursor(string head, string end) {
                var hit = new Act(); bool call = head.IndexOf("\"type\":\"tool-call\"", StringComparison.Ordinal) >= 0;
                bool result = head.IndexOf("\"type\":\"tool-result\"", StringComparison.Ordinal) >= 0; if (call || result) {
                    hit.Kind = KindFor(JsonString(head, "\"toolName\":\"", 0)); hit.File = FileFrom(head + end);
                    hit.Open = call && !result; return hit; }
                if (head.IndexOf("\"type\":\"reasoning\"", StringComparison.Ordinal) >= 0) { hit.Kind = "thinking"; return hit; }
                if (head.IndexOf("\"type\":\"text\"", StringComparison.Ordinal) >= 0 && JsonString(head, "\"role\":\"", 0) == "assistant") { hit.Kind = "answering"; return hit; }
                return null; }
            static string KindFor(string name) {
                if (name == "Edit" || name == "Write" || name == "StrReplace" || name == "NotebookEdit") return "editing";
                if (name == "Read" || name == "Grep" || name == "Glob") return "reading";
                if (name == "Bash" || name == "PowerShell" || name == "Shell") return "shell"; return "tool"; }
            static GitSnap GitOf(Memo m) {
                if (string.IsNullOrEmpty(m.Cwd)) return m.Git; string marker; string top = FindProject(m.Cwd, out marker);
                if (string.IsNullOrEmpty(top)) return null; string gitPath = Path.Combine(top, ".git"); string gitDir = gitPath; string main = top;
                if (File.Exists(gitPath)) {
                    string gd = ReadGitdir(gitPath); if (!string.IsNullOrEmpty(gd)) {
                        gitDir = gd; string common = CommonDir(gd);
                        string guess = !string.IsNullOrEmpty(common) ? Path.GetDirectoryName(common.TrimEnd('\\')) : MainFromGitdir(gd);
                        if (!string.IsNullOrEmpty(guess)) main = guess; } }
                string head = ReadText(Path.Combine(gitDir, "HEAD"), 200); long stamp = 0;
                try { stamp = File.GetLastWriteTimeUtc(Path.Combine(gitDir, "index")).Ticks; } catch (Exception) { }
                if (m.Git != null && m.GitStamp == stamp && m.GitHead == (head ?? "")) return m.Git; var g = new GitSnap();
                g.Main = main; string wt = NameOf(top);
                if (!string.IsNullOrEmpty(wt) && !string.Equals(wt, NameOf(main), StringComparison.OrdinalIgnoreCase)) g.Worktree = wt;
                g.Branch = BranchOf(head); g.Commits = CountReflog(Path.Combine(gitDir, "logs", "HEAD"), m.Create);
                g.Changed = CountIndex(Path.Combine(gitDir, "index"), m.Create); m.Git = g; m.GitStamp = stamp; m.GitHead = head ?? ""; return g; }
            static string ReadGitdir(string gitFile) {
                string line = ReadText(gitFile, 500); if (string.IsNullOrEmpty(line)) return null;
                int i = line.IndexOf("gitdir:", StringComparison.OrdinalIgnoreCase);
                return i < 0 ? null : line.Substring(i + 7).Trim().Replace('/', '\\'); }
            static string CommonDir(string gitDir) {
                string line = ReadText(Path.Combine(gitDir, "commondir"), 500); if (string.IsNullOrEmpty(line)) return null;
                line = line.Trim().Replace('/', '\\'); if (!Path.IsPathRooted(line)) {
                    try { line = Path.GetFullPath(Path.Combine(gitDir, line)); } catch (Exception) { return null; } }
                return line; }
            static string MainFromGitdir(string gitDir) {
                int mark = gitDir.IndexOf("\\.git\\", StringComparison.OrdinalIgnoreCase);
                if (mark < 0 && gitDir.EndsWith("\\.git", StringComparison.OrdinalIgnoreCase)) mark = gitDir.Length - 5;
                return mark < 0 ? null : gitDir.Substring(0, mark); }
            static string BranchOf(string head) {
                if (string.IsNullOrEmpty(head)) return null; head = head.Trim();
                if (!head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase)) return null;
                string r = head.Substring(4).Trim().Replace('/', '\\'); int slash = r.LastIndexOf('\\');
                string name = slash >= 0 ? r.Substring(slash + 1) : r; return name.Length == 0 ? null : name; }
            static int CountReflog(string path, long create) {
                if (!File.Exists(path)) return 0; long start;
                try { start = (long)(DateTime.FromFileTimeUtc(create) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
                catch (Exception) { return 0; } int n = 0; try {
                    using (var sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))) {
                        string line;
                        while ((line = sr.ReadLine()) != null && n < 5000) {
                            int tab = line.IndexOf('\t'); string[] parts = (tab < 0 ? line : line.Substring(0, tab)).Split(' ');
                            if (parts.Length < 2) continue; long ts;
                            if (long.TryParse(parts[parts.Length - 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ts) && ts >= start) n++; } } }
                catch (Exception) { } return n; }
            static int CountIndex(string path, long create) {
                if (!File.Exists(path)) return 0; long start;
                try { start = (long)(DateTime.FromFileTimeUtc(create) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
                catch (Exception) { return 0; } try {
                    byte[] buf = File.ReadAllBytes(path); if (buf.Length < 12 || buf[0] != (byte)'D' || buf[1] != (byte)'I') return 0;
                    int count = Be32(buf, 8), at = 12, changed = 0; for (int i = 0; i < count && at + 62 < buf.Length; i++) {
                        int startAt = at; long mtime = Be32(buf, at + 8);
                        int nameLen = ((buf[at + 60] << 8) | buf[at + 61]) & 0xFFF; at += 62 + nameLen;
                        if (nameLen == 0 || at > buf.Length) break; int span = at - startAt; int pad = 8 - (span % 8);
                        if (pad == 0) pad = 8; at += pad; if (mtime >= start) changed++; }
                    return changed; }
                catch (Exception) { return 0; } }
            static int Be32(byte[] b, int o) { return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]; } static string Weights(int pid) {
                IntPtr h = Native.OpenProcess(Native.QueryInfo | Native.VmRead, false, pid); if (h == IntPtr.Zero) return null;
                IntPtr info = Marshal.AllocHGlobal(48); try {
                    if (dos == null) dos = DosMap(); var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    var seen = new HashSet<long>(); long addr = 0; var sb = new StringBuilder(520);
                    for (int steps = 0; addr >= 0 && addr < 0x00007FFFFFFFFFFF && steps < 80000; steps++) {
                        for (int i = 0; i < 48; i++) Marshal.WriteByte(info, i, 0);
                        if (Native.VirtualQueryEx(h, new IntPtr(addr), info, new IntPtr(48)) == IntPtr.Zero) break;
                        long size = Marshal.ReadIntPtr(info, 24).ToInt64(); uint state = (uint)Marshal.ReadInt32(info, 32);
                        uint type = (uint)Marshal.ReadInt32(info, 40); long alloc = Marshal.ReadIntPtr(info, 8).ToInt64(); if (size <= 0) break;
                        if (state == 0x1000 && type == 0x40000 && seen.Add(alloc)) {
                            sb.Length = 0; if (Native.GetMappedFileName(h, new IntPtr(addr), sb, sb.Capacity) > 0) {
                                string path = ToDos(sb.ToString()); long have; files.TryGetValue(path, out have); files[path] = have + size; } }
                        long next = addr + size; if (next <= addr) break; addr = next; }
                    string best = null; long bestBytes = 0; var names = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in files) {
                        if (!IsWeightFile(kv.Key, kv.Value)) continue; long bytes = kv.Value;
                        try { if (File.Exists(kv.Key)) bytes = new FileInfo(kv.Key).Length; } catch (Exception) { }
                        string name = Path.GetFileNameWithoutExtension(Shard.Replace(Path.GetFileName(kv.Key), "")); long have;
                        names.TryGetValue(name, out have); names[name] = have + bytes;
                        if (have + bytes > bestBytes) { bestBytes = have + bytes; best = name; } }
                    return best; }
                catch (Exception) { return null; }
                finally { Marshal.FreeHGlobal(info); Native.CloseHandle(h); } }
            static bool IsWeightFile(string path, long bytes) {
                string ext = ""; try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch (Exception) { return false; }
                if (ext == ".gguf" || ext == ".safetensors") return true;
                return (ext == ".bin" || ext == ".pt" || ext == ".pth") && bytes >= (20L << 20); }
            static bool SkipMap(string exe) {
                string b = Base(exe);
                return b == "cmd" || b == "conhost" || b == "openconsole" || b == "nohup" || b == "ping" || b == "svchost" || b == "sihost" || b == "explorer" || b == "dllhost" || b == "runtimebroker"; }
            static Dictionary<string, string> DosMap() {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var buf = new char[1024]; try {
                    foreach (DriveInfo di in DriveInfo.GetDrives()) {
                        string letter = di.Name.TrimEnd('\\'); uint n = Native.QueryDosDevice(letter, buf, (uint)buf.Length);
                        if (n == 0) continue; int z = Array.IndexOf(buf, '\0'); if (z > 0) map[new string(buf, 0, z)] = letter; } }
                catch (Exception) { } return map; }
            static string ToDos(string nt) {
                if (string.IsNullOrEmpty(nt) || dos == null) return nt; foreach (var kv in dos)
                    if (nt.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value + nt.Substring(kv.Key.Length);
                return nt; }
            static Dictionary<int, List<int>> Listeners() {
                var map = new Dictionary<int, List<int>>(); ReadPorts(2, 24, 8, 20, -1, 3, map); ReadPorts(23, 56, 20, 52, -1, 3, map);
                ReadPorts(2, 24, 8, 20, 0, 5, map); ReadPorts(23, 56, 20, 52, 48, 5, map); return map; }
            static void ReadPorts(int af, int row, int portOff, int pidOff, int stateOff, int table, Dictionary<int, List<int>> map) {
                int len = 65536; for (int attempt = 0; attempt < 4; attempt++) {
                    IntPtr buf = Marshal.AllocHGlobal(len); try {
                        int cap = len; uint err = Native.GetExtendedTcpTable(buf, ref cap, false, af, table, 0);
                        if (err == 122) { len = cap > len ? cap + 4096 : len * 2; continue; }
                        if (err != 0) return; int n = Marshal.ReadInt32(buf); if (n < 0 || n > 100000) return; for (int i = 0; i < n; i++) {
                            IntPtr r = buf + 4 + i * row; if (stateOff >= 0 && Marshal.ReadInt32(r, stateOff) != 2) continue;
                            int raw = Marshal.ReadInt32(r, portOff);
                            int port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); int pid = Marshal.ReadInt32(r, pidOff);
                            if (port <= 0 || port > 65535 || pid <= 0) continue; List<int> have;
                            if (!map.TryGetValue(pid, out have)) map[pid] = have = new List<int>(); if (!have.Contains(port)) have.Add(port); }
                        return; }
                    catch (Exception) { return; }
                    finally { Marshal.FreeHGlobal(buf); } } }
            static byte[] SqlBlob(IntPtr conn, string sql, string arg) {
                byte[] sqlb = Encoding.UTF8.GetBytes(sql + "\0"); IntPtr stmt;
                if (Native.sqlite3_prepare_v2(conn, sqlb, sqlb.Length, out stmt, IntPtr.Zero) != 0) return null; try {
                    if (arg != null) {
                        byte[] ab = Encoding.UTF8.GetBytes(arg); Native.sqlite3_bind_text(stmt, 1, ab, ab.Length, new IntPtr(-1)); }
                    if (Native.sqlite3_step(stmt) != Native.SqliteRow) return null;
                    IntPtr col = Native.sqlite3_column_blob(stmt, 0); int n = Native.sqlite3_column_bytes(stmt, 0);
                    if (col == IntPtr.Zero || n <= 0) return null; int copy = n > 65536 ? 65536 : n; byte[] buf = new byte[copy];
                    Marshal.Copy(col, buf, 0, copy); return buf; }
                finally { Native.sqlite3_finalize(stmt); } }
            static string HexJson(byte[] hexText, string key) {
                int n = hexText.Length / 2; if (n <= 0 || n > 65536) return null; byte[] raw = new byte[n]; for (int i = 0; i < n; i++) {
                    int hi = HexVal(hexText[i * 2]), lo = HexVal(hexText[i * 2 + 1]); if (hi < 0 || lo < 0) return null;
                    raw[i] = (byte)((hi << 4) | lo); }
                return JsonString(Encoding.UTF8.GetString(raw), "\"" + key + "\":\"", 0); }
            static int HexVal(byte c) {
                if (c >= (byte)'0' && c <= (byte)'9') return c - (byte)'0'; if (c >= (byte)'a' && c <= (byte)'f') return c - (byte)'a' + 10;
                if (c >= (byte)'A' && c <= (byte)'F') return c - (byte)'A' + 10; return -1; }
            static string Hex(byte[] b, int o, int n) {
                var sb = new StringBuilder(n * 2); for (int i = 0; i < n; i++) sb.Append(b[o + i].ToString("x2")); return sb.ToString(); }
            static string Encode(string cwd) {
                var sb = new StringBuilder(cwd.Length); for (int i = 0; i < cwd.Length; i++) sb.Append(char.IsLetterOrDigit(cwd[i]) ? cwd[i] : '-');
                return sb.ToString(); }
            static string Md5Hex(string s) {
                using (MD5 md = MD5.Create()) {
                    byte[] hash = md.ComputeHash(Encoding.UTF8.GetBytes(s ?? "")); var sb = new StringBuilder(32);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2")); return sb.ToString(); } }
            static string JsonString(string hay, string key, int from) {
                if (string.IsNullOrEmpty(hay) || from < 0) return null; int i = hay.IndexOf(key, from, StringComparison.Ordinal);
                if (i < 0) return null; i += key.Length; var sb = new StringBuilder(); for (int n = 0; i < hay.Length && n < 400; i++, n++) {
                    char c = hay[i]; if (c == '"') break; if (c == '\\' && i + 1 < hay.Length) { sb.Append(hay[i + 1]); i++; continue; }
                    sb.Append(c); }
                return sb.Length == 0 ? null : sb.ToString(); }
            static long JsonLong(string hay, string key) {
                if (string.IsNullOrEmpty(hay)) return 0; int i = hay.IndexOf(key, StringComparison.Ordinal); if (i < 0) return 0;
                i += key.Length; long n = 0; int digits = 0;
                while (i < hay.Length && hay[i] >= '0' && hay[i] <= '9' && digits < 18) { n = n * 10 + (hay[i] - '0'); i++; digits++; }
                return digits == 0 ? 0 : n; }
            static string FileFrom(string blob) {
                string p = JsonString(blob, "\"file_path\":\"", 0); if (p == null) p = JsonString(blob, "\"path\":\"", 0); return BaseName(p); }
            static string BaseName(string path) {
                if (string.IsNullOrEmpty(path)) return null; path = path.Replace('/', '\\'); int slash = path.LastIndexOf('\\');
                string name = slash >= 0 ? path.Substring(slash + 1) : path; return name.Length == 0 || name.Length > 120 ? null : name; }
            static DateTime Stamp(string s) {
                DateTime dt;
                if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                    return dt.ToUniversalTime();
                return DateTime.MinValue; }
            static string Arg(string cmd, string name) {
                if (string.IsNullOrEmpty(cmd)) return null; var parts = Paths.Split(cmd); string eq = name + "=";
                for (int i = 0; i < parts.Count; i++) {
                    if (parts[i].StartsWith(eq, StringComparison.Ordinal)) return parts[i].Substring(eq.Length);
                    if (string.Equals(parts[i], name, StringComparison.Ordinal) && i + 1 < parts.Count) return parts[i + 1]; }
                return null; }
            static bool Has(string cmd, string name) {
                return Arg(cmd, name) != null || (cmd != null && Paths.Split(cmd).Contains(name)); }
            static bool CmdHas(string cmd, string path) {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(cmd)) return false; string name = Path.GetFileName(path);
                return name.Length >= 5 && cmd.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0; }
            static bool SamePath(string a, string b) {
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
                try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
                catch (Exception) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); } }
            static bool AtOrUnder(string path, string root) {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
                try { path = Path.GetFullPath(path); root = Path.GetFullPath(root); } catch (Exception) { }
                path = path.TrimEnd('\\'); root = root.TrimEnd('\\');
                return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase); }
            static bool UnderWindows(string path) { return AtOrUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows)); }
            static string LeafChild(int pid, Dictionary<int, Proc> byPid) {
                string leaf = null; var stack = new List<int>(); stack.Add(pid); var seen = new HashSet<int>();
                while (stack.Count > 0) {
                    int id = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1); foreach (var kv in byPid) {
                        Proc c = kv.Value; if (c.Parent != id || c.Pid == pid || c.CreateTime <= 0 || !seen.Add(c.Pid)) continue;
                        string exe = (c.Exe ?? "").ToLowerInvariant();
                        if (exe == "conhost.exe" || exe == "cmd.exe" || exe == "nohup.exe") { stack.Add(c.Pid); continue; }
                        leaf = c.Exe; stack.Add(c.Pid); } }
                return leaf; }
            static double AgeMin(Proc p) {
                if (p.CreateTime <= 0) return 0; try { return (DateTime.Now - DateTime.FromFileTime(p.CreateTime)).TotalMinutes; }
                catch (Exception) { return 0; } }
            static string NameOf(string path) {
                if (string.IsNullOrEmpty(path)) return null; try { return Path.GetFileName(path.TrimEnd('\\')); } catch (Exception) { return null; } }
            static string PrettyExe(string exe) {
                string n = Path.GetFileNameWithoutExtension(exe ?? "process"); if (n.Length == 0) return "process";
                if (n.Equals("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome"; return n; }
            static string SizeText(long bytes) {
                double gb = bytes / 1073741824.0; if (gb >= 1) return gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
                return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB"; }
            static string Slug(string s) {
                if (string.IsNullOrEmpty(s)) return "x"; var sb = new StringBuilder(s.Length); foreach (char c in s.ToLowerInvariant()) {
                    if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                    else if (sb.Length == 0 || sb[sb.Length - 1] != '-') sb.Append('-'); }
                string t = sb.ToString().Trim('-'); return t.Length == 0 ? "x" : t; }
            static string ReadText(string path, int cap) {
                try {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                        int n = (int)Math.Min(cap, fs.Length); if (n <= 0) return ""; byte[] buf = new byte[n];
                        int got = fs.Read(buf, 0, n); return Encoding.UTF8.GetString(buf, 0, got); } }
                catch (Exception) { return null; } }
            static string Role(string exe) {
                string n = Base(exe); if (n == "windowsterminal" || n == "wt") return "terminal";
                if (n == "cursor" || n == "code" || n == "devenv") return "editor"; if (n == "explorer") return "explorer"; return null; }
            static bool Shell(string exe) {
                string n = Base(exe); return n == "cmd" || n == "powershell" || n == "pwsh" || n == "bash" || n == "mintty" || n == "wsl"; }
            static bool Infra(string exe) {
                string n = Base(exe); return n == "conhost" || n == "openconsole" || n == "nohup"; }
            static string RoleLabel(string exe) {
                string n = Base(exe); if (n == "pwsh" || n == "powershell") return "PowerShell"; if (n == "cmd") return "cmd";
                if (n == "windowsterminal" || n == "wt") return "Windows Terminal"; if (n == "cursor") return "Cursor";
                if (n == "code") return "VS Code"; if (n == "devenv") return "Visual Studio"; if (n == "explorer") return "Explorer"; return n; }
            static string Base(string exe) { return Path.GetFileNameWithoutExtension(exe ?? "").ToLowerInvariant(); } } } }
