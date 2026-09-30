using System.Globalization;
using System.IO;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Scripting.AutoAssembler;

/// <summary>
/// Resolves Auto Assembler symbols the same way the original does:
/// registered names first, then `"module"+0xRVA`, `module.export`, bare
/// export names, and finally plain hexadecimal literals (`0x`, `$` or bare).
/// </summary>
public sealed class AutoAssemblerSymbolResolver
{
    private readonly ITargetProcess _target;
    private readonly Func<IReadOnlyList<ModuleDescriptor>> _moduleProvider;
    private readonly Dictionary<string, IReadOnlyList<PeExport>> _exportCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> _symbols =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ModuleDescriptor>? _modules;

    public AutoAssemblerSymbolResolver(
        ITargetProcess target,
        Func<IReadOnlyList<ModuleDescriptor>> moduleProvider)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _moduleProvider = moduleProvider ?? throw new ArgumentNullException(nameof(moduleProvider));
    }

    public IReadOnlyDictionary<string, ulong> Symbols => _symbols;

    public void RegisterSymbol(string name, ulong address)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _symbols[name.Trim()] = address;
        }
    }

    public void UnregisterSymbol(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _symbols.Remove(name.Trim());
        }
    }

    public void InvalidateModules() => _modules = null;

    public bool TryResolve(string symbol, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        string text = symbol.Trim();
        if (_symbols.TryGetValue(text, out address))
        {
            return true;
        }

        return TryResolveModuleOffset(text, out address) ||
               TryResolveModuleExport(text, out address) ||
               TryParseHexLiteral(text, out address);
    }

    public ulong Resolve(string symbol)
    {
        if (!TryResolve(symbol, out ulong address))
        {
            throw new AutoAssemblerException($"无法解析符号 '{symbol}'", -1);
        }

        return address;
    }

    public IReadOnlyList<ModuleDescriptor> GetModules()
    {
        if (_modules is not null)
        {
            return _modules;
        }

        if (!_target.IsOpen)
        {
            return [];
        }

        try
        {
            _modules = _moduleProvider();
        }
        catch
        {
            _modules = [];
        }

        return _modules;
    }

    private bool TryResolveModuleOffset(string text, out ulong address)
    {
        address = 0;
        int plus = text.LastIndexOf('+');
        if (plus <= 0 || plus >= text.Length - 1)
        {
            return false;
        }

        string moduleName = text[..plus].Trim().Trim('"');
        string offsetText = text[(plus + 1)..].Trim();
        if (moduleName.Length == 0 || !TryParseHexLiteral(offsetText, out ulong offset))
        {
            return false;
        }

        foreach (ModuleDescriptor module in GetModules())
        {
            if (MatchesModuleName(module, moduleName))
            {
                address = module.BaseAddress + offset;
                return true;
            }
        }

        return false;
    }

    private bool TryResolveModuleExport(string text, out ulong address)
    {
        address = 0;
        IReadOnlyList<ModuleDescriptor> modules = GetModules();
        int dot = text.IndexOf('.');
        if (dot > 0 && dot < text.Length - 1)
        {
            string moduleName = text[..dot];
            string exportName = text[(dot + 1)..];
            foreach (ModuleDescriptor module in modules)
            {
                if (MatchesModuleName(module, moduleName) &&
                    TryResolveExport(module, exportName, out address))
                {
                    return true;
                }
            }
        }

        foreach (ModuleDescriptor module in modules)
        {
            if (TryResolveExport(module, text, out address))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryResolveExport(ModuleDescriptor module, string exportName, out ulong address)
    {
        address = 0;
        foreach (PeExport export in GetExports(module))
        {
            if (!string.Equals(export.Name, exportName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(export.ForwarderName))
            {
                string forwarder = export.ForwarderName;
                int separator = forwarder.LastIndexOf('.');
                if (separator <= 0 || separator >= forwarder.Length - 1)
                {
                    return false;
                }

                string forwardedModule = forwarder[..separator];
                if (!forwardedModule.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    forwardedModule += ".dll";
                }

                foreach (ModuleDescriptor candidate in GetModules())
                {
                    if (MatchesModuleName(candidate, forwardedModule) &&
                        TryResolveExport(candidate, forwarder[(separator + 1)..], out address))
                    {
                        return true;
                    }
                }

                return false;
            }

            address = module.BaseAddress + export.FunctionRva;
            return address != 0;
        }

        return false;
    }

    private IReadOnlyList<PeExport> GetExports(ModuleDescriptor module)
    {
        if (_exportCache.TryGetValue(module.FilePath, out IReadOnlyList<PeExport>? cached))
        {
            return cached;
        }

        IReadOnlyList<PeExport> exports = [];
        if (!string.IsNullOrWhiteSpace(module.FilePath) && File.Exists(module.FilePath))
        {
            try
            {
                exports = new PeModuleAnalyzer().AnalyzeFile(module.FilePath).Exports;
            }
            catch
            {
                exports = [];
            }
        }

        _exportCache[module.FilePath] = exports;
        return exports;
    }

    private static bool MatchesModuleName(ModuleDescriptor module, string name)
    {
        string fileName = Path.GetFileName(module.FilePath);
        return string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(module.FilePath, name, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrEmpty(module.FilePath) &&
                module.FilePath.EndsWith(name, StringComparison.OrdinalIgnoreCase)) ||
               string.Equals(
                   Path.GetFileNameWithoutExtension(module.Name),
                   name,
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   Path.GetFileNameWithoutExtension(fileName),
                   name,
                   StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseHexLiteral(string text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string candidate = text.Trim();
        if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[2..];
        }
        else if (candidate.StartsWith('$'))
        {
            candidate = candidate[1..];
        }

        return candidate.Length > 0 &&
               ulong.TryParse(
                   candidate,
                   NumberStyles.HexNumber,
                   CultureInfo.InvariantCulture,
                   out value);
    }
}
