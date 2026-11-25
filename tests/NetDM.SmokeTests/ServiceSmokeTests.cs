using System.Text.Json;
using NetDM.Core;
using NetDM.Services;

/// <summary>
/// P2 contract checks over a real shared medium and packet transport: schema
/// validation, fixed recipients, idempotent publication, generation fencing,
/// revocation before presentation, bounded inboxes and application ACK.
/// </summary>
static class ServiceSmokeTests
{
    private static readonly SchemaRegistry Schemas = SchemaRegistry.CreateDefault();

    public static void Run()
    {
        CheckSchemas();
        CheckContractModules();
        CheckDeliveryLifecycle();
        CheckLatestValueWithOutOfOrderDelivery();
        CheckAcknowledgedEventJournal();
        CheckBounds();
        CheckOutboxBackpressure();
        CheckCommands();
        CheckCameraOperationRetention();
        CheckPoliciesAndDiscovery();
        CheckTelemetrySnapshot();
        CheckHostProvidedCommands();
        CheckMessenger();
        CheckDirectoryQuery();
        Console.WriteLine("P2 service foundation, P4 telemetry snapshot, P7 messenger and P10 directory query checks passed.");
    }



    /// <summary>Event rules observe actual DM-ACKed packets only, with per-owner isolation.</summary>
    private static void CheckAcknowledgedEventJournal()
    {
        var stand = new Stand();
        var services = stand.Services;
        var sensor = new EndpointKey("automation.sensor", 1);
        var reader = new EndpointKey("automation.reader", 1);
        var stranger = new EndpointKey("automation.stranger", 1);
        Check(services.AttachEndpoint(sensor, new NodeId(1)) == ServiceStatus.Ok &&
            services.AttachEndpoint(reader, new NodeId(3)) == ServiceStatus.Ok &&
            services.AttachEndpoint(stranger, new NodeId(4), "other") == ServiceStatus.Ok,
            "attach event sender, subscriber and disconnected stranger");
        Check(services.RegisterService(sensor, "sensor.auto", "atmos.telemetry", "atmos.sample", 1, 0) ==
            ServiceStatus.Ok, "register automation sensor");
        Check(services.Subscribe(reader, "oxygen", "sensor.auto", "atmos.sample", 1, "panel.v1",
            DeliveryMode.LatestValue, 1).Status == ServiceStatus.Ok, "register automation subscription");

        services.SetEventReaders([reader]);
        var published = services.Publish(sensor, "sensor.auto", 1, Sample(150), 30, PacketTrafficClass.Background);
        Check(published.Status == ServiceStatus.Ok, "publish packet for event-driven shell");
        stand.Settle();
        var delivered = Inbox(stand, reader.Id, "event").Single();
        Check(services.EventRevisionOf(reader) == 0 &&
            services.AcknowledgedEvents(reader, 0, 8).Events.Count == 0, "transport delivery alone does not fire rules");
        Check(delivered.Payload!.Value.TryGetProperty("pressureKPa", out var pressure) &&
            pressure.GetDouble() == 150 && !delivered.Payload.Value.TryGetProperty("sampleTick", out _),
            "subscriber observes only the projection it requested");
        Check(services.Acknowledge(delivered.DeliveryId, true, null).Status == ServiceStatus.Ok,
            "DM ACKs the event after applying or capturing");
        var page = services.AcknowledgedEvents(reader, 0, 8);
        Check(page.Latest == 1 && page.Through == 1 && page.Events.Count == 1 &&
            page.Events[0].SubscriptionKey == "oxygen" &&
            page.Events[0].Payload.GetProperty("pressureKPa").GetDouble() == 150,
            "ACKed event is exposed once to exactly its receiving endpoint");
        Check(services.Acknowledge(delivered.DeliveryId, true, null).Status == ServiceStatus.Duplicate &&
            services.EventRevisionOf(reader) == 1, "repeat ACK never creates another event");
        Check(services.AcknowledgedEvents(stranger, 0, 8).Events.Count == 0,
            "different endpoints cannot read other subscriptions");
        Check(services.Publish(sensor, "sensor.auto", 2, Sample(170), 30,
            PacketTrafficClass.Background).Status == ServiceStatus.Ok, "second event offered");
        stand.Settle();
        var rejectedDelivery = Inbox(stand, reader.Id, "event").Single(item => item.MessageId != delivered.MessageId);
        Check(services.Acknowledge(rejectedDelivery.DeliveryId, false, "unsupported_schema").Status == ServiceStatus.Ok,
            "DM can reject applying a delivered event without suppressing the program's observation");
        var rejectedEvent = services.AcknowledgedEvents(reader, 1, 8).Events.Single();
        Check(!rejectedEvent.Applied && rejectedEvent.Reason == "unsupported_schema" &&
            rejectedEvent.Payload.GetProperty("pressureKPa").GetDouble() == 170,
            "event tools report gameplay rejection separately from successful network delivery");

        // This journal has its own strict memory and reader limits even under bursts.
        var journal = new NetworkEventJournal();
        journal.SetReaders([reader]);
        var bytes = System.Text.Encoding.UTF8.GetBytes("""{"pressureKPa":151.5,"alarm":true}""");
        journal.Record(stranger, 1, "spy", "sensor.auto", "atmos.sample", bytes, true, null);
        Check(journal.Read(stranger, 0, 16).Events.Count == 0, "unarmed endpoint cannot be observed");
        for (var i = 1; i <= NetworkEventJournal.MaxEventsPerReader + 9; i++)
            journal.Record(reader, i, "oxygen", "sensor.auto", "atmos.sample", bytes, true, null);
        var firstPage = journal.Read(reader, 0, 16);
        Check(firstPage.Overflowed && firstPage.Events.Count == 16 &&
            firstPage.Events[0].Payload.GetProperty("alarm").GetBoolean() &&
            firstPage.Through < firstPage.Latest, "overflow is explicit, projected fields remain typed");
        var rest = journal.Read(reader, firstPage.Through, 16);
        Check(rest.Events.Count == 16 && rest.Through == rest.Latest,
            "bounded event batches advance an explicit cursor");
        journal.SetReaders([]);
        Check(journal.Revision(reader) == 0 && journal.Read(reader, 0, 16).Events.Count == 0,
            "removing active program listeners forgets the journal");
        Check(services.DetachEndpoint(reader) == ServiceStatus.Ok, "detach old subscriber");
        Check(services.AcknowledgedEvents(reader, 0, 8).Events.Count == 0,
            "detached endpoint cannot read stale events");
    }

    /// <summary>
    /// A new game feature should be able to introduce a schema and a command without
    /// modifying the built-in SchemaRegistry catalog. Duplicates fail at startup.
    /// </summary>
    private static void CheckContractModules()
    {
        var registry = SchemaRegistry.CreateDefault([new ExampleFeatureContracts()]);
        var schema = registry.Find("test.ping", 1);
        Check(schema is { Minor: 0 } && registry.FindCommand("test.ping_service", "ping") is not null,
            "a feature module registers its schema and command");
        var payload = Json("""{"message":"probe"}""");
        Check(registry.Validate(schema!, 0, payload, out var canonical) == ServiceStatus.Ok &&
              canonical!.Values["message"].GetString() == "probe",
            "feature contract validates a published payload");
        var duplicateRejected = false;
        try
        {
            _ = SchemaRegistry.CreateDefault([new ExampleFeatureContracts(), new ExampleFeatureContracts()]);
        }
        catch (ArgumentException)
        {
            duplicateRejected = true;
        }
        Check(duplicateRejected, "duplicate feature contracts must be rejected, not overwritten");
    }

    private sealed class ExampleFeatureContracts : IServiceContractModule
    {
        public void Register(SchemaRegistry registry)
        {
            registry.Register(new SchemaDefinition("test.ping", 1, 0,
                [new SchemaField("message", SchemaFieldType.Text, true, MaxLength: 32)],
                new Dictionary<string, IReadOnlyList<string>> { ["full"] = ["message"] }, MaxPayloadBytes: 128));
            registry.RegisterCommand("test.ping_service", "ping", "test.ping", 1, "test.ping", 1);
        }
    }

    private static void CheckCameraOperationRetention()
    {
        var stand = new Stand(new ServiceRuntimeOptions(MaxOperations: 1));
        var services = stand.Services;
        var console = new EndpointKey("camera-console", 1);
        var camera = new EndpointKey("camera-device", 1);
        Check(services.AttachEndpoint(console, new NodeId(1), "camera") == ServiceStatus.Ok &&
            services.AttachEndpoint(camera, new NodeId(3), "camera") == ServiceStatus.Ok, "attach the camera system");
        Check(services.RegisterService(camera, "camera.front", "camera.access", "camera.access", 1, 0) == ServiceStatus.Ok,
            "register camera access");
        InvokeResult Acquire(string key) => services.Invoke(console, "camera.front", "acquire", key,
            Json("""{"request":"feed"}"""), 35, PacketTrafficClass.Interactive);
        var initial = Acquire("feed-1");
        Check(initial.Status == ServiceStatus.Ok, "request the feed");
        stand.Settle();
        var request = Inbox(stand, "camera-device", "command").Single();
        Check(services.Acknowledge(request.DeliveryId, true, null, Json("""{"request":"feed","state":"ready"}""")).Status == ServiceStatus.Ok,
            "confirm the feed");
        stand.Settle();
        var result = Inbox(stand, "camera-console", "result").Single();
        Check(services.Acknowledge(result.DeliveryId, true, null, null).Status == ServiceStatus.Ok, "acknowledge the confirmation");
        Check(Acquire("feed-1").Status == ServiceStatus.Duplicate && Acquire("feed-2").Status == ServiceStatus.LimitExceeded,
            "the completed operation keeps its idempotency key for retries");
        stand.Advance(1_200);
        Check(Acquire("feed-2").Status == ServiceStatus.Ok && services.Operation(console, initial.Operation!.OperationId).Status == ServiceStatus.Unknown,
            "an old camera operation leaves room for a new lease");
    }

