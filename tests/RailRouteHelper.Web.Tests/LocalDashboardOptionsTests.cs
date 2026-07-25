namespace RailRouteHelper.Web.Tests;

public sealed class LocalDashboardOptionsTests
{
    [Fact]
    public void No_arguments_selects_default_runtime_source()
    {
        var options = LocalDashboardOptions.Parse([]);

        Assert.Equal(5081, options.RuntimePort);
        Assert.Null(options.SaveDirectory);
        Assert.Equal(
            new Uri("http://127.0.0.1:5080"),
            options.ListenUri);
    }

    [Fact]
    public void Runtime_port_can_be_selected_explicitly()
    {
        var options = LocalDashboardOptions.Parse(
            ["--runtime-port", "6081"]);

        Assert.Equal(6081, options.RuntimePort);
        Assert.Null(options.SaveDirectory);
    }

    [Fact]
    public void Save_directory_remains_an_offline_compatibility_source()
    {
        var options = LocalDashboardOptions.Parse(
            [Path.GetTempPath()]);

        Assert.Null(options.RuntimePort);
        Assert.Equal(
            Path.GetFullPath(Path.GetTempPath()),
            options.SaveDirectory);
    }

    [Fact]
    public void Runtime_and_save_sources_cannot_be_combined()
    {
        Assert.Throws<ArgumentException>(
            () => LocalDashboardOptions.Parse(
                [Path.GetTempPath(), "--runtime-port", "6081"]));
    }
}
