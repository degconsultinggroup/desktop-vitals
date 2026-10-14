// Facts the window reads: processes, scripts, models, tasks, GPU counters.
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
    class Proc
    {
        public int Pid;
        public string Exe;        // image name, e.g. chrome.exe
        public long CpuTime;      // 100 ns units, user + kernel
        public long CreateTime;
        public long Private;      // private working set, what Task Manager calls Memory
        public long Committed;    // private bytes, held even when Windows pages an idle process out of RAM
        public double Cpu;       // percent of the whole machine
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
                    pr.Committed = Marshal.ReadInt64(p, 200);   // PrivatePageCount
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
                if ((st == Native.StatusLengthMismatch || st == Native.StatusBufferTooSmall) && ret > len && ret <= 1024 * 1024)
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

        // PEB current directory: display-only, PROCESS_VM_READ, skip on failure; never execute.
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
                if (len == 0 || len > 32 * 1024 || bufAddr == 0) return null;
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

    // Interpreter script: python/node/powershell -File, or python -m.
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
            System.IO.Path.GetTempPath(),   // throwaway scripts tools write and run once
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

        // Edge, updaters, OEM helpers, and svchost: Windows noise, not something you run yourself.
        public static bool IsWindowsNoise(string name, string path)
        {
            string s = ((name ?? "") + "\n" + (path ?? "")).ToLowerInvariant();
            if (s.IndexOf("svchost") >= 0) return true;
            if (s.IndexOf("msedge") >= 0 || s.IndexOf("edgeupdate") >= 0 || s.IndexOf("\\microsoft\\edge") >= 0) return true;
            if (s.IndexOf("microsoft edge") >= 0 || (s.IndexOf("edge") >= 0 && s.IndexOf("autolaunch") >= 0)) return true;
            if (s.IndexOf("googleupdate") >= 0 || (s.IndexOf("zoom") >= 0 && s.IndexOf("update") >= 0)) return true;
            if (s.IndexOf("dellsupport") >= 0 || s.IndexOf("lenovo") >= 0 || s.IndexOf("hpsupport") >= 0) return true;
            if (s.IndexOf("asus") >= 0 && s.IndexOf("system") >= 0) return true;
            return false;
        }
    }

    // A model on disk: one weights file (or its shards), or one folder of them.

    class InstalledModel
    {
        public string Name, Path, Where;
        public long Bytes;
        public int Files;
        public DateTime Added;
        public long WeightBytes;   // the weights alone, for spotting the same model kept in two places
        public bool Beside;        // found in a running script's own model folder
        public List<string> PathKeys = new List<string>();   // looked for in command lines and the config files they name
        public List<string> NameKeys = new List<string>();   // looked for in the source of a script using the GPU
    }

    // Installed models: drive/user/AppData models folders, beside scripts, HF/Ollama/LM Studio, @models in watch.txt.

    static class ModelStore
    {
        static readonly string[] WeightExts = { ".gguf", ".safetensors", ".ggml", ".onnx", ".bin", ".pt", ".pth", ".ckpt" };
        static readonly string[] PlainNames = { "model", "models", "weights", "checkpoint", "checkpoints" };
        const long MinWeights = 20L << 20;   // smaller .bin and .pt files are usually not weights

        public static string Norm(string s) { return (s ?? "").Replace("\\\\", "\\").Replace('/', '\\').ToLowerInvariant(); }

        static bool IsWeights(FileInfo f)
        {
            string ext = f.Extension.ToLowerInvariant();
            if (!WeightExts.Contains(ext)) return false;
            return ext == ".gguf" || ext == ".safetensors" || f.Length >= MinWeights;
        }

        // beside: folders of running scripts, whose own "model" or "models" folder counts.
        public static List<InstalledModel> Scan(IEnumerable<string> extra, IEnumerable<string> beside)
        {
            var list = new List<InstalledModel>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new List<string>(extra);
            foreach (var d in DriveInfo.GetDrives())
                try { if (d.DriveType == DriveType.Fixed && d.IsReady) roots.Add(System.IO.Path.Combine(d.RootDirectory.FullName, "models")); } catch (Exception) { }
            roots.Add(System.IO.Path.Combine(home, "models"));
            foreach (var app in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
                try
                {
                    foreach (var d in new DirectoryInfo(Environment.GetFolderPath(app)).EnumerateDirectories())
                        if ((d.Attributes & FileAttributes.ReparsePoint) == 0 && !d.Name.Equals("Temp", StringComparison.OrdinalIgnoreCase))
                            roots.Add(System.IO.Path.Combine(d.FullName, "models"));
                }
                catch (Exception) { }
            roots.Add(System.IO.Path.Combine(home, ".lmstudio", "models"));
            roots.Add(System.IO.Path.Combine(home, ".cache", "lm-studio", "models"));
            string hf = Environment.GetEnvironmentVariable("HF_HUB_CACHE");
            if (string.IsNullOrEmpty(hf))
            {
                string hfHome = Environment.GetEnvironmentVariable("HF_HOME");
                hf = System.IO.Path.Combine(string.IsNullOrEmpty(hfHome) ? System.IO.Path.Combine(home, ".cache", "huggingface") : hfHome, "hub");
            }
            string ollama = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
            if (string.IsNullOrEmpty(ollama)) ollama = System.IO.Path.Combine(home, ".ollama", "models");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in roots)
            {
                string root;
                try { root = System.IO.Path.GetFullPath(r).TrimEnd('\\'); } catch (Exception) { continue; }
                if (!seen.Add(root) || !Directory.Exists(root)) continue;
                try { Folder(new DirectoryInfo(root), root, 0, list); } catch (Exception) { }
            }
            // A script's own model folder: one model when the weights sit right in it (encoder + decoder files), else a shelf.
            foreach (string b in beside)
                foreach (string name in new[] { "model", "models" })
                {
                    string root;
                    try { root = System.IO.Path.GetFullPath(System.IO.Path.Combine(b, name)).TrimEnd('\\'); } catch (Exception) { continue; }
                    if (!seen.Add(root) || !Directory.Exists(root)) continue;
                    try
                    {
                        var dir = new DirectoryInfo(root);
                        int before = list.Count;
                        if (dir.EnumerateFiles().Any(IsWeights)) { var m = FolderModel(dir, b); if (m != null) list.Add(m); }
                        else Folder(dir, root, 0, list);
                        for (int i = before; i < list.Count; i++) list[i].Beside = true;
                    }
                    catch (Exception) { }
                }
            try { if (Directory.Exists(hf) && seen.Add(hf)) HuggingFace(hf, list); } catch (Exception) { }
            try { if (Directory.Exists(ollama) && seen.Add(ollama)) Ollama(ollama, list); } catch (Exception) { }
            return Merge(list);
        }

        // Same weights in two places stay one model; prefer the installed copy's name.
        static List<InstalledModel> Merge(List<InstalledModel> list)
        {
            var output = new List<InstalledModel>();
            foreach (var g in list.GroupBy(m => m.WeightBytes > 0 ? m.WeightBytes + ":" + m.Files : m.Path, StringComparer.OrdinalIgnoreCase))
            {
                var keep = g.OrderBy(m => m.Beside ? 1 : 0).ThenBy(m => m.Added).First();
                foreach (var m in g)
                {
                    if (m == keep) continue;
                    keep.PathKeys.AddRange(m.PathKeys.Where(k => !keep.PathKeys.Contains(k)));
                    keep.NameKeys.AddRange(m.NameKeys.Where(k => !keep.NameKeys.Contains(k)));
                }
                output.Add(keep);
            }
            return output;
        }

        // One folder of models: shelf of weight-subfolders, else each weights file or subfolder is a model.
        static void Folder(DirectoryInfo dir, string root, int depth, List<InstalledModel> list)
        {
            var shards = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in dir.EnumerateFiles())
            {
                if (!IsWeights(f)) continue;
                string key = Regex.Replace(f.Name, "-\\d{5}-of-\\d{5}(?=\\.[a-z]+$)", "", RegexOptions.IgnoreCase);
                List<FileInfo> l;
                if (!shards.TryGetValue(key, out l)) shards[key] = l = new List<FileInfo>();
                l.Add(f);
            }
            foreach (var kv in shards)
            {
                var m = new InstalledModel { Name = Pretty(System.IO.Path.GetFileNameWithoutExtension(kv.Key)), Path = kv.Value[0].FullName, Where = root };
                m.Bytes = m.WeightBytes = kv.Value.Sum(f => f.Length);
                m.Files = kv.Value.Count;
                m.Added = kv.Value.Max(f => f.CreationTime);
                foreach (var f in kv.Value) m.PathKeys.Add(Norm(f.FullName));
                m.NameKeys.Add(Norm(System.IO.Path.GetFileNameWithoutExtension(kv.Key)));
                list.Add(m);
            }
            foreach (var sub in dir.EnumerateDirectories())
            {
                if (sub.Name.StartsWith(".") || (sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (depth < 2 && IsShelf(sub)) { Folder(sub, root, depth + 1, list); continue; }
                var m = FolderModel(sub, root);
                if (m != null) list.Add(m);
            }
        }

        // One folder of weights as one model, or null when it holds none. BFS skips reparse points (no symlink hang).
        static InstalledModel FolderModel(DirectoryInfo sub, string root)
        {
            var weights = new List<FileInfo>(); long bytes = 0; int n = 0;
            var q = new Queue<DirectoryInfo>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (q.Enqueue(sub); q.Count > 0 && n <= 20000; )
            {
                var dir = q.Dequeue(); string full; try { full = dir.FullName; } catch (Exception) { continue; }
                if (!seen.Add(full)) continue;
                try
                {
                    foreach (var f in dir.EnumerateFiles()) { bytes += f.Length; if (IsWeights(f)) weights.Add(f); if (++n > 20000) break; }
                    if (n <= 20000) foreach (var c in dir.EnumerateDirectories())
                        if ((c.Attributes & FileAttributes.ReparsePoint) == 0 && !c.Name.StartsWith(".")) q.Enqueue(c);
                }
                catch (Exception) { }
            }
            if (weights.Count == 0) return null;
            // "push-to-talk\model" is better called push-to-talk.
            string name = PlainNames.Contains(sub.Name.ToLowerInvariant()) && sub.Parent != null ? sub.Parent.Name : sub.Name;
            var m = new InstalledModel { Name = Pretty(name), Path = sub.FullName, Where = root, Bytes = bytes, Files = weights.Count, WeightBytes = weights.Sum(f => f.Length) };
            m.Added = weights.Max(f => f.CreationTime);
            m.PathKeys.Add(Norm(sub.FullName));
            if (sub.Name.Length >= 5) m.NameKeys.Add(Norm(sub.Name));
            if (sub.Name.Contains("__")) m.NameKeys.Add(Norm(sub.Name.Replace("__", "/")));
            return m;
        }

        // No weights of its own, and every subfolder holds weights directly.
        static bool IsShelf(DirectoryInfo d)
        {
            if (d.EnumerateFiles().Any(IsWeights)) return false;
            var subs = d.EnumerateDirectories().Where(s => !s.Name.StartsWith(".") && (s.Attributes & FileAttributes.ReparsePoint) == 0).Take(200).ToList();
            return subs.Count > 0 && subs.All(s => s.EnumerateFiles().Any(IsWeights));
        }

        // "Qwen__Qwen3-8B-GGUF" -> "Qwen3-8B": a download tool's "owner__" prefix and the format name say little.
        static string Pretty(string name)
        {
            int at = name.IndexOf("__");
            if (at > 0 && at < name.Length - 2) name = name.Substring(at + 2);
            string bare = Regex.Replace(name, "[-_.](gguf|ggml|safetensors|onnx)$", "", RegexOptions.IgnoreCase);
            return bare.Length > 0 ? bare : name;
        }

        // hub\models--owner--name, one model per folder.
        static void HuggingFace(string hub, List<InstalledModel> list)
        {
            foreach (var d in new DirectoryInfo(hub).EnumerateDirectories("models--*"))
            {
                var blobs = new DirectoryInfo(System.IO.Path.Combine(d.FullName, "blobs"));
                if (!blobs.Exists) continue;
                var files = blobs.EnumerateFiles().ToList();
                long bytes = files.Sum(f => f.Length);
                if (bytes < MinWeights) continue;
                var parts = d.Name.Substring(8).Split(new[] { "--" }, 2, StringSplitOptions.None);
                string repo = parts.Length == 2 ? parts[0] + "/" + parts[1] : parts[0];
                var m = new InstalledModel { Name = parts[parts.Length - 1], Path = d.FullName, Where = "Hugging Face", Bytes = bytes, Files = files.Count(f => f.Length >= MinWeights) };
                m.Added = files.Where(f => f.Length >= MinWeights).Select(f => f.CreationTime).DefaultIfEmpty(d.CreationTime).Max();
                m.PathKeys.Add(Norm(d.FullName));
                m.NameKeys.Add(Norm(repo));
                list.Add(m);
            }
        }

        // manifests\<host>\<namespace>\<model>\<tag>: JSON naming the blobs. The runner's command line names the model blob.
        static void Ollama(string dir, List<InstalledModel> list)
        {
            string man = System.IO.Path.Combine(dir, "manifests");
            if (!Directory.Exists(man)) return;
            foreach (var f in new DirectoryInfo(man).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string json;
                try { json = File.ReadAllText(f.FullName); } catch (Exception) { continue; }
                var rel = f.FullName.Substring(man.Length + 1).Split('\\');
                if (rel.Length < 4) continue;
                string name = (rel[rel.Length - 3] == "library" ? "" : rel[rel.Length - 3] + "/") + rel[rel.Length - 2] + ":" + rel[rel.Length - 1];
                var m = new InstalledModel { Name = name, Path = f.FullName, Where = "Ollama", Added = f.CreationTime };
                foreach (Match x in Regex.Matches(json, "\"mediaType\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"digest\"\\s*:\\s*\"sha256:([0-9a-f]+)\"\\s*,\\s*\"size\"\\s*:\\s*(\\d+)"))
                {
                    m.Bytes += long.Parse(x.Groups[3].Value);
                    if (x.Groups[1].Value.EndsWith(".model")) { m.PathKeys.Add(Norm(System.IO.Path.Combine(dir, "blobs", "sha256-" + x.Groups[2].Value))); m.Files++; }
                }
                m.NameKeys.Add(Norm(name));
                if (m.Bytes > 0) list.Add(m);
            }
        }

        // What a process says about the files it uses: its command line, plus small config files that command line names.
        public static string CommandText(Proc p)
        {
            var sb = new StringBuilder(Norm(p.Cmd));
            foreach (string a in Scripts.SplitArgs(p.Cmd ?? ""))
            {
                string ext;
                try { ext = System.IO.Path.GetExtension(a).ToLowerInvariant(); } catch (Exception) { continue; }
                if (ext != ".json" && ext != ".yaml" && ext != ".yml" && ext != ".toml" && ext != ".ini" && ext != ".cfg") continue;
                try
                {
                    var fi = new FileInfo(a);
                    if (fi.Exists && fi.Length < 256 << 10) sb.Append('\n').Append(Norm(File.ReadAllText(fi.FullName)));
                }
                catch (Exception) { }
            }
            return sb.ToString();
        }

        // The source of a script and the scripts beside it, for telling which model a Python process put on the GPU.
        public static string ScriptText(string script)
        {
            var sb = new StringBuilder();
            try
            {
                string dir = Scripts.Folder(script);
                if (dir == null || !Directory.Exists(dir)) return "";
                var files = new List<string>();
                if (File.Exists(script)) files.Add(script);
                files.AddRange(Directory.EnumerateFiles(dir).Where(Scripts.IsScriptFile).Where(f => !f.Equals(script, StringComparison.OrdinalIgnoreCase)).Take(40));
                foreach (string f in files)
                    try { if (new FileInfo(f).Length < 512 << 10) sb.Append('\n').Append(Norm(File.ReadAllText(f))); } catch (Exception) { }
            }
            catch (Exception) { }
            return sb.ToString();
        }
    }

    // A scheduled task you (or your tools) made: not Windows' own, not an app's updater.

    class TaskInfo
    {
        public string Name, Path, Action, Script, Exe;
        public int State;            // 4 = running
        public int Result;
        public DateTime LastRun, NextRun;
        public bool AtLogon;         // starts when you sign in or the PC boots
        public bool Timed;           // starts at set times: once, daily, weekly, monthly, or on a repeat
        public bool OnDemand { get { return !AtLogon && !Timed; } }   // no trigger at all; something else starts it
        public bool StartsWithWindows { get { return AtLogon && !Timed; } }

        public bool Running { get { return State == 4; } }
        public bool NeverRan { get { return Result == 0x41303 || LastRun.Year < 2000; } }
        public bool Failed { get { return !Running && !NeverRan && Result != 0 && Result != 0x41306 && Result != 0x41301; } }
    }

    static class TaskList
    {
        public static List<TaskInfo> Items = new List<TaskInfo>();
        public static bool Ready { get { return read != DateTime.MinValue; } }
        public static bool Failed;
        static bool busy;
        static DateTime read = DateTime.MinValue;

        // Read again on the next Refresh, without waiting out the 30 s.
        public static void Soon() { if (!busy) read = DateTime.MinValue.AddSeconds(1); }

        // Reads Task Scheduler off the UI thread, at most every 30 s. One reader at a time; no watchdog.
        public static void Refresh(Action done)
        {
            if (busy || (Ready && (DateTime.Now - read).TotalSeconds < 30)) return;
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var found = new List<TaskInfo>();
                bool failed = false;
                try
                {
                    dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                    svc.Connect();
                    Walk(svc.GetFolder("\\"), found);
                }
                catch (Exception) { failed = true; }
                Items = found;
                Failed = failed;
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
                    foreach (dynamic tr in t.Definition.Triggers)
                    {
                        int type = (int)tr.Type;
                        if (!(bool)tr.Enabled) continue;
                        if (type == 8 || type == 9) info.AtLogon = true;      // boot, logon
                        else if (type >= 1 && type <= 5) info.Timed = true;   // once, daily, weekly, monthly, monthly by weekday
                    }
                    bool mine = false;
                    foreach (dynamic a in t.Definition.Actions)
                    {
                        if ((int)a.Type != 0) continue;   // only "start a program" actions
                        string exe = Environment.ExpandEnvironmentVariables(((string)a.Path ?? "").Trim('"'));
                        string args = Environment.ExpandEnvironmentVariables((string)a.Arguments ?? "");
                        info.Action = (exe + " " + args).Trim();
                        info.Exe = exe;
                        foreach (string arg in Scripts.SplitArgs(args))
                            if (System.IO.Path.IsPathRooted(arg) && Scripts.IsScriptFile(arg) && !Scripts.IsSystemPath(arg)) { info.Script = arg; break; }
                        if (info.Script == null && Scripts.IsScriptFile(exe) && !Scripts.IsSystemPath(exe)) info.Script = exe;
                        if (info.Script != null || (exe.Length > 0 && System.IO.Path.IsPathRooted(exe) && !Scripts.IsSystemPath(exe))) mine = true;
                    }
                    // App updaters, Edge, OEM helpers, and svchost are noise unless they run a script of yours.
                    if (info.Script == null && (info.Name.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0 || Scripts.IsWindowsNoise(info.Name, info.Exe))) mine = false;
                    if (mine) found.Add(info);
                }
                catch (Exception) { }
            }
            foreach (dynamic sub in folder.GetFolders(0)) Walk(sub, found);
        }
    }

    // Sign-in start: Startup folder, registry Run, or a logon/boot-only scheduled task.

    class StartItem
    {
        public string Name, Where, Exe, Script;
        public TaskInfo Task;
    }

    // Sign-in programs (Startup apps), minus off switches. Refresh at most once a minute off UI thread.

    static class StartupList
    {
        public static List<StartItem> Items = new List<StartItem>();
        static bool busy;
        static DateTime read = DateTime.MinValue;

        public static void Soon() { if (!busy) read = DateTime.MinValue; }

        public static void Refresh()
        {
            if (busy || (DateTime.Now - read).TotalSeconds < 60) return;
            busy = true;
            var t = new Thread(delegate ()
            {
                var found = new List<StartItem>();
                try { Folders(found); } catch (Exception) { }
                try { RunKeys(found); } catch (Exception) { }
                Items = found;
                read = DateTime.Now;
                busy = false;
            });
            t.SetApartmentState(ApartmentState.STA);   // the shortcut reader is a COM object
            t.IsBackground = true;
            t.Start();
        }

        // Task Manager's on/off switch for each entry: the first byte is even when on, odd when turned off.
        static bool TurnedOff(Microsoft.Win32.RegistryKey hive, string list, string name)
        {
            try
            {
                using (var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + list))
                {
                    var v = k == null ? null : k.GetValue(name) as byte[];
                    return v != null && v.Length > 0 && (v[0] & 1) == 1;
                }
            }
            catch (Exception) { return false; }
        }

        static void Folders(List<StartItem> found)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            var places = new[]
            {
                new { Dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup), Hive = Microsoft.Win32.Registry.CurrentUser },
                new { Dir = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Hive = Microsoft.Win32.Registry.LocalMachine },
            };
            foreach (var place in places)
            {
                if (string.IsNullOrEmpty(place.Dir) || !Directory.Exists(place.Dir)) continue;
                foreach (string f in Directory.GetFiles(place.Dir))
                {
                    string file = System.IO.Path.GetFileName(f);
                    if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || TurnedOff(place.Hive, "StartupFolder", file)) continue;
                    var item = new StartItem { Name = System.IO.Path.GetFileNameWithoutExtension(f), Where = "Startup folder", Exe = f };
                    if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            dynamic lnk = shell.CreateShortcut(f);
                            item.Exe = (string)lnk.TargetPath;
                            item.Script = ScriptIn((string)lnk.Arguments);
                        }
                        catch (Exception) { }
                    }
                    else if (Scripts.IsScriptFile(f)) item.Script = f;
                    if (item.Script == null && Scripts.IsWindowsNoise(item.Name, item.Exe ?? f)) continue;
                    found.Add(item);
                }
            }
        }

        static void RunKeys(List<StartItem> found)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                foreach (var sub in new[] { "Run", "Run32" })
                {
                    string path = sub == "Run" ? @"Software\Microsoft\Windows\CurrentVersion\Run" : @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
                    using (var k = hive.OpenSubKey(path))
                    {
                        if (k == null) continue;
                        foreach (string name in k.GetValueNames())
                        {
                            if (TurnedOff(hive, sub, name)) continue;
                            string cmd = Environment.ExpandEnvironmentVariables(k.GetValue(name) as string ?? "").Trim();
                            if (cmd.Length == 0) continue;
                            string exe = ExeOf(cmd);
                            if (exe.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) continue;   // Windows' own (Security, etc.)
                            // "GoogleChromeAutoLaunch_23DF09..." says less than the program's own description.
                            string label = name;
                            if (Regex.IsMatch(name, "_[0-9A-Fa-f]{16,}$"))
                            {
                                label = Regex.Replace(name, "(AutoLaunch)?_[0-9A-Fa-f]{16,}$", "");
                                try { string d = FileVersionInfo.GetVersionInfo(exe).FileDescription; if (!string.IsNullOrWhiteSpace(d)) label = d.Trim(); } catch (Exception) { }
                            }
                            if (Scripts.IsWindowsNoise(label, exe)) continue;
                            int at = cmd.IndexOf(exe, StringComparison.OrdinalIgnoreCase);
                            string rest = at < 0 ? "" : cmd.Substring(at + exe.Length).TrimStart('"');
                            found.Add(new StartItem { Name = label, Where = "Startup apps", Exe = exe, Script = ScriptIn(rest) });
                        }
                    }
                }
        }

        // The program a command line starts: the quoted first part, or everything up to ".exe".
        static string ExeOf(string cmd)
        {
            if (cmd.StartsWith("\"")) { int end = cmd.IndexOf('"', 1); return end > 0 ? cmd.Substring(1, end - 1) : cmd.Trim('"'); }
            int at = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return at > 0 ? cmd.Substring(0, at + 4) : Scripts.SplitArgs(cmd)[0];
        }

        static string ScriptIn(string args)
        {
            foreach (string a in Scripts.SplitArgs(args ?? ""))
                if (!a.StartsWith("-") && Scripts.IsScriptFile(a)) return a;
            return null;
        }
    }

    // GPU load/memory from Windows counters when NVML is absent (AMD, Intel).

    static class GpuCounters
    {
        public static string Name;
        public static double Util, UsedGb, TotalGb;
        public static bool Have, Failed, UtilReady;
        static Dictionary<string, long> prevRaw = new Dictionary<string, long>();
        static long prevTs;
        static bool busy, tried;
        static DateTime read = DateTime.MinValue;
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

        public static void Refresh()
        {
            if (busy || (DateTime.Now - read).TotalSeconds < 2) return;
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (!tried) { tried = true; ReadAdapter(); }
                    var eng = new PerformanceCounterCategory(PerfName("GPU Engine")).ReadCategory()[PerfName("Utilization Percentage")];
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
                        UtilReady = true;
                    }
                    prevRaw = raw; prevTs = ts;
                    var mem = new PerformanceCounterCategory(PerfName("GPU Adapter Memory")).ReadCategory()[PerfName("Dedicated Usage")];
                    long used = 0;
                    foreach (InstanceData d in mem.Values) used = Math.Max(used, d.RawValue);
                    UsedGb = used / 1073741824.0;
                    Have = Name != null || used > 0 || UtilReady;
                    Failed = false;
                }
                catch (Exception) { Have = false; Failed = true; }
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

    // What a bot's check says: when it last showed signs of work, and how long it has been up.

    class BotStatus
    {
        public DateTime? LastSign;     // newest heartbeat file write, or the time the http check answered
        public TimeSpan? Uptime;       // from the http check, when it reports one
        public string Problem;         // set when the http check answers but reports something failing
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
                }
                catch (Exception) { }
                finally { Read = DateTime.Now; Busy = false; }
            });
        }

        void ReadHttp(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)
                || !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                || !uri.IsLoopback)
            {
                Uptime = null; LastSign = null; Problem = "check must be a local address";
                return;
            }
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(uri);
                req.Timeout = 3000; req.ReadWriteTimeout = 3000; req.Proxy = null; req.AllowAutoRedirect = false;
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var sr = new StreamReader(stream))
                {
                    var buf = new char[64 * 1024];
                    sr.Read(buf, 0, buf.Length);
                }
                LastSign = DateTime.Now;
                Uptime = null;
                Problem = null;
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
            int seen = 0;
            for (int depth = 0; depth < 4 && dir != null; depth++)
            {
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch (Exception) { break; }
                foreach (var f in files)
                {
                    if (++seen > 500) return newest;
                    try
                    {
                        var t = File.GetLastWriteTime(f);
                        if (newest == null || t > newest) newest = t;
                    }
                    catch (Exception) { }
                }
                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch (Exception) { break; }
                dir = subs.Length == 0 ? null : subs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Last();
            }
            return newest;
        }
    }

}