    /// <summary>
    /// P7: an addressed PDA message is a command to the recipient's messenger. The same message ID under
    /// the same key is never delivered twice; the receipt is the result leg; an unreachable PDA gets nothing.
    /// </summary>
    private static void CheckMessenger()
    {
        var stand = new Stand(new ServiceRuntimeOptions());
        var services = stand.Services;
        var alice = new EndpointKey("pda-a", 1);
        var bob = new EndpointKey("pda-b", 1);
        var lost = new EndpointKey("pda-c", 1);
        Check(services.AttachEndpoint(alice, new NodeId(1)) == ServiceStatus.Ok && services.AttachEndpoint(bob, new NodeId(3)) == ServiceStatus.Ok &&
            services.AttachEndpoint(lost, new NodeId(4)) == ServiceStatus.Ok, "attach PDAs");
        foreach (var (pda, instance) in new[] { (bob, "pda.b"), (lost, "pda.c") })
            Check(services.RegisterService(pda, instance, "pda.messenger", "pda.message", 1, 0) == ServiceStatus.Ok, "register messenger");
        var message = Json("""{"id":"m-1","text":"hello","sender":"Alice (Scientist)","photo":false}""");
        Check(services.Invoke(alice, "pda.b", "deliver", "m-1.b", Json("""{"id":"m-1","sender":"x"}"""), 30,
            PacketTrafficClass.Interactive).Status == ServiceStatus.InvalidPayload, "a message needs its text");
        var sent = services.Invoke(alice, "pda.b", "deliver", "m-1.b", message, 30, PacketTrafficClass.Interactive);
        Check(sent.Status == ServiceStatus.Ok, "message accepted");
        Check(services.Invoke(alice, "pda.b", "deliver", "m-1.b", message, 30, PacketTrafficClass.Interactive).Status ==
            ServiceStatus.Duplicate, "a retried message is the same operation");
        stand.Settle();
        var delivered = Inbox(stand, "pda-b", "command");
        Check(delivered.Length == 1 && delivered[0].Payload!.Value.GetProperty("text").GetString() == "hello", "one delivery reaches the PDA");
        Check(services.Acknowledge(delivered[0].DeliveryId, true, null, Json("""{"id":"m-1","state":"shown"}""")).Status == ServiceStatus.Ok,
            "the PDA shows it");
        stand.Settle();
        var receipt = Inbox(stand, "pda-a", "result").Single();
        Check(receipt.Payload!.Value.GetProperty("state").GetString() == "shown", "the receipt travels back to the sender");
        var unreachable = services.Invoke(alice, "pda.c", "deliver", "m-2.c", message, 30, PacketTrafficClass.Interactive);
        stand.Settle();
        Check(services.Operation(alice, unreachable.Operation!.OperationId).Operation!.Outcome == CommandOutcome.NotApplied &&
            Inbox(stand, "pda-c", "command").Length == 0, "an unreachable PDA receives nothing");
        CheckMessengerRelay();
    }

    /// <summary>
    /// P7 authoritative: the sender's PDA hands the message to a message server, the server delivers it to
    /// each recipient and reports each outcome back. A server that is off rejects the relay; a recipient
    /// out of the network gets nothing, and the server learns that only from its own operation.
    /// </summary>
    private static void CheckMessengerRelay()
    {
        var stand = new Stand(new ServiceRuntimeOptions());
        var services = stand.Services;
        var alice = new EndpointKey("pda-a", 1);
        var server = new EndpointKey("msgsrv-1", 1);
        var bob = new EndpointKey("pda-b", 1);
        var lost = new EndpointKey("pda-c", 1);
        Check(services.AttachEndpoint(alice, new NodeId(1)) == ServiceStatus.Ok && services.AttachEndpoint(server, new NodeId(2)) == ServiceStatus.Ok &&
            services.AttachEndpoint(bob, new NodeId(3)) == ServiceStatus.Ok && services.AttachEndpoint(lost, new NodeId(4)) == ServiceStatus.Ok,
            "attach PDAs and the server");
        Check(services.RegisterService(server, "msgsrv.1", "pda.server", "pda.relay", 1, 0) == ServiceStatus.Ok, "register the server");
        foreach (var (pda, instance) in new[] { (alice, "pda.a"), (bob, "pda.b"), (lost, "pda.c") })
            Check(services.RegisterService(pda, instance, "pda.messenger", "pda.message", 1, 0) == ServiceStatus.Ok, "register messenger");
        Check(services.Invoke(alice, "msgsrv.1", "relay", "m-1", Json("""{"id":"m-1","text":"hi","sender":"Alice"}"""), 30,
            PacketTrafficClass.Interactive).Status == ServiceStatus.InvalidPayload, "a relay names its recipients");
        Check(services.Invoke(alice, "msgsrv.1", "relay", "m-1", Json("""{"id":"m-1","text":"hi","sender":"Alice","to":"pda.b,pda.c"}"""),
            30, PacketTrafficClass.Interactive).Status == ServiceStatus.Ok, "relay accepted");
        stand.Settle();
        var relay = Inbox(stand, "msgsrv-1", "command").Single();
        Check(relay.Producer == "pda-a" && relay.Payload!.Value.GetProperty("to").GetString() == "pda.b,pda.c",
            "the server sees who sent the relay");
        Check(services.Acknowledge(relay.DeliveryId, true, null, Json("""{"id":"m-1","state":"queued","recipients":2}""")).Status ==
            ServiceStatus.Ok, "the server queues the message");
        var toBob = services.Invoke(server, "pda.b", "deliver", "m-1.b",
            Json("""{"id":"m-1","text":"hi","sender":"Alice","from":"pda.a"}"""), 300, PacketTrafficClass.Interactive);
        var toLost = services.Invoke(server, "pda.c", "deliver", "m-1.c",
            Json("""{"id":"m-1","text":"hi","sender":"Alice","from":"pda.a"}"""), 30, PacketTrafficClass.Interactive);
        Check(toBob.Status == ServiceStatus.Ok && toLost.Status == ServiceStatus.Ok, "the server delivers to each recipient");
        stand.Settle();
        Check(Inbox(stand, "pda-a", "result").Single().Payload!.Value.GetProperty("state").GetString() == "queued",
            "the sender learns the server took it");
        var atBob = Inbox(stand, "pda-b", "command").Single();
        Check(atBob.Producer == "msgsrv-1" && atBob.Payload!.Value.GetProperty("from").GetString() == "pda.a",
            "the recipient gets it from the server, with the sender's instance");
        Check(services.Acknowledge(atBob.DeliveryId, true, null, Json("""{"id":"m-1","state":"shown"}""")).Status == ServiceStatus.Ok,
            "the recipient shows it");
        stand.Settle();
        Check(Inbox(stand, "msgsrv-1", "result").Single().Payload!.Value.GetProperty("state").GetString() == "shown",
            "the receipt reaches the server");
        Check(services.Operation(server, toLost.Operation!.OperationId).Operation!.Outcome == CommandOutcome.NotApplied &&
            Inbox(stand, "pda-c", "command").Length == 0, "the server sees its delivery to an unreachable PDA fail");
        Check(services.Invoke(server, "pda.a", "status", "m-1.c.s",
            Json("""{"id":"m-1","recipient":"pda.c","state":"unreachable","reason":"no_route"}"""), 30,
            PacketTrafficClass.Interactive).Status == ServiceStatus.Ok, "the server reports the failed recipient");
        stand.Settle();
        Check(Inbox(stand, "pda-a", "command").Single().Payload!.Value.GetProperty("state").GetString() == "unreachable",
            "the sender learns which recipient was not reached");

        // A server that is off refuses: the sender gets a rejection, not a silent loss.
        Check(services.Invoke(alice, "msgsrv.1", "relay", "m-2", Json("""{"id":"m-2","text":"hi","sender":"Alice","to":"pda.b"}"""),
            30, PacketTrafficClass.Interactive).Status == ServiceStatus.Ok, "second relay accepted");
        stand.Settle();
        var refused = Inbox(stand, "msgsrv-1", "command").Single(item => item.Payload!.Value.GetProperty("id").GetString() == "m-2");
        Check(services.Acknowledge(refused.DeliveryId, false, "offline", null).Status == ServiceStatus.Ok, "the server is off");
        stand.Settle();
        var rejection = Inbox(stand, "pda-a", "result").Single(item => item.OperationId != 0 && item.Outcome == "rejected_by_world");
        Check(rejection.Reason == "offline", "the sender learns the server is off");
        CheckRadioUplink();
        CheckDoorControl();
    }

