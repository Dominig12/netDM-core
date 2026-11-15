// P2 DM side of the service network: round-scoped endpoint identity and an
// allowlisted effect dispatcher. Include after netdm_bridge.dm.
//
// The Host only ever sees "endpoint ID + generation". It never receives a DM
// reference, a type path or a proc name, and DM never executes anything that the
// Host names: deliveries are mapped to handlers registered here by schema.

/// Round-scoped endpoint identity. IDs are never reused within a round and survive
/// Host reconnects, so a durable service can recognise the same receiver again.
/// A generation increases only when an existing ID is rebound to another object.
/datum/netdm_endpoint_registry
    var/prefix = "e"
    var/next_id = 0
    var/limit = 65536
    /// id -> stored reference (see make_ref)
    var/list/refs = list()
    /// id -> generation number
    var/list/generations = list()

/// Override in a game to store a weak reference (for example WEAKREF) instead.
/datum/netdm_endpoint_registry/proc/make_ref(datum/thing)
    return thing

/datum/netdm_endpoint_registry/proc/resolve_ref(stored)
    return stored

/// Returns a new endpoint ID with generation 1, or null when the round budget is spent.
/datum/netdm_endpoint_registry/proc/register(datum/thing, id_prefix)
    if(!thing || next_id >= limit)
        return null
    var/id = "[id_prefix || prefix][++next_id]"
    refs[id] = make_ref(thing)
    generations[id] = 1
    return id

/// The same logical endpoint now lives in another object. Old deliveries are fenced.
/datum/netdm_endpoint_registry/proc/rebind(id, datum/thing)
    if(!thing || !(id in generations))
        return null
    refs[id] = make_ref(thing)
    generations[id] = generations[id] + 1
    return generations[id]

/// The ID is retired for the rest of the round.
/datum/netdm_endpoint_registry/proc/retire(id)
    refs -= id
    generations -= id

/datum/netdm_endpoint_registry/proc/generation_of(id)
    return generations[id]

/// The live object for exactly this ID and generation, or null.
/datum/netdm_endpoint_registry/proc/resolve(id, generation)
    if(!istext(id) || !(id in generations))
        return null
    if(istext(generation))
        generation = text2num(generation)
    if(generation != generations[id])
        return null
    return resolve_ref(refs[id])

/// Applies one delivered event or result to a live DM object. Return TRUE when
/// applied, or a short reason text ("[a-z0-9_.-]") when the world rejects it. Must not yield.
/datum/netdm_effect_handler/proc/apply(datum/target, list/item)
    return "not_implemented"

/// Executes one command on the provider's live object after DM's own guards.
/// Return list("result" = list(...typed result fields...)) when applied, or a
/// short reason text when the world refuses. Must not yield. Prefer idempotent set_state commands.
/datum/netdm_command_handler/proc/execute(datum/target, list/item)
    return "not_implemented"

/// Maps Host deliveries to allowlisted handlers, deduplicates by delivery ID and
/// prepares application ACKs. The Host keeps entries until ACK, so a lost poll
/// reply only means the same items come again; they are never applied twice.
/datum/netdm_effect_dispatcher
    /// event schema ID -> /datum/netdm_effect_handler
    var/list/handlers = list()
    /// "serviceType/command" -> /datum/netdm_command_handler
    var/list/command_handlers = list()
    /// "serviceType/command" or command name -> /datum/netdm_effect_handler for results returned to an initiator
    var/list/result_handlers = list()
    /// delivery ID -> list("applied", "reason", "result"), bounded FIFO
    var/list/outcomes = list()
    var/outcome_limit = 1024
    /// Inbox cursor of the last processed page, as Host decimal text.
    var/cursor = "0"
    /// Highest processed sequence. A replay may move cursor back, never this watermark.
    var/processed_through = "0"

/datum/netdm_effect_dispatcher/proc/allow(schema, datum/netdm_effect_handler/handler)
    handlers[schema] = handler

