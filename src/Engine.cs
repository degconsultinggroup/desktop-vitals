// Facts to a Board: processes, scripts, models, tasks, and GPU counters.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

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

    // Installed models: drive/user/AppData models folders, beside scripts, HF/Ollama/LM Studio.

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
        public static bool Ready { get { return finished; } }
        public static bool Failed;
        static bool busy;
        static volatile bool finished;
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
                finished = true;
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

    // The board is the contract the window draws. Agents, quiet bots, ended work, and heavy other land here later.

    [DataContract]
    public class Board
    {
        [DataMember(Order = 1)] public string name = "";
        [DataMember(Order = 2)] public Strip strip = new Strip();
        [DataMember(Order = 3)] public Headline headline = new Headline();
        [DataMember(Order = 4)] public List<Group> groups = new List<Group>();
    }

    [DataContract]
    public class Strip
    {
        [DataMember(Order = 1)] public double cpu;
        [DataMember(Order = 2)] public double ram_used_gb;
        [DataMember(Order = 3)] public double ram_total_gb;
        [DataMember(Order = 4, EmitDefaultValue = false)] public double? gpu;
        [DataMember(Order = 5, EmitDefaultValue = false)] public double? gpu_temp;
        [DataMember(Order = 6, EmitDefaultValue = false)] public double? vram_used_gb;
        [DataMember(Order = 7, EmitDefaultValue = false)] public double? vram_total_gb;
        [DataMember(Order = 8)] public double disk_free_gb;
        [DataMember(Order = 9)] public double disk_total_gb;
    }

    [DataContract]
    public class Headline
    {
        [DataMember(Order = 1)] public string text = "All good";
        [DataMember(Order = 2)] public bool needs_you;
    }

    [DataContract]
    public class Group
    {
        [DataMember(Order = 1)] public string title = "";
        [DataMember(Order = 2)] public string kind = "project";
        [DataMember(Order = 3)] public List<Thing> things = new List<Thing>();
    }

    [DataContract]
    public class Thing
    {
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
        [DataMember(Order = 16)] public bool pinned;
    }

    // Dedicated video memory per process. Off the UI thread, at most every 5 s.
    static class Vram
    {
        public static Dictionary<int, long> ByPid = new Dictionary<int, long>();
        static bool busy;
        static DateTime read = DateTime.MinValue;

        public static void Refresh()
        {
            if (busy || (DateTime.Now - read).TotalSeconds < 5) return;
            busy = true;
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
                ByPid = next;
                read = DateTime.Now;
                busy = false;
            });
        }
    }

    // ModelStore.Scan on a background thread, at most every 30 s.
    static class ModelScan
    {
        public static List<InstalledModel> Items = new List<InstalledModel>();
        public static volatile bool Ready;
        static bool busy;
        static DateTime read = DateTime.MinValue;

        public static void Refresh(IEnumerable<string> folders)
        {
            if (busy || (Ready && (DateTime.Now - read).TotalSeconds < 30)) return;
            busy = true;
            var copy = new List<string>(folders);
            ThreadPool.QueueUserWorkItem(delegate
            {
                var found = new List<InstalledModel>();
                try { found = ModelStore.Scan(new string[0], copy); }
                catch (Exception) { }
                Items = found;
                read = DateTime.Now;
                Ready = true;
                busy = false;
            });
        }
    }

    static class BoardText
    {
        public static string Dot(string state)
        {
            switch (state)
            {
                case "working":
                case "running": return "\u25CF";
                case "waiting": return "\u25D0";
                case "quiet": return "\u25CC";
                case "failed": return "\u2715";
                case "idle": return "\u25CB";
                case "ended": return "\u2713";
                default: return "\u25CB";
            }
        }

        public static string Phrase(string state)
        {
            switch (state)
            {
                case "working": return "working";
                case "running": return "running";
                case "waiting": return "waiting on you";
                case "quiet": return "quiet";
                case "idle": return "idle";
                case "failed": return "failed";
                case "ended": return "ended";
                default: return state ?? "";
            }
        }

        public static string DisplayName(Thing t)
        {
            if (t.count > 1 && (t.name == null || t.name.IndexOf(" models on disk", StringComparison.Ordinal) < 0))
                return (t.name ?? "") + " \u00D7" + t.count.ToString(CultureInfo.InvariantCulture);
            return t.name ?? "";
        }

        public static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            double rounded = Math.Round(v, 1);
            double whole = Math.Round(rounded);
            bool near = Math.Abs(rounded - whole) < 0.05;
            if (near && (Math.Abs(whole) >= 10 || Math.Abs(v - whole) < 0.001))
                return whole.ToString("0", CultureInfo.InvariantCulture);
            return rounded.ToString("0.0", CultureInfo.InvariantCulture);
        }

        public static string Pct(double v)
        {
            int n = (int)Math.Round(v);
            if (n < 0) n = 0;
            if (n > 100) n = 100;
            return n.ToString(CultureInfo.InvariantCulture) + "%";
        }

        public static string Strip(Strip s)
        {
            if (s == null) return "";
            var parts = new List<string>();
            parts.Add("CPU " + Pct(s.cpu));
            parts.Add("RAM " + Num(s.ram_used_gb) + "/" + Num(s.ram_total_gb) + " GB");
            if (s.gpu.HasValue)
            {
                string gpu = "GPU " + Pct(s.gpu.Value);
                if (s.gpu_temp.HasValue) gpu += " " + ((int)Math.Round(s.gpu_temp.Value)).ToString(CultureInfo.InvariantCulture) + "\u00B0";
                parts.Add(gpu);
            }
            if (s.vram_used_gb.HasValue || s.vram_total_gb.HasValue)
            {
                string used = s.vram_used_gb.HasValue ? Num(s.vram_used_gb.Value) : "0";
                string total = s.vram_total_gb.HasValue ? Num(s.vram_total_gb.Value) : "?";
                parts.Add("VRAM " + used + "/" + total + " GB");
            }
            parts.Add("Disk " + Num(s.disk_free_gb) + " GB free");
            return string.Join(" \u00B7 ", parts.ToArray());
        }

        public static string Render(Board b)
        {
            if (b == null) b = new Board();
            var sb = new StringBuilder();
            sb.Append("Desktop Vitals  ").Append(b.name ?? "").Append("  ");
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(Strip(b.strip)).Append('\n');
            sb.Append('\n');
            sb.Append(b.headline == null || b.headline.text == null ? "All good" : b.headline.text).Append('\n');
            if (b.groups != null)
                foreach (Group g in b.groups)
                {
                    sb.Append('\n');
                    sb.Append(g.title ?? "").Append('\n');
                    if (g.things == null) continue;
                    foreach (Thing t in g.things)
                    {
                        sb.Append("  ").Append(Dot(t.state)).Append(' ').Append(DisplayName(t));
                        if (!string.IsNullOrEmpty(t.project)) sb.Append("  ").Append(t.project);
                        sb.Append("  ").Append(Phrase(t.state));
                        if (!string.IsNullOrEmpty(t.since)) sb.Append(" \u00B7 ").Append(t.since);
                        sb.Append('\n');
                        if (!string.IsNullOrEmpty(t.detail)) sb.Append("    ").Append(t.detail).Append('\n');
                        if (!string.IsNullOrEmpty(t.why)) sb.Append("    ").Append(t.why).Append('\n');
                    }
                }
            return sb.ToString();
        }
    }

    static class Engine
    {
        public static Board Current = Empty();
        public static bool TasksReady { get { return TaskList.Ready; } }
        public static bool ModelsReady { get { return ModelScan.Ready; } }
        // Both background scans have finished a pass. Snapshot waits on this, not on a fixed delay.
        public static bool ScansComplete { get { return TaskList.Ready && ModelScan.Ready; } }

        // Drop the one-time import headline after the window or snapshot has actually shown it.
        public static void AcknowledgeImport(Board board)
        {
            if (!Prefs.ImportNote || board == null || board.headline == null || board.headline.text == null) return;
            if (board.headline.text.IndexOf("Imported hides and pins", StringComparison.Ordinal) < 0) return;
            Prefs.ImportNote = false;
            Prefs.Save();
        }

        static Dictionary<int, Proc> prevProcs = new Dictionary<int, Proc>();
        static readonly Dictionary<string, string> cmds = new Dictionary<string, string>();
        static readonly Dictionary<string, string> cwds = new Dictionary<string, string>();
        static long prevWall, cpuIdle, cpuKernel, cpuUser;
        static bool haveWall, haveCpu, logged;

        public static void Reset()
        {
            prevProcs = new Dictionary<int, Proc>();
            cmds.Clear();
            cwds.Clear();
            haveWall = false;
            haveCpu = false;
            prevWall = cpuIdle = cpuKernel = cpuUser = 0;
        }

        public static void Prime()
        {
            SampleCpu();
            try { SampleProcs(Procs.Snapshot()); } catch (Exception) { }
            try { TaskList.Refresh(null); } catch (Exception) { }
            try { GpuCounters.Refresh(); } catch (Exception) { }
            try { Vram.Refresh(); } catch (Exception) { }
        }

        public static Board Tick()
        {
            try
            {
                Current = Build();
                logged = false;
            }
            catch (Exception ex)
            {
                if (!logged) { ErrorLog.Write(ex); logged = true; }
            }
            return Current;
        }

        static Board Empty()
        {
            var b = new Board();
            b.name = Environment.MachineName;
            b.headline = new Headline();
            b.headline.text = "All good";
            return b;
        }

        static string KeyOf(Proc p) { return p.Pid.ToString(CultureInfo.InvariantCulture) + ":" + p.CreateTime.ToString(CultureInfo.InvariantCulture); }

        static string CmdOf(Proc p)
        {
            if (!string.IsNullOrEmpty(p.Cmd)) return p.Cmd;
            string key = KeyOf(p);
            string cmd;
            if (cmds.TryGetValue(key, out cmd)) { p.Cmd = cmd; return cmd; }
            try { cmd = Procs.CommandLine(p.Pid) ?? ""; }
            catch (Exception) { cmd = ""; }
            if (cmds.Count > 4000) cmds.Clear();
            cmds[key] = cmd;
            p.Cmd = cmd;
            return cmd;
        }

        static string CwdOf(Proc p)
        {
            string key = KeyOf(p);
            string dir;
            if (cwds.TryGetValue(key, out dir)) return dir;
            try { dir = Procs.CurrentDirectory(p.Pid); }
            catch (Exception) { dir = null; }
            if (cwds.Count > 4000) cwds.Clear();
            cwds[key] = dir;
            return dir;
        }

        static double SampleCpu()
        {
            long idle, kernel, user;
            if (!Native.GetSystemTimes(out idle, out kernel, out user)) return 0;
            double pct = 0;
            if (haveCpu)
            {
                long dIdle = idle - cpuIdle;
                long dTotal = (kernel - cpuKernel) + (user - cpuUser);
                if (dTotal > 0)
                {
                    pct = (dTotal - dIdle) * 100.0 / dTotal;
                    if (pct < 0) pct = 0;
                    if (pct > 100) pct = 100;
                }
            }
            cpuIdle = idle; cpuKernel = kernel; cpuUser = user; haveCpu = true;
            return pct;
        }

        static void SampleProcs(List<Proc> list)
        {
            long wall = (long)Native.GetTickCount64();
            double wallMs = haveWall ? wall - prevWall : 0;
            int ncpu = Math.Max(1, Environment.ProcessorCount);
            foreach (Proc p in list)
            {
                Proc old;
                if (wallMs > 0 && prevProcs.TryGetValue(p.Pid, out old) && old.CreateTime == p.CreateTime)
                {
                    long d = p.CpuTime - old.CpuTime;
                    if (d < 0) d = 0;
                    p.Cpu = (d / 10000.0) / wallMs * 100.0 / ncpu;
                }
            }
            var next = new Dictionary<int, Proc>();
            foreach (Proc p in list) next[p.Pid] = p;
            prevProcs = next;
            prevWall = wall;
            haveWall = true;
        }

        static string FormatSpan(TimeSpan span)
        {
            int sec = (int)span.TotalSeconds;
            if (sec < 0) sec = 0;
            if (sec < 60) return sec.ToString(CultureInfo.InvariantCulture) + "s";
            int mins = (int)span.TotalMinutes;
            if (mins < 60) return mins.ToString(CultureInfo.InvariantCulture) + "m";
            int h = mins / 60;
            int m = mins % 60;
            if (m == 0) return h.ToString(CultureInfo.InvariantCulture) + "h";
            return h.ToString(CultureInfo.InvariantCulture) + "h " + m.ToString(CultureInfo.InvariantCulture) + "m";
        }

        static string Age(long fileTime)
        {
            if (fileTime <= 0) return "";
            try
            {
                TimeSpan span = DateTime.Now - DateTime.FromFileTime(fileTime);
                return FormatSpan(span);
            }
            catch (Exception) { return ""; }
        }

        static string SinceWhen(DateTime t)
        {
            if (t.Year < 2000) return "";
            try { return FormatSpan(DateTime.Now - t); }
            catch (Exception) { return ""; }
        }

        static string Cap(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 2000 ? s : s.Substring(0, 2000);
        }

        static bool Hidden(string id, string name)
        {
            if (!string.IsNullOrEmpty(id) && Prefs.Hides.Contains(id)) return true;
            if (!string.IsNullOrEmpty(name) && Prefs.Hides.Contains(name)) return true;
            return false;
        }

        static void Finish(Thing t)
        {
            if (t.ports == null) t.ports = new List<int>();
            string renamed;
            if (!string.IsNullOrEmpty(t.id) && Prefs.Rename.TryGetValue(t.id, out renamed) && !string.IsNullOrEmpty(renamed))
            {
                if (!Hidden(t.id, renamed)) t.name = renamed;
            }
            if (!string.IsNullOrEmpty(t.id) && Prefs.PinIds.Contains(t.id)) t.pinned = true;
            t.name = Redact.Show(t.name);
            t.project = Redact.Show(t.project);
            t.branch = Redact.Show(t.branch);
            t.detail = Redact.Show(t.detail);
            t.why = Cap(Redact.Show(t.why));
            t.since = t.since ?? "";
            if (t.count < 1) t.count = 1;
        }

        static long VramOf(int pid)
        {
            long bytes;
            var map = Vram.ByPid;
            if (map != null && map.TryGetValue(pid, out bytes)) return bytes;
            return 0;
        }

        static Board Build()
        {
            var board = Empty();
            string named = Prefs.Name;
            board.name = Redact.Show(string.IsNullOrEmpty(named) ? Environment.MachineName : named);
            board.strip.cpu = SampleCpu();
            try
            {
                var mem = new Native.MEMSTAT();
                mem.len = (uint)Marshal.SizeOf(typeof(Native.MEMSTAT));
                if (Native.GlobalMemoryStatusEx(ref mem) && mem.total > 0)
                {
                    board.strip.ram_total_gb = mem.total / 1073741824.0;
                    board.strip.ram_used_gb = (mem.total - mem.avail) / 1073741824.0;
                }
            }
            catch (Exception) { }
            try
            {
                string root = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
                var drive = new DriveInfo(root);
                board.strip.disk_free_gb = drive.AvailableFreeSpace / 1073741824.0;
                board.strip.disk_total_gb = drive.TotalSize / 1073741824.0;
            }
            catch (Exception) { }

            double util = -1, usedGb = -1, totalGb = -1, watts = -1;
            int temp = -1;
            bool nv = false;
            try { nv = Nvml.Read(out util, out usedGb, out totalGb, out temp, out watts); }
            catch (Exception) { nv = false; util = usedGb = totalGb = watts = -1; temp = -1; }
            if (nv && util >= 0 && watts >= -1) board.strip.gpu = util;
            if (nv && temp >= 0) board.strip.gpu_temp = temp;
            if (nv && totalGb >= 0) { board.strip.vram_used_gb = usedGb < 0 ? 0 : usedGb; board.strip.vram_total_gb = totalGb; }
            if (!board.strip.gpu.HasValue || !board.strip.vram_total_gb.HasValue)
            {
                try { GpuCounters.Refresh(); } catch (Exception) { }
                if (GpuCounters.Have)
                {
                    if (!board.strip.gpu.HasValue && GpuCounters.UtilReady) board.strip.gpu = GpuCounters.Util;
                    if (!board.strip.vram_total_gb.HasValue && (GpuCounters.TotalGb > 0 || GpuCounters.UsedGb > 0))
                    {
                        board.strip.vram_used_gb = GpuCounters.UsedGb;
                        if (GpuCounters.TotalGb > 0) board.strip.vram_total_gb = GpuCounters.TotalGb;
                    }
                }
            }
            try { Vram.Refresh(); } catch (Exception) { }

            var procs = new List<Proc>();
            try { procs = Procs.Snapshot(); SampleProcs(procs); }
            catch (Exception) { procs = new List<Proc>(); }

            bool textPin = false;
            foreach (PinRule rule in Prefs.PinRules) if (!rule.ByExe) textPin = true;
            foreach (Proc p in procs)
                if (textPin || Scripts.IsInterpreter(p.Exe ?? "")) CmdOf(p);

            var order = new List<Group>();
            var byFolder = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            var scriptHits = new Dictionary<string, Thing>(StringComparer.OrdinalIgnoreCase);
            var scriptFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Proc p in procs)
            {
                if (!Scripts.IsInterpreter(p.Exe ?? "")) continue;
                string script = null;
                try
                {
                    script = Scripts.Find(p, delegate
                    {
                        string dir = CwdOf(p);
                        return dir;
                    });
                }
                catch (Exception) { script = null; }
                if (string.IsNullOrEmpty(script)) continue;
                string folder = Scripts.Folder(script);
                Thing hit;
                if (!scriptHits.TryGetValue(script, out hit))
                {
                    hit = new Thing();
                    hit.id = "script:" + script;
                    hit.name = Scripts.Label(script);
                    hit.kind = "script";
                    hit.state = "running";
                    hit.project = Scripts.Language(p.Exe);
                    hit.folder = script.StartsWith("module:", StringComparison.Ordinal) ? folder : script;
                    hit.why = p.Cmd ?? "";
                    hit.since = Age(p.CreateTime);
                    hit.count = 0;
                    scriptHits[script] = hit;
                    if (!string.IsNullOrEmpty(folder)) scriptFolders.Add(folder);
                }
                hit.count++;
                hit.cpu += p.Cpu;
                hit.ram_mb += p.Private / 1048576.0;
                hit.vram_mb += VramOf(p.Pid) / 1048576.0;
                string older = Age(p.CreateTime);
                if (hit.since.Length == 0) hit.since = older;
            }

            foreach (var kv in scriptHits)
            {
                Thing hit = kv.Value;
                if (hit.count < 1) hit.count = 1;
                if (Hidden(hit.id, hit.name)) continue;
                string folder = Scripts.Folder(kv.Key) ?? "";
                string title = "";
                try { title = System.IO.Path.GetFileName(folder.TrimEnd('\\')); } catch (Exception) { title = ""; }
                if (string.IsNullOrEmpty(title)) title = "Scripts";
                Group g = TakeGroup(byFolder, order, string.IsNullOrEmpty(folder) ? "scripts" : folder, title, "project");
                Finish(hit);
                g.things.Add(hit);
            }

            try { TaskList.Refresh(null); } catch (Exception) { }
            foreach (TaskInfo task in TaskList.Items)
            {
                var t = new Thing();
                t.id = "task:" + (string.IsNullOrEmpty(task.Path) ? task.Name : task.Path);
                t.name = task.Name ?? "";
                t.kind = "task";
                t.project = "task";
                if (task.Running) t.state = "running";
                else if (task.Failed) t.state = "failed";
                else t.state = "idle";
                t.folder = task.Script;
                t.why = task.Action ?? "";
                t.since = SinceWhen(task.LastRun);
                if (task.Failed && task.LastRun.Year >= 2000)
                    t.detail = "failed " + task.LastRun.ToString("HH:mm", CultureInfo.InvariantCulture);
                if (Hidden(t.id, t.name)) continue;
                string folder = "";
                try { if (!string.IsNullOrEmpty(task.Script)) folder = System.IO.Path.GetDirectoryName(task.Script) ?? ""; }
                catch (Exception) { folder = ""; }
                string title = "Tasks";
                string key = "tasks";
                if (folder.Length > 0)
                {
                    key = folder;
                    try { title = System.IO.Path.GetFileName(folder.TrimEnd('\\')); } catch (Exception) { title = "Tasks"; }
                    if (string.IsNullOrEmpty(title)) title = "Tasks";
                }
                Finish(t);
                TakeGroup(byFolder, order, key, title, "project").things.Add(t);
            }

            try { ModelScan.Refresh(scriptFolders); } catch (Exception) { }
            var models = ModelScan.Items;
            if (models != null && models.Count > 0)
            {
                var t = new Thing();
                t.id = "models";
                t.kind = "model";
                t.state = "idle";
                t.count = models.Count;
                if (models.Count == 1)
                {
                    t.name = models[0].Name ?? "model";
                    t.folder = models[0].Path;
                    t.why = models[0].Name ?? "";
                }
                else
                {
                    t.name = models.Count.ToString(CultureInfo.InvariantCulture) + " models on disk";
                    var names = new StringBuilder();
                    foreach (InstalledModel m in models)
                    {
                        if (names.Length > 0) names.Append(" \u00B7 ");
                        names.Append(m.Name);
                        if (names.Length > 4000) break;
                    }
                    t.why = names.ToString();
                }
                if (!Hidden(t.id, t.name))
                {
                    Finish(t);
                    var g = new Group();
                    g.title = "MODELS";
                    g.kind = "models";
                    g.things.Add(t);
                    order.Add(g);
                }
            }

            if (Prefs.PinRules.Count > 0)
            {
                var pinGroup = new Group();
                pinGroup.title = "Pinned";
                pinGroup.kind = "other";
                foreach (PinRule rule in Prefs.PinRules)
                {
                    var t = new Thing();
                    t.id = string.IsNullOrEmpty(rule.Id) ? "pin:" + rule.Label : rule.Id;
                    t.name = rule.Label ?? "";
                    t.kind = "other";
                    t.pinned = true;
                    t.state = "idle";
                    t.count = 0;
                    foreach (Proc p in procs)
                    {
                        bool hit = false;
                        if (rule.ByExe) hit = string.Equals(p.Exe, rule.Match, StringComparison.OrdinalIgnoreCase);
                        else if (!string.IsNullOrEmpty(rule.Match) && !string.IsNullOrEmpty(p.Cmd))
                            hit = p.Cmd.IndexOf(rule.Match, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!hit) continue;
                        if (t.count == 0)
                        {
                            t.why = p.Cmd ?? p.Exe ?? "";
                            try { if (!string.IsNullOrEmpty(rule.Match) && System.IO.Path.IsPathRooted(rule.Match) && (File.Exists(rule.Match) || Directory.Exists(rule.Match))) t.folder = rule.Match; }
                            catch (Exception) { }
                        }
                        t.count++;
                        t.cpu += p.Cpu;
                        t.ram_mb += p.Private / 1048576.0;
                        t.state = "running";
                    }
                    if (t.count < 1) t.count = 1;
                    if (Hidden(t.id, t.name)) continue;
                    Finish(t);
                    pinGroup.things.Add(t);
                }
                if (pinGroup.things.Count > 0) order.Add(pinGroup);
            }

            foreach (Group g in order)
                g.things.Sort(delegate(Thing a, Thing b) { return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase); });

            int waiting = 0, failed = 0, quiet = 0;
            foreach (Group g in order)
                foreach (Thing t in g.things)
                {
                    if (t.state == "waiting" && t.kind == "agent") waiting += Math.Max(1, t.count);
                    else if (t.state == "failed") failed += Math.Max(1, t.count);
                    else if (t.state == "quiet" && t.kind == "bot") quiet += Math.Max(1, t.count);
                }
            if (waiting == 0 && failed == 0 && quiet == 0)
            {
                board.headline.text = "All good";
                board.headline.needs_you = false;
                // Wait until the task scan has finished. An earlier tick looks calm only because failures are not in yet.
                if (Prefs.ImportNote && TaskList.Ready)
                    board.headline.text = "Imported hides and pins from your old watch.txt. Change them anytime with right-click.";
            }
            else
            {
                var bits = new List<string>();
                if (waiting > 0) bits.Add(waiting.ToString(CultureInfo.InvariantCulture) + (waiting == 1 ? " agent waiting on you" : " agents waiting on you"));
                if (failed > 0) bits.Add(failed.ToString(CultureInfo.InvariantCulture) + (failed == 1 ? " task failed" : " tasks failed"));
                if (quiet > 0) bits.Add(quiet.ToString(CultureInfo.InvariantCulture) + (quiet == 1 ? " bot quiet" : " bots quiet"));
                board.headline.text = string.Join(" \u00B7 ", bits.ToArray());
                board.headline.needs_you = true;
            }
            board.headline.text = Redact.Show(board.headline.text);
            board.groups = order;
            return board;
        }

        static Group TakeGroup(Dictionary<string, Group> map, List<Group> order, string key, string title, string kind)
        {
            Group g;
            if (map.TryGetValue(key, out g)) return g;
            g = new Group();
            g.title = Redact.Show(title ?? "");
            g.kind = kind;
            map[key] = g;
            order.Add(g);
            return g;
        }
    }
}