    /// <summary>
    /// P9: a button sets the state it wants on each door it controls. The door it reaches answers its
    /// state; a door out of reach gets nothing; a toggle is not a valid request.
    /// </summary>
    private static void CheckDoorControl()
    {
        var stand = new Stand(new ServiceRuntimeOptions());
        var services = stand.Services;
        var button = new EndpointKey("button-1", 1);
        var door = new EndpointKey("door-1", 1);
        var far = new EndpointKey("door-2", 1);
        Check(services.AttachEndpoint(button, new NodeId(1)) == ServiceStatus.Ok && services.AttachEndpoint(door, new NodeId(3)) == ServiceStatus.Ok &&
            services.AttachEndpoint(far, new NodeId(4)) == ServiceStatus.Ok, "attach the button and the doors");
        foreach (var (key, instance) in new[] { (door, "door.1"), (far, "door.2") })
            Check(services.RegisterService(key, instance, "machine.door", "machine.door_set", 1, 0) == ServiceStatus.Ok, "register door");
        Check(services.Invoke(button, "door.1", "set_state", "p-1.1", Json("""{"press":"p-1","state":"toggle"}"""), 20,
            PacketTrafficClass.Interactive).Status == ServiceStatus.InvalidPayload, "a toggle is not a state");
        Check(services.Invoke(button, "door.1", "set_state", "p-1.1", Json("""{"press":"p-1","state":"open","bolted":false}"""), 20,
            PacketTrafficClass.Interactive).Status == ServiceStatus.Ok, "set the reachable door");
        var lost = services.Invoke(button, "door.2", "set_state", "p-1.2", Json("""{"press":"p-1","state":"open"}"""), 20,
            PacketTrafficClass.Interactive);
        stand.Settle();
        var arrived = Inbox(stand, "door-1", "command").Single();
        Check(arrived.Payload!.Value.GetProperty("state").GetString() == "open", "the door gets the wanted state");
        Check(services.Acknowledge(arrived.DeliveryId, true, null, Json("""{"press":"p-1","state":"open","bolted":false,"changed":true}""")).Status ==
            ServiceStatus.Ok, "the door applies it");
        stand.Settle();
        Check(Inbox(stand, "button-1", "result").Single().Payload!.Value.GetProperty("changed").GetBoolean(),
            "the button learns the door changed");
        Check(services.Operation(button, lost.Operation!.OperationId).Operation!.Outcome == CommandOutcome.NotApplied &&
            Inbox(stand, "door-2", "command").Length == 0, "a door out of reach gets nothing");

        var operatorEndpoint = new EndpointKey("door-ui-operator", 1);
        Check(services.AttachEndpoint(operatorEndpoint, new NodeId(1)) == ServiceStatus.Ok, "attach the door UI operator");
        Check(services.Invoke(operatorEndpoint, "door.1", "control", "ui-1", Json("""{"ticket":"t-1","action":"toggle"}"""), 20,
            PacketTrafficClass.Interactive).Status == ServiceStatus.InvalidPayload, "the door UI accepts only named state changes");
        var control = services.Invoke(operatorEndpoint, "door.1", "control", "ui-1",
            Json("""{"action":"bolt","ticket":"t-1"}"""), 20, PacketTrafficClass.Interactive);
        Check(control.Status == ServiceStatus.Ok, "an operator action shares the same registered door");
        stand.Settle();
        var command = Inbox(stand, "door-1", "command").Single(item => item.OperationId == control.Operation!.OperationId);
        Check(command.Schema == "machine.door_control_request" && command.Payload!.Value.GetRawText() == """{"ticket":"t-1","action":"bolt"}""",
            "the door receives the normalized ticket and action without a button press");
        Check(services.Acknowledge(command.DeliveryId, true, null, Json("""{"ticket":"t-1","accepted":1}""")).Status ==
            ServiceStatus.Ok, "the door confirms queuing the operator action");
        stand.Settle();
        var queued = Inbox(stand, operatorEndpoint.Id, "result").Single();
        Check(queued.Schema == "machine.door_control_accepted" && queued.Payload!.Value.GetProperty("accepted").GetBoolean(),
            "the operator receives a typed acceptance rather than the state-change result");
    }

    /// <summary>
    /// P8 uplink: a speaker's radio hands a transmission to a telecomms receiver over the network. Only
    /// the context ID and the frequency travel; a receiver out of reach gets nothing.
    /// </summary>
    private static void CheckRadioUplink()
    {
        var stand = new Stand(new ServiceRuntimeOptions());
        var services = stand.Services;
        var headset = new EndpointKey("radio-1", 1);
        var receiver = new EndpointKey("rx-1", 1);
        var far = new EndpointKey("rx-2", 1);
        Check(services.AttachEndpoint(headset, new NodeId(1)) == ServiceStatus.Ok && services.AttachEndpoint(receiver, new NodeId(3)) == ServiceStatus.Ok &&
            services.AttachEndpoint(far, new NodeId(4)) == ServiceStatus.Ok, "attach the radio and the receivers");
        foreach (var (rx, instance) in new[] { (receiver, "rx.1"), (far, "rx.2") })
            Check(services.RegisterService(rx, instance, "radio.receiver", "radio.uplink", 1, 0) == ServiceStatus.Ok, "register receiver");
        Check(services.Invoke(headset, "rx.1", "uplink", "r-1.1", Json("""{"id":"r-1","frequency":"text"}"""), 20,
            PacketTrafficClass.Interactive).Status == ServiceStatus.InvalidPayload, "the frequency is a number");
        Check(services.Invoke(headset, "rx.1", "uplink", "r-1.1", Json("""{"id":"r-1","frequency":1459}"""), 20,
            PacketTrafficClass.Interactive).Status == ServiceStatus.Ok, "uplink to the reachable receiver");
        var lost = services.Invoke(headset, "rx.2", "uplink", "r-1.2", Json("""{"id":"r-1","frequency":1459}"""), 20,
            PacketTrafficClass.Interactive);
        stand.Settle();
        var arrived = Inbox(stand, "rx-1", "command").Single();
        Check(arrived.Payload!.Value.GetProperty("frequency").GetInt32() == 1459, "the receiver gets the context and frequency");
        Check(services.Acknowledge(arrived.DeliveryId, true, null, Json("""{"id":"r-1","state":"relayed"}""")).Status == ServiceStatus.Ok,
            "the receiver relays it");
        stand.Settle();
        Check(Inbox(stand, "radio-1", "result").Single().Payload!.Value.GetProperty("state").GetString() == "relayed",
            "the radio learns the receiver took it");
        Check(services.Operation(headset, lost.Operation!.OperationId).Operation!.Outcome == CommandOutcome.NotApplied &&
            Inbox(stand, "rx-2", "command").Length == 0, "a receiver out of reach gets nothing");
    }

    /// <summary>
    /// P5: a research server's state lives in the Host. Its command still travels the route to the
    /// server's node; there the Host takes it instead of a DM inbox and answers with a result leg.
    /// </summary>
    private static void CheckHostProvidedCommands()
    {
        var stand = new Stand(new ServiceRuntimeOptions(HostProvidedTypes: ["research.server"]));
        var services = stand.Services;
        var console = new EndpointKey("rdconsole-1", 1);
        var server = new EndpointKey("rdserver-1", 1);
        var lost = new EndpointKey("rdserver-2", 1);
        Check(services.AttachEndpoint(console, new NodeId(1)) == ServiceStatus.Ok, "attach console");
        Check(services.AttachEndpoint(server, new NodeId(3)) == ServiceStatus.Ok, "attach server");
        Check(services.AttachEndpoint(lost, new NodeId(4)) == ServiceStatus.Ok, "attach unreachable server");
        Check(services.RegisterService(server, "research.srv1", "research.server", "research.status", 1, 0) == ServiceStatus.Ok,
            "register research server");
        Check(services.RegisterService(lost, "research.srv2", "research.server", "research.status", 1, 0) == ServiceStatus.Ok,
            "register unreachable research server");
        var request = Json("""{"node":"engineering","quote":300,"revision":1}""");
        Check(services.Invoke(console, "research.srv1", "research", "op-1", request, 30, PacketTrafficClass.Interactive).Status ==
            ServiceStatus.Ok, "purchase request accepted");
        Check(services.TakeHostCommands(8).Length == 0, "nothing reaches the server before its packet");
        stand.Settle();
        Check(Inbox(stand, "rdserver-1", "command").Length == 0, "DM never sees a host-provided command");
        var taken = services.TakeHostCommands(8);
        Check(taken is [{ Instance: "research.srv1", Command: "research", IdempotencyKey: "op-1", Initiator: "rdconsole-1" }] &&
            JsonDocument.Parse(taken[0].Arguments).RootElement.GetProperty("quote").GetInt32() == 300, "the Host takes the command");
        Check(services.TakeHostCommands(8).Length == 0, "a command is taken once");
        var answer = Json("""{"outcome":"researched","node":"engineering","revision":2,"price":300,"available":100}""");
        Check(services.Acknowledge(taken[0].DeliveryId, true, null, answer).Status == ServiceStatus.Ok, "the Host answers");
        stand.Settle();
        var result = Inbox(stand, "rdconsole-1", "result").Single();
        Check(result.Payload!.Value.GetProperty("outcome").GetString() == "researched" && result.Producer == "rdserver-1",
            "the answer travels back over the network");

        // A server the console cannot reach never receives the purchase.
        var unreachable = services.Invoke(console, "research.srv2", "research", "op-2", request, 30, PacketTrafficClass.Interactive);
        stand.Settle();
        Check(services.TakeHostCommands(8).Length == 0 &&
            services.Operation(console, unreachable.Operation!.OperationId).Operation!.Outcome == CommandOutcome.NotApplied,
            "no route, no purchase");
    }

    private static readonly JsonElement PowerOn = Json("""{"on":true}""");
    private static readonly JsonElement PowerOff = Json("""{"on":false}""");
    private static readonly JsonElement StateOn = Json("""{"on":true,"changed":true}""");

    private static InboxItem[] Inbox(Stand stand, string recipient, string kind) =>
        stand.Services.Poll(0, 64, 65_536).Items.Where(i => i.Recipient == recipient && i.Kind == kind).ToArray();

