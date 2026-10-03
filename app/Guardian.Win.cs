// DSH Guardian - WinForms shell.
//
// Why this file exists: the console UI this replaces drew a full screen through
// raw console APIs (absolute-coordinate writes, attribute fills, viewport
// scrolling) in a console that other processes also write to. On this machine
// that produced text on the wrong rows, vanished lines and permanent corruption,
// and no amount of care in the drawing code fixed it. A real window has no such
// coordinate system to get wrong: controls own their own pixels.
//
// All DSH logic stays in the PowerShell scripts (snapshot / watchdog), exactly as
// before, so this is a presentation-layer replacement, not a logic rewrite.
//
// Pure ASCII source, Chinese text as \uXXXX escapes: see docs/TECHNICAL.md --
// Windows PowerShell 5.1 and csc read this file comfortably, and the project
// already relies on that rule everywhere else.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace GuardianGui
{
    // One palette and one set of control styles, used by all three windows.
    //
    // Every colour in the program lives here. Scattered Color.FromArgb calls are how a
    // UI ends up with three nearly-identical greys and two different blues, which reads
    // as sloppy even when nobody can say why.
    internal static class Theme
    {
        // Surfaces
        public static readonly Color Canvas = Color.FromArgb(243, 245, 249);   // window
        public static readonly Color Card = Color.White;                       // zones
        public static readonly Color Border = Color.FromArgb(213, 219, 228);
        public static readonly Color ConsoleBg = Color.FromArgb(24, 28, 36);
        public static readonly Color ConsoleFg = Color.FromArgb(226, 232, 240);

        // Text
        public static readonly Color Text = Color.FromArgb(28, 34, 44);
        public static readonly Color Muted = Color.FromArgb(110, 120, 134);

        // Meaning
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentDown = Color.FromArgb(29, 78, 216);
        public static readonly Color Ok = Color.FromArgb(21, 128, 71);
        public static readonly Color Warn = Color.FromArgb(176, 112, 0);
        public static readonly Color Danger = Color.FromArgb(193, 61, 61);

        public static Font Ui(float size, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", size, style);
        }

        // Rounded corners, via a Region. Fixed-size controls only, because a Region does
        // not follow a resize on its own.
        public static void Round(Control c, int radius)
        {
            try
            {
                System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
                int d = radius * 2;
                p.AddArc(0, 0, d, d, 180, 90);
                p.AddArc(c.Width - d, 0, d, d, 270, 90);
                p.AddArc(c.Width - d, c.Height - d, d, d, 0, 90);
                p.AddArc(0, c.Height - d, d, d, 90, 90);
                p.CloseFigure();
                c.Region = new Region(p);
            }
            catch { }
        }

        // A flat button that reacts to the mouse. Primary = the one action worth
        // emphasising in that row; it gets the accent colour so the eye lands on it.
        public static void Style(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Border;
            b.BackColor = primary ? Accent : Card;
            b.ForeColor = primary ? Color.White : Text;
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            Color rest = b.BackColor;
            b.MouseEnter += delegate { if (b.Enabled) { b.BackColor = primary ? AccentDown : Color.FromArgb(238, 242, 248); } };
            b.MouseLeave += delegate { b.BackColor = rest; };
            b.EnabledChanged += delegate { b.BackColor = b.Enabled ? rest : Color.FromArgb(232, 235, 240); };
        }

        // A zone: white card, thin border, accent caption.
        public static void Style(GroupBox g)
        {
            g.BackColor = Card;
            g.ForeColor = Accent;
            g.Font = Ui(9.75F, FontStyle.Bold);
        }

        // A list view without the dated chrome: no 3D border, no grid lines, white rows.
        public static void Style(ListView lv)
        {
            lv.BorderStyle = BorderStyle.None;
            lv.GridLines = false;
            lv.BackColor = Card;
            lv.ForeColor = Text;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        }

        public static void Style(Label l, Color color, float size, FontStyle style)
        {
            l.ForeColor = color;
            l.Font = Ui(size, style);
            l.BackColor = Color.Transparent;
        }

        // Height of one line of text in a given font, in real pixels.
        //
        // Any box that holds text must be sized from this rather than from a constant.
        // On this machine (144 DPI) one line of 9.75pt text is 31px tall, so the 24px
        // the path label used to be given clipped it -- and a hardcoded 24 is wrong at
        // every DPI except the one it was guessed at.
        public static int LineHeight(Font f)
        {
            try { return TextRenderer.MeasureText("Ag\u4E2D", f).Height; }
            catch { return 20; }
        }
    }

    internal static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            // Declare DPI awareness before any window exists.
            //
            // Without this the process is DPI-unaware and Windows bitmap-stretches the
            // whole window. That is not cosmetic: the stretch applied to this machine's
            // scaling meant the client area and the window's own pixels disagreed, so
            // the right-hand buttons were drawn outside the visible frame and a
            // screen-grab of the window bounds cut them off. It is also the reason
            // layout maths kept giving answers that did not match what was on screen.
            try { SetProcessDPIAware(); } catch { }

            // Self test FIRST, before any WinForms initialisation and before the
            // form is even considered. Two earlier attempts put this after
            // EnableVisualStyles or inside the form and never reached it.
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "dsh-gui-args.txt"),
                    (args == null ? "(null)" : string.Join("|", args)) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }

            // Any unhandled exception writes itself to disk. The browser exited
            // silently on startup twice and there was nothing to read; guessing at
            // a WinForms layout exception is not a debugging strategy.
            Application.ThreadException += delegate(object s2, System.Threading.ThreadExceptionEventArgs e2)
            {
                CrashLog("ThreadException", e2.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s2, UnhandledExceptionEventArgs e2)
            {
                CrashLog("UnhandledException", e2.ExceptionObject as Exception);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            if (MainForm.HandleCheck(args)) { return; }

            // Command-line verbs are forwarded to the console build.
            //
            // The README tells users to run `dsh-guardian.exe shortcut`, and the
            // diagnostics shortcut runs `dsh-guardian.exe diagnostics`. Neither verb was
            // ever handled here: this Main only knew the four test modes below, so both
            // commands silently opened the GUI window instead of doing the work.
            //
            // Forwarding rather than reimplementing: the console build already has every
            // verb, and a second copy of that logic in this file would be the same
            // duplication this project spent a pass removing. One implementation, one
            // place to fix.
            if (args != null && args.Length > 0 && Cli.IsForwardedVerb(args[0]))
            {
                int forwarded = Cli.ForwardToConsole(args);
                if (forwarded >= 0) { Environment.Exit(forwarded); }
            }

            // "setting <0|1>" -- self test for the close-behaviour setting.
            //
            // Synthetic mouse clicks do not reach WinForms controls on this machine, so
            // the one path that matters here (flip the checkbox while the guard is
            // armed, which must rebuild the watcher immediately) cannot be exercised by
            // clicking. This arms the guard and then flips the box, which is the same
            // code the checkbox handler runs.
            if (args != null && args.Length > 1
                && args[0].TrimStart('-', '/').Equals("setting", StringComparison.OrdinalIgnoreCase))
            {
                bool want = args[1] == "1";
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                MainForm mf2 = new MainForm();
                Timer t3 = new Timer();
                t3.Interval = MainForm.StartupTestDelayMs;
                t3.Tick += delegate
                {
                    t3.Stop();
                    mf2.RunSettingTest(want);
                };
                t3.Start();
                Application.Run(mf2);
                return;
            }

            // "click <n>" runs action <n> shortly after startup: same code path a
            // button click takes, with no mouse involved.
            if (args != null && args.Length > 1
                && args[0].TrimStart('-', '/').Equals("click", StringComparison.OrdinalIgnoreCase))
            {
                int idx;
                if (int.TryParse(args[1], out idx))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    MainForm mf = new MainForm();
                    Timer t2 = new Timer();
                    t2.Interval = MainForm.StartupTestDelayMs;
                    t2.Tick += delegate
                    {
                        t2.Stop();
                        mf.RunActionForTest(idx);
                    };
                    t2.Start();
                    Application.Run(mf);
                    return;
                }
            }

            // "picker" opens the version dialog directly, for screenshots: the
            // dialog normally needs a real click on the main window to appear.
            if (args != null && args.Length > 0
                && args[0].TrimStart('-', '/').Equals("picker", StringComparison.OrdinalIgnoreCase))
            {
                string bd2 = Path.GetDirectoryName(Application.ExecutablePath);
                string dd2 = Path.Combine(Path.GetDirectoryName(bd2), "data");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new RollbackDialog(
                    Path.Combine(dd2, "snapshots"),
                    delegate { return MainForm.BaselineNameFor(dd2); },
                    null));
                return;
            }

            // Opens the data browser directly. Exists so the browser can be
            // launched for a screenshot without driving menus: synthetic mouse
            // input does not reliably reach WinForms controls on this machine.
            if (args != null && args.Length > 0
                && args[0].TrimStart('-', '/').Equals("browser", StringComparison.OrdinalIgnoreCase))
            {
                string bd = Path.GetDirectoryName(Application.ExecutablePath);
                string dd = Path.Combine(Path.GetDirectoryName(bd), "data");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new FolderBrowser(dd));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        internal static void CrashLog(string where, Exception ex)
        {
            try
            {
                string txt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + where + Environment.NewLine
                    + (ex == null ? "(no exception object)" : ex.ToString())
                    + Environment.NewLine + Environment.NewLine;
                string dir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Application.ExecutablePath)), "data");
                try { Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, "gui-crash.log"), txt, new UTF8Encoding(false)); } catch { }
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "dsh-gui-crash.log"), txt, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- CLI forwarding
    internal static class Cli
    {
        // Verbs the console build implements. Kept as a list rather than "anything not
        // recognised" so a typo still reaches the GUI's own argument handling instead of
        // being silently handed to another process.
        private static readonly string[] Verbs = new string[]
        {
            "logs", "baseline", "rollback", "preview", "arm", "on", "disarm", "off",
            "shortcut", "diagnostics", "diag", "probe", "help", "?"
        };

        internal static bool IsForwardedVerb(string a)
        {
            if (string.IsNullOrEmpty(a)) { return false; }
            string v = a.TrimStart('-', '/').ToLowerInvariant();
            foreach (string s in Verbs) { if (s == v) { return true; } }
            return false;
        }

        // Runs the console build with the same arguments and returns its exit code, or
        // -1 when the console build is not next to this one (in which case the caller
        // falls through to the normal GUI start rather than failing silently).
        internal static int ForwardToConsole(string[] args)
        {
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                string exe = Path.Combine(dir, "dsh-guardian-console.exe");
                if (!File.Exists(exe)) { return -1; }

                StringBuilder sb = new StringBuilder();
                foreach (string a in args)
                {
                    if (sb.Length > 0) { sb.Append(' '); }
                    sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = sb.ToString();
                psi.WorkingDirectory = dir;
                psi.UseShellExecute = false;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }
    }

    internal sealed class MainForm : Form
    {
        // Named constants instead of numbers inline in the layout/logic.
        //   StatusRefreshMs     how often the status panel re-reads the state files
        //   StartupTestDelayMs  delay before the "click <n>" test mode fires, long
        //                       enough for the window to be up and painted
        //   StatusRowPx / DetailRowPx / ActionRowPx
        //                       the three fixed row heights. DetailRowPx has to fit
        //                       TWO lines (the second one wraps), which a 44px row did
        //                       not -- the second line was drawn into the button row.
        internal const int StatusRefreshMs = 2000;
        // Undetermined states are polled fast, settled ones slowly.
        //
        // The window's answer to "is anything watching" has to arrive before the user
        // acts on it. A fixed 2s poll let the display lag the truth by up to two seconds
        // on every transition, and while the state is still unknown that lag IS the
        // problem; once it is settled there is nothing to poll for. The watcher claims
        // its pid file in one to two seconds, so a 400ms poll resolves the question
        // almost as soon as there is an answer to give.
        internal const int StatusFastRefreshMs = 400;
        internal const int StartupTestDelayMs = 1200;
        // How long "armed but no watcher yet" may last before the window calls it a
        // failure. Measured: the watcher claims its pid file in about a second, two on a
        // cold PowerShell start. Five seconds is several times that, and still short
        // enough that nobody stands in front of the window wondering.
        internal const int WatcherStartGraceSeconds = 5;
        // Waiting for the watcher to claim data\runtime.pid after it is launched.
        // 20 x 300ms = 6s, deliberately a little longer than the 5s status grace so the
        // arming action finishes before the status line declares a failure.
        internal const int WatcherPidWaitTries = 20;
        internal const int WatcherPidWaitMs = 300;
        // How often the C# watch-loop runs one round of the watchdog. Matches the
        // PowerShell default it replaced, so the documented 55s cadence is unchanged.
        internal const int WatchIntervalSeconds = 55;
        internal const int StatusRowPx = 48;
        internal const int DetailRowPx = 72;
        internal const int ActionRowPx = 52;
        // Zone heights: each GroupBox adds its caption line plus padding on top of the
        // rows it holds.
        internal const int StatusZonePx = StatusRowPx + DetailRowPx + 40;
        internal const int ActionZonePx = ActionRowPx + 30;
        internal const int ButtonWidthPx = 128;
        internal const int ButtonHeightPx = 34;

        // ---- paths (same layout the console version used) --------------------
        private readonly string BaseDir;
        private readonly string DataDir;
        private readonly string WatchdogPath;
        private readonly string SnapshotPath;

        private readonly string PsExe;

        // dsh-guardian-console.exe -- hosts the resident watch loop (see watch-loop).
        private readonly string LoopExe;

        // ---- controls --------------------------------------------------------
        private Label detailLine;
        private CheckBox keepBox;
        // When "armed but no live watcher pid" was first seen, so the window can tell
        // "still starting" apart from "never started". Reset whenever the state resolves.
        private DateTime armedNoPidSince = DateTime.MinValue;
        private string lastStatusLogged = null;
        private Label statusLine;
        private FlowLayoutPanel actions;
        private GroupBox statusZone;
        private Button zoomButton;
        private bool resultZoomed;
        private TableLayoutPanel layout;
        private TextBox output;
        private Timer ticker;

        private bool busy;

        public MainForm()
        {
            BaseDir = Path.GetDirectoryName(Application.ExecutablePath);
            DataDir = Path.Combine(Path.GetDirectoryName(BaseDir), "data");
            WatchdogPath = Path.Combine(BaseDir, "dsh-watchdog.ps1");
            SnapshotPath = Path.Combine(BaseDir, "dsh-snapshot.ps1");
            LoopExe = Path.Combine(BaseDir, "dsh-guardian-console.exe");
            PsExe = FindPowerShell();

            BuildUi();
            Refresh4();
            ticker = new Timer();
            // Start fast: the first status is the one the user is waiting for.
            ticker.Interval = StatusFastRefreshMs;
            ticker.Tick += delegate { Refresh4(); };
            ticker.Start();
        }

        private static string FindPowerShell()
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string p = Path.Combine(windir, @"System32\WindowsPowerShell\v1.0\powershell.exe");
            if (File.Exists(p)) { return p; }
            return "powershell.exe";
        }

        private void BuildUi()
        {
            // "Microsoft YaHei UI" is present on every Simplified Chinese Windows;
            // the fallback keeps this readable if it is not.
            Font baseFont = PickFont();

            Text = T("DSH Guardian") + "  " + T("DSH \u5D29\u6E83\u81EA\u52A8\u56DE\u9000");
            Font = baseFont;
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Theme.Canvas;
            // Preferred size; FitToScreen trims it to the desktop so a scaled display
            // cannot push part of the window off the screen.
            FitToScreen(this, 1120, 470);
            StartPosition = FormStartPosition.CenterScreen;

            // --- status ---------------------------------------------------------
            statusLine = new Label();
            statusLine.AutoSize = false;
            statusLine.Height = StatusRowPx;
            Theme.Style(statusLine, Theme.Muted, 15F, FontStyle.Bold);
            statusLine.TextAlign = ContentAlignment.MiddleLeft;
            statusLine.Padding = new Padding(0, 0, 0, 0);   // aligned by the zone padding
            statusLine.Margin = new Padding(0, 0, 0, 0);     // Label defaults to a 3px margin

            detailLine = new Label();
            detailLine.AutoSize = false;
            // Three lines of text live here (two facts plus a hint that wraps), so this
            // row has to be tall enough for all of them. At 44px the hint was cut in
            // half and read as garbage; at 54px it was still clipped by the zone edge.
            detailLine.Height = DetailRowPx;
            detailLine.Padding = new Padding(0, 2, 0, 0);
            detailLine.Margin = new Padding(0, 0, 0, 0);
            Theme.Style(detailLine, Theme.Muted, 9.75F, FontStyle.Regular);

            // --- actions: buttons only ------------------------------------------
            // This window used to show the same seven actions twice, as a table AND
            // as a quick button bar, both wired to the same handlers. Two controls
            // for one action is a bug in the interface, not a convenience: it makes
            // people wonder which one is authoritative. Buttons win because they
            // need one click instead of select-then-run, so the table is gone.
            // Buttons sized to their own text and laid out by a FlowLayoutPanel.
            // Fixed widths cut the labels off ("查看错误日"): a Chinese glyph is about
            // 14px at this font size, not the 12px the first estimate assumed.
            actions = new FlowLayoutPanel();   // the FIELD, not a local: a local here shadowed it
                                                // and left the field null, so every action died on
                                                // "actions.Enabled = false" inside EnterBusy.
            actions.Padding = new Padding(0, RowGap, 0, RowGap);   // the zone already pads the left edge
            actions.WrapContents = true;
            // No horizontal scrollbar: buttons must wrap (and the row height grow), not
            // slide out of reach. The layout gives this row the height it asks for.
            actions.AutoScroll = false;
            actions.MinimumSize = new Size(0, 46);
            actions.SizeChanged += delegate { LayoutActionsRow(layout, actions); };
            actions.ControlAdded += delegate { LayoutActionsRow(layout, actions); };

            // The one setting, next to the buttons that it governs.
            //
            // It sits here rather than in a dialog because the decision it controls --
            // what happens when this window closes -- is made while looking at the
            // window, and a setting nobody finds is the same as no setting.
            keepBox = new CheckBox();
            keepBox.AutoSize = false;
            keepBox.Height = ButtonHeightPx;
            keepBox.Width = ButtonWidthPx + 76;
            keepBox.Margin = new Padding(ButtonGap * 2, 0, 0, ButtonGap);
            keepBox.TextAlign = ContentAlignment.MiddleLeft;
            keepBox.Padding = new Padding(0, 0, 0, 0);
            keepBox.Font = new Font("Microsoft YaHei UI", 9.75F);
            keepBox.ForeColor = Theme.Text;
            keepBox.BackColor = Theme.Card;
            keepBox.Text = T("\u5173\u7A97\u540E\u7EE7\u7EED\u76D1\u89C6");
            keepBox.Checked = KeepWatchingAfterClose;
            new ToolTip().SetToolTip(keepBox,
                T("\u52FE\u4E0A\uFF1A\u5173\u6389\u7A97\u53E3\u540E\u76D1\u89C6\u5668\u7EE7\u7EED\u8DD1\uFF0C\u9700\u8981\u624B\u52A8\u505C\u6B62\u3002")
                + Environment.NewLine
                + T("\u4E0D\u52FE\uFF08\u9ED8\u8BA4\uFF09\uFF1A\u5173\u7A97\u53E3\u5373\u505C\u6B62\u76D1\u89C6\uFF0C\u4E0D\u4F1A\u6709\u540E\u53F0\u8FDB\u7A0B\u3002"));
            keepBox.CheckedChanged += delegate
            {
                KeepWatchingAfterClose = keepBox.Checked;
                // Apply it NOW, not at the next arm.
                //
                // The watcher's binding is fixed when it starts, so a setting that only
                // takes effect later means the window can describe one behaviour while
                // the running process does another -- which is exactly how "关窗即停"
                // came to be reported as broken. Rebuilding the watcher on the spot
                // costs a couple of seconds and removes the whole class of mismatch.
                if (Armed)
                {
                    Append(T("\u6B63\u5728\u6309\u65B0\u8BBE\u7F6E\u91CD\u5EFA\u76D1\u89C6\u5668\u2026"));
                    Application.DoEvents();
                    Disarm();
                    Arm();
                }
                UpdateStatus();
                Append(keepBox.Checked
                    ? T("\u5DF2\u751F\u6548\uFF1A\u5173\u7A97\u540E\u7EE7\u7EED\u76D1\u89C6\u3002\u60F3\u505C\u5C31\u91CD\u5F00\u7A97\u53E3\u70B9\u300C\u5F00\u5173\u76D1\u89C6\u300D\u3002")
                    : T("\u5DF2\u751F\u6548\uFF1A\u5173\u7A97\u53E3\u5373\u505C\u6B62\u76D1\u89C6\u3002"));
            };
            actions.Controls.Add(keepBox);

            // Short labels, and a tooltip carrying the full wording.
            //
            // Four layout attempts failed to make long labels fit a narrow window
            // (fixed width clipped them, measured width overflowed, AutoSize and
            // min/max sizing both left the right-hand buttons unreachable). The label
            // itself was the problem: two characters per action fit at any width the
            // window allows, so there is no wrapping left to get wrong.
            Button primaryBtn = AddQuick(actions, T("\u6253\u57FA\u7EBF"), T("\u628A\u5F53\u524D\u72B6\u6001\u8BB0\u4E3A\u4E00\u4E2A\u201C\u597D\u7248\u672C\u201D"), delegate { RunAction(0); });
            Theme.Style(primaryBtn, true);
            AddQuick(actions, T("\u5F00\u5173\u76D1\u89C6"), T("\u6253\u5F00 / \u5173\u95ED\u81EA\u52A8\u68C0\u67E5"), delegate { RunAction(1); });
            AddQuick(actions, T("\u56DE\u9000"), T("\u9009\u4E00\u4E2A\u7248\u672C\uFF1A\u73B0\u5728\u56DE\u9000\u3001\u53EA\u8BBE\u4E3A\u4EE5\u540E\u7684\u76EE\u6807\uFF0C\u6216\u5220\u9664"), delegate { RunAction(2); });
            AddQuick(actions, T("\u65E5\u5FD7"), T("\u67E5\u770B\u9519\u8BEF\u65E5\u5FD7\uFF0C\u542B DSH \u539F\u59CB\u62A5\u9519"), delegate { RunAction(3); });
            AddQuick(actions, T("\u76EE\u5F55"), T("\u6253\u5F00\u6570\u636E\u76EE\u5F55\uFF1A\u5FEB\u7167\u3001\u65E5\u5FD7\u3001\u5D29\u6E83\u8BC1\u636E"), delegate { RunAction(4); });
            AddQuick(actions, T("\u5237\u65B0"), T("\u91CD\u65B0\u8BFB\u53D6\u76D1\u89C6\u72B6\u6001"), delegate { RunAction(5); });
            // Long output (a baseline lists every file it copied) does not fit the result
            // zone at its normal height. This hands that zone the status zone's space.
            // The button lives in the action zone, which is deliberately NOT collapsed:
            // hiding the zone that holds the way back would be a trap.
            zoomButton = AddQuick(actions, T("\u653E\u5927\u7ED3\u679C"),
                T("\u628A\u72B6\u6001\u533A\u8BA9\u7ED9\u7ED3\u679C\u533A\uFF0C\u65B9\u4FBF\u8BFB\u957F\u8F93\u51FA"),
                delegate { ToggleResultZoom(); });
            Button quitBtn = AddQuick(actions, T("\u9000\u51FA"), T("\u5173\u95ED\u7A97\u53E3\uFF08\u4F1A\u5148\u95EE\u76D1\u89C6\u600E\u4E48\u529E\uFF09"), delegate { ExitApp(); });
            if (quitBtn != null) { quitBtn.ForeColor = Theme.Danger; }

            // --- output ---------------------------------------------------------
            output = new TextBox();
            output.Dock = DockStyle.Fill;
            output.Multiline = true;
            output.ReadOnly = true;
            // Both, not Vertical. Snapshot paths are long, and with only a vertical
            // scrollbar the tail of every long line was clipped with no way to reach it.
            output.ScrollBars = ScrollBars.Both;
            output.BackColor = Theme.ConsoleBg;
            output.ForeColor = Theme.ConsoleFg;
            output.BorderStyle = BorderStyle.None;
            output.Font = new Font("Consolas", 9.5F);
            output.WordWrap = false;

            // Layout: three labelled zones instead of four anonymous rows.
            //
            // The window used to be status / details / buttons / output stacked with no
            // boundaries at all, so "where do I look" and "where did my click go" had to
            // be worked out from the content. Now each zone is a GroupBox with a
            // caption, which says what it is before anything is read:
            //
            //   当前状态   what the watchdog is doing right now
            //   操作       the buttons; one click each
            //   执行结果   what the last action actually printed
            //
            // Inside a zone the rows are a nested TableLayoutPanel, so no zone depends
            // on Dock order: the earlier alternative overlapped when the Add order
            // changed and was invisible until something drew in the wrong place.
            layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, StatusZonePx));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ActionZonePx));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // --- zone 1: current state -----------------------------------------
            statusZone = new GroupBox();
            Theme.Style(statusZone);
            statusZone.Text = T("\u5F53\u524D\u72B6\u6001");
            statusZone.Dock = DockStyle.Fill;
            statusZone.Padding = new Padding(ZonePad, 6, ZonePad, 6);

            TableLayoutPanel statusRows = new TableLayoutPanel();
            statusRows.Dock = DockStyle.Fill;
            statusRows.ColumnCount = 1;
            statusRows.RowCount = 2;
            statusRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            statusRows.RowStyles.Add(new RowStyle(SizeType.Absolute, StatusRowPx));
            statusRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            statusLine.Dock = DockStyle.Fill;
            detailLine.Dock = DockStyle.Fill;
            statusRows.Controls.Add(statusLine, 0, 0);
            statusRows.Controls.Add(detailLine, 0, 1);
            statusZone.Controls.Add(statusRows);

            // --- zone 2: actions -------------------------------------------------
            GroupBox actionZone = new GroupBox();
            Theme.Style(actionZone);
            actionZone.Text = T("\u64CD\u4F5C");
            actionZone.Dock = DockStyle.Fill;
            actionZone.Padding = new Padding(ZonePad, 6, ZonePad, 6);
            actions.Dock = DockStyle.Fill;
            actionZone.Controls.Add(actions);

            // --- zone 3: result --------------------------------------------------
            GroupBox resultZone = new GroupBox();
            Theme.Style(resultZone);
            resultZone.Text = T("\u6267\u884C\u7ED3\u679C");
            resultZone.Dock = DockStyle.Fill;
            resultZone.Padding = new Padding(ZonePad, 6, ZonePad, RowGap);
            output.Dock = DockStyle.Fill;
            resultZone.Controls.Add(output);

            layout.Controls.Add(statusZone, 0, 0);
            layout.Controls.Add(actionZone, 0, 1);
            layout.Controls.Add(resultZone, 0, 2);
            Controls.Add(layout);
            LayoutActionsRow(layout, actions);
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F5) { Refresh4(); }
                if (e.KeyCode == Keys.Escape) { Close(); }
                if (e.KeyCode == Keys.Enter && !busy) { RunSelected(-1); }
            };

            // Re-added after a line-range edit swallowed this handler: without it the
            // status panel stayed blank until the 2s timer first fired, and nothing
            // recorded that the form finished building or how many buttons exist.
            Load += delegate
            {
                UpdateStatus();
                ClickLog("form loaded, action buttons=" + actions.Controls.Count);
                DumpLayout("main", this, statusLine, detailLine, actions, output);
                DumpButtonGeometry();
                RestoreWindowBounds();
            };

            // Size and position are remembered. Someone who drags this window to a
            // second monitor and resizes it should not have to do it again every launch.
            FormClosing += delegate { SaveWindowBounds(); };
        }

        // --- window bounds memory ------------------------------------------------
        // Stored next to the other state, and validated on the way back in: a
        // remembered rectangle can point at a monitor that is no longer attached, and
        // a window restored off-screen is worse than one that forgets.

        private string BoundsFile
        {
            get { return Path.Combine(DataDir, "gui-window.json"); }
        }

        private void SaveWindowBounds()
        {
            try
            {
                if (WindowState != FormWindowState.Normal) { return; }   // maximised: keep the normal size
                string s = "{\"x\":" + Location.X + ",\"y\":" + Location.Y
                    + ",\"w\":" + Width + ",\"h\":" + Height + "}";
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(BoundsFile, s, new UTF8Encoding(false));
            }
            catch { }
        }

        private void RestoreWindowBounds()
        {
            try
            {
                if (!File.Exists(BoundsFile)) { return; }
                string s = File.ReadAllText(BoundsFile);
                int x = IntVal(s, "\"x\""), y = IntVal(s, "\"y\"");
                int w = IntVal(s, "\"w\""), h = IntVal(s, "\"h\"");
                if (w < MinimumSize.Width || h < MinimumSize.Height) { return; }

                // Must still land on a screen that exists.
                Rectangle want = new Rectangle(x, y, w, h);
                bool visible = false;
                foreach (Screen sc in Screen.AllScreens)
                {
                    if (Rectangle.Intersect(sc.WorkingArea, want).Width > 80
                        && Rectangle.Intersect(sc.WorkingArea, want).Height > 40)
                    {
                        visible = true;
                        break;
                    }
                }
                if (!visible)
                {
                    ClickLog("window bounds ignored (off-screen): " + x + "," + y + " " + w + "x" + h);
                    return;
                }
                StartPosition = FormStartPosition.Manual;
                Bounds = want;
                ClickLog("window bounds restored: " + x + "," + y + " " + w + "x" + h);
            }
            catch { }
        }

        private static int IntVal(string json, string key)
        {
            try
            {
                int i = json.IndexOf(key, StringComparison.Ordinal);
                if (i < 0) { return 0; }
                i = json.IndexOf(':', i);
                int j = i + 1;
                while (j < json.Length && (json[j] == ' ' || json[j] == '\t')) { j++; }
                int k = j;
                while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '-')) { k++; }
                int v;
                return int.TryParse(json.Substring(j, k - j), out v) ? v : 0;
            }
            catch { return 0; }
        }

        private static ListViewItem Row(string key, string name, string note)
        {
            ListViewItem it = new ListViewItem(key);
            it.SubItems.Add(name);
            it.SubItems.Add(note);
            return it;
        }

        // Gives the button row exactly the height its wrapped content needs, so a
        // narrow window grows that row instead of squeezing the output box. Row index
        // 1 is the action zone (0 = status zone, 2 = result zone).
        private static void LayoutActionsRow(TableLayoutPanel layout, FlowLayoutPanel actions)
        {
            try
            {
                if (layout == null || actions == null) { return; }
                if (layout.RowStyles.Count < 2) { return; }
                int wanted = actions.PreferredSize.Height + 30;
                if (wanted < ActionZonePx) { wanted = ActionZonePx; }
                if (layout.RowStyles[1].Height != wanted)
                {
                    layout.RowStyles[1] = new RowStyle(SizeType.Absolute, wanted);
                }
            }
            catch { }
        }

        // One fixed size per button, from the constants at the top of the class. With
        // two-character labels there is nothing to negotiate: seven 128px buttons plus
        // margins fit the minimum window width with room to spare, so no wrapping, no
        // shrink-to-fit and no width arithmetic at run time.
        private Button AddQuick(Control host, string text, string tooltip, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = false;
            b.Width = ButtonWidthPx;
            b.Height = ButtonHeightPx;
            b.Margin = new Padding(0, 0, ButtonGap, ButtonGap);
            Theme.Style(b, false);
            Theme.Round(b, 6);
            if (!string.IsNullOrEmpty(tooltip))
            {
                // The full wording lives here instead of on the button face.
                new ToolTip().SetToolTip(b, tooltip);
            }
            // Logging must never be able to break the click. The previous version
            // called ClickLog(...) bare in the handler, and ClickLog threw a
            // NullReferenceException, so EVERY button click died before it reached
            // the action -- while the log stayed empty and the cause was invisible.
            // The action now runs first and its outcome is what gets reported.
            b.Click += delegate(object s2, EventArgs e2)
            {
                string outcome;
                try
                {
                    onClick(s2, e2);
                    outcome = "ok";
                }
                catch (Exception ex)
                {
                    outcome = "threw " + ex.GetType().Name + ": " + ex.Message;
                    Text = T("DSH Guardian") + "  " + T("\u51FA\u9519");
                    // The status line reports one thing only: whether anything is watching.
                    // An error goes to the result area (and to the crash log); putting it
                    // on the status line meant the answer to "is it watching?" was hidden
                    // by whatever the last failed action had to say.
                    try { Append(T("\u51FA\u9519\uFF1A") + ex.Message); } catch { }
                    Program.CrashLog("button " + text, ex);
                }
                try { ClickLog("click: " + text + "  -> " + outcome); } catch { }
            };
            host.Controls.Add(b);
            return b;
        }

        // Hides the status zone and gives its height to the result text, so a long
        // baseline listing fits. The button that triggers this lives in the action zone,
        // which is deliberately NOT hidden -- collapsing the zone that holds the way
        // back would be a trap.
        private void ToggleResultZoom()
        {
            try
            {
                resultZoomed = !resultZoomed;
                layout.RowStyles[0] = new RowStyle(SizeType.Absolute, resultZoomed ? 0F : StatusZonePx);
                statusZone.Visible = !resultZoomed;
                if (zoomButton != null)
                {
                    zoomButton.Text = resultZoomed ? T("\u8FD4\u56DE") : T("\u653E\u5927\u7ED3\u679C");
                }
                ScrollOutputToEnd();
            }
            catch { }
        }

        // Clamps a window to the actual desktop working area.
        //
        // This is the cause of the "top of the window is missing" report. Since the
        // process became DPI-aware, the numbers given to ClientSize are PHYSICAL
        // pixels, so a 1100x640 dialog on a scaled or smaller display is simply bigger
        // than the screen and gets cut off -- by the display, not by the layout. Every
        // window here now asks for its preferred size and this trims it to fit.
        internal static void FitToScreen(Form f, int wantW, int wantH)
        {
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int maxW = wa.Width - 40;
                int maxH = wa.Height - 80;
                int w = Math.Min(wantW, maxW);
                int h = Math.Min(wantH, maxH);
                if (w < 520) { w = Math.Min(520, maxW); }
                if (h < 380) { h = Math.Min(380, maxH); }
                f.ClientSize = new Size(w, h);
                f.MinimumSize = new Size(Math.Min(880, maxW), Math.Min(360, maxH));
                ClickLog("screen workarea=" + wa.Width + "x" + wa.Height
                    + "  wanted=" + wantW + "x" + wantH
                    + "  client set to=" + w + "x" + h);
            }
            catch
            {
                f.ClientSize = new Size(wantW, wantH);
            }
        }

        // Shared dialog-button geometry. The picker and the browser each rolled their
        // own, with 26px filter buttons next to 30px action buttons and hand-computed
        // x offsets -- which is why the rows looked cramped and unevenly spaced.
        //
        // One gap, used everywhere. Earlier there were three different spacings in the
        // same program (8px on the main window, 10px in the dialogs, and 30px in front
        // of Cancel), which is exactly the kind of inconsistency that reads as "the
        // spacing is off" without anyone being able to point at which gap it is.
        internal const int ButtonGap = 10;
        internal const int DlgButtonH = 38;
        internal const int DlgButtonSmallH = 34;

        // Calibration scale. Every window uses these three numbers and nothing else, so
        // the left edge of the content is the same in all of them and the eye does not
        // have to re-find it after switching windows.
        //   EdgePad  distance from the window edge to the first content pixel
        //   ZonePad  padding inside a GroupBox caption frame
        //   RowGap   vertical space between a zone and the next one
        internal const int EdgePad = 14;   // matches the 14px a GroupBox border + ZonePad yields
        internal const int ZonePad = 12;
        internal const int RowGap = 8;
        // SplitContainer minimum pane sizes. Inline literals here were the last magic
        // numbers the standards scan found.
        internal const int PaneListMin = 260;
        internal const int PaneDetailMin = 200;
        internal const int PanePickerListMin = 160;
        internal const int PanePickerBundlesMin = 90;

        // primary = the one action worth emphasising in that row; it takes the accent
        // colour so the eye lands on it first.
        internal static Button DlgButton(string text, int width, int height, EventHandler onClick)
        {
            return DlgButton(text, width, height, onClick, false);
        }

        internal static Button DlgButton(string text, int width, int height, EventHandler onClick, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = false;
            b.Width = width;
            b.Height = height;
            b.Margin = new Padding(0, 0, ButtonGap, 0);
            Theme.Style(b, primary);
            Theme.Round(b, 5);
            if (onClick != null) { b.Click += onClick; }
            return b;
        }

        // Records the real on-screen rectangle of every zone, so "aligned" is a number
        // and not an opinion. Inside a zone the content starts at EdgePad + ZonePad;
        // outside one it starts at EdgePad.
        internal static void DumpLayout(string which, Form f, params Control[] zones)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(which + " layout: client=" + f.ClientSize.Width + "x" + f.ClientSize.Height);
                foreach (Control c in zones)
                {
                    if (c == null) { continue; }
                    Point p = c.PointToScreen(Point.Empty);
                    Point o = f.PointToScreen(Point.Empty);
                    sb.AppendLine("  " + c.GetType().Name + " '" + c.Text + "'"
                        + "  x=" + (p.X - o.X) + " y=" + (p.Y - o.Y)
                        + " w=" + c.Width + " h=" + c.Height
                        + "  back=" + Hex(c.BackColor) + " fore=" + Hex(c.ForeColor));
                }
                // Buttons carry the theme, so report them too: "flat, rounded, one accent"
                // is a claim that should be checkable in the log, not just on screen.
                foreach (Control c in AllControls(f))
                {
                    Button b = c as Button;
                    if (b != null)
                    {
                        sb.AppendLine("  BUTTON '" + b.Text + "'  back=" + Hex(b.BackColor)
                            + " fore=" + Hex(b.ForeColor) + "  flat=" + (b.FlatStyle == FlatStyle.Flat)
                            + "  rounded=" + (b.Region != null));
                        continue;
                    }
                    // Does a label's text actually FIT its box? Fixed pixel heights are not
                    // scaled by the font, so on a non-96 DPI display a label can be handed a
                    // box shorter than its own text and clip it silently. Measured, not judged:
                    // this is how the truncated path line was found.
                    Label lab = c as Label;
                    if (lab == null || string.IsNullOrEmpty(lab.Text)) { continue; }
                    Size need = TextRenderer.MeasureText(lab.Text, lab.Font);
                    int textH = need.Height + lab.Padding.Vertical;
                    int textW = need.Width + lab.Padding.Horizontal;
                    bool clipV = textH > lab.Height + 1;
                    bool clipH = textW > lab.Width + 1;
                    sb.AppendLine("  LABEL '" + lab.Text.Substring(0, Math.Min(26, lab.Text.Length)) + "'"
                        + "  box=" + lab.Width + "x" + lab.Height
                        + "  text=" + textW + "x" + textH
                        + "  " + lab.Font.SizeInPoints.ToString("0.##") + "pt"
                        + (clipV ? "   <== CLIPPED (height)" : "")
                        + (clipH ? "   <== CLIPPED (width)" : ""));
                }
                ClickLog(sb.ToString().TrimEnd());
            }
            catch { }
        }

        private static string Hex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        private static System.Collections.Generic.List<Control> AllControls(Control root)
        {
            System.Collections.Generic.List<Control> all = new System.Collections.Generic.List<Control>();
            foreach (Control c in root.Controls)
            {
                all.Add(c);
                all.AddRange(AllControls(c));
            }
            return all;
        }

        // Measures every column against the widest text it actually holds, and writes
        // the numbers to the log. Judging "does it fit" by eye produced two wrong
        // conclusions in a row; a measured width cannot be misread. Lives in MainForm
        // because both dialogs report through it.
        internal static void DumpColumns(string which, ListView lv, Form owner)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(which + ": client=" + owner.ClientSize.Width + "x" + owner.ClientSize.Height
                    + "  list=" + lv.Width + "x" + lv.Height + "  rows=" + lv.Items.Count);
                for (int c = 0; c < lv.Columns.Count; c++)
                {
                    int widest = 0;
                    string sample = "";
                    foreach (ListViewItem it in lv.Items)
                    {
                        if (c >= it.SubItems.Count) { continue; }
                        string t = it.SubItems[c].Text;
                        int w = TextRenderer.MeasureText(t, lv.Font).Width;
                        if (w > widest) { widest = w; sample = t; }
                    }
                    int header = TextRenderer.MeasureText(lv.Columns[c].Text, lv.Font).Width;
                    if (header > widest) { widest = header; }
                    int need = widest + 16;   // cell padding
                    int col = lv.Columns[c].Width;
                    sb.AppendLine("  col[" + c + "] '" + lv.Columns[c].Text + "' width=" + col
                        + " needs=" + need + (col < need ? "   <== TOO NARROW" : "")
                        + "   widest='" + sample + "'");
                }
                int total = 0;
                foreach (ColumnHeader ch in lv.Columns) { total += ch.Width; }
                sb.AppendLine("  columns total=" + total + "  list inner=" + (lv.Width - 24)
                    + (total > lv.Width - 24 ? "   <== OVERFLOWS" : ""));
                ClickLog(sb.ToString().TrimEnd());
            }
            catch { }
        }

        // Records what the UI actually received, into a file the user can hand back.
        // Every line all the way through is defensive, for the reason above.
        //
        // Size-capped. Every launch writes a layout dump, so without a cap this file
        // grows for the life of the install -- a diagnostic aid that quietly becomes
        // the biggest file in data\. When it passes the cap it is trimmed to the most
        // recent lines, which is the only part anyone reads.
        private const int ClickLogMaxBytes = 256 * 1024;
        private const int ClickLogKeepLines = 800;

        internal static void ClickLog(string line)
        {
            try
            {
                string exe = Application.ExecutablePath;
                string app = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
                string dir = string.IsNullOrEmpty(app) ? null : Path.Combine(Path.GetDirectoryName(app), "data");
                if (string.IsNullOrEmpty(dir)) { return; }
                try { Directory.CreateDirectory(dir); } catch { }
                string file = Path.Combine(dir, "gui-clicks.log");

                try
                {
                    FileInfo fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > ClickLogMaxBytes)
                    {
                        string[] old = File.ReadAllLines(file);
                        int from = old.Length > ClickLogKeepLines ? old.Length - ClickLogKeepLines : 0;
                        System.Collections.Generic.List<string> keep =
                            new System.Collections.Generic.List<string>();
                        keep.Add("--- trimmed at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ---");
                        for (int i = from; i < old.Length; i++) { keep.Add(old[i]); }
                        File.WriteAllLines(file, keep.ToArray(), new UTF8Encoding(false));
                    }
                }
                catch { }

                File.AppendAllText(file,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + (line == null ? "" : line) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        // Runs one action by index, used by the quick-action buttons so they do not
        // have to move the selection first.
        // Runs one action by index. Buttons call this directly; there is no list
        // selection involved any more.
        private void RunAction(int index)
        {
            if (busy) { return; }
            RunSelected(index);
        }

        // Test hook for the action dispatch, used by the "click <n>" startup mode.
        // Exists because synthetic mouse input does not reach these controls on this
        // machine, so "the button does nothing" could not be split into its two very
        // different causes: the click never arriving, or the action never running.
        internal void RunActionForTest(int index)
        {
            ClickLog("selftest: entering RunAction(" + index + ")");
            RunAction(index);
            ClickLog("selftest: returned from RunAction(" + index + ")");
        }


        private static string T2(string s) { return s; }

        // MakeButton() used to sit here: the first button bar, with fixed 88x30 sizes and
        // hard-coded left offsets. Buttons are built by the action zone now and sized to
        // their own text, which is what stopped the labels being cut off.
        private static Font PickFont()
        {
            try
            {
                Font f = new Font("Microsoft YaHei UI", 9.75F);
                if (f.FontFamily.Name == "Microsoft YaHei UI") { return f; }
            }
            catch { }
            return new Font(FontFamily.GenericSansSerif, 10F);
        }

        // Chinese strings stay in one place so the ASCII rule above still holds.
        private static string T(string s) { return s; }

        // ---- settings ----------------------------------------------------------
        // One user-visible setting, stored next to the rest of the state.
        //
        // Kept as its own small file rather than folded into mode.json: mode.json is the
        // arm/pause state that the watcher reads on every round, and mixing a user
        // preference into it would make "paused" and "prefers X" the same field.
        private bool KeepWatchingAfterClose
        {
            get
            {
                try
                {
                    string p = Path.Combine(DataDir, "gui-settings.json");
                    if (!File.Exists(p)) { return false; }
                    string s = File.ReadAllText(p);
                    return s.IndexOf("\"keepWatchingAfterClose\":true", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    File.WriteAllText(Path.Combine(DataDir, "gui-settings.json"),
                        "{\"keepWatchingAfterClose\":" + (value ? "true" : "false") + "}",
                        new UTF8Encoding(false));
                    ClickLog("setting keepWatchingAfterClose=" + value);
                }
                catch { }
            }
        }

        // ---- state ------------------------------------------------------------
        private bool Armed
        {
            get
            {
                try
                {
                    string p = Path.Combine(DataDir, "mode.json");
                    if (!File.Exists(p)) { return false; }
                    return File.ReadAllText(p).IndexOf("\"auto\"", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch { return false; }
            }
        }

        private int WatcherPid
        {
            get
            {
                try
                {
                    string p = Path.Combine(DataDir, "runtime.pid");
                    if (!File.Exists(p)) { return 0; }
                    int pid;
                    if (!int.TryParse(File.ReadAllText(p).Trim(), out pid)) { return 0; }
                    Process.GetProcessById(pid);
                    return pid;
                }
                catch { return 0; }
            }
        }

        private string BaselineName()
        {
            try
            {
                string p = Path.Combine(DataDir, "last-known-good.json");
                if (!File.Exists(p)) { return null; }
                string s = File.ReadAllText(p);
                int i = s.IndexOf("\"snapshot\"", StringComparison.Ordinal);
                if (i < 0) { return null; }
                i = s.IndexOf(':', i);
                int a = s.IndexOf('"', i + 1);
                int b = s.IndexOf('"', a + 1);
                if (a < 0 || b < 0) { return null; }
                string v = s.Substring(a + 1, b - a - 1);
                return v.Length == 0 ? null : v;
            }
            catch { return null; }
        }

        private string LastCheck()
        {
            try
            {
                string p = Path.Combine(DataDir, "last-tick.json");
                if (!File.Exists(p)) { return T("\u8FD8\u6CA1\u6709\u8BB0\u5F55"); }
                string s = File.ReadAllText(p);
                int i = s.IndexOf("\"at\"", StringComparison.Ordinal);
                if (i < 0) { return T("\u8FD8\u6CA1\u6709\u8BB0\u5F55"); }
                i = s.IndexOf(':', i);
                int a = s.IndexOf('"', i + 1);
                int b = s.IndexOf('"', a + 1);
                DateTime t;
                if (!DateTime.TryParse(s.Substring(a + 1, b - a - 1), out t)) { return T("\u8FD8\u6CA1\u6709\u8BB0\u5F55"); }
                TimeSpan age = DateTime.Now - t;
                if (age.TotalSeconds < 90) { return ((int)age.TotalSeconds) + T(" \u79D2\u524D"); }
                if (age.TotalMinutes < 90) { return ((int)age.TotalMinutes) + T(" \u5206\u949F\u524D"); }
                return ((int)age.TotalHours) + T(" \u5C0F\u65F6\u524D");
            }
            catch { return T("\u8FD8\u6CA1\u6709\u8BB0\u5F55"); }
        }

        // Nudge the whole form; calling Refresh() directly confuses the designer.
        private void Refresh4() { if (!busy) { UpdateStatus(); } }

        private void UpdateStatus()
        {
            int pid = WatcherPid;
            bool armed = Armed;
            string baseline = BaselineName();

            if (!armed)
            {
                statusLine.Text = T("\u25CB \u672A\u76D1\u89C6");
                statusLine.ForeColor = Theme.Muted;
            }
            else if (pid > 0)
            {
                // The pid is part of the answer, not decoration: it is what lets the user
                // (or a support thread) confirm that the process they see in Task Manager
                // is the one this window is talking about.
                statusLine.Text = T("\u25CF \u76D1\u89C6\u4E2D\uFF08\u8FDB\u7A0B ") + pid + T("\uFF09");
                statusLine.ForeColor = Theme.Ok;
            }
            else
            {
                // Three states, and only three: 未监视 / 启动中 / 监视中.
                //
                // This line carried five at one point -- two of them warnings about a
                // watcher that had died or never started. The reasoning was sound (a
                // window that says 启动中 forever hides a dead guard) but the result was a
                // status line that changed shape depending on which failure it was
                // describing, so the answer to "is anything watching?" took reading
                // instead of glancing. The failure cases are not gone: they are in the
                // log, where a diagnosis belongs, and the process id stays on the line so
                // the state can be checked against Task Manager.
                statusLine.Text = T("\u25D0 \u68C0\u6D4B\u4E2D\u2026");
                statusLine.ForeColor = Theme.Warn;
            }

            // Poll fast whenever the answer can still change, slowly when it cannot.
            //
            // "Armed" is never a settled state: the watcher can die at any moment, and
            // the window is the only thing that would tell the user. Only a disarmed
            // window has nothing left to report, so that is the one case worth polling
            // slowly. A few small file reads plus one process lookup at 400ms costs
            // nothing measurable and turns "it died a moment ago" into "it died".
            try
            {
                int want = armed ? StatusFastRefreshMs : StatusRefreshMs;
                if (ticker != null && ticker.Interval != want) { ticker.Interval = want; }
            }
            catch { }

            // Record the status line whenever it changes.
            //
            // The window's whole promise is "this line tells you whether anything is
            // watching", and there was no way to check what it said without looking at
            // the screen -- a screenshot and an OCR pass, which is how the stuck
            // "启动中…" was reported. One line per change is cheap and makes the claim
            // checkable from data/gui-clicks.log.
            try
            {
                string shown = statusLine.Text;
                if (shown != lastStatusLogged)
                {
                    lastStatusLogged = shown;
                    ClickLog("status: " + shown);
                }
            }
            catch { }

            if (!armed)
            {
                statusLine.Text += T("   \u2014\u2014 \u4E0D\u5360\u7528\u4EFB\u4F55\u8D44\u6E90");
                armedNoPidSince = DateTime.MinValue;
            }
            else if (pid > 0)
            {
                armedNoPidSince = DateTime.MinValue;
                // The suffix states the behaviour that is actually in force, which now
                // depends on the setting rather than being fixed.
                // Read the RUNNING watcher's binding, not the setting: they diverge as
                // soon as the checkbox is toggled while monitoring. When they disagree,
                // say so instead of silently describing the wrong one.
                statusLine.Text += WatcherIsParentBound
                    ? T("   \u2014\u2014 \u5173\u6389\u672C\u7A97\u53E3\u5373\u505C\u6B62")
                    : T("   \u2014\u2014 \u5173\u7A97\u540E\u7EE7\u7EED\u76D1\u89C6");
                if (WatcherIsParentBound == KeepWatchingAfterClose)
                {
                    // Should not happen through the interface any more -- ticking the box
                    // rebuilds the watcher immediately. It can still happen if the
                    // settings file is edited while the window is open, so it is reported
                    // as a mismatch to clear rather than as a normal step.
                    statusLine.Text += T("\uFF08\u8BBE\u7F6E\u4E0E\u8FD0\u884C\u4E2D\u7684\u76D1\u89C6\u5668\u4E0D\u4E00\u81F4\uFF0C\u70B9\u4E00\u6B21\u300C\u5F00\u5173\u76D1\u89C6\u300D\u91CD\u65B0\u5BF9\u9F50\uFF09");
                }
            }

            string detail = T("\u4E0A\u6B21\u68C0\u67E5\uFF1A") + LastCheck();
            detail += "        " + T("\u56DE\u9000\u76EE\u6807\uFF1A")
                + (baseline == null ? T("\uFF08\u672A\u8BBE\u7F6E\uFF09") : baseline);
            if (baseline == null)
            {
                detail += Environment.NewLine
                    + T("\u26A0 \u8FD8\u6CA1\u6709\u57FA\u7EBF\uFF0C\u5D29\u4E86\u6CA1\u5F97\u9000\u3002\u5148\u9009\u300C\u6253\u57FA\u7EBF\u300D\u3002");
                detailLine.ForeColor = Theme.Warn;
            }
            else if (!armed)
            {
                detail += Environment.NewLine
                    + T("\u88C5\u63D2\u4EF6\u524D\uFF1A\u5148\u6253\u5F00\u76D1\u89C6\uFF0C\u5E76\u4FDD\u6301\u672C\u7A97\u53E3\u5F00\u7740\u3002");
                Theme.Style(detailLine, Theme.Muted, 9.75F, FontStyle.Regular);
            }
            else
            {
                detail += Environment.NewLine
                    + T("\u5B88\u62A4\u4E2D\u3002\u88C5\u5B8C\u63D2\u4EF6\u786E\u8BA4 DSH \u6B63\u5E38 \u2192 \u91CD\u65B0\u6253\u57FA\u7EBF \u2192 \u5173\u95ED\u76D1\u89C6\u3002");
                Theme.Style(detailLine, Theme.Muted, 9.75F, FontStyle.Regular);
            }
            detailLine.Text = detail;
        }

        // ---- the running watcher's actual binding ------------------------------
        // The setting says what the NEXT arm will do. It does not describe the watcher
        // that is running right now, and those two can differ: the setting is read at
        // arm time and the watcher keeps the binding it was started with.
        //
        // Reported as "关窗即停功能无法实现": the checkbox showed 关窗即停 while the
        // running watcher had in fact been started without -ParentPid, so closing the
        // window left it running. The label was reading the setting, not the process.
        //
        // So the binding is written down when the watcher is launched, and every label
        // that talks about closing the window reads this instead.
        private string LaunchRecordPath { get { return Path.Combine(DataDir, "watcher-launch.json"); } }

        private void WriteLaunchRecord(bool parentBound)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(LaunchRecordPath,
                    "{\"parentBound\":" + (parentBound ? "true" : "false") + "}",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        // true = the running watcher is bound to this window (closing stops it).
        private bool WatcherIsParentBound
        {
            get
            {
                try
                {
                    if (!File.Exists(LaunchRecordPath)) { return true; }   // older launches were always bound
                    string s = File.ReadAllText(LaunchRecordPath);
                    return s.IndexOf("\"parentBound\":false", StringComparison.OrdinalIgnoreCase) < 0;
                }
                catch { return true; }
            }
        }

        // ---- actions ----------------------------------------------------------
        // Self-test entry for the close-behaviour setting: arm, then flip the box.
        // Exists because the checkbox cannot be clicked from a script on this machine.
        internal void RunSettingTest(bool want)
        {
            try
            {
                ClickLog("setting test: arming");
                RunActionForTest(1);
                if (keepBox != null) { keepBox.Checked = want; }
                ClickLog("setting test: keepWatchingAfterClose=" + want
                    + "  parentBound=" + WatcherIsParentBound);
            }
            catch (Exception ex) { Program.CrashLog("setting test", ex); }
        }

        private void RunSelected(int i)
        {
            if (i < 0) { return; }
            switch (i)
            {
                case 0: RunSnapshot("-Action Mark-Good", T("\u6253\u57FA\u7EBF")); break;
                case 1: ToggleWatch(); break;
                case 2: RollbackFlow(); break;
                case 3: ShowLogs(); break;
                case 4: OpenData(); break;
                case 5: Refresh4(); Append(T("\u5DF2\u5237\u65B0\u3002")); break;
                default: Close(); break;
            }
        }

        private void ToggleWatch()
        {
            if (Armed) { Disarm(); }
            else { Arm(); }
        }

        // Ported from the console version rather than re-invented: the watcher is
        // spawned as a child and confirms itself by claiming data\runtime.pid, and
        // arming has to give it -Mode auto on the first round or it reads the stale
        // mode file and quits.
        private void Arm()
        {
            EnterBusy(T("\u6253\u5F00\u76D1\u89C6"));
            Append(T("\u2500\u2500 ") + T("\u6253\u5F00\u76D1\u89C6") + T(" \u2500\u2500"));
            Application.DoEvents();

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                // The resident watcher is now this project's own C# code, not a
                // long-lived PowerShell. Hosting the loop in PowerShell meant holding
                // ~139 MB for hours so a probe could run once a minute; the same loop in
                // watch-loop holds a few MB and starts PowerShell only for the round
                // itself. The decision logic and the rollback are unchanged -- they are
                // still dsh-watchdog.ps1 / dsh-snapshot.ps1.
                psi.FileName = LoopExe;
                psi.Arguments = "watch-loop"
                    + " -IntervalSeconds " + WatchIntervalSeconds
                    + (KeepWatchingAfterClose
                        ? ""
                        // The start time goes with the pid: Windows recycles pids, and a
                        // recycled one makes the binding look alive forever, so the loop
                        // outlives the window it was told to follow.
                        : " -ParentPid " + Process.GetCurrentProcess().Id
                          + " -ParentStartTicks " + Process.GetCurrentProcess().StartTime.Ticks);
                // Record what was actually used, so the window can tell the truth about
                // this watcher even after the setting is changed.
                WriteLaunchRecord(!KeepWatchingAfterClose);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);

                int pid = 0;
                // How long to wait for the watcher to claim its pid file, and how often
                // to look. Measured: one to two seconds. Named rather than inlined so the
                // wait and the poll are the same numbers the constants describe.
                for (int i = 0; i < WatcherPidWaitTries && pid == 0; i++)
                {
                    System.Threading.Thread.Sleep(WatcherPidWaitMs);
                    pid = WatcherPid;
                }
                if (pid > 0)
                {
                    Append(T("\u76D1\u89C6\u5DF2\u5F00\u542F\uFF08\u8FDB\u7A0B ") + pid + T("\uFF09\u3002\u88C5\u5B8C\u63D2\u4EF6\u540E\u56DE\u6765\u91CD\u65B0\u6253\u57FA\u7EBF\u3002"));
                    // This line used to read "本窗口可以关掉：监视跑在独立进程里，不依赖这个界面"
                    // -- the exact opposite of what the program does. The watcher is started
                    // with -ParentPid <this process> and exits as soon as this window's
                    // process is gone, which is what the status line eighty lines above
                    // already says ("关掉本窗口即停止") and what the exit dialog warns about.
                    //
                    // The false version is the one users read, because it appears exactly when
                    // they have just armed the guard -- so it also leaked into the README, the
                    // Chinese guide and the pinned issue. One wrong string, copied outward.
                    Append(T("\u672C\u7A97\u53E3\u5C31\u662F\u5F00\u5173\uFF1A\u76D1\u89C6\u5668\u7ED1\u5B9A\u5728\u5B83\u4E0A\uFF0C\u5173\u6389\u7A97\u53E3\u5C31\u4F1A\u505C\u6B62\u76D1\u89C6\u3002"));
                }
                else
                {
                    Append(T("\u76D1\u89C6\u6CA1\u80FD\u542F\u52A8\u3002\u8BF7\u68C0\u67E5\uFF1A"));
                    Append("  " + WatchdogPath);
                    Append("  " + Path.Combine(DataDir, "exe-trace.log"));
                }
            }
            catch (Exception ex)
            {
                Append(T("\u542F\u52A8\u5931\u8D25\uFF1A") + ex.Message);
            }

            ExitBusy(0);
        }

        private void Disarm()
        {
            try
            {
                RunScript(WatchdogPath, "-Pause", T("\u5173\u95ED\u76D1\u89C6"));
                int pid = WatcherPid;
                if (pid > 0)
                {
                    try { Process.GetProcessById(pid).Kill(); } catch { }
                }
                try { File.Delete(Path.Combine(DataDir, "runtime.pid")); } catch { }
                Append(T("\u76D1\u89C6\u5DF2\u505C\u6B62\uFF0C\u4E0D\u518D\u5360\u7528\u4EFB\u4F55\u8D44\u6E90\u3002"));
            }
            catch (Exception ex)
            {
                Append(T("\u5173\u95ED\u5931\u8D25\uFF1A") + ex.Message);
            }
            UpdateStatus();
        }

        // Confirms, then deletes the chosen snapshots. Returns true only when the delete
        // really happened, so the picker knows whether to rescan or leave the list alone.
        // Accepts several names: the snapshot script takes a comma-separated list, and
        // clearing out a run of old versions is the actual use case.
        private bool DeleteSnapshotsWithConfirm(string[] names)
        {
            if (names == null || names.Length == 0) { return false; }

            string list;
            if (names.Length <= 8)
            {
                list = string.Join(Environment.NewLine, names);
            }
            else
            {
                list = string.Join(Environment.NewLine, names, 0, 8)
                    + Environment.NewLine + T("\u2026 \u5171 ") + names.Length + T(" \u4E2A");
            }

            string warn = (names.Length == 1
                    ? T("\u5220\u9664\u8FD9\u4E2A\u5FEB\u7167\uFF1A")
                    : T("\u5220\u9664\u8FD9 ") + names.Length + T(" \u4E2A\u5FEB\u7167\uFF1A"))
                + Environment.NewLine + list + Environment.NewLine + Environment.NewLine
                + T("\u6587\u4EF6\u4F1A\u88AB\u771F\u6B63\u5220\u6389\uFF0C\u8FD9\u662F\u672C\u7A0B\u5E8F\u552F\u4E00\u4E0D\u53EF\u64A4\u9500\u7684\u64CD\u4F5C\u3002")
                + Environment.NewLine
                + T("\u5982\u679C\u5176\u4E2D\u5305\u542B\u5F53\u524D\u56DE\u9000\u76EE\u6807\uFF0C\u76EE\u6807\u4F1A\u81EA\u52A8\u6539\u4E3A\u5269\u4E0B\u6700\u65B0\u7684\u4E00\u4E2A\u3002");
            if (MessageBox.Show(warn, T("\u786E\u8BA4\u5220\u9664"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return false;
            }

            int rc = RunScriptForResult(SnapshotPath,
                "-Action Delete -Snapshot \"" + string.Join(",", names) + "\"",
                T("\u5220\u9664 ") + names.Length + T(" \u4E2A\u5FEB\u7167"));
            return rc == 0;
        }

        private void RollbackFlow()
        {
            // A real dialog, not a hand-off to the console build. The console
            // version's picker needs a console window to draw in, so launching it
            // hidden from here produced no window at all -- that was the "window?"
            // report. Every interaction in this program now happens in a WinForms
            // control, which is the whole point of the rewrite.
            string snapRoot = Path.Combine(DataDir, "snapshots");
            string[] dirs;
            try { dirs = Directory.GetDirectories(snapRoot, "snap-*"); }
            catch { dirs = new string[0]; }

            if (dirs.Length == 0)
            {
                MessageBox.Show(
                    T("\u8FD8\u6CA1\u6709\u5FEB\u7167\u3002\u5148\u9009\u300C1 \u6253\u57FA\u7EBF\u300D\u5EFA\u4E00\u4E2A\u3002"),
                    T("DSH Guardian"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (RollbackDialog dlg = new RollbackDialog(
                snapRoot,
                delegate { return BaselineName(); },
                delegate(string[] snaps) { return DeleteSnapshotsWithConfirm(snaps); }))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Chosen == null) { return; }
                string name = Path.GetFileName(dlg.Chosen);

                if (dlg.Mode == RollbackMode.SetTargetOnly)
                {
                    RunScript(SnapshotPath, "-Action Promote -Snapshot \"" + name + "\"",
                        T("\u8BBE\u4E3A\u4EE5\u540E\u7684\u56DE\u9000\u76EE\u6807"));
                    return;
                }

                string msg = T("\u786E\u5B9A\u73B0\u5728\u56DE\u9000\u5230\uFF1A") + Environment.NewLine + name
                    + Environment.NewLine + Environment.NewLine
                    + T("\u5F53\u524D\u914D\u7F6E\u4F1A\u5148\u53E6\u5B58\u5230 snapshots\\pre-restore-<\u65F6\u95F4>\\\uFF0C\u4E0D\u4F1A\u4E22\u5931\u3002");
                if (MessageBox.Show(msg, T("\u786E\u8BA4\u56DE\u9000"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    return;
                }
                RunScript(SnapshotPath, "-Action Restore -Force -Snapshot \"" + name + "\"",
                    T("\u56DE\u9000\u5230 ") + name);
                RunScript(SnapshotPath, "-Action Promote -Snapshot \"" + name + "\"",
                    T("\u56DE\u9000\u540E\u540C\u6B65\u76EE\u6807"));
            }
        }

        private void ShowLogs()
        {
            string file = Path.Combine(DataDir, "watchdog.log");

            // Explain the file BEFORE showing it. A wall of "[INFO] 27 ... [ALERT] 42"
            // tells a first-time reader nothing about what they are looking at, and the
            // lines that look alarming are mostly protections working as designed.
            Append("");
            Append(T("\u2500\u2500 \u8FD9\u662F\u4EC0\u4E48\u65E5\u5FD7 \u2500\u2500"));
            Append(T("  ") + T("\u76D1\u89C6\u5668\u7684\u8FD0\u884C\u65E5\u5FD7\uFF1A\u6BCF\u4E00\u8F6E\u63A2\u6D4B\u5199\u4E00\u884C\uFF0C"));
            Append(T("  ") + T("\u8BB0\u5F55\u7AEF\u53E3\u63A2\u6D4B\u3001\u5065\u5EB7\u5224\u5B9A\u3001\u5D29\u6E83\u5224\u5B9A\u548C\u81EA\u52A8\u56DE\u9000\u7684\u5168\u8FC7\u7A0B\u3002"));
            Append(T("  ") + T("\u4F4D\u7F6E\uFF1A") + file);
            Append(T("  ") + T("\u540C\u7C7B\u65E5\u5FD7\u8FD8\u6709\uFF1Aconsole-*.log\uFF08DSH \u81EA\u5DF1\u7684\u62A5\u9519\u539F\u6587\uFF09\u3001"));
            Append(T("     ") + T("crash-evidence-*.json\uFF08\u5224\u5B9A\u5D29\u6E83\u65F6\u7684\u73B0\u573A\uFF09\u3002"));
            Append(T("\u2500\u2500 \u600E\u4E48\u770B \u2500\u2500"));
            Append(T("  ") + T("\u2022 \u8D8A\u9760\u4E0B\u8D8A\u65B0\uFF1B\u8FD9\u91CC\u53EA\u663E\u793A\u6700\u540E 40 \u884C\u3002"));
            Append(T("  ") + T("\u2022 \u53EA\u6709 [ALERT] \u9700\u8981\u4EBA\u5DE5\u5904\u7406\uFF1B[INFO] / [WARN] \u662F\u8FC7\u7A0B\u8BB0\u5F55\u3002"));
            Append(T("  ") + T("\u2022 suppressed\u3001still inside the boot window \u662F\u4FDD\u62A4\u751F\u6548\uFF0C\u4E0D\u662F\u6545\u969C\u3002"));
            Append(T("  ") + T("\u2022 \u6BCF\u884C\u9010\u5B57\u8BB2\u89E3\uFF1Adocs\\LOG.md\u3002"));
            Append(T("\u2500\u2500 \u65E5\u5FD7\u5185\u5BB9\uFF08\u672B\u5C3E 40 \u884C\uFF09\u2500\u2500"));

            if (!File.Exists(file))
            {
                Append(T("\uFF08\u8FD9\u4E2A\u6587\u4EF6\u8FD8\u6CA1\u751F\u6210\uFF1A\u76D1\u89C6\u8FD0\u884C\u8FC7\u4E00\u8F6E\u4E4B\u540E\u5C31\u4F1A\u6709\u3002\uFF09"));
                return;
            }
            string[] lines = File.ReadAllLines(file);
            int from = lines.Length > 40 ? lines.Length - 40 : 0;
            for (int i = from; i < lines.Length; i++) { Append(lines[i]); }
        }

        // Opens the folder INSIDE this program. Two earlier attempts asked another
        // process to show it (explorer.exe directly, then the shell COM object) and
        // both failed on this machine -- startup failure 0xc0000142, then "Windows
        // cannot access the specified device, path, or file". The lesson recorded
        // for this project is to stop repeating a technique that has already failed
        // twice, so this program no longer asks anyone to show a folder: a small
        // browser lists the contents and opens a file with its default handler,
        // which is the same Process.Start-of-a-file call that already works for
        // powershell.exe on this machine.
        private void OpenData()
        {
            string dir = DataDir;
            try { if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); } }
            catch { }
            try
            {
                using (FolderBrowser dlg = new FolderBrowser(dir))
                {
                    dlg.ShowDialog(this);
                }
                Append(T("\u5DF2\u6D4F\u89C8\uFF1A") + dir);
            }
            catch (Exception ex)
            {
                Append(T("\u6D4F\u89C8\u5931\u8D25\uFF1A") + ex.Message);
                Append(T("\u8DEF\u5F84\uFF1A") + dir);
            }
        }

        private void RunSnapshot(string args, string title)
        {
            RunScript(SnapshotPath, args, title);
        }

        // Runs one PowerShell script with its output piped in, so the child process
        // can never write to this window and cannot steal focus. This is the same
        // rule the console version had to learn: one writer per surface.
        private void RunScript(string script, string args, string title)
        {
            RunScriptForResult(script, args, title);
        }

        // Same as RunScript but reports the script's exit code, for callers that must
        // know whether the operation actually happened (the picker's delete).
        private int RunScriptForResult(string script, string args, string title)
        {
            EnterBusy(title);
            int rc = 1;
            Append("");
            Append(T("\u2500\u2500 ") + title + T(" \u2500\u2500"));
            Application.DoEvents();

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = PsExe;
                psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""
                    + script + "\" -DataDir \"" + DataDir + "\" " + args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                Process p = Process.Start(psi);
                string err = p.StandardError.ReadToEnd();
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                rc = p.ExitCode;

                if (outp != null && outp.Trim().Length > 0) { Append(outp.TrimEnd()); }
                if (err != null && err.Trim().Length > 0) { Append(err.TrimEnd()); }
            }
            catch (Exception ex)
            {
                Append(T("\u65E0\u6CD5\u542F\u52A8 PowerShell\uFF1A") + ex.Message);
                rc = 3;
            }

            Append(rc == 0 ? T("[\u5B8C\u6210]") : T("[\u9000\u51FA\u7801 ") + rc + "]");
            ExitBusy(rc);
            return rc;
        }

        // Immediate feedback. Before this, clicking a button produced nothing visible
        // until the child process finished -- several seconds of a UI that looked
        // dead, which is the "按钮按完无反馈" report. Now the status line, the title
        // bar, the cursor and the button row all change on the spot.
        private void EnterBusy(string what)
        {
            busy = true;
            actions.Enabled = false;
            Cursor = Cursors.WaitCursor;
            // The status line is NOT touched here: it reports whether anything is
            // watching, and that does not change because a button was pressed. The busy
            // feedback lives in the title bar and the result area, which is enough to
            // answer "did my click register" without overwriting the one line the user
            // checks before walking away.
            Text = T("DSH Guardian") + "  " + T("\u6267\u884C\u4E2D\uFF1A") + what;
            Append(T("\u25B6 \u5F00\u59CB\uFF1A") + what);
            ScrollOutputToEnd();
            Application.DoEvents();
        }

        private void ExitBusy(int rc)
        {
            busy = false;
            actions.Enabled = true;
            Cursor = Cursors.Default;
            Text = T("DSH Guardian") + "  " + T("DSH \u5D29\u6E83\u81EA\u52A8\u56DE\u9000");
            Append(rc == 0 ? T("\u2714 \u5B8C\u6210") : T("\u2716 \u5931\u8D25\uFF08\u9000\u51FA\u7801 ") + rc + T("\uFF09"));
            ScrollOutputToEnd();
            UpdateStatus();
        }

        // Exit, spelled out. The old binding was a bare Close() that said nothing:
        // the window vanished and left the user unsure whether monitoring had been
        // stopped or was still running. The dialog now states the current state and
        // offers the choice, and the watcher is handled explicitly either way.
        private void ExitApp()
        {
            if (busy)
            {
                MessageBox.Show(T("\u6B63\u5728\u6267\u884C\u4E00\u4E2A\u52A8\u4F5C\uFF0C\u7B49\u5B83\u7ED3\u675F\u540E\u518D\u9000\u51FA\u3002"),
                    T("DSH Guardian"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool armed = Armed;
            int pid = WatcherPid;

            if (!armed)
            {
                if (MessageBox.Show(
                        T("\u76D1\u89C6\u672A\u5F00\u542F\uFF0C\u9000\u51FA\u4E0D\u4F1A\u5F71\u54CD\u4EFB\u4F55\u4E1C\u897F\u3002") + Environment.NewLine
                        + T("\u786E\u5B9A\u9000\u51FA\uFF1F"),
                        T("\u9000\u51FA DSH Guardian"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    Close();
                }
                return;
            }

            string who = pid > 0 ? (T("\uFF08\u8FDB\u7A0B ") + pid + T("\uFF09")) : T("\uFF08\u68C0\u6D4B\u4E2D\uFF09");
            // This dialog used to offer "keep watching, just close this window" -- which
            // the implementation cannot honour. The watcher is started with
            // -ParentPid <this process>, and dsh-watchdog.ps1 exits as soon as that pid is
            // gone (Test-PidAlive -> 'launcher closed'). A user could pick "yes", watch the
            // window disappear and believe monitoring was still running, while it had in
            // fact stopped. For a tool whose entire purpose is to be watching when a plugin
            // breaks DSH, that is the worst possible outcome.
            //
            // The parent binding is what makes "nothing keeps running behind your back"
            // true -- and it is now optional, so the dialog has to say which of the two
            // behaviours the user actually chose.
            //
            // Either way the reply closes the window: with the binding in force the
            // watcher stops by itself, and without it Disarm() is what stops it. Asking
            // "stop watching, or keep watching?" here would be a second, hidden copy of
            // the setting.
            string msg = T("\u76D1\u89C6\u6B63\u5728\u8FD0\u884C") + who + T("\u3002") + Environment.NewLine
                + Environment.NewLine
                + (!WatcherIsParentBound
                    ? T("\u5F53\u524D\u8FD9\u4E2A\u76D1\u89C6\u5668\u4E0D\u7ED1\u5B9A\u7A97\u53E3\uFF1A\u5173\u6389\u672C\u7A97\u53E3\u540E\u5B83\u4F1A\u7EE7\u7EED\u8DD1\u3002") + Environment.NewLine
                        + T("\u60F3\u505C\u5C31\u91CD\u65B0\u6253\u5F00\u672C\u7A97\u53E3\u70B9\u300C\u5F00\u5173\u76D1\u89C6\u300D\u3002")
                    : T("\u5173\u6389\u672C\u7A97\u53E3\u4F1A\u540C\u65F6\u505C\u6B62\u76D1\u89C6\uFF1A\u76D1\u89C6\u5668\u7ED1\u5B9A\u5728\u672C\u7A97\u53E3\u4E0A\u3002") + Environment.NewLine
                        + T("\u60F3\u5173\u7A97\u540E\u7EE7\u7EED\u76D1\u89C6\uFF0C\u5148\u52FE\u4E0A\u300C\u5173\u7A97\u540E\u7EE7\u7EED\u76D1\u89C6\u300D\u5E76\u91CD\u65B0\u5F00\u5173\u4E00\u6B21\u76D1\u89C6\u3002"))
                + Environment.NewLine + Environment.NewLine
                + T("\u786E\u5B9A\u9000\u51FA\uFF1F");
            DialogResult r = MessageBox.Show(msg, T("\u9000\u51FA DSH Guardian"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (r != DialogResult.Yes) { return; }
            Disarm();
            Close();
        }

        // Records the real geometry of every action button. Four layout attempts were
        // argued from how FlowLayoutPanel is supposed to behave; this writes down what
        // it actually did, so the next fix starts from measurements.
        private void DumpButtonGeometry()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("client=" + ClientSize.Width + "x" + ClientSize.Height
                    + "  panel=" + actions.Width + "x" + actions.Height
                    + "  actionsPref=" + actions.PreferredSize.Width + "x" + actions.PreferredSize.Height);
                foreach (Control c in actions.Controls)
                {
                    sb.AppendLine("  '" + c.Text + "'  x=" + c.Left + " w=" + c.Width
                        + "  right=" + (c.Left + c.Width) + "  visible=" + c.Visible);
                }
                ClickLog(sb.ToString().TrimEnd());
            }
            catch { }
        }

        private void ScrollOutputToEnd()
        {
            try
            {
                output.SelectionStart = output.TextLength;
                output.ScrollToCaret();
            }
            catch { }
        }

        internal static string BaselineNameFor(string dataDir)
        {
            try
            {
                string p = Path.Combine(dataDir, "last-known-good.json");
                if (!File.Exists(p)) { return null; }
                string s = File.ReadAllText(p);
                int i = s.IndexOf("\"snapshot\"", StringComparison.Ordinal);
                if (i < 0) { return null; }
                i = s.IndexOf(':', i);
                int a = s.IndexOf('"', i + 1);
                int b = s.IndexOf('"', a + 1);
                if (a < 0 || b < 0) { return null; }
                string v = s.Substring(a + 1, b - a - 1);
                return v.Length == 0 ? null : v;
            }
            catch { return null; }
        }

        // Returns true when the command line asked for the self test. Static, so no
        // form instance (and therefore no Timer or message loop) is needed.
        internal static bool HandleCheck(string[] args)
        {
            if (args == null || args.Length == 0) { return false; }
            if (!args[0].TrimStart('-', '/').Equals("check", StringComparison.OrdinalIgnoreCase)) { return false; }
            Environment.ExitCode = SelfCheck();
            return true;
        }

        // QA entry point: exercises the same RunScript path the buttons use.
        private static int SelfCheck()
        {
            // Form-free self test: proves every wiring the GUI buttons depend on,
            // without creating a window or a message loop.
            StringBuilder log = new StringBuilder();
            string baseDir = Path.GetDirectoryName(Application.ExecutablePath);
            string dataDir = Path.Combine(Path.GetDirectoryName(baseDir), "data");
            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\WindowsPowerShell\v1.0\powershell.exe");
            string wd = Path.Combine(baseDir, "dsh-watchdog.ps1");
            string sn = Path.Combine(baseDir, "dsh-snapshot.ps1");
            string cs = Path.Combine(baseDir, "dsh-guardian-console.exe");
            int fail = 0;

            Action<string, bool> check = delegate(string what, bool ok)
            {
                log.AppendLine((ok ? "[ok]   " : "[FAIL] ") + what);
                if (!ok) { fail++; }
            };

            check("powershell.exe", File.Exists(ps));
            check("dsh-watchdog.ps1", File.Exists(wd));
            check("dsh-snapshot.ps1", File.Exists(sn));
            check("dsh-guardian-console.exe (version picker)", File.Exists(cs));
            check("data dir exists", Directory.Exists(dataDir));
            log.AppendLine("       BaseDir = " + baseDir);
            log.AppendLine("       DataDir = " + dataDir);

            // Can we read the state the status panel shows?
            try
            {
                string mode = File.Exists(Path.Combine(dataDir, "mode.json"))
                    ? File.ReadAllText(Path.Combine(dataDir, "mode.json")) : "";
                log.AppendLine("       mode.json: " + (mode.IndexOf("\"auto\"") >= 0 ? "auto (armed)" : "paused/absent"));
                check("mode.json readable", true);
            }
            catch (Exception ex) { check("mode.json readable (" + ex.Message + ")", false); }

            // Does a real script actually run through the same pipe setup the
            // buttons use? -Action List is read-only and safe to run here.
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = ps;
                psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""
                    + sn + "\" -DataDir \"" + dataDir + "\" -Action List";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                Process p = Process.Start(psi);
                string err = p.StandardError.ReadToEnd();
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                log.AppendLine("       snapshot -Action List exit=" + p.ExitCode
                    + " stdout=" + (outp == null ? 0 : outp.Length) + " chars"
                    + " stderr=" + (err == null ? 0 : err.Length) + " chars");
                check("script round-trip (pipe capture)", p.ExitCode == 0);
                if (!string.IsNullOrEmpty(outp))
                {
                    string first = outp.Replace("\r\n", "\n").Split('\n')[0];
                    log.AppendLine("       first line: " + first);
                }
            }
            catch (Exception ex) { check("script round-trip threw: " + ex.Message, false); }

            // The state object the status panel reads must parse.
            try
            {
                string st = Path.Combine(dataDir, "state.json");
                if (File.Exists(st)) { File.ReadAllText(st); }
                check("state.json readable", true);
            }
            catch (Exception ex) { check("state.json readable (" + ex.Message + ")", false); }

            log.AppendLine(fail == 0 ? "RESULT: all checks passed" : ("RESULT: " + fail + " failed"));
            try
            {
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(Path.Combine(dataDir, "gui-check.txt"), log.ToString(), new UTF8Encoding(false));
            }
            catch { }
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "dsh-gui-check.txt"), log.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return fail == 0 ? 0 : 1;
        }

        // RunForTest() used to sit here: a second copy of RunScript that also captured
        // stdout/stderr into a string. It was written for the --selftest harness and then
        // superseded by RunScript + the per-action self-test, leaving no callers. Two
        // functions that both "run the watchdog" is how the two drift apart.
        private void Append(string text)
        {
            if (text == null) { return; }
            output.AppendText(text + Environment.NewLine);
            ScrollOutputToEnd();
        }
    }

    // Snapshot picker. A ListBox of versions, the active target marked, and two
    // explicit actions instead of "Yes / No / Cancel": the console version's
    // three-way message box was the part users found hardest to read.
    internal enum RollbackMode { RestoreNow, SetTargetOnly }

    // Version picker with columns and quick filters.
    //
    // Was a bare ListBox of names, which stops working the moment there are more
    // than a handful: the name alone does not say when it was taken or which
    // plugins it holds. Now: columns (version / captured / plugins / note), the
    // active target marked in its own column, and filter buttons for the views
    // people actually want -- all, newest 10, or baselines only.
    internal sealed class RollbackDialog : Form
    {
        public string Chosen { get; private set; }
        public RollbackMode Mode { get; private set; }

        private ListView list;
        private ListBox bundlesBox;
        private CheckBox multiBox;
        private Label hint;
        private Label summary;
        private string[] dirs;
        private string active;
        // Supplied by the caller: re-reads the target, and performs a delete. The
        // dialog does not shell out itself, and the caller keeps ownership of what
        // "delete" means (confirm dialog, script call).
        private readonly string snapRoot;
        private readonly Func<string> getActive;
        private readonly Func<string[], bool> onDelete;

        public RollbackDialog(string snapshotRoot, Func<string> activeProvider, Func<string[], bool> deleteHandler)
        {
            snapRoot = snapshotRoot;
            getActive = activeProvider;
            onDelete = deleteHandler;
            dirs = Scan();
            active = CurrentActive();

            Text = T2("DSH Guardian") + "  " + T2("\u9009\u62E9\u7248\u672C");
            // Preferred size; trimmed to the desktop by FitToScreen.
            MainForm.FitToScreen(this, 1000, 560);
            // Centred on the SCREEN, not the parent: these dialogs are taller than the
            // main window, so CenterParent can push them off the bottom of the display.
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9.75F);

            hint = new Label();
            hint.Dock = DockStyle.Top;
            // Height is set in Load (after font auto-scaling); it wraps to two lines.
            hint.Padding = new Padding(MainForm.EdgePad, 8, MainForm.EdgePad, 0);
            hint.Text = T2("\u9009\u4E2D\u4E00\u4E2A\u7248\u672C\uFF1A\u73B0\u5728\u5C31\u56DE\u9000\u3001\u53EA\u8BBE\u4E3A\u4EE5\u540E\u7684\u76EE\u6807\u3002")
                + T2("\u8981\u4E00\u6B21\u5220\u9664\u591A\u4E2A\uFF0C\u6253\u5F00\u53F3\u4FA7\u300C\u591A\u9009\u300D\u540E\u52FE\u9009\uFF08\u591A\u9009\u4EC5\u5BF9\u5220\u9664\u751F\u6548\uFF09\u3002");

            // Filter row: one FlowLayoutPanel, one button size, nothing hand-placed.
            FlowLayoutPanel filters = new FlowLayoutPanel();
            filters.Dock = DockStyle.Top;
            filters.Height = MainForm.DlgButtonSmallH + 20;
            filters.Padding = new Padding(MainForm.EdgePad, 8, MainForm.EdgePad, 6);
            filters.WrapContents = false;
            filters.Controls.Add(MainForm.DlgButton(T2("\u5168\u90E8"), 110, MainForm.DlgButtonSmallH,
                delegate { Fill(0, false); }));
            filters.Controls.Add(MainForm.DlgButton(T2("\u6700\u8FD1 10 \u4E2A"), 130, MainForm.DlgButtonSmallH,
                delegate { Fill(10, false); }));
            filters.Controls.Add(MainForm.DlgButton(T2("\u4EC5\u57FA\u7EBF"), 110, MainForm.DlgButtonSmallH,
                delegate { Fill(0, true); }));

            // Multi-select is a MODE, not a permanent behaviour.
            //
            // Checkboxes on every row are noise when all you want is to pick one version
            // and roll back to it. So they are off until this switch is on: single choice
            // is the normal state, and deleting a batch is something you opt into.
            CheckBox multiToggle = new CheckBox();
            multiToggle.Text = T2("\u591A\u9009");
            // Same height as the buttons beside it, and the same top margin, so the
            // baseline matches by construction. Estimating the checkbox height and
            // computing a centring margin got it wrong (it is 31px, not the 21px the
            // font suggests), and a guess that is 10px out is exactly the kind of
            // misalignment this is supposed to fix.
            multiToggle.AutoSize = false;
            multiToggle.Width = 90;
            multiToggle.Height = MainForm.DlgButtonSmallH;
            multiToggle.TextAlign = ContentAlignment.MiddleLeft;
            multiToggle.Font = new Font("Microsoft YaHei UI", 10F);
            multiToggle.Margin = new Padding(MainForm.ButtonGap * 2, 0, 0, 0);
            // Says what the switch does and, more importantly, what it does NOT do:
            // selecting several versions never changes what "现在就回退" acts on.
            new ToolTip().SetToolTip(multiToggle,
                T2("\u591A\u9009\u4EC5\u5BF9\u300C\u5220\u9664\u9009\u4E2D\u7248\u672C\u300D\u751F\u6548\uFF1A\u53EF\u52FE\u9009\u591A\u4E2A\u4E00\u6B21\u5220\u9664\u3002")
                + T2("\u300C\u73B0\u5728\u5C31\u56DE\u9000\u300D\u548C\u300C\u53EA\u8BBE\u4E3A\u4EE5\u540E\u7684\u76EE\u6807\u300D\u59CB\u7EC8\u53EA\u4F5C\u7528\u4E8E\u5F53\u524D\u9AD8\u4EAE\u7684\u90A3\u4E00\u4E2A\u3002"));
            multiToggle.CheckedChanged += delegate
            {
                try
                {
                    list.CheckBoxes = multiToggle.Checked;
                    if (!multiToggle.Checked)
                    {
                        foreach (ListViewItem it in list.Items) { it.Checked = false; }
                    }
                    UpdateSummary();
                }
                catch { }
            };
            filters.Controls.Add(multiToggle);
            multiBox = multiToggle;

            summary = new Label();
            summary.Dock = DockStyle.Bottom;
            // Height is set in Load, after AutoScaleMode.Font has scaled the font.
            summary.Padding = new Padding(MainForm.EdgePad, 0, 0, 0);
            summary.ForeColor = Theme.Muted;

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            // Checkboxes are switched on by the "多选" toggle in the filter row; off means
            // the plain single-choice list people expect when rolling back.
            list.CheckBoxes = false;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.Font = new Font("Consolas", 9.5F);
            Theme.Style(list);
            // Widths from measurement, not estimate. DumpColumns reported every one of
            // these as "<== TOO NARROW" (the widest version name needs 366px, the time
            // 186, the tag 126), so each gets its measured need plus a little slack.
            list.Columns.Add(T2("\u7248\u672C"), 400, HorizontalAlignment.Left);
            list.Columns.Add(T2("\u72B6\u6001"), 96, HorizontalAlignment.Left);
            list.Columns.Add(T2("\u6293\u53D6\u65F6\u95F4"), 200, HorizontalAlignment.Left);
            list.Columns.Add(T2("\u63D2\u4EF6\u6570"), 92, HorizontalAlignment.Right);
            list.Columns.Add(T2("\u6807\u7B7E"), 136, HorizontalAlignment.Left);
            list.DoubleClick += delegate { Accept(true); };
            list.SelectedIndexChanged += delegate { ShowBundles(); };
            list.ItemChecked += delegate { UpdateSummary(); };

            // "Contains" pane: the actual plugin names of the highlighted snapshot.
            bundlesBox = new ListBox();
            bundlesBox.Dock = DockStyle.Fill;
            bundlesBox.Font = new Font("Consolas", 9.5F);
            bundlesBox.IntegralHeight = false;

            GroupBox bundlesZone = new GroupBox();
            Theme.Style(bundlesZone);
            bundlesZone.Text = T2("\u5305\u542B\u63D2\u4EF6");
            bundlesZone.Dock = DockStyle.Fill;
            bundlesZone.Padding = new Padding(MainForm.ZonePad, 6, MainForm.ZonePad, MainForm.RowGap);
            bundlesBox.Dock = DockStyle.Fill;
            bundlesZone.Controls.Add(bundlesBox);

            // Action row: same treatment, and the buttons are wide enough for their
            // own labels instead of the 80-170px mix that made the row look ragged.
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = MainForm.DlgButtonH + 26;
            buttons.Padding = new Padding(MainForm.EdgePad, 12, MainForm.EdgePad, 12);
            buttons.WrapContents = false;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.Controls.Add(MainForm.DlgButton(T2("\u73B0\u5728\u5C31\u56DE\u9000"), 150, MainForm.DlgButtonH,
                delegate { Accept(true); }));
            buttons.Controls.Add(MainForm.DlgButton(T2("\u53EA\u8BBE\u4E3A\u4EE5\u540E\u7684\u76EE\u6807"), 220, MainForm.DlgButtonH,
                delegate { Accept(false); }));
            buttons.Controls.Add(MainForm.DlgButton(T2("\u5220\u9664\u9009\u4E2D\u7248\u672C"), 170, MainForm.DlgButtonH,
                delegate { DeleteChosen(); }));

            Button cancel = MainForm.DlgButton(T2("\u53D6\u6D88"), 110, MainForm.DlgButtonH, null);
            cancel.Margin = new Padding(0, 0, MainForm.ButtonGap, 0);   // same gap as the rest
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(cancel);

            // Zone caption, same idea as the main window: the list says what it is
            // before a single row is read.
            GroupBox listZone = new GroupBox();
            Theme.Style(listZone);
            listZone.Text = T2("\u7248\u672C\u5217\u8868");
            listZone.Dock = DockStyle.Fill;
            listZone.Padding = new Padding(MainForm.ZonePad, 6, MainForm.ZonePad, 6);
            list.Dock = DockStyle.Fill;

            listZone.Controls.Add(list);

            // Version list over plugin list, with a DRAGGABLE bar between them: how much
            // of each you want to see depends on whether you are picking a version or
            // checking what is inside one. Same SplitContainer rules as the browser --
            // the three sizing properties are set in Load, in a valid order, because
            // each setter validates against the other two at assignment time.
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterWidth = 6;
            split.Panel1.Controls.Add(listZone);
            split.Panel2.Controls.Add(bundlesZone);

            Controls.Add(split);
            Controls.Add(summary);
            Controls.Add(filters);
            Controls.Add(buttons);

            CancelButton = cancel;
            Load += delegate
            {
                Fill(0, false);
                // Post-scale heights, same reason as the browser: AutoScaleMode.Font
                // resizes the font while the form loads, so a measurement taken during
                // construction returns the unscaled line height and the text is clipped.
                hint.Height = Theme.LineHeight(hint.Font) * 2 + 8;
                summary.Height = Theme.LineHeight(summary.Font) + 6;
                MainForm.DumpColumns("picker", list, this);
                MainForm.DumpLayout("picker", this, list, bundlesBox, multiBox);
                try
                {
                    // Horizontal splitter: the measured axis is Height, and the same
                    // order applies (centre first, then the two minimums, then the
                    // wanted position clamped).
                    int h = split.Height;
                    if (h >= 300)
                    {
                        split.SplitterDistance = h / 2;
                        split.Panel1MinSize = MainForm.PanePickerListMin;
                        split.Panel2MinSize = MainForm.PanePickerBundlesMin;
                        int want = (int)(h * 0.68);
                        int max = h - split.Panel2MinSize - split.SplitterWidth;
                        if (want > max) { want = max; }
                        if (want < split.Panel1MinSize) { want = split.Panel1MinSize; }
                        split.SplitterDistance = want;
                    }
                    MainForm.ClickLog("picker split: height=" + h
                        + " distance=" + split.SplitterDistance
                        + " list=" + split.Panel1.Height + " bundles=" + split.Panel2.Height);
                }
                catch (Exception ex) { MainForm.ClickLog("picker split failed: " + ex.Message); }
            };
        }

        // Row source. dirs is already newest-first.
        private void Fill(int limit, bool onlyKnownGood)
        {
            list.BeginUpdate();
            list.Items.Clear();
            int n = 0;
            foreach (string d in dirs)
            {
                string name = Path.GetFileName(d);
                if (onlyKnownGood && name.IndexOf("known-good", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                if (limit > 0 && n >= limit) { break; }

                // The "current target" marker goes INSIDE the version cell, not in
                // the last column: that column sits against the right edge and was
                // cut off at 820px wide, hiding the one thing people look for first.
                bool isActive = (name == active);
                // "-known-good" is every name's suffix, so it is stripped out of the
                // name and shown in its own column: otherwise it repeats 20 times and
                // pushes the information that differs off the right edge.
                string tag = "";
                string shortName = name;
                int kg = name.IndexOf("-known-good", StringComparison.OrdinalIgnoreCase);
                if (kg >= 0) { shortName = name.Substring(0, kg); tag = "known-good"; }
                else { tag = T2("\u975E\u57FA\u7EBF"); }
                ListViewItem it = new ListViewItem(shortName);
                it.SubItems.Add(isActive ? T2("\u2190 \u76EE\u6807") : "");
                it.SubItems.Add(Captured(name));
                it.SubItems.Add(CountOf(d));
                it.SubItems.Add(tag);
                if (isActive) { it.Font = new Font(list.Font, FontStyle.Bold); }
                it.Tag = d;
                list.Items.Add(it);
                n++;
            }
            if (list.Items.Count > 0) { list.Items[0].Selected = true; }
            list.EndUpdate();
            UpdateSummary();
        }

        // Re-reads the snapshots directory, newest first.
        private string[] Scan()
        {
            try
            {
                string[] d = Directory.GetDirectories(snapRoot, "snap-*");
                Array.Sort(d, StringComparer.OrdinalIgnoreCase);
                Array.Reverse(d);
                return d;
            }
            catch { return new string[0]; }
        }

        private string CurrentActive()
        {
            try
            {
                string a = getActive == null ? null : getActive();
                return a == null ? "" : a;
            }
            catch { return ""; }
        }

        // snap-20261003-004123-known-good -> 2026-10-03 00:41
        private static string Captured(string name)
        {
            try
            {
                string[] parts = name.Split('-');
                if (parts.Length >= 4 && parts[1].Length == 8 && parts[2].Length == 6)
                {
                    return parts[1].Substring(0, 4) + "-" + parts[1].Substring(4, 2) + "-" + parts[1].Substring(6, 2)
                        + " " + parts[2].Substring(0, 2) + ":" + parts[2].Substring(2, 2);
                }
            }
            catch { }
            return "";
        }

        private static string CountOf(string dir)
        {
            return BundleList(dir).Length.ToString();
        }

        // The plugin names inside a snapshot's manifest, so the picker can show WHAT a
        // version contains instead of only how many things it contains. A count of "9"
        // cannot answer the only question that matters when choosing a rollback target:
        // does this version have the plugin I am trying to get rid of?
        private static string[] BundleList(string dir)
        {
            try
            {
                string man = Path.Combine(dir, "manifest.json");
                if (!File.Exists(man)) { return new string[0]; }
                string s = File.ReadAllText(man);
                int i = s.IndexOf("\"bundles\"", StringComparison.Ordinal);
                if (i < 0) { return new string[0]; }
                int a = s.IndexOf('[', i);
                int b = s.IndexOf(']', i);
                if (a < 0 || b < a) { return new string[0]; }
                string body = s.Substring(a + 1, b - a - 1);
                System.Collections.Generic.List<string> names = new System.Collections.Generic.List<string>();
                foreach (string piece in body.Split(','))
                {
                    string t = piece.Trim().Trim('"').Trim();
                    if (t.Length > 0) { names.Add(t); }
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                return names.ToArray();
            }
            catch { return new string[0]; }
        }

        // Fills the "contains" pane for the highlighted row.
        private void ShowBundles()
        {
            try
            {
                bundlesBox.Items.Clear();
                if (list.SelectedItems.Count == 0) { return; }
                string dir = list.SelectedItems[0].Tag as string;
                if (string.IsNullOrEmpty(dir)) { return; }
                string[] names = BundleList(dir);
                if (names.Length == 0)
                {
                    bundlesBox.Items.Add(T2("\uFF08\u6E05\u5355\u91CC\u6CA1\u6709\u63D2\u4EF6\uFF0C\u6216\u8BFB\u4E0D\u5230 manifest.json\uFF09"));
                    return;
                }
                foreach (string n in names) { bundlesBox.Items.Add(n); }
            }
            catch { }
        }

        private void Accept(bool restoreNow)
        {
            if (list.SelectedItems.Count == 0) { return; }
            Chosen = (string)list.SelectedItems[0].Tag;
            Mode = restoreNow ? RollbackMode.RestoreNow : RollbackMode.SetTargetOnly;
            DialogResult = DialogResult.OK;
            Close();
        }

        // Summary line doubles as the selection readout. The ticked count only appears
        // when multi-select is on, because that is the only time it means anything.
        private void UpdateSummary()
        {
            try
            {
                int ticked = list.CheckedItems.Count;
                string text = T2("\u5171 ") + list.Items.Count + T2(" \u4E2A\u7248\u672C");
                if (list.CheckBoxes)
                {
                    text += ticked > 0
                        ? T2("\uFF0C\u5DF2\u52FE\u9009 ") + ticked + T2(" \u4E2A\uFF08\u591A\u9009\u4EC5\u7528\u4E8E\u5220\u9664\uFF09")
                        : T2("\uFF0C\u52FE\u9009\u8981\u5220\u9664\u7684\u7248\u672C\uFF08\u591A\u9009\u4EC5\u7528\u4E8E\u5220\u9664\uFF09");
                }
                summary.Text = text;
            }
            catch { }
        }

        private void DeleteChosen()
        {
            // The ticked rows when multi-select is on; otherwise the highlighted row.
            // Same button either way, so there is nothing to remember.
            System.Collections.Generic.List<string> names = new System.Collections.Generic.List<string>();
            if (list.CheckBoxes && list.CheckedItems.Count > 0)
            {
                foreach (ListViewItem it in list.CheckedItems)
                {
                    string d = it.Tag as string;
                    if (!string.IsNullOrEmpty(d)) { names.Add(Path.GetFileName(d)); }
                }
            }
            else if (list.SelectedItems.Count > 0)
            {
                string d = list.SelectedItems[0].Tag as string;
                if (!string.IsNullOrEmpty(d)) { names.Add(Path.GetFileName(d)); }
            }
            if (names.Count == 0 || onDelete == null) { return; }

            int first = list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : 0;

            // This window stays OPEN after a delete, and rescans: clearing out a run of
            // old snapshots is several deletes in a row, and it used to take a reopen
            // per version. It also stays open on cancel or failure, so the list never
            // pretends something was removed when it was not.
            bool ok;
            try { ok = onDelete(names.ToArray()); }
            catch (Exception ex) { MainForm.ClickLog("picker delete threw: " + ex.Message); return; }
            if (!ok) { return; }

            dirs = Scan();
            active = CurrentActive();
            Fill(0, false);
            if (list.Items.Count > 0)
            {
                if (first >= list.Items.Count) { first = list.Items.Count - 1; }
                list.Items[first].Selected = true;
                list.Items[first].Focused = true;
            }
            MainForm.ClickLog("picker: deleted " + names.Count + " snapshot(s): "
                + string.Join(", ", names.ToArray()) + "  -> " + list.Items.Count + " rows remain");
        }

        private static string T2(string s) { return s; }
    }

    internal sealed class FolderBrowser : Form
    {
        private readonly string root;
        private string current;
        private ListView list;
        private Label pathLabel;
        private TextBox preview;
        private StatusStrip status;

        public FolderBrowser(string startDir)
        {
            root = startDir;
            current = startDir;

            Text = T2("DSH Guardian") + "  " + T2("\u6570\u636E\u76EE\u5F55");
            StartPosition = FormStartPosition.CenterScreen;   // see the picker above
            MainForm.FitToScreen(this, 1100, 640);
            MinimizeBox = false;
            Font = PickFont2();

            pathLabel = new Label();
            pathLabel.Dock = DockStyle.Top;
            pathLabel.Padding = new Padding(MainForm.EdgePad, 4, MainForm.EdgePad, 0);
            pathLabel.ForeColor = Theme.Muted;
            pathLabel.BackColor = Theme.Canvas;

            // Same button geometry as the picker's rows (MainForm.DlgButton): the jump
            // bar used to be 26-30px buttons at hand-computed x offsets.
            FlowLayoutPanel jumps = new FlowLayoutPanel();
            jumps.Dock = DockStyle.Top;
            jumps.Height = MainForm.DlgButtonSmallH + 20;
            jumps.Padding = new Padding(MainForm.EdgePad, 8, MainForm.EdgePad, 6);
            jumps.WrapContents = false;
            jumps.Controls.Add(MainForm.DlgButton(T2("\u6570\u636E\u6839\u76EE\u5F55"), 140, MainForm.DlgButtonSmallH,
                delegate { Go(root); }));
            jumps.Controls.Add(MainForm.DlgButton(T2("\u5FEB\u7167"), 110, MainForm.DlgButtonSmallH,
                delegate { Go(Path.Combine(root, "snapshots")); }));
            jumps.Controls.Add(MainForm.DlgButton(T2("\u8BCA\u65AD\u62A5\u544A"), 130, MainForm.DlgButtonSmallH,
                delegate { Go(Path.Combine(root, T2("\u8BCA\u65AD\u62A5\u544A"))); }));
            jumps.Controls.Add(MainForm.DlgButton(T2("\u5237\u65B0"), 110, MainForm.DlgButtonSmallH,
                delegate { Fill(); }));

            // SplitContainer this time, with a DRAGGABLE bar.
            //
            // A fixed-percentage TableLayoutPanel was used before because two attempts
            // to set SplitterDistance threw or landed in the wrong place. The fix for
            // that is not to avoid the control, it is to assign the distance after the
            // control has its real width and to clamp it:
            //   SplitterDistance must be between Panel1MinSize and Width-Panel2MinSize,
            // and both are validated against the CURRENT width at assignment time.
            // With that done, the user gets what they asked for: a boundary they can
            // drag, instead of one the program decides.
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            split.SplitterWidth = 6;
            // Panel1MinSize / Panel2MinSize / SplitterDistance are deliberately NOT set
            // here. Each setter validates against the CURRENT width and the current
            // SplitterDistance, and inside the constructor the control is still 150px
            // wide with SplitterDistance 50, so Panel2MinSize = 200 throws:
            //   "SplitterDistance 必须在 Panel1MinSize 和 Width - Panel2MinSize 之间"
            // They are applied in Load, in the order that keeps every intermediate state
            // valid (see the Load handler below).

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.Font = new Font("Consolas", 9.5F);
            Theme.Style(list);
            list.Columns.Add(T2("\u540D\u79F0"), 440, HorizontalAlignment.Left);
            list.Columns.Add(T2("\u5927\u5C0F"), 84, HorizontalAlignment.Right);
            list.Columns.Add(T2("\u4FEE\u6539\u65F6\u95F4"), 140, HorizontalAlignment.Left);
            list.DoubleClick += delegate { OpenSelected(); };
            list.SelectedIndexChanged += delegate { Preview2(); };
            list.KeyDown += delegate(object s2, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; }
                if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
            };

            preview = new TextBox();
            preview.Dock = DockStyle.Fill;
            preview.Multiline = true;
            preview.ReadOnly = true;
            preview.ScrollBars = ScrollBars.Both;
            preview.WordWrap = false;
            preview.Font = new Font("Consolas", 9F);
            preview.BackColor = Theme.ConsoleBg;
            preview.ForeColor = Theme.ConsoleFg;
            preview.BorderStyle = BorderStyle.None;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = MainForm.DlgButtonH + 26;
            buttons.Padding = new Padding(MainForm.EdgePad, 12, MainForm.EdgePad, 12);
            buttons.WrapContents = false;

            Button up = MainForm.DlgButton(T2("\u4E0A\u4E00\u5C42"), 130, MainForm.DlgButtonH, delegate { GoUp(); });
            Button open = MainForm.DlgButton(T2("\u6253\u5F00"), 130, MainForm.DlgButtonH, delegate { OpenSelected(); });
            Button closeB = MainForm.DlgButton(T2("\u5173\u95ED"), 130, MainForm.DlgButtonH, null);
            closeB.DialogResult = DialogResult.Cancel;
            closeB.Margin = new Padding(0, 0, MainForm.ButtonGap, 0);   // same gap as the rest
            buttons.Controls.Add(up);
            buttons.Controls.Add(open);
            buttons.Controls.Add(closeB);

            status = new StatusStrip();
            status.SizingGrip = false;
            ToolStripStatusLabel sl = new ToolStripStatusLabel();
            sl.Name = "info";
            status.Items.Add(sl);
            status.Dock = DockStyle.Bottom;

            // Caption each pane, so the two halves of this window say what they are
            // without the user inferring it from the content.
            GroupBox filesZone = new GroupBox();
            Theme.Style(filesZone);
            filesZone.Text = T2("\u6587\u4EF6");
            filesZone.Dock = DockStyle.Fill;
            filesZone.Padding = new Padding(MainForm.ZonePad, 6, MainForm.ZonePad, MainForm.RowGap);
            list.Dock = DockStyle.Fill;
            filesZone.Controls.Add(list);

            GroupBox previewZone = new GroupBox();
            Theme.Style(previewZone);
            previewZone.Text = T2("\u9884\u89C8");
            previewZone.Dock = DockStyle.Fill;
            previewZone.Padding = new Padding(MainForm.ZonePad, 6, MainForm.ZonePad, MainForm.RowGap);
            preview.Dock = DockStyle.Fill;
            previewZone.Controls.Add(preview);

            split.Panel1.Controls.Add(filesZone);
            split.Panel2.Controls.Add(previewZone);
            Controls.Add(split);
            Controls.Add(buttons);
            Controls.Add(jumps);
            Controls.Add(pathLabel);
            Controls.Add(status);

            // One button to give the preview the whole window and one to go back, so the
            // pane is enlargeable without asking the user to find a 6px drag handle.
            Button zoom = MainForm.DlgButton(T2("\u9884\u89C8\u653E\u5927"), 150, MainForm.DlgButtonH, null);
            zoom.Margin = new Padding(30, 0, MainForm.ButtonGap, 0);
            zoom.Click += delegate
            {
                try
                {
                    split.Panel1Collapsed = !split.Panel1Collapsed;
                    zoom.Text = split.Panel1Collapsed
                        ? T2("\u8FD4\u56DE\u5217\u8868") : T2("\u9884\u89C8\u653E\u5927");
                }
                catch { }
            };
            buttons.Controls.Add(zoom);

            // Distance is assigned here, not in the constructor: the setter validates
            // against the control's CURRENT width, which is not final until the form is
            // laid out. Clamped on both sides so it can never throw.
            Load += delegate
            {
                Fill();
                // Heights are set HERE, not in the constructor.
                //
                // AutoScaleMode.Font scales the form's font when the form loads, so a
                // measurement taken while the form is being built returns the unscaled
                // line height (18px) while the text is drawn later at the scaled one
                // (31px at 144 DPI). That mismatch is why the path line was clipped.
                pathLabel.Height = Theme.LineHeight(pathLabel.Font) + 10;
                MainForm.DumpColumns("browser", list, this);
                MainForm.DumpLayout("browser", this, pathLabel, jumps, list, preview);
                try
                {
                    // Order matters. Every one of these setters validates against the
                    // value it is NOT setting, so each step must leave a state the next
                    // one accepts:
                    //   1. centre the splitter  -> valid against the default 25/25 mins
                    //   2. Panel1MinSize = 260  -> needs distance >= 260 and <= W - 25
                    //   3. Panel2MinSize = 200  -> needs distance <= W - 200
                    //   4. final 60% position, clamped
                    int w = split.Width;
                    if (w < 560)
                    {
                        MainForm.ClickLog("browser split: window too narrow for two panes (" + w + "), left at default");
                    }
                    else
                    {
                        split.SplitterDistance = w / 2;
                        split.Panel1MinSize = MainForm.PaneListMin;
                        split.Panel2MinSize = MainForm.PaneDetailMin;
                        int want = (int)(w * 0.6);
                        int max = w - split.Panel2MinSize - split.SplitterWidth;
                        if (want > max) { want = max; }
                        if (want < split.Panel1MinSize) { want = split.Panel1MinSize; }
                        split.SplitterDistance = want;
                    }
                    MainForm.ClickLog("browser split: width=" + w
                        + " distance=" + split.SplitterDistance
                        + " panel1=" + split.Panel1.Width + " panel2=" + split.Panel2.Width);
                }
                catch (Exception ex) { MainForm.ClickLog("browser split failed: " + ex.Message); }
            };
            CancelButton = closeB;
        }

        private static Font PickFont2()
        {
            try
            {
                Font f = new Font("Microsoft YaHei UI", 9.75F);
                if (f.FontFamily.Name == "Microsoft YaHei UI") { return f; }
            }
            catch { }
            return new Font(FontFamily.GenericSansSerif, 10F);
        }

        private void Go(string dir)
        {
            if (dir == null) { return; }
            if (!Directory.Exists(dir)) { SetStatus(T2("\u8FD9\u4E2A\u76EE\u5F55\u8FD8\u4E0D\u5B58\u5728\uFF1A") + dir); return; }
            current = dir;
            Fill();
        }


        private void Fill()
        {
            pathLabel.Text = current;
            list.BeginUpdate();
            list.Items.Clear();
            preview.Text = "";

            int dirsN = 0, filesN = 0;
            try
            {
                if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                {
                    ListViewItem upItem = new ListViewItem("..");
                    upItem.SubItems.Add("");
                    upItem.SubItems.Add("");
                    upItem.Tag = null;
                    list.Items.Add(upItem);
                }

                string[] dirs = Directory.GetDirectories(current);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                foreach (string d in dirs)
                {
                    DirectoryInfo di = new DirectoryInfo(d);
                    ListViewItem it = new ListViewItem("[" + di.Name + "]");
                    it.SubItems.Add("");
                    it.SubItems.Add(di.LastWriteTime.ToString("MM-dd HH:mm"));
                    it.Tag = d;
                    it.ForeColor = Color.FromArgb(30, 90, 170);
            it.ForeColor = Theme.Accent;
                    dirsN++;
                }

                string[] files = Directory.GetFiles(current);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string fl in files)
                {
                    FileInfo fi = new FileInfo(fl);
                    ListViewItem it = new ListViewItem(fi.Name);
                    it.SubItems.Add(FormatSize(fi.Length));
                    it.SubItems.Add(fi.LastWriteTime.ToString("MM-dd HH:mm"));
                    it.Tag = fl;
                    list.Items.Add(it);
                    filesN++;
                }
            }
            catch (Exception ex) { preview.Text = ex.Message; }

            list.EndUpdate();
            if (list.Items.Count > 0) { list.Items[0].Selected = true; }
            SetStatus(dirsN + T2(" \u4E2A\u76EE\u5F55\uFF0C") + filesN + T2(" \u4E2A\u6587\u4EF6"));
        }

        private static string FormatSize(long n)
        {
            if (n < 1024) { return n + " B"; }
            if (n < 1024 * 1024) { return (n / 1024) + " KB"; }
            return (n / 1024 / 1024) + " MB";
        }

        private void SetStatus(string s)
        {
            try { status.Items["info"].Text = s; } catch { }
        }

        private string SelectedPath()
        {
            if (list.SelectedItems.Count == 0) { return null; }
            return list.SelectedItems[0].Tag as string;
        }

        private void GoUp()
        {
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) { return; }
            string parent = Path.GetDirectoryName(current);
            if (parent != null) { Go(parent); }
        }

        private void OpenSelected()
        {
            string p = SelectedPath();
            if (p == null) { GoUp(); return; }
            try
            {
                if (Directory.Exists(p)) { Go(p); return; }
                if (File.Exists(p))
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = p;
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    SetStatus(T2("\u5DF2\u4EA4\u7ED9\u7CFB\u7EDF\u6253\u5F00\uFF1A") + Path.GetFileName(p));
                }
            }
            catch (Exception ex) { SetStatus(T2("\u6253\u5F00\u5931\u8D25\uFF1A") + ex.Message); }
        }

        private void Preview2()
        {
            string p = SelectedPath();
            if (p == null || !File.Exists(p)) { preview.Text = ""; return; }
            try
            {
                FileInfo fi = new FileInfo(p);
                if (fi.Length == 0) { preview.Text = T2("\uFF08\u7A7A\u6587\u4EF6\uFF09"); return; }
                if (fi.Length > 200000)
                {
                    preview.Text = T2("\uFF08\u6587\u4EF6\u8F83\u5927\uFF0C\u4E0D\u9884\u89C8\uFF09") + "  "
                        + FormatSize(fi.Length) + Environment.NewLine + p;
                    return;
                }
                preview.Text = File.ReadAllText(p);
            }
            catch (Exception ex) { preview.Text = ex.Message; }
        }

        private static string T2(string s) { return s; }
    }
}
