namespace DevicePulse.Core;

/// <summary>
/// Immutable point-in-time view of a device, including its status computed
/// at query time. This is what query methods return instead of the live
/// mutable <see cref="Device"/> entry, so callers get a consistent,
/// race-free read.
/// </summary>
public sealed record DeviceSnapshot(
    string DeviceId,
    long LastSequenceNumber,
    DateTimeOffset LastSeenAt,
    DeviceStatus Status);
