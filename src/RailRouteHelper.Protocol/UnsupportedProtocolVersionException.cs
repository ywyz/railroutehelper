namespace RailRouteHelper.Protocol;

public sealed class UnsupportedProtocolVersionException : Exception
{
    public UnsupportedProtocolVersionException(int actualVersion, int supportedVersion)
        : base(
            $"Protocol version {actualVersion} is unsupported; "
            + $"this build supports version {supportedVersion}.")
    {
        ActualVersion = actualVersion;
        SupportedVersion = supportedVersion;
    }

    public int ActualVersion { get; }

    public int SupportedVersion { get; }
}

