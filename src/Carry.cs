// ClaudeCarry core: export / import Claude Code chats and memory between Claude accounts.
// C# 5 (built with the csc.exe that ships with Windows), .NET Framework 4.8.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ClaudeCarry
{
    public class AccountInfo
    {
        public string Id;       // "<accountId>\<orgId>"
        public string Path;
        public int Chats;
        public DateTime LastUsed;

        public override string ToString()
        {
            string acct = Id.Split('\\')[0];
            return string.Format("{0}...  ({1} chats, last used {2:yyyy-MM-dd HH:mm})", acct.Substring(0, Math.Min(8, acct.Length)), Chats, LastUsed);
        }
    }

    public class ChatInfo
    {
        public string AccountId, SessionId, CliId, Title, Cwd;
        public DateTime LastActivity;
        public bool Archived, HasTranscript;
        public long Size;
    }

    public class ImportResult
    {
        public int Added, Same, Chats, ChatsSkipped;
        public List<string> Conflicts = new List<string>();
        public List<string> Merged = new List<string>();
        public bool SettingsAside;
        public string Backup;
    }

    public class Carry
    {
        public string Home = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        string app = DetectApp();
        bool appFixed;   // set by --app / CLAUDECARRY_APPDATA: stop looking for the app's folder ourselves
        public string App { get { return app; } set { app = value; appFixed = true; } }
        public string BackupDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClaudeCarry-backups");
        public Action<string> Log = delegate { };

        // Settings keys whose values are dropped. "auth(?!ored)" keeps includeCoAuthoredBy and
        // "token(?!s)" keeps limits like MAX_THINKING_TOKENS.
        static readonly Regex SecretKey = new Regex(
            @"api[_-]?key|access[_-]?key|private[_-]?key|session[_-]?key|secret|token(?!s)|passw|pwd|^pass$|credential|cookie|bearer|" +
            @"(^|[_-])pat($|[_-])|dsn|connection[_-]?string|database[_-]?url|auth(?!ored)", RegexOptions.IgnoreCase);
        // Token-shaped text inside any string value (command lines in permissions, MCP args, URLs).
        static readonly Regex SecretValue = new Regex(
            @"sk-ant-[\w\-]{8,}|sk-[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_\-]{20,}|" +
            @"xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{30,}|" +
            @"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}|(?<=[Bb]earer\s+)[A-Za-z0-9._\-+/=]{8,}");
        static readonly Regex UrlCredentials = new Regex(@"(?<=://[^/\s:@]+:)[^/\s@]+(?=@)");
        static readonly Regex SecretFlag = new Regex(@"^--?[\w-]*(key|token|secret|passw|pwd|credential)[\w-]*$", RegexOptions.IgnoreCase);
        static readonly Regex SecretFlagInline = new Regex(@"(--?[\w-]*(key|token|secret|passw|pwd|credential)[\w-]*=)\S+", RegexOptions.IgnoreCase);
        const string Removed = "[removed by ClaudeCarry]";

        // Already-compressed files are stored as they are: compressing them again only costs time.
        static readonly HashSet<string> Stored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".zip", ".gz", ".7z", ".mp4", ".mp3", ".webm", ".pdf", ".woff", ".woff2" };

        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        // All file access goes through \\?\ paths so folders deeper than 260 characters work
        // (scratch-chat project folders often are). Needs the long-path switches set in Program.Main.
        const string LongPrefix = @"\\?\";
        const string LongUnc = @"\\?\UNC\";
        static string LP(string p)
        {
            if (p.StartsWith(LongPrefix)) return p;
            p = System.IO.Path.GetFullPath(p);
            return p.StartsWith(@"\\") ? LongUnc + p.Substring(2) : LongPrefix + p;   // \\server\share needs \\?\UNC\
        }
        public static string Plain(string p)
        {
            if (p.StartsWith(LongUnc)) return @"\\" + p.Substring(LongUnc.Length);
            return p.StartsWith(LongPrefix) ? p.Substring(4) : p;
        }
        string HomeL { get { return LP(Home); } }
        string SessionsDir { get { return System.IO.Path.Combine(LP(App), "claude-code-sessions"); } }

        static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };
        }

        static Dictionary<string, object> TryParse(string text)
        {
            try { return Json().DeserializeObject(text) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        static string Full(string p) { return Plain(System.IO.Path.GetFullPath(p)).TrimEnd('\\'); }

        // Where the Claude app can keep its data (the folder that holds claude-code-sessions):
        //  - %APPDATA%\Claude: the version from the Claude installer.
        //  - %LOCALAPPDATA%\Packages\<package>\LocalCache\Roaming\Claude: the Microsoft Store version, which redirects that folder.
        //  - %LOCALAPPDATA%\Claude-Data\<name>: where newer Microsoft Store builds keep it, outside the package.
        static string StandardApp { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude"); } }
        static string LocalApp { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string PackagesDir { get { return System.IO.Path.Combine(LocalApp, "Packages"); } }

        static List<string> AppFolders()
        {
            var list = new List<string> { StandardApp };
            try
            {
                if (Directory.Exists(PackagesDir))
                    foreach (var p in Directory.GetDirectories(PackagesDir))
                    {
                        string d = System.IO.Path.Combine(p, "LocalCache", "Roaming", "Claude");
                        if (Directory.Exists(d)) list.Add(d);
                    }
            }
            catch (Exception) { }
            try
            {
                foreach (var data in Directory.GetDirectories(LocalApp, "Claude-Data*"))
                    list.AddRange(Directory.GetDirectories(data));
            }
            catch (Exception) { }
            return list;
        }

        // The app folder that holds a chat list. If several do, the one written to last wins.
        static string DetectApp()
        {
            string best = null;
            var bestTime = DateTime.MinValue;
            foreach (var c in AppFolders())
            {
                string sessions = System.IO.Path.Combine(c, "claude-code-sessions");
                try
                {
                    if (!Directory.Exists(sessions)) continue;
                    var t = Directory.GetLastWriteTime(sessions);
                    foreach (var d in Directory.GetDirectories(sessions))
                    {
                        var dt = Directory.GetLastWriteTime(d);
                        if (dt > t) t = dt;
                    }
                    if (best == null || t > bestTime) { best = c; bestTime = t; }
                }
                catch (Exception) { }
            }
            return best ?? StandardApp;
        }

        // The chat-list folders we look in, for the message shown when no account is found.
        public List<string> SearchedFolders()
        {
            var list = new List<string>();
            if (appFixed) { list.Add(Plain(SessionsDir)); return list; }
            var folders = AppFolders();
            foreach (var f in folders) list.Add(System.IO.Path.Combine(f, "claude-code-sessions"));
            // Name the places that don't exist on this PC too, so the list shows everything that was tried.
            string dataDir = System.IO.Path.Combine(LocalApp, "Claude-Data");
            if (!folders.Any(f => f.StartsWith(PackagesDir, StringComparison.OrdinalIgnoreCase)))
                list.Add(System.IO.Path.Combine(PackagesDir, "*", "LocalCache", "Roaming", "Claude", "claude-code-sessions"));
            if (!folders.Any(f => f.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase)))
                list.Add(System.IO.Path.Combine(dataDir + "*", "*", "claude-code-sessions"));
            return list;
        }

        // False when pointed at a copy for testing, so the "Claude is running" check doesn't apply.
        public bool IsLiveApp
        {
            get
            {
                if (!appFixed) return true;
                string a = Full(App);
                return AppFolders().Any(s => string.Equals(a, Full(s), StringComparison.OrdinalIgnoreCase));
            }
        }

        // True while the Claude desktop app runs. The Claude Code CLI is also named claude.exe, so
        // only processes from the app's install folders count; one we can't inspect counts too.
        public static bool ClaudeRunning()
        {
            foreach (var p in Process.GetProcessesByName("claude"))
            {
                string path;
                try { path = p.MainModule.FileName; } catch (Exception) { return true; }
                finally { p.Dispose(); }
                if (path.IndexOf(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf(@"\Claude\claude-code\", StringComparison.OrdinalIgnoreCase) >= 0)   // CLI started by the app
                    return true;
            }
            return false;
        }

        // ---------- accounts ----------

        public List<AccountInfo> GetAccounts()
        {
            var list = new List<AccountInfo>();
            if (!appFixed) app = DetectApp();   // the folder may have appeared since the window opened
            if (!Directory.Exists(SessionsDir)) return list;
            foreach (var a in Directory.GetDirectories(SessionsDir))
                foreach (var o in Directory.GetDirectories(a))
                {
                    var chats = Directory.GetFiles(o, "local_*.json");
                    list.Add(new AccountInfo
                    {
                        Id = System.IO.Path.GetFileName(a) + "\\" + System.IO.Path.GetFileName(o),
                        Path = o,
                        Chats = chats.Length,
                        LastUsed = LastUsed(o, chats)
                    });
                }
            return list.OrderByDescending(x => x.LastUsed).ToList();
        }

        // When the app last used this account, from what only the Claude app writes: scheduled-tasks.json,
        // deleted_ markers, waiting-input/backlog folders. The chat list and archive index are skipped,
        // because ClaudeCarry writes those too (and copied chats carry another account's activity times).
        // The chats' own times are a fallback for a folder with nothing else in it.
        static DateTime LastUsed(string dir, string[] chats)
        {
            var newest = DateTime.MinValue;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                string n = System.IO.Path.GetFileName(f);
                if ((n.StartsWith("local_") && n.EndsWith(".json")) || n == "archived-sessions.idx") continue;
                var t = File.GetLastWriteTime(f);
                if (t > newest) newest = t;
            }
            foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
            {
                var t = Directory.GetLastWriteTime(d);
                if (t > newest) newest = t;
            }
            if (newest != DateTime.MinValue) return newest;
            foreach (var f in chats)
            {
                var j = TryParse(SafeRead(f));
                if (j == null) continue;
                foreach (var k in new[] { "lastFocusedAt", "lastActivityAt", "createdAt" })
                {
                    var t = UnixMs(j, k);
                    if (t > newest) newest = t;
                }
            }
            return newest == DateTime.MinValue ? Directory.GetLastWriteTime(dir) : newest;
        }

        static string SafeRead(string path)
        {
            try { return File.ReadAllText(path, Utf8); } catch (Exception) { return ""; }
        }

        static DateTime UnixMs(Dictionary<string, object> j, string key)
        {
            object v;
            if (!j.TryGetValue(key, out v) || v == null) return DateTime.MinValue;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(v)).LocalDateTime; }
            catch (Exception) { return DateTime.MinValue; }
        }

        // ---------- zip helpers ----------

        class Stats { public int Files; public long Bytes; public List<string> Failed = new List<string>(); }

        static void AddFile(ZipArchive zip, string path, string entryName)
        {
            // FileShare.ReadWrite so transcripts of running chats can still be read.
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            {
                var level = Stored.Contains(System.IO.Path.GetExtension(path)) ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                var entry = zip.CreateEntry(entryName, level);
                entry.LastWriteTime = ZipTime(File.GetLastWriteTime(path));
                using (var dst = entry.Open()) src.CopyTo(dst, 1 << 16);
            }
        }

        // Zip files only hold dates from 1980 to 2107.
        static DateTimeOffset ZipTime(DateTime t)
        {
            if (t.Year < 1980) return new DateTime(1980, 1, 1, 0, 0, 0);
            if (t.Year > 2107) return new DateTime(2107, 12, 31, 0, 0, 0);
            return t;
        }

        // Adds one file; a file that can't be read is listed instead of stopping the whole export.
        static void TryAddFile(ZipArchive zip, string path, string entryName, string label, Stats stats)
        {
            try
            {
                AddFile(zip, path, entryName);
                stats.Files++;
                stats.Bytes += new FileInfo(path).Length;
            }
            catch (Exception) { stats.Failed.Add(label); }
        }

        static void AddText(ZipArchive zip, string text, string entryName)
        {
            var entry = zip.CreateEntry(entryName);
            using (var w = new StreamWriter(entry.Open(), Utf8)) w.Write(text);
        }

        static void AddTree(ZipArchive zip, string root, string prefix, Func<string, bool> filter, Stats stats)
        {
            if (!Directory.Exists(root)) return;
            string rootFull = LP(root).TrimEnd('\\');
            foreach (var f in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(rootFull.Length + 1);
                if (filter != null && !filter(rel)) continue;
                TryAddFile(zip, f, prefix + "/" + rel.Replace('\\', '/'), rel, stats);
            }
        }

        static string ReadText(ZipArchiveEntry e)
        {
            using (var r = new StreamReader(e.Open(), Utf8)) return r.ReadToEnd();
        }

        static bool SameContent(ZipArchiveEntry e, string path)
        {
            if (new FileInfo(path).Length != e.Length) return false;
            using (var sha = SHA256.Create())
            {
                byte[] a, b;
                using (var s = e.Open()) a = sha.ComputeHash(s);
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16)) b = sha.ComputeHash(s);
                return a.SequenceEqual(b);
            }
        }

        static void WriteNew(ZipArchiveEntry e, string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            // CreateNew: fails instead of overwriting if something appeared in the meantime.
            using (var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            using (var src = e.Open()) src.CopyTo(dst, 1 << 16);
            File.SetLastWriteTime(path, e.LastWriteTime.DateTime);
        }

        // ---------- settings without secrets ----------

        // Returns the node with secrets removed: secret-named keys are dropped whatever their value,
        // and token-shaped text is blanked inside every string, including strings in arrays.
        static object RemoveSecrets(object node)
        {
            var dict = node as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (var k in dict.Keys.ToList())
                {
                    if (SecretKey.IsMatch(k)) dict.Remove(k);
                    else dict[k] = RemoveSecrets(dict[k]);
                }
                return dict;
            }
            var arr = node as object[];
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    // "--api-key", "value": the value after a secret flag goes too.
                    if (i > 0 && arr[i - 1] is string && SecretFlag.IsMatch((string)arr[i - 1]) && arr[i] is string) arr[i] = Removed;
                    else arr[i] = RemoveSecrets(arr[i]);
                }
                return arr;
            }
            var s = node as string;
            if (s != null)
            {
                s = SecretValue.Replace(s, Removed);
                s = UrlCredentials.Replace(s, Removed);
                s = SecretFlagInline.Replace(s, "$1" + Removed);
                return s;
            }
            return node;
        }

        // ---------- chats ----------

        // Transcript id -> its folder under .claude\projects, for every transcript on disk.
        public Dictionary<string, string> TranscriptIndex()
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string projects = System.IO.Path.Combine(HomeL, "projects");
            if (!Directory.Exists(projects)) return index;
            foreach (var d in Directory.GetDirectories(projects))
                foreach (var f in Directory.EnumerateFiles(d, "*.jsonl"))
                {
                    string id = System.IO.Path.GetFileNameWithoutExtension(f);
                    if (!index.ContainsKey(id)) index[id] = d;
                }
            return index;
        }

        static HashSet<string> ReadArchived(string idxText)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var idx = TryParse(idxText);
            if (idx != null && idx.ContainsKey("archived") && idx["archived"] is object[])
                foreach (var o in (object[])idx["archived"]) set.Add(Convert.ToString(o));
            return set;
        }

        static ChatInfo ParseChat(string accountId, string fileName, string text, HashSet<string> archived)
        {
            var j = TryParse(text);
            if (j == null || !j.ContainsKey("cliSessionId") || j["cliSessionId"] == null) return null;
            object t;
            var c = new ChatInfo
            {
                AccountId = accountId,
                SessionId = fileName.Substring(0, fileName.Length - 5),
                CliId = Convert.ToString(j["cliSessionId"]),
                Title = j.TryGetValue("title", out t) && t != null ? Convert.ToString(t) : "(untitled)",
                Cwd = j.TryGetValue("cwd", out t) && t != null ? Convert.ToString(t) : ""
            };
            c.LastActivity = UnixMs(j, "lastActivityAt");
            if (c.LastActivity == DateTime.MinValue) c.LastActivity = UnixMs(j, "createdAt");
            c.Archived = archived.Contains(c.SessionId) || (j.TryGetValue("isArchived", out t) && t is bool && (bool)t);
            return c;
        }

        public List<ChatInfo> GetChats(AccountInfo acc)
        {
            return GetChats(acc, TranscriptIndex());
        }

        public List<ChatInfo> GetChats(AccountInfo acc, Dictionary<string, string> index)
        {
            var list = new List<ChatInfo>();
            string idx = System.IO.Path.Combine(acc.Path, "archived-sessions.idx");
            var archived = File.Exists(idx) ? ReadArchived(SafeRead(idx)) : new HashSet<string>();
            foreach (var f in Directory.GetFiles(acc.Path, "local_*.json"))
            {
                var c = ParseChat(acc.Id, System.IO.Path.GetFileName(f), SafeRead(f), archived);
                if (c == null) continue;
                string dir;
                if (index.TryGetValue(c.CliId, out dir))
                {
                    c.HasTranscript = true;
                    try
                    {
                        c.Size = new FileInfo(System.IO.Path.Combine(dir, c.CliId + ".jsonl")).Length;
                        string sub = System.IO.Path.Combine(dir, c.CliId);
                        if (Directory.Exists(sub)) c.Size += Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
                    }
                    catch (Exception) { }
                }
                list.Add(c);
            }
            return list.OrderByDescending(x => x.LastActivity).ToList();
        }

        // Chats inside an export zip that have their transcript included.
        public static List<ChatInfo> ListZipChats(string zipPath)
        {
            var list = new List<ChatInfo>();
            using (var zip = ZipFile.OpenRead(LP(zipPath)))
            {
                var transcripts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var archived = new Dictionary<string, HashSet<string>>();
                foreach (var e in zip.Entries)
                {
                    var m = Regex.Match(e.FullName, @"^claude/projects/[^/]+/([^/]+)\.jsonl$");
                    if (m.Success) transcripts[m.Groups[1].Value] = e.Length;
                    m = Regex.Match(e.FullName, @"^app/sessions/(.+)/archived-sessions\.idx$");
                    if (m.Success) archived[m.Groups[1].Value] = ReadArchived(ReadText(e));
                }
                // Subagent logs and tool results count towards a chat's size.
                foreach (var e in zip.Entries)
                {
                    var m = Regex.Match(e.FullName, @"^claude/projects/[^/]+/([^/]+)/");
                    long n;
                    if (m.Success && transcripts.TryGetValue(m.Groups[1].Value, out n)) transcripts[m.Groups[1].Value] = n + e.Length;
                }
                var seen = new HashSet<string>();
                foreach (var e in zip.Entries)
                {
                    var m = Regex.Match(e.FullName, @"^app/sessions/(.+)/(local_[^/]+\.json)$");
                    if (!m.Success) continue;
                    HashSet<string> arch;
                    if (!archived.TryGetValue(m.Groups[1].Value, out arch)) arch = new HashSet<string>();
                    var c = ParseChat(m.Groups[1].Value.Replace('/', '\\'), m.Groups[2].Value, ReadText(e), arch);
                    long size;
                    if (c == null || !transcripts.TryGetValue(c.CliId, out size) || !seen.Add(c.SessionId)) continue;
                    c.HasTranscript = true;
                    c.Size = size;
                    list.Add(c);
                }
            }
            return list.OrderByDescending(x => x.LastActivity).ToList();
        }

        // ---------- export ----------

        // chats == null exports everything. Otherwise only those chats (their transcript, subagent logs
        // and sidebar entry); includeShared adds the memory of their projects plus CLAUDE.md, skills, settings.
        public void Export(string outPath, List<ChatInfo> chats = null, bool includeShared = true)
        {
            outPath = LP(outPath);
            if (File.Exists(outPath)) throw new IOException(Plain(outPath) + " already exists.");
            var stats = new Stats();
            Log("Exporting to " + Plain(outPath) + " ...");

            var accounts = GetAccounts();
            string projects = System.IO.Path.Combine(HomeL, "projects");
            try
            {
                using (var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    if (chats == null)
                    {
                        // Transcripts, subagent logs, tool results and memory for every project.
                        AddTree(zip, projects, "claude/projects", null, stats);
                    }
                    else
                    {
                        var index = TranscriptIndex();
                        var memoryDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var cliDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // same chat picked in two accounts
                        foreach (var c in chats)
                        {
                            string dir;
                            if (!index.TryGetValue(c.CliId, out dir)) { Log("No transcript on disk for \"" + c.Title + "\", skipped."); continue; }
                            if (!cliDone.Add(c.CliId)) continue;
                            string key = System.IO.Path.GetFileName(dir);
                            string prefix = "claude/projects/" + key;
                            TryAddFile(zip, System.IO.Path.Combine(dir, c.CliId + ".jsonl"), prefix + "/" + c.CliId + ".jsonl", c.Title, stats);
                            AddTree(zip, System.IO.Path.Combine(dir, c.CliId), prefix + "/" + c.CliId, null, stats);
                            if (includeShared && memoryDone.Add(key))
                                AddTree(zip, System.IO.Path.Combine(dir, "memory"), prefix + "/memory", null, stats);
                        }
                    }

                    if (includeShared)
                    {
                        AddTree(zip, System.IO.Path.Combine(HomeL, "skills"), "claude/skills", r => !r.StartsWith(".trash"), stats);
                        string md = System.IO.Path.Combine(HomeL, "CLAUDE.md");
                        if (File.Exists(md)) TryAddFile(zip, md, "claude/CLAUDE.md", "CLAUDE.md", stats);

                        string settings = System.IO.Path.Combine(HomeL, "settings.json");
                        if (File.Exists(settings))
                        {
                            var obj = TryParse(SafeRead(settings));
                            if (obj == null) { Log("settings.json could not be read as JSON, left out."); stats.Failed.Add("settings.json"); }
                            else { AddText(zip, Json().Serialize(RemoveSecrets(obj)), "claude/settings.json"); stats.Files++; }
                        }
                    }

                    // The desktop app's per-account chat list (sidebar). No tokens in these files.
                    var wanted = chats == null ? null : new HashSet<string>(chats.Select(c => c.AccountId + "|" + c.SessionId), StringComparer.OrdinalIgnoreCase);
                    foreach (var acc in accounts)
                    {
                        string name = "app/sessions/" + acc.Id.Replace('\\', '/');
                        foreach (var f in Directory.GetFiles(acc.Path))
                        {
                            string fn = System.IO.Path.GetFileName(f);
                            if (fn.StartsWith("local_") && fn.EndsWith(".json"))
                            {
                                string sid = fn.Substring(0, fn.Length - 5);
                                if (wanted != null && !wanted.Contains(acc.Id + "|" + sid)) continue;
                                TryAddFile(zip, f, name + "/" + fn, fn, stats);
                            }
                            else if (fn == "archived-sessions.idx" && wanted == null)
                                TryAddFile(zip, f, name + "/" + fn, fn, stats);
                        }
                        if (wanted != null)
                        {
                            var arch = chats.Where(c => c.AccountId == acc.Id && c.Archived).Select(c => c.SessionId).ToArray();
                            if (arch.Length > 0)
                                AddText(zip, Json().Serialize(new Dictionary<string, object> { { "v", 1 }, { "archived", arch } }), name + "/archived-sessions.idx");
                        }
                    }

                    var manifest = new Dictionary<string, object>
                    {
                        { "tool", "ClaudeCarry" }, { "version", 1 }, { "appVersion", typeof(Carry).Assembly.GetName().Version.ToString(3) },
                        { "created", DateTime.Now.ToString("o") }, { "computer", Environment.MachineName }, { "claudeHome", Full(Home) },
                        { "files", stats.Files }, { "bytes", stats.Bytes }, { "partial", chats != null },
                        { "accounts", accounts.Select(a => a.Id).ToArray() }, { "skipped", stats.Failed.ToArray() }
                    };
                    AddText(zip, Json().Serialize(manifest), "manifest.json");
                }
            }
            catch (Exception)
            {
                // Don't leave a half-written zip behind (it would also block retrying with the same name).
                try { File.Delete(outPath); } catch (Exception) { }
                throw;
            }

            long zipSize = new FileInfo(outPath).Length;
            Log(string.Format("Done. {0} files ({1:0.0} GB) -> {2:0} MB zip.", stats.Files, stats.Bytes / 1073741824.0, zipSize / 1048576.0));
            if (stats.Failed.Count > 0)
            {
                Log("Could not read " + stats.Failed.Count + " file(s), left out:");
                foreach (var f in stats.Failed.Take(20)) Log("  " + f);
            }
        }

        // ---------- import ----------

        string MakeBackup(string targetDir, bool accountOnly = false)
        {
            // Back up everything import may touch: memory, CLAUDE.md, skills, settings.json and the
            // target account's chat list. Transcripts are left out: import never changes existing ones.
            // A copy only touches the account's chat list, so accountOnly backs up just that.
            string backupDir = LP(BackupDir);
            Directory.CreateDirectory(backupDir);
            string stamp = (accountOnly ? "before-copy-" : "before-import-") + DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string path = System.IO.Path.Combine(backupDir, stamp + ".zip");
            for (int n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(backupDir, stamp + "-" + n + ".zip");
            var stats = new Stats();
            using (var fs = new FileStream(path, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (!accountOnly)
                {
                    AddTree(zip, System.IO.Path.Combine(HomeL, "projects"), "claude/projects", r => r.Contains("\\memory\\"), stats);
                    AddTree(zip, System.IO.Path.Combine(HomeL, "skills"), "claude/skills", null, stats);
                    foreach (var n in new[] { "CLAUDE.md", "settings.json" })
                    {
                        string p = System.IO.Path.Combine(HomeL, n);
                        if (File.Exists(p)) TryAddFile(zip, p, "claude/" + n, n, stats);
                    }
                }
                if (Directory.Exists(targetDir)) AddTree(zip, targetDir, "app/account", null, stats);
            }
            // The backup must be complete: a file that couldn't be read means we stop before changing anything.
            if (stats.Failed.Count > 0)
            {
                try { File.Delete(path); } catch (Exception) { }
                throw new IOException("Could not back up " + stats.Failed[0] + (stats.Failed.Count > 1 ? " and " + (stats.Failed.Count - 1) + " more" : "") +
                    ". Nothing was changed. Close programs that may be using these files and try again.");
            }
            return path;
        }

        // Reads the manifest so the caller can show where an export came from.
        public static Dictionary<string, object> ReadManifest(string zipPath)
        {
            using (var zip = ZipFile.OpenRead(LP(zipPath)))
            {
                var mf = zip.GetEntry("manifest.json");
                var m = mf == null ? null : TryParse(ReadText(mf));
                if (m == null) throw new InvalidDataException(zipPath + " is not a ClaudeCarry export (no readable manifest.json).");
                return m;
            }
        }

        // Zip entry names are trusted only if they are plain relative paths with forward slashes.
        static bool SafeEntryName(string name)
        {
            return name.Length > 0 && !name.Contains("\\") && !name.Contains(":") && !name.StartsWith("/") &&
                   !Regex.IsMatch(name, @"(^|/)\.\.?(/|$)");
        }

        // onlySessions == null imports everything in the zip; otherwise only those chats (by local_ id)
        // plus the shared files (memory, CLAUDE.md, skills, settings).
        public ImportResult Import(string zipPath, string accountId, bool dryRun, HashSet<string> onlySessions = null)
        {
            string targetDir = System.IO.Path.Combine(SessionsDir, accountId);
            if (!Directory.Exists(targetDir)) throw new DirectoryNotFoundException("No account folder " + Plain(targetDir));
            if (!dryRun && IsLiveApp && ClaudeRunning())
                throw new InvalidOperationException("The Claude app is running. Quit it completely (also from the tray), then import again.");

            var r = new ImportResult();
            zipPath = LP(zipPath);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.GetEntry("manifest.json") == null) throw new InvalidDataException("Not a ClaudeCarry export.");
                if (!dryRun) { r.Backup = Plain(MakeBackup(targetDir)); Log("Backup: " + r.Backup); }

                string homePlain = Full(Home) + "\\";
                var chatEntries = new List<ZipArchiveEntry>();

                // Transcript ids of the chosen chats; other transcripts in the zip are left out.
                HashSet<string> keepCli = null;
                if (onlySessions != null)
                {
                    keepCli = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in zip.Entries)
                    {
                        if (!e.FullName.StartsWith("app/sessions/") || !e.Name.StartsWith("local_") || !e.Name.EndsWith(".json")) continue;
                        if (!onlySessions.Contains(e.Name.Substring(0, e.Name.Length - 5))) continue;
                        var j = TryParse(ReadText(e));
                        if (j != null && j.ContainsKey("cliSessionId")) keepCli.Add(Convert.ToString(j["cliSessionId"]));
                    }
                }

                var zipTranscripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries)
                {
                    string name = e.FullName;
                    if (name.EndsWith("/")) continue;
                    if (!SafeEntryName(name)) { Log("Skipping unsafe path " + name); continue; }
                    if (name.StartsWith("app/sessions/")) { chatEntries.Add(e); continue; }
                    if (!name.StartsWith("claude/")) continue;
                    var tm = Regex.Match(name, @"^claude/projects/[^/]+/([^/]+)\.jsonl$");
                    if (keepCli != null)
                    {
                        var m = Regex.Match(name, @"^claude/projects/[^/]+/([0-9a-fA-F-]{36})(\.jsonl$|/)");
                        if (m.Success && !keepCli.Contains(m.Groups[1].Value)) continue;
                    }
                    if (tm.Success) zipTranscripts.Add(tm.Groups[1].Value);

                    string rel = name.Substring(7).Replace('/', '\\');
                    string destPlain = System.IO.Path.GetFullPath(System.IO.Path.Combine(Full(Home), rel));
                    if (!destPlain.StartsWith(homePlain, StringComparison.OrdinalIgnoreCase)) { Log("Skipping unsafe path " + name); continue; }
                    string dest = LP(destPlain);

                    if (rel == "settings.json" && File.Exists(dest))
                    {
                        // Never touch the live settings; leave the imported copy next to it.
                        if (SameContent(e, dest)) r.Same++;
                        else
                        {
                            if (!dryRun)
                            {
                                string aside = System.IO.Path.Combine(HomeL, "settings.imported.json");
                                for (int n = 2; File.Exists(aside); n++) aside = System.IO.Path.Combine(HomeL, "settings.imported-" + n + ".json");
                                WriteNew(e, aside);
                                Log("settings.json differs: imported copy saved as " + System.IO.Path.GetFileName(aside) + " (yours untouched).");
                            }
                            r.SettingsAside = true;
                        }
                        continue;
                    }

                    if (!File.Exists(dest))
                    {
                        if (!dryRun) WriteNew(e, dest);
                        r.Added++;
                    }
                    else if (SameContent(e, dest)) r.Same++;
                    else if (Regex.IsMatch(rel, @"\\memory\\MEMORY\.md$"))
                    {
                        // Memory index: append the imported lines that are missing, keep everything else.
                        var have = new HashSet<string>(File.ReadAllLines(dest, Utf8));
                        var missing = Regex.Split(ReadText(e), "\r?\n").Where(l => l.Trim().Length > 0 && !have.Contains(l)).Distinct().ToList();
                        if (missing.Count > 0)
                        {
                            if (!dryRun) File.AppendAllText(dest, "\r\n" + string.Join("\r\n", missing) + "\r\n", Utf8);
                            r.Merged.Add(rel);
                        }
                        else r.Same++;
                    }
                    else r.Conflicts.Add(rel);
                }

                // Chat list: copy entries from every exported account into the target account.
                var haveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var haveCli = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(targetDir, "local_*.json"))
                {
                    haveIds.Add(System.IO.Path.GetFileName(f));
                    var j = TryParse(SafeRead(f));
                    if (j != null && j.ContainsKey("cliSessionId")) haveCli.Add(Convert.ToString(j["cliSessionId"]));
                }
                var onDisk = TranscriptIndex();

                var archived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var written = new List<string>();
                foreach (var e in chatEntries)
                {
                    if (e.Name == "archived-sessions.idx") { archived.UnionWith(ReadArchived(ReadText(e))); continue; }
                    if (!e.Name.StartsWith("local_") || !e.Name.EndsWith(".json")) continue;
                    string sid = e.Name.Substring(0, e.Name.Length - 5);
                    if (onlySessions != null && !onlySessions.Contains(sid)) continue;

                    var chat = TryParse(ReadText(e));
                    if (chat == null || !chat.ContainsKey("cliSessionId") || chat["cliSessionId"] == null) { r.ChatsSkipped++; continue; }
                    string cli = Convert.ToString(chat["cliSessionId"]);
                    bool hasTranscript = onDisk.ContainsKey(cli) || (dryRun && zipTranscripts.Contains(cli));
                    bool deletedHere = File.Exists(System.IO.Path.Combine(targetDir, "deleted_" + sid.Substring(6)));

                    if (haveIds.Contains(e.Name) || haveCli.Contains(cli) || deletedHere || !hasTranscript) { r.ChatsSkipped++; continue; }
                    if (!dryRun) WriteNew(e, System.IO.Path.Combine(targetDir, e.Name));
                    haveIds.Add(e.Name);
                    haveCli.Add(cli);
                    written.Add(sid);
                    r.Chats++;
                }

                // Chats archived in the old account stay archived, but only the ones added just now:
                // chats already in this account keep the state they have here.
                if (!dryRun) AddToArchived(targetDir, written.Where(archived.Contains).ToList());
            }

            string verb = dryRun ? "Will add" : "Added";
            Log(string.Format("{0} {1} file(s); {2} already identical.", verb, r.Added, r.Same));
            Log(string.Format("{0} {1} chat(s) to the sidebar; {2} already there or skipped.", verb, r.Chats, r.ChatsSkipped));
            if (r.Merged.Count > 0) Log("Memory index lines appended in: " + string.Join(", ", r.Merged));
            if (r.Conflicts.Count > 0)
            {
                Log(r.Conflicts.Count + " file(s) differ from yours and are left alone:");
                foreach (var c in r.Conflicts.Take(30)) Log("  " + c);
            }
            return r;
        }

        static void AddToArchived(string accountDir, List<string> ids)
        {
            if (ids.Count == 0) return;
            var js = Json();
            string idxPath = System.IO.Path.Combine(accountDir, "archived-sessions.idx");
            Dictionary<string, object> idx = File.Exists(idxPath) ? TryParse(SafeRead(idxPath)) : null;
            if (idx == null) idx = new Dictionary<string, object> { { "v", 1 } };
            var list = idx.ContainsKey("archived") && idx["archived"] is object[] ? ((object[])idx["archived"]).Select(Convert.ToString).ToList() : new List<string>();
            idx["archived"] = list.Concat(ids).Distinct().ToArray();
            File.WriteAllText(idxPath, js.Serialize(idx), Utf8);
        }

        // ---------- copy (no zip) ----------

        // Copies chats straight from their account into another account on this PC. Transcripts and
        // memory are shared by all accounts already, so only the sidebar entries are copied.
        public ImportResult CopyChats(List<ChatInfo> chats, string accountId, bool dryRun)
        {
            string targetDir = System.IO.Path.Combine(SessionsDir, accountId);
            if (!Directory.Exists(targetDir)) throw new DirectoryNotFoundException("No account folder " + Plain(targetDir));
            if (!dryRun && IsLiveApp && ClaudeRunning())
                throw new InvalidOperationException("The Claude app is running. Quit it completely (also from the tray), then try again.");

            var r = new ImportResult();
            var haveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var haveCli = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(targetDir, "local_*.json"))
            {
                haveIds.Add(System.IO.Path.GetFileName(f));
                var j = TryParse(SafeRead(f));
                if (j != null && j.ContainsKey("cliSessionId")) haveCli.Add(Convert.ToString(j["cliSessionId"]));
            }
            var onDisk = TranscriptIndex();

            var todo = new List<ChatInfo>();
            foreach (var c in chats)
            {
                string src = System.IO.Path.Combine(SessionsDir, c.AccountId, c.SessionId + ".json");
                bool deletedHere = File.Exists(System.IO.Path.Combine(targetDir, "deleted_" + c.SessionId.Substring(6)));
                if (!File.Exists(src) || haveIds.Contains(c.SessionId + ".json") || haveCli.Contains(c.CliId) || deletedHere || !onDisk.ContainsKey(c.CliId))
                { r.ChatsSkipped++; continue; }
                haveIds.Add(c.SessionId + ".json");
                haveCli.Add(c.CliId);
                todo.Add(c);
            }
            r.Chats = todo.Count;

            if (!dryRun && todo.Count > 0)
            {
                r.Backup = Plain(MakeBackup(targetDir, true));
                Log("Backup: " + r.Backup);
                foreach (var c in todo)
                    File.Copy(System.IO.Path.Combine(SessionsDir, c.AccountId, c.SessionId + ".json"), System.IO.Path.Combine(targetDir, c.SessionId + ".json"), false);
                AddToArchived(targetDir, todo.Where(c => c.Archived).Select(c => c.SessionId).ToList());
            }
            Log(string.Format("{0} {1} chat(s); {2} already there or skipped.", dryRun ? "Will copy" : "Copied", r.Chats, r.ChatsSkipped));
            return r;
        }
    }
}
