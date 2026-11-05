# netDM architecture — M1 (prototype)

## Runtime deployment

```text
  BlueMoon-Station / DreamDaemon [future]          Observability browser
            |                                            |
    Small 32-bit native shim                    HTTP, 127.0.0.1:5087
       (not implemented)                                  |
            | local framed IPC [future]                   |
            v                                            v
        .NET 10 x64 NetDM.Host ------------------- Network Observatory
            |
            +-- local named-pipe request handlers (bootstrap protocol)
            |       | global route semaphore: bounded admission
            |       + mutations call a synchronized graph store
            |
            +-- BenchmarkRunner: reproducible route and mutation workload
            |
            +-- NetDM.Core / NetworkRuntime
                    + TopologyStore: immutable snapshot + spatial index
                    + Router: request-local Dijkstra calculations
                    + RouteCache: bounded, versioned route cache
                    + NetworkMetrics: rolling latency + counters
```

## Graph consistency

TopologyStore owns one published TopologySnapshot with:

- `Version`: increments on *any* effective topology or position batch
- `ConnectivityVersion`: increments only if links, nodes, or costs actually change
- `Nodes`: immutable dictionary of immutable nodes and adjacency lists
- `SpatialCells`: immutable cell-ID mapping (32 world units/cell)

Changes are validated before acquiring the writer lock, mutated against
immutable dictionary *builders*, then published via `Volatile.Write` once per
batch. Readers capture one `Volatile.Read` snapshot, using only that immutable
data until their computation completes. Routing never holds the writer lock.

Cache keys are `(ConnectivityVersion, sourceId, targetId)`, not
`(TopologyVersion, ...)`. Move-only batches retain route-cache utility.
A cached response's `Version` is updated to the latest observed topology
version when reused. **A new or deleted link invalidates all cached routes** in
this prototype; selective invalidation is a future optimization.

Commands in a batch are applied in order. A missing node/duplicate edge is
an explicit no-op, and the response includes applied/skipped counts. Invalid
IDs and link costs reject the batch. Batch updates are atomic to readers, but
not transactional for caller semantic errors.

## Routing

Weighted undirected Dijkstra; arbitrary nonnegative edge costs (positive
integer costs in this prototype). Complexity O((V + E) log V), route-local
allocations and bounded exploration. This is intentionally a baseline, not
an optimized shortest-path engine for 30k-node real-time routing. Planned:
hierarchical partitions, bidirectional search, selective cache invalidation,
topology-aware routing and multi-commodity traffic approximation.

## Benchmark semantics / reproducibility

Generator builds a rectangular connected weighted grid with occasional
shortcuts using deterministic `Random(seed)`. Independent asynchronous
route workers exercise `NetworkRuntime.Route`, and the mutation worker moves
positions in small random steps and creates/removes occasional links.
Each worker has a 100 ms quota loop. **The workload does not guarantee the
target route count per second under saturation.** Results report achieved
throughput per sample, moving-node rate, cache-hit percentage and latency.

A routing metric's p50/p95/p99 comes from the latest 2048 recorded route
latencies. It is an approximate rolling window, not lifetime exact percentile.
`ManagedBytes` uses `GC.GetTotalMemory(false)`; process working set is
reported separately. Route-heat samples only one in 16 successful routes and
is therefore an illustrative *sampled activity*, not per-link bandwidth.

Benchmarks run inside the host process; results include any self-interference
from UI HTTP requests and the host's GC. Real production benchmarking must
also measure costs of the 32-bit shim and DM game thread, use fixed hardware
and trace replay, and export machine-readable measurements.

## UI and large-network behavior

Browser canvas uses camera transforms for pan/zoom. The map endpoint uses
`SpatialCells` to query visible nodes and returns only edges with both
endpoints present. Browser rendering is not on the server's simulation
threads. Auto-refresh is around 1.6s; metric updates around 1s.

For huge zoomed-out viewports, the spatial index intentionally falls back to
a node scan rather than iterating millions of empty cells. The per-response
node cap avoids unbounded JSON/map work. **A truncated view is not a complete
representation of the graph**; the UI identifies truncation and asks you to
zoom in. Future zoomed-out views should use aggregated tiles and LOD rather
than arbitrarily sampling nodes.

