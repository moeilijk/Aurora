using ChromaEventEmitter;

// A controllable "fake game" that registers with the Chroma SDK as a Game and
// fires SetEventName events on demand — a fast, scriptable stand-in for
// 007: First Light so we can analyse the event-forwarding path (Aurora #292)
// without launching the real game.
//
// Usage:
//   ChromaEventEmitter [options] [eventName ...]
//
// Options:
//   --chromatic        Use RzChromatic64.dll PluginCore* (the exact path 007 uses).
//                      Default is the official RzChromaSDK64.dll.
//   --title <name>     App title to register as (default: "007FirstLight").
//   --auto <ms>        Auto-fire mode: cycle through the event list every <ms> ms
//                      until a key is pressed. Great for a steady, observable stream
//                      while attaching a debugger / dumping shared memory.
//   --once             Fire the given event name(s) once, then exit (UnInit).
//   -h | --help        Show this help.
//
// Interactive mode (no positional events and no --once):
//   type any text + Enter   -> SetEventName(text)
//   (empty) + Enter         -> repeat last event
//   :list                   -> print the sample event list
//   :N                      -> fire sample event number N
//   :q                      -> quit (UnInit)

var opts = Options.Parse(args);
if (opts.ShowHelp)
{
    Options.PrintHelp();
    return 0;
}

IChromaBackend chroma = opts.UseChromatic ? new ChromaticPlugin() : new ChromaSdkDirect();

Console.WriteLine($"Chroma Event Emitter");
Console.WriteLine($"  backend : {chroma.Name}");
Console.WriteLine($"  appName : {opts.Title}");
Console.WriteLine();

// --color RRGGBB: register as a game via the official SDK AND write a solid keyboard color,
// so the full Aurora render path (current-app + color buffer) can be validated without 007.
var colorArg = Array.IndexOf(args, "--color");
if (colorArg >= 0 && colorArg + 1 < args.Length && uint.TryParse(args[colorArg + 1], System.Globalization.NumberStyles.HexNumber, null, out var rgb))
{
    var direct = new ChromaSdkDirect();
    IChromaBackend backend = direct;
    var ai = APPINFOTYPE.CreateGame(opts.Title);
    Console.WriteLine($"InitSDK -> {RzResult.Describe(backend.InitSdk(ref ai))}");
    uint colorref = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF); // RRGGBB -> 0x00BBGGRR
    // Own a real foreground WINDOW (owned by ChromaEventEmitter.exe) so Aurora's foreground-process
    // detection matches this exe in AllChromaApps — a console window is owned by the terminal host.
    var t = new Thread(() =>
    {
        var form = new System.Windows.Forms.Form { Text = $"{opts.Title} — Chroma #{rgb:X6}", Width = 460, Height = 180 };
        var lbl = new System.Windows.Forms.Label { Dock = System.Windows.Forms.DockStyle.Fill, Text = $"Fake Chroma game. Keep THIS window focused.\nWriting keyboard #{rgb:X6} every 700ms." };
        form.Controls.Add(lbl);
        var timer = new System.Windows.Forms.Timer { Interval = 700 };
        timer.Tick += (_, _) => direct.SetKeyboardCustom(colorref);
        form.Load += (_, _) => { timer.Start(); form.Activate(); };
        form.FormClosed += (_, _) => { timer.Stop(); backend.UnInit(); };
        System.Windows.Forms.Application.Run(form);
    });
    t.SetApartmentState(ApartmentState.STA);
    t.Start();
    t.Join();
    return 0;
}

// --anim [RRGGBB]: PROVE the effect-rendering path. Create+play an in-memory keyboard animation of a
// known colour (default green 00FF00) and hold it, so the correlator can confirm it lands in 74164FAD.
var animArg = Array.IndexOf(args, "--anim");
if (animArg >= 0)
{
    int r = 0, g = 255, b = 0;
    if (animArg + 1 < args.Length && uint.TryParse(args[animArg + 1], System.Globalization.NumberStyles.HexNumber, null, out var arc))
    { r = (int)((arc >> 16) & 0xFF); g = (int)((arc >> 8) & 0xFF); b = (int)(arc & 0xFF); }
    AnimDemo.Run(opts.Title, r, g, b, 20);
    return 0;
}

