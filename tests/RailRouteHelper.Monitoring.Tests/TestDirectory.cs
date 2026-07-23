namespace RailRouteHelper.Monitoring.Tests;

internal static class TestDirectory
{
    public static string Create()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "railroutehelper-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