## Known limitations / correctness and security

- No game objects, physics or global DM state are accessed from managed worker threads.
- DreamDaemon is 32-bit; embedding x64 .NET into it is **not** attempted.
- HTTP dashboard is localhost-only and **has no authentication or CSRF
  protection**. Never bind it to a public interface. Production observability
  must have an authenticated, isolated admin interface.
- Bootstrap named-pipe messages are newline-delimited JSON. The current
  `ReadLineAsync` allocates before its 64 KiB post-read limit; replace with
  pre-allocation-bounded frames for production.
- Global route semaphore caps simultaneously admitted pipe route tasks.
  It is **not** a complete global CPU budget (benchmark route workers are
  independent). Add one global scheduler and fair quotas before production.
- No durable graph persistence, event replay, reconnect/resync, permissions
  or multi-network isolation. Restart clears the entire graph.
- Position/index updates have immutable allocation costs. Profile memory and
  mutation throughput at high move rates before selecting a different data
  structure such as copy-on-write adjacency arrays / RCU segments.
- The current cost model is symmetric static weights. No dynamic link
  bandwidth, queueing delay, radio interference or timed game logic.
- Stable ID ownership and generation counters must be implemented in the DM
  bridge; do not forward DM references, `datum` handles or 64-bit IDs as
  unsafe JavaScript numbers.
- There is no automatic cable/radio neighbor discovery yet: positions only
  support spatial queries and visualization. A node's movement does not
  automatically change its links.

## Next implementation order

1. Extend protocol: handshake, frame limits, sequence IDs, replay and
   reconnection; one nonblocking native x86 bridge at the DM boundary.
2. Add world lifecycle synchronization and ownership contracts: BYOND controls
   power, equipment and physical entities; NetDM controls the logical network.
3. Introduce spatial link rules: wired/LOS/radio ranges, incremental index
   updates, network realms and restrictions.
4. Improve route engine for worst-case paths and mutable large graphs;
   compare Dijkstra, bidirectional Dijkstra and hierarchical routing with
   explicit benchmark scenarios.
5. Add independent scheduler with per-priority work budgets, channel depth,
   dropped/rejected counts, and P95/P99 instrumentation.
6. Add reproducible SS13 map traces and realistic benchmark baselines; only
   then choose production SLO targets.

## M2 experiment: graph representations and routing algorithms

Four interchangeable **measurement-only** route implementations are available.
The mutable graph is an immutable snapshot of dictionaries with structural
sharing. The experimental CSR projection sorts stable IDs and materializes
compact contiguous adjacency arrays (`offsets[n + 1]`, `arcs[2m]`) and a
ID→index lookup. It eliminates per-hop dictionary traversal but has a full
projection build cost and is *not* incrementally maintained.

`BidirectionalRouter.Find` and `CompactRouter.Bidirectional` expand a
weighted frontier from each endpoint and stop when the minimal unresolved
distance lower bound cannot improve the best known connection. They are
valid for the present **undirected, positive-weight** graph; directed / timed
links need a reverse index and separate algorithm review.

`RoutingComparisonRunner` constructs deterministic graph topology with a
weighted 2-D backbone and random shortcuts; then benchmarks identical pairs
without cached routes, with warmup outside timings. P50/P95/P99 reported in
µs; average explored nodes; allocated bytes for each synchronous request;
aggregate serial equivalent QPS; viewport spatial hash vs full scan; 64-node
movement batches; CSR build time and estimated payload bytes.

**Correctness is a prerequisite, not just a score.** The comparison checks
existence and shortest-path cost against baseline, and independently validates
every successful route's exact edge sequence and sum. Smoke tests further
exercise random weighted and partially disconnected graphs across algorithms.

### Critical interpretation of measurements

- A slower/faster CSR route does not establish a faster *dynamic* engine:
  amortize CSR rebuild across edge mutations, and account for its extra
  memory and synchronization cost.
- `SerialQueriesPerSecond` is derived from measured synchronous query time
  on one background worker. It is **not** a measured system-wide requests/s
  under multi-worker load or a BYOND tick budget.
