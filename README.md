# netDM-core

Independent **C# / .NET 10** network simulation kernel for Space Station 13 / BYOND.

**Status:** prototype / developer preview. The graph, router, multi-threaded host,
real workload generator and interactive visualizer are implemented. BYOND integration,
production IPC hardening and performance SLO validation are not implemented yet.

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```shell
dotnet build src/NetDM.Host/NetDM.Host.csproj -c Release
dotnet run --project tests/NetDM.SmokeTests -c Release
dotnet run --project src/NetDM.Host -c Release
```

Open **http://127.0.0.1:5087/** to use **Network Observatory**.

The host runs a local HTTP dashboard and an independent local named-pipe server
(`netdm-core`). The HTTP server is **loopback-only** and unauthenticated;
do not expose it to external clients or production players.

## Network Observatory

- Canvas topology explorer: pan, wheel zoom, fit view, click nodes to inspect links and traffic
- Viewport-based spatial queries, bounded to 6,000 visible nodes per response (8,000 maximum via API)
- Actual routing activity heat from sampled traversed paths (1 in 16 successful routes)
- Observed routes/s, mutations/s, cache hits, graph versions, and latency p50/p95/p99
- Live throughput, latency and managed-memory history
- Configurable benchmarks: node count, target routes/s, moving nodes/s,
  link changes/s, duration and deterministic random seed
- Light, Standard and Stress presets; Stop and JSON export buttons

**Benchmark model:** Build a connected grid with redundant cross-links, then apply
simultaneous random weighted route queries, 2-D movement mutations and random edge
changes. Route generation is best-effort, limited by actual CPU throughput. The UI
reports measured throughput; input `RoutesPerSecond` is a *target*, not a promise.
The benchmark is synthetic and does not yet simulate BYOND's event rates, radio
attenuation, real map wiring, network security, or packet-level traffic.

## Core capabilities

- Stable 64-bit node IDs and undirected weighted edges
- Atomic mutation batches: add/remove/move nodes and connect/disconnect edges
- Immutable topology snapshots; concurrent readers do not block topology writers
- Spatial hash index (32-unit grid cells) and bounded viewport queries
- Separate topology and connectivity versions: pure movements do not invalidate routes
- Dijkstra shortest-path routing with cost-aware priority queue
- Bounded route result cache with explicit connectivity version validation
- Approximate rolling latency percentiles and runtime counters
- Local named-pipe JSON-lines control API; globally bounded routing admission

## API examples

### HTTP (development diagnostics only)

```bash
curl http://127.0.0.1:5087/api/health
curl http://127.0.0.1:5087/api/metrics
curl 'http://127.0.0.1:5087/api/map?minX=0&minY=0&maxX=300&maxY=300'
curl http://127.0.0.1:5087/api/history

curl -X POST http://127.0.0.1:5087/api/benchmark/start \
  -H 'Content-Type: application/json' \
  -d '{"nodes":2500,"routesPerSecond":300,"movesPerSecond":120,"linkChangesPerSecond":4,"durationSeconds":60,"seed":1337}'

curl -X POST http://127.0.0.1:5087/api/benchmark/stop
```

### Named pipe (bridge prototype)

Connect to `netdm-core` and send one compact JSON object per line.
Each reply includes its request `id`; concurrent routes may finish out of order.

```jsonl
{"id":"1","op":"add_node","a":1,"x":10,"y":20}
{"id":"2","op":"add_node","a":2,"x":20,"y":20}
{"id":"3","op":"connect","a":1,"b":2,"cost":3}
{"id":"4","op":"route","a":1,"b":2}
{"id":"5","op":"move_node","a":1,"x":12,"y":22}
{"id":"6","op":"stats"}
{"id":"7","op":"batch","changes":[{"op":"add_node","a":3,"x":40,"y":40},{"op":"connect","a":2,"b":3,"cost":2}]}
```

The pipe format is intended only for local trusted experimentation; it will be
replaced by a length-prefixed, versioned protocol and a nonblocking 32-bit shim
before SS13 integration. Unsigned 64-bit IDs in the dashboard are JSON strings to
avoid JavaScript precision loss.

## Repository layout

```text
src/
  NetDM.Core/
    Topology.cs       immutable graph, batches, spatial index
    Routing.cs        weighted shortest paths
    Runtime.cs        cache, traffic sampling, metrics
  NetDM.Host/
    Program.cs        HTTP observability API
    Transport/        named-pipe request handling
    Benchmarks/       repeatable live synthetic load
    wwwroot/          dependency-free network visualizer
tests/
  NetDM.SmokeTests/   concurrency, correctness, cache, position tests
  host-integration.sh HTTP + benchmark end-to-end check
docs/
  architecture.md    design, safety limits and next steps
```

## Next milestones

1. Protocol versioning, chunked bounded frames, batch acknowledgements, replay/resync,
   global admission limits, x86 shim and minimal DM subsystem.
2. Incremental spatial neighbor discovery, radio/wired link policies and edge lifecycle.
3. Network domains, access policies, QoS and bandwidth/latency effects.
4. Large dynamic-network benchmarks using realistic BlueMoon-Station traces and
   measured p50/p95/p99 under defined hardware and concurrency budgets.

See [architecture.md](docs/architecture.md).

## Routing Laboratory (isolated algorithm profiling)

In the same dashboard, open **Routing laboratory** below the live charts.
Choose **100–30,000 nodes**, **10–1,000 route queries**, viewport query count and a seed,
then start the comparison. The report measures **four alternatives** on identical
source/target requests from one immutable graph:

1. Dictionary Dijkstra (existing baseline)
2. Dictionary bidirectional Dijkstra
3. CSR Dijkstra (compact array-based adjacency)
4. CSR bidirectional Dijkstra

Each solution is checked against the baseline's optimal cost, and every successful
path is checked against the actual snapshot links. The run fails instead of reporting
misleading timings if a mismatch is detected. P50/P95/P99 use per-route
microseconds; memory is **per-thread allocated bytes per query**; throughput is
**serial equivalent queries/s**, not parallel or production capacity.

The same experiment compares viewport spatial-hash queries against full graph
scans, measures **64-move batches** on the persistent topology store, and reports
CSR build latency, allocations, and estimated CSR array payload. A compact CSR
projection is not automatically activated in the live router, because rebuilding
it after frequent link changes may negate faster queries. Route choice will be
decided only after profiling realistic dynamic workloads.

### Comparison API

```bash
curl -X POST http://127.0.0.1:5087/api/comparison/start \
  -H 'Content-Type: application/json' \
  -d '{"nodes":10000,"routes":160,"viewportQueries":120,"moveBatches":32,"seed":1337}'

curl http://127.0.0.1:5087/api/comparison
curl -X POST http://127.0.0.1:5087/api/comparison/stop
```

The comparison builds **its own isolated synthetic graph** so the live network
visualizer is not overwritten. Live and isolated benchmark HTTP start requests
reject most overlapping runs, but they are not yet coordinated by a single
atomic admission lock. The synthetic workloads are **not** real SS13 traffic traces.
On Windows, the new CI build also compiles and executes the core smoke tests.

## Repeatable large-network profiles

Run the same isolated comparison on 1k, 10k and 30k nodes with three random
seeds, preserving JSON reports and a CSV summary on disk:

```powershell
# Build once, launch and stop NetDM.Host automatically
dotnet build src/NetDM.Host/NetDM.Host.csproj -c Release
pwsh -File scripts/run-profiles.ps1 -LaunchHost

# Or use an already-running local host for a smaller targeted matrix
pwsh -File scripts/run-profiles.ps1 -Sizes '1000,10000' -Seeds '1337,42' -Routes 200
```

Output: `benchmarks/results/nodes-<count>-seed-<seed>.json` and `summary.csv`.
The JSON includes hardware/OS metadata, build time, allocations, route p95/p99,
index versus full-scan cost and movement cost. Results are useful for **relative
comparisons on the same machine**, not for production SLO claims.

The optional **Routing profiles (manual)** GitHub Actions workflow uploads
the report matrix as a CI artifact. GitHub displays manually triggered
workflows once the workflow file is available on the repository default branch.
Until then use the PowerShell script or run the workflow from a supported ref.

CI also runs a small profile-script smoke test on both Linux and Windows.

## Dynamic proximity topology (experimental)

The `DynamicRadioNetwork` engine derives **actual network edges** from node
positions rather than drawing imaginary connections. In the Observatory,
choose **RADIO · Dynamic proximity** and configure `Radio range` (default 14
world units with nodes spaced 12 units apart). During the live test:

- Both endpoints must have enabled radio profiles on the same nonzero channel.
- Radio connections are **mutual**: same Z-level, distance no more than the
  smaller endpoint's configured range.
- A movement batch rechecks previous radio neighbors and indexed candidates
  near the *new* position. All changes are published with node moves in one
  immutable topology snapshot.
- Automatic edge creation/removal increments connectivity version and
  invalidates stale route results; movement with unchanged connectivity
  preserves route cache reuse.
