# NetDM packet completion / BYOND adapter contract (M23 Core)

**Status: implemented in \`NetDM.Core.RadioPacketTransport\`.** The existing
\`netdm-core\` named-pipe Host currently accepts \`route\`, \`stats\`, and
topology mutations. It does **not yet** implement live \`packet_send\` or
\`packet_receipts\` pipe operations. These DTOs are the bounded core
foundation for that future adapter; do not treat a \`route\` response as a
packet receipt or assume that the benchmark's simulated packets are
real player messages.

## Lifecycle and outcomes

\`Submit(requestId, route, bytes, tick, trafficClass, timeoutTicks?)\`
returns \`PacketSubmissionResult\`.

- \`Accepted=true\` means an entry was placed in the transport's bounded
  queue. It **does not** mean a hop was transmitted or the destination
  received an operation. The response contains \`StreamId\` (UUID for
  this transport instance), \`PacketId\` (integer unique within that
  instance), caller-supplied \`RequestId\` (opaque string up to 128
  characters), and \`Rejection=None\`.
- \`Accepted=false\` is a synchronous rejection, with \`PacketId=0\`.
  Possible \`Rejection\`: \`InvalidRoute\`, \`QueueFull\`, or
  \`BackgroundCongestion\`. No completion event will follow.
- Before an expensive route calculation, clients can use
  \`TryReserveAdmission(trafficClass, out rejection)\` to reserve a
  slot and receive the same \`QueueFull\` or \`BackgroundCongestion\`
  result. \`SubmitReserved(..., lease)\` consumes the lease exactly once.
  Disposing an unused lease releases the slot. A previously granted lease
  will not be retroactively rejected by the congestion policy.
- An accepted **tracked** packet eventually emits one terminal
  \`PacketDeliveryReceipt\` when the simulation advances to its result:
  \`Delivered/Delivered\`, \`Dropped/LinkFailure\`,
  \`Dropped/PacketTooLarge\`, \`Expired/TimeToLive\`, or
  \`Cancelled/CancelledByCaller\`. Every record has \`Sequence\`,
  \`PacketId\`, \`RequestId\`, \`TrafficClass\`, \`CreatedTick\`,
  \`CompletedTick\`, \`Bytes\` and \`SuccessfulHops\`.
- \`timeoutTicks\` is optional and must be between 1 and the transport's
  \`MaxAgeTicks\`. It is a simulation tick deadline, **not a wall-clock
  deadline**. With the standard tick driver, one tick represents
  approximately 100 ms; if the process stops advancing ticks, neither
  expiration nor receipt emission progresses.
- \`TryCancel(streamId, packetId)\` retires a still-active packet. It
  rejects mismatched stream epochs and does not generate a second event
  for packets already terminal.

## Polling and retention

\`CaptureReceipts(afterSequence: 0, limit: 128)\` returns a
\`PacketReceiptPage\` with:

- \`StreamId\`: must match the session held by the client;
- \`OldestSequence\`, \`LatestSequence\`: bounds of retained receipt
  history and last emitted sequence;
- \`NextSequence\`: pass this back as \`afterSequence\` to advance;
- \`Gap\`: \`true\` if older receipts were overwritten or the supplied
  cursor was ahead of the current stream. **Never interpret a missing
  event as successful delivery.** Resynchronize/fail outstanding
  operations as *unknown* and query application state before retrying;
- \`Receipts\`: at most \`limit\` terminal results, ordered by
  \`Sequence\`. Polling is non-destructive and replayable within the
  retention window.

The history uses a bounded in-memory FIFO, size
\`ReceiptHistoryCapacity\` (default 1,024; valid 0–65,536). With capacity
0, completions still occur but **no events are retained**; clients
intending to poll must configure a positive capacity. A consumer that
is slower than the terminal event rate may lose records: this is not an
unbounded durable queue or persistent log. A restarted host/transport
uses a new UUID even when numeric packet IDs repeat.

Multiple \`Submit\` calls using the *same* \`RequestId\` are distinct
packets; the Core does **not** guarantee idempotent application or dedup.
The future BYOND adapter must provide an idempotency table keyed by a
scoped player/session request ID (with explicit retention and retries)
before using acknowledgments to trigger irreversible game actions.

## C# example

\`\`\`csharp
using var lease = transport.TryReserveAdmission(
    PacketTrafficClass.Interactive, out var reason);
if (lease is null)
{
    // QueueFull or BackgroundCongestion: return immediate failure to DM.
    return;
}

// Route search happens outside the packet-forwarding gate.
// 'route' must be a RouteResult calculated for the desired endpoints.
var submit = transport.SubmitReserved(
    "session42:action83", route, bytes: 96, tick: currentTick,
    lease, timeoutTicks: 30);

if (!submit.Accepted)
{
    // A rejected submission has no later receipt.
    return;
}

var cursor = 0L;
var expectedStream = submit.StreamId;
var page = transport.CaptureReceipts(cursor, limit: 128);
if (page.StreamId != expectedStream || page.Gap)
{
    // The previous status is unknown; do not silently replay a game action.
    return;
}
foreach (var receipt in page.Receipts)
{
    if (receipt.PacketId == submit.PacketId)
    {
        // Delivered means the NetDM packet simulation finished its path,
        // not that a remote game entity executed the requested action.
    }
}
cursor = page.NextSequence;
\`\`\`

## Proposed wire format (not yet a supported pipe operation)

When implemented, the adapter should carry the equivalent data in a
versioned, bounded, framed message protocol. If the DTOs are serialized
using \`System.Text.Json\` Web defaults, property names are **camelCase**
and enums are **numeric**. Example (illustrative UUID and ticks):

\`\`\`json
{
  "streamId": "7cf416db-7f30-4e67-aed4-384250ee0b91",
  "oldestSequence": 3,
  "latestSequence": 4,
  "nextSequence": 4,
  "gap": false,
  "receipts": [
    {
      "sequence": 4,
      "packetId": 12,
      "requestId": "session42:action83",
      "trafficClass": 1,
      "status": 0,
      "reason": 0,
      "createdTick": 120,
      "completedTick": 125,
      "bytes": 96,
      "successfulHops": 5
    }
  ]
}
\`\`\`

Enum numeric codes:
- \`PacketTrafficClass\`: Background=0, Interactive=1
- \`PacketCompletionStatus\`: Delivered=0, Dropped=1, Expired=2, Cancelled=3
- \`PacketCompletionReason\`: Delivered=0, LinkFailure=1,
  PacketTooLarge=2, TimeToLive=3, CancelledByCaller=4
- \`PacketSubmissionRejection\`: None=0, InvalidRoute=1,
  QueueFull=2, BackgroundCongestion=3

## Remaining integration work

The production packet engine must be hosted independently of synthetic
benchmark runs, manage radio registrations/topology/ticks and bounded
reroute CPU workers, and expose named-pipe operations for submission,
cancellation, cursor polling and session resynchronization. The BYOND
x86 shim/DM adapter will need backpressure, lifetime management,
request-level deduplication, timeout/failure propagation, and
**application-level ACKs** that correlate a remote action's response
with the originating player request. This Core milestone supplies
terminal *transport* receipts, not end-to-end game command replies.
