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
        public string App = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        public string BackupDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClaudeCarry-backups");
        public Action<string> Log = delegate { };

        static readonly Regex SecretKey = new Regex("(key|token|secret|password|passwd|auth|credential|cookie|bearer)", RegexOptions.IgnoreCase);
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        // All file access goes through \\?\ paths so folders deeper than 260 characters work
        // (scratch-chat project folders often are). Needs the long-path switches set in Program.Main.
        const string LongPrefix = @"\\?\";
        static string LP(string p) { p = System.IO.Path.GetFullPath(p); return p.StartsWith(LongPrefix) ? p : LongPrefix + p; }
        public static string Plain(string p) { return p.StartsWith(LongPrefix) ? p.Substring(4) : p; }
        string HomeL { get { return LP(Home); } }
        string SessionsDir { get { return System.IO.Path.Combine(LP(App), "claude-code-sessions"); } }

        static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };
        }

        static string Full(string p) { return System.IO.Path.GetFullPath(p).TrimEnd('\\'); }

        public bool IsLiveApp { get { return string.Equals(Full(App), Full(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude")), StringComparison.OrdinalIgnoreCase); } }

        public static bool ClaudeRunning() { return Process.GetProcessesByName("claude").Length > 0; }

        // ---------- accounts ----------

        public List<AccountInfo> GetAccounts()
        {
            var list = new List<AccountInfo>();
            if (!Directory.Exists(SessionsDir)) return list;
            foreach (var a in Directory.GetDirectories(SessionsDir))
                foreach (var o in Directory.GetDirectories(a))
                {
                    var newest = Directory.GetLastWriteTime(o);
                    foreach (var f in Directory.GetFiles(o, "*", SearchOption.AllDirectories))
                    {
                        var t = File.GetLastWriteTime(f);
                        if (t > newest) newest = t;
                    }
                    list.Add(new AccountInfo
                    {
                        Id = System.IO.Path.GetFileName(a) + "\\" + System.IO.Path.GetFileName(o),
                        Path = o,
                        Chats = Directory.GetFiles(o, "local_*.json").Length,
                        LastUsed = newest
                    });
                }
            return list.OrderByDescending(x => x.LastUsed).ToList();
        }

        // ---------- zip helpers ----------

        class Stats { public int Files; public long Bytes; public List<string> Failed = new List<string>(); }

        static void AddFile(ZipArchive zip, string path, string entryName)
        {
            // FileShare.ReadWrite so transcripts of running chats can still be read.
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                entry.LastWriteTime = File.GetLastWriteTime(path);
                using (var dst = entry.Open()) src.CopyTo(dst);
            }
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
                try
                {
                    AddFile(zip, f, prefix + "/" + rel.Replace('\\', '/'));
                    stats.Files++;
                    stats.Bytes += new FileInfo(f).Length;
                }
                catch (Exception) { stats.Failed.Add(rel); }
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
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) b = sha.ComputeHash(s);
                return a.SequenceEqual(b);
            }
        }

        static void WriteNew(ZipArchiveEntry e, string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            // CreateNew: fails instead of overwriting if something appeared in the meantime.
            using (var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var src = e.Open()) src.CopyTo(dst);
            File.SetLastWriteTime(path, e.LastWriteTime.DateTime);
        }

        // ---------- settings without secrets ----------

        static void RemoveSecrets(object node)
        {
            var dict = node as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (var k in dict.Keys.ToList())
                {
                    object v = dict[k];
                    if (SecretKey.IsMatch(k) && !(v is Dictionary<string, object>) && !(v is object[])) dict.Remove(k);
                    else RemoveSecrets(v);
                }
                return;
            }
            var arr = node as object[];
            if (arr != null) foreach (var i in arr) RemoveSecrets(i);
        }

        // ---------- export ----------

        // ---------- chats ----------

        // Folder under .claude\projects that holds the chat's transcript, or null.
        string FindProjectDir(string cliId)
        {
            string projects = System.IO.Path.Combine(HomeL, "projects");
            if (!Directory.Exists(projects)) return null;
            return Directory.GetDirectories(projects).FirstOrDefault(d => File.Exists(System.IO.Path.Combine(d, cliId + ".jsonl")));
        }

        static HashSet<string> ReadArchived(string idxText)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var idx = Json().DeserializeObject(idxText) as Dictionary<string, object>;
                if (idx != null && idx.ContainsKey("archived") && idx["archived"] is object[])
                    foreach (var o in (object[])idx["archived"]) set.Add(Convert.ToString(o));
            }
            catch (Exception) { }
            return set;
        }

        static ChatInfo ParseChat(string accountId, string fileName, string text, HashSet<string> archived)
        {
            var j = Json().DeserializeObject(text) as Dictionary<string, object>;
            if (j == null || !j.ContainsKey("cliSessionId")) return null;
            object t;
            var c = new ChatInfo
            {
                AccountId = accountId,
                SessionId = fileName.Substring(0, fileName.Length - 5),
                CliId = Convert.ToString(j["cliSessionId"]),
                Title = j.TryGetValue("title", out t) && t != null ? Convert.ToString(t) : "(untitled)",
                Cwd = j.TryGetValue("cwd", out t) && t != null ? Convert.ToString(t) : ""
            };
            if ((j.TryGetValue("lastActivityAt", out t) || j.TryGetValue("createdAt", out t)) && t != null)
                try { c.LastActivity = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(t)).LocalDateTime; } catch (Exception) { }
            c.Archived = archived.Contains(c.SessionId) || (j.TryGetValue("isArchived", out t) && t is bool && (bool)t);
            return c;
        }

        public List<ChatInfo> GetChats(AccountInfo acc)
        {
            var list = new List<ChatInfo>();
            string idx = System.IO.Path.Combine(acc.Path, "archived-sessions.idx");
            var archived = File.Exists(idx) ? ReadArchived(File.ReadAllText(idx, Utf8)) : new HashSet<string>();
            foreach (var f in Directory.GetFiles(acc.Path, "local_*.json"))
            {
                ChatInfo c = null;
                try { c = ParseChat(acc.Id, System.IO.Path.GetFileName(f), File.ReadAllText(f, Utf8), archived); } catch (Exception) { }
                if (c == null) continue;
                string dir = FindProjectDir(c.CliId);
                if (dir != null)
                {
                    c.HasTranscript = true;
                    c.Size = new FileInfo(System.IO.Path.Combine(dir, c.CliId + ".jsonl")).Length;
                    string sub = System.IO.Path.Combine(dir, c.CliId);
                    if (Directory.Exists(sub)) c.Size += Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
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
                var seen = new HashSet<string>();
                foreach (var e in zip.Entries)
                {
                    var m = Regex.Match(e.FullName, @"^app/sessions/(.+)/(local_[^/]+\.json)$");
                    if (!m.Success) continue;
                    HashSet<string> arch;
                    if (!archived.TryGetValue(m.Groups[1].Value, out arch)) arch = new HashSet<string>();
                    ChatInfo c = null;
                    try { c = ParseChat(m.Groups[1].Value.Replace('/', '\\'), m.Groups[2].Value, ReadText(e), arch); } catch (Exception) { }
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
            using (var fs = new FileStream(outPath, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (chats == null)
                {
                    // Transcripts, subagent logs, tool results and memory for every project.
                    AddTree(zip, projects, "claude/projects", null, stats);
                }
                else
                {
                    var memoryDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in chats)
                    {
                        string dir = FindProjectDir(c.CliId);
                        if (dir == null) { Log("No transcript on disk for \"" + c.Title + "\", skipped."); continue; }
                        string key = System.IO.Path.GetFileName(dir);
                        string prefix = "claude/projects/" + key;
                        string jsonl = System.IO.Path.Combine(dir, c.CliId + ".jsonl");
                        AddFile(zip, jsonl, prefix + "/" + c.CliId + ".jsonl");
                        stats.Files++; stats.Bytes += new FileInfo(jsonl).Length;
                        AddTree(zip, System.IO.Path.Combine(dir, c.CliId), prefix + "/" + c.CliId, null, stats);
                        if (includeShared && memoryDone.Add(key))
                            AddTree(zip, System.IO.Path.Combine(dir, "memory"), prefix + "/memory", null, stats);
                    }
                }

                if (includeShared)
                {
                    AddTree(zip, System.IO.Path.Combine(HomeL, "skills"), "claude/skills", r => !r.StartsWith(".trash"), stats);
                    string md = System.IO.Path.Combine(HomeL, "CLAUDE.md");
                    if (File.Exists(md)) { AddFile(zip, md, "claude/CLAUDE.md"); stats.Files++; }

                    string settings = System.IO.Path.Combine(HomeL, "settings.json");
                    if (File.Exists(settings))
                    {
                        var js = Json();
                        object obj = js.DeserializeObject(File.ReadAllText(settings, Utf8));
                        RemoveSecrets(obj);
                        AddText(zip, js.Serialize(obj), "claude/settings.json");
                        stats.Files++;
                    }
                }

                // The desktop app's per-account chat list (sidebar). No tokens in these files.
                var wanted = chats == null ? null : new HashSet<string>(chats.Select(c => c.AccountId + "|" + c.SessionId), StringComparer.OrdinalIgnoreCase);
                foreach (var acc in accounts)
                {
                    string name = "app/sessions/" + acc.Id.Replace('\\', '/');
                    var archivedHere = new List<string>();
                    foreach (var f in Directory.GetFiles(acc.Path))
                    {
                        string fn = System.IO.Path.GetFileName(f);
                        if (fn.StartsWith("local_") && fn.EndsWith(".json"))
                        {
                            string sid = fn.Substring(0, fn.Length - 5);
                            if (wanted != null && !wanted.Contains(acc.Id + "|" + sid)) continue;
                            AddFile(zip, f, name + "/" + fn);
                            stats.Files++;
                        }
                        else if (fn == "archived-sessions.idx" && wanted == null)
                        {
                            AddFile(zip, f, name + "/" + fn);
                            stats.Files++;
                        }
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
                    { "tool", "ClaudeCarry" }, { "version", 1 }, { "created", DateTime.Now.ToString("o") },
                    { "computer", Environment.MachineName }, { "claudeHome", Full(Home) },
                    { "files", stats.Files }, { "bytes", stats.Bytes }, { "partial", chats != null },
                    { "accounts", accounts.Select(a => a.Id).ToArray() }, { "skipped", stats.Failed.ToArray() }
                };
                AddText(zip, Json().Serialize(manifest), "manifest.json");
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
                        if (File.Exists(p)) AddFile(zip, p, "claude/" + n);
                    }
                }
                if (Directory.Exists(targetDir)) AddTree(zip, targetDir, "app/account", null, stats);
            }
            return path;
        }

        // Reads the manifest so the caller can show where an export came from.
        public static Dictionary<string, object> ReadManifest(string zipPath)
        {
            using (var zip = ZipFile.OpenRead(LP(zipPath)))
            {
                var mf = zip.GetEntry("manifest.json");
                if (mf == null) throw new InvalidDataException(zipPath + " is not a ClaudeCarry export (no manifest.json).");
                return (Dictionary<string, object>)Json().DeserializeObject(ReadText(mf));
            }
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
            if (!dryRun) { r.Backup = Plain(MakeBackup(targetDir)); Log("Backup: " + r.Backup); }

            zipPath = LP(zipPath);
            string homeFull = HomeL.TrimEnd('\\') + "\\";
            string projects = System.IO.Path.Combine(HomeL, "projects");
            var js = Json();

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.GetEntry("manifest.json") == null) throw new InvalidDataException("Not a ClaudeCarry export.");
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
                        var j = js.DeserializeObject(ReadText(e)) as Dictionary<string, object>;
                        if (j != null && j.ContainsKey("cliSessionId")) keepCli.Add(Convert.ToString(j["cliSessionId"]));
                    }
                }

                foreach (var e in zip.Entries)
                {
                    string name = e.FullName;
                    if (name.EndsWith("/")) continue;
                    if (Regex.IsMatch(name, @"(^|/)\.\.(/|$)") || name.StartsWith("/") || name.StartsWith("\\") || name.Contains(":")) { Log("Skipping unsafe path " + name); continue; }
                    if (name.StartsWith("app/sessions/")) { chatEntries.Add(e); continue; }
                    if (!name.StartsWith("claude/")) continue;
                    if (keepCli != null)
                    {
                        var m = Regex.Match(name, @"^claude/projects/[^/]+/([0-9a-fA-F-]{36})(\.jsonl$|/)");
                        if (m.Success && !keepCli.Contains(m.Groups[1].Value)) continue;
                    }

                    string rel = name.Substring(7).Replace('/', '\\');
                    string dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(HomeL, rel));
                    if (!dest.StartsWith(homeFull, StringComparison.OrdinalIgnoreCase)) continue;

                    if (rel == "settings.json" && File.Exists(dest))
                    {
                        // Never touch the live settings; leave the imported copy next to it.
                        if (SameContent(e, dest)) r.Same++;
                        else
                        {
                            string aside = System.IO.Path.Combine(HomeL, "settings.imported.json");
                            if (!dryRun && !File.Exists(aside)) WriteNew(e, aside);
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
                    try
                    {
                        var j = js.DeserializeObject(File.ReadAllText(f, Utf8)) as Dictionary<string, object>;
                        if (j != null && j.ContainsKey("cliSessionId")) haveCli.Add(Convert.ToString(j["cliSessionId"]));
                    }
                    catch (Exception) { }
                }
                string[] projectDirs = Directory.Exists(projects) ? Directory.GetDirectories(projects) : new string[0];

                var archived = new List<string>();
                foreach (var e in chatEntries)
                {
                    if (e.Name == "archived-sessions.idx")
                    {
                        try
                        {
                            var idx = js.DeserializeObject(ReadText(e)) as Dictionary<string, object>;
                            if (idx != null && idx.ContainsKey("archived")) archived.AddRange(((object[])idx["archived"]).Select(Convert.ToString));
                        }
                        catch (Exception) { }
                        continue;
                    }
                    if (!e.Name.StartsWith("local_") || !e.Name.EndsWith(".json")) continue;
                    if (onlySessions != null && !onlySessions.Contains(e.Name.Substring(0, e.Name.Length - 5))) continue;

                    var chat = js.DeserializeObject(ReadText(e)) as Dictionary<string, object>;
                    if (chat == null || !chat.ContainsKey("cliSessionId")) { r.ChatsSkipped++; continue; }
                    string cli = Convert.ToString(chat["cliSessionId"]);
                    string sid = e.Name.Substring(0, e.Name.Length - 5);
                    bool hasTranscript = projectDirs.Any(d => File.Exists(System.IO.Path.Combine(d, cli + ".jsonl")))
                        || (dryRun && zip.Entries.Any(x => x.FullName.StartsWith("claude/projects/") && x.FullName.EndsWith("/" + cli + ".jsonl")));
                    bool deletedHere = File.Exists(System.IO.Path.Combine(targetDir, "deleted_" + sid.Substring(6)));

                    if (haveIds.Contains(e.Name) || haveCli.Contains(cli) || deletedHere || !hasTranscript) { r.ChatsSkipped++; continue; }
                    if (!dryRun) WriteNew(e, System.IO.Path.Combine(targetDir, e.Name));
                    haveIds.Add(e.Name);
                    haveCli.Add(cli);
                    r.Chats++;
                }

                // Keep chats that were archived in the old account archived in the new one.
                var toArchive = archived.Where(id => haveIds.Contains(id + ".json")).Distinct().ToList();
                if (!dryRun) AddToArchived(targetDir, toArchive);
            }

            string verb = dryRun ? "Will add" : "Added";
            Log(string.Format("{0} {1} file(s); {2} already identical.", verb, r.Added, r.Same));
            Log(string.Format("{0} {1} chat(s) to the sidebar; {2} already there or skipped.", verb, r.Chats, r.ChatsSkipped));
            if (r.Merged.Count > 0) Log("Memory index lines appended in: " + string.Join(", ", r.Merged));
            if (r.SettingsAside) Log("settings.json differs: imported copy saved as settings.imported.json (yours untouched).");
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
            Dictionary<string, object> idx = null;
            if (File.Exists(idxPath)) idx = js.DeserializeObject(File.ReadAllText(idxPath, Utf8)) as Dictionary<string, object>;
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
            var js = Json();
            var haveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var haveCli = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(targetDir, "local_*.json"))
            {
                haveIds.Add(System.IO.Path.GetFileName(f));
                try
                {
                    var j = js.DeserializeObject(File.ReadAllText(f, Utf8)) as Dictionary<string, object>;
                    if (j != null && j.ContainsKey("cliSessionId")) haveCli.Add(Convert.ToString(j["cliSessionId"]));
                }
                catch (Exception) { }
            }

            var todo = new List<ChatInfo>();
            foreach (var c in chats)
            {
                string src = System.IO.Path.Combine(SessionsDir, c.AccountId, c.SessionId + ".json");
                bool deletedHere = File.Exists(System.IO.Path.Combine(targetDir, "deleted_" + c.SessionId.Substring(6)));
                if (!File.Exists(src) || haveIds.Contains(c.SessionId + ".json") || haveCli.Contains(c.CliId) || deletedHere || FindProjectDir(c.CliId) == null)
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