- Registered **cable links** survive wireless disconnect/reconfiguration;
  if both a radio and cable link exist, the lower routing cost wins.
- Runtime metrics include current radio edges, cumulative created/removed links
  and per-second link churn. The map is generated from the **actual graph**.

The simulator's `linkChangesPerSecond` setting switches between radio on/off
events in radio mode; in wired mode it continues to create/remove synthetic
optional links. These workloads simulate **logical signal reachability**, not
radio wave propagation, walls/obstacles, interference, packet bandwidth,
authentication, power consumption, or server-side physical effects.

The radio subsystem is intentionally a separate component from the base graph.
The underlying graph can still be used for wired-only or custom gameplay
rules. **One logical owner per managed edge** is required: when using
`DynamicRadioNetwork`, create cables through `SetCable` and changes to
radio profiles/positions through `Update`, rather than writing their edges
directly to `TopologyStore`.

Example configuration for the live bench API:

```json
{"nodes":2500,"routesPerSecond":300,"movesPerSecond":250,"linkChangesPerSecond":40,"durationSeconds":60,"seed":1337,"wirelessEnabled":true,"radioRange":14}
```

### Radio density and overload limits

Automatic radio links are currently individual pairwise edges, not
shared-medium broadcast domains. The core therefore **fails safely** if a
radio update sees more than 128 candidate endpoints in its proximity cells,
rather than constructing an unbounded clique that could exhaust memory.

The overload condition does not publish partial topology or radio ownership
changes; it surfaces as an error in the simulator. For dense SS13 environments,
the production design should replace pairwise radio visibility with
**radio-domain/group routing**, with transmission contention modeled separately.
Do not treat the 128-candidate threshold as a gameplay rule.

Long-running radio discovery accepts a `CancellationToken`; cancellation
before the graph commit leaves the current topology unchanged.

## Shared Medium: dense radio networks without quadratic edge storage

The new **SHARED · Lazy medium domains** mode in Network Observatory uses
`SharedMediumNetwork` instead of `DynamicRadioNetwork`. It keeps radio
membership indexed by `(channel, Z-level, spatial cell)` and discovers
eligible neighbours **during routing**, without generating a pairwise
edge for every device within radio range.

This is an alternative execution model, not an automatic replacement for
the earlier pairwise radio engine. Both remain selectable in the benchmark.

The model preserves geometric reachability:
devices must use the same channel and Z-level, and their distance must
be no greater than the smaller configured range. **Being in the same
channel-domain does not give two distant devices an imaginary direct route.**
Paths may traverse multiple short-range radio hops or explicitly
registered physical cables. A domain is the shared airtime accounting scope,
not a connected component.

There is also a deterministic **per-channel capacity** expressed as bytes
per simulation tick. A successful route can call `TryTransmit(route, size, tick)`.
All wireless hops on that route reserve bytes against their shared
channel's budget. Reservation is atomic across all domains used; if any
domain is saturated, the transfer is rejected without partial debits.
Cable-only hops do not consume wireless capacity.

This is a **logical contention approximation**. It does not yet simulate
radio interference, collision backoff, spatial reuse, packet queues,
propagation delays or wall occlusion. A domain's budget is shared even
between geographically disconnected clusters using the same channel/Z.
Simulation tick is explicitly supplied by the caller; in the benchmark
one tick is approximately 100 ms of monotonic host time.

The dashboard shows occupied radio-domain cells as translucent tiles,
current domains and registered devices, and a chart of successful vs
rejected transmissions. Its dynamic graph map displays **physical edges**;
shared-medium radio reachability is deliberately not serialized as a
quadratic set of visual edges.

### Example live benchmark request

```json
{"nodes":2500,"routesPerSecond":300,"movesPerSecond":200,"linkChangesPerSecond":20,"durationSeconds":60,"seed":1337,"wirelessEnabled":true,"radioModel":"shared","radioRange":64,"mediumBytesPerTick":8192}
```

The default `radioModel` is `pairwise` for backward compatibility;
`wirelessEnabled=false` selects the wired graph.

Shared-medium route computations run against one composite immutable
snapshot containing both physical topology and radio membership. A profile
change invalidates radio-route caches even when the physical graph edges
remain unchanged. Do not bypass this layer to mutate its physical graph;
ownership checks reject subsequent updates if they detect an external write.

Tests compare its routes to the pairwise model on randomized channels,
positions, and moved devices; also cover a dense 400-device cell without
adding any physical radio edges, movement snapshot consistency,
stale-route transmission rejection and shared capacity saturation.

## Priority work scheduler (M5 prototype)

All **named-pipe player route requests** and **live benchmark route requests**
now share one globally bounded `PriorityWorkScheduler`. Direct calls to the
core `NetworkRuntime.Route`, and the isolated algorithm-comparison runner,
remain synchronous on purpose. The latter is for measuring algorithms without
scheduler overhead; do not mistake those numbers for end-to-end latency.

### Admission and fairness

- Fixed `Math.Clamp(Environment.ProcessorCount / 2, 1, 8)` concurrent CPU
  workers; no unbounded `Task.Run` for each incoming route.
- Maximum **256 queued requests**. New requests are immediately rejected
  with `busy` when the queue is full. The synthetic benchmark records these
  rejections rather than building an unbounded producer backlog.
- Weighted selection gives Critical / Interactive / Background a 4:2:1
  service pattern when all classes are backlogged, without permanently
  starving any class. The pipe currently uses Interactive priority and
  background workload generation uses Background.
- Connection disconnects propagate cancellation into scheduled route work.
  Already executing routes check their cancellation token while searching.
- A soft **65 ms per 100 ms** admission budget defers starting new jobs
  once the window is consumed. It uses task elapsed wall time as a proxy.
  **It cannot preempt running work or guarantee CPU usage**, especially
  with multiple workers and operating-system scheduling.

### Observatory

The **CPU Work Scheduler** panel shows current/peak backlog,
queued counts by priority, worker utilization, accepted/rejected tasks,
queue-wait p95 and processing p95. New charts show queue depth and
rejections per second. Both the `GET /api/scheduler` endpoint and the
`scheduler` field in `GET /api/metrics` expose these live counters.

```bash
curl http://127.0.0.1:5087/api/scheduler
```

Synthetic overload must be judged by *observed* routes/s,
queue-wait p95, queue-depth growth and rejection rate; the configured
target load is not the achieved throughput. Priority fairness is tested
using a held worker and deliberately saturated queue.

### Dense shared-radio routing fast path

With strictly positive graph costs, Dijkstra can stop as soon as the
smallest unsettled distance is no smaller than an already discovered
target distance. This avoids expanding hundreds of equivalent-cost
nodes in a fully connected radio cell, without assuming that one
channel/Z-level implies direct geometric connectivity.

The dense 400-device test checks the returned exact cost and that
the search visits at most two nodes for a direct target. General
multi-hop networks can still trigger expensive neighbor enumeration;
profile worst-case geometry before switching the live default.

## Benchmark health diagnostics (M6)

The 12k-node / 120-second shared-radio stress scenario found that the
observed route throughput changed drastically over time while radio
profiles were being toggled. **A fast rejected/unreachable route is not
proof of a fast connected graph.**

The benchmark now records **`actualFoundPerSecond`** and
**`foundPercent`** every sampling interval, plus
**`mediumConfiguredDevices`**. The **Route reachability** chart overlays
the fraction of successfully found routes (green) with the fraction of
configured shared-radio devices (blue). This makes progressive network
fragmentation visible alongside the existing route throughput plot.

The **Radio rejection reasons** chart and `sharedMedium` statistics now
separately count:

- `rejectedStaleRoute`: route snapshot invalidated before transmit.
- `rejectedInvalidPath`: absent or inconsistent route/hop sequence.
- `rejectedTransferTooLarge`: total radio-hop bytes for **one**
  payload exceed the entire domain capacity in a tick.
- `rejectedCapacity`: a normally admissible payload exceeds the
  **remaining** budget in the active tick.
- `rejectedOldTick`: an attempted older tick would roll back a
  domain's monotonic transmission ledger.

`attemptedWirelessHops` tracks the combined hop count of all
eligible radio transmissions, including those that ultimately fail to
reserve airtime. Counters sum to `rejectedTransfers`; each rejection
has exactly one reason.

For reproducible comparisons, hold the source graph and radio settings
constant before attributing routing changes to an optimization. Run
three separate **Shared Medium** tests with the same seed, routes/s
and node count:

| Scenario | Moves/s | Link changes/s | Measures |
|---|---:|---:|---|
| Static baseline | 0 | 0 | Routing performance on an intact graph |
| Movement only | 1,000 | 0 | Position churn and route-cache invalidation |
| Movement + outages | 1,000 | 30 | Fragmentation plus mutation cost |

Compare observed routes/s, **found percentage**, queue-wait p95,
radio rejection causes and active-device percentage. At 12k nodes,
radioRange=14 and mediumBytesPerTick=8192, expect a long radio path
to require many more bytes of airtime than its payload size: a
30-hop route carrying 512 bytes consumes **15,360 radio-hop bytes**,
which is too large for even an idle 8,192-byte/tick channel.

