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
