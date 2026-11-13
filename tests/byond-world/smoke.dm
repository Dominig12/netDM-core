/world
    fps = 20

/world/New()
    ..()
    world.log = file("smoke.log")
    spawn(0)
        var/datum/netdm_world_smoke/test = new
        var/result = test.exercise()
        test.bridge.stop()
        world.log << (result ? "NETDM_WORLD_FAIL [result]" : "NETDM_WORLD_PASS")
        shutdown()

/datum/netdm_bridge/v1/staged/smoke
    var/disconnects = 0
    var/last_resync = null

/datum/netdm_bridge/v1/staged/smoke/on_connection_lost()
    disconnects++

/datum/netdm_bridge/v1/staged/smoke/on_resync_required(reason)
    last_resync = reason

/datum/smoke_sensor

/datum/smoke_display
    var/pressure = null
    var/applied = 0

/datum/netdm_effect_handler/smoke_panel/apply(datum/smoke_display/target, list/item)
    if(!istype(target))
        return "wrong_target"
    var/list/payload = item["payload"]
    if(!islist(payload) || !isnum(payload["pressureKPa"]))
        return "invalid_payload"
    target.pressure = payload["pressureKPa"]
    target.applied++
    return TRUE

/datum/smoke_pump
    var/on = FALSE
    var/executions = 0

/datum/smoke_console
    var/results = 0
    var/last_outcome
    var/list/last_result
    var/list/discovered = list()

/datum/netdm_command_handler/smoke_power/execute(datum/smoke_pump/target, list/item)
    if(!istype(target))
        return "wrong_target"
    var/list/arguments = item["payload"]
    var/want = !!arguments?["on"]
    var/changed = target.on != want
    target.on = want
    target.executions++
    return list("result" = list("on" = want, "changed" = changed))

/datum/netdm_effect_handler/smoke_console_result/apply(datum/smoke_console/target, list/item)
    if(!istype(target))
        return "wrong_target"
    target.results++
    target.last_outcome = item["outcome"]
    target.last_result = item["payload"]
    return TRUE

/datum/netdm_effect_handler/smoke_discovery/apply(datum/smoke_console/target, list/item)
    var/list/descriptor = item["payload"]
    if(!istype(target) || !islist(descriptor))
        return "invalid_descriptor"
    target.discovered |= descriptor["instance"]
    return TRUE

/datum/netdm_world_smoke
    var/datum/netdm_bridge/v1/staged/smoke/bridge = new
    var/a = "9007199254740993"
    var/b = "9007199254740994"
    var/datum/netdm_endpoint_registry/registry = new
    var/datum/netdm_effect_dispatcher/dispatcher = new
    var/datum/smoke_sensor/sensor = new
    var/datum/smoke_display/display = new
    var/sensor_id
    var/display_id

/datum/netdm_world_smoke/proc/response(id)
    if(!id)
        world.log << "netDM request was not queued (connected=[bridge.connected], owner=[!!bridge.owner_fence], bridge=[json_encode(native_status())])"
        return null
    var/deadline = world.time + 50
    var/wall_started = world.timeofday
    while(world.time < deadline)
        for(var/list/message in bridge.pump())
            if(message["id"] == id)
                return message
        sleep(1)
    var/wall_elapsed = (world.timeofday - wall_started + 864000) % 864000
    world.log << "netDM response timeout for [id] (wall_ds=[wall_elapsed], disconnects=[bridge.disconnects], resync=[bridge.last_resync], bridge=[json_encode(native_status())])"
    return null

/datum/netdm_world_smoke/proc/ok(id)
    var/list/reply = response(id)
    if(reply && !reply["ok"])
        world.log << "netDM request [id] rejected: [reply["error"]]"
    return reply?["ok"]

