namespace DevicePulse.Core;

/// <summary>
/// Mutable per-device storage entry held by <see cref="DeviceRegistry"/>.
/// All reads and writes happen under <c>lock (device)</c> inside the registry,
/// so concurrent heartbeats for the same device serialize while other devices
/// proceed in parallel. External callers only ever see immutable
/// <see cref="DeviceSnapshot"/> values.
/// </summary>
public sealed class Device
{
    public required string DeviceId { get; init; }

    /// <summary>
    /// Highest accepted sequence number so far; null until the first heartbeat
    /// is accepted. Sequence numbers are 0-based, so 0 is a valid first value
    /// and cannot double as the "nothing accepted yet" marker.
    /// </summary>
    public long? LastSequenceNumber { get; internal set; }

    /// <summary>Server-assigned receipt time of the last accepted heartbeat.</summary>
    public DateTimeOffset LastSeenAt { get; internal set; }
}