// --game: faithfully emulate 007 First Light. Combines everything the real game presents to Synapse:
//   - process name 007FirstLight.exe (set via AssemblyName) so ChromaConnect recognises us as the game,
//   - a real FOREGROUND window owned by this process (focused) — a console is owned by the terminal host,
//   - registration as the Chroma app "007 First Light" via the PluginCore* path (exactly what 007 uses),
//   - the real 007 event names fired on a timer while the window is focused.
if (Array.IndexOf(args, "--game") >= 0)
{
    // --auto <ms> => cycle the event list. NO --auto => fire the given event(s) ONCE and HOLD
    // (the real semantics: an event's animation plays/loops until the NEXT event), so a single
    // `--game MainMenu_On` lets us see that one event's full sustained effect.
    int? ms = opts.AutoMs;
    IChromaBackend game = new ChromaticPlugin();
    var gameTitle = opts.TitleSet ? opts.Title : "007 First Light";
    var gi = APPINFOTYPE.CreateGame(gameTitle);
    Console.WriteLine($"InitSDK (007 First Light, PluginCore) -> {RzResult.Describe(game.InitSdk(ref gi))}");
    var list = opts.Events.Count > 0 ? opts.Events : SampleEvents.Default;
    void FireEv(string name)
    {
        var r = game.SetEventName(name);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] SetEventName(\"{name}\") -> {RzResult.Describe(r)}");
    }
    var idx = 0;
    var t = new Thread(() =>
    {
        var form = new System.Windows.Forms.Form { Text = "007 First Light", Width = 540, Height = 170 };
        var lbl = new System.Windows.Forms.Label { Dock = System.Windows.Forms.DockStyle.Fill,
            Text = ms is { } c
                ? $"Emulating 007FirstLight.exe — cycling {list.Count} events every {c}ms. Keep focused."
                : $"Emulating 007FirstLight.exe — fired {list.Count} event(s) once, HOLDING (effect persists until next event). Keep focused." };
        form.Controls.Add(lbl);
        System.Windows.Forms.Timer? timer = null;
        form.Load += (_, _) =>
        {
            form.Activate();
            if (ms is { } interval)
            {
                timer = new System.Windows.Forms.Timer { Interval = interval };
                timer.Tick += (_, _) => FireEv(list[idx++ % list.Count]);
                timer.Start();
            }
            else
            {
                foreach (var e in list) FireEv(e);   // fire once, then HOLD (no further events)
            }
        };
        form.FormClosed += (_, _) => { timer?.Stop(); game.UnInit(); };
        System.Windows.Forms.Application.Run(form);
    });
    t.SetApartmentState(ApartmentState.STA);
    t.Start();
    t.Join();
    return 0;
}

var appInfo = APPINFOTYPE.CreateGame(opts.Title);

long init;
try
{
    init = chroma.InitSdk(ref appInfo);
}
catch (DllNotFoundException ex)
{
    Console.Error.WriteLine($"FATAL: could not load the Chroma DLL: {ex.Message}");
    Console.Error.WriteLine("Is the Razer Chroma SDK / runtime installed? (rzsdkservice.exe)");
    return 2;
}

Console.WriteLine($"InitSDK -> {RzResult.Describe(init)}");
if (init != RzResult.Success)
{
    Console.Error.WriteLine("InitSDK failed — the Chroma service is likely not running.");
    // Continue anyway so SetEventName failures are also visible during testing.
}

void Fire(string name)
{
    var r = chroma.SetEventName(name);
    var ts = DateTime.Now.ToString("HH:mm:ss.fff");
    Console.WriteLine($"[{ts}] SetEventName(\"{name}\") -> {RzResult.Describe(r)}");
}