/datum/netdm_world_smoke/proc/negotiate()
    var/deadline = world.time + 150
    while(world.time < deadline)
        bridge.pump()
        if(bridge.connected && ok(bridge.negotiate("dreamdaemon-smoke", "9606dce0-4396-4d6f-a016-0739f5604088")))
            return bridge.owner_fence && bridge.stream_id
        sleep(1)
    return FALSE

/datum/netdm_world_smoke/proc/node_count()
    var/list/reply = response(bridge.game_stats())
    var/list/stats = reply?["game"]
    return stats?["nodes"]

/datum/netdm_world_smoke/proc/synchronize(snapshot_id)
    var/old_stream = bridge.stream_id
    if(!ok(bridge.snapshot_begin(snapshot_id, 2, 4, "0")))
        return "begin"
    var/nodes = json_encode(list(list("op" = "add_node", "a" = a, "x" = 0, "y" = 0), list("op" = "add_node", "a" = b, "x" = 10, "y" = 0)))
    var/radios = json_encode(list(list("op" = "game_radio", "a" = a), list("op" = "game_radio", "a" = b)))
    var/nodes_digest = uppertext(md5(nodes))
    var/radios_digest = uppertext(md5(radios))
    if(!ok(bridge.snapshot_chunk(snapshot_id, 0, nodes, nodes_digest)) || !ok(bridge.snapshot_chunk(snapshot_id, 1, radios, radios_digest)))
        return "chunks"
    if(!ok(bridge.snapshot_commit(snapshot_id, md5("[nodes_digest][radios_digest]"))) || node_count() != 0)
        return "commit must keep graph private"
    var/list/denied = response(bridge.packet_send(a, b, 96, "before-ready"))
    if(denied?["error"] != "game_not_ready")
        return "pre-ready admission"
    if(!ok(bridge.world_delta(snapshot_id, "1", list(list("op" = "move_node", "a" = b, "x" = 9, "y" = 0)))))
        return "catch-up delta"
    if(!ok(bridge.snapshot_ready(snapshot_id, "1")) || bridge.stream_id == old_stream || node_count() != 2)
        return "ready publication"
    if(!ok(bridge.heartbeat(snapshot_id, "1")))
        return "heartbeat"
    return null

/datum/netdm_world_smoke/proc/deliver(request_id, source = null, destination = null, expected_hops = null)
    var/list/reply = response(bridge.packet_send(isnull(source) ? a : source, isnull(destination) ? b : destination, 96, request_id))
    var/list/submission = reply?["submission"]
    if(!submission || !submission["accepted"] || !istext(submission["packetId"]))
        return FALSE
    var/deadline = world.time + 40
    while(world.time < deadline)
        reply = response(bridge.request_receipts())
        var/list/page = reply?["receiptPage"]
        var/list/receipts = page?["receipts"]
        for(var/list/item in receipts)
            if(item["requestId"] == request_id && item["status"] == 0)
                return isnull(expected_hops) || item["successfulHops"] == expected_hops
        sleep(1)
    return FALSE

