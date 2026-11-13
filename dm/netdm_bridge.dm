// A standalone BYOND DM adapter for the classic call_ext() string ABI.
// Include in a .dme; the x86 bridge must be trusted by DreamDaemon.
// Ordinary calls only access bounded local queues. stop() joins the worker.
#ifndef NETDM_DLL
#define NETDM_DLL "netdm_byond.dll"
#endif

/datum/netdm_bridge
    var/serial = 0
    var/ready = FALSE
    var/stream_id = null
    var/receipt_cursor = 0
    var/receipt_pending = FALSE
    var/receipt_request_id = null
    var/last_lost_responses = 0
    var/framed = FALSE
    var/protocol_minor = 0
    var/connected = FALSE
    var/owner_fence = null
    var/command_sequence = 0
    var/handshake_request_id = null
    var/reset_request_id = null
    var/session_deadline = 0
    var/receipt_deadline = 0
    /// Replies pump() discarded (outside the current stream or session reset) and the last one, for diagnostics.
    var/dropped = 0
    var/last_dropped = null

/datum/netdm_bridge/v1
    framed = TRUE

/datum/netdm_bridge/v1/staged
    protocol_minor = 1

/datum/netdm_bridge/proc/initialize()
    ready = (call_ext(NETDM_DLL, "netdm_init")() == "1")
    return ready

/datum/netdm_bridge/proc/stop()
    if(ready)
        call_ext(NETDM_DLL, "netdm_shutdown")()
    ready = FALSE
    connected = FALSE
    clear_session()

/datum/netdm_bridge/proc/clear_session()
    owner_fence = null
    stream_id = null
    command_sequence = 0
    handshake_request_id = null
    reset_request_id = null
    receipt_cursor = framed ? "0" : 0
    receipt_pending = FALSE
    receipt_request_id = null

/// V1: call after pump observes a connection. A reconnect requires a new
/// handshake followed by a full export of current world facts.
/datum/netdm_bridge/proc/negotiate(server_id, round_epoch)
    if(!framed || !connected || owner_fence || handshake_request_id)
        return null
    return request(list("op" = "hello", "protocolMajor" = 1, "protocolMinor" = protocol_minor,
        "serverId" = server_id, "roundEpoch" = round_epoch))

/datum/netdm_bridge/proc/recover_session(reason)
    clear_session()
    if(framed)
        connected = FALSE
        call_ext(NETDM_DLL, "netdm_reconnect")()
    on_resync_required(reason)

/// Send an associative JSON object; returns the pipe command id on successful
/// SHIM QUEUE acceptance (not Host acceptance or physical delivery).
/datum/netdm_bridge/proc/request(list/fields)
    if(!ready || !islist(fields))
        return null
    var/op = fields["op"]
    if(framed && op != "hello")
        if(!owner_fence || reset_request_id)
            return null
        if(command_sequence >= 16000000)
            recover_session("sequence_budget")
            return null
        fields["streamId"] = stream_id
        fields["ownerFence"] = owner_fence
        fields["commandSequence"] = num2text(command_sequence + 1, 16)
    serial = (serial % 16000000) + 1
    var/id = "dm-[serial]"
    fields["id"] = id
    var/result = call_ext(NETDM_DLL, "netdm_send")(json_encode(fields))
    if(result != "1")
        return null
    if(framed)
        if(op == "hello")
            handshake_request_id = id
            session_deadline = world.time + 50
        else
            command_sequence++
            if(op == "game_reset" || op == "snapshot_ready")
                reset_request_id = id
                receipt_pending = FALSE
                receipt_request_id = null
                session_deadline = world.time + 50
    return id

/datum/netdm_bridge/v1/staged/proc/snapshot_begin(snapshot_id, chunk_count, fact_count, fact_sequence, profile = null)
    return request(list("op" = "snapshot_begin", "snapshotId" = snapshot_id,
        "chunkCount" = chunk_count, "factCount" = fact_count, "factSequence" = fact_sequence, "profile" = profile))

