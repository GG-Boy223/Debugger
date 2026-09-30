using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

namespace DogeDebugger.UI.ViewModels.Panels;

public enum SourceLineMarkerKind
{
    None,
    Current,
    Exception
}

public sealed partial class SourceDocument : ObservableObject
{
    public SourceDocument(string filePath, string text)
    {
        FilePath = filePath;
        _text = text;
    }

    public string FilePath { get; }

    public string Title => Path.GetFileName(FilePath);

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private int _markerLine;

    [ObservableProperty]
    private int _markerColumn = 1;

    [ObservableProperty]
    private SourceLineMarkerKind _markerKind;

    public void ApplyMarker(
        int line,
        int column,
        SourceLineMarkerKind markerKind)
    {
        MarkerLine = Math.Max(0, line);
        MarkerColumn = Math.Max(1, column);
        MarkerKind = markerKind;
    }

    public void ClearExecution() => ApplyMarker(0, 1, SourceLineMarkerKind.None);
}

public sealed class ParameterNode
{
    public ParameterNode(
        string name,
        string type,
        string value,
        bool isLocalGroup = false,
        bool isPlaceholder = false,
        bool hasError = false,
        bool isSeparator = false)
    {
        Name = name;
        Type = type;
        Value = value;
        IsLocalGroup = isLocalGroup;
        IsPlaceholder = isPlaceholder;
        HasError = hasError;
        IsSeparator = isSeparator;
    }

    public string Name { get; }

    public string Type { get; }

    public string Value { get; }

    public bool IsLocalGroup { get; }

    public bool IsPlaceholder { get; }

    public bool HasError { get; }

    public bool IsSeparator { get; }

    public bool IsExpanded { get; set; } = true;

    public IList<ParameterNode> Children { get; } = [];
}