try
{
    if (opts.Once)
    {
        foreach (var e in opts.Events.Count > 0 ? opts.Events : SampleEvents.Default)
            Fire(e);
    }
    else if (opts.AutoMs is { } ms)
    {
        var list = opts.Events.Count > 0 ? opts.Events : SampleEvents.Default;
        Console.WriteLine($"Auto-firing {list.Count} events every {ms}ms. Press any key to stop.");
        var i = 0;
        while (!Console.KeyAvailable)
        {
            Fire(list[i % list.Count]);
            i++;
            Thread.Sleep(ms);
        }
        Console.ReadKey(intercept: true);
    }
    else
    {
        // Interactive
        var last = SampleEvents.Default[0];
        if (opts.Events.Count > 0)
            foreach (var e in opts.Events) { Fire(e); last = e; }

        Console.WriteLine();
        Console.WriteLine("Interactive mode. Type event name + Enter. :list, :N, :q for help.");
        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null || line == ":q") break;
            if (line.Length == 0) { Fire(last); continue; }
            if (line == ":list")
            {
                for (var n = 0; n < SampleEvents.Default.Count; n++)
                    Console.WriteLine($"  {n}: {SampleEvents.Default[n]}");
                continue;
            }
            if (line.StartsWith(':') && int.TryParse(line[1..], out var idx)
                && idx >= 0 && idx < SampleEvents.Default.Count)
            {
                last = SampleEvents.Default[idx];
                Fire(last);
                continue;
            }
            last = line;
            Fire(line);
        }
    }
}
finally
{
    var un = chroma.UnInit();
    Console.WriteLine($"UnInit -> {RzResult.Describe(un)}");
}

return 0;


// ---- helpers ----

internal static class SampleEvents
{
    // Real 007: First Light event names. The game uses ONLY SetEventName, no effect calls;
    // ChromaConnect recognises the 007 identity and renders the curated per-event effect.
    // The COMBAT events (Backstab / Melee_Punch / Melee_HeadHit) were captured LIVE from the
    // real game (correlator) and PROVEN to render DISTINCT effects: Melee_Punch = bright red,
    // Backstab = a darker red-purple, Open_Door/menu = the gold ambient. They are listed first
    // and interleaved so cycling shows clearly different lighting per event.
    public static readonly IReadOnlyList<string> Default = new[]
    {
        "Backstab",        // distinct: dark red-purple flash
        "MainMenu_On",     // gold ambient
        "Melee_Punch",     // distinct: bright red flash
        "Open_Door",       // gold/amber
        "Melee_HeadHit",   // distinct: red flash
        "Mantle",
        "Vault_DropDown",
        "Crouch_On",
        "Cinematic_On",
        "Cinematic_Off",
        "Land",
        "Jump",
        "Drive_Brake_Off",
        "Drive_Accelerate_Off",
        "Recon_Off",
        "Start",
        "PauseMenu_On",
        "PauseMenu_Off",
        "Interact_Environment",
        "Reborn",
        "Close_Door",
        "Climb_Jump",
        "Crouch_Off",
        "MainMenu_Off",
    };
}

internal sealed class Options
{
    public bool UseChromatic;
    public string Title = "007FirstLight";
    public bool TitleSet;
    public int? AutoMs;
    public bool Once;
    public bool ShowHelp;
    public List<string> Events = new();

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--chromatic": o.UseChromatic = true; break;
                case "--game": break;   // handled directly in Main; must NOT fall into Events
                case "--once": o.Once = true; break;
                case "-h" or "--help": o.ShowHelp = true; break;
                case "--title":
                    if (++i < args.Length) { o.Title = args[i]; o.TitleSet = true; }
                    break;
                case "--auto":
                    if (++i < args.Length && int.TryParse(args[i], out var ms)) o.AutoMs = ms;
                    else o.AutoMs = 500;
                    break;
                default:
                    o.Events.Add(args[i]);
                    break;
            }
        }
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
            """
            Chroma Event Emitter — fake SetEventName game for Aurora Chroma RE (#292)

            Usage:
              ChromaEventEmitter [options] [eventName ...]

            Options:
              --chromatic     Use RzChromatic64.dll PluginCore* (exact path 007 uses).
                              Default backend is the official RzChromaSDK64.dll.
              --title <name>  App title to register as (default: 007FirstLight).
              --auto <ms>     Auto-fire the event list every <ms> ms until a key press.
              --once          Fire the given event name(s) once, then exit.
              -h, --help      Show this help.

            Interactive mode (default): type an event name + Enter to fire it.
              (empty)=repeat last, :list=show samples, :N=fire sample N, :q=quit.
            """);
    }
}