/// A fresh snapshot derives the wired path from area geometry rather than APC positions.
/datum/netdm_world_smoke/proc/apc_topology()
    var/c = "9007199254740995"
    var/diagonal = "9007199254740996"
    var/other_level = "9007199254740997"
    var/list/facts = list(
        list("op" = "add_node", "a" = a, "x" = 100, "y" = 100, "z" = 1),
        list("op" = "add_node", "a" = b, "x" = 200, "y" = 200, "z" = 1),
        list("op" = "add_node", "a" = c, "x" = 300, "y" = 300, "z" = 1),
        list("op" = "add_node", "a" = diagonal, "x" = 301, "y" = 300, "z" = 1),
        list("op" = "add_node", "a" = other_level, "x" = 301, "y" = 300, "z" = 2),
        list("op" = "area_span", "area" = "1", "x" = 1, "endX" = 4, "y" = 1, "z" = 1),
        list("op" = "area_span", "area" = "2", "x" = 5, "endX" = 8, "y" = 1, "z" = 1),
        list("op" = "area_span", "area" = "3", "x" = 7, "endX" = 9, "y" = 2, "z" = 1),
        list("op" = "area_span", "area" = "4", "x" = 10, "endX" = 10, "y" = 3, "z" = 1),
        list("op" = "area_span", "area" = "2", "x" = 5, "endX" = 8, "y" = 1, "z" = 2),
        list("op" = "area_span", "area" = "5", "x" = 9, "endX" = 10, "y" = 1, "z" = 2),
        list("op" = "area_apc", "a" = a, "area" = "1", "x" = 1, "y" = 1, "z" = 1),
        list("op" = "area_apc", "a" = b, "area" = "2", "x" = 8, "y" = 1, "z" = 1),
        list("op" = "area_apc", "a" = c, "area" = "3", "x" = 7, "y" = 2, "z" = 1),
        list("op" = "area_apc", "a" = diagonal, "area" = "4", "x" = 10, "y" = 3, "z" = 1),
        list("op" = "area_apc", "a" = b, "area" = "2", "x" = 8, "y" = 1, "z" = 2),
        list("op" = "area_apc", "a" = other_level, "area" = "5", "x" = 10, "y" = 1, "z" = 2),
    )
    var/data = json_encode(facts)
    var/digest = uppertext(md5(data))
    var/list/first_node = facts[1]
    first_node["z"] = 2
    var/invalid_data = json_encode(facts)
    var/invalid_digest = uppertext(md5(invalid_data))
    if(!ok(bridge.snapshot_begin("apc-invalid", 1, length(facts), "1")) || !ok(bridge.snapshot_chunk("apc-invalid", 0, invalid_data, invalid_digest)))
        return "invalid APC fixture"
    var/list/invalid = response(bridge.snapshot_commit("apc-invalid", md5(invalid_digest)))
    if(!invalid || invalid["ok"] || node_count() != 2)
        return "APC assignment must stay on the served level"
    if(!ok(bridge.snapshot_begin("apc", 1, length(facts), "1")) || !ok(bridge.snapshot_chunk("apc", 0, data, digest)))
        return "APC facts"
    if(!ok(bridge.snapshot_commit("apc", md5(digest))) || node_count() != 2)
        return "APC commit must keep the new graph private"
    var/list/level_change = list(list("op" = "move_node", "a" = b, "x" = 200, "y" = 200, "z" = 3))
    invalid = response(bridge.world_delta("apc", "2", level_change))
    if(!invalid || invalid["ok"])
        return "catch-up APC move must stay on a served level"
    if(!ok(bridge.snapshot_ready("apc", "1")) || node_count() != 5)
        return "APC graph did not publish"
    invalid = response(bridge.world_delta("apc", "2", level_change))
    if(!invalid || invalid["ok"])
        return "live APC move must stay on a served level"
    if(!deliver("apc-chain", a, c, 2))
        return "adjacent areas must carry a two-hop delivery"
    if(!deliver("apc-cross-level", a, other_level, 2))
        return "shared APC must connect its served levels"
    var/list/reply = response(bridge.packet_send(c, diagonal, 96, "apc-diagonal"))
    var/list/submission = reply?["submission"]
    if(!reply?["ok"] || !submission || submission["accepted"] || submission["rejection"] != 1)
        return "diagonal area must be unreachable"
    reply = response(bridge.packet_send(diagonal, other_level, 96, "apc-overlapping-xy"))
    submission = reply?["submission"]
    if(!reply?["ok"] || !submission || submission["accepted"] || submission["rejection"] != 1)
        return "overlapping XY on another level must not create a link"
    if(!ok(bridge.world_delta("apc", "2", list(list("op" = "move_node", "a" = b, "x" = 200, "y" = 200, "z" = 2)))))
        return "APC can move to another served level"
    if(!deliver("apc-cross-level-after-move", a, other_level, 2))
        return "move within served levels must preserve the route"
    if(!ok(bridge.world_delta("apc", "3", list(list("op" = "set_forwarding", "a" = b, "role" = "endpoint")))))
        return "remove transit role"
    reply = response(bridge.packet_send(a, other_level, 96, "apc-no-transit"))
    submission = reply?["submission"]
    if(!reply?["ok"] || !submission || submission["accepted"] || submission["rejection"] != 1 || !deliver("apc-as-recipient", a, b, 1))
        return "endpoint must receive without forwarding"
    if(!ok(bridge.world_delta("apc", "4", list(list("op" = "set_forwarding", "a" = b, "role" = "relay")))))
        return "restore transit role"
    if(!deliver("apc-relay", a, other_level, 2))
        return "restored relay must forward across its served levels"
    if(!ok(bridge.world_delta("apc", "5", list(list("op" = "remove_node", "a" = b)))))
        return "remove intermediate APC"
    reply = response(bridge.packet_send(a, c, 96, "apc-cut"))
    submission = reply?["submission"]
    if(!reply?["ok"] || !submission || submission["accepted"] || submission["rejection"] != 1)
        return "removed APC must sever the chain"
    world.log << "NETDM_WORLD_APC_TOPOLOGY_PASS"
    return device_attachment()

