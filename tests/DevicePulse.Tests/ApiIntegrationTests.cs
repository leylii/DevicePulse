using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevicePulse.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePulse.Tests;

/// <summary>
/// End-to-end HTTP tests against the real API host via
/// <see cref="WebApplicationFactory{Program}"/>. Each test gets its own host
/// (isolated in-memory registry) with a <see cref="FakeTimeProvider"/> replacing
/// TimeProvider.System, so offline transitions are advanced deterministically —
/// no test relies on real-time delays.
/// </summary>
public sealed class ApiIntegrationTests : IDisposable
{
    private const string SentAt = "2026-01-01T00:00:00Z";
    private static readonly TimeSpan OfflineTimeout = TimeSpan.FromSeconds(60);

    private readonly List<WebApplicationFactory<Program>> _factories = new();

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    /// <summary>
    /// Boots the API with the timeout from configuration (exercising the config
    /// binding path) and TimeProvider.System swapped for the test's fake clock.
    /// </summary>
    private (HttpClient Client, FakeTimeProvider Clock) CreateClient()
    {
        var clock = new FakeTimeProvider();
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["DevicePulse:OfflineTimeoutSeconds"] = "60",
                    }));

                // Runs after Program.cs, so this registration wins over TimeProvider.System.
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(clock);
                });
            });

        _factories.Add(factory);
        return (factory.CreateClient(), clock);
    }

    private static async Task<HttpResponseMessage> PostHeartbeatAsync(
        HttpClient client, string deviceId, object payload) =>
        await client.PostAsJsonAsync($"/devices/{deviceId}/heartbeats", payload);

    private static async Task<JsonElement> GetDeviceAsync(HttpClient client, string deviceId)
    {
        var response = await client.GetAsync($"/devices/{deviceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task PostHeartbeat_ValidRequest_ReturnsAcceptedTrueAndStoresState()
    {
        var (client, clock) = CreateClient();
        var beforePost = clock.GetUtcNow();

        var response = await PostHeartbeatAsync(client, "device-1",
            new { sequenceNumber = 0, sentAt = SentAt });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());

        var snapshot = await GetDeviceAsync(client, "device-1");
        Assert.Equal("device-1", snapshot.GetProperty("deviceId").GetString());
        Assert.Equal(0, snapshot.GetProperty("lastSequenceNumber").GetInt64());
        Assert.Equal(beforePost, snapshot.GetProperty("lastSeenAt").GetDateTimeOffset());
        Assert.Equal("Online", snapshot.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostHeartbeat_DuplicateSequence_ReturnsAcceptedFalseAndKeepsState()
    {
        var (client, clock) = CreateClient();
        var beforePost = clock.GetUtcNow();
        await PostHeartbeatAsync(client, "device-1", new { sequenceNumber = 7, sentAt = SentAt });

        clock.Advance(TimeSpan.FromSeconds(5));
        var duplicate = await PostHeartbeatAsync(client, "device-1",
            new { sequenceNumber = 7, sentAt = SentAt });

        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode); // not an error
        using var doc = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("accepted").GetBoolean());

        var snapshot = await GetDeviceAsync(client, "device-1");
        Assert.Equal(7, snapshot.GetProperty("lastSequenceNumber").GetInt64());
        Assert.Equal(beforePost, snapshot.GetProperty("lastSeenAt").GetDateTimeOffset()); // not refreshed
    }

    [Fact]
    public async Task PostHeartbeat_OutOfOrderSequence_ReturnsAcceptedFalseAndKeepsState()
    {
        var (client, _) = CreateClient();
        await PostHeartbeatAsync(client, "device-1", new { sequenceNumber = 5, sentAt = SentAt });

        var stale = await PostHeartbeatAsync(client, "device-1",
            new { sequenceNumber = 3, sentAt = SentAt });

        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        using var doc = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("accepted").GetBoolean());

        var snapshot = await GetDeviceAsync(client, "device-1");
        Assert.Equal(5, snapshot.GetProperty("lastSequenceNumber").GetInt64());
    }

    [Fact]
    public async Task PostHeartbeat_MalformedPayloads_Returns400WithoutRegisteringDevice()
    {
        var (client, _) = CreateClient();

        var malformedPayloads = new object[]
        {
            new { sequenceNumber = -1, sentAt = SentAt },                       // negative sequence
            new { sequenceNumber = 1, sentAt = "not-a-date" },                  // unparseable sentAt
            new { },                                                            // sequenceNumber and sentAt missing
            new Dictionary<string, object?> { ["sentAt"] = SentAt },            // sequenceNumber missing
            new Dictionary<string, object?> { ["sequenceNumber"] = 1 },         // sentAt missing
            new { sequenceNumber = 1, sentAt = SentAt, deviceId = "device-2" }, // body deviceId mismatches route
        };

        for (var i = 0; i < malformedPayloads.Length; i++)
        {
            var response = await PostHeartbeatAsync(client, "device-1", malformedPayloads[i]);
            Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"payload[{i}] should return 400.");
        }

        // No malformed request may have registered the device.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/devices/device-1")).StatusCode);

        var allResponse = await client.GetAsync("/devices");
        using var doc = JsonDocument.Parse(await allResponse.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task PostHeartbeat_ClientSentReceivedAt_IsIgnoredAndServerStampsItsOwn()
    {
        var (client, clock) = CreateClient();
        var beforePost = clock.GetUtcNow();

        // Client tries to fake freshness with a far-future receivedAt.
        var response = await PostHeartbeatAsync(client, "device-1",
            new { sequenceNumber = 1, sentAt = SentAt, receivedAt = "2099-01-01T00:00:00Z" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // extra field is ignored, not an error
        var snapshot = await GetDeviceAsync(client, "device-1");
        Assert.Equal(beforePost, snapshot.GetProperty("lastSeenAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task GetDevice_UnknownDevice_Returns404()
    {
        var (client, _) = CreateClient();

        var response = await client.GetAsync("/devices/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAllDevices_ReturnsAllRegisteredDevices()
    {
        var (client, _) = CreateClient();
        await PostHeartbeatAsync(client, "device-a", new { sequenceNumber = 0, sentAt = SentAt });
        await PostHeartbeatAsync(client, "device-b", new { sequenceNumber = 5, sentAt = SentAt });

        var response = await client.GetAsync("/devices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var deviceIds = doc.RootElement.EnumerateArray()
            .Select(device => device.GetProperty("deviceId").GetString())
            .OrderBy(id => id)
            .ToArray();
        Assert.Equal(new[] { "device-a", "device-b" }, deviceIds);
    }

    [Fact]
    public async Task Device_GoesOfflineOnlyAfterTimeoutElapses_ThenRecoversWithNewHeartbeat()
    {
        var (client, clock) = CreateClient();
        await PostHeartbeatAsync(client, "device-1", new { sequenceNumber = 0, sentAt = SentAt });

        // Exactly at the boundary: still Online.
        clock.Advance(OfflineTimeout);
        Assert.Equal("Online", (await GetDeviceAsync(client, "device-1")).GetProperty("status").GetString());

        // Strictly past the timeout: Offline.
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("Offline", (await GetDeviceAsync(client, "device-1")).GetProperty("status").GetString());

        // A fresh, newer heartbeat brings the device back Online.
        await PostHeartbeatAsync(client, "device-1", new { sequenceNumber = 1, sentAt = SentAt });
        Assert.Equal("Online", (await GetDeviceAsync(client, "device-1")).GetProperty("status").GetString());
    }
}
