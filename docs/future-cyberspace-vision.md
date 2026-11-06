# Future architecture vision: digital existence and cyberspace simulation

> **Status: concept / future scope — not scheduled for implementation.**
> This document records intended gameplay and architectural constraints. The
> current milestone remains network transport correctness, performance and
> reproducibility. Nothing described here is implemented merely by merging
> this document.

## Product direction

NetDM is intended to evolve beyond a routing or packet-transport library into
the backend of a shared **cyberspace simulation for Space Station 13**.

Future gameplay areas include:

- Network-resident intelligences and other digital entities.
- Player netrunning, including entering cyberspace from a physical character.
- Digital-born player characters, initially existing on a server.
- Network exploration, intrusion, access control, vulnerabilities and hacking.
- Filesystems, files, services, programs and persistent digital information.
- Scripting and simulated malware/virus mechanics.
- Autonomous network NPCs, bots, AI behavior and digital combat/ICE.
- Interactions among these systems within one causal simulation, rather than
  unrelated BYOND-only mechanics.

These are **planned capabilities, not commitments about individual gameplay
rules or their release order**. Details such as death, respawn, copying,
memory limits, resource economics and combat rules remain to be designed.

## Core principle: a player may be a digital entity

A **digital entity** is a gameplay identity/participant in the network.
It is not synonymous with an NPC, a physical server, a packet, a BYOND
`/mob`, a specific process, or a single avatar.

Two important player origins must be representable by the same underlying
identity model:

1. **Netrunner / immersed player** — a physical SS13 character remains in
   the physical world while the player controls a digital presence through a
   network connection. Loss of connection or physical-body damage should be
   representable without conflating the physical character and the digital
   presence. Exact consequences are future gameplay decisions.
2. **Digital-born player** — an entity comes into being on a server, with
   cyberspace as its original environment rather than an optional projection
   from a biological body. The initial server need not be a permanent anchor
   to identity; possible migration or distribution depends on future rules.

The player must be able to meet and interact with AI-driven entities on the
same network. An AI-driven entity and a player-driven entity should not require
two independent sets of network gameplay, permission or combat mechanics.

## Conceptual boundaries (proposed, not implemented)

### DigitalEntity — identity and durable gameplay state

- Stable simulation identity, independent of a current node or host machine.
- Entity-specific persistent state, abilities, memory and progression as
  future gameplay may require.
- Identity is distinct from individual running processes, projected avatars
  and physical controllers.
- Identity survival across disconnection, migration or host failure is a
  **gameplay policy**, not an unconditional guarantee.

### Controller — who supplies decisions

- `PlayerController`: user commands from an authenticated SS13 session.
- `AIController`: autonomous policy/NPC behavior.
- Potential ownership/handoff is a separate explicit operation; it must not
  accidentally duplicate or destroy an entity's identity.
- The entity's rights and interaction contracts should not depend on whether
  a player or an AI currently provides decisions.

### DigitalPresence — where and how the entity manifests

- One or more runtime manifestations on network hosts/nodes, including
  avatars, processes, sessions or replicas if supported by later mechanics.
- Presence has a lifecycle independent of identity: spawned, connected,
  disconnected, suspended, migrated or terminated.
- A distributed entity may own multiple presences or computation fragments.
  Avoid assuming that `entityId == nodeId` or `entityId == processId`.
- A multi-node distributed intelligence is a valid design target, including
  partial partitions and later synchronization. **No consistency/conflict
  model is decided yet.**

### Resource and simulation domains

- `Network/Transport`: connectivity, routing, delivery, channels, bandwidth,
  packet TTL and scheduling — the present scope.
- `Runtime/Simulation`: simulation time, entity lifecycle, bounded work,
  events, identity and causal state — potential future foundation.
- `Services/Data`: filesystem, access policies, program execution, digital
  resource ownership and persistence — future scope.
- `Gameplay`: intrusions, malware, AI behavior, netrunning, combat/ICE and
  progression — future scope.
- `Bridge`: BYOND/DreamDaemon owns physical-world entities, client
  interactions, local equipment and the UI; NetDM may own logical cyberspace
  state once the corresponding mechanics exist.

This is a responsibility sketch, **not a mandatory project or assembly split**.
A physically separate process is already part of current deployment planning,
but no new cross-process services are being requested now.

## Invariants to respect while optimizing today's transport

1. **Never hardwire network identity to location.** Routing uses node IDs,
   while future entities and presences must have separate identity spaces.
2. **Transport stays content-agnostic.** A transport hop must not know whether
   the payload is a file, netrunner interaction, AI command or cyberattack.
3. **Control plane vs gameplay semantics.** Distinguish state changes,
   commands, events and observations from packet movement. Future modules
   can build on the network without altering the routing algorithm.
4. **Clock and work budgets matter.** Preserve deterministic simulation-tick
   semantics where feasible, and avoid unbounded gameplay callbacks inside
   routing or packet locks. Different gameplay operations may need different
   latency and priority objectives.
5. **No untrusted host execution.** Future in-game scripts and viruses are
   simulated programs subject to explicit instruction/time/memory quotas
   and capabilities. They must not execute arbitrary .NET code, system shell
   commands or host filesystem operations.
6. **No implicit trust across the bridge.** Future entity control, access
   rights and persistence require explicit authenticated authority and
   bounded input, not arbitrary client-asserted entity IDs or paths.
7. **Preserve observability and fairness.** Measure per-class latency,
   service budgets, overload behavior and starvation instead of optimizing
   aggregate throughput alone.

## Consequences for future benchmarks

Current open-loop packet benchmarks are useful for optimizing transport, but
they are **not yet proxies for player-perceived cyberspace performance**.

Future acceptance tests should eventually include a mix of:

- Interactive netrunner commands with explicit responsiveness objectives.
- Long-lived sessions and many active but mostly idle digital presences.
- NPC/background activity competing with player operations.
- Filesystem operations and script-generated traffic.
- Movement, outages, partitions, reconnect and transfer cancellation.
- Persistent identities and consistency checks after host failures.

Latency budgets and workload distributions must be measured or agreed with
game designers before they become acceptance criteria. Never claim that
successful stress-test packet throughput alone proves satisfactory gameplay.

## Deferred decisions / non-goals for current milestones

**Do not implement digital entities, netrunning, scripting, filesystems,
malware, NPC intelligence or cyber-combat during the transport performance
work.** No speculative public interfaces or premature persistence formats
are required.

Open design questions, to resolve when work on the simulation layer starts:

- What separates identity, memory, processes and replicated instances?
- What persists when a host, body, connection or presence is destroyed?
- How do player/AI controller handoffs and concurrent control work?
- What authority does the BYOND server retain over digital-born players?
- Which entity actions need strong ordering, and what is eventually consistent?
- What are the quotas and security boundaries of in-game scripts?
- Which interactions are latency-sensitive vs bulk/background traffic?

The next active step remains profiling and stabilizing transport modes
G/H/I and the BYOND integration boundary. This document is intended as
a design constraint for that work, not as a diversion from it.
