namespace DogeDebugger.Core.Search;

public enum MemoryValueComparison
{
    Exact,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Between,
    UnknownInitialValue,
    Changed,
    Unchanged,
    Increased,
    Decreased,
    IncreasedBy,
    DecreasedBy
}
