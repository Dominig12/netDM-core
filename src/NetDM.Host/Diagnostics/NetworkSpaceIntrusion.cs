using System.Globalization;
using System.Text.Json;
using NetDM.Core;
using NetDM.Host.Transport;
using NetDM.Services;

namespace NetDM.Host.Diagnostics;

/// <summary>
/// P13.5 · Hacking, stolen access and anonymity.
///
/// The layer is deliberately small and opt-in, like the rest of the network space:
/// - <b>Hack</b>: an operator on a live presence starts a breach against a known wire node. The Host issues a
///   short code and a deadline; the operator must type it back through their window. A bot cannot solve it,
///   and a frightened player can still fumble it. A failed attempt raises suspicion and sets a cooldown.
/// - <b>Stolen access</b>: a successful breach stores a key for that node for a few minutes. The key is the
///   Host's authority: the window may ask to use it (for example, to switch an APC's breaker), and the world
///   performs the effect only after the Host says yes.
/// - <b>Anonymity</b>: the operator may mask their persona. Other operators see a nameless presence, the
///   activity feed and the chat show "скрытый оператор", while the Host still knows the owner.
///
/// Everything is bounded: one challenge per operator, a small key ring, explicit cooldowns. Nothing here
/// changes the authority model: rights still belong to the entry device and the service policies.
/// </summary>
public static partial class NetworkSpace
{
    private const int HackCodeLength = 6, HackWindowMs = 15_000, HackCooldownMs = 20_000;
    private const int HackFailedCooldownMs = 45_000, HackKeyTtlMs = 10 * 60_000, MaxHackKeys = 8;
    private const int MaskCooldownMs = 30_000;

    private sealed record HackChallenge(string Code, string Target, string Label, long ExpiresAt, long CooldownUntil);

    private sealed record StolenKey(string Node, string Label, string Service, long ExpiresAt);

    private static readonly Dictionary<EndpointKey, HackChallenge> HackChallenges = [];
    private static readonly Dictionary<EndpointKey, List<StolenKey>> StolenKeys = [];
    private static readonly Dictionary<EndpointKey, long> MaskCooldowns = [];

    private static JsonElement Refusal(string status) => JsonSerializer.SerializeToElement(new { status });

    private static void RegisterIntrusionCommands(Dictionary<string, SpaceCommand> commands)
    {
        commands.Add("hack_start", IntrusionReply(static (context, presence) =>
            StartHack(context.Session, presence, context.Arguments, context.Now)));
        commands.Add("hack_answer", IntrusionReply(static (context, presence) =>
            AnswerHack(presence, context.Arguments, context.Now)));
        commands.Add("hack_keys", IntrusionReply(static (context, presence) => JsonSerializer.SerializeToElement(new
        {
            status = "ok", keys = KeysText(presence.Owner, context.Now), masked = presence.Masked
        })));
        commands.Add("hack_use", IntrusionReply(static (context, presence) =>
            UseKey(presence, context.Arguments, context.Now)));
        commands.Add("mask", IntrusionReply(static (context, presence) =>
            ToggleMask(presence, context.Arguments, context.Now)));
    }

    private static SpaceCommand IntrusionReply(Func<CommandContext, Presence, JsonElement> handler) =>
        Reply(context => Controlled(context.Game, context.Session, context.Request, context.Now, out var refusal) is { } presence
            ? handler(context, presence) : Refusal(refusal!));

