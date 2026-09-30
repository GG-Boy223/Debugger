using System.Windows;
using System.Windows.Controls;
using DogeDebugger.Core.Signatures.CrossVersion;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class SignatureSearchDetailWindow : Window
{
    private readonly CrossVersionResult _result;

    public SignatureSearchDetailWindow(CrossVersionResult result)
    {
        _result = result;
        InitializeComponent();
        TargetNameText.Text = result.Name;
        TargetDescriptionText.Text = result.Explanation;
        SourceOffsetText.Text = result.SourceOffsetText;
        TargetOffsetText.Text = result.ResolvedOffsetText;
        ConfidenceText.Text = result.ConfidenceLevelText;
        EvidenceGrid.ItemsSource = result.Evidence;
        CandidateCombo.ItemsSource = result.Candidates;
        CandidateCombo.SelectedIndex = result.Candidates.Count > 0 ? 0 : -1;
        UpdateDetails(null);
        ConfirmCandidateButton.IsEnabled =
            result.State == CrossVersionResolutionState.Ambiguous &&
            result.Candidates.Count > 0;
    }

    public CrossVersionCandidate? ConfirmedCandidate { get; private set; }

    private void OnEvidenceSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        UpdateDetails(EvidenceGrid.SelectedItem as CrossVersionEvidence);
    }

    private void OnCandidateSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        ConfirmCandidateButton.IsEnabled =
            _result.State == CrossVersionResolutionState.Ambiguous &&
            CandidateCombo.SelectedItem is CrossVersionCandidate;
        UpdateDetails(EvidenceGrid.SelectedItem as CrossVersionEvidence);
    }

    private void UpdateDetails(CrossVersionEvidence? evidence)
    {
        string evidenceText = evidence?.Description ?? string.Empty;
        CrossVersionCandidate? candidate =
            CandidateCombo.SelectedItem as CrossVersionCandidate;
        string candidateText = candidate?.Explanation ?? string.Empty;
        string[] parts = new[] { evidenceText, candidateText, _result.Explanation }
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        DetailsText.Text = parts.Length == 0
            ? _result.Explanation
            : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private void OnConfirmCandidateClick(object sender, RoutedEventArgs eventArgs)
    {
        if (CandidateCombo.SelectedItem is CrossVersionCandidate candidate)
        {
            ConfirmedCandidate = candidate;
            DialogResult = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }
}
