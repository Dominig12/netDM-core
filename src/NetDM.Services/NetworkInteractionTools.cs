using System.Text.Json;
using NetDM.Core;

namespace NetDM.Services;

/// <summary>
/// The four transport-backed interactions available to a device, program or authorized
/// network entity. No action applies a gameplay effect locally: ServiceRuntime owns
/// validation, authorization, delivery and command/result state.
/// </summary>
public enum NetworkToolKind { Invoke, Watch, Unwatch, Publish }

/// <summary>
/// A verified endpoint is the actor. A resident shell may determine the physical
/// origin of an invocation, but can never lend the resident's authorization.
/// Publishing requires an explicit self-owned endpoint, never a borrowed provider.
/// </summary>
public sealed record NetworkToolActor(
    EndpointKey Principal,
    EndpointKey? Publisher = null,
    EndpointKey? ResidentProvider = null,
    string? ResidentInstance = null,
    NodeId? ResidentNode = null);

/// <summary>
/// Data-only action request. The same command can be produced by the shell, a device
/// automation or an NPC with an explicitly attached endpoint. The target is a
/// service instance for invoke/watch/publish and a subscription key for unwatch.
/// </summary>
public sealed record NetworkToolRequest(
    NetworkToolKind Kind,
    string Target,
    string Command = "",
    string Key = "",
    JsonElement Payload = default,
    string Projection = "full",
    DeliveryMode Mode = DeliveryMode.LatestValue,
    int MaxPending = 1,
    PacketTrafficClass Traffic = PacketTrafficClass.Interactive);

public sealed record NetworkToolResult(
    ServiceStatus Status,
    long Operation = 0,
    long Subscription = 0,
    long Message = 0,
    int Recipients = 0);

/// <summary>
/// Common gameplay action gateway. It never fabricates a principal, invokes DM
/// directly or marks an operation applied when the network merely accepted it.
/// Outstanding service submissions must be drained by the owning GameNetworkService.
/// </summary>
public sealed class NetworkInteractionTools(ServiceRuntime services)
{
    public NetworkToolResult Execute(NetworkToolActor actor, NetworkToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Kind) || !ServiceNames.IsValid(request.Target))
            return new(ServiceStatus.InvalidRequest);
        if (!services.IsAttached(actor.Principal))
            return new(ServiceStatus.UnknownEndpoint);

        switch (request.Kind)
        {
            case NetworkToolKind.Invoke:
            {
                if (!ServiceNames.IsValid(request.Command) || !ServiceNames.IsValid(request.Key) ||
                    request.Payload.ValueKind != JsonValueKind.Object)
                    return new(ServiceStatus.InvalidRequest);
                // The existing ServiceRuntime origin fence validates the resident shell
                // on admission and presentation; moving the presence cannot grant rights.
                var invocation = actor.ResidentProvider is { } host
                    ? actor.ResidentInstance is { Length: > 0 } instance && actor.ResidentNode is { } node
                        ? services.InvokeFrom(actor.Principal, host, instance, node,
                            request.Target, request.Command, request.Key, request.Payload, null, request.Traffic)
                        : new InvokeResult(ServiceStatus.InvalidRequest, null)
                    : services.Invoke(actor.Principal, request.Target, request.Command, request.Key,
                        request.Payload, null, request.Traffic);
                return new(invocation.Status, Operation: invocation.Operation?.OperationId ?? 0);
            }
            case NetworkToolKind.Watch:
            {
                if (!ServiceNames.IsValid(request.Key) || !ServiceNames.IsValid(request.Projection))
                    return new(ServiceStatus.InvalidRequest);
                // Lookup is through the caller's discover policy, never an unrestricted
                // service registry. Subscribe independently checks subscribe policy.
                var descriptor = services.Describe(actor.Principal, request.Target);
                if (descriptor is null)
                    return new(ServiceStatus.UnknownService);
                var result = services.Subscribe(actor.Principal, request.Key, request.Target,
                    descriptor.Schema, descriptor.Major, request.Projection, request.Mode, request.MaxPending);
                return new(result.Status, Subscription: result.SubscriptionId);
            }
            case NetworkToolKind.Unwatch:
                return new(services.Unsubscribe(actor.Principal, request.Target));
            case NetworkToolKind.Publish:
            {
                if (actor.Publisher is not { } publisher || publisher != actor.Principal)
                    return new(ServiceStatus.Forbidden);
                if (request.Payload.ValueKind != JsonValueKind.Object)
                    return new(ServiceStatus.InvalidRequest);
                var sequence = services.LastSequence(publisher, request.Target);
                if (sequence is null)
                    return new(ServiceStatus.UnknownService);
                if (sequence.Value == long.MaxValue)
                    return new(ServiceStatus.LimitExceeded);
                var published = services.Publish(publisher, request.Target,
                    sequence.Value + 1, request.Payload, null, request.Traffic);
                return new(published.Status, Message: published.MessageId, Recipients: published.Recipients);
            }
            default:
                return new(ServiceStatus.InvalidRequest);
        }
    }
}