Important: the capacity budget is an intentionally simple shared-airtime
approximation, not a physically accurate radio bandwidth model.

## Open-loop offered-load benchmarking (M7)

The earlier benchmark waited for all routes in every 100-ms batch to finish
before attempting the next batch. Thus a 1,500 routes/s target on the
12k-node connected radio grid only completed ~69/s, with **zero scheduler
rejections**, even though the desired rate was much higher. This was
self-throttling from the test harness, not actual proof of server capacity.

The new producer uses monotonic Stopwatch arrival pacing independently of
route completion. It never awaits the prior batch; the global bounded
scheduler applies backpressure. Catch-up bursts after OS scheduling pauses
are capped at 512 requests/producer iteration.

Three explicit counters are available in `benchmark.load` and plotted in
the new **Load admission** chart:

- `offered`: requested scheduler admissions;
- `admitted`: accepted for execution;
- `rejected`: immediately denied by the global scheduler.

Per-sample rates are `offeredRoutesPerSecond`,
`admittedRoutesPerSecond` and `rejectedRoutesPerSecond`. These are
distinct from `actualRoutesPerSecond`, which counts completed searches.

Do not compare results from old closed-loop tests directly to new
open-loop runs as if they used the same offered load. This change is a
benchmark-methodology correction, not an algorithm speedup.

## Route validity across unrelated topology updates (M8)

Previous shared-medium transfers categorically rejected any computed route whose
`RouteResult.Version` was different from the current global
`SharedMediumSnapshot.Version`. The open-loop movement benchmark showed that
**5,593 of 8,307 failed transfers** were classified as stale with 1,000
node moves/s, even though the route's local hops might still have been valid.

The radio transmitter now pins a single current immutable composite snapshot
and **revalidates every hop** of an older route (physical cable or mutual
radio reachability). A valid multi-hop route can be delivered without a
full shortest-path recomputation: it need not remain *optimal*, only
connected. If any endpoint has been removed or any hop has disappeared,
the stale route is rejected. Never blindly trust an old route's global
revision or cost. The existing byte/tick budget is still checked against
the currently valid medium and selected cable-versus-radio hops.

`AcceptedRevalidatedTransfers` counts successfully delivered routes that
were calculated under an earlier global version; the history includes
`revalidatedTransfersPerSecond`. The Observatory shared airtime chart
overlays these accepted transmissions in blue. The old
`RejectedStaleRoute` counter now refers to **invalid old paths**, not
all old revisions, and is therefore **not directly comparable** to
pre-M8 benchmarks.

This does **not** guarantee that the route will remain valid for the
entire duration of an eventual multi-tick packet transfer. A future
per-hop transmission scheduler must revalidate the next hop at its own
dispatch time, and fail or re-route at the broken hop.

## One-click three-scenario diagnostics for ChatGPT

Network Observatory now has a **Three-scenario diagnostic suite** section
with **RUN ALL THREE SCENARIOS**. Clicking it starts a host-owned job with
these reproducible settings (no manual reconfiguration):

| Stage | Nodes | Offered routes/s | Moves/s | Radio toggles/s |
|---|---:|---:|---:|---:|
| 01: static | 12,000 | 1,500 | 0 | 0 |
| 02: movement | 12,000 | 1,500 | 1,000 | 0 |
| 03: movement + outages | 12,000 | 1,500 | 1,000 | 30 |

Every stage uses the same **seed 1337**, **120-second run**, **shared-radio
model**, **14-unit range** and **8192 bytes/tick budget**. Nodes and radio
registrations are reconstructed afresh before each stage. The host runs
the three stages **sequentially**, never all at once.

The dashboard tracks active stage and progress. Once a stage is complete,
its **DOWNLOAD JSON** link appears immediately. When all stages finish,
the **DOWNLOAD ALL 3 JSON FILES AS ZIP** link becomes available. Send
the three JSON files or the ZIP to ChatGPT for an apples-to-apples
analysis. Each exported report includes its own scenario metadata, the
same top-level `exportedAt/metrics/history/topology` fields as manual
exports, and the full benchmark/scheduler/shared-medium diagnostics.
The `topology` field intentionally includes a compact summary instead
of a massive full viewport dump.

The experiment is **managed by the server**: closing or refreshing the
browser does not cancel it. The last suite's reports remain accessible
after a reload until you launch a new suite or restart the host.
Manual benchmark/algorithm-comparison starts are blocked while the
suite runs; **STOP SUITE** cancels the current stage and leaves any
previously completed JSON reports downloadable.

### Programmatic access

```bash
curl -X POST http://127.0.0.1:5087/api/benchmark-suite/start \
  -H 'Content-Type: application/json' -d '{}'
curl http://127.0.0.1:5087/api/benchmark-suite
# Use runId from the state response to download each of 1, 2 and 3:
curl -OJ 'http://127.0.0.1:5087/api/benchmark-suite/download/1?runId=...'
curl -OJ 'http://127.0.0.1:5087/api/benchmark-suite/download-all?runId=...'
```

The API accepts optional smaller `nodes`, `routesPerSecond`, `durationSeconds`
etc. solely to allow quick CI verification of the same three-stage
lifecycle. The dashboard button always requests the canonical settings.

## M10 — Radio A*, hopwise packet transport and allocation diagnostics

### Routing: exact Dijkstra versus radio-aware A*

Network Observatory's Shared Medium mode has a **Shortest-path solver**
control. `dijkstra` remains the baseline; `astar` uses an admissible
spatial lower bound to prioritize nodes nearer the destination:

```
floor(EuclideanDistance(node, goal) / maxRegisteredRadioRange) * minRadioLinkCost
```

The enforced maximum range and minimum link cost come from the radio
configuration. For the benchmark's homogeneous grid they are **14 world
units** and **cost 8**. When arbitrary-length cable edges exist, the
heuristic is forced to zero; this preserves exact optimality (the
A* engine effectively becomes Dijkstra).

The implementation does **not** assume that all radio devices on one
channel are directly connected. Same channel/Z is necessary for a
radio-only route; actual neighbour discovery still enforces each
endpoint's range.

Randomized correctness tests compare route existence and exact cost
against the existing Shared Medium Dijkstra before and after radio
mutations, with channels, Z-layers, and cable shortcuts. A 900-node
grid regression compares explored nodes for a long, fully connected path.

### Transport: single-tick versus store-and-forward packets

Shared Medium's **Delivery simulation** selector provides:

- **Legacy**: `TryTransmit` reserves radio airtime for every hop of the
  entire path in one tick; retains previous benchmark semantics.
- **Hopwise**: `RadioPacketTransport` admits a packet to a bounded
  (2,048 slot) queue; each accepted packet advances **at most one hop
  per 100ms simulation tick**. Every hop is checked against the current
  immutable network and debits only that hop's bytes from its channel's
  airtime budget. A busy channel defers the packet to a later tick.
  A broken link triggers a bounded route recomputation from the packet's
  current node; retries, age (default 180 ticks) and tick work are bounded.

The new option measures **completed packet delivery**, not merely
successful reservation of airtime. Counters include enqueued, rejected,
delivered, dropped, expired, queued/peak, reroutes, congestion deferrals,
wireless/cable hops and delivered payload bytes. Packet queues live
inside the benchmark's runtime; a fresh test resets them. The same
fixed-priority worker scheduler runs packet forwarding with
`Interactive` priority, while route generation stays `Background`.

**Important:** the packet simulator does not yet model actual RF
collisions, propagation, retransmission acknowledgements, real packet
serialization or delivery guarantees across concurrent topology
updates. It is a bounded, ticked logical transport prototype. The old
legacy API remains available for before/after comparison.

### Three-scenario suite

The three-stage diagnostic button now exposes routing and transport
selectors as well. Every stage uses the chosen pair, with the same
seed and workload, so all three downloaded JSON reports can be
compared to each other. The default remains **Dijkstra + Legacy**, to
preserve the methodology of previously exported tests.

For a new run, select **A* spatial search** and **Hop-by-hop packet
forwarding** before pressing **RUN ALL THREE SCENARIOS**. Report fields
`suite.parameters.sharedSearch` and
`suite.parameters.packetTransportEnabled` identify the variant;
each `benchmark.packetTransport` contains terminal packet counters.

**Do not compare** the raw count of legacy airtime reservations with
hopwise packet deliveries as if they represented the same operation.

### Host instrumentation

The runtime now reports `totalExploredNodes`,
`totalRouteAllocatedBytes`, and `gcTotalPauseMilliseconds`.
Each history sample includes average **explored nodes and allocated
bytes per completed route**, **GC pause milliseconds per wall second**,
and number of Gen2 collections since the preceding sample. The new
Observatory charts show explored nodes and GC pauses. These are
process-level managed allocation/GC measurements, not exact
per-command profiling of DreamDaemon or x86 bridge overhead.