/datum/netdm_bridge/v1/staged/proc/snapshot_chunk(snapshot_id, chunk_index, facts_json, digest)
    return request(list("op" = "snapshot_chunk", "snapshotId" = snapshot_id,
        "chunkIndex" = chunk_index, "factsJson" = facts_json, "digest" = digest))

/datum/netdm_bridge/v1/staged/proc/snapshot_commit(snapshot_id, digest)
    return request(list("op" = "snapshot_commit", "snapshotId" = snapshot_id, "digest" = digest))

/datum/netdm_bridge/v1/staged/proc/snapshot_ready(snapshot_id, fact_sequence)
    return request(list("op" = "snapshot_ready", "snapshotId" = snapshot_id, "factSequence" = fact_sequence))

/// Stage a replacement for the APC-owned map without changing the runtime stream.
/datum/netdm_bridge/v1/staged/proc/area_update_begin(snapshot_id, update_id, chunk_count, fact_count, fact_sequence)
    return request(list("op" = "area_update_begin", "snapshotId" = snapshot_id, "updateId" = update_id,
        "chunkCount" = chunk_count, "factCount" = fact_count, "factSequence" = fact_sequence))

/datum/netdm_bridge/v1/staged/proc/area_update_chunk(snapshot_id, update_id, chunk_index, facts_json, digest)
    return request(list("op" = "area_update_chunk", "snapshotId" = snapshot_id, "updateId" = update_id,
        "chunkIndex" = chunk_index, "factsJson" = facts_json, "digest" = digest))

/datum/netdm_bridge/v1/staged/proc/area_update_commit(snapshot_id, update_id, digest)
    return request(list("op" = "area_update_commit", "snapshotId" = snapshot_id, "updateId" = update_id, "digest" = digest))

/datum/netdm_bridge/v1/staged/proc/world_delta(snapshot_id, fact_sequence, list/changes)
    return request(list("op" = "world_delta", "snapshotId" = snapshot_id,
        "factSequence" = fact_sequence, "changes" = changes))

/// Bounded diagnostic view of one level: nodes [id, x, y, flags], wired [a, b] and radio [device, partner].
/// Passing the last version back answers "unchanged" without the lists.
/datum/netdm_bridge/v1/staged/proc/topology_view(z, since_version = null, max_nodes = 2048)
    var/list/fields = list("op" = "topology_view", "z" = z, "maxNodes" = max_nodes)
    if(since_version)
        fields["sinceVersion"] = since_version
    return request(fields)

/datum/netdm_bridge/v1/staged/proc/heartbeat(snapshot_id, fact_sequence)
    return request(list("op" = "heartbeat", "snapshotId" = snapshot_id, "factSequence" = fact_sequence))

// P2 service control plane. Endpoint IDs and generations come from
// /datum/netdm_endpoint_registry; numbers that the Host treats as 64-bit
// (generation, sequences, delivery IDs) travel as decimal text.
/// domain is a world fact (station, a faction, a training zone) used by service policies.
/datum/netdm_bridge/v1/staged/proc/endpoint_attach(endpoint_id, generation, node_id, domain = "station")
    return request(list("op" = "endpoint_attach", "endpoint" = endpoint_id,
        "generation" = num2text(generation, 16), "a" = node_id, "domain" = domain))

/datum/netdm_bridge/v1/staged/proc/endpoint_detach(endpoint_id, generation)
    return request(list("op" = "endpoint_detach", "endpoint" = endpoint_id,
        "generation" = num2text(generation, 16)))

/// Scopes: "none", "domain" (default) or "open". A service hidden from discovery
/// answers like a missing one; the owner always has access.
/datum/netdm_bridge/v1/staged/proc/service_register(endpoint_id, generation, instance, service_type, schema, major, minor = 0, discover = "domain", subscribe = "domain", invoke = "domain")
    return request(list("op" = "service_register", "endpoint" = endpoint_id,
        "generation" = num2text(generation, 16), "instance" = instance, "serviceType" = service_type,
        "schema" = schema, "major" = major, "minor" = minor, "discoverScope" = discover,
        "subscribeScope" = subscribe, "invokeScope" = invoke))

