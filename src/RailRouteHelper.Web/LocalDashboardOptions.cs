using System.Net;

namespace RailRouteHelper.Web;

public sealed record LocalDashboardOptions
{
    public LocalDashboardOptions(
        Uri listenUri,
        string? saveDirectory,
        int? runtimePort = null)
    {
        ArgumentNullException.ThrowIfNull(listenUri);
        ValidateListenUri(listenUri);
        if (runtimePort is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runtimePort),
                "Runtime TCP port must be between 0 and 65535.");
        }

        if (!string.IsNullOrWhiteSpace(saveDirectory)
            && runtimePort is not null)
        {
            throw new ArgumentException(
                "Save-directory and runtime sources are mutually exclusive.");
        }

        ListenUri = listenUri;
        SaveDirectory = string.IsNullOrWhiteSpace(saveDirectory)
            ? null
            : Path.GetFullPath(saveDirectory);
        RuntimePort = runtimePort;
    }

    public Uri ListenUri { get; }

    public string? SaveDirectory { get; }

    public int? RuntimePort { get; }

    public static LocalDashboardOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? saveDirectory = null;
        var runtimePort = 5081;
        var runtimePortWasSpecified = false;
        var listenUri = new Uri("http://127.0.0.1:5080");
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--listen", StringComparison.Ordinal))
            {
                if (++index >= args.Count
                    || !Uri.TryCreate(
                        args[index],
                        UriKind.Absolute,
                        out listenUri))
                {
                    throw new ArgumentException(
                        "--listen requires an absolute loopback HTTP URL.");
                }

                continue;
            }

            if (string.Equals(
                    argument,
                    "--runtime-port",
                    StringComparison.Ordinal))
            {
                runtimePortWasSpecified = true;
                if (++index >= args.Count
                    || !int.TryParse(args[index], out runtimePort)
                    || runtimePort is < 1 or > ushort.MaxValue)
                {
                    throw new ArgumentException(
                        "--runtime-port requires a TCP port from 1 to 65535.");
                }

                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Unknown dashboard option: {argument}");
            }

            if (saveDirectory is not null)
            {
                throw new ArgumentException(
                    "The dashboard accepts exactly one save directory.");
            }

            saveDirectory = argument;
        }

        if (saveDirectory is not null && runtimePortWasSpecified)
        {
            throw new ArgumentException(
                "A save directory cannot be combined with --runtime-port.");
        }

        var options = new LocalDashboardOptions(
            listenUri,
            saveDirectory,
            saveDirectory is null ? runtimePort : null);
        if (options.SaveDirectory is not null
            && !Directory.Exists(options.SaveDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The save directory does not exist: {options.SaveDirectory}");
        }

        return options;
    }

    private static void ValidateListenUri(Uri listenUri)
    {
        var loopbackHost = string.Equals(
            listenUri.Host,
            "localhost",
            StringComparison.OrdinalIgnoreCase);
        if (!loopbackHost
            && (!IPAddress.TryParse(listenUri.Host, out var address)
                || !IPAddress.IsLoopback(address)))
        {
            throw new ArgumentException(
                "The dashboard may listen only on localhost or a loopback IP.",
                nameof(listenUri));
        }

        if (!string.Equals(
                listenUri.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(listenUri.UserInfo)
            || !string.IsNullOrEmpty(listenUri.Query)
            || !string.IsNullOrEmpty(listenUri.Fragment)
            || !string.Equals(
                listenUri.AbsolutePath,
                "/",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The dashboard listen URL must be a plain loopback HTTP origin.",
                nameof(listenUri));
        }
    }
}