/datum/netdm_effect_dispatcher/proc/allow_command(service_type, command, datum/netdm_command_handler/handler)
    command_handlers["[service_type]/[command]"] = handler

/// Two services may share a command name ("status"): give service_type to keep their results apart.
/// A handler without a service type takes the results of every service with that command.
/datum/netdm_effect_dispatcher/proc/allow_result(command, datum/netdm_effect_handler/handler, service_type = null)
    result_handlers[service_type ? "[service_type]/[command]" : command] = handler

/// Call when the Host runtime (stream) changes. Delivery IDs belong to one runtime
/// and restart in the next, so the cursor and the dedup state are cleared together.
/datum/netdm_effect_dispatcher/proc/reset_cursor()
    cursor = "0"
    processed_through = "0"
    outcomes.Cut()

/// Compare canonical nonnegative decimal strings without rounding them to DM numbers.
/datum/netdm_effect_dispatcher/proc/is_later_sequence(sequence, previous)
    if(length(sequence) != length(previous))
        return length(sequence) > length(previous)
    for(var/i = 1 to length(sequence))
        var/digit = text2ascii(sequence, i)
        var/previous_digit = text2ascii(previous, i)
        if(digit != previous_digit)
            return digit > previous_digit
    return FALSE

/// Processes one delivery_poll page and returns the list of ACK entries to send.
/datum/netdm_effect_dispatcher/proc/handle_page(list/page, datum/netdm_endpoint_registry/registry)
    var/list/acks = list()
    if(!islist(page))
        return acks
    var/list/items = page["items"]
    if(islist(items))
        for(var/list/item in items)
            var/delivery_id = item["deliveryId"]
            var/sequence = item["sequence"]
            if(!istext(delivery_id) || !istext(sequence))
                continue
            var/list/outcome = outcomes[delivery_id]
            if(!islist(outcome))
                // No cached outcome after eviction. Leave it unacknowledged instead of applying again.
                if(!is_later_sequence(sequence, processed_through))
                    continue
                processed_through = sequence
                outcome = apply_item(item, registry)
                outcomes[delivery_id] = outcome
                if(length(outcomes) > outcome_limit)
                    outcomes.Cut(1, length(outcomes) - outcome_limit + 1)
            var/list/ack = list("deliveryId" = delivery_id, "applied" = outcome["applied"], "reason" = outcome["reason"])
            if(islist(outcome["result"]))
                ack["result"] = outcome["result"]
            acks += list(ack)
    if(istext(page["nextSequence"]))
        cursor = page["nextSequence"]
    return acks

/datum/netdm_effect_dispatcher/proc/apply_item(list/item, datum/netdm_endpoint_registry/registry)
    var/datum/target = registry.resolve(item["recipient"], item["recipientGeneration"])
    var/kind = item["kind"]
    if(kind == "command")
        var/datum/netdm_command_handler/command_handler = command_handlers["[item["serviceType"]]/[item["command"]]"]
        if(!command_handler)
            return list("applied" = FALSE, "reason" = "unsupported_command")
        if(!target)
            return list("applied" = FALSE, "reason" = "receiver_gone")
        var/executed = command_handler.execute(target, item)
        if(islist(executed))
            var/list/result = executed["result"]
            return list("applied" = TRUE, "result" = islist(result) ? result : null)
        return list("applied" = FALSE, "reason" = istext(executed) ? executed : "rejected")
    var/datum/netdm_effect_handler/handler = kind == "result" \
        ? (result_handlers["[item["serviceType"]]/[item["command"]]"] || result_handlers[item["command"]]) : handlers[item["schema"]]
    if(!handler)
        return list("applied" = FALSE, "reason" = "unsupported_schema")
    if(!target)
        return list("applied" = FALSE, "reason" = "receiver_gone")
    var/applied = handler.apply(target, item)
    if(applied == TRUE)
        return list("applied" = TRUE)
    return list("applied" = FALSE, "reason" = istext(applied) ? applied : "rejected")