- The current movement microbench updates an immutable index and records
  per-batch allocations, but compares no alternative mutable spatial structure.
  The full-scan comparison verifies the index returns the same nodes.
- Benchmark numbers depend on CPU, JIT, GC and concurrent activity. Avoid
  comparing runs across unrelated hardware and workloads.
- In the live `NetworkRuntime`, deliberately small exploration budgets bypass
  the shared route cache; they must never poison full-budget route answers.
- Moving a node affects viewport geometry only in the current model, not
  wired/radio reachability. This must change when spatial link policies exist.

### Follow-up

Gather experiments on 1k, 10k and 30k nodes with at least three repeated seeds
and report median and variance. Introduce links that appear/disappear when moving
within radio range, and replay topology deltas from BlueMoon-Station. Then
implement a partitioned/compact-hot immutable read projection only if the
measurements show a net benefit after rebuild and memory costs.

## Benchmark automation / artifacts

To reproduce comparative measurements rather than trusting one result,
`scripts/run-profiles.ps1` drives the comparison HTTP API across 1k, 10k,
and 30k nodes and the seeds `1337,42,2026`. It writes one JSON per
configuration and a consolidated CSV containing the lowest observed p95
algorithm per run. No actual large-network performance claims have yet been
established by these implementation changes alone.

The script is cross-platform PowerShell 7. With `-LaunchHost` it starts the
Release .NET 10 host on loopback and terminates it after testing. Use it
only when the loopback port `5087` is available and the live benchmark is
not running. The manual Actions workflow stores the reports as artifacts;
shared CI runners are not suitable for capacity planning.

Note: the current comparison uses one query worker and isolates its
synthetic graph from the live network, but competes for process CPU
and memory if some other workload is started independently. It is intended
for algorithm selection before integration, not for hard latency guarantees.

## Incremental spatial-radio topology (M3 foundation)

`DynamicRadioNetwork` is an **optional domain service** above the
`NetworkRuntime`. It owns auto-discovered radio links, radio profiles and
registered cable links; `TopologyStore` remains a generic undirected graph.

For a movement/profile-update batch:

1. Validate all node IDs, positions and radio profiles against the currently
   published graph and reject duplicate updates.
2. Stage desired new positions/profiles without modifying the current graph.
3. For each changed endpoint, enumerate its *previous radio neighbors* plus
   candidate nodes from the immutable spatial cell index near the **new**
   endpoint position. Include cells staged for concurrently moved endpoints.
4. Evaluate mutual radio reachability from the final intended positions:
   `same channel && same Z && distance <= min(rangeA, rangeB)`.
5. Merge radio links with explicitly registered physical cables, using the
   smaller available cost for overlapping links. Generate only effective
   connect/disconnect/cost changes.
6. Submit all movements + link deltas as **one atomic graph batch**. Only on
   success update radio link ownership and neighbor indexes.

There is no full graph scan in the movement hot path. The spatial cells
limit discovery to nearby nodes, while the prior adjacency index ensures
old links that leave range are removed. No radio profile -> no auto links.
Node removal cleans radio and cable ownership to permit later ID reuse.

**Important current limits**: discovery models 2-D circles on a single
Z-plane, ignores walls and physics, supports up to 128 spatial units per radio,
and assumes one domain service owns its managed edges. Large overlapping
broadcast areas still produce potentially quadratic physical links; this
requires an explicit radio-density admission policy or shared-medium model
before production. Initialization of many radio nodes currently publishes a
node snapshot before publishing their auto-links; it is only used during
benchmark startup, before workload readers begin. A production world
lifecycle manager must construct these changes transactionally.

The `wirelessEnabled` benchmark option creates actual proximity edges and
simulates radio outages by toggling profiles, as well as independent
position changes. Observatory counters and the topology visualizer derive
from the resulting graph, not a UI-only approximation.

### Failure and cancellation contracts

The current radio layer implements a conservative per-updated-endpoint
**128 candidate** fail-fast ceiling to guard against unbounded pairwise radio
cliques. The threshold is an implementation safeguard, not the eventual
per-channel capacity or realistic RF topology.

