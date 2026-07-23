namespace RailRouteHelper.SaveFiles;

public interface ISaveFileAdapter
{
    ValueTask<SaveDocument> ReadAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}

