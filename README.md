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
