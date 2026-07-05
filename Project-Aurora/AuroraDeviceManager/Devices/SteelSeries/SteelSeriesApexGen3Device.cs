using System.ComponentModel;
using System.Diagnostics;
using Common;
using Common.Devices;
using Common.Utils;
using HidSharp;

namespace AuroraDeviceManager.Devices.SteelSeries;

// Direct HID driver for the Apex Pro Gen 3 family: no GameSense/GG needed, Aurora owns the board.
// Protocol (validated against SignalRGB 2.5.72 driving PID 0x1640 on 2026-07-05):
//   vendor interface (mi_01, max feature report 643) takes FEATURE reports with report id 0x00;
//   [0x00, 0x4B] enters software mode, [0x00, <modelByte>, <ledCount>, (<hidCode>, R, G, B)*] sets colours.
// LED ids are USB HID keyboard usage codes, the same table GameSense used (USBHIDCodes.cs).
public class SteelSeriesApexGen3Device : DefaultDevice
{
    private const int VendorId = 0x1038;
    private const int ReportLength = 642;
    private const byte SoftwareModeCommand = 0x4B;
    private const byte OledCommand = 0x61;
    private const int OledWidth = 128;
    private const int OledHeight = 40;

    // Second report byte differs per model; the device ignores the frame when it is wrong.
    private static readonly (int Pid, byte ModelByte, string Model)[] SupportedDevices =
    [
        (0x1640, 0x3A, "Apex Pro Gen 3"),
        (0x1642, 0x40, "Apex Pro TKL Gen 3"),
        (0x1644, 0x61, "Apex Pro TKL Gen 3 Wireless"),
        (0x1646, 0x21, "Apex Pro TKL Gen 3 Wireless (wired)"),
        (0x1648, 0x40, "Apex Pro Mini Gen 3"),
    ];

    private HidStream? _stream;
    private byte _modelByte;
    private byte[] _report = new byte[ReportLength];
    private byte[] _prevReport = new byte[ReportLength];

    private long _framesIn;
    private long _framesSkipped;
    private long _framesWritten;
    private readonly Stopwatch _statsWatch = Stopwatch.StartNew();
    private readonly Dictionary<byte, int> _hidSlot = new();

    private bool _oledClock;
    private int _lastClockMinute = -1;

    public override string DeviceName => "SteelSeries Apex Gen3";

    protected override void RegisterVariables(VariableRegistry variableRegistry)
    {
        base.RegisterVariables(variableRegistry);
        variableRegistry.Register($"{DeviceName}_oled_clock", true, "Clock (HH:mm) on the OLED screen");
    }

    protected override string DeviceInfo => _deviceInfo;
    private string _deviceInfo = "";

    protected override Task<bool> DoInitialize(CancellationToken cancellationToken)
    {
        foreach (var (pid, modelByte, model) in SupportedDevices)
        {
            // The vendor interface is the only one with feature reports large enough for a full colour frame.
            var hidDevice = DeviceList.Local.GetHidDevices(VendorId, pid)
                .FirstOrDefault(d => d.GetMaxFeatureReportLength() >= ReportLength);
            if (hidDevice == null)
                continue;

            try
            {
                if (!hidDevice.TryOpen(out _stream))
                {
                    LogError($"{model} found but the HID interface could not be opened (in use by GG/SignalRGB?)");
                    continue;
                }

                var reportLength = hidDevice.GetMaxFeatureReportLength();
                _report = new byte[reportLength];
                _prevReport = new byte[reportLength];
                _modelByte = modelByte;

                var softwareMode = new byte[reportLength];
                softwareMode[1] = SoftwareModeCommand;
                _stream.SetFeature(softwareMode);

                _deviceInfo = $"{model} (PID 0x{pid:X4})";
                LogInfo($"connected to {_deviceInfo}, report length {reportLength}");
                _oledClock = Global.DeviceConfig.VarRegistry.GetVariable<bool>($"{DeviceName}_oled_clock");
                _lastClockMinute = -1;
                IsInitialized = true;
                return Task.FromResult(true);
            }
            catch (Exception e)
            {
                LogError($"failed to initialize {model}", e);
                _stream?.Dispose();
                _stream = null;
            }
        }

        return Task.FromResult(false);
    }

    protected override Task Shutdown()
    {
        try
        {
            _stream?.Dispose();
        }
        catch { /* device may already be gone */ }

        _stream = null;
        _deviceInfo = "";
        IsInitialized = false;
        return Task.CompletedTask;
    }

