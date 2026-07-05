using System.Runtime.InteropServices;
using System.Text;
using RazerSdkReader;
using RazerSdkReader.Structures;

// O1 correlator: prove the 007 event->color mapping with hard data.
// Reads the decrypted keyboard via the official RazerSdkReader (74164FAD) AND the latest SetEventName
// event via the raw 280F4D44 ring buffer; logs (time, event, sample key colors) on every change.

ChromaReader reader;
try { reader = new ChromaReader(); }
catch (Exception ex) { Console.Error.WriteLine("ChromaReader ctor failed: " + ex.Message); return 2; }

string currentApp = "";
var kb = new ColorBox();
reader.AppDataUpdated += (object? _, in ChromaAppData d) => currentApp = d.CurrentAppName;
reader.KeyboardUpdated += (object? _, in ChromaKeyboard k) =>
{
    var c0 = k.GetColor(0); var c10 = k.GetColor(10); var c50 = k.GetColor(50); var c100 = k.GetColor(100);
    kb.Set($"{c0.R:X2}{c0.G:X2}{c0.B:X2} {c10.R:X2}{c10.G:X2}{c10.B:X2} {c50.R:X2}{c50.G:X2}{c50.B:X2} {c100.R:X2}{c100.G:X2}{c100.B:X2}");
};
reader.Exception += (object? _, RazerSdkReaderException e) => Console.Error.WriteLine("READER ERR: " + e.Message);
reader.Start();

Console.WriteLine("CORRELATOR: time | event(280F4D44) | app | key0 key10 key50 key100 (decrypted). Ctrl-C to stop.");
uint lastCounter = 0; string lastEvent = "", lastLogged = "";
while (true)
{
    var (counter, ev) = ReadLatestEvent();
    if (counter != lastCounter && ev.Length > 0) { lastEvent = ev; lastCounter = counter; }
    var colors = kb.Get();
    var line = $"{lastEvent} | {currentApp} | {colors}";
    if (line != lastLogged && (lastEvent.Length > 0 || colors.Length > 0))
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} | {line}");
        lastLogged = line;
    }
    Thread.Sleep(60);
}

static (uint counter, string ev) ReadLatestEvent()
{
    var b = Shm.Read(@"Global\{280F4D44-6AC8-4B21-9F33-EFD0548D76B4}");
    if (b == null || b.Length < 8) return (0, "");
    uint counter = (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);
    if (counter == 0) return (0, "");
    int off = 0x8 + (int)((counter - 1) % 100) * 0x8008;
    var p = Utf16(b, off).Split(';');
    return (counter, p.Length >= 3 && p[1] == "play" ? p[2] : "");
}
static string Utf16(byte[] b, int off)
{
    var sb = new StringBuilder();
    for (int i = off; i + 1 < b.Length; i += 2) { char c = (char)(b[i] | b[i + 1] << 8); if (c == 0) break; sb.Append(c); }
    return sb.ToString();
}

sealed class ColorBox { private string _v = ""; private readonly object _l = new(); public void Set(string v){lock(_l)_v=v;} public string Get(){lock(_l)return _v;} }

static class Shm
{
    const uint FILE_MAP_READ = 0x0004;
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenFileMappingW(uint a, bool i, string n);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr h, uint a, uint oh, uint ol, UIntPtr n);
    [DllImport("kernel32", SetLastError = true)] static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32")] static extern UIntPtr VirtualQuery(IntPtr a, out MBI b, UIntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public IntPtr RegionSize; public uint State, Protect, Type; }
    public static byte[]? Read(string name)
    {
        var h = OpenFileMappingW(FILE_MAP_READ, false, name); if (h == IntPtr.Zero) return null;
        try { var p = MapViewOfFile(h, FILE_MAP_READ, 0, 0, UIntPtr.Zero); if (p == IntPtr.Zero) return null;
            try { VirtualQuery(p, out var m, (UIntPtr)Marshal.SizeOf<MBI>()); long s = (long)m.RegionSize; if (s <= 0 || s > 64*1024*1024) s = 4096; var buf = new byte[s]; Marshal.Copy(p, buf, 0, (int)s); return buf; }
            finally { UnmapViewOfFile(p); } }
        finally { CloseHandle(h); }
    }
}
