namespace NetRun.Core;

/// <summary>
/// What one step of a program does (§7.1, §11.3). The vocabulary is deliberately tiny: a program can invoke
/// a registered action, wait, repeat a bounded body, emit an event or stop. There is no expression language
/// and no loop that a user could make unbounded.
/// </summary>
public enum StepKind : byte
{
    /// <summary>Invoke a registered action as the program's owner. Rights, cost and cooldown all apply.</summary>
    Invoke,
    /// <summary>Suspend until game time passes; the worker is released while waiting.</summary>
    Wait,
    /// <summary>Run the body a fixed number of times. Nested repeats multiply and are bounded by the definition.</summary>
    Repeat,
    /// <summary>Record a program event for the owner; never a way to change the world.</summary>
    Emit,
    /// <summary>End the program.</summary>
    Stop
}

public sealed record ProgramStep(
    StepKind Kind,
    string ActionId = "",
    HostId Target = default,
    long WaitMilliseconds = 0,
    int Count = 1,
    string Topic = "",
    IReadOnlyList<ProgramStep>? Body = null)
{
    public static ProgramStep Invoke(string actionId, HostId target) => new(StepKind.Invoke, actionId, target);

    public static ProgramStep Wait(long milliseconds) => new(StepKind.Wait, WaitMilliseconds: milliseconds);

    public static ProgramStep Repeat(int count, params ProgramStep[] body) => new(StepKind.Repeat, Count: count, Body: body);

    public static ProgramStep Emit(string topic) => new(StepKind.Emit, Topic: topic);

    public static ProgramStep Stop() => new(StepKind.Stop);
}

public enum ProgramState : byte { Running, Waiting, WaitingForOperation, Completed, Faulted, Cancelled }

public enum ProgramStatus : byte
{
    Ok, Duplicate, InvalidDefinition, LimitExceeded, UnknownProgram, InstructionBudget, DepthBudget,
    StepBudget, NotRunning
}

/// <summary>A validated program: the steps were checked for depth, size and bounds before it was accepted.</summary>
public sealed record ProgramDefinition(
    string Id,
    HostId ExecutionHost,
    IReadOnlyList<ProgramStep> Steps,
    int InstructionBudget = 512,
    int MaxRunMilliseconds = 600_000);

public sealed record ProgramSnapshot(
    ProcessId Process,
    PrincipalId Owner,
    string DefinitionId,
    ProgramState State,
    int InstructionCount,
    int CompletedRuns,
    string? Fault,
    string? LastAction,
    string? LastOutcome);

public sealed record ProgramResult(ProgramStatus Status, ProcessId Process, string? Reason = null);

public sealed record ProgramOptions(
    int MaxPrograms = 4_096,
    int MaxProgramsPerOwner = 16,
    int InstructionsPerTick = 256,
    int MaxDepth = 6,
    int MaxSteps = 256,
    int MaxRepeatCount = 1_000,
    long MaxWaitMilliseconds = 300_000);

/// <summary>
/// The bounded executor of §17.3 and SCRIPT-002. A program gets an instruction budget per tick, runs through
/// the ordinary action pipeline as its owner, and is stopped by limits rather than by a watchdog killing a
/// thread. Nothing here interprets text, loads code or reaches outside the domain.
/// </summary>
public sealed class ProgramScheduler(ISimulationClock clock, ActionPipeline actions, ProgramOptions? options = null)
{
    private sealed class Frame(IReadOnlyList<ProgramStep> steps, int remaining)
    {
        public readonly IReadOnlyList<ProgramStep> Steps = steps;
        public int Index;
        public int Remaining = remaining;
    }

    private sealed class Instance(ProcessId id, PrincipalId owner, ProgramDefinition definition, long createdAt)
    {
        public readonly ProcessId Id = id;
        public readonly PrincipalId Owner = owner;
        public readonly ProgramDefinition Definition = definition;
        public readonly long CreatedAt = createdAt;
        public readonly List<Frame> Frames = [new(definition.Steps, 1)];
        public ProgramState State = ProgramState.Running;
        public int Instructions;
        public int CompletedRuns;
        public long ResumeAt;
        public long StartedAt = createdAt;
        public string? Fault;
        public string? LastAction;
        public string? LastOutcome;
        public int StepCounter;
    }

    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ActionPipeline _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    private readonly ProgramOptions _options = options ?? new ProgramOptions();
    private readonly Dictionary<ulong, Instance> _programs = [];
    private readonly Dictionary<ulong, int> _perOwner = [];
    private ulong _nextProcess;

    public int Count => _programs.Count;