    private static JsonElement StartHack(GameNetworkService.Session session, Presence presence,
        JsonElement arguments, long now)
    {
        if (!arguments.TryGetProperty("node", out var nodeValue) ||
            nodeValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            return Refusal("invalid_request");
        var nodeText = nodeValue.ValueKind == JsonValueKind.String ? nodeValue.GetString()! : nodeValue.GetRawText();
        if (!ulong.TryParse(nodeText, out var nodeId))
            return Refusal("invalid_request");
        var node = new NodeId(nodeId);
        if (!session.Medium.Snapshot.Physical.Nodes.ContainsKey(node))
            return Refusal("unknown_node");
        // Only nodes the operator already knows can be attacked: the network does not leak the map.
        var knowledge = KnowledgeOf(presence.Owner, session.Packets.ReceiptStreamId, now);
        if (knowledge is null || !knowledge.Items.ContainsKey($"n|{node.Value.ToString(CultureInfo.InvariantCulture)}"))
            return Refusal("no_knowledge");
        if (HackChallenges.TryGetValue(presence.Owner, out var previous) && now < previous.CooldownUntil)
            return JsonSerializer.SerializeToElement(new { status = "cooldown", retryIn = (previous.CooldownUntil - now + 999) / 1000 });
        var code = RandomCode();
        var challenge = new HackChallenge(code, node.Value.ToString(CultureInfo.InvariantCulture), $"узел {node.Value}",
            now + HackWindowMs, now + HackCooldownMs);
        HackChallenges[presence.Owner] = challenge;
        return JsonSerializer.SerializeToElement(new
        {
            status = "challenge", code, target = challenge.Target, label = challenge.Label,
            expires = (now + HackWindowMs) / 1000
        });
    }

    private static JsonElement AnswerHack(Presence presence, JsonElement arguments, long now)
    {
        if (!arguments.TryGetProperty("code", out var codeValue) || codeValue.ValueKind != JsonValueKind.String)
            return Refusal("invalid_request");
        if (!HackChallenges.TryGetValue(presence.Owner, out var challenge))
            return Refusal("expired");
        if (now > challenge.ExpiresAt)
        {
            HackChallenges.Remove(presence.Owner);
            return Refusal("expired");
        }
        var answer = (codeValue.GetString() ?? "").Trim();
        if (!answer.Equals(challenge.Code, StringComparison.OrdinalIgnoreCase))
        {
            // A failed attempt is noisy: the cooldown is longer, and the event is named (or masked) in the feed.
            HackChallenges[presence.Owner] = challenge with { CooldownUntil = now + HackFailedCooldownMs };
            if (LifeEnabled)
                Record("hack_failed", new NodeId(ulong.Parse(challenge.Target, CultureInfo.InvariantCulture)),
                    OperatorName(presence), "неудачный взлом узла");
            return JsonSerializer.SerializeToElement(new { status = "failed", retryIn = HackFailedCooldownMs / 1000 });
        }
        HackChallenges.Remove(presence.Owner);
        var key = new StolenKey(challenge.Target, challenge.Label, "node", now + HackKeyTtlMs);
        var ring = StolenKeys.TryGetValue(presence.Owner, out var existing) ? existing : (StolenKeys[presence.Owner] = []);
        ring.RemoveAll(entry => entry.Node == key.Node);
        ring.Insert(0, key);
        while (ring.Count > MaxHackKeys)
            ring.RemoveAt(ring.Count - 1);
        if (LifeEnabled)
            Record("hack", new NodeId(ulong.Parse(key.Node, CultureInfo.InvariantCulture)), OperatorName(presence),
                "получил доступ к узлу");
        return JsonSerializer.SerializeToElement(new
        {
            status = "ok", node = key.Node, label = key.Label, expiresIn = HackKeyTtlMs / 1000,
            keys = KeysText(presence.Owner, now)
        });
    }

    private static JsonElement UseKey(Presence presence, JsonElement arguments, long now)
    {
        if (!arguments.TryGetProperty("node", out var nodeValue) || nodeValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            return Refusal("invalid_request");
        var nodeText = nodeValue.ValueKind == JsonValueKind.String ? nodeValue.GetString()! : nodeValue.GetRawText();
        if (!StolenKeys.TryGetValue(presence.Owner, out var ring))
            return Refusal("no_key");
        var key = ring.FirstOrDefault(entry => entry.Node == nodeText && now < entry.ExpiresAt);
        if (key is null)
        {
            ring.RemoveAll(entry => entry.Node == nodeText && now >= entry.ExpiresAt);
            return Refusal("no_key");
        }
        var purpose = arguments.TryGetProperty("purpose", out var purposeValue) && purposeValue.ValueKind == JsonValueKind.String
            ? purposeValue.GetString() : "node";
        if (LifeEnabled)
            Record("key_use", new NodeId(ulong.Parse(key.Node, CultureInfo.InvariantCulture)), OperatorName(presence),
                $"использовал доступ ({purpose})");
        return JsonSerializer.SerializeToElement(new { status = "ok", node = key.Node, label = key.Label, purpose });
    }

    private static JsonElement ToggleMask(Presence presence, JsonElement arguments, long now)
    {
        if (MaskCooldowns.TryGetValue(presence.Owner, out var next) && now < next)
            return JsonSerializer.SerializeToElement(new { status = "cooldown", retryIn = (next - now + 999) / 1000 });
        var wanted = !arguments.TryGetProperty("on", out var onValue) || onValue.ValueKind != JsonValueKind.False;
        presence.Masked = wanted;
        MaskCooldowns[presence.Owner] = now + MaskCooldownMs;
        return JsonSerializer.SerializeToElement(new { status = wanted ? "masked" : "unmasked", masked = presence.Masked });
    }

    /// <summary>The key ring as a text list: "node|label|expiresAt" joined by semicolons.</summary>
    private static string KeysText(EndpointKey owner, long now)
    {
        if (!StolenKeys.TryGetValue(owner, out var ring))
            return "";
        ring.RemoveAll(entry => now >= entry.ExpiresAt);
        return string.Join(';', ring.Select(entry => string.Join('|', entry.Node, Clean(entry.Label),
            ((entry.ExpiresAt - now + 999) / 1000).ToString(CultureInfo.InvariantCulture))));
    }

    private static string RandomCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> buffer = stackalloc char[HackCodeLength];
        for (var index = 0; index < buffer.Length; index++)
            buffer[index] = alphabet[Random.Shared.Next(alphabet.Length)];
        return new string(buffer);
    }
}