### Example

```bash
curl -X POST http://127.0.0.1:5087/api/benchmark/start \
  -H 'Content-Type: application/json' \
  -d '{"nodes":10000,"routesPerSecond":1000,"movesPerSecond":0,"linkChangesPerSecond":0,"durationSeconds":60,"seed":1337,"wirelessEnabled":true,"radioModel":"shared","radioRange":14,"mediumBytesPerTick":8192,"sharedSearch":"astar","packetTransportEnabled":true}'
curl http://127.0.0.1:5087/api/packets
```

## M11 — Spatial airtime reuse and packet queue fairness

The previous 12k-node **A* + Hop-by-hop** run revealed only 2 packet
deliveries in the static case, with ~205k deferrals and over 10k TTL
expirations. The radio model used one global 8,192-byte capacity for
every device on the same channel and Z-level, so spatially separated
transmissions unnecessarily blocked one another.

### New airtime scope

Shared Medium now provides two alternative `RadioAirtimeScope` modes:

- **ChannelWide** is the original model. Every radio hop using the same
  channel/Z spends the same global budget for that simulation tick.
- **SpatialCells** is the new logical interference model. Each hop
  reserves the requested bytes **atomically in every spatial cell**
  intersecting a conservative interference rectangle around both
  endpoints. Overlapping footprints contend; distant non-overlapping
  transmissions can reuse the same channel and tick.

The spatial model uses the existing 32-unit cell grid, channel and Z,
and the larger of both endpoints' radio ranges. It is intentionally
conservative and may reserve more cells than a precise circular
interference model. Cell reservations are kept **only for the current
simulation tick**, so the ledger does not grow across the full benchmark.

The model is still **not** an RF signal/interference simulation. It
does not include walls, hidden terminal problems, antenna patterns,
capture effect, collisions, retransmit ACKs or interference-free
non-overlapping frequency bands. Compare both modes using the same
scenario and hardware before drawing performance conclusions.

### Fair bounded store-and-forward

`RadioPacketTransport` still has a hard admission limit of 2,048
queued packets, and packets progress by **at most one hop per tick**.
The per-tick round-robin processing budget now defaults to 1,024 packet
visits, rather than 512. The default TTL increases from 180 to
**900 ticks (90 seconds at a 100 ms tick)**, avoiding an automatic
expiry of 70-hop routes that receive a turn only every 2–4 ticks.
Neither change guarantees delivery under sustained congestion.

When an intermediate radio hop breaks, the packet tries to reroute
from its *current node*. If no path exists, it waits a configurable
three-tick retry interval before attempting again; it may recover
from short outages without being immediately discarded. Each
packet's reroute count and age remain bounded, so an absent route
cannot create infinite retry work.

The packet statistics now include `deliveryAgeP50Ticks`,
`deliveryAgeP95Ticks`, `meanDeliveredHops`,
`totalDeliveryAgeTicks`, `noRouteDeferrals`,
`linkFailureDrops`, `oversizeDrops`,
`queueFullRejections` and `invalidRejections`.
The medium additionally exposes `spatialSuccessfulHops`,
`spatialBusyHops`, `spatialCellReservations`,
`spatialActiveCells`, and `spatialPayloadBytes`.

### Running the three-scenario suite

In **Three-scenario diagnostic suite**, choose
**A* spatial search → Hop-by-hop packet forwarding → Spatial reuse
(new)**, leave the TTL at 900 ticks, and press
**RUN ALL THREE SCENARIOS**. Once complete, export the same three JSON
files (or the combined ZIP). Each includes routing performance,
packet delivery, reroutes, delivery latency, spatial airtime pressure
and old/new network settings for reliable comparisons.

The manual Shared Medium benchmark has the same options, including
global channel mode for the control experiment.

Configuration fields: `spatialReuseEnabled` (defaults to true for
hopwise simulations), `packetMaxAgeTicks` (default 900), and
`packetMaxAdvancesPerTick` (default 1024).
Legacy whole-route reservation still uses global channel capacity
and is unaffected by the spatial toggle.

## M12 — Isolated asynchronous packet reroutes

The 12k-node spatial-radio stress run confirmed large improvements for
static networks (4,624 delivered packets), but packet throughput collapsed
under mobility and outages. The former `RadioPacketTransport.Advance`
held the *entire packet queue lock* while running full A*/Dijkstra
`_network.Route(...)` for broken hops. Those expensive searches were
also executed inside the Interactive packet-tick task, blocking fast
delivery work and interfering with original player routing.

### The split

`Advance(tick)` now only forwards bounded ready hops or generates
**bounded** `PacketRerouteRequest` tickets for broken links. It **never**
computes a route itself. The new API splits rerouting into:

- `TryTakeReroute(out ticket)`: acquire at most the configured number of
  in-flight tickets; the packet remains queued but is marked pending.
- `CompleteReroute(ticket, route)`: only apply if the original packet is
  still active, still at the same node and has the same destination.
- `CancelReroute(ticket, schedulerRejected)`: release in-flight ownership
  on cancellation, error or CPU admission rejection and retry later.

Expired packets cannot be resurrected by late search results. Their
original queue slots are released even while their old calculation
is still in progress. Sending and cancellation never silently discard
accepted payloads. Every accepted packet still contributes to exactly
one of Delivered, Dropped, Expired or Queued.

Reroute tickets are bounded by `MaxPendingReroutes=64`, and at most one
worker is concurrently active by default (`MaxConcurrentReroutes=1`
in the benchmark). Another packet simply defers its broken hop while
the queue is full, rather than blocking the entire forwarding pass.

### Separate CPU admission

The benchmark starts a **second** `PriorityWorkScheduler` only when
hopwise packet simulation is active. Its defaults are:

| Budget | Default |
|---|---:|
| Dedicated reroute workers | 1 |
| Reroute CPU executor backlog | 32 |
| Soft reroute CPU admission per 100ms | 25ms |
| Maximum pending reroute tickets | 64 |
| Global player/original-route scheduler backlog | 256 |

This is still *soft wall-time admission*, not preemptive CPU isolation
or a processor-affinity guarantee. A running route may exceed its
budget. The dedicated worker prevents a costly reroute from executing
while the Interactive forwarding task owns the packet queue lock.

### New diagnostics

The three-scenario JSON reports include a distinct
`metrics.benchmark.rerouteScheduler` snapshot and full packet counters:
`rerouteQueued`, `rerouteInFlight`, `rerouteCompleted`,
`rerouteCancelled`, `rerouteQueueDeferrals`,
`rerouteSchedulerRejections`, `rerouteWaitP95Ticks`,
`skippedTicks`, `tickAdmissionFailures`,
`forwardingP95Milliseconds`, `firstHopAgeP50Ticks`
and `firstHopAgeP95Ticks`.

The Observatory now plots **Reroute pressure** and **Packet tick health**
beside existing radio and packet charts. First-hop wait p95 is shown
separately from final delivery age p95. A high first-hop wait implies
queue congestion; high delivery age with a low first-hop wait is more
likely due to intermediate-hop contention, reroutes or long paths.

The manual benchmark and the three-scenario suite offer
`packetRerouteWorkers` (1–4) and
`packetRerouteCpuBudgetMs` (0–100) controls; leave them at
**1 worker / 25ms per 100ms** for the next A/B comparison.

The previous suite's report format is still compatible: existing
fields remain, and all new fields are additive. Delivery improvements
under mobility remain **unmeasured** until another three-scenario
12k-node run. Old and new data should be compared by actual offered,
admitted, completed and delivered rates, and by sampled task execution
and queue latencies.

## M13 — Dedicated packet tick runner (100ms monotonic clock)

The PR #12 three-scenario stress run showed that reroute isolation helped
original routing, but the packet tick task still competed with thousands
of route requests in the global scheduler. Roughly 810/861/625 of
~1,200 possible intervals were missed in static/movement/outage cases,
and packet-tick admission was rejected under heavy CPU load.

**`PacketTickRunner` now executes forwarding on a dedicated long-running
.NET thread** (rather than submitting ticks to `PriorityWorkScheduler`).
It uses the same absolute `Stopwatch` epoch as packet enqueue timestamps
and wakes at 100ms monotonic boundaries. When the process is delayed,
it executes **only the current clock tick**, recording skipped intervals,
never replaying old intervals back-to-back or letting a packet traverse
many hops immediately to catch up.

The packet queue still limits visits/tick (1,024 default). A new soft
**`MaxTickWorkMilliseconds` = 25ms** stops the scan between packets
once the per-tick work budget has elapsed; unvisited packets retain
their FIFO position and are eligible next tick. A single hop cannot be
preempted, and the limit includes lock/wall-clock delays, not strictly
measured CPU cycles.

For controlled comparisons, hopwise transport offers both modes:

| Tick mode | Behavior |
|---|---|
| **Dedicated (default)** | Own long-running worker, no global route admission needed |
| Shared (baseline) | Former Interactive task submitted to global bounded scheduler |

