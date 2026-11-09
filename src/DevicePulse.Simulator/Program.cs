using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

// ---------------------------------------------------------------------------
// DevicePulse.Simulator — simulates devices sending heartbeats to a
// DevicePulse API, deliberately injecting occasional duplicates and
// out-of-order messages to exercise the API's deduplication rules.
// ---------------------------------------------------------------------------

if (args.Any(a => a is "-h" or "--help"))
{
    Simulator.PrintHelp();
    return 0;
}

var options = SimulatorOptions.Parse(args);
if (options is null)
{
    return 1; // Parse printed the error and pointed at --help.
}

if (!Uri.TryCreate(options.ApiUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress)
    || baseAddress.Scheme is not ("http" or "https"))
{
    Console.Error.WriteLine($"Error: --api-url must be an absolute http(s) URL (got '{options.ApiUrl}').");
    return 1;
}

using var client = new HttpClient { BaseAddress = baseAddress, Timeout = Simulator.RequestTimeout };

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true; // stop the device loops gracefully instead of killing the process
    cancellation.Cancel();
};

Console.WriteLine($"DevicePulse.Simulator — {options.DeviceCount} device(s), one heartbeat every {options.IntervalMs} ms -> {baseAddress}");
Console.WriteLine("Roughly 1 in 20 heartbeats per device is a deliberate duplicate, 1 in 20 is out-of-order.");
Console.WriteLine("Press Ctrl+C to stop and see the run summary.");
Console.WriteLine();

var deviceTasks = new Task<DeviceSummary>[options.DeviceCount];
for (var i = 0; i < options.DeviceCount; i++)
{
    deviceTasks[i] = Simulator.RunDeviceAsync(client, $"device-{i}", options.IntervalMs, cancellation.Token);
}

var summaries = await Task.WhenAll(deviceTasks);

Console.WriteLine();
Console.WriteLine(
    $"Stopped. Sent: {summaries.Sum(s => s.Sent)}, accepted: {summaries.Sum(s => s.Accepted)}, " +
    $"rejected (duplicates/out-of-order): {summaries.Sum(s => s.Rejected)}, failed: {summaries.Sum(s => s.Failed)}.");

return 0;

internal sealed record SimulatorOptions(string ApiUrl, int DeviceCount, int IntervalMs)
{
    public const string DefaultApiUrl = "http://localhost:5254";

    public static SimulatorOptions? Parse(string[] args)
    {
        var apiUrl = DefaultApiUrl;
        var deviceCount = 3;
        var intervalMs = 1000;

        for (var i = 0; i < args.Length; i += 2)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--api-url" when value is not null:
                    apiUrl = value;
                    break;
                case "--devices" when value is not null && int.TryParse(value, out var devices) && devices >= 1:
                    deviceCount = devices;
                    break;
                case "--interval-ms" when value is not null && int.TryParse(value, out var milliseconds) && milliseconds >= 0:
                    intervalMs = milliseconds;
                    break;
                default:
                    Console.Error.WriteLine($"Error: unknown option or invalid/missing value at '{args[i]}' (value: '{value ?? "<none>"}').");
                    Console.Error.WriteLine("Run with --help to see usage.");
                    return null;
            }
        }

        return new SimulatorOptions(apiUrl, deviceCount, intervalMs);
    }
}

internal readonly record struct DeviceSummary(long Sent, long Accepted, long Rejected, long Failed);

internal static class Simulator
{
    internal const double DuplicateProbability = 0.05;
    internal const double OutOfOrderProbability = 0.05;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    internal static void PrintHelp()
    {
        Console.WriteLine("""
            DevicePulse.Simulator — simulates devices sending heartbeats to a DevicePulse API.

            Usage:
              dotnet run --project src/DevicePulse.Simulator [--api-url <url>] [--devices <n>] [--interval-ms <ms>]

            Options:
              --api-url <url>     Base URL of the DevicePulse API.
                                  Default: http://localhost:5254
              --devices <n>       Number of simulated devices (1 or more). Default: 3
              --interval-ms <ms>  Delay between heartbeats per device, in milliseconds. Default: 1000
              -h, --help          Show this help and exit.

            Behavior:
              - Devices are named device-0 .. device-(N-1). Each keeps its own 0-based,
                monotonically increasing sequence number; duplicate and out-of-order
                injections never decrease or reset that counter.
              - Roughly 1 in 20 heartbeats is deliberately sent as a duplicate (the
                previous sequence again) and roughly 1 in 20 as out-of-order (an older,
                already-superseded sequence) to exercise the API's deduplication rules.
                The API should normally answer these with accepted=false — unless
                earlier messages failed to reach the API, in which case the older
                sequence may still be accepted.
              - Press Ctrl+C to stop; a run summary is printed on exit.

            MVP limitation:
              On startup the simulator's sequence numbers reset to 0, while the API
              keeps the highest sequence it has seen. After a simulator restart the
              API therefore rejects heartbeats as out-of-order (accepted=false) until
              the new sequence track catches up. For a fresh demo, restart the API
              and the simulator TOGETHER.
            """);
    }

