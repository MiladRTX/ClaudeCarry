// Entry point. With no arguments it opens the window (Host.cs); with arguments it is a small
// command line for scripting and testing:
//   ClaudeCarry.exe export <out.zip> [--chats local_a,local_b] [--no-shared]
//   ClaudeCarry.exe import <file.zip> [--account <accountId\orgId>] [--chats local_a,...] [--dry-run]
//   ClaudeCarry.exe copy --chats local_a,... --account <accountId\orgId> [--dry-run]
//   ClaudeCarry.exe accounts | chats
//   Test overrides: --home <dir> --app <dir> --backup <dir>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyVersion("1.5.2.0")]
[assembly: AssemblyFileVersion("1.5.2.0")]
[assembly: AssemblyProduct("ClaudeCarry")]
[assembly: AssemblyTitle("ClaudeCarry")]

namespace ClaudeCarry
{
    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        // Where the native WebView2Loader.dll is unpacked (the managed WebView2 DLLs load from memory).
        public static string NativeDir;

        [STAThread]
        static int Main(string[] args)
        {
            // Allow paths longer than 260 characters (\\?\ paths); must run before any file access.
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            if (args.Length > 0) return RunCli(args);

            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name + ".dll";
                using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (st == null) return null;
                    var buf = new byte[st.Length];
                    st.Read(buf, 0, buf.Length);
                    return Assembly.Load(buf);
                }
            };
            NativeDir = UnpackLoader();
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            RunWindow();
            return 0;
        }

        // Separate method so the WebView2 types are only resolved after AssemblyResolve is hooked up.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RunWindow() { Application.Run(new HostForm()); }

        static string UnpackLoader()
        {
            string ver = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCarry", "bin", ver);
            string dll = Path.Combine(dir, "WebView2Loader.dll");
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("WebView2Loader.dll"))
            {
                if (!File.Exists(dll) || new FileInfo(dll).Length != st.Length)
                {
                    Directory.CreateDirectory(dir);
                    using (var f = File.Create(dll)) st.CopyTo(f);
                }
            }
            return dir;
        }

        public static string ReadResource(string name)
        {
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var r = new StreamReader(st, Encoding.UTF8)) return r.ReadToEnd();
        }

        static void PrintSearched(Carry carry)
        {
            Console.WriteLine("No accounts found. Looked for chat lists in:");
            foreach (var f in carry.SearchedFolders()) Console.WriteLine("  " + f);
            Console.WriteLine("Log into Claude and open the Code tab once, or point to the folder with --app <Claude data folder>.");
        }

        static int RunCli(string[] args)
        {
            if (!Console.IsOutputRedirected) AttachConsole(-1);
            var carry = new Carry { Log = Console.WriteLine };
            var pos = new List<string>();
            string account = null;
            HashSet<string> only = null;
            bool dry = false, shared = true;
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    Func<string> value = () =>
                    {
                        if (i + 1 >= args.Length) throw new ArgumentException(args[i] + " needs a value.");
                        return args[++i];
                    };
                    switch (args[i])
                    {
                        case "--home": carry.Home = value(); break;
                        case "--app": carry.App = value(); break;
                        case "--backup": carry.BackupDir = value(); break;
                        case "--account": account = value(); break;
                        case "--chats": only = new HashSet<string>(value().Split(','), StringComparer.OrdinalIgnoreCase); break;
                        case "--no-shared": shared = false; break;
                        case "--dry-run": dry = true; break;
                        default: pos.Add(args[i]); break;
                    }
                }
                if (pos.Count == 0) pos.Add("help");
                string cmd = pos[0].ToLowerInvariant();
                if (cmd == "export")
                {
                    List<ChatInfo> chats = null;
                    if (only != null) chats = carry.GetAccounts().SelectMany(a => carry.GetChats(a)).Where(c => only.Contains(c.SessionId)).ToList();
                    string outPath = pos.Count > 1 ? pos[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClaudeCarry-" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".zip");
                    carry.Export(outPath, chats, shared);
                }
                else if (cmd == "copy")
                {
                    if (only == null || account == null) throw new ArgumentException("copy needs --chats and --account");
                    var chats = carry.GetAccounts().SelectMany(a => carry.GetChats(a)).Where(c => only.Contains(c.SessionId)).ToList();
                    carry.CopyChats(chats, account, dry);
                }
                else if (cmd == "accounts")
                {
                    var found = carry.GetAccounts();
                    foreach (var a in found) Console.WriteLine("{0}  chats: {1,3}  last used: {2:yyyy-MM-dd HH:mm}", a.Id, a.Chats, a.LastUsed);
                    if (found.Count == 0) PrintSearched(carry);
                }
                else if (cmd == "chats")
                {
                    var found = carry.GetAccounts();
                    foreach (var a in found)
                    {
                        Console.WriteLine(a.Id);
                        foreach (var c in carry.GetChats(a)) Console.WriteLine("  {0}  {1:yyyy-MM-dd}  {2}{3}", c.SessionId, c.LastActivity, c.Title, c.HasTranscript ? "" : "  (no transcript)");
                    }
                    if (found.Count == 0) PrintSearched(carry);
                }
                else if (cmd == "import")
                {
                    if (pos.Count < 2) throw new ArgumentException("import needs the zip file: ClaudeCarry.exe import <file.zip>");
                    if (account == null)
                    {
                        var newest = carry.GetAccounts().FirstOrDefault();
                        if (newest == null) { PrintSearched(carry); throw new InvalidOperationException("No account folder found. Log in and open the Code tab once first."); }
                        account = newest.Id;
                    }
                    Console.WriteLine("Importing into account " + account + (dry ? " (dry run)" : ""));
                    carry.Import(pos[1], account, dry, only);
                }
                else { Console.WriteLine("Commands: export [out.zip] | import <zip> | accounts | chats  (see README)"); return 2; }
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("Error: " + ex.Message); return 1; }
        }
    }
}
