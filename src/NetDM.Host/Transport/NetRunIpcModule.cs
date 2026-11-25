using System.Text.Json;
using NetRun.Core;

namespace NetDM.Host.Transport;

/// <summary>
/// Control plane of the NETRUN domain. These operations describe digital hosts, hand out and revoke
/// capabilities and run registered actions. They never impersonate a network delivery between DM
/// endpoints: a command that must reach a physical device still travels over NETDM.
/// </summary>
internal sealed class NetRunIpcModule(NetRunHostService netrun) : IHostIpcModule
{
    public IReadOnlyCollection<string> Operations { get; } =
    [
        "netrun_principal", "netrun_host", "netrun_destroy", "netrun_lifecycle",
        "netrun_grant", "netrun_revoke", "netrun_actions", "netrun_execute", "netrun_complete",
        "netrun_knowledge", "netrun_view", "netrun_panel", "netrun_program", "netrun_program_stop",
        "netrun_life", "netrun_presence", "netrun_state"
    ];

    // NETRUN administration has to work while the map snapshot is still being synchronized: the domain
    // is not the topology, and a host can be declared before its node is published.
    public bool RequiresNetworkReady => false;

    public HostIpcResult Execute(string operation, JsonElement? payload) => operation switch
    {
        "netrun_principal" => Principal(payload),
        "netrun_host" => Host(payload),
        "netrun_destroy" => Destroy(payload),
        "netrun_lifecycle" => Lifecycle(payload),
        "netrun_grant" => Grant(payload),
        "netrun_revoke" => Revoke(payload),
        "netrun_actions" => Actions(payload),
        "netrun_execute" => ExecuteAction(payload),
        "netrun_complete" => CompleteAction(payload),
        "netrun_knowledge" => KnowledgeProjection(payload),
        "netrun_view" => ObservationProjection(payload),
        "netrun_panel" => PanelProjection(payload),
        "netrun_program" => ProgramImport(payload),
        "netrun_program_stop" => ProgramStop(payload),
        "netrun_life" => LifeOperation(payload),
        "netrun_presence" => PresenceOperation(payload),
        "netrun_state" => State(payload),
        _ => new HostIpcResult(false, "unknown_operation", null)
    };