/datum/netdm_world_smoke/proc/attachment_of(list/reply, node_id)
    var/list/result = reply?["result"]
    for(var/list/device in result?["devices"])
        if(device["a"] == node_id)
            return device["attachment"]
    return null

/datum/netdm_world_smoke/proc/device_delta(sequence, list/changes)
    var/list/reply = response(bridge.world_delta("dev", sequence, changes))
    if(reply && !reply["ok"])
        world.log << "netDM device delta [sequence] rejected: [reply["error"]]"
    return reply

/// Devices report position and mounting. The Host derives a wired port to the APC of the
/// served segment or radio within range, and switches them atomically.
/datum/netdm_world_smoke/proc/device_attachment()
    var/apc_one = "9007199254741001"
    var/apc_two = "9007199254741002"
    var/fixed = "9007199254741011"
    var/roaming = "9007199254741012"
    var/list/facts = list(
        list("op" = "add_node", "a" = apc_one, "x" = 2, "y" = 1, "z" = 1, "accessRange" = 4),
        list("op" = "add_node", "a" = apc_two, "x" = 7, "y" = 1, "z" = 1, "accessRange" = 4),
        list("op" = "area_span", "area" = "1", "x" = 1, "endX" = 4, "y" = 1, "z" = 1),
        list("op" = "area_span", "area" = "2", "x" = 5, "endX" = 8, "y" = 1, "z" = 1),
        list("op" = "area_apc", "a" = apc_one, "area" = "1", "x" = 1, "y" = 1, "z" = 1),
        list("op" = "area_apc", "a" = apc_two, "area" = "2", "x" = 8, "y" = 1, "z" = 1),
    )
    var/data = json_encode(facts)
    var/digest = uppertext(md5(data))
    if(!ok(bridge.snapshot_begin("dev", 1, length(facts), "5", "apc_map")) || !ok(bridge.snapshot_chunk("dev", 0, data, digest)))
        return "device map facts"
    if(!ok(bridge.snapshot_commit("dev", md5(digest))) || !ok(bridge.snapshot_ready("dev", "5")) || node_count() != 2)
        return "device map publication"
    var/list/anchored_state = list("op" = "device_state", "a" = fixed, "x" = 4, "y" = 1, "z" = 1, "mount" = "fixed", "anchored" = TRUE, "range" = 3)
    var/list/loose_state = anchored_state.Copy()
    loose_state["anchored"] = FALSE
    var/list/roaming_state = list("op" = "device_state", "a" = roaming, "x" = 7, "y" = 2, "z" = 1, "mount" = "mobile", "anchored" = TRUE, "range" = 3)
    var/list/reply = device_delta("6", list(anchored_state, roaming_state))
    if(attachment_of(reply, fixed) != "wired" || attachment_of(reply, roaming) != "radio")
        return "anchored fixed device must be wired, mobile must use radio"
    if(!ok(bridge.heartbeat("dev", "6")) || !deliver("device-wired", fixed, roaming, 3))
        return "wired device must reach the mobile device through both APCs"
    reply = response(bridge.world_delta("dev", "7", list(list("op" = "move_node", "a" = fixed, "x" = 3, "y" = 1, "z" = 1))))
    if(!reply || reply["ok"])
        return "raw topology must not change a device"
    if(attachment_of(device_delta("7", list(loose_state)), fixed) != "radio")
        return "unanchored fixed device must use radio"
    if(!ok(bridge.heartbeat("dev", "7")) || !deliver("device-radio", fixed, roaming, 2))
        return "unanchored device must use the nearest access point"
    if(attachment_of(device_delta("8", list(anchored_state)), fixed) != "wired")
        return "anchoring must restore the wired port"
    if(!ok(bridge.world_delta("dev", "9", list(list("op" = "remove_node", "a" = apc_one)))))
        return "remove APC of the wired device"
    if(!ok(bridge.heartbeat("dev", "9")) || !deliver("device-fallback", fixed, roaming, 2))
        return "device of a removed APC must fall back to radio in the same batch"
    if(!device_delta("10", list(list("op" = "device_remove", "a" = fixed), list("op" = "device_remove", "a" = roaming)))?["ok"] || node_count() != 1)
        return "device removal"
    reply = device_delta("11", list(anchored_state))
    if(!reply || reply["ok"])
        return "a removed device ID must not be reused in the stream"
    world.log << "NETDM_WORLD_DEVICE_ATTACHMENT_PASS"
    return null

