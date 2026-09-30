using DogeDebugger.Core.CrossReference;

namespace DogeDebugger.Core.StringRef;

public sealed class StringReferenceScanner
{
    public void PopulateReferences(
        IEnumerable<StringEntry> strings,
        XrefDatabase database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(database);
        foreach (StringEntry entry in strings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.References = database.Find(entry.Address)
                .Select(static reference => reference.FromAddress)
                .Distinct()
                .OrderBy(static address => address)
                .ToArray();
        }
    }
}
