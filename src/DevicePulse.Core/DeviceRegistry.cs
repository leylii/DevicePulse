using System.Collections.Concurrent;

namespace DevicePulse.Core;

/// <summary>
/// Thread-safe, in-memory store of the latest known state of each device.
/// Intended to be registered as a singleton. A <see cref="TimeProvider"/> and
/// the offline timeout are injected so offline detection is deterministic and
/// testable (no dependency on wall-clock time).
/// </summary>
public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, Device> _devices = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _offlineTimeout;

    public DeviceRegistry(TimeProvider timeProvider, TimeSpan offlineTimeout)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        if (offlineTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(offlineTimeout), "Offline timeout must be positive.");
        }

        _offlineTimeout = offlineTimeout;
    }

    /// <summary>
    /// Atomically compare-and-update the state of one device. A heartbeat is
    /// accepted only if its sequence number is strictly greater than the stored
    /// one — a single rule that rejects both duplicates and out-of-order
    /// (stale) messages. The first heartbeat for a device (sequence 0 included)
    /// is always accepted.
    /// <para>
    /// Concurrency: <see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd"/>
    /// guarantees a single entry per device ID; the compare and the update run
    /// inside <c>lock (device)</c>, so heartbeats for the same device serialize
    /// and state can never regress. Heartbeats for different devices proceed
    /// in parallel.
    /// </para>
    /// </summary>
    /// <returns>true if the heartbeat was accepted, false for duplicates or stale messages.</returns>
    public bool Accept(Heartbeat heartbeat)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);

        // Defensive guard: malformed input is normally rejected with HTTP 400 at
        // the controller, but the registry must never let a malformed message
        // create or corrupt state, even from non-HTTP callers.
        if (string.IsNullOrWhiteSpace(heartbeat.DeviceId) || heartbeat.SequenceNumber < 0)
        {
            return false;
        }

        var device = _devices.GetOrAdd(heartbeat.DeviceId, static id => new Device { DeviceId = id });

        lock (device)
        {
            // LastSequenceNumber is null until the first accepted heartbeat, so the
            // very first heartbeat — including sequence 0 — is always accepted.
            if (device.LastSequenceNumber.HasValue && heartbeat.SequenceNumber <= device.LastSequenceNumber.Value)
            {
                return false; // duplicate or out-of-order
            }

            device.LastSequenceNumber = heartbeat.SequenceNumber;
            device.LastSeenAt = heartbeat.ReceivedAt; // server-assigned, never client-supplied
            return true;
        }
    }

    /// <summary>
    /// Latest state of one device, or null if the device is unknown.
    /// Status is computed at query time: Online while elapsed time since the
    /// last accepted heartbeat is less than or equal to the offline timeout;
    /// Offline only once it strictly exceeds the timeout.
    /// </summary>
    public DeviceSnapshot? Get(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !_devices.TryGetValue(deviceId, out var device))
        {
            return null;
        }

        lock (device)
        {
            return Snapshot(device, _timeProvider.GetUtcNow());
        }
    }

    /// <summary>Latest state of all known devices. Status is computed at query time.</summary>
    public IReadOnlyList<DeviceSnapshot> GetAll()
    {
        var now = _timeProvider.GetUtcNow();

        return _devices.Values
            .Select(device => { lock (device) return Snapshot(device, now); })
            .OfType<DeviceSnapshot>() // skip entries with no accepted heartbeat yet
            .ToList();
    }

    /// <summary>
    /// Null while the entry has no accepted heartbeat yet — GetOrAdd creates the
    /// entry before the acceptance decision, and such entries must stay invisible
    /// to queries ("a device exists only after its first accepted heartbeat").
    /// </summary>
    private DeviceSnapshot? Snapshot(Device device, DateTimeOffset now) =>
        device.LastSequenceNumber.HasValue
            ? new DeviceSnapshot(
                device.DeviceId,
                device.LastSequenceNumber.Value,
                device.LastSeenAt,
                now - device.LastSeenAt <= _offlineTimeout ? DeviceStatus.Online : DeviceStatus.Offline)
            : null;
}
