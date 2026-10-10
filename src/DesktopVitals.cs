// Desktop Vitals: a small glass window that shows what this PC is doing: system load, scripts, scheduled tasks, bots.
// One NtQuerySystemInformation call per tick covers every process. Nothing is
// collected while the switch is off or the window is minimized.
// Built with the C# 5 compiler that ships with Windows (see build.ps1).
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
    static class Native
    {
        [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
        [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMSTAT { public uint len, load; public ulong total, avail, totalPage, availPage, totalVirt, availVirt, availExt; }
        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMSTAT m);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int l, r, t, b; }
        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        delegate bool EnumProc(IntPtr h, IntPtr lp);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
        public const int SwRestore = 9;
        [DllImport("kernel32.dll")] static extern bool GetLogicalProcessorInformationEx(int rel, IntPtr buf, ref int len);

        // Logical processors grouped by physical core, fastest cores first. Each entry is (efficiency class, thread numbers).
        public static List<KeyValuePair<int, int[]>> CoreLayout()
        {
            var cores = new List<KeyValuePair<int, int[]>>();
            int len = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            if (len <= 0) return cores;
            IntPtr b = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetLogicalProcessorInformationEx(0, b, ref len)) return cores;
                for (int off = 0; off < len; )
                {
                    int size = Marshal.ReadInt32(b, off + 4);
                    int eff = Marshal.ReadByte(b, off + 9);
                    long mask = Marshal.ReadInt64(b, off + 32);
                    var threads = new List<int>();
                    for (int i = 0; i < 64; i++) if ((mask & (1L << i)) != 0) threads.Add(i);
                    cores.Add(new KeyValuePair<int, int[]>(eff, threads.ToArray()));
                    off += size;
                }
            }
            finally { Marshal.FreeHGlobal(b); }
            return cores.OrderByDescending(c => c.Key).ThenBy(c => c.Value[0]).ToList();
        }

        // Busy and total time per logical processor, in 100 ns units.
        public static bool ThreadTimes(long[] busy, long[] total)
        {
            int n = busy.Length, len = n * 48, ret;
            IntPtr b = Marshal.AllocHGlobal(len);
            try
            {
                if (NtQuerySystemInformation(8, b, len, out ret) != 0) return false;
                for (int i = 0; i < n; i++)
                {
                    long idle = Marshal.ReadInt64(b, i * 48), kernel = Marshal.ReadInt64(b, i * 48 + 8), user = Marshal.ReadInt64(b, i * 48 + 16);
                    total[i] = kernel + user;
                    busy[i] = kernel + user - idle;
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(b); }
        }

        // The windows you would see in Alt+Tab for these processes: visible, titled, unowned, not cloaked.
        public static List<IntPtr> AppWindows(int[] pids)
        {
            var want = new HashSet<int>(pids);
            var found = new List<IntPtr>();
            EnumWindows(delegate (IntPtr h, IntPtr lp)
            {
                int pid, cloaked;
                GetWindowThreadProcessId(h, out pid);
                if (want.Contains(pid) && IsWindowVisible(h) && GetWindow(h, 4) == IntPtr.Zero && GetWindowTextLength(h) > 0
                    && !(DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0))
                    found.Add(h);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public const int QueryLimited = 0x1000;
        public const int StatusLengthMismatch = unchecked((int)0xC0000004);
        public const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    }

    // NVIDIA's management library ships with the driver. Missing or failing calls leave the GPU rows blank.
    static class Nvml
    {
        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr dev);
        [DllImport("nvml.dll", CharSet = CharSet.Ansi)] static extern int nvmlDeviceGetName(IntPtr d, StringBuilder name, uint len);
        [StructLayout(LayoutKind.Sequential)] struct Util { public uint gpu, mem; }
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr d, out Util u);
        [StructLayout(LayoutKind.Sequential)] struct Mem { public ulong total, free, used; }
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(IntPtr d, out Mem m);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr d, int sensor, out uint t);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr d, out uint mw);

        static IntPtr dev;
        static bool ready, tried;
        public static string Name;

        public static bool Read(out double util, out double usedGb, out double totalGb, out int temp, out double watts)
        {
            util = usedGb = totalGb = watts = 0; temp = 0;
            try
            {
                if (!tried)
                {
                    tried = true;
                    // DESKTOPVITALS_NO_NVML=1 shows what AMD and Intel owners see, for testing.
                    if (Environment.GetEnvironmentVariable("DESKTOPVITALS_NO_NVML") == "1") return false;
                    if (nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out dev) == 0)
                    {
                        var sb = new StringBuilder(96);
                        if (nvmlDeviceGetName(dev, sb, 96) == 0) Name = sb.ToString().Replace("NVIDIA ", "").Replace("GeForce ", "");
                        ready = true;
                    }
                }
                if (!ready) return false;
                Util u; Mem m; uint t, mw;
                if (nvmlDeviceGetUtilizationRates(dev, out u) == 0) util = u.gpu;
                if (nvmlDeviceGetMemoryInfo(dev, out m) == 0) { usedGb = m.used / 1073741824.0; totalGb = m.total / 1073741824.0; }
                if (nvmlDeviceGetTemperature(dev, 0, out t) == 0) temp = (int)t;
                if (nvmlDeviceGetPowerUsage(dev, out mw) == 0) watts = mw / 1000.0;
                return true;
            }
            catch (Exception) { ready = false; return false; }
        }
    }

    class Proc
    {
        public int Pid;
        public string Exe;        // image name, e.g. chrome.exe
        public long CpuTime;      // 100 ns units, user + kernel
        public long CreateTime;
        public long Private;      // private working set, what Task Manager calls Memory
        public double Cpu;        // percent of the whole machine
        public string Cmd = "";
    }

    static class Procs
    {
        static IntPtr buf = IntPtr.Zero;
        static int bufLen;

        public static List<Proc> Snapshot()
        {
            var list = new List<Proc>(400);
            if (bufLen == 0) { bufLen = 1 << 20; buf = Marshal.AllocHGlobal(bufLen); }
            for (int tries = 0; ; tries++)
            {
                int ret;
                int st = Native.NtQuerySystemInformation(5, buf, bufLen, out ret);
                if (st == Native.StatusLengthMismatch && tries < 4)
                {
                    Marshal.FreeHGlobal(buf);
                    bufLen = Math.Max(ret + (1 << 16), bufLen * 2);
                    buf = Marshal.AllocHGlobal(bufLen);
                    continue;
                }
                if (st != 0) return list;
                break;
            }
            // SYSTEM_PROCESS_INFORMATION, x64 offsets.
            int off = 0;
            while (true)
            {
                IntPtr p = buf + off;
                int pid = (int)Marshal.ReadIntPtr(p, 80).ToInt64();
                if (pid != 0)
                {
                    var pr = new Proc();
                    pr.Pid = pid;
                    pr.Private = Marshal.ReadInt64(p, 8);
                    pr.CreateTime = Marshal.ReadInt64(p, 32);
                    pr.CpuTime = Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48);
                    int nlen = Marshal.ReadInt16(p, 56) & 0xffff;
                    IntPtr name = Marshal.ReadIntPtr(p, 64);
                    pr.Exe = name == IntPtr.Zero ? "System" : Marshal.PtrToStringUni(name, nlen / 2);
                    list.Add(pr);
                }
                int next = Marshal.ReadInt32(p, 0);
                if (next == 0) break;
                off += next;
            }
            return list;
        }

        public static string CommandLine(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.QueryLimited, false, pid);
            if (h == IntPtr.Zero) return "";
            int len = 4096;
            IntPtr b = Marshal.AllocHGlobal(len);
            try
            {
                int ret;
                int st = Native.NtQueryInformationProcess(h, 60, b, len, out ret);
                if ((st == Native.StatusLengthMismatch || st == Native.StatusBufferTooSmall) && ret > len && ret < (1 << 20))
                {
                    Marshal.FreeHGlobal(b);
                    len = ret;
                    b = Marshal.AllocHGlobal(len);
                    st = Native.NtQueryInformationProcess(h, 60, b, len, out ret);
                }
                if (st != 0) return "";
                int l = Marshal.ReadInt16(b, 0) & 0xffff;
                IntPtr s = Marshal.ReadIntPtr(b, 8);
                return s == IntPtr.Zero ? "" : Marshal.PtrToStringUni(s, l / 2);
            }
            finally { Marshal.FreeHGlobal(b); Native.CloseHandle(h); }
        }

        // The process's working folder, read from its parameters block. Works for 64-bit processes you own.
        public static string CurrentDirectory(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.QueryLimited | 0x0010, false, pid);   // PROCESS_VM_READ
            if (h == IntPtr.Zero) return null;
            IntPtr pbi = Marshal.AllocHGlobal(48);
            try
            {
                int ret;
                if (Native.NtQueryInformationProcess(h, 0, pbi, 48, out ret) != 0) return null;
                IntPtr peb = Marshal.ReadIntPtr(pbi, 8);
                IntPtr parms = ReadPtr(h, peb + 0x20);
                if (parms == IntPtr.Zero) return null;
                byte[] us = new byte[16];
                IntPtr got;
                if (!Native.ReadProcessMemory(h, parms + 0x38, us, 16, out got)) return null;
                int len = BitConverter.ToUInt16(us, 0);
                long bufAddr = BitConverter.ToInt64(us, 8);
                if (len == 0 || len > 4096 || bufAddr == 0) return null;
                byte[] chars = new byte[len];
                if (!Native.ReadProcessMemory(h, new IntPtr(bufAddr), chars, len, out got)) return null;
                return Encoding.Unicode.GetString(chars).TrimEnd('\\');
            }
            catch (Exception) { return null; }
            finally { Marshal.FreeHGlobal(pbi); Native.CloseHandle(h); }
        }

        static IntPtr ReadPtr(IntPtr h, IntPtr addr)
        {
            byte[] b = new byte[8];
            IntPtr got;
            return Native.ReadProcessMemory(h, addr, b, 8, out got) ? new IntPtr(BitConverter.ToInt64(b, 0)) : IntPtr.Zero;
        }

        public static string ImagePath(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.QueryLimited, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { Native.CloseHandle(h); }
        }
    }

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

    class WatchRule
    {
        public string Label;
        public string Match;
        public bool ByExe;
        public string Check;      // a file, folder or http address that shows the bot is working
        public string Log;        // the supervisor log, for counting crash restarts
        public string Code;       // the bot's folder, opened in your editor on click
        public bool IsBot { get { return Check != null; } }

        public string Crash = "restart";   // text in a log line that marks a crash restart

        // Your settings live in %APPDATA%\DesktopVitals\watch.txt, outside the app folder. A watch.txt next to the app works too.
        public static string ConfigFile()
        {
            string mine = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopVitals", "watch.txt");
            if (File.Exists(mine)) return mine;
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string file = System.IO.Path.Combine(dir, "watch.txt");
            if (!File.Exists(file)) file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir.TrimEnd('\\')), "watch.txt");
            return File.Exists(file) ? file : mine;
        }

        // Lines that start with @ are settings: "@temperature-file = <json>", "@temperature-task = <task path>".
        public static Dictionary<string, string> Settings()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string file = ConfigFile();
            if (!File.Exists(file)) return d;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (!line.StartsWith("@") || eq < 2) continue;
                d[line.Substring(1, eq - 1).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        // "Label = something.exe" matches a process name; anything else matches text in the command line.
        public static List<WatchRule> Load()
        {
            var rules = new List<WatchRule>();
            string file = ConfigFile();
            if (!File.Exists(file)) return rules;
            foreach (string raw in File.ReadAllLines(file))
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
                    else if (x.StartsWith("log:", StringComparison.OrdinalIgnoreCase)) r.Log = x.Substring(4).Trim();
                    else if (x.StartsWith("code:", StringComparison.OrdinalIgnoreCase)) r.Code = x.Substring(5).Trim();
                    else if (x.StartsWith("crash:", StringComparison.OrdinalIgnoreCase)) r.Crash = x.Substring(6).Trim();
                }
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

    // A script some interpreter is running: python bot.py, node server.js, powershell -File job.ps1, python -m app.
    static class Scripts
    {
        static readonly string[] Interpreters = { "python", "pythonw", "py", "pyw", "node", "bun", "deno", "powershell", "pwsh", "wscript", "cscript", "cmd", "ruby", "php", "perl", "java", "javaw", "bash" };
        static readonly string[] Exts = { ".py", ".pyw", ".js", ".mjs", ".cjs", ".ts", ".ps1", ".bat", ".cmd", ".vbs", ".rb", ".php", ".pl", ".jar", ".sh" };
        static readonly string[] SkipRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft"),
        }.Where(x => !string.IsNullOrEmpty(x)).ToArray();

        public static bool IsInterpreter(string exe)
        {
            string n = System.IO.Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            n = Regex.Replace(n, "[0-9.]+$", "");   // python3.12 -> python
            return Interpreters.Contains(n);
        }

        // Part of an app or of Windows rather than something you run yourself.
        public static bool IsSystemPath(string path)
        {
            if (path.IndexOf("\\node_modules\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (path.IndexOf("\\resources\\app", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (path.IndexOf("\\site-packages\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return SkipRoots.Any(r => path.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsScriptFile(string path)
        {
            if (path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) return false;
            int dot = path.LastIndexOf('.');
            return dot > 0 && Exts.Contains(path.Substring(dot).ToLowerInvariant());
        }

        public static List<string> SplitArgs(string cmd)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            foreach (char c in cmd)
            {
                if (c == '"') { quoted = !quoted; continue; }
                if (c == ' ' && !quoted) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } continue; }
                sb.Append(c);
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list;
        }

        // The script file this process runs, as a full path, or "module:<name>" for python -m. Null when it isn't running a script of yours.
        public static string Find(Proc p, Func<string> cwd)
        {
            try { return FindScript(p, cwd); }
            catch (Exception) { return null; }   // odd command lines are skipped, never fatal
        }

        static string FindScript(Proc p, Func<string> cwd)
        {
            if (!IsInterpreter(p.Exe) || p.Cmd.Length == 0) return null;
            var args = SplitArgs(p.Cmd);
            for (int i = 1; i < args.Count; i++)
            {
                string a = args[i];
                if ((a == "-m" || a == "-M") && i + 1 < args.Count && p.Exe.StartsWith("py", StringComparison.OrdinalIgnoreCase))
                {
                    string dir = cwd();
                    return dir != null && !IsSystemPath(dir + "\\") ? "module:" + args[i + 1] + "|" + dir : null;
                }
                if (a.StartsWith("-") || a.StartsWith("/")) continue;
                if (!IsScriptFile(a)) continue;
                string full = a;
                if (!System.IO.Path.IsPathRooted(full))
                {
                    string dir = cwd();
                    if (dir == null) return null;
                    try { full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, a)); } catch (Exception) { return null; }
                }
                if (IsSystemPath(full) || !File.Exists(full)) return null;
                return full;
            }
            return null;
        }

        // A short name: the file name, with its folder when the name alone says little (run.py, main.py, index.js).
        public static string Label(string key)
        {
            if (key.StartsWith("module:"))
            {
                string rest = key.Substring(7);
                int bar = rest.IndexOf('|');
                return (bar < 0 ? rest : rest.Substring(0, bar)) + " (module)";
            }
            string name = System.IO.Path.GetFileName(key);
            string stem = System.IO.Path.GetFileNameWithoutExtension(key).ToLowerInvariant();
            string[] plain = { "run", "main", "index", "app", "server", "start", "bot", "script", "record", "__main__", "cli", "worker" };
            if (plain.Contains(stem))
            {
                string parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(key) ?? "");
                if (parent.Length > 0) return parent + "/" + name;
            }
            return name;
        }

        public static string Folder(string key)
        {
            if (key.StartsWith("module:")) { int bar = key.IndexOf('|'); return bar < 0 ? null : key.Substring(bar + 1); }
            return System.IO.Path.GetDirectoryName(key);
        }

        public static string Language(string exe)
        {
            string n = System.IO.Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            if (n.StartsWith("py")) return "Python";
            if (n == "node") return "Node";
            if (n == "bun") return "Bun";
            if (n == "deno") return "Deno";
            if (n == "powershell" || n == "pwsh") return "PowerShell";
            if (n == "wscript" || n == "cscript") return "Windows Script";
            if (n == "cmd") return "Batch";
            if (n.StartsWith("java")) return "Java";
            return char.ToUpperInvariant(n[0]) + n.Substring(1);
        }
    }

    // A scheduled task you (or your tools) made: not Windows' own, not an app's updater.
    class TaskInfo
    {
        public string Name, Path, Action, Script;
        public int State;            // 4 = running
        public int Result;
        public DateTime LastRun, NextRun;

        public bool Running { get { return State == 4; } }
        public bool NeverRan { get { return Result == 0x41303 || LastRun.Year < 2000; } }
        public bool Failed { get { return !Running && !NeverRan && Result != 0 && Result != 0x41306 && Result != 0x41301; } }
    }

    static class TaskList
    {
        public static List<TaskInfo> Items = new List<TaskInfo>();
        static bool busy;
        static DateTime read = DateTime.MinValue;

        // Reads Task Scheduler off the UI thread, at most every 30 s.
        public static void Refresh(Action done)
        {
            if (busy || (DateTime.Now - read).TotalSeconds < 30) return;
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var found = new List<TaskInfo>();
                try
                {
                    dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                    svc.Connect();
                    Walk(svc.GetFolder("\\"), found);
                }
                catch (Exception) { }
                Items = found;
                read = DateTime.Now;
                busy = false;
                if (done != null) done();
            });
        }

        static void Walk(dynamic folder, List<TaskInfo> found)
        {
            string path = (string)folder.Path;
            if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase)) return;
            foreach (dynamic t in folder.GetTasks(0))
            {
                try
                {
                    if (!(bool)t.Enabled) continue;
                    var info = new TaskInfo { Name = Regex.Replace((string)t.Name, "-S-1-5-[0-9-]+$", ""), Path = (string)t.Path, State = (int)t.State, Result = (int)t.LastTaskResult, LastRun = (DateTime)t.LastRunTime, NextRun = (DateTime)t.NextRunTime };
                    bool mine = false;
                    foreach (dynamic a in t.Definition.Actions)
                    {
                        if ((int)a.Type != 0) continue;   // only "start a program" actions
                        string exe = Environment.ExpandEnvironmentVariables(((string)a.Path ?? "").Trim('"'));
                        string args = Environment.ExpandEnvironmentVariables((string)a.Arguments ?? "");
                        info.Action = (exe + " " + args).Trim();
                        foreach (string arg in Scripts.SplitArgs(args))
                            if (System.IO.Path.IsPathRooted(arg) && Scripts.IsScriptFile(arg) && !Scripts.IsSystemPath(arg)) { info.Script = arg; break; }
                        if (info.Script == null && Scripts.IsScriptFile(exe) && !Scripts.IsSystemPath(exe)) info.Script = exe;
                        if (info.Script != null || (exe.Length > 0 && System.IO.Path.IsPathRooted(exe) && !Scripts.IsSystemPath(exe))) mine = true;
                    }
                    // App updaters (Zoom, Google, Adobe ...) are noise unless they run a script.
                    if (info.Script == null && info.Name.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0) mine = false;
                    if (mine) found.Add(info);
                }
                catch (Exception) { }
            }
            foreach (dynamic sub in folder.GetFolders(0)) Walk(sub, found);
        }
    }

    // Graphics load and memory from Windows' own counters, for cards without NVIDIA's library (AMD, Intel).
    static class GpuCounters
    {
        public static string Name;
        public static double Util, UsedGb, TotalGb;
        public static bool Have;
        static Dictionary<string, long> prevRaw = new Dictionary<string, long>();
        static long prevTs;
        static bool busy, tried;
        static DateTime read = DateTime.MinValue;

        public static void Refresh()
        {
            if (busy || (DateTime.Now - read).TotalSeconds < 2) return;
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (!tried) { tried = true; ReadAdapter(); }
                    var eng = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"];
                    var raw = new Dictionary<string, long>();
                    long ts = 0;
                    foreach (InstanceData d in eng.Values)
                    {
                        raw[d.InstanceName] = d.RawValue;
                        ts = Math.Max(ts, d.Sample.TimeStamp100nSec);
                    }
                    if (prevTs > 0 && ts > prevTs)
                    {
                        // Busy time per engine type, summed over processes; the busiest type is the card's load, as in Task Manager.
                        var byType = new Dictionary<string, double>();
                        foreach (var kv in raw)
                        {
                            long before;
                            if (!prevRaw.TryGetValue(kv.Key, out before) || kv.Value < before) continue;
                            var m = Regex.Match(kv.Key, "luid_(\\w+?_\\w+?)_phys.*engtype_(.+)$");
                            if (!m.Success) continue;
                            string k = m.Groups[1].Value + "/" + m.Groups[2].Value;
                            double have;
                            byType.TryGetValue(k, out have);
                            byType[k] = have + (kv.Value - before) * 100.0 / (ts - prevTs);
                        }
                        Util = byType.Count == 0 ? 0 : Math.Min(100, byType.Values.Max());
                    }
                    prevRaw = raw; prevTs = ts;
                    var mem = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory()["Dedicated Usage"];
                    long used = 0;
                    foreach (InstanceData d in mem.Values) used = Math.Max(used, d.RawValue);
                    UsedGb = used / 1073741824.0;
                    Have = Name != null || used > 0;
                }
                catch (Exception) { Have = false; }
                read = DateTime.Now;
                busy = false;
            });
        }

        // Name and size of the largest display adapter, from the driver's registry entry.
        static void ReadAdapter()
        {
            try
            {
                using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return;
                    foreach (string sub in cls.GetSubKeyNames())
                    {
                        try
                        {
                            using (var k = cls.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                object size = k.GetValue("HardwareInformation.qwMemorySize");
                                long bytes = size is long ? (long)size : size is byte[] && ((byte[])size).Length >= 8 ? BitConverter.ToInt64((byte[])size, 0) : 0;
                                if (bytes / 1073741824.0 <= TotalGb) continue;
                                TotalGb = bytes / 1073741824.0;
                                Name = ((string)k.GetValue("DriverDesc") ?? "GPU").Replace("NVIDIA ", "").Replace("GeForce ", "").Replace("AMD ", "").Replace("Radeon(TM) ", "Radeon ");
                            }
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }
    }

    // What a bot's check says: when it last showed signs of work, how long it has been up, crashes in the last day.
    class BotStatus
    {
        public DateTime? LastSign;     // newest heartbeat file write, or the time the http check answered
        public TimeSpan? Uptime;       // from the http check, when it reports one
        public string Problem;         // set when the http check answers but reports something failing
        public int Crashes = -1;
        public int CrashLine;          // line in the supervisor log where the latest crash in the last day is recorded
        public DateTime CrashTime;
        public bool Busy;
        public DateTime Read = DateTime.MinValue;

        public void Refresh(WatchRule r)
        {
            if (Busy || (DateTime.Now - Read).TotalSeconds < 10) return;
            Busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (r.Check.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ReadHttp(r.Check);
                    else LastSign = NewestWrite(r.Check);
                    if (r.Log != null) CountCrashes(r.Log, r.Crash);
                }
                catch (Exception) { }
                finally { Read = DateTime.Now; Busy = false; }
            });
        }

        void ReadHttp(string url)
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Timeout = 3000; req.ReadWriteTimeout = 3000; req.Proxy = null;
                string body;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream())) body = sr.ReadToEnd();
                LastSign = DateTime.Now;
                var up = Regex.Match(body, "\"uptimeMs\"\\s*:\\s*([0-9.]+)");
                Uptime = up.Success ? TimeSpan.FromMilliseconds(double.Parse(up.Groups[1].Value, CultureInfo.InvariantCulture)) : (TimeSpan?)null;
                var failing = Regex.Match(body, "\"failing\"\\s*:\\s*\\[([^\\]]*)\\]");
                string f = failing.Success ? failing.Groups[1].Value.Replace("\"", "").Trim() : "";
                bool ready = !Regex.IsMatch(body, "\"ready\"\\s*:\\s*false");
                Problem = f.Length > 0 ? "failing: " + f : ready ? null : "starting";
            }
            catch (Exception) { Uptime = null; Problem = null; }
        }

        // A file's last write, or for a folder the newest file in its newest subfolder (data\20261010\...).
        static DateTime? NewestWrite(string path)
        {
            if (File.Exists(path)) return File.GetLastWriteTime(path);
            if (!Directory.Exists(path)) return null;
            DateTime? newest = null;
            string dir = path;
            for (int depth = 0; depth < 4 && dir != null; depth++)
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    var t = File.GetLastWriteTime(f);
                    if (newest == null || t > newest) newest = t;
                }
                var subs = Directory.GetDirectories(dir);
                dir = subs.Length == 0 ? null : subs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Last();
            }
            return newest;
        }

        // A crash is a log line from the last day that starts with a timestamp and contains the crash text ("restart" by default).
        void CountCrashes(string log, string crashText)
        {
            if (!File.Exists(log)) { Crashes = -1; return; }
            var since = DateTime.Now.AddHours(-24);
            int n = 0, lineNo = 0, crashLine = 0;
            DateTime crashTime = DateTime.MinValue;
            using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    lineNo++;
                    if (line.Length < 19 || line.IndexOf(crashText, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    DateTime t;
                    string stamp = line.Substring(0, 19).Replace('T', ' ');
                    if (DateTime.TryParseExact(stamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) && t >= since)
                    {
                        n++;
                        crashLine = lineNo;
                        crashTime = t;
                    }
                }
            }
            CrashLine = crashLine; CrashTime = crashTime; Crashes = n;
        }
    }

    static class Theme
    {
        public static readonly Brush Text = Solid(0xFF, 0xF3, 0xF5, 0xF8);
        public static readonly Brush Sub = Solid(0x9E, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Faint = Solid(0x5C, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Card = Solid(0x14, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Hover = Solid(0x14, 0xFF, 0xFF, 0xFF);
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
        public static readonly Brush Edge = Gradient(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), false);
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

        public static string Gb(double bytes)
        {
            double gb = bytes / 1073741824.0;
            return gb >= 100 ? gb.ToString("0") : gb.ToString("0.0");
        }

        public static string Mem(long bytes)
        {
            double mb = bytes / 1048576.0;
            if (mb >= 1024) return (mb / 1024).ToString("0.0") + " GB";
            return mb.ToString("0") + " MB";
        }

        public static string Pct(double v)
        {
            if (v < 0.05) return "0%";
            if (v < 10) return v.ToString("0.0") + "%";
            return v.ToString("0") + "%";
        }
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
            Height = 4;
            var track = new Border();
            track.CornerRadius = new CornerRadius(2);
            track.Background = Theme.Track;
            fill = new Border();
            fill.CornerRadius = new CornerRadius(2);
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
        readonly TextBlock name, count, cpu, mem;
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
                return mark + name.Text + "  " + string.Join("  \u00B7  ", parts);
            }
        }

        public ProcRow()
        {
            Padding = new Thickness(6, 4, 6, 4);
            Margin = new Thickness(-6, 0, -6, 0);
            CornerRadius = new CornerRadius(8);
            Background = Brushes.Transparent;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
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
            nameLine.Children.Add(name); nameLine.Children.Add(count);
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

        void Fill(string t, string c, string cp, string m, Brush color, string tip)
        {
            if (name.Text != t) name.Text = t;
            if (name.Foreground != color) name.Foreground = color;
            countText = c;
            if (!hovering && count.Text != c) count.Text = c;
            if (cpu.Text != cp) cpu.Text = cp;
            if (mem.Text != m) mem.Text = m;
            if (!object.Equals(ToolTip, tip)) ToolTip = tip;
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

    class VitalsWindow : Window
    {
        const int TopCount = 9;
        static readonly string StateDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopVitals");
        static readonly string[] ModelExes = { "llama-server.exe", "llama-cli.exe", "ollama.exe", "ollama_llama_server.exe", "koboldcpp.exe", "lms.exe", "LM Studio.exe" };

        readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background);
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly int cores = Environment.ProcessorCount;
        readonly List<WatchRule> rules = WatchRule.Load().Where(r => !r.IsBot).ToList();
        readonly List<WatchRule> botRules = WatchRule.Load().Where(r => r.IsBot).ToList();
        readonly Dictionary<string, BotStatus> botStatus = new Dictionary<string, BotStatus>();
        Dictionary<int, Proc> prev = new Dictionary<int, Proc>();
        readonly Dictionary<long, string> cmdCache = new Dictionary<long, string>();
        long prevTicks, prevIdle, prevKernel, prevUser;
        double cpuTotal;
        int tickCount;
        bool live = true;
        bool sortByMemory;
        string diskDetail = "";
        double diskFrac;
        string diskValue = "";

        TextBlock subtitle, stateLabel, modelsEmpty;
        Border switchTrack;
        TranslateTransform knob;
        StackPanel body, modelsList, botList, watchList, topList;
        StatRow cpuRow, memRow, gpuRow, vramRow, diskRow;
        ThreadStrip threadStrip;
        StackPanel vramList;
        Dictionary<int, long> vramByPid = new Dictionary<int, long>();
        bool vramBusy;
        DateTime vramRead = DateTime.MinValue;
        readonly List<string> vramLines = new List<string>();
        long[] threadBusy, threadTotal, prevThreadBusy, prevThreadTotal;
        readonly List<ProcRow> modelRows = new List<ProcRow>();
        readonly List<ProcRow> watchRows = new List<ProcRow>();
        readonly List<ProcRow> botRows = new List<ProcRow>();
        readonly List<ProcRow> topRows = new List<ProcRow>();
        readonly List<ProcRow> scriptRows = new List<ProcRow>();
        readonly List<ProcRow> taskRows = new List<ProcRow>();
        StackPanel scriptList, taskList;
        Border modelsCard, scriptsCard, tasksCard;
        readonly Dictionary<long, string> scriptCache = new Dictionary<long, string>();
        readonly Dictionary<string, string> settings = WatchRule.Settings();
        static readonly string DiskRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory);

        readonly string snapshotDir;

        public VitalsWindow() : this(null) { }

        // With a folder, the window renders off screen, writes vitals.txt and vitals.png there, and closes.
        public VitalsWindow(string snapshotDir)
        {
            this.snapshotDir = snapshotDir;
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
                var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4500) };
                done.Tick += delegate { done.Stop(); WriteSnapshot(); Close(); };
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

        // ---------- layout ----------

        UIElement Build()
        {
            var root = new Grid();
            root.Background = snapshotDir == null && TransparencyOn() ? new SolidColorBrush(Color.FromArgb(0x70, 0x0E, 0x10, 0x16)) : new SolidColorBrush(Color.FromArgb(0xFF, 0x17, 0x1A, 0x21));
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            root.Children.Add(BuildHeader());

            body = new StackPanel { Margin = new Thickness(14, 2, 14, 14) };
            var scroll = new ScrollViewer();
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
            vramList = new StackPanel { Margin = new Thickness(0, -2, 0, 6) };
            body.Children.Add(Card("System", null, cpuRow, threadStrip, memRow, gpuRow, vramRow, vramList, diskRow));

            modelsList = new StackPanel();
            modelsEmpty = Theme.Label("No local models running", 12.5, Theme.Faint, FontWeights.Normal);
            modelsEmpty.Margin = new Thickness(0, 2, 0, 2);
            modelsList.Children.Add(modelsEmpty);
            modelsCard = Card("Models", Columns(Head("CPU"), Head("Memory")), modelsList);
            modelsCard.Visibility = Visibility.Collapsed;
            body.Children.Add(modelsCard);

            botList = new StackPanel();
            if (botRules.Count > 0) body.Children.Add(Card("Bots", Columns(Head("CPU"), Head("Last work")), botList));
            scriptList = new StackPanel();
            scriptsCard = Card("Scripts", Columns(Head("CPU"), Head("Memory")), scriptList);
            scriptsCard.Visibility = Visibility.Collapsed;
            body.Children.Add(scriptsCard);

            taskList = new StackPanel();
            tasksCard = Card("Scheduled tasks", Columns(Head("Result"), Head("Last run")), taskList);
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
            var sw = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 4, 8, 0) };
            stateLabel = Theme.Label("Live", 12, Theme.Sub, FontWeights.Normal);
            stateLabel.Margin = new Thickness(0, 0, 8, 0);
            stateLabel.Width = 26;
            switchTrack = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(11), Background = Theme.Green, VerticalAlignment = VerticalAlignment.Center };
            var k = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
            knob = new TranslateTransform(20, 0);
            k.RenderTransform = knob;
            k.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.3 };
            switchTrack.Child = new Grid { Margin = new Thickness(0), Children = { k }, VerticalAlignment = VerticalAlignment.Center };
            k.Margin = new Thickness(0, 0, 0, 0);
            sw.Children.Add(stateLabel);
            sw.Children.Add(switchTrack);
            sw.ToolTip = "Turn readings on or off";
            sw.MouseLeftButtonUp += delegate { SetLive(!live); };
            WindowChrome.SetIsHitTestVisibleInChrome(sw, true);
            right.Children.Add(sw);

            right.Children.Add(CaptionButton("\uE9D9", "Open Task Manager", delegate
            {
                try { using (Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })) { } }
                catch (Exception) { } // declined the admin prompt
            }));
            right.Children.Add(CaptionButton("\uE921", "Minimize", delegate { WindowState = WindowState.Minimized; }));
            right.Children.Add(CaptionButton("\uE8BB", "Close", delegate { Close(); }));
            g.Children.Add(right);
            return g;
        }

        UIElement CaptionButton(string glyph, string tip, MouseButtonEventHandler click)
        {
            var b = new Border { Width = 32, Height = 30, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip };
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

        // Sits over a list's two number columns, which are 56 and 72 wide in ProcRow.
        static Grid Columns(TextBlock a, TextBlock b)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
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
            head.Children.Add(Theme.Label(heading, 12, Theme.Sub, FontWeights.SemiBold));
            if (extra is Grid) { Grid.SetColumnSpan(extra, 2); head.Children.Add(extra); }
            else if (extra != null) { Grid.SetColumn(extra, 1); head.Children.Add(extra); }
            stack.Children.Add(head);
            foreach (var r in rows) stack.Children.Add(r);
            return new Border
            {
                Background = Theme.Card,
                BorderBrush = Theme.Edge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 11, 14, 10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = stack
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
                if (!live) subtitle.Text = "Off \u00B7 not collecting";
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

            ShowVram(procs);

            var ms = new Native.MEMSTAT { len = (uint)Marshal.SizeOf(typeof(Native.MEMSTAT)) };
            Native.GlobalMemoryStatusEx(ref ms);
            double used = ms.total - ms.avail;
            memRow.Set(Theme.Gb(used) + " of " + Theme.Gb(ms.total) + " GB", Theme.Pct(100 * used / ms.total), used / ms.total);

            double gu, vu, vt, gw; int gt;
            if (Nvml.Read(out gu, out vu, out vt, out gt, out gw))
            {
                gpuRow.Set((Nvml.Name ?? "GPU") + " \u00B7 " + gt + "\u00B0C \u00B7 " + gw.ToString("0") + " W", Theme.Pct(gu), gu / 100);
                vramRow.Set(vu.ToString("0.0") + " of " + vt.ToString("0") + " GB", Theme.Pct(100 * vu / vt), vu / vt);
            }
            else
            {
                GpuCounters.Refresh();
                if (GpuCounters.Have)
                {
                    gpuRow.Set(GpuCounters.Name ?? "GPU", Theme.Pct(GpuCounters.Util), GpuCounters.Util / 100);
                    double t = GpuCounters.TotalGb, u = GpuCounters.UsedGb;
                    vramRow.Set(t > 0 ? u.ToString("0.0") + " of " + t.ToString("0") + " GB" : u.ToString("0.0") + " GB", t > 0 ? Theme.Pct(100 * u / t) : "", t > 0 ? u / t : 0);
                }
                else
                {
                    gpuRow.Set("not available", "", 0);
                    vramRow.Set("not available", "", 0);
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
            subtitle.Text = "Up " + upText + " \u00B7 " + procs.Count + " processes";

            // Models
            var models = new List<KeyValuePair<string, Group>>();
            foreach (var p in procs)
            {
                string label = ModelLabel(p);
                if (label == null) continue;
                var g = new Group { Key = label, Cpu = p.Cpu, Mem = p.Private };
                g.Items.Add(p);
                models.Add(new KeyValuePair<string, Group>(label, g));
            }
            modelsEmpty.Visibility = models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            modelsCard.Visibility = models.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EnsureRows(modelsList, modelRows, models.Count);
            for (int i = 0; i < models.Count; i++)
            {
                var g = models[i].Value;
                var id = AppIdentity.For(g.Items[0].Exe, g.Pids);
                modelRows[i].SetApp(id.Icon, models[i].Key, 1, g.Cpu, g.Mem, g.Items[0].Cmd);
                modelRows[i].Pids = g.Pids;
            }

            // Watching
            EnsureRows(watchList, watchRows, rules.Count);
            for (int i = 0; i < rules.Count; i++)
            {
                var hits = procs.Where(rules[i].Hit).ToList();
                var tip = hits.Count == 0 ? null : string.Join("\n", hits.Select(h => h.Pid + "  " + Trim(h.Cmd.Length > 0 ? h.Cmd : h.Exe, 140)));
                watchRows[i].SetWatched(rules[i].Label, hits.Count > 0, hits.Count, hits.Sum(h => h.Cpu), hits.Sum(h => h.Private), tip);
                watchRows[i].Pids = hits.Select(h => h.Pid).ToArray();
            }

            // Bots
            EnsureRows(botList, botRows, botRules.Count);
            for (int i = 0; i < botRules.Count; i++)
            {
                var r = botRules[i];
                BotStatus st;
                if (!botStatus.TryGetValue(r.Label, out st)) botStatus[r.Label] = st = new BotStatus();
                st.Refresh(r);
                var hits = procs.Where(r.Hit).ToList();
                var tip = hits.Count == 0 ? null : string.Join("\n", hits.Select(h => h.Pid + "  " + Trim(h.Cmd.Length > 0 ? h.Cmd : h.Exe, 140)));
                botRows[i].Pids = hits.Select(h => h.Pid).ToArray();
                botRows[i].Bot = r;
                if (r.Code != null)
                {
                    string how = st.Crashes > 0 && r.Log != null ? OpenInEditor + " at the crash from " + st.CrashTime.ToString("ddd h:mm tt") : OpenInEditor;
                    tip = tip == null ? how : how + "\n\n" + tip;
                }
                if (hits.Count == 0) { botRows[i].SetBot(r.Label, 0, "", "", "not running", null); continue; }

                TimeSpan botUp = st.Uptime ?? (DateTime.UtcNow - DateTime.FromFileTimeUtc(hits.Max(h => h.CreateTime)));
                var bits = new List<string> { "up " + Ago(botUp) };
                if (st.Problem != null) bits.Add(st.Problem);
                else if (st.Crashes == 0) bits.Add("no crashes");
                else if (st.Crashes > 0) bits.Add(st.Crashes + (st.Crashes == 1 ? " crash" : " crashes") + " in 24h");
                double quiet = st.LastSign.HasValue ? (DateTime.Now - st.LastSign.Value).TotalSeconds : -1;
                bool stale = st.Read != DateTime.MinValue && (quiet < 0 || quiet > 300);
                string right = st.Read == DateTime.MinValue ? "" : quiet < 0 ? "no sign" : quiet < 60 ? "just now" : Short(TimeSpan.FromSeconds(quiet)) + " ago";
                botRows[i].SetBot(r.Label, stale || st.Problem != null ? 2 : 1, string.Join(" \u00B7 ", bits), Theme.Pct(hits.Sum(h => h.Cpu)), right, tip);
            }

            // Scripts: anything an interpreter is running from a script file of yours, one line per script.
            var claimed = new HashSet<int>(botRules.SelectMany(r => procs.Where(r.Hit)).Select(p => p.Pid));
            var byScript = new Dictionary<string, List<Proc>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in procs)
            {
                if (claimed.Contains(p.Pid)) continue;
                long key = ((long)p.Pid << 32) ^ p.CreateTime;
                string script;
                if (!scriptCache.TryGetValue(key, out script))
                {
                    int pid = p.Pid;
                    script = Scripts.Find(p, () => Procs.CurrentDirectory(pid));
                    scriptCache[key] = script;
                }
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
            var scripts = byScript.OrderBy(kv => Scripts.Label(kv.Key), StringComparer.OrdinalIgnoreCase).Take(12).ToList();
            scriptsCard.Visibility = scripts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EnsureRows(scriptList, scriptRows, scripts.Count);
            for (int i = 0; i < scripts.Count; i++)
            {
                var items = scripts[i].Value;
                TimeSpan sUp = DateTime.UtcNow - DateTime.FromFileTimeUtc(items.Min(h => h.CreateTime));
                string tip = scripts[i].Key.StartsWith("module:") ? Scripts.Folder(scripts[i].Key) : scripts[i].Key;
                tip += "\n\n" + string.Join("\n", items.Select(h => h.Pid + "  " + Trim(h.Cmd, 140)));
                scriptRows[i].Script = scripts[i].Key;
                scriptRows[i].Pids = items.Select(h => h.Pid).ToArray();
                scriptRows[i].SetPlain(Scripts.Label(scripts[i].Key), Theme.Green, Scripts.Language(items[0].Exe) + " \u00B7 up " + Ago(sUp),
                    Theme.Pct(items.Sum(h => h.Cpu)), Theme.Mem(items.Sum(h => h.Private)), tip);
            }

            // Scheduled tasks: running first, then failures, then the most recent runs.
            TaskList.Refresh(null);
            var tasks = TaskList.Items.OrderByDescending(t => t.Running).ThenByDescending(t => t.Failed).ThenByDescending(t => t.LastRun).Take(8).ToList();
            tasksCard.Visibility = tasks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EnsureRows(taskList, taskRows, tasks.Count);
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                string result = t.Running ? "running" : t.NeverRan ? "\u2013" : t.Result == 0x41306 ? "stopped" : t.Failed ? "failed" : "OK";
                string last = t.NeverRan ? "never" : Short(DateTime.Now - t.LastRun) + " ago";
                string next = t.NextRun > DateTime.Now ? "next in " + Ago(t.NextRun - DateTime.Now) : "";
                string tip = t.Path + "\n" + Trim(t.Action ?? "", 160) + (t.Failed ? "\nLast result: 0x" + t.Result.ToString("X") : "");
                taskRows[i].Task = t;
                taskRows[i].SetPlain(t.Name, t.Running ? Theme.Green : t.Failed ? Theme.AmberDot : Theme.Off, next, result, last, tip);
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
        }

        static string Trim(string s, int n) { return s.Length <= n ? s : s.Substring(0, n) + "\u2026"; }

        // One unit, for narrow columns: 45s, 12m, 13h, 2d.
        static string Short(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return Math.Max(0, (int)t.TotalSeconds) + "s";
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + "m";
            if (t.TotalHours < 24) return (int)t.TotalHours + "h";
            return (int)t.TotalDays + "d";
        }

        static string Ago(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return Math.Max(0, (int)t.TotalSeconds) + "s";
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + "m";
            if (t.TotalHours < 24) return (int)t.TotalHours + "h " + t.Minutes.ToString("00") + "m";
            return (int)t.TotalDays + "d " + t.Hours + "h";
        }

        // Optional: "@temperature-file" points at a JSON file a sensor tool keeps writing, with "cpu_temp_c",
        // "cpu_pkg_power_w" and "ts". "@temperature-task" is a scheduled task to start when the file goes stale.
        string SensorDetail()
        {
            string file;
            if (!settings.TryGetValue("temperature-file", out file)) return "";
            try
            {
                if (!File.Exists(file)) return "temperature sensor off";
                string json;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) json = sr.ReadToEnd();
                var ts = Regex.Match(json, "\"ts\"\\s*:\\s*\"([^\"]+)\"");
                DateTimeOffset when;
                if (!ts.Success || !DateTimeOffset.TryParse(ts.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out when)) return "";
                if ((DateTimeOffset.Now - when).TotalSeconds > 60) { WakeSensor(); return "temperature startingâ€¦"; }
                var t = Regex.Match(json, "\"cpu_temp_c\"\\s*:\\s*([0-9.]+)");
                var w = Regex.Match(json, "\"cpu_pkg_power_w\"\\s*:\\s*([0-9.]+)");
                var parts = new List<string>();
                if (t.Success) parts.Add(double.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture).ToString("0") + "\u00B0C");
                if (w.Success) parts.Add(double.Parse(w.Groups[1].Value, CultureInfo.InvariantCulture).ToString("0") + " W");
                return string.Join(" \u00B7 ", parts);
            }
            catch (Exception) { return ""; }
        }

        // While this window is live, start the sensor task again when its file goes quiet.
        DateTime lastWake = DateTime.MinValue;
        void WakeSensor()
        {
            string task;
            if (!settings.TryGetValue("temperature-task", out task) || (DateTime.Now - lastWake).TotalMinutes < 2) return;
            lastWake = DateTime.Now;
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/run /tn \"" + task + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                using (Process.Start(psi)) { }
            }
            catch (Exception) { }
        }

        // Local model servers: llama.cpp, Ollama, LM Studio, koboldcpp, vLLM.
        static string ModelLabel(Proc p)
        {
            string exe = p.Exe;
            bool known = ModelExes.Any(x => string.Equals(x, exe, StringComparison.OrdinalIgnoreCase));
            bool pyServer = exe.StartsWith("python", StringComparison.OrdinalIgnoreCase)
                && (p.Cmd.IndexOf("vllm", StringComparison.OrdinalIgnoreCase) >= 0 || p.Cmd.IndexOf("llama_cpp.server", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!known && !pyServer) return null;
            var args = SplitArgs(p.Cmd);
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

        static List<string> SplitArgs(string cmd)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            foreach (char c in cmd)
            {
                if (c == '"') { quoted = !quoted; continue; }
                if (c == ' ' && !quoted) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } continue; }
                sb.Append(c);
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list;
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

        // Clicking a line never ends anything. It brings that app's window to the front,
        // cycling through its windows on repeat clicks, or shows its file in Explorer when it has none.
        IntPtr lastJump = IntPtr.Zero;
        // Cursor or VS Code, whichever is on PATH.
        static readonly string[] Editor = FindEditor();
        static string[] FindEditor()
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
            foreach (var e in new[] { new[] { "cursor", "Cursor" }, new[] { "code", "VS Code" } })
                if (dirs.Any(d => { try { return File.Exists(System.IO.Path.Combine(d.Trim(), e[0] + ".cmd")); } catch (Exception) { return false; } })) return e;
            return null;
        }
        static string OpenInEditor { get { return Editor == null ? "Show in File Explorer" : "Open in " + Editor[1]; } }

        string Hint(ProcRow row)
        {
            if (row.Task != null) return row.Task.Script != null ? OpenInEditor : "Open Task Scheduler";
            if (row.Bot != null && row.Bot.Code != null) return OpenInEditor;
            if (row.Pids.Length == 0) return "";
            if (Native.AppWindows(row.Pids).Count > 0) return "Switch to window";
            if (row.Script != null || ScriptOf(row.Pids) != null) return OpenInEditor;
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

        // Opens a folder (and a file in it) in the editor, or in File Explorer when there is no editor.
        static void OpenCode(string folder, string file, int line)
        {
            if (Editor == null)
            {
                string target = file ?? folder;
                try { using (Process.Start("explorer.exe", (file != null ? "/select,\"" : "\"") + target + "\"")) { } } catch (Exception) { }
                return;
            }
            string args = "\"" + folder + "\"";
            if (file != null) args += line > 0 ? " -g \"" + file + ":" + line + "\"" : " \"" + file + "\"";
            RunEditor(args);
        }

        static void RunEditor(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + Editor[0] + " " + args) { CreateNoWindow = true, UseShellExecute = false };
                using (Process.Start(psi)) { }
            }
            catch (Exception) { }
        }

        void GoTo(ProcRow row)
        {
            if (row.Task != null)
            {
                if (row.Task.Script != null) OpenCode(System.IO.Path.GetDirectoryName(row.Task.Script), row.Task.Script, 0);
                else try { using (Process.Start(new ProcessStartInfo("taskschd.msc") { UseShellExecute = true })) { } } catch (Exception) { }
                return;
            }
            if (row.Bot != null && row.Bot.Code != null) { OpenBot(row.Bot); return; }
            if (row.Pids.Length == 0) return;
            var wins = Native.AppWindows(row.Pids);
            if (wins.Count == 0)
            {
                if (row.Script != null)
                {
                    bool module = row.Script.StartsWith("module:");
                    OpenCode(Scripts.Folder(row.Script), module ? null : row.Script, 0);
                    return;
                }
                string script = ScriptOf(row.Pids);
                if (script != null) { OpenCode(System.IO.Path.GetDirectoryName(script), script, 0); return; }
            }
            if (wins.Count > 0)
            {
                IntPtr h = wins[(wins.IndexOf(lastJump) + 1) % wins.Count];
                lastJump = h;
                if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SwRestore);
                Native.SetForegroundWindow(h);
                return;
            }
            string path = row.Pids.Select(Procs.ImagePath).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            if (path == null) return;
            try { using (Process.Start("explorer.exe", "/select,\"" + path + "\"")) { } }
            catch (Exception) { }
        }

        // The bot's folder in the editor, with its log open at the latest crash when there was one in the last day.
        void OpenBot(WatchRule bot)
        {
            BotStatus st;
            botStatus.TryGetValue(bot.Label, out st);
            bool crash = st != null && st.Crashes > 0 && bot.Log != null && st.CrashLine > 0;
            OpenCode(bot.Code, crash ? bot.Log : null, crash ? st.CrashLine : 0);
        }

        // Dedicated video memory per process, from Windows' "GPU Process Memory" counters. Read off the UI thread every 5 s.
        void ShowVram(List<Proc> procs)
        {
            if (!vramBusy && (DateTime.Now - vramRead).TotalSeconds >= 5)
            {
                vramBusy = true;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var next = new Dictionary<int, long>();
                    try
                    {
                        var data = new PerformanceCounterCategory("GPU Process Memory").ReadCategory();
                        var usage = data["Local Usage"];
                        if (usage != null)
                            foreach (InstanceData d in usage.Values)
                            {
                                var m = Regex.Match(d.InstanceName, "^pid_(\\d+)_");
                                if (!m.Success || d.RawValue <= 0) continue;
                                int pid = int.Parse(m.Groups[1].Value);
                                long have;
                                next.TryGetValue(pid, out have);
                                next[pid] = have + d.RawValue;
                            }
                    }
                    catch (Exception) { }
                    Dispatcher.BeginInvoke(new Action(delegate { vramByPid = next; vramRead = DateTime.Now; vramBusy = false; }));
                });
            }

            var byExe = new Dictionary<string, List<Proc>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in procs)
            {
                long b;
                if (!vramByPid.TryGetValue(p.Pid, out b) || b <= 0) continue;
                List<Proc> l;
                if (!byExe.TryGetValue(p.Exe, out l)) byExe[p.Exe] = l = new List<Proc>();
                l.Add(p);
            }
            var top = byExe.Select(kv => new { Exe = kv.Key, Pids = kv.Value.Select(x => x.Pid).ToArray(), Bytes = kv.Value.Sum(x => vramByPid[x.Pid]) })
                .Where(x => x.Bytes >= 32L << 20).OrderByDescending(x => x.Bytes).Take(4).ToList();

            while (vramList.Children.Count < top.Count)
            {
                var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var n = Theme.Label("", 12, Theme.Sub, FontWeights.Normal);
                var v = Theme.Label("", 12, Theme.Sub, FontWeights.Normal);
                Grid.SetColumn(v, 1);
                g.Children.Add(n); g.Children.Add(v);
                vramList.Children.Add(g);
            }
            vramLines.Clear();
            for (int i = 0; i < vramList.Children.Count; i++)
            {
                var g = (Grid)vramList.Children[i];
                g.Visibility = i < top.Count ? Visibility.Visible : Visibility.Collapsed;
                if (i >= top.Count) continue;
                string name = AppIdentity.For(top[i].Exe, top[i].Pids).Title;
                string amount = Theme.Mem(top[i].Bytes);
                var n = (TextBlock)g.Children[0]; var v = (TextBlock)g.Children[1];
                if (n.Text != name) n.Text = name;
                if (v.Text != amount) v.Text = amount;
                vramLines.Add(name + "  " + amount);
            }
        }

        // ---------- snapshot for other programs (scripts, AI agents) ----------

        void WriteSnapshot()
        {
            try
            {
                Directory.CreateDirectory(snapshotDir);
                var sb = new StringBuilder();
                sb.AppendLine("Desktop Vitals  " + Environment.MachineName + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "  \u00B7  " + subtitle.Text);
                sb.AppendLine();
                foreach (var r in new[] { cpuRow, memRow, gpuRow, vramRow, diskRow }) sb.AppendLine(r.Summary);
                if (vramLines.Count > 0) sb.AppendLine("Video memory by app: " + string.Join("  \u00B7  ", vramLines));
                string busy = threadStrip.Busiest();
                if (busy.Length > 0) sb.AppendLine("Busiest thread: " + busy);
                Section(sb, "Models", modelRows, "");
                Section(sb, "Bots", botRows, "");
                Section(sb, "Scripts", scriptRows, "none running");
                Section(sb, "Scheduled tasks", taskRows, "");
                Section(sb, "Watching", watchRows, "");
                Section(sb, "Top apps", topRows, "");
                string text = sb.ToString();
                File.WriteAllText(System.IO.Path.Combine(snapshotDir, "vitals.txt"), text, new UTF8Encoding(false));

                var root = (FrameworkElement)Content;
                double scale = 2;
                var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bmp.Render(root);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(System.IO.Path.Combine(snapshotDir, "vitals.png"))) png.Save(fs);

                try { Console.Out.Write(text); Console.Out.Flush(); } catch (Exception) { }
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(System.IO.Path.Combine(snapshotDir, "vitals.txt"), "Snapshot failed: " + ex.Message); } catch (Exception) { }
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

        // Second launch (the Ctrl+Alt+P shortcut): bring the window up, or tuck it away if it is already in front.
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

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            int at = Array.FindIndex(args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                string dir = at + 1 < args.Length ? args[at + 1] : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopVitals", "snapshot");
                var snapApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                snapApp.DispatcherUnhandledException += (s, e) => { e.Handled = true; };
                snapApp.Run(new VitalsWindow(System.IO.Path.GetFullPath(dir)));
                return;
            }
            bool created;
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DesktopVitals.Show", out created))
            {
                if (!created) { signal.Set(); return; }
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                // One bad reading (an odd process, a driver hiccup) should skip a refresh, not close the window.
                app.DispatcherUnhandledException += (s, e) => { e.Handled = true; };
                var win = new VitalsWindow();
                ThreadPool.RegisterWaitForSingleObject(signal, delegate { win.Dispatcher.BeginInvoke(new Action(win.Toggle)); }, null, -1, false);
                app.Run(win);
            }
        }
    }
}
