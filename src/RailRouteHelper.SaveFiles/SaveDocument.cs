namespace RailRouteHelper.SaveFiles;

public sealed record SaveDocument(
    string SourcePath,
    long SourceLength,
    DateTimeOffset LastWriteTimeUtc,
    SaveValue Root);
