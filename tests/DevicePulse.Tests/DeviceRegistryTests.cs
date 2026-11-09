using System.Collections.Concurrent;
using DevicePulse.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePulse.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceRegistry"/>, all driven by a
/// <see cref="FakeTimeProvider"/> so offline detection is deterministic —
/// no test relies on real-time delays.
/// </summary>
public class DeviceRegistryTests
{
    private static readonly TimeSpan OfflineTimeout = TimeSpan.FromMinutes(1);

    private static Heartbeat HeartbeatAt(string deviceId, long sequence, DateTimeOffset receivedAt) =>
        new(deviceId, sequence, SentAt: receivedAt, ReceivedAt: receivedAt);

    [Fact]
    public void Accept_FirstHeartbeatWithSequenceZero_IsAccepted()
    {
        var clock = new FakeTimeProvider();
        var registry = new DeviceRegistry(clock, OfflineTimeout);
        var receivedAt = clock.GetUtcNow();

        var accepted = registry.Accept(new Heartbeat("device-1", SequenceNumber: 0, SentAt: receivedAt, ReceivedAt: receivedAt));

        Assert.True(accepted);
        var snapshot = registry.Get("device-1");

        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.LastSequenceNumber);
        Assert.Equal(receivedAt, snapshot.LastSeenAt);
        Assert.Equal(DeviceStatus.Online, snapshot.Status);
    }

    [Fact]
    public void Device_IsNotVisible_BeforeFirstAcceptedHeartbeat()
    {
        var clock = new FakeTimeProvider();
        var registry = new DeviceRegistry(clock, OfflineTimeout);

        // Nothing registered yet.
        Assert.Null(registry.Get("device-1"));
        Assert.Empty(registry.GetAll());

        // Malformed heartbeats are rejected before an entry can be created,
        // so they must not make the device surface in query results.
        var now = clock.GetUtcNow();
        Assert.False(registry.Accept(new Heartbeat("device-1", SequenceNumber: -1, SentAt: now, ReceivedAt: now)));
        Assert.False(registry.Accept(new Heartbeat("", SequenceNumber: 5, SentAt: now, ReceivedAt: now)));

        Assert.Null(registry.Get("device-1"));
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void Accept_NewerHeartbeat_UpdatesSequenceAndLastSeenAt()
    {
        var clock = new FakeTimeProvider();
        var registry = new DeviceRegistry(clock, OfflineTimeout);
        var t0 = clock.GetUtcNow();
        Assert.True(registry.Accept(HeartbeatAt("device-1", 0, t0)));

        clock.Advance(TimeSpan.FromSeconds(10));
        var t1 = clock.GetUtcNow();
        Assert.True(registry.Accept(HeartbeatAt("device-1", 1, t1)));

        var snapshot = registry.Get("device-1")!;
        Assert.Equal(1, snapshot.LastSequenceNumber);
        Assert.Equal(t1, snapshot.LastSeenAt);
        Assert.Equal(DeviceStatus.Online, snapshot.Status);
    }

    [Fact]
    public void Accept_DuplicateSequence_RejectedAndStateUnchanged()
    {
        var clock = new FakeTimeProvider();
        var registry = new DeviceRegistry(clock, OfflineTimeout);
        var t0 = clock.GetUtcNow();
        Assert.True(registry.Accept(HeartbeatAt("device-1", 5, t0)));

        clock.Advance(TimeSpan.FromSeconds(10));
        var accepted = registry.Accept(HeartbeatAt("device-1", 5, clock.GetUtcNow()));

        Assert.False(accepted);
        var snapshot = registry.Get("device-1")!;
        Assert.Equal(5, snapshot.LastSequenceNumber);
        Assert.Equal(t0, snapshot.LastSeenAt); // not refreshed by the duplicate
    }

    [Fact]
    public void Accept_OutOfOrderOlderSequence_RejectedAndStateUnchanged()
    {
        var clock = new FakeTimeProvider();
        var registry = new DeviceRegistry(clock, OfflineTimeout);
        var t0 = clock.GetUtcNow();
        Assert.True(registry.Accept(HeartbeatAt("device-1", 5, t0)));

        clock.Advance(TimeSpan.FromSeconds(10));
        var accepted = registry.Accept(HeartbeatAt("device-1", 3, clock.GetUtcNow()));

        Assert.False(accepted);
        var snapshot = registry.Get("device-1")!;
        Assert.Equal(5, snapshot.LastSequenceNumber);
        Assert.Equal(t0, snapshot.LastSeenAt);
    }

    [Fact]
    public void Get_ElapsedExactlyTimeout_IsStillOnline()
    {
        var clock = new FakeTimeProvider();
        var registry = new DeviceRegistry(clock, OfflineTimeout);
        var t0 = clock.GetUtcNow();
        registry.Accept(HeartbeatAt("device-1", 0, t0));

        clock.Advance(OfflineTimeout); // elapsed == timeout: the boundary is still Online

        Assert.Equal(DeviceStatus.Online, registry.Get("device-1")!.Status);
    }

    [Fact]
    public void Get_ElapsedBeyondTimeout_IsOffline()
    {
        var clock = new FakeTimeProvider();
        var registry = new DeviceRegistry(clock, OfflineTimeout);
        var t0 = clock.GetUtcNow();
        registry.Accept(HeartbeatAt("device-1", 0, t0));

        clock.Advance(OfflineTimeout + TimeSpan.FromMilliseconds(1)); // strictly past the timeout

        var snapshot = registry.Get("device-1")!;
        Assert.Equal(DeviceStatus.Offline, snapshot.Status);
        Assert.Equal(t0, snapshot.LastSeenAt); // raw evidence is preserved for clients
    }

    [Fact]
    public void Devices_AreIndependent()
    {
        var clock = new FakeTimeProvider();
        var registry = new DeviceRegistry(clock, OfflineTimeout);
        var t0 = clock.GetUtcNow();
        Assert.True(registry.Accept(HeartbeatAt("device-a", 0, t0)));
        Assert.True(registry.Accept(HeartbeatAt("device-b", 10, t0)));

        // A duplicate for A doesn't affect B's track, and each device keeps its own sequence.
        Assert.False(registry.Accept(HeartbeatAt("device-a", 0, t0)));
        Assert.True(registry.Accept(HeartbeatAt("device-a", 1, t0)));
        Assert.Equal(10, registry.Get("device-b")!.LastSequenceNumber);

        // Past the timeout both are offline together.
        clock.Advance(OfflineTimeout + TimeSpan.FromMilliseconds(1));
        Assert.Equal(DeviceStatus.Offline, registry.Get("device-a")!.Status);
        Assert.Equal(DeviceStatus.Offline, registry.Get("device-b")!.Status);

        // A fresh heartbeat brings only B back online; A stays offline.
        var t1 = clock.GetUtcNow();
        Assert.True(registry.Accept(HeartbeatAt("device-b", 11, t1)));
        Assert.Equal(DeviceStatus.Offline, registry.Get("device-a")!.Status);
        Assert.Equal(DeviceStatus.Online, registry.Get("device-b")!.Status);

        // And A's sequence track continues independently (2 > 1 is accepted).
        Assert.True(registry.Accept(HeartbeatAt("device-a", 2, t1)));
    }

    [Fact]
    public void Accept_ConcurrentOutOfOrderHeartbeats_SatisfiesConcurrencyInvariants()
    {
        var clock = new FakeTimeProvider();
        var registry = new DeviceRegistry(clock, OfflineTimeout);
        const int sequenceCount = 200; // 0-based sequences 0..199

        // Each sequence gets its own receive time and is sent TWICE (duplicate copies).
        var receivedAt = new DateTimeOffset[sequenceCount];
        var heartbeats = new List<Heartbeat>(capacity: sequenceCount * 2);
        for (var sequence = 0; sequence < sequenceCount; sequence++)
        {
            receivedAt[sequence] = clock.GetUtcNow();
            clock.Advance(TimeSpan.FromMilliseconds(1));
            heartbeats.Add(HeartbeatAt("device-1", sequence, receivedAt[sequence]));
            heartbeats.Add(HeartbeatAt("device-1", sequence, receivedAt[sequence]));
        }

        // Deterministic shuffle: arrival order is deliberately out of order, so
        // whether a given (non-maximal) sequence is accepted is non-deterministic.
        var rng = new Random(Seed: 20261107);
        var shuffled = heartbeats.OrderBy(_ => rng.Next()).ToList();

        var acceptedSequences = new ConcurrentBag<long>();
        Parallel.ForEach(shuffled, heartbeat =>
        {
            if (registry.Accept(heartbeat))
            {
                acceptedSequences.Add(heartbeat.SequenceNumber);
            }
        });

        var snapshot = registry.Get("device-1")!;

        // Invariant 1: the newest message always wins — final stored sequence is the maximum.
        // (The max sequence, whenever it arrives, is strictly greater than anything stored.)
        Assert.Equal(sequenceCount - 1, snapshot.LastSequenceNumber);

        // Invariant 2: LastSeenAt comes from the accepted heartbeat carrying that sequence.
        Assert.Equal(receivedAt[sequenceCount - 1], snapshot.LastSeenAt);

        // Invariant 3: no sequence is accepted twice (each was sent twice here).
        Assert.Equal(acceptedSequences.Distinct().Count(), acceptedSequences.Count);

        // Invariant 4: with out-of-order arrival, some stale copies are rejected —
        // accepted count never exceeds the number of distinct sequences.
        Assert.True(acceptedSequences.Count <= sequenceCount);
    }
}
