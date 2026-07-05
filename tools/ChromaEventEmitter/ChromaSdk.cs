using System.Runtime.InteropServices;

namespace ChromaEventEmitter;

/// <summary>
/// Official Razer Chroma SDK C-API binding, used to reproduce exactly what an
/// event-driven Chroma game (e.g. 007: First Light) does:
///   InitSDK(APPINFOTYPE)  ->  SetEventName("...")  ->  UnInit()
///
/// Two backends are provided so we can compare both code paths during analysis:
///   - <see cref="ChromaSdkDirect"/>     : RzChromaSDK64.dll   (the official SDK DLL)
///   - <see cref="ChromaticPlugin"/>     : RzChromatic64.dll   (PluginCore* wrapper the game uses;
///                                          its event/effect calls forward to RzChromaSDK64.dll)
/// </summary>
public interface IChromaBackend
{
    string Name { get; }
    long InitSdk(ref APPINFOTYPE appInfo);
    long SetEventName(string eventName);
    long UnInit();
}

/// <summary>
/// ChromaSDK::APPINFOTYPE from RzChromaSDKTypes.h. The SDK is built Unicode, so
/// TCHAR == WCHAR; marshal as ByValTStr with CharSet.Unicode. The nested Author
/// struct is flattened here (sequential layout is identical).
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct APPINFOTYPE
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Title;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string Description;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AuthorName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AuthorContact;
    public uint SupportedDevice; // bitmask, see DeviceSupport
    public uint Category;        // 1 = Utility, 2 = Game

    /// <summary>A "Game" app advertising support for every device class.</summary>
    public static APPINFOTYPE CreateGame(string title) => new()
    {
        Title = title,
        Description = "Chroma event emitter test harness (reproduces SetEventName games)",
        AuthorName = "Aurora RE",
        AuthorContact = "https://github.com/Aurora-RGB/Aurora",
        SupportedDevice = DeviceSupport.All,
        Category = AppCategory.Game,
    };
}

public static class DeviceSupport
{
    public const uint Keyboard   = 0x01;
    public const uint Mouse      = 0x02;
    public const uint Headset    = 0x04;
    public const uint Mousepad   = 0x08;
    public const uint Keypad     = 0x10;
    public const uint ChromaLink = 0x20;
    public const uint All = Keyboard | Mouse | Headset | Mousepad | Keypad | ChromaLink;
}

public static class AppCategory
{
    public const uint Utility = 1;
    public const uint Game = 2;
}

public static class RzResult
{
    public const long Success = 0; // RZRESULT_SUCCESS

    public static string Describe(long r) => r switch
    {
        0           => "RZRESULT_SUCCESS",
        0x57        => "RZRESULT_INVALID_PARAMETER",
        0x32        => "RZRESULT_NOT_SUPPORTED",
        0x4C7       => "RZRESULT_CANCELLED",
        0x490       => "RZRESULT_NOT_FOUND",
        0x70        => "RZRESULT_INSUFFICIENT_BUFFER",
        0x05        => "RZRESULT_ACCESS_DENIED",
        0x521       => "RZRESULT_RESOURCE_DISABLED",
        0x4005      => "RZRESULT_ALREADY_INITIALIZED",
        0x4002      => "RZRESULT_DEVICE_NOT_AVAILABLE",
        0x4001      => "RZRESULT_NOT_VALID_STATE",
        0x4004      => "RZRESULT_NO_MORE_ITEMS",
        unchecked((long)0xFFFFFFFF) => "RZRESULT_FAILED",
        _           => $"0x{r:X}",
    };
}

/// <summary>Official SDK: RzChromaSDK64.dll.</summary>
public sealed class ChromaSdkDirect : IChromaBackend
{
    public string Name => "RzChromaSDK64.dll (official SDK)";