/datum/netdm_world_smoke/proc/value(list/reply)
    var/list/result = reply?["result"]
    return result?["value"]

/// Local control-plane facts for the current runtime: attach, register, subscribe.
/datum/netdm_world_smoke/proc/bind_services()
    var/sensor_generation = registry.generation_of(sensor_id)
    var/display_generation = registry.generation_of(display_id)
    if(!ok(bridge.endpoint_attach(sensor_id, sensor_generation, a)) || !ok(bridge.endpoint_attach(display_id, display_generation, b)))
        return "attach"
    if(!ok(bridge.service_register(sensor_id, sensor_generation, "atmos-1", "atmos.telemetry", "atmos.sample", 1)))
        return "register"
    if(!ok(bridge.subscribe(display_id, display_generation, "main", "atmos-1", "atmos.sample", 1, "panel.v1")))
        return "subscribe"
    dispatcher.reset_cursor()
    return null

/// Returns the accepted delivery ID of a one-recipient publication.
/datum/netdm_world_smoke/proc/publish(snapshot_id, sequence, pressure)
    if(!ok(bridge.heartbeat(snapshot_id, "1")))
        return null
    var/list/payload = list("pressureKPa" = pressure, "temperatureK" = 293.15, "sampleTick" = round(world.time), "quality" = "current")
    var/list/accepted = value(response(bridge.publish(sensor_id, registry.generation_of(sensor_id), "atmos-1", sequence, payload)))
    var/list/deliveries = accepted?["deliveries"]
    return length(deliveries) == 1 ? deliveries[1] : null

