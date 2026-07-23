namespace RailRouteHelper.Replay;

public sealed class ReplayLineException : Exception
{
    public ReplayLineException(long lineNumber, Exception innerException)
        : base($"Replay line {lineNumber} is invalid.", innerException)
    {
        LineNumber = lineNumber;
    }

    public long LineNumber { get; }
}

