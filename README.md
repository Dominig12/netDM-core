# netDM-core

A standalone **C# / .NET 10** network topology and routing engine for SS13/BYOND.

## Scope of the first milestone

- Undirected, weighted graph with stable 64-bit node IDs
- Immutable, structurally shared snapshots; readers do not block topology writers
- Atomic add/remove/connect/disconnect commands and topology versioning
- Dijkstra shortest-path routing with stale-priority handling and bounded exploration
- Local Windows/Linux named-pipe sidecar host with JSON-lines request/response protocol
- Serialized mutation channel; concurrent route calculations
- Dependency-free executable smoke tests

**This is a bootstrap, not a performance-validated production server.** Topology auto-discovery, spatial indexes, route caching, QoS, packet simulation, durable event logs, permissions, admission control, client reconnect and the BYOND x86 bridge are not implemented yet.

## Run

Install the .NET 10 SDK, then:

```sh
dotnet build src/NetDM.Host/NetDM.Host.csproj
dotnet run --project tests/NetDM.SmokeTests
dotnet run --project src/NetDM.Host -- netdm-core
```

The host listens on the local named pipe `netdm-core`. The first CLI argument overrides its name. Each connection sends newline-delimited JSON; one response per request. Responses may arrive out of order when routing overlaps other requests; match them by `id`.

Example commands:

```json
{"id":"1","op":"add_node","a":1}
{"id":"2","op":"add_node","a":2}
{"id":"3","op":"connect","a":1,"b":2,"cost":4}
{"id":"4","op":"route","a":1,"b":2}
{"id":"5","op":"stats"}
```

The `route` response includes `version`, `found`, `cost`, and the `path` node ID array. Mutations return `changed`; topology queries include `nodeCount` when applicable.

**Consistency:** each route reads one immutable snapshot. A mutation acknowledged after a route started need not affect that route; compare topology versions or submit a new route after the mutation acknowledgement.

## Design rules

1. BYOND owns world entities and physical/gameplay actions. NetDM owns logical network topology and route calculations.
2. The bridge sends stable entity IDs and batched deltas; never pass DM object pointers across process boundaries.
3. The only native boundary to BYOND should be a small asynchronous x86 transport shim. Do not load the x64 .NET host into DreamDaemon.
4. Game-world changes are applied only on the DM main thread. NetDM responds with results/events, never directly mutates the BYOND world.
5. Treat input from DM as untrusted until validated at the bridge/host; a later milestone must add authentication, quotas and robust backpressure.

See [docs/architecture.md](docs/architecture.md) for architecture and limitations.