/// Polls until the delivery appears, applies it through the allowlist and ACKs.
/// lose_first_ack drops the first ACK, as if its reply was lost with the session.
/datum/netdm_world_smoke/proc/apply_delivery(delivery_id, lose_first_ack = FALSE, suppress_ack = FALSE)
    var/deadline = world.time + 50
    while(world.time < deadline)
        var/list/page = response(bridge.delivery_poll(dispatcher.cursor))?["result"]
        var/list/acks = dispatcher.handle_page(page, registry)
        var/seen = FALSE
        for(var/list/ack in acks)
            seen = seen || ack["deliveryId"] == delivery_id
        if(seen && suppress_ack)
            return "transport_delivered"
        if(seen && lose_first_ack)
            lose_first_ack = FALSE
            dispatcher.cursor = "0"
            continue
        if(length(acks))
            var/list/answer = response(bridge.delivery_ack(acks))?["result"]
            for(var/list/result in answer?["acks"])
                if(result["deliveryId"] == delivery_id)
                    return result["state"]
        sleep(1)
    return null

/datum/netdm_world_smoke/proc/delivery_state(delivery_id)
    var/list/result = response(bridge.delivery_status(delivery_id))?["result"]
    var/list/deliveries = result?["deliveries"]
    var/list/view = length(deliveries) ? deliveries[1] : null
    return view?["state"]

/// P2 vertical slice on a real DreamDaemon: typed publication, physical delivery,
/// allowlisted application, idempotent ACK and generation fencing.
/datum/netdm_world_smoke/proc/services(snapshot_id)
    dispatcher.allow("atmos.sample", new /datum/netdm_effect_handler/smoke_panel)
    sensor_id = registry.register(sensor)
    display_id = registry.register(display)
    var/problem = bind_services()
    if(problem)
        return problem
    var/delivery = publish(snapshot_id, 1, 101.5)
    if(!delivery || apply_delivery(delivery) != "applied" || display.pressure != 101.5 || display.applied != 1)
        return "first publication was not applied once"
    if(delivery_state(delivery) != "applied")
        return "Host does not report the application ACK"

    delivery = publish(snapshot_id, 2, 99)
    if(!delivery || apply_delivery(delivery, lose_first_ack = TRUE) != "applied" || display.applied != 2)
        return "a repeated poll applied an effect twice"

    // A delayed replay stays fenced even after newer outcomes evict its cached ACK.
    dispatcher.outcome_limit = 1
    var/rewind_cursor = dispatcher.cursor
    delivery = publish(snapshot_id, 3, 50)
    if(!delivery || apply_delivery(delivery, suppress_ack = TRUE) != "transport_delivered")
        return "could not leave a delivery without ACK"
    var/newer = publish(snapshot_id, 4, 60)
    if(!newer || apply_delivery(newer) != "applied" || display.applied != 4)
        return "newer delivery did not evict the old outcome"
    var/list/replay = response(bridge.delivery_poll(rewind_cursor))?["result"]
    var/list/replayed_items = replay?["items"]
    if(length(replayed_items) != 1 || length(dispatcher.handle_page(replay, registry)) || display.applied != 4)
        return "an evicted outcome was applied again or falsely acknowledged"
    dispatcher.outcome_limit = 1024

    // The same logical display now lives in a new object: generation 2.
    delivery = publish(snapshot_id, 5, 51)
    var/datum/smoke_display/replacement = new
    var/generation = registry.rebind(display_id, replacement)
    if(!delivery || generation != 2 || !ok(bridge.endpoint_attach(display_id, generation, b)))
        return "rebind"
    if(delivery_state(delivery) != "receiver_gone")
        return "an old-generation delivery was not fenced"
    if(apply_delivery(delivery) || display.applied != 4 || replacement.applied != 0)
        return "a fenced delivery reached DM"
    display = replacement
    world.log << "NETDM_WORLD_SERVICES_APPLIED"
    return null

/// One poll/apply/ACK pass over the inbox through the allowlisted dispatcher.
/datum/netdm_world_smoke/proc/drain_inbox()
    var/list/page = response(bridge.delivery_poll(dispatcher.cursor))?["result"]
    var/list/acks = dispatcher.handle_page(page, registry)
    if(length(acks))
        response(bridge.delivery_ack(acks))

