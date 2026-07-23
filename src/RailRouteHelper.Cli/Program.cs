using System.Text;
using RailRouteHelper.Cli;
using RailRouteHelper.SaveSchema;

Console.OutputEncoding = new UTF8Encoding(
    encoderShouldEmitUTF8Identifier: false);

try
{
    return await CliApplication.RunAsync(
        args,
        Console.Out,
        Console.Error);
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
