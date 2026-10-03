// Morse - bunyikan teks yang sedang diselect sebagai kode morse (beep).
//
// Windows : jalan di latar belakang, tekan Ctrl+Alt+M untuk membunyikan teks terseleksi
//           (tekan lagi saat berbunyi untuk menghentikan), Ctrl+Alt+Q untuk keluar.
// Linux   : sekali jalan -> baca seleksi PRIMARY -> bunyikan -> selesai.
//
// Opsi (semua OS):  Morse -w 20 -f 600 "teks langsung"
//   -w  kecepatan dalam kata/menit (default 15)
//   -f  frekuensi nada dalam Hz    (default 700)
//   Jika ada teks pada argumen, program membunyikan teks itu lalu selesai.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MorseBeep;

static class Program
{
    const int Rate = 22050;
    const int MaxChars = 300;

    static double Wpm = 15;
    static double Freq = 700;
    static volatile bool playing;

    static readonly Dictionary<char, string> Table = new()
    {
        ['A'] = ".-", ['B'] = "-...", ['C'] = "-.-.", ['D'] = "-..", ['E'] = ".", ['F'] = "..-.",
        ['G'] = "--.", ['H'] = "....", ['I'] = "..", ['J'] = ".---", ['K'] = "-.-", ['L'] = ".-..",
        ['M'] = "--", ['N'] = "-.", ['O'] = "---", ['P'] = ".--.", ['Q'] = "--.-", ['R'] = ".-.",
        ['S'] = "...", ['T'] = "-", ['U'] = "..-", ['V'] = "...-", ['W'] = ".--", ['X'] = "-..-",
        ['Y'] = "-.--", ['Z'] = "--..",
        ['0'] = "-----", ['1'] = ".----", ['2'] = "..---", ['3'] = "...--", ['4'] = "....-",
        ['5'] = ".....", ['6'] = "-....", ['7'] = "--...", ['8'] = "---..", ['9'] = "----.",
        ['.'] = ".-.-.-", [','] = "--..--", ['?'] = "..--..", ['\''] = ".----.", ['!'] = "-.-.--",
        ['/'] = "-..-.", ['('] = "-.--.", [')'] = "-.--.-", ['&'] = ".-...", [':'] = "---...",
        [';'] = "-.-.-.", ['='] = "-...-", ['+'] = ".-.-.", ['-'] = "-....-", ['_'] = "..--.-",
        ['"'] = ".-..-.", ['$'] = "...-..-", ['@'] = ".--.-.",
    };

