using ApexLab.App.Presentation;

namespace ApexLab.IntegrationTests.App;

[TestClass]
public sealed class MvvmPrimitivesTests
{
    [TestMethod]
    public void Changed_property_raises_notification_with_property_name()
    {
        var subject = new TestObservable();
        var propertyNames = new List<string?>();
        subject.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        subject.Name = "updated";

        Assert.HasCount(1, propertyNames);
        Assert.AreEqual(nameof(TestObservable.Name), propertyNames[0]);
    }

    [TestMethod]
    public void Equal_property_value_does_not_raise_notification()
    {
        var subject = new TestObservable();
        var propertyNames = new List<string?>();
        subject.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        subject.Name = "initial";

        Assert.IsEmpty(propertyNames);
    }

    [TestMethod]
    public void Relay_command_executes_action()
    {
        var executed = false;
        var command = new RelayCommand(() => executed = true);

        command.Execute(parameter: null);

        Assert.IsTrue(executed);
    }

    [TestMethod]
    public void Relay_command_uses_can_execute_predicate()
    {
        var enabled = false;
        var command = new RelayCommand(() => { }, () => enabled);

        Assert.IsFalse(command.CanExecute(parameter: null));

        enabled = true;

        Assert.IsTrue(command.CanExecute(parameter: null));
    }

    [TestMethod]
    public void Relay_command_raises_can_execute_changed()
    {
        var command = new RelayCommand(() => { });
        object? eventSender = null;
        EventArgs? eventArgs = null;
        var raisedCount = 0;
        command.CanExecuteChanged += (sender, args) =>
        {
            eventSender = sender;
            eventArgs = args;
            raisedCount++;
        };

        command.RaiseCanExecuteChanged();

        Assert.AreEqual(1, raisedCount);
        Assert.AreSame(command, eventSender);
        Assert.AreSame(EventArgs.Empty, eventArgs);
    }

    [TestMethod]
    public void Relay_command_forwards_its_parameter()
    {
        object? received = null;
        var command = new RelayCommand(parameter => received = parameter);
        var expected = new object();

        command.Execute(expected);

        Assert.AreSame(expected, received);
    }

    private sealed class TestObservable : ObservableObject
    {
        private string _name = "initial";

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
    }
}