    private static void CheckCommands()
    {
        var stand = new Stand(new ServiceRuntimeOptions(MaxActiveOperationsPerEndpoint: 3));
        var services = stand.Services;
        var console = new EndpointKey("console-1", 1);
        var pump = new EndpointKey("pump-1", 1);
        Check(services.AttachEndpoint(console, new NodeId(1)) == ServiceStatus.Ok, "attach console");
        Check(services.AttachEndpoint(pump, new NodeId(3)) == ServiceStatus.Ok, "attach pump");
        Check(services.RegisterService(pump, "pump-1", "machine.control", "machine.power_state", 1, 0) == ServiceStatus.Ok,
            "register machine control");
        Check(services.Invoke(console, "pump-1", "self_destruct", "op-0", PowerOn, 30, PacketTrafficClass.Interactive).Status ==
            ServiceStatus.UnknownCommand, "only contract commands");
        Check(services.Invoke(console, "pump-1", "set_power", "op-0", Json("""{"on":"yes"}"""), 30,
            PacketTrafficClass.Interactive).Status == ServiceStatus.InvalidPayload, "typed arguments");

        // Request leg, provider application, then a separate result leg back.
        var invoked = services.Invoke(console, "pump-1", "set_power", "op-1", PowerOn, 30, PacketTrafficClass.Interactive);
        Check(invoked is { Status: ServiceStatus.Ok, Operation.Outcome: CommandOutcome.Pending }, "invoke accepted");
        var operationId = invoked.Operation!.OperationId;
        Check(Inbox(stand, "pump-1", "command").Length == 0, "accepted is not delivered");
        stand.Settle();
        var commands = Inbox(stand, "pump-1", "command");
        Check(commands.Length == 1, "command delivery: " + string.Join(", ",
            Enumerable.Range(1, 64).Select(id => services.Delivery(id)).Where(d => d is not null)
                .Select(d => $"{d!.DeliveryId}:{d.Kind}:{d.State}:{d.Reason}")));
        var command = commands[0];
        Check(command.Command == "set_power" && command.ServiceType == "machine.control" && command.Producer == "console-1" &&
            command.Payload!.Value.GetProperty("on").GetBoolean(), "command reaches the provider over the network");
        Check(services.Operation(console, operationId).Operation!.Result == ResultLeg.None, "no result before application");
        Check(services.Operation(pump, operationId).Status == ServiceStatus.Unknown, "only the initiator sees its operation");
        Check(services.Acknowledge(command.DeliveryId, true, null, StateOn).Status == ServiceStatus.Ok, "provider applies");
        var applied = services.Operation(console, operationId).Operation!;
        Check(applied is { Outcome: CommandOutcome.Applied, Result: ResultLeg.InTransit }, "result leg starts after application");
        Check(Inbox(stand, "console-1", "result").Length == 0, "result needs its own packet");
        stand.Settle();
        var result = Inbox(stand, "console-1", "result").Single();
        Check(result.Outcome == "applied" && result.Payload!.Value.GetProperty("changed").GetBoolean() &&
            result.Producer == "pump-1", "result reaches the initiator over the network");
        Check(services.Acknowledge(result.DeliveryId, true, null).Status == ServiceStatus.Ok, "initiator ACKs result");
        Check(services.Operation(console, operationId).Operation!.Result == ResultLeg.Delivered, "ResultDelivered");

        // The same business operation never runs twice.
        var repeated = services.Invoke(console, "pump-1", "set_power", "op-1", PowerOn, 30, PacketTrafficClass.Interactive);
        Check(repeated is { Status: ServiceStatus.Duplicate, Operation.Outcome: CommandOutcome.Applied } &&
            stand.Submit() == 0, "duplicate command is not sent again");
        Check(services.Invoke(console, "pump-1", "set_power", "op-1", PowerOff, 30, PacketTrafficClass.Interactive).Status ==
            ServiceStatus.IdempotencyConflict, "same key, other arguments");

        // The world may refuse: the refusal is a result too, without a payload.
        var refused = services.Invoke(console, "pump-1", "set_power", "op-2", PowerOff, 30, PacketTrafficClass.Interactive);
        stand.Settle();
        Check(services.Acknowledge(Inbox(stand, "pump-1", "command").Single().DeliveryId, false, "interlock").Status ==
            ServiceStatus.Ok, "provider rejects");
        stand.Settle();
        var refusal = Inbox(stand, "console-1", "result").Single();
        Check(refusal.Outcome == "rejected_by_world" && refusal.Reason == "interlock" && refusal.Payload is null,
            "rejection is reported back");
        services.Acknowledge(refusal.DeliveryId, true, null);
        Check(services.Operation(console, refused.Operation!.OperationId).Operation is
            { Outcome: CommandOutcome.RejectedByWorld, Result: ResultLeg.Delivered }, "rejection delivered");

        // A lost result leg is resent from the recorded outcome; the command is not re-applied.
        var lossy = services.Invoke(console, "pump-1", "set_power", "op-3", PowerOn, 30, PacketTrafficClass.Interactive);
        stand.Settle();
        services.Acknowledge(Inbox(stand, "pump-1", "command").Single().DeliveryId, true, null, StateOn);
        stand.Submit();
        stand.Tick += 60;
        stand.Advance(2);
        Check(services.Operation(console, lossy.Operation!.OperationId).Operation is
            { Outcome: CommandOutcome.Applied, Result: ResultLeg.Failed }, "result leg expired");
        var retried = services.Invoke(console, "pump-1", "set_power", "op-3", PowerOn, 30, PacketTrafficClass.Interactive);
        Check(retried.Status == ServiceStatus.Duplicate && retried.Operation!.ResultAttempts == 2, "retry resends the result");
        stand.Settle();
        Check(Inbox(stand, "pump-1", "command").Length == 0 && Inbox(stand, "console-1", "result").Length == 1,
            "provider is not asked again");
        services.Acknowledge(Inbox(stand, "console-1", "result").Single().DeliveryId, true, null);

        // Applied without a usable result: the effect stands and is never redone.
        var unreported = services.Invoke(console, "pump-1", "set_power", "op-4", PowerOn, 30, PacketTrafficClass.Interactive);
        stand.Settle();
        services.Acknowledge(Inbox(stand, "pump-1", "command").Single().DeliveryId, true, null, Json("""{"on":1}"""));
        Check(services.Operation(console, unreported.Operation!.OperationId).Operation is
            { Outcome: CommandOutcome.Applied, Result: ResultLeg.Failed, ResultReason: "invalid_result" }, "invalid result");

        // Provider destroyed after DM received the command: the outcome is Unknown.
        var unknown = services.Invoke(console, "pump-1", "set_power", "op-5", PowerOff, 30, PacketTrafficClass.Interactive);
        stand.Settle();
        Check(Inbox(stand, "pump-1", "command").Length == 1, "dispatched");
        Check(services.OnNodesRemoved([new NodeId(3)]) == 1, "pump destroyed");
        Check(services.Operation(console, unknown.Operation!.OperationId).Operation!.Outcome == CommandOutcome.Unknown,
            "may have been applied: Unknown");

        // Provider gone before the request arrives: certainly not applied.
        var pumpNext = pump with { Generation = 2 };
        Check(services.AttachEndpoint(pumpNext, new NodeId(2)) == ServiceStatus.Ok, "new pump");
        Check(services.RegisterService(pumpNext, "pump-1", "machine.control", "machine.power_state", 1, 0) == ServiceStatus.Ok,
            "new pump registers the instance again");
        var lost = services.Invoke(console, "pump-1", "set_power", "op-6", PowerOn, 30, PacketTrafficClass.Interactive);
        Check(services.DetachEndpoint(pumpNext) == ServiceStatus.Ok, "pump detached");
        stand.Settle();
        Check(services.Operation(console, lost.Operation!.OperationId).Operation is
            { Outcome: CommandOutcome.NotApplied, OutcomeReason: "detached" }, "never reached DM: NotApplied");

        // Bounded in-flight commands per initiator.
        var pumpThird = pump with { Generation = 3 };
        services.AttachEndpoint(pumpThird, new NodeId(2));
        services.RegisterService(pumpThird, "pump-1", "machine.control", "machine.power_state", 1, 0);
        for (var i = 0; i < 3; i++)
            Check(services.Invoke(console, "pump-1", "set_power", $"busy-{i}", PowerOn, 30, PacketTrafficClass.Interactive).Status ==
                ServiceStatus.Ok, "within quota");
        Check(services.Invoke(console, "pump-1", "set_power", "busy-3", PowerOn, 30, PacketTrafficClass.Interactive).Status ==
            ServiceStatus.Busy, "initiator quota");
    }

    /// <summary>P4: a display asks the sensor for its latest sample; both legs cross the network.</summary>
    private static void CheckTelemetrySnapshot()
    {
        var stand = new Stand(new ServiceRuntimeOptions());
        var services = stand.Services;
        var display = new EndpointKey("display-1", 1);
        var sensor = new EndpointKey("sensor-1", 1);
        Check(services.AttachEndpoint(display, new NodeId(1)) == ServiceStatus.Ok, "attach display");
        Check(services.AttachEndpoint(sensor, new NodeId(3)) == ServiceStatus.Ok, "attach sensor");
        Check(services.RegisterService(sensor, "atmos.o2_sensor", "atmos.telemetry", "atmos.sample", 1, 0) == ServiceStatus.Ok,
            "register sensor");
        Check(services.Invoke(display, "atmos.o2_sensor", "sample", "snap-0", Json("""{"reason":"later"}"""), 30,
            PacketTrafficClass.Interactive).Status == ServiceStatus.InvalidPayload, "request reason is an enum");
        var invoked = services.Invoke(display, "atmos.o2_sensor", "sample", "snap-1", Json("""{"reason":"subscribe"}"""), 30,
            PacketTrafficClass.Interactive);
        Check(invoked.Status == ServiceStatus.Ok, "snapshot request accepted");
        stand.Settle();
        var request = Inbox(stand, "sensor-1", "command").Single();
        Check(request.Command == "sample" && request.Payload!.Value.GetProperty("reason").GetString() == "subscribe",
            "the request reaches the sensor");
        Check(services.Acknowledge(request.DeliveryId, true, null, Json("""{"pressureKPa":101.3}""")).Status ==
            ServiceStatus.Ok, "an invalid result is still an application");
        var operation = services.Operation(display, invoked.Operation!.OperationId).Operation!;
        Check(operation is { Outcome: CommandOutcome.Applied, Result: ResultLeg.Failed }, "a partial sample is not delivered");
        var second = services.Invoke(display, "atmos.o2_sensor", "sample", "snap-2", Json("""{"reason":"refresh"}"""), 30,
            PacketTrafficClass.Interactive);
        stand.Settle();
        var next = Inbox(stand, "sensor-1", "command").Single(item => item.OperationId == second.Operation!.OperationId);
        var sample = Json("""{"pressureKPa":101.3,"temperatureK":293.15,"sampleTick":40,"quality":"current"}""");
        Check(services.Acknowledge(next.DeliveryId, true, null, sample).Status == ServiceStatus.Ok, "sensor answers");
        stand.Settle();
        var result = Inbox(stand, "display-1", "result").Single(item => item.OperationId == second.Operation!.OperationId);
        Check(result.Outcome == "applied" && result.Schema == "atmos.sample" &&
            result.Payload!.Value.GetProperty("sampleTick").GetInt64() == 40, "the snapshot returns to the display");
    }