The manual benchmark and three-scenario suite expose
`dedicatedPacketTicks` (default true) and
`packetTickWorkBudgetMs` (default 25; range 1–1000).
The earlier isolated reroute CPU scheduler remains independent and
unchanged.

### Telemetry

Every scenario's JSON includes `benchmark.tickRunner` when dedicated
mode is active:

- `executedTicks` — actual monotonic forwarding passes;
- `skippedIntervals` — intervals skipped while the worker was busy;
- `startLagP95Milliseconds`, `maxStartLagMilliseconds` — deviation
  from the scheduled tick boundary;
- `executionP95Milliseconds`, `failedTicks`.

Also available: packet `tickBudgetStops` when forwarding hits the
soft 25ms per-tick cap, and per-interval
`tickExecutedPerSecond`, `tickRunnerSkippedPerSecond`,
`tickBudgetStopsPerSecond`, `tickStartLagP95Milliseconds`,
`tickExecutionP95Milliseconds`.

Network Observatory now has **Packet tick health** and **Tick timing**
plots. In dedicated mode, `packetTickAdmissionFailures` should remain
zero, since packet passes are no longer submitted to the global
work-queue. They can still miss a monotonic tick due to OS/GC scheduling
or work overruns; dedicated means queue isolation, **not a hard
real-time guarantee or reserved CPU core**.

### Next reproducible experiment

Use **A* / Hop-by-hop / Spatial reuse / TTL 900 / 1 reroute
worker / 25ms reroute budget / Dedicated packet ticks /
25ms forwarding budget** in the one-click three-scenario suite.
Download its ZIP and compare against the PR #12 measurements:
delivered packets, first-hop/delivery p95, executed ticks per second,
skipped intervals, packet work p95 and original route throughput.

Linux/Windows smoke tests include a deliberately saturated
`PriorityWorkScheduler` while a separate packet tick thread continues
to forward packets. HTTP integration exercises all three scenario
exports and tick runner fields.

## M14 — Adaptive reroute backoff with local recovery notifications

Under 12k-node mobility tests, many packets repeatedly paid for A*
searches even when their destination was temporarily disconnected.
The new **bounded negative-result backoff** applies separately to
each packet's repeated *completed searches which found no path*:
`3, 6, 12, 24, 48, 96, …` simulation ticks, capped at the
configurable `packetMaxNoRouteRetryDelayTicks` (default 96, or 9.6s).
The maximum reroute attempts and packet TTL remain unchanged; a
disconnected packet can never perform unbounded searches.

When the bounded reroute ticket buffer is full, the packet waits
`packetRerouteQueueRetryDelayTicks` (default 8 ticks), reducing
repeated queue-admission probes. Packets continue to count toward
the 2,048-slot hard queue limit throughout waiting.

A radio update now calls
`RadioPacketTransport.NotifyRadioUpdates(updates)`, recording which
node IDs actually moved or changed radio profiles. Only a packet whose
**current failed next-hop endpoints** have changed may wake early for
a full search, and such wakeups have a minimum interval between
search attempts. If its original hop has directly recovered,
`SharedMediumNetwork.HasUsableHop` allows immediate forwarding without
running A*. The probe does not reserve airtime; real forwarding still
uses `TryTransmitHop` and the current spatial interference budget.
Unrelated global topology changes do not wake every waiting packet.

A failure result from an old snapshot version does not poison the
negative backoff: topology may have become connected while A* was
running. Such results are retried after a short minimum delay.
The existing per-snapshot exact-key route cache is retained and
continues to reuse safe same-version routing results. No negative
result is reused after a topology epoch change without recomputation.
The bounded local-change hint table clears when its 65,536 entry
limit would be exceeded, and periodic retries remain a fallback.

The new exported counters are `noRouteBackoffs`,
`noRouteBackoffTicks`, `cooldownSkips`,
`localChangeWakeups`, `recoveredOriginalHops`,
`staleNegativeResults` and `trackedChangedNodes`.
Network Observatory shows **No-route retry suppression** and supports
configuring the backoff cap and full-ticket-queue cooldown in both
manual and three-scenario benchmarks.

**These are performance/latency tradeoffs, not a proven speedup.**
Longer waiting between attempts may improve CPU admission at the
cost of slower recovery if alternate connectivity appears far from
the affected packet's two next-hop nodes. Use the same 3-scenario
suite to measure original routes/sec, reroutes/sec, delivered fraction,
TTL expiry, first-hop/delivery p95, and queue pressure. A/B
comparison with a low cap is recommended before making this default
in the production BYOND adapter.

## M15 — Pooled A* and controlled backoff A/B experiments

Shared Medium adds an opt-in `SharedSearch: "astar-pooled"` mode.
The original `"astar"` implementation is kept intact as the control,
and `"dijkstra"` remains the exact shortest-path reference.

Pooled A* uses an **exclusive reusable search workspace** containing
the cost dictionary, parent dictionary and priority queue. Each request
rents one workspace from a bounded, thread-safe `ConcurrentBag`, then
clears and returns it in a `finally` block, including during exceptions
and cancellation. Up to **8 workspaces** are retained, each with bounded
search-state sizes; oversized searches are not pooled. Nothing about
graph snapshots or route caches is shared mutably between queries.

Radio-neighbor traversal is now performed inline, removing the
`IEnumerable<Edge>` iterator and transient iterator states from each
expanded node. The radio eligibility and path-weight rules are
identical to the original A*: same channel, Z-level, mutual range,
positive link costs, and zero heuristic fallback when arbitrary cable
shortcuts exist. The result remains an **exact shortest path**, not
an approximate heuristic route.

### CI allocation micro-probe

The .NET smoke test warms both algorithms and calculates
`GC.GetAllocatedBytesForCurrentThread` over 32 identical searches on
a **900-node static radio grid**. A measured run reported:

| Mode | Bytes allocated per query |
|---|---:|
| Original A* | 503,280 |
| Pooled A* | 159,416 |

That is **68% less thread-local allocation** for this micro-probe.
It is not a measured speedup or a guarantee of the same allocation
reduction on the user's 12k-node moving radio network. Real p95/CPU,
Gen2 collections, original-route throughput and actual packet
deliveries must be compared with exported 120-second scenarios.

Smoke tests compare original, pooled and Dijkstra on multiple dynamic
graphs (channel/Z, position changes, cables), 900-node connected
network, 160 parallel pooled queries, cancellation and max-explored
early exits. The public original A* path is unchanged.

### Ready-to-run comparison presets

Network Observatory → **Three-scenario diagnostic suite** now has a
**Controlled A/B experiment presets** selector with:

| Preset | Routing | Reroute workers | Negative backoff cap |
|---|---|---:|---:|
| A (control) | Original A* | 2 | 96 ticks |
| B | Pooled A* | 2 | 96 ticks |
| C (sensitivity) | Pooled A* | 2 | 24 ticks |

Each preset also selects Hop-by-hop, Spatial reuse, TTL=900, dedicated
packet ticks, forwarding budget 25ms/tick, reroute budget 25ms/100ms,
full-ticket-queue cooldown 8 ticks. Each still runs **three**
static/movement/movement+outage scenarios with identical seed=1337,
12k nodes, 1500 offered routes/s and 120 seconds per scenario.

Run A and B to isolate the pooled solver. Run C only after comparing
A and B to assess the tradeoff between CPU work and recovery latency.
The presets apply the controls when selected; a user may still change
settings manually before starting.

**Report and ZIP file names now include search mode, reroute-worker
count, maximum negative-retry cap and seed** to avoid overwriting
the baseline when downloading B and C. Each JSON report still includes
the full, authoritative parameter set in
`suite.parameters` and `metrics.benchmark.options`.

**Interpretation:** Compare `runtime.totalRouteAllocatedBytes` by
scenario-local delta, `allocatedBytesPerRoute`, completed original
routes/s, found%, p95, Gen2 and GC pauses, reroutes, packet deliveries
and delivery p95. Global counters in the host's `metrics.runtime`
may include earlier scenarios; use per-interval history or deltas.

## M16 — Dense array-backed exact A*

The previous A/B/C hardware experiments confirmed that `astar-pooled`
decreased managed allocations by about 70% compared with the original
A*, but connected movement scenarios still spent about 60 ms at the
end-of-run p95 and admitted only ~113 original route requests per
second. M16 adds an independent **`astar-dense`** candidate to attack
CPU time in the hottest search loop.

### Dense topology view, published with each immutable snapshot

- A persistent `NodeId → dense int index` map is built **once for a
  stable node set**; moving radios and changing profiles reuse it.
- Route lookups use indexed node state: coordinates, active radio
  profile, and a precomputed array of physical cable edges.
- Spatial radio cells hold immutable `int[]` member arrays in the
  dense view. Changes to one device update **only affected cells**,
  and copy only the 64-entry node blocks that actually changed.
- New nodes are not accepted from outside the Shared Medium owner.
  Removing nodes explicitly rebuilds the index to avoid stale offsets.
