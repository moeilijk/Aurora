using System.Text;
using ChromaEventEmitter; // linked ChromaSdk.cs
using ChromaShmProbe;

// Fase 0 experiment (#292): does SetEventName surface the event NAME into any
// readable shared-memory mapping WITHOUT Synapse?
//
// One process: open+snapshot all known Global\{GUID} mappings, InitSDK as a game,
// fire a UNIQUE SetEventName, re-snapshot, then report which mappings changed and
// whether the unique string is readable anywhere.
//
//   --direct   use RzChromaSDK64.dll (note: it does NOT export SetEventName; will fail)
//   default    use RzChromatic64.dll PluginCore* (where SetEventName actually lives)

// --events: continuously read the 280F4D44 event ring buffer (no Synapse needed) and report
// each new SetEventName as it arrives — the core of the #292 standalone interception.
if (args.Contains("--events"))
{
    const string name = @"Global\{280F4D44-6AC8-4B21-9F33-EFD0548D76B4}";
    const int rec0 = 0x8, stride = 0x8008, slots = 100;
    uint last = 0; bool first = true;
    Console.WriteLine("Reading SetEventName events from 280F4D44 (no Synapse). Ctrl-C to stop.");
    while (true)
    {
        var b = Shm.Read(name);
        if (b != null && b.Length >= 8)
        {
            uint counter = (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);
            if (counter != last)
            {
                if (!first)
                {
                    for (uint c = last + 1; c <= counter; c++)
                    {
                        int off = rec0 + (int)((c - 1) % slots) * stride;
                        var rec = ReadUtf16(b, off);
                        var parts = rec.Split(';');
                        if (parts.Length >= 3 && parts[1] == "play")
                        {
                            var game = ExtractName(rec);
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  EVENT  {parts[2]}  (game: {game})");
                        }
                        else if (parts.Length >= 3)
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {parts[1]}  {parts[2]}");
                    }
                }
                last = counter; first = false;
            }
        }
        Thread.Sleep(80);
    }
}

bool useDirect = args.Contains("--direct");
IChromaBackend chroma = useDirect ? new ChromaSdkDirect() : new ChromaticPlugin();

// Named mappings referenced by RzChromaSDK64.dll / RzChromatic64.dll (from strings RE).
string[] guids =
{
    "0DB0CEFA-C51E-4255-87FB-2D36A0159896", "0DBE78AC-AC93-408F-A27E-8F61EA067B05",
    "0F9297E6-E80C-47E4-9A8B-1237E50484B7", "0FFE5A62-387E-4360-95A3-5D8D4075780D",
    "17EFA16B-E476-4E43-A98A-3AA837681741", "1C68F494-B74D-46E5-9A2F-56F8C526A7C9",
    "416FA77A-EC97-44AA-9C3C-DFEA2AC245D3", "45C97C2C-2D50-4F30-B50E-AFBB1CE22E93",
    "4D006319-9569-4E38-B0DF-811AA2DF115F", "5C49A446-0B97-46CA-BD60-EE5CAF8DDD59",
    "5CD8AF82-56E4-4C36-9144-6D04931A522B", "6D0C47C9-E199-48C4-B55D-5298974EF8F3",
    "735A64EB-02D2-498D-954E-23FC11A050A9", "74164FAD-E73C-4FA1-A9AA-70813315ED9C",
    "821AA2A2-8215-4A16-BE9D-7CD8CEBDC398", "893ED63D-F9D7-472A-AA34-FFB18CF28C55",
    "89811F96-91C2-4C19-8E0A-54469F491550", "8AE08F8C-BE3E-4248-AB01-0B595960EC3E",
    "96264859-7FDE-4DA1-A433-BB5109D17DB2", "9B7B099A-F7F3-44CE-AB88-28F79D6D273A",
    "9FE422BE-A752-4F67-9EC6-11ED6135478E", "A84AF9C8-EFE0-430D-871C-10DA760C2CCD",
    "A966C3C0-231A-4BE5-9C90-5E0C80349891", "B8B918C0-9790-47F2-AC7A-F36B8414140C",
    "CDB274E2-C50A-4425-8076-1E71550CBE8A", "D41D8537-2D95-4AD2-8A77-51DC00946366",
    "D42A3EF5-1B8D-4055-B42F-C5D34A9CA4E4", "D4E1A960-872F-4BF8-B09A-9E54F646D7CE",
    "DA5A60F0-A3C5-4335-A039-BCC6136C61A3", "E26206E3-F6C2-4E07-BA5C-6C214FD726D9",
    "FFED75C2-17DC-4886-AA2C-DBAF1F662351", "280F4D44-6AC8-4B21-9F33-EFD0548D76B4",
    "5E91644B-084F-43B9-9902-AC476EC2428E",
};
string[] names = guids.Select(g => $@"Global\{{{g}}}").ToArray();

