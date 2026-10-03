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

    // RECT, GetWindowRect, SetWindowPos and the three SWP_ flags used to live here.
    //
    // They were kept from the attempt to resize the console window to fit the menu.
    // That approach was abandoned (it desynchronised the console buffer and the
    // window, which produced the wrapped and overwritten rows), and the drawing code
    // was replaced by atomic per-row writes that do not need it. Nothing has called
    // them since: GetWindowRect appeared once in the file -- its own declaration --
    // and SetWindowPos only in a comment explaining why it is gone, while each SWP_
    // constant appeared exactly once. Removed together with the RECT struct, which
    // existed only to be their out-parameter.
    //
    // Re-add from git history if a future change genuinely needs to move the console
    // window; do not leave them declared "just in case".

    // ------------------------------------------------------------ mouse input
    //
    // Console.ReadKey cannot see the mouse at all, so the menu reads the raw
    // console input buffer instead. Y coordinates come back in buffer rows, and
    // the menu is the only thing drawn above the prompt, so "which row was
    // clicked" maps straight onto "which item" -- see ReadMenuKey.
    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    // INPUT_RECORD is exactly 20 bytes in Win32: a 2-byte event type, 2 bytes of
    // alignment, then the 16-byte event union. Declaring an extra pad field made
    // it 24 (measured with Marshal.SizeOf) and every Peek/ReadConsoleInput call
    // then failed, which silently disabled the whole menu loop -- the menu drew
    // once and the process sat there. Field order alone reproduces the layout.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_RECORD
    {
        public ushort EventType;
        public MOUSE_EVENT_RECORD MouseEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSE_EVENT_RECORD
    {
        public COORD MousePosition;
        public uint ButtonState;
        public uint ControlKeyState;
        public uint EventFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekConsoleInput(IntPtr hConsoleInput, [Out] INPUT_RECORD[] buffer, uint length, out uint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadConsoleInput(IntPtr hConsoleInput, [Out] INPUT_RECORD[] buffer, uint length, out uint read);

    private const uint ENABLE_MOUSE_INPUT = 0x0010;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    private const int STD_INPUT_HANDLE = -10;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    // The console input buffer must be opened by NAME, not taken from the std
    // handle. Measured on this machine: with stdin redirected (which is the case
    // for any launch whose stdio we did not create ourselves) GetStdHandle
    // returned a handle that GetConsoleMode rejects with "invalid handle" (err 6),
    // so SetConsoleMode never turned mouse reporting on and no mouse event ever
    // reached the program -- the menu looked alive and ignored the mouse.
    // "CONIN$" always names the real console, whatever stdio was redirected to.
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    private static IntPtr GetConsoleInputHandle()
    {
        try
        {
            IntPtr h = CreateFileW("CONIN$", GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h != IntPtr.Zero && h != new IntPtr(-1)) { return h; }
        }
        catch { }
        try { return GetStdHandle(STD_INPUT_HANDLE); } catch { return IntPtr.Zero; }
    }

    // Same lesson on the output side, and it was hiding everything: with stdout
    // redirected (any launch whose stdio we did not create) the inherited stdout
    // handle is not a console, so Console.Out is not a console writer and every
    // line vanished -- no menu, no logs, no trace. "CONOUT$" is the real console
    // screen no matter what stdout points at.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteConsoleW(IntPtr h, string text, uint chars, out uint written, IntPtr reserved);

    private static IntPtr GetConsoleOutputHandle()
    {
        try
        {
            IntPtr h = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h != IntPtr.Zero && h != new IntPtr(-1)) { return h; }
        }
        catch { }
        return IntPtr.Zero;
    }

    // Set once at startup. When it is valid, all screen output goes through it and
    // Console.ForegroundColor is bypassed (a control call on a console that is not
    // ours is what used to throw).
    private static IntPtr ConsoleOut = IntPtr.Zero;

    private static void WriteOut(string text, ConsoleColor? color)
    {
        if (text == null) { text = ""; }
        if (ConsoleOut != IntPtr.Zero)
        {
            try
            {
                if (color.HasValue)
                {
                    try { Console.ForegroundColor = color.Value; } catch { }
                }
                uint written;
                WriteConsoleW(ConsoleOut, text, (uint)text.Length, out written, IntPtr.Zero);
                if (color.HasValue)
                {
                    try { Console.ResetColor(); } catch { }
                }
                return;
            }
            catch { }
        }
        try { Console.Write(text); } catch { }
    }

    private static void WriteOutLine(string text, ConsoleColor? color)
    {
        WriteOut(text + Environment.NewLine, color);
    }

    // --------------------------------------------------------- frame rendering
    //
    // Everything on screen is composed into ONE string and written with ONE call.
    // Writing the menu line by line is what produced the garbled screen the user
    // photographed ("t 选择", "3 基线" twice, the first item's note on the status
    // line): each WriteConsoleW can land at a wrapped or scrolled position, so 20+
    // independent writes only need one of them to be off for the whole frame to
    // fall apart. A single call cannot interleave with itself.
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    private static bool VtOk = false;

    private static void EnableVt()
    {
        try
        {
            IntPtr h = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) { return; }
            uint mode;
            if (!GetConsoleMode(h, out mode)) { return; }
            VtOk = SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
        catch { VtOk = false; }
    }

    // Col() and Inv() used to sit here. Both were no-ops that returned their argument
    // unchanged -- leftovers from the ANSI-escape revision, kept "in case" the console
    // colour work came back. Nothing called them, and a function whose entire body is
    // "return text" is a place where a reader expects behaviour to be happening.

    // ------------------------------------------------------- atomic row drawing
    //
    // Per-row primitives. The previous revision wrote the whole frame through the
    // stream and carried colour in ANSI escapes; the escape numbers were wrong
    // (ESC[37m is white, i.e. invisible on a white console) and leaked, so rows
    // came out magenta. These two calls draw ONE row of characters and then ONE
    // row of attributes -- two atomic operations per line, so no amount of mouse
    // traffic can smear text across rows, and no escape sequence is involved.
    [StructLayout(LayoutKind.Sequential)]
    private struct CHAR_INFO
    {
        public char UnicodeChar;
        public ushort Attributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteConsoleOutputCharacterW(IntPtr h, string text, uint length,
        COORD pos, out uint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FillConsoleOutputAttribute(IntPtr h, ushort attr, uint length,
        COORD pos, out uint written);

    private const ushort ATTR_NORMAL = 0x0007;   // light grey on black
    private const ushort ATTR_DIM = 0x0008;   // dark grey
    private const ushort ATTR_HILITE = 0x0003;   // cyan on black
    private const ushort ATTR_WARN = 0x000E;   // yellow
    private const ushort ATTR_OK = 0x000A;   // green
    private const ushort ATTR_TITLE = 0x000B;   // light cyan
    private const ushort ATTR_SELECT = 0x0030;   // black on cyan

    private static ushort AttrOf(ConsoleColor c)
    {
        switch (c)
        {
            case ConsoleColor.DarkGray: return ATTR_DIM;
            case ConsoleColor.Gray: return 0x0007;
            case ConsoleColor.White: return 0x000F;
            case ConsoleColor.Cyan: return ATTR_HILITE;
            case ConsoleColor.DarkCyan: return ATTR_HILITE;
            case ConsoleColor.Green: return ATTR_OK;
            case ConsoleColor.Yellow: return ATTR_WARN;
            default: return ATTR_NORMAL;
        }
    }

    // Pins the console cursor to the frame origin. Row drawing does not move the
    // cursor, so without this the caret kept its old position, the console scrolled
    // to follow it, and the top of the frame slid off screen.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCursorPosition(IntPtr h, COORD pos);

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_CURSOR_INFO
    {
        public uint dwSize;
        public bool bVisible;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCursorInfo(IntPtr h, ref CONSOLE_CURSOR_INFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left; public short Top; public short Right; public short Bottom;
    }

    // DrawRow writes into the buffer at fixed coordinates but does not move the
    // viewport, so once the console had scrolled (startup output, an action) the
    // frame was drawn above the visible area. HomeCursor is what pins it now;
    // ScrollTop did the same job through SetConsoleWindowInfo and had no callers left.
    private static void HomeCursor()
    {
        if (ConsoleOut == IntPtr.Zero) { return; }
        try
        {
            COORD p = new COORD();
            p.X = 0; p.Y = 0;
            SetConsoleCursorPosition(ConsoleOut, p);
            CONSOLE_CURSOR_INFO ci = new CONSOLE_CURSOR_INFO();
            ci.dwSize = 1;
            ci.bVisible = false;
            SetConsoleCursorInfo(ConsoleOut, ref ci);
        }
        catch { }
    }
    // ---- decisive screen primitives -------------------------------------------
    // Everything below replaces the piecemeal drawing that kept producing ghosts
    // (three rows highlighted at once) and leftover text. The rules are:
    //   1. Clear the WHOLE screen with ONE attribute first. Any previous frame is
    //      gone by construction, so nothing can survive.
    //   2. Draw each line's text with ONE call at an absolute row.
    //   3. Draw the highlighted row LAST with a second call. Exactly one row can
    //      be highlighted, because only one row is ever passed to Fill-Screen.
    private static int ScreenW()
    {
        int w = 80;
        try { w = Console.WindowWidth; } catch { }
        if (w < 40) { w = 40; }
        if (w > 100) { w = 100; }
        return w;
    }

    private static void ClearScreen(ushort attr)
    {
        if (ConsoleOut == IntPtr.Zero) { return; }
        try
        {
            int w = ScreenW();
            int h = 30;
            try { h = Console.WindowHeight; } catch { }
            string blank = new string(' ', w);
            uint done;
            for (int y = 0; y < h; y++)
            {
                COORD p = new COORD();
                p.X = 0; p.Y = (short)y;
                WriteConsoleOutputCharacterW(ConsoleOut, blank, (uint)blank.Length, p, out done);
                FillConsoleOutputAttribute(ConsoleOut, attr, (uint)blank.Length, p, out done);
            }
        }
        catch { }
    }

    private static void PutLine(int y, string text, ushort attr)
    {
        if (ConsoleOut == IntPtr.Zero || text == null) { return; }
        try
        {
            int w = ScreenW();
            if (text.Length > w) { text = text.Substring(0, w); }
            uint done;
            COORD p = new COORD();
            p.X = 0; p.Y = (short)y;
            WriteConsoleOutputCharacterW(ConsoleOut, text, (uint)text.Length, p, out done);
            FillConsoleOutputAttribute(ConsoleOut, attr, (uint)text.Length, p, out done);
        }
        catch { }
    }

    private static void FillRowBand(int y, ushort attr)
    {
        if (ConsoleOut == IntPtr.Zero) { return; }
        try
        {
            int w = ScreenW();
            uint done;
            COORD p = new COORD();
            p.X = 0; p.Y = (short)y;
            FillConsoleOutputAttribute(ConsoleOut, attr, (uint)w, p, out done);
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleWindowInfo(IntPtr h, bool absolute, ref SMALL_RECT rect);

    // Scrolls the visible window so its top row is buffer row 0. Drawing targets
    // absolute buffer rows, so without this the menu can be written above the
    // visible area and the top of the frame is cut off.
    //
    // ScrollTop() used to sit next to this with a byte-identical body and no callers.
    private static void PinViewTop()
    {
        if (ConsoleOut == IntPtr.Zero) { return; }
        try
        {
            int h = 25;
            try { h = Console.WindowHeight; } catch { }
            SMALL_RECT r = new SMALL_RECT();
            r.Left = 0; r.Top = 0; r.Right = 120; r.Bottom = (short)(h - 1);
            SetConsoleWindowInfo(ConsoleOut, true, ref r);
        }
        catch { }
    }
    private static void DrawRow(int y, string text, ushort attr, int width)
    {
        if (ConsoleOut == IntPtr.Zero || text == null) { return; }
        try
        {
            if (text.Length < width) { text = text.PadRight(width); }
            if (text.Length > width) { text = text.Substring(0, width); }
            uint done;
            COORD p = new COORD();
            p.X = 0; p.Y = (short)y;
            WriteConsoleOutputCharacterW(ConsoleOut, text, (uint)text.Length, p, out done);
            FillConsoleOutputAttribute(ConsoleOut, attr, (uint)text.Length, p, out done);
        }
        catch { }
    }


    // WriteFrame() used to sit here, writing one composed frame through a single
    // WriteConsoleW call. The frame renderer draws row by row through DrawRow now
    // (one row of characters, then one row of attributes), so the whole-frame path
    // had no callers. WriteConsoleW is still declared: DrawRow uses it.
    private const ushort MOUSE_EVENT = 0x0002;
    private const uint MOUSE_MOVED = 0x0001;
    private const uint FROM_LEFT_1ST_BUTTON_PRESSED = 0x0001;

    // Turns on window-mouse reporting. QuickEdit is switched OFF because while it
    // is on, any click starts a text selection and the application never sees the
    // mouse -- the classic reason "mouse support" appears dead in a console. The
    // flag combination is applied in one call: Windows ignores
    // ENABLE_QUICK_EDIT_MODE unless ENABLE_EXTENDED_FLAGS accompanies it.
    private static void EnableConsoleMouse()
    {
        try
        {
            IntPtr h = GetConsoleInputHandle();
            uint mode;
            if (!GetConsoleMode(h, out mode)) { return; }
            mode |= ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS;
            mode &= ~ENABLE_QUICK_EDIT_MODE;
            SetConsoleMode(h, mode);
        }
        catch { }
    }

    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_YESNO = 0x4;
    private const uint MB_ICONWARNING = 0x30;
    private const uint MB_ICONQUESTION = 0x20;
    private const uint MB_YESNOCANCEL = 0x3;
    private const int IDCANCEL = 2;
    private const int IDYES = 6;

    // True when launched by the GUI shell: no console window, no console output.
    private static bool Quiet = false;

    // Named constants, so no timing value appears inline in the logic.
    //   KeyWaitTimeoutSeconds  how long "press any key" waits before giving up, so a
    //                          redirected stdin cannot hang the console build forever
    private const int KeyWaitTimeoutSeconds = 120;
    //   MinConsoleBufferHeight  the scrollback the console build keeps available
    private const int MinConsoleBufferHeight = 400;

    // True while this process is the watcher (watch-loop). Every message box in this exe
    // is behind this flag: a watcher runs unattended for hours, so a dialog is not a
    // notification, it is a hang -- it blocks the loop until somebody notices a window
    // they never asked for. Non-interactive runs report to the log instead.
    private static bool NoDialogs = false;

    private static string BaseDir
    {
        get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
    }

    private static string WatchdogPath { get { return Path.Combine(BaseDir, "dsh-watchdog.ps1"); } }
    private static string SnapshotPath { get { return Path.Combine(BaseDir, "dsh-snapshot.ps1"); } }
    private static string DiagnosticsPath { get { return Path.Combine(BaseDir, "collect-diagnostics.ps1"); } }
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
        if (Quiet) { Trace("quiet: " + text); return; }
        if (ConsoleOut != IntPtr.Zero)
        {
            WriteOutLine(text, null);
            return;
        }
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
        // WriteOut already knows how to paint when it owns the console handle.
        if (ConsoleOut != IntPtr.Zero) { WriteOutLine(text, color); return; }
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

    // SayRule() used to sit here, drawing a run of dashes. The frame renderer took over
    // every rule in the layout and nothing called it any more.

    // Say() ends the line, which is wrong for the left half of a label/value
    // pair: the first version used it for both halves and every row came out as
    // "label" / "value" on two lines.
    private static void SayPart(string text)
    {
        if (ConsoleOut != IntPtr.Zero)
        {
            WriteOut(text, null);
            return;
        }
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
    //
    // The SayRow() helper that came out of that revision was replaced by the frame
    // renderer; this comment is kept because it explains the column budget the
    // renderer still uses.

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
        // Every verb runs inside this guard, because a winexe has nowhere to print.
        //
        // Found the hard way: `dsh-guardian-console.exe shortcut` was crashing with an
        // unhandled managed exception (event log 0xe0434352 in KERNELBASE), and the user
        // saw nothing at all -- no window, no message, no shortcut. A silent crash is
        // the worst possible failure mode for a maintenance verb, so the exception now
        // lands in data\console-crash.log with its type, message and stack.
        try
        {
            return RunMain(args);
        }
        catch (Exception ex)
        {
            try
            {
                string dir = Path.Combine(BaseDir, "..", "data");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "console-crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + "  verb=" + ((args != null && args.Length > 0) ? args[0] : "(none)")
                    + Environment.NewLine + ex.ToString() + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
            return 3;
        }
    }

    private static int RunMain(string[] args)
    {
        string verb = (args != null && args.Length > 0) ? args[0].TrimStart('-', '/').ToLowerInvariant() : null;

        // Silence every message box for the watcher, and do it HERE.
        //
        // The path check further down raises an error dialog when the scripts are
        // missing, and it runs before the verb switch -- so setting this flag inside
        // `case "watch-loop"` would be too late and the dialog would already be on
        // screen. A watcher is unattended by definition; a modal box it raises is not a
        // notification, it is a hang: the loop waits behind it for a click that never
        // comes, and monitoring stops without a word.
        NoDialogs = verb == "watch-loop";

        // -Quiet is passed by the GUI shell when it runs the version picker here:
        // this process manipulates state and shows its own dialogs, and must not
        // leave a console window sitting behind the GUI window.
        Quiet = args != null && Array.Exists(args, delegate(string a)
        {
            return a != null && a.TrimStart('-', '/').Equals("quiet", StringComparison.OrdinalIgnoreCase);
        });

        // Interactive use (a double-click, where no console exists because this
        // is a winexe) gets one allocated so the user can actually see the menu.
        if (!HasConsole && !Quiet)
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
            ConsoleOut = GetConsoleOutputHandle();
            EnableVt();
            PinViewTop();   // scroll the viewport to the top exactly once
            // The allocated console comes up about 80 columns wide, while the
            // Layout adapts to whatever width the console actually has, so nothing
            // needs to be resized here. An earlier revision resized the window with
            // SetWindowPos; that left the console buffer and the window out of step
            // and every long line wrapped and overwrote its neighbour, which is the
            // garbled screen users saw. Buffer growth stays (cheap, and it only ever
            // adds scrollback); the window is left alone.
            try
            {
                if (Console.BufferWidth < 100) { Console.BufferWidth = 100; }
                if (Console.BufferHeight < MinConsoleBufferHeight) { Console.BufferHeight = MinConsoleBufferHeight; }
            }
            catch { }
        }
        EnableConsoleMouse();
        try { Console.Title = "DSH Guardian"; } catch { }

        if (!File.Exists(WatchdogPath) || !File.Exists(SnapshotPath))
        {
            Say("FATAL: dsh-watchdog.ps1 / dsh-snapshot.ps1 not found next to this exe.");
            Say("       expected in: " + BaseDir);
            // No dialog when this process is (or is about to become) the watcher.
            //
            // This exe hosts watch-loop, which stays up for hours with nobody in front of
            // it. A modal box raised here would sit there undismissed and the loop would
            // never start -- monitoring stopped, silently, because of a dialog nobody
            // asked for and nobody can see fit to close. The condition is already reported
            // above and in the log; a window adds nothing an absent user can act on.
            if (!NoDialogs)
            {
                try
                {
                    MessageBoxW(IntPtr.Zero,
                        "dsh-watchdog.ps1 / dsh-snapshot.ps1 were not found next to this exe.\n\nExpected in:\n" + BaseDir,
                        "DSH Guardian", MB_OK | MB_ICONERROR);
                }
                catch { }
            }
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
            case "probe": return ConsoleProbe();
            // The resident watcher, hosted in C# instead of in a long-lived PowerShell.
            case "watch-loop": return WatchLoop(args);
            // Runs the diagnostic collector through the exe rather than through the
            // .cmd wrapper, so the desktop shortcut can point at THIS executable.
            //
            // Why it matters: when a shortcut's target is not the app's own exe,
            // Windows ignores IconLocation and draws the target type's generic icon.
            // Measured on the desktop shortcut whose target was launch-diagnostics.cmd:
            // 29 blue pixels against 565 white -- a blank page -- while the identical
            // IconLocation on an exe target gave 764 blue. Same icon source, same file,
            // only the target differed. Pointing at the exe is the fix; the script still
            // lives in its own file and is invoked by path.
            case "diagnostics":
            case "diag": return Exec(DiagnosticsPath, "");
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
        Say("  dsh-guardian.exe diagnostics collect a diagnostic report (as the shortcut does)");
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
            Exec(WatchdogPath, "-Pause");
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
            Exec(WatchdogPath, "-Pause");
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

    // -------------------------------------------------------------- probe verb
    //
    // Acceptance harness for the mouse work. Guessing at coordinate systems from
    // screenshots wasted two rounds; this prints the real numbers the console
    // reports (buffer/window/cursor geometry, whether mouse mode actually turned
    // on) and then records every mouse and key event with raw coordinates for a
    // few seconds. Run: dsh-guardian.exe probe [seconds]
    private static int ConsoleProbe()
    {
        int seconds = 8;
        try { if (EnvSecond != null) { int.TryParse(EnvSecond, out seconds); } } catch { }
        if (seconds <= 0) { seconds = 8; }

        string file = Path.Combine(DataDir, "probe.log");
        try { Directory.CreateDirectory(DataDir); } catch { }
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("=== DSH Guardian console probe " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");

        EnableConsoleMouse();
        sb.AppendLine("HasConsole          = " + HasConsole);
        try { sb.AppendLine("IsInputRedirected   = " + Console.IsInputRedirected); } catch { }
        try { sb.AppendLine("IsOutputRedirected  = " + Console.IsOutputRedirected); } catch { }
        try { sb.AppendLine("BufferWidth/Height  = " + Console.BufferWidth + " x " + Console.BufferHeight); } catch { }
        try { sb.AppendLine("WindowWidth/Height  = " + Console.WindowWidth + " x " + Console.WindowHeight); } catch { }
        try { sb.AppendLine("WindowLeft/Top      = " + Console.WindowLeft + " , " + Console.WindowTop); } catch { }
        try { sb.AppendLine("CursorLeft/Top      = " + Console.CursorLeft + " , " + Console.CursorTop); } catch { }

        IntPtr hIn = GetConsoleInputHandle();
        sb.AppendLine("stdin handle        = " + hIn);
        uint mode = 0;
        if (GetConsoleMode(hIn, out mode))
        {
            sb.AppendLine("console input mode  = 0x" + mode.ToString("X4")
                + "  MOUSE=" + ((mode & ENABLE_MOUSE_INPUT) != 0)
                + " QUICKEDIT=" + ((mode & ENABLE_QUICK_EDIT_MODE) != 0));
        }
        else { sb.AppendLine("GetConsoleMode FAILED, err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error()); }

        // Draw five marked rows exactly the way the menu does, then read back the
        // cursor so the recorded rows and the mouse's reported rows can be
        // compared directly.
        try { Console.Clear(); Console.SetCursorPosition(0, 0); } catch { }
        Say("probe: five marked rows follow (they must line up with the numbers)");
        int top = -1;
        int[] rows = new int[5];
        try { top = Console.CursorTop; } catch { }
        for (int i = 0; i < 5; i++)
        {
            try { rows[i] = Console.CursorTop; } catch { }
            Say("   row" + i + "   <-- marker " + i);
        }
        int bottom = -1;
        try { bottom = Console.CursorTop; } catch { }
        sb.AppendLine("drawn top row        = " + top);
        sb.AppendLine("drawn item rows      = " + rows[0] + "," + rows[1] + "," + rows[2] + "," + rows[3] + "," + rows[4]);
        sb.AppendLine("cursor after draw    = " + bottom);

        sb.AppendLine("--- now recording input for " + seconds + "s; move the mouse over the rows and click ---");
        DateTime deadline = DateTime.Now.AddSeconds(seconds);
        INPUT_RECORD[] recs = new INPUT_RECORD[16];
        int events = 0;
        while (DateTime.Now < deadline)
        {
            uint read;
            bool got = false;
            try { got = PeekConsoleInput(hIn, recs, (uint)recs.Length, out read) && read > 0; }
            catch (Exception ex) { sb.AppendLine("PeekConsoleInput threw: " + ex.Message); break; }
            if (!got) { System.Threading.Thread.Sleep(20); continue; }

            uint n;
            if (!ReadConsoleInput(hIn, recs, (uint)recs.Length, out n)) { break; }
            for (int i = 0; i < n; i++)
            {
                events++;
                if (events > 400) { break; }
                if (recs[i].EventType == MOUSE_EVENT)
                {
                    sb.AppendLine("MOUSE x=" + recs[i].MouseEvent.MousePosition.X
                        + " y=" + recs[i].MouseEvent.MousePosition.Y
                        + " btn=0x" + recs[i].MouseEvent.ButtonState.ToString("X4")
                        + " flags=0x" + recs[i].MouseEvent.EventFlags.ToString("X4"));
                }
                else if (recs[i].EventType == 0x0001)
                {
                    sb.AppendLine("KEY   eventType=0x0001");
                }
            }
        }
        sb.AppendLine("events recorded      = " + events);
        try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        try { Console.WriteLine(); Console.WriteLine("probe written to " + file); } catch { }
        return 0;
    }

    private static string EnvSecond
    {
        get
        {
            try
            {
                string[] a = Environment.GetCommandLineArgs();
                return (a != null && a.Length > 2) ? a[2] : null;
            }
            catch { return null; }
        }
    }

    // ---------------------------------------------------------------- menu UI
    //
    // One menu row. Selected rows are inverse video, which is the only highlight
    // that survives every console theme: some terminals map "bright white" onto
    // the background and would make a foreground-only highlight invisible.
    private static void SayItem(int index, bool selected, string key, string text, string note, ConsoleColor valueColor, int nameW)
    {
        string body = (selected ? " > " : "   ") + key.PadRight(2) + " " + text.PadRight(nameW) + note;
        if (body.Length < 36) { body = body.PadRight(36); }   // one item per line, bar stays short
        if (!selected) { Say(body, valueColor); return; }

        // Own the console handle: paint through it, like Say(...) does.
        if (ConsoleOut != IntPtr.Zero)
        {
            try
            {
                // Bright bar, dark text. DarkCyan-on-white was too close to the
                // normal rows on some console themes to read as "this one".
                Console.BackgroundColor = ConsoleColor.Cyan;
                Console.ForegroundColor = ConsoleColor.Black;
                WriteOut(body, null);
            }
            catch { }
            finally { try { Console.ResetColor(); } catch { } }
            return;
        }
        bool painted = false;
        if (HasConsole)
        {
            try
            {
                bool redirected = false;
                try { redirected = Console.IsOutputRedirected; } catch { }
                if (!redirected)
                {
                    Console.BackgroundColor = ConsoleColor.DarkCyan;
                    Console.ForegroundColor = ConsoleColor.White;
                    painted = true;
                }
            }
            catch { painted = false; }
        }
        try { Say(body); }
        finally
        {
            if (painted)
            {
                try { Console.ResetColor(); } catch { }
            }
        }
    }

    // Repaints ONE item row in place. The previous revision answered every mouse
    // move with a full clear + redraw, which at ~100 moves/second is what made the
    // hover feel slow and the screen flash: each redraw cleared the console and
    // wrote ~20 lines through WriteConsoleW. Only the two rows whose highlight
    // actually changed need touching, so that is all this does.
    private static void RedrawItemRow(int sel, int index, bool armed, bool hasBaseline, int[] rows)
    {
        if (rows == null || index < 0 || index >= rows.Length) { return; }
        try
        {
            if (Console.CursorTop != rows[index]) { Console.SetCursorPosition(0, rows[index]); }
            SayItem(index, sel == index, ItemKey(index), ItemText(index, armed),
                ItemNote(index, armed, hasBaseline), ItemColor(index, armed), 10);
        }
        catch { }
    }

    //
    // Returns:
    //   1000 up, 1001 down, 1002 confirm, 1003 escape, 1004 quit, 0..4 direct
    //   1100 + item = mouse click on that item
    //   1200 + item = mouse hovered that item (only reported when it changes)
    //   1005 a key we ignore, 1006 nothing happened at all, -1 stdin is not a console
    private static int ReadMenuKey(int itemCount, int itemTop, int[] itemRows, int lastHover)
    {
        try
        {
            if (!Console.KeyAvailable)
            {
                IntPtr h = GetConsoleInputHandle();
                if (h != IntPtr.Zero && h != new IntPtr(-1))
                {
                    INPUT_RECORD[] recs = new INPUT_RECORD[16];
                    uint read;
                    int pending = -1;      // best event seen in this batch
                    while (PeekConsoleInput(h, recs, (uint)recs.Length, out read) && read > 0)
                    {
                        uint got;
                        if (!ReadConsoleInput(h, recs, (uint)recs.Length, out got) || got == 0) { break; }
                        for (int i = 0; i < got; i++)
                        {
                            if (recs[i].EventType != MOUSE_EVENT) { continue; }
                            int row = recs[i].MouseEvent.MousePosition.Y;
                            int col = recs[i].MouseEvent.MousePosition.X;

                            // Exact row only. The ±1 slack this had was the direct
                            // cause of "the second item merges into the first": the
                            // blank line between two items resolved to the item above
                            // it, and the redraw then wrote that item's text onto a row
                            // where its neighbour already lived. Forgiving aim is
                            // provided horizontally (inFrame) instead, which cannot
                            // corrupt the layout.
                            int idx = -1;
                            for (int n = 0; n < itemCount; n++)
                            {
                                if (itemRows[n] == row) { idx = n; break; }
                            }
                            bool inFrame = col >= 0 && col <= 60;

                            bool leftDown = (recs[i].MouseEvent.ButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0;
                            bool moved = (recs[i].MouseEvent.EventFlags & MOUSE_MOVED) != 0;
                            Trace("mouse: row=" + row + " col=" + col + " idx=" + idx
                                + " down=" + leftDown + " moved=" + moved);

                            if (idx >= 0 && inFrame && leftDown) { return 1100 + idx; }
                            if (idx >= 0 && idx != lastHover) { pending = 1200 + idx; }
                            else if (idx < 0 && lastHover >= 0) { pending = 1200 + itemCount; }
                        }
                        // Everything readable has been consumed; hand back the most
                        // recent hover so intermediate moves are not dropped.
                        if (pending >= 0) { return pending; }
                    }
                }
                System.Threading.Thread.Sleep(12);
                return 1006;   // nothing happened: the caller must NOT redraw for this
            }
        }
        catch { return -1; }

        ConsoleKeyInfo k;
        try { k = Console.ReadKey(true); }
        catch { return -1; }

        switch (k.Key)
        {
            case ConsoleKey.UpArrow: return 1000;
            case ConsoleKey.DownArrow: return 1001;
            case ConsoleKey.Enter: return 1002;
            case ConsoleKey.Escape: return 1003;
            case ConsoleKey.Q: return 1004;
            case ConsoleKey.D0:
            case ConsoleKey.NumPad0: return 0;
            case ConsoleKey.D1:
            case ConsoleKey.NumPad1: return 1;
            case ConsoleKey.D2:
            case ConsoleKey.NumPad2: return 2;
            case ConsoleKey.D3:
            case ConsoleKey.NumPad3: return 3;
            case ConsoleKey.D4:
            case ConsoleKey.NumPad4: return 4;
            default: return 1005;   // ignore anything else
        }
    }

    private static int[] ShowMenuView(int sel, int itemCount)
    {
        bool armed = IsArmed();
        int runtimePid = LiveRuntimePid();
        string lastCheck = RelativeTime(LastTick());
        string target = ShortName(JVal(Path.Combine(DataDir, "last-known-good.json"), "snapshot"));
        bool hasBaseline = !string.IsNullOrEmpty(target);

        string stateText;
        ushort stateAttr;
        if (!armed) { stateText = "○ 未监视 —— 不占用任何资源"; stateAttr = ATTR_DIM; }
        else if (runtimePid > 0) { stateText = "● 监视中 —— 关掉本窗口即停止"; stateAttr = ATTR_OK; }
        else { stateText = "◐ 已开启，监视器启动中…"; stateAttr = ATTR_WARN; }

        int W = ScreenW() - 2;
        string rule = "+" + new string('-', W - 2) + "+";

        // The frame is composed FIRST, then written row by row from row 0. An
        // earlier revision drew row by row straight to the console and relied on
        // scrolling the viewport back to the top; after an action only the bottom
        // half of the menu reappeared, because the console had scrolled and the
        // later rows landed below it. Composing first makes the repaint a single
        // ordered pass over rows 0..N, which cannot half-apply.
        List<string> text = new List<string>();
        List<ushort> attrs = new List<ushort>();
        text.Add(rule); attrs.Add(ATTR_HILITE);
        text.Add("| DSH Guardian                崩溃自动回退"); attrs.Add(ATTR_TITLE);
        text.Add(rule); attrs.Add(ATTR_HILITE);
        text.Add(""); attrs.Add(ATTR_NORMAL);
        text.Add("   " + stateText); attrs.Add(stateAttr);
        text.Add(""); attrs.Add(ATTR_NORMAL);

        int[] rows = new int[itemCount];
        for (int n = 0; n < itemCount; n++)
        {
            rows[n] = text.Count;
            text.Add((sel == n ? " > " : "   ") + ItemKey(n).PadRight(2) + " "
                + ItemText(n, armed).PadRight(12) + ItemNote(n, armed, hasBaseline));
            attrs.Add(AttrOf(ItemColor(n, armed)));
        }
        text.Add(rule); attrs.Add(ATTR_HILITE);
        text.Add("   上次检查  " + Fit(lastCheck, 40)); attrs.Add(ATTR_NORMAL);
        text.Add("   回退目标  " + Fit(target, 40)); attrs.Add(hasBaseline ? ATTR_NORMAL : ATTR_WARN);
        text.Add(""); attrs.Add(ATTR_NORMAL);
        if (!hasBaseline)
        {
            text.Add("   ⚠ 还没有基线，崩了没得退。"); attrs.Add(ATTR_WARN);
            text.Add("     先选「3 打基线」，再装插件。"); attrs.Add(ATTR_WARN);
        }
        else if (!armed)
        {
            text.Add("   装插件前：选「4」打开自动检查，窗口别关。"); attrs.Add(ATTR_NORMAL);
        }
        else
        {
            text.Add("   守护中。装完插件确认 DSH 正常 → 选「3」打新基线"); attrs.Add(ATTR_NORMAL);
            text.Add("   → 选「4」关掉 → 关窗口。"); attrs.Add(ATTR_NORMAL);
        }
        text.Add(""); attrs.Add(ATTR_NORMAL);
        text.Add(rule); attrs.Add(ATTR_HILITE);
        text.Add("   ↑↓ 选择   Enter 确认   数字键直选   Q 退出   也可鼠标点"); attrs.Add(ATTR_DIM);

        ClearScreen(ATTR_NORMAL);          // whole screen, one attribute: no ghosts
        for (int i = 0; i < text.Count; i++) { PutLine(i, text[i], attrs[i]); }
        FillRowBand(rows[sel], ATTR_SELECT);   // exactly one highlighted row
        HomeCursor();
        return rows;
    }

    // The five items, in one place so the layout and the hit-testing cannot drift
    // apart. Mouse row -> item index is decided by ShowMenuView and returned.
    private const int MenuItems = 5;

    private static string ItemKey(int i)
    {
        return i == 0 ? "1" : i == 1 ? "2" : i == 2 ? "3" : i == 3 ? "4" : "Q";
    }

    private static string ItemText(int i, bool armed)
    {
        switch (i)
        {
            case 0: return "日志";
            case 1: return "回退";
            case 2: return "基线";
            case 3: return armed ? "关检查" : "开检查";
            default: return "退出";
        }
    }

    private static string ItemNote(int i, bool armed, bool hasBaseline)
    {
        switch (i)
        {
            case 0: return "原始报错";
            case 1: return hasBaseline ? "退回旧版" : "无可退";
            case 2: return "记下好状态";
            case 3: return armed ? "当前开" : "当前关";
            default: return armed ? "停止监视" : "";
        }
    }

    private static ConsoleColor ItemColor(int i, bool armed)
    {
        if (i == 3 && armed) { return ConsoleColor.Green; }
        return ConsoleColor.White;
    }

    private static int RunMenu()
    {
        // Interactive means: stdout is a real console (so Clear/drawing mean
        // something) AND the console input buffer is reachable. It deliberately
        // does NOT test Console.IsInputRedirected: that reads true for any launch
        // whose stdin we did not inherit as a console, even though the process has
        // a perfectly usable console -- and ReadKey/ReadConsoleInput work through
        // CONIN$ in that case. Gating on it silently disabled the whole
        // interactive menu, mouse included.
        bool interactive = true;
        try { interactive = !Console.IsOutputRedirected; } catch { interactive = false; }
        IntPtr hIn = GetConsoleInputHandle();
        if (hIn == IntPtr.Zero || hIn == new IntPtr(-1)) { interactive = false; }
        else
        {
            uint m;
            if (!GetConsoleMode(hIn, out m)) { interactive = false; }
        }
        Trace("menu: interactive=" + interactive + " outRedirected=" + Console.IsOutputRedirected);

        int sel = 0;
        int lastHover = -1;
        int[] rows = null;
        bool hasBaseline = !string.IsNullOrEmpty(ShortName(JVal(Path.Combine(DataDir, "last-known-good.json"), "snapshot")));
        bool needFull = true;
        while (true)
        {
            if (!interactive)
            {
                // Line mode: same five choices, typed as a number. Keeps the menu
                // usable from a pipe or an automated check.
                ShowMenuView(sel, MenuItems);
                Console.Write("请选择 (0-4): ");
                string typed = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                Trace("menu: typed=\"" + typed + "\"");
                if (typed == "0" || typed == "q") { return ExitMenu(); }
                if (typed == "1") { After(RunMenuAction("ShowLogs", ShowLogs), "错误日志"); continue; }
                if (typed == "2") { After(RunMenuAction("RollbackTo", RollbackTo), "回退 / 切换目标"); continue; }
                if (typed == "3") { After(RunMenuAction("Mark-Good", MarkGood), "打基线"); continue; }
                if (typed == "4") { After(RunMenuAction("Arm", delegate { return Arm(!IsArmed()); }), "自动检查"); continue; }
                continue;
            }

            // A full repaint happens only when the content actually changed: at
            // startup and after an action. Hovering repaints just the two rows
            // whose highlight moved -- clearing the screen for every mouse move is
            // what made this flicker and feel slow.
            if (needFull) { rows = ShowMenuView(sel, MenuItems); needFull = false; }
            SayPart("  ");

            int key = ReadMenuKey(MenuItems, 0, rows, lastHover);
            if (key == 1005 || key == 1006) { continue; }         // nothing to redraw for

            // Mouse: hovering moves the bar, clicking acts on it.
            if (key >= 1200)
            {
                int h = key - 1200;
                lastHover = h >= MenuItems ? -1 : h;
                if (lastHover >= 0 && lastHover != sel)
                {
                    int was = sel;
                    sel = lastHover;
                    bool armedNow = IsArmed();
                    RedrawItemRow(sel, was, armedNow, hasBaseline, rows);
                    RedrawItemRow(sel, sel, armedNow, hasBaseline, rows);
                }
                continue;
            }
            if (key >= 1100)
            {
                int clicked = key - 1100;
                Trace("menu: mouse click item=" + clicked);
                key = clicked + 1;                                // 1..4, or 5 for quit
                if (key == 5) { key = 0; }
            }

            Trace("menu: key=" + key + " sel=" + sel);
            // Keyboard navigation also repaints only the two affected rows.
            if (key == 1000 || key == 1001)
            {
                int was = sel;
                sel = key == 1000 ? (sel + MenuItems - 1) % MenuItems : (sel + 1) % MenuItems;
                bool armedNow = IsArmed();
                RedrawItemRow(sel, was, armedNow, hasBaseline, rows);
                RedrawItemRow(sel, sel, armedNow, hasBaseline, rows);
            }
            if (key == -1) { interactive = false; continue; }     // stdin vanished
            if (key == 1003 || key == 1004)
            {
                sel = 0;
                if (ExitMenu() == 0) { return 0; }
                continue;   // user cancelled the exit; stay in the menu
            }

            if (key == 1002) { key = sel + 1; }                   // Enter = confirm
            if (key < 1 || key > 4) { if (key == 0) { return ExitMenu(); } continue; }

            // The action gets the screen to itself. Without this the action's
            // output was appended under the still-drawn menu and overwrote the
            // hint line, which reads like the UI broke.
            try { Console.Clear(); } catch { }
            switch (key)
            {
                case 1: After(RunMenuAction("ShowLogs", ShowLogs), "错误日志"); break;
                case 2: After(RunMenuAction("RollbackTo", RollbackTo), "回退 / 切换目标"); break;
                case 3: After(RunMenuAction("Mark-Good", MarkGood), "打基线"); break;
                case 4: After(RunMenuAction("Arm", delegate { return Arm(!IsArmed()); }), "自动检查"); break;
            }
            // Content or state changed, so the next iteration repaints the whole
            // screen once. Hover and arrow keys never take this path.
            needFull = true;
            try { Console.Clear(); } catch { }
        }
    }

    // Result pause. Waiting for ANY key instead of Enter is the smallest change
    // that removes "press Enter" from every dead end a beginner can hit.
    //
    // The result is also placed under a heading, and the screen is cleared before
    // the menu is drawn again: the console holds about 25 rows, the menu is 20 of
    // them, so a bare echo scrolled the menu off the top and the option list
    // appeared to vanish. Clearing puts the menu back at the top instead of
    // leaving the selection bar stranded above the fold.
    private static void After(int rc, string title)
    {
        // Everything here goes through the console handle we own, never through
        // Console.Write/ReadKey. Those follow the *redirected* std handles in this
        // program: the result text went to a pipe nobody reads and ReadKey threw,
        // so the pause never happened, the menu never came back, and there was no
        // way to take the next step. That is the "刷屏把 UI 刷掉" report.
        ConsoleColor titleColor = ConsoleColor.Cyan;
        ConsoleColor resultColor = rc == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
        Say("");
        if (!string.IsNullOrEmpty(title)) { Say("【" + title + "】", titleColor); }
        Say(rc == 0 ? "[完成]" : "[退出码 " + rc + "]", resultColor);
        // The child's own output, replayed here. It goes through the writer that
        // owns the screen, so it cannot scroll the menu away.
        string body = LastChildOutput;
        if (!string.IsNullOrEmpty(body))
        {
            string[] lines = body.Replace("\r\n", "\n").Split('\n');
            int from = lines.Length > 22 ? lines.Length - 22 : 0;
            for (int i = from; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0) { continue; }
                Say("   " + lines[i].TrimEnd(), ConsoleColor.Gray);
            }
        }
        Say("");
        SayPart("按任意键返回菜单…");
        ReadAnyKey();
    }

    // Reads one key from the console input buffer we opened, so the pause works
    // even when stdin is redirected. Falls back to the managed call only if the
    // handle is unusable, and gives up rather than blocking forever.
    private static void ReadAnyKey()
    {
        IntPtr h = GetConsoleInputHandle();
        INPUT_RECORD[] recs = new INPUT_RECORD[8];
        DateTime deadline = DateTime.Now.AddSeconds(KeyWaitTimeoutSeconds);
        while (DateTime.Now < deadline)
        {
            try
            {
                uint read;
                if (PeekConsoleInput(h, recs, (uint)recs.Length, out read) && read > 0)
                {
                    uint got;
                    if (ReadConsoleInput(h, recs, (uint)recs.Length, out got) && got > 0)
                    {
                        for (int i = 0; i < got; i++)
                        {
                            if (recs[i].EventType == 0x0001
                                || (recs[i].EventType == MOUSE_EVENT
                                    && (recs[i].MouseEvent.ButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0))
                            {
                                return;
                            }
                        }
                    }
                    continue;
                }
            }
            catch { return; }
            System.Threading.Thread.Sleep(20);
        }
    }
    private static int ExitMenu()
    {
        // Exiting stops monitoring. Ask before doing it, rather than silently
        // leaving the user unprotected.
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
                Console.Write("按任意键返回菜单…");
                try { Console.ReadKey(true); } catch { }
                return -1;   // -1 = "not really exiting"
            }
            Exec(WatchdogPath, "-Pause");
            StopWatcher();
        }
        return 0;
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
            Exec(SnapshotPath, "-Action Promote -Snapshot \"" + name + "\"");
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
    // ---------------------------------------------------------------- watch-loop
    //
    // The resident watcher used to BE a long-lived PowerShell process: ~139 MB held for
    // hours so that a probe could run every 55 seconds. The loop below keeps the same
    // cadence in this process (a few MB) and starts PowerShell only for the round
    // itself, which lives one or two seconds and then exits. The decision logic and the
    // rollback stay in dsh-watchdog.ps1 / dsh-snapshot.ps1, untouched -- only the thing
    // that stays resident changed.
    //
    // The log lines deliberately match the PowerShell version word for word
    // ("resident: started/exiting/stopped"), because docs\LOG.md documents them.
    private static int WatchLoop(string[] args)
    {
        int interval = ArgNum(args, "-IntervalSeconds", 55);
        int parentPid = ArgNum(args, "-ParentPid", 0);
        long parentTicks = ArgNumLong(args, "-ParentStartTicks", 0);
        int maxMinutes = ArgNum(args, "-MaxResidentMinutes", 240);
        // Extra parameters forwarded to every round. The GUI passes none, but the
        // sandbox QC has to drive the same loop with a short boot window and a lower
        // crash threshold, and users tuning the guard need the same door. Without it the
        // loop's round arguments are frozen in code and cannot be exercised by a test.
        string roundArgs = ArgText(args, "-RoundArgs");
        // Same thing, but read from a file. Passed on the command line the value would
        // need its own quoting, and the parameters a caller wants to forward already
        // contain quoted values (-LaunchCommand is a whole command line), so the two
        // levels of quotes collide. The sandbox QC hit exactly that. A file has no
        // quoting rules to get wrong.
        if (roundArgs.Length == 0)
        {
            string f = ArgText(args, "-RoundArgsFile");
            if (f.Length > 0)
            {
                try { roundArgs = File.ReadAllText(f).Trim(); }
                catch (Exception ex) { WatchLog("watch-loop: cannot read " + f + ": " + ex.Message); }
            }
        }

        string pidFile = Path.Combine(DataDir, "runtime.pid");
        int me = Process.GetCurrentProcess().Id;
        string parentNote = parentPid > 0 ? "parent " + parentPid : "no parent binding";

        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(pidFile, me.ToString(), new UTF8Encoding(false));
            WatchLog("resident: started (pid " + me + ", " + parentNote
                + ", interval " + interval + "s, max " + maxMinutes + " min)");
        }
        catch (Exception ex)
        {
            WatchLog("resident: cannot claim the pid file: " + ex.Message);
            return 4;
        }

        DateTime deadline = DateTime.Now.AddMinutes(maxMinutes);
        int rounds = 0;
        int failedRounds = 0;
        string exitReason = null;

        try
        {
            while (true)
            {
                rounds++;

                // One round of the real watcher, bounded: see RunRoundBounded for why
                // Exec() must not be used inside the loop.
                int rc = RunRoundBounded(WatchdogPath, "-Silent -AutoRollback -Mode auto"
                    + (roundArgs.Length > 0 ? " " + roundArgs : ""), RoundTimeoutSeconds);

                // A round that cannot run must never be silent.
                //
                // Measured failure: a duplicated -AutoRollback made PowerShell refuse to
                // bind its parameters. Every round died in under a second, the loop kept
                // looping, and the window went on saying 监视中 -- the guard was not
                // watching anything and nothing said so. The exit code is the only signal
                // that distinguishes "watched and found nothing" from "never watched".
                if (rc != 0)
                {
                    failedRounds++;
                    WatchLog("round: failed (exit " + rc + ", " + failedRounds + " in a row)");
                    if (failedRounds == FailedRoundsBeforeAlert)
                    {
                        WatchLog("ALERT: " + FailedRoundsBeforeAlert
                            + " rounds in a row failed to run; nothing is being watched."
                            + " See data\\child-output.log for the reason.");
                    }
                }
                else { failedRounds = 0; }
                if (rounds == 1) { WatchLog("resident: first round done"); }

                if (ModeIsPaused()) { exitReason = "disarmed"; break; }
                if (ParentGone(parentPid, parentTicks)) { exitReason = "launcher closed"; break; }
                if (DateTime.Now >= deadline) { exitReason = "lifetime cap reached"; break; }

                // Sleep out the interval, but stay responsive to the two things that
                // must stop the loop promptly: the user disarming, and the window that
                // asked for this closing.
                DateTime woke = DateTime.Now;
                while ((DateTime.Now - woke).TotalSeconds < interval)
                {
                    System.Threading.Thread.Sleep(WatchIntervalCheckMs);
                    if (ModeIsPaused()) { exitReason = "disarmed"; break; }
                    if (ParentGone(parentPid, parentTicks)) { exitReason = "launcher closed"; break; }
                }
                if (exitReason != null) { break; }
            }
        }
        catch (Exception ex)
        {
            exitReason = "threw " + ex.GetType().Name;
            WatchLog("resident: " + exitReason + ": " + ex.Message);
        }
        finally
        {
            // Only remove the pid file if it still names us: another instance may have
            // taken the slot, and deleting its file would orphan it.
            try
            {
                if (File.Exists(pidFile)
                    && File.ReadAllText(pidFile).Trim() == me.ToString())
                {
                    File.Delete(pidFile);
                }
            }
            catch { }
        }

        if (exitReason != null) { WatchLog("resident: exiting (" + exitReason + ")"); }
        WatchLog("resident: stopped after " + rounds + " round(s)");
        return 0;
    }

    // How often the sleep between rounds re-checks the exit conditions. Matches the
    // PowerShell loop's own 3s cadence so "closing the window stops it" feels the same.
    private const int WatchIntervalCheckMs = 3000;

    // Budget for one round. The watchdog's own boot window can be 90s, and a round that
    // waits one out is legitimate, so this is generous -- it exists to stop a wedged
    // child, not to hurry a working one.
    private const int RoundTimeoutSeconds = 180;

    // Consecutive failed rounds before the log says out loud that nothing is being
    // watched. Three is enough to rule out a one-off while still being prompt.
    private const int FailedRoundsBeforeAlert = 3;

    private static void WatchLog(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "watchdog.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [INFO] " + message + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { }
    }

    // Tolerant on purpose: mode.json is written by two other components, and a strict
    // match against "mode":"paused" would treat a spaced file as "not paused" -- which
    // fails in the dangerous direction, since it means the watcher keeps running.
    private static bool ModeIsPaused()
    {
        try
        {
            string p = Path.Combine(DataDir, "mode.json");
            if (!File.Exists(p)) { return false; }
            string s = File.ReadAllText(p);
            int i = s.IndexOf("\"mode\"", StringComparison.Ordinal);
            if (i < 0) { return false; }
            i = s.IndexOf(':', i);
            if (i < 0) { return false; }
            int a = s.IndexOf('"', i);
            if (a < 0) { return false; }
            int b = s.IndexOf('"', a + 1);
            if (b < 0) { return false; }
            return s.Substring(a + 1, b - a - 1).Trim()
                .Equals("paused", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // A pid on its own is not proof the launcher is alive: Windows recycles pids, and a
    // recycled one made the PowerShell side keep running after its window had gone.
    // The start time travels with the pid for exactly that reason.
    private static bool ParentGone(int pid, long startTicks)
    {
        if (pid <= 0) { return false; }
        try
        {
            Process p = Process.GetProcessById(pid);
            if (startTicks > 0)
            {
                try
                {
                    long delta = Math.Abs(p.StartTime.Ticks - startTicks);
                    if (delta > TimeSpan.FromSeconds(2).Ticks) { return true; }
                }
                catch { }
            }
            return false;
        }
        catch { return true; }
    }

    // One round, bounded in time and safe against pipe deadlock.
    //
    // Exec() cannot be used here. It reads the child's stderr to EOF and only then its
    // stdout; a child that fills the stdout pipe buffer blocks writing while the parent
    // blocks reading stderr, so both wait forever. Exec() also waits without a timeout.
    // In the CLI that merely hangs the one command the user ran; inside watch-loop it
    // hangs the LOOP, and the failure is silent -- the window still says 监视中 while
    // nothing is being watched. Measured: R3 rollback never fired because round 2 never
    // returned.
    //
    // So: both streams are drained concurrently, and the child is killed if it outstays
    // its budget. A round that cannot finish is a fact worth logging, not a reason to
    // stop watching.
    private static int RunRoundBounded(string script, string scriptArgs, int timeoutSeconds)
    {
        Process p = null;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = PsExe();
            psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""
                + script + "\" -DataDir \"" + DataDir + "\" " + scriptArgs;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            p = Process.Start(psi);

            // Drain both pipes at once. Reading them in sequence is the deadlock.
            System.Threading.Tasks.Task<string> outTask =
                System.Threading.Tasks.Task.Factory.StartNew(delegate { return p.StandardOutput.ReadToEnd(); });
            System.Threading.Tasks.Task<string> errTask =
                System.Threading.Tasks.Task.Factory.StartNew(delegate { return p.StandardError.ReadToEnd(); });

            bool exited = p.WaitForExit(timeoutSeconds * 1000);
            if (!exited)
            {
                WatchLog("round: exceeded " + timeoutSeconds + "s, killing the child");
                try { p.Kill(); } catch { }
                try { p.WaitForExit(5000); } catch { }
            }

            string outp = null, err = null;
            try { outp = outTask.Result; } catch { }
            try { err = errTask.Result; } catch { }

            string merged = ((outp ?? "") + (err ?? "")).Trim();
            if (merged.Length > 0)
            {
                try
                {
                    File.AppendAllText(Path.Combine(DataDir, "child-output.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " +
                        Path.GetFileName(script) + " " + scriptArgs + Environment.NewLine +
                        merged + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
            }
            return exited ? p.ExitCode : 5;
        }
        catch (Exception ex)
        {
            WatchLog("round: failed to run: " + ex.Message);
            return 3;
        }
        finally
        {
            try { if (p != null) { p.Dispose(); } } catch { }
        }
    }

    private static int ArgNum(string[] args, string name, int fallback)
    {
        try
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    int v;
                    if (int.TryParse(args[i + 1], out v)) { return v; }
                }
            }
        }
        catch { }
        return fallback;
    }

    // Free-text parameter (a whole argument list, not a number).
    private static string ArgText(string[] args, string name)
    {
        try
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) { return args[i + 1]; }
            }
        }
        catch { }
        return "";
    }

    private static long ArgNumLong(string[] args, string name, long fallback)    {
        try
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    long v;
                    if (long.TryParse(args[i + 1], out v)) { return v; }
                }
            }
        }
        catch { }
        return fallback;
    }

    //
    // Child stderr is captured to data\child-stderr.log as well as shown. The
    // snapshot script can fail with a PowerShell error that closes the child
    // before anything reaches the console; without a copy on disk there is
    // nothing left to diagnose.
    private static int Exec(string script, string scriptArgs)
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
            // The child must NEVER touch the console. This is the root cause of the
            // disappearing menu text: stdout was inherited, so powershell wrote its
            // progress into the same console buffer the menu lives in, scrolled it,
            // and the frames the program had just drawn were pushed away or wiped.
            // No amount of care in drawing survives a second writer on the same
            // screen, so the child is pointed at pipes instead and the menu redraws
            // itself afterwards.
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process p = Process.Start(psi);

            string err = null;
            string childOut = null;
            try { err = p.StandardError.ReadToEnd(); } catch { }
            try { childOut = p.StandardOutput.ReadToEnd(); } catch { }
            p.WaitForExit();

            string merged = ((childOut ?? "") + (err ?? "")).Trim();
            if (merged.Length > 0)
            {
                Trace("child output (" + Path.GetFileName(script) + " " + scriptArgs + "): " + merged.Replace("\r", " ").Replace("\n", " | "));
                LastChildOutput = merged;
                try
                {
                    File.AppendAllText(Path.Combine(DataDir, "child-output.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " +
                        Path.GetFileName(script) + " " + scriptArgs + Environment.NewLine +
                        merged + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
            }
            else { LastChildOutput = null; }
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

    // Text the last child process produced, shown to the user instead of letting
    // the child write to the console itself.
    private static string LastChildOutput = null;

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

    // OpenShortcut() used to live here: it resolved WScript.Shell through
    // Type.GetTypeFromProgID and handed the raw __ComObject to callers. Both that
    // reflected pattern and the C# dynamic binder that replaced it fail from inside
    // this executable with UnauthorizedAccessException on the Desktop path, while a
    // PowerShell process doing the identical calls succeeds. The shortcut is now
    // written by make-shortcut.ps1, so nothing here called it any more -- and dead
    // COM plumbing that is known not to work is worse than no plumbing.

    // The wording a shortcut created by THIS binary should carry.
    //
    // Both executables are compiled from one source, so "which build am I" is answered
    // by the file name. The console build is the CLI/automation entry point; the GUI
    // build is what the desktop shortcut is meant to launch.
    private static string ShortcutDescription(string exePath)
    {
        string name = "";
        try { name = Path.GetFileNameWithoutExtension(exePath) ?? ""; } catch { }
        if (name.IndexOf("console", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "DSH \u5D29\u6E83\u81EA\u52A8\u56DE\u9000\uFF08\u547D\u4EE4\u884C\u7248\uFF09";
        }
        return "DSH \u5D29\u6E83\u81EA\u52A8\u56DE\u9000\uFF08\u56FE\u5F62\u754C\u9762\uFF09";
    }

    private static void WriteShortcut(string lnk)
    {
        string self = Assembly.GetExecutingAssembly().Location;
        // The desktop shortcut launches the user-facing program, so it points at the GUI
        // build when that sits next to this one.
        //
        // Running `shortcut` used to make a shortcut to whichever binary executed the
        // verb. Because the GUI forwards its command line here, the shortcut users got
        // pointed at dsh-guardian-console.exe -- a windowless console build -- instead
        // of the program they double-click to use. The console build is the
        // implementation; the GUI build is the entry point.
        string gui = Path.Combine(BaseDir, "dsh-guardian.exe");
        string exe = File.Exists(gui) ? gui : self;
        // The icon comes from the shipped .ico, falling back to the icon embedded in
        // the executable. Both are the same 7-size artwork; neither can be regenerated
        // into something worse at run time.
        //
        // This used to look for dsh-guardian.ico and, when that file was missing, CALL
        // WriteIcon() to generate one: a 32x32 blue square with a white "G". That is
        // what users actually saw whenever the loose file was absent -- the shortcut
        // was written with a worse icon than the program ships with, and the cause was
        // invisible because it looked deliberate.
        string icon = Path.Combine(BaseDir, "dsh-guardian-app.ico");
        if (!File.Exists(icon)) icon = exe + ",0";

        // Written by make-shortcut.ps1, not by this process.
        //
        // Two in-process attempts failed here. Raw reflected COM threw
        // DISP_E_TYPEMISMATCH; the dynamic binder threw UnauthorizedAccessException
        // "cannot save shortcut <desktop path>". A PowerShell process on the same
        // machine, same user, same path, doing the identical WScript.Shell calls
        // succeeds -- the failure follows the process, not the path or permissions.
        //
        // So the work is done by the mechanism that is measured to work. Each language
        // stays in its own file and is invoked by path; nothing is generated.
        string script = Path.Combine(BaseDir, "make-shortcut.ps1");
        if (!File.Exists(script))
        {
            throw new FileNotFoundException(
                "make-shortcut.ps1 not found next to the program; cannot create the shortcut.", script);
        }

        string args = " -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\""
            + " -Lnk \"" + lnk + "\""
            + " -Target \"" + exe + "\""
            + " -WorkDir \"" + BaseDir + "\""
            + " -Icon \"" + icon + "\""
            // The description has to match the build writing it. Both binaries come from
            // one source, so the console build used to stamp the GUI's wording onto a
            // shortcut that actually launches the console -- a label that misdescribes
            // the thing it points at.
            + " -Description \"" + ShortcutDescription(exe) + "\"";

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = PsExe();
        psi.Arguments = args;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        // Explicit, not inherited. The identical script and arguments succeed when
        // started from an ordinary shell and fail when started from this process, and
        // the inherited working directory is the one input that differs between those
        // two launches. Pinning it to the program's own folder removes the variable
        // instead of leaving the child to inherit whatever the parent happened to have.
        psi.WorkingDirectory = BaseDir;

        string output;
        using (Process p = Process.Start(psi))
        {
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000))
            {
                try { p.Kill(); } catch { }
                throw new TimeoutException("make-shortcut.ps1 did not finish within 30s");
            }
            if (p.ExitCode != 0 || !File.Exists(lnk))
            {
                throw new InvalidOperationException(
                    "make-shortcut.ps1 failed (exit " + p.ExitCode + "): " + output.Trim());
            }
        }
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

    // WriteIcon() used to live here. It drew a 32x32 blue square with a white "G" and
    // hand-assembled a single-size .ico, purely as a fallback for when the loose
    // dsh-guardian.ico was missing. WriteShortcut now takes the icon from the
    // executable's own embedded resource, so the fallback had no caller left -- and it
    // was the source of the crude icon users saw. Deleted rather than left in place:
    // dead code that produces a worse result is worse than no code.
}
