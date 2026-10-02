using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

[assembly: AssemblyTitle("DSH Guardian")]
[assembly: AssemblyProduct("DSH Guardian")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

internal static class Guardian
{
    private const string ShortcutName = "DSH Guardian";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetConsoleCP(uint codePage);

    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_YESNO = 0x4;
    private const uint MB_ICONWARNING = 0x30;
    private const uint MB_ICONQUESTION = 0x20;
    private const uint MB_YESNOCANCEL = 0x3;
    private const int IDCANCEL = 2;
    private const int IDYES = 6;

    private static string BaseDir
    {
        get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
    }

    private static string WatchdogPath { get { return Path.Combine(BaseDir, "dsh-watchdog.ps1"); } }
    private static string SnapshotPath { get { return Path.Combine(BaseDir, "dsh-snapshot.ps1"); } }
    private static string DataDir { get { return Path.Combine(Path.GetDirectoryName(BaseDir), "data"); } }

    private static bool HasConsole
    {
        get { try { return GetConsoleWindow() != IntPtr.Zero; } catch { return false; } }
    }

    // With /target:winexe there is no console unless one is allocated, and
    // writing to a console that does not exist is a silent no-op. Everything
    // printed goes through here: to the console when there is one, otherwise
    // appended to data\dsh-guardian.out.log so nothing is ever lost.
    private static void Say(string text)
    {
        if (HasConsole)
        {
            try { Console.WriteLine(text); return; } catch { }
        }
        try
        {
            string dir = DataDir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "dsh-guardian.out.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
    }

    // Permanent, tiny breadcrumb trail. Without it there is no way to tell an
    // action that did its job from one that silently did nothing, which is
    // exactly the failure this project already hit once.
    private static void Trace(string text)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "exe-trace.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + text + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
    }

    // Colour, but only where it is both meaningful and safe.
    //
    // Two guards, for two different failures:
    //  - no console (stdout redirected / piped): Console.ForegroundColor throws
    //    or silently does nothing, and ANSI escapes would pollute a pipe, so the
    //    text is written plain.
    //  - the user's console may predate VT support, so nothing here emits ANSI
    //    escapes; Console.ForegroundColor goes through the Win32 console API.
    //
    // The state colours carry the whole point of the screen: green means
    // "watched", yellow means "changing", dim means "nothing is running".
    private static void Say(string text, ConsoleColor color)
    {
        bool painted = false;
        if (HasConsole)
        {
            try
            {
                bool redirected = false;
                try { redirected = Console.IsOutputRedirected; } catch { }
                if (!redirected)
                {
                    Console.ForegroundColor = color;
                    painted = true;
                }
            }
            catch { painted = false; }
        }
        try { Say(text); }
        finally
        {
            if (painted)
            {
                try { Console.ResetColor(); } catch { }
            }
        }
    }

    private static void SayRule(int width, ConsoleColor color)
    {
        Say(new string('-', Math.Max(20, width)), color);
    }

    // Say() ends the line, which is wrong for the left half of a label/value
    // pair: the first version used it for both halves and every row came out as
    // "label" / "value" on two lines.
    private static void SayPart(string text)
    {
        if (HasConsole)
        {
            try { Console.Write(text); return; } catch { }
        }
        try
        {
            string dir = DataDir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "dsh-guardian.out.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text,
                Encoding.UTF8);
        }
        catch { }
    }

    // One "label  value" line. The old version right-padded the label and put a
    // colon before the value, which wrapped as soon as the value was long
    // ("20261002-230811-post-qc-clean" alone is 28 columns) and left the colon
    // stranded on its own line. Width is budgeted instead: label 10 + value 60.
    private static void SayRow(string label, string value, ConsoleColor valueColor)
    {
        SayPart("  ");
        SayPart(label.PadRight(10));
        Say(value, valueColor);
    }

    // Snapshot names are long and the tail ("-known-good") is the informative
    // part, so short strings are kept whole and long ones are trimmed in the
    // middle rather than being left to wrap.
    private static string Fit(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) { return "(无)"; }
        if (text.Length <= max) { return text; }
        int tail = max / 2;
        int head = max - tail - 3;
        return text.Substring(0, head) + "..." + text.Substring(text.Length - tail);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string verb = (args != null && args.Length > 0) ? args[0].TrimStart('-', '/').ToLowerInvariant() : null;

        // Interactive use (a double-click, where no console exists because this
        // is a winexe) gets one allocated so the user can actually see the menu.
        if (!HasConsole)
        {
            try { AllocConsole(); } catch { }
            // The console must be told to use UTF-8 BEFORE anything is written,
            // otherwise the Chinese labels render as mojibake: the .NET side
            // would emit UTF-8 bytes that a GBK console decodes as garbage.
            try { SetConsoleOutputCP(65001); SetConsoleCP(65001); } catch { }
            try
            {
                Stream stdout = Console.OpenStandardOutput();
                Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
                Console.SetError(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
            }
            catch { }
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            // The allocated console comes up about 80 columns wide, while the
            // longest menu line needs ~76 plus a scrollbar margin -- and every
            // Chinese glyph takes two of them. Without this the menu wrapped and
            // scrolled horizontally. Buffer must be widened before the window,
            // or the window set fails; both are wrapped because a console that
            // is already larger, or has no scrollback, must not break startup.
            try
            {
                int w = 92;
                if (Console.BufferWidth < w) { Console.BufferWidth = w; }
                if (Console.WindowWidth < w) { Console.WindowWidth = w; }
                if (Console.BufferHeight < 300) { Console.BufferHeight = 300; }
            }
            catch { }
        }
        try { Console.Title = "DSH Guardian"; } catch { }

        if (!File.Exists(WatchdogPath) || !File.Exists(SnapshotPath))
        {
            Say("FATAL: dsh-watchdog.ps1 / dsh-snapshot.ps1 not found next to this exe.");
            Say("       expected in: " + BaseDir);
            try
            {
                MessageBoxW(IntPtr.Zero,
                    "dsh-watchdog.ps1 / dsh-snapshot.ps1 were not found next to this exe.\n\nExpected in:\n" + BaseDir,
                    "DSH Guardian", MB_OK | MB_ICONERROR);
            }
            catch { }
            return 2;
        }

        // Deliberately NO shortcut handling on startup.
        //
        // This program used to create a desktop shortcut on every launch and,
        // later, to "repair" one it found. Both are unrequested changes to the
        // user's desktop: merely running the exe - including a copy extracted
        // from a release archive just to look at it - could add or redirect a
        // shortcut. A shortcut is now created only when explicitly requested
        // with the 'shortcut' command.

        // args is null, not empty, when the process is started with no
        // command line at all (Explorer double-click, some shims).
        if (verb != null) return RunCli(args);
        try { return RunMenu(); }
        catch (Exception ex)
        {
            // Logged, never a popup: this program must be able to run with no
            // user present and must not put anything on screen unasked.
            Say("ERROR: menu failed: " + ex.Message);
            return 1;
        }
    }

    // Minimal JSON string/number field reader: avoids a JSON dependency and
    // tolerates the formatting PowerShell's ConvertTo-Json produces.
    // Returns null for both a missing key and a JSON null literal.
    private static string JVal(string file, string key)
    {
        if (!File.Exists(file)) return null;
        try
        {
            string text = File.ReadAllText(file);
            int i = text.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            i = text.IndexOf(':', i);
            if (i < 0) return null;
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) return null;
            if (text[i] == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0) return null;
                return text.Substring(i + 1, end - i - 1);
            }
            int stop = i;
            while (stop < text.Length && text[stop] != ',' && text[stop] != '}' && text[stop] != '\n' && text[stop] != '\r') stop++;
            string v = text.Substring(i, stop - i).Trim();
            return (v.Length == 0 || v == "null") ? null : v;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ CLI
    private static int RunCli(string[] args)
    {
        string verb = args[0].TrimStart('-', '/').ToLowerInvariant();

        switch (verb)
        {
            case "logs": return ShowLogs();
            case "baseline": return Exec(SnapshotPath, "-Action Mark-Good");
            // Same code path as menu key 2, so the CLI also lets the user pick
            // the version and cancel. Having two rollback implementations meant
            // the CLI silently lacked the picker and the cancel option.
            case "rollback": return RollbackTo();
            case "preview": return Exec(SnapshotPath, "-Action Restore -DryRun");
            case "arm":
            case "on": return Arm(true);
            case "disarm":
            case "off": return Arm(false);
            case "shortcut": ForceShortcut(); return 0;
            case "help":
            case "?": PrintHelp(); return 0;
            default:
                // Not an error worth a dialog: log it and show usage.
                Say("Unknown action: " + args[0]);
                PrintHelp();
                return 2;
        }
    }

    private static void PrintHelp()
    {
        Say("DSH Guardian");
        Say("  dsh-guardian.exe             open the menu");
        Say("  dsh-guardian.exe logs        show the error log");
        Say("  dsh-guardian.exe baseline    mark the current state as the good baseline");
        Say("  dsh-guardian.exe rollback    pick a version, then roll back / set target");
        Say("  dsh-guardian.exe preview     show the rollback plan, writes nothing");
        Say("  dsh-guardian.exe arm         start watching (runs with this window)");
        Say("  dsh-guardian.exe disarm      stop watching");
        Say("  dsh-guardian.exe on | off    same as arm | disarm");
        Say("  dsh-guardian.exe shortcut    create the desktop shortcut (manual only)");
        Say("  dsh-guardian.exe help | ?    this text");
        Say("");
        Say("Nothing runs in the background: the watcher is started from the menu");
        Say("and stops when this window closes. There is no scheduled task.");
    }

    // Arming starts the watcher; disarming stops it. This is the ONLY way the
    // watcher is ever started: there is no scheduled task and no autostart, so
    // nothing runs unless the user opened this window and asked for it.
    private static int Arm(bool enable)
    {
        if (!enable)
        {
            // Order matters when disarming: flag first so the watcher stops at
            // its next poll, then make sure it is really gone.
            Exec(WatchdogPath, "-Pause", true);
            StopWatcher();
            Console.WriteLine("自动检查：已关闭 —— 监视器已停止，不再占用任何资源");
            return 0;
        }

        // The first round writes the mode file itself. Doing it from this process
        // first and then spawning raced: the child sometimes read the old value
        // and quit as "not armed", leaving the menu showing ON with no watcher.
        int pid = StartWatcher("auto");
        if (pid <= 0)
        {
            Exec(WatchdogPath, "-Pause", true);
            Console.WriteLine("自动检查：启动失败，已保持关闭。");
            Console.WriteLine("  监视器没能启动，请检查这两个文件：");
            Console.WriteLine("    " + WatchdogPath);
            Console.WriteLine("    " + Path.Combine(DataDir, "exe-trace.log"));
            return 4;
        }
        Console.WriteLine("自动检查：已开启 —— 监视器运行中。");
        Console.WriteLine();
        return WatchWhileArmed(pid);
    }

    // The watcher is bound to this process on purpose, so this process must stay
    // alive while it runs. Waiting here is what makes "runs only while I have the
    // window open" true: close the window and the watcher stops by itself.
    private static int WatchWhileArmed(int watcherPid)
    {
        Console.WriteLine("【监视中】本窗口现在必须保持打开。");
        Console.WriteLine("  关掉本窗口 = 停止监视。");
        Console.WriteLine("  装完插件、确认 DSH 正常后，按任意键回来打基线并关闭监视。");
        Console.WriteLine();
        Console.Write("监视运行中");

        int dots = 0;
        bool canReadKeys = true;
        while (true)
        {
            System.Threading.Thread.Sleep(1000);
            dots = (dots + 1) % 4;
            Console.Write("\r监视运行中" + new string('.', dots) + "   ");

            if (LiveRuntimePid() <= 0)
            {
                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine("监视器已停止（可能已自动回退或发生错误）。");
                Console.WriteLine("请按 1 查看错误日志。");
                Console.Write("按回车返回菜单");
                try { Console.ReadLine(); } catch { }
                return 0;
            }
            if (!IsArmed())
            {
                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine("监视已关闭。");
                Console.Write("按回车返回菜单");
                try { Console.ReadLine(); } catch { }
                return 0;
            }

            // Only poll the keyboard while a keyboard is actually readable. A
            // redirect makes KeyAvailable throw on every pass, which would spin.
            if (canReadKeys)
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        Console.ReadKey(true);
                        Console.WriteLine();
                        Console.WriteLine();
                        Console.WriteLine("已返回菜单。监视仍在运行 —— 要停止请按 4。");
                        return 0;
                    }
                }
                catch { canReadKeys = false; }
            }
            else if (dots == 0)
            {
                // No keyboard: refresh the line so the user sees it is alive, and
                // keep waiting for the window to close or the watcher to stop.
                Console.Write("\r监视运行中（无键盘输入，关窗口即可停止）   ");
            }
        }
    }

    // Reads one line of user input, never silently.
    //
    // Console.ReadLine() returns null at end-of-input, and after a child process
    // has shared this console the reader can be left at that state. Treating
    // null as "empty answer" made the picker look like it exited by itself, so
    // null is now reported as a failure instead of being swallowed.
    private static string ReadLineOrNull()
    {
        try
        {
            string s = Console.ReadLine();
            if (s == null)
            {
                Console.WriteLine();
                Console.WriteLine("读取输入失败（输入流已结束）。");
                Console.WriteLine("本窗口仍可使用：请再试一次，或直接用命令行：");
                Console.WriteLine("  dsh-guardian.exe     重新打开菜单");
            }
            return s;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("读取输入出错: " + ex.Message);
            return null;
        }
    }

    // Spawns the resident watcher, bound to this process: it polls our pid and
    // exits when the window closes, so it can never outlive the user's intent.
    // Returns the watcher pid, or 0 if it could not be confirmed. Callers must
    // not claim success on 0: a silently absent watcher is the worst outcome.
    // modeForFirstRound lets the child arm itself, so no state has to be written
    // before it starts.
    private static int StartWatcher(string modeForFirstRound)
    {
        int already = LiveRuntimePid();
        if (already > 0) return already;
        try { File.Delete(Path.Combine(DataDir, "runtime.pid")); } catch { }

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = PsExe();
        psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + WatchdogPath
            + "\" -DataDir \"" + DataDir + "\" -Silent -AutoRollback -Resident"
            + " -Mode " + modeForFirstRound
            + " -ParentPid " + Process.GetCurrentProcess().Id;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        Process.Start(psi);

        for (int i = 0; i < 20; i++)
        {
            System.Threading.Thread.Sleep(300);
            int pid = LiveRuntimePid();
            if (pid > 0) { Trace("arm: watcher pid " + pid); return pid; }
        }
        Trace("arm: watcher did not claim the slot");
        return 0;
    }

    // Asks the watcher to stop and waits briefly for it to clear its pid file.
    private static void StopWatcher()
    {
        int pid = LiveRuntimePid();
        if (pid <= 0) return;
        try
        {
            using (Process p = Process.GetProcessById(pid))
            {
                p.Kill();
                p.WaitForExit(4000);
            }
            Trace("disarm: watcher pid " + pid + " stopped");
        }
        catch { }
        try { File.Delete(Path.Combine(DataDir, "runtime.pid")); } catch { }
    }

    private static bool IsArmed()
    {
        string m = JVal(Path.Combine(DataDir, "mode.json"), "mode");
        return m != "paused";   // absent means armed, matching the watchdog default
    }

    // Timestamp of the most recent round, as written by the watchdog.
    private static string LastTick()
    {
        return JVal(Path.Combine(DataDir, "last-tick.json"), "at");
    }

    // "12 秒前" / "3 分钟前" reads better than an ISO timestamp in a menu.
    private static string RelativeTime(string iso)
    {
        if (string.IsNullOrEmpty(iso)) return "还没有记录";
        try
        {
            DateTime t = DateTime.Parse(iso, null,
                System.Globalization.DateTimeStyles.RoundtripKind);
            double s = (DateTime.Now - t).TotalSeconds;
            if (s < 0) return "刚刚";
            if (s < 60) return string.Format("{0} 秒前", (int)s);
            if (s < 3600) return string.Format("{0} 分钟前", (int)(s / 60));
            return string.Format("{0} 小时前", (int)(s / 3600));
        }
        catch { return iso; }
    }

    // Snapshot names are already readable; keep them whole. Only trim the
    // leading "snap-" so the timestamp lines up.
    private static string ShortName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "（未设置）";
        return name.StartsWith("snap-") ? name.Substring(5) : name;
    }

    // ----------------------------------------------------------------- menu
    // Runs one menu action. An exception here must not end the program: the
    // user should see what went wrong and be able to try again. Before this
    // wrapper existed, a throw inside the rollback picker closed the window
    // with no message at all (the console was already unusable, so the
    // top-level handler had nowhere to report to).
    private static int RunMenuAction(string name, Func<int> action)
    {
        Trace("menu: enter " + name);
        try
        {
            int rc = action();
            Trace("menu: " + name + " rc=" + rc);
            return rc;
        }
        catch (Exception ex)
        {
            string detail = ex.GetType().FullName + ": " + ex.Message
                + Environment.NewLine + ex.StackTrace;
            Trace("menu: " + name + " THREW " + detail);
            // Also keep it in the data directory, which the diagnostic report
            // already collects, so a user can send it without hunting for it.
            try
            {
                File.AppendAllText(Path.Combine(DataDir, "menu-error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  action=" + name
                    + Environment.NewLine + detail + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }

            // Report on screen without assuming the console still works.
            try
            {
                Console.WriteLine();
                Console.WriteLine("出错了（" + name + "）：");
                Console.WriteLine("  " + ex.Message);
                Console.WriteLine();
                Console.WriteLine("详情已记录到 data\\menu-error.log");
                Console.WriteLine("请双击桌面「DSH Guardian 诊断」把报告发给我。");
            }
            catch { }
            return 5;
        }
    }

    private static int MarkGood()
    {
        return Exec(SnapshotPath, "-Action Mark-Good");
    }

    private static int RunMenu()
    {
        // Redraw only when stdin is a real console. Clear() throws when input is
        // piped or the handle is not a console, which would abort the whole menu.
        bool interactive = true;
        try { interactive = !Console.IsInputRedirected; } catch { interactive = false; }

        while (true)
        {
            if (interactive)
            {
                try { Console.Clear(); } catch { interactive = false; }
            }
            // Read state once per redraw so the header reflects reality.
            bool armed = IsArmed();
            int runtimePid = LiveRuntimePid();
            string lastCheck = RelativeTime(LastTick());
            string target = ShortName(JVal(Path.Combine(DataDir, "last-known-good.json"), "snapshot"));

            // The one thing this screen must answer at a glance: is anything
            // watching right now? So the state owns the top line and a colour,
            // and the menu is the quiet part of the screen.
            ConsoleColor stateColor;
            string stateText;
            if (!armed)
            {
                stateColor = ConsoleColor.DarkGray;
                stateText = "○ 未监视 —— 本程序当前不占用任何资源";
            }
            else if (runtimePid > 0)
            {
                stateColor = ConsoleColor.Green;
                stateText = "● 监视中 —— 关掉本窗口即停止";
            }
            else
            {
                stateColor = ConsoleColor.Yellow;
                stateText = "◐ 已开启，监视器启动中…";
            }

            // Width budget: the allocated console is about 80 columns wide, and a
            // Chinese glyph occupies two of them. The previous revision made
            // every item two lines and overflowed, so each line is kept inside
            // 72 columns (36 Chinese glyphs) and rule() stays ASCII: '─' is not
            // guaranteed to exist in an OEM console font.
            SayRule(50, ConsoleColor.DarkGray);
            Say("  DSH Guardian", ConsoleColor.Cyan);
            Say("   · DSH 崩溃自动回退", ConsoleColor.DarkGray);
            SayRule(50, ConsoleColor.DarkGray);
            Say("");
            Say("  " + stateText, stateColor);
            Say("");
            Say("  1  查看错误日志    崩了先看这里，含 DSH 原始报错", ConsoleColor.White);
            Say("  2  回退 / 切换目标  现在回退，或只设成以后的目标", ConsoleColor.White);
            Say("  3  打基线          把当前状态记为一个“好版本”", ConsoleColor.White);
            Say("  4  自动检查        " + (armed ? "开 —— 按 4 关闭" : "关 —— 按 4 打开"),
                armed ? ConsoleColor.Green : ConsoleColor.DarkGray);
            Say("  0  退出", ConsoleColor.White);
            SayRule(50, ConsoleColor.DarkGray);
            SayRow("上次检查", Fit(lastCheck, 40), ConsoleColor.Gray);
            SayRow("回退目标", Fit(target, 40), ConsoleColor.Gray);
            SayRow("监视状态", armed ? "开（关掉本窗口即停止）" : "关（本程序不占用任何资源）",
                armed ? ConsoleColor.Green : ConsoleColor.DarkGray);
            Say("");
            Say("【简易使用流程】", ConsoleColor.DarkGray);
            Say("  装插件之前 : 按 4 打开自动检查（变“开”），并保持本窗口开着");
            Say("  装完插件后 : DSH 能正常启动 → 按 3 打基线");
            Say("  平时不用时 : 按 4 关掉，然后关掉本窗口（关掉就完全不运行）");
            Say("");
            Say("  注意：监视只在“开”且本窗口开着时有效。", ConsoleColor.DarkGray);
            Say("        关掉窗口 = 停止监视。", ConsoleColor.DarkGray);
            Say("");
            Say("  profile : " + Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles", "desktop"), ConsoleColor.DarkGray);
            Say("  data    : " + DataDir, ConsoleColor.DarkGray);
            Say("");
            // Plain Write, not Say(..., color): the reset after a coloured
            // write would strip the colour from whatever the user types next.
            Console.Write("请选择: ");

            string c = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            int rc;
            // Record every menu action. When a user reports "pressing N just
            // exits", this log says whether the handler even ran.
            Trace("menu: choice=\"" + c + "\"");

            switch (c)
            {
                case "1": rc = RunMenuAction("ShowLogs", ShowLogs); break;
                case "2": rc = RunMenuAction("RollbackTo", RollbackTo); break;
                case "3": rc = RunMenuAction("Mark-Good", MarkGood); break;
                case "4": rc = RunMenuAction("Arm", delegate { return Arm(!IsArmed()); }); break;
                case "0":
                    // Exiting stops monitoring. Say so before doing it, rather
                    // than silently leaving the user unprotected.
                    if (IsArmed() && LiveRuntimePid() > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("注意：自动检查已开启，退出会同时停止监视。");
                        int go = MessageBoxW(IntPtr.Zero,
                            "自动检查正在运行。\n\n退出会停止监视，之后 DSH 崩溃将不会自动回退。\n\n确定要退出吗？",
                            "DSH Guardian", MB_YESNO | MB_ICONWARNING);
                        if (go != IDYES)
                        {
                            Console.WriteLine("已取消，监视继续运行。");
                            Console.Write("按回车返回菜单");
                            Console.ReadLine();
                            continue;
                        }
                        Exec(WatchdogPath, "-Pause", true);
                        StopWatcher();
                    }
                    return 0;
                default: continue;
            }

            Console.WriteLine();
            Console.WriteLine(rc == 0 ? "[完成] 按回车返回菜单" : "[退出码 " + rc + "] 按回车返回菜单");
            Console.ReadLine();
        }
    }

    // Reads the pid file the watchdog writes and reports it only if alive.
    private static int LiveRuntimePid()
    {
        try
        {
            string f = Path.Combine(DataDir, "runtime.pid");
            if (!File.Exists(f)) return 0;
            int pid;
            if (!int.TryParse(File.ReadAllText(f).Trim(), out pid)) return 0;
            if (pid <= 0) return 0;
            try { Process.GetProcessById(pid); return pid; }
            catch { return 0; }
        }
        catch { return 0; }
    }

    // ------------------------------------------------------- rollback targets
    // Reads the kept snapshots, newest first. Snapshots are never deleted, so
    // there can be many and the user picks one explicitly.
    private static string[] SnapshotDirs()
    {
        try
        {
            string[] dirs = Directory.GetDirectories(Path.Combine(DataDir, "snapshots"), "snap-*");
            Array.Sort(dirs);
            Array.Reverse(dirs);
            return dirs;
        }
        catch { return new string[0]; }
    }

    private static string ActiveTarget()
    {
        return JVal(Path.Combine(DataDir, "last-known-good.json"), "snapshot") ?? "";
    }

    private static string TrimName(string name)
    {
        return name.StartsWith("snap-") ? name.Substring(5) : name;
    }

    // ISO timestamp -> "yyyy-MM-dd HH:mm:ss" for display.
    //
    // Uses the simpler form on purpose. An earlier version built an intermediate
    // "head" string (the date part plus a space, ~11 chars) and then called
    // Substring(0, 19) on it, which threw ArgumentOutOfRangeException and took
    // the whole menu down whenever the picker listed a snapshot.
    private static string CapturedAt(string dir)
    {
        string c = JVal(Path.Combine(dir, "manifest.json"), "capturedAt");
        if (c == null || c.Length == 0) return "（无记录）";
        int n = Math.Min(19, c.Length);
        string s = c.Substring(0, n);
        if (n > 10 && s[10] == 'T') s = s.Substring(0, 10) + " " + s.Substring(11);
        return s;
    }

    // Shared numbering used by both the picker and the target switcher, so the
    // number a user sees in one place means the same snapshot in the other.
    private static void PrintSnapshotList(string[] dirs, string active)
    {
        for (int i = 0; i < dirs.Length; i++)
        {
            string name = Path.GetFileName(dirs[i]);
            Console.WriteLine(string.Format("  {0}. {1}{2}",
                i + 1, TrimName(name), name == active ? "   <== 当前生效" : ""));
            // Each field is logged before and after so a throw pinpoints which
            // one failed, rather than leaving "it exits" as the only symptom.
            Trace("picker: item " + (i + 1) + " capturedAt...");
            Console.WriteLine("       抓取于 " + CapturedAt(dirs[i]));
            Trace("picker: item " + (i + 1) + " bundleCount...");
            string b = BundleCount(Path.Combine(dirs[i], "manifest.json"));
            if (b != null) Console.WriteLine("       " + b);
            Trace("picker: item " + (i + 1) + " done");
        }
    }

    // Prints the list and returns the chosen directory, or null if cancelled.
    private static string PickSnapshot(string title, string hint, out string[] listed)
    {
        Trace("picker: scanning snapshots");
        listed = SnapshotDirs();
        Trace("picker: found " + listed.Length + " snapshot(s)");
        if (listed.Length == 0)
        {
            Console.WriteLine("还没有任何快照。先回菜单按 3 打一个基线。");
            WaitKey();
            return null;
        }

        string active = ActiveTarget();
        Trace("picker: active target = " + active);
        Console.WriteLine("DSH Guardian · " + title);
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine(hint);
        Console.WriteLine();
        PrintSnapshotList(listed, active);
        Console.WriteLine();
        Console.WriteLine("  0. 取消（什么都不做）");
        Console.WriteLine();
        Console.Write("输入序号（0 或回车 = 取消）: ");
        Trace("picker: waiting for input");

        string pick = (ReadLineOrNull() ?? "").Trim();
        Trace("picker: input = \"" + pick + "\"");
        if (pick == "0")
        {
            Console.WriteLine("已取消，未做任何改动。");
            return null;
        }
        int idx;
        if (!int.TryParse(pick, out idx) || idx < 1 || idx > listed.Length)
        {
            Console.WriteLine("已取消，未做任何改动。");
            return null;
        }
        return listed[idx - 1];
    }

    // ------------------------------------------------- rollback / set target
    // One entry point for both jobs. They used to be two menu items (2 and 5)
    // that asked the user to choose a snapshot first and only then differed, so
    // the snapshot list appeared twice and the choice of WHICH job to do was
    // made before seeing what the versions actually were. Now the list is shown
    // once and the action is chosen afterwards, when the user can see them.
    //
    // Restoring a snapshot that is not the current target also promotes it, so
    // later automatic rollbacks stay consistent with what was chosen here.
    private static int RollbackTo()
    {
        string[] listed;
        string chosen = PickSnapshot("回退 / 切换目标",
            "选一个版本；下一步再决定是现在回退，还是只让它作以后自动回退的目标。", out listed);
        if (chosen == null) return 0;

        string name = Path.GetFileName(chosen);
        bool wasActive = (name == ActiveTarget());

        Console.WriteLine();
        Console.WriteLine("你选的是: " + TrimName(name));
        Console.WriteLine("  抓取于 " + CapturedAt(chosen));
        if (wasActive)
            Console.WriteLine("  （它已经是当前回退目标）");
        else
            Console.WriteLine("  （它不是当前回退目标）");
        Console.WriteLine();
        Console.WriteLine("当前配置会先另存到 snapshots\\pre-restore-<时间>\\，不会丢失。");

        // 是 = roll back now, 否 = only make it the target for later,
        // 取消 = do nothing at all.
        int go = MessageBoxW(IntPtr.Zero,
            "已选择: " + TrimName(name) + "\n抓取于 " + CapturedAt(chosen)
            + "\n\n【是】现在就回退到这个版本"
            + "\n【否】先不回退，只把它设为以后自动回退的目标"
            + "\n【取消】什么都不做",
            "DSH Guardian", MB_YESNOCANCEL | MB_ICONQUESTION);

        if (go == IDCANCEL || go == 0)
        {
            Console.WriteLine("已取消，未做任何改动。");
            WaitKey();
            return 0;
        }

        if (go != IDYES)
        {
            // "No": record the choice without touching the profile.
            if (wasActive)
            {
                Console.WriteLine();
                Console.WriteLine("它已经是当前回退目标，无需改动。");
                WaitKey();
                return 0;
            }
            int pr = Exec(SnapshotPath, "-Action Promote -Snapshot \"" + name + "\"");
            if (pr == 0)
            {
                Console.WriteLine();
                Console.WriteLine("已把它设为回退目标，当前配置未改动。");
                Console.WriteLine("之后崩溃会自动退到这一份；想立刻回退就再按一次 2 选【是】。");
            }
            WaitKey();
            return pr;
        }

        int rc = Exec(SnapshotPath, "-Action Restore -Force -Snapshot \"" + name + "\"");
        if (rc == 0 && !wasActive)
        {
            // Keep the automatic path consistent with this manual choice.
            Exec(SnapshotPath, "-Action Promote -Snapshot \"" + name + "\"", true);
            Console.WriteLine();
            Console.WriteLine("回退目标已同步为这一份，之后崩溃会自动退到这里。");
        }
        WaitKey();
        return rc;
    }

    // "bundles : a, b, c" trimmed for the picker.
    private static string BundleCount(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            string text = File.ReadAllText(manifestPath);
            int i = text.IndexOf("\"bundles\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int open = text.IndexOf('[', i);
            if (open < 0) return null;
            // Search for ']' only AFTER '[', otherwise IndexOf(-1) would find a
            // bracket from the start of the file and yield a negative length.
            int close = text.IndexOf(']', open + 1);
            if (close < 0) return null;
            string body = text.Substring(open + 1, close - open - 1);
            string[] parts = body.Split(',');
            int n = 0;
            foreach (string p in parts) { if (p.Trim().Length > 0) n++; }
            return "含 " + n + " 个插件层";
        }
        catch { return null; }
    }

    private static void WaitKey()
    {
        Console.WriteLine();
        Console.Write("按回车返回菜单");
        try { Console.ReadLine(); } catch { }
    }

    // ------------------------------------------------------------- error log
    // Shows the newest crash evidence file, then the recent failure events.
    private static int ShowLogs()
    {
        string stateFile = Path.Combine(DataDir, "state.json");
        Console.WriteLine("DSH Guardian · 错误日志");
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine("上次失败   : " + (JVal(stateFile, "lastFail") ?? "（没有）"));
        Console.WriteLine("上次回退   : " + (JVal(stateFile, "lastAutoRollbackAt") ?? "（没有）"));
        Console.WriteLine("连续失败数 : " + (JVal(stateFile, "failStreak") ?? "0"));
        Console.WriteLine("profile    : " + Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles", "desktop"));
        Console.WriteLine("data       : " + DataDir);

        string newest = null;
        DateTime newestTime = DateTime.MinValue;
        try
        {
            foreach (string f in Directory.GetFiles(DataDir, "crash-evidence-*.json"))
            {
                DateTime t = File.GetLastWriteTime(f);
                if (t > newestTime) { newestTime = t; newest = f; }
            }
        }
        catch { }

        if (newest == null)
        {
            Console.WriteLine();
            Console.WriteLine("没有崩溃记录 —— 至今一切正常。");
        }
        else
        {
            Console.WriteLine("证据文件   : " + newest);
            Console.WriteLine("--------------------------------------------------");
            string text;
            try { text = File.ReadAllText(newest); }
            catch (Exception ex) { text = "(无法读取: " + ex.Message + ")"; }

            // The captured stderr tail is the useful part; print those, else dump.
            int printed = 0;
            foreach (string raw in text.Split('\n'))
            {
                string l = raw.TrimEnd('\r');
                if (l.TrimStart().StartsWith("\"tail\""))
                {
                    string body = l.Substring(l.IndexOf(':') + 1).Trim().Trim('"');
                    body = body.Replace("\\n", "\n  ").Replace("\\r", "").Replace("\\\"", "\"");
                    Console.WriteLine("--- DSH 崩溃时的原始输出 ---");
                    Console.WriteLine("  " + body);
                    if (++printed >= 4) break;
                }
            }
            if (printed == 0) Console.WriteLine(text.Length > 4000 ? text.Substring(0, 4000) : text);
        }

        string events = Path.Combine(DataDir, "events.jsonl");
        if (File.Exists(events))
        {
            try
            {
                string[] all = File.ReadAllLines(events);
                Console.WriteLine("--------------------------------------------------");
                Console.WriteLine("最近的事件（新→旧）:");
                int shown = 0;
                for (int i = all.Length - 1; i >= 0 && shown < 5; i--)
                {
                    if (all[i].IndexOf("FAIL", StringComparison.Ordinal) < 0 &&
                        all[i].IndexOf("SUSPECT", StringComparison.Ordinal) < 0 &&
                        all[i].IndexOf("RESCUE-FAILED", StringComparison.Ordinal) < 0) continue;
                    string l = all[i];
                    if (l.Length > 160) l = l.Substring(0, 160) + "...";
                    Console.WriteLine("  " + l);
                    shown++;
                }
                if (shown == 0) Console.WriteLine("  （没有）");
            }
            catch { }
        }
        return 0;
    }

    // -------------------------------------------------------------- process
    private static int Exec(string script, string scriptArgs)
    {
        return Exec(script, scriptArgs, false);
    }

    // quietChild: hide the child console entirely (used by the resident watcher).
    //
    // Child stderr is captured to data\child-stderr.log as well as shown. The
    // snapshot script can fail with a PowerShell error that closes the child
    // before anything reaches the console; without a copy on disk there is
    // nothing left to diagnose.
    private static int Exec(string script, string scriptArgs, bool quietChild)
    {
        try
        {
            // Explicit roots are mandatory: under Windows PowerShell 5.1
            // $PSScriptRoot is empty for some -File invocations, so any script
            // default built from it fails parameter binding. Both scripts accept
            // -DataDir, which takes precedence over their own defaults.
            string prefix = " -DataDir \"" + DataDir + "\"";

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = PsExe();
            psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"" + prefix + " " + scriptArgs;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = quietChild;
            if (quietChild) psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardError = true;
            Process p = Process.Start(psi);

            string err = null;
            try { err = p.StandardError.ReadToEnd(); } catch { }
            p.WaitForExit();

            if (!string.IsNullOrEmpty(err))
            {
                Trace("child stderr (" + Path.GetFileName(script) + " " + scriptArgs + "): " + err.Trim());
                try
                {
                    File.AppendAllText(Path.Combine(DataDir, "child-stderr.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " +
                        Path.GetFileName(script) + " " + scriptArgs + Environment.NewLine +
                        err.Trim() + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
            }
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            // A failed shell-out must not put a dialog on screen; log it and
            // report a non-zero exit code.
            Say("failed to start powershell: " + ex.Message);
            Trace("Exec start failed: " + ex.Message);
            return 3;
        }
    }

    private static string PsExe()
    {
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string p = Path.Combine(sys, "WindowsPowerShell\\v1.0\\powershell.exe");
        return File.Exists(p) ? p : "powershell.exe";
    }

    // ------------------------------------------------------------- shortcut
    // Shortcuts are ONLY touched on an explicit request.
    //
    // Nothing here runs at startup. Earlier versions created a desktop shortcut
    // on every launch and then "repaired" whatever they found, which meant that
    // merely running the exe - including a copy extracted from a release archive
    // just to inspect it - could add or redirect a shortcut on the user's
    // desktop without being asked. That is gone: 'dsh-guardian.exe shortcut' is
    // the only way a shortcut is ever created or changed.
    //
    // When it IS asked for: write the user's own Desktop shortcut and clear
    // copies from the shared Public Desktop, so no duplicate is left behind.

    // Every "DSH Guardian.lnk" in either Desktop folder.
    private static List<string> ExistingShortcuts()
    {
        List<string> list = new List<string>();
        foreach (string dir in ShortcutDirs())
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    string p = Path.Combine(dir, ShortcutName + ".lnk");
                    if (File.Exists(p) && !list.Contains(p)) list.Add(p);
                }
            }
            catch { }
        }
        return list;
    }

    private static string[] ShortcutDirs()
    {
        List<string> dirs = new List<string>();
        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)); } catch { }
        try
        {
            string pub = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"..\Public\Desktop");
            dirs.Add(Path.GetFullPath(pub));
        }
        catch { }
        return dirs.ToArray();
    }

    private static bool IsUserDesktop(string p)
    {
        try
        {
            string mine = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return string.Equals(Path.GetDirectoryName(p), mine, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static object OpenShortcut(string lnk)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return null;
        object shell = Activator.CreateInstance(t);
        return t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
    }

    private static void WriteShortcut(string lnk)
    {
        string exe = Assembly.GetExecutingAssembly().Location;
        string icon = Path.Combine(BaseDir, "dsh-guardian.ico");
        if (!File.Exists(icon)) icon = WriteIcon();

        object shortcut = OpenShortcut(lnk);
        if (shortcut == null) throw new InvalidOperationException("WScript.Shell unavailable");
        Type st = shortcut.GetType();
        st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
        st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { BaseDir });
        st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { icon });
        st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "DSH Guardian" });
        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
    }

    // Explicit "recreate it" request from the menu / CLI: always (re)write the
    // user's Desktop shortcut and remove duplicates elsewhere.
    private static void ForceShortcut()
    {
        // Clear copies from the shared Public Desktop first, then write the
        // user's own shortcut. One pass is enough: WriteShortcut does not add
        // anything to the shared folder.
        foreach (string p in ExistingShortcuts())
        {
            if (!IsUserDesktop(p)) { try { File.Delete(p); } catch { } }
        }
        string user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        WriteShortcut(Path.Combine(user, ShortcutName + ".lnk"));
    }

    private static string WriteIcon()
    {
        string path = Path.Combine(BaseDir, "dsh-guardian.ico");
        try
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(28, 92, 168));
                    using (Font f = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (Brush br = new SolidBrush(Color.White))
                    {
                        StringFormat sf = new StringFormat();
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString("G", f, br, new RectangleF(0, 0, 32, 32), sf);
                    }
                }
                using (FileStream fs = new FileStream(path, FileMode.Create))
                using (BinaryWriter w = new BinaryWriter(fs))
                {
                    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);
                    w.Write((byte)32); w.Write((byte)32); w.Write((byte)0); w.Write((byte)0);
                    w.Write((ushort)1); w.Write((ushort)32);
                    w.Write((uint)0);
                    w.Write((uint)(22 + 32 * 32 * 4));
                    w.Write((uint)22);
                    w.Write(40); w.Write(32); w.Write(64); w.Write((ushort)1); w.Write((ushort)32);
                    w.Write(0); w.Write(32 * 32 * 4); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                    for (int y = 31; y >= 0; y--)
                        for (int x = 0; x < 32; x++)
                        {
                            Color c = bmp.GetPixel(x, y);
                            w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write((byte)255);
                        }
                }
            }
            return path;
        }
        catch { return null; }
    }
}
