using ApexLab.App.CommandLine;

namespace ApexLab.IntegrationTests.CommandLine;

[TestClass]
public sealed class StartupArgumentsTests
{
    private static readonly string DataRoot = Path.Combine(Path.GetTempPath(), "ApexLab", Guid.NewGuid().ToString("N"));
    private static readonly string ResultFile = Path.Combine(Path.GetTempPath(), "ApexLab", "result.json");

    [TestMethod]
    public void No_arguments_select_interactive_mode_that_can_show_the_window()
    {
        var result = StartupArguments.Parse([]);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(StartupMode.Interactive, result.Value!.Mode);
        Assert.IsTrue(result.Value.CanShowMainWindow);
        Assert.IsNull(result.Value.DataRoot);
        Assert.IsNull(result.Value.ResultFile);
    }

    [TestMethod]
    public void Exact_smoke_command_selects_noninteractive_mode_and_normalizes_paths()
    {
        var result = StartupArguments.Parse(["--smoke-test", "--data-root", DataRoot, "--result-file", ResultFile]);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual(StartupMode.SmokeTest, result.Value!.Mode);
        Assert.IsFalse(result.Value.CanShowMainWindow);
        Assert.AreEqual(Path.GetFullPath(DataRoot), result.Value.DataRoot);
        Assert.AreEqual(Path.GetFullPath(ResultFile), result.Value.ResultFile);
    }

    [TestMethod]
    [DataRow("--unknown")]
    [DataRow("positional")]
    [DataRow("--smoke-test", "--smoke-test", "--data-root", "C:\\Temp\\ApexLab", "--result-file", "C:\\Temp\\result.json")]
    [DataRow("--smoke-test")]
    [DataRow("--smoke-test", "--data-root", "C:\\Temp\\ApexLab")]
    [DataRow("--smoke-test", "--data-root", "C:\\Temp\\ApexLab", "--result-file")]
    [DataRow("--data-root", "C:\\Temp\\ApexLab", "--result-file", "C:\\Temp\\result.json")]
    [DataRow("--smoke-test", "--data-root", "relative", "--result-file", "C:\\Temp\\result.json")]
    [DataRow("--smoke-test", "--data-root", "C:\\Temp\\ApexLab", "--result-file", "relative.json")]
    [DataRow("--smoke-test", "--data-root", "   ", "--result-file", "C:\\Temp\\result.json")]
    [DataRow("--smoke-test", "--data-root", "C:\\Temp\\ApexLab", "--result-file", "C:\\Temp\\result.txt")]
    [DataRow("--smoke-test", "--data-root", "C:\\Temp\\ApexLab", "--result-file", "C:\\Temp\\bad*.json")]
    [DataRow("--smoke-test", "--result-file", "C:\\Temp\\result.json", "--data-root", "C:\\Temp\\ApexLab")]
    [DataRow("--smoke-test", "--data-root", "C:\\", "--result-file", "C:\\Temp\\result.json")]
    public void Invalid_forms_are_rejected_without_a_value(params string[] arguments)
    {
        var result = StartupArguments.Parse(arguments);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Value);
        Assert.IsNotNull(result.Error);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Error.Message));
    }

    [TestMethod]
    public void Duplicate_value_options_have_a_specific_deterministic_error()
    {
        var result = StartupArguments.Parse([
            "--smoke-test", "--data-root", DataRoot,
            "--data-root", DataRoot, "--result-file", ResultFile]);

        Assert.AreEqual(StartupArgumentErrorCode.DuplicateOption, result.Error!.Code);
        Assert.AreEqual("Option '--data-root' may be specified only once.", result.Error.Message);
    }

    [TestMethod]
    public void Invalid_path_characters_return_a_stable_error_instead_of_exception_details()
    {
        var result = StartupArguments.Parse([
            "--smoke-test", "--data-root", DataRoot,
            "--result-file", "C:\\Temp\\bad\0.json"]);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual("Result file contains an unsafe or non-canonical Windows file name.", result.Error.Message);
    }

    [TestMethod]
    [DataRow("CON.json")]
    [DataRow("nul.JSON")]
    [DataRow("COM1.json")]
    [DataRow("LPT9.json")]
    [DataRow("result.json.")]
    [DataRow("result.json ")]
    public void Reserved_or_noncanonical_windows_result_names_are_rejected(string fileName)
    {
        var result = StartupArguments.Parse([
            "--smoke-test", "--data-root", DataRoot,
            "--result-file", Path.Combine(Path.GetTempPath(), "ApexLab", fileName)]);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNotNull(result.Error);
    }
}
