using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.ComponentModel;

namespace DogeDebugger.UI.Views.Panels;

public partial class LuaScriptView : UserControl
{
    private bool _synchronizing;
    private bool _updatingViewModelFromEditor;
    private MainViewModel? _observedViewModel;

    public LuaScriptView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        ScriptEditor.TextChanged += OnScriptTextChanged;
        ScriptEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        SynchronizeFromViewModel();
        UpdateCaret();
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _observedViewModel = DataContext as MainViewModel;
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        SynchronizeFromViewModel();
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (_updatingViewModelFromEditor)
        {
            return;
        }

        if (eventArgs.PropertyName == nameof(MainViewModel.LuaScriptText) &&
            _observedViewModel is { } viewModel &&
            !string.Equals(
                ScriptEditor.Text,
                viewModel.LuaScriptText,
                StringComparison.Ordinal))
        {
            SynchronizeFromViewModel();
        }
    }

    private void SynchronizeFromViewModel()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _synchronizing = true;
        try
        {
            ScriptEditor.Text = viewModel.LuaScriptText;
            ScriptEditor.CaretOffset = Math.Clamp(
                ScriptEditor.CaretOffset,
                0,
                ScriptEditor.Document.TextLength);
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void OnScriptTextChanged(object? sender, EventArgs eventArgs)
    {
        if (_synchronizing || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _updatingViewModelFromEditor = true;
        try
        {
            viewModel.LuaScriptText = ScriptEditor.Text;
        }
        finally
        {
            _updatingViewModelFromEditor = false;
        }
    }

    private void OnCaretPositionChanged(object? sender, EventArgs eventArgs) =>
        UpdateCaret();

    private void UpdateCaret()
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateLuaCaret(
                ScriptEditor.TextArea.Caret.Line,
                ScriptEditor.TextArea.Caret.Column);
        }
    }

    private async void OnRunSelectionClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.RunLuaSelectionAsync(ScriptEditor.SelectedText);
        }
    }

    private void OnApiListMouseDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (ApiListBox.SelectedItem is not LuaApiEntry entry)
        {
            return;
        }

        int insertionOffset = Math.Clamp(
            ScriptEditor.CaretOffset,
            0,
            ScriptEditor.Document.TextLength);
        ScriptEditor.Document.Insert(insertionOffset, entry.InsertText);
        ScriptEditor.CaretOffset = Math.Clamp(
            insertionOffset + entry.InsertText.Length,
            0,
            ScriptEditor.Document.TextLength);
        ScriptEditor.Focus();
    }
}
