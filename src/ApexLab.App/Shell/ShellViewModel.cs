using System.Collections.ObjectModel;
using System.Windows.Input;
using ApexLab.App.Presentation;

namespace ApexLab.App.Shell;

public sealed class ShellViewModel : ObservableObject
{
    private static readonly WorkflowArea DriveArea = new(
        "drive",
        "Drive",
        'D',
        "Connection readiness and the next pre-stint instruction");

    private static readonly WorkflowArea ReviewArea = new(
        "review",
        "Review",
        'R',
        "Inspect completed laps and the evidence behind a finding");

    private static readonly WorkflowArea CoachArea = new(
        "coach",
        "Coach",
        'C',
        "Plan and evaluate one measurable driving experiment");

    private static readonly WorkflowArea DataArea = new(
        "data-settings",
        "Data & Settings",
        'S',
        "Manage local data, diagnostics, and capture policy");

    private static readonly ReadOnlyCollection<WorkflowArea> Areas = Array.AsReadOnly(
        [DriveArea, ReviewArea, CoachArea, DataArea]);

    private WorkflowArea _selectedArea = DriveArea;
    private string _currentRoute = DriveArea.Title;
    private bool _isDriveSelected = true;
    private bool _isReviewSelected;
    private bool _isCoachSelected;
    private bool _isDataSettingsSelected;
    private bool _areReviewToolsVisible;
    private bool _areDataToolsVisible;

    public ShellViewModel()
    {
        SelectAreaCommand = new RelayCommand(SelectArea);
        OpenCornerEditorCommand = new RelayCommand(OpenCornerEditor);
        OpenReplayDiagnosticsCommand = new RelayCommand(OpenReplayDiagnostics);
    }

    public IReadOnlyList<WorkflowArea> PrimaryAreas => Areas;

    public WorkflowArea SelectedArea
    {
        get => _selectedArea;
        private set => SetProperty(ref _selectedArea, value);
    }

    public string CurrentRoute
    {
        get => _currentRoute;
        private set => SetProperty(ref _currentRoute, value);
    }

    public bool IsDriveSelected
    {
        get => _isDriveSelected;
        private set => SetProperty(ref _isDriveSelected, value);
    }

    public bool IsReviewSelected
    {
        get => _isReviewSelected;
        private set => SetProperty(ref _isReviewSelected, value);
    }

    public bool IsCoachSelected
    {
        get => _isCoachSelected;
        private set => SetProperty(ref _isCoachSelected, value);
    }

    public bool IsDataSettingsSelected
    {
        get => _isDataSettingsSelected;
        private set => SetProperty(ref _isDataSettingsSelected, value);
    }

    public bool AreReviewToolsVisible
    {
        get => _areReviewToolsVisible;
        private set => SetProperty(ref _areReviewToolsVisible, value);
    }

    public bool AreDataToolsVisible
    {
        get => _areDataToolsVisible;
        private set => SetProperty(ref _areDataToolsVisible, value);
    }

    public ICommand SelectAreaCommand { get; }

    public ICommand OpenCornerEditorCommand { get; }

    public ICommand OpenReplayDiagnosticsCommand { get; }

    private void SelectArea(object? parameter)
    {
        if (parameter is not WorkflowArea requestedArea
            || !Areas.Contains(requestedArea))
        {
            return;
        }

        ApplySelection(requestedArea);
        CurrentRoute = requestedArea.Title;
    }

    private void OpenCornerEditor()
    {
        ApplySelection(ReviewArea);
        CurrentRoute = "Review / Corner Editor";
    }

    private void OpenReplayDiagnostics()
    {
        ApplySelection(DataArea);
        CurrentRoute = "Data & Settings / Replay & Diagnostics";
    }

    private void ApplySelection(WorkflowArea area)
    {
        SelectedArea = area;
        IsDriveSelected = area == DriveArea;
        IsReviewSelected = area == ReviewArea;
        IsCoachSelected = area == CoachArea;
        IsDataSettingsSelected = area == DataArea;
        AreReviewToolsVisible = area == ReviewArea;
        AreDataToolsVisible = area == DataArea;
    }
}
