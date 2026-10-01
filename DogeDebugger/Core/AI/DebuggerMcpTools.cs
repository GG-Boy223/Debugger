using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using DogeDebugger.Core.Handles;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.Memory;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;
using DogeDebugger.Core.Rtti;
using DogeDebugger.Core.Search;
using DogeDebugger.Core.Scripting.AutoAssembler;
using DogeDebugger.Core.Signatures;
using DogeDebugger.Core.StringRef;
using DogeDebugger.Core.Threading;
using DogeDebugger.Debugger.Breakpoints;
using DogeDebugger.Debugger.Session;
using DogeDebugger.Debugger.UserMode;
using DogeDebugger.Plugins.UnrealEngine.Models;

namespace DogeDebugger.Core.AI;

public sealed class DebuggerMcpTools
{
    private readonly DebuggerSession _session;
    private readonly ConcurrentDictionary<string, List<MemoryValueMatch>> _valueScans =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DogeDebugger.Core.CrossReference.XrefDatabase>
        _xrefDatabases = new(StringComparer.Ordinal);
    private IReadOnlyList<StringEntry> _stringReferences = [];
    private string? _activeSearchId;

    public DebuggerMcpTools(DebuggerSession session)
    {
        _session = session;
    }

    public void Register(McpToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(new McpToolDefinition
        {
            Name = "get_debugger_status",
            Description = "Return the current target, debugger state, architecture, and breakpoint counts.",
            InputSchema = EmptyObjectSchema(),
            Handler = GetStatusAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_exception_handlers",
            Description = "Scan the current target for vectored handlers, .pdata SEH handlers, and exception dispatcher hooks.",
            InputSchema = EmptyObjectSchema(),
            Handler = GetExceptionHandlersAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "scan_rtti",
            Description = "Scan loaded modules for MSVC RTTI type descriptors, complete object locators, and class hierarchies.",
            InputSchema = ObjectSchema(("module", "string"), ("maxResults", "integer")),
            Handler = ScanRttiAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "create_struct",
            Description = "Create a new RTTI viewer struct definition with typed fields.",
            InputSchema = ObjectSchema(
                ("name", "string"),
                ("baseAddress", "string"),
                ("comment", "string"),
                ("fields", "array")),
            Handler = CreateStructAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "list_structs",
            Description = "List all structs in the RTTI viewer with their field definitions.",
            InputSchema = EmptyObjectSchema(),
            Handler = ListStructsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "add_struct_field",
            Description = "Add a typed field to an existing RTTI viewer struct.",
            InputSchema = ObjectSchema(
                ("structName", "string"),
                ("type", "string"),
                ("name", "string"),
                ("comment", "string"),
                ("length", "integer"),
                ("insertIndex", "integer")),
            Handler = AddStructFieldAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "remove_struct",
            Description = "Remove a struct from the RTTI viewer by name.",
            InputSchema = ObjectSchema(("name", "string")),
            Handler = RemoveStructAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_struct_data",
            Description = "Read the actual memory values of a struct's fields from the target process.",
            InputSchema = ObjectSchema(("name", "string"), ("address", "string")),
            Handler = GetStructDataAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "rtti_project_file",
            Description = "Save or load RTTI struct definitions from a .rtti project file.",
            InputSchema = ObjectSchema(
                ("action", "string"),
                ("path", "string")),
            Handler = RttiProjectFileAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "rtti_auto_guess",
            Description = "Read target memory and automatically infer a typed RTTI struct definition.",
            InputSchema = ObjectSchema(
                ("address", "string"),
                ("length", "integer"),
                ("name", "string"),
                ("comment", "string")),
            Handler = RttiAutoGuessAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "ue_status",
            Description = "Return the current Unreal Engine reflection session state and diagnostics.",
            InputSchema = EmptyObjectSchema(),
            Handler = GetUnrealStatusAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "ue_scan",
            Description = "Scan an Unreal Engine target using cached or manually supplied reflection offsets.",
            InputSchema = ObjectSchema(
                ("module", "string"),
                ("useCache", "boolean"),
                ("autoDetect", "boolean"),
                ("persistCache", "boolean"),
                ("namePoolAddress", "string"),
                ("namePoolBlockArrayAddress", "string"),
                ("fNameEntryStride", "integer"),
                ("fNameBlockOffsetBits", "integer"),
                ("gObjectsAddress", "string"),
                ("objectArrayKind", "string"),
                ("gWorldAddress", "string"),
                ("gEngineAddress", "string")),
            Handler = ScanUnrealAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "ue_get_offsets",
            Description = "Return the active Unreal Engine reflection offsets.",
            InputSchema = EmptyObjectSchema(),
            Handler = GetUnrealOffsetsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "ue_list_objects",
            Description = "List Unreal Engine objects with optional text and kind filters.",
            InputSchema = ObjectSchema(
                ("filter", "string"),
                ("kind", "string"),
                ("offset", "integer"),
                ("limit", "integer")),
            Handler = ListUnrealObjectsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "ue_get_type",
            Description = "Find an Unreal Engine type by exact or partial name.",
            InputSchema = ObjectSchema(("name", "string"), ("address", "string")),
            Handler = GetUnrealTypeAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "ue_list_actors",
            Description = "List actors from the active Unreal Engine world.",
            InputSchema = ObjectSchema(("limit", "integer")),
            Handler = ListUnrealActorsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "ue_inspect_object",
            Description = "Inspect a live UObject instance, including super chain and reflected member values.",
            InputSchema = ObjectSchema(
                ("address", "string"),
                ("path", "string"),
                ("includeMembers", "boolean"),
                ("includeValues", "boolean"),
                ("includeInherited", "boolean"),
                ("filter", "string"),
                ("maxStringLength", "integer"),
                ("arrayPreview", "integer"),
                ("structDepth", "integer"),
                ("offset", "integer"),
                ("limit", "integer")),
            Handler = InspectUnrealObjectAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "list_processes",
            Description = "List running processes, optionally filtered by process name.",
            InputSchema = ObjectSchema(("name", "string"), ("limit", "integer")),
            Handler = ListProcessesAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "attach_process",
            Description = "Attach the debugger to a process by id or exact process name.",
            InputSchema = ObjectSchema(("pid", "integer"), ("name", "string")),
            Handler = AttachProcessAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "launch_process",
            Description = "Launch a process under debugger control.",
            InputSchema = ObjectSchema(("commandLine", "string"), ("workingDirectory", "string")),
            Handler = LaunchProcessAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "list_modules",
            Description = "List modules in the current target process.",
            InputSchema = ObjectSchema(("filter", "string")),
            Handler = ListModulesAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "list_memory_regions",
            Description = "List virtual memory regions in the current target process.",
            InputSchema = ObjectSchema(("readableOnly", "boolean"), ("writableOnly", "boolean"), ("limit", "integer")),
            Handler = ListMemoryRegionsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "list_threads",
            Description = "List threads in the current target process.",
            InputSchema = EmptyObjectSchema(),
            Handler = ListThreadsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "list_handles",
            Description = "List handles owned by the current target process, including object type and granted access.",
            InputSchema = ObjectSchema(("type", "string"), ("limit", "integer")),
            Handler = ListHandlesAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_module_metadata",
            Description = "Parse PE sections, imports, exports, and forwarded exports for a loaded module.",
            InputSchema = ObjectSchema(("module", "string")),
            Handler = GetModuleMetadataAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "read_memory",
            Description = "Read bytes from target memory. Addresses accept decimal or 0x-prefixed hexadecimal.",
            InputSchema = ObjectSchema(("address", "string"), ("size", "integer"), ("format", "string")),
            Handler = ReadMemoryAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "write_memory",
            Description = "Write bytes to target memory. bytes may be a hex string or an array of integers.",
            InputSchema = ObjectSchema(("address", "string"), ("bytes", "string")),
            Handler = WriteMemoryAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "disassemble",
            Description = "Disassemble instructions from the current target.",
            InputSchema = ObjectSchema(("address", "string"), ("count", "integer")),
            Handler = DisassembleAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "aob_search",
            Description = "Search target memory using an AOB pattern such as '48 8B ?? ?? 89'.",
            InputSchema = ObjectSchema(
                ("pattern", "string"),
                ("startAddress", "string"),
                ("endAddress", "string"),
                ("maxResults", "integer"),
                ("private", "boolean"),
                ("image", "boolean"),
                ("mapped", "boolean")),
            Handler = SearchAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "value_scan",
            Description = "Run a typed CE-style value scan and return a scanId for subsequent refinements.",
            InputSchema = ObjectSchema(
                ("kind", "string"),
                ("value", "string"),
                ("comparison", "string"),
                ("secondValue", "string"),
                ("startAddress", "string"),
                ("endAddress", "string"),
                ("alignment", "integer"),
                ("maxResults", "integer"),
                ("writableOnly", "boolean"),
                ("executableOnly", "boolean"),
                ("ignoreCase", "boolean")),
            Handler = ValueScanAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "value_scan_next",
            Description = "Refine a previous value scan using a scanId.",
            InputSchema = ObjectSchema(
                ("scanId", "string"),
                ("value", "string"),
                ("comparison", "string"),
                ("secondValue", "string"),
                ("maxResults", "integer")),
            Handler = ValueScanNextAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "pointer_scan",
            Description = "Scan for module-based pointer chains leading to a target address.",
            InputSchema = ObjectSchema(
                ("address", "string"),
                ("maxOffset", "integer"),
                ("maxDepth", "integer"),
                ("alignment", "integer"),
                ("maxResults", "integer"),
                ("writableOnly", "boolean")),
            Handler = PointerScanAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "scan_strings",
            Description = "Scan target memory for UTF-8/ASCII and UTF-16LE strings.",
            InputSchema = ObjectSchema(
                ("minimumLength", "integer"),
                ("maximumLength", "integer"),
                ("maxResults", "integer"),
                ("ascii", "boolean"),
                ("utf16", "boolean"),
                ("private", "boolean"),
                ("image", "boolean"),
                ("mapped", "boolean")),
            Handler = ScanStringsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "build_xrefs",
            Description = "Build a cross-reference database for executable memory and return a database id.",
            InputSchema = EmptyObjectSchema(),
            Handler = BuildXrefsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_xrefs",
            Description = "Query references to an address from a database returned by build_xrefs.",
            InputSchema = ObjectSchema(("databaseId", "string"), ("address", "string")),
            Handler = GetXrefsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "generate_signature",
            Description = "Generate a unique module-relative AOB signature for an address.",
            InputSchema = ObjectSchema(
                ("address", "string"),
                ("module", "string"),
                ("maximumLength", "integer"),
                ("instructionCount", "integer")),
            Handler = GenerateSignatureAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "set_breakpoint",
            Description = "Set a software breakpoint at an address.",
            InputSchema = ObjectSchema(("address", "string")),
            Handler = SetBreakpointAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "remove_breakpoint",
            Description = "Remove a software breakpoint at an address.",
            InputSchema = ObjectSchema(("address", "string")),
            Handler = RemoveBreakpointAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "continue_execution",
            Description = "Continue execution of the paused debuggee.",
            InputSchema = EmptyObjectSchema(),
            Handler = async (_, cancellationToken) =>
                BoolResult(await _session.Debugger.ContinueAsync(cancellationToken).ConfigureAwait(false))
        });
        registry.Register(new McpToolDefinition
        {
            Name = "break_execution",
            Description = "Break into the running debuggee.",
            InputSchema = EmptyObjectSchema(),
            Handler = async (_, cancellationToken) =>
                BoolResult(await _session.Debugger.BreakAsync(cancellationToken).ConfigureAwait(false))
        });
        registry.Register(new McpToolDefinition
        {
            Name = "start_instruction_access_watch",
            Description = "Watch which addresses one memory operand of an instruction accesses. " +
                          "The debugger must be attached; the internal breakpoint auto-continues.",
            InputSchema = ObjectSchema(
                ("instruction_address", "string"),
                ("memory_operand_index", "integer"),
                ("memory_operand_text", "string"),
                ("value_size", "string"),
                ("max_records", "integer")),
            Handler = StartInstructionAccessWatchAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_instruction_access_watch",
            Description = "Return the instruction access watch state and captured addresses. " +
                          "Omit watch_id to list all watches.",
            InputSchema = ObjectSchema(
                ("watch_id", "string"),
                ("offset", "integer"),
                ("limit", "integer"),
                ("resolve_mono_candidates", "boolean"),
                ("resolve_unreal_candidates", "boolean")),
            Handler = GetInstructionAccessWatchAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "stop_instruction_access_watch",
            Description = "Stop one instruction access watch and remove its internal breakpoint.",
            InputSchema = ObjectSchema(
                ("watch_id", "string"),
                ("resume", "string")),
            Handler = StopInstructionAccessWatchAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "step_into",
            Description = "Execute one instruction and enter calls.",
            InputSchema = EmptyObjectSchema(),
            Handler = async (_, cancellationToken) =>
                BoolResult(await _session.Debugger.StepIntoAsync(cancellationToken).ConfigureAwait(false))
        });
        registry.Register(new McpToolDefinition
        {
            Name = "step_over",
            Description = "Execute one instruction and step over calls.",
            InputSchema = EmptyObjectSchema(),
            Handler = async (_, cancellationToken) =>
                BoolResult(await _session.Debugger.StepOverAsync(cancellationToken).ConfigureAwait(false))
        });
        registry.Register(new McpToolDefinition
        {
            Name = "step_out",
            Description = "Run until the current function returns.",
            InputSchema = EmptyObjectSchema(),
            Handler = async (_, cancellationToken) =>
                BoolResult(await _session.Debugger.StepOutAsync(cancellationToken).ConfigureAwait(false))
        });
        registry.Register(new McpToolDefinition
        {
            Name = "manage_debugger",
            Description = "Attach or detach the user-mode debugger to/from the target process.",
            InputSchema = ObjectSchema(("action", "string")),
            Handler = ManageDebuggerAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "reopen_process",
            Description = "Open a target process by PID and optionally attach the debugger.",
            InputSchema = ObjectSchema(("pid", "integer"), ("attach", "boolean")),
            Handler = ReopenProcessAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_process_info",
            Description = "Get information about the currently opened target process.",
            InputSchema = EmptyObjectSchema(),
            Handler = GetProcessInfoAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_registers",
            Description = "Read the general purpose registers of the paused thread.",
            InputSchema = ObjectSchema(("registerSet", "string")),
            Handler = GetRegistersAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_threads",
            Description = "List threads of the current target process.",
            InputSchema = EmptyObjectSchema(),
            Handler = GetThreadsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_modules",
            Description = "List loaded modules of the current target process.",
            InputSchema = EmptyObjectSchema(),
            Handler = GetModulesAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_memory_map",
            Description = "List virtual memory regions of the current target process.",
            InputSchema = ObjectSchema(
                ("offset", "integer"),
                ("limit", "integer"),
                ("start_address", "string"),
                ("end_address", "string")),
            Handler = GetMemoryMapAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "list_breakpoints",
            Description = "List software and hardware breakpoints with state and hit counts.",
            InputSchema = EmptyObjectSchema(),
            Handler = ListBreakpointsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_current_state",
            Description = "Get the full debugger state when paused: RIP, registers, disassembly context, and stack summary.",
            InputSchema = ObjectSchema(
                ("contextLines", "integer"),
                ("includeBytes", "boolean"),
                ("registerSet", "string")),
            Handler = GetCurrentStateAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_callstack",
            Description = "Read the call stack of the paused thread using return-address scanning.",
            InputSchema = ObjectSchema(("refresh", "boolean"), ("limit", "integer")),
            Handler = GetCallStackAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "set_rip",
            Description = "Set the instruction pointer of the paused thread.",
            InputSchema = ObjectSchema(("address", "string"), ("registerSet", "string")),
            Handler = SetInstructionPointerAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "step",
            Description = "Step the paused thread: direction into, over, or out.",
            InputSchema = ObjectSchema(("direction", "string"), ("registerSet", "string")),
            Handler = StepAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_module_exports",
            Description = "List exported symbols of a loaded module.",
            InputSchema = ObjectSchema(("moduleName", "string")),
            Handler = GetModuleExportsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_module_imports",
            Description = "List imported modules and symbols of a loaded module.",
            InputSchema = ObjectSchema(("moduleName", "string")),
            Handler = GetModuleImportsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_string_refs",
            Description = "List cached string references for the current target.",
            InputSchema = ObjectSchema(
                ("offset", "integer"),
                ("limit", "integer"),
                ("filter", "string")),
            Handler = GetStringReferencesAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "scan_string_refs",
            Description = "Scan target memory for string references and cache the result.",
            InputSchema = ObjectSchema(
                ("includeUnicode", "boolean"),
                ("module", "string")),
            Handler = ScanStringReferencesAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "assemble",
            Description = "Assemble x86/x64 instructions and write them to the target process at the specified address.",
            InputSchema = ObjectSchema(("address", "string"), ("instructions", "string")),
            Handler = AssembleAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "resolve_address",
            Description = "Resolve an address expression against the target modules, exports, and symbols.",
            InputSchema = ObjectSchema(("expression", "string")),
            Handler = ResolveAddressAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "analyze_xrefs",
            Description = "Build the static cross-reference database for a module or the whole target.",
            InputSchema = ObjectSchema(("module", "string")),
            Handler = AnalyzeXrefsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_xref_status",
            Description = "Report the state of the cached cross-reference database.",
            InputSchema = EmptyObjectSchema(),
            Handler = GetXrefStatusAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "list_functions",
            Description = "List functions discovered in the cached cross-reference database.",
            InputSchema = ObjectSchema(
                ("name_filter", "string"),
                ("offset", "integer"),
                ("limit", "integer"),
                ("min_xref_count", "integer"),
                ("order_by", "string")),
            Handler = ListFunctionsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "find_instruction_references",
            Description = "Scan executable code for instructions referencing a target address or constant.",
            InputSchema = ObjectSchema(
                ("target_kind", "string"),
                ("target", "string"),
                ("modules", "array"),
                ("max_results", "integer")),
            Handler = FindInstructionReferencesAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "memory_search",
            Description = "Run the memory search workflow: mode=first starts a scan, mode=next filters results, mode=reset clears them.",
            InputSchema = ObjectSchema(
                ("mode", "string"),
                ("value", "string"),
                ("value_min", "string"),
                ("value_max", "string"),
                ("value_type", "string"),
                ("scan_type", "string"),
                ("is_hex", "boolean"),
                ("start_address", "string"),
                ("end_address", "string"),
                ("writable", "boolean"),
                ("executable", "boolean"),
                ("alignment", "integer"),
                ("ignore_case", "boolean")),
            Handler = MemorySearchAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "get_memory_search_results",
            Description = "Return the current memory search result list.",
            InputSchema = ObjectSchema(("offset", "integer"), ("limit", "integer")),
            Handler = GetMemorySearchResultsAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "write_export_file",
            Description = "Write a text, JSON, or base64 payload under the application Exports folder.",
            InputSchema = ObjectSchema(
                ("relative_path", "string"),
                ("content_type", "string"),
                ("overwrite", "boolean"),
                ("encoding", "string"),
                ("text", "array"),
                ("json", "object"),
                ("base64", "string")),
            Handler = WriteExportFileAsync
        });
        registry.Register(new McpToolDefinition
        {
            Name = "dump_memory",
            Description = "Dump target process memory to the Exports\\MemoryDump folder.",
            InputSchema = ObjectSchema(
                ("scope", "string"),
                ("start_address", "string"),
                ("size", "string"),
                ("module_name", "string"),
                ("file_name", "string"),
                ("overwrite", "boolean"),
                ("max_total_bytes", "string")),
            Handler = DumpMemoryAsync
        });
    }

