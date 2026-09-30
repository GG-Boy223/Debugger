using System.Globalization;
using System.IO;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;
using DogeDebugger.Debugger.UserMode;

namespace DogeDebugger.Core.Debugger.Stack;

public enum CallStackDisplayMode
{
    StackTrace,
    FullStack,
    ModulesOnly,
    NonSystemModulesOnly
}

public enum CallStackReferenceBase
{
    Rsp,
    Rbp,
    SelectedRow
}

public sealed class CallStackFrame
{
    public int Index { get; init; }

    public ulong Address { get; init; }

    public ulong SlotAddress { get; init; }

    public string Symbol { get; init; } = string.Empty;

    public string ModuleName { get; init; } = string.Empty;

    public string ReferenceText { get; init; } = string.Empty;

    public ulong PointerTarget { get; init; }

    public bool IsPointerIntoModule { get; init; }
}

/// <summary>
/// Walks the paused thread's stack. Stack-trace mode follows the frame pointer
/// chain and falls back to return-address heuristics; the raw modes scan the
/// requested stack window and classify each pointer-sized slot.
/// </summary>
public sealed class CallStackWalker
{
    private static readonly Dictionary<string, IReadOnlyList<PeExport>> ExportCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string WindowsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private readonly ITargetProcess _process;
    private readonly Func<IReadOnlyList<ModuleDescriptor>> _moduleProvider;

    public CallStackWalker(
        ITargetProcess process,
        Func<IReadOnlyList<ModuleDescriptor>> moduleProvider)
    {
        _process = process;
        _moduleProvider = moduleProvider;
    }

    public IReadOnlyList<CallStackFrame> Walk(
        RegisterSnapshot registers,
        CallStackDisplayMode mode,
        CallStackReferenceBase referenceBase,
        int maxStackBytes,
        ulong selectedReferenceAddress = 0)
    {
        int pointerSize = _process.Is64Bit ? 8 : 4;
        int window = Math.Clamp(maxStackBytes, 512, 1024);
        ulong stackPointer = registers.StackPointer;
        byte[] stack = stackPointer == 0 ? [] : _process.ReadBytes(stackPointer, window);

        (ulong referenceAddress, string referenceLabel) = referenceBase switch
        {
            CallStackReferenceBase.Rbp => (
                registers.FramePointer,
                _process.Is64Bit ? "rbp" : "ebp"),
            CallStackReferenceBase.SelectedRow => (
                selectedReferenceAddress,
                "ref"),
            _ => (
                stackPointer,
                _process.Is64Bit ? "rsp" : "esp")
        };

        return mode == CallStackDisplayMode.StackTrace
            ? WalkTrace(registers, stack, pointerSize, referenceAddress, referenceLabel)
            : WalkRaw(
                stackPointer,
                stack,
                pointerSize,
                referenceAddress,
                referenceLabel);
    }

    private List<CallStackFrame> WalkTrace(
        RegisterSnapshot registers,
        byte[] stack,
        int pointerSize,
        ulong referenceAddress,
        string referenceLabel)
    {
        List<CallStackFrame> frames =
        [
            CreateTraceFrame(0, registers.InstructionPointer, registers.StackPointer, referenceAddress, referenceLabel)
        ];

        // Follow the frame pointer chain when it is plausible.
        ulong framePointer = registers.FramePointer;
        ulong stackEnd = registers.StackPointer + (ulong)stack.Length;
        HashSet<ulong> seen = [registers.InstructionPointer];
        for (int depth = 0;
             depth < 128 &&
             framePointer >= registers.StackPointer &&
             framePointer + (ulong)(pointerSize * 2) <= stackEnd + (ulong)pointerSize;
             depth++)
        {
            byte[] frame = _process.ReadBytes(framePointer, pointerSize * 2);
            if (frame.Length < pointerSize * 2)
            {
                break;
            }

            ulong nextFrame = ReadPointer(frame, 0, pointerSize);
            ulong returnAddress = ReadPointer(frame, pointerSize, pointerSize);
            if (returnAddress == 0 || !seen.Add(returnAddress))
            {
                break;
            }

            frames.Add(CreateTraceFrame(
                frames.Count,
                returnAddress,
                framePointer + (ulong)pointerSize,
                referenceAddress,
                referenceLabel));
            if (nextFrame <= framePointer)
            {
                break;
            }

            framePointer = nextFrame;
        }

        if (frames.Count >= 2)
        {
            return frames;
        }

        // Fallback: scan the stack window and stop at the first value that no
        // longer points into a module.
        foreach ((ulong slotAddress, ulong value) in EnumerateSlots(
                     registers.StackPointer,
                     stack,
                     pointerSize))
        {
            if (value == 0)
            {
                continue;
            }

            if (FindModule(value) is null)
            {
                if (frames.Count >= 2)
                {
                    break;
                }

                continue;
            }

            if (!seen.Add(value))
            {
                continue;
            }

            frames.Add(CreateTraceFrame(
                frames.Count,
                value,
                slotAddress,
                referenceAddress,
                referenceLabel));
        }

        return frames;
    }

