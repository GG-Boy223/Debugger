using System.Diagnostics;
using System.IO;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Native;

namespace DogeDebugger.Core.Process;

internal enum DllInjectionMode
{
    LoadLibrary,
    DogeHyperSecurity
}

internal sealed record DllInjectionTarget(
    int ProcessId,
    string ProcessName,
    string FilePath,
    bool Is64Bit);

internal sealed class DllInjectionService
{
    public bool TryCaptureTarget(
        ProcessDescriptor process,
        out DllInjectionTarget target,
        out string error)
    {
        return TryCaptureTarget(
            process.ProcessId,
            process.Name,
            process.FilePath ?? string.Empty,
            out target,
            out error);
    }

    public bool TryCaptureTarget(
        int processId,
        out DllInjectionTarget target,
        out string error)
    {
        return TryCaptureTarget(
            processId,
            string.Empty,
            string.Empty,
            out target,
            out error);
    }

    public RemoteOperationResult Inject(
        DllInjectionTarget target,
        string dllPath,
        PeExport? selectedExport,
        DllInjectionMode mode,
        int timeoutMilliseconds = 5000)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!File.Exists(dllPath))
        {
            return RemoteOperationResult.Failure(
                "DLL 文件不存在。",
                TimeSpan.Zero);
        }

        if (mode == DllInjectionMode.DogeHyperSecurity)
        {
            return InjectThroughDogeHyperSecurity(
                target,
                dllPath,
                selectedExport);
        }

        return ExecuteTargetOperation(
            target,
            targetProcess => targetProcess.InjectDll(
                dllPath,
                selectedExport?.Name,
                timeoutMilliseconds));
    }

    public RemoteOperationResult Unload(
        DllInjectionTarget target,
        string dllPath,
        int timeoutMilliseconds = 5000)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!File.Exists(dllPath))
        {
            return RemoteOperationResult.Failure(
                "DLL 文件不存在。",
                TimeSpan.Zero);
        }

        return ExecuteTargetOperation(
            target,
            targetProcess => targetProcess.UnloadDll(
                dllPath,
                timeoutMilliseconds));
    }

    private bool TryCaptureTarget(
        int processId,
        string processName,
        string filePath,
        out DllInjectionTarget target,
        out string error)
    {
        target = default!;
        error = string.Empty;
        if (processId <= 0)
        {
            error = "请选择有效进程。";
            return false;
        }

        try
        {
            using TargetProcess process = new();
            if (!process.Open(processId, processName, filePath))
            {
                error = "无法打开目标进程。";
                return false;
            }

            target = new DllInjectionTarget(
                process.ProcessId,
                process.ProcessName,
                process.FilePath,
                process.Is64Bit);
            return true;
        }
        catch (Exception exception)
        {
            error = $"无法读取进程信息: {exception.Message}";
            return false;
        }
    }

    private static RemoteOperationResult InjectThroughDogeHyperSecurity(
        DllInjectionTarget target,
        string dllPath,
        PeExport? selectedExport)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!target.Is64Bit)
        {
            return RemoteOperationResult.Failure(
                "DHS 字节注入仅支持 x64 目标进程。",
                stopwatch.Elapsed);
        }

        if (!DogeHyperSecurityBridge.IsLoaded())
        {
            return RemoteOperationResult.Failure(
                "DHS 未加载，无法使用模式二。",
                stopwatch.Elapsed);
        }

        try
        {
            byte[] payload = File.ReadAllBytes(dllPath);
            DogeHyperSecurityResult result = DogeHyperSecurityBridge.InjectBytes(
                target.ProcessId,
                payload,
                selectedExport?.FunctionRva ?? 0);
            stopwatch.Stop();
            if (result.Status < 0)
            {
                return RemoteOperationResult.Failure(
                    $"DHS 注入失败，驱动状态: 0x{result.Status:X8}。",
                    stopwatch.Elapsed);
            }

            return RemoteOperationResult.Success(
                result.ReturnValue,
                stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            return RemoteOperationResult.Failure(
                $"DHS 注入失败: {exception.Message}",
                stopwatch.Elapsed);
        }
    }

    private static RemoteOperationResult ExecuteTargetOperation(
        DllInjectionTarget target,
        Func<TargetProcess, RemoteOperationResult> operation)
    {
        try
        {
            using TargetProcess process = new();
            if (!process.Open(
                    target.ProcessId,
                    target.ProcessName,
                    target.FilePath))
            {
                return RemoteOperationResult.Failure(
                    "无法打开目标进程。",
                    TimeSpan.Zero);
            }

            if (process.Is64Bit != target.Is64Bit)
            {
                return RemoteOperationResult.Failure(
                    "目标进程架构已变化，请重新选择目标进程。",
                    TimeSpan.Zero);
            }

            return operation(process);
        }
        catch (Exception exception)
        {
            return RemoteOperationResult.Failure(
                $"远程操作失败: {exception.Message}",
                TimeSpan.Zero);
        }
    }
}
