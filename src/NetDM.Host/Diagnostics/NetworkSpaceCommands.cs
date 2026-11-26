using System.Collections.Frozen;
using System.Text.Json;
using NetDM.Host.Transport;
using NetDM.Services;

namespace NetDM.Host.Diagnostics;

public static partial class NetworkSpace
{
    private readonly record struct CommandContext(GameNetworkService Game, GameNetworkService.Session Session,
        HostCommand Request, JsonElement Arguments, long Now);

    // Some commands stage an effect for the existing Host acknowledgement path.
    private delegate JsonElement SpaceCommand(CommandContext context, out Action? applied);
    private static readonly FrozenDictionary<string, SpaceCommand> Commands = CreateCommands();

    private static FrozenDictionary<string, SpaceCommand> CreateCommands()
    {
        var commands = new Dictionary<string, SpaceCommand>(StringComparer.Ordinal);
        RegisterSpaceCommands(commands);
        RegisterIntrusionCommands(commands);
        RegisterLifeCommands(commands);
        return commands.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static SpaceCommand Reply(Func<CommandContext, JsonElement> handler) =>
        (CommandContext context, out Action? applied) =>
        {
            applied = null;
            EnsureLifeSession(context.Session.Packets.ReceiptStreamId);
            return handler(context);
        };

    internal static bool TryAnswer(GameNetworkService game, GameNetworkService.Session session, HostCommand command,
        JsonElement arguments, out JsonElement reply, out Action? applied)
    {
        if (!Commands.TryGetValue(command.Command, out var handler))
        {
            reply = default;
            applied = null;
            return false;
        }
        reply = handler(new(game, session, command, arguments, Environment.TickCount64), out applied);
        return true;
    }
}
