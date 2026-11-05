# Architecture / design notes (M0)

```text
DreamDaemon (32-bit, single game thread)
    | batched events / bounded result polling [future]
    v
  x86 native shim [future]
    | local IPC, framed/versioned binary messages [future]
    v
  NetDM.Host (.NET 10, x64 process)
    | JSON-lines + named pipes (bootstrap protocol)
    | bounded mutation Channel<T>, one mutation consumer
    v
  NetDM.Core
    | immutable structurally shared topology snapshots
    +---- routing tasks on captured snapshots (parallel)
```

## Ownership / threading

- **TopologyStore** publishes a new `TopologySnapshot` via `Volatile.Write` after each successful mutation.
- The writer uses a private lock. Readers use `Volatile.Read` and never acquire that lock.
- Each `Node` and its immutable adjacency dictionary is immutable; updates replace only changed paths.
- `Router.Find` uses request-local dictionaries and a priority queue. No mutable global route state.
- Host applies mutations sequentially with a bounded channel; route calculations can run concurrently on captured snapshots.
- A route's `version` is the topology it saw, not a claim that the world is still at that version.

## Important limitations before production

- Current host's routing semaphore is **per connection**. Global admission control and CPU work quotas must be implemented before load testing in production.
- `ReadLineAsync` allocates the entire input line before size checking. The production transport must enforce frame length **before** allocation.
- Per-connection task tracking uses a `ConcurrentBag<Task>`, which retains completed tasks for connection lifetime. Use a bounded in-flight task set and explicit completion handling.
- Asynchronous route ordering vs mutations is not a globally linearizable command stream. The returned topology version is the consistency contract.
- No topology batching yet: frequent single-edge changes still allocate immutable dictionary updates and publish one snapshot per change.
- No route cache, no spatial neighbor discovery, no multi-network isolation, no directed/permissioned links.
- The default pipe is not yet access-controlled or authenticated. Do not expose it to untrusted processes or networks.
- No persistence or recovery snapshot protocol. Host restart currently clears graph.
- Node IDs are caller-defined; integrating DM must handle generation counters to avoid stale entity-ID reuse.

## Suggested next milestones

**M1: Transport & lifecycle.** Explicit protocol version, bounded binary frames, handshake, authentication/pipe ACLs, request IDs, leases, heartbeat/reconnect, idempotent batch mutations, resync snapshots, and global admission control. Implement an x86 BYOND shim that never blocks the game thread on routing.

**M2: Scalable dynamic topology.** Batch deltas, node positions and radio/wired link policy, spatial hash/grid, incremental neighborhood recalculation, graph partitions, directed links, optimized route cache invalidation and bounded per-tick CPU budgets.

**M3: Gameplay.** Networks/sectors, ACLs, node devices, power dependency, discoverability, bandwidth/latency QoS, intrusion/ICE/events, scripts and plugin contracts.

**M4: Production.** Benchmark representative real station maps and action traces, P50/P95/P99 latency, memory growth and GC pauses, observability, overload admission, restart resilience and compatibility tests with BlueMoon-Station.

## Decision: out-of-process

A 64-bit .NET 10 sidecar avoids trying to host the managed runtime directly inside 32-bit DreamDaemon. It adds an IPC hop, but isolates GC and compute threads from the game thread and allows independent profiling/restart. Future binary transport and batch delivery should keep the boundary cost low.