/// P2.2 on a real DreamDaemon: a command crosses the network to a provider object,
/// is applied once through an allowlisted handler and its result crosses back.
/datum/netdm_world_smoke/proc/commands(snapshot_id)
    var/datum/smoke_pump/pump = new
    var/datum/smoke_console/console = new
    var/pump_id = registry.register(pump)
    var/console_id = registry.register(console)
    var/rogue_id = registry.register(new /datum/smoke_console)
    dispatcher.allow_command("machine.control", "set_power", new /datum/netdm_command_handler/smoke_power)
    dispatcher.allow_result("set_power", new /datum/netdm_effect_handler/smoke_console_result)
    dispatcher.allow_result("directory.describe", new /datum/netdm_effect_handler/smoke_discovery)
    if(!ok(bridge.endpoint_attach(pump_id, 1, b)) || !ok(bridge.endpoint_attach(console_id, 1, a)))
        return "attach"
    if(!ok(bridge.endpoint_attach(rogue_id, 1, a, "syndicate")))
        return "attach rogue"
    if(!ok(bridge.service_register(pump_id, 1, "pump-1", "machine.control", "machine.power_state", 1)))
        return "register"
    if(!ok(bridge.heartbeat(snapshot_id, "1")))
        return "heartbeat"
    var/list/arguments = list("on" = TRUE)
    var/list/invoked = response(bridge.invoke(console_id, 1, "pump-1", "set_power", "op-1", arguments))
    var/list/operation = value(invoked)
    if(!invoked?["ok"] || !istext(operation?["operationId"]))
        return "invoke"
    var/list/hidden = response(bridge.invoke(rogue_id, 1, "pump-1", "set_power", "op-1", arguments))
    if(hidden?["error"] != "unknown_service")
        return "another domain must not see the service"
    var/deadline = world.time + 50
    while(console.results < 1 && world.time < deadline)
        drain_inbox()
        sleep(1)
    if(console.results != 1 || console.last_outcome != "applied" || !pump.on || pump.executions != 1)
        var/list/failure = value(response(bridge.command_status(console_id, 1, operation["operationId"])))
        return "command result did not return over the network (executions=[pump.executions], results=[console.results], cursor=[dispatcher.cursor], status=[json_encode(failure)])"
    var/list/status = value(response(bridge.command_status(console_id, 1, operation["operationId"])))
    if(status?["outcome"] != "applied" || status?["result"] != "delivered")
        return "operation status"

    if(!ok(bridge.heartbeat(snapshot_id, "1")))
        return "heartbeat"
    var/list/repeated = response(bridge.invoke(console_id, 1, "pump-1", "set_power", "op-1", arguments))
    if(value(repeated)?["outcome"] != "applied")
        return "repeated invoke"
    deadline = world.time + 10
    while(world.time < deadline)
        drain_inbox()
        sleep(1)
    if(pump.executions != 1)
        return "a repeated command was applied twice"

    if(!ok(bridge.heartbeat(snapshot_id, "1")) || !ok(bridge.discover(console_id, 1, "machine.control", "scan-1")))
        return "discover"
    deadline = world.time + 50
    while(!("pump-1" in console.discovered) && world.time < deadline)
        drain_inbox()
        sleep(1)
    if(!("pump-1" in console.discovered) || pump.executions != 1)
        return "discovery round trip"
    world.log << "NETDM_WORLD_COMMANDS_APPLIED"
    return null

/// After a full reconnect the same round-stable IDs and generations are reused.
/datum/netdm_world_smoke/proc/services_resume(snapshot_id)
    var/old_sensor = sensor_id
    var/problem = bind_services()
    if(problem)
        return "resume [problem]"
    var/delivery = publish(snapshot_id, 1, 77)
    if(sensor_id != old_sensor || !delivery || apply_delivery(delivery) != "applied" || display.pressure != 77)
        return "services after reconnect"
    world.log << "NETDM_WORLD_SERVICES_RESUMED"
    return null

