// v2 grouping prototype. One snapshot in, Things as JSON out. C# 5, in-box csc.
// Facts decide. The display-name map runs last and is not consulted by Decide.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace V2Proto
{
    static class Native
    {
        public const int QueryLimited = 0x1000;
        public const int QueryInfo = 0x0400;
        public const int VmRead = 0x0010;
        public const int MapAccess = QueryInfo | VmRead;
        public const int StatusLengthMismatch = unchecked((int)0xC0000004);
        public const int StatusBufferTooSmall = unchecked((int)0xC0000023);

        [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
        [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern int GetMappedFileName(IntPtr h, IntPtr addr, StringBuilder sb, int cap);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, IntPtr info, IntPtr len);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern uint QueryDosDevice(string name, char[] buf, uint cap);
        [DllImport("iphlpapi.dll", SetLastError = true)] public static extern uint GetExtendedTcpTable(IntPtr buf, ref int len, bool sort, int af, int table, uint reserved);
    }

    class Proc
    {
        public int Pid, Parent, Session;
        public string Exe = "";
        public long CpuTime, Create, Private;
        public double Cpu;
        public string Cmd = "", Cwd, Image;
        public bool Opened, MappedOk, AccessDenied;
        public List<int> Ports = new List<int>();
        public long Vram;
        public List<string> Models = new List<string>();
        public string LargestMap;
        public long LargestMapBytes;
        public double MapMs;
        public bool Yours, ServicesTree, TaskHit, StartupHit;
        public string LauncherKind, LauncherName, LauncherImage, Project, ProjectMarker, Script, RecentWrite, TaskName;
        public int RootPid;
    }

    static class Snap
    {
        public static List<Proc> Take()
        {
            int len = 1 << 20;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                for (int tries = 0; ; tries++)
                {
                    int ret;
                    int st = Native.NtQuerySystemInformation(5, buf, len, out ret);
                    if (st == Native.StatusLengthMismatch && tries < 4)
                    {
                        Marshal.FreeHGlobal(buf);
                        len = Math.Max(ret + (1 << 16), len * 2);
                        buf = Marshal.AllocHGlobal(len);
                        continue;
                    }
                    if (st != 0) return new List<Proc>();
                    break;
                }
                var list = new List<Proc>(400);
                int off = 0;
                while (true)
                {
                    IntPtr p = buf + off;
                    int pid = (int)Marshal.ReadIntPtr(p, 80).ToInt64();
                    if (pid != 0)
                    {
                        var pr = new Proc();
                        pr.Pid = pid;
                        pr.Parent = (int)Marshal.ReadIntPtr(p, 88).ToInt64();
                        pr.Session = Marshal.ReadInt32(p, 100);
                        pr.Private = Marshal.ReadInt64(p, 8);
                        pr.Create = Marshal.ReadInt64(p, 32);
                        pr.CpuTime = Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48);
                        int nlen = Marshal.ReadInt16(p, 56) & 0xffff;
                        IntPtr name = Marshal.ReadIntPtr(p, 64);
                        pr.Exe = name == IntPtr.Zero || nlen <= 0 ? "System" : Marshal.PtrToStringUni(name, nlen / 2);
                        list.Add(pr);
                    }
                    int next = Marshal.ReadInt32(p, 0);
                    if (next == 0) break;
                    off += next;
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static void ReadIdentity(Proc p)
        {
            IntPtr h = Native.OpenProcess(Native.MapAccess | Native.QueryLimited, false, p.Pid);
            if (h == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() == 5) p.AccessDenied = true;
                h = Native.OpenProcess(Native.QueryLimited | Native.VmRead, false, p.Pid);
            }
            else p.MappedOk = true;
            if (h == IntPtr.Zero)
            {
                h = Native.OpenProcess(Native.QueryLimited, false, p.Pid);
                if (h == IntPtr.Zero)
                {
                    if (Marshal.GetLastWin32Error() == 5) p.AccessDenied = true;
                    return;
                }
            }
            p.Opened = true;
            try
            {
                p.Cmd = CommandLine(h);
                p.Image = ImagePath(h);
                p.Cwd = CurrentDirectory(h);
            }
            finally { Native.CloseHandle(h); }
        }

        static string CommandLine(IntPtr h)
        {
            int len = 4096;
            IntPtr b = Marshal.AllocHGlobal(len);
            try
            {
                int ret;
                int st = Native.NtQueryInformationProcess(h, 60, b, len, out ret);
                if ((st == Native.StatusLengthMismatch || st == Native.StatusBufferTooSmall) && ret > len && ret <= 1024 * 1024)
                {
                    Marshal.FreeHGlobal(b);
                    len = ret;
                    b = Marshal.AllocHGlobal(len);
                    st = Native.NtQueryInformationProcess(h, 60, b, len, out ret);
                }
                if (st != 0) return "";
                int n = Marshal.ReadInt16(b, 0) & 0xffff;
                IntPtr s = Marshal.ReadIntPtr(b, 8);
                return s == IntPtr.Zero || n <= 0 ? "" : Marshal.PtrToStringUni(s, n / 2);
            }
            finally { Marshal.FreeHGlobal(b); }
        }

        static string ImagePath(IntPtr h)
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }

        // 64-bit PEB, then the WOW64 PEB when the process is 32-bit.
        static string CurrentDirectory(IntPtr h)
        {
            try
            {
                IntPtr pbi = Marshal.AllocHGlobal(48);
                try
                {
                    int ret;
                    if (Native.NtQueryInformationProcess(h, 0, pbi, 48, out ret) != 0) return null;
                    IntPtr peb = Marshal.ReadIntPtr(pbi, 8);
                    string cwd = ReadCwd64(h, peb);
                    if (cwd != null) return cwd;
                    IntPtr wowBuf = Marshal.AllocHGlobal(IntPtr.Size);
                    try
                    {
                        if (Native.NtQueryInformationProcess(h, 26, wowBuf, IntPtr.Size, out ret) != 0) return null;
                        IntPtr wow = Marshal.ReadIntPtr(wowBuf);
                        if (wow == IntPtr.Zero) return null;
                        return ReadCwd32(h, wow);
                    }
                    finally { Marshal.FreeHGlobal(wowBuf); }
                }
                finally { Marshal.FreeHGlobal(pbi); }
            }
            catch (Exception) { return null; }
        }

        static string ReadCwd64(IntPtr h, IntPtr peb)
        {
            IntPtr parms = ReadPtr(h, peb + 0x20, 8);
            if (parms == IntPtr.Zero) return null;
            return ReadUnicode(h, parms + 0x38, 8);
        }

        static string ReadCwd32(IntPtr h, IntPtr peb)
        {
            IntPtr parms = ReadPtr(h, peb + 0x10, 4);
            if (parms == IntPtr.Zero) return null;
            return ReadUnicode(h, parms + 0x24, 4);
        }

        static string ReadUnicode(IntPtr h, IntPtr us, int ptrBytes)
        {
            byte[] hdr = new byte[ptrBytes == 8 ? 16 : 8];
            IntPtr got;
            if (!Native.ReadProcessMemory(h, us, hdr, hdr.Length, out got)) return null;
            int len = BitConverter.ToUInt16(hdr, 0);
            long addr = ptrBytes == 8 ? BitConverter.ToInt64(hdr, 8) : BitConverter.ToUInt32(hdr, 4);
            if (len <= 0 || len > 32 * 1024 || addr == 0) return null;
            byte[] chars = new byte[len];
            if (!Native.ReadProcessMemory(h, new IntPtr(addr), chars, len, out got)) return null;
            return Encoding.Unicode.GetString(chars).TrimEnd('\\');
        }

        static IntPtr ReadPtr(IntPtr h, IntPtr addr, int bytes)
        {
            byte[] b = new byte[bytes];
            IntPtr got;
            if (!Native.ReadProcessMemory(h, addr, b, bytes, out got)) return IntPtr.Zero;
            long v = bytes == 8 ? BitConverter.ToInt64(b, 0) : BitConverter.ToUInt32(b, 0);
            return new IntPtr(v);
        }
    }

    static class Ports
    {
        public static Dictionary<int, List<int>> Listeners()
        {
            var map = new Dictionary<int, List<int>>();
            Read(2, 24, 8, 20, map);
            Read(23, 56, 20, 52, map);
            return map;
        }

        // af 2 = IPv4, 23 = IPv6. table 3 = TCP_TABLE_OWNER_PID_LISTENER.
        static void Read(int af, int row, int portOff, int pidOff, Dictionary<int, List<int>> map)
        {
            int len = 0;
            Native.GetExtendedTcpTable(IntPtr.Zero, ref len, false, af, 3, 0);
            if (len <= 0) return;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (Native.GetExtendedTcpTable(buf, ref len, false, af, 3, 0) != 0) return;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    IntPtr r = buf + 4 + i * row;
                    int raw = Marshal.ReadInt32(r, portOff);
                    int port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                    int pid = Marshal.ReadInt32(r, pidOff);
                    if (port <= 0 || pid <= 0) continue;
                    List<int> have;
                    if (!map.TryGetValue(pid, out have)) map[pid] = have = new List<int>();
                    if (!have.Contains(port)) have.Add(port);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    static class Vram
    {
        static readonly Dictionary<string, string> perfNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string PerfName(string english)
        {
            string have;
            if (perfNames.TryGetValue(english, out have)) return have;
            string local = english;
            try
            {
                using (var en = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\009"))
                using (var cur = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\CurrentLanguage"))
                {
                    string[] enList = en != null ? en.GetValue("Counter") as string[] : null;
                    string[] curList = cur != null ? cur.GetValue("Counter") as string[] : null;
                    if (enList != null && curList != null)
                    {
                        string index = null;
                        for (int i = 0; i + 1 < enList.Length; i += 2)
                            if (string.Equals(enList[i + 1], english, StringComparison.OrdinalIgnoreCase)) { index = enList[i]; break; }
                        if (index != null)
                            for (int i = 0; i + 1 < curList.Length; i += 2)
                                if (curList[i] == index) { local = curList[i + 1]; break; }
                    }
                }
            }
            catch (Exception) { }
            return perfNames[english] = local;
        }

        public static Dictionary<int, long> ByPid()
        {
            var next = new Dictionary<int, long>();
            try
            {
                var data = new PerformanceCounterCategory(PerfName("GPU Process Memory")).ReadCategory();
                var usage = data[PerfName("Local Usage")];
                if (usage == null) return next;
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
            return next;
        }
    }

    class Weight
    {
        public string Name;
        public long Bytes;
        public int Shards;
    }

    static class Maps
    {
        const uint MemCommit = 0x1000;
        const uint MemMapped = 0x40000;
        const long MinBin = 20L << 20;
        static readonly Regex Shard = new Regex(@"-\d{5}-of-\d{5}(?=\.[A-Za-z0-9]+$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static Dictionary<string, string> DosMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var buf = new char[1024];
            foreach (var di in DriveInfo.GetDrives())
            {
                string letter = di.Name.TrimEnd('\\');
                uint n = Native.QueryDosDevice(letter, buf, (uint)buf.Length);
                if (n == 0) continue;
                int z = Array.IndexOf(buf, '\0');
                if (z <= 0) continue;
                map[new string(buf, 0, z)] = letter;
            }
            return map;
        }

        public static string ToDos(string nt, Dictionary<string, string> dos)
        {
            if (string.IsNullOrEmpty(nt)) return nt;
            foreach (var kv in dos)
                if (nt.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value + nt.Substring(kv.Key.Length);
            return nt;
        }

        public static List<Weight> Weights(int pid, Dictionary<string, string> dos, out string largest, out long largestBytes)
        {
            largest = null;
            largestBytes = 0;
            var found = new List<Weight>();
            IntPtr h = Native.OpenProcess(Native.MapAccess, false, pid);
            if (h == IntPtr.Zero) return found;
            IntPtr info = Marshal.AllocHGlobal(48);
            try
            {
                var seen = new HashSet<long>();
                var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                long addr = 0;
                int steps = 0;
                var sb = new StringBuilder(520);
                while (addr >= 0 && addr < 0x00007FFFFFFFFFFF)
                {
                    for (int i = 0; i < 48; i++) Marshal.WriteByte(info, i, 0);
                    if (Native.VirtualQueryEx(h, new IntPtr(addr), info, new IntPtr(48)) == IntPtr.Zero) break;
                    long baseAddr = Marshal.ReadIntPtr(info, 0).ToInt64();
                    long alloc = Marshal.ReadIntPtr(info, 8).ToInt64();
                    long size = Marshal.ReadIntPtr(info, 24).ToInt64();
                    uint state = (uint)Marshal.ReadInt32(info, 32);
                    uint type = (uint)Marshal.ReadInt32(info, 40);
                    if (size <= 0 || ++steps > 200000) break;
                    if (state == MemCommit && type == MemMapped && seen.Add(alloc))
                    {
                        sb.Clear();
                        try
                        {
                            if (Native.GetMappedFileName(h, new IntPtr(baseAddr), sb, sb.Capacity) > 0)
                            {
                                string path = ToDos(sb.ToString(), dos);
                                long have;
                                files.TryGetValue(path, out have);
                                files[path] = have + size;
                            }
                        }
                        catch (Exception) { }
                    }
                    long next = addr + size;
                    if (next <= addr) break;
                    addr = next;
                }
                var groups = new Dictionary<string, Weight>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in files)
                {
                    bool candidate = IsWeight(kv.Key, Math.Max(kv.Value, MinBin)) || kv.Value >= MinBin;
                    long bytes = candidate ? FileBytes(kv.Key, kv.Value) : kv.Value;
                    if (bytes > largestBytes) { largestBytes = bytes; largest = kv.Key; }
                    if (!IsWeight(kv.Key, bytes)) continue;
                    string key = Shard.Replace(Path.GetFileName(kv.Key), "");
                    Weight w;
                    if (!groups.TryGetValue(key, out w))
                    {
                        w = new Weight();
                        w.Name = Path.GetFileNameWithoutExtension(key);
                        groups[key] = w;
                    }
                    w.Bytes += bytes;
                    w.Shards++;
                }
                found.AddRange(groups.Values);
            }
            catch (Exception) { }
            finally { Marshal.FreeHGlobal(info); Native.CloseHandle(h); }
            return found;
        }

        static long FileBytes(string path, long mapped)
        {
            try { if (File.Exists(path)) return new FileInfo(path).Length; }
            catch (Exception) { }
            return mapped;
        }

        static bool IsWeight(string path, long bytes)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".gguf" || ext == ".safetensors") return bytes > 0;
            if (ext == ".bin" || ext == ".pt" || ext == ".pth") return bytes >= MinBin;
            return false;
        }

        // Command-line fallback. python -m module is not a model path.
        public static string FromCommand(string cmd)
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
                if (path == null) continue;
                if (path.IndexOf('\\') < 0 && path.IndexOf('/') < 0 && Path.GetExtension(path).Length == 0) continue;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".gguf" || ext == ".safetensors" || ext == ".bin" || ext == ".pt" || ext == ".pth" || File.Exists(path))
                    return Path.GetFileNameWithoutExtension(Shard.Replace(Path.GetFileName(path), ""));
            }
            return null;
        }

        public static List<string> Split(string cmd)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            foreach (char c in cmd)
            {
                if (c == '"') { q = !q; continue; }
                if (c == ' ' && !q) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } continue; }
                sb.Append(c);
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list;
        }
    }

    class TaskRow
    {
        public string Name, Exe, Script, Action;
        public int State, Result;
        public DateTime LastRun, NextRun;
    }

    static class Sched
    {
        public static List<TaskRow> Read()
        {
            var found = new List<TaskRow>();
            try
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                svc.Connect();
                Walk(svc.GetFolder("\\"), found);
            }
            catch (Exception) { }
            return found;
        }

        static void Walk(dynamic folder, List<TaskRow> found)
        {
            string path = "";
            try { path = (string)folder.Path; } catch (Exception) { return; }
            if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                foreach (dynamic t in folder.GetTasks(0))
                {
                    try
                    {
                        if (!(bool)t.Enabled) continue;
                        string exe = "", args = "";
                        foreach (dynamic a in t.Definition.Actions)
                        {
                            if ((int)a.Type != 0) continue;
                            exe = Environment.ExpandEnvironmentVariables(((string)a.Path ?? "").Trim().Trim('"'));
                            args = Environment.ExpandEnvironmentVariables((string)a.Arguments ?? "");
                            break;
                        }
                        if (!UserTask((string)t.Name, exe, args)) continue;
                        var row = new TaskRow();
                        row.Name = Regex.Replace((string)t.Name, "-S-1-5-[0-9-]+$", "");
                        row.Exe = exe;
                        row.Script = ScriptArg(args);
                        row.Action = (exe + " " + args).Trim();
                        row.State = (int)t.State;
                        row.Result = (int)t.LastTaskResult;
                        row.LastRun = (DateTime)t.LastRunTime;
                        row.NextRun = (DateTime)t.NextRunTime;
                        found.Add(row);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            try { foreach (dynamic sub in folder.GetFolders(0)) Walk(sub, found); }
            catch (Exception) { }
        }

        // Microsoft's folder is already skipped. A vendor task runs an installed app; a user task runs something of theirs.
        static bool UserTask(string name, string exe, string args)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            string script = ScriptArg(args);
            if (Updater(name))
            {
                string marker;
                if (script == null || !UnderUser(script) || ProjectRoot.Find(script, out marker) == null) return false;
            }
            if (script != null && UnderUser(script)) return true;
            if (UnderWindows(exe) || Installed(exe)) return false;
            return true;
        }

        static bool Updater(string name)
        {
            return (name ?? "").IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string ScriptArg(string args)
        {
            foreach (string a in Maps.Split(args ?? ""))
            {
                if (a.StartsWith("-") || a.StartsWith("/")) continue;
                string ext = "";
                try { ext = Path.GetExtension(a).ToLowerInvariant(); } catch (Exception) { continue; }
                if (ext == ".py" || ext == ".js" || ext == ".ps1" || ext == ".bat" || ext == ".cmd" || ext == ".vbs") return a.Trim('"');
            }
            return null;
        }

        public static bool AtOrUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            path = path.TrimEnd('\\');
            root = root.TrimEnd('\\');
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static bool UnderWindows(string path)
        {
            return AtOrUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }

        public static bool Installed(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programs = string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Programs");
            if (AtOrUnder(path, pf) || AtOrUnder(path, pf86) || AtOrUnder(path, programs)) return true;
            return path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool UnderUser(string path)
        {
            return AtOrUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
    }

    static class Startup
    {
        public static List<string> Exes()
        {
            var list = new List<string>();
            try { Folders(list); } catch (Exception) { }
            try { RunKeys(list); } catch (Exception) { }
            return list;
        }

        static void Folders(List<string> list)
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

        static void RunKeys(List<string> list)
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                using (var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (k == null) continue;
                    foreach (string name in k.GetValueNames())
                    {
                        string cmd = Environment.ExpandEnvironmentVariables(k.GetValue(name) as string ?? "");
                        string exe = ExeOf(cmd);
                        if (exe.Length > 0) list.Add(exe);
                    }
                }
            }
        }

        static string ExeOf(string cmd)
        {
            cmd = (cmd ?? "").Trim();
            if (cmd.StartsWith("\"")) { int end = cmd.IndexOf('"', 1); return end > 1 ? cmd.Substring(1, end - 1) : ""; }
            int at = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return at > 0 ? cmd.Substring(0, at + 4) : "";
        }
    }

    static class ProjectRoot
    {
        static readonly string[] Markers = { ".git", "package.json", "pyproject.toml", "Cargo.toml", "go.mod" };

        public static string Find(string start, out string marker)
        {
            marker = null;
            if (string.IsNullOrEmpty(start)) return null;
            string dir = start;
            try
            {
                if (File.Exists(start) || !Directory.Exists(start)) dir = Path.GetDirectoryName(start);
            }
            catch (Exception) { return null; }
            for (int n = 0; n < 40 && !string.IsNullOrEmpty(dir); n++)
            {
                try
                {
                    foreach (string m in Markers)
                    {
                        string p = Path.Combine(dir, m);
                        if (File.Exists(p) || Directory.Exists(p)) { marker = m; return dir; }
                    }
                    if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.sln").Length > 0) { marker = "*.sln"; return dir; }
                }
                catch (Exception) { }
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }
    }

    // Launcher roles are the provenance fact (who started it), not the agent display map.
    static class Launch
    {
        // App that can start your work. Shells are not apps: a shell only splits a tree when it is the session host.
        public static string AppRole(string exe)
        {
            string n = Base(exe);
            if (n.Length == 0) return null;
            if (n == "windowsterminal" || n == "wt") return "terminal";
            if (n == "cursor" || n == "code" || n == "code - insiders" || n == "devenv" || n == "idea64" || n == "pycharm64" || n == "webstorm64" || n == "rider64" || n == "goland64" || n == "clion64" || n == "rustrover64" || n == "phpstorm64" || n == "datagrip64" || n == "studio64")
                return "editor";
            if (n == "explorer") return "explorer";
            return null;
        }

        public static bool Shell(string exe)
        {
            string n = Base(exe);
            return n == "cmd" || n == "powershell" || n == "pwsh" || n == "bash" || n == "mintty" || n == "wsl";
        }

        public static bool Infra(string exe)
        {
            string n = Base(exe);
            return n == "conhost" || n == "openconsole" || n == "nohup";
        }

        public static string Label(string exe)
        {
            string n = Base(exe);
            if (n == "windowsterminal" || n == "openconsole" || n == "wt") return "Windows Terminal";
            if (n == "pwsh" || n == "powershell") return "PowerShell";
            if (n == "cmd") return "cmd";
            if (n == "bash" || n == "git-bash" || n == "mintty") return "bash";
            if (n == "cursor") return "Cursor";
            if (n == "code" || n == "code - insiders") return "VS Code";
            if (n == "devenv") return "Visual Studio";
            if (n == "explorer") return "Explorer";
            if (n.Length == 0) return null;
            return n;
        }

        static string Base(string exe)
        {
            return Path.GetFileNameWithoutExtension(exe ?? "").ToLowerInvariant();
        }
    }

    static class Facts
    {
        public static void Decide(List<Proc> procs, List<TaskRow> tasks, List<string> startup)
        {
            var byPid = new Dictionary<int, Proc>();
            foreach (var p in procs) byPid[p.Pid] = p;
            foreach (var p in procs)
            {
                Walk(p, byPid);
                p.Script = ScriptOf(p);
                AssignProject(p);
                if (p.Session == 0) p.Yours = false;
                else if (MatchTask(p, tasks)) { p.Yours = true; p.TaskHit = true; if (p.LauncherKind == null || p.ServicesTree) { p.LauncherKind = "task"; p.LauncherName = "Scheduled task"; } }
                else if (p.ServicesTree) p.Yours = false;
                else if ((p.LauncherKind == "terminal" || p.LauncherKind == "editor") && !PartOfLauncher(p)) p.Yours = true;
                else if (MatchStartup(p, startup) && !AppImage(p)) { p.Yours = true; p.StartupHit = true; p.LauncherKind = "startup"; p.LauncherName = "Startup"; }
                else if (p.LauncherKind == "explorer" && !AppImage(p)) p.Yours = true;
                else if (p.LauncherKind == "explorer" && Launch.Shell(p.Exe)) p.Yours = true;
            }
            bool changed = true;
            for (int n = 0; n < 40 && changed; n++)
            {
                changed = false;
                foreach (var p in procs)
                {
                    if (p.Yours || p.Session == 0) continue;
                    Proc parent;
                    if (!byPid.TryGetValue(p.Parent, out parent) || !parent.Yours) continue;
                    if (parent.Create == 0 || p.Create == 0 || parent.Create >= p.Create) continue;
                    p.Yours = true;
                    if (p.LauncherKind == null) { p.LauncherKind = parent.LauncherKind; p.LauncherName = parent.LauncherName; }
                    if (parent.TaskHit) { p.TaskHit = true; p.TaskName = parent.TaskName; }
                    changed = true;
                }
            }
        }

        static void Walk(Proc p, Dictionary<int, Proc> byPid)
        {
            Proc cur = p;
            string bestKind = null, bestName = null, bestImage = null;
            var guard = new HashSet<int>();
            for (int n = 0; n < 40; n++)
            {
                if (!guard.Add(cur.Pid)) break;
                Proc parent;
                if (!byPid.TryGetValue(cur.Parent, out parent)) break;
                if (parent.Create == 0 || cur.Create == 0 || parent.Create >= cur.Create) break;
                if (parent.Session == 0 || string.Equals(parent.Exe, "services.exe", StringComparison.OrdinalIgnoreCase))
                    p.ServicesTree = true;
                string role = Launch.AppRole(parent.Exe);
                if (role == "terminal" || role == "editor") { bestKind = role; bestName = Launch.Label(parent.Exe); bestImage = parent.Image; }
                else if (role == "explorer" && bestKind == null) { bestKind = role; bestName = "Explorer"; }
                else if (Launch.Shell(parent.Exe) && bestImage == null) { bestKind = "terminal"; bestName = Launch.Label(parent.Exe); }
                cur = parent;
            }
            p.LauncherKind = bestKind;
            p.LauncherName = bestName;
            p.LauncherImage = bestImage;
        }

        // The editor's own binaries are the app. A tool it started is yours when its project sits outside that install.
        static bool PartOfLauncher(Proc p)
        {
            if (string.IsNullOrEmpty(p.LauncherImage)) return false;
            string home = Path.GetDirectoryName(p.LauncherImage);
            if (string.IsNullOrEmpty(home)) return false;
            string image = p.Image ?? "";
            bool inside = image.StartsWith(home.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Exe, Path.GetFileName(p.LauncherImage), StringComparison.OrdinalIgnoreCase);
            if (!inside) return false;
            return string.IsNullOrEmpty(p.Project) || p.Project.StartsWith(home, StringComparison.OrdinalIgnoreCase);
        }

        static bool AppImage(Proc p)
        {
            string path = p.Image ?? "";
            if (Sched.UnderWindows(path) && !Launch.Shell(p.Exe)) return true;
            return Sched.Installed(path);
        }

        static bool MatchTask(Proc p, List<TaskRow> tasks)
        {
            foreach (var t in tasks)
            {
                if (SamePath(p, t.Exe) || CmdHas(p, t.Script) || (ScriptExt(t.Exe) && CmdHas(p, t.Exe)))
                {
                    p.TaskName = t.Name;
                    return true;
                }
            }
            return false;
        }

        static bool CmdHas(Proc p, string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileName(path);
            return name.Length >= 5 && (p.Cmd ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool MatchStartup(Proc p, List<string> startup)
        {
            foreach (string exe in startup)
                if (SamePath(p, exe) && !Sched.Installed(exe) && !Sched.UnderWindows(exe)) return true;
            return false;
        }

        static bool SamePath(Proc p, string exe)
        {
            if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(p.Image)) return false;
            return string.Equals(Paths.GetFullPathSafe(p.Image), Paths.GetFullPathSafe(exe), StringComparison.OrdinalIgnoreCase);
        }

        static void AssignProject(Proc p)
        {
            string marker;
            string root = ProjectRoot.Find(p.Cwd, out marker);
            if (string.IsNullOrEmpty(root) || ToolRoot(root, p)) root = null;
            if (root == null)
            {
                root = ProjectRoot.Find(p.Script, out marker);
                if (ToolRoot(root, p)) root = null;
            }
            if (root == null)
            {
                root = ProjectRoot.Find(p.Image, out marker);
                if (ToolRoot(root, p)) root = null;
            }
            if (root != null && UnderInstall(root, p.Image)) { root = null; marker = null; }
            p.Project = root;
            p.ProjectMarker = root == null ? null : marker;
        }

        // The folder that contains the tool's own binary is not the work, unless the process was started there.
        static bool ToolRoot(string root, Proc p)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(p.Image)) return false;
            if (!Sched.AtOrUnder(p.Image, root)) return false;
            return string.IsNullOrEmpty(p.Cwd) || !Sched.AtOrUnder(p.Cwd, root);
        }

        static bool UnderInstall(string project, string image)
        {
            if (string.IsNullOrEmpty(image)) return false;
            if (Sched.Installed(image) || Sched.UnderWindows(image))
            {
                string dir = Path.GetDirectoryName(image);
                if (string.IsNullOrEmpty(dir)) return false;
                return project.StartsWith(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        static string ScriptOf(Proc p)
        {
            var args = Maps.Split(p.Cmd ?? "");
            for (int i = 1; i < args.Count; i++)
            {
                string a = args[i];
                if (a == "-m" || a == "-M")
                {
                    if (i + 1 < args.Count && a.Length == 2) return "module:" + args[i + 1];
                }
                if (a.StartsWith("-") || a.StartsWith("/")) continue;
                string full = a.Trim('"');
                try
                {
                    if (!Path.IsPathRooted(full))
                    {
                        if (string.IsNullOrEmpty(p.Cwd)) continue;
                        full = Path.Combine(p.Cwd, full);
                    }
                    if (!ScriptExt(full) || !File.Exists(full)) continue;
                    return full;
                }
                catch (Exception) { }
            }
            return null;
        }

        static bool ScriptExt(string path)
        {
            string ext = "";
            try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch (Exception) { return false; }
            return ext == ".py" || ext == ".pyw" || ext == ".js" || ext == ".mjs" || ext == ".cjs" || ext == ".ts" || ext == ".ps1" || ext == ".bat" || ext == ".cmd" || ext == ".vbs" || ext == ".sh";
        }

        public static string RecentWrite(Proc p)
        {
            string dir = p.Cwd;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            if (Sched.UnderWindows(dir) || Sched.Installed(dir)) return null;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.Equals(dir.TrimEnd('\\'), home.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
            DateTime born = p.Create > 0 ? DateTime.FromFileTime(p.Create) : DateTime.MinValue;
            DateTime best = DateTime.MinValue;
            string name = null;
            try
            {
                int n = 0;
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    if (++n > 80) break;
                    DateTime w;
                    try { w = File.GetLastWriteTime(f); } catch (Exception) { continue; }
                    if (w > born && w > best) { best = w; name = Path.GetFileName(f); }
                }
            }
            catch (Exception) { return null; }
            if (name == null) return null;
            if ((DateTime.Now - best).TotalSeconds > 90) return null;
            return name;
        }
    }

    static class Paths
    {
        public static string GetFullPathSafe(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception) { return path ?? ""; }
        }
    }

    class Thing
    {
        public string Name, Kind, Project, Launcher, State, Why;
        public List<int> Pids = new List<int>();
        public double Cpu, RamMB, VramMB;
        public List<int> Ports = new List<int>();
        public List<string> Models = new List<string>();
        public bool Gap;
    }

    static class Group
    {
        const long ModelVram = 1L << 30;
        const long HeavyRam = 2L << 30;

        public static List<Thing> Build(List<Proc> procs)
        {
            var byPid = new Dictionary<int, Proc>();
            foreach (var p in procs) byPid[p.Pid] = p;
            var roots = new HashSet<int>();
            foreach (var p in procs)
            {
                if (!p.Yours || Launch.Infra(p.Exe)) continue;
                if (IsRoot(p, byPid)) roots.Add(p.Pid);
            }
            foreach (var p in procs)
            {
                if (!p.Yours || Launch.Infra(p.Exe)) { p.RootPid = 0; continue; }
                p.RootPid = FindRoot(p, byPid, roots);
            }
            var drop = new HashSet<int>();
            foreach (int r in roots)
            {
                Proc shell = byPid[r];
                if (!Launch.Shell(shell.Exe)) continue;
                foreach (var p in procs)
                    if (p.RootPid != 0 && p.RootPid != r && Ancestor(p, r, byPid)) drop.Add(r);
            }
            var things = new List<Thing>();
            foreach (int r in roots.OrderBy(x => x))
            {
                if (drop.Contains(r)) continue;
                var members = procs.Where(p => p.RootPid == r).ToList();
                if (members.Count == 0) continue;
                things.Add(Make(byPid[r], members));
            }
            CollapseGap(things, procs, "WSL", new[] { "wsl", "wslhost", "wslrelay", "vmmemwsl" });
            CollapseGap(things, procs, "Docker", new[] { "docker", "com.docker.backend", "com.docker.service", "dockerd" });
            Heavy(things, procs);
            return things;
        }

        public static void AddIdleTasks(List<Thing> things, List<TaskRow> tasks, List<Proc> procs)
        {
            foreach (var task in tasks)
            {
                bool live = false;
                foreach (var p in procs)
                    if (p.TaskHit && string.Equals(p.TaskName, task.Name, StringComparison.OrdinalIgnoreCase)) live = true;
                if (live) continue;
                var t = new Thing();
                t.Name = task.Script != null ? Path.GetFileName(task.Script) : task.Name;
                t.Kind = "task";
                t.Launcher = "Scheduled task";
                string marker;
                string root = ProjectRoot.Find(task.Script ?? task.Exe, out marker);
                t.Project = root != null ? Path.GetFileName(root) : null;
                bool never = task.Result == 0x41303 || task.LastRun.Year < 2000;
                if (task.State == 4) t.State = "running";
                else if (never) t.State = "quiet";
                else if (task.Result == 0) t.State = "ended";
                else t.State = "failed";
                t.Why = "launcher=Scheduled task; last=" + task.Result.ToString("X", CultureInfo.InvariantCulture);
                if (root != null) t.Why += "; project=" + (marker ?? "marker") + " at " + root;
                if (task.NextRun.Year > 2000) t.Why += "; next " + task.NextRun.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                things.Add(t);
            }
        }

        static bool IsRoot(Proc p, Dictionary<int, Proc> byPid)
        {
            Proc parent;
            if (!byPid.TryGetValue(p.Parent, out parent)) return true;
            if (parent.Create == 0 || p.Create == 0 || parent.Create >= p.Create) return true;
            if (!parent.Yours || Launch.Infra(parent.Exe)) return true;
            if (Launch.AppRole(parent.Exe) != null) return true;
            if (Launch.Shell(parent.Exe) && ShellHost(parent, byPid)) return true;
            if (!string.IsNullOrEmpty(p.Project) && !string.IsNullOrEmpty(parent.Project) && !string.Equals(p.Project, parent.Project, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool ShellHost(Proc shell, Dictionary<int, Proc> byPid)
        {
            Proc parent;
            if (!byPid.TryGetValue(shell.Parent, out parent)) return true;
            if (parent.Create >= shell.Create) return true;
            if (!parent.Yours || Launch.Infra(parent.Exe) || Launch.AppRole(parent.Exe) != null) return true;
            return false;
        }

        static int FindRoot(Proc p, Dictionary<int, Proc> byPid, HashSet<int> roots)
        {
            Proc cur = p;
            var guard = new HashSet<int>();
            while (cur != null && guard.Add(cur.Pid))
            {
                if (roots.Contains(cur.Pid)) return cur.Pid;
                Proc parent;
                if (!byPid.TryGetValue(cur.Parent, out parent)) break;
                if (parent.Create >= cur.Create) break;
                cur = parent;
            }
            return p.Pid;
        }

        static bool Ancestor(Proc p, int pid, Dictionary<int, Proc> byPid)
        {
            Proc cur = p;
            var guard = new HashSet<int>();
            while (cur != null && guard.Add(cur.Pid))
            {
                if (cur.Parent == pid) return true;
                Proc parent;
                if (!byPid.TryGetValue(cur.Parent, out parent)) return false;
                if (parent.Create >= cur.Create) return false;
                cur = parent;
            }
            return false;
        }

        static Thing Make(Proc root, List<Proc> members)
        {
            var t = new Thing();
            t.Pids = members.Select(m => m.Pid).Distinct().OrderBy(x => x).ToList();
            t.Cpu = Math.Round(members.Sum(m => m.Cpu), 1);
            t.RamMB = Math.Round(members.Sum(m => m.Private) / 1048576.0, 1);
            t.VramMB = Math.Round(members.Sum(m => m.Vram) / 1048576.0, 1);
            t.Ports = members.SelectMany(m => m.Ports).Distinct().OrderBy(x => x).ToList();
            t.Models = members.SelectMany(m => m.Models).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string projectFull = root.Project ?? CommonProject(members);
            t.Project = projectFull != null ? Path.GetFileName(projectFull) : null;
            t.Launcher = root.LauncherName;
            string leaf = !string.IsNullOrEmpty(root.TaskName) ? root.TaskName : Leaf(root);
            t.Name = leaf;
            if (t.Models.Count > 0) t.Name = leaf + " · " + t.Models[0];
            t.Name = Pretty.Apply(t.Name, members);
            bool weights = t.Models.Count > 0;
            bool bigVram = members.Sum(m => m.Vram) >= ModelVram;
            string write = members.Select(m => m.RecentWrite).FirstOrDefault(w => w != null);
            double ageMin = members.Max(m => AgeMin(m));
            if (weights || bigVram) t.Kind = "model";
            else if (t.Ports.Count > 0) t.Kind = "server";
            else if (ageMin > 10 && write != null) t.Kind = "bot";
            else t.Kind = "script";
            bool admin = members.TrueForAll(m => m.AccessDenied && !m.Opened);
            if (admin) t.State = "running as admin";
            else if (ageMin > 5 && t.Cpu < 0.5 && t.Ports.Count == 0 && write == null) t.State = "quiet";
            else t.State = "running";
            t.Why = Why(root, members, t, write);
            if (admin) { t.Name = Path.GetFileNameWithoutExtension(root.Exe); t.Why = "running as admin; " + t.Why; }
            return t;
        }

        static string CommonProject(List<Proc> members)
        {
            string best = null;
            int bestN = 0;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in members)
            {
                if (string.IsNullOrEmpty(m.Project)) continue;
                int n;
                counts.TryGetValue(m.Project, out n);
                counts[m.Project] = ++n;
                if (n > bestN) { bestN = n; best = m.Project; }
            }
            return best;
        }

        static string Leaf(Proc p)
        {
            if (!string.IsNullOrEmpty(p.Script))
            {
                if (p.Script.StartsWith("module:")) return p.Script.Substring(7);
                return Path.GetFileName(p.Script);
            }
            return Path.GetFileNameWithoutExtension(p.Exe ?? "process");
        }

        static double AgeMin(Proc p)
        {
            if (p.Create <= 0) return 0;
            return (DateTime.Now - DateTime.FromFileTime(p.Create)).TotalMinutes;
        }

        static string Why(Proc root, List<Proc> members, Thing t, string write)
        {
            var bits = new List<string>();
            if (root.LauncherName != null) bits.Add("launcher=" + root.LauncherName);
            else if (root.TaskHit) bits.Add("launcher=Scheduled task");
            else if (root.StartupHit) bits.Add("launcher=Startup");
            else bits.Add("launcher=unknown");
            string marker = members.Select(m => m.ProjectMarker).FirstOrDefault(m => m != null);
            string full = members.Select(m => m.Project).FirstOrDefault(m => m != null);
            if (full != null) bits.Add("project=" + (marker ?? "marker") + " at " + full);
            else bits.Add("project=none");
            if (t.Ports.Count > 0) bits.Add("listens " + string.Join(",", t.Ports.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()));
            if (t.VramMB > 0) bits.Add("vram " + t.VramMB.ToString("0.0", CultureInfo.InvariantCulture) + " MB");
            if (t.Models.Count > 0) bits.Add("weights " + string.Join(", ", t.Models.ToArray()));
            if (write != null) bits.Add("wrote " + write);
            return string.Join("; ", bits.ToArray());
        }

        static void CollapseGap(List<Thing> things, List<Proc> procs, string name, string[] bases)
        {
            var set = new HashSet<string>(bases, StringComparer.OrdinalIgnoreCase);
            var hit = new List<Proc>();
            foreach (var p in procs)
            {
                string b = Path.GetFileNameWithoutExtension(p.Exe ?? "");
                if (set.Contains(b) || b.StartsWith("com.docker", StringComparison.OrdinalIgnoreCase)) hit.Add(p);
            }
            if (hit.Count == 0) return;
            var pids = new HashSet<int>(hit.Select(p => p.Pid));
            things.RemoveAll(t => t.Pids.Any(pid => pids.Contains(pid)));
            var tNew = new Thing();
            tNew.Name = name;
            tNew.Kind = "gap";
            tNew.Launcher = hit.Select(p => p.LauncherName).FirstOrDefault(x => x != null);
            tNew.Pids = hit.Select(p => p.Pid).Distinct().OrderBy(x => x).ToList();
            tNew.Cpu = Math.Round(hit.Sum(p => p.Cpu), 1);
            tNew.RamMB = Math.Round(hit.Sum(p => p.Private) / 1048576.0, 1);
            tNew.State = "running";
            tNew.Why = name + " is one line; processes inside are not enumerated";
            tNew.Gap = true;
            things.Add(tNew);
        }

        static void Heavy(List<Thing> things, List<Proc> procs)
        {
            var used = new HashSet<int>();
            foreach (var t in things) foreach (int pid in t.Pids) used.Add(pid);
            var groups = new Dictionary<string, List<Proc>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in procs)
            {
                if (p.Yours || used.Contains(p.Pid)) continue;
                string key = string.IsNullOrEmpty(p.Exe) ? "process" : p.Exe;
                List<Proc> list;
                if (!groups.TryGetValue(key, out list)) groups[key] = list = new List<Proc>();
                list.Add(p);
            }
            foreach (var kv in groups)
            {
                double cpu = kv.Value.Sum(p => p.Cpu);
                long ram = kv.Value.Sum(p => p.Private);
                if (cpu < 25 && ram < HeavyRam) continue;
                var t = new Thing();
                t.Name = Path.GetFileNameWithoutExtension(kv.Key);
                t.Kind = "other";
                t.Pids = kv.Value.Select(p => p.Pid).OrderBy(x => x).ToList();
                t.Cpu = Math.Round(cpu, 1);
                t.RamMB = Math.Round(ram / 1048576.0, 1);
                t.State = "running";
                t.Why = "not yours; " + (cpu >= 25 ? "cpu " + t.Cpu.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "ram " + t.RamMB.ToString("0.0", CultureInfo.InvariantCulture) + " MB");
                things.Add(t);
            }
        }
    }

    // Applied after the facts. Detection never reads this.
    static class Pretty
    {
        static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static Pretty()
        {
            Names["claude"] = "Claude Code";
            Names["claude-code"] = "Claude Code";
            Names["codex"] = "Codex";
            Names["cursor-agent"] = "Cursor";
            Names["openclaw"] = "OpenClaw";
        }

        public static string Apply(string name, List<Proc> members)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int dot = name.IndexOf('·');
            string leaf = dot < 0 ? name : name.Substring(0, dot).Trim();
            string rest = dot < 0 ? "" : name.Substring(dot - 1);
            string nice = Hit(leaf);
            if (nice == null)
                foreach (var m in members)
                {
                    nice = Hit(m.Image);
                    if (nice == null) nice = Hit(m.Script);
                    if (nice == null) nice = Hit(m.TaskName);
                    if (nice != null) break;
                }
            return nice == null ? name : nice + rest;
        }

        static string Hit(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string nice;
            foreach (string part in text.Split('\\', '/'))
            {
                string key = Path.GetFileNameWithoutExtension(part).Split(' ')[0];
                if (Names.TryGetValue(key, out nice)) return nice;
                if (key.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return Names["claude"];
                if (key.StartsWith("codex", StringComparison.OrdinalIgnoreCase)) return Names["codex"];
            }
            return null;
        }
    }

    static class Emit
    {
        static readonly Regex Secret = new Regex(@"(?i)(?:--?(?:api[_-]?key|token|access[_-]?token|secret|password|passwd|pwd)\b\s*[=:]?\s*)\S+|(?:(?:api[_-]?key|token|password|passwd|secret)\s*=\s*)\S+|\b(?:sk-[A-Za-z0-9_\-]{8,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9\-]{10,})\b|\bBearer\s+\S+|(?:https?|ftp)://[^/\s:@]+:[^/\s:@]+@[^\s]+", RegexOptions.Compiled);

        public static string Scrub(string s)
        {
            return string.IsNullOrEmpty(s) ? s : Secret.Replace(s, "<redacted>");
        }

        public static string Things(List<Thing> things)
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            for (int i = 0; i < things.Count; i++)
            {
                var t = things[i];
                sb.Append("  {\"name\":").Append(J(t.Name));
                sb.Append(",\"kind\":").Append(J(t.Kind));
                sb.Append(",\"project\":").Append(J(t.Project));
                sb.Append(",\"launcher\":").Append(J(t.Launcher));
                sb.Append(",\"pids\":").Append(Ints(t.Pids));
                sb.Append(",\"cpu\":").Append(Num(t.Cpu));
                sb.Append(",\"ramMB\":").Append(Num(t.RamMB));
                sb.Append(",\"vramMB\":").Append(Num(t.VramMB));
                sb.Append(",\"ports\":").Append(Ints(t.Ports));
                sb.Append(",\"models\":").Append(Strs(t.Models));
                sb.Append(",\"state\":").Append(J(t.State));
                sb.Append(",\"why\":").Append(J(Scrub(t.Why)));
                sb.Append(i + 1 == things.Count ? "}\n" : "},\n");
            }
            sb.Append("]\n");
            return sb.ToString();
        }

        public static string Raw(List<Proc> procs)
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            var list = procs.OrderBy(p => p.Pid).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                sb.Append("  {\"pid\":").Append(p.Pid);
                sb.Append(",\"ppid\":").Append(p.Parent);
                sb.Append(",\"session\":").Append(p.Session);
                sb.Append(",\"exe\":").Append(J(p.Exe));
                sb.Append(",\"yours\":").Append(p.Yours ? "true" : "false");
                sb.Append(",\"services\":").Append(p.ServicesTree ? "true" : "false");
                sb.Append(",\"launcher\":").Append(J(p.LauncherName));
                sb.Append(",\"launcherKind\":").Append(J(p.LauncherKind));
                sb.Append(",\"project\":").Append(J(p.Project));
                sb.Append(",\"marker\":").Append(J(p.ProjectMarker));
                sb.Append(",\"cwd\":").Append(J(p.Cwd));
                sb.Append(",\"image\":").Append(J(p.Image));
                sb.Append(",\"script\":").Append(J(p.Script));
                sb.Append(",\"root\":").Append(p.RootPid);
                sb.Append(",\"ports\":").Append(Ints(p.Ports));
                sb.Append(",\"vramMB\":").Append(Num(Math.Round(p.Vram / 1048576.0, 1)));
                sb.Append(",\"models\":").Append(Strs(p.Models));
                sb.Append(",\"largestMap\":").Append(J(p.LargestMap));
                sb.Append(",\"largestMapMB\":").Append(Num(Math.Round(p.LargestMapBytes / 1048576.0, 1)));
                sb.Append(",\"mapMs\":").Append(Num(Math.Round(p.MapMs, 1)));
                sb.Append(",\"admin\":").Append(p.AccessDenied && !p.Opened ? "true" : "false");
                sb.Append(",\"cmd\":").Append(J(Scrub(p.Cmd)));
                sb.Append(i + 1 == list.Count ? "}\n" : "},\n");
            }
            sb.Append("]\n");
            return sb.ToString();
        }

        static string J(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '\\' || c == '"') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string Num(double d)
        {
            return d.ToString("0.0", CultureInfo.InvariantCulture);
        }

        static string Ints(List<int> xs)
        {
            return "[" + string.Join(",", xs.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + "]";
        }

        static string Strs(List<string> xs)
        {
            return "[" + string.Join(",", xs.Select(J).ToArray()) + "]";
        }
    }

    static class Program
    {
        static int Main(string[] args)
        {
            string rawPath = null;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--raw" && i + 1 < args.Length) rawPath = args[++i];
            var clock = Stopwatch.StartNew();
            long ws0 = Process.GetCurrentProcess().WorkingSet64;
            long peak = ws0;
            try
            {
                var procs = Snap.Take();
                long tSnap = clock.ElapsedMilliseconds;
                long cpuT0 = tSnap;
                var ports = Ports.Listeners();
                var vram = Vram.ByPid();
                var dos = Maps.DosMap();
                foreach (var p in procs)
                {
                    List<int> lp;
                    if (ports.TryGetValue(p.Pid, out lp)) p.Ports = lp;
                    long vb;
                    if (vram.TryGetValue(p.Pid, out vb)) p.Vram = vb;
                    if (p.Session == 0) continue;
                    Snap.ReadIdentity(p);
                }
                long tId = clock.ElapsedMilliseconds;
                var tasks = Sched.Read();
                var startup = Startup.Exes();
                Facts.Decide(procs, tasks, startup);
                long mapAll = 0, mapYours = 0, mapVram = 0;
                int nMap = 0, nYours = 0, nVram = 0;
                foreach (var p in procs)
                {
                    bool wantMap = p.Yours || p.Vram >= (1L << 30) || (p.Cmd != null && p.Cmd.IndexOf(".gguf", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!wantMap || p.Session == 0) continue;
                    var sw = Stopwatch.StartNew();
                    string largest;
                    long largestBytes;
                    var weights = Maps.Weights(p.Pid, dos, out largest, out largestBytes);
                    sw.Stop();
                    p.MapMs = sw.Elapsed.TotalMilliseconds;
                    p.LargestMap = largest;
                    p.LargestMapBytes = largestBytes;
                    foreach (var w in weights) if (!p.Models.Contains(w.Name)) p.Models.Add(w.Name);
                    if (p.Models.Count == 0)
                    {
                        string fb = Maps.FromCommand(p.Cmd);
                        if (fb != null) p.Models.Add(fb);
                    }
                    if (p.Yours) p.RecentWrite = Facts.RecentWrite(p);
                    mapAll += sw.ElapsedMilliseconds;
                    nMap++;
                    if (p.Yours) { mapYours += sw.ElapsedMilliseconds; nYours++; }
                    if (p.Vram > 0) { mapVram += sw.ElapsedMilliseconds; nVram++; }
                    long ws = Process.GetCurrentProcess().WorkingSet64;
                    if (ws > peak) peak = ws;
                }
                var cpuTimes = new Dictionary<int, long>();
                foreach (var p in procs) cpuTimes[p.Pid] = p.CpuTime;
                long beforeSleep = clock.ElapsedMilliseconds;
                var later = Snap.Take();
                long dt = clock.ElapsedMilliseconds - cpuT0;
                if (dt < 1) dt = 1;
                int cpus = Math.Max(1, Environment.ProcessorCount);
                var laterCpu = new Dictionary<int, long>();
                foreach (var p in later) laterCpu[p.Pid] = p.CpuTime;
                foreach (var p in procs)
                {
                    long a, b;
                    if (!cpuTimes.TryGetValue(p.Pid, out a) || !laterCpu.TryGetValue(p.Pid, out b) || b < a) continue;
                    p.Cpu = (b - a) / 100.0 / dt / cpus;
                }
                var things = Group.Build(procs);
                Group.AddIdleTasks(things, tasks, procs);
                string json = Emit.Things(things);
                Console.OutputEncoding = Encoding.UTF8;
                Console.Write(json);
                if (rawPath != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(rawPath)));
                    File.WriteAllText(rawPath, Emit.Raw(procs), new UTF8Encoding(false));
                }
                clock.Stop();
                long ws1 = Process.GetCurrentProcess().WorkingSet64;
                if (ws1 > peak) peak = ws1;
                var err = Console.Error;
                err.WriteLine("procs=" + procs.Count);
                err.WriteLine("things=" + things.Count);
                err.WriteLine("yours=" + procs.Count(p => p.Yours));
                err.WriteLine("pass_ms=" + clock.ElapsedMilliseconds);
                err.WriteLine("snap_ms=" + tSnap);
                err.WriteLine("identity_ms=" + (tId - tSnap));
                err.WriteLine("work_ms=" + beforeSleep);
                err.WriteLine("map_ms=" + mapAll);
                err.WriteLine("map_procs=" + nMap);
                err.WriteLine("map_yours_ms=" + mapYours);
                err.WriteLine("map_yours_procs=" + nYours);
                err.WriteLine("map_vram_ms=" + mapVram);
                err.WriteLine("map_vram_procs=" + nVram);
                err.WriteLine("tasks=" + tasks.Count);
                err.WriteLine("ws_start_mb=" + (ws0 / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture));
                err.WriteLine("ws_peak_mb=" + (peak / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture));
                err.WriteLine("ws_extra_mb=" + ((peak - ws0) / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error=" + ex.GetType().Name + " " + Emit.Scrub(ex.Message));
                return 1;
            }
        }
    }
}
