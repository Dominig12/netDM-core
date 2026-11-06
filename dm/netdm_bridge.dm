// A standalone BYOND DM adapter for the classic call_ext() string ABI.
// Include in a .dme; the x86 DLL must be trusted by DreamDaemon.
// All native operations are local bounded queue accesses, never pipe I/O.
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

/datum/netdm_bridge/proc/initialize()
    ready = (call_ext(NETDM_DLL, "netdm_init")() == "1")
    return ready

/datum/netdm_bridge/proc/stop()
    if(ready)
        call_ext(NETDM_DLL, "netdm_shutdown")()
    ready = FALSE

/// Send an associative JSON object; returns the pipe command id on successful
/// SHIM QUEUE acceptance (not Host acceptance or physical delivery).
/datum/netdm_bridge/proc/request(list/fields)
    if(!ready || !islist(fields))
        return null
    serial++
    var/id = "dm-[serial]"
    fields["id"] = id
    var/result = call_ext(NETDM_DLL, "netdm_send")(json_encode(fields))
    if(result != "1")
        return null
    return id

/datum/netdm_bridge/proc/game_add_node(node_id, x, y, z = 0)
    return request(list("op" = "game_add_node", "a" = node_id, "x" = x, "y" = y, "z" = z))

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

/// Request a bounded page (max 256) using the cursor last confirmed in DM.
/// One poll in flight, preventing accidental buildup when the server is slow.
/datum/netdm_bridge/proc/request_receipts(limit = 64)
    if(!ready || receipt_pending)
        return null
    var/list/q = list("op" = "packet_receipts",
        "afterSequence" = receipt_cursor, "limit" = min(max(limit, 1), 256))
    if(stream_id)
        q["streamId"] = stream_id
    var/id = request(q)
    if(id)
        receipt_pending = TRUE
        receipt_request_id = id
    return id

/// Invoke from an SS13 subsystem at a bounded interval (e.g. every tick).
/// Returns Host responses to the game subsystem; none of these are blocking.
/// Never silently treat disconnection/gap/unknown result as delivered.
/datum/netdm_bridge/proc/pump(max_messages = 32)
    var/list/results = list()
    if(!ready)
        return results
    for(var/i = 1, i <= min(max(max_messages, 1), 256), i++)
        var/raw = call_ext(NETDM_DLL, "netdm_poll")()
        if(!length(raw))
            break
        var/list/message = json_decode(raw)
        if(!islist(message))
            continue
        if(message["bridge"] == "disconnected")
            stream_id = null
            receipt_cursor = 0
            receipt_pending = FALSE
            receipt_request_id = null
            on_connection_lost()
        else if(message["bridge"] == "connected")
            receipt_pending = FALSE
            receipt_request_id = null
            stream_id = null
            receipt_cursor = 0

        if(message["id"] == receipt_request_id)
            receipt_pending = FALSE
            receipt_request_id = null
        var/list/submission = message["submission"]
        if(islist(submission))
            stream_id = submission["streamId"]
            on_packet_submitted(submission)
        var/list/page = message["receiptPage"]
        if(islist(page))
            var/new_stream = page["streamId"]
            if(stream_id && stream_id != new_stream)
                on_resync_required("stream_changed")
                receipt_cursor = 0
            stream_id = new_stream
            if(page["gap"])
                on_resync_required("receipt_gap")
            receipt_cursor = page["nextSequence"]
            var/list/items = page["receipts"]
            if(islist(items))
                for(var/list/receipt in items)
                    on_packet_receipt(receipt)
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