The planning phase checks cancellation; no partial graph changes or radio
ledger mutations are published if planning is aborted. The graph commit
and ownership ledger update are intentionally non-cancellable after
commit begins. Range checks avoid squared-distance overflow for teleports
between extreme 32-bit map coordinates.

## Shared-medium logical domains (M4 prototype)

**Why:** `DynamicRadioNetwork` models each radio pair as a graph edge,
creating quadratic storage when many devices share a large coverage area.
The new `SharedMediumNetwork` is a separate model with the following
trade-off:

- Radio memberships: immutable `RadioMediumCell(channel, z, gridX, gridY)`
  map to immutable node-ID sets. Storage grows with devices and occupied
  cells rather than number of radio pairs.
- Route search: Dijkstra merges physical cable neighbours with **lazy
  radio neighbours** from nearby domain cells, applying mutual range,
  channel, and Z tests at relaxation time. It does not assume that every
  device sharing a channel can reach every other directly.
- Route consistency: one `SharedMediumSnapshot` contains both a captured
  physical `TopologySnapshot` and the corresponding radio membership
  index. Readers never mix two movement epochs. Updates under a single
  writer lock publish one new composite snapshot.
- Cache invalidation: independent radio-domain version increments on
  meaningful updates (including movement of configured devices), even
  when the physical graph's `ConnectivityVersion` does not change.
- Physical links: cables still use `TopologyStore`, but are written
  through the medium owner so the composite snapshot stays coherent.

### Shared airtime

`TryTransmit(route, payloadBytes, tick)` verifies a route's snapshot
version, identifies cable versus radio hops, then reserves bytes for each
radio hop against the capacity of its `RadioDomainKey(channel, Z)`.
All reservations happen under one short lock and are committed atomically
only when every used domain has sufficient capacity in that tick.

The benchmark uses a monotonic `Stopwatch` clock quantized to approximately
100 ms and sends synthetic payload sizes of 128–1024 bytes after successful
route queries. Accepted/rejected transfer counts are **synthetic offered
traffic**, not measured real BYOND packets.

There is no current RF collision/backoff/propagation or spatial reuse.
Moreover, independent radio clusters on the same channel/Z compete for
one shared airtime pool in this first approximation.

### Scalability warnings

No quadratic **storage** does not imply no quadratic **CPU**: densely
populated cells can still cause large lazy-neighbour scans in Dijkstra.
The first performance studies should compare cold and warm query latency,
high-density configurations (hundreds/thousands of devices per cell),
radio movement churn, and heap memory across both implementations.

The dashboard renders *occupied channel cells* in the shared mode rather
than inventing direct radio edges. Up to 1,500 domain tiles are returned per
map query, and the backend does not serialize the entire dense radio graph.

Implementation is intentionally isolated from BYOND. For a production
shared medium, add hierarchical intra-domain routing, contention-aware
queue scheduling, packet lifetime/retry policies, per-device permissions
and a native bridge with bounded framed IPC.

## Global CPU admission scheduler (M5)

`PriorityWorkScheduler` is a standalone disposable .NET Core component.
It has a fixed worker pool, finite FIFO queues per priority and a
globally capped backlog. Scheduling is nonblocking: `TrySchedule`
reports rejection immediately rather than consuming additional server
threads. Cancellation is honoured while queued and during routing.
Completion/failure/cancellation counts and rolling queue wait/operation
latencies are published to host telemetry.

Weighted round robin (Critical:Interactive:Background = 4:2:1) ensures
background jobs continue to receive service when a high-priority stream
is sustained. It is not strict deadline scheduling or an SS13 tick
scheduler. The worker soft 65ms/100ms admission gate uses elapsed
`Stopwatch` time and cannot interrupt a running Dijkstra; it may
overrun when workers execute concurrently. Treat it as **load shaping**,
not a hard CPU or DreamDaemon tick SLA.

The global scheduler is used at the asynchronous host boundary and
in the synthetic workload driver; it does **not** automatically govern
direct calls inside a game module to `NetworkRuntime.Route`.
The host still requires protocol framing and authenticated client
isolation before accepting untrusted connections. Per-player and
per-sector quotas, cost estimates and hardware-measured service
targets belong to subsequent integration work.

