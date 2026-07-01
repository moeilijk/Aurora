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

        foreach (var key in Keys)
        {
            if (!RazerLayoutMap.GenericKeyboard.TryGetValue(key, out var pos))
                continue;

            // Bilinear-sample the 8×24 field at this key's proportional position in the 6×22 grid, then set it
            // exactly like Chroma Connect (RazerLayerHandler).
            var color = SampleBilinear(pos[0], pos[1]);
            EffectLayer.Set(key, in color);
        }

        if (!_loggedFirst)
        {
            _loggedFirst = true;
            Global.logger.Information("[ChromaCapture/Wyvrn] rendering 8x24->6x22 bilinear + GenericKeyboard, counter={Counter} [{BuildTag}]",
                counter, ChromaEventModule.BuildTag);
        }

        return EffectLayer;
    }

    // Bilinear sample of the source 8×24 field at the centre of destination cell (dr,dc) of the 6×22 grid.
    private System.Drawing.Color SampleBilinear(int dr, int dc)
    {
        var sy = (dr + 0.5) * SrcRows / DstRows - 0.5;
        var sx = (dc + 0.5) * SrcCols / DstCols - 0.5;
        var y0 = (int)Math.Floor(sy);
        var x0 = (int)Math.Floor(sx);
        var fy = sy - y0;
        var fx = sx - x0;

        var r = Lerp2(_r, x0, y0, fx, fy);
        var g = Lerp2(_g, x0, y0, fx, fy);
        var b = Lerp2(_b, x0, y0, fx, fy);
        return CommonColorUtils.FastColor((byte)r, (byte)g, (byte)b);
    }

    private static double Lerp2(byte[] ch, int x0, int y0, double fx, double fy)
    {
        var x1 = Math.Clamp(x0 + 1, 0, SrcCols - 1);
        var y1 = Math.Clamp(y0 + 1, 0, SrcRows - 1);
        var cx0 = Math.Clamp(x0, 0, SrcCols - 1);
        var cy0 = Math.Clamp(y0, 0, SrcRows - 1);
        double c00 = ch[cy0 * SrcCols + cx0], c10 = ch[cy0 * SrcCols + x1];
        double c01 = ch[y1 * SrcCols + cx0], c11 = ch[y1 * SrcCols + x1];
        var top = c00 + (c10 - c00) * fx;
        var bot = c01 + (c11 - c01) * fx;
        return top + (bot - top) * fy;
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