    private async ValueTask<McpToolResult> ManageDebuggerAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        string action = (GetString(arguments, "action") ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        if (action == "detach")
        {
            if (!_session.Debugger.IsDebugging)
            {
                return JsonResult(new { success = true, debugging = false });
            }

            await _session.Debugger.DetachAsync(cancellationToken).ConfigureAwait(false);
            return JsonResult(new { success = true, debugging = false });
        }

        if (action != "attach")
        {
            return ErrorResult("manage_debugger requires action=attach or action=detach.");
        }

        if (!_session.Target.IsOpen)
        {
            return ErrorResult("No process is currently open.");
        }

        ProcessDescriptor descriptor = new(
            _session.Target.ProcessId,
            _session.Target.ProcessName,
            _session.Target.FilePath,
            0,
            null);
        bool attached = await _session
            .AttachAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);
        return attached
            ? JsonResult(new { success = true, debugging = true })
            : ErrorResult("DebugActiveProcess failed.");
    }

    private ValueTask<McpToolResult> StartInstructionAccessWatchAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Target.IsOpen || !_session.Debugger.IsDebugging)
        {
            return ValueTask.FromResult(
                ErrorResult("Attach the debugger before starting an instruction access watch."));
        }

        ulong instructionAddress = 0;
        string? addressText = GetString(arguments, "instruction_address");
        if (!string.IsNullOrWhiteSpace(addressText))
        {
            if (!TryResolveAddressExpression(
                    addressText,
                    out instructionAddress,
                    out string? addressError))
            {
                return ValueTask.FromResult(ErrorResult(addressError!));
            }
        }
        else if (_session.Debugger.IsPaused)
        {
            instructionAddress = _session.Debugger.CurrentInstructionPointer;
        }

        if (instructionAddress == 0)
        {
            return ValueTask.FromResult(
                ErrorResult(
                    "instruction_address is required unless the debugger is paused."));
        }

        InstructionSnapshot? instruction = _session
            .Disassemble(instructionAddress, 1)
            .FirstOrDefault();
        if (instruction is null)
        {
            return ValueTask.FromResult(
                ErrorResult(
                    $"Failed to decode an instruction at {FormatAddress(instructionAddress)}."));
        }

        int bitness = _session.Target.Is64Bit ? 64 : 32;
        IReadOnlyList<InstructionMemoryOperand> operands =
            InstructionMemoryOperandBuilder.Build(
                instruction,
                bitness,
                AssemblySyntax.Intel,
                _session.EnumerateModules());
        if (operands.Count == 0)
        {
            return ValueTask.FromResult(
                ErrorResult("The instruction has no memory operand to watch."));
        }

        InstructionMemoryOperand? operand = SelectInstructionMemoryOperand(
            arguments,
            operands,
            out string? operandError);
        if (operand is null)
        {
            return ValueTask.FromResult(ErrorResult(operandError!));
        }

        if (!TryParseInstructionAccessValueSize(
                GetString(arguments, "value_size"),
                operand.DefaultValueSize,
                out int valueSize,
                out string? valueSizeError))
        {
            return ValueTask.FromResult(ErrorResult(valueSizeError!));
        }

        int maximumRecords = Math.Clamp(
            GetInt32(arguments, "max_records") ?? 100_000,
            1,
            100_000);
        InstructionAccessWatchStartResult result =
            _session.InstructionAccessWatches.Start(
                instruction,
                operand,
                bitness,
                valueSize,
                maximumRecords);
        if (!result.Success || string.IsNullOrWhiteSpace(result.WatchId))
        {
            return ValueTask.FromResult(
                ErrorResult(result.Error ?? "Failed to start the instruction access watch."));
        }

        return ValueTask.FromResult(JsonResult(new
        {
            success = true,
            watchId = result.WatchId,
            instructionAddress = FormatAddress(instruction.Address),
            instruction = instruction.Text,
            operandIndex = operand.OperandIndex,
            operand = operand.DisplayText,
            valueSize,
            maximumRecords,
            state = "running"
        }));
    }

    private ValueTask<McpToolResult> GetInstructionAccessWatchAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int offset = Math.Max(GetInt32(arguments, "offset") ?? 0, 0);
        int limit = Math.Clamp(GetInt32(arguments, "limit") ?? 100, 0, 10_000);
        string? watchId = GetString(arguments, "watch_id");
        if (string.IsNullOrWhiteSpace(watchId))
        {
            IReadOnlyList<InstructionAccessWatchSnapshot> snapshots =
                _session.InstructionAccessWatches.GetSnapshots(offset, limit);
            return ValueTask.FromResult(JsonResult(new
            {
                count = snapshots.Count,
                watches = snapshots
                    .Select(ToInstructionAccessWatchDto)
                    .ToArray()
            }));
        }

        if (!_session.InstructionAccessWatches.TryGetSnapshot(
                watchId,
                offset,
                limit,
                out InstructionAccessWatchSnapshot? snapshot) ||
            snapshot is null)
        {
            return ValueTask.FromResult(
                ErrorResult($"Instruction access watch '{watchId}' was not found."));
        }

        return ValueTask.FromResult(JsonResult(ToInstructionAccessWatchDto(snapshot)));
    }

    private async ValueTask<McpToolResult> StopInstructionAccessWatchAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        string? watchId = GetString(arguments, "watch_id");
        if (string.IsNullOrWhiteSpace(watchId))
        {
            return ErrorResult("stop_instruction_access_watch requires watch_id.");
        }

        InstructionAccessResumeMode resumeMode = ParseInstructionAccessResumeMode(
            GetString(arguments, "resume"));
        InstructionAccessWatchStopResult result =
            await _session.InstructionAccessWatches.StopAsync(
                    watchId,
                    resumeMode,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!result.Success)
        {
            return ErrorResult(result.Error ?? "Failed to stop the instruction access watch.");
        }

        return JsonResult(new
        {
            success = true,
            watchId = result.WatchId,
            wasRunning = result.WasRunning,
            pauseConfirmed = result.PauseConfirmed,
            resumeRequested = result.ResumeRequested,
            resumed = result.Resumed,
            resumeMode = result.ResumeMode.ToString(),
            status = result.Status
        });
    }

    private async ValueTask<McpToolResult> ReopenProcessAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        int? processId = GetInt32(arguments, "pid");
        if (processId is null)
        {
            return ErrorResult("reopen_process requires pid.");
        }

        ProcessDescriptor? process = _session
            .EnumerateProcesses()
            .FirstOrDefault(candidate => candidate.ProcessId == processId.Value);
        if (process is null)
        {
            return ErrorResult("The requested process was not found.");
        }

        bool opened = await _session
            .OpenAsync(process, cancellationToken)
            .ConfigureAwait(false);
        if (!opened)
        {
            return ErrorResult("OpenProcess failed.");
        }

        bool attach = GetBoolean(arguments, "attach") ?? false;
        bool debugging = false;
        if (attach)
        {
            debugging = await _session
                .AttachAsync(process, cancellationToken)
                .ConfigureAwait(false);
        }

        return JsonResult(new
        {
            success = true,
            pid = process.ProcessId,
            name = process.Name,
            filePath = process.FilePath,
            x64 = _session.Target.Is64Bit,
            debugging
        });
    }

    private ValueTask<McpToolResult> GetProcessInfoAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No process is currently open."));
        }

        return ValueTask.FromResult(JsonResult(new
        {
            open = true,
            pid = _session.Target.ProcessId,
            name = _session.Target.ProcessName,
            filePath = _session.Target.FilePath,
            x64 = _session.Target.Is64Bit,
            debugging = _session.Debugger.IsDebugging,
            paused = _session.Debugger.IsPaused,
            currentThreadId = _session.Debugger.CurrentThreadId,
            instructionPointer = FormatAddress(_session.Debugger.CurrentInstructionPointer),
            modules = _session.EnumerateModules().Count,
            threads = _session.EnumerateThreads().Count
        }));
    }

    private ValueTask<McpToolResult> GetRegistersAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Debugger.IsPaused)
        {
            return ValueTask.FromResult(ErrorResult("Target is not paused."));
        }

        RegisterSnapshot snapshot = _session.Debugger.GetCurrentRegisters();
        return ValueTask.FromResult(JsonResult(new
        {
            instructionPointer = FormatAddress(snapshot.InstructionPointer),
            stackPointer = FormatAddress(snapshot.StackPointer),
            framePointer = FormatAddress(snapshot.FramePointer),
            flags = FormatAddress(snapshot.Flags),
            generalPurpose = snapshot.GeneralPurposeRegisters
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => FormatAddress(pair.Value))
        }));
    }

    private ValueTask<McpToolResult> GetThreadsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No process is currently open."));
        }

        IReadOnlyList<ThreadDescriptor> threads = _session.EnumerateThreads();
        return ValueTask.FromResult(JsonResult(new
        {
            total = threads.Count,
            threads = threads.Select(thread => new
            {
                tid = thread.ThreadId,
                name = thread.Name,
                startAddress = FormatAddress(thread.StartAddress),
                instructionPointer = FormatAddress(thread.InstructionPointer),
                teb = FormatAddress(thread.TebBaseAddress),
                priority = thread.BasePriority,
                suspendCount = thread.SuspendCount
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> GetModulesAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No process is currently open."));
        }

        IReadOnlyList<ModuleDescriptor> modules = _session.EnumerateModules();
        return ValueTask.FromResult(JsonResult(new
        {
            total = modules.Count,
            modules = modules.Select(module => new
            {
                name = module.Name,
                path = module.FilePath,
                baseAddress = FormatAddress(module.BaseAddress),
                size = module.Size,
                entryPoint = FormatAddress(module.EntryPoint),
                main = module.IsMainModule
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> GetMemoryMapAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No process is currently open."));
        }

        int offset = Math.Max(0, GetInt32(arguments, "offset") ?? 0);
        int limit = Math.Clamp(GetInt32(arguments, "limit") ?? 256, 1, 5000);
        ulong start = GetAddress(arguments, "start_address") ?? 0;
        ulong end = GetAddress(arguments, "end_address") ?? ulong.MaxValue;
        IReadOnlyList<MemoryRegionInfo> regions = _session
            .EnumerateMemoryRegions()
            .Where(region =>
                region.BaseAddress + region.Size > start &&
                region.BaseAddress < end)
            .ToArray();
        MemoryRegionInfo[] page = regions
            .Skip(offset)
            .Take(limit)
            .ToArray();
        return ValueTask.FromResult(JsonResult(new
        {
            total = regions.Count,
            offset,
            count = page.Length,
            more = offset + page.Length < regions.Count,
            regions = page.Select(region => new
            {
                baseAddress = FormatAddress(region.BaseAddress),
                allocationBase = FormatAddress(region.AllocationBase),
                size = region.Size,
                state = region.StateText,
                type = region.TypeText,
                protect = region.ProtectDisplayText,
                allocationProtect = region.AllocationProtectDisplayText,
                module = region.ModuleName,
                section = region.SectionName
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> ListBreakpointsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<BreakpointEntry> software = _session.Breakpoints.Entries;
        IReadOnlyList<BreakpointEntry> hardware = _session.HardwareBreakpoints.Entries;
        object[] all = software
            .Concat(hardware)
            .Select(entry => (object)new
            {
                address = FormatAddress(entry.Address),
                kind = entry.KindText,
                enabled = entry.IsEnabled,
                armed = entry.IsArmed,
                temporary = entry.IsTemporary,
                hits = entry.HitCount,
                condition = entry.Condition,
                comment = entry.Comment,
                module = entry.ModuleName,
                symbol = entry.SymbolName
            })
            .ToArray();
        return ValueTask.FromResult(JsonResult(new
        {
            total = all.Length,
            software = software.Count,
            hardware = hardware.Count,
            breakpoints = all
        }));
    }

    private ValueTask<McpToolResult> GetCurrentStateAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Debugger.IsPaused)
        {
            return ValueTask.FromResult(ErrorResult("Target is not paused."));
        }

        int contextLines = Math.Clamp(GetInt32(arguments, "contextLines") ?? 10, 1, 50);
        RegisterSnapshot registers = _session.Debugger.GetCurrentRegisters();
        ulong ip = registers.InstructionPointer;
        ulong back = (ulong)(contextLines * 8);
        ulong start = ip > back ? ip - back : ip;
        InstructionSnapshot[] instructions = _session
            .Disassemble(start, contextLines * 3, new DisassemblyOptions())
            .ToArray();
        object[] context = instructions.Select(instruction => (object)new
        {
            address = FormatAddress(instruction.Address),
            bytes = instruction.BytesText,
            text = instruction.Text,
            current = instruction.Address == ip
        }).ToArray();
        return ValueTask.FromResult(JsonResult(new
        {
            instructionPointer = FormatAddress(ip),
            stackPointer = FormatAddress(registers.StackPointer),
            framePointer = FormatAddress(registers.FramePointer),
            flags = FormatAddress(registers.Flags),
            threadId = _session.Debugger.CurrentThreadId,
            registers = registers.GeneralPurposeRegisters
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => FormatAddress(pair.Value)),
            context
        }));
    }

    private ValueTask<McpToolResult> GetCallStackAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Debugger.IsPaused)
        {
            return ValueTask.FromResult(ErrorResult("Target is not paused."));
        }

        int limit = Math.Clamp(GetInt32(arguments, "limit") ?? 32, 1, 256);
        RegisterSnapshot registers = _session.Debugger.GetCurrentRegisters();
        int pointerSize = _session.Target.Is64Bit ? 8 : 4;
        byte[] stack = _session.ReadBytes(
            registers.StackPointer,
            pointerSize * limit);
        IReadOnlyList<ModuleDescriptor> modules = _session.EnumerateModules();
        List<object> frames = [];
        for (int index = 0; index + pointerSize <= stack.Length; index += pointerSize)
        {
            ulong value = pointerSize == 8
                ? BitConverter.ToUInt64(stack, index)
                : BitConverter.ToUInt32(stack, index);
            if (value == 0)
            {
                continue;
            }

            ModuleDescriptor? module = modules.FirstOrDefault(
                candidate => candidate.Contains(value));
            if (module is null)
            {
                continue;
            }

            frames.Add(new
            {
                index = frames.Count,
                returnAddress = FormatAddress(value),
                module = module.Name,
                moduleOffset = FormatAddress(value - module.BaseAddress),
                stackSlot = FormatAddress(registers.StackPointer + (ulong)index)
            });
            if (frames.Count >= limit)
            {
                break;
            }
        }

        return ValueTask.FromResult(JsonResult(new
        {
            frames = frames.Count,
            stackPointer = FormatAddress(registers.StackPointer),
            callStack = frames.ToArray()
        }));
    }

    private ValueTask<McpToolResult> SetInstructionPointerAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Debugger.IsPaused)
        {
            return ValueTask.FromResult(ErrorResult("Target is not paused."));
        }

        ulong address = GetAddress(arguments, "address") ?? 0;
        if (address == 0)
        {
            return ValueTask.FromResult(ErrorResult("set_rip requires a valid address."));
        }

        bool updated = _session.Debugger.SetCurrentInstructionPointer(address);
        return ValueTask.FromResult(JsonResult(new
        {
            success = updated,
            instructionPointer = FormatAddress(
                _session.Debugger.GetCurrentRegisters().InstructionPointer)
        }));
    }

    private async ValueTask<McpToolResult> StepAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        string direction = (GetString(arguments, "direction") ?? "into")
            .Trim()
            .ToLowerInvariant();
        bool stepped = direction switch
        {
            "over" => await _session.Debugger
                .StepOverAsync(cancellationToken)
                .ConfigureAwait(false),
            "out" => await _session.Debugger
                .StepOutAsync(cancellationToken)
                .ConfigureAwait(false),
            _ => await _session.Debugger
                .StepIntoAsync(cancellationToken)
                .ConfigureAwait(false)
        };
        return JsonResult(new { success = stepped, direction });
    }

    private ValueTask<McpToolResult> GetModuleExportsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryFindModule(
                GetString(arguments, "moduleName"),
                out ModuleDescriptor? module,
                out McpToolResult? error))
        {
            return ValueTask.FromResult(error!);
        }

        PeModuleMetadata metadata = _session.AnalyzeModule(module!);
        return ValueTask.FromResult(JsonResult(new
        {
            module = module!.Name,
            baseAddress = FormatAddress(module.BaseAddress),
            total = metadata.Exports.Count,
            exports = metadata.Exports.Select(item => new
            {
                name = item.Name,
                ordinal = item.Ordinal,
                address = item.DisplayAddress(module.BaseAddress),
                forwarder = item.ForwarderName
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> GetModuleImportsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryFindModule(
                GetString(arguments, "moduleName"),
                out ModuleDescriptor? module,
                out McpToolResult? error))
        {
            return ValueTask.FromResult(error!);
        }

        PeModuleMetadata metadata = _session.AnalyzeModule(module!);
        return ValueTask.FromResult(JsonResult(new
        {
            module = module!.Name,
            baseAddress = FormatAddress(module.BaseAddress),
            total = metadata.Imports.Count,
            imports = metadata.Imports.Select(item => new
            {
                module = item.ModuleName,
                function = item.FunctionDisplayName,
                ordinal = item.Ordinal,
                iatAddress = item.DisplayAddress(module.BaseAddress)
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> GetStringReferencesAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int offset = Math.Max(0, GetInt32(arguments, "offset") ?? 0);
        int limit = Math.Clamp(GetInt32(arguments, "limit") ?? 200, 1, 5000);
        string filter = GetString(arguments, "filter") ?? string.Empty;
        IEnumerable<StringEntry> entries = _stringReferences;
        if (filter.Length > 0)
        {
            entries = entries.Where(entry =>
                entry.Text.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        StringEntry[] all = entries.ToArray();
        StringEntry[] page = all.Skip(offset).Take(limit).ToArray();
        return ValueTask.FromResult(JsonResult(new
        {
            total = all.Length,
            offset,
            count = page.Length,
            strings = page.Select(entry => new
            {
                address = FormatAddress(entry.Address),
                text = entry.Text,
                encoding = entry.EncodingName,
                byteLength = entry.ByteLength,
                references = entry.References
                    .Select(address => FormatAddress(address))
                    .ToArray()
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> ScanStringReferencesAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No process is currently open."));
        }

        bool includeUnicode = GetBoolean(arguments, "includeUnicode") ?? true;
        StringScanOptions options = new()
        {
            ScanAsciiUtf8 = true,
            ScanUtf16Le = includeUnicode,
            IncludeImageMemory = true,
            IncludePrivateMemory = true
        };
        _stringReferences = _session.ScanStrings(options, cancellationToken);
        return ValueTask.FromResult(JsonResult(new
        {
            success = true,
            total = _stringReferences.Count
        }));
    }

    private ValueTask<McpToolResult> AssembleAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No process is currently open."));
        }

        if (!TryGetAddress(arguments, "address", out ulong address))
        {
            return ValueTask.FromResult(ErrorResult("assemble requires a valid address."));
        }

        string source = GetString(arguments, "instructions") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            return ValueTask.FromResult(ErrorResult("assemble requires instructions."));
        }

        ulong cursor = address;
        List<object> encoded = [];
        try
        {
            foreach (string rawLine in source.Replace("\r\n", "\n").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 ||
                    line.StartsWith(';') ||
                    line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                byte[] bytes = AutoAssemblerTextAssembler.Assemble(
                    line,
                    cursor,
                    _session.Target.Is64Bit);
                if (bytes.Length == 0)
                {
                    continue;
                }

                if (!_session.WriteBytes(cursor, bytes))
                {
                    return ValueTask.FromResult(
                        ErrorResult($"Failed to write at {FormatAddress(cursor)}."));
                }

                encoded.Add(new
                {
                    address = FormatAddress(cursor),
                    bytes = Convert.ToHexString(bytes),
                    text = line
                });
                cursor += (ulong)bytes.Length;
            }
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(ErrorResult(exception.Message));
        }

        return ValueTask.FromResult(JsonResult(new
        {
            success = true,
            address = FormatAddress(address),
            bytesWritten = cursor - address,
            instructions = encoded.ToArray()
        }));
    }

    private ValueTask<McpToolResult> ResolveAddressAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string expression = (GetString(arguments, "expression") ?? string.Empty).Trim();
        if (expression.Length == 0)
        {
            return ValueTask.FromResult(ErrorResult("resolve_address requires an expression."));
        }

        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No process is currently open."));
        }

        IReadOnlyList<ModuleDescriptor> modules = _session.EnumerateModules();
        int separator = expression.IndexOf('!');
        if (separator > 0)
        {
            string moduleName = expression[..separator];
            string exportName = expression[(separator + 1)..];
            ModuleDescriptor? module = modules.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, moduleName, StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                return ValueTask.FromResult(ErrorResult($"Module '{moduleName}' was not found."));
            }

            PeExport? export = _session
                .AnalyzeModule(module)
                .Exports
                .FirstOrDefault(item =>
                    string.Equals(item.Name, exportName, StringComparison.OrdinalIgnoreCase));
            if (export is null)
            {
                return ValueTask.FromResult(ErrorResult($"Export '{exportName}' was not found."));
            }

            ulong exportAddress = module.BaseAddress + export.FunctionRva;
            return ValueTask.FromResult(JsonResult(new
            {
                expression,
                address = FormatAddress(exportAddress),
                module = module.Name,
                moduleOffset = FormatAddress(exportAddress - module.BaseAddress),
                export = export.Name
            }));
        }

        int plus = expression.LastIndexOf('+');
        if (plus > 0)
        {
            string moduleName = expression[..plus].Trim();
            string offsetText = expression[(plus + 1)..].Trim();
            ModuleDescriptor? module = modules.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, moduleName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    Path.GetFileName(candidate.FilePath),
                    moduleName,
                    StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                return ValueTask.FromResult(ErrorResult($"Module '{moduleName}' was not found."));
            }

            if (!TryParseAddressLiteral(offsetText, out ulong offset))
            {
                return ValueTask.FromResult(ErrorResult($"Invalid offset '{offsetText}'."));
            }

            ulong resolved = module.BaseAddress + offset;
            return ValueTask.FromResult(JsonResult(new
            {
                expression,
                address = FormatAddress(resolved),
                module = module.Name,
                moduleOffset = FormatAddress(offset)
            }));
        }

        ModuleDescriptor? directModule = modules.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, expression, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Path.GetFileName(candidate.FilePath),
                expression,
                StringComparison.OrdinalIgnoreCase));
        if (directModule is not null)
        {
            return ValueTask.FromResult(JsonResult(new
            {
                expression,
                address = FormatAddress(directModule.BaseAddress),
                module = directModule.Name,
                moduleOffset = FormatAddress(0)
            }));
        }

        if (TryParseAddressLiteral(expression, out ulong literal))
        {
            return ValueTask.FromResult(JsonResult(new
            {
                expression,
                address = FormatAddress(literal)
            }));
        }

        return ValueTask.FromResult(
            ErrorResult($"Unable to resolve '{expression}'."));
    }

    private static bool TryParseAddressLiteral(string text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(
                trimmed[2..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out value);
        }

        return ulong.TryParse(
                   trimmed,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out value) ||
               ulong.TryParse(
                   trimmed,
                   NumberStyles.AllowHexSpecifier,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private ValueTask<McpToolResult> AnalyzeXrefsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No process is currently open."));
        }

        try
        {
            string? moduleName = GetString(arguments, "module");
            DogeDebugger.Core.CrossReference.XrefDatabase database;
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                database = _session.BuildCrossReferences(cancellationToken);
                _xrefDatabases["*"] = database;
            }
            else
            {
                if (!TryFindModule(moduleName, out ModuleDescriptor? module, out McpToolResult? error))
                {
                    return ValueTask.FromResult(error!);
                }

                database = _session.BuildModuleCrossReferences(
                    module!,
                    null,
                    null,
                    cancellationToken);
                _xrefDatabases[module!.Name] = database;
            }

            return ValueTask.FromResult(JsonResult(new
            {
                success = true,
                module = database.ModuleName,
                imageBase = FormatAddress(database.ImageBase),
                xrefs = database.TotalXrefs,
                functions = database.TotalFunctions,
                elapsedMs = (int)database.AnalysisDuration.TotalMilliseconds
            }));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(ErrorResult(exception.Message));
        }
    }

    private ValueTask<McpToolResult> GetXrefStatusAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object[] databases = _xrefDatabases
            .Select(pair => (object)new
            {
                key = pair.Key,
                module = pair.Value.ModuleName,
                imageBase = FormatAddress(pair.Value.ImageBase),
                xrefs = pair.Value.TotalXrefs,
                functions = pair.Value.TotalFunctions,
                elapsedMs = (int)pair.Value.AnalysisDuration.TotalMilliseconds
            })
            .ToArray();
        return ValueTask.FromResult(JsonResult(new
        {
            ready = _xrefDatabases.Count > 0,
            databases
        }));
    }

    private ValueTask<McpToolResult> ListFunctionsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_xrefDatabases.Count == 0)
        {
            return ValueTask.FromResult(
                ErrorResult("No cross-reference database. Run analyze_xrefs first."));
        }

        string filter = GetString(arguments, "name_filter") ?? string.Empty;
        int offset = Math.Max(0, GetInt32(arguments, "offset") ?? 0);
        int limit = Math.Clamp(GetInt32(arguments, "limit") ?? 200, 1, 5000);
        int minXrefs = Math.Max(0, GetInt32(arguments, "min_xref_count") ?? 0);
        List<object> rows = [];
        foreach (DogeDebugger.Core.CrossReference.XrefDatabase database in _xrefDatabases.Values)
        {
            foreach (DogeDebugger.Core.CrossReference.FunctionEntry function in database.Functions)
            {
                int xrefCount = database.CountXrefsTo(function.StartRva);
                if (xrefCount < minXrefs)
                {
                    continue;
                }

                string name = function.DisplayName;
                if (filter.Length > 0 &&
                    !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rows.Add(new
                {
                    module = database.ModuleName,
                    name,
                    address = FormatAddress(database.RvaToVa(function.StartRva)),
                    rva = $"0x{function.StartRva:X}",
                    endRva = $"0x{function.EndRva:X}",
                    size = function.Size,
                    xrefs = xrefCount,
                    source = function.Source.ToString()
                });
            }
        }

        object[] ordered = rows
            .OrderByDescending(row => (int)row.GetType().GetProperty("xrefs")!.GetValue(row)!)
            .ToArray();
        return ValueTask.FromResult(JsonResult(new
        {
            total = ordered.Length,
            offset,
            count = Math.Min(limit, Math.Max(0, ordered.Length - offset)),
            functions = ordered.Skip(offset).Take(limit).ToArray()
        }));
    }

    private ValueTask<McpToolResult> FindInstructionReferencesAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No process is currently open."));
        }

        string kindText = (GetString(arguments, "target_kind") ?? "address").Trim();
        InstructionReferenceKind kind = kindText.Equals(
            "constant",
            StringComparison.OrdinalIgnoreCase)
                ? InstructionReferenceKind.Constant
                : InstructionReferenceKind.Address;
        string targetText = GetString(arguments, "target") ?? string.Empty;
        if (targetText.Length == 0)
        {
            return ValueTask.FromResult(ErrorResult("find_instruction_references requires a target."));
        }

        ulong targetAddress = 0;
        if (kind == InstructionReferenceKind.Address &&
            !TryParseAddressLiteral(targetText, out targetAddress))
        {
            IReadOnlyList<ModuleDescriptor> allModules = _session.EnumerateModules();
            int plus = targetText.LastIndexOf('+');
            if (plus > 0)
            {
                string moduleName = targetText[..plus];
                string offsetText = targetText[(plus + 1)..];
                ModuleDescriptor? module = allModules.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, moduleName, StringComparison.OrdinalIgnoreCase));
                if (module is not null &&
                    TryParseAddressLiteral(offsetText, out ulong offset))
                {
                    targetAddress = module.BaseAddress + offset;
                }
                else
                {
                    return ValueTask.FromResult(
                        ErrorResult($"Unable to resolve '{targetText}'."));
                }
            }
            else
            {
                return ValueTask.FromResult(
                    ErrorResult($"Unable to resolve '{targetText}'."));
            }
        }

        ulong value = targetAddress;
        if (kind == InstructionReferenceKind.Constant &&
            !TryParseAddressLiteral(targetText, out value))
        {
            return ValueTask.FromResult(
                ErrorResult($"Unable to parse constant '{targetText}'."));
        }

        List<ModuleDescriptor> scanModules = [];
        if (arguments.TryGetProperty("modules", out JsonElement moduleArray) &&
            moduleArray.ValueKind == JsonValueKind.Array)
        {
            IReadOnlyList<ModuleDescriptor> allModules = _session.EnumerateModules();
            foreach (JsonElement item in moduleArray.EnumerateArray())
            {
                string? name = item.GetString();
                ModuleDescriptor? module = allModules.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(name) &&
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (module is not null)
                {
                    scanModules.Add(module);
                }
            }
        }

        if (scanModules.Count == 0)
        {
            ModuleDescriptor? main = _session
                .EnumerateModules()
                .FirstOrDefault(module => module.IsMainModule);
            if (main is not null)
            {
                scanModules.Add(main);
            }
        }

        try
        {
            CommandSearchService service = new();
            CommandSearchScanResult result = service.SearchReferences(
                _session.Target,
                new InstructionReferenceSearchRequest
                {
                    Value = value,
                    Kind = kind,
                    Bitness = _session.Target.Is64Bit ? 64 : 32,
                    ScanModules = scanModules,
                    MaxResults = Math.Clamp(
                        GetInt32(arguments, "max_results") ?? 2000,
                        1,
                        200_000)
                },
                null,
                cancellationToken);
            return ValueTask.FromResult(JsonResult(new
            {
                target = FormatAddress(value),
                kind = kind.ToString(),
                truncated = result.Truncated,
                scannedBytes = result.ScannedBytes,
                total = result.Results.Count,
                references = result.Results.Select(item => new
                {
                    address = FormatAddress(item.Address),
                    module = item.ModuleName,
                    moduleOffset = FormatAddress(item.ModuleOffset),
                    bytes = Convert.ToHexString(item.Bytes),
                    instruction = item.Disassembly
                }).ToArray()
            }));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(ErrorResult(exception.Message));
        }
    }

    private ValueTask<McpToolResult> MemorySearchAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        string mode = (GetString(arguments, "mode") ?? "first").Trim().ToLowerInvariant();
        if (mode == "reset")
        {
            _activeSearchId = null;
            return ValueTask.FromResult(JsonResult(new { success = true, cleared = true }));
        }

        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No process is currently open."));
        }

        MemoryValueScanOptions options = ReadOriginalMemorySearchOptions(arguments);
        try
        {
            if (mode == "next")
            {
                if (_activeSearchId is null ||
                    !_valueScans.TryGetValue(_activeSearchId, out List<MemoryValueMatch>? previous))
                {
                    return ValueTask.FromResult(
                        ErrorResult("No active memory search. Run mode=first first."));
                }

                MemoryValueScanResult refined = _session.RefineValues(
                    previous,
                    options,
                    cancellationToken);
                _valueScans[_activeSearchId] = refined.Matches.ToList();
                return ValueTask.FromResult(JsonResult(new
                {
                    mode,
                    searchId = _activeSearchId,
                    truncated = refined.Truncated,
                    scannedBytes = refined.ScannedBytes,
                    count = refined.Matches.Count,
                    matches = refined.Matches.Take(500).Select(ToValueMatchDto).ToArray()
                }));
            }

            MemoryValueScanResult result = _session.ScanValues(options, cancellationToken);
            string searchId = Guid.NewGuid().ToString("N");
            _valueScans[searchId] = result.Matches.ToList();
            _activeSearchId = searchId;
            return ValueTask.FromResult(JsonResult(new
            {
                mode = "first",
                searchId,
                truncated = result.Truncated,
                scannedBytes = result.ScannedBytes,
                count = result.Matches.Count,
                matches = result.Matches.Take(500).Select(ToValueMatchDto).ToArray()
            }));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(ErrorResult(exception.Message));
        }
    }

    private ValueTask<McpToolResult> GetMemorySearchResultsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_activeSearchId is null ||
            !_valueScans.TryGetValue(_activeSearchId, out List<MemoryValueMatch>? matches))
        {
            return ValueTask.FromResult(JsonResult(new
            {
                total = 0,
                results = Array.Empty<object>()
            }));
        }

        int offset = Math.Max(0, GetInt32(arguments, "offset") ?? 0);
        int limit = Math.Clamp(GetInt32(arguments, "limit") ?? 200, 1, 5000);
        return ValueTask.FromResult(JsonResult(new
        {
            searchId = _activeSearchId,
            total = matches.Count,
            offset,
            results = matches.Skip(offset).Take(limit).Select(ToValueMatchDto).ToArray()
        }));
    }

    private static MemoryValueScanOptions ReadOriginalMemorySearchOptions(JsonElement arguments)
    {
        MemoryValueKind kind = (GetString(arguments, "value_type") ?? "four_bytes")
            .Trim()
            .ToLowerInvariant() switch
        {
            "byte" => MemoryValueKind.Byte,
            "two_bytes" => MemoryValueKind.Int16,
            "four_bytes" => MemoryValueKind.Int32,
            "eight_bytes" => MemoryValueKind.Int64,
            "float" => MemoryValueKind.Single,
            "double" => MemoryValueKind.Double,
            "string" => MemoryValueKind.Utf8String,
            "byte_array" => MemoryValueKind.ByteArray,
            _ => MemoryValueKind.Int32
        };
        MemoryValueComparison comparison = (GetString(arguments, "scan_type") ?? "exact")
            .Trim()
            .ToLowerInvariant() switch
        {
            "unknown" => MemoryValueComparison.UnknownInitialValue,
            "changed" => MemoryValueComparison.Changed,
            "unchanged" => MemoryValueComparison.Unchanged,
            "increased" => MemoryValueComparison.Increased,
            "decreased" => MemoryValueComparison.Decreased,
            "greater_than" => MemoryValueComparison.GreaterThan,
            "less_than" => MemoryValueComparison.LessThan,
            "between" => MemoryValueComparison.Between,
            "not_equal" => MemoryValueComparison.NotEqual,
            _ => MemoryValueComparison.Exact
        };
        string? value = GetString(arguments, "value");
        if (comparison == MemoryValueComparison.Between &&
            GetString(arguments, "value_min") is { } minimum)
        {
            value = minimum;
        }

        return new MemoryValueScanOptions
        {
            Kind = kind,
            Comparison = comparison,
            Value = value ?? "0",
            SecondValue = GetString(arguments, "value_max"),
            StartAddress = GetAddress(arguments, "start_address") ?? 0,
            EndAddress = GetAddress(arguments, "end_address") ?? ulong.MaxValue,
            Alignment = Math.Clamp(GetInt32(arguments, "alignment") ?? 1, 1, 16),
            MaximumResults = 1_000_000,
            WritableOnly = GetBoolean(arguments, "writable") ?? false,
            ExecutableOnly = GetBoolean(arguments, "executable") ?? false,
            IgnoreCase = GetBoolean(arguments, "ignore_case") ?? false
        };
    }

    private ValueTask<McpToolResult> WriteExportFileAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? relativePath = GetString(arguments, "relative_path");
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return ValueTask.FromResult(ErrorResult("write_export_file requires relative_path."));
        }

        if (!TryResolveExportPath(relativePath, out string? fullPath, out string? error))
        {
            return ValueTask.FromResult(ErrorResult(error!));
        }

        bool overwrite = GetBoolean(arguments, "overwrite") ?? false;
        if (File.Exists(fullPath) && !overwrite)
        {
            return ValueTask.FromResult(
                ErrorResult("The file already exists. Pass overwrite=true to replace it."));
        }

        try
        {
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            byte[] payload;
            if (GetString(arguments, "base64") is { Length: > 0 } base64)
            {
                payload = Convert.FromBase64String(base64);
            }
            else if (arguments.TryGetProperty("text", out JsonElement textValue))
            {
                string content = textValue.ValueKind == JsonValueKind.Array
                    ? string.Join(
                        Environment.NewLine,
                        textValue.EnumerateArray().Select(item => item.GetString() ?? string.Empty))
                    : textValue.ToString();
                payload = Encoding.UTF8.GetBytes(content);
            }
            else if (arguments.TryGetProperty("json", out JsonElement jsonValue))
            {
                payload = JsonSerializer.SerializeToUtf8Bytes(
                    jsonValue,
                    new JsonSerializerOptions { WriteIndented = true });
            }
            else
            {
                payload = [];
            }

            File.WriteAllBytes(fullPath, payload);
            return ValueTask.FromResult(JsonResult(new
            {
                success = true,
                path = fullPath,
                bytes = payload.Length
            }));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(ErrorResult(exception.Message));
        }
    }

    private bool TryResolveExportPath(
        string relativePath,
        out string fullPath,
        out string? error)
    {
        fullPath = string.Empty;
        error = null;
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Exports"));
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            error = "relative_path must stay inside the Exports folder.";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private ValueTask<McpToolResult> DumpMemoryAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No process is currently open."));
        }

        string scope = (GetString(arguments, "scope") ?? "range")
            .Trim()
            .ToLowerInvariant();
        ulong start;
        ulong size;
        if (scope == "module" || scope == "main_module")
        {
            string? moduleName = GetString(arguments, "module_name");
            ModuleDescriptor? module;
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                module = _session
                    .EnumerateModules()
                    .FirstOrDefault(item => item.IsMainModule);
                if (module is null)
                {
                    return ValueTask.FromResult(ErrorResult("Main module was not found."));
                }
            }
            else if (!TryFindModule(
                         moduleName,
                         out module,
                         out McpToolResult? moduleError))
            {
                return ValueTask.FromResult(moduleError!);
            }

            start = module!.BaseAddress;
            size = module.Size;
        }
        else
        {
            if (!TryGetAddress(arguments, "start_address", out start))
            {
                return ValueTask.FromResult(
                    ErrorResult("dump_memory requires start_address for range scope."));
            }

            string? sizeText = GetString(arguments, "size");
            if (sizeText is null &&
                arguments.TryGetProperty("size", out JsonElement sizeValue) &&
                sizeValue.ValueKind == JsonValueKind.Number)
            {
                sizeText = sizeValue.ToString();
            }

            if (string.IsNullOrWhiteSpace(sizeText) ||
                !TryParseAddressLiteral(sizeText, out size) ||
                size == 0)
            {
                return ValueTask.FromResult(
                    ErrorResult("dump_memory requires a valid size."));
            }
        }

        ulong maximum = 256UL * 1024 * 1024;
        if (GetString(arguments, "max_total_bytes") is { Length: > 0 } maxText &&
            TryParseAddressLiteral(maxText, out ulong parsedMaximum) &&
            parsedMaximum > 0)
        {
            maximum = parsedMaximum;
        }

        if (size > maximum)
        {
            return ValueTask.FromResult(
                ErrorResult($"Requested dump exceeds the {maximum} byte limit."));
        }

        string fileName = GetString(arguments, "file_name") ?? $"dump_{start:X}.bin";
        if (!TryResolveExportPath(
                Path.Combine("MemoryDump", fileName),
                out string? fullPath,
                out string? pathError))
        {
            return ValueTask.FromResult(ErrorResult(pathError!));
        }

        bool overwrite = GetBoolean(arguments, "overwrite") ?? false;
        if (File.Exists(fullPath) && !overwrite)
        {
            return ValueTask.FromResult(
                ErrorResult("The dump file already exists. Pass overwrite=true to replace it."));
        }

        try
        {
            byte[] bytes = _session.ReadBytes(start, (int)Math.Min(size, int.MaxValue));
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(fullPath, bytes);
            return ValueTask.FromResult(JsonResult(new
            {
                success = true,
                scope,
                startAddress = FormatAddress(start),
                requestedSize = size,
                bytes = bytes.Length,
                path = fullPath
            }));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(ErrorResult(exception.Message));
        }
    }

    private bool TryFindModule(
        string? moduleName,
        out ModuleDescriptor? module,
        out McpToolResult? error)
    {
        module = null;
        error = null;
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            error = ErrorResult("A module name is required.");
            return false;
        }

        if (!_session.Target.IsOpen)
        {
            error = ErrorResult("No process is currently open.");
            return false;
        }

        module = _session.EnumerateModules().FirstOrDefault(candidate =>
            string.Equals(candidate.Name, moduleName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Path.GetFileName(candidate.FilePath),
                moduleName,
                StringComparison.OrdinalIgnoreCase));
        if (module is null)
        {
            error = ErrorResult($"Module '{moduleName}' was not found.");
            return false;
        }

        return true;
    }

    private ValueTask<McpToolResult> GetStatusAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(JsonResult(new
        {
            open = _session.Target.IsOpen,
            debugging = _session.Debugger.IsDebugging,
            paused = _session.Debugger.IsPaused,
            pid = _session.Target.ProcessId,
            process = _session.Target.ProcessName,
            path = _session.Target.FilePath,
            x64 = _session.Target.Is64Bit,
            currentThreadId = _session.Debugger.CurrentThreadId,
            instructionPointer = FormatAddress(_session.Debugger.CurrentInstructionPointer),
            breakpoints = _session.Breakpoints.Entries.Count
        }));
    }

    private ValueTask<McpToolResult> GetExceptionHandlersAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(JsonResult(new
            {
                success = false,
                error = "No target process is open."
            }));
        }

        try
        {
            DogeDebugger.Core.ExceptionHandler.ExceptionHandlerScanResult result =
                _session.ScanExceptionHandlers(cancellationToken);
            return ValueTask.FromResult(JsonResult(new
            {
                success = true,
                veh = result.VehEntries.Select(entry => new
                {
                    index = entry.Index,
                    type = entry.TypeName,
                    nodeAddress = FormatAddress(entry.NodeAddress),
                    encodedPointer = FormatAddress(entry.EncodedPointer),
                    decodedAddress = FormatAddress(entry.DecodedAddress),
                    module = entry.ModuleName,
                    symbol = entry.SymbolName
                }).ToArray(),
                pdata = result.PdataEntries.Select(entry => new
                {
                    index = entry.Index,
                    module = entry.ModuleName,
                    functionStart = FormatAddress(entry.FunctionStart),
                    functionEnd = FormatAddress(entry.FunctionEnd),
                    handler = FormatAddress(entry.HandlerAddress),
                    symbol = entry.HandlerSymbol,
                    flags = entry.FlagNames
                }).ToArray(),
                hooks = result.HookEntries.Select(entry => new
                {
                    function = entry.FunctionName,
                    address = FormatAddress(entry.Address),
                    expected = entry.ExpectedInstruction,
                    actual = entry.ActualInstruction,
                    bytes = entry.ActualBytes,
                    isHooked = entry.IsHooked,
                    status = entry.Status
                }).ToArray(),
                pdataCandidateCount = result.PdataCandidateCount,
                pdataTruncated = result.PdataTruncated
            }));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(JsonResult(new
            {
                success = false,
                error = exception.Message,
                details = exception.ToString()
            }));
        }
    }

    private ValueTask<McpToolResult> ScanRttiAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(JsonResult(new
            {
                success = false,
                error = "No target process is open."
            }));
        }

        try
        {
            DogeDebugger.Core.Rtti.RttiScanResult result =
                _session.ScanRtti(cancellationToken);
            string? moduleFilter = GetString(arguments, "module");
            int maxResults = Math.Clamp(
                GetInt32(arguments, "maxResults") ?? 5000,
                1,
                50_000);
            IEnumerable<DogeDebugger.Core.Rtti.RttiTypeInfo> types = result.Types;
            if (!string.IsNullOrWhiteSpace(moduleFilter))
            {
                types = types.Where(type =>
                    type.ModuleName.Contains(
                        moduleFilter,
                        StringComparison.OrdinalIgnoreCase));
            }

            return ValueTask.FromResult(JsonResult(new
            {
                success = true,
                scannedModuleCount = result.ScannedModuleCount,
                candidateTypeCount = result.CandidateTypeCount,
                truncated = result.Truncated,
                totalTypeCount = result.Types.Count,
                types = types.Take(maxResults).Select(type => new
                {
                    name = type.Name,
                    decoratedName = type.DecoratedName,
                    module = type.ModuleName,
                    kind = type.Kind,
                    typeDescriptor = FormatAddress(type.TypeDescriptorAddress),
                    completeObjectLocator = FormatAddress(type.CompleteObjectLocatorAddress),
                    classHierarchy = FormatAddress(type.ClassHierarchyAddress),
                    baseClassArray = FormatAddress(type.BaseClassArrayAddress),
                    vftable = type.VftableAddress == 0
                        ? null
                        : FormatAddress(type.VftableAddress),
                    signature = type.Signature,
                    offset = type.Offset,
                    constructorDisplacement = type.ConstructorDisplacement,
                    baseClassCount = type.BaseClassCount,
                    baseClasses = type.BaseClasses.Select(baseClass => new
                    {
                        name = baseClass.Name,
                        decoratedName = baseClass.DecoratedName,
                        typeDescriptor = FormatAddress(baseClass.TypeDescriptorAddress),
                        containedBaseCount = baseClass.ContainedBaseCount,
                        memberDisplacement = baseClass.MemberDisplacement,
                        vtableDisplacement = baseClass.VtableDisplacement,
                        vbtableDisplacement = baseClass.VbtableDisplacement,
                        attributes = baseClass.Attributes
                    }).ToArray()
                }).ToArray()
            }));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(JsonResult(new
            {
                success = false,
                error = exception.Message,
                details = exception.ToString()
            }));
        }
    }

    private ValueTask<McpToolResult> CreateStructAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? name = GetString(arguments, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValueTask.FromResult(ErrorResult("create_struct requires a struct name."));
        }

        if (!TryGetAddress(arguments, "baseAddress", out ulong baseAddress))
        {
            return ValueTask.FromResult(
                ErrorResult("create_struct requires a valid baseAddress."));
        }

        if (!arguments.TryGetProperty("fields", out JsonElement fieldsElement) ||
            fieldsElement.ValueKind != JsonValueKind.Array ||
            fieldsElement.GetArrayLength() == 0)
        {
            return ValueTask.FromResult(
                ErrorResult("At least one field is required."));
        }

        bool is64Bit = _session.Target.IsOpen && _session.Target.Is64Bit;
        List<RttiFieldDefinition> fields = [];
        foreach (JsonElement fieldElement in fieldsElement.EnumerateArray())
        {
            if (!TryReadRttiField(fieldElement, is64Bit, out RttiFieldDefinition? field, out string error))
            {
                return ValueTask.FromResult(ErrorResult(error));
            }

            fields.Add(field!);
        }

        RttiClassDefinition definition = _session.RttiWorkspace.CreateStruct(
            name,
            baseAddress,
            GetString(arguments, "comment") ?? string.Empty,
            fields,
            is64Bit);
        return ValueTask.FromResult(JsonResult(new
        {
            success = true,
            name = definition.Name,
            baseAddress = FormatAddress(definition.BaseAddress),
            totalSize = definition.GetSize(is64Bit),
            fieldCount = definition.Fields.Count
        }));
    }

    private ValueTask<McpToolResult> ListStructsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool is64Bit = !_session.Target.IsOpen || _session.Target.Is64Bit;
        IReadOnlyList<RttiClassDefinition> classes = _session.RttiWorkspace.GetClasses();
        return ValueTask.FromResult(JsonResult(new
        {
            success = true,
            count = classes.Count,
            structs = classes.Select(definition => new
            {
                name = definition.Name,
                baseAddress = FormatAddress(definition.BaseAddress),
                totalSize = definition.GetSize(is64Bit),
                comment = definition.Comment,
                fields = definition.Fields.Select(field => ToRttiFieldDto(field, is64Bit)).ToArray()
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> AddStructFieldAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? structName = GetString(arguments, "structName");
        if (string.IsNullOrWhiteSpace(structName))
        {
            return ValueTask.FromResult(
                ErrorResult("add_struct_field requires structName."));
        }

        bool is64Bit = !_session.Target.IsOpen || _session.Target.Is64Bit;
        if (!TryReadRttiField(arguments, is64Bit, out RttiFieldDefinition? field, out string error))
        {
            return ValueTask.FromResult(ErrorResult(error));
        }

        bool added = _session.RttiWorkspace.AddField(
            structName,
            field!,
            is64Bit,
            GetInt32(arguments, "insertIndex"));
        return ValueTask.FromResult(added
            ? JsonResult(new
            {
                success = true,
                structName,
                field = ToRttiFieldDto(field!, is64Bit)
            })
            : ErrorResult($"Struct not found: {structName}"));
    }

    private ValueTask<McpToolResult> RemoveStructAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? name = GetString(arguments, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValueTask.FromResult(ErrorResult("remove_struct requires a name."));
        }

        bool removed = _session.RttiWorkspace.RemoveStruct(name);
        return ValueTask.FromResult(removed
            ? JsonResult(new { success = true, name })
            : ErrorResult($"Struct not found: {name}"));
    }

    private ValueTask<McpToolResult> GetStructDataAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No target process is open."));
        }

        string? name = GetString(arguments, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValueTask.FromResult(ErrorResult("get_struct_data requires a name."));
        }

        RttiClassDefinition? definition = _session.RttiWorkspace.FindClass(name);
        if (definition is null)
        {
            return ValueTask.FromResult(ErrorResult($"Struct not found: {name}"));
        }

        ulong? address = GetAddress(arguments, "address");
        RttiInstance instance = _session.ReadRttiInstance(definition, address);
        IReadOnlyList<RttiFieldValue> values = instance.HasMemory
            ? RttiMemoryReader.ReadValues(_session.Target, instance)
            : [];
        return ValueTask.FromResult(JsonResult(new
        {
            success = true,
            name = definition.Name,
            baseAddress = FormatAddress(instance.BaseAddress),
            status = instance.StatusText,
            readError = instance.ReadError,
            fields = values.Select(value => new
            {
                offset = value.OffsetText,
                address = value.AddressText,
                type = value.TypeText,
                name = value.Name,
                value = value.Value,
                comment = value.Comment,
                readable = value.IsReadable
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> RttiProjectFileAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? action = GetString(arguments, "action");
        string? path = GetString(arguments, "path");
        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(path))
        {
            return ValueTask.FromResult(
                ErrorResult("rtti_project_file requires action and path."));
        }

        try
        {
            if (string.Equals(action, "save", StringComparison.OrdinalIgnoreCase))
            {
                _session.RttiWorkspace.SaveProject(path);
                return ValueTask.FromResult(JsonResult(new
                {
                    success = true,
                    action = "save",
                    path = Path.GetFullPath(path)
                }));
            }

            if (string.Equals(action, "load", StringComparison.OrdinalIgnoreCase))
            {
                bool is64Bit = !_session.Target.IsOpen || _session.Target.Is64Bit;
                _session.RttiWorkspace.LoadProject(path, is64Bit);
                return ValueTask.FromResult(JsonResult(new
                {
                    success = true,
                    action = "load",
                    path = Path.GetFullPath(path),
                    count = _session.RttiWorkspace.GetClasses().Count
                }));
            }

            return ValueTask.FromResult(
                ErrorResult("rtti_project_file action must be save or load."));
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            JsonException)
        {
            return ValueTask.FromResult(ErrorResult(exception.Message));
        }
    }

    private ValueTask<McpToolResult> RttiAutoGuessAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.Target.IsOpen)
        {
            return ValueTask.FromResult(ErrorResult("No target process is open."));
        }

        if (!TryGetAddress(arguments, "address", out ulong address) || address == 0)
        {
            return ValueTask.FromResult(
                ErrorResult("rtti_auto_guess requires a valid non-zero address."));
        }

        int length = Math.Clamp(GetInt32(arguments, "length") ?? 256, 1, 65_536);
        string name = GetString(arguments, "name") ?? $"Auto_{address:X}";
        RttiClassDefinition definition = _session.RttiWorkspace.AutoGuess(
            _session.Target,
            name,
            address,
            length,
            GetString(arguments, "comment") ?? string.Empty);
        return ValueTask.FromResult(JsonResult(new
        {
            success = true,
            name = definition.Name,
            baseAddress = FormatAddress(definition.BaseAddress),
            totalSize = definition.GetSize(_session.Target.Is64Bit),
            fieldCount = definition.Fields.Count,
            fields = definition.Fields.Select(field =>
                ToRttiFieldDto(field, _session.Target.Is64Bit)).ToArray()
        }));
    }

    private ValueTask<McpToolResult> GetUnrealStatusAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnrealSession? session = _session.UnrealEngine.Current;
        return ValueTask.FromResult(JsonResult(new
        {
            success = true,
            targetOpen = _session.Target.IsOpen,
            state = session is null
                ? "idle"
                : session.Diagnostics.HasProblems
                    ? "incomplete"
                    : "ready",
            module = session?.Module.Name,
            modulePath = session?.Module.FilePath,
            objectCount = session?.ObjectCount ?? 0,
            packageCount = session?.Packages.Count ?? 0,
            typeCount = session?.Types.Count ?? 0,
            classCount = session?.ClassCount ?? 0,
            structCount = session?.StructCount ?? 0,
            functionCount = session?.FunctionCount ?? 0,
            enumCount = session?.EnumCount ?? 0,
            truncated = session?.Truncated ?? false,
            diagnostics = session?.Diagnostics.Entries.Select(ToUnrealDiagnosticDto).ToArray()
        }));
    }

    private async ValueTask<McpToolResult> ScanUnrealAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!_session.Target.IsOpen)
        {
            return ErrorResult("No target process is open.");
        }

        UnrealObjectArrayKind objectArrayKind = UnrealObjectArrayKind.Unknown;
        string? kindText = GetString(arguments, "objectArrayKind");
        if (!string.IsNullOrWhiteSpace(kindText) &&
            !Enum.TryParse(kindText, ignoreCase: true, out objectArrayKind))
        {
            return ErrorResult($"Unknown objectArrayKind: {kindText}");
        }

        try
        {
            UnrealSession session = await _session.UnrealEngine.ScanAsync(
                    new UnrealScanOptions
                    {
                        ModuleName = GetString(arguments, "module"),
                        UseCache = GetBoolean(arguments, "useCache") ?? true,
                        AutoDetect = GetBoolean(arguments, "autoDetect") ?? true,
                        PersistCache = GetBoolean(arguments, "persistCache") ?? true,
                        NamePoolAddress = GetAddress(arguments, "namePoolAddress") ?? 0,
                        NamePoolBlockArrayAddress =
                            GetAddress(arguments, "namePoolBlockArrayAddress") ?? 0,
                        FNameEntryStride = GetInt32(arguments, "fNameEntryStride") ?? 0,
                        FNameBlockOffsetBits =
                            GetInt32(arguments, "fNameBlockOffsetBits") ?? 0,
                        GObjectsAddress = GetAddress(arguments, "gObjectsAddress") ?? 0,
                        ObjectArrayKind = objectArrayKind,
                        GWorldAddress = GetAddress(arguments, "gWorldAddress") ?? 0,
                        GEngineAddress = GetAddress(arguments, "gEngineAddress") ?? 0
                    },
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return JsonResult(new
            {
                success = !session.Diagnostics.HasProblems,
                state = session.Diagnostics.HasProblems ? "incomplete" : "ready",
                module = session.Module.Name,
                objectCount = session.ObjectCount,
                packageCount = session.Packages.Count,
                typeCount = session.Types.Count,
                classCount = session.ClassCount,
                structCount = session.StructCount,
                functionCount = session.FunctionCount,
                enumCount = session.EnumCount,
                truncated = session.Truncated,
                diagnostics = session.Diagnostics.Entries.Select(ToUnrealDiagnosticDto).ToArray()
            });
        }
        catch (Exception exception)
        {
            return JsonResult(new
            {
                success = false,
                error = exception.Message,
                details = exception.ToString()
            });
        }
    }

    private ValueTask<McpToolResult> GetUnrealOffsetsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnrealSession? session = _session.UnrealEngine.Current;
        if (session is null)
        {
            return ValueTask.FromResult(ErrorResult(
                "No Unreal Engine session. Call ue_scan first."));
        }

        return ValueTask.FromResult(JsonResult(new
        {
            success = true,
            module = session.Module.Name,
            offsets = ToUnrealOffsetsDto(session.Offsets)
        }));
    }

    private ValueTask<McpToolResult> ListUnrealObjectsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnrealSession? session = _session.UnrealEngine.Current;
        if (session is null)
        {
            return ValueTask.FromResult(ErrorResult(
                "No Unreal Engine session. Call ue_scan first."));
        }

        string? filter = GetString(arguments, "filter");
        string? kindText = GetString(arguments, "kind");
        UnrealObjectKind? kind = null;
        if (!string.IsNullOrWhiteSpace(kindText))
        {
            if (!Enum.TryParse(kindText, ignoreCase: true, out UnrealObjectKind parsedKind))
            {
                return ValueTask.FromResult(ErrorResult($"Unknown kind: {kindText}"));
            }

            kind = parsedKind;
        }

        int offset = Math.Max(GetInt32(arguments, "offset") ?? 0, 0);
        int limit = Math.Clamp(GetInt32(arguments, "limit") ?? 1000, 1, 100_000);
        IEnumerable<UnrealObjectInfo> objects = session.Objects;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            objects = objects.Where(item => item.Matches(filter));
        }

        if (kind.HasValue)
        {
            objects = objects.Where(item => item.Kind == kind.Value);
        }

        UnrealObjectInfo[] filtered = objects.ToArray();
        return ValueTask.FromResult(JsonResult(new
        {
            success = true,
            totalCount = filtered.Length,
            offset,
            objects = filtered.Skip(offset).Take(limit).Select(ToUnrealObjectDto).ToArray()
        }));
    }

    private ValueTask<McpToolResult> GetUnrealTypeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnrealSession? session = _session.UnrealEngine.Current;
        if (session is null)
        {
            return ValueTask.FromResult(ErrorResult(
                "No Unreal Engine session. Call ue_scan first."));
        }

        UnrealTypeInfo? type = null;
        if (TryGetAddress(arguments, "address", out ulong address))
        {
            type = session.Types.FirstOrDefault(item => item.Object.Address == address);
        }

        string? name = GetString(arguments, "name");
        if (type is null && !string.IsNullOrWhiteSpace(name))
        {
            type = session.Types.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) ??
                session.Types.FirstOrDefault(item =>
                    item.FullName.Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        return type is null
            ? ValueTask.FromResult(ErrorResult("Unreal Engine type not found."))
            : ValueTask.FromResult(JsonResult(new
            {
                success = true,
                type = ToUnrealTypeDto(type),
                inheritance = type.Kind == UnrealObjectKind.Enum
                    ? []
                    : _session.UnrealEngine
                        .GetInheritance(type.Object)
                        .Select(item => new
                        {
                            level = item.Level,
                            name = item.Name,
                            address = item.AddressHex
                        })
                        .ToArray(),
                members = _session.UnrealEngine
                    .GetMembers(type.Object)
                    .Select(member => ToUnrealMemberDto(
                        member,
                        _session.UnrealEngine.GetMemberTypeName(member)))
                    .ToArray(),
                functions = _session.UnrealEngine
                    .GetFunctions(type.Object)
                    .Select(member => ToUnrealMemberDto(
                        member,
                        _session.UnrealEngine.GetMemberTypeName(member)))
                    .ToArray(),
                enumEntries = type.Kind == UnrealObjectKind.Enum
                    ? _session.UnrealEngine
                        .GetEnumEntries(type.Object)
                        .Select(entry => new
                        {
                            name = entry.Name,
                            shortName = entry.ShortName,
                            value = entry.Value
                        })
                        .ToArray()
                    : null
            }));
    }

    private ValueTask<McpToolResult> ListUnrealActorsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_session.UnrealEngine.Current is null)
        {
            return ValueTask.FromResult(ErrorResult(
                "No Unreal Engine session. Call ue_scan first."));
        }

        int limit = Math.Clamp(GetInt32(arguments, "limit") ?? 10_000, 1, 100_000);
        IReadOnlyList<UnrealWorldActorInfo> actors =
            _session.UnrealEngine.ReadWorldActors(cancellationToken);
        return ValueTask.FromResult(JsonResult(new
        {
            success = true,
            count = actors.Count,
            actors = actors.Take(limit).Select(actor => new
            {
                index = actor.Index,
                address = actor.AddressHex,
                name = actor.Name,
                className = actor.ClassName,
                pathName = actor.PathName
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> InspectUnrealObjectAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnrealSession? session = _session.UnrealEngine.Current;
        if (session is null)
        {
            return ValueTask.FromResult(
                ErrorResult("No Unreal Engine session. Call ue_scan first."));
        }

        string? path = GetString(arguments, "path");
        UnrealObjectInfo? objectInfo = TryGetAddress(
            arguments,
            "address",
            out ulong address)
                ? _session.UnrealEngine.FindObject(address)
                : null;
        if (objectInfo is null && !string.IsNullOrWhiteSpace(path))
        {
            objectInfo = _session.UnrealEngine.FindObjectByPath(path);
        }

        if (objectInfo is null)
        {
            return ValueTask.FromResult(ErrorResult(
                "Object address or path is not present in the active Unreal Engine session."));
        }

        bool includeMembers = GetBoolean(arguments, "includeMembers") ?? true;
        bool includeValues = GetBoolean(arguments, "includeValues") ?? true;
        bool includeInherited = GetBoolean(arguments, "includeInherited") ?? true;
        string? filter = GetString(arguments, "filter");
        int offset = Math.Max(GetInt32(arguments, "offset") ?? 0, 0);
        int limit = Math.Clamp(GetInt32(arguments, "limit") ?? 200, 1, 2000);
        UnrealValueReadOptions valueOptions = new()
        {
            MaxStringLength = Math.Clamp(
                GetInt32(arguments, "maxStringLength") ?? 256,
                0,
                4096),
            ArrayPreview = Math.Clamp(
                GetInt32(arguments, "arrayPreview") ?? 8,
                0,
                64),
            StructDepth = Math.Clamp(
                GetInt32(arguments, "structDepth") ?? 1,
                0,
                3)
        };

        UnrealObjectInfo? classInfo = _session.UnrealEngine.FindObject(
            objectInfo.ClassAddress);
        IReadOnlyList<UnrealInheritanceInfo> inheritance = classInfo is null
            ? []
            : _session.UnrealEngine.GetInheritance(classInfo);
        IReadOnlyList<UnrealMemberInfo> allMembers = [];
        if (includeMembers && classInfo is not null)
        {
            allMembers = _session.UnrealEngine.GetMembers(
                classInfo,
                includeInherited);
            if (!string.IsNullOrWhiteSpace(filter))
            {
                allMembers = allMembers
                    .Where(member =>
                        member.Name.Contains(
                            filter,
                            StringComparison.OrdinalIgnoreCase) ||
                        member.TypeName.Contains(
                            filter,
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
        }

        UnrealMemberInfo[] page = allMembers
            .Skip(offset)
            .Take(limit)
            .ToArray();
        IReadOnlyList<UnrealMemberValueEntry>? values = includeValues
            ? _session.UnrealEngine.ReadObjectValues(
                objectInfo,
                page,
                valueOptions)
            : null;
        bool hasMore = offset + page.Length < allMembers.Count;

        return ValueTask.FromResult(JsonResult(new
        {
            success = true,
            @object = ToUnrealObjectDto(objectInfo),
            superChain = inheritance.Select(item => new
            {
                level = item.Level,
                name = item.Name,
                address = item.AddressHex
            }).ToArray(),
            total = allMembers.Count,
            offset,
            limit,
            returned = page.Length,
            hasMore,
            nextOffset = hasMore ? offset + page.Length : (int?)null,
            members = includeMembers
                ? values is not null
                    ? values.Select(ToUnrealMemberValueDto).ToArray()
                    : page.Select(member => ToUnrealMemberDto(
                        member,
                        _session.UnrealEngine.GetMemberTypeName(member))).ToArray()
                : null
        }));
    }

    private static bool TryReadRttiField(
        JsonElement element,
        bool is64Bit,
        out RttiFieldDefinition? field,
        out string error)
    {
        field = null;
        error = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "Each field must be an object.";
            return false;
        }

        string? typeName = GetString(element, "type");
        if (string.IsNullOrWhiteSpace(typeName) ||
            !Enum.TryParse(typeName, ignoreCase: true, out RttiNodeType type))
        {
            error = $"Unknown field type: {typeName}";
            return false;
        }

        if (type is RttiNodeType.Class or RttiNodeType.Instance)
        {
            error = $"Cannot use {type} as a field type in create_struct.";
            return false;
        }

        RttiNodeType? pointedType = null;
        string? pointedTypeName = GetString(element, "pointedType");
        if (!string.IsNullOrWhiteSpace(pointedTypeName))
        {
            if (!Enum.TryParse(
                    pointedTypeName,
                    ignoreCase: true,
                    out RttiNodeType parsedPointedType))
            {
                error = $"Unknown pointed field type: {pointedTypeName}";
                return false;
            }

            pointedType = parsedPointedType;
        }

        int length = GetInt32(element, "length") ?? 0;
        if (type is RttiNodeType.Utf8Text or RttiNodeType.Utf16Text && length <= 0)
        {
            length = 256;
        }

        field = new RttiFieldDefinition
        {
            Name = GetString(element, "name") ?? string.Empty,
            Comment = GetString(element, "comment") ?? string.Empty,
            Offset = GetInt32(element, "offset") ?? 0,
            Type = type,
            Length = Math.Clamp(length, 0, 65_536),
            PointerSize = GetInt32(element, "pointerSize") ??
                (type == RttiNodeType.Pointer ? is64Bit ? 8 : 4 : 0),
            PointedType = pointedType,
            PointedClass = GetString(element, "pointedClass"),
            ArrayIndex = GetInt32(element, "arrayIndex")
        };
        return true;
    }

    private static object ToRttiFieldDto(RttiFieldDefinition field, bool is64Bit) => new
    {
        offset = $"0x{field.Offset:X}",
        type = field.Type.ToString(),
        size = field.GetSize(is64Bit),
        name = field.Name,
        comment = field.Comment,
        length = field.Length,
        pointerSize = field.PointerSize,
        pointedType = field.PointedType?.ToString(),
        pointedClass = field.PointedClass,
        arrayIndex = field.ArrayIndex
    };

    private static object ToUnrealDiagnosticDto(UnrealDiagnosticEntry entry) => new
    {
        stage = entry.Stage,
        level = entry.Level.ToString(),
        levelText = entry.LevelText,
        message = entry.Message
    };

    private static object ToUnrealObjectDto(UnrealObjectInfo item) => new
    {
        index = item.Index,
        indexHex = item.IndexHex,
        address = item.AddressHex,
        classAddress = item.ClassAddressHex,
        outerAddress = item.OuterAddressHex,
        name = item.Name,
        className = item.ClassName,
        outerName = item.OuterName,
        packageName = item.PackageName,
        pathName = item.PathName,
        fullName = item.FullName,
        kind = item.Kind.ToString(),
        kindText = item.KindText
    };

    private static object ToUnrealTypeDto(UnrealTypeInfo type) => new
    {
        name = type.Name,
        fullName = type.FullName,
        className = type.ClassName,
        address = type.AddressHex,
        kind = type.Kind.ToString(),
        kindText = type.KindText,
        @object = ToUnrealObjectDto(type.Object)
    };

    private static object ToUnrealMemberDto(
        UnrealMemberInfo member,
        string? displayType = null) => new
    {
        source = member.Source,
        address = member.AddressHex,
        name = member.Name,
        type = string.IsNullOrWhiteSpace(displayType)
            ? member.TypeName
            : displayType,
        offset = member.Offset,
        offsetHex = member.OffsetHex,
        size = member.Size,
        sizeText = member.SizeText,
        arrayDim = member.ArrayDim,
        flags = member.FlagsHex,
        nativeAddress = member.NativeAddressHex,
        declaringType = member.DeclaringType,
        inherited = member.IsInherited
    };

    private static object ToUnrealMemberValueDto(
        UnrealMemberValueEntry entry)
    {
        UnrealMemberValue value = entry.Value;
        return new
        {
            name = entry.Member.Name,
            type = string.IsNullOrWhiteSpace(entry.TypeName)
                ? entry.Member.TypeName
                : entry.TypeName,
            offset = entry.Member.OffsetHex,
            size = entry.Member.Size,
            arrayDim = entry.Member.ArrayDim > 1
                ? entry.Member.ArrayDim
                : (int?)null,
            declaringType = entry.Member.IsInherited
                ? entry.Member.DeclaringType
                : null,
            propertyType = entry.Member.TypeName,
            propertyAddress = entry.Member.AddressHex,
            flags = entry.Member.FlagsHex,
            value = ToUnrealValueToken(value),
            target = GetUnrealValueTarget(value),
            kind = value.Kind.ToString(),
            address = FormatAddress(value.Address),
            items = value.Kind == UnrealValueKind.Array
                ? value.Children.Select(ToUnrealNestedValueDto).ToArray()
                : null,
            members = value.Kind == UnrealValueKind.Struct
                ? value.Children.Select(ToUnrealNestedValueDto).ToArray()
                : null,
            error = value.Error
        };
    }

    private static object ToUnrealNestedValueDto(UnrealMemberValue value) => new
    {
        name = value.DisplayName,
        type = value.TypeName,
        kind = value.Kind.ToString(),
        address = FormatAddress(value.Address),
        value = ToUnrealValueToken(value),
        target = GetUnrealValueTarget(value),
        items = value.Kind == UnrealValueKind.Array
            ? value.Children.Select(ToUnrealNestedValueDto).ToArray()
            : null,
        members = value.Kind == UnrealValueKind.Struct
            ? value.Children.Select(ToUnrealNestedValueDto).ToArray()
            : null,
        error = value.Error
    };

    private static object? ToUnrealValueToken(UnrealMemberValue value) => value.Kind switch
    {
        UnrealValueKind.Bool or
        UnrealValueKind.Int8 or
        UnrealValueKind.Int16 or
        UnrealValueKind.Int32 or
        UnrealValueKind.Int64 or
        UnrealValueKind.UInt8 or
        UnrealValueKind.UInt16 or
        UnrealValueKind.UInt32 or
        UnrealValueKind.UInt64 or
        UnrealValueKind.Float or
        UnrealValueKind.Double => value.Value,
        _ => value.DisplayName
    };

    private static string? GetUnrealValueTarget(UnrealMemberValue value) =>
        value.Kind == UnrealValueKind.Object &&
        value.Value is ulong address &&
        address != 0
            ? FormatAddress(address)
            : null;

    private static object ToUnrealOffsetsDto(UnrealOffsets offsets) => new
    {
        pointerSize = offsets.PointerSize,
        namePoolKind = offsets.NamePoolKind.ToString(),
        namePoolAddress = FormatAddress(offsets.NamePoolAddress),
        namePoolBlockArrayAddress = FormatAddress(offsets.NamePoolBlockArrayAddress),
        namePoolFirstBlockAddress = FormatAddress(offsets.NamePoolFirstBlockAddress),
        fNameEntryStride = offsets.FNameEntryStride,
        fNameBlockOffsetBits = offsets.FNameBlockOffsetBits,
        gObjectsAddress = FormatAddress(offsets.GObjectsAddress),
        objectArrayKind = offsets.ObjectArrayKind.ToString(),
        objectArrayObjectsOffset = offsets.ObjectArrayObjectsOffset,
        objectArrayNumOffset = offsets.ObjectArrayNumOffset,
        objectArrayMaxOffset = offsets.ObjectArrayMaxOffset,
        objectArrayNumChunksOffset = offsets.ObjectArrayNumChunksOffset,
        objectArrayMaxChunksOffset = offsets.ObjectArrayMaxChunksOffset,
        objectArrayChunkSize = offsets.ObjectArrayChunkSize,
        fUObjectItemSize = offsets.FUObjectItemSize,
        fUObjectItemObjectOffset = offsets.FUObjectItemObjectOffset,
        gWorldAddress = FormatAddress(offsets.GWorldAddress),
        gEngineAddress = FormatAddress(offsets.GEngineAddress),
        uObjectFlags = offsets.UObjectFlags,
        uObjectIndex = offsets.UObjectIndex,
        uObjectClass = offsets.UObjectClass,
        uObjectName = offsets.UObjectName,
        uObjectOuter = offsets.UObjectOuter,
        uFieldNext = offsets.UFieldNext,
        uStructSuperStruct = offsets.UStructSuperStruct,
        uStructChildren = offsets.UStructChildren,
        uStructChildProperties = offsets.UStructChildProperties,
        uStructPropertiesSize = offsets.UStructPropertiesSize,
        uFunctionFunctionFlags = offsets.UFunctionFunctionFlags,
        uFunctionExecFunction = offsets.UFunctionExecFunction,
        fFieldName = offsets.FFieldName,
        fFieldNext = offsets.FFieldNext,
        fFieldClassName = offsets.FFieldClassName,
        fPropertyOffsetInternal = offsets.FPropertyOffsetInternal,
        fPropertyArrayDim = offsets.FPropertyArrayDim,
        fPropertyElementSize = offsets.FPropertyElementSize,
        fPropertyPropertyFlags = offsets.FPropertyPropertyFlags,
        fPropertyBitMaskField = offsets.FPropertyBitMaskField,
        fPropertyObjectClassType = offsets.FPropertyObjectClassType,
        fPropertyEnumField = offsets.FPropertyEnumField,
        uEnumNames = offsets.UEnumNames,
        uEnumNamesLayout = offsets.UEnumNamesLayout.ToString(),
        uWorldPersistentLevel = offsets.UWorldPersistentLevel,
        uLevelActors = offsets.ULevelActors
    };

    private ValueTask<McpToolResult> ListProcessesAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? filter = GetString(arguments, "name");
        int limit = Math.Clamp(GetInt32(arguments, "limit") ?? 500, 1, 10_000);
        IEnumerable<ProcessDescriptor> processes = _session.EnumerateProcesses();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            processes = processes.Where(
                process => process.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return ValueTask.FromResult(JsonResult(processes.Take(limit).Select(ToProcessDto).ToArray()));
    }

    private async ValueTask<McpToolResult> AttachProcessAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        int? processId = GetInt32(arguments, "pid");
        string? name = GetString(arguments, "name");
        ProcessDescriptor? process;
        if (processId.HasValue)
        {
            process = _session.EnumerateProcesses()
                .FirstOrDefault(candidate => candidate.ProcessId == processId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            process = _session.EnumerateProcesses()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        System.IO.Path.GetFileNameWithoutExtension(candidate.Name),
                        name,
                        StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            return ErrorResult("attach_process requires pid or name.");
        }

        if (process is null)
        {
            return ErrorResult("The requested process was not found.");
        }

        bool attached = await _session.AttachAsync(process, cancellationToken).ConfigureAwait(false);
        return attached
            ? JsonResult(new { attached = true, process = ToProcessDto(process) })
            : ErrorResult("DebugActiveProcess failed.");
    }

    private async ValueTask<McpToolResult> LaunchProcessAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        string? commandLine = GetString(arguments, "commandLine");
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return ErrorResult("launch_process requires commandLine.");
        }

        string? workingDirectory = GetString(arguments, "workingDirectory");
        bool launched = await _session.LaunchAsync(
                commandLine,
                workingDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        return launched
            ? JsonResult(new { launched = true, pid = _session.Target.ProcessId })
            : ErrorResult("CreateProcessW failed.");
    }

    private ValueTask<McpToolResult> ListModulesAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? filter = GetString(arguments, "filter");
        IEnumerable<ModuleDescriptor> modules = _session.EnumerateModules();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            modules = modules.Where(module =>
                module.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                module.FilePath.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return ValueTask.FromResult(JsonResult(modules.Select(ToModuleDto).ToArray()));
    }

    private ValueTask<McpToolResult> ListMemoryRegionsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool readableOnly = GetBoolean(arguments, "readableOnly") ?? false;
        bool writableOnly = GetBoolean(arguments, "writableOnly") ?? false;
        int limit = Math.Clamp(GetInt32(arguments, "limit") ?? 10_000, 1, 100_000);
        IEnumerable<MemoryRegionInfo> regions = _session.EnumerateMemoryRegions();
        if (readableOnly)
        {
            regions = regions.Where(static region => region.IsReadable);
        }

        if (writableOnly)
        {
            regions = regions.Where(static region => region.IsWritable);
        }

        return ValueTask.FromResult(JsonResult(regions.Take(limit).Select(ToMemoryRegionDto).ToArray()));
    }

    private ValueTask<McpToolResult> ListThreadsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(JsonResult(
            _session.EnumerateThreads().Select(ToThreadDto).ToArray()));
    }

    private ValueTask<McpToolResult> ListHandlesAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? typeFilter = GetString(arguments, "type");
        int limit = Math.Clamp(GetInt32(arguments, "limit") ?? 10_000, 1, 1_000_000);
        IEnumerable<ProcessHandleEntry> handles = _session.EnumerateHandles();
        if (!string.IsNullOrWhiteSpace(typeFilter))
        {
            handles = handles.Where(handle =>
                handle.TypeName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase));
        }

        return ValueTask.FromResult(JsonResult(handles.Take(limit).Select(ToHandleDto).ToArray()));
    }

    private ValueTask<McpToolResult> GetModuleMetadataAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? moduleName = GetString(arguments, "module");
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return ValueTask.FromResult(ErrorResult("get_module_metadata requires module."));
        }

        ModuleDescriptor? module = _session.EnumerateModules().FirstOrDefault(candidate =>
            string.Equals(candidate.Name, moduleName, StringComparison.OrdinalIgnoreCase));
        if (module is null)
        {
            return ValueTask.FromResult(ErrorResult($"Module '{moduleName}' was not found."));
        }

        try
        {
            PeModuleMetadata metadata = _session.AnalyzeModule(module);
            return ValueTask.FromResult(JsonResult(new
            {
                path = metadata.FilePath,
                machine = $"0x{metadata.Machine:X4}",
                x64 = metadata.Is64Bit,
                imageBase = FormatAddress(metadata.ImageBase),
                entryPointRva = $"0x{metadata.EntryPointRva:X}",
                timestamp = metadata.Timestamp,
                subsystem = metadata.Subsystem,
                sections = metadata.Sections.Select(section => new
                {
                    name = section.Name,
                    virtualAddress = $"0x{section.VirtualAddress:X}",
                    virtualSize = section.VirtualSize,
                    rawOffset = $"0x{section.RawOffset:X}",
                    rawSize = section.RawSize,
                    executable = section.IsExecutable,
                    readable = section.IsReadable,
                    writable = section.IsWritable
                }).ToArray(),
                imports = metadata.Imports.Select(import => new
                {
                    module = import.ModuleName,
                    name = import.FunctionName,
                    ordinal = import.Ordinal,
                    iatRva = $"0x{import.IatRva:X}"
                }).ToArray(),
                exports = metadata.Exports.Select(export => new
                {
                    name = export.Name,
                    ordinal = export.Ordinal,
                    functionRva = $"0x{export.FunctionRva:X}",
                    forwarder = export.ForwarderName
                }).ToArray()
            }));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(ErrorResult(exception.Message));
        }
    }

    private ValueTask<McpToolResult> ReadMemoryAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetAddress(arguments, "address", out ulong address))
        {
            return ValueTask.FromResult(ErrorResult("read_memory requires a valid address."));
        }

        int size = GetInt32(arguments, "size") ?? 64;
        if (size is < 1 or > 16 * 1024 * 1024)
        {
            return ValueTask.FromResult(ErrorResult("size must be between 1 and 16777216."));
        }

        byte[] bytes = _session.ReadBytes(address, size);
        string format = GetString(arguments, "format") ?? "hex";
        return ValueTask.FromResult(JsonResult(new
        {
            address = FormatAddress(address),
            requested = size,
            read = bytes.Length,
            bytes = format.Equals("array", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.SerializeToElement(bytes)
                : JsonSerializer.SerializeToElement(Convert.ToHexString(bytes))
        }));
    }

    private ValueTask<McpToolResult> WriteMemoryAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetAddress(arguments, "address", out ulong address))
        {
            return ValueTask.FromResult(ErrorResult("write_memory requires a valid address."));
        }

        if (!TryGetBytes(arguments, "bytes", out byte[]? bytes) || bytes is null)
        {
            return ValueTask.FromResult(ErrorResult("bytes must be a hex string or an integer array."));
        }

        bool written = _session.WriteBytes(address, bytes);
        return ValueTask.FromResult(JsonResult(new
        {
            address = FormatAddress(address),
            size = bytes.Length,
            written
        }));
    }

    private ValueTask<McpToolResult> DisassembleAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetAddress(arguments, "address", out ulong address))
        {
            return ValueTask.FromResult(ErrorResult("disassemble requires a valid address."));
        }

        int count = Math.Clamp(GetInt32(arguments, "count") ?? 32, 1, 4096);
        IReadOnlyList<DogeDebugger.Core.Disassembly.InstructionSnapshot> instructions =
            _session.Disassemble(address, count);

        return ValueTask.FromResult(JsonResult(new
        {
            address = FormatAddress(address),
            count = instructions.Count,
            lines = instructions.Select(instruction => new
            {
                address = FormatAddress(instruction.Address),
                bytes = Convert.ToHexString(instruction.Bytes),
                text = instruction.Text,
                flow = instruction.FlowControl,
                target = instruction.NearBranchTarget is { } target
                    ? FormatAddress(target)
                    : null
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> SearchAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        string? pattern = GetString(arguments, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return ValueTask.FromResult(ErrorResult("aob_search requires pattern."));
        }

        SearchRequest request = new()
        {
            Pattern = pattern,
            StartAddress = GetAddress(arguments, "startAddress") ?? 0,
            EndAddress = GetAddress(arguments, "endAddress") ?? ulong.MaxValue,
            MaximumResults = Math.Clamp(GetInt32(arguments, "maxResults") ?? 10_000, 1, 1_000_000),
            SearchPrivateMemory = GetBoolean(arguments, "private") ?? true,
            SearchImageMemory = GetBoolean(arguments, "image") ?? true,
            SearchMappedMemory = GetBoolean(arguments, "mapped") ?? false
        };

        SearchResult result = _session.Search(request, cancellationToken);
        return ValueTask.FromResult(JsonResult(new
        {
            truncated = result.Truncated,
            scannedBytes = result.ScannedBytes,
            matches = result.Matches.Select(match => new
            {
                address = FormatAddress(match.Address),
                bytes = Convert.ToHexString(match.Bytes)
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> ValueScanAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        MemoryValueScanOptions options = ReadValueScanOptions(arguments);
        try
        {
            MemoryValueScanResult result = _session.ScanValues(options, cancellationToken);
            string scanId = Guid.NewGuid().ToString("N");
            _valueScans[scanId] = result.Matches.ToList();
            return ValueTask.FromResult(JsonResult(new
            {
                scanId,
                truncated = result.Truncated,
                scannedBytes = result.ScannedBytes,
                count = result.Matches.Count,
                matches = result.Matches.Select(ToValueMatchDto).ToArray()
            }));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(ErrorResult(exception.Message));
        }
    }

    private ValueTask<McpToolResult> ValueScanNextAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        string? scanId = GetString(arguments, "scanId");
        if (string.IsNullOrWhiteSpace(scanId) ||
            !_valueScans.TryGetValue(scanId, out List<MemoryValueMatch>? previous))
        {
            return ValueTask.FromResult(ErrorResult("Unknown or missing scanId."));
        }

        MemoryValueScanOptions options = ReadValueScanOptions(arguments);
        try
        {
            MemoryValueScanResult result = _session.RefineValues(
                previous,
                options,
                cancellationToken);
            _valueScans[scanId] = result.Matches.ToList();
            return ValueTask.FromResult(JsonResult(new
            {
                scanId,
                truncated = result.Truncated,
                scannedBytes = result.ScannedBytes,
                count = result.Matches.Count,
                matches = result.Matches.Select(ToValueMatchDto).ToArray()
            }));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(ErrorResult(exception.Message));
        }
    }

    private ValueTask<McpToolResult> PointerScanAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryGetAddress(arguments, "address", out ulong address))
        {
            return ValueTask.FromResult(ErrorResult("pointer_scan requires a valid address."));
        }

        PointerScanOptions options = new()
        {
            TargetAddress = address,
            MaximumOffset = Math.Clamp(GetInt32(arguments, "maxOffset") ?? 0x1000, 0, 0x100000),
            MaximumDepth = Math.Clamp(GetInt32(arguments, "maxDepth") ?? 4, 1, 12),
            Alignment = Math.Clamp(GetInt32(arguments, "alignment") ?? (address > uint.MaxValue ? 8 : 4), 1, 16),
            MaximumResults = Math.Clamp(GetInt32(arguments, "maxResults") ?? 5000, 1, 1_000_000),
            WritableOnly = GetBoolean(arguments, "writableOnly") ?? true
        };

        PointerScanResult result = _session.ScanPointers(options, cancellationToken);
        return ValueTask.FromResult(JsonResult(new
        {
            truncated = result.Truncated,
            scannedBytes = result.ScannedBytes,
            count = result.Chains.Count,
            chains = result.Chains.Select(chain => new
            {
                baseAddress = FormatAddress(chain.BaseAddress),
                module = chain.ModuleName,
                moduleOffset = $"0x{chain.ModuleOffset:X}",
                offsets = chain.Offsets.Select(static offset => $"0x{offset & 0xFFFFFFFF:X}").ToArray(),
                expression = $"\"{chain.ModuleName}\"+0x{chain.ModuleOffset:X}"
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> ScanStringsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        StringScanOptions options = new()
        {
            MinimumLength = Math.Clamp(GetInt32(arguments, "minimumLength") ?? 4, 1, 4096),
            MaximumLength = Math.Clamp(GetInt32(arguments, "maximumLength") ?? 4096, 1, 65_536),
            MaximumResults = Math.Clamp(GetInt32(arguments, "maxResults") ?? 100_000, 1, 1_000_000),
            ScanAsciiUtf8 = GetBoolean(arguments, "ascii") ?? true,
            ScanUtf16Le = GetBoolean(arguments, "utf16") ?? true,
            IncludePrivateMemory = GetBoolean(arguments, "private") ?? true,
            IncludeImageMemory = GetBoolean(arguments, "image") ?? true,
            IncludeMappedMemory = GetBoolean(arguments, "mapped") ?? false
        };

        IReadOnlyList<StringEntry> strings = _session.ScanStrings(options, cancellationToken);
        return ValueTask.FromResult(JsonResult(new
        {
            count = strings.Count,
            strings = strings.Select(entry => new
            {
                address = FormatAddress(entry.Address),
                length = entry.ByteLength,
                encoding = entry.EncodingName,
                text = entry.Text
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> BuildXrefsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        DogeDebugger.Core.CrossReference.XrefDatabase database =
            _session.BuildCrossReferences(cancellationToken);
        string databaseId = Guid.NewGuid().ToString("N");
        _xrefDatabases[databaseId] = database;
        return ValueTask.FromResult(JsonResult(new
        {
            databaseId,
            count = database.Entries.Count
        }));
    }

    private ValueTask<McpToolResult> GetXrefsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? databaseId = GetString(arguments, "databaseId");
        if (string.IsNullOrWhiteSpace(databaseId) ||
            !_xrefDatabases.TryGetValue(
                databaseId,
                out DogeDebugger.Core.CrossReference.XrefDatabase? database))
        {
            return ValueTask.FromResult(ErrorResult("Unknown or missing databaseId."));
        }

        if (!TryGetAddress(arguments, "address", out ulong address))
        {
            return ValueTask.FromResult(ErrorResult("get_xrefs requires a valid address."));
        }

        return ValueTask.FromResult(JsonResult(new
        {
            address = FormatAddress(address),
            count = database.Find(address).Count,
            references = database.Find(address).Select(reference => new
            {
                from = FormatAddress(reference.FromAddress),
                kind = reference.Kind.ToString(),
                module = reference.ModuleName,
                instruction = reference.InstructionText
            }).ToArray()
        }));
    }

    private ValueTask<McpToolResult> GenerateSignatureAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryGetAddress(arguments, "address", out ulong address))
        {
            return ValueTask.FromResult(ErrorResult("generate_signature requires a valid address."));
        }

        string? moduleName = GetString(arguments, "module");
        ModuleDescriptor? module = string.IsNullOrWhiteSpace(moduleName)
            ? ModuleCatalog.FindByAddress(_session.EnumerateModules(), address)
            : _session.EnumerateModules().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, moduleName, StringComparison.OrdinalIgnoreCase));
        AobSignature? signature = _session.GenerateSignature(
            address,
            module,
            new AobSignatureOptions
            {
                MaximumLength = Math.Clamp(GetInt32(arguments, "maximumLength") ?? 128, 1, 4096),
                InstructionCount = Math.Clamp(GetInt32(arguments, "instructionCount") ?? 6, 1, 128)
            },
            cancellationToken);
        return ValueTask.FromResult(signature is null
            ? ErrorResult("A unique signature could not be generated.")
            : JsonResult(new
            {
                address = FormatAddress(address),
                module = module?.Name,
                pattern = signature.Pattern,
                length = signature.Length,
                matches = signature.MatchCount
            }));
    }

    private ValueTask<McpToolResult> SetBreakpointAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetAddress(arguments, "address", out ulong address))
        {
            return ValueTask.FromResult(ErrorResult("set_breakpoint requires a valid address."));
        }

        bool added = _session.AddBreakpoint(address);
        return ValueTask.FromResult(JsonResult(new
        {
            address = FormatAddress(address),
            added,
            alreadyPresent = !added && _session.Breakpoints.Contains(address)
        }));
    }

    private ValueTask<McpToolResult> RemoveBreakpointAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetAddress(arguments, "address", out ulong address))
        {
            return ValueTask.FromResult(ErrorResult("remove_breakpoint requires a valid address."));
        }

        bool removed = _session.RemoveBreakpoint(address);
        return ValueTask.FromResult(JsonResult(new
        {
            address = FormatAddress(address),
            removed
        }));
    }

    private static object ToProcessDto(ProcessDescriptor process) => new
    {
        pid = process.ProcessId,
        name = process.Name,
        path = process.FilePath,
        threads = process.ThreadCount,
        started = process.StartTime
    };

    private static object ToModuleDto(ModuleDescriptor module) => new
    {
        name = module.Name,
        path = module.FilePath,
        baseAddress = FormatAddress(module.BaseAddress),
        size = module.Size,
        entryPoint = FormatAddress(module.EntryPoint),
        main = module.IsMainModule
    };

    private static object ToMemoryRegionDto(MemoryRegionInfo region) => new
    {
        baseAddress = FormatAddress(region.BaseAddress),
        allocationBase = FormatAddress(region.AllocationBase),
        size = region.Size,
        state = region.State,
        type = region.Type,
        protect = region.ProtectText,
        module = string.IsNullOrWhiteSpace(region.ModuleName) ? null : region.ModuleName,
        readable = region.IsReadable,
        writable = region.IsWritable,
        executable = region.IsExecutable
    };

    private static object ToThreadDto(ThreadDescriptor thread) => new
    {
        tid = thread.ThreadId,
        pid = thread.ProcessId,
        name = thread.Name,
        basePriority = thread.BasePriority,
        startAddress = FormatAddress(thread.StartAddress),
        instructionPointer = FormatAddress(thread.InstructionPointer)
    };

    private static object ToHandleDto(ProcessHandleEntry handle) => new
    {
        processId = handle.ProcessId,
        handle = $"0x{handle.HandleValue:X}",
        type = handle.TypeName,
        access = $"0x{handle.GrantedAccess:X8}",
        @object = $"0x{handle.ObjectAddress:X}"
    };

    private static object ToValueMatchDto(MemoryValueMatch match) => new
    {
        address = FormatAddress(match.Address),
        value = match.DisplayValue,
        previous = Convert.ToHexString(match.PreviousBytes),
        current = Convert.ToHexString(match.CurrentBytes)
    };

    private static MemoryValueScanOptions ReadValueScanOptions(JsonElement arguments)
    {
        MemoryValueKind kind = Enum.TryParse(
            GetString(arguments, "kind") ?? "Int32",
            ignoreCase: true,
            out MemoryValueKind parsedKind)
                ? parsedKind
                : MemoryValueKind.Int32;
        MemoryValueComparison comparison = Enum.TryParse(
            GetString(arguments, "comparison") ?? "Exact",
            ignoreCase: true,
            out MemoryValueComparison parsedComparison)
                ? parsedComparison
                : MemoryValueComparison.Exact;
        return new MemoryValueScanOptions
        {
            Kind = kind,
            Comparison = comparison,
            Value = GetString(arguments, "value") ?? "0",
            SecondValue = GetString(arguments, "secondValue"),
            StartAddress = GetAddress(arguments, "startAddress") ?? 0,
            EndAddress = GetAddress(arguments, "endAddress") ?? ulong.MaxValue,
            Alignment = Math.Clamp(GetInt32(arguments, "alignment") ?? 1, 1, 16),
            MaximumResults = Math.Clamp(GetInt32(arguments, "maxResults") ?? 100_000, 1, 10_000_000),
            WritableOnly = GetBoolean(arguments, "writableOnly") ?? false,
            ExecutableOnly = GetBoolean(arguments, "executableOnly") ?? false,
            IgnoreCase = GetBoolean(arguments, "ignoreCase") ?? false
        };
    }

    private bool TryResolveAddressExpression(
        string expression,
        out ulong address,
        out string? error)
    {
        address = 0;
        error = null;
        expression = expression.Trim();
        if (expression.Length == 0)
        {
            error = "The address expression is empty.";
            return false;
        }

        if (TryGetAddress(expression, out address))
        {
            return true;
        }

        IReadOnlyList<ModuleDescriptor> modules = _session.EnumerateModules();
        int separator = expression.IndexOf('!');
        if (separator > 0)
        {
            string moduleName = expression[..separator].Trim();
            string exportName = expression[(separator + 1)..].Trim();
            ModuleDescriptor? module = modules.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Name,
                    moduleName,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    Path.GetFileName(candidate.FilePath),
                    moduleName,
                    StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                error = $"Module '{moduleName}' was not found.";
                return false;
            }

            PeExport? export = _session
                .AnalyzeModule(module)
                .Exports
                .FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.Name,
                        exportName,
                        StringComparison.OrdinalIgnoreCase));
            if (export is null)
            {
                error = $"Export '{exportName}' was not found in '{module.Name}'.";
                return false;
            }

            address = module.BaseAddress + export.FunctionRva;
            return true;
        }

        int plus = expression.LastIndexOf('+');
        if (plus > 0)
        {
            string moduleName = expression[..plus].Trim();
            string offsetText = expression[(plus + 1)..].Trim();
            ModuleDescriptor? module = modules.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Name,
                    moduleName,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    Path.GetFileName(candidate.FilePath),
                    moduleName,
                    StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                error = $"Module '{moduleName}' was not found.";
                return false;
            }

            if (!TryGetAddress(offsetText, out ulong offset))
            {
                error = $"Invalid module offset '{offsetText}'.";
                return false;
            }

            address = module.BaseAddress + offset;
            return true;
        }

        error = $"Invalid address expression '{expression}'.";
        return false;
    }

    private static InstructionMemoryOperand? SelectInstructionMemoryOperand(
        JsonElement arguments,
        IReadOnlyList<InstructionMemoryOperand> operands,
        out string? error)
    {
        error = null;
        string? operandText = GetString(arguments, "memory_operand_text");
        if (!string.IsNullOrWhiteSpace(operandText))
        {
            InstructionMemoryOperand? match = operands.FirstOrDefault(operand =>
                operand.DisplayText.Contains(
                    operandText,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    operand.DisplayText,
                    operandText,
                    StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            error = $"The instruction does not contain memory operand '{operandText}'.";
            return null;
        }

        int? operandIndex = GetInt32(arguments, "memory_operand_index");
        if (operandIndex.HasValue)
        {
            InstructionMemoryOperand? match = operands.FirstOrDefault(
                operand => operand.OperandIndex == operandIndex.Value);
            if (match is null &&
                operandIndex.Value >= 0 &&
                operandIndex.Value < operands.Count)
            {
                match = operands[operandIndex.Value];
            }

            if (match is not null)
            {
                return match;
            }

            error = $"Memory operand index {operandIndex.Value} was not found.";
            return null;
        }

        if (operands.Count == 1)
        {
            return operands[0];
        }

        error =
            "The instruction has multiple memory operands. " +
            "Pass memory_operand_index or memory_operand_text. Candidates: " +
            string.Join(
                ", ",
                operands.Select(operand =>
                    $"{operand.OperandIndex}:{operand.DisplayText}"));
        return null;
    }

    private static bool TryParseInstructionAccessValueSize(
        string? text,
        int defaultValueSize,
        out int valueSize,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text) ||
            string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase))
        {
            valueSize = defaultValueSize is 1 or 2 or 4 or 8
                ? defaultValueSize
                : 8;
            return true;
        }

        valueSize = text.Trim().ToLowerInvariant() switch
        {
            "byte" or "1" => 1,
            "word" or "2" => 2,
            "dword" or "4" => 4,
            "qword" or "8" => 8,
            _ => 0
        };
        if (valueSize == 0)
        {
            error = "value_size must be auto, byte, word, dword, qword, 1, 2, 4, or 8.";
            return false;
        }

        return true;
    }

    private static InstructionAccessResumeMode ParseInstructionAccessResumeMode(
        string? text)
    {
        return text?.Trim().ToLowerInvariant() switch
        {
            "never" => InstructionAccessResumeMode.Never,
            "always" => InstructionAccessResumeMode.Always,
            _ => InstructionAccessResumeMode.WatchOnly
        };
    }

    private static object ToInstructionAccessWatchDto(
        InstructionAccessWatchSnapshot snapshot) => new
    {
        watchId = snapshot.WatchId,
        processId = snapshot.ProcessId,
        processName = snapshot.ProcessName,
        is64Bit = snapshot.Is64Bit,
        instructionAddress = FormatAddress(snapshot.CodeAddress),
        operandIndex = snapshot.OperandIndex,
        operand = snapshot.OperandText,
        defaultValueSize = snapshot.DefaultValueSize,
        canParseMonoInstance = snapshot.CanParseMonoInstance,
        monoBaseRegister = snapshot.MonoBaseRegisterName,
        valueSize = snapshot.ValueSize,
        state = snapshot.State == InstructionAccessWatchState.Running
            ? "running"
            : "stopped",
        createdAt = snapshot.CreatedAt,
        stoppedAt = snapshot.StoppedAt,
        totalRecordCount = snapshot.TotalRecordCount,
        addressCount = snapshot.AddressCount,
        offset = snapshot.Offset,
        limit = snapshot.Limit,
        records = snapshot.Records.Select(record => new
        {
            address = FormatAddress(record.AccessAddress),
            value = $"0x{record.Value:X}",
            valueReadSucceeded = record.ValueReadSucceeded,
            valueSize = record.ValueSize,
            threadId = record.LastThreadId,
            count = record.Count,
            firstSeen = record.FirstSeen,
            lastSeen = record.LastSeen,
            monoBaseRegister = record.MonoBaseRegisterName,
            monoBaseAddress = FormatAddress(record.MonoBaseAddress)
        }).ToArray()
    };

    internal static McpToolResult JsonResult(object value)
    {
        JsonElement json = JsonSerializer.SerializeToElement(value, ToolJsonOptions);
        return new McpToolResult
        {
            Text = JsonSerializer.Serialize(json, CompactJsonOptions),
            StructuredContent = json
        };
    }

    internal static McpToolResult BoolResult(bool value) => JsonResult(new { success = value });

    internal static McpToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Text = message
    };

    internal static string FormatAddress(ulong address) => $"0x{address:X}";

    internal static bool TryGetAddress(JsonElement arguments, string property, out ulong address)
    {
        address = 0;
        if (!arguments.TryGetProperty(property, out JsonElement value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out address))
        {
            return true;
        }

        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return TryGetAddress(text, out address);
    }

    private static bool TryGetAddress(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(
                text[2..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out address)
            : ulong.TryParse(
                  text,
                  NumberStyles.Integer,
                  CultureInfo.InvariantCulture,
                  out address) ||
              ulong.TryParse(
                  text,
                  NumberStyles.AllowHexSpecifier,
                  CultureInfo.InvariantCulture,
                  out address);
    }

    internal static ulong? GetAddress(JsonElement arguments, string property) =>
        TryGetAddress(arguments, property, out ulong address) ? address : null;

    internal static string? GetString(JsonElement arguments, string property) =>
        arguments.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static int? GetInt32(JsonElement arguments, string property) =>
        arguments.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int result)
            ? result
            : null;

    internal static bool? GetBoolean(JsonElement arguments, string property) =>
        arguments.TryGetProperty(property, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    internal static bool TryGetBytes(
        JsonElement arguments,
        string property,
        out byte[]? bytes)
    {
        bytes = null;
        if (!arguments.TryGetProperty(property, out JsonElement value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            List<byte> parsed = [];
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number ||
                    !item.TryGetByte(out byte current))
                {
                    return false;
                }

                parsed.Add(current);
            }

            bytes = parsed.ToArray();
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string text = value.GetString() ?? string.Empty;
        string normalized = new(
            text.Where(static character => Uri.IsHexDigit(character))
                .ToArray());
        if (normalized.Length == 0 || normalized.Length % 2 != 0)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(normalized);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static JsonElement EmptyObjectSchema() =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { },
            additionalProperties = false
        });

    internal static JsonElement ObjectSchema(params (string Name, string Type)[] properties)
    {
        Dictionary<string, object> schemaProperties = new(StringComparer.Ordinal);
        foreach ((string name, string type) in properties)
        {
            schemaProperties[name] = new { type };
        }

        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = schemaProperties,
            additionalProperties = false
        });
    }

    private static readonly JsonSerializerOptions ToolJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };
}
