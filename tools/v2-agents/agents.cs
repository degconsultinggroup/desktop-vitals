// Agent activity probe. C# 5, in-box csc, no NuGet.
// Reads process facts always. Session logs are optional and read-only.
// Output strings pass through Redact.Show. Prompt text, file bodies, and tool output are never kept.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentProbe
{
    static class Native
    {
        public const int QueryLimited = 0x1000;
        public const int VmRead = 0x0010;
        public const int StatusLengthMismatch = unchecked((int)0xC0000004);
        public const int StatusBufferTooSmall = unchecked((int)0xC0000023);
        public const int SqliteOpenReadonly = 0x00000001;
        public const int SqliteOpenUri = 0x00000040;
        public const int SqliteRow = 100;

        [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
        [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPStr)] string filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int i);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_column_bytes(IntPtr stmt, int i);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_bind_text(IntPtr stmt, int i, byte[] val, int n, IntPtr dtor);
    }

    class Proc
    {
        public int Pid;
        public int Ppid;
        public string Exe = "";
        public long CreateTime;
        public long CpuTime;
        public string Cmd = "";
        public string Cwd;
    }

    class Hit
    {
        public string Kind;
        public string File;
        public DateTime When;
        public bool Open;
        public string Model;
        public Hit() { When = DateTime.MinValue; }
    }

    class Tail
    {
        public long Offset;
        public StringBuilder Head = new StringBuilder();
        public StringBuilder Roll = new StringBuilder();
        public bool InLine;
        public bool Overflow;
        public Hit Last;
        public string Model;
        public long DbBytes;
        public long WalBytes;
        public bool Sized;
    }

    class GitInfo
    {
        public string Repo;
        public string Branch;
        public string Worktree;
        public int Commits;
        public int Changed;
        public string Subject;
    }

    class Row
    {
        public string Agent;
        public string Model;
        public int Pid;
        public GitInfo Git;
        public string Child;
        public Hit Action;
        public string State;
        public string Why;
        public string WorkCwd;
    }

    static class Redact
    {
        static readonly Regex Secret = new Regex(
            "(?i)(sk-|ghp_|github_pat_|xox[baprs]-|AKIA|Bearer\\s+)[A-Za-z0-9_\\-\\.]{8,}",
            RegexOptions.Compiled);

        public static string Show(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = Secret.Replace(s, "•••");
            s = s.Replace("\r", " ").Replace("\n", " ");
            if (s.Length > 160) s = s.Substring(0, 160);
            return s;
        }
    }

    static class Procs
    {
        static IntPtr buf;
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
            int off = 0;
            while (true)
            {
                IntPtr p = new IntPtr(buf.ToInt64() + off);
                int pid = (int)Marshal.ReadIntPtr(p, 80).ToInt64();
                if (pid != 0)
                {
                    var pr = new Proc();
                    pr.Pid = pid;
                    pr.Ppid = (int)Marshal.ReadIntPtr(p, 88).ToInt64();
                    pr.CreateTime = Marshal.ReadInt64(p, 32);
                    pr.CpuTime = Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48);
                    int nlen = Marshal.ReadInt16(p, 56) & 0xffff;
                    IntPtr name = Marshal.ReadIntPtr(p, 64);
                    pr.Exe = name == IntPtr.Zero ? "" : Marshal.PtrToStringUni(name, nlen / 2);
                    list.Add(pr);
                }
                int next = Marshal.ReadInt32(p, 0);
                if (next == 0) break;
                off += next;
            }
            return list;
        }

        // PEB cwd and command line. Display-only, skip on failure, never execute.
        public static string CurrentDirectory(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.QueryLimited | Native.VmRead, false, pid);
            if (h == IntPtr.Zero) return null;
            IntPtr pbi = Marshal.AllocHGlobal(48);
            try
            {
                int ret;
                if (Native.NtQueryInformationProcess(h, 0, pbi, 48, out ret) != 0) return null;
                IntPtr peb = Marshal.ReadIntPtr(pbi, 8);
                IntPtr parms = ReadPtr(h, new IntPtr(peb.ToInt64() + 0x20));
                if (parms == IntPtr.Zero) return null;
                byte[] us = new byte[16];
                IntPtr got;
                if (!Native.ReadProcessMemory(h, new IntPtr(parms.ToInt64() + 0x38), us, 16, out got)) return null;
                int len = BitConverter.ToUInt16(us, 0);
                long bufAddr = BitConverter.ToInt64(us, 8);
                if (len <= 0 || len > 32 * 1024 || bufAddr == 0) return null;
                byte[] chars = new byte[len];
                if (!Native.ReadProcessMemory(h, new IntPtr(bufAddr), chars, len, out got)) return null;
                return Encoding.Unicode.GetString(chars).TrimEnd('\\');
            }
            catch (Exception) { return null; }
            finally { Marshal.FreeHGlobal(pbi); Native.CloseHandle(h); }
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
                if (s == IntPtr.Zero || l <= 0 || l > 1024 * 1024) return "";
                return Marshal.PtrToStringUni(s, l / 2);
            }
            catch (Exception) { return ""; }
            finally { Marshal.FreeHGlobal(b); Native.CloseHandle(h); }
        }

        static IntPtr ReadPtr(IntPtr h, IntPtr addr)
        {
            byte[] b = new byte[8];
            IntPtr got;
            return Native.ReadProcessMemory(h, addr, b, 8, out got) ? new IntPtr(BitConverter.ToInt64(b, 0)) : IntPtr.Zero;
        }
    }

    static class Scan
    {
        public static string EncodeCwd(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return "";
            var sb = new StringBuilder(cwd.Length);
            for (int i = 0; i < cwd.Length; i++)
            {
                char c = cwd[i];
                sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            }
            return sb.ToString();
        }

        public static string Md5Hex(string s)
        {
            byte[] hash;
            using (MD5 md = MD5.Create()) hash = md.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
            var sb = new StringBuilder(32);
            for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        public static string Arg(string cmd, string name)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            string eq = name + "=";
            var parts = Split(cmd);
            for (int i = 0; i < parts.Count; i++)
            {
                string p = parts[i];
                if (p.StartsWith(eq, StringComparison.Ordinal)) return p.Substring(eq.Length);
                if (string.Equals(p, name, StringComparison.Ordinal) && i + 1 < parts.Count) return parts[i + 1];
            }
            return null;
        }

        public static bool HasFlag(string cmd, string name)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            var parts = Split(cmd);
            for (int i = 0; i < parts.Count; i++)
            {
                if (string.Equals(parts[i], name, StringComparison.Ordinal)) return true;
                if (parts[i].StartsWith(name + "=", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        static List<string> Split(string cmd)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();
            bool q = false;
            for (int i = 0; i < cmd.Length; i++)
            {
                char c = cmd[i];
                if (c == '"') { q = !q; continue; }
                if (!q && (c == ' ' || c == '\t'))
                {
                    if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Length = 0; }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0) parts.Add(cur.ToString());
            return parts;
        }

        public static string JsonString(string hay, string key, int from)
        {
            if (string.IsNullOrEmpty(hay)) return null;
            int i = hay.IndexOf(key, from, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            var sb = new StringBuilder();
            for (int n = 0; i < hay.Length && n < 500; i++, n++)
            {
                char c = hay[i];
                if (c == '"') break;
                if (c == '\\' && i + 1 < hay.Length)
                {
                    char nch = hay[i + 1];
                    if (nch == '\\' || nch == '"' || nch == '/') { sb.Append(nch); i++; continue; }
                    if (nch == 'n') { break; }
                }
                if (c == '\r' || c == '\n') break;
                sb.Append(c);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        public static long JsonLong(string hay, string key)
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

        public static string BaseName(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            path = path.Replace('/', '\\');
            int slash = path.LastIndexOf('\\');
            string name = slash >= 0 ? path.Substring(slash + 1) : path;
            if (name.Length == 0 || name.Length > 120) return null;
            if (name.IndexOf('"') >= 0 || name.IndexOf(':') >= 0) return null;
            return name;
        }

        public static DateTime Stamp(string s)
        {
            if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
            DateTime dt;
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out dt))
                return dt.ToUniversalTime();
            return DateTime.MinValue;
        }

        public static string FileFrom(string head, string tail)
        {
            string p = JsonString(head, "\"file_path\":\"", 0);
            if (p == null) p = JsonString(head, "\"path\":\"", 0);
            if (p == null) p = JsonString(head, "\"target_directory\":\"", 0);
            if (p == null) p = JsonString(tail, "\"file_path\":\"", 0);
            if (p == null) p = JsonString(tail, "\"path\":\"", 0);
            if (p == null) return null;
            if (p.IndexOf('\\') < 0 && p.IndexOf('/') < 0 && p.IndexOf('.') < 0) return null;
            return BaseName(p);
        }
    }

    static class Logs
    {
        const int Cap = 64 * 1024;
        public static long BytesRead;

        static Dictionary<string, Tail> tails = new Dictionary<string, Tail>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<int, string> cursorDb = new Dictionary<int, string>();

        public static Tail For(string key)
        {
            Tail t;
            if (!tails.TryGetValue(key, out t))
            {
                t = new Tail();
                tails[key] = t;
            }
            return t;
        }

        public static void Claude(string home, Proc p, Row row)
        {
            string reg = Path.Combine(home, ".claude", "sessions", p.Pid + ".json");
            if (!File.Exists(reg)) return;
            string text;
            try { text = File.ReadAllText(reg); }
            catch (Exception) { return; }
            BytesRead += text.Length;
            string procStart = Scan.JsonString(text, "\"procStart\":\"", 0);
            if (!string.IsNullOrEmpty(procStart) && procStart != p.CreateTime.ToString()) return;
            string sid = Scan.JsonString(text, "\"sessionId\":\"", 0);
            string cwd = Scan.JsonString(text, "\"cwd\":\"", 0);
            if (!string.IsNullOrEmpty(cwd)) row.WorkCwd = cwd;
            string status = Scan.JsonString(text, "\"status\":\"", 0);
            if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(cwd)) return;
            if (row.Model == null) row.Model = Scan.JsonString(text, "\"model\":\"", 0);
            string file = Path.Combine(home, ".claude", "projects", Scan.EncodeCwd(cwd), sid + ".jsonl");
            if (!File.Exists(file)) return;
            Tail tail = For(file);
            Pull(file, tail, true);
            if (tail.Model != null && row.Model == null) row.Model = tail.Model;
            row.Action = tail.Last;
            if (row.Action != null && row.Action.Open && status == "idle" && string.IsNullOrEmpty(row.Child))
                row.Action.Kind = "waiting";
        }

        public static void Codex(string home, Proc p, Row row)
        {
            if (string.IsNullOrEmpty(p.Cwd)) return;
            string root = Path.Combine(home, ".codex", "sessions");
            if (!Directory.Exists(root)) return;
            string file = FindCodex(root, p);
            if (file == null) return;
            Tail tail = For(file);
            Pull(file, tail, false);
            if (tail.Model != null) row.Model = tail.Model;
            row.Action = tail.Last;
        }

        static bool codexReady;
        static string codexIndex = "";

        static string FindCodex(string root, Proc p)
        {
            // Index once per process: first 2 KB of each transcript, cwd and filename time only.
            if (!codexReady)
            {
                var sb = new StringBuilder();
                string[] files;
                try { files = Directory.GetFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories); }
                catch (Exception) { files = new string[0]; }
                for (int i = 0; i < files.Length; i++)
                {
                    string cwd = HeadCwd(files[i]);
                    if (string.IsNullOrEmpty(cwd)) continue;
                    sb.Append(files[i]).Append('\t').Append(cwd).Append('\n');
                    BytesRead += 2048;
                }
                codexIndex = sb.ToString();
                codexReady = true;
            }
            string best = null;
            long bestSkew = long.MaxValue;
            DateTime start = DateTime.FromFileTimeUtc(p.CreateTime);
            string[] lines = codexIndex.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int tab = lines[i].IndexOf('\t');
                if (tab < 0) continue;
                string path = lines[i].Substring(0, tab);
                string cwd = lines[i].Substring(tab + 1);
                if (!string.Equals(cwd, p.Cwd, StringComparison.OrdinalIgnoreCase)) continue;
                DateTime born = CodexBorn(path);
                long skew = Math.Abs((long)(born - start).TotalSeconds);
                if (born < start.AddSeconds(-5)) continue;
                if (skew < bestSkew) { bestSkew = skew; best = path; }
            }
            return best;
        }

        static DateTime CodexBorn(string path)
        {
            string name = Path.GetFileName(path);
            // rollout-YYYY-MM-DDTHH-MM-SS-<uuid>.jsonl
            if (name == null || name.Length < 27) return DateTime.MinValue;
            string raw = name.Substring(8, 19);
            DateTime dt;
            if (DateTime.TryParseExact(raw, "yyyy-MM-ddTHH-mm-ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out dt))
                return dt.ToUniversalTime();
            return DateTime.MinValue;
        }

        static string HeadCwd(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] b = new byte[2048];
                    int n = fs.Read(b, 0, b.Length);
                    string s = Encoding.UTF8.GetString(b, 0, n);
                    return Scan.JsonString(s, "\"cwd\":\"", 0);
                }
            }
            catch (Exception) { return null; }
        }

        public static void Cursor(string home, Proc p, Row row)
        {
            if (string.IsNullOrEmpty(p.Cwd)) return;
            string cached;
            if (cursorDb.TryGetValue(p.Pid, out cached))
            {
                Tail held = For(cached);
                PullCursor(cached, held);
                row.Action = held.Last;
                return;
            }
            string hash = Scan.Md5Hex(p.Cwd);
            string dir = Path.Combine(home, ".cursor", "chats", hash);
            if (!Directory.Exists(dir)) return;
            string bestId = null;
            long bestSkew = long.MaxValue;
            long startMs = (long)(DateTime.FromFileTimeUtc(p.CreateTime) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            string[] chats;
            try { chats = Directory.GetDirectories(dir); }
            catch (Exception) { return; }
            for (int i = 0; i < chats.Length; i++)
            {
                string metaPath = Path.Combine(chats[i], "meta.json");
                if (!File.Exists(metaPath)) continue;
                string meta;
                try { meta = File.ReadAllText(metaPath); }
                catch (Exception) { continue; }
                BytesRead += meta.Length;
                string cwd = Scan.JsonString(meta, "\"cwd\":\"", 0);
                if (!string.Equals(cwd, p.Cwd, StringComparison.OrdinalIgnoreCase)) continue;
                long cms = Scan.JsonLong(meta, "\"createdAtMs\":");
                if (cms == 0) continue;
                long skew = Math.Abs(cms - startMs);
                if (skew < bestSkew) { bestSkew = skew; bestId = chats[i]; }
            }
            if (bestId == null) return;
            string db = Path.Combine(bestId, "store.db");
            if (!File.Exists(db)) return;
            cursorDb[p.Pid] = db;
            Tail tail = For(db);
            PullCursor(db, tail);
            row.Action = tail.Last;
        }

        static void Pull(string path, Tail tail, bool claude)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long end = fs.Length;
                    if (end < tail.Offset) { tail.Offset = 0; tail.InLine = false; tail.Overflow = false; tail.Head.Length = 0; tail.Roll.Length = 0; }
                    long start = tail.Offset;
                    if (end - start > Cap) start = end - Cap;
                    if (start < 0) start = 0;
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[end - start];
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int n = fs.Read(buf, got, buf.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    BytesRead += got;
                    int i = 0;
                    bool jumped = start > tail.Offset;
                    if (jumped)
                    {
                        tail.Overflow = tail.InLine;
                        int nl = -1;
                        for (int k = 0; k < got; k++) if (buf[k] == (byte)'\n') { nl = k; break; }
                        if (!tail.InLine)
                        {
                            if (nl < 0) { tail.Offset = end; return; }
                            i = nl + 1;
                            tail.Head.Length = 0;
                            tail.Roll.Length = 0;
                            tail.Overflow = false;
                        }
                        else if (nl >= 0)
                        {
                            RememberTail(tail, buf, 0, nl);
                            FinishLine(tail, tail.Head.ToString(), claude);
                            tail.Head.Length = 0;
                            tail.Roll.Length = 0;
                            tail.Overflow = false;
                            tail.InLine = false;
                            i = nl + 1;
                        }
                        else
                        {
                            RememberTail(tail, buf, 0, got);
                            tail.InLine = true;
                            tail.Offset = end;
                            return;
                        }
                    }
                    var line = tail.Head;
                    for (; i < got; i++)
                    {
                        byte c = buf[i];
                        if (c == (byte)'\n')
                        {
                            FinishLine(tail, line.ToString(), claude);
                            line.Length = 0;
                            tail.Roll.Length = 0;
                            tail.Overflow = false;
                            tail.InLine = false;
                            continue;
                        }
                        if (c == (byte)'\r') continue;
                        if (line.Length < 4096) line.Append((char)c);
                        else
                        {
                            tail.Overflow = true;
                            if (tail.Roll.Length >= 2048) tail.Roll.Remove(0, tail.Roll.Length - 2047);
                            tail.Roll.Append((char)c);
                        }
                    }
                    tail.InLine = line.Length > 0 || tail.Overflow;
                    tail.Offset = end;
                }
            }
            catch (Exception) { }
        }

        static void RememberTail(Tail tail, byte[] buf, int from, int to)
        {
            tail.Roll.Length = 0;
            int begin = to - 2048;
            if (begin < from) begin = from;
            for (int i = begin; i < to; i++)
            {
                if (buf[i] == (byte)'\r' || buf[i] == (byte)'\n') continue;
                tail.Roll.Append((char)buf[i]);
            }
            tail.Overflow = true;
        }

        static void FinishLine(Tail tail, string head, bool claude)
        {
            string tailText = tail.Overflow ? tail.Roll.ToString() : "";
            Hit hit = claude ? ParseClaude(head, tailText) : ParseCodex(head, tailText);
            if (hit == null) return;
            if (hit.Model != null) tail.Model = hit.Model;
            if (hit.Kind == null) return;
            if (!hit.Open && tail.Last != null && tail.Last.Open)
            {
                if (hit.Kind == "tool") hit.Kind = tail.Last.Kind;
                if (hit.File == null) hit.File = tail.Last.File;
            }
            if (hit.When == DateTime.MinValue && tail.Last != null) hit.When = tail.Last.When;
            tail.Last = hit;
        }

        static Hit ParseClaude(string head, string tailText)
        {
            string blob = head + "\n" + tailText;
            var hit = new Hit();
            hit.When = Scan.Stamp(Scan.JsonString(head, "\"timestamp\":\"", 0) ?? Scan.JsonString(tailText, "\"timestamp\":\"", 0));
            string req = Scan.JsonString(head, "\"requestedModel\":\"", 0);
            if (req != null) hit.Model = req;
            bool toolUse = blob.IndexOf("\"type\":\"tool_use\"", StringComparison.Ordinal) >= 0;
            bool toolResult = blob.IndexOf("\"type\":\"tool_result\"", StringComparison.Ordinal) >= 0;
            bool thinking = blob.IndexOf("\"type\":\"thinking\"", StringComparison.Ordinal) >= 0;
            bool text = blob.IndexOf("\"type\":\"text\"", StringComparison.Ordinal) >= 0;
            bool endTurn = blob.IndexOf("\"stop_reason\":\"end_turn\"", StringComparison.Ordinal) >= 0;
            if (toolResult && !toolUse)
            {
                hit.Open = false;
                hit.Kind = "tool";
                return hit;
            }
            if (toolUse)
            {
                string name = Scan.JsonString(blob, "\"name\":\"", Math.Max(0, blob.IndexOf("\"type\":\"tool_use\"", StringComparison.Ordinal)));
                hit.Kind = KindFor(name);
                hit.File = Scan.FileFrom(head, tailText);
                hit.Open = true;
                return hit;
            }
            if (thinking && !text)
            {
                hit.Kind = "thinking";
                hit.Open = false;
                return hit;
            }
            if (text && endTurn)
            {
                hit.Kind = "answering";
                hit.Open = false;
                return hit;
            }
            return hit.Model == null ? null : hit;
        }

        static Hit ParseCodex(string head, string tailText)
        {
            string blob = head;
            var hit = new Hit();
            hit.When = Scan.Stamp(Scan.JsonString(head, "\"timestamp\":\"", 0) ?? Scan.JsonString(tailText, "\"timestamp\":\"", 0));
            if (head.IndexOf("\"type\":\"turn_context\"", StringComparison.Ordinal) >= 0 ||
                head.IndexOf("thread_settings", StringComparison.Ordinal) >= 0)
            {
                string model = Scan.JsonString(head, "\"model\":\"", 0);
                if (model != null && model.IndexOf(' ') < 0 && model.Length < 64) hit.Model = model;
            }
            if (blob.IndexOf("\"type\":\"FileChange\"", StringComparison.Ordinal) >= 0)
            {
                hit.Kind = "editing";
                hit.Open = false;
                int at = blob.IndexOf("\"changes\":{", StringComparison.Ordinal);
                if (at >= 0) hit.File = Scan.BaseName(Scan.JsonString(blob, "\"", at + "\"changes\":{".Length));
                return hit;
            }
            if (blob.IndexOf("\"type\":\"CommandExecution\"", StringComparison.Ordinal) >= 0)
            {
                hit.Kind = "shell";
                hit.Open = false;
                return hit;
            }
            if (blob.IndexOf("\"type\":\"McpToolCall\"", StringComparison.Ordinal) >= 0 ||
                blob.IndexOf("\"type\":\"custom_tool_call\"", StringComparison.Ordinal) >= 0)
            {
                hit.Kind = "tool";
                hit.Open = false;
                return hit;
            }
            if (blob.IndexOf("\"type\":\"Reasoning\"", StringComparison.Ordinal) >= 0)
            {
                hit.Kind = "thinking";
                hit.Open = false;
                return hit;
            }
            if (blob.IndexOf("\"type\":\"AgentMessage\"", StringComparison.Ordinal) >= 0 ||
                blob.IndexOf("\"phase\":\"final_answer\"", StringComparison.Ordinal) >= 0)
            {
                hit.Kind = "answering";
                hit.Open = false;
                return hit;
            }
            return hit.Model == null ? null : hit;
        }

        static string KindFor(string name)
        {
            if (string.IsNullOrEmpty(name)) return "tool";
            if (name == "Edit" || name == "Write" || name == "StrReplace" || name == "NotebookEdit") return "editing";
            if (name == "Read" || name == "Grep" || name == "Glob") return "reading";
            if (name == "Bash" || name == "PowerShell" || name == "Shell") return "shell";
            return "tool";
        }

        static void PullCursor(string db, Tail tail)
        {
            long dbLen = new FileInfo(db).Length;
            string wal = db + "-wal";
            long walLen = File.Exists(wal) ? new FileInfo(wal).Length : 0;
            if (tail.Sized && tail.DbBytes == dbLen && tail.WalBytes == walLen) return;
            tail.Sized = true;
            tail.DbBytes = dbLen;
            tail.WalBytes = walLen;
            IntPtr conn;
            string uri = "file:" + db.Replace('\\', '/') + "?mode=ro";
            if (Native.sqlite3_open_v2(uri, out conn, Native.SqliteOpenReadonly | Native.SqliteOpenUri, IntPtr.Zero) != 0)
                return;
            try
            {
                Native.sqlite3_busy_timeout(conn, 0);
                byte[] meta = QueryBlob(conn, "SELECT value FROM meta WHERE key='0'", null);
                if (meta == null) return;
                BytesRead += meta.Length;
                string root = HexJsonString(meta, "latestRootBlobId");
                if (string.IsNullOrEmpty(root)) return;
                byte[] rootBlob = QueryBlob(conn, "SELECT data FROM blobs WHERE id=?", root);
                if (rootBlob == null) return;
                BytesRead += Math.Min(rootBlob.Length, Cap);
                var ids = new List<string>();
                for (int i = 0; i + 34 <= rootBlob.Length; i++)
                {
                    if (rootBlob[i] != 0x0a || rootBlob[i + 1] != 0x20) continue;
                    ids.Add(Hex(rootBlob, i + 2, 32));
                    i += 33;
                }
                int from = Math.Max(0, ids.Count - 24);
                Hit last = null;
                for (int n = ids.Count - 1; n >= from; n--)
                {
                    // One byte first so opaque blobs are not copied. substr never pulls the file body.
                    byte[] kind = QueryBlob(conn, "SELECT substr(data,1,1) FROM blobs WHERE id=?", ids[n]);
                    if (kind == null || kind.Length == 0 || kind[0] != (byte)'{') continue;
                    byte[] blob = QueryBlob(conn, "SELECT substr(data,1,4096) FROM blobs WHERE id=?", ids[n]);
                    if (blob == null || blob.Length == 0 || blob[0] != (byte)'{') continue;
                    string head = Encoding.UTF8.GetString(blob);
                    string end = "";
                    if (head.IndexOf("\"path\":\"", StringComparison.Ordinal) < 0 &&
                        head.IndexOf("\"file_path\":\"", StringComparison.Ordinal) < 0)
                    {
                        byte[] tailBytes = QueryBlob(conn, "SELECT substr(data, max(1, length(data)-1023), 1024) FROM blobs WHERE id=?", ids[n]);
                        if (tailBytes != null) end = Encoding.UTF8.GetString(tailBytes);
                    }
                    BytesRead += head.Length + end.Length;
                    Hit hit = ParseCursor(head, end);
                    if (hit == null || hit.Kind == null) continue;
                    if (walLen > 0)
                    {
                        try { hit.When = File.GetLastWriteTimeUtc(wal); }
                        catch (Exception) { hit.When = DateTime.UtcNow; }
                    }
                    else hit.When = DateTime.UtcNow;
                    last = hit;
                    break;
                }
                if (last != null) tail.Last = last;
            }
            finally { Native.sqlite3_close(conn); }
        }

        static Hit ParseCursor(string head, string end)
        {
            var hit = new Hit();
            string role = Scan.JsonString(head, "\"role\":\"", 0);
            bool call = head.IndexOf("\"type\":\"tool-call\"", StringComparison.Ordinal) >= 0;
            bool result = head.IndexOf("\"type\":\"tool-result\"", StringComparison.Ordinal) >= 0;
            bool reasoning = head.IndexOf("\"type\":\"reasoning\"", StringComparison.Ordinal) >= 0;
            bool text = head.IndexOf("\"type\":\"text\"", StringComparison.Ordinal) >= 0;
            if (call || result)
            {
                string name = Scan.JsonString(head, "\"toolName\":\"", 0);
                hit.Kind = KindFor(name);
                hit.File = Scan.FileFrom(head, end);
                hit.Open = call && !result;
                return hit;
            }
            if (reasoning && role == "assistant")
            {
                hit.Kind = "thinking";
                return hit;
            }
            if (text && role == "assistant")
            {
                hit.Kind = "answering";
                return hit;
            }
            return null;
        }

        static byte[] QueryBlob(IntPtr conn, string sql, string arg)
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
                int st = Native.sqlite3_step(stmt);
                if (st != Native.SqliteRow) return null;
                IntPtr p = Native.sqlite3_column_blob(stmt, 0);
                int n = Native.sqlite3_column_bytes(stmt, 0);
                if (p == IntPtr.Zero || n <= 0) return null;
                // Meta values are hex text. Blobs may be large; cap the copy.
                int copy = n;
                if (copy > 256 * 1024) copy = 256 * 1024;
                byte[] buf = new byte[copy];
                Marshal.Copy(p, buf, 0, copy);
                if (n > copy)
                {
                    // Keep the tail of a large blob so a path that sits after a file body is still visible.
                    int back = Math.Min(2048, n);
                    byte[] both = new byte[copy + back];
                    Buffer.BlockCopy(buf, 0, both, 0, copy);
                    Marshal.Copy(new IntPtr(p.ToInt64() + (n - back)), both, copy, back);
                    return both;
                }
                return buf;
            }
            finally { Native.sqlite3_finalize(stmt); }
        }

        static string HexJsonString(byte[] hexText, string key)
        {
            // meta.value is hex of JSON. Decode only enough to find one key.
            int n = hexText.Length / 2;
            byte[] raw = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int hi = HexVal(hexText[i * 2]);
                int lo = HexVal(hexText[i * 2 + 1]);
                if (hi < 0 || lo < 0) return null;
                raw[i] = (byte)((hi << 4) | lo);
            }
            string json = Encoding.UTF8.GetString(raw);
            return Scan.JsonString(json, "\"" + key + "\":\"", 0);
        }

        static int HexVal(byte c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        static string Hex(byte[] b, int o, int n)
        {
            var sb = new StringBuilder(n * 2);
            for (int i = 0; i < n; i++) sb.Append(b[o + i].ToString("x2"));
            return sb.ToString();
        }
    }

    static class Git
    {
        public static GitInfo Facts(string cwd, long createFileTime)
        {
            var g = new GitInfo();
            if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) return g;
            string top = One(cwd, "rev-parse --show-toplevel");
            if (string.IsNullOrEmpty(top)) return g;
            top = top.Trim();
            string gitPath = Path.Combine(top, ".git");
            if (File.Exists(gitPath))
            {
                string gitdir = ReadGitdir(gitPath);
                string main = MainRepo(gitdir);
                g.Repo = NameOf(string.IsNullOrEmpty(main) ? top : main);
                g.Worktree = NameOf(top);
                if (string.Equals(g.Repo, g.Worktree, StringComparison.OrdinalIgnoreCase)) g.Worktree = null;
            }
            else
            {
                g.Repo = NameOf(top);
            }
            string branch = One(cwd, "rev-parse --abbrev-ref HEAD");
            g.Branch = string.IsNullOrEmpty(branch) ? null : branch.Trim();
            if (g.Branch == "HEAD") g.Branch = null;
            string since = DateTime.FromFileTimeUtc(createFileTime).ToString("yyyy-MM-ddTHH:mm:ssZ");
            string count = One(cwd, "rev-list --count --since=" + since + " HEAD");
            int n;
            if (int.TryParse((count ?? "").Trim(), out n)) g.Commits = n;
            g.Subject = Line(cwd, "log -1 --since=" + since + " --format=%s");
            g.Changed = CountLines(cwd, "diff --name-only HEAD") + CountLines(cwd, "ls-files --others --exclude-standard");
            return g;
        }

        static string NameOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return Scan.BaseName(path.TrimEnd('\\', '/'));
        }

        static string ReadGitdir(string gitFile)
        {
            try
            {
                string line = File.ReadAllText(gitFile);
                const string p = "gitdir:";
                int i = line.IndexOf(p, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return null;
                return line.Substring(i + p.Length).Trim();
            }
            catch (Exception) { return null; }
        }

        static string MainRepo(string gitdir)
        {
            if (string.IsNullOrEmpty(gitdir)) return null;
            gitdir = gitdir.Replace('/', '\\');
            int mark = gitdir.IndexOf("\\.git\\", StringComparison.OrdinalIgnoreCase);
            if (mark < 0 && gitdir.EndsWith("\\.git", StringComparison.OrdinalIgnoreCase))
                mark = gitdir.Length - 5;
            if (mark < 0) return null;
            return gitdir.Substring(0, mark);
        }

        static string One(string cwd, string args) { return Line(cwd, args); }

        static string Line(string cwd, string args)
        {
            string[] lines = Run(cwd, args, 4);
            return lines.Length == 0 ? null : lines[0];
        }

        static int CountLines(string cwd, string args)
        {
            return Run(cwd, args, 2000).Length;
        }

        static string[] Run(string cwd, string args, int maxLines)
        {
            var psi = new ProcessStartInfo();
            psi.FileName = "git";
            psi.Arguments = "--no-optional-locks " + args;
            psi.WorkingDirectory = cwd;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            Process proc = null;
            try
            {
                proc = Process.Start(psi);
                proc.ErrorDataReceived += delegate { };
                proc.BeginErrorReadLine();
                var lines = new List<string>();
                while (lines.Count < maxLines)
                {
                    string line = proc.StandardOutput.ReadLine();
                    if (line == null) break;
                    if (line.Length > 0) lines.Add(line);
                }
                if (!proc.WaitForExit(2000))
                {
                    try { proc.Kill(); } catch (Exception) { }
                    proc.WaitForExit(1000);
                }
                return lines.ToArray();
            }
            catch (Exception) { return new string[0]; }
            finally { if (proc != null) proc.Dispose(); }
        }
    }

    static class Probe
    {
        static Dictionary<int, long> cpu = new Dictionary<int, long>();
        public static long LastLogUs;

        public static List<Row> Pass(out long logMs)
        {
            var procs = Procs.Snapshot();
            var byPid = new Dictionary<int, Proc>();
            for (int i = 0; i < procs.Count; i++) byPid[procs[i].Pid] = procs[i];
            var candidates = new List<Proc>();
            for (int i = 0; i < procs.Count; i++)
            {
                Proc p = procs[i];
                if (!Interesting(p.Exe)) continue;
                if (p.CreateTime <= 0) continue;
                Proc parent;
                if (byPid.TryGetValue(p.Ppid, out parent) && parent.CreateTime > p.CreateTime) p.Ppid = 0;
                p.Cmd = Procs.CommandLine(p.Pid);
                p.Cwd = Procs.CurrentDirectory(p.Pid);
                if (IsAgent(p)) candidates.Add(p);
            }
            var keep = new List<Proc>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!HasAgentDescendant(candidates[i], candidates)) keep.Add(candidates[i]);
            }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var rows = new List<Row>();
            long ticks = 0;
            for (int i = 0; i < keep.Count; i++)
            {
                Proc p = keep[i];
                var row = new Row();
                row.Pid = p.Pid;
                row.Agent = Pretty(p);
                row.Model = ModelOf(p);
                row.Child = LeafChild(p.Pid, byPid);
                var part = Stopwatch.StartNew();
                if (row.Agent == "Claude Code") Logs.Claude(home, p, row);
                else if (row.Agent == "Codex") Logs.Codex(home, p, row);
                else if (row.Agent == "Cursor") Logs.Cursor(home, p, row);
                ticks += part.ElapsedTicks;
                string gitCwd = p.Cwd;
                if (!string.IsNullOrEmpty(row.WorkCwd)) gitCwd = row.WorkCwd;
                row.Git = Git.Facts(gitCwd, p.CreateTime);
                Decide(row, p);
                rows.Add(row);
            }
            LastLogUs = Stopwatch.Frequency == 0 ? 0 : ticks * 1000000 / Stopwatch.Frequency;
            logMs = LastLogUs / 1000;
            return rows;
        }

        static bool Interesting(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            string n = exe.ToLowerInvariant();
            return n == "claude.exe" || n == "codex.exe" || n == "node.exe" || n == "python.exe" || n == "bun.exe";
        }

        static bool IsAgent(Proc p)
        {
            string n = (p.Exe ?? "").ToLowerInvariant();
            if (n == "claude.exe") return true;
            if (n == "codex.exe") return true;
            if (Scan.HasFlag(p.Cmd, "--model") && Scan.HasFlag(p.Cmd, "--worktree")) return true;
            if (Scan.HasFlag(p.Cmd, "--model") && (n == "python.exe" || n == "bun.exe" || n == "node.exe")) return true;
            return false;
        }

        static bool HasAgentDescendant(Proc p, List<Proc> all)
        {
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Pid == p.Pid) continue;
                int guard = 0;
                int id = all[i].Ppid;
                while (id != 0 && guard++ < 12)
                {
                    if (id == p.Pid) return true;
                    Proc up = null;
                    for (int j = 0; j < all.Count; j++) if (all[j].Pid == id) { up = all[j]; break; }
                    if (up == null) break;
                    id = up.Ppid;
                }
            }
            return false;
        }

        static string Pretty(Proc p)
        {
            string n = (p.Exe ?? "").ToLowerInvariant();
            if (n == "claude.exe") return "Claude Code";
            if (n == "codex.exe") return "Codex";
            if (Scan.HasFlag(p.Cmd, "--worktree")) return "Cursor";
            string exe = string.IsNullOrEmpty(p.Exe) ? "process" : Path.GetFileNameWithoutExtension(p.Exe);
            return exe;
        }

        static string ModelOf(Proc p)
        {
            string m = Scan.Arg(p.Cmd, "--model");
            if (string.IsNullOrEmpty(m)) return null;
            if (m.IndexOf('\\') >= 0 || m.IndexOf('/') >= 0) m = Scan.BaseName(m);
            return m;
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
                    if (c.Ppid != id || c.Pid == pid) continue;
                    if (!seen.Add(c.Pid)) continue;
                    string exe = (c.Exe ?? "").ToLowerInvariant();
                    if (exe == "conhost.exe" || exe == "cmd.exe" || exe == "nohup.exe")
                    {
                        stack.Add(c.Pid);
                        continue;
                    }
                    leaf = c.Exe;
                    stack.Add(c.Pid);
                }
            }
            return leaf;
        }

        static void Decide(Row row, Proc p)
        {
            long prev;
            bool cpuMoved = cpu.TryGetValue(p.Pid, out prev) && p.CpuTime > prev + 10000 * 20;
            cpu[p.Pid] = p.CpuTime;
            int age = Age(row.Action);
            bool open = row.Action != null && row.Action.Open;
            string kind = row.Action == null ? null : row.Action.Kind;
            if (!string.IsNullOrEmpty(row.Child) || cpuMoved)
            {
                row.State = "working";
                row.Why = !string.IsNullOrEmpty(row.Child) ? "running: " + row.Child : "cpu";
            }
            else if (kind == "thinking" && age >= 0 && age < 45)
            {
                row.State = "working";
                row.Why = "thinking";
            }
            else if (open && age >= 0 && age < 180)
            {
                row.State = "waiting";
                row.Why = kind == "waiting" ? "waiting for approval" : "tool started, no child";
            }
            else if (age >= 300 || (row.Action == null && ProcessAgeMin(p) >= 5))
            {
                row.State = "quiet";
                row.Why = "no activity for 5 min";
            }
            else if (kind == "answering")
            {
                row.State = "waiting";
                row.Why = "answered, waiting";
            }
            else if (kind == "waiting")
            {
                row.State = "waiting";
                row.Why = "waiting for approval";
            }
            else if (row.Action != null && age >= 0 && age < 300)
            {
                row.State = "working";
                row.Why = kind;
            }
            else
            {
                row.State = "waiting";
                row.Why = p.Cwd == null ? "running as admin" : "no child yet";
            }
        }

        static int ProcessAgeMin(Proc p)
        {
            try
            {
                return (int)(DateTime.UtcNow - DateTime.FromFileTimeUtc(p.CreateTime)).TotalMinutes;
            }
            catch (Exception) { return 0; }
        }

        static int Age(Hit hit)
        {
            if (hit == null || hit.When == DateTime.MinValue) return -1;
            int s = (int)(DateTime.UtcNow - hit.When.ToUniversalTime()).TotalSeconds;
            return s < 0 ? 0 : s;
        }

        public static string Json(List<Row> rows)
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                GitInfo g = r.Git ?? new GitInfo();
                sb.Append("  {\n");
                Field(sb, "agent", r.Agent, true);
                Field(sb, "model", r.Model, true);
                sb.Append("    \"pid\": ").Append(r.Pid).Append(",\n");
                Field(sb, "repo", g.Repo, true);
                Field(sb, "branch", g.Branch, true);
                Field(sb, "worktree", g.Worktree, true);
                Field(sb, "child_now", r.Child, true);
                sb.Append("    \"git\": {");
                sb.Append("\"commits_since_start\": ").Append(g.Commits);
                sb.Append(", \"changed_files\": ").Append(g.Changed);
                sb.Append(", \"last_subject\": ");
                sb.Append(J(g.Subject));
                sb.Append("},\n");
                sb.Append("    \"last_action\": ");
                if (r.Action == null || r.Action.Kind == null) sb.Append("null");
                else
                {
                    int age = Age(r.Action);
                    sb.Append("{\"kind\": ").Append(J(r.Action.Kind));
                    sb.Append(", \"target_file_name\": ").Append(J(r.Action.File));
                    sb.Append(", \"age_s\": ").Append(age < 0 ? "null" : age.ToString());
                    sb.Append("}");
                }
                sb.Append(",\n");
                Field(sb, "state", r.State, true);
                Field(sb, "why", r.Why, false);
                sb.Append("  }");
                if (i + 1 < rows.Count) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("]\n");
            return sb.ToString();
        }

        static void Field(StringBuilder sb, string key, string value, bool comma)
        {
            sb.Append("    \"").Append(key).Append("\": ").Append(J(value));
            if (comma) sb.Append(",");
            sb.Append("\n");
        }

        static string J(string s)
        {
            if (s == null) return "null";
            s = Redact.Show(s);
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' || c == '"') { sb.Append('\\'); sb.Append(c); }
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest") return SelfTest();
            if (args.Length > 0 && args[0] == "--bench")
            {
                int n = 20;
                if (args.Length > 1) int.TryParse(args[1], out n);
                if (n < 1) n = 1;
                long logMs;
                List<Row> rows = Probe.Pass(out logMs);
                long sum = 0;
                long max = 0;
                for (int i = 0; i < n; i++)
                {
                    long before = Logs.BytesRead;
                    var sw = Stopwatch.StartNew();
                    // Re-run a full pass so offsets advance the way a live poll does.
                    rows = Probe.Pass(out logMs);
                    sw.Stop();
                    long bytes = Logs.BytesRead - before;
                    Console.Error.WriteLine("pass_ms=" + sw.ElapsedMilliseconds + " log_ms=" + logMs + " log_us=" + Probe.LastLogUs + " bytes=" + bytes + " agents=" + rows.Count);
                    sum += logMs;
                    if (logMs > max) max = logMs;
                }
                Console.Error.WriteLine("log_avg_ms=" + (sum / n) + " log_max_ms=" + max);
                Console.Write(Probe.Json(rows));
                return 0;
            }
            if (args.Length > 0 && args[0] == "--watch")
            {
                int seconds = 300;
                int every = 10;
                if (args.Length < 4) { Console.Error.WriteLine("usage: agents --watch <seconds> <every> <dir>"); return 2; }
                int.TryParse(args[1], out seconds);
                int.TryParse(args[2], out every);
                if (every < 1) every = 10;
                string dir = args[3];
                string parent = Path.GetDirectoryName(Path.GetFullPath(dir));
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) { Console.Error.WriteLine("parent missing"); return 2; }
                Directory.CreateDirectory(dir);
                string root = Path.GetFullPath(dir);
                if (!root.EndsWith("\\")) root += "\\";
                int n = 0;
                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                while (true)
                {
                    n++;
                    long logMs;
                    var sw = Stopwatch.StartNew();
                    long before = Logs.BytesRead;
                    List<Row> rows = Probe.Pass(out logMs);
                    sw.Stop();
                    string json = Probe.Json(rows);
                    string name = Path.GetFullPath(Path.Combine(dir, "sample-" + n.ToString("000") + ".json"));
                    if (!name.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return 2;
                    File.WriteAllText(name, json);
                    string costLine = "n=" + n + " pass_ms=" + sw.ElapsedMilliseconds + " log_ms=" + logMs + " log_us=" + Probe.LastLogUs + " bytes=" + (Logs.BytesRead - before) + " agents=" + rows.Count;
                    Console.Error.WriteLine(costLine);
                    File.AppendAllText(Path.Combine(dir, "cost.txt"), costLine + "\n");
                    if (DateTime.UtcNow >= deadline) break;
                    System.Threading.Thread.Sleep(every * 1000);
                }
                return 0;
            }
            long one;
            Console.Write(Probe.Json(Probe.Pass(out one)));
            Console.Error.WriteLine("log_ms=" + one);
            return 0;
        }

        static int SelfTest()
        {
            int bad = 0;
            if (Scan.EncodeCwd("C:\\repo\\app") != "C--repo-app") bad++;
            if (Scan.Md5Hex("a") != "0cc175b9c0f1b6a831c399e269772661") bad++;
            if (Scan.Arg("node x --model grok --worktree wt", "--model") != "grok") bad++;
            if (Scan.BaseName("C:\\repo\\app\\notes.md") != "notes.md") bad++;
            if (Redact.Show("token sk-abcdefghijklmnopqrstuvwxyz") .IndexOf("sk-") >= 0) bad++;
            string head = "{\"type\":\"assistant\",\"timestamp\":\"2026-01-01T00:00:01.000Z\",\"requestedModel\":\"example-model\",\"message\":{\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\",\"input\":{\"file_path\":\"C:\\\\repo\\\\app\\\\notes.md\",\"old_string\":\"a\",\"new_string\":\"b\"}}]}}";
            if (Scan.FileFrom(head, "") != "notes.md") bad++;
            if (Scan.JsonString(head, "\"requestedModel\":\"", 0) != "example-model") bad++;
            if (Scan.JsonString(head, "\"name\":\"", 0) != "Edit") bad++;
            Console.WriteLine(bad == 0 ? "ok" : "fail " + bad);
            return bad == 0 ? 0 : 1;
        }
    }
}