    protected override Task<bool> UpdateDevice(Dictionary<DeviceKeys, SimpleColor> keyColors, DoWorkEventArgs e, bool forced = false)
    {
        if (!IsInitialized || _stream == null)
            return Task.FromResult(false);

        if (_oledClock)
            UpdateOledClock();

        _framesIn++;
        if (_statsWatch.ElapsedMilliseconds >= 10000)
        {
            LogInfo($"frames in={_framesIn} skipped={_framesSkipped} written={_framesWritten} (10s window)");
            _framesIn = _framesSkipped = _framesWritten = 0;
            _statsWatch.Restart();
        }

        Array.Clear(_report, 0, _report.Length);
        _hidSlot.Clear();
        _report[1] = _modelByte;

        var ledCount = 0;
        var maxLeds = (_report.Length - 3) / 4;
        foreach (var (key, color) in keyColors)
        {
            // The board follows the standard USB HID usage table (probe-verified: 0xE5=RShift,
            // 0xE7=RWin); only the apex/context key is vendor-specific at 0xF0.
            var hid = key == DeviceKeys.APPLICATION_SELECT ? (byte)0xF0 : SteelSeriesDevice.GetHIDCode(key);
            // 0x00 (legacy LOGO) floods the whole board; 0xE8-0xEF are GameSense G-key/logo
            // aliases that do not exist as LEDs on Gen 3 hardware.
            if (hid == 0x00 || hid == (byte)USBHIDCodes.ERROR || hid is >= 0xE8 and <= 0xEF)
                continue;

            var corrected = CommonColorUtils.CorrectWithAlpha(color);
            // Several DeviceKeys alias to one hid code (TILDE/OEM variants, slash variants);
            // unrendered aliases are black and must not overwrite a lit key.
            if (_hidSlot.TryGetValue(hid, out var slot))
            {
                if (corrected is { R: 0, G: 0, B: 0 })
                    continue;
                _report[slot + 1] = corrected.R;
                _report[slot + 2] = corrected.G;
                _report[slot + 3] = corrected.B;
                continue;
            }

            if (ledCount >= maxLeds)
                continue;

            var offset = 3 + ledCount * 4;
            _hidSlot[hid] = offset;
            _report[offset] = hid;
            _report[offset + 1] = corrected.R;
            _report[offset + 2] = corrected.G;
            _report[offset + 3] = corrected.B;
            ledCount++;
        }

        // The media/mute button next to the roller (led 251, "LCD Button") has no Aurora
        // DeviceKeys the layouts render; it sits directly above Num -, so follow that key.
        if (ledCount < maxLeds && keyColors.TryGetValue(DeviceKeys.NUM_MINUS, out var mediaColor))
        {
            var corrected = CommonColorUtils.CorrectWithAlpha(mediaColor);
            var offset = 3 + ledCount * 4;
            _report[offset] = 251;
            _report[offset + 1] = corrected.R;
            _report[offset + 2] = corrected.G;
            _report[offset + 3] = corrected.B;
            ledCount++;
        }

        _report[2] = (byte)ledCount;

        if (!forced && _report.AsSpan().SequenceEqual(_prevReport))
        {
            _framesSkipped++;
            return Task.FromResult(true);
        }

        try
        {
            _stream!.SetFeature(_report);
            _framesWritten++;
            (_prevReport, _report) = (_report, _prevReport);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            LogError("failed to write colour report", ex);
            return Task.FromResult(false);
        }
    }

    // Replaces the GameSense-dependent gamesense-essentials clock. Protocol from apex-tux,
    // probe-verified 2026-07-06: unnumbered feature report [0x00, 0x61, <bitmap>] padded to
    // the max report length; bitmap is SSD1306 page-major (5 pages x 128 bytes, each byte
    // 8 vertical pixels, bit 0 = topmost row of the page).
    private void UpdateOledClock()
    {
        var now = DateTime.Now;
        var minute = (int)(now.Ticks / TimeSpan.TicksPerMinute);
        if (minute == _lastClockMinute)
            return;

        var report = new byte[_report.Length];
        report[1] = OledCommand;
        DrawClock(report, now);

        try
        {
            _stream!.SetFeature(report);
            _lastClockMinute = minute;
        }
        catch (Exception ex)
        {
            LogError("failed to write OLED clock", ex);
            _lastClockMinute = minute; // do not retry every frame on a broken OLED path
        }
    }

    // segment bits: 1=top 2=right-top 4=right-bottom 8=bottom 16=left-bottom 32=left-top 64=middle
    private static readonly byte[] SevenSegment = [0b0111111, 0b0000110, 0b1011011, 0b1001111, 0b1100110, 0b1101101, 0b1111101, 0b0000111, 0b1111111, 0b1101111];

    private static void DrawClock(byte[] report, DateTime now)
    {
        void FillRect(int x, int y, int w, int h)
        {
            for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++)
            {
                if ((uint)xx >= OledWidth || (uint)yy >= OledHeight) continue;
                report[2 + (yy >> 3) * OledWidth + xx] |= (byte)(1 << (yy & 7));
            }
        }

        const int dw = 15, dh = 26, t = 3, top = 1;

