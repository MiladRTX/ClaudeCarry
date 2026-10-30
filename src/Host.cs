// The app window: a WebView2 control showing ui/index.html (embedded), and a small message bridge
// between that page and Carry. The page sends {id, cmd, args}; we answer {id, ok, result | error}
// and push {event: "log", text} while long jobs run.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeCarry
{
    class HostForm : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        readonly Carry carry = new Carry();
        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };
        readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        bool busy;

        public HostForm()
        {
            Text = "ClaudeCarry";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1180, 760);
            MinimumSize = new Size(860, 560);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            bool dark = SystemUsesDark();
            BackColor = dark ? Color.FromArgb(9, 9, 11) : Color.White;
            web.DefaultBackgroundColor = BackColor;
            Controls.Add(web);
            // Test mode: point the app at copies instead of the live folders.
            string v;
            if ((v = Environment.GetEnvironmentVariable("CLAUDECARRY_HOME")) != null) carry.Home = v;
            if ((v = Environment.GetEnvironmentVariable("CLAUDECARRY_APPDATA")) != null) carry.App = v;
            if ((v = Environment.GetEnvironmentVariable("CLAUDECARRY_BACKUPS")) != null) carry.BackupDir = v;
            carry.Log = line => Post(new Dictionary<string, object> { { "event", "log" }, { "text", line } });
            HandleCreated += delegate { SetDarkTitleBar(dark); };
            Load += async delegate { await Init(); };
        }

        static bool SystemUsesDark()
        {
            try
            {
                var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return v is int && (int)v == 0;
            }
            catch (Exception) { return false; }
        }

        void SetDarkTitleBar(bool dark)
        {
            int on = dark ? 1 : 0;
            try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch (Exception) { }
        }

        async Task Init()
        {
            try
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCarry");
                CoreWebView2Environment.SetLoaderDllFolderPath(Program.NativeDir);
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(data, "WebView2"));
                await web.EnsureCoreWebView2Async(env);
                var s = web.CoreWebView2.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsStatusBarEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.AreDevToolsEnabled = Environment.GetEnvironmentVariable("CLAUDECARRY_DEVTOOLS") == "1";
                web.AllowExternalDrop = false;
                web.CoreWebView2.WebMessageReceived += (o, e) =>
                {
                    string json = e.WebMessageAsJson;
                    BeginInvoke(new Action(() => OnMessage(json)));   // never block inside the WebView2 event
                };
                // Links never open inside the app.
                web.CoreWebView2.NewWindowRequested += (o, e) => { e.Handled = true; };
                web.CoreWebView2.NavigateToString(Program.ReadResource("index.html"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The Microsoft Edge WebView2 Runtime is needed to show this window.\n\n" +
                    "It is built into Windows 11. On Windows 10, install it from:\nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703\n\n(" + ex.Message + ")",
                    "ClaudeCarry", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        void Post(Dictionary<string, object> msg)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => Post(msg))); return; }
            if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(js.Serialize(msg));
        }

        async void OnMessage(string json)
        {
            Dictionary<string, object> msg;
            try { msg = js.DeserializeObject(json) as Dictionary<string, object>; } catch (Exception) { return; }   // never throw out of an async void
            if (msg == null) return;
            object id = msg.ContainsKey("id") ? msg["id"] : null;
            var args = msg.ContainsKey("args") ? msg["args"] as Dictionary<string, object> : null;
            if (args == null) args = new Dictionary<string, object>();
            try
            {
                string cmd = msg.ContainsKey("cmd") ? Convert.ToString(msg["cmd"]) : "";
                object result = await Run(cmd, args);
                Post(new Dictionary<string, object> { { "id", id }, { "ok", true }, { "result", result } });
            }
            catch (CarryError ex)
            {
                Post(new Dictionary<string, object> { { "id", id }, { "ok", false }, { "error", ex.Message }, { "code", ex.Code } });
            }
            catch (Exception ex)
            {
                Post(new Dictionary<string, object> { { "id", id }, { "ok", false }, { "error", ex.Message } });
            }
        }

        class CarryError : Exception
        {
            public string Code;
            public CarryError(string code, string message) : base(message) { Code = code; }
        }

        static string Str(Dictionary<string, object> a, string k) { object v; return a.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null; }

        static List<string> StrList(Dictionary<string, object> a, string k)
        {
            object v;
            if (!a.TryGetValue(k, out v) || !(v is object[])) return null;
            return ((object[])v).Select(Convert.ToString).ToList();
        }

        async Task<object> Run(string cmd, Dictionary<string, object> a)
        {
            switch (cmd)
            {
                case "init":
                    return new Dictionary<string, object>
                    {
                        { "accounts", Accounts() }, { "dark", SystemUsesDark() },
                        { "version", Assembly.GetExecutingAssembly().GetName().Version.ToString(3) },
                        { "backupDir", carry.BackupDir }
                    };

                case "accounts":
                    return Accounts();

                case "searched":
                    return carry.SearchedFolders().ToArray();

                case "chats":
                {
                    var acc = carry.FindAccount(Str(a, "account"));
                    if (acc == null) return new object[0];
                    return await Task.Run(() => carry.GetChats(acc).Select(Chat).ToArray());
                }

                case "theme":
                    SetDarkTitleBar(Str(a, "theme") == "dark");
                    BackColor = Str(a, "theme") == "dark" ? Color.FromArgb(9, 9, 11) : Color.White;
                    return null;

                case "export":
                {
                    if (busy) throw new CarryError("busy", "Another job is still running.");
                    var keys = StrList(a, "chats");     // "accountId|local_x", or null for everything
                    bool shared = !a.ContainsKey("shared") || Convert.ToBoolean(a["shared"]);
                    var dlg = new SaveFileDialog
                    {
                        Filter = "Zip file|*.zip", OverwritePrompt = false,
                        FileName = "ClaudeCarry-" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".zip",
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    };
                    if (dlg.ShowDialog(this) != DialogResult.OK) return new Dictionary<string, object> { { "cancelled", true } };
                    if (File.Exists(dlg.FileName)) throw new CarryError("exists", "That file already exists. Pick a new name.");
                    if (busy) throw new CarryError("busy", "Another job is still running.");
                    string path = dlg.FileName;
                    busy = true;
                    try
                    {
                        int count = 0;
                        await Task.Run(() =>
                        {
                            List<ChatInfo> chats = null;
                            if (keys != null) { chats = ResolveChats(keys); count = chats.Count; }
                            carry.Export(path, chats, shared);
                        });
                        return new Dictionary<string, object> { { "path", path }, { "name", Path.GetFileName(path) }, { "bytes", new FileInfo(path).Length }, { "chats", keys == null ? -1 : count } };
                    }
                    finally { busy = false; }
                }

                case "pickZip":
                {
                    var dlg = new OpenFileDialog { Filter = "ClaudeCarry export|*.zip", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
                    if (dlg.ShowDialog(this) != DialogResult.OK) return new Dictionary<string, object> { { "cancelled", true } };
                    string zip = dlg.FileName;
                    return await Task.Run(() =>
                    {
                        Dictionary<string, object> m;
                        try { m = Carry.ReadManifest(zip); }
                        catch (Exception) { throw new CarryError("not_export", "This zip isn't a ClaudeCarry export."); }
                        var chats = Carry.ListZipChats(zip);
                        object home; m.TryGetValue("claudeHome", out home);
                        bool sameHome = string.Equals(Convert.ToString(home), Carry.Plain(Path.GetFullPath(carry.Home)).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                        object partial; m.TryGetValue("partial", out partial);
                        return (object)new Dictionary<string, object>
                        {
                            { "path", zip }, { "name", Path.GetFileName(zip) }, { "bytes", new FileInfo(zip).Length },
                            { "created", Convert.ToString(m["created"]) }, { "computer", Convert.ToString(m["computer"]) },
                            { "fromHome", Convert.ToString(home) }, { "sameHome", sameHome }, { "partial", partial is bool && (bool)partial },
                            { "chats", chats.Select(Chat).ToArray() }
                        };
                    });
                }

                case "claudeRunning":
                    return carry.IsLiveApp && Carry.ClaudeRunning();

                case "preview":
                case "import":
                {
                    if (busy) throw new CarryError("busy", "Another job is still running.");
                    string zip = Str(a, "zip"), account = Str(a, "account");
                    var ids = StrList(a, "sessions");
                    var only = ids == null ? null : new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
                    bool dry = cmd == "preview";
                    if (!dry && carry.IsLiveApp && Carry.ClaudeRunning())
                        throw new CarryError("claude_running", "The Claude app is still open. Quit it completely (also from the tray icon), then try again.");
                    // Only a real run holds the busy flag, so a preview finishing can't release someone else's job.
                    if (!dry) busy = true;
                    try
                    {
                        var r = await Task.Run(() => carry.Import(zip, account, dry, only));
                        return new Dictionary<string, object>
                        {
                            { "added", r.Added }, { "same", r.Same }, { "chats", r.Chats }, { "chatsSkipped", r.ChatsSkipped },
                            { "conflicts", r.Conflicts.ToArray() }, { "merged", r.Merged.ToArray() },
                            { "settingsAside", r.SettingsAside }, { "backup", r.Backup }
                        };
                    }
                    finally { if (!dry) busy = false; }
                }

                case "copy":
                case "copyPreview":
                {
                    if (busy) throw new CarryError("busy", "Another job is still running.");
                    var keys = StrList(a, "chats") ?? new List<string>();
                    string account = Str(a, "account");
                    bool dry = cmd == "copyPreview";
                    if (!dry && carry.IsLiveApp && Carry.ClaudeRunning())
                        throw new CarryError("claude_running", "The Claude app is still open. Quit it completely (also from the tray icon), then try again.");
                    if (!dry) busy = true;
                    try
                    {
                        var r = await Task.Run(() => carry.CopyChats(ResolveChats(keys), account, dry));
                        return new Dictionary<string, object> { { "chats", r.Chats }, { "chatsSkipped", r.ChatsSkipped }, { "backup", r.Backup } };
                    }
                    finally { if (!dry) busy = false; }
                }

                case "reveal":
                {
                    string p = Str(a, "path");
                    if (p != null && (File.Exists(p) || Directory.Exists(p)))
                        Process.Start("explorer.exe", File.Exists(p) ? "/select,\"" + p + "\"" : "\"" + p + "\"");
                    return null;
                }
            }
            throw new CarryError("unknown", "Unknown command " + cmd);
        }

        // Chat keys from the page ("accountId|local_x") back to ChatInfo.
        List<ChatInfo> ResolveChats(List<string> keys)
        {
            var want = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            var index = carry.TranscriptIndex();
            return carry.EnumerateAccounts().Where(x => keys.Any(k => k.StartsWith(x.Id + "|")))
                .SelectMany(x => carry.GetChats(x, index)).Where(c => want.Contains(c.AccountId + "|" + c.SessionId)).ToList();
        }

        object[] Accounts()
        {
            var list = carry.GetAccounts();
            var perAccount = list.GroupBy(x => x.Id.Split('\\')[0]).ToDictionary(g => g.Key, g => g.Count());
            return list.Select((x, i) =>
            {
                var parts = x.Id.Split('\\');
                string label = parts[0].Substring(0, Math.Min(8, parts[0].Length));
                // Two organizations under one account would otherwise look identical.
                if (perAccount[parts[0]] > 1 && parts.Length > 1) label += "/" + parts[1].Substring(0, Math.Min(4, parts[1].Length));
                return (object)new Dictionary<string, object>
                {
                    { "id", x.Id }, { "short", label },
                    { "chats", x.Chats }, { "lastUsed", x.LastUsed.ToString("s") }, { "current", i == 0 }
                };
            }).ToArray();
        }

        static object Chat(ChatInfo c)
        {
            return new Dictionary<string, object>
            {
                { "key", c.AccountId + "|" + c.SessionId }, { "account", c.AccountId }, { "session", c.SessionId },
                { "title", c.Title }, { "cwd", c.Cwd }, { "folder", string.IsNullOrEmpty(c.Cwd) ? "" : Path.GetFileName(c.Cwd.TrimEnd('\\')) },
                { "last", c.LastActivity == DateTime.MinValue ? null : c.LastActivity.ToString("s") },
                { "bytes", c.Size }, { "archived", c.Archived }, { "hasTranscript", c.HasTranscript }
            };
        }
    }
}