/datum/netdm_world_smoke/proc/native_status()
    return json_decode(call_ext(NETDM_DLL, "netdm_status")())

/// Fault matrix: stop pumping until the bounded native inbox overflows. The
/// bridge must fence the session without surfacing any of the queued replies,
/// and the world must recover through a new owner and a full snapshot.
/datum/netdm_world_smoke/proc/overflow(snapshot_id)
    var/old_owner = bridge.owner_fence
    var/old_stream = bridge.stream_id
    var/list/status = native_status()
    var/lost = status["lostResponses"]
    var/sent = 0
    var/deadline = world.time + 200
    while(world.time < deadline)
        status = native_status()
        if(status["lostResponses"] > lost)
            break
        for(var/i = 1 to 64)
            if(!bridge.heartbeat(snapshot_id, "1"))
                break
            sent++
        sleep(world.tick_lag)
    if(status["lostResponses"] <= lost || sent <= 1024)
        return "native inbox did not overflow after [sent] requests: [json_encode(status)]"
    var/list/replies = bridge.pump()
    if(length(replies) || bridge.owner_fence || bridge.last_resync != "native_mailbox_overflow")
        return "overflow must fence the session without stale replies"
    if(!negotiate() || bridge.owner_fence == old_owner || bridge.stream_id == old_stream)
        return "new owner after overflow"
    var/problem = synchronize("overflow")
    if(problem)
        return "overflow resync [problem]"
    if(!deliver("overflow-packet"))
        return "delivery after overflow"
    world.log << "NETDM_WORLD_OVERFLOW_RECOVERED sent=[sent]"
    return null

/datum/netdm_world_smoke/proc/exercise()
    if(!bridge.initialize() || !negotiate())
        return "initial handshake"
    var/problem = synchronize("initial")
    if(problem)
        return "initial [problem]"
    if(!deliver("initial-packet"))
        return "initial delivery"
    world.log << "NETDM_WORLD_INITIAL_READY"

    var/old_stream = bridge.stream_id
    var/old_owner = bridge.owner_fence
#ifdef NETDM_CRASH_SMOKE
    var/old_disconnects = bridge.disconnects
    text2file("ready", "/out/crash.ready")
    var/deadline = world.time + 450
    while(bridge.disconnects == old_disconnects && world.time < deadline)
        bridge.pump()
        sleep(1)
    if(bridge.disconnects == old_disconnects)
        return "Host crash was not observed"
    world.log << "NETDM_WORLD_OFFLINE"
    text2file("offline", "/out/offline.ready")
    while(!file2text("restart.ready") && world.time < deadline)
        bridge.pump()
        sleep(1)
    if(!file2text("restart.ready"))
        return "Host restart was not confirmed"
    world.log << "NETDM_WORLD_RESTART_GATE"
#else
    bridge.recover_session("smoke_reconnect")
#endif
    if(!negotiate() || bridge.stream_id == old_stream || bridge.owner_fence == old_owner)
        return "new owner after reconnect"
    world.log << "NETDM_WORLD_RENEGOTIATED"
    problem = synchronize("reconnected")
    if(problem)
        return "reconnected [problem]"
    if(!deliver("reconnected-packet"))
        return "delivery after resnapshot"
    world.log << "NETDM_WORLD_RESTORED"
#ifdef NETDM_CRASH_SMOKE
    text2file("ready", "/out/after.ready")
    deadline = world.time + 150
    while(!file2text("finish.ready") && world.time < deadline)
        bridge.pump()
        sleep(1)
    if(!file2text("finish.ready"))
        return "restored game readiness was not confirmed"
#endif
    problem = services("reconnected")
    if(problem)
        return "services: [problem]"
    problem = commands("reconnected")
    if(problem)
        return "commands: [problem]"
    problem = overflow("reconnected")
    if(problem)
        return problem
    problem = services_resume("overflow")
    if(problem)
        return problem
    return apc_topology()
