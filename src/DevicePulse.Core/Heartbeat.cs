namespace DevicePulse.Core;

/// <summary>
/// Immutable heartbeat message.
/// <see cref="ReceivedAt"/> is always stamped by the server from the injected
/// <see cref="TimeProvider"/> at ingestion time; clients never supply it.
/// <see cref="SentAt"/> is client-supplied and informational only.
/// </summary>
public sealed record Heartbeat(
    string DeviceId,
    long SequenceNumber,
    DateTimeOffset SentAt,
    DateTimeOffset ReceivedAt);