    /// <summary>
    /// P10: a directory query sees what its asker may discover, in a stable order and in bounded slices.
    /// A hidden service is absent, never refused; the revision moves with every service that comes or goes.
    /// </summary>
    private static void CheckDirectoryQuery()
    {
        var stand = new Stand();
        var services = stand.Services;
        var console = new EndpointKey("console-1", 1);
        var rogue = new EndpointKey("rogue-1", 1);
        var pump = new EndpointKey("pump-1", 1);
        services.AttachEndpoint(console, new NodeId(1));
        services.AttachEndpoint(rogue, new NodeId(1), "syndicate");
        services.AttachEndpoint(pump, new NodeId(3));
        Check(services.QueryDirectory("console-1", null, null, 8) is { Entries.Count: 0, More: false }, "an empty directory answers nothing");
        var empty = services.QueryDirectory("console-1", null, null, 8)!.Revision;
        foreach (var name in new[] { "pump-c", "pump-a", "pump-b" })
            services.RegisterService(pump, name, "machine.control", "machine.power_state", 1, 0);
        services.RegisterService(pump, "open-door", "machine.door", "machine.door_state", 1, 0, new ServicePolicy(Discover: AccessScope.Open));
        var all = services.QueryDirectory("console-1", null, null, 8)!;
        Check(all.Entries.Select(entry => entry.Instance).SequenceEqual(["open-door", "pump-a", "pump-b", "pump-c"]) && !all.More &&
            all.Entries.All(entry => entry.Node == new NodeId(3)) && all.Revision == empty + 4, "the directory is listed in instance order with its providers");
        Check(services.QueryDirectory("console-1", "machine.control", null, 8)!.Entries.Count == 3 &&
            services.QueryDirectory("console-1", "no.such", null, 8)!.Entries.Count == 0, "a type filter is exact");
        Check(services.QueryDirectory("rogue-1", null, null, 8)!.Entries.Select(entry => entry.Instance).SequenceEqual(["open-door"]),
            "another domain sees only what is open to it");
        var first = services.QueryDirectory("console-1", "machine.control", null, 2)!;
        var rest = services.QueryDirectory("console-1", "machine.control", first.Entries[^1].Instance, 2)!;
        Check(first is { Entries.Count: 2, More: true } && rest.Entries.Single().Instance == "pump-c" && !rest.More &&
            rest.Revision == first.Revision, "a cursor continues after the last entry at the same revision");
        var pumps = services.QueryDirectory(console, null, null, 8, "pump-")!;
        var afterA = services.QueryDirectory(console, "machine.control", "pump-a", 8, "pump-")!;
        Check(pumps.Entries.Select(entry => entry.Instance).SequenceEqual(["pump-a", "pump-b", "pump-c"]) && !pumps.More &&
            afterA.Entries.Select(entry => entry.Instance).SequenceEqual(["pump-b", "pump-c"]) &&
            services.QueryDirectory(console, null, null, 8, "pump-c")!.Entries.Single().Instance == "pump-c" &&
            services.QueryDirectory(console, null, null, 8, "zzz")!.Entries.Count == 0 &&
            services.QueryDirectory(rogue, null, null, 8, "pump")!.Entries.Count == 0, "a prefix narrows the index ranges, within what the asker may discover");
        Check(services.QueryDirectory("nobody", null, null, 8) is null && services.QueryDirectory("console-1", null, null, 0) is null,
            "an unknown asker and an empty budget get no slice");
        services.DetachEndpoint(pump);
        var gone = services.QueryDirectory("console-1", null, null, 8)!;
        Check(gone.Entries.Count == 0 && gone.Revision == all.Revision + 4, "services that left move the revision");

        // Hidden and wrong-type entries cannot consume the scan budget or create an empty continuation.
        for (var i = 0; i < 80; i++)
        {
            var endpoint = new EndpointKey($"bulk-{i:D2}", 1);
            services.AttachEndpoint(endpoint, new NodeId(3), "syndicate");
            services.RegisterService(endpoint, $"hidden-{i:D2}", "machine.control", "machine.power_state", 1, 0);
            services.RegisterService(endpoint, $"open-{i:D2}", "machine.door", "machine.door_state", 1, 0,
                new ServicePolicy(Discover: AccessScope.Open));
        }
        services.RegisterService(console, "own-pump", "machine.control", "machine.power_state", 1, 0,
            new ServicePolicy(Discover: AccessScope.None));
        var only = services.QueryDirectory(console, "machine.control", null, 1)!;
        Check(only.Entries.Single().Instance == "own-pump" && !only.More, "the budget covers only discoverable entries of the requested type");
        var visible = services.QueryDirectory(console, "machine.door", null, 64)!;
        var next = services.QueryDirectory(console, "machine.door", visible.Entries[^1].Instance, 64)!;
        Check(visible.Entries.Count == 64 && visible.More && next.Entries.Count == 16 && !next.More &&
            next.Entries[0].Instance == "open-64", "the bounded range advances without gaps or duplicates");
        Check(services.QueryNearbyDirectory(console, [new NodeId(1), new NodeId(3)], 2) is { More: true } nearby &&
            nearby.Entries[0].Instance == "own-pump" && nearby.Entries[1].Instance.StartsWith("open-", StringComparison.Ordinal) &&
            services.QueryNearbyDirectory(console, [new NodeId(2)], 2) is { Entries.Count: 0, More: false },
            "nearby discovery starts with services at the closest scanned node and respects the requester's visibility");
        var moved = new EndpointKey("bulk-00", 1);
        services.AttachEndpoint(moved, new NodeId(2), "station");
        var changed = services.QueryDirectory(console, "machine.control", null, 8)!;
        Check(changed.Revision > only.Revision && changed.Entries.Select(entry => entry.Instance).SequenceEqual(["hidden-00", "own-pump"]) &&
            changed.Entries[0].Node == new NodeId(2), "a same-generation domain change reindexes its services and moves the revision");
    }