- Every dense view is created before one atomic snapshot publication.
  Old snapshots retain their original positions, cell membership and
  cable arrays even while other threads move devices.
- Arbitrary sparse 64-bit Node IDs are supported; array indexes are
  **never** calculated from NodeId numeric values.

### Search workspace

`RouteAStarDenseCore` replaces cost/parent/hash-state dictionaries with
resizable `ulong[]`, `int[]` and epoch-stamped `int[]`, and uses
indexed priority-queue entries. Epoch stamping avoids clearing the
arrays after every request. A concurrent worker rents a private
workspace; at most eight workspaces with bounded size are pooled.
The original exact A* heuristic is unchanged, including the safe
zero-heuristic fallback when cables could shortcut Euclidean distance.
Same-cost routes and graph expansion are verified against pooled A*.

### Controlled benchmark

Use **Three-scenario diagnostic suite → Controlled A/B experiment presets**:

| Preset | Search | Reroute workers | Negative backoff |
|---|---|---:|---:|
| **B** | Pooled A* | 2 | 96 ticks |
| **D** | Dense A* | 2 | 96 ticks |

Both preserve all other conditions: 12k nodes, 1500 offered routes/s,
120 seconds per scenario, seed 1337, spatial airtime reuse, hopwise
delivery, TTL 900, dedicated packet ticks, and 25ms soft budgets.
Each produces a ZIP with three uniquely named JSON reports; new
`astar-dense` keeps the original `astar` and `astar-pooled` modes
available. A separate C profile tests backoff 24 ticks, **not** dense
algorithm performance.

The accompanying small-grid smoke tests check path cost/exploration
and exact path parity with pooled A* and Dijkstra on moving networks,
cable shortcuts, radio toggles, sparse IDs, node removal and concurrent
queries. They also print allocation/timing micro-probes. Those
micro-probes are **not equivalent** to the full 12k-node benchmark.
The new dense index trades additional snapshot-update work/memory for
faster read-side searches; measure actual movement rate, original
route throughput, p95, allocations, GC, and packet delivery before
changing the default.

## M23 — Correlated packet operation outcomes (Core API)

The NetDM packet transport now exposes an **opt-in C# completion contract**:
`Submit`/`SubmitReserved` return a synchronous acceptance result with a
transport `StreamId`, unique packet ID, opaque caller `RequestId` and a
typed rejection reason. Accepted packets produce **one terminal outcome**
(`Delivered`, `Dropped`, `Expired` or `Cancelled`) with a specific
reason when they finish, not at route calculation or enqueue time.
`timeoutTicks` allows a per-packet TTL shorter than `MaxAgeTicks`.
Typed `TryReserveAdmission(..., out rejection)` distinguishes congested
background admission from a physically full queue.

`CaptureReceipts(afterSequence, limit)` is a bounded and replayable
receipt stream with monotonically increasing sequence numbers, a stable
transport-instance UUID and explicit `Gap` reporting for evicted
events or invalid cursors. `ReceiptHistoryCapacity` bounds retained
events (default 1024, 0 disables retention); the poll limit is 1–256.
`TryCancel(StreamId, PacketId)` prevents stale packet IDs from
cancelling a packet after a restart. Untracked legacy `Enqueue`
calls remain unchanged and do not allocate receipts.

See [Packet completion contract](docs/packet-receipts.md) for precise
usage, wire-ready DTO fields, limitations and planned BYOND bridge
integration. **The current named pipe still exposes only topology and
route operations**; packet send/receipt poll operations are not yet
wired to a live gameplay packet engine, and an acknowledged hopwise
simulated delivery is not an application response from another player.
No synthetic benchmark result is presented as a player-visible ACK.

## M22 — Hysteretic overload admission and honest tail-latency telemetry

**Experimental branch:** `feature/congestion-latency-observability`,
stacked on M21. J/K/L/M and the legacy G/H/I modes remain unchanged
by default.

Congestion control (`BackgroundCongestionHighPercent=0` by default) is an
opt-in, **admission-only** guard. While there are interactive packets or
interactive pre-route leases outstanding, a background request is rejected
once queue occupancy (including reserved slots) reaches a configurable
high watermark. Background admission resumes after occupancy falls to
the lower watermark, or immediately when interactive demand disappears.
Default demo N uses **40% high / 20% low** on the shared bounded packet
queue. This does not evict any accepted packet, exceed the queue cap,
or invalidate an existing `PacketAdmissionLease`. Both route pre-admission
and direct packet enqueue enforce the guard. Every rejection is
counted separately from queue-full rejection; this is *not* a
promise that accepted interactive requests finish faster.

The latency diagnostics separate **successful completions** from
uncompleted/lost work:
- Per-class first-hop P95/P99 and completed-delivery P95/P99 (rolling
  last 1024 samples each). A first hop is recorded only once a packet
  actually forwards; this is not a first-response/ack guarantee.
- `InteractiveLatencyObjectiveTicks` is an **observational** objective
  (default 10 ticks = 1 second at 100ms/tick), not a kill deadline.
  The monitor counts completed interactive packets that missed it,
  currently outstanding packets past it, and oldest in-flight age.
- Per-class expired and dropped counts prevent low delivered-packet
  latency percentiles from hiding failed traffic.
- The Host half-second timeline adds class first-hop P95/P99, delivery
  P99, pending over objective, oldest ages, class losses, completion
  objective misses, and background congestion rejection rates.

Network Observatory **N** is M plus the configurable overload guard.
Short CI J/K/L/M/N tests check accounting and invariants automatically;
no full user-machine rerun is required. N can intentionally reject more
background route requests to preserve capacity: throughput and loss
must be evaluated alongside interactive latency on target hardware
before enabling by default.

This stage does **not** yet implement explicit per-request ACKs, real
SS13 packet capture/replay, sender retry contracts or admission feedback
to BYOND. A realistic interactive response SLO will require tracking a
response back to the originating player and reporting failures.

## M21 — Demand-aware wireless airtime QoS

**Experimental branch** `feature/interactive-airtime-qos`, stacked on M20.
Old modes G/H/I and gameplay controls J/K/L retain their original settings.

- Optional `PrioritizeInteractiveAirtime` adds per-class wireless bytes to
  each existing channel-wide or spatial radio-cell airtime ledger. It does
  **not** create additional bandwidth or use a separate wireless channel.
- `InteractiveAirtimeReservePercent` (default 25%) protects that fraction
  of each radio airtime region while tick-start interactive-ready packets
  have not all been visited. Background hops cannot consume more than the
  unprotected share while such demand remains; interactive hops can use any
  free physical bytes. Once that ready snapshot is drained, background
  packets may borrow unused airtime **within the same tick**, rather than
  wasting a reserved share. With no interactive demand, background can
  immediately use all available capacity.
- The demand/reservation decision comes from the packet scheduler, based
  on remaining interactive-ready packets from the tick-start snapshot.
  Wired cable hops
  bypass airtime entirely. No preemption, packet eviction, or overbooking;
  overlapping spatial regions still reserve atomically.
- `SharedMediumStatistics` reports per-class wireless hop counts and bytes,
  plus protected-background deferrals. Host half-second history includes
  bytes/sec per class and protected deferrals/sec.
- Radio QoS requires interactive packet priorities and is off by default.
  An oversized background packet that exceeds the available per-tick
  background share may be deferred until the ready interactive snapshot
  drains or a later idle tick, and can expire under sustained interactive
  demand; QoS does **not** guarantee
  background completion under all traffic mixes. Nor does it prioritize
  radio hardware by preempting a transmission already admitted.
- The Network Observatory adds **M**, identical to L except for a
  25% demand-aware reservation of shared radio airtime. Under the chosen
  workload the new mode can be compared against J/K/L without changing
  topology, offered packet mix or scheduled reroute CPU budget.
- CI smoke tests cover the channel-wide and spatial-cell ledgers, per-tick
  interactive reservations, idle-time borrowing, exact physical limits
  and class-accounting. The automatic workflow compares **J/K/L/M**;
  results are correctness evidence only, not a server latency SLA.

No further manual benchmarks are needed for routine changes. Final QoS
policy selection and any server SLO should be measured as a milestone
on target hardware, with representative actual network traffic.

## M20 — Priority reroutes under bounded mixed gameplay load

**Experimental branch** `feature/interactive-reroute-qos`, stacked on M19.
J/K and the legacy G/H/I settings remain unchanged by default.

- Opt-in `PrioritizeInteractiveReroutes` adds separate interactive and
  background FIFO reroute ticket lanes; a configurable weighted 3:1 service
  policy picks tickets when both lanes are queued. Background requests
  retain a guaranteed share of **tickets taken**, not of CPU time.
- `InteractiveRerouteReservedSlots` reserves part of the existing
  `MaxPendingReroutes` budget against background queued and in-flight
  searches. Interactive requests still cannot exceed the same global
  total. This is bounded admission, **not** eviction or preemption of
  already-running background route searches.
