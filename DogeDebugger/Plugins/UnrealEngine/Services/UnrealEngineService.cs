using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;
using DogeDebugger.Debugger.Session;
using DogeDebugger.Plugins.UnrealEngine.Models;

namespace DogeDebugger.Plugins.UnrealEngine.Services;

public sealed class UnrealEngineService
{
    private const int DefaultMaximumObjects = 500_000;
    private readonly DebuggerSession _session;
    private readonly UnrealOffsetCache _cache = new();
    private readonly object _gate = new();
    private readonly Dictionary<ulong, IReadOnlyList<UnrealEnumEntry>> _enumCache = [];
    private UnrealSession? _current;
    private UnrealNameResolver? _names;

    public UnrealEngineService(DebuggerSession session)
    {
        _session = session;
    }

    public event Action? Changed;

    public UnrealSession? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public IReadOnlyList<ModuleDescriptor> GetCandidateModules()
    {
        if (!_session.Target.IsOpen)
        {
            return [];
        }

        ModuleDescriptor[] modules = _session.EnumerateModules().ToArray();
        ModuleDescriptor[] executables = modules
            .Where(module =>
                module.FilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                module.IsMainModule)
            .ToArray();
        ModuleDescriptor[] scored = executables
            .OrderByDescending(GetModuleScore)
            .ThenByDescending(module => module.IsMainModule)
            .ToArray();
        return scored.Length > 0 ? scored : modules;
    }

    public async Task<UnrealSession> ScanAsync(
        UnrealScanOptions options,
        IProgress<UnrealScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_session.Target.IsOpen)
        {
            throw new InvalidOperationException("No target process is open.");
        }

        IReadOnlyList<ModuleDescriptor> candidates = GetCandidateModules();
        ModuleDescriptor module = ResolveModule(candidates, options.ModuleName);
        UnrealOffsets offsets = new()
        {
            PointerSize = _session.Target.Is64Bit ? 8 : 4
        };
        UnrealDiagnosticLog diagnostics = new();

        progress?.Report(new UnrealScanProgress
        {
            Stage = "缓存",
            Message = "正在检查偏移缓存",
            Progress = 0.02
        });
        bool hasCachedOffsets = false;
        string cacheError = string.Empty;
        if (options.UseCache &&
            _cache.TryLoad(module, _session.Target, out UnrealOffsets? cached, out cacheError) &&
            cached is not null)
        {
            offsets = cached;
            hasCachedOffsets = true;
            diagnostics.Success("缓存", "命中并通过基础校验，已恢复偏移。");
        }
        else if (!string.IsNullOrWhiteSpace(cacheError))
        {
            diagnostics.Warning("缓存", $"{cacheError} 继续使用手工偏移。");
        }

        ApplyManualOffsets(options, offsets);
        if (options.AutoDetect)
        {
            UnrealAutoDetector detector = new(
                _session.Target,
                _session.EnumerateMemoryRegions());
            if (!offsets.HasNamePool &&
                detector.TryFindNamePool(
                    module,
                    cancellationToken,
                    out UnrealOffsets? detectedNamePool) &&
                detectedNamePool is not null)
            {
                offsets = detectedNamePool;
                diagnostics.Success(
                    "NamePool",
                    $"自动定位到 0x{offsets.NamePoolAddress:X}。");
            }

            if (!offsets.HasObjectArray && offsets.HasNamePool)
            {
                UnrealNameResolver detectorNames = new(_session.Target, offsets);
                if (detector.TryFindGObjects(
                        module,
                        detectorNames,
                        cancellationToken,
                        out UnrealOffsets? detectedObjects) &&
                    detectedObjects is not null)
                {
                    offsets.GObjectsAddress = detectedObjects.GObjectsAddress;
                    offsets.ObjectArrayKind = detectedObjects.ObjectArrayKind;
                    offsets.ObjectArrayObjectsOffset =
                        detectedObjects.ObjectArrayObjectsOffset;
                    offsets.ObjectArrayMaxOffset =
                        detectedObjects.ObjectArrayMaxOffset;
                    offsets.ObjectArrayNumOffset =
                        detectedObjects.ObjectArrayNumOffset;
                    offsets.ObjectArrayMaxChunksOffset =
                        detectedObjects.ObjectArrayMaxChunksOffset;
                    offsets.ObjectArrayNumChunksOffset =
                        detectedObjects.ObjectArrayNumChunksOffset;
                    offsets.ObjectArrayChunkSize =
                        detectedObjects.ObjectArrayChunkSize;
                    offsets.FUObjectItemSize = detectedObjects.FUObjectItemSize;
                    offsets.FUObjectItemObjectOffset =
                        detectedObjects.FUObjectItemObjectOffset;
                    diagnostics.Success(
                        "GObjects",
                        $"自动定位到 0x{offsets.GObjectsAddress:X}（{offsets.ObjectArrayKind}）。");
                }
            }
        }

