using System.ComponentModel;
using ApexLab.App.Shell;

namespace ApexLab.IntegrationTests.Shell;

[TestClass]
public sealed class ShellViewModelTests
{
    [TestMethod]
    public void Primary_areas_are_the_four_workflow_steps_in_order()
    {
        var subject = new ShellViewModel();

        CollectionAssert.AreEqual(
            new[] { "Drive", "Review", "Coach", "Data & Settings" },
            subject.PrimaryAreas.Select(area => area.Title).ToArray());
        Assert.HasCount(4, subject.PrimaryAreas);
    }

    [TestMethod]
    public void Primary_areas_have_distinct_explicit_access_keys()
    {
        var subject = new ShellViewModel();

        CollectionAssert.AreEqual(
            new[] { 'D', 'R', 'C', 'S' },
            subject.PrimaryAreas.Select(area => area.AccessKey).ToArray());
        Assert.AreEqual(4, subject.PrimaryAreas.Select(area => area.AccessKey).Distinct().Count());
    }

    [TestMethod]
    public void Drive_is_selected_initially()
    {
        var subject = new ShellViewModel();

        Assert.AreEqual("Drive", subject.SelectedArea.Title);
        Assert.AreEqual("Drive", subject.CurrentRoute);
        Assert.IsTrue(subject.IsDriveSelected);
        Assert.IsFalse(subject.IsReviewSelected);
        Assert.IsFalse(subject.IsCoachSelected);
        Assert.IsFalse(subject.IsDataSettingsSelected);
        Assert.IsFalse(subject.AreReviewToolsVisible);
        Assert.IsFalse(subject.AreDataToolsVisible);
    }

    [TestMethod]
    public void Selecting_a_new_area_notifies_once_and_reselecting_is_idempotent()
    {
        var subject = new ShellViewModel();
        var selectedAreaNotifications = 0;
        subject.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShellViewModel.SelectedArea))
            {
                selectedAreaNotifications++;
            }
        };
        var review = subject.PrimaryAreas.Single(area => area.Title == "Review");

        subject.SelectAreaCommand.Execute(review);
        subject.SelectAreaCommand.Execute(review);

        Assert.AreSame(review, subject.SelectedArea);
        Assert.AreEqual(1, selectedAreaNotifications);
    }

    [TestMethod]
    public void Selection_state_notifies_only_the_properties_that_actually_change()
    {
        var subject = new ShellViewModel();
        var propertyNames = new List<string?>();
        subject.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);
        var review = subject.PrimaryAreas.Single(area => area.Title == "Review");

        subject.SelectAreaCommand.Execute(review);
        subject.SelectAreaCommand.Execute(review);

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(ShellViewModel.SelectedArea),
                nameof(ShellViewModel.IsDriveSelected),
                nameof(ShellViewModel.IsReviewSelected),
                nameof(ShellViewModel.AreReviewToolsVisible),
                nameof(ShellViewModel.CurrentRoute),
            },
            propertyNames);
    }

    [TestMethod]
    public void Corner_editor_routes_within_review_instead_of_becoming_primary_navigation()
    {
        var subject = new ShellViewModel();

        subject.OpenCornerEditorCommand.Execute(null);

        Assert.AreEqual("Review", subject.SelectedArea.Title);
        Assert.AreEqual("Review / Corner Editor", subject.CurrentRoute);
        Assert.IsTrue(subject.IsReviewSelected);
        Assert.IsTrue(subject.AreReviewToolsVisible);
        Assert.IsFalse(subject.AreDataToolsVisible);
        Assert.IsFalse(subject.PrimaryAreas.Any(area => area.Title.Contains("Corner", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Replay_and_diagnostics_routes_within_data_instead_of_becoming_primary_navigation()
    {
        var subject = new ShellViewModel();

        subject.OpenReplayDiagnosticsCommand.Execute(null);

        Assert.AreEqual("Data & Settings", subject.SelectedArea.Title);
        Assert.AreEqual("Data & Settings / Replay & Diagnostics", subject.CurrentRoute);
        Assert.IsTrue(subject.IsDataSettingsSelected);
        Assert.IsFalse(subject.AreReviewToolsVisible);
        Assert.IsTrue(subject.AreDataToolsVisible);
        Assert.IsFalse(subject.PrimaryAreas.Any(area => area.Title.Contains("Replay", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Construction_is_pure_and_requires_no_infrastructure()
    {
        var constructor = typeof(ShellViewModel).GetConstructors().Single();

        Assert.HasCount(0, constructor.GetParameters());
        Assert.IsFalse(typeof(IDisposable).IsAssignableFrom(typeof(ShellViewModel)));
        Assert.IsTrue(typeof(INotifyPropertyChanged).IsAssignableFrom(typeof(ShellViewModel)));
    }
}