    static int Main(string[] args)
    {
        bool watch = false;
        var textParts = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-w" when i + 1 < args.Length:
                    Wpm = Math.Clamp(double.Parse(args[++i], CultureInfo.InvariantCulture), 5, 60);
                    break;
                case "-f" when i + 1 < args.Length:
                    Freq = Math.Clamp(double.Parse(args[++i], CultureInfo.InvariantCulture), 200, 2000);
                    break;
                case "--watch" or "-a":
                    watch = true;
                    break;
                default:
                    textParts.Add(args[i]);
                    break;
            }
        }

        if (textParts.Count > 0)
            return SpeakText(string.Join(" ", textParts)) ? 0 : 1;

        if (watch && !OperatingSystem.IsWindows())
            return LinuxWatch();

        if (OperatingSystem.IsWindows())
            return WindowsHotkeyLoop();

        return SpeakText(LinuxSelection()) ? 0 : 1;
    }

    // ------------------------------------------------------------------
    // Teks -> morse -> WAV
    // ------------------------------------------------------------------
    static List<List<string>> ToMorse(string text)
    {
        var words = new List<List<string>>();
        foreach (var word in text.ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var letters = new List<string>();
            foreach (char ch in word)
                if (Table.TryGetValue(ch, out var code)) letters.Add(code);
            if (letters.Count > 0) words.Add(letters);
        }
        return words;
    }

    static List<(double freq, double sec)> MorseSegments(List<List<string>> words)
    {
        double unit = 1.2 / Wpm; // standar PARIS
        var segs = new List<(double, double)>();
        foreach (var letters in words)
        {
            foreach (var code in letters)
            {
                foreach (char s in code)
                {
                    segs.Add((Freq, unit * (s == '.' ? 1 : 3)));
                    segs.Add((0, unit));          // jeda antar simbol
                }
                segs.Add((0, unit * 2));          // total 3 satuan antar huruf
            }
            segs.Add((0, unit * 4));              // total 7 satuan antar kata
        }
        return segs;
    }

    static byte[] BuildWav(IEnumerable<(double freq, double sec)> segs)
    {
        var samples = new List<short>();
        foreach (var (freq, sec) in segs)
        {
            int n = (int)(Rate * sec);
            if (freq <= 0)
            {
                for (int i = 0; i < n; i++) samples.Add(0);
                continue;
            }
            int fade = Math.Min((int)(Rate * 0.005), n / 2); // fade 5 ms supaya tidak "klik"
            for (int i = 0; i < n; i++)
            {
                double env = 1.0;
                if (fade > 0)
                {
                    if (i < fade) env = (double)i / fade;
                    else if (i > n - fade) env = (double)(n - i) / fade;
                }
                samples.Add((short)(0.5 * short.MaxValue * env * Math.Sin(2 * Math.PI * freq * i / Rate)));
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Count * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataLen);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataLen);
        foreach (short s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    // ------------------------------------------------------------------
    // Alur utama: teks -> bunyi
    // ------------------------------------------------------------------
    static bool SpeakText(string text)
    {
        text = text.Trim();
        if (text.Length > MaxChars) text = text[..MaxChars];

        var words = ToMorse(text);
        if (words.Count == 0)
        {
            Warn("Tidak ada teks terseleksi yang bisa diterjemahkan.");
            return false;
        }

        Console.WriteLine(string.Join(" / ", words.Select(w => string.Join(" ", w))));
        Play(BuildWav(MorseSegments(words)));
        return true;
    }

    static void Play(byte[] wav)
    {
        string path = Path.Combine(Path.GetTempPath(), $"morse_{Environment.ProcessId}_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, wav);
        playing = true;
        try
        {
            if (OperatingSystem.IsWindows())
                Win.PlaySound(path, IntPtr.Zero, Win.SND_FILENAME | Win.SND_NODEFAULT);
            else
                LinuxPlay(path);
        }
        finally
        {
            playing = false;
            try { File.Delete(path); } catch { /* abaikan */ }
        }
    }

    // Nada pendek sebagai penanda (jalan / selesai / error) saat tidak ada jendela
    static void Signal(params (double freq, double sec)[] segs) => Play(BuildWav(segs));

    static void Warn(string message)
    {
        Console.Error.WriteLine(message);
        if (OperatingSystem.IsWindows())
            Signal((300, 0.35));                         // nada rendah = ada masalah
        else if (Which("notify-send") != null)
            RunProcess(new[] { "notify-send", "Morse", message }, 2000);
    }

    // ------------------------------------------------------------------
    // Windows: hotkey global
    // ------------------------------------------------------------------
    static int WindowsHotkeyLoop()
    {
        using var mutex = new Mutex(true, @"Local\MorseBeepHotkey", out bool first);
        if (!first)
        {
            Signal((300, 0.35));      // sudah ada yang berjalan
            return 1;
        }

        const int IdSpeak = 1, IdQuit = 2;
        uint mods = Win.MOD_ALT | Win.MOD_CONTROL | Win.MOD_NOREPEAT;
        bool ok = Win.RegisterHotKey(IntPtr.Zero, IdSpeak, mods, 'M')
               && Win.RegisterHotKey(IntPtr.Zero, IdQuit, mods, 'Q');
        if (!ok)
        {
            Signal((300, 0.5));       // kombinasi tombol sudah dipakai program lain
            return 2;
        }

        Signal((900, 0.08), (0, 0.05), (1200, 0.12));   // tanda: siap

        while (Win.GetMessage(out Win.MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message != Win.WM_HOTKEY) continue;
            int id = (int)(uint)msg.wParam;
            if (id == IdQuit) break;
            if (id == IdSpeak) OnSpeakHotkey();
        }

        Win.UnregisterHotKey(IntPtr.Zero, IdSpeak);
        Win.UnregisterHotKey(IntPtr.Zero, IdQuit);
        Signal((1200, 0.08), (0, 0.05), (900, 0.12));   // tanda: keluar
        return 0;
    }

    static void OnSpeakHotkey()
    {
        if (playing)                       // ditekan lagi saat berbunyi = stop
        {
            Win.PlaySound(null, IntPtr.Zero, 0);
            return;
        }
        string text = WinCopySelection();
        Task.Run(() => SpeakText(text));   // jangan blokir pesan hotkey berikutnya
    }

    static string WinCopySelection()
    {
        Thread.Sleep(300);                 // beri waktu tombol hotkey dilepas
        string old = WinClipGet();
        WinClipSet("");                    // kosongkan agar ketahuan kalau tidak ada yang tercopy

        foreach (byte vk in new byte[] { Win.VK_SHIFT, Win.VK_MENU, Win.VK_LWIN, Win.VK_RWIN, Win.VK_CONTROL })
            Win.keybd_event(vk, 0, Win.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Win.keybd_event(Win.VK_CONTROL, 0, 0, UIntPtr.Zero);
        Win.keybd_event(Win.VK_C, 0, 0, UIntPtr.Zero);
        Win.keybd_event(Win.VK_C, 0, Win.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Win.keybd_event(Win.VK_CONTROL, 0, Win.KEYEVENTF_KEYUP, UIntPtr.Zero);

        string text = "";
        for (int i = 0; i < 10 && text.Length == 0; i++)   // tunggu sampai 1 detik
        {
            Thread.Sleep(100);
            text = WinClipGet();
        }
        if (text.Length > 0) WinClipSet(old);              // kembalikan clipboard lama (hanya teks)
        return text;
    }

    static bool OpenClip()
    {
        for (int i = 0; i < 10; i++)
        {
            if (Win.OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    static string WinClipGet()
    {
        if (!OpenClip()) return "";
        try
        {
            IntPtr h = Win.GetClipboardData(Win.CF_UNICODETEXT);
            if (h == IntPtr.Zero) return "";
            IntPtr p = Win.GlobalLock(h);
            if (p == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; }
            finally { Win.GlobalUnlock(h); }
        }
        finally { Win.CloseClipboard(); }
    }

    static void WinClipSet(string text)
    {
        if (!OpenClip()) return;
        try
        {
            Win.EmptyClipboard();
            if (text.Length == 0) return;
            byte[] data = Encoding.Unicode.GetBytes(text + "\0");
            IntPtr h = Win.GlobalAlloc(Win.GMEM_MOVEABLE, new UIntPtr((uint)data.Length));
            IntPtr p = Win.GlobalLock(h);
            Marshal.Copy(data, 0, p, data.Length);
            Win.GlobalUnlock(h);
            Win.SetClipboardData(Win.CF_UNICODETEXT, h);
        }
        finally { Win.CloseClipboard(); }
    }

    // ------------------------------------------------------------------
    // Linux: seleksi PRIMARY + pemutar audio
    // ------------------------------------------------------------------
    static string LinuxSelection()
    {
        bool wayland = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland"
                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        var cmds = new List<string[]>();
        if (wayland) cmds.Add(new[] { "wl-paste", "--primary", "--no-newline" });
        cmds.Add(new[] { "xclip", "-o", "-selection", "primary" });
        cmds.Add(new[] { "xsel", "-o", "-p" });
        if (!wayland) cmds.Add(new[] { "wl-paste", "--primary", "--no-newline" });

        bool anyTool = false;
        foreach (var cmd in cmds)
        {
            if (Which(cmd[0]) == null) continue;
            anyTool = true;
            string? text = RunProcess(cmd, 2000);
            if (text != null) return text;
        }

        if (!anyTool)
            Warn("Perlu xclip, xsel, atau wl-clipboard. Contoh: sudo apt install wl-clipboard");
        return "";
    }

    // Mode otomatis: pantau seleksi PRIMARY; begitu teks baru terseleksi dan stabil, bunyikan.
    static int LinuxWatch()
    {
        if (new[] { "wl-paste", "xclip", "xsel" }.All(t => Which(t) == null))
        {
            Warn("Perlu xclip, xsel, atau wl-clipboard. Contoh: sudo apt install wl-clipboard");
            return 1;
        }

        using var mutex = new Mutex(true, @"Local\MorseBeepWatch", out bool first);
        if (!first)
        {
            Console.Error.WriteLine("Mode otomatis sudah berjalan.");
            return 1;
        }

        Console.WriteLine("Mode otomatis aktif: select teks, tunggu sebentar, lalu berbunyi. Ctrl+C untuk berhenti.");
        string last = LinuxSelection();   // seleksi yang sudah ada saat mulai diabaikan
        string pending = last;
        int stable = 0;
        while (true)
        {
            Thread.Sleep(300);
            string cur = LinuxSelection();
            if (cur != pending)
            {
                pending = cur;            // seleksi masih berubah (sedang di-drag)
                stable = 0;
            }
            else if (cur != last && ++stable >= 2)   // stabil ~0,6 detik = seleksi selesai
            {
                last = cur;
                stable = 0;
                if (ToMorse(cur.Trim()).Count > 0) SpeakText(cur);
            }
        }
    }

    static void LinuxPlay(string path)
    {
        var players = new[]
        {
            new[] { "paplay", path },
            new[] { "pw-play", path },
            new[] { "aplay", "-q", path },
            new[] { "play", "-q", path },
            new[] { "ffplay", "-nodisp", "-autoexit", "-loglevel", "quiet", path },
        };
        foreach (var cmd in players)
        {
            if (Which(cmd[0]) == null) continue;
            RunProcess(cmd, 120_000);
            return;
        }
        Warn("Tidak ada pemutar audio (paplay / aplay / sox / ffplay).");
    }

    static string? Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string full = Path.Combine(dir, exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    // Jalankan perintah; kembalikan stdout jika sukses (exit code 0), selain itu null
    static string? RunProcess(string[] cmd, int timeoutMs)
    {
        var psi = new ProcessStartInfo(cmd[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in cmd.Skip(1)) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* abaikan */ }
                return null;
            }
            return p.ExitCode == 0 ? stdout.Result : null;
        }
        catch
        {
            return null;
        }
    }
}

// ----------------------------------------------------------------------
// P/Invoke Windows (hanya dipanggil di Windows)
// ----------------------------------------------------------------------
static class Win
{
    public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    public const uint WM_HOTKEY = 0x0312;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_C = 0x43;
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint SND_FILENAME = 0x00020000, SND_NODEFAULT = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern bool PlaySound(string? sound, IntPtr hmod, uint flags);
}