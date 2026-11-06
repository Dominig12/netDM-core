# BYOND x86 → NetDM.Host x64 native bridge

## Architecture

```text
BYOND DreamDaemon (32-bit / Win32)
  | call_ext("netdm_byond.dll", "netdm_send")(json)
  | call_ext("netdm_byond.dll", "netdm_poll")()
  v
netdm_byond.dll (native x86 C++, no CLR dependency)
  | bounded queues + background worker; connection state + overflow counter
  | named pipe: \\.\pipe\netdm-core (UTF-8 JSON lines)
  v
NetDM.Host (64-bit .NET 10)
  | PipeHostedService [JSON request correlation]
  | GameNetworkService [independent, live game graph and radio medium]
  | PriorityWorkScheduler -> RadioPacketTransport -> PacketTickRunner (100ms)
  | bounded receipt stream -> pipe -> shim -> BYOND
```

This is a **local trusted-process development bridge**, not a network
server. The named-pipe endpoint is not authenticated and must only be
available to the intended local game server account. DreamDaemon must
permit trusted DLL access; on newer BYOND versions use `call_ext()`,
not regular proc `call()`. Native exports use the classic string ABI
with `__cdecl`; all arguments passed to DLL are strings. Consult
BYOND's official `call_ext` documentation for DLL trust rules.

## Build

Run in PowerShell with Visual Studio 2022 or 2026 C++ toolchain installed
(the test script automatically selects the installed Win32 generator):

```powershell
dotnet build src/NetDM.Host/NetDM.Host.csproj -c Release
cmake -S native/byond -B native/byond/build -G "Visual Studio 18 2026" -A Win32
# Or use -G "Visual Studio 17 2022" on VS 2022 machines.
cmake --build native/byond/build --config Release
# Built: native/byond/build/Release/netdm_byond.dll
```

Use a **Win32** DLL even though Host is x64. It has no .NET dependency.
The DLL must be visible in the BYOND bin/trusted path; see official docs.

The automated integration harness builds and launches the x64 Host and
tests the 32-bit native exports against its real named-pipe service:

```powershell
./scripts/test-byond-bridge.ps1
```

## DM integration

```dm
#include "dm/netdm_bridge.dm"

// In your SS13 networking subsystem (pseudo-integration):
var/datum/netdm_bridge/netdm

// Initialize once, after Host has started:
netdm = new
netdm.initialize()

netdm.game_add_node(1, 0, 0)
netdm.game_add_node(2, 10, 0)
netdm.game_radio(1, 14, 1)
netdm.game_radio(2, 14, 1)

// The return value is the *native outbound queue command ID*.
// The pipe will return a separate packet acceptance payload.
var/command_id = netdm.packet_send(1, 2, 96, "session42:cmd83", TRUE, 30)

// On each subsystem tick, bounded work with no blocking I/O:
netdm.pump(32)
netdm.request_receipts(64)
```

Do not block the BYOND tick awaiting `packet_send` completion. The
`pump` return contains decoded response objects. Override the
`on_packet_submitted`, `on_packet_receipt`,
`on_connection_lost`, `on_resync_required` callbacks and store
application request state yourself.

## Supported JSON pipe commands

Each JSON-line message has unique `id` (caller correlation token)
and `op`; response echoes `id` with `ok`.

- `game_add_node`: `a` node ID, `x`, `y`, `z`;
  `game_move_node`: same fields.
- `game_remove_node`: `a` ID.
- `game_connect`: `a`, `b` IDs, `cost`; `game_disconnect` same.
- `game_batch`: `changes` array of 1..4096 topology commands.
  Both the radio grid and physical topology are published atomically
  by `SharedMediumNetwork.ApplyExternalTopology`.
- `game_radio`: `a`, `enabled` (false removes the radio profile),
  `range`, `channel`, `radioCost`.
- `game_stats`: packet stats, radio stats, game stream UUID,
  ticking counters and node counts.
- `packet_send`: `a`, `b`, `bytes`, `requestId` (max 128 chars),
  `trafficClass` (0 background, 1 interactive), optional
  `timeoutTicks` (1..900). Its response has `submission` with
  `accepted`, `streamId`, `packetId` and `rejection`
  (0 none / 1 invalid route / 2 full / 3 congestion / 4 route
  scheduler busy). `ok` means the command was parsed, not
  that it was delivered.
- `packet_receipts`: `afterSequence` (default 0), `limit`
  (1..256, default 128), optional `streamId`. The response has
  `receiptPage` with `streamId`, `gap`, `nextSequence` and
  terminal events. An invalid stream returns `stream_changed`.
- `packet_cancel`: `streamId`, `packetId`. Response
  `cancelled=true` only for an outstanding packet in that stream.

**Important boundaries and failure behavior**

- A native `netdm_send` result of `"1"` means only the JSON request
  entered the native outbound queue; `submission.accepted=true`
  means only the C# transport enqueued the packet; a
  `status=Delivered` transport receipt means the simulated radio
  route completed. None indicates the target game object actually
  executed a command; the receiving DM system must send a separate
  **application-level ACK**.
- On disconnect, the shim clears unsent commands but **never replays**
  already submitted commands because it cannot know whether the x64
  Host processed them. It emits a `bridge=disconnected` event.
  `netdm_status` exposes `lostResponses` for native inbox overflow.
  Consumer should mark in-flight requests `unknown`, resynchronize
  using the game `streamId`/receipt cursor and verify target state
  before retrying irreversible operations.
- `PacketId` is local to one `StreamId` and may repeat after Host
  restart. `RequestId` is an opaque correlation tag, not replay
  protection. Duplicate send commands create duplicate packets.
- The engine has its **own** topology, separate from the legacy
  observability graph and benchmark. This prevents benchmark resets
  from invalidating an active gameplay network.
- The current wire format is a **development JSON-lines protocol**,
  capped at 64 KiB incoming / 60 KiB shim outbound. The native DLL
  bounds its outbound queue (256 requests) and inbound mailbox
  (1024 replies); the host bounds route workers, packet capacity,
  reroute tickets and receipt retention (8192 events for gameplay).
  High-volume use will require a versioned framed protocol, security,
  admission semantics and DM lifecycle integration.
- IDs crossing DM as JSON numbers should remain within BYOND's
  numerical precision; a future version should send 64-bit IDs as
  decimal strings to eliminate floating-point truncation.
- No bundled DreamDaemon executable/compiler in GitHub CI: **Win32
  call_ext ABI is compile/link/run-tested using a genuine 32-bit
  Windows test process, but the final BYOND DM compile/runtime
  and server trust settings still require verification in BYOND.**


## Live SS13 topology in Observatory

Open `http://127.0.0.1:5087` after starting NetDM.Host, choose
**SS13 · LIVE GAME** in *Topology explorer*, and select a BYOND Z-level.
**FIT** frames game nodes on the selected layer. The viewer refreshes
as devices are added, removed, moved or connected. Physical cables are
shown as edges; wireless occupancy as channel-domain cells without
creating fake pairwise radio edges. Selecting a device shows its ID,
coordinates, wired link count, radio range and channel.

**BENCHMARK** still reads its independent synthetic graph.
`GET /api/game/summary` shows graph counts and Z-level bounds;
`GET /api/game/map` accepts the benchmark map viewport filters but reads
the actual `GameNetworkService.Snapshot`.

New local pipe commands: `game_reset` atomically clears the previous
SS13 graph; `game_radio_batch` applies 1..256 profiles. Only one SS13
process may own the Host game graph at a time: do not attach independent
game servers to one development Host. HTTP observation is localhost-only.