    private static void CheckPoliciesAndDiscovery()
    {
        var stand = new Stand();
        var services = stand.Services;
        var console = new EndpointKey("console-1", 1);
        var rogue = new EndpointKey("rogue-1", 1);
        var pump = new EndpointKey("pump-1", 1);
        var far = new EndpointKey("far-1", 1);
        services.AttachEndpoint(console, new NodeId(1));
        services.AttachEndpoint(rogue, new NodeId(1), "syndicate");
        services.AttachEndpoint(pump, new NodeId(3));
        services.AttachEndpoint(far, new NodeId(4));
        Check(services.RegisterService(pump, "pump-1", "machine.control", "machine.power_state", 1, 0) == ServiceStatus.Ok, "domain");
        Check(services.RegisterService(pump, "pump-2", "machine.control", "machine.power_state", 1, 0,
            new ServicePolicy(Discover: AccessScope.Open)) == ServiceStatus.Ok, "visible to all, invokable in domain");
        Check(services.RegisterService(pump, "pump-3", "machine.control", "machine.power_state", 1, 0,
            new ServicePolicy(Invoke: AccessScope.None)) == ServiceStatus.Ok, "owner only");
        Check(services.RegisterService(far, "far-pump", "machine.control", "machine.power_state", 1, 0) == ServiceStatus.Ok, "far");
        Check(services.RegisterService(pump, "pump-1", "machine.control", "machine.power_state", 1, 0,
            new ServicePolicy(Invoke: AccessScope.Open)) == ServiceStatus.Conflict, "policy is part of the registration");

        Check(services.Invoke(rogue, "pump-1", "set_power", "x", PowerOn, 30, PacketTrafficClass.Interactive).Status ==
            ServiceStatus.UnknownService, "a hidden service looks missing");
        Check(services.Subscribe(rogue, "spy", "pump-1", "machine.power_state", 1, "full", DeliveryMode.LatestValue, 1).Status ==
            ServiceStatus.UnknownService, "hidden from subscribe");
        Check(services.Invoke(rogue, "pump-2", "set_power", "x", PowerOn, 30, PacketTrafficClass.Interactive).Status ==
            ServiceStatus.Forbidden, "visible but not invokable");
        Check(services.Invoke(console, "pump-3", "set_power", "x", PowerOn, 30, PacketTrafficClass.Interactive).Status ==
            ServiceStatus.Forbidden, "invoke disabled for others");
        Check(services.Invoke(console, "pump-1", "directory.describe", "x", PowerOn, 30, PacketTrafficClass.Interactive).Status ==
            ServiceStatus.InvalidRequest, "describe is internal");

        // Revocation before presentation: a subscriber that leaves the domain stops receiving.
        Check(services.Subscribe(console, "state", "pump-1", "machine.power_state", 1, "full", DeliveryMode.LatestValue, 1).Status ==
            ServiceStatus.Ok, "console subscribes");
        var published = services.Publish(pump, "pump-1", 1, StateOn, 30, PacketTrafficClass.Interactive);
        stand.Submit();
        Check(services.AttachEndpoint(console, new NodeId(1), "syndicate") == ServiceStatus.Ok, "console changes domain");
        stand.Advance(10);
        Check(services.Delivery(published.Deliveries[0]) is { State: DeliveryState.Cancelled, Reason: "access_revoked" },
            "revoked access wins before presentation");
        Check(services.AttachEndpoint(console, new NodeId(1)) == ServiceStatus.Ok, "console back in the station domain");

        var queuedEvent = services.Publish(pump, "pump-1", 2, StateOn, 30, PacketTrafficClass.Interactive);
        var queuedCommand = services.Invoke(console, "pump-1", "set_power", "revoked", PowerOn, 30,
            PacketTrafficClass.Interactive).Operation!;
        stand.Settle();
        Check(stand.State(queuedEvent.Deliveries.Single()) == DeliveryState.TransportDelivered &&
            services.Capture().InboxEntries == 2, "event and command await their first poll");
        Check(services.AttachEndpoint(console, new NodeId(1), "syndicate") == ServiceStatus.Ok, "access revoked in inbox");
        Check(services.Poll(0, 64, 32_768).Items.Length == 0 &&
            services.Delivery(queuedEvent.Deliveries.Single()) is { State: DeliveryState.Cancelled, Reason: "access_revoked" } &&
            services.Operation(console, queuedCommand.OperationId).Operation is
                { Outcome: CommandOutcome.NotApplied, OutcomeReason: "access_revoked" }, "revoked inbox entries never reach DM");
        Check(services.AttachEndpoint(console, new NodeId(1)) == ServiceStatus.Ok, "restore domain");

        var issuedCommand = services.Invoke(console, "pump-1", "set_power", "issued", PowerOn, 30,
            PacketTrafficClass.Interactive).Operation!;
        stand.Settle();
        var issued = Inbox(stand, "pump-1", "command").Single();
        Check(services.AttachEndpoint(console, new NodeId(1), "syndicate") == ServiceStatus.Ok, "access revoked after issue");
        Check(Inbox(stand, "pump-1", "command").Length == 0 &&
            services.Acknowledge(issued.DeliveryId, true, null, StateOn).Status == ServiceStatus.Ok &&
            services.Operation(console, issuedCommand.OperationId).Operation!.Outcome == CommandOutcome.Applied,
            "suppress replay but accept the outcome of an already issued command");
        stand.Settle();
        Check(Inbox(stand, "console-1", "result").Length == 0 &&
            services.Operation(console, issuedCommand.OperationId).Operation!.Result == ResultLeg.Failed,
            "revocation also prevents disclosure on the return leg");
        Check(services.AttachEndpoint(console, new NodeId(1)) == ServiceStatus.Ok, "restore domain after issue");

        // Discovery is a network round trip: only reachable, visible providers answer.
        var scan = services.Discover(console, "machine.control", "scan-1", PacketTrafficClass.Interactive);
        Check(scan.Status == ServiceStatus.Ok && scan.Operations.Length == 4, "probes to visible providers");
        Check(Inbox(stand, "console-1", "result").Length == 0, "no descriptor without a round trip");
        stand.Settle();
        var found = Inbox(stand, "console-1", "result").Select(r => r.Payload!.Value.GetProperty("instance").GetString()).OrderBy(n => n).ToArray();
        Check(found.SequenceEqual(new[] { "pump-1", "pump-2", "pump-3" }), "unreachable provider never appears");
        var describe = Inbox(stand, "console-1", "result")[0];
        Check(describe.Command == "directory.describe" && describe.Payload!.Value.GetProperty("commands").GetString() == "set_power",
            "descriptor lists contract commands");
        Check(Inbox(stand, "pump-1", "command").Length == 0, "the Host answers describe without DM");
        Check(services.Operation(console, scan.Operations.Single(id =>
            services.Operation(console, id).Operation!.Instance == "far-pump")).Operation!.Outcome == CommandOutcome.NotApplied,
            "no route to the far provider");
        Check(services.Discover(console, "machine.control", "scan-1", PacketTrafficClass.Interactive) is
            { Status: ServiceStatus.Duplicate } repeat && repeat.Operations.SequenceEqual(scan.Operations), "scan key is idempotent");
        var rogueScan = services.Discover(rogue, "machine.control", "scan-1", PacketTrafficClass.Interactive);
        Check(rogueScan.Operations.Length == 1, "another domain discovers only open services");
        Check(services.Discover(console, "atmos.telemetry", "scan-1", PacketTrafficClass.Interactive).Status ==
            ServiceStatus.IdempotencyConflict, "scan key identifies its service type");
        var newConsole = console with { Generation = 2 };
        Check(services.AttachEndpoint(newConsole, new NodeId(1)) == ServiceStatus.Ok &&
            services.Discover(newConsole, "machine.control", "scan-1", PacketTrafficClass.Interactive).Status ==
                ServiceStatus.Ok, "reused endpoint ID has a fresh scan key space");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Service check failed: " + message);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement Sample(double pressure, long tick = 10) => Json(
        $$"""{"pressureKPa":{{pressure.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"temperatureK":293.15,"sampleTick":{{tick}},"quality":"current"}""");

    private static void CheckSchemas()
    {
        var schema = Schemas.Find("atmos.sample", 1)!;
        Check(Schemas.Validate(schema, 0, Sample(101.3), out var a) == ServiceStatus.Ok, "valid sample");
        var reordered = Json("""{"quality":"current","sampleTick":10,"temperatureK":293.15,"pressureKPa":101.3}""");
        Check(Schemas.Validate(schema, 0, reordered, out var b) == ServiceStatus.Ok && a!.Digest == b!.Digest,
            "canonical digest ignores property order");
        foreach (var invalid in new[]
        {
            """{"pressureKPa":-1,"temperatureK":1,"sampleTick":1,"quality":"current"}""",
            """{"pressureKPa":"1","temperatureK":1,"sampleTick":1,"quality":"current"}""",
            """{"pressureKPa":1,"temperatureK":1,"sampleTick":1.5,"quality":"current"}""",
            """{"pressureKPa":1,"temperatureK":1,"sampleTick":1,"quality":"great"}""",
            """{"pressureKPa":1,"temperatureK":1,"sampleTick":1}""",
            """{"pressureKPa":1,"temperatureK":1,"sampleTick":1,"quality":"current","admin":true}""",
            """{"pressureKPa":1,"pressureKPa":2,"temperatureK":1,"sampleTick":1,"quality":"current"}""",
            """[1,2,3]"""
        })
            Check(Schemas.Validate(schema, 0, Json(invalid), out _) == ServiceStatus.InvalidPayload, "reject " + invalid);
        Check(Schemas.Validate(schema, 1, Sample(1), out _) == ServiceStatus.Ok,
            "minor 1 must accept the original payload without optional fields");
        var gasSample = Json("""{"pressureKPa":101.3,"temperatureK":293.15,"sampleTick":10,"quality":"current","gasesJson":"{}"}""");
        Check(Schemas.Validate(schema, 1, gasSample, out _) == ServiceStatus.Ok &&
              Schemas.Validate(schema, 0, gasSample, out _) == ServiceStatus.InvalidPayload,
            "new gas composition requires minor 1, not a hidden expansion of minor 0");
        Check(Schemas.Validate(schema, 2, Sample(1), out _) == ServiceStatus.InvalidPayload,
            "unknown producer minor must be rejected");
        var panel = JsonDocument.Parse(SchemaRegistry.Project(a!, "panel.v1")).RootElement;
        Check(!panel.TryGetProperty("sampleTick", out _) && panel.GetProperty("pressureKPa").GetDouble() == 101.3,
            "projection discloses only its fields");
    }

    private sealed class Stand
    {
        public long Tick = 1;
        public readonly SharedMediumNetwork Medium;
        public readonly RadioPacketTransport Packets;
        public readonly ServiceRuntime Services;

        public Stand(ServiceRuntimeOptions? options = null)
        {
            Medium = new SharedMediumNetwork(new NetworkRuntime(), searchMode: SharedRadioSearchMode.AStarDense);
            Medium.ApplyExternalTopology(
            [
                new TopologyChange(TopologyOperation.AddNode, new NodeId(1), Position: new NodePosition(0, 0, 1)),
                new TopologyChange(TopologyOperation.AddNode, new NodeId(2), Position: new NodePosition(4, 0, 1)),
                new TopologyChange(TopologyOperation.AddNode, new NodeId(3), Position: new NodePosition(8, 0, 1)),
                // Node 4 has no link and no radio: nothing can reach it.
                new TopologyChange(TopologyOperation.AddNode, new NodeId(4), Position: new NodePosition(90, 90, 1)),
                new TopologyChange(TopologyOperation.Connect, new NodeId(1), new NodeId(2)),
                new TopologyChange(TopologyOperation.Connect, new NodeId(2), new NodeId(3))
            ]);
            Services = new ServiceRuntime(Schemas, () => Tick, options);
            Packets = new RadioPacketTransport(Medium, new PacketTransportOptions(Capacity: 256), Services.OnTransportReceipt);
        }

        /// <summary>Routes every packet the runtime has queued, as the Host's drain does.</summary>
        public void Submit(PublishResult? _ = null) => Submit();

        public int Submit()
        {
            var submissions = Services.TakeSubmissions(1024);
            foreach (var submission in submissions)
                Submit(submission);
            return submissions.Length;
        }

        public void Submit(DeliverySubmission submission)
        {
            var route = Medium.Route(submission.From, submission.To);
            Services.OnSubmitted(submission.DeliveryId, Packets.Submit(submission.RequestId, route,
                submission.Bytes, Tick, submission.TrafficClass, submission.TimeoutTicks));
        }

        /// <summary>Routes, advances and repeats until no service packet is waiting.</summary>
        public void Settle(int ticks = 6)
        {
            for (var round = 0; round < 4; round++)
            {
                if (Submit() == 0 && round > 0)
                    return;
                Advance(ticks);
            }
        }