### Dense shared-medium route optimization

`SharedMediumNetwork.RouteCore` now checks the best discovered target
upper bound before exploring a queued route candidate. If no unsettled
node has lower cost than the best target, positive-weight Dijkstra
can return an exact shortest path without scanning all remaining
same-cost radio neighbours. This is particularly effective when
radio endpoints share a dense cell. It is safe for mixed cable/radio
weights because any cheaper candidate still has a lower priority
and must be processed first.

It does not eliminate worst-case quadratic *query* complexity for
large, sparse hop-separated connected domains. Hierarchical routing,
portal nodes and spatially organized cost bounds remain candidates
for future experiments.

## M6: interpret stress measurements without false speedups

The uploaded real-world 12k-node benchmark had 28,369 completed route
queries in ~120.35s versus a target of 1,500/s, with 4,618 found
routes and only 246 accepted airtime reservations. Final radio
membership was 9,647 active devices; the fast final window coincided
with reduced live radio participation. This is **not yet proof** of
network fragmentation: component count/connectivity is not recorded,
so the observation is a strong hypothesis only.

Use `foundPercent` and `mediumConfiguredDevices` alongside
actual throughput to discriminate expensive connected routing from
fast disconnected searches. Treat startup latency separately from the
steady state (JIT, warm caches and network connectivity all change).

The `SharedMediumStatistics` rejection counters now distinguish
stale snapshots, invalid paths, impossible per-packet radio-hop
budgets, temporarily exhausted capacity, and regressed ticks.
Each failed transmission increments exactly one reason, and the
sum must equal `RejectedTransfers`. A long radio path consumes
airtime for every hop, so 8,192 bytes/tick cannot necessarily carry
even one moderate payload end-to-end.

Route-latency p50/p95/p99 describe the last rolling window of
completed searches; queue-wait p95 describes scheduling delay.
They are NOT interchangeable, and the total user response latency
is not simply the latest values of both percentiles added together.
The scheduler itself reported zero admission rejections in the
uploaded scenario, so do not claim CPU exhaustion solely from
failed radio transmissions.

Next experiments:
1. Hold node positions/radio profiles fixed for a static routing baseline.
2. Enable movement with zero outages, then enable outages with the same seed.
3. Add sampled connected-component estimators or bounded landmark
   reachability diagnostics for the shared-medium model.
4. Calibrate airtime against hop count, payload size, and realistic
   game network latency; introduce explicit packet queues and
   contention instead of interpreting every denial as a routing error.

## M8: local route validation vs global snapshot epochs

A snapshot generation advance does not imply that every previously found
path has become disconnected. For a 12k-node radio world with 1k moves/s,
strict whole-network version equality caused thousands of unnecessary
transmission rejections.

`SharedMediumNetwork.TryTransmit` now pins one current snapshot, walks
the supplied route and verifies that **every endpoint still exists**
and that every consecutive pair is connected by a current cable or an
eligible same-channel, same-Z, mutual-range radio hop. It recomputes
radio-airtime usage from the current topology and atomically reserves
the resulting channel budget. It does **not** trust the old route cost
as the transmission cost. If the original route version is older but
every hop survives, it can be transmitted; a successful such reservation
increments `AcceptedRevalidatedTransfers`.

If an obsolete path actually contains a missing node or invalid hop,
`RejectedStaleRoute` increments. Malformed paths at the current epoch
increment `RejectedInvalidPath`. Any future-version path is rejected.
Packet routing and transmission are still concurrent: a topology change
after the pinned snapshot is read is not synchronously prevented. The
next milestone is reliable per-hop packet progression with bounded queues
and per-hop version/availability checks.

## Three-scenario experiment orchestration (M9)

`ThreeScenarioBenchmarkSuite` runs fixed ordered experiment cases using
`BenchmarkRunner` with an exclusive reservation. Reservation is checked
inside the benchmark's writer lock: a manual benchmark cannot reset
the runtime between stages, and manual stop cannot cancel the suite's
current stage. The algorithm-comparison endpoint also refuses starts
while the suite is active.