Console.WriteLine("Chroma shared-memory probe (Fase 0 / #292)");
Console.WriteLine($"  backend: {chroma.Name}");
Console.WriteLine();

// --appdata: parse the ChromaAppData mapping (D4E1A960) the way RazerSdkReader does,
// to see if CurrentAppId matches an AppInfo entry (RzHelper needs that match → CurrentAppExecutable).
if (args.Contains("--appdata"))
{
    const int S = 520;            // ChromaString = wchar[260]
    const int infoSize = S + 4 + 4; // AppName + AppId + padding
    const int infoBase = 4 + S + 4 + 4; // AppCount + UnusedAppName + CurrentAppId + padding = 0x214
    var b = Shm.Read(@"Global\{D4E1A960-872F-4BF8-B09A-9E54F646D7CE}");
    if (b == null) { Console.WriteLine("D4E1A960 not open"); return 1; }
    uint U(int o) => (uint)(b[o] | b[o+1]<<8 | b[o+2]<<16 | b[o+3]<<24);
    string Str(int o) { var sb=new StringBuilder(); for(int i=0;i<S-1;i+=2){char c=(char)(b[o+i]|b[o+i+1]<<8); if(c==0)break; sb.Append(c);} return sb.ToString(); }
    uint appCount = U(0);
    uint currentAppId = U(4 + S);
    Console.WriteLine($"AppCount={appCount}  CurrentAppId={currentAppId}  UnusedAppName=\"{Str(4)}\"");
    int n = (int)Math.Min(appCount == 0 ? 50 : appCount, 50);
    for (int i = 0; i < n; i++)
    {
        int bAppName = infoBase + i*infoSize;
        uint appId = U(bAppName + S);
        var name = Str(bAppName);
        if (name.Length == 0 && appId == 0) continue;
        var match = appId == currentAppId ? "  <== matches CurrentAppId" : "";
        Console.WriteLine($"  [{i}] AppId={appId}  AppName=\"{name}\"{match}");
    }
    Console.WriteLine(currentAppId != 0 ? "" : "CurrentAppId is 0 → RzHelper finds no match → CurrentAppExecutable null → RazerLayer renders black.");
    return 0;
}

Dictionary<string, byte[]> Snapshot2()
{
    var d = new Dictionary<string, byte[]>();
    foreach (var n in names)
    {
        var b = Shm.Read(n);
        if (b != null) d[n] = b;
    }
    return d;
}

// --dump: read-only observation of current shared-memory content (run while game is live).
if (args.Contains("--dump"))
{
    foreach (var kv in Snapshot2())
    {
        int nz = 0, firstNz = -1;
        ulong h = 1469598103934665603UL; // FNV-1a 64
        for (int i = 0; i < kv.Value.Length; i++)
        {
            if (kv.Value[i] != 0) { nz++; if (firstNz < 0) firstNz = i; }
            h = (h ^ kv.Value[i]) * 1099511628211UL;
        }
        Console.Write($"{kv.Key}  {kv.Value.Length,8} bytes  nonzero={nz,8}  hash={h:X16}");
        if (firstNz >= 0)
        {
            Console.Write($"  firstNZ=0x{firstNz:X}  hex:");
            for (int i = firstNz; i < Math.Min(firstNz + 32, kv.Value.Length); i++)
                Console.Write($" {kv.Value[i]:X2}");
        }
        Console.WriteLine();
        // UTF-16LE printable strings (len>=4), with offsets — reveals event names / commands.
        foreach (var (off, s) in Utf16Strings(kv.Value, 4, 60))
            Console.WriteLine($"    @0x{off:X}  \"{s}\"");
    }
    return 0;
}

