using System.ComponentModel;
using System.Runtime.CompilerServices;
using DogeDebugger.Core.Modules;

namespace DogeDebugger.UI.ViewModels.Dialogs;

public sealed class ModuleSelectionItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public ModuleSelectionItem(ModuleDescriptor module, bool isSelected)
    {
        Module = module;
        _isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ModuleDescriptor Module { get; }

    public string Name => Module.Name;

    public string BaseAddressText => $"0x{Module.BaseAddress:X}";

    public string SizeText => Module.Size < 1024
        ? $"({Module.Size:N0} B)"
        : $"({Module.Size / 1024:N0} KB)";

    public string Path => Module.FilePath;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