    private HostIpcResult Principal(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body || Text(body, "name") is not { Length: > 0 and <= 64 } name)
            return new HostIpcResult(false, "invalid_request", null);
        var principal = netrun.Principal(name);
        return principal.IsNone
            ? new HostIpcResult(false, "limit_exceeded", null)
            : new HostIpcResult(true, null, new { principal = principal.Value });
    }

    private HostIpcResult Host(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body)
            return new HostIpcResult(false, "invalid_request", null);
        if (Text(body, "name") is not { Length: > 0 and <= 64 } name)
            return new HostIpcResult(false, "invalid_request", null);
        if (!TryHostClass(Text(body, "class"), out var hostClass))
            return new HostIpcResult(false, "invalid_class", null);

        NetworkComponent? network = null;
        if (Integer(body, "node") is { } node)
            network = new NetworkComponent(new NetDM.Core.NodeId((ulong)node),
                Text(body, "domain") ?? "station", Flag(body, "wired") ?? true,
                Clamp(Integer(body, "radioRange"), 0, 64, 7), Clamp(Integer(body, "radioChannel"), 0, 65_535, 1));

        ComputeComponent? compute = null;
        if (Integer(body, "capacityCu") is { } capacity)
            compute = new ComputeComponent(Clamp(capacity, 1, 100_000, 4),
                Clamp(Integer(body, "rateCu"), 0, 100_000, 2));

        MemoryComponent? memory = Integer(body, "memorySlots") is { } slots
            ? new MemoryComponent(Clamp(slots, 0, 100_000, 4))
            : null;
        StorageComponent? storage = Integer(body, "storageBytes") is { } bytes
            ? new StorageComponent(Math.Max(0, bytes))
            : null;

        SecurityComponent? security = null;
        if (Text(body, "security") is { } profileName)
        {
            if (!TryProfile(profileName, out var profile))
                return new HostIpcResult(false, "invalid_security", null);
            security = new SecurityComponent(profile,
                Clamp(Integer(body, "maxServices"), 0, 256, 8),
                Clamp(Integer(body, "maxPresences"), 0, 64, 1));
        }

        ServicesComponent? services = null;
        if (body.TryGetProperty("services", out var servicesJson) && servicesJson.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in servicesJson.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    return new HostIpcResult(false, "invalid_services", null);
                var instance = property.Value.GetString();
                if (property.Name.Length is 0 or > 64 || string.IsNullOrEmpty(instance) || instance.Length > 64)
                    return new HostIpcResult(false, "invalid_services", null);
                map[property.Name] = instance;
            }
            if (map.Count is > 16)
                return new HostIpcResult(false, "invalid_services", null);
            if (map.Count > 0)
                services = new ServicesComponent(map);
        }

        var definition = new HostDefinition(name, hostClass, network, compute, memory, storage, security, services);
        var result = netrun.RegisterHost(definition);
        return result.Host is { } host
            ? new HostIpcResult(result.Succeeded, result.Succeeded ? null : StatusCode(result.Status),
                new { status = StatusCode(result.Status), host = host.Id.Value, generation = host.Generation.Value })
            : new HostIpcResult(result.Succeeded, StatusCode(result.Status), null);
    }

    private HostIpcResult Destroy(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body || !TryHostRef(body, out var id, out var generation))
            return new HostIpcResult(false, "invalid_request", null);
        var result = netrun.DestroyHost(id, generation);
        return new HostIpcResult(result.Status == HostStatus.Ok, StatusCode(result.Status),
            new { status = StatusCode(result.Status) });
    }

    private HostIpcResult Lifecycle(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body || !TryHostRef(body, out var id, out var generation))
            return new HostIpcResult(false, "invalid_request", null);
        var powered = Flag(body, "powered") ?? true;
        var operational = Flag(body, "operational") ?? true;
        var result = netrun.SetLifecycle(id, generation, powered, operational);
        return new HostIpcResult(result.Status == HostStatus.Ok, StatusCode(result.Status),
            new { status = StatusCode(result.Status) });
    }

    private HostIpcResult Grant(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body)
            return new HostIpcResult(false, "invalid_request", null);
        if (Text(body, "subject") is not { Length: > 0 } subjectName || !TryCapability(Text(body, "category"), out var category))
            return new HostIpcResult(false, "invalid_request", null);
        // A grant names a host, not a generation: the capability is revoked together with the host when
        // it is destroyed, so a stale generation would only make administration harder to express.
        if (Integer(body, "host") is not { } hostValue || hostValue <= 0)
            return new HostIpcResult(false, "invalid_request", null);
        var target = new HostId((ulong)hostValue);
        var service = Text(body, "service");
        if (service is { Length: > 64 })
            return new HostIpcResult(false, "invalid_service", null);
        var lifetime = Clamp(Integer(body, "lifetimeMs"), 1, 3_600_000, 60_000);
        var subject = netrun.Principal(subjectName);
        if (subject.IsNone)
            return new HostIpcResult(false, "limit_exceeded", null);
        var granted = netrun.Grant(subject, target, category, service, lifetime);
        return granted.Allowed
            ? new HostIpcResult(true, null, new { grant = granted.Token.Grant.Value, fence = granted.Token.Fence })
            : new HostIpcResult(false, granted.Status.ToString().ToLowerInvariant(), null);
    }

    private HostIpcResult Revoke(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body || Integer(body, "grant") is not { } grant)
            return new HostIpcResult(false, "invalid_request", null);
        var status = netrun.Revoke(new GrantId((ulong)grant));
        return new HostIpcResult(status == AuthorizationStatus.Revoked, status.ToString().ToLowerInvariant(), null);
    }

    private HostIpcResult Actions(JsonElement? payload)
    {
        var limit = Clamp(Integer(payload, "limit"), 1, 512, 128);
        var catalogue = netrun.Actions(limit);
        return new HostIpcResult(true, null, new
        {
            count = catalogue.Length,
            actions = catalogue.Select(action => new
            {
                id = action.Id,
                version = action.Version,
                kind = action.Kind.ToString().ToLowerInvariant(),
                category = action.Category.ToString().ToLowerInvariant(),
                capability = action.RequiredCapability.ToString().ToLowerInvariant(),
                computeCost = action.ComputeCost,
                memoryCost = action.MemoryCost,
                cooldownMs = action.CooldownMilliseconds,
                rangeHops = action.RangeHops,
                displayName = action.DisplayName,
                documentation = action.Documentation,
                service = action.ServiceKey
            }).ToArray()
        });
    }

    private HostIpcResult ExecuteAction(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body)
            return new HostIpcResult(false, "invalid_request", null);
        if (Text(body, "principal") is not { Length: > 0 } principalName ||
            Text(body, "action") is not { Length: > 0 and <= 96 } actionId ||
            Text(body, "key") is not { Length: > 0 and <= 96 } key)
            return new HostIpcResult(false, "invalid_request", null);
        if (Integer(body, "execution") is not { } execution || Integer(body, "target") is not { } target)
            return new HostIpcResult(false, "invalid_request", null);
        // A remote action travels as the endpoint that speaks for the principal. Binding is optional so a
        // local query still works for an operator that has not spoken on the network yet.
        if (Text(body, "endpoint") is { Length: > 0 and <= 64 } endpointId)
        {
            if (Integer(body, "generation") is not { } generationValue || generationValue <= 0)
                return new HostIpcResult(false, "invalid_request", null);
            var subject = netrun.Principal(principalName);
            if (subject.IsNone)
                return new HostIpcResult(false, "limit_exceeded", null);
            var bound = netrun.BindEndpoint(subject, new NetDM.Services.EndpointKey(endpointId, (ulong)generationValue));
            if (bound != NetDM.Services.ServiceStatus.Ok)
                return new HostIpcResult(false, bound.ToString().ToLowerInvariant(), null);
        }
        // A presence acts under its life: the controlling generation travels with the request and is fenced.
        var identity = Integer(body, "identity") is { } identityValue && identityValue > 0
            ? new NetRun.Core.IdentityId((ulong)identityValue) : NetRun.Core.IdentityId.None;
        var control = (ulong)Clamp(Integer(body, "control"), 0, int.MaxValue, 0);
        var outcome = netrun.Execute(principalName, actionId, new HostId((ulong)execution), new HostId((ulong)target),
            key, identity, control);
        return new HostIpcResult(true, null, new
        {
            status = StatusCode(outcome.Status),
            operation = outcome.OperationId.Value,
            cooldownRemaining = outcome.CooldownRemainingMilliseconds,
            reason = outcome.Reason,
            result = outcome.Result
        });
    }

    private HostIpcResult CompleteAction(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body)
            return new HostIpcResult(false, "invalid_request", null);
        if (Integer(body, "operation") is not { } operation || operation <= 0)
            return new HostIpcResult(false, "invalid_request", null);
        if (!TryActionStatus(Text(body, "status"), out var status))
            return new HostIpcResult(false, "invalid_status", null);
        var outcome = netrun.Complete(new OperationId((ulong)operation), status, Text(body, "reason"),
            Text(body, "result"));
        return new HostIpcResult(true, null, new
        {
            status = StatusCode(outcome.Status),
            operation = outcome.OperationId.Value,
            reason = outcome.Reason,
            result = outcome.Result
        });
    }

    private static bool TryActionStatus(string? name, out ActionStatus status)
    {
        switch (name?.ToLowerInvariant())
        {
            case "committed" or "applied" or "ok":
                status = ActionStatus.Committed;
                return true;
            case "cancelled" or "cancelled_by_target":
                status = ActionStatus.Cancelled;
                return true;
            case "failed" or "rejected" or "rejected_by_world":
                status = ActionStatus.Rejected;
                return true;
            case "unknown" or "unknown_outcome":
                status = ActionStatus.UnknownOutcome;
                return true;
            default:
                status = ActionStatus.Rejected;
                return false;
        }
    }

    /// <summary>
    /// What one observer knows, with the age of every record and whether it is still fresh. This is the
    /// projection the client renders for the map: stale knowledge is shown as stale, never as live data.
    /// </summary>
    private HostIpcResult KnowledgeProjection(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body || Text(body, "principal") is not { Length: > 0 } name)
            return new HostIpcResult(false, "invalid_request", null);
        var limit = Clamp(Integer(body, "limit"), 1, 512, 128);
        var now = netrun.State(1).NowMilliseconds;
        var records = netrun.Knowledge(name, limit);
        return new HostIpcResult(true, null, new
        {
            count = records.Length,
            entries = records.Select(record => new
            {
                host = record.Host.Value,
                level = record.Level.ToString().ToLowerInvariant(),
                confidence = record.Confidence,
                ageMs = record.AgeMilliseconds(now),
                stale = netrun.IsStale(record)
            }).ToArray()
        });
    }

    /// <summary>
    /// The player-facing projection of the digital world. It shows only what the observer learned, never the
    /// identity of a host it has only scanned, and nothing at all about hosts it cannot discover.
    /// </summary>
    private HostIpcResult ObservationProjection(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body || Text(body, "principal") is not { Length: > 0 } name)
            return new HostIpcResult(false, "invalid_request", null);
        var limit = Clamp(Integer(body, "limit"), 1, 256, 64);
        var views = netrun.View(name, limit);
        return new HostIpcResult(true, null, new { count = views.Length, hosts = views });
    }

    /// <summary>The whole client refresh in one call: bounded, already filtered, safe to hand to a player.</summary>
    private HostIpcResult PanelProjection(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body || Text(body, "principal") is not { Length: > 0 } name)
            return new HostIpcResult(false, "invalid_request", null);
        var hosts = Clamp(Integer(body, "hosts"), 1, 256, 64);
        var actions = Clamp(Integer(body, "actions"), 1, 256, 64);
        var operations = Clamp(Integer(body, "operations"), 0, 64, 12);
        return new HostIpcResult(true, null, netrun.Panel(name, hosts, actions, operations));
    }

    /// <summary>Imports program text and starts it as the caller's principal on its own host.</summary>
    private HostIpcResult ProgramImport(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body)
            return new HostIpcResult(false, "invalid_request", null);
        if (Text(body, "principal") is not { Length: > 0 } name ||
            Text(body, "source") is not { Length: > 0 } source ||
            Integer(body, "execution") is not { } execution || execution <= 0)
            return new HostIpcResult(false, "invalid_request", null);
        var started = netrun.StartProgram(name, source, new HostId((ulong)execution), Text(body, "name") ?? "",
            Flag(body, "confirm") ?? false);
        return new HostIpcResult(started.Status == ProgramStatus.Ok,
            started.Status == ProgramStatus.Ok ? null : StatusCode(started.Status),
            new
            {
                status = StatusCode(started.Status),
                process = started.Process.Value,
                name = started.Name,
                digest = started.Digest,
                reason = started.Reason
            });
    }

    /// <summary>Stops one program of the caller.</summary>
    private HostIpcResult ProgramStop(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body ||
            Text(body, "principal") is not { Length: > 0 } name ||
            Integer(body, "process") is not { } process || process <= 0)
            return new HostIpcResult(false, "invalid_request", null);
        var status = netrun.StopProgram(name, new ProcessId((ulong)process));
        return new HostIpcResult(status == ProgramStatus.Ok, StatusCode(status),
            new { status = StatusCode(status) });
    }

    /// <summary>
    /// Digital life operations: create the round''s identity, move its core, take and restore a backup. The
    /// controlling generation is returned with every answer, so the caller always knows what it may still do.
    /// </summary>
    /// <summary>
    /// Presence and travel: a life enters where its core lives, moves over a route the transport plans, or
    /// aborts a move before it lands. The answer carries where it stands and when it arrives (§8.1).
    /// </summary>
    private HostIpcResult PresenceOperation(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body)
            return new HostIpcResult(false, "invalid_request", null);
        if (Integer(body, "identity") is not { } identityValue || identityValue <= 0)
            return new HostIpcResult(false, "invalid_request", null);
        var identity = new NetRun.Core.IdentityId((ulong)identityValue);
        var action = Text(body, "action") ?? "";
        var result = action switch
        {
            "enter" when Integer(body, "host") is { } host && host > 0 =>
                netrun.EnterPresence(identity, new HostId((ulong)host)),
            "move" when Integer(body, "destination") is { } destination && destination > 0 =>
                netrun.MovePresence(identity, new HostId((ulong)destination)),
            "abort" => netrun.AbortPresence(identity),
            "leave" => netrun.LeavePresence(identity),
            _ => (PresenceResult?)null
        };
        if (result is not { } presence)
            return new HostIpcResult(false, "invalid_request", null);
        var snapshot = presence.Presence ?? netrun.PresenceOf(identity);
        return new HostIpcResult(presence.Succeeded, presence.Succeeded ? null : presence.Status.ToString().ToLowerInvariant(),
            new
            {
                status = presence.Status.ToString().ToLowerInvariant(),
                identity = identity.Value,
                host = snapshot?.Host.Value ?? 0,
                state = snapshot?.State.ToString().ToLowerInvariant() ?? "absent",
                destination = snapshot?.Destination.Value ?? 0,
                hops = snapshot?.Hops ?? 0,
                arriveAt = snapshot?.ArriveAtMilliseconds ?? 0,
                reason = presence.Reason
            });
    }

    private HostIpcResult LifeOperation(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body)
            return new HostIpcResult(false, "invalid_request", null);
        var action = Text(body, "action") ?? "";
        switch (action)
        {
            case "create":
            {
                if (Text(body, "principal") is not { Length: > 0 } name || Integer(body, "host") is not { } host ||
                    host <= 0)
                    return new HostIpcResult(false, "invalid_request", null);
                var controller = netrun.Principal(name);
                if (controller.IsNone)
                    return new HostIpcResult(false, "limit_exceeded", null);
                var (identity, created) = netrun.CreateLife(controller, new HostId((ulong)host));
                return created.Core is null
                    ? new HostIpcResult(false, StatusCode(created.Status), new { status = StatusCode(created.Status) })
                    : new HostIpcResult(true, null, LifeDto(identity, created));
            }
            default:
            {
                if (Integer(body, "identity") is not { } identityValue || identityValue <= 0)
                    return new HostIpcResult(false, "invalid_request", null);
                var identity = new IdentityId((ulong)identityValue);
                if (action == "backup")
                {
                    if (Integer(body, "host") is not { } storage || storage <= 0)
                        return new HostIpcResult(false, "invalid_request", null);
                    var (backup, result) = netrun.Backup(identity, new HostId((ulong)storage));
                    return new HostIpcResult(result.Core is not null || backup is not null,
                        StatusCode(result.Status),
                        new { status = StatusCode(result.Status), backup = backup?.Id ?? 0 });
                }
                if (action is "migrate" or "migrate_commit" or "migrate_cancel")
                {
                    var destination = Integer(body, "destination") is { } target && target > 0
                        ? new HostId((ulong)target) : HostId.None;
                    var result = netrun.Migrate(identity, destination, action == "migrate_commit",
                        (ulong)Clamp(Integer(body, "control"), 0, int.MaxValue, 0), Text(body, "reason"));
                    return LifeOutcome(identity, result);
                }
                if (action is "restore" or "restore_backup")
                {
                    if (Integer(body, "host") is not { } host || host <= 0)
                        return new HostIpcResult(false, "invalid_request", null);
                    var integrity = Clamp(Integer(body, "integrity"), 1, 100, 50);
                    var result = action == "restore"
                        ? netrun.RestoreLife(identity, new HostId((ulong)host), integrity)
                        : netrun.RestoreFromBackup(identity, (ulong)Clamp(Integer(body, "backup"), 0, int.MaxValue, 0),
                            new HostId((ulong)host), integrity);
                    return LifeOutcome(identity, result);
                }
                return new HostIpcResult(false, "unknown_action", null);
            }
        }
    }

    private static HostIpcResult LifeOutcome(IdentityId identity, LifeResult result) =>
        result.Core is null
            ? new HostIpcResult(result.Status is LifeStatus.Ok or LifeStatus.Duplicate, StatusCode(result.Status),
                new { status = StatusCode(result.Status) })
            : new HostIpcResult(result.Succeeded, result.Succeeded ? null : StatusCode(result.Status),
                LifeDto(identity, result));

    private static object LifeDto(IdentityId identity, LifeResult result) => new
    {
        status = StatusCode(result.Status),
        identity = identity.Value,
        host = result.Core?.Host.Value ?? 0,
        generation = result.Core?.HostGeneration.Value ?? 0,
        control = result.Core?.ControlGeneration ?? 0,
        integrity = result.Core?.Integrity ?? 0,
        state = result.Core?.State.ToString().ToLowerInvariant() ?? string.Empty,
        migrating = result.Core?.Migrating ?? false,
        destination = result.Core?.MigrationDestination.Value ?? 0,
        reason = result.Reason
    };

    private HostIpcResult State(JsonElement? payload)
    {
        var limit = Clamp(Integer(payload, "limit"), 1, 1_024, 256);
        return new HostIpcResult(true, null, netrun.State(limit));
    }

    /// <summary>Wire spelling of a status: the same snake_case the transport uses for its own codes.</summary>
    private static string StatusCode<T>(T status) where T : struct, Enum
    {
        var name = status.ToString();
        Span<char> buffer = stackalloc char[name.Length + 8];
        var length = 0;
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && char.IsLower(name[i - 1]))
                buffer[length++] = '_';
            buffer[length++] = char.ToLowerInvariant(name[i]);
        }
        return new string(buffer[..length]);
    }

    private static bool TryHostRef(JsonElement body, out HostId id, out HostGeneration generation)
    {
        id = HostId.None;
        generation = HostGeneration.None;
        if (Integer(body, "host") is not { } host || host <= 0 ||
            Integer(body, "generation") is not { } generationValue || generationValue <= 0)
            return false;
        id = new HostId((ulong)host);
        generation = new HostGeneration((ulong)generationValue);
        return true;
    }

    private static bool TryHostClass(string? name, out HostClass hostClass)
    {
        hostClass = HostClass.Terminal;
        if (name is null)
            return true;
        foreach (var candidate in Enum.GetValues<HostClass>())
        {
            if (!string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
                continue;
            hostClass = candidate;
            return true;
        }
        return false;
    }

    private static bool TryProfile(string name, out SecurityProfile profile)
    {
        foreach (var candidate in Enum.GetValues<SecurityProfile>())
        {
            if (!string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
                continue;
            profile = candidate;
            return true;
        }
        profile = SecurityProfile.Standard;
        return false;
    }

    private static bool TryCapability(string? name, out CapabilityCategory category)
    {
        foreach (var candidate in Enum.GetValues<CapabilityCategory>())
        {
            if (!string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
                continue;
            category = candidate;
            return true;
        }
        category = CapabilityCategory.Discovery;
        return false;
    }

    private static string? Text(JsonElement? body, string name) =>
        body is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Integer(JsonElement? body, string name)
    {
        if (body is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) &&
            number is >= int.MinValue and <= int.MaxValue)
            return (int)number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
            return parsed;
        return null;
    }

    private static bool? Flag(JsonElement? body, string name)
    {
        if (body is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var flag) => flag != 0,
            _ => null
        };
    }

    private static int Clamp(int? value, int minimum, int maximum, int fallback) =>
        value is { } present ? Math.Clamp(present, minimum, maximum) : fallback;
}
