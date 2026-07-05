using System;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using AuroraRgb.EffectsEngine;
using AuroraRgb.Modules;
using AuroraRgb.Modules.Razer;
using AuroraRgb.Profiles;
using Common.Devices;
using Common.Utils;
using WyvrnChroma;

namespace AuroraRgb.Settings.Layers;

public partial class ChromaCaptureLayerHandlerProperties : LayerHandlerProperties;

/// <summary>
/// #292 standalone, capture path: lights the keyboard from the per-key colours a Chroma game renders itself,
/// with NO Synapse/Chroma service. A native stub <c>RzChromaSDK64.dll</c> (system-wide, found by the game's own
/// <c>RzChromatic64.dll</c> after InitSDK succeeds) captures each keyboard frame the game renders from its
/// <c>.chroma</c> into <c>Global\WyvrnCapture</c> (<c>[0]=counter, [1..192]=0x00BBGGRR</c>).
///
/// The 007/Wyvrn <c>.chroma</c> are authored on the Razer <b>extended</b> keyboard grid — <b>8×24 = 192</b>
/// (device 3; confirmed from the file layout: 60 frames × 192 ints). The game hands that 8×24 field to
/// <c>CreateKeyboardEffect</c> verbatim. Aurora — like its own "Chroma Connect" (<see cref="RazerLayerHandler"/>)
/// — renders the classic 6×22 grid via <see cref="RazerLayoutMap.GenericKeyboard"/>, so we downsample the 8×24
/// field to 6×22 with <b>bilinear</b> sampling (smooth, no blocky nearest-neighbour steps) and map each key
/// exactly like Chroma Connect. This is the closest faithful reproduction of the authored wave achievable without
/// the proprietary Razer SDK's own extended→standard conversion.
/// </summary>
[LayerHandlerMeta(Name = "Chroma Capture (Wyvrn)", IsDefault = false)]
public sealed partial class ChromaCaptureLayerHandler() : LayerHandler<ChromaCaptureLayerHandlerProperties>("Chroma Capture Layer")
{
    private const string MappingName = @"Global\WyvrnCapture";
    private const int SrcRows = 8;
    private const int SrcCols = 24;
    private const int SrcCount = SrcRows * SrcCols; // 192
    private const int DstRows = 6;
    private const int DstCols = 22;

    private const uint FileMapRead = 0x0004;

    [LibraryImport("kernel32", EntryPoint = "OpenFileMappingW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenFileMappingW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [LibraryImport("kernel32", SetLastError = true)]
    private static partial IntPtr MapViewOfFile(IntPtr h, uint access, uint offHigh, uint offLow, UIntPtr bytes);
    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(IntPtr addr);
    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);

    private static readonly DeviceKeys[] Keys = Enum.GetValues<DeviceKeys>();

    // If the capture counter stops advancing (game closed / not publishing), fall back to nothing after this long
    // so lower layers / the default profile show through instead of a frozen frame.
    private const int StaleMs = 500;

    private readonly byte[] _r = new byte[SrcCount];
    private readonly byte[] _g = new byte[SrcCount];
    private readonly byte[] _b = new byte[SrcCount];
    private IntPtr _handle;
    private IntPtr _view;
    private bool _loggedFirst;
    private int _lastCounter = -1;
    private long _lastChangeTicks;

    protected override UserControl CreateControl() => new();

    public override EffectLayer Render(IGameState gameState)
    {
        if (!EnsureView())
            return EmptyLayer.Instance;

        var counter = Marshal.ReadInt32(_view, 0);
        if (counter == 0)
            return EmptyLayer.Instance;

        // Track freshness: only render while the game keeps publishing new frames.
        var now = Environment.TickCount64;
        if (counter != _lastCounter)
        {
            _lastCounter = counter;
            _lastChangeTicks = now;
        }
        else if (now - _lastChangeTicks > StaleMs)
        {
            return EmptyLayer.Instance; // stale -> let the default profile take over
        }

        for (var i = 0; i < SrcCount; i++)
        {
            var word = Marshal.ReadInt32(_view, (i + 1) * 4);
            _r[i] = (byte)(word & 0xFF);
            _g[i] = (byte)((word >> 8) & 0xFF);
            _b[i] = (byte)((word >> 16) & 0xFF);
        }

        // The game renders on Razer's EXTENDED grid (8×24), classic 6×22 keyboard block centred inside it.
        // Map each key straight to its exact captured cell at that offset — NO resample — same as the Wyvrn
        // event layer, so a captured frame lands identically to how Synapse lights it.
        const int rowOffset = (SrcRows - DstRows) / 2;  // 1
        const int colOffset = (SrcCols - DstCols) / 2;  // 1

        foreach (var key in Keys)
        {
            if (!RazerLayoutMap.GenericKeyboard.TryGetValue(key, out var pos))
                continue;

            var sr = pos[0] + rowOffset;
            var sc = pos[1] + colOffset;
            if (sr < 0 || sr >= SrcRows || sc < 0 || sc >= SrcCols)
                continue;
            var i = sr * SrcCols + sc;
            var color = CommonColorUtils.FastColor(_r[i], _g[i], _b[i]);
            EffectLayer.Set(key, in color);
        }

        if (!_loggedFirst)
        {
            _loggedFirst = true;
            Global.logger.Information("[ChromaCapture/Wyvrn] rendering 8x24 exact per-key mapping, counter={Counter} [{BuildTag}]",
                counter, ChromaEventModule.BuildTag);
        }

        return EffectLayer;
    }

    private bool EnsureView()
    {
        if (_view != IntPtr.Zero)
            return true;

        _handle = OpenFileMappingW(FileMapRead, false, MappingName);
        if (_handle == IntPtr.Zero)
            return false;

        _view = MapViewOfFile(_handle, FileMapRead, 0, 0, UIntPtr.Zero);
        if (_view != IntPtr.Zero)
            return true;

        CloseHandle(_handle);
        _handle = IntPtr.Zero;
        return false;
    }

    public override void Dispose()
    {
        if (_view != IntPtr.Zero)
            UnmapViewOfFile(_view);
        if (_handle != IntPtr.Zero)
            CloseHandle(_handle);
        _view = IntPtr.Zero;
        _handle = IntPtr.Zero;
        base.Dispose();
    }
}
