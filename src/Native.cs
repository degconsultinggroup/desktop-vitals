// P/Invoke: process snapshot, memory, DWM glass, NVML.
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
        [DllImport("kernel32.dll")] static extern bool GetLogicalProcessorInformationEx(int rel, IntPtr buf, ref int len);

        // Logical processors by physical core, fastest first: (efficiency class, thread numbers).
        static List<KeyValuePair<int, int[]>> FlatCores(int n)
        {
            var list = new List<KeyValuePair<int, int[]>>(n);
            for (int i = 0; i < n; i++) list.Add(new KeyValuePair<int, int[]>(0, new[] { i }));
            return list;
        }

        public static List<KeyValuePair<int, int[]>> CoreLayout()
        {
            int n = Math.Max(1, Environment.ProcessorCount);
            // One 64-bit group mask only; beyond that, flat bars without hybrid labels.
            if (n > 64) return FlatCores(n);
            var cores = new List<KeyValuePair<int, int[]>>();
            bool multiGroup = false;
            int len = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            if (len <= 0) return FlatCores(n);
            IntPtr b = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetLogicalProcessorInformationEx(0, b, ref len)) return FlatCores(n);
                for (int off = 0; off < len; )
                {
                    int size = Marshal.ReadInt32(b, off + 4);
                    int eff = Marshal.ReadByte(b, off + 9);
                    int groupCount = Marshal.ReadInt16(b, off + 30) & 0xffff;
                    if (groupCount > 1) multiGroup = true;
                    long mask = Marshal.ReadInt64(b, off + 32);
                    int group = Marshal.ReadInt16(b, off + 40) & 0xffff;
                    if (group != 0) multiGroup = true;
                    var threads = new List<int>();
                    for (int i = 0; i < 64; i++) if ((mask & (1L << i)) != 0) threads.Add(i);
                    if (threads.Count > 0) cores.Add(new KeyValuePair<int, int[]>(eff, threads.ToArray()));
                    off += size;
                }
            }
            finally { Marshal.FreeHGlobal(b); }
            if (multiGroup || cores.Count == 0) return FlatCores(n);
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

        public const int QueryLimited = 0x1000;
        public const int StatusLengthMismatch = unchecked((int)0xC0000004);
        public const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    }

    // NVIDIA NVML via the driver. Missing or failing calls leave GPU rows blank.

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
            util = usedGb = totalGb = watts = -1; temp = -1;
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
                return util >= 0 || totalGb >= 0;
            }
            catch (Exception) { ready = false; return false; }
        }
    }

}
