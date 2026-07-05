using AuroraDeviceManager.Utils;
using Common.Devices;
using IronPython.Runtime.Operations;
using RGB.NET.Core;
using RGB.NET.Devices.SteelSeries;

namespace AuroraDeviceManager.Devices.RGBNet.Implementations;

public class SteelSeriesRgbNetDevice() : RgbNetDevice(true)
{
    private static readonly string SsEngineProcess = "SteelSeriesEngine".lower();
    private static readonly string SsGgProcess = "SteelSeriesGG".lower();

    private bool _sdkDetectedOff;

    protected override SteelSeriesDeviceProvider Provider => SteelSeriesDeviceProvider.Instance;

    public override string DeviceName => "SteelSeries (RGB.NET)";

    protected override void RegisterVariables(VariableRegistry variableRegistry)
    {
        base.RegisterVariables(variableRegistry);

        variableRegistry.Register($"{DeviceName}_update_rate_cap", 15, "Max updates per second (0 = uncapped)",
            remark: "GameSense-driven per-key lighting flickers when repainted too often (gamesense-sdk#34)");
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();

        var cap = Global.DeviceConfig.VarRegistry.GetVariable<int>($"{DeviceName}_update_rate_cap");
        if (cap <= 0)
            return;

        foreach (var (_, trigger) in Provider.UpdateTriggers)
        {
            // RGB.NET 3.2.0 consumes MaxUpdateRate as a PERIOD in seconds despite its name:
            // UpdateFrequency = MaxUpdateRate and the update loop sleeps UpdateFrequency*1000 ms.
            if (trigger is DeviceUpdateTrigger deviceTrigger)
                deviceTrigger.MaxUpdateRate = 1.0 / cap;
        }
        Global.Logger.Information("{DeviceName} update rate capped at {Cap} updates/s (period {Period}ms)",
            DeviceName, cap, 1000 / cap);
    }

    protected override async Task ConfigureProvider(CancellationToken cancellationToken)
    {
        await base.ConfigureProvider(cancellationToken);

        var isSteelGgRunning = ProcessUtils.IsProcessRunning(SsEngineProcess);
        var isSteelEngineRunning = ProcessUtils.IsProcessRunning(SsGgProcess);

        if (!(isSteelGgRunning && isSteelEngineRunning))
        {
            _sdkDetectedOff = true;
            throw new DeviceProviderException(new ApplicationException("SteelSeries Engine is not running!"), false);
        }

        if (_sdkDetectedOff)
        {
            await Task.Delay(5000, cancellationToken);
        }

        _sdkDetectedOff = false;
    }
}