    /// <summary>
    /// Accepts a program. The definition is validated here, once: step count, nesting depth, repeat sizes and
    /// wait lengths are all bounded, so no later execution can surprise the simulation.
    /// </summary>
    public ProgramResult Start(PrincipalId owner, ProgramDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (owner.IsNone || string.IsNullOrEmpty(definition.Id) || definition.Id.Length > 64)
            return new ProgramResult(ProgramStatus.InvalidDefinition, ProcessId.None, "owner_or_id");
        if (definition.Steps.Count == 0 || definition.Steps.Count > _options.MaxSteps)
            return new ProgramResult(ProgramStatus.InvalidDefinition, ProcessId.None, "steps");
        if (!Validate(definition.Steps, depth: 1, out var reason))
            return new ProgramResult(ProgramStatus.InvalidDefinition, ProcessId.None, reason);
        if (_programs.Count >= _options.MaxPrograms ||
            _perOwner.GetValueOrDefault(owner.Value) >= _options.MaxProgramsPerOwner)
            return new ProgramResult(ProgramStatus.LimitExceeded, ProcessId.None, "programs");

        var id = new ProcessId(++_nextProcess);
        var instance = new Instance(id, owner, definition, _clock.NowMilliseconds);
        _programs[id.Value] = instance;
        _perOwner[owner.Value] = _perOwner.GetValueOrDefault(owner.Value) + 1;
        return new ProgramResult(ProgramStatus.Ok, id);
    }