- The event-driven capacity-blocked queues also use weighted class service.
  Expired and stale tickets are ignored; completion/cancellation frees
  the correct class capacity. A missing or broken link remains subject to
  retry backoff and packet TTL.
- Host schedules interactive reroutes with interactive CPU-work priority
  only when the new option is enabled. The existing reroute CPU budget,
  worker count and total search capacity remain unchanged.
- Telemetry includes per-class queued/in-flight reroutes, started searches,
  capacity-blocked packets, wakeups and P95 reroute wait.
- **Correctness fix for M19 weighted ready lanes:** only packets present
  in a class queue at tick start are eligible to be visited that tick.
  Newly requeued packets are deferred to the next tick so that no
  packet can traverse multiple hops in a single 100ms simulation tick.

The Network Observatory offers **L**, identical to K except for reroute
prioritization (3:1 weighted tickets and four reserved interactive slots
out of the existing 64 ticket slots). Use J/K/L for controlled A/B/C;
do not mix with the uniform G/H/I stress profile.

The automated GitHub Actions gameplay workflow runs **J, K and L** (512
nodes, 6 seconds/scenario) and attaches all three archives plus summaries.
The smoke is a correctness contract check, not a performance ranking.
No additional manual benchmark is requested per change. End-to-end
player responsiveness on the target server still needs a milestone
test with representative topology and a latency budget.

**Limitations:** This does not yet implement radio-airtime QoS, file
transfer fragmentation, live game traffic replay, digital entities or
netrunning gameplay.

## M19 — SS13 mixed traffic benchmark + opt-in interactive packet service

### Automated short J/K checks

