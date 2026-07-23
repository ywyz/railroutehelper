namespace RailRouteHelper.SaveSchema;

public sealed class UnsupportedGameVersionException : Exception
{
    public UnsupportedGameVersionException(string gameVersion)
        : base($"No save schema mapper is registered for game version '{gameVersion}'.")
    {
        GameVersion = gameVersion;
    }

    public string GameVersion { get; }
}

public sealed class InvalidSaveSchemaException : Exception
{
    public InvalidSaveSchemaException(string path, string expectation)
        : base($"Save value at '{path}' must be {expectation}.")
    {
        Path = path;
        Expectation = expectation;
    }

    public string Path { get; }

    public string Expectation { get; }
}