    private List<CallStackFrame> WalkRaw(
        ulong stackPointer,
        byte[] stack,
        int pointerSize,
        ulong referenceAddress,
        string referenceLabel)
    {
        List<CallStackFrame> frames = [];
        foreach ((ulong slotAddress, ulong value) in EnumerateSlots(stackPointer, stack, pointerSize))
        {
            ModuleDescriptor? module = FindModule(value);
            bool isModulePointer = module is not null;

            string symbol = isModulePointer
                ? DescribeAddress(value)
                : string.Empty;
            frames.Add(new CallStackFrame
            {
                Index = frames.Count,
                Address = value,
                SlotAddress = slotAddress,
                Symbol = symbol,
                ModuleName = module?.Name ?? string.Empty,
                ReferenceText = FormatReference(slotAddress, referenceAddress, referenceLabel),
                PointerTarget = isModulePointer ? value : 0,
                IsPointerIntoModule = isModulePointer
            });
        }

        return frames;
    }

    private CallStackFrame CreateTraceFrame(
        int index,
        ulong address,
        ulong slotAddress,
        ulong referenceAddress,
        string referenceLabel)
    {
        ModuleDescriptor? module = FindModule(address);
        return new CallStackFrame
        {
            Index = index,
            Address = address,
            SlotAddress = slotAddress,
            Symbol = module is null ? string.Empty : DescribeAddress(address),
            ModuleName = module?.Name ?? string.Empty,
            ReferenceText = FormatReference(slotAddress, referenceAddress, referenceLabel),
            PointerTarget = module is null ? 0 : address,
            IsPointerIntoModule = module is not null
        };
    }

    private static IEnumerable<(ulong SlotAddress, ulong Value)> EnumerateSlots(
        ulong stackPointer,
        byte[] stack,
        int pointerSize)
    {
        for (int offset = 0; offset + pointerSize <= stack.Length; offset += pointerSize)
        {
            yield return (
                stackPointer + (ulong)offset,
                ReadPointer(stack, offset, pointerSize));
        }
    }

    private static ulong ReadPointer(byte[] buffer, int offset, int pointerSize) =>
        pointerSize == 8
            ? BitConverter.ToUInt64(buffer, offset)
            : BitConverter.ToUInt32(buffer, offset);

    private ModuleDescriptor? FindModule(ulong address)
    {
        if (address < 0x10000)
        {
            return null;
        }

        IReadOnlyList<ModuleDescriptor> modules = _moduleProvider();
        return ModuleCatalog.FindByAddress(modules, address);
    }

    public static bool IsSystemModule(ModuleDescriptor module)
    {
        if (string.IsNullOrEmpty(module.FilePath))
        {
            string name = module.Name;
            return name.StartsWith("ntdll", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("kernel", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("user32", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("gdi32", StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrEmpty(WindowsDirectory) &&
               module.FilePath.StartsWith(WindowsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private string DescribeAddress(ulong address)
    {
        ModuleDescriptor? module = FindModule(address);
        if (module is null)
        {
            return $"0x{address:X}";
        }

        ulong rva = address - module.BaseAddress;
        foreach (PeExport export in GetExports(module))
        {
            if (rva < export.FunctionRva)
            {
                continue;
            }

            ulong delta = rva - export.FunctionRva;
            if (delta > 0x2000)
            {
                continue;
            }

            return delta == 0
                ? $"{module.Name}!{export.Name}"
                : $"{module.Name}!{export.Name}+0x{delta:X}";
        }

        return $"{module.Name}+0x{rva:X}";
    }

    private static IReadOnlyList<PeExport> GetExports(ModuleDescriptor module)
    {
        if (string.IsNullOrWhiteSpace(module.FilePath) || !File.Exists(module.FilePath))
        {
            return [];
        }

        if (ExportCache.TryGetValue(module.FilePath, out IReadOnlyList<PeExport>? cached))
        {
            return cached;
        }

        IReadOnlyList<PeExport> exports;
        try
        {
            exports = new PeModuleAnalyzer().AnalyzeFile(module.FilePath).Exports;
        }
        catch
        {
            exports = [];
        }

        ExportCache[module.FilePath] = exports;
        return exports;
    }

    private static string FormatReference(
        ulong address,
        ulong referenceAddress,
        string referenceLabel)
    {
        if (referenceAddress == 0)
        {
            return string.Empty;
        }

        if (address >= referenceAddress)
        {
            return $"{referenceLabel}+0x{address - referenceAddress:X}";
        }

        return $"{referenceLabel}-0x{referenceAddress - address:X}";
    }
}
