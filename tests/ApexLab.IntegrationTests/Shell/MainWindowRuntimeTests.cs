using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ApexLab.App;
using ApexLab.App.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace ApexLab.IntegrationTests.Shell;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowRuntimeTests
{
    [TestMethod]
    public void Window_runtime_navigation_bindings_focus_and_contextual_tools_work()
    {
        Exception? threadFailure = null;
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                ExerciseWindow();
            }
            catch (Exception exception)
            {
                threadFailure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "ApexLab.MainWindowRuntimeTests.STA",
        };
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
        {
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.Fail("The STA WPF runtime test did not complete within 20 seconds.");
        }

        if (threadFailure is not null)
        {
            ExceptionDispatchInfo.Capture(threadFailure).Throw();
        }
    }

    private static void ExerciseWindow()
    {
        var bindingErrors = new BindingErrorTraceListener();
        var bindingSource = PresentationTraceSources.DataBindingSource;
        var previousLevel = bindingSource.Switch.Level;
        bindingSource.Listeners.Add(bindingErrors);
        bindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;

        MainWindow? window = null;
        try
        {
            using var host = AppComposition.CreateHost(
                Path.Combine(
                    Path.GetTempPath(),
                    $"apexlab-window-{Guid.NewGuid():N}"));
            window = host.Services.GetRequiredService<MainWindow>();
            var viewModel = (ShellViewModel)window.DataContext;
            window.Width = 1100;
            window.Height = 700;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10_000;
            window.Top = -10_000;
            window.ShowInTaskbar = false;

            window.Show();
            window.UpdateLayout();
            DrainDispatcher();

            Assert.AreEqual(1100, window.Width);
            Assert.AreEqual(700, window.Height);

            var primaryControls = FindVisualDescendants<RadioButton>(window)
                .Where(control => Equals(control.Tag, "PrimaryNavigation"))
                .ToArray();
            Assert.HasCount(4, primaryControls);

            var drive = GetRequiredElement<RadioButton>(window, "DriveNavigation");
            var review = GetRequiredElement<RadioButton>(window, "ReviewNavigation");
            var reviewTools = GetRequiredElement<FrameworkElement>(window, "ReviewToolsPanel");
            var dataTools = GetRequiredElement<FrameworkElement>(window, "DataToolsPanel");

            Assert.IsTrue(drive.IsChecked);
            Assert.IsFalse(review.IsChecked);
            Assert.AreEqual("Selected", AutomationProperties.GetItemStatus(drive));
            Assert.IsNotNull(drive.FocusVisualStyle);
            Assert.IsTrue(drive.Focus());
            Keyboard.Focus(drive);
            DrainDispatcher();
            Assert.AreSame(drive, Keyboard.FocusedElement);
            Assert.AreEqual(Visibility.Collapsed, reviewTools.Visibility);
            Assert.AreEqual(Visibility.Collapsed, dataTools.Visibility);

            Assert.IsNotNull(review.Command);
            Assert.IsTrue(review.Command.CanExecute(review.CommandParameter));
            review.Command.Execute(review.CommandParameter);
            DrainDispatcher();

            Assert.IsFalse(drive.IsChecked);
            Assert.IsTrue(review.IsChecked);
            Assert.AreEqual("Selected", AutomationProperties.GetItemStatus(review));
            Assert.AreEqual("Review", viewModel.SelectedArea.Title);
            Assert.AreEqual(Visibility.Visible, reviewTools.Visibility);
            Assert.AreEqual(Visibility.Collapsed, dataTools.Visibility);

            AssertBindingActive(drive, ToggleButton.IsCheckedProperty);
            AssertBindingActive(review, ToggleButton.CommandProperty);
            AssertBindingActive(reviewTools, UIElement.VisibilityProperty);
            Assert.AreEqual(string.Empty, bindingErrors.Messages);
        }
        finally
        {
            window?.Close();
            DrainDispatcher();
            bindingSource.Listeners.Remove(bindingErrors);
            bindingSource.Switch.Level = previousLevel;
        }
    }

    private static T GetRequiredElement<T>(FrameworkElement root, string name)
        where T : FrameworkElement
    {
        return root.FindName(name) as T
            ?? throw new AssertFailedException($"Element '{name}' was not found as {typeof(T).Name}.");
    }

    private static void AssertBindingActive(DependencyObject target, DependencyProperty property)
    {
        var expression = BindingOperations.GetBindingExpressionBase(target, property);

        Assert.IsNotNull(expression, $"Expected a binding on {property.OwnerType.Name}.{property.Name}.");
        Assert.AreEqual(BindingStatus.Active, expression.Status);
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void DrainDispatcher()
    {
        Dispatcher.CurrentDispatcher.Invoke(
            static () => { },
            DispatcherPriority.ApplicationIdle);
    }

    private sealed class BindingErrorTraceListener : TraceListener
    {
        private readonly StringBuilder _messages = new();

        public string Messages => _messages.ToString();

        public override void Write(string? message) => _messages.Append(message);

        public override void WriteLine(string? message) => _messages.AppendLine(message);
    }
}
