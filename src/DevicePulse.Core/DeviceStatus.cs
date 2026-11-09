namespace DevicePulse.Core;

/// <summary>
/// Connectivity status of a device, computed at query time from the elapsed
/// time since its last accepted heartbeat and the configured offline timeout.
/// </summary>
public enum DeviceStatus
{
    Online,
    Offline
}