        public void Advance(int ticks)
        {
            for (var i = 0; i < ticks; i++)
                Packets.Advance(++Tick);
        }

        public DeliveryState State(long deliveryId) => Services.Delivery(deliveryId)!.State;
    }

    private static void CheckDeliveryLifecycle()
    {
        var stand = new Stand();
        var services = stand.Services;
        var sensor = new EndpointKey("sensor-1", 1);
        var display = new EndpointKey("display-1", 1);
        var panel = new EndpointKey("panel-1", 1);
        Check(services.AttachEndpoint(sensor, new NodeId(1)) == ServiceStatus.Ok, "attach sensor");
        Check(services.AttachEndpoint(display, new NodeId(3)) == ServiceStatus.Ok, "attach display");
        Check(services.AttachEndpoint(panel, new NodeId(2)) == ServiceStatus.Ok, "attach panel");
        Check(services.AttachEndpoint(display, new NodeId(3)) == ServiceStatus.Duplicate, "idempotent attach");
        Check(services.AttachEndpoint(display with { Generation = 0 }, new NodeId(3)) == ServiceStatus.InvalidRequest,
            "generation starts at one");

        Check(services.RegisterService(sensor, "atmos-1", "atmos.telemetry", "atmos.sample", 1, 0) == ServiceStatus.Ok, "register");
        Check(services.RegisterService(sensor, "atmos-1", "atmos.telemetry", "atmos.sample", 1, 0) == ServiceStatus.Duplicate, "register twice");
        Check(services.RegisterService(display, "atmos-1", "atmos.telemetry", "atmos.sample", 1, 0) == ServiceStatus.Forbidden,
            "an instance never changes owner");
        Check(services.RegisterService(sensor, "atmos-2", "atmos.telemetry", "atmos.sample", 2, 0) == ServiceStatus.UnsupportedSchema,
            "unknown major");

        Check(services.Subscribe(display, "main", "atmos-1", "atmos.sample", 2, "panel.v1", DeliveryMode.LatestValue, 1).Status ==
            ServiceStatus.UnsupportedSchema, "no shared major");
        Check(services.Subscribe(display, "main", "atmos-1", "atmos.sample", 1, "secret", DeliveryMode.LatestValue, 1).Status ==
            ServiceStatus.UnsupportedSchema, "no heuristic projection");
        Check(services.Subscribe(display with { Generation = 2 }, "main", "atmos-1", "atmos.sample", 1, "panel.v1",
            DeliveryMode.LatestValue, 1).Status == ServiceStatus.StaleGeneration, "generation is verified");
        var subscription = services.Subscribe(display, "main", "atmos-1", "atmos.sample", 1, "panel.v1", DeliveryMode.LatestValue, 1);
        Check(subscription.Status == ServiceStatus.Ok && subscription.Revision == 1, "subscribe");

        // Accepted is not delivered: nothing is presentable before the packet arrives.
        var first = services.Publish(sensor, "atmos-1", 1, Sample(101.3), 30, PacketTrafficClass.Interactive);
        Check(first.Status == ServiceStatus.Ok && first.Recipients == 1, "publish");
        var firstDelivery = first.Deliveries[0];
        Check(stand.State(firstDelivery) == DeliveryState.Accepted && services.Poll(0, 8, 32_768).Items.Length == 0,
            "acceptance is not transport delivery");
        Check(services.Acknowledge(firstDelivery, true, null).Status == ServiceStatus.InvalidRequest,
            "DM cannot apply an undelivered message");
        stand.Submit(first);
        stand.Advance(10);
        var page = services.Poll(0, 8, 32_768);
        Check(page.Items.Length == 1 && page.Items[0].DeliveryId == firstDelivery &&
            page.Items[0].RecipientGeneration == 1 && !page.Items[0].Payload!.Value.TryGetProperty("sampleTick", out _),
            "physical delivery presents the recipient projection");
        Check(stand.State(firstDelivery) == DeliveryState.TransportDelivered, "transport delivered");
        Check(services.Poll(0, 8, 32_768).Items.Length == 1, "polling is repeatable until ACK");
        Check(services.Acknowledge(firstDelivery, true, null).Status == ServiceStatus.Ok, "ack applied");
        Check(services.Acknowledge(firstDelivery, true, null) is { Status: ServiceStatus.Duplicate, Delivery.State: DeliveryState.Applied },
            "duplicate ack keeps the outcome");
        Check(services.Acknowledge(999_999, true, null).Status == ServiceStatus.Unknown, "late ack for unknown delivery");
        Check(services.Poll(page.NextSequence, 8, 32_768).Items.Length == 0, "ack empties the inbox");

        var repeat = services.Publish(sensor, "atmos-1", 1, Sample(101.3), 30, PacketTrafficClass.Interactive);
        Check(repeat.Status == ServiceStatus.Duplicate && repeat.MessageId == first.MessageId && repeat.Deliveries.Length == 0,
            "duplicate command creates no second effect");
        Check(services.Publish(sensor, "atmos-1", 1, Sample(55), 30, PacketTrafficClass.Interactive).Status ==
            ServiceStatus.IdempotencyConflict, "same key with different content");
        Check(services.Publish(display, "atmos-1", 2, Sample(1), 30, PacketTrafficClass.Interactive).Status ==
            ServiceStatus.Forbidden, "only the owner publishes");

        // Recipients are fixed at acceptance; a late subscriber does not join an in-flight event.
        var second = services.Publish(sensor, "atmos-1", 2, Sample(102), 30, PacketTrafficClass.Interactive);
        Check(services.Subscribe(panel, "side", "atmos-1", "atmos.sample", 1, "full", DeliveryMode.LatestValue, 1).Status ==
            ServiceStatus.Ok, "late subscribe");
        stand.Submit(second);
        stand.Advance(10);
        Check(services.MessageDeliveries(second.MessageId).Length == 1, "late subscriber not added to in-flight message");
        Check(services.Publish(sensor, "atmos-1", 1, Sample(101.3), 30, PacketTrafficClass.Interactive).Status ==
            ServiceStatus.StaleSequence, "older producer sequence");
        foreach (var item in services.Poll(0, 8, 32_768).Items)
            services.Acknowledge(item.DeliveryId, true, null);

        // A recreated receiver with the same ID is a new generation: old deliveries never reach it.
        var third = services.Publish(sensor, "atmos-1", 3, Sample(103), 30, PacketTrafficClass.Interactive);
        stand.Submit(third);
        var displayNext = display with { Generation = 2 };
        Check(services.AttachEndpoint(display, new NodeId(3)) == ServiceStatus.Duplicate &&
            services.AttachEndpoint(displayNext, new NodeId(3)) == ServiceStatus.Ok, "replace generation");
        Check(services.AttachEndpoint(display, new NodeId(3)) == ServiceStatus.StaleGeneration, "old generation is fenced");
        var toOldDisplay = services.MessageDeliveries(third.MessageId).Single(d => d.Recipient == "display-1");
        Check(toOldDisplay.State == DeliveryState.ReceiverGone, "in-flight delivery to old generation");
        stand.Advance(10);
        Check(stand.State(toOldDisplay.DeliveryId) == DeliveryState.ReceiverGone &&
            services.Poll(0, 8, 32_768).Items.All(item => item.Recipient != "display-1"), "late packet is not presented");
        foreach (var item in services.Poll(0, 8, 32_768).Items)
            services.Acknowledge(item.DeliveryId, true, null);

        // Revocation before presentation wins.
        var fourth = services.Publish(sensor, "atmos-1", 4, Sample(104), 30, PacketTrafficClass.Interactive);
        stand.Submit(fourth);
        Check(services.Unsubscribe(panel, "side") == ServiceStatus.Ok, "unsubscribe");
        stand.Advance(10);
        Check(services.MessageDeliveries(fourth.MessageId).All(d => d.State == DeliveryState.Cancelled),
            "unsubscribe cancels undelivered messages");

        // Latest-value replaces an undispatched value; a dispatched one waits for its ACK.
        Check(services.Subscribe(displayNext, "main", "atmos-1", "atmos.sample", 1, "panel.v1",
            DeliveryMode.LatestValue, 1).Status == ServiceStatus.Ok, "resubscribe new generation");
        var fifth = services.Publish(sensor, "atmos-1", 5, Sample(105), 30, PacketTrafficClass.Interactive);
        stand.Submit(fifth);
        stand.Advance(10);
        var sixth = services.Publish(sensor, "atmos-1", 6, Sample(106), 30, PacketTrafficClass.Interactive);
        stand.Submit(sixth);
        stand.Advance(10);
        var fifthDelivery = fifth.Deliveries[0];
        var sixthDelivery = sixth.Deliveries[0];
        Check(stand.State(fifthDelivery) == DeliveryState.Superseded &&
            services.Poll(0, 8, 32_768).Items.Single().DeliveryId == sixthDelivery, "latest value supersedes");

        // Destroying the world object removes its endpoint and fails what was waiting for it.
        Check(services.OnNodesRemoved([new NodeId(3)]) == 1, "node removal cascades");
        Check(stand.State(sixthDelivery) == DeliveryState.ReceiverGone && services.Poll(0, 8, 32_768).Items.Length == 0,
            "dispatched-or-not, a destroyed receiver gets nothing new");
        Check(services.AttachEndpoint(displayNext, new NodeId(3)) == ServiceStatus.StaleGeneration &&
            services.AttachEndpoint(display, new NodeId(3)) == ServiceStatus.StaleGeneration, "destroy fences retired generations");
        Check(services.AttachEndpoint(displayNext with { Generation = 3 }, new NodeId(3)) == ServiceStatus.Ok,
            "a newer generation may reuse the ID");
        Check(services.Acknowledge(sixthDelivery, true, null) is
            (ServiceStatus.Duplicate, { State: DeliveryState.ReceiverGone }), "late ACK leaves the terminal outcome unchanged");

        // A dispatched delivery without timely ACK becomes Unknown even without another poll.
        Check(services.Subscribe(panel, "side", "atmos-1", "atmos.sample", 1, "full", DeliveryMode.LatestValue, 1).Status ==
            ServiceStatus.Ok, "panel subscribes again");
        var seventh = services.Publish(sensor, "atmos-1", 7, Sample(107), 5, PacketTrafficClass.Interactive);
        stand.Submit(seventh);
        stand.Advance(4);
        Check(services.Poll(0, 8, 32_768).Items.Length == 1, "dispatched");
        stand.Tick += 700;
        Check(services.Acknowledge(seventh.Deliveries[0], true, null) is
            (ServiceStatus.Duplicate, { State: DeliveryState.Unknown, Reason: "ack_timeout" }), "ACK cannot bypass expiry");
        Check(services.Poll(0, 8, 32_768).Items.Length == 0 &&
            stand.State(seventh.Deliveries[0]) == DeliveryState.Unknown, "missing ACK is Unknown");

        // Expired before delivery.
        var eighth = services.Publish(sensor, "atmos-1", 8, Sample(108), 1, PacketTrafficClass.Interactive);
        stand.Submit(eighth);
        stand.Tick += 5;
        stand.Advance(3);
        Check(stand.State(eighth.Deliveries[0]) is DeliveryState.Expired, "deadline expires delivery");
    }