        void DrawDigit(int x, int d)
        {
            var s = SevenSegment[d];
            if ((s & 1) != 0) FillRect(x, top, dw, t);
            if ((s & 2) != 0) FillRect(x + dw - t, top, t, dh / 2);
            if ((s & 4) != 0) FillRect(x + dw - t, top + dh / 2, t, dh / 2);
            if ((s & 8) != 0) FillRect(x, top + dh - t, dw, t);
            if ((s & 16) != 0) FillRect(x, top + dh / 2, t, dh / 2);
            if ((s & 32) != 0) FillRect(x, top, t, dh / 2);
            if ((s & 64) != 0) FillRect(x, top + dh / 2 - t / 2, dw, t);
        }

        var text = now.ToString("HHmm");
        ReadOnlySpan<int> xs = [22, 43, 70, 91];
        for (var i = 0; i < 4; i++)
            DrawDigit(xs[i], text[i] - '0');
        FillRect(62, top + 6, t, t);
        FillRect(62, top + dh - 9, t, t);

        // Abbreviated day name plus the Windows short-date format, centred under the time.
        var date = $"{now:ddd} {now:d}".ToUpperInvariant();
        var dateX = (OledWidth - date.Length * 6) / 2;
        for (var i = 0; i < date.Length; i++)
        {
            if (!SmallFont.TryGetValue(date[i], out var glyph))
                continue;
            for (var c = 0; c < 5; c++)
            for (var b = 0; b < 7; b++)
                if ((glyph[c] & (1 << b)) != 0)
                    FillRect(dateX + i * 6 + c, 32 + b, 1, 1);
        }
    }

    // 5x7 column font (bit 0 = top) for short-date output: digits plus common separators.
    private static readonly Dictionary<char, byte[]> SmallFont = new()
    {
        ['0'] = [0x3E, 0x51, 0x49, 0x45, 0x3E],
        ['1'] = [0x00, 0x42, 0x7F, 0x40, 0x00],
        ['2'] = [0x42, 0x61, 0x51, 0x49, 0x46],
        ['3'] = [0x21, 0x41, 0x45, 0x4B, 0x31],
        ['4'] = [0x18, 0x14, 0x12, 0x7F, 0x10],
        ['5'] = [0x27, 0x45, 0x45, 0x45, 0x39],
        ['6'] = [0x3C, 0x4A, 0x49, 0x49, 0x30],
        ['7'] = [0x01, 0x71, 0x09, 0x05, 0x03],
        ['8'] = [0x36, 0x49, 0x49, 0x49, 0x36],
        ['9'] = [0x06, 0x49, 0x49, 0x29, 0x1E],
        ['-'] = [0x08, 0x08, 0x08, 0x08, 0x08],
        ['/'] = [0x20, 0x10, 0x08, 0x04, 0x02],
        ['.'] = [0x00, 0x60, 0x60, 0x00, 0x00],
        [' '] = [0x00, 0x00, 0x00, 0x00, 0x00],
        ['A'] = [0x7E, 0x11, 0x11, 0x11, 0x7E],
        ['B'] = [0x7F, 0x49, 0x49, 0x49, 0x36],
        ['C'] = [0x3E, 0x41, 0x41, 0x41, 0x22],
        ['D'] = [0x7F, 0x41, 0x41, 0x22, 0x1C],
        ['E'] = [0x7F, 0x49, 0x49, 0x49, 0x41],
        ['F'] = [0x7F, 0x09, 0x09, 0x09, 0x01],
        ['G'] = [0x3E, 0x41, 0x49, 0x49, 0x7A],
        ['H'] = [0x7F, 0x08, 0x08, 0x08, 0x7F],
        ['I'] = [0x00, 0x41, 0x7F, 0x41, 0x00],
        ['J'] = [0x20, 0x40, 0x41, 0x3F, 0x01],
        ['K'] = [0x7F, 0x08, 0x14, 0x22, 0x41],
        ['L'] = [0x7F, 0x40, 0x40, 0x40, 0x40],
        ['M'] = [0x7F, 0x02, 0x0C, 0x02, 0x7F],
        ['N'] = [0x7F, 0x04, 0x08, 0x10, 0x7F],
        ['O'] = [0x3E, 0x41, 0x41, 0x41, 0x3E],
        ['P'] = [0x7F, 0x09, 0x09, 0x09, 0x06],
        ['Q'] = [0x3E, 0x41, 0x51, 0x21, 0x5E],
        ['R'] = [0x7F, 0x09, 0x19, 0x29, 0x46],
        ['S'] = [0x46, 0x49, 0x49, 0x49, 0x31],
        ['T'] = [0x01, 0x01, 0x7F, 0x01, 0x01],
        ['U'] = [0x3F, 0x40, 0x40, 0x40, 0x3F],
        ['V'] = [0x1F, 0x20, 0x40, 0x20, 0x1F],
        ['W'] = [0x3F, 0x40, 0x38, 0x40, 0x3F],
        ['X'] = [0x63, 0x14, 0x08, 0x14, 0x63],
        ['Y'] = [0x07, 0x08, 0x70, 0x08, 0x07],
        ['Z'] = [0x61, 0x51, 0x49, 0x45, 0x43],
    };
}
