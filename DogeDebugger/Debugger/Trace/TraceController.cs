using DogeDebugger.Core.Trace;
using DogeDebugger.Debugger.Session;
using DogeDebugger.Debugger.UserMode;
using MoonSharp.Interpreter;

namespace DogeDebugger.Debugger.Trace;

public sealed class TraceController
{
    private const int MaximumStepCount = 10_000_000;
    private readonly DebuggerSession _session;
    private readonly SemaphoreSlim _advanceGate = new(1, 1);
    private bool _stopRequested;

    public TraceController(DebuggerSession session)
    {
        _session = session;
        _session.Paused += OnPaused;
    }

    public TraceSession? CurrentSession { get; private set; }

    public bool IsTracing { get; private set; }

    public event Action<int, int>? ProgressChanged;

    public event Action<TraceSession>? Completed;

    public async Task<string?> StartAsync(
        TraceStopCondition condition,
        TraceMode mode)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (IsTracing)
        {
            return "追踪已在进行中。";
        }

        if (!_session.Target.IsOpen)
        {
            return "请先打开一个进程。";
        }

        if (!_session.Debugger.IsDebugging || !_session.Debugger.IsPaused)
        {
            return "目标进程必须处于暂停状态才能开始追踪。";
        }

        if (condition.Type == TraceStopType.StepCount &&
            condition.MaxSteps is < 1 or > MaximumStepCount)
        {
            return $"请输入 1~{MaximumStepCount} 之间的步数。";
        }

        if (condition.Type == TraceStopType.RipMatch && condition.TargetRip == 0)
        {
            return "请输入有效的目标 RIP。";
        }

        if (condition.Type == TraceStopType.LuaCondition &&
            string.IsNullOrWhiteSpace(condition.LuaCondition))
        {
            return "Lua 条件不能为空。";
        }

        CurrentSession = new TraceSession
        {
            Id = Guid.NewGuid().ToString("N"),
            StartTime = DateTime.Now,
            Mode = mode,
            StopCondition = CloneCondition(condition)
        };
        _stopRequested = false;
        IsTracing = true;
        await AdvanceAsync(_session.Debugger.GetCurrentRegisters()).ConfigureAwait(false);
        return null;
    }

    public async Task StopAsync()
    {
        if (!IsTracing)
        {
            return;
        }

        _stopRequested = true;
        if (_session.Debugger.IsPaused)
        {
            Complete("UserCancel");
            return;
        }

        await _session.Debugger.BreakAsync().ConfigureAwait(false);
    }

    private void OnPaused(object? sender, DebuggerPausedEventArgs args)
    {
        if (IsTracing)
        {
            _ = AdvanceAsync(args.Registers);
        }
    }

    private async Task AdvanceAsync(RegisterSnapshot registers)
    {
        await _advanceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsTracing || CurrentSession is null)
            {
                return;
            }

            if (_stopRequested)
            {
                Complete("UserCancel");
                return;
            }

            TraceStep step = CreateStep(registers);
            CurrentSession.Steps.Add(step);
            ProgressChanged?.Invoke(
                CurrentSession.Steps.Count,
                CurrentSession.StopCondition.Type == TraceStopType.StepCount
                    ? CurrentSession.StopCondition.MaxSteps
                    : -1);

            if (TryGetStopReason(step, out string stopReason, out string? errorMessage))
            {
                Complete(stopReason, errorMessage);
                return;
            }

            bool continued = CurrentSession.Mode == TraceMode.StepInto
                ? await _session.Debugger.StepIntoAsync().ConfigureAwait(false)
                : await _session.Debugger.StepOverAsync().ConfigureAwait(false);
            if (!continued)
            {
                Complete("DebuggerUnavailable");
            }
        }
        catch (Exception exception)
        {
            Complete("TraceError", exception.Message);
        }
        finally
        {
            _advanceGate.Release();
        }
    }

    private TraceStep CreateStep(RegisterSnapshot registers)
    {
        string? disassembly = _session
            .Disassemble(registers.InstructionPointer, 1)
            .FirstOrDefault()
            ?.Text;
        Dictionary<string, ulong> values =
            new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, ulong value) in registers.GeneralPurposeRegisters)
        {
            values[name] = value;
        }

        values["RIP"] = registers.InstructionPointer;
        values["RSP"] = registers.StackPointer;
        values["RBP"] = registers.FramePointer;
        values["EFLAGS"] = registers.Flags;
        return new TraceStep
        {
            StepIndex = CurrentSession?.Steps.Count ?? 0,
            Rip = registers.InstructionPointer,
            DisassemblyText = disassembly,
            Registers = values
        };
    }

    private bool TryGetStopReason(
        TraceStep step,
        out string stopReason,
        out string? errorMessage)
    {
        stopReason = string.Empty;
        errorMessage = null;
        TraceSession session = CurrentSession!;
        if (session.Steps.Count >= MaximumStepCount)
        {
            stopReason = "ForcedStop";
            return true;
        }

        switch (session.StopCondition.Type)
        {
            case TraceStopType.StepCount:
                if (session.Steps.Count >= session.StopCondition.MaxSteps)
                {
                    stopReason = "StepCount";
                    return true;
                }

                break;

            case TraceStopType.RipMatch:
                if (session.Steps.Count > 1 &&
                    step.Rip == session.StopCondition.TargetRip)
                {
                    stopReason = "RipMatch";
                    return true;
                }

                break;

            case TraceStopType.LuaCondition:
                if (!TryEvaluateLua(step, out bool matched, out errorMessage))
                {
                    stopReason = "LuaConditionError";
                    return true;
                }

                if (matched)
                {
                    stopReason = "LuaCondition";
                    return true;
                }

                break;
        }

        return false;
    }

    private bool TryEvaluateLua(
        TraceStep step,
        out bool matched,
        out string? errorMessage)
    {
        matched = false;
        errorMessage = null;
        try
        {
            Script script = new(CoreModules.Preset_HardSandbox);
            foreach ((string name, ulong value) in step.Registers)
            {
                script.Globals[name] = value;
            }

            DynValue result = script.DoString(
                $"return ({CurrentSession!.StopCondition.LuaCondition})");
            matched = result.Type switch
            {
                DataType.Boolean => result.Boolean,
                DataType.Number => result.Number != 0,
                _ => false
            };
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private void Complete(string stopReason, string? errorMessage = null)
    {
        if (!IsTracing || CurrentSession is null)
        {
            return;
        }

        IsTracing = false;
        _stopRequested = false;
        CurrentSession.EndTime = DateTime.Now;
        CurrentSession.StopReason = stopReason;
        CurrentSession.ErrorMessage = errorMessage;
        TraceSession completed = CurrentSession;
        Completed?.Invoke(completed);
    }

    private static TraceStopCondition CloneCondition(TraceStopCondition condition) =>
        new()
        {
            Type = condition.Type,
            MaxSteps = condition.MaxSteps,
            TargetRip = condition.TargetRip,
            LuaCondition = condition.LuaCondition
        };
}