/// Command to one instance. idempotency_key names the business operation: repeating
/// it never applies the command twice. The result arrives later as an inbox item.
/datum/netdm_bridge/v1/staged/proc/invoke(endpoint_id, generation, instance, command, idempotency_key, list/arguments, timeout_ticks = 50, interactive = TRUE)
    return request(list("op" = "invoke", "endpoint" = endpoint_id,
        "generation" = num2text(generation, 16), "instance" = instance, "command" = command,
        "idempotencyKey" = idempotency_key, "arguments" = arguments,
        "timeoutTicks" = timeout_ticks, "trafficClass" = interactive ? 1 : 0))

/// Own operation status only: outcome and result-leg codes, never the result payload.
/datum/netdm_bridge/v1/staged/proc/command_status(endpoint_id, generation, operation_id)
    return request(list("op" = "command_status", "endpoint" = endpoint_id,
        "generation" = num2text(generation, 16), "operationId" = operation_id))

/// Network scan: descriptors of reachable, visible providers arrive as result items.
/datum/netdm_bridge/v1/staged/proc/discover(endpoint_id, generation, service_type, scan_key, interactive = TRUE)
    return request(list("op" = "discover", "endpoint" = endpoint_id,
        "generation" = num2text(generation, 16), "serviceType" = service_type,
        "scanKey" = scan_key, "trafficClass" = interactive ? 1 : 0))

/// mode: "latest_value" or "queue" (max_pending 1..16).
/datum/netdm_bridge/v1/staged/proc/subscribe(endpoint_id, generation, key, instance, schema, major, projection, mode = "latest_value", max_pending = 1)
    return request(list("op" = "subscribe", "endpoint" = endpoint_id,
        "generation" = num2text(generation, 16), "key" = key, "instance" = instance,
        "schema" = schema, "major" = major, "projection" = projection, "mode" = mode,
        "maxPending" = max_pending))

/datum/netdm_bridge/v1/staged/proc/unsubscribe(endpoint_id, generation, key)
    return request(list("op" = "unsubscribe", "endpoint" = endpoint_id,
        "generation" = num2text(generation, 16), "key" = key))

/// Acceptance fixes the recipients. It is not delivery and not application.
/datum/netdm_bridge/v1/staged/proc/publish(endpoint_id, generation, instance, producer_sequence, list/payload, timeout_ticks = 30, interactive = TRUE)
    return request(list("op" = "publish", "endpoint" = endpoint_id,
        "generation" = num2text(generation, 16), "instance" = instance,
        "producerSequence" = num2text(producer_sequence, 16), "payload" = payload,
        "timeoutTicks" = timeout_ticks, "trafficClass" = interactive ? 1 : 0))

/// Non-destructive: entries stay until acknowledged.
/datum/netdm_bridge/v1/staged/proc/delivery_poll(after_sequence = "0", limit = 16)
    return request(list("op" = "delivery_poll", "afterSequence" = after_sequence, "limit" = limit))

/// acks: list(list("deliveryId" = text, "applied" = TRUE/FALSE, "reason" = text|null))
/datum/netdm_bridge/v1/staged/proc/delivery_ack(list/acks)
    return request(list("op" = "delivery_ack", "acks" = acks))

/datum/netdm_bridge/v1/staged/proc/delivery_status(delivery_id)
    return request(list("op" = "delivery_status", "deliveryId" = delivery_id))

/// Every delivery of one message with its recipient, state and reason.
/datum/netdm_bridge/v1/staged/proc/delivery_status_message(message_id)
    return request(list("op" = "delivery_status", "messageId" = message_id))