Pull requests changing the transport or game-workload code automatically run
the separate \`SS13 gameplay J/K smoke\` GitHub Actions workflow. It runs
J and K on the SAME Linux runner with a **short 512-node, 6-second/scenario**
profile (seed 1337) and verifies queue bounds, reservations, per-class
routing counters, delivery accounting and successful scenario completion.

The workflow publishes J/K raw three-scenario ZIPs, summary JSON and
Markdown, and the Host log as Actions artifacts. The Markdown results are
also displayed in the GitHub Actions run summary. These quick runs are
correctness/observability checks: CI does **not** require K to beat J
in delivery P95 on shared hardware. They do **not** replace long-form
performance qualification.

The 12,000-node, 120-second manual suites are now **milestone-only**,
ideally on the target SS13 server. Do not request a full user-run
benchmark after each transport change.



**Experimental branch:** `feature/ss13-mixed-traffic`, stacked on
M18. Does not change H/I preset defaults or the core workload when
`trafficProfile="uniform"`.

The prior 12k/1500 routes/s synthetic uniform workload saturates rerouting
and cannot prove suitability for a gameplay network. The alternative
`ss13-mixed` profile models **assumed** (not yet replayed from a live SS13
round) traffic: 85% nearby endpoint pairs, 20% interactive requests with
64–256B packets, background requests with 512–4096B packets, and cyclical
bursts 3x baseline for 2 seconds every 20 seconds. All values are configurable
on the backend. Baseline rate `routesPerSecond` is exclusive of bursts.
Routes use the same bounded scheduler and shared-medium radio simulation
as the uniform profile. Each scenario still tests static, moving, and
moving+outages under the configured game-like load.

With `prioritizeInteractiveTraffic=true` and event-driven waiting enabled:
- The transport stores interactive and background ready packets in separate
  bounded logical lanes, visiting 3 interactive per 1 background while
  **both** are ready. The weight is configurable in `PacketTransportOptions`.
  The background lane is not strictly FIFO behind all interactive traffic.
- `interactiveReservedSlots` reserves a subset of the **total packet queue
  capacity** against background producers, including pending pre-route
  leases. Interactive requests can consume any free capacity; the reservation
  is **not a second queue**. There is no packet eviction or unbounded growth.
- `PacketTrafficClass` travels with the packet and its admission lease,
  independent of a future player's digital entity or NPC controller. If
  priorities are disabled, the original single ready queue is used.
- Per-class offered/admitted/rejected route counters are reported in
  `metrics.benchmark.load`. The packet transport exposes per-class delivered,
  currently queued, pre-route rejections, and delivery-age P95. History tracks
  both lanes' queue depths, delivery rates and P95 delivery ages.
- This is a **first step** in prioritization: it does not yet prioritise
  reroute *tickets* or per-hop radio airtime; end-to-end interactive latency
  improvement is **not guaranteed** and requires real measurements.

The Network Observatory exposes J/K three-scenario A/B presets:
**J = gameplay traffic with transport priority off; K = identical gameplay
traffic with transport priority on and 256 slots reserved**. Both use I's
existing event-driven scheduler, Dense A*, 12k nodes, 120 base offered
requests/s, 120 movements/s and 3 link changes/s, with optional bursts.
Names of old G/H/I ZIP exports are unchanged; gameplay exports include a
`-ss13-...` suffix identifying the tested traffic shape and priority mode.

The random benchmark is a **synthetic game-inspired approximation**, not
evidence about the actual SS13 round distribution or player SLOs. Next phase:
realistic trace replay, interactive first-response semantics, P95/P99 objectives
and per-class saturation/fairness experiments on target server hardware.

## M18 — Event-driven packet waiting (opt-in)

### M18.5 — GC snapshots and optional EventPipe trace

The process monitor now adds last-completed GC generation/index,
per-GC pause duration, concurrent/compacted flags, heap size,
fragmentation, committed bytes, and allocation totals. Collection deltas
and the last full blocking GC are distinguished. The same fields are
exposed in history and three-scenario ZIPs. Heap details describe the
last completed GC, not necessarily the current live heap.

For an optional GC event trace, install the diagnostic tool:

~~~powershell
dotnet tool install --global dotnet-trace
~~~

Before starting a suite, run one capture per mode:

~~~powershell
pwsh -File scripts/capture-stalls.ps1 -Label H -DurationSeconds 420
pwsh -File scripts/capture-stalls.ps1 -Label I -DurationSeconds 420
~~~

These are **separate** runs; start the corresponding H or I suite
while that capture is recording. The helper discovers the real NetDM
PID through localhost /api/health and collects a low-overhead
gc-collect EventPipe .nettrace while an external PowerShell process
records heartbeat/HTTP/CPU liveness to CSV. Each run writes a manifest
with PID and UTC timestamps to correlate the artifacts. Use
-TraceProfile gc-verbose only if sampled allocation events are needed,
since that mode may change benchmark performance.

For each mode retain the benchmark suite ZIP, external CSV, trace
and manifest. Traces may include runtime/application metadata; review
them before sharing. EventPipe GC collection events provide stronger
evidence about GC cause and timing than cumulative GC pause totals.
OS scheduler/context-switch diagnostics would require an additional
OS trace and are not captured by this script.

### M18.4 — independent external process watchdog

`scripts/observe-host.ps1` runs in **a separate PowerShell 7 process**
during an H or I three-scenario experiment. It records a monotonic
100-ms cadence, asynchronous localhost `/api/health` responsiveness
every second and optionally the host process's liveness and cumulative
CPU seconds. It outputs a timestamped CSV independently of
`NetDM.Host` (no shared ThreadPool or GC). Launch one copy before each
three-scenario suite, e.g.:

```powershell
pwsh -File scripts/observe-host.ps1 -DurationSeconds 420 -BaseUrl http://127.0.0.1:5087 -OutputPath benchmarks/results/H-external.csv
```

Choose a distinct output file for I. Run the suite during the 420-second
observation window, then attach **both the original suite ZIP and
the external CSV**. An external watchdog gap concurrent with a packet
runner/history gap suggests a broader host/OS scheduling pause;
continuous external samples with HTTP failure imply a NetDM process
problem. If the external clock remains regular and HTTP succeeds while
the packet runner misses ticks, investigate only the dedicated runner.
These are **diagnostic indications**, not conclusive proof of causes.

The HTTP request is asynchronous and never blocks the watchdog loop.
Files are flushed each second; no privileged OS tracing is needed.
The test suite smoke-checks CSV generation on Linux and Windows.

### M18.3 — process-wide independent heartbeat and ThreadPool observations

During each benchmark scenario, a low-frequency independent background
OS thread samples monotonic wake-up lateness every ~100 ms and queues
at most one ThreadPool dispatch probe every ~200 ms. It records process
CPU cores used, current ThreadPool worker count and pending item count,
GC pause duration relative to the scenario start, and bounded recent
events (up to 64) when heartbeat or ThreadPool dispatch delay is >=250 ms.
All timestamps are UTC and should be correlated with the packet runner
lag/skip counters and sample interval gaps. There is no socket, file IO,
unbounded queue or full-tracing subscription in the hot path.

`metrics.benchmark.processStalls` contains the snapshot including
`events`. Each history entry includes heartbeat lateness, maxima,
ThreadPool counts/dispatch delay, sampled CPU cores, GC pause and actual
sample interval. A maximum heartbeat gap means the monitor OS thread
did not execute for that interval; it is **not** proof of a GC pause or
of an entire-process stop. Conversely a stalled ThreadPool probe with
regular heartbeats is evidence of a worker pool scheduling problem.
GC totals are process-wide and cannot prove the pause happened inside
a particular span; corroborate with EventPipe or OS tracing.

H and I transport algorithms are unchanged. Re-run both on the same
host and report the original ZIP files; no baseline gain is claimed
from this observability-only revision.

### M18.2 — stall attribution counters

The packet runner now exports `maxExecutionMilliseconds` (maximum
wall time in any individual `Advance` call) and
`overPeriodExecutionTicks` (calls exceeding the scheduled tick period).
Packet transport adds `maxAdvanceMilliseconds`,
`maxLockWaitMilliseconds`, and `lockWaitOver100Milliseconds`.
All maxima cover the entire isolated benchmark, not a rolling window.
These counters are included in the existing three-scenario JSON export.

The counters distinguish slow forwarding or monitor contention from
delayed packet-thread scheduling. They cannot identify the underlying
OS cause by themselves. The H/I results at seed 1337 showed several
5–6 second telemetry gaps with tick skipping; controlled repeats with
these diagnostics are required before assigning cause to I.

### M18.1 — event-driven reroute capacity (experimental profile I)

`eventDrivenRerouteCapacity` extends H with a capacity-blocked state.
When a reroute finishes/cancels, the transport wakes only as many
waiting packets as there are free reroute slots, rather than polling
the saturated queue every eight ticks. H remains the timer-only control.

Blocked packets remain indexed for local topology changes and receive
a bounded fallback probe (default 32 ticks) to recover unannounced
physical cable repairs. Versioned FIFO entries ignore obsolete wakeups;
admission limits, TTL and one-hop-per-tick guarantees are unchanged.
Counters: `capacityBlockedPackets`, `capacityWakeups`.

TTL expiry heap compaction is now amortized to at most one rebuild per
512 packet retirements, preventing repeated full-heap rebuilds while
holding the forwarding lock. Tick runner maximum start lag now
correctly includes overdue and skipped intervals.

**I** = H + `eventDrivenRerouteCapacity=true`. The new three-scenario
profile has filenames ending `-eventwait-capacitywake` for safe
comparison against G and H. This remains opt-in until measured.



The `eventDrivenPacketWaiting` option is disabled by default to preserve
the original FIFO as a reproducible control. When enabled, ready packets
run through a round-robin FIFO while packets delayed by negative-route
backoff and reroute-queue saturation are parked in a min-heap.
Packets awaiting reroute are also parked; a safety probe every eight
ticks detects unreported cable recovery. An index from failed-hop
endpoints to waiting packet IDs wakes only affected packets when
`NotifyRadioUpdates` reports a local change.

An independent expiry heap enforces TTL while packets sleep. Parked
packets and in-flight admission leases still consume the same bounded
capacity. Generation checks reject stale heap/ready entries and heap
compaction bounds retained references after frequent changes.
An admitted packet can make at most one hop per simulation tick.
Radio airtime and channel capacity are unchanged.

**H** is identical to **G** (Dense A*, 4096-capacity queue, adaptive
4096 visits/tick, pre-route backpressure, two reroute workers,
96-tick backoff cap, TTL 900, 12k nodes, 1500 offered/s, seed 1337)
except for `eventDrivenPacketWaiting=true`.
ZIP names contain `-eventwait` or `-fifo` to avoid overwriting controls.

New packet counters report ready, delayed and awaiting-reroute
packets, timer/topology wakeups and expiry during parking.
Compare G and H over all three 120-second scenarios and evaluate
delivery, TTL, visit efficiency, reroute pressure and tick execution
before changing the default. No throughput improvement is presumed.

## M17 — Adaptive packet forwarding and pre-route backpressure

The Dense A* 12k-node test found that a static network can calculate
~419 original routes/s while the forwarding loop, limited to 1,024
packet visits each 100ms tick, performs only ~10k hops/s. At ~72 radio
hops/delivery, that is about 140 packet deliveries/s even in an ideal
uncongested model. The full transport queue rejects the surplus AFTER
expensive A* work has been spent. Faster routing alone cannot fix that.

### Independent budgets and fair packet rotation

A new opt-in `adaptivePacketVisits` switch adjusts packet visits per
tick. It starts at **min(1024, maxVisits)** and grows the limit by up to
25% when the queue remains full enough and wall work takes at most
60% of the tick's CPU/wall-time budget. If work reaches 80% of the
budget, it reduces the limit by 25%, never below the starting floor.
Every tick still honors **maxVisits** as a hard visit count and
`packetTickWorkBudgetMs` (25ms default) as a soft wall-time cutoff.
Packets are processed in bounded FIFO round-robin order and **never
advance twice in one simulation tick**. If the limit or time budget
expires, unvisited packets retain their place in the queue.

The new `packetQueueCapacity` option (default 2048) is intentionally
separate from the visit limit. With a 2048-packet queue, raising
maxVisits to 4096 cannot process more than 2048 *different* packets in
one tick. The comparison presets therefore raise both when testing
4096 visits. This is an application processing limit, **not more
physical radio capacity**; channel/Z spatial airtime rules remain
unchanged and are checked on every hop.

### Reserve queue capacity before computing A*

Opt-in `packetPreRouteBackpressure` uses
`RadioPacketTransport.TryReserveAdmission` to reserve one bounded
queue slot **before** submitting a route task to the main work
scheduler. If the transport is full, that offered route is recorded
as a **transport-backpressured rejection**, not a route that was
computed and subsequently discarded.

The reservation is a single-owner, idempotently disposable
`PacketAdmissionLease`. A running route consumes its lease upon
enqueue; routes without a path and failed/cancelled tasks dispose
theirs. The route producer also releases it if CPU admission fails
or the scheduled task is cancelled before it starts. Unreserved
enqueues cannot steal reserved capacity. Each lease is included in
the queue-capacity check; no unbounded implicit admission occurs.

Rejections are stage-specific: `load.offered` still counts every
generated request, `load.admitted` means **the main CPU scheduler
accepted the route computation**, and `load.rejected` includes both
scheduler and transport admission denials. The additional
`load.transportBackpressured` is the TRANSPORT subset of rejected
requests, not an extra category to add to offered/admitted totals.

### Useful-work and fairness telemetry

- Packet counters: `forwardingVisits`, `visitBudgetStops`,
  `currentVisitLimit`, `adaptiveLimitIncreases`,
  `adaptiveLimitDecreases`, `preRouteBackpressureRejections`,
  `reservedAdmissions`, and `admissionReservations`.
- Each history sample: routes offered/admitted/rejected per second,
  packets enqueued per second, packets delivered per second,
  *transport-backpressured* requests per second, forwarding visits
  per second, adaptive visit limit, visit-budget stops, and current
  outstanding admission leases.
- Network Observatory charts: **Transport pipeline** and
  **Adaptive forwarding**, in addition to previous delivery p95,
  reroute pressure and packet timing.

### Controlled E / F / G three-scenario experiments

Select one of these profiles in **Three-scenario diagnostic suite**:

| Profile | Dense A* | Visit scheduling | Queue capacity | Pre-route backpressure |
|---|---|---|---:|---|
| **E — control** | yes | fixed 1024 visits/tick | 2048 | off |
| **F — adaptive** | yes | adaptive up to 4096 visits/tick | 4096 | off |
| **G — backpressure** | yes | adaptive up to 4096 visits/tick | 4096 | on |

All profiles use two reroute workers, negative route backoff cap 96
ticks, TTL=900, dedicated packet ticks and spatial reuse. Run each
profile separately: **3 × 120s, 12k nodes, 1500 offered routes/s,
seed 1337**. The ZIP/JSON names include search mode, worker count,
backoff, packet queue capacity, visit budget, and the adaptive/
backpressure flags to keep downloads distinct.

E→F isolates a *combination* of adaptive visit count and a larger
queue, not the effect of each parameter individually. F→G isolates
pre-route backpressure at identical queue and visit settings. The
manual benchmark can vary either value individually for extra A/B
experiments.

Compare actual offered/admitted routes, found routes, packet
acceptance, **delivered packets and latency p95**, queue occupancy,
visit-budget stops, tick execution p95 and achieved moves/s.
An increase in route admissions that is not accompanied by better
packet delivery is not an improvement in the end-to-end service.

Smoke tests verify concurrent lease admission, cancellation/release,
queue bounds, adaptive growth, fixed-visit regression, and one-hop
per packet per tick. Linux/Windows integration validates the
three-scenario ZIP and per-stage transport accounting.

### Limits

The benchmark remains a logical packet simulator, not a true RF
collision MAC or a production transport protocol. Backpressure
rejects new transport-bound route requests rather than blocking or
retrying them, so interactive callers need their own bounded retry
policy. The 25ms budget is soft and does not preempt a single hop.
Neither the effectiveness of the new strategy nor latency under the
full 12k-node load is claimed before running E/F/G on target hardware.