        if (!offsets.HasNamePool)
        {
            diagnostics.Failure(
                "NamePool",
                "未找到有效的 FNamePool/GNames。请在面板中填写名字池地址后重扫。");
        }

        if (!offsets.HasObjectArray)
        {
            diagnostics.Failure(
                "GObjects",
                "未找到有效的对象表。请在面板中填写 GObjects 地址并选择对象表布局。");
        }

        UnrealSession session = new()
        {
            Module = module,
            Offsets = offsets,
            Diagnostics = diagnostics
        };
        if (!offsets.HasNamePool || !offsets.HasObjectArray)
        {
            Publish(session);
            return session;
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new UnrealScanProgress
        {
            Stage = "NamePool",
            Message = "正在读取 FName 索引",
            Progress = 0.15
        });
        UnrealNameResolver names = new(_session.Target, offsets);
        _names = names;
        if (names.TryResolveIndex(0, out string firstName))
        {
            diagnostics.Success("NamePool", $"0 号名字：{firstName}");
        }
        else
        {
            diagnostics.Warning("NamePool", "0 号名字无法读取，名称字段可能为空。");
        }

        progress?.Report(new UnrealScanProgress
        {
            Stage = "GObjects",
            Message = "正在读取对象表",
            Progress = 0.28
        });
        UnrealObjectArrayReader reader = new(_session.Target, offsets, names);
        if (!reader.TryReadCount(out int objectCount))
        {
            diagnostics.Failure("GObjects", "对象表数量字段无效。");
            Publish(session);
            return session;
        }

