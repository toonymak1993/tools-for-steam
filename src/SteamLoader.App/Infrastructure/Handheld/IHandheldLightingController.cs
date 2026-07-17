namespace SteamLoader.App.Infrastructure.Handheld;

internal interface IHandheldLightingController
{
    string Apply(HandheldLightingSettings settings);
}

internal static class HandheldLightingControllerFactory
{
    public static IHandheldLightingController Create(HandheldDeviceProfile device) => device.Id switch
    {
        "msi-claw-a8" => new MsiClawA8LightingController(),
        _ => throw new NotSupportedException($"RGB lighting is not supported by device adapter '{device.Id}'.")
    };
}