static IEnumerable<(int off, string s)> Utf16Strings(byte[] buf, int minLen, int max)
{
    int count = 0;
    int i = 0;
    while (i + 1 < buf.Length && count < max)
    {
        int start = i;
        var sb = new StringBuilder();
        while (i + 1 < buf.Length)
        {
            char c = (char)(buf[i] | (buf[i + 1] << 8));
            if (c >= 0x20 && c < 0x7F) { sb.Append(c); i += 2; }
            else break;
        }
        if (sb.Length >= minLen) { yield return (start, sb.ToString()); count++; i += 2; }
        else i = start + 2;
    }
}

Dictionary<string, byte[]> Snapshot()
{
    var d = new Dictionary<string, byte[]>();
    foreach (var n in names)
    {
        var b = Shm.Read(n);
        if (b != null) d[n] = b;
    }
    return d;
}

var open0 = Snapshot();
Console.WriteLine($"Opened {open0.Count}/{names.Length} mappings before init:");
foreach (var kv in open0) Console.WriteLine($"  {kv.Key}  ({kv.Value.Length} bytes)");
Console.WriteLine();

var app = APPINFOTYPE.CreateGame("AuroraProbe");
Console.WriteLine($"InitSDK -> {RzResult.Describe(chroma.InitSdk(ref app))}");
Thread.Sleep(400);

var before = Snapshot();
var needle = "AURORAPROBE_" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
Console.WriteLine($"SetEventName(\"{needle}\") -> {RzResult.Describe(chroma.SetEventName(needle))}");
Thread.Sleep(600);
var after = Snapshot();

Console.WriteLine();
Console.WriteLine("== Mappings changed by SetEventName ==");
bool anyChange = false;
foreach (var n in after.Keys)
{
    if (!before.TryGetValue(n, out var a)) { Console.WriteLine($"  NEW: {n}"); anyChange = true; continue; }
    var b = after[n];
    if (a.Length != b.Length || !a.AsSpan().SequenceEqual(b))
    {
        Console.WriteLine($"  CHANGED: {n}  (first diff @ 0x{FirstDiff(a, b):X})");
        anyChange = true;
    }
}
if (!anyChange) Console.WriteLine("  (none)");

Console.WriteLine();
Console.WriteLine("== Needle search across all mappings (ASCII + UTF-16LE) ==");
var asc = Encoding.ASCII.GetBytes(needle);
var uni = Encoding.Unicode.GetBytes(needle);
bool found = false;
foreach (var kv in after)
{
    int ia = IndexOf(kv.Value, asc);
    int iu = IndexOf(kv.Value, uni);
    if (ia >= 0) { Console.WriteLine($"  FOUND ascii  in {kv.Key} @ 0x{ia:X}"); found = true; }
    if (iu >= 0) { Console.WriteLine($"  FOUND utf16  in {kv.Key} @ 0x{iu:X}"); found = true; }
}
if (!found) Console.WriteLine("  (needle not found in any opened mapping)");

Console.WriteLine();
Console.WriteLine(found
    ? "RESULT: event name IS readable in shared memory without Synapse -> shared-memory reader route VIABLE."
    : "RESULT: event name NOT readable in opened mappings -> need static analysis / other entry point (A or C).");

chroma.UnInit();
return found ? 0 : 1;


static string ReadUtf16(byte[] b, int off)
{
    var sb = new StringBuilder();
    for (int i = off; i + 1 < b.Length; i += 2)
    {
        char c = (char)(b[i] | b[i + 1] << 8);
        if (c == 0) break;
        sb.Append(c);
    }
    return sb.ToString();
}

static string ExtractName(string rec)
{
    const string m = "\"name\":\"";
    int i = rec.IndexOf(m, StringComparison.Ordinal);
    if (i < 0) return "?";
    i += m.Length;
    int j = rec.IndexOf('"', i);
    return j < 0 ? "?" : rec[i..j];
}

static int FirstDiff(byte[] a, byte[] b)
{
    int n = Math.Min(a.Length, b.Length);
    for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
    return n;
}

static int IndexOf(byte[] hay, byte[] needle)
{
    if (needle.Length == 0 || hay.Length < needle.Length) return -1;
    for (int i = 0; i <= hay.Length - needle.Length; i++)
    {
        int j = 0;
        while (j < needle.Length && hay[i + j] == needle[j]) j++;
        if (j == needle.Length) return i;
    }
    return -1;
}