        int maximum = Math.Clamp(objectCount, 1, DefaultMaximumObjects);
        IReadOnlyList<UnrealObjectInfo> objects = await Task.Run(
                () => reader.ReadObjects(maximum, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        session = new UnrealSession
        {
            Module = module,
            Offsets = offsets,
            ObjectCount = objectCount,
            Truncated = objectCount > maximum,
            Objects = objects,
            Packages = objects
                .Where(item => item.Kind == UnrealObjectKind.Package)
                .ToArray(),
            Types = objects
                .Where(item => item.Kind is
                    UnrealObjectKind.Class or
                    UnrealObjectKind.Struct or
                    UnrealObjectKind.Function or
                    UnrealObjectKind.Enum)
                .Select(item => new UnrealTypeInfo { Object = item })
                .ToArray(),
            Diagnostics = diagnostics
        };
        if (options.AutoDetect)
        {
            if (offsets.NamePoolKind == UnrealNamePoolKind.LegacyGNames ||
                offsets.UsesLegacyUProperties)
            {
                UnrealLegacyPropertyLayoutDetector legacyDetector = new(
                    _session.Target,
                    offsets,
                    session.Objects);
                if (legacyDetector.TryDetect(
                        cancellationToken,
                        out string legacyLayoutMessage))
                {
                    diagnostics.Success("旧版反射布局", legacyLayoutMessage);
                }
                else
                {
                    diagnostics.Warning(
                        "旧版反射布局",
                        "反射样本不足，保留当前 FProperty/UStruct 偏移。");
                }
            }

            UnrealAutoDetector detector = new(
                _session.Target,
                _session.EnumerateMemoryRegions());
            UnrealWorldLayoutDetector worldDetector = new(
                _session.Target,
                offsets,
                names,
                session.Objects);

            if (offsets.GWorldAddress == 0 &&
                worldDetector.TryFindWorldObject(out UnrealObjectInfo? worldObject) &&
                worldObject is not null &&
                detector.TryFindGlobalPointer(
                    module,
                    worldObject.Address,
                    cancellationToken,
                    out ulong gWorldSlot))
            {
                offsets.GWorldAddress = gWorldSlot;
                diagnostics.Success(
                    "GWorld",
                    $"已定位全局指针 0x{gWorldSlot:X} -> {worldObject.FullName}。");
            }

            if (offsets.GEngineAddress == 0 &&
                worldDetector.TryFindEngineObject(out UnrealObjectInfo? engineObject) &&
                engineObject is not null &&
                detector.TryFindGlobalPointer(
                    module,
                    engineObject.Address,
                    cancellationToken,
                    out ulong gEngineSlot))
            {
                offsets.GEngineAddress = gEngineSlot;
                diagnostics.Success(
                    "GEngine",
                    $"已定位全局指针 0x{gEngineSlot:X} -> {engineObject.FullName}。");
            }

            if (worldDetector.TryDetectWorldLayout(
                    cancellationToken,
                    out string worldLayoutMessage))
            {
                diagnostics.Success("World 布局", worldLayoutMessage);
            }
        }

        if (offsets.UEnumNamesLayout == UnrealEnumNamesLayout.Unknown &&
            session.Types.Any(type => type.Kind == UnrealObjectKind.Enum))
        {
            if (TryDetectEnumLayout(
                    session,
                    session.Objects,
                    out int enumNamesOffset,
                    out UnrealEnumNamesLayout enumNamesLayout,
                    out int enumLayoutScore,
                    out string enumLayoutFailure))
            {
                offsets.UEnumNames = enumNamesOffset;
                offsets.UEnumNamesLayout = enumNamesLayout;
                diagnostics.Success(
                    "UEnum",
                    $"Names 偏移 0x{enumNamesOffset:X}，元素布局 {enumNamesLayout}。");
            }
            else
            {
                diagnostics.Warning(
                    "UEnum",
                    $"已发现 Enum 对象，但无法确定 UEnum::Names 布局（候选 {session.Types.Count(type => type.Kind == UnrealObjectKind.Enum)}，分数 {enumLayoutScore}，{enumLayoutFailure}）。");
            }
        }

        diagnostics.Success(
            "完成",
            $"已读取 {objects.Count:N0} 个对象，{session.Types.Count:N0} 个类型。");
        if (!hasCachedOffsets && options.PersistCache)
        {
            _cache.Save(module, offsets, _session.Target);
            diagnostics.Success("缓存", "已将偏移写入缓存。");
        }

        Publish(session);
        return session;
    }

    public UnrealObjectInfo? FindObject(ulong address) =>
        Current?.Objects.FirstOrDefault(item => item.Address == address);

    public UnrealTypeInfo? FindType(ulong address) =>
        Current?.Types.FirstOrDefault(item => item.Object.Address == address);

    public IReadOnlyList<UnrealWorldActorInfo> ReadWorldActors(
        CancellationToken cancellationToken = default)
    {
        UnrealSession? session = Current;
        if (session is null ||
            session.Offsets.GWorldAddress == 0 ||
            session.Offsets.UWorldPersistentLevel <= 0)
        {
            return [];
        }

        ulong world = ResolveWorldAddress(session.Offsets.GWorldAddress);
        if (world == 0 ||
            !TryReadPointer(
                world + (uint)session.Offsets.UWorldPersistentLevel,
                out ulong level) ||
            level == 0)
        {
            return [];
        }

        ulong actorsAddress = level + (uint)session.Offsets.ULevelActors;
        if (!TryReadPointer(actorsAddress, out ulong actors) ||
            actors == 0 ||
            !TryReadInt32(actorsAddress + 8, out int count) ||
            !TryReadInt32(actorsAddress + 12, out int maximum) ||
            count is <= 0 or > 1_000_000 ||
            maximum < count ||
            maximum > 2_000_000)
        {
            return [];
        }

        List<UnrealWorldActorInfo> result = [];
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadPointer(actors + (uint)(index * 8), out ulong actor) ||
                actor == 0)
            {
                continue;
            }

            UnrealObjectInfo? objectInfo = FindObject(actor);
            result.Add(new UnrealWorldActorInfo
            {
                Index = index,
                Address = actor,
                Name = objectInfo?.Name ?? string.Empty,
                ClassName = objectInfo?.ClassName ?? string.Empty,
                PathName = objectInfo?.PathName ?? string.Empty
            });
        }

