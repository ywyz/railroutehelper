namespace RailRouteHelper.Replay;

public sealed class ReplaySequenceException : Exception
{
    public ReplaySequenceException(
        long lineNumber,
        long expectedSequence,
        long actualSequence)
        : base(
            $"Replay line {lineNumber} has sequence {actualSequence}; "
            + $"expected {expectedSequence}.")
    {
        LineNumber = lineNumber;
        ExpectedSequence = expectedSequence;
        ActualSequence = actualSequence;
    }

    public long LineNumber { get; }

    public long ExpectedSequence { get; }

    public long ActualSequence { get; }
}
