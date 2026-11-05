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