        return result;
    }

    private ulong ResolveWorldAddress(ulong address)
    {
        if (address == 0)
        {
            return 0;
        }

        if (IsObjectDerivedFrom(address, "World"))
        {
            return address;
        }

        return TryReadPointer(address, out ulong world) &&
               IsObjectDerivedFrom(world, "World")
            ? world
            : 0;
    }

    private bool IsObjectDerivedFrom(ulong objectAddress, string baseClassName)
    {
        if (objectAddress == 0 ||
            !TryReadPointer(
                objectAddress + (uint)(Current?.Offsets.UObjectClass ?? 16),
                out ulong classAddress))
        {
            return false;
        }

        HashSet<ulong> visited = [];
        int depth = 0;
        while (classAddress != 0 &&
               visited.Add(classAddress) &&
               depth < 64)
        {
            string className = FindObject(classAddress)?.Name ??
                               ResolveObjectClassName(classAddress);
            if (className.Equals(
                    baseClassName,
                    StringComparison.OrdinalIgnoreCase) ||
                className.Contains(
                    baseClassName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!TryReadPointer(
                    classAddress +
                    (uint)(Current?.Offsets.UStructSuperStruct ?? 64),
                    out classAddress))
            {
                break;
            }

            depth++;
        }

        return false;
    }

    public IReadOnlyList<UnrealInheritanceInfo> GetInheritance(
        UnrealObjectInfo? objectInfo)
    {
        UnrealSession? session = Current;
        if (objectInfo is null || session is null)
        {
            return [];
        }

        List<UnrealInheritanceInfo> result = [];
        HashSet<ulong> visited = [];
        ulong address = objectInfo.Address;
        int level = 0;
        while (address != 0 && visited.Add(address) && level < 64)
        {
            result.Add(new UnrealInheritanceInfo
            {
                Level = level,
                Address = address,
                Name = ResolveObjectName(address)
            });
            if (!TryReadPointer(
                    address + (uint)session.Offsets.UStructSuperStruct,
                    out address))
            {
                break;
            }

            level++;
        }

        return result;
    }

    public IReadOnlyList<UnrealMemberInfo> GetMembers(
        UnrealObjectInfo? objectInfo,
        bool includeInherited = true)
    {
        UnrealSession? session = Current;
        if (objectInfo is null ||
            session is null ||
            objectInfo.Kind is not (
                UnrealObjectKind.Class or
                UnrealObjectKind.Struct or
                UnrealObjectKind.Function))
        {
            return [];
        }

        List<UnrealMemberInfo> result = [];
        HashSet<ulong> visited = [];
        CollectMembers(objectInfo, includeInherited, result, visited, depth: 0);
        return result
            .GroupBy(member => member.Address == 0
                ? $"{member.Name}:{member.Offset}"
                : member.Address.ToString("X"))
            .Select(group => group.First())
            .OrderBy(member => member.Offset < 0 ? int.MaxValue : member.Offset)
            .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<UnrealMemberInfo> GetFunctions(
        UnrealObjectInfo? objectInfo)
    {
        UnrealSession? session = Current;
        if (objectInfo is null || session is null)
        {
            return [];
        }

        List<UnrealMemberInfo> result = [];
        HashSet<ulong> visited = [];
        CollectFunctions(objectInfo, result, visited, depth: 0);
        return result
            .GroupBy(member => member.Address)
            .Select(group => group.First())
            .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<UnrealEnumEntry> GetEnumEntries(
        UnrealObjectInfo? objectInfo)
    {
        UnrealSession? session = Current;
        if (objectInfo is null ||
            session is null ||
            _names is null ||
            session.Offsets.UEnumNamesLayout == UnrealEnumNamesLayout.Unknown ||
            !_names.TryResolveName(
                objectInfo.Address + (uint)session.Offsets.UObjectName,
                out _))
        {
            return [];
        }

        lock (_gate)
        {
            if (_enumCache.TryGetValue(
                    objectInfo.Address,
                    out IReadOnlyList<UnrealEnumEntry>? cached))
            {
                return cached;
            }
        }

        if (!TryReadEnumEntries(
                session,
                objectInfo.Address,
                session.Offsets.UEnumNames,
                session.Offsets.UEnumNamesLayout,
                4096,
                strict: false,
                out IReadOnlyList<UnrealEnumEntry> entries,
                out _))
        {
            return [];
        }

        lock (_gate)
        {
            _enumCache[objectInfo.Address] = entries;
        }

        return entries;
    }

    public bool TryResolveEnumValue(
        ulong enumAddress,
        long value,
        out string name)
    {
        name = string.Empty;
        if (enumAddress == 0)
        {
            return false;
        }

        foreach (UnrealEnumEntry entry in GetEnumEntries(FindObject(enumAddress)))
        {
            if (entry.Value == value)
            {
                name = entry.ShortName;
                return true;
            }
        }

        return false;
    }

    public string GetMemberTypeName(UnrealMemberInfo? member)
    {
        UnrealSession? session = Current;
        UnrealNameResolver? names = _names;
        if (member is null || session is null || names is null)
        {
            return member?.TypeName ?? string.Empty;
        }

        return CreateValueReader(session, names).GetDisplayTypeName(member);
    }

    public IReadOnlyList<UnrealMemberValueEntry> ReadObjectValues(
        UnrealObjectInfo? objectInfo,
        IReadOnlyList<UnrealMemberInfo>? members = null,
        UnrealValueReadOptions? options = null)
    {
        UnrealSession? session = Current;
        UnrealNameResolver? names = _names;
        if (objectInfo is null || session is null || names is null)
        {
            return [];
        }

        IReadOnlyList<UnrealMemberInfo> layout = members ??
            GetMembers(FindObject(objectInfo.ClassAddress), includeInherited: true);
        if (layout.Count == 0)
        {
            return [];
        }

        return CreateValueReader(session, names).ReadObjectValues(
            objectInfo,
            layout,
            options ?? new UnrealValueReadOptions());
    }

    public UnrealObjectInfo? FindObjectByPath(string path)
    {
        UnrealSession? session = Current;
        if (session is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.Trim();
        UnrealObjectInfo? exact = session.Objects.FirstOrDefault(item =>
            string.Equals(item.PathName, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.FullName, normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        UnrealObjectInfo[] matches = session.Objects
            .Where(item =>
                item.PathName.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.FullName.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private bool TryDetectEnumLayout(
        UnrealSession session,
        IReadOnlyList<UnrealObjectInfo> objects,
        out int namesOffset,
        out UnrealEnumNamesLayout layout,
        out int bestScore,
        out string failure)
    {
        namesOffset = -1;
        layout = UnrealEnumNamesLayout.Unknown;
        bestScore = 0;
        failure = string.Empty;

        UnrealObjectInfo[] candidates = objects
            .Where(item => item.Kind == UnrealObjectKind.Enum && item.Address != 0)
            .OrderByDescending(item => IsKnownEnumName(item.Name))
            .Take(32)
            .ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        int pointerSize = session.Offsets.PointerSize;
        string bestFailure = string.Empty;
        for (int offset = 48; offset <= 128; offset += Math.Max(pointerSize, 4))
        {
            foreach (UnrealEnumNamesLayout candidateLayout in new[]
                     {
                         UnrealEnumNamesLayout.NameInt64,
                         UnrealEnumNamesLayout.NameUInt8,
                         UnrealEnumNamesLayout.NameOnly
                     })
            {
                int score = 0;
                foreach (UnrealObjectInfo candidate in candidates)
                {
                    if (!TryReadEnumEntries(
                            session,
                            candidate.Address,
                            offset,
                            candidateLayout,
                            12,
                            strict: true,
                            out IReadOnlyList<UnrealEnumEntry> entries,
                            out string readFailure) ||
                        entries.Count == 0)
                    {
                        if (bestFailure.Length == 0 &&
                            offset == 64 &&
                            candidateLayout == UnrealEnumNamesLayout.NameInt64)
                        {
                            bestFailure = readFailure;
                        }

                        continue;
                    }

                    score++;
                    if (IsKnownEnumName(candidate.Name) &&
                        MatchesKnownEnumEntries(candidate.Name, entries))
                    {
                        score += 10;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    namesOffset = offset;
                    layout = candidateLayout;
                }
            }
        }

        if (bestScore >= 2)
        {
            return true;
        }

        failure = bestFailure;
        return false;
    }

    private bool TryReadEnumEntries(
        UnrealSession session,
        ulong enumAddress,
        int namesOffset,
        UnrealEnumNamesLayout layout,
        int maximum,
        bool strict,
        out IReadOnlyList<UnrealEnumEntry> entries,
        out string failure)
    {
        entries = [];
        failure = string.Empty;
        UnrealNameResolver? resolver = _names;
        if (resolver is null ||
            layout == UnrealEnumNamesLayout.Unknown ||
            namesOffset < 0)
        {
            failure = "名称解析器或布局无效。";
            return false;
        }

        ulong namesAddress = enumAddress + (uint)namesOffset;
        if (!TryReadPointer(namesAddress, out ulong data) ||
            data == 0 ||
            !TryReadInt32(
                namesAddress + (uint)session.Offsets.PointerSize,
                out int count) ||
            !TryReadInt32(
                namesAddress + (uint)session.Offsets.PointerSize + 4,
                out int maximumCount) ||
            count is < 1 or > 4096 ||
            maximumCount < count ||
            maximumCount > 65536)
        {
            failure = "TArray 头无效。";
            return false;
        }

        int elementSize = layout switch
        {
            UnrealEnumNamesLayout.NameOnly => 8,
            UnrealEnumNamesLayout.NameUInt8 => 12,
            _ => 16
        };
        int readCount = Math.Min(Math.Min(count, maximum), 4096);
        List<UnrealEnumEntry> result = new(readCount);
        HashSet<string> names = new(StringComparer.Ordinal);
        for (int index = 0; index < readCount; index++)
        {
            ulong entryAddress = data + (uint)(index * elementSize);
            if (!resolver.TryResolveName(entryAddress, out string name) ||
                string.IsNullOrWhiteSpace(name) ||
                !names.Add(name))
            {
                if (strict)
                {
                    failure = $"条目 {index} 的名称解析失败（FName 地址 0x{entryAddress:X}）。";
                    return false;
                }

                continue;
            }

            long value;
            if (layout == UnrealEnumNamesLayout.NameOnly)
            {
                value = index;
            }
            else if (layout == UnrealEnumNamesLayout.NameUInt8)
            {
                if (!TryReadByte(entryAddress + 8, out byte byteValue))
                {
                    failure = $"条目 {index} 的 uint8 值读取失败。";
                    return false;
                }

                value = byteValue;
            }
            else
            {
                if (!TryReadInt64(entryAddress + 8, out value))
                {
                    failure = $"条目 {index} 的 int64 值读取失败。";
                    return false;
                }
            }

            result.Add(new UnrealEnumEntry
            {
                Name = name,
                ShortName = GetEnumShortName(name),
                Value = value
            });
        }

        entries = result;
        return result.Count > 0;
    }

    private static bool IsKnownEnumName(string name) =>
        name is "ENetRole" or
            "ETraceTypeQuery" or
            "ECollisionChannel" or
            "EAlphaBlendOption";

    private static bool MatchesKnownEnumEntries(
        string enumName,
        IReadOnlyList<UnrealEnumEntry> entries)
    {
        string[] expected = enumName switch
        {
            "ENetRole" =>
            [
                "ROLE_None",
                "ROLE_SimulatedProxy",
                "ROLE_AutonomousProxy"
            ],
            "ETraceTypeQuery" =>
            [
                "TraceTypeQuery1",
                "TraceTypeQuery2",
                "TraceTypeQuery3"
            ],
            "ECollisionChannel" =>
            [
                "ECC_WorldStatic",
                "ECC_WorldDynamic",
                "ECC_Pawn"
            ],
            "EAlphaBlendOption" =>
            [
                "Linear",
                "Cubic",
                "HermiteCubic"
            ],
            _ => []
        };
        for (int index = 0; index < expected.Length && index < entries.Count; index++)
        {
            if (!entries[index].ShortName.Equals(
                    expected[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return expected.Length > 0;
    }

    private static string GetEnumShortName(string name)
    {
        int separator = name.IndexOf("::", StringComparison.Ordinal);
        return separator < 0 || separator + 2 >= name.Length
            ? name
            : name[(separator + 2)..];
    }

    private void CollectMembers(
        UnrealObjectInfo objectInfo,
        bool includeInherited,
        List<UnrealMemberInfo> result,
        HashSet<ulong> visited,
        int depth)
    {
        UnrealSession? session = Current;
        if (session is null || depth >= 64 || !visited.Add(objectInfo.Address))
        {
            return;
        }

        if (!session.Offsets.UsesLegacyUProperties &&
            TryReadPointer(
                objectInfo.Address +
                (uint)session.Offsets.UStructChildProperties,
                out ulong field) &&
            field != 0)
        {
            HashSet<ulong> fields = [];
            while (field != 0 && fields.Add(field) && result.Count < 8192)
            {
                result.Add(ReadField(field, objectInfo.Name));
                if (!TryReadPointer(
                        field + (uint)session.Offsets.FFieldNext,
                        out field))
                {
                    break;
                }
            }
        }
        else if (TryReadPointer(
                     objectInfo.Address +
                     (uint)session.Offsets.UStructChildren,
                     out ulong child) &&
                 child != 0)
        {
            HashSet<ulong> children = [];
            while (child != 0 && children.Add(child) && result.Count < 8192)
            {
                string childClass = ResolveObjectClassName(child);
                if (!childClass.Contains("Function", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(ReadLegacyField(child, objectInfo.Name, childClass));
                }

                if (!TryReadPointer(
                        child + (uint)session.Offsets.UFieldNext,
                        out child))
                {
                    break;
                }
            }
        }

        if (includeInherited &&
            TryReadPointer(
                objectInfo.Address + (uint)session.Offsets.UStructSuperStruct,
                out ulong super) &&
            super != 0)
        {
            UnrealObjectInfo? superObject = FindObject(super);
            if (superObject is not null)
            {
                CollectMembers(superObject, includeInherited, result, visited, depth + 1);
            }
        }
    }

    private void CollectFunctions(
        UnrealObjectInfo objectInfo,
        List<UnrealMemberInfo> result,
        HashSet<ulong> visited,
        int depth)
    {
        UnrealSession? session = Current;
        if (session is null || depth >= 64 || !visited.Add(objectInfo.Address))
        {
            return;
        }

        if (TryReadPointer(
                objectInfo.Address + (uint)session.Offsets.UStructChildren,
                out ulong child) &&
            child != 0)
        {
            HashSet<ulong> children = [];
            while (child != 0 && children.Add(child) && result.Count < 8192)
            {
                string className = ResolveObjectClassName(child);
                if (className.Contains("Function", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(ReadFunction(child, objectInfo.Name, className));
                }

                if (!TryReadPointer(
                        child + (uint)session.Offsets.UFieldNext,
                        out child))
                {
                    break;
                }
            }
        }

        if (TryReadPointer(
                objectInfo.Address + (uint)session.Offsets.UStructSuperStruct,
                out ulong super) &&
            super != 0)
        {
            UnrealObjectInfo? superObject = FindObject(super);
            if (superObject is not null)
            {
                CollectFunctions(superObject, result, visited, depth + 1);
            }
        }
    }

    private UnrealMemberInfo ReadField(ulong address, string declaringType)
    {
        UnrealSession session = Current!;
        string name = _names?.TryResolveName(
                address + (uint)session.Offsets.FFieldName,
                out string resolvedName) == true
            ? resolvedName
            : string.Empty;
        int offset = TryReadInt32(
            address + (uint)session.Offsets.FPropertyOffsetInternal,
            out int propertyOffset)
                ? propertyOffset
                : -1;
        int size = TryReadInt32(
            address + (uint)session.Offsets.FPropertyElementSize,
            out int propertySize)
                ? propertySize
                : 0;
        int arrayDim = TryReadInt32(
            address + (uint)session.Offsets.FPropertyArrayDim,
            out int propertyArrayDim)
                ? propertyArrayDim
                : 0;
        ulong flags = TryReadPointer(
            address + (uint)session.Offsets.FPropertyPropertyFlags,
            out ulong propertyFlags)
                ? propertyFlags
                : 0;
        return new UnrealMemberInfo
        {
            Source = "FField",
            Address = address,
            Name = name,
            TypeName = ResolveFieldClassName(address),
            Offset = offset,
            Size = size,
            ArrayDim = arrayDim,
            Flags = flags,
            DeclaringType = declaringType
        };
    }

    private UnrealMemberInfo ReadLegacyField(
        ulong address,
        string declaringType,
        string className)
    {
        return new UnrealMemberInfo
        {
            Source = "UField",
            Address = address,
            Name = ResolveObjectName(address),
            TypeName = string.IsNullOrWhiteSpace(className) ? "Property" : className,
            Offset = -1,
            DeclaringType = declaringType
        };
    }

    private UnrealMemberInfo ReadFunction(
        ulong address,
        string declaringType,
        string className)
    {
        UnrealSession session = Current!;
        ulong flags = TryReadPointer(
            address + (uint)session.Offsets.UFunctionFunctionFlags,
            out ulong functionFlags)
                ? functionFlags
                : 0;
        ulong exec = TryReadPointer(
            address + (uint)session.Offsets.UFunctionExecFunction,
            out ulong execAddress)
                ? execAddress
                : 0;
        return new UnrealMemberInfo
        {
            Source = "UFunction",
            Address = address,
            Name = ResolveObjectName(address),
            TypeName = className,
            Offset = -1,
            Flags = flags,
            NativeAddress = exec,
            DeclaringType = declaringType
        };
    }

    private string ResolveObjectName(ulong address) =>
        FindObject(address)?.Name ??
        (_names?.TryResolveName(
             address + (uint)(Current?.Offsets.UObjectName ?? 0),
             out string name) == true
            ? name
            : string.Empty);

    private string ResolveObjectClassName(ulong address)
    {
        UnrealObjectInfo? objectInfo = FindObject(address);
        if (objectInfo is not null)
        {
            return objectInfo.ClassName;
        }

        UnrealSession? session = Current;
        if (session is null ||
            !TryReadPointer(
                address + (uint)session.Offsets.UObjectClass,
                out ulong classAddress))
        {
            return string.Empty;
        }

        return FindObject(classAddress)?.Name ??
               (_names?.TryResolveName(
                    classAddress + (uint)session.Offsets.UObjectName,
                    out string name) == true
                   ? name
                   : string.Empty);
    }

    private string ResolveFieldClassName(ulong fieldAddress)
    {
        UnrealSession? session = Current;
        if (session is null ||
            !TryReadPointer(
                fieldAddress + (uint)session.Offsets.FFieldClass,
                out ulong fieldClass))
        {
            return "Property";
        }

        return FindObject(fieldClass)?.Name ??
               (_names?.TryResolveName(
                    fieldClass + (uint)session.Offsets.FFieldClassName,
                    out string name) == true
                   ? name
                   : "Property");
    }

    private void ApplyManualOffsets(UnrealScanOptions options, UnrealOffsets offsets)
    {
        if (options.NamePoolAddress != 0)
        {
            offsets.NamePoolAddress = options.NamePoolAddress;
            offsets.NamePoolKind = UnrealNamePoolKind.FNamePool;
        }

        if (options.NamePoolBlockArrayAddress != 0)
        {
            offsets.NamePoolBlockArrayAddress = options.NamePoolBlockArrayAddress;
        }

        if (options.FNameEntryStride > 0)
        {
            offsets.FNameEntryStride = options.FNameEntryStride;
        }

        if (options.FNameBlockOffsetBits > 0)
        {
            offsets.FNameBlockOffsetBits = options.FNameBlockOffsetBits;
        }

        if (options.GObjectsAddress != 0)
        {
            offsets.GObjectsAddress = options.GObjectsAddress;
        }

        if (options.ObjectArrayKind != UnrealObjectArrayKind.Unknown)
        {
            offsets.ObjectArrayKind = options.ObjectArrayKind;
        }

        if (options.GWorldAddress != 0)
        {
            offsets.GWorldAddress = options.GWorldAddress;
        }

        if (options.GEngineAddress != 0)
        {
            offsets.GEngineAddress = options.GEngineAddress;
        }
    }

    private static ModuleDescriptor ResolveModule(
        IReadOnlyList<ModuleDescriptor> candidates,
        string? moduleName)
    {
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("没有可扫描的模块。");
        }

        if (!string.IsNullOrWhiteSpace(moduleName))
        {
            ModuleDescriptor? selected = candidates.FirstOrDefault(module =>
                module.Name.Contains(moduleName, StringComparison.OrdinalIgnoreCase) ||
                module.FilePath.Contains(moduleName, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return selected;
            }
        }

        return candidates[0];
    }

    private static int GetModuleScore(ModuleDescriptor module)
    {
        string text = $"{module.Name} {module.FilePath}";
        int score = module.IsMainModule ? 20 : 0;
        if (text.Contains("-Win64-Shipping", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (text.Contains("-Win64-", StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }

        if (text.Contains("Unreal", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UE4", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UE5", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        return score;
    }

    private UnrealValueReader CreateValueReader(
        UnrealSession session,
        UnrealNameResolver names) => new(
        _session.Target,
        session.Offsets,
        names,
        session.Objects,
        GetMembers,
        (address, value) => TryResolveEnumValue(
            address,
            value,
            out string enumName)
                ? enumName
                : null);

    private void Publish(UnrealSession session)
    {
        lock (_gate)
        {
            _current = session;
            _enumCache.Clear();
        }

        Changed?.Invoke();
    }

    private bool TryReadPointer(ulong address, out ulong value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[8];
        int size = _session.Target.Is64Bit ? 8 : 4;
        if (!_session.Target.TryReadBytes(address, bytes[..size]))
        {
            return false;
        }

        value = size == 8
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes)
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    private bool TryReadInt32(ulong address, out int value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[4];
        if (!_session.Target.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    private bool TryReadInt64(ulong address, out long value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[8];
        if (!_session.Target.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes);
        return true;
    }

    private bool TryReadByte(ulong address, out byte value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[1];
        if (!_session.Target.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = bytes[0];
        return true;
    }
}
