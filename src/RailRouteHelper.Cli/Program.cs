using System.Text;
using RailRouteHelper.Cli;
using RailRouteHelper.SaveSchema;

Console.OutputEncoding = new UTF8Encoding(
    encoderShouldEmitUTF8Identifier: false);

using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArguments) =>
{
    eventArguments.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    try
    {
        return await CliApplication.RunAsync(
            args,
            Console.Out,
            Console.Error,
            shutdown.Token);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
        return 0;
    }
}
catch (Exception error) when (
    error is IOException
        or UnauthorizedAccessException
        or UnsupportedGameVersionException
        or InvalidSaveSchemaException)
{
    Console.Error.WriteLine($"error: {error.Message}");
    return 2;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
