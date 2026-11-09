using DevicePulse.Core;
using Microsoft.AspNetCore.Mvc;

namespace DevicePulse.Api.Controllers;

[ApiController]
[Route("devices")]
public sealed class DevicesController : ControllerBase
{
    private readonly DeviceRegistry _registry;
    private readonly TimeProvider _timeProvider;

    public DevicesController(DeviceRegistry registry, TimeProvider timeProvider)
    {
        _registry = registry;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Ingests one heartbeat. Returns 200 with accepted=false for duplicates and
    /// out-of-order messages (protocol-level outcomes, not errors) and 400 for
    /// malformed input. ReceivedAt is stamped here from the injected
    /// TimeProvider — clients never supply it.
    /// </summary>
    [HttpPost("{id}/heartbeats")]
    [ProducesResponseType(typeof(HeartbeatResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult PostHeartbeat(string id, [FromBody] HeartbeatRequest request)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new ProblemDetails { Title = "Device ID must not be empty." });
        }

        // When the body carries a device ID it must agree with the route; otherwise
        // it is optional and the route ID is authoritative.
        if (request.DeviceId is not null && !string.Equals(request.DeviceId, id, StringComparison.Ordinal))
        {
            return BadRequest(new ProblemDetails { Title = "Device ID in the body, when provided, must match the route." });
        }

        if (request.SequenceNumber is null)
        {
            return BadRequest(new ProblemDetails { Title = "sequenceNumber is required." });
        }

        if (request.SequenceNumber.Value < 0)
        {
            return BadRequest(new ProblemDetails { Title = "sequenceNumber must not be negative." });
        }

        if (request.SentAt is null)
        {
            return BadRequest(new ProblemDetails { Title = "sentAt is required." });
        }

        var heartbeat = new Heartbeat(
            DeviceId: id,
            SequenceNumber: request.SequenceNumber.Value,
            SentAt: request.SentAt.Value,
            ReceivedAt: _timeProvider.GetUtcNow()); // server-stamped; any client value is ignored

        return Ok(new HeartbeatResponse(_registry.Accept(heartbeat)));
    }

    /// <summary>Latest state of all known devices, with status computed at query time.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<DeviceSnapshot>), StatusCodes.Status200OK)]
    public IActionResult GetAllDevices() => Ok(_registry.GetAll());

    /// <summary>Latest state of one device; 404 if the device is unknown.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(DeviceSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetDevice(string id)
    {
        var snapshot = _registry.Get(id);
        return snapshot is null ? NotFound() : Ok(snapshot);
    }
}

/// <summary>
/// Client-supplied heartbeat payload. Deliberately has no ReceivedAt field:
/// the server stamps it, and any receivedAt sent in the JSON body is ignored.
/// </summary>
public sealed record HeartbeatRequest(long? SequenceNumber, DateTimeOffset? SentAt, string? DeviceId);

/// <summary>Outcome of heartbeat ingestion: false for duplicates and stale messages.</summary>
public sealed record HeartbeatResponse(bool Accepted);