    private bool Validate(IReadOnlyList<ProgramStep> steps, int depth, out string? reason)
    {
        reason = null;
        if (depth > _options.MaxDepth)
        {
            reason = "depth";
            return false;
        }
        foreach (var step in steps)
        {
            if (!Enum.IsDefined(step.Kind))
            {
                reason = "kind";
                return false;
            }
            switch (step.Kind)
            {
                case StepKind.Invoke when string.IsNullOrEmpty(step.ActionId) || step.ActionId.Length > 96:
                    reason = "invoke";
                    return false;
                case StepKind.Wait when step.WaitMilliseconds <= 0 || step.WaitMilliseconds > _options.MaxWaitMilliseconds:
                    reason = "wait";
                    return false;
                case StepKind.Repeat when step.Count is < 1 or > 1_000 || step.Body is not { Count: > 0 }:
                    reason = "repeat";
                    return false;
                case StepKind.Repeat:
                    if (!Validate(step.Body!, depth + 1, out reason))
                        return false;
                    break;
                case StepKind.Emit when string.IsNullOrEmpty(step.Topic) || step.Topic.Length > 64:
                    reason = "emit";
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Runs one tick of every ready program under a shared instruction budget. Programs are served in
    /// identifier order, so one busy program cannot starve the rest: the budget simply runs out evenly.
    /// </summary>
    public int Advance(long nowMilliseconds)
    {
        var budget = _options.InstructionsPerTick;
        var executed = 0;
        foreach (var instance in _programs.Values.OrderBy(instance => instance.Id.Value).ToArray())
        {
            if (budget <= 0)
                break;
            if (instance.State == ProgramState.Waiting)
            {
                if (instance.ResumeAt > nowMilliseconds)
                    continue;
                instance.State = ProgramState.Running;
            }
            if (instance.State != ProgramState.Running)
                continue;
            executed += Run(instance, ref budget, nowMilliseconds);
        }
        return executed;
    }

    private int Run(Instance instance, ref int budget, long now)
    {
        var executed = 0;
        while (budget > 0 && instance.State == ProgramState.Running)
        {
            if (instance.Instructions >= instance.Definition.InstructionBudget)
            {
                Fault(instance, "instruction_budget");
                break;
            }
            if (now - instance.StartedAt > instance.Definition.MaxRunMilliseconds)
            {
                Fault(instance, "run_timeout");
                break;
            }
            budget--;
            executed++;
            instance.Instructions++;
            if (!Step(instance, now))
                break;
        }
        return executed;
    }

    /// <summary>Executes the next step. Returns false when the program stopped, waited or faulted.</summary>
    private bool Step(Instance instance, long now)
    {
        if (instance.Frames.Count == 0)
        {
            instance.State = ProgramState.Completed;
            instance.CompletedRuns++;
            return false;
        }
        var frame = instance.Frames[^1];
        if (frame.Index >= frame.Steps.Count)
        {
            instance.Frames.RemoveAt(instance.Frames.Count - 1);
            frame.Remaining--;
            if (frame.Remaining > 0)
            {
                frame.Index = 0;
                instance.Frames.Add(frame);
            }
            return instance.Frames.Count > 0 || instance.State == ProgramState.Running;
        }
        var step = frame.Steps[frame.Index++];
        switch (step.Kind)
        {
            case StepKind.Wait:
                instance.State = ProgramState.Waiting;
                instance.ResumeAt = now + step.WaitMilliseconds;
                return false;
            case StepKind.Repeat:
                instance.Frames.Add(new Frame(step.Body!, step.Count));
                return true;
            case StepKind.Emit:
                instance.LastAction = $"emit:{step.Topic}";
                instance.LastOutcome = "ok";
                return true;
            case StepKind.Stop:
                instance.State = ProgramState.Completed;
                instance.CompletedRuns++;
                return false;
            case StepKind.Invoke:
                return Invoke(instance, step, now);
            default:
                Fault(instance, "unknown_step");
                return false;
        }
    }

    private bool Invoke(Instance instance, ProgramStep step, long now)
    {
        var action = _actions.GetAction(step.ActionId);
        if (action is null)
        {
            Fault(instance, "unknown_action");
            return false;
        }
        instance.LastAction = step.ActionId;
        var key = $"prog-{instance.Id.Value}-{++instance.StepCounter}";
        var request = new ActionRequest(instance.Owner, instance.Definition.ExecutionHost, step.Target,
            EntityId.None, instance.Id, CorrelationId.None, key);
        var outcome = _actions.Execute(request, action);
        instance.LastOutcome = outcome.Status.ToString().ToLowerInvariant();
        switch (outcome.Status)
        {
            case ActionStatus.Accepted:
                instance.State = ProgramState.WaitingForOperation;
                instance.ResumeAt = now;
                return false;
            case ActionStatus.Committed:
            case ActionStatus.Prepared:
            case ActionStatus.Duplicate:
                return true;
            default:
                // A refused action ends the program: continuing after a refusal would hide why it stopped.
                Fault(instance, outcome.Reason ?? outcome.Status.ToString().ToLowerInvariant());
                return false;
        }
    }

    /// <summary>
    /// Completes a program that waited for a remote operation. A lost answer is reported as such and is never
    /// turned into a success (SVC-002).
    /// </summary>
    public ProgramResult CompleteOperation(ProcessId process, ActionStatus status, string? reason = null)
    {
        if (!_programs.TryGetValue(process.Value, out var instance))
            return new ProgramResult(ProgramStatus.UnknownProgram, process);
        if (instance.State != ProgramState.WaitingForOperation)
            return new ProgramResult(ProgramStatus.NotRunning, process, "not_waiting");
        instance.LastOutcome = status.ToString().ToLowerInvariant();
        if (status == ActionStatus.Committed)
        {
            instance.State = ProgramState.Running;
            return new ProgramResult(ProgramStatus.Ok, process);
        }
        Fault(instance, reason ?? status.ToString().ToLowerInvariant());
        return new ProgramResult(ProgramStatus.Ok, process, instance.Fault);
    }

    public ProgramResult Cancel(ProcessId process)
    {
        if (!_programs.TryGetValue(process.Value, out var instance))
            return new ProgramResult(ProgramStatus.UnknownProgram, process);
        Carrying(instance);
        instance.State = ProgramState.Cancelled;
        _programs.Remove(process.Value);
        return new ProgramResult(ProgramStatus.Ok, process);
    }

    /// <summary>Cancels everything one principal owns, for example when its session ends.</summary>
    public int CancelOwner(PrincipalId owner)
    {
        var cancelled = 0;
        foreach (var instance in _programs.Values.ToArray())
        {
            if (instance.Owner != owner)
                continue;
            _programs.Remove(instance.Id.Value);
            Carrying(instance);
            cancelled++;
        }
        return cancelled;
    }

    /// <summary>Cancels programs that ran on a destroyed host: their launchpad is gone.</summary>
    public int CancelHost(HostId host)
    {
        var cancelled = 0;
        foreach (var instance in _programs.Values.ToArray())
        {
            if (instance.Definition.ExecutionHost != host)
                continue;
            _programs.Remove(instance.Id.Value);
            Carrying(instance);
            cancelled++;
        }
        return cancelled;
    }

    /// <summary>Removes finished programs from the table and returns how many were dropped.</summary>
    public int Reap()
    {
        var removed = 0;
        foreach (var instance in _programs.Values.ToArray())
        {
            if (instance.State is not (ProgramState.Completed or ProgramState.Faulted or ProgramState.Cancelled))
                continue;
            _programs.Remove(instance.Id.Value);
            Carrying(instance);
            removed++;
        }
        return removed;
    }

    public ProgramSnapshot? Peek(ProcessId process) =>
        _programs.TryGetValue(process.Value, out var instance)
            ? new ProgramSnapshot(instance.Id, instance.Owner, instance.Definition.Id, instance.State,
                instance.Instructions, instance.CompletedRuns, instance.Fault, instance.LastAction,
                instance.LastOutcome)
            : null;

    /// <summary>Bounded listing for the client, newest last.</summary>
    public ProgramSnapshot[] All(int limit)
    {
        var count = Math.Min(Math.Max(limit, 0), _programs.Count);
        var result = new ProgramSnapshot[count];
        var index = 0;
        foreach (var instance in _programs.Values.OrderBy(instance => instance.Id.Value))
        {
            if (index >= count)
                break;
            result[index++] = new ProgramSnapshot(instance.Id, instance.Owner, instance.Definition.Id,
                instance.State, instance.Instructions, instance.CompletedRuns, instance.Fault,
                instance.LastAction, instance.LastOutcome);
        }
        return result;
    }

    private void Fault(Instance instance, string reason)
    {
        instance.State = ProgramState.Faulted;
        instance.Fault = reason;
    }

    private void Carrying(Instance instance)
    {
        if (_perOwner.TryGetValue(instance.Owner.Value, out var count) && count > 0)
            _perOwner[instance.Owner.Value] = count - 1;
    }
}