/// Research control plane (P5): DM facts about its research servers, answered by the Host ledger.
/// op is one of research_catalog, research_storage, research_income, research_grant, research_debit,
/// research_import, research_boost or research_state; payload carries its fields. Milli-points travel as text.
/datum/netdm_bridge/v1/staged/proc/research(op, list/payload)
    if(!istext(op) || copytext(op, 1, 10) != "research_" || !islist(payload))
        return null
    return request(list("op" = op, "payload" = payload))

/// P8 Shadow: analytic reach of a radio transmission (radio_reach); it changes nothing in the Host.
/datum/netdm_bridge/v1/staged/proc/radio(op, list/payload)
    if(op != "radio_reach" || !islist(payload))
        return null
    return request(list("op" = op, "payload" = payload))

/datum/netdm_bridge/proc/game_add_node(node_id, x, y, z = 0)
    return request(list("op" = "game_add_node", "a" = node_id, "x" = x, "y" = y, "z" = z))

/datum/netdm_bridge/proc/game_remove_node(node_id)
    return request(list("op" = "game_remove_node", "a" = node_id))

/datum/netdm_bridge/proc/game_move_node(node_id, x, y, z = 0)
    return request(list("op" = "game_move_node", "a" = node_id, "x" = x,
        "y" = y, "z" = z))

/datum/netdm_bridge/proc/game_connect(from_id, to_id, cost = 1)
    return request(list("op" = "game_connect", "a" = from_id, "b" = to_id,
        "cost" = cost))

/datum/netdm_bridge/proc/game_disconnect(from_id, to_id)
    return request(list("op" = "game_disconnect", "a" = from_id, "b" = to_id))

/datum/netdm_bridge/proc/game_batch(list/changes)
    return request(list("op" = "game_batch", "changes" = changes))

/datum/netdm_bridge/proc/game_stats()
    return request(list("op" = "game_stats"))

/datum/netdm_bridge/proc/game_reset()
    return request(list("op" = "game_reset"))

/datum/netdm_bridge/proc/game_radio(node_id, range = 14, channel = 1, enabled = TRUE)
    return request(list("op" = "game_radio", "a" = node_id,
        "range" = range, "channel" = channel, "enabled" = !!enabled))

/datum/netdm_bridge/proc/packet_send(from_id, to_id, bytes, request_id, interactive = TRUE, timeout_ticks = 30)
    return request(list("op" = "packet_send", "a" = from_id,
        "b" = to_id, "bytes" = bytes, "requestId" = request_id,
        "trafficClass" = interactive ? 1 : 0, "timeoutTicks" = timeout_ticks))

/datum/netdm_bridge/proc/packet_cancel(packet_id)
    if(!stream_id)
        return null
    return request(list("op" = "packet_cancel", "packetId" = packet_id,
        "streamId" = stream_id))

/// Request a bounded page using the cursor last processed in DM.
/// One poll in flight, preventing accidental buildup when the server is slow.
/datum/netdm_bridge/proc/request_receipts(limit = 64)
    if(!ready || receipt_pending)
        return null
    var/cursor = receipt_cursor
    if(framed && !istext(cursor))
        cursor = "0"
    var/list/q = list("op" = "packet_receipts",
        "afterSequence" = cursor, "limit" = min(max(limit, 1), framed ? 32 : 256))
    if(stream_id)
        q["streamId"] = stream_id
    var/id = request(q)
    if(id)
        receipt_pending = TRUE
        receipt_request_id = id
        receipt_deadline = world.time + 50
    return id

