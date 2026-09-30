using System.Text.Json.Serialization;

namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealOffsets
{
    public int PointerSize { get; set; } = 8;

    public UnrealNamePoolKind NamePoolKind { get; set; }

    public ulong NamePoolAddress { get; set; }

    public ulong NamePoolBlockArrayAddress { get; set; }

    public ulong NamePoolFirstBlockAddress { get; set; }

    public int FNameEntryStride { get; set; } = 2;

    public int FNameBlockOffsetBits { get; set; } = 16;

    public bool LegacyNamePoolIsIndirect { get; set; }

    public int LegacyNameEntryIndexOffset { get; set; }

    public int LegacyNameEntryStringOffset { get; set; } = 16;

    public int LegacyNameCountOffset { get; set; } = 1024;

    public int LegacyNameChunkCountOffset { get; set; } = 1028;

    public ulong GObjectsAddress { get; set; }

    public UnrealObjectArrayKind ObjectArrayKind { get; set; }

    public int ObjectArrayObjectsOffset { get; set; }

    public int ObjectArrayNumOffset { get; set; } = 20;

    public int ObjectArrayMaxOffset { get; set; } = 16;

    public int ObjectArrayNumChunksOffset { get; set; } = 28;

    public int ObjectArrayMaxChunksOffset { get; set; } = 24;

    public int ObjectArrayChunkSize { get; set; } = 65536;

    public int FUObjectItemSize { get; set; } = 24;

    public int FUObjectItemObjectOffset { get; set; }

    public ulong GWorldAddress { get; set; }

    public ulong GEngineAddress { get; set; }

    public int UObjectFlags { get; set; } = 8;

    public int UObjectIndex { get; set; } = 12;

    public int UObjectClass { get; set; } = 16;

    public int UObjectName { get; set; } = 24;

    public int UObjectOuter { get; set; } = 32;

    public int UFieldNext { get; set; } = 40;

    public bool UsesLegacyUProperties { get; set; }

    public int UStructSuperStruct { get; set; } = 64;

    public int UStructChildren { get; set; } = 72;

    public int UStructChildProperties { get; set; } = 80;

    public int UStructPropertiesSize { get; set; } = 88;

    public int UStructMinAlignment { get; set; } = 92;

    public int UFunctionFunctionFlags { get; set; } = 176;

    public int UFunctionExecFunction { get; set; } = 216;

    public int FFieldClass { get; set; } = 8;

    public int FFieldNext { get; set; } = 32;

    public int FFieldName { get; set; } = 40;

    public int FFieldClassName { get; set; }

    public int FFieldOwner { get; set; } = 16;

    public int FFieldClassSuperClass { get; set; } = 32;

    public int FFieldClassCastFlags { get; set; } = 16;

    public int FPropertyArrayDim { get; set; } = 56;

    public int FPropertyElementSize { get; set; } = 60;

    public int FPropertyPropertyFlags { get; set; } = 64;

    public int FPropertyOffsetInternal { get; set; } = 76;

    public int FPropertyPropertyLinkNext { get; set; } = 88;

    public int FPropertyBitMaskField { get; set; } = 120;

    public int FPropertyObjectClassType { get; set; } = 120;

    public int UClassPropertyLink { get; set; } = 112;

    public int UClassPropertyLinkAlt { get; set; } = 120;

    public int UWorldPersistentLevel { get; set; } = 48;

    public int ULevelActors { get; set; } = 152;

    public int UEnumNames { get; set; } = 64;

    public UnrealEnumNamesLayout UEnumNamesLayout { get; set; }

    public int FPropertyEnumField { get; set; } = 128;

    [JsonIgnore]
    public bool HasNamePool =>
        NamePoolFirstBlockAddress != 0 ||
        NamePoolBlockArrayAddress != 0 ||
        NamePoolAddress != 0;

    [JsonIgnore]
    public bool HasObjectArray =>
        GObjectsAddress != 0 &&
        ObjectArrayKind != UnrealObjectArrayKind.Unknown;

    public UnrealOffsets Clone()
    {
        return (UnrealOffsets)MemberwiseClone();
    }
}
