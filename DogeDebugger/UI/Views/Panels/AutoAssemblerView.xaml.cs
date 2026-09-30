using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using DogeDebugger.UI.Views.Dialogs;

namespace DogeDebugger.UI.Views.Panels;

public partial class AutoAssemblerView : UserControl
{
    private bool _synchronizing;
    private MainViewModel? _viewModel;

    public AutoAssemblerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        ScriptEditor.TextChanged += OnScriptEditorTextChanged;
        ScriptEditor.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnScriptEditorScrollChanged));
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs) =>
        SynchronizeFromViewModel();

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        SynchronizeFromViewModel();
    }

    private void SynchronizeFromViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        SetEditorText(_viewModel.AutoAssemblerText);
        OutputLog.Text = _viewModel.AutoAssemblerOutputText;
    }

    private void OnScriptEditorTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        UpdateLineNumbers();
        if (_synchronizing || _viewModel is null)
        {
            return;
        }

        string text = ReadEditorText();
        if (!string.Equals(_viewModel.AutoAssemblerText, text, StringComparison.Ordinal))
        {
            _viewModel.AutoAssemblerText = text;
        }
    }

    private void OnScriptEditorScrollChanged(object sender, ScrollChangedEventArgs eventArgs)
    {
        Canvas.SetTop(LineNumberBlock, -eventArgs.VerticalOffset);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (eventArgs.PropertyName)
        {
            case nameof(MainViewModel.AutoAssemblerText):
                if (!string.Equals(
                        ReadEditorText(),
                        _viewModel.AutoAssemblerText,
                        StringComparison.Ordinal))
                {
                    SetEditorText(_viewModel.AutoAssemblerText, preserveCaret: true);
                }

                break;
            case nameof(MainViewModel.AutoAssemblerOutputText):
                OutputLog.Text = _viewModel.AutoAssemblerOutputText;
                OutputLog.ScrollToEnd();
                break;
        }
    }

    private void OnInsertBasicTemplateClick(object sender, RoutedEventArgs eventArgs) =>
        _viewModel?.InsertAutoAssemblerBasicTemplate();

    private void OnInsertJump5TemplateClick(object sender, RoutedEventArgs eventArgs) =>
        InsertInjectionTemplate(5);

    private void OnInsertJump14TemplateClick(object sender, RoutedEventArgs eventArgs) =>
        InsertInjectionTemplate(14);

    private void OnInsertAobTemplateClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel is null)
        {
            return;
        }

        string? symbol = ShowAddressDialog(
            "AOB注入模版",
            "你想注入哪个地址?",
            "INJECT");
        if (symbol is not null)
        {
            _viewModel.InsertAutoAssemblerAobTemplate(symbol);
        }
    }

    private void InsertInjectionTemplate(int jumpLength)
    {
        if (_viewModel is null)
        {
            return;
        }

        string? addressText = ShowAddressDialog(
            "代码注入模版",
            "你想Hook哪个地址?",
            _viewModel.GetAutoAssemblerSuggestedAddress());
        if (addressText is not null)
        {
            _viewModel.InsertAutoAssemblerInjectionTemplate(addressText, jumpLength);
        }
    }

    private string? ShowAddressDialog(string title, string prompt, string initialValue)
    {
        AutoAssemblerAddressDialog dialog = new(title, prompt, initialValue)
        {
            Owner = Window.GetWindow(this)
        };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void SetEditorText(string text, bool preserveCaret = false)
    {
        int caretOffset = preserveCaret ? GetCaretOffset() : 0;
        _synchronizing = true;
        try
        {
            FlowDocument document = new();
            string normalized = (text ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            foreach (string line in normalized.Split('\n'))
            {
                document.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0) });
            }

            if (document.Blocks.Count == 0)
            {
                document.Blocks.Add(new Paragraph());
            }

            ScriptEditor.Document = document;
        }
        finally
        {
            _synchronizing = false;
        }

        UpdateLineNumbers();
        if (preserveCaret)
        {
            SetCaretOffset(caretOffset);
        }
    }

    private string ReadEditorText()
    {
        StringBuilder builder = new();
        foreach (Block block in ScriptEditor.Document.Blocks)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(new TextRange(block.ContentStart, block.ContentEnd).Text.TrimEnd('\r', '\n'));
        }

        return builder.ToString();
    }

    private void UpdateLineNumbers()
    {
        int lineCount = Math.Max(1, ScriptEditor.Document.Blocks.Count);
        if (lineCount == 1 &&
            ScriptEditor.Document.Blocks.FirstBlock is Paragraph { Inlines.Count: 0 })
        {
            lineCount = 1;
        }

        StringBuilder builder = new();
        for (int line = 1; line <= lineCount; line++)
        {
            if (line > 1)
            {
                builder.Append('\n');
            }

            builder.Append(line);
        }

        LineNumberBlock.Text = builder.ToString();
    }

    private int GetCaretOffset()
    {
        TextPointer start = ScriptEditor.Document.ContentStart;
        TextPointer caret = ScriptEditor.CaretPosition;
        return start.GetOffsetToPosition(caret);
    }

    private void SetCaretOffset(int offset)
    {
        TextPointer position = ScriptEditor.Document.ContentStart.GetPositionAtOffset(offset);
        if (position is not null)
        {
            ScriptEditor.CaretPosition = position;
        }
    }
}
