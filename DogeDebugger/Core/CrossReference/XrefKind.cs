namespace DogeDebugger.Core.CrossReference;

public enum XrefKind
{
    Call,
    TailCall,
    Lea,
    Jump,
    Read,
    Write,
    Address
}