The suite runs on the ASP.NET Core host regardless of browser connection.
It waits for each underlying benchmark task to **finish** (including its
producer/mutation worker drain), captures current metrics/history, then
starts the next stage with identical network size, seed and radio settings.
The old runner declared its status `complete` slightly *before* worker
drain; this was moved after the drain to ensure a stable captured report.

Three reports are retained in memory, each scoped to a random run ID, and
can be requested individually or as a ZIP once all three complete.
Reports have compact topology summaries to avoid serializing 12k nodes;
their metrics and per-interval history match the manual export envelope.
The data survives browser reloads but **not host restarts**. The next
suite invalidates old download links and clears the three reports.

CI runs a small 3 x 3-second server-owned experiment, verifies that a
manual start is rejected during execution, and downloads/inspects every
JSON plus the combined ZIP. The canonical dashboard button starts
3 x 120-second runs on 12k devices.

## M10 prototype: exact radio A* and store-and-forward transport

An A* solver is opt-in for Shared Medium. Its heuristic is a
mathematically safe bound on the cost of wireless progress:

`floor(distance / configuredMaximumRange) * configuredMinimumCost`.

The registration API validates that all radio profiles obey these
bounds. The heuristic is disabled whenever the physical graph has
arbitrary cable edges, since cheap teleport-like connections would
make a geometric lower bound inadmissible. The solver uses its own
per-query priority queue and maintains exact shortest-path cost;
smoke tests compare it with the existing Dijkstra on moving networks,
disconnected domains, cable shortcuts and dense grids.

`RadioPacketTransport` is a bounded, deterministic tick-driven queue.
A radio packet may advance **once per tick**, consuming shared airtime
only for that hop. Queue full -> immediate reject; channel busy -> defer;
next link unavailable -> bounded route recalculation from current node;
TTL or repeated failures -> drop. Delivery counts only final-hop
success. The benchmark schedules forwarding passes through the same
global CPU work scheduler, at Interactive priority.

`GC.GetTotalPauseDuration` and sampled thread-local allocation deltas
enable comparison of allocation churn against network cost. Per-route
allocation estimates include routing/cache overhead on the worker
thread; they exclude concurrent allocations on unrelated threads.
GC pause totals describe the .NET process, not BYOND or x86 IPC.
The history's per-interval GC pause figure is the difference between
successive monotonic totals divided by elapsed sample seconds.

### Safety and remaining work

- Packet forwarding currently validates a hop on a pinned read snapshot;
  another thread can mutate the graph between that check and the airtime
  reservation. No end-to-end topology lease is provided yet.
- Per-hop reroute work can be expensive in dense disconnected networks;
  work per tick and number of retries are bounded, but a scheduler
  budget is still a *soft* CPU quota.
- Old `TryTransmit` reserves entire routes in a single tick; new packet
  transport is opt-in so historical performance data remains interpretable.
- Packet simulation does not implement encryption, bandwidth queues per
  sender, retransmission acknowledgements, line-of-sight, packet
  fragmentation or reliable ordering.
- Calibrate A* on static connected 12k-node workloads before choosing a
  default; fewer explored nodes does not automatically mean lower
  p95 latency if spatial heuristic overhead dominates.

## M11: per-cell interference budget, retry fairness and metrics

`SharedMediumNetwork.TryTransmitHop` chooses either
`RadioAirtimeScope.ChannelWide` or `RadioAirtimeScope.SpatialCells`
when instantiated. The legacy `TryTransmit` method deliberately
retains whole-route global capacity semantics for compatibility.
Switching the hop scope is per Shared Medium instance, not runtime
mutable configuration. The benchmark uses SpatialCells by default
for the opt-in hopwise transport, but can select ChannelWide for an
A/B experiment.

SpatialCells approximates channel contention by reserving the
payload bytes across all grid cells of a swept, range-inflated
rectangle containing the transmitter and receiver. Its width is
proportional to the larger endpoint range, and it is keyed by
(channel, Z, Xcell, Ycell). Under one medium lock it performs
two phases: verify **every** touched cell can admit the packet, then
debit **every** cell. A single busy cell denies the entire hop with
no partial charge. Separate distant cells can share the channel.
The ledger resets on increasing simulation tick; older ticks are
rejected. No physical throughput claim should be inferred from the
count of cell reservations: it is deliberately duplicated airtime
bookkeeping per cell, not unique payload bytes transmitted.

