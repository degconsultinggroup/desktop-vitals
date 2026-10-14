// Facts 1-5: provenance, project, behavior, mapped weights, and what an agent is doing.
// Provenance, project, and kind are cached per pid. A later pass refreshes CPU, RAM, VRAM, ports, and state.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopVitals
{
    static partial class Engine
    {
        static class LiveBoard
        {
            const long HeavyRam = 2L << 30;
            const long ModelVram = 1L << 30;
            const int TailCap = 16 * 1024;
            static readonly Regex Shard = new Regex(@"-\d{5}-of-\d{5}(?=\.[A-Za-z0-9]+$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            static readonly Dictionary<int, Memo> memos = new Dictionary<int, Memo>();
            static readonly Dictionary<string, string> projects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            static readonly Dictionary<string, Tail> tails = new Dictionary<string, Tail>(StringComparer.OrdinalIgnoreCase);
            static readonly List<ExitNote> exits = new List<ExitNote>();
            static Dictionary<string, string> dos;
            static List<string> startup = new List<string>();
            static DateTime startupRead = DateTime.MinValue;

            class Memo
            {
                public long Create;
                public string Cmd = "", Cwd, Image, Script, Leaf = "";
                public bool Yours, Services, Orphan, Plain, TaskHit, Mapped;
                public string LaunchKind, LaunchName, TaskName, TaskPath, ProjectFull, Marker, Agent, Weight, AgentModel, Kind = "script";
                public long LastIo, LastCpu, CpuBucket;
                public DateTime LastActive, WriteChecked;
                public string RecentWrite, LogFile;
                public IntPtr Handle;
                public Act Action;
                public GitSnap Git;
                public long GitStamp;
                public string GitHead = "";
            }

            class Act
            {
                public string Kind, File, Model;
                public DateTime When;
                public bool Open;
            }

            class Tail
            {
                public long Offset;
                public Act Last;
                public string Model;
            }

            class GitSnap
            {
                public string Branch, Worktree, Main;
                public int Commits, Changed;
            }

            class ExitNote
            {
                public string Name, Project, Folder, Kind;
                public int Code;
                public DateTime When;
                public bool Failed;
            }

            public static void Clear()
            {
                foreach (var kv in memos)
                    if (kv.Value.Handle != IntPtr.Zero) Native.CloseHandle(kv.Value.Handle);
                memos.Clear();
                tails.Clear();
            }

            public static List<Group> Make(List<Proc> procs)
            {
                LastProcs = procs == null ? 0 : procs.Count;
                var byPid = new Dictionary<int, Proc>();
                if (procs != null) foreach (Proc p in procs) byPid[p.Pid] = p;
                var listeners = Listeners();
                var now = DateTime.UtcNow;
                var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (procs != null)
                    foreach (Proc p in procs)
                    {
                        Memo m = Touch(p, byPid);
                        if (!m.TaskHit && MatchTask(p, m)) m.Yours = true;
                        if (Orphan(p, byPid) && ProjectHolds(m)) { m.Yours = true; m.Orphan = true; m.Plain = false; }
                        NoteActivity(m, p, now);
                        if (!string.IsNullOrEmpty(m.Script))
                        {
                            string folder = Scripts.Folder(m.Script);
                            if (!string.IsNullOrEmpty(folder)) folders.Add(folder);
                        }
                    }
                if (procs != null) CatchUp(procs, byPid);
                NoteExits(byPid, now);
                try { TaskList.Refresh(null); } catch (Exception) { }
                try { ModelScan.Refresh(folders); } catch (Exception) { }

                var used = new HashSet<int>();
                var things = new List<Thing>();
                var taskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var plain = new Dictionary<string, Thing>(StringComparer.OrdinalIgnoreCase);
                if (procs != null)
                {
                    Agents(procs, byPid, listeners, things, used);
                    foreach (Proc p in procs)
                    {
                        if (used.Contains(p.Pid)) continue;
                        Memo m = memo(p);
                        if (m == null || !m.Yours || Infra(p.Exe)) continue;
                        if (PlainHost(p, m))
                        {
                            used.Add(p.Pid);
                            RollPlain(plain, p);
                            continue;
                        }
                        if (!OwnThing(p, m, byPid)) continue;
                        if (ShellOnly(p, m, byPid)) continue;
                        Thing t = WorkThing(p, m, byPid, listeners, used);
                        if (t == null) continue;
                        if (!string.IsNullOrEmpty(m.TaskName)) taskNames.Add(m.TaskName);
                        things.Add(t);
                    }
                    foreach (Thing t in plain.Values)
                    {
                        if (t.count < 1) t.count = 1;
                        if (Hidden(t.id, t.name)) continue;
                        Finish(t);
                        things.Add(t);
                    }
                    CollapsePlain(things);
                }
                Tasks(taskNames, things);
                Models(things);
                Heavy(procs, used, things);
                Ended(things);
                Pins(procs, things);
                return Order(things);
            }

            static Memo Touch(Proc p, Dictionary<int, Proc> byPid)
            {
                Memo m;
                if (memos.TryGetValue(p.Pid, out m) && m.Create == p.CreateTime) return m;
                if (m != null) Forget(p.Pid, m, DateTime.UtcNow);
                m = new Memo();
                m.Create = p.CreateTime;
                m.LastActive = DateTime.UtcNow;
                m.LastCpu = p.CpuTime;
                m.LastIo = p.Io;
                memos[p.Pid] = m;
                if (p.Session == 0) { m.Services = true; return m; }
                if (!Infra(p.Exe))
                {
                    try { m.Cmd = Procs.CommandLine(p.Pid) ?? ""; } catch (Exception) { m.Cmd = ""; }
                    try { m.Cwd = Procs.CurrentDirectory(p.Pid); } catch (Exception) { }
                    try { m.Image = Procs.ImagePath(p.Pid); } catch (Exception) { }
                    p.Cmd = m.Cmd;
                    m.Script = ScriptOf(p, m.Cwd);
                }
                Decide(p, m, byPid);
                MapOnce(p, m);
                m.Plain = m.Yours && m.Agent == null && string.IsNullOrEmpty(m.Weight) && string.IsNullOrEmpty(m.Script) && !m.TaskHit && !m.Orphan;
                m.Leaf = LeafName(p, m);
                if (m.Yours && !m.Plain) Hold(m, p.Pid);
                return m;
            }

            static void MapOnce(Proc p, Memo m)
            {
                if (!m.Yours || m.Mapped || Infra(p.Exe)) return;
                if (!SkipMap(p.Exe))
                {
                    m.Weight = Weights(p.Pid);
                    if (string.IsNullOrEmpty(m.Weight)) m.Weight = WeightArg(m.Cmd);
                    if (!string.IsNullOrEmpty(m.Weight)) m.Plain = false;
                }
                m.Mapped = true;
            }

            // Parent memos exist only after the first walk, so inheritance and a late map run again without reopening command lines.
            static void CatchUp(List<Proc> procs, Dictionary<int, Proc> byPid)
            {
                for (int pass = 0; pass < 8; pass++)
                {
                    bool changed = false;
                    foreach (Proc p in procs)
                    {
                        Memo m = memo(p);
                        if (m == null || p.Session == 0) continue;
                        if (!m.Yours)
                        {
                            Inherit(p, m, byPid);
                            if (m.Yours) changed = true;
                        }
                        if (m.Yours && !m.Mapped)
                        {
                            MapOnce(p, m);
                            if (!string.IsNullOrEmpty(m.Weight)) m.Plain = false;
                        }
                    }
                    if (!changed) break;
                }
            }

            static bool PlainHost(Proc p, Memo m)
            {
                if (!Shell(p.Exe) || m.Agent != null || m.TaskHit || m.Orphan) return false;
                if (!string.IsNullOrEmpty(m.Script) || !string.IsNullOrEmpty(m.Weight)) return false;
                return true;
            }

            static void RollPlain(Dictionary<string, Thing> plain, Proc p)
            {
                string key = Base(p.Exe);
                Thing have;
                if (!plain.TryGetValue(key, out have))
                {
                    have = new Thing();
                    have.kind = "app";
                    have.state = "running";
                    have.name = PrettyExe(p.Exe);
                    have.id = "app:" + key;
                    have.count = 0;
                    have.since = Age(p.CreateTime);
                    have.why = "shell";
                    plain[key] = have;
                }
                have.count++;
                have.cpu += p.Cpu;
                have.ram_mb += p.Private / 1048576.0;
            }

            static void Hold(Memo m, int pid)
            {
                if (m.Handle != IntPtr.Zero) return;
                m.Handle = Native.OpenProcess(Native.QueryLimited, false, pid);
            }

            static void NoteActivity(Memo m, Proc p, DateTime now)
            {
                long dCpu = p.CpuTime - m.LastCpu;
                long dIo = p.Io - m.LastIo;
                if (dCpu < 0) dCpu = 0;
                m.CpuBucket += dCpu;
                if (dIo != 0 || m.CpuBucket >= 5000000)
                {
                    m.LastActive = now;
                    m.CpuBucket = 0;
                }
                m.LastCpu = p.CpuTime;
                m.LastIo = p.Io;
                if (m.Yours && !m.Plain && (now - m.WriteChecked).TotalSeconds > 30 && AgeMin(p) > 5)
                {
                    m.WriteChecked = now;
                    m.RecentWrite = RecentWrite(p, m.Cwd);
                }
            }

            static void Decide(Proc p, Memo m, Dictionary<int, Proc> byPid)
            {
                bool sihost = false;
                Proc cur = p;
                var guard = new HashSet<int>();
                for (int n = 0; n < 40 && guard.Add(cur.Pid); n++)
                {
                    Proc parent;
                    if (!byPid.TryGetValue(cur.Parent, out parent)) break;
                    if (parent.CreateTime == 0 || cur.CreateTime == 0 || parent.CreateTime >= cur.CreateTime) break;
                    string role = AppRole(parent.Exe);
                    string b = Base(parent.Exe);
                    if (b == "sihost") sihost = true;
                    if (!sihost && (parent.Session == 0 || b == "services")) m.Services = true;
                    if (role == "terminal" || role == "editor") { m.LaunchKind = role; m.LaunchName = RoleLabel(parent.Exe); }
                    else if (role == "explorer" && m.LaunchKind == null) { m.LaunchKind = role; m.LaunchName = "Explorer"; }
                    else if (Shell(parent.Exe) && m.LaunchKind == null) { m.LaunchKind = "terminal"; m.LaunchName = RoleLabel(parent.Exe); }
                    cur = parent;
                }
                AssignProject(p, m);
                m.Agent = AgentOf(p.Exe, m.Cmd);
                if (p.Session == 0) { m.Yours = false; return; }
                if (MatchTask(p, m)) { m.Yours = true; if (m.LaunchKind == null || m.Services) { m.LaunchKind = "task"; m.LaunchName = "Scheduled task"; } }
                else if (sihost && !WindowsComponent(m.Image)) m.Yours = true;
                else if (m.Services) m.Yours = false;
                else if ((m.LaunchKind == "terminal" || m.LaunchKind == "editor") && !PartOfLauncher(p, m, byPid)) m.Yours = true;
                else if (MatchStartup(m) && !AppImage(m)) { m.Yours = true; m.LaunchKind = "startup"; m.LaunchName = "Startup"; }
                else if (m.LaunchKind == "explorer" && (!AppImage(m) || Shell(p.Exe))) m.Yours = true;
                else if (Orphan(p, byPid) && ProjectHolds(m)) { m.Yours = true; m.Orphan = true; }
                Inherit(p, m, byPid);
            }

            static void Inherit(Proc p, Memo m, Dictionary<int, Proc> byPid)
            {
                if (m.Yours || p.Session == 0) return;
                Proc parent;
                if (!byPid.TryGetValue(p.Parent, out parent)) return;
                if (parent.CreateTime == 0 || p.CreateTime == 0 || parent.CreateTime >= p.CreateTime) return;
                Memo pm;
                if (!memos.TryGetValue(parent.Pid, out pm) || pm.Create != parent.CreateTime || !pm.Yours) return;
                m.Yours = true;
                if (m.LaunchKind == null) { m.LaunchKind = pm.LaunchKind; m.LaunchName = pm.LaunchName; }
                if (pm.TaskHit) { m.TaskHit = true; m.TaskName = pm.TaskName; m.TaskPath = pm.TaskPath; }
            }

            static bool Orphan(Proc p, Dictionary<int, Proc> byPid)
            {
                Proc parent;
                if (!byPid.TryGetValue(p.Parent, out parent)) return true;
                if (parent.CreateTime == 0 || p.CreateTime == 0 || parent.CreateTime >= p.CreateTime) return true;
                return false;
            }

            // Parent already exited: a script living in a project under the profile is still yours.
            static bool ProjectHolds(Memo m)
            {
                if (string.IsNullOrEmpty(m.ProjectFull) || !UnderUser(m.ProjectFull)) return false;
                if (!string.IsNullOrEmpty(m.Script))
                {
                    string path = m.Script.StartsWith("module:", StringComparison.Ordinal) ? Scripts.Folder(m.Script) : m.Script;
                    if (!string.IsNullOrEmpty(path) && AtOrUnder(path, m.ProjectFull)) return true;
                }
                if (!string.IsNullOrEmpty(m.Image) && AtOrUnder(m.Image, m.ProjectFull) && !AppImage(m)) return true;
                if (!string.IsNullOrEmpty(m.Cwd) && AtOrUnder(m.Cwd, m.ProjectFull) && !AppImage(m)) return true;
                return false;
            }

            static bool PartOfLauncher(Proc p, Memo m, Dictionary<int, Proc> byPid)
            {
                Proc cur = p;
                string home = null;
                var guard = new HashSet<int>();
                for (int n = 0; n < 40 && guard.Add(cur.Pid); n++)
                {
                    Proc parent;
                    if (!byPid.TryGetValue(cur.Parent, out parent)) break;
                    if (parent.CreateTime >= cur.CreateTime) break;
                    if (AppRole(parent.Exe) == "editor" || AppRole(parent.Exe) == "terminal")
                    {
                        Memo pm;
                        string image = null;
                        if (memos.TryGetValue(parent.Pid, out pm)) image = pm.Image;
                        if (string.IsNullOrEmpty(image)) image = parent.Exe;
                        home = Path.GetDirectoryName(image ?? "");
                        break;
                    }
                    cur = parent;
                }
                if (string.IsNullOrEmpty(home)) return false;
                string mine = m.Image ?? "";
                bool inside = mine.StartsWith(home.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
                if (!inside) return false;
                return string.IsNullOrEmpty(m.ProjectFull) || m.ProjectFull.StartsWith(home, StringComparison.OrdinalIgnoreCase);
            }

            static bool MatchTask(Proc p, Memo m)
            {
                foreach (TaskInfo t in TaskList.Items)
                {
                    if (t == null) continue;
                    if (CmdHas(m.Cmd, t.Script) || CmdHas(m.Cmd, ScriptFile(t.Exe)) || SamePath(m.Image, t.Exe))
                    {
                        m.TaskHit = true;
                        m.TaskName = t.Name;
                        m.TaskPath = string.IsNullOrEmpty(t.Path) ? t.Name : t.Path;
                        if (string.IsNullOrEmpty(m.ProjectFull) && !string.IsNullOrEmpty(t.Script)) AssignProjectPath(m, t.Script);
                        return true;
                    }
                }
                return false;
            }

            static bool MatchStartup(Memo m)
            {
                if ((DateTime.UtcNow - startupRead).TotalSeconds > 60) { startupRead = DateTime.UtcNow; startup = StartupExes(); }
                foreach (string exe in startup)
                    if (SamePath(m.Image, exe) && !UnderWindows(exe) && !Installed(exe)) return true;
                return false;
            }

            static void AssignProject(Proc p, Memo m)
            {
                string marker;
                string root = FindProject(m.Cwd, out marker);
                if (ToolRoot(root, m)) root = null;
                if (root == null) { root = FindProject(ScriptFile(m.Script), out marker); if (ToolRoot(root, m)) root = null; }
                if (root == null) { root = FindProject(m.Image, out marker); if (ToolRoot(root, m)) root = null; }
                if (root != null && AppImage(m))
                {
                    string dir = Path.GetDirectoryName(m.Image ?? "");
                    if (!string.IsNullOrEmpty(dir) && root.StartsWith(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) root = null;
                }
                m.ProjectFull = root;
                m.Marker = root == null ? null : marker;
            }

            static void AssignProjectPath(Memo m, string path)
            {
                string marker;
                string root = FindProject(path, out marker);
                if (string.IsNullOrEmpty(root)) return;
                m.ProjectFull = root;
                m.Marker = marker;
            }

            static bool ToolRoot(string root, Memo m)
            {
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(m.Image)) return false;
                if (!AtOrUnder(m.Image, root)) return false;
                return string.IsNullOrEmpty(m.Cwd) || !AtOrUnder(m.Cwd, root);
            }

            static string FindProject(string start, out string marker)
            {
                marker = null;
                if (string.IsNullOrEmpty(start)) return null;
                string dir = start;
                try { if (File.Exists(start) || !Directory.Exists(start)) dir = Path.GetDirectoryName(start); }
                catch (Exception) { return null; }
                string cached;
                if (!string.IsNullOrEmpty(dir) && projects.TryGetValue(dir, out cached))
                {
                    if (cached.Length == 0) return null;
                    int bar = cached.IndexOf('|');
                    marker = bar < 0 ? null : cached.Substring(bar + 1);
                    return bar < 0 ? cached : cached.Substring(0, bar);
                }
                string walk = dir;
                string found = null;
                string foundMarker = null;
                for (int n = 0; n < 40 && !string.IsNullOrEmpty(walk); n++)
                {
                    try
                    {
                        if (Directory.Exists(Path.Combine(walk, ".git")) || File.Exists(Path.Combine(walk, ".git"))) { found = walk; foundMarker = ".git"; break; }
                        foreach (string name in new[] { "package.json", "pyproject.toml", "Cargo.toml", "go.mod" })
                            if (File.Exists(Path.Combine(walk, name))) { found = walk; foundMarker = name; break; }
                        if (found == null && Directory.Exists(walk) && Directory.GetFiles(walk, "*.sln").Length > 0) { found = walk; foundMarker = "*.sln"; break; }
                    }
                    catch (Exception) { }
                    if (found != null) break;
                    DirectoryInfo parent = null;
                    try { parent = Directory.GetParent(walk); } catch (Exception) { parent = null; }
                    if (parent == null) break;
                    walk = parent.FullName;
                }
                if (!string.IsNullOrEmpty(dir)) projects[dir] = found == null ? "" : found + "|" + (foundMarker ?? "");
                marker = foundMarker;
                return found;
            }

            static void Agents(List<Proc> procs, Dictionary<int, Proc> byPid, Dictionary<int, List<int>> listeners, List<Thing> things, HashSet<int> used)
            {
                var groups = new Dictionary<string, Thing>(StringComparer.OrdinalIgnoreCase);
                foreach (Proc p in procs)
                {
                    Memo m = memo(p);
                    if (m == null || m.Agent == null || !m.Yours) continue;
                    if (ChildOfAgent(p, procs)) continue;
                    used.Add(p.Pid);
                    GitSnap git = GitOf(m);
                    string child = LeafChild(p.Pid, byPid);
                    Act act = LogOf(m, p);
                    string repo = git != null && !string.IsNullOrEmpty(git.Main) ? git.Main : m.ProjectFull;
                    string repoName = NameOf(repo);
                    if (string.IsNullOrEmpty(repoName)) repoName = "no project";
                    string key = m.Agent + "\n" + (repo ?? "");
                    Thing t;
                    if (!groups.TryGetValue(key, out t))
                    {
                        t = new Thing();
                        t.id = "agent:" + Slug(m.Agent) + ":" + Slug(repoName);
                        t.name = m.Agent;
                        t.kind = "agent";
                        t.project = repoName;
                        t.folder = repo ?? m.Cwd;
                        t.count = 0;
                        t.since = Age(p.CreateTime);
                        if (git != null) t.branch = git.Branch;
                        groups[key] = t;
                        things.Add(t);
                    }
                    t.count++;
                    t.cpu += p.Cpu;
                    t.ram_mb += p.Private / 1048576.0;
                    t.vram_mb += VramOf(p.Pid) / 1048576.0;
                    List<int> ports;
                    if (listeners.TryGetValue(p.Pid, out ports)) foreach (int port in ports) if (!t.ports.Contains(port)) t.ports.Add(port);
                    string state = AgentState(m, p, act, child);
                    if (Worse(state, t.state)) t.state = state;
                    string detail = AgentDetail(m, act, git, child);
                    if (string.IsNullOrEmpty(t.detail) || state == "waiting") t.detail = detail;
                    if (string.IsNullOrEmpty(t.branch) && git != null) t.branch = git.Branch;
                    if (string.IsNullOrEmpty(t.why))
                    {
                        if (!string.IsNullOrEmpty(m.AgentModel)) t.why = "model " + m.AgentModel;
                        else t.why = "agent";
                    }
                    string older = Age(p.CreateTime);
                    if (t.since.Length == 0) t.since = older;
                    m.Kind = "agent";
                    m.Leaf = m.Agent;
                }
                for (int i = things.Count - 1; i >= 0; i--)
                {
                    if (things[i].kind != "agent") continue;
                    if (things[i].count < 1) things[i].count = 1;
                    if (Hidden(things[i].id, things[i].name)) { things.RemoveAt(i); continue; }
                    Finish(things[i]);
                }
            }

            static bool ChildOfAgent(Proc p, List<Proc> procs)
            {
                int id = p.Parent;
                var guard = new HashSet<int>();
                while (id != 0 && guard.Add(id))
                {
                    Proc up = null;
                    foreach (Proc q in procs) if (q.Pid == id) { up = q; break; }
                    if (up == null) return false;
                    if (up.CreateTime >= p.CreateTime) return false;
                    Memo m = memo(up);
                    if (m != null && m.Agent != null) return true;
                    id = up.Parent;
                }
                return false;
            }

            static string AgentState(Memo m, Proc p, Act act, string child)
            {
                bool open = act != null && act.Open && string.IsNullOrEmpty(child);
                if (open) return "waiting";
                if (!string.IsNullOrEmpty(child)) return "working";
                if (act != null && act.Kind == "answering" && string.IsNullOrEmpty(child)) return "waiting";
                double quiet = (DateTime.UtcNow - m.LastActive).TotalMinutes;
                if (AgeMin(p) >= 5 && quiet >= 5) return string.IsNullOrEmpty(m.ProjectFull) ? "idle" : "quiet";
                return "working";
            }

            static string AgentDetail(Memo m, Act act, GitSnap git, string child)
            {
                var bits = new List<string>();
                if (act != null && !string.IsNullOrEmpty(act.Kind))
                {
                    string bit = act.Kind;
                    if (!string.IsNullOrEmpty(act.File)) bit += " " + act.File;
                    if (act.When != DateTime.MinValue)
                    {
                        string age = FormatSpan(DateTime.UtcNow - act.When.ToUniversalTime());
                        if (age.Length > 0) bit += " \u00B7 " + age;
                    }
                    bits.Add(bit);
                }
                if (git != null && git.Commits > 0) bits.Add(git.Commits.ToString(CultureInfo.InvariantCulture) + (git.Commits == 1 ? " commit" : " commits"));
                if (git != null && git.Changed > 0) bits.Add(git.Changed.ToString(CultureInfo.InvariantCulture) + (git.Changed == 1 ? " file changed" : " files changed"));
                if (bits.Count == 0 && git != null && !string.IsNullOrEmpty(git.Branch)) bits.Add("branch " + git.Branch);
                if (!string.IsNullOrEmpty(child)) bits.Add("running " + child);
                else if (bits.Count == 0 && git != null && !string.IsNullOrEmpty(git.Worktree)) bits.Add(git.Worktree);
                return bits.Count == 0 ? null : string.Join(" \u00B7 ", bits.ToArray());
            }

            static bool Worse(string state, string have)
            {
                return Rank(state) > Rank(have);
            }

            static int Rank(string state)
            {
                if (state == "waiting") return 5;
                if (state == "failed") return 4;
                if (state == "working" || state == "running") return 3;
                if (state == "quiet") return 2;
                if (state == "idle") return 1;
                return 0;
            }

            static Thing WorkThing(Proc p, Memo m, Dictionary<int, Proc> byPid, Dictionary<int, List<int>> listeners, HashSet<int> used)
            {
                var members = new List<Proc>();
                members.Add(p);
                used.Add(p.Pid);
                foreach (var kv in byPid)
                {
                    Proc c = kv.Value;
                    if (c.Parent != p.Pid || c.CreateTime <= p.CreateTime) continue;
                    Memo cm = memo(c);
                    if (cm != null && cm.Agent != null) continue;
                    if (cm != null && OwnThing(c, cm, byPid) && !Infra(c.Exe)) continue;
                    members.Add(c);
                    used.Add(c.Pid);
                }
                List<int> ports = new List<int>();
                long vram = 0;
                double cpu = 0, ram = 0;
                foreach (Proc c in members)
                {
                    cpu += c.Cpu;
                    ram += c.Private / 1048576.0;
                    vram += VramOf(c.Pid);
                    List<int> lp;
                    if (listeners.TryGetValue(c.Pid, out lp)) foreach (int port in lp) if (!ports.Contains(port)) ports.Add(port);
                }
                vram = vram / 1048576;
                if (m.Weight == null && vram >= (ModelVram / 1048576)) m.Weight = m.AgentModel;
                string kind = "script";
                if (!string.IsNullOrEmpty(m.Weight)) kind = "model";
                else if (ports.Count > 0) kind = "server";
                else if (m.Orphan || (AgeMin(p) > 10 && !string.IsNullOrEmpty(m.RecentWrite))) kind = "bot";
                else if (string.IsNullOrEmpty(m.Script) && AppImage(m)) kind = "app";
                else if (string.IsNullOrEmpty(m.Script)) kind = "app";
                if (m.TaskHit && kind == "app") kind = "task";
                string state = "running";
                if (ports.Count == 0 && AgeMin(p) >= 5 && (DateTime.UtcNow - m.LastActive).TotalMinutes >= 5) state = "quiet";
                var t = new Thing();
                t.kind = kind;
                t.state = state;
                t.name = !string.IsNullOrEmpty(m.TaskName) ? m.TaskName : (kind == "model" ? m.Weight : m.Leaf);
                t.id = IdOf(m, p, kind, t.name);
                t.project = ProjectLabel(m, p);
                t.folder = FolderOf(m);
                t.cpu = cpu;
                t.ram_mb = ram;
                t.vram_mb = vram;
                t.ports = ports;
                t.since = Age(p.CreateTime);
                t.count = 1;
                t.why = Why(m, ports);
                if (kind == "model") t.detail = "loaded by " + (p.Exe ?? "process");
                else if (!string.IsNullOrEmpty(m.RecentWrite)) t.detail = "wrote " + m.RecentWrite;
                else if (m.Orphan) t.detail = "works on its own";
                m.Kind = kind;
                m.Leaf = t.name;
                if (Hidden(t.id, t.name)) return null;
                Finish(t);
                return t;
            }

            static bool OwnThing(Proc p, Memo m, Dictionary<int, Proc> byPid)
            {
                if (!m.Yours || Infra(p.Exe)) return false;
                Proc parent;
                if (!byPid.TryGetValue(p.Parent, out parent)) return true;
                if (parent.CreateTime == 0 || p.CreateTime == 0 || parent.CreateTime >= p.CreateTime) return true;
                Memo pm = memo(parent);
                if (pm == null || !pm.Yours || Infra(parent.Exe)) return true;
                if (AppRole(parent.Exe) != null) return true;
                // A script the shell started is its own thing. ping and other bare commands stay with the shell.
                if (Shell(parent.Exe))
                    return !string.IsNullOrEmpty(m.Script) || m.Agent != null || !string.IsNullOrEmpty(m.Weight) || m.TaskHit || m.Orphan;
                if (!string.IsNullOrEmpty(m.ProjectFull) && !string.IsNullOrEmpty(pm.ProjectFull) && !string.Equals(m.ProjectFull, pm.ProjectFull, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            static bool ShellOnly(Proc p, Memo m, Dictionary<int, Proc> byPid)
            {
                if (!Shell(p.Exe) || !string.IsNullOrEmpty(m.Script) || m.Agent != null || m.TaskHit) return false;
                foreach (var kv in byPid)
                {
                    Proc c = kv.Value;
                    if (c.Parent != p.Pid || c.CreateTime <= p.CreateTime) continue;
                    Memo cm = memo(c);
                    if (cm != null && cm.Yours && !Infra(c.Exe) && (!string.IsNullOrEmpty(cm.Script) || cm.Agent != null)) return true;
                }
                return false;
            }

            static void CollapsePlain(List<Thing> things)
            {
                var map = new Dictionary<string, Thing>(StringComparer.OrdinalIgnoreCase);
                for (int i = things.Count - 1; i >= 0; i--)
                {
                    Thing t = things[i];
                    if (t.kind != "app" || t.pinned) continue;
                    if (!string.IsNullOrEmpty(t.folder) && t.why != null && t.why.IndexOf("project=", StringComparison.Ordinal) >= 0) continue;
                    if (t.ports != null && t.ports.Count > 0) continue;
                    string key = "plain:" + (t.name ?? "");
                    Thing have;
                    if (!map.TryGetValue(key, out have)) { map[key] = t; continue; }
                    have.count += Math.Max(1, t.count);
                    have.cpu += t.cpu;
                    have.ram_mb += t.ram_mb;
                    have.vram_mb += t.vram_mb;
                    things.RemoveAt(i);
                }
            }

            static void Tasks(HashSet<string> live, List<Thing> things)
            {
                foreach (TaskInfo task in TaskList.Items)
                {
                    if (task == null || live.Contains(task.Name ?? "")) continue;
                    var t = new Thing();
                    t.id = "task:" + (string.IsNullOrEmpty(task.Path) ? task.Name : task.Path);
                    t.name = task.Name ?? "";
                    t.kind = "task";
                    t.folder = task.Script;
                    string marker;
                    string root = FindProject(task.Script ?? task.Exe, out marker);
                    t.project = root != null ? Path.GetFileName(root.TrimEnd('\\')) : "task";
                    if (task.Running) t.state = "running";
                    else if (task.Failed) t.state = "failed";
                    else if (task.NeverRan) t.state = "idle";
                    else if (task.Result == 0) t.state = "ended";
                    else t.state = "idle";
                    t.since = SinceWhen(task.LastRun);
                    t.why = task.Failed ? "last result was not success" : "scheduled task";
                    if (task.Failed && task.LastRun.Year >= 2000)
                    {
                        string next = "";
                        if (task.NextRun > DateTime.Now) next = " \u00B7 next in " + FormatSpan(task.NextRun - DateTime.Now);
                        t.detail = "failed " + task.LastRun.ToString("HH:mm", CultureInfo.InvariantCulture) + next;
                    }
                    if (Hidden(t.id, t.name)) continue;
                    Finish(t);
                    things.Add(t);
                }
            }

            static void Models(List<Thing> things)
            {
                var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Thing t in things) if (t.kind == "model" && t.state == "running" && !string.IsNullOrEmpty(t.name)) loaded.Add(t.name);
                var rest = new List<InstalledModel>();
                var items = ModelScan.Items;
                if (items != null)
                    foreach (InstalledModel m in items)
                    {
                        if (m == null || string.IsNullOrEmpty(m.Name) || loaded.Contains(m.Name)) continue;
                        rest.Add(m);
                    }
                if (rest.Count == 0) return;
                var tRest = new Thing();
                tRest.kind = "model";
                tRest.state = "idle";
                tRest.count = 1;
                if (rest.Count == 1)
                {
                    tRest.id = "model:" + Slug(rest[0].Name);
                    tRest.name = rest[0].Name;
                    tRest.folder = rest[0].Path;
                    tRest.why = rest[0].Name;
                    long bytes = rest[0].WeightBytes > 0 ? rest[0].WeightBytes : rest[0].Bytes;
                    if (bytes > 0) tRest.detail = SizeText(bytes);
                }
                else
                {
                    tRest.id = "models:rest";
                    tRest.name = (loaded.Count > 0 ? rest.Count.ToString(CultureInfo.InvariantCulture) + " more on disk" : rest.Count.ToString(CultureInfo.InvariantCulture) + " models on disk");
                    long bytes = 0;
                    var names = new StringBuilder();
                    foreach (InstalledModel m in rest)
                    {
                        bytes += m.WeightBytes > 0 ? m.WeightBytes : m.Bytes;
                        if (names.Length > 0) names.Append(" \u00B7 ");
                        names.Append(m.Name);
                        if (names.Length > 2000) break;
                    }
                    tRest.detail = SizeText(bytes);
                    tRest.why = names.ToString();
                }
                if (Hidden(tRest.id, tRest.name)) return;
                Finish(tRest);
                things.Add(tRest);
            }

            static void Heavy(List<Proc> procs, HashSet<int> used, List<Thing> things)
            {
                if (procs == null) return;
                var groups = new Dictionary<string, List<Proc>>(StringComparer.OrdinalIgnoreCase);
                foreach (Proc p in procs)
                {
                    if (p.Session == 0 || used.Contains(p.Pid)) continue;
                    Memo m = memo(p);
                    if (m != null && m.Yours) continue;
                    if (Infra(p.Exe) || Scripts.IsWindowsNoise(p.Exe, m == null ? null : m.Image)) continue;
                    string key = string.IsNullOrEmpty(p.Exe) ? "process" : p.Exe;
                    List<Proc> list;
                    if (!groups.TryGetValue(key, out list)) groups[key] = list = new List<Proc>();
                    list.Add(p);
                }
                foreach (var kv in groups)
                {
                    double cpu = 0; long ram = 0;
                    foreach (Proc p in kv.Value) { cpu += p.Cpu; ram += p.Private; }
                    if (cpu < 25 && ram < HeavyRam) continue;
                    var t = new Thing();
                    t.name = PrettyExe(kv.Key);
                    t.kind = "app";
                    t.state = "running";
                    t.id = "other:" + Slug(t.name);
                    t.count = kv.Value.Count;
                    t.cpu = cpu;
                    t.ram_mb = ram / 1048576.0;
                    t.detail = cpu.ToString("0", CultureInfo.InvariantCulture) + "% \u00B7 " + (t.ram_mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
                    t.why = "not yours";
                    if (Hidden(t.id, t.name)) continue;
                    Finish(t);
                    things.Add(t);
                }
            }

            static void Ended(List<Thing> things)
            {
                var now = DateTime.UtcNow;
                for (int i = exits.Count - 1; i >= 0; i--)
                    if ((now - exits[i].When).TotalHours > 1) exits.RemoveAt(i);
                foreach (ExitNote e in exits)
                {
                    var t = new Thing();
                    t.id = "ended:" + Slug(e.Name) + ":" + Slug(e.Project ?? "");
                    t.name = e.Name ?? "";
                    t.kind = string.IsNullOrEmpty(e.Kind) ? "script" : e.Kind;
                    t.state = e.Failed ? "failed" : "ended";
                    t.project = e.Project;
                    t.folder = e.Folder;
                    t.since = FormatSpan(now - e.When);
                    string code = e.Code < 0 ? "exit" : "exit " + e.Code.ToString(CultureInfo.InvariantCulture);
                    t.detail = code + " \u00B7 " + t.since + " ago";
                    t.why = e.Failed ? "exit was not success" : "exit 0";
                    if (Hidden(t.id, t.name)) continue;
                    Finish(t);
                    things.Add(t);
                }
            }

            static void Pins(List<Proc> procs, List<Thing> things)
            {
                foreach (Thing t in things)
                    if (!string.IsNullOrEmpty(t.id) && Prefs.PinIds.Contains(t.id)) t.pinned = true;
                if (procs == null) return;
                foreach (PinRule rule in Prefs.PinRules)
                {
                    if (rule == null) continue;
                    Thing hit = null;
                    foreach (Thing t in things)
                        if (string.Equals(t.id, rule.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(t.name, rule.Label, StringComparison.OrdinalIgnoreCase))
                            hit = t;
                    int n = 0;
                    double cpu = 0, ram = 0;
                    foreach (Proc p in procs)
                    {
                        Memo m = memo(p);
                        bool ok = false;
                        if (rule.ByExe) ok = string.Equals(p.Exe, rule.Match, StringComparison.OrdinalIgnoreCase);
                        else if (m != null && !string.IsNullOrEmpty(rule.Match) && !string.IsNullOrEmpty(m.Cmd))
                            ok = m.Cmd.IndexOf(rule.Match, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!ok) continue;
                        n++;
                        cpu += p.Cpu;
                        ram += p.Private / 1048576.0;
                    }
                    if (hit != null) { hit.pinned = true; continue; }
                    if (n == 0) continue;
                    var tNew = new Thing();
                    tNew.id = string.IsNullOrEmpty(rule.Id) ? "pin:" + rule.Label : rule.Id;
                    tNew.name = rule.Label ?? "";
                    tNew.kind = "app";
                    tNew.state = "running";
                    tNew.pinned = true;
                    tNew.count = n;
                    tNew.cpu = cpu;
                    tNew.ram_mb = ram;
                    tNew.why = "pinned";
                    if (Hidden(tNew.id, tNew.name)) continue;
                    Finish(tNew);
                    things.Add(tNew);
                }
            }

            static List<Group> Order(List<Thing> things)
            {
                var agents = new List<Thing>();
                var models = new List<Thing>();
                var other = new List<Thing>();
                var ended = new List<Thing>();
                var by = new Dictionary<string, List<Thing>>(StringComparer.OrdinalIgnoreCase);
                var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Thing t in things)
                {
                    if (t.kind == "agent") agents.Add(t);
                    else if (t.kind == "model") models.Add(t);
                    else if (t.id != null && t.id.StartsWith("other:", StringComparison.Ordinal)) other.Add(t);
                    else if (t.id != null && t.id.StartsWith("ended:", StringComparison.Ordinal)) ended.Add(t);
                    else if (t.kind == "app" && t.pinned && string.IsNullOrEmpty(t.project)) other.Add(t);
                    else
                    {
                        string title = GroupTitle(t);
                        string key = title.ToLowerInvariant();
                        List<Thing> list;
                        if (!by.TryGetValue(key, out list)) { by[key] = list = new List<Thing>(); titles[key] = title; }
                        list.Add(t);
                    }
                }
                agents.Sort(delegate(Thing a, Thing b)
                {
                    int c = Rank(b.state) - Rank(a.state);
                    if (c != 0) return c;
                    return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
                });
                var order = new List<Group>();
                AddGroup(order, "AGENTS", "agents", agents);
                var keys = new List<string>(by.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string key in keys)
                {
                    by[key].Sort(delegate(Thing a, Thing b) { return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase); });
                    AddGroup(order, titles[key], "project", by[key]);
                }
                AddGroup(order, "MODELS", "models", models);
                AddGroup(order, "OTHER", "other", other);
                AddGroup(order, "ENDED IN THE LAST HOUR", "ended", ended);
                return order;
            }

            static void AddGroup(List<Group> order, string title, string kind, List<Thing> things)
            {
                if (things == null || things.Count == 0) return;
                var g = new Group();
                g.title = Redact.Show(title ?? "");
                g.kind = kind;
                g.things = things;
                order.Add(g);
            }

            static string GroupTitle(Thing t)
            {
                if (!string.IsNullOrEmpty(t.folder))
                {
                    try
                    {
                        string dir = Directory.Exists(t.folder) ? t.folder : Path.GetDirectoryName(t.folder);
                        string name = string.IsNullOrEmpty(dir) ? "" : Path.GetFileName(dir.TrimEnd('\\'));
                        if (!string.IsNullOrEmpty(name)) return name;
                    }
                    catch (Exception) { }
                }
                if (!string.IsNullOrEmpty(t.project) && t.project != "task") return t.project;
                return "Scripts";
            }

            static void NoteExits(Dictionary<int, Proc> live, DateTime now)
            {
                var gone = new List<int>();
                foreach (var kv in memos)
                {
                    Proc p;
                    if (live.TryGetValue(kv.Key, out p) && p.CreateTime == kv.Value.Create) continue;
                    gone.Add(kv.Key);
                }
                foreach (int pid in gone)
                {
                    Memo m;
                    if (!memos.TryGetValue(pid, out m)) continue;
                    Forget(pid, m, now);
                }
            }

            static void Forget(int pid, Memo m, DateTime now)
            {
                if (m.Yours && !m.Plain && !string.IsNullOrEmpty(m.Leaf))
                {
                    int code = -1;
                    if (m.Handle != IntPtr.Zero)
                    {
                        uint c;
                        if (Native.GetExitCodeProcess(m.Handle, out c) && c != 259) code = unchecked((int)c);
                    }
                    var e = new ExitNote();
                    e.Name = m.Leaf;
                    e.Project = NameOf(m.ProjectFull);
                    e.Folder = FolderOf(m);
                    e.Kind = m.Kind;
                    e.Code = code;
                    e.Failed = code > 0;
                    e.When = now;
                    exits.Add(e);
                }
                if (m.Handle != IntPtr.Zero) Native.CloseHandle(m.Handle);
                memos.Remove(pid);
            }

            static Memo memo(Proc p)
            {
                Memo m;
                if (p == null || !memos.TryGetValue(p.Pid, out m) || m.Create != p.CreateTime) return null;
                return m;
            }

            static string IdOf(Memo m, Proc p, string kind, string name)
            {
                if (m.TaskHit && !string.IsNullOrEmpty(m.TaskPath)) return "task:" + m.TaskPath;
                if (kind == "model") return "model:" + Slug(m.Weight);
                if (!string.IsNullOrEmpty(m.Script)) return kind + ":" + m.Script;
                return kind + ":" + (p.Pid.ToString(CultureInfo.InvariantCulture));
            }

            static string ProjectLabel(Memo m, Proc p)
            {
                string repo = NameOf(m.ProjectFull);
                if (!string.IsNullOrEmpty(repo)) return repo;
                if (!string.IsNullOrEmpty(m.Script) || Scripts.IsInterpreter(p.Exe ?? "")) return Scripts.Language(p.Exe ?? "process");
                return null;
            }

            static string FolderOf(Memo m)
            {
                if (!string.IsNullOrEmpty(m.ProjectFull)) return m.ProjectFull;
                if (!string.IsNullOrEmpty(m.Script)) return m.Script.StartsWith("module:", StringComparison.Ordinal) ? Scripts.Folder(m.Script) : m.Script;
                return m.Cwd;
            }

            static string Why(Memo m, List<int> ports)
            {
                var bits = new List<string>();
                if (!string.IsNullOrEmpty(m.LaunchName)) bits.Add("launcher=" + m.LaunchName);
                else if (m.Orphan) bits.Add("parent exited");
                if (!string.IsNullOrEmpty(m.ProjectFull)) bits.Add("project=" + (m.Marker ?? "marker"));
                if (!string.IsNullOrEmpty(m.Weight)) bits.Add("weights " + m.Weight);
                if (ports != null && ports.Count > 0) bits.Add("listens " + ports[0].ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(m.RecentWrite)) bits.Add("wrote " + m.RecentWrite);
                return bits.Count == 0 ? "yours" : string.Join("; ", bits.ToArray());
            }

            static string LeafName(Proc p, Memo m)
            {
                if (!string.IsNullOrEmpty(m.TaskName)) return m.TaskName;
                string script = ScriptName(m.Script);
                if (!string.IsNullOrEmpty(script)) return script;
                return PrettyExe(p.Exe);
            }

            static string ScriptName(string script)
            {
                if (string.IsNullOrEmpty(script)) return null;
                if (script.StartsWith("module:", StringComparison.Ordinal))
                {
                    string rest = script.Substring(7);
                    int bar = rest.IndexOf('|');
                    return bar < 0 ? rest : rest.Substring(0, bar);
                }
                try { return Path.GetFileName(script); } catch (Exception) { return script; }
            }

            static string ScriptOf(Proc p, string cwd)
            {
                try
                {
                    var shadow = new Proc();
                    shadow.Exe = p.Exe;
                    shadow.Cmd = p.Cmd;
                    return Scripts.Find(shadow, delegate { return cwd; });
                }
                catch (Exception) { return null; }
            }

            static string ScriptFile(string script)
            {
                if (string.IsNullOrEmpty(script) || script.StartsWith("module:", StringComparison.Ordinal)) return null;
                return script;
            }

            static string AgentOf(string exe, string cmd)
            {
                string model = Arg(cmd, "--model");
                if (IsWeightPath(model)) model = null;
                if (!string.IsNullOrEmpty(model) && Has(cmd, "--worktree")) return "Cursor agent";
                var args = Split(cmd);
                for (int i = 1; i < args.Count; i++)
                {
                    string a = args[i];
                    string named = null;
                    if (a.StartsWith("--agent=", StringComparison.OrdinalIgnoreCase)) named = PrettyAgent(a.Substring(8));
                    else if (a.Equals("--agent", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) named = PrettyAgent(args[++i]);
                    else named = PrettyAgent(Path.GetFileNameWithoutExtension(a.Trim('"')));
                    if (named != null) return named;
                }
                string fromExe = PrettyAgent(Path.GetFileNameWithoutExtension(exe ?? ""));
                if (fromExe != null) return fromExe;
                if (!string.IsNullOrEmpty(model)) return Path.GetFileNameWithoutExtension(exe ?? "node");
                return null;
            }

            static string PrettyAgent(string token)
            {
                if (string.IsNullOrEmpty(token)) return null;
                if (token.Equals("claude", StringComparison.OrdinalIgnoreCase) || token.Equals("claude-code", StringComparison.OrdinalIgnoreCase)) return "Claude Code";
                if (token.Equals("codex", StringComparison.OrdinalIgnoreCase)) return "Codex";
                if (token.Equals("cursor-agent", StringComparison.OrdinalIgnoreCase)) return "Cursor agent";
                return null;
            }

            static bool IsWeightPath(string path)
            {
                if (string.IsNullOrEmpty(path)) return false;
                string ext = "";
                try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch (Exception) { return false; }
                return ext == ".gguf" || ext == ".safetensors" || ext == ".bin" || ext == ".pt" || ext == ".pth";
            }

            static string WeightArg(string cmd)
            {
                if (string.IsNullOrEmpty(cmd)) return null;
                var args = Split(cmd);
                for (int i = 1; i < args.Count; i++)
                {
                    string a = args[i];
                    string path = null;
                    if (a.StartsWith("--model=", StringComparison.OrdinalIgnoreCase)) path = a.Substring(8).Trim('"');
                    else if (a.Equals("--model", StringComparison.OrdinalIgnoreCase) || a.Equals("-m", StringComparison.OrdinalIgnoreCase) || a.Equals("--model-path", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 >= args.Count) continue;
                        path = args[++i].Trim('"');
                    }
                    if (!IsWeightPath(path)) continue;
                    return Path.GetFileNameWithoutExtension(Shard.Replace(Path.GetFileName(path), ""));
                }
                return null;
            }

            static Act LogOf(Memo m, Proc p)
            {
                try
                {
                    string home = AgentHome();
                    if (m.Agent == "Claude Code") Claude(home, m, p);
                    else if (m.Agent == "Codex") Codex(home, m, p);
                    else if (m.Agent == "Cursor agent") Cursor(home, m, p);
                    if (m.Action != null && m.AgentModel == null && m.Action.Model != null) m.AgentModel = m.Action.Model;
                    if (m.AgentModel == null) m.AgentModel = Arg(m.Cmd, "--model");
                    if (IsWeightPath(m.AgentModel)) m.AgentModel = null;
                }
                catch (Exception) { }
                return m.Action;
            }

            static string AgentHome()
            {
                string h = Environment.GetEnvironmentVariable("DESKTOPVITALS_HOME");
                if (!string.IsNullOrEmpty(h)) return h;
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            static void Claude(string home, Memo m, Proc p)
            {
                string file = m.LogFile;
                if (string.IsNullOrEmpty(file))
                {
                    string reg = Path.Combine(home, ".claude", "sessions", p.Pid.ToString(CultureInfo.InvariantCulture) + ".json");
                    if (File.Exists(reg))
                    {
                        string text = ReadText(reg, 8000);
                        string procStart = JsonString(text, "\"procStart\":\"", 0);
                        long ps;
                        if (!string.IsNullOrEmpty(procStart) && long.TryParse(procStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out ps))
                        {
                            long d = ps - p.CreateTime;
                            if (d < 0) d = -d;
                            if (d > 20000000) text = null;
                        }
                        if (text != null)
                        {
                            string sid = JsonString(text, "\"sessionId\":\"", 0);
                            string cwd = JsonString(text, "\"cwd\":\"", 0);
                            if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(cwd))
                                file = Path.Combine(home, ".claude", "projects", Encode(cwd), sid + ".jsonl");
                        }
                    }
                    if (string.IsNullOrEmpty(file) || !File.Exists(file)) file = ClaudeByCwd(home, m.Cwd, p.CreateTime);
                    m.LogFile = file ?? "";
                }
                if (string.IsNullOrEmpty(m.LogFile) || !File.Exists(m.LogFile)) return;
                Act act = TailFile(m.LogFile, true);
                if (act != null) m.Action = act;
            }

            static string ClaudeByCwd(string home, string cwd, long create)
            {
                if (string.IsNullOrEmpty(cwd)) return null;
                string dir = Path.Combine(home, ".claude", "projects", Encode(cwd));
                if (!Directory.Exists(dir)) return null;
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                string[] files;
                try { files = Directory.GetFiles(dir, "*.jsonl"); } catch (Exception) { return null; }
                foreach (string f in files)
                {
                    string head = ReadText(f, 1500);
                    string got = JsonString(head, "\"cwd\":\"", 0);
                    if (!string.IsNullOrEmpty(got) && !string.Equals(got, cwd, StringComparison.OrdinalIgnoreCase)) continue;
                    DateTime w;
                    try { w = File.GetLastWriteTimeUtc(f); } catch (Exception) { continue; }
                    if (w < DateTime.FromFileTimeUtc(create).AddMinutes(-1)) continue;
                    if (w >= bestTime) { bestTime = w; best = f; }
                }
                return best;
            }

            static void Codex(string home, Memo m, Proc p)
            {
                if (string.IsNullOrEmpty(m.Cwd)) return;
                string file = m.LogFile;
                if (string.IsNullOrEmpty(file))
                {
                    file = CodexFile(home, m.Cwd, p.CreateTime);
                    m.LogFile = file ?? "";
                }
                if (string.IsNullOrEmpty(m.LogFile) || !File.Exists(m.LogFile)) return;
                Act act = TailFile(m.LogFile, false);
                if (act != null) m.Action = act;
            }

            static string CodexFile(string home, string cwd, long create)
            {
                string root = Path.Combine(home, ".codex", "sessions");
                if (!Directory.Exists(root)) return null;
                string[] files;
                try { files = Directory.GetFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories); } catch (Exception) { return null; }
                string best = null;
                long bestSkew = long.MaxValue;
                DateTime start = DateTime.FromFileTimeUtc(create);
                foreach (string path in files)
                {
                    DateTime born = CodexBorn(path);
                    if (born == DateTime.MinValue) continue;
                    if (born < start.AddSeconds(-5) || born > start.AddMinutes(5)) continue;
                    string got = JsonString(ReadText(path, 600), "\"cwd\":\"", 0);
                    if (!string.Equals(got, cwd, StringComparison.OrdinalIgnoreCase)) continue;
                    long skew = (long)Math.Abs((born - start).TotalSeconds);
                    if (skew < bestSkew) { bestSkew = skew; best = path; }
                }
                return best;
            }

            static DateTime CodexBorn(string path)
            {
                string name = Path.GetFileName(path);
                if (name == null || name.Length < 27 || !name.StartsWith("rollout-", StringComparison.Ordinal)) return DateTime.MinValue;
                DateTime dt;
                if (DateTime.TryParseExact(name.Substring(8, 19), "yyyy-MM-ddTHH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
                    return dt.ToUniversalTime();
                return DateTime.MinValue;
            }

            static void Cursor(string home, Memo m, Proc p)
            {
                if (string.IsNullOrEmpty(m.Cwd)) return;
                string db = m.LogFile;
                if (string.IsNullOrEmpty(db))
                {
                    db = CursorDb(home, m.Cwd, p.CreateTime);
                    m.LogFile = db ?? "";
                }
                if (string.IsNullOrEmpty(m.LogFile) || !File.Exists(m.LogFile)) return;
                Act act = CursorAct(m.LogFile);
                if (act != null) m.Action = act;
            }

            static string CursorDb(string home, string cwd, long create)
            {
                string dir = Path.Combine(home, ".cursor", "chats", Md5Hex(cwd));
                if (!Directory.Exists(dir)) return null;
                string[] chats;
                try { chats = Directory.GetDirectories(dir); } catch (Exception) { return null; }
                string best = null;
                long bestSkew = long.MaxValue;
                long startMs = (long)(DateTime.FromFileTimeUtc(create) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                foreach (string chat in chats)
                {
                    string meta = ReadText(Path.Combine(chat, "meta.json"), 4000);
                    if (string.IsNullOrEmpty(meta)) continue;
                    string got = JsonString(meta, "\"cwd\":\"", 0);
                    if (!string.Equals(got, cwd, StringComparison.OrdinalIgnoreCase)) continue;
                    long cms = JsonLong(meta, "\"createdAtMs\":");
                    if (cms == 0) continue;
                    long skew = cms - startMs;
                    if (skew < 0) skew = -skew;
                    if (skew < bestSkew) { bestSkew = skew; best = Path.Combine(chat, "store.db"); }
                }
                return best;
            }

            static Act TailFile(string path, bool claude)
            {
                Tail tail;
                if (!tails.TryGetValue(path, out tail)) tails[path] = tail = new Tail();
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long end = fs.Length;
                        long start = tail.Offset;
                        if (end < start) start = 0;
                        if (end - start > TailCap) start = end - TailCap;
                        if (start < 0) start = 0;
                        fs.Seek(start, SeekOrigin.Begin);
                        int n = (int)(end - start);
                        if (n <= 0) return tail.Last;
                        byte[] buf = new byte[n];
                        int got = 0;
                        while (got < n)
                        {
                            int r = fs.Read(buf, got, n - got);
                            if (r <= 0) break;
                            got += r;
                        }
                        int i = 0;
                        if (start > tail.Offset)
                        {
                            while (i < got && buf[i] != (byte)'\n') i++;
                            if (i < got) i++;
                        }
                        var line = new StringBuilder();
                        for (; i < got; i++)
                        {
                            if (buf[i] == (byte)'\n')
                            {
                                Act hit = claude ? ParseClaude(line.ToString()) : ParseCodex(line.ToString());
                                if (hit != null && hit.Kind != null) tail.Last = hit;
                                else if (hit != null && hit.Model != null) tail.Model = hit.Model;
                                line.Length = 0;
                                continue;
                            }
                            if (buf[i] != (byte)'\r' && line.Length < 6000) line.Append((char)buf[i]);
                        }
                        tail.Offset = end;
                    }
                }
                catch (Exception) { }
                if (tail.Last != null && tail.Last.Model == null && tail.Model != null) tail.Last.Model = tail.Model;
                return tail.Last;
            }

            static Act ParseClaude(string line)
            {
                if (string.IsNullOrEmpty(line)) return null;
                var hit = new Act();
                hit.When = Stamp(JsonString(line, "\"timestamp\":\"", 0));
                hit.Model = JsonString(line, "\"requestedModel\":\"", 0);
                bool tool = line.IndexOf("\"type\":\"tool_use\"", StringComparison.Ordinal) >= 0;
                bool result = line.IndexOf("\"type\":\"tool_result\"", StringComparison.Ordinal) >= 0;
                bool thinking = line.IndexOf("\"type\":\"thinking\"", StringComparison.Ordinal) >= 0;
                bool text = line.IndexOf("\"type\":\"text\"", StringComparison.Ordinal) >= 0;
                bool endTurn = line.IndexOf("\"stop_reason\":\"end_turn\"", StringComparison.Ordinal) >= 0;
                if (result && !tool) { hit.Kind = "tool"; hit.Open = false; return hit; }
                if (tool)
                {
                    string name = JsonString(line, "\"name\":\"", Math.Max(0, line.IndexOf("\"type\":\"tool_use\"", StringComparison.Ordinal)));
                    hit.Kind = KindFor(name);
                    hit.File = FileFrom(line);
                    hit.Open = true;
                    return hit;
                }
                if (thinking && !text) { hit.Kind = "thinking"; return hit; }
                if (text && endTurn) { hit.Kind = "answering"; return hit; }
                return hit.Model == null ? null : hit;
            }

            static Act ParseCodex(string line)
            {
                if (string.IsNullOrEmpty(line)) return null;
                var hit = new Act();
                hit.When = Stamp(JsonString(line, "\"timestamp\":\"", 0));
                if (line.IndexOf("\"type\":\"turn_context\"", StringComparison.Ordinal) >= 0)
                    hit.Model = JsonString(line, "\"model\":\"", 0);
                if (line.IndexOf("\"type\":\"FileChange\"", StringComparison.Ordinal) >= 0)
                {
                    hit.Kind = "editing";
                    int at = line.IndexOf("\"changes\":{", StringComparison.Ordinal);
                    if (at >= 0) hit.File = BaseName(JsonString(line, "\"", at + "\"changes\":{".Length));
                    return hit;
                }
                if (line.IndexOf("\"type\":\"CommandExecution\"", StringComparison.Ordinal) >= 0) { hit.Kind = "shell"; return hit; }
                if (line.IndexOf("\"type\":\"Reasoning\"", StringComparison.Ordinal) >= 0) { hit.Kind = "thinking"; return hit; }
                if (line.IndexOf("\"type\":\"AgentMessage\"", StringComparison.Ordinal) >= 0) { hit.Kind = "answering"; return hit; }
                return hit.Model == null ? null : hit;
            }

            static Act CursorAct(string db)
            {
                IntPtr conn;
                string uri = "file:" + db.Replace('\\', '/') + "?mode=ro";
                try
                {
                    if (Native.sqlite3_open_v2(uri, out conn, Native.SqliteOpenReadonly | Native.SqliteOpenUri, IntPtr.Zero) != 0) return null;
                }
                catch (Exception) { return null; }
                try
                {
                    Native.sqlite3_busy_timeout(conn, 0);
                    byte[] meta = SqlBlob(conn, "SELECT value FROM meta WHERE key='0'", null);
                    if (meta == null) return null;
                    string root = HexJson(meta, "latestRootBlobId");
                    if (string.IsNullOrEmpty(root)) return null;
                    byte[] blob = SqlBlob(conn, "SELECT data FROM blobs WHERE id=?", root);
                    if (blob == null) return null;
                    var ids = new List<string>();
                    for (int i = 0; i + 34 <= blob.Length; i++)
                    {
                        if (blob[i] != 0x0a || blob[i + 1] != 0x20) continue;
                        ids.Add(Hex(blob, i + 2, 32));
                        i += 33;
                    }
                    for (int n = ids.Count - 1; n >= 0 && n >= ids.Count - 12; n--)
                    {
                        byte[] kind = SqlBlob(conn, "SELECT substr(data,1,1) FROM blobs WHERE id=?", ids[n]);
                        if (kind == null || kind.Length == 0 || kind[0] != (byte)'{') continue;
                        byte[] body = SqlBlob(conn, "SELECT substr(data,1,4096) FROM blobs WHERE id=?", ids[n]);
                        if (body == null || body.Length == 0) continue;
                        string head = Encoding.UTF8.GetString(body);
                        string end = "";
                        if (head.IndexOf("\"path\":\"", StringComparison.Ordinal) < 0)
                        {
                            byte[] tail = SqlBlob(conn, "SELECT substr(data, max(1, length(data)-1023), 1024) FROM blobs WHERE id=?", ids[n]);
                            if (tail != null) end = Encoding.UTF8.GetString(tail);
                        }
                        Act hit = ParseCursor(head, end);
                        if (hit != null && hit.Kind != null)
                        {
                            try { hit.When = File.GetLastWriteTimeUtc(db); } catch (Exception) { hit.When = DateTime.UtcNow; }
                            return hit;
                        }
                    }
                }
                catch (Exception) { }
                finally { try { Native.sqlite3_close(conn); } catch (Exception) { } }
                return null;
            }

            static Act ParseCursor(string head, string end)
            {
                var hit = new Act();
                bool call = head.IndexOf("\"type\":\"tool-call\"", StringComparison.Ordinal) >= 0;
                bool result = head.IndexOf("\"type\":\"tool-result\"", StringComparison.Ordinal) >= 0;
                if (call || result)
                {
                    hit.Kind = KindFor(JsonString(head, "\"toolName\":\"", 0));
                    hit.File = FileFrom(head + end);
                    hit.Open = call && !result;
                    return hit;
                }
                if (head.IndexOf("\"type\":\"reasoning\"", StringComparison.Ordinal) >= 0) { hit.Kind = "thinking"; return hit; }
                if (head.IndexOf("\"type\":\"text\"", StringComparison.Ordinal) >= 0 && JsonString(head, "\"role\":\"", 0) == "assistant") { hit.Kind = "answering"; return hit; }
                return null;
            }

            static string KindFor(string name)
            {
                if (name == "Edit" || name == "Write" || name == "StrReplace" || name == "NotebookEdit") return "editing";
                if (name == "Read" || name == "Grep" || name == "Glob") return "reading";
                if (name == "Bash" || name == "PowerShell" || name == "Shell") return "shell";
                return "tool";
            }

            static GitSnap GitOf(Memo m)
            {
                if (string.IsNullOrEmpty(m.Cwd)) return m.Git;
                string marker;
                string top = FindProject(m.Cwd, out marker);
                if (string.IsNullOrEmpty(top)) return null;
                string gitPath = Path.Combine(top, ".git");
                string gitDir = gitPath;
                string main = top;
                if (File.Exists(gitPath))
                {
                    string gd = ReadGitdir(gitPath);
                    if (!string.IsNullOrEmpty(gd))
                    {
                        gitDir = gd;
                        string common = CommonDir(gd);
                        if (!string.IsNullOrEmpty(common))
                        {
                            string parent = Path.GetDirectoryName(common.TrimEnd('\\'));
                            if (!string.IsNullOrEmpty(parent)) main = parent;
                        }
                        else
                        {
                            string guess = MainFromGitdir(gd);
                            if (!string.IsNullOrEmpty(guess)) main = guess;
                        }
                    }
                }
                string head = ReadText(Path.Combine(gitDir, "HEAD"), 200);
                long stamp = 0;
                try { stamp = File.GetLastWriteTimeUtc(Path.Combine(gitDir, "index")).Ticks; } catch (Exception) { }
                if (m.Git != null && m.GitStamp == stamp && m.GitHead == (head ?? "")) return m.Git;
                var g = new GitSnap();
                g.Main = main;
                string mainName = NameOf(main);
                string wt = NameOf(top);
                if (!string.IsNullOrEmpty(wt) && !string.Equals(wt, mainName, StringComparison.OrdinalIgnoreCase)) g.Worktree = wt;
                g.Branch = BranchOf(head, gitDir);
                g.Commits = CountReflog(Path.Combine(gitDir, "logs", "HEAD"), m.Create);
                g.Changed = CountIndex(Path.Combine(gitDir, "index"), m.Create);
                m.Git = g;
                m.GitStamp = stamp;
                m.GitHead = head ?? "";
                return g;
            }

            static string ReadGitdir(string gitFile)
            {
                string line = ReadText(gitFile, 500);
                if (string.IsNullOrEmpty(line)) return null;
                int i = line.IndexOf("gitdir:", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return null;
                return line.Substring(i + 7).Trim().Replace('/', '\\');
            }

            static string CommonDir(string gitDir)
            {
                string line = ReadText(Path.Combine(gitDir, "commondir"), 500);
                if (string.IsNullOrEmpty(line)) return null;
                line = line.Trim().Replace('/', '\\');
                if (!Path.IsPathRooted(line))
                {
                    try { line = Path.GetFullPath(Path.Combine(gitDir, line)); } catch (Exception) { return null; }
                }
                return line;
            }

            static string MainFromGitdir(string gitDir)
            {
                int mark = gitDir.IndexOf("\\.git\\", StringComparison.OrdinalIgnoreCase);
                if (mark < 0 && gitDir.EndsWith("\\.git", StringComparison.OrdinalIgnoreCase)) mark = gitDir.Length - 5;
                if (mark < 0) return null;
                return gitDir.Substring(0, mark);
            }

            static string BranchOf(string head, string gitDir)
            {
                if (string.IsNullOrEmpty(head)) return null;
                head = head.Trim();
                const string pref = "ref:";
                if (head.StartsWith(pref, StringComparison.OrdinalIgnoreCase))
                {
                    string r = head.Substring(4).Trim().Replace('/', '\\');
                    ResolveRef(gitDir, r);
                    int slash = r.LastIndexOf('\\');
                    string name = slash >= 0 ? r.Substring(slash + 1) : r;
                    return name.Length == 0 ? null : name;
                }
                return null;
            }

            // Confirm the branch against refs/heads and packed-refs. The name still comes from HEAD.
            static void ResolveRef(string gitDir, string rel)
            {
                if (string.IsNullOrEmpty(gitDir) || string.IsNullOrEmpty(rel)) return;
                string text = ReadText(Path.Combine(gitDir, rel), 80);
                if (!string.IsNullOrEmpty(text) && text.Trim().Length >= 4) return;
                string packed = ReadText(Path.Combine(gitDir, "packed-refs"), 65536);
                if (string.IsNullOrEmpty(packed)) return;
                packed.IndexOf(" " + rel.Replace('\\', '/'), StringComparison.Ordinal);
            }

            static int CountReflog(string path, long create)
            {
                if (!File.Exists(path)) return 0;
                long start = 0;
                try { start = (long)(DateTime.FromFileTimeUtc(create) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
                catch (Exception) { return 0; }
                int n = 0;
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null && n < 5000)
                        {
                            int tab = line.IndexOf('\t');
                            string left = tab < 0 ? line : line.Substring(0, tab);
                            string[] parts = left.Split(' ');
                            if (parts.Length < 2) continue;
                            long ts;
                            if (!long.TryParse(parts[parts.Length - 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ts)) continue;
                            if (ts >= start) n++;
                        }
                    }
                }
                catch (Exception) { }
                return n;
            }

            static int CountIndex(string path, long create)
            {
                if (!File.Exists(path)) return 0;
                long start = 0;
                try { start = (long)(DateTime.FromFileTimeUtc(create) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
                catch (Exception) { return 0; }
                try
                {
                    byte[] buf = File.ReadAllBytes(path);
                    if (buf.Length < 12 || buf[0] != (byte)'D' || buf[1] != (byte)'I') return 0;
                    int count = Be32(buf, 8);
                    int at = 12;
                    int changed = 0;
                    for (int i = 0; i < count && at + 62 < buf.Length; i++)
                    {
                        int startAt = at;
                        long mtime = Be32(buf, at + 8);
                        int flags = (buf[at + 60] << 8) | buf[at + 61];
                        int nameLen = flags & 0xFFF;
                        at += 62;
                        if (nameLen == 0 || at + nameLen > buf.Length) break;
                        at += nameLen;
                        int span = at - startAt;
                        int pad = 8 - (span % 8);
                        if (pad == 0) pad = 8;
                        at += pad;
                        if (mtime >= start) changed++;
                    }
                    return changed;
                }
                catch (Exception) { return 0; }
            }

            static int Be32(byte[] b, int o)
            {
                return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
            }

            static string Weights(int pid)
            {
                IntPtr h = Native.OpenProcess(Native.QueryInfo | Native.VmRead, false, pid);
                if (h == IntPtr.Zero) return null;
                IntPtr info = Marshal.AllocHGlobal(48);
                try
                {
                    if (dos == null) dos = DosMap();
                    var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    var seen = new HashSet<long>();
                    long addr = 0;
                    int steps = 0;
                    var sb = new StringBuilder(520);
                    while (addr >= 0 && addr < 0x00007FFFFFFFFFFF && steps < 80000)
                    {
                        for (int i = 0; i < 48; i++) System.Runtime.InteropServices.Marshal.WriteByte(info, i, 0);
                        if (Native.VirtualQueryEx(h, new IntPtr(addr), info, new IntPtr(48)) == IntPtr.Zero) break;
                        long size = System.Runtime.InteropServices.Marshal.ReadIntPtr(info, 24).ToInt64();
                        uint state = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(info, 32);
                        uint type = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(info, 40);
                        long alloc = System.Runtime.InteropServices.Marshal.ReadIntPtr(info, 8).ToInt64();
                        if (size <= 0) break;
                        steps++;
                        if (state == 0x1000 && type == 0x40000 && seen.Add(alloc))
                        {
                            sb.Length = 0;
                            if (Native.GetMappedFileName(h, new IntPtr(addr), sb, sb.Capacity) > 0)
                            {
                                string path = ToDos(sb.ToString());
                                long have;
                                files.TryGetValue(path, out have);
                                files[path] = have + size;
                            }
                        }
                        long next = addr + size;
                        if (next <= addr) break;
                        addr = next;
                    }
                    string best = null;
                    long bestBytes = 0;
                    var names = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in files)
                    {
                        if (!IsWeightFile(kv.Key, kv.Value)) continue;
                        long bytes = kv.Value;
                        try { if (File.Exists(kv.Key)) bytes = new FileInfo(kv.Key).Length; } catch (Exception) { }
                        string key = Shard.Replace(Path.GetFileName(kv.Key), "");
                        string name = Path.GetFileNameWithoutExtension(key);
                        long have;
                        names.TryGetValue(name, out have);
                        names[name] = have + bytes;
                        if (have + bytes > bestBytes) { bestBytes = have + bytes; best = name; }
                    }
                    return best;
                }
                catch (Exception) { return null; }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(info); Native.CloseHandle(h); }
            }

            static bool IsWeightFile(string path, long bytes)
            {
                string ext = "";
                try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch (Exception) { return false; }
                if (ext == ".gguf" || ext == ".safetensors") return true;
                if (ext == ".bin" || ext == ".pt" || ext == ".pth") return bytes >= (20L << 20);
                return false;
            }

            static bool SkipMap(string exe)
            {
                string b = Base(exe);
                return b == "cmd" || b == "conhost" || b == "openconsole" || b == "nohup" || b == "ping" || b == "svchost" || b == "sihost" || b == "explorer" || b == "dllhost" || b == "runtimebroker";
            }

            static Dictionary<string, string> DosMap()
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var buf = new char[1024];
                try
                {
                    foreach (var di in DriveInfo.GetDrives())
                    {
                        string letter = di.Name.TrimEnd('\\');
                        uint n = Native.QueryDosDevice(letter, buf, (uint)buf.Length);
                        if (n == 0) continue;
                        int z = Array.IndexOf(buf, '\0');
                        if (z <= 0) continue;
                        map[new string(buf, 0, z)] = letter;
                    }
                }
                catch (Exception) { }
                return map;
            }

            static string ToDos(string nt)
            {
                if (string.IsNullOrEmpty(nt) || dos == null) return nt;
                foreach (var kv in dos)
                    if (nt.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                        return kv.Value + nt.Substring(kv.Key.Length);
                return nt;
            }

            static Dictionary<int, List<int>> Listeners()
            {
                var map = new Dictionary<int, List<int>>();
                ReadPorts(2, 24, 8, 20, map);
                ReadPorts(23, 56, 20, 52, map);
                return map;
            }

            static void ReadPorts(int af, int row, int portOff, int pidOff, Dictionary<int, List<int>> map)
            {
                int len = 0;
                Native.GetExtendedTcpTable(IntPtr.Zero, ref len, false, af, 3, 0);
                if (len <= 0) return;
                IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(len);
                try
                {
                    if (Native.GetExtendedTcpTable(buf, ref len, false, af, 3, 0) != 0) return;
                    int n = System.Runtime.InteropServices.Marshal.ReadInt32(buf);
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr r = buf + 4 + i * row;
                        int raw = System.Runtime.InteropServices.Marshal.ReadInt32(r, portOff);
                        int port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                        int pid = System.Runtime.InteropServices.Marshal.ReadInt32(r, pidOff);
                        if (port <= 0 || pid <= 0) continue;
                        List<int> have;
                        if (!map.TryGetValue(pid, out have)) map[pid] = have = new List<int>();
                        if (!have.Contains(port)) have.Add(port);
                    }
                }
                catch (Exception) { }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
            }

            static List<string> StartupExes()
            {
                var list = new List<string>();
                try
                {
                    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    foreach (string dir in new[] {
                        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
                    {
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                        foreach (string f in Directory.GetFiles(dir))
                        {
                            if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                            {
                                try { list.Add((string)shell.CreateShortcut(f).TargetPath); } catch (Exception) { }
                            }
                            else list.Add(f);
                        }
                    }
                }
                catch (Exception) { }
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    {
                        if (k != null)
                            foreach (string name in k.GetValueNames())
                            {
                                string cmd = Environment.ExpandEnvironmentVariables(k.GetValue(name) as string ?? "");
                                string exe = ExeOf(cmd);
                                if (exe.Length > 0) list.Add(exe);
                            }
                    }
                }
                catch (Exception) { }
                return list;
            }

            static string ExeOf(string cmd)
            {
                cmd = (cmd ?? "").Trim();
                if (cmd.StartsWith("\"")) { int end = cmd.IndexOf('"', 1); return end > 1 ? cmd.Substring(1, end - 1) : ""; }
                int at = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                return at > 0 ? cmd.Substring(0, at + 4) : "";
            }

            static string RecentWrite(Proc p, string dir)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
                if (UnderWindows(dir) || Installed(dir)) return null;
                DateTime born = p.CreateTime > 0 ? DateTime.FromFileTime(p.CreateTime) : DateTime.MinValue;
                DateTime best = DateTime.MinValue;
                string name = null;
                try
                {
                    int n = 0;
                    foreach (string f in Directory.EnumerateFiles(dir))
                    {
                        if (++n > 40) break;
                        DateTime w;
                        try { w = File.GetLastWriteTime(f); } catch (Exception) { continue; }
                        if (w > born && w > best && (DateTime.Now - w).TotalSeconds <= 90) { best = w; name = Path.GetFileName(f); }
                    }
                }
                catch (Exception) { return null; }
                return name;
            }

            static byte[] SqlBlob(IntPtr conn, string sql, string arg)
            {
                byte[] sqlb = Encoding.UTF8.GetBytes(sql + "\0");
                IntPtr stmt;
                if (Native.sqlite3_prepare_v2(conn, sqlb, sqlb.Length, out stmt, IntPtr.Zero) != 0) return null;
                try
                {
                    if (arg != null)
                    {
                        byte[] ab = Encoding.UTF8.GetBytes(arg);
                        Native.sqlite3_bind_text(stmt, 1, ab, ab.Length, new IntPtr(-1));
                    }
                    if (Native.sqlite3_step(stmt) != Native.SqliteRow) return null;
                    IntPtr col = Native.sqlite3_column_blob(stmt, 0);
                    int n = Native.sqlite3_column_bytes(stmt, 0);
                    if (col == IntPtr.Zero || n <= 0) return null;
                    int copy = n > 65536 ? 65536 : n;
                    byte[] buf = new byte[copy];
                    System.Runtime.InteropServices.Marshal.Copy(col, buf, 0, copy);
                    return buf;
                }
                finally { Native.sqlite3_finalize(stmt); }
            }

            static string HexJson(byte[] hexText, string key)
            {
                int n = hexText.Length / 2;
                if (n <= 0 || n > 65536) return null;
                byte[] raw = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    int hi = HexVal(hexText[i * 2]);
                    int lo = HexVal(hexText[i * 2 + 1]);
                    if (hi < 0 || lo < 0) return null;
                    raw[i] = (byte)((hi << 4) | lo);
                }
                return JsonString(Encoding.UTF8.GetString(raw), "\"" + key + "\":\"", 0);
            }

            static int HexVal(byte c)
            {
                if (c >= (byte)'0' && c <= (byte)'9') return c - (byte)'0';
                if (c >= (byte)'a' && c <= (byte)'f') return c - (byte)'a' + 10;
                if (c >= (byte)'A' && c <= (byte)'F') return c - (byte)'A' + 10;
                return -1;
            }

            static string Hex(byte[] b, int o, int n)
            {
                var sb = new StringBuilder(n * 2);
                for (int i = 0; i < n; i++) sb.Append(b[o + i].ToString("x2"));
                return sb.ToString();
            }

            static string Encode(string cwd)
            {
                var sb = new StringBuilder(cwd.Length);
                for (int i = 0; i < cwd.Length; i++)
                {
                    char c = cwd[i];
                    sb.Append(char.IsLetterOrDigit(c) ? c : '-');
                }
                return sb.ToString();
            }

            static string Md5Hex(string s)
            {
                using (MD5 md = MD5.Create())
                {
                    byte[] hash = md.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                    var sb = new StringBuilder(32);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }

            static string JsonString(string hay, string key, int from)
            {
                if (string.IsNullOrEmpty(hay) || from < 0) return null;
                int i = hay.IndexOf(key, from, StringComparison.Ordinal);
                if (i < 0) return null;
                i += key.Length;
                var sb = new StringBuilder();
                for (int n = 0; i < hay.Length && n < 400; i++, n++)
                {
                    char c = hay[i];
                    if (c == '"') break;
                    if (c == '\\' && i + 1 < hay.Length) { sb.Append(hay[i + 1]); i++; continue; }
                    sb.Append(c);
                }
                return sb.Length == 0 ? null : sb.ToString();
            }

            static long JsonLong(string hay, string key)
            {
                if (string.IsNullOrEmpty(hay)) return 0;
                int i = hay.IndexOf(key, StringComparison.Ordinal);
                if (i < 0) return 0;
                i += key.Length;
                while (i < hay.Length && (hay[i] == ' ' || hay[i] == '\t')) i++;
                long n = 0;
                int digits = 0;
                while (i < hay.Length && hay[i] >= '0' && hay[i] <= '9' && digits < 18)
                {
                    n = n * 10 + (hay[i] - '0');
                    i++;
                    digits++;
                }
                return digits == 0 ? 0 : n;
            }

            static string FileFrom(string blob)
            {
                string p = JsonString(blob, "\"file_path\":\"", 0);
                if (p == null) p = JsonString(blob, "\"path\":\"", 0);
                if (p == null) return null;
                return BaseName(p);
            }

            static string BaseName(string path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                path = path.Replace('/', '\\');
                int slash = path.LastIndexOf('\\');
                string name = slash >= 0 ? path.Substring(slash + 1) : path;
                if (name.Length == 0 || name.Length > 120) return null;
                return name;
            }

            static DateTime Stamp(string s)
            {
                if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
                DateTime dt;
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                    return dt.ToUniversalTime();
                return DateTime.MinValue;
            }

            static string Arg(string cmd, string name)
            {
                if (string.IsNullOrEmpty(cmd)) return null;
                var parts = Split(cmd);
                string eq = name + "=";
                for (int i = 0; i < parts.Count; i++)
                {
                    if (parts[i].StartsWith(eq, StringComparison.Ordinal)) return parts[i].Substring(eq.Length);
                    if (string.Equals(parts[i], name, StringComparison.Ordinal) && i + 1 < parts.Count) return parts[i + 1];
                }
                return null;
            }

            static bool Has(string cmd, string name)
            {
                return Arg(cmd, name) != null || (cmd != null && cmd.IndexOf(name, StringComparison.Ordinal) >= 0 && Split(cmd).Contains(name));
            }

            static List<string> Split(string cmd)
            {
                return Scripts.SplitArgs(cmd ?? "");
            }

            static bool CmdHas(string cmd, string path)
            {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(cmd)) return false;
                string name = Path.GetFileName(path);
                return name.Length >= 5 && cmd.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            static bool SamePath(string a, string b)
            {
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
                try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
                catch (Exception) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
            }

            static bool AtOrUnder(string path, string root)
            {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
                try { path = Path.GetFullPath(path); root = Path.GetFullPath(root); } catch (Exception) { }
                path = path.TrimEnd('\\');
                root = root.TrimEnd('\\');
                return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
            }

            static bool UnderWindows(string path)
            {
                return AtOrUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            }

            static bool WindowsComponent(string path)
            {
                if (!UnderWindows(path)) return false;
                return path == null || path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) < 0;
            }

            static bool Installed(string path)
            {
                if (string.IsNullOrEmpty(path)) return false;
                if (UnderWindows(path) && WindowsComponent(path)) return true;
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string programs = string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Programs");
                if (AtOrUnder(path, pf) || AtOrUnder(path, pf86) || AtOrUnder(path, programs)) return true;
                return path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            static bool AppImage(Memo m)
            {
                return Installed(m.Image) || (UnderWindows(m.Image) && WindowsComponent(m.Image));
            }

            static bool UnderUser(string path)
            {
                return AtOrUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }

            static string LeafChild(int pid, Dictionary<int, Proc> byPid)
            {
                string leaf = null;
                var stack = new List<int>();
                stack.Add(pid);
                var seen = new HashSet<int>();
                while (stack.Count > 0)
                {
                    int id = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    foreach (var kv in byPid)
                    {
                        Proc c = kv.Value;
                        if (c.Parent != id || c.Pid == pid || c.CreateTime <= 0) continue;
                        if (!seen.Add(c.Pid)) continue;
                        string exe = (c.Exe ?? "").ToLowerInvariant();
                        if (exe == "conhost.exe" || exe == "cmd.exe" || exe == "nohup.exe") { stack.Add(c.Pid); continue; }
                        leaf = c.Exe;
                        stack.Add(c.Pid);
                    }
                }
                return leaf;
            }

            static double AgeMin(Proc p)
            {
                if (p.CreateTime <= 0) return 0;
                try { return (DateTime.Now - DateTime.FromFileTime(p.CreateTime)).TotalMinutes; }
                catch (Exception) { return 0; }
            }

            static string NameOf(string path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                try { return Path.GetFileName(path.TrimEnd('\\')); } catch (Exception) { return null; }
            }

            static string PrettyExe(string exe)
            {
                string n = Path.GetFileNameWithoutExtension(exe ?? "process");
                if (n.Length == 0) return "process";
                if (n.Equals("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
                return n;
            }

            static string SizeText(long bytes)
            {
                double gb = bytes / 1073741824.0;
                if (gb >= 1) return gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
                return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
            }

            static string Slug(string s)
            {
                if (string.IsNullOrEmpty(s)) return "x";
                var sb = new StringBuilder(s.Length);
                foreach (char c in s.ToLowerInvariant())
                {
                    if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                    else if (sb.Length == 0 || sb[sb.Length - 1] != '-') sb.Append('-');
                }
                string t = sb.ToString().Trim('-');
                return t.Length == 0 ? "x" : t;
            }

            static string ReadText(string path, int cap)
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        int n = (int)Math.Min(cap, fs.Length);
                        if (n <= 0) return "";
                        byte[] buf = new byte[n];
                        int got = fs.Read(buf, 0, n);
                        return Encoding.UTF8.GetString(buf, 0, got);
                    }
                }
                catch (Exception) { return null; }
            }

            static string AppRole(string exe)
            {
                string n = Base(exe);
                if (n == "windowsterminal" || n == "wt") return "terminal";
                if (n == "cursor" || n == "code" || n == "devenv") return "editor";
                if (n == "explorer") return "explorer";
                return null;
            }

            static bool Shell(string exe)
            {
                string n = Base(exe);
                return n == "cmd" || n == "powershell" || n == "pwsh" || n == "bash" || n == "mintty" || n == "wsl";
            }

            static bool Infra(string exe)
            {
                string n = Base(exe);
                return n == "conhost" || n == "openconsole" || n == "nohup";
            }

            static string RoleLabel(string exe)
            {
                string n = Base(exe);
                if (n == "pwsh" || n == "powershell") return "PowerShell";
                if (n == "cmd") return "cmd";
                if (n == "windowsterminal" || n == "wt") return "Windows Terminal";
                if (n == "cursor") return "Cursor";
                if (n == "code") return "VS Code";
                if (n == "devenv") return "Visual Studio";
                if (n == "explorer") return "Explorer";
                return n;
            }

            static string Base(string exe)
            {
                return Path.GetFileNameWithoutExtension(exe ?? "").ToLowerInvariant();
            }
        }
    }
}
