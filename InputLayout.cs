using System.Globalization;
using System.Runtime.InteropServices;

namespace DesktopKeyboard;

// One positional key's identity under a specific Windows keyboard layout (HKL): the VK the
// layout assigns to that physical position plus the glyphs to display. Dead marks the key as
// a dead key (the target app's own dead-key state machine still does the composing).
internal readonly record struct Glyph(ushort Vk, string Normal, string Shifted, bool Dead, bool ShiftDead);

// Windows keyboard-layout (HKL) service: per-layout glyph tables for positional keys,
// foreground-window layout detection, and layout enumeration/switching.
//
// Threading: ForegroundHkl is stateless user32 reads and safe from any thread (the UIA
// thread polls it); everything else caches into plain dictionaries and shared buffers and
// must only be called from the UI thread.
internal static class InputLayout
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint idThread);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyExW(uint code, uint mapType, IntPtr hkl);
    // CharSet.Unicode is load-bearing: it makes the char[] marshal as WCHARs. The default
    // (Ansi) allocates cch *bytes* while ToUnicodeEx writes cch WCHARs — heap corruption.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint vk, uint sc, byte[] state, [Out] char[] buf, int cch, uint flags, IntPtr hkl);
    [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int n, IntPtr[]? list);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetLocaleInfoW(uint lcid, uint lcType, char[] buf, int cch);

    private const uint MAPVK_VSC_TO_VK_EX = 3;
    // Bit 2 of ToUnicodeEx wFlags (Win10 1607+): translate without touching the kernel's
    // keyboard state, so building tables never corrupts an in-flight dead-key sequence.
    private const uint NoKeyboardStateChange = 0x04;
    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    private const uint LOCALE_SABBREVLANGNAME = 0x0003;
    private const byte VK_SHIFT = 0x10;

    private static readonly Dictionary<IntPtr, Dictionary<byte, Glyph>> _tables = new();
    private static readonly Dictionary<IntPtr, string> _names = new();
    private static readonly byte[] _state = new byte[256];
    private static readonly char[] _buf = new char[8];

    // Layout of the thread that owns the foreground window — the app our keystrokes land in.
    // Falls back to this thread's layout when there's no usable foreground (e.g. desktop).
    public static IntPtr ForegroundHkl()
    {
        IntPtr fg = GetForegroundWindow();
        IntPtr hkl = fg != IntPtr.Zero ? GetKeyboardLayout(GetWindowThreadProcessId(fg, out _)) : IntPtr.Zero;
        return hkl != IntPtr.Zero ? hkl : GetKeyboardLayout(0);
    }

    // Glyph table for the given layout covering the given scan codes. Cached per HKL (a user
    // has a handful of layouts at most, so no eviction).
    public static IReadOnlyDictionary<byte, Glyph> Table(IntPtr hkl, byte[] scanCodes)
    {
        if (hkl == IntPtr.Zero) hkl = GetKeyboardLayout(0);
        if (_tables.TryGetValue(hkl, out var cached)) return cached;

        var table = new Dictionary<byte, Glyph>(scanCodes.Length);
        foreach (byte sc in scanCodes)
        {
            uint vk = MapVirtualKeyExW(sc, MAPVK_VSC_TO_VK_EX, hkl);
            if (vk == 0) { table[sc] = new Glyph(0, "", "", false, false); continue; }   // position unused by this layout
            string normal = GlyphFor(vk, sc, shift: false, hkl, out bool dead);
            string shifted = GlyphFor(vk, sc, shift: true, hkl, out bool shiftDead);
            table[sc] = new Glyph((ushort)vk, normal, shifted, dead, shiftDead);
        }
        _tables[hkl] = table;
        return table;
    }

    private static string GlyphFor(uint vk, uint sc, bool shift, IntPtr hkl, out bool dead)
    {
        Array.Clear(_state);
        if (shift) _state[VK_SHIFT] = 0x80;
        int n = ToUnicodeEx(vk, sc, _state, _buf, _buf.Length, NoKeyboardStateChange, hkl);
        dead = n < 0;
        if (n < 0) n = 1;   // dead key: the buffer holds the dead-key character itself
        if (n <= 0) return "";
        // A combining mark is invisible on its own — give it a dotted-circle base to sit on.
        return char.GetUnicodeCategory(_buf[0]) == UnicodeCategory.NonSpacingMark
            ? "◌" + new string(_buf, 0, n)
            : new string(_buf, 0, n);
    }

    // Short language tag for the LANG key, e.g. "ENU"/"ENG"/"FRA"/"DEU". Win32 lookup because
    // the app runs with InvariantGlobalization=true, where CultureInfo(langid) is unusable.
    // A LANGID is a valid LCID with the default sort order.
    public static string ShortName(IntPtr hkl)
    {
        if (_names.TryGetValue(hkl, out var cached)) return cached;
        var buf = new char[9];
        int n = GetLocaleInfoW((uint)((ulong)hkl & 0xFFFF), LOCALE_SABBREVLANGNAME, buf, buf.Length);
        string name = n > 1 ? new string(buf, 0, n - 1).ToUpperInvariant() : "??";
        _names[hkl] = name;
        return name;
    }

    public static IntPtr[] InstalledLayouts()
    {
        int n = GetKeyboardLayoutList(0, null);
        if (n <= 0) return Array.Empty<IntPtr>();
        var list = new IntPtr[n];
        n = GetKeyboardLayoutList(n, list);
        if (n < list.Length) Array.Resize(ref list, Math.Max(n, 0));
        return list;
    }

    // Cyclic successor among the installed layouts (unknown current → the first installed).
    public static IntPtr NextLayout(IntPtr current)
    {
        var list = InstalledLayouts();
        if (list.Length == 0) return current;
        return list[(Array.IndexOf(list, current) + 1) % list.Length];
    }

    // Ask a window to switch layout. Posted, not sent: delivery is async and the target is
    // free to ignore it, so callers must re-check the actual foreground HKL afterwards.
    public static void RequestSwitch(IntPtr hwnd, IntPtr hkl)
    {
        if (hwnd != IntPtr.Zero && hkl != IntPtr.Zero)
            PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
    }
}
