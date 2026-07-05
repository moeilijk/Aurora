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
            if (trigger is DeviceUpdateTrigger deviceTrigger)
                deviceTrigger.MaxUpdateRate = cap;
        }
        Global.Logger.Information("{DeviceName} update rate capped at {Cap} updates/s", DeviceName, cap);
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