/// Invoke from an SS13 subsystem at a bounded interval (e.g. every tick).
/// Returns Host responses to the game subsystem; none of these are blocking.
/// Never silently treat disconnection/gap/unknown result as delivered.
/datum/netdm_bridge/proc/pump(max_messages = 32)
    var/list/results = list()
    if(!ready)
        return results
    var/list/status = json_decode(call_ext(NETDM_DLL, "netdm_status")())
    if(islist(status) && status["lostResponses"] > last_lost_responses)
        last_lost_responses = status["lostResponses"]
        recover_session("native_mailbox_overflow")
        if(framed)
            return results
    if((handshake_request_id || reset_request_id) && world.time >= session_deadline)
        recover_session("session_timeout")
        return results
    if(receipt_pending && world.time >= receipt_deadline)
        receipt_pending = FALSE
        receipt_request_id = null
        on_resync_required("receipt_timeout")
    var/bytes = 0
    for(var/i = 1, i <= min(max(max_messages, 1), 256), i++)
        // A single frame is at most 48 KiB. Leave room for the next one.
        if(framed && bytes >= 16 * 1024)
            break
        var/raw = call_ext(NETDM_DLL, "netdm_poll")()
        if(!length(raw))
            break
        bytes += length(raw)
        var/list/message = json_decode(raw)
        if(!islist(message))
            continue
        if(message["bridge"] == "disconnected")
            connected = FALSE
            clear_session()
            if(framed)
                results.Cut()
            on_connection_lost()
        else if(message["bridge"] == "connected")
            connected = TRUE
            clear_session()

        if(reset_request_id && !message["bridge"] && message["id"] != reset_request_id)
            dropped++
            last_dropped = copytext(raw, 1, 240)
            continue
        var/hello_reply = handshake_request_id && message["id"] == handshake_request_id
        if(hello_reply)
            handshake_request_id = null
            var/list/session = message["session"]
            if(message["ok"] && islist(session) && session["protocolMajor"] == 1 && session["protocolMinor"] == protocol_minor)
                owner_fence = session["ownerFence"]
                stream_id = message["streamId"]
        if(reset_request_id && message["id"] == reset_request_id)
            reset_request_id = null
            if(message["ok"] && message["streamId"])
                stream_id = message["streamId"]
                receipt_cursor = "0"
        var/expected_poll = receipt_request_id && message["id"] == receipt_request_id
        if(expected_poll)
            receipt_pending = FALSE
            receipt_request_id = null
        var/current_stream = owner_fence && stream_id && (message["error"] == "stream_changed" || stream_id == message["streamId"])
        if(framed && !message["bridge"] && !hello_reply && !current_stream)
            dropped++
            last_dropped = copytext(raw, 1, 240)
            continue
        if(framed && (message["error"] in list("stream_changed", "sequence_mismatch", "owner_required", "invalid_json")))
            recover_session(message["error"])
            return list()
        var/list/submission = message["submission"]
        if(islist(submission))
            stream_id = submission["streamId"]
            on_packet_submitted(submission)
        var/list/page = message["receiptPage"]
        if(islist(page) && expected_poll)
            var/new_stream = page["streamId"]
            if(stream_id && stream_id != new_stream)
                on_resync_required("stream_changed")
                receipt_cursor = 0
            stream_id = new_stream
            if(page["gap"])
                if(framed)
                    recover_session("receipt_gap")
                    return list()
                on_resync_required("receipt_gap")
            else
                var/list/items = page["receipts"]
                if(islist(items))
                    for(var/list/receipt in items)
                        on_packet_receipt(receipt)
                receipt_cursor = page["nextSequence"]
        if(message["error"] == "stream_changed")
            on_resync_required("stream_changed")
            receipt_pending = FALSE
            stream_id = null
            receipt_cursor = 0
        results += list(message)
    return results

/// Override these callbacks in the project adapter. Never execute a game
/// action solely because a transport receipt says 'delivered': it is a
/// simulated final hop, not an application-level ACK from the target.
/datum/netdm_bridge/proc/on_packet_submitted(list/submission)
    return

/datum/netdm_bridge/proc/on_packet_receipt(list/receipt)
    return

/datum/netdm_bridge/proc/on_connection_lost()
    return

/datum/netdm_bridge/proc/on_resync_required(reason)
    return