    private static void CheckLatestValueWithOutOfOrderDelivery()
    {
        var stand = new Stand();
        var services = stand.Services;
        var sensor = new EndpointKey("latest-sensor", 1);
        var display = new EndpointKey("latest-display", 1);
        Check(services.AttachEndpoint(sensor, new NodeId(1)) == ServiceStatus.Ok, "attach latest-value sensor");
        Check(services.AttachEndpoint(display, new NodeId(3)) == ServiceStatus.Ok, "attach latest-value display");
        Check(services.RegisterService(sensor, "latest-atmos", "atmos.telemetry", "atmos.sample", 1, 0) == ServiceStatus.Ok,
            "register latest-value sensor");
        Check(services.Subscribe(display, "main", "latest-atmos", "atmos.sample", 1, "panel.v1",
            DeliveryMode.LatestValue, 1).Status == ServiceStatus.Ok, "subscribe to latest sensor value");
        for (var sequence = 1; sequence <= 3; sequence++)
            Check(services.Publish(sensor, "latest-atmos", sequence, Sample(100 + sequence), 30,
                PacketTrafficClass.Background).Status == ServiceStatus.Ok, "publish a sample");
        var submissions = services.TakeSubmissions(3);
        Check(submissions.Length == 3, "each publication creates one delivery");

        // Complete the newest route first, then complete both delayed routes around the DM ACK.
        stand.Submit(submissions[2]);
        stand.Advance(4);
        stand.Submit(submissions[1]);
        stand.Advance(4);
        var newest = services.Poll(0, 8, 32_768).Items.Single();
        Check(newest.DeliveryId == submissions[2].DeliveryId && newest.Payload!.Value.GetProperty("pressureKPa").GetDouble() == 103 &&
            stand.State(submissions[1].DeliveryId) == DeliveryState.Superseded, "late packet cannot replace the newest queued sample");
        Check(services.Acknowledge(newest.DeliveryId, true, null).Status == ServiceStatus.Ok, "acknowledge the newest sample");
        stand.Submit(submissions[0]);
        stand.Advance(4);
        Check(services.Poll(0, 8, 32_768).Items.Length == 0 &&
            stand.State(submissions[0].DeliveryId) == DeliveryState.Superseded, "late packet cannot roll back an acknowledged sample");
    }

    private static void CheckBounds()
    {
        var stand = new Stand(new ServiceRuntimeOptions(MaxFanout: 2, MaxSubscriptionsPerEndpoint: 2, MaxQueuePending: 1));
        var services = stand.Services;
        var sensor = new EndpointKey("sensor-1", 1);
        Check(services.AttachEndpoint(sensor, new NodeId(1)) == ServiceStatus.Ok, "attach");
        Check(services.AttachEndpoint(new EndpointKey("ghost", 1), new NodeId(1)) == ServiceStatus.Ok, "second endpoint");
        Check(services.RegisterService(sensor, "atmos-1", "atmos.telemetry", "atmos.sample", 1, 0) == ServiceStatus.Ok, "register");
        for (var i = 1; i <= 3; i++)
            Check(services.AttachEndpoint(new EndpointKey($"reader-{i}", 1), new NodeId(3)) == ServiceStatus.Ok, "reader");
        Check(services.Subscribe(new EndpointKey("reader-1", 1), "q", "atmos-1", "atmos.sample", 1, "full", DeliveryMode.Queue, 1).Status == ServiceStatus.Ok, "queue");
        Check(services.Subscribe(new EndpointKey("reader-1", 1), "q2", "atmos-1", "atmos.sample", 1, "full", DeliveryMode.Queue, 1).Status == ServiceStatus.Ok, "second");
        Check(services.Subscribe(new EndpointKey("reader-1", 1), "q3", "atmos-1", "atmos.sample", 1, "full", DeliveryMode.Queue, 1).Status ==
            ServiceStatus.LimitExceeded, "per-endpoint subscription quota");
        Check(services.Subscribe(new EndpointKey("reader-1", 1), "q4", "atmos-1", "atmos.sample", 1, "full", DeliveryMode.Queue, 2).Status ==
            ServiceStatus.InvalidRequest, "queue depth bound");
        Check(services.Subscribe(new EndpointKey("reader-2", 1), "q", "atmos-1", "atmos.sample", 1, "full", DeliveryMode.Queue, 1).Status == ServiceStatus.Ok, "third");
        Check(services.Publish(sensor, "atmos-1", 1, Sample(1), 30, PacketTrafficClass.Background).Status == ServiceStatus.LimitExceeded,
            "fanout bound rejects before acceptance");
        Check(services.AttachEndpoint(new EndpointKey("reader-2", 1), new NodeId(3), "away") == ServiceStatus.Ok &&
            services.Publish(sensor, "atmos-1", 1, Sample(1), 30, PacketTrafficClass.Background).Recipients == 2,
            "revoked subscription does not consume fanout");
        Check(services.Unsubscribe(new EndpointKey("reader-1", 1), "q") == ServiceStatus.Ok &&
            services.Subscribe(new EndpointKey("reader-1", 1), "q", "atmos-1", "atmos.sample", 1, "full",
                DeliveryMode.Queue, 1).Status == ServiceStatus.Ok, "cancel first publication for inbox bound fixture");
        Check(services.Unsubscribe(new EndpointKey("reader-2", 1), "q") == ServiceStatus.Ok &&
            services.Unsubscribe(new EndpointKey("reader-1", 1), "q2") == ServiceStatus.Ok, "trim");

        // A slow queue subscriber is isolated by its own bound.
        var one = services.Publish(sensor, "atmos-1", 2, Sample(1), 30, PacketTrafficClass.Background);
        var two = services.Publish(sensor, "atmos-1", 3, Sample(2), 30, PacketTrafficClass.Background);
        stand.Submit(one);
        stand.Submit(two);
        stand.Advance(12);
        Check(stand.State(one.Deliveries[0]) == DeliveryState.TransportDelivered &&
            stand.State(two.Deliveries[0]) == DeliveryState.Busy, "queue bound gives an explicit Busy");
        var counters = services.Capture();
        Check(counters.InboxEntries == 1 && counters.ActiveDeliveries == 1, "bounded active deliveries");
    }

    private static void CheckOutboxBackpressure()
    {
        var stand = new Stand(new ServiceRuntimeOptions(MaxActiveDeliveries: 2, MaxEndpoints: 2));
        var services = stand.Services;
        var sender = new EndpointKey("sender", 1);
        var receiver = new EndpointKey("receiver", 1);
        Check(services.AttachEndpoint(sender, new NodeId(1)) == ServiceStatus.Ok &&
            services.AttachEndpoint(receiver, new NodeId(3)) == ServiceStatus.Ok &&
            services.RegisterService(sender, "atmos-1", "atmos.telemetry", "atmos.sample", 1, 0) == ServiceStatus.Ok,
            "bounded outbox fixture");
        for (var i = 1; i <= 2; i++)
        {
            Check(services.Subscribe(receiver, "q", "atmos-1", "atmos.sample", 1, "full", DeliveryMode.Queue, 1).Status ==
                ServiceStatus.Ok, "subscribe before publish");
            Check(services.Publish(sender, "atmos-1", i, Sample(i), 30, PacketTrafficClass.Interactive).Status ==
                ServiceStatus.Ok && services.Unsubscribe(receiver, "q") == ServiceStatus.Ok, "cancel before transport drains");
        }
        services.Subscribe(receiver, "q", "atmos-1", "atmos.sample", 1, "full", DeliveryMode.Queue, 1);
        Check(services.Capture() is { ActiveDeliveries: 0, PendingSubmissions: 2 } &&
            services.Publish(sender, "atmos-1", 3, Sample(3), 30, PacketTrafficClass.Interactive).Status == ServiceStatus.Busy,
            "cancelling queued work cannot bypass outbox capacity");
        Check(stand.Submit() == 0 &&
            services.Publish(sender, "atmos-1", 3, Sample(3), 30, PacketTrafficClass.Interactive).Status == ServiceStatus.Ok,
            "drain discards cancelled work and allows retry");
        Check(services.DetachEndpoint(receiver) == ServiceStatus.Ok &&
            services.AttachEndpoint(new EndpointKey("third", 1), new NodeId(3)) == ServiceStatus.LimitExceeded,
            "retired IDs count against the runtime's identity budget");
    }
}