    [DllImport("RzChromaSDK64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int InitSDK(ref APPINFOTYPE appInfo);

    [DllImport("RzChromaSDK64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int UnInit();

    [DllImport("RzChromaSDK64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetEventName(string name);

    // Keyboard CHROMA_STATIC = 4; STATIC_EFFECT_TYPE { COLORREF Color } (0x00BBGGRR).
    [DllImport("RzChromaSDK64.dll", ExactSpelling = true)]
    private static extern int CreateKeyboardEffect(int effect, ref uint param, ref Guid effectId);

    /// <summary>Fill the whole keyboard with one static color (COLORREF 0x00BBGGRR). Registers writes into the legacy Chroma buffer.</summary>
    public long SetKeyboardStatic(uint colorref)
    {
        var color = colorref;
        var id = Guid.Empty;
        return CreateKeyboardEffect(4, ref color, ref id);
    }

    // CHROMA_CUSTOM = 2: per-key 6x22 COLORREF grid (what RazerSdkReader's ChromaKeyboard reads).
    [DllImport("RzChromaSDK64.dll", EntryPoint = "CreateKeyboardEffect", ExactSpelling = true)]
    private static extern int CreateKeyboardEffectCustom(int effect, uint[] param, ref Guid effectId);

    [DllImport("RzChromaSDK64.dll", ExactSpelling = true)]
    private static extern int SetEffect(Guid effectId);

    /// <summary>Fill the whole 6x22 keyboard grid with one color via CHROMA_CUSTOM, then apply it with SetEffect.</summary>
    public long SetKeyboardCustom(uint colorref)
    {
        var grid = new uint[6 * 22];
        for (var i = 0; i < grid.Length; i++) grid[i] = colorref;
        var id = Guid.Empty;
        var create = CreateKeyboardEffectCustom(2, grid, ref id);
        if (create != 0) return create;
        return SetEffect(id); // <-- apply the created effect (was missing)
    }

    long IChromaBackend.InitSdk(ref APPINFOTYPE appInfo) => InitSDK(ref appInfo);
    long IChromaBackend.SetEventName(string eventName) => SetEventName(eventName);
    long IChromaBackend.UnInit() => UnInit();
}

/// <summary>
/// Positive proof of the EFFECT-RENDERING path: RzChromatic64.dll's ChromaAnimation API
/// (the CChromaEditorLibrary the disasm showed SetEventName uses to play "&lt;event&gt;_Keyboard.chroma").
/// Creates an in-memory keyboard animation of a known solid colour, plays it, and holds it so the
/// correlator can confirm whether that colour appears in the 74164FAD keyboard buffer. If it does,
/// it proves animations ARE what the engine renders into 74164FAD (i.e. where event effects come from);
/// the only thing 007 lacks is the .chroma animation DATA, not the rendering path.
/// </summary>
public static class AnimDemo
{
    private const string Dll = "RzChromatic64.dll";
    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int PluginInitSDK(ref APPINFOTYPE appInfo);
    [DllImport(Dll, ExactSpelling = true)] private static extern int PluginUninit();
    [DllImport(Dll, ExactSpelling = true)] private static extern int PluginCreateAnimationInMemory(int deviceType, int device);
    [DllImport(Dll, ExactSpelling = true)] private static extern int PluginMakeBlankFramesRGB(int animationId, int frameCount, float duration, int red, int green, int blue);
    [DllImport(Dll, ExactSpelling = true)] private static extern int PluginPlayAnimation(int animationId);
    [DllImport(Dll, ExactSpelling = true)] private static extern int PluginStopAnimation(int animationId);

    // EChromaSDKDeviceTypeEnum.DE_2D = 1 ; EChromaSDKDevice2DEnum.DE_Keyboard = 0
    public static void Run(string title, int r, int g, int b, int seconds)
    {
        var ai = APPINFOTYPE.CreateGame(title);
        Console.WriteLine($"PluginInitSDK -> {RzResult.Describe(PluginInitSDK(ref ai))}");
        var id = PluginCreateAnimationInMemory(1, 0);
        Console.WriteLine($"CreateAnimationInMemory(DE_2D, Keyboard) -> id={id}");
        Console.WriteLine($"MakeBlankFramesRGB(1 frame, RGB {r},{g},{b}) -> {PluginMakeBlankFramesRGB(id, 1, 1.0f, r, g, b)}");
        Console.WriteLine($"Playing animation for {seconds}s (measure 74164FAD now)...");
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            PluginPlayAnimation(id);
            Thread.Sleep(400);
        }
        PluginStopAnimation(id);
        Console.WriteLine($"PluginUninit -> {PluginUninit()}");
    }
}

/// <summary>
/// The exact path 007: First Light uses: RzChromatic64.dll PluginCore* wrappers.
/// PluginCoreSetEventName forwards to RzChromaSDK64.dll's SetEventName.
/// </summary>
public sealed class ChromaticPlugin : IChromaBackend
{
    public string Name => "RzChromatic64.dll (PluginCore* — same path as 007)";

    [DllImport("RzChromatic64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int PluginCoreInitSDK(ref APPINFOTYPE appInfo);

    [DllImport("RzChromatic64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int PluginCoreUnInit();

    [DllImport("RzChromatic64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int PluginCoreSetEventName(string name);

    long IChromaBackend.InitSdk(ref APPINFOTYPE appInfo) => PluginCoreInitSDK(ref appInfo);
    long IChromaBackend.SetEventName(string eventName) => PluginCoreSetEventName(eventName);
    long IChromaBackend.UnInit() => PluginCoreUnInit();
}
