namespace DogeDebugger.UI.Views.Panels;

public sealed record LuaApiEntry(
    string Name,
    string Category,
    string Description,
    string InsertText);

/// <summary>
/// Lua API help entries recovered from the original Lua script panel.
/// </summary>
public static class LuaApiCatalog
{
    public static IReadOnlyList<LuaApiEntry> Entries { get; } =
    [
        new(
            "OnStart",
            "Lifecycle",
            "function OnStart() - Required lifecycle function called after the script is loaded and run.",
            "function OnStart()\n    \nend"),
        new(
            "OnEnd",
            "Lifecycle",
            "function OnEnd() - Required lifecycle function called when the active script is stopped.",
            "function OnEnd()\n    \nend"),
        new(
            "onStart",
            "Lifecycle",
            "function onStart() - Legacy alias for OnStart. AI generated scripts should use OnStart.",
            "function onStart()\n    \nend"),
        new(
            "onEnd",
            "Lifecycle",
            "function onEnd() - Legacy alias for OnEnd. AI generated scripts should use OnEnd.",
            "function onEnd()\n    \nend"),
        new(
            "getProcessId",
            "Process",
            "getProcessId() -> number - Returns the current target process id, or 0 when no process is open.",
            "getProcessId()"),
        new(
            "getProcessName",
            "Process",
            "getProcessName() -> string - Returns the current target process name, or an empty string.",
            "getProcessName()"),
        new(
            "is64Bit",
            "Process",
            "is64Bit() -> bool - Returns true when the target process is 64-bit.",
            "is64Bit()"),
        new(
            "isProcessOpen",
            "Process",
            "isProcessOpen() -> bool - Returns true when a target process is open.",
            "isProcessOpen()"),
        new(
            "file_append",
            "File",
            "file_append(fileName, content) -> bool - Appends content to a UTF-8 file, creating the directory when needed.",
            "file_append(fileName, content)"),
        new(
            "file_appendLine",
            "File",
            "file_appendLine(fileName, content) -> bool - Appends content plus a newline to a UTF-8 file.",
            "file_appendLine(fileName, content)"),
        new(
            "readBytes",
            "Memory",
            "readBytes(address, size) -> table - Reads bytes from target memory and returns a 1-based Lua byte table.",
            "readBytes(address, size)"),
        new(
            "writeBytes",
            "Memory",
            "writeBytes(address, bytes) -> bool - Writes a 1-based Lua byte table to target memory.",
            "writeBytes(address, bytes)"),
        new(
            "readInteger",
            "Memory",
            "readInteger(address) -> number|nil - Reads a signed 32-bit integer. Returns nil on failure.",
            "readInteger(address)"),
        new(
            "readQword",
            "Memory",
            "readQword(address) -> number|nil - Reads an unsigned 64-bit integer. Returns nil on failure.",
            "readQword(address)"),
        new(
            "readFloat",
            "Memory",
            "readFloat(address) -> number|nil - Reads a 32-bit float. Returns nil on failure.",
            "readFloat(address)"),
        new(
            "readDouble",
            "Memory",
            "readDouble(address) -> number|nil - Reads a 64-bit double. Returns nil on failure.",
            "readDouble(address)"),
        new(
            "readPointer",
            "Memory",
            "readPointer(address) -> number|nil - Reads a pointer-sized integer based on target bitness. Returns nil on failure.",
            "readPointer(address)"),
        new(
            "readString",
            "Memory",
            "readString(address, maxLength, wide) -> string|nil - Reads a null-terminated ASCII or UTF-16 string. Set wide=true for UTF-16.",
            "readString(address, maxLength, wide)"),
        new(
            "writeInteger",
            "Memory",
            "writeInteger(address, value) -> bool - Writes a signed 32-bit integer.",
            "writeInteger(address, value)"),
        new(
            "writeQword",
            "Memory",
            "writeQword(address, value) -> bool - Writes an unsigned 64-bit integer.",
            "writeQword(address, value)"),
        new(
            "writeFloat",
            "Memory",
            "writeFloat(address, value) -> bool - Writes a 32-bit float.",
            "writeFloat(address, value)"),
        new(
            "writeDouble",
            "Memory",
            "writeDouble(address, value) -> bool - Writes a 64-bit double.",
            "writeDouble(address, value)"),
        new(
            "writeString",
            "Memory",
            "writeString(address, text, wide) -> bool - Writes a null-terminated ASCII or UTF-16 string.",
            "writeString(address, text, wide)"),
        new(
            "getRegister",
            "Registers",
            "getRegister(name) -> number|nil - Reads a register while the debugger is paused or inside a breakpoint callback.",
            "getRegister(name)"),
        new(
            "setRegister",
            "Registers",
            "setRegister(name, value) - Writes a register while the debugger is paused or inside a breakpoint callback.",
            "setRegister(name, value)"),
        new(
            "getRegisters",
            "Registers",
            "getRegisters() -> table|nil - Returns a table of current registers while the debugger is paused.",
            "getRegisters()"),
        new(
            "getAddress",
            "Symbols",
            "getAddress(expression) -> number|nil - Resolves an address expression such as module offsets, registered symbols, or exports like ntdll.dll.NtClose.",
            "getAddress(expression)"),
        new(
            "getModuleBase",
            "Symbols",
            "getModuleBase(name) -> number|nil - Returns a module base address by module name.",
            "getModuleBase(name)"),
        new(
            "registerSymbol",
            "Symbols",
            "registerSymbol(name, address) -> boolean - Registers a custom symbol shown in the disassembly and symbol panel and persisted with the process. address may be a number or an expression string like \"game.exe+0x1000\". Returns true on success.",
            "registerSymbol(name, address)"),
        new(
            "unregisterSymbol",
            "Symbols",
            "unregisterSymbol(name) -> boolean - Removes a custom symbol by name. Returns true if one was removed.",
            "unregisterSymbol(name)"),
        new(
            "clearSymbols",
            "Symbols",
            "clearSymbols() -> number - Removes all custom symbols and returns how many were removed.",
            "clearSymbols()"),
        new(
            "saveSymbols",
            "Symbols",
            "saveSymbols() - Persists current custom symbols to the process .saved file immediately.",
            "saveSymbols()"),
        new(
            "getModuleList",
            "Modules",
            "getModuleList() -> table - Returns modules as entries with name, base, size, and path fields.",
            "getModuleList()"),
        new(
            "getThreadList",
            "Threads",
            "getThreadList() -> table - Returns threads as entries with id and entryPoint fields.",
            "getThreadList()"),
        new(
            "getCurrentThreadId",
            "Threads",
            "getCurrentThreadId() -> number - Returns the active callback thread id or current paused debugger thread id.",
            "getCurrentThreadId()"),
        new(
            "autoAssemble",
            "MB2E1BB1",
            "autoAssemble(script, enable) -> table - Runs an Auto Assembler script and returns { success = bool, error = string }.",
            "autoAssemble(script, enable)"),
        new(
            "aobScan",
            "MB2E1BB1",
            "aobScan(pattern) -> number|nil - Scans all target memory for an AOB pattern and returns the first address.",
            "aobScan(pattern)"),
        new(
            "aobScanModule",
            "MB2E1BB1",
            "aobScanModule(module, pattern) -> number|nil - Scans one module for an AOB pattern and returns the first address.",
            "aobScanModule(module, pattern)"),
        new(
            "navigate",
            "UI",
            "navigate(address) - Navigates the disassembly view to an address.",
            "navigate(address)"),
        new(
            "setComment",
            "UI",
            "setComment(address, text) - Sets a disassembly comment at an address.",
            "setComment(address, text)"),
        new(
            "showMessage",
            "UI",
            "showMessage(text) - Shows a DogeDebugger message box.",
            "showMessage(text)"),
        new(
            "debug_isDebugging",
            "Debugger",
            "debug_isDebugging() -> bool - Returns true when the target is under debugger control.",
            "debug_isDebugging()"),
        new(
            "debug_isAttached",
            "Debugger",
            "debug_isAttached() -> bool - Returns true when the user-mode debugger is attached.",
            "debug_isAttached()"),
        new(
            "debug_isPaused",
            "Debugger",
            "debug_isPaused() -> bool - Returns true when the debugger is paused.",
            "debug_isPaused()"),
        new(
            "debug_getCallStack",
            "Debugger",
            "debug_getCallStack(maxLevel) -> table - Returns call stack frames with id, returnAddress, and moduleOffset fields.",
            "debug_getCallStack(maxLevel)"),
        new(
            "debug_continue",
            "Debugger",
            "debug_continue() - Continues the debuggee. Use only when explicitly requested.",
            "debug_continue()"),
        new(
            "debug_stepInto",
            "Debugger",
            "debug_stepInto() - Steps into the next instruction. Use only when explicitly requested.",
            "debug_stepInto()"),
        new(
            "debug_stepOver",
            "Debugger",
            "debug_stepOver() - Steps over the next instruction. Use only when explicitly requested.",
            "debug_stepOver()"),
        new(
            "debug_stepOut",
            "Debugger",
            "debug_stepOut() - Steps out of the current function. Use only when explicitly requested.",
            "debug_stepOut()"),
        new(
            "debug_break",
            "Debugger",
            "debug_break() - Breaks into the debuggee. Use only when explicitly requested.",
            "debug_break()"),
        new(
            "debug_detach",
            "Debugger",
            "debug_detach() - Detaches the debugger. Use only when explicitly requested.",
            "debug_detach()"),
        new(
            "debug_setBreakpoint",
            "Breakpoints",
            "debug_setBreakpoint(address, callback) -> bool - Sets a software breakpoint. In callbacks, return false by default to auto-continue; return true only when the user explicitly wants to break.",
            "debug_setBreakpoint(address, callback)"),
        new(
            "debug_setBreakpointWithCondition",
            "Breakpoints",
            "debug_setBreakpointWithCondition(address, condition, callback) -> bool - Sets a software breakpoint with a Lua condition expression string.",
            "debug_setBreakpointWithCondition(address, condition, callback)"),
        new(
            "debug_updateBreakpointUpdateCondition",
            "Breakpoints",
            "debug_updateBreakpointUpdateCondition(address, condition) -> bool - Updates a breakpoint condition. Pass nil or empty string to clear it.",
            "debug_updateBreakpointUpdateCondition(address, condition)"),
        new(
            "debug_removeBreakpoint",
            "Breakpoints",
            "debug_removeBreakpoint(address) -> bool - Removes a software breakpoint.",
            "debug_removeBreakpoint(address)"),
        new(
            "debug_setHardwareBreakpoint",
            "Breakpoints",
            "debug_setHardwareBreakpoint(address, condition, size, mode, callback) -> bool - Sets a hardware breakpoint. In callbacks, return false by default to auto-continue. condition: 0=Execute, 1=Write, 3=ReadWrite. size: 0=Byte, 1=Word, 3=Dword, 2=Qword. mode: 'break' or 'log'.",
            "debug_setHardwareBreakpoint(address, condition, size, mode, callback)"),
        new(
            "debug_removeHardwareBreakpoint",
            "Breakpoints",
            "debug_removeHardwareBreakpoint(address) -> bool - Removes a hardware breakpoint.",
            "debug_removeHardwareBreakpoint(address)"),
    ];
}