`RadioPacketTransport` uses a bounded FIFO round-robin rotation.
At the start of `Advance(tick)`, it fixes a maximum visit count
and never reprocesses a packet in the same tick. Full queues still
cannot grow without bound and each accepted packet remains eligible
for a visit within approximately
`ceil(Capacity / MaxAdvancesPerTick)` ticks. Busy packets are deferred
without allocating new tasks. Retries after missing links wait for
`NoRouteRetryDelayTicks` (default 3), and max reroutes and TTL remain
hard bounds. Default TTL has been raised to 900 ticks and the per-tick
visit budget to 1024.

Delivery-age p50/p95 are quantiles over a ring of 1,024 most recently
delivered packets; this is NOT a full-history percentile. Mean delivered
hops is cumulative over delivered packets only. Per-sample deferrals
and spatial-success counts are differences of cumulative counters.

No actual RF behavior, packet serialization, sender isolation,
packet acknowledgements, or hard deadline scheduling are implemented.
The queue is still processed by the shared priority work scheduler,
and expensive reroute computations are cooperative, not preemptible.

### M11 verification

Smoke tests exercise two distant radio pairs sharing a channel in the
**same tick**, overlapping interference regions denying simultaneous
transmission, tick rollover and old-tick rejection, bounded packet
admission, recovery after temporary disconnection and delivery ages.
HTTP integration runs A* with spatial hopwise forwarding and checks
per-stage JSON statistics from the three-scenario suite.

## M12: separate reroute work from packet forwarding

Packet forwarding and full shortest-path search have radically
different CPU characteristics. The old `Advance` called
`SharedMediumNetwork.Route` while holding the packet queue lock.
In motion-heavy runs, this caused long critical sections and
interfered with original routing at the Interactive priority.

The M12 ownership model is:

- A packet resides in a bounded round-robin queue for its entire life.
- Failed next-hop validation generates a bounded `PacketRerouteRequest`
  containing a packet ID, request generation, current source node,
  destination and tick. `Advance` never calls `Route`.
- A second bounded `PriorityWorkScheduler` executes reroute requests
  (1 worker and 25ms soft time-window budget by default) independently
  of the primary player/packet-advance scheduler.
- Results are applied under a short lock, **only** if the request is
  still the packet's current outstanding ticket, its source node is
  unchanged and it has not been expired or dropped.
- Cancelling a scheduled job returns the packet to a retryable state.
  Expired packets cannot be resurrected. Excess ticket demand is
  deferred without enqueueing an unbounded number of tasks.

The ticket buffer has 64 slots, of which at most
`MaxConcurrentReroutes` may be in flight. The dedicated CPU executor
also has a 32-item backlog ceiling. Both limits are independent of
the 2,048-packet admission queue and of the primary host's 256-job
scheduler backlog.

`PacketTransportCounters` now exposes ticket depth/inflight, reroute
completion/cancellation/admission rejection counts, p95 waiting from
ticket creation to completion (ticks), p95 `Advance` duration (ms),
first-hop wait p50/p95 (ticks) and skipped forwarding ticks.
The benchmark stores per-interval deltas and the latest p95 values,
including separate reroute worker queue depth/wait.

These counters distinguish application delivery, actual work admitted,
CPU backlog, and missing packet ticks. Soft CPU budgeting still does
not guarantee hard real-time deadlines. Reroute workers are not yet
safe for untrusted clients: the host remains localhost-only and the
future BYOND bridge needs per-client quotas/authentication.

### Next measurements

Repeat the 3 x 120s scenario suite at 12k nodes, A* + Hopwise +
Spatial reuse, TTL=900, reroute workers=1 and CPU budget=25ms/100ms.
Compare against the previous run:
- delivered packets and first-hop p95;
- reroute requests/s, in-flight count, queue deferrals;
- skipped ticks and tick admission failures/s;
- original-route completed requests/s, global work wait/exec p95;
- reroute scheduler's own CPU admission and work p95.

A full 12k-node benchmark has **not** been rerun by the CI tests.