    internal static async Task<DeviceSummary> RunDeviceAsync(
        HttpClient client, string deviceId, int intervalMs, CancellationToken cancellationToken)
    {
        long sent = 0, accepted = 0, rejected = 0, failed = 0;
        var random = new Random();

        // The device's main, monotonic sequence counter: it only ever advances after
        // a normal send. Quirk injections resend older sequences without touching
        // this counter, so it can never decrease or reset.
        long nextSequence = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Decide what to send: usually the next sequence in the track, but
            // occasionally a duplicate or an older (out-of-order) sequence.
            // Note: an injected duplicate or out-of-order heartbeat may still be
            // ACCEPTED by the API if earlier messages failed to reach it (timeouts,
            // connection errors), because the API then never stored a higher
            // sequence for this device — the older one is no longer stale then.
            long sequenceToSend;
            var quirkLabel = string.Empty;

            var roll = random.NextDouble();
            if (nextSequence > 0 && roll < DuplicateProbability)
            {
                sequenceToSend = nextSequence - 1; // duplicate of the previously sent sequence
                quirkLabel = "[duplicate]";
            }
            else if (nextSequence > 1 && roll < DuplicateProbability + OutOfOrderProbability)
            {
                sequenceToSend = nextSequence - 2; // an already-superseded sequence
                quirkLabel = "[out-of-order]";
            }
            else
            {
                sequenceToSend = nextSequence; // the normal next heartbeat
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(RequestTimeout);

                using var response = await client.PostAsJsonAsync(
                    $"devices/{deviceId}/heartbeats",
                    new { sequenceNumber = sequenceToSend, sentAt = DateTimeOffset.UtcNow },
                    requestTimeout.Token);
                stopwatch.Stop();
                sent++;

                var acceptedFlag = response.IsSuccessStatusCode
                    ? ParseAccepted(await response.Content.ReadAsStringAsync(requestTimeout.Token))
                    : null;

                string outcome;
                if (acceptedFlag is true)
                {
                    accepted++;
                    outcome = "accepted=True ";
                }
                else if (acceptedFlag is false)
                {
                    rejected++;
                    outcome = "accepted=False";
                }
                else
                {
                    failed++;
                    outcome = $"unexpected response ({(int)response.StatusCode} {response.ReasonPhrase})";
                }

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] {deviceId,-10} seq {sequenceToSend,-6} {quirkLabel,-14} -> {outcome}  ({stopwatch.ElapsedMilliseconds} ms)");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Request timed out; the API may be slow or down. Keep the device running.
                failed++;
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] {deviceId,-10} seq {sequenceToSend,-6} {quirkLabel,-14} -> FAILED (timed out)");
            }
            catch (OperationCanceledException)
            {
                break; // Ctrl+C during the request
            }
            catch (HttpRequestException error)
            {
                // API unreachable; keep the device running so it recovers when the API returns.
                failed++;
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] {deviceId,-10} seq {sequenceToSend,-6} {quirkLabel,-14} -> FAILED ({error.Message})");
            }

            // Quirk sends never move the main sequence counter — only normal sends do.
            if (quirkLabel.Length == 0)
            {
                nextSequence++;
            }

            try
            {
                await Task.Delay(intervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break; // Ctrl+C during the wait
            }
        }

        return new DeviceSummary(sent, accepted, rejected, failed);
    }

    private static bool? ParseAccepted(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.GetProperty("accepted").GetBoolean();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException)
        {
            return null;
        }
    }
}
