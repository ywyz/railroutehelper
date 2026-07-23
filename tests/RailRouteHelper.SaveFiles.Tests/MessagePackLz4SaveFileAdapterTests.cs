using MessagePack;
using MessagePack.Resolvers;
using RailRouteHelper.SaveFiles;

namespace RailRouteHelper.SaveFiles.Tests;

public sealed class MessagePackLz4SaveFileAdapterTests
{
    [Fact]
    public async Task ReadAsync_decodes_an_lz4_block_array_without_mutating_the_file()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "railroutehelper-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.mp.lz4");

        try
        {
            var fixture = new Dictionary<string, object?>
            {
                ["saveVersion"] = 7,
                ["mapName"] = "synthetic-map",
                ["trains"] = new object[]
                {
                    new Dictionary<string, object?> { ["id"] = "T-001" },
                },
                ["manualRoutes"] = new Dictionary<object, object?>
                {
                    [new Dictionary<string, object?> { ["segment"] = "A" }] = "open",
                },
            };
            var options = ContractlessStandardResolver.Options.WithCompression(
                MessagePackCompression.Lz4BlockArray);
            var encoded = MessagePackSerializer.Serialize(
                fixture,
                options,
                cancellationToken);
            await File.WriteAllBytesAsync(path, encoded, cancellationToken);
            var before = await File.ReadAllBytesAsync(path, cancellationToken);

            var adapter = new MessagePackLz4SaveFileAdapter();
            var document = await adapter.ReadAsync(path, cancellationToken);
            var root = Assert.IsType<SaveMap>(document.Root);

            Assert.Equal(
                7,
                Assert.IsType<SaveSignedInteger>(root["saveVersion"]).Value);
            Assert.Equal(
                "synthetic-map",
                Assert.IsType<SaveString>(root["mapName"]).Value);
            var trains = Assert.IsType<SaveArray>(root["trains"]);
            var firstTrain = Assert.IsType<SaveMap>(trains.Items[0]);
            Assert.Equal(
                "T-001",
                Assert.IsType<SaveString>(firstTrain["id"]).Value);
            var manualRoutes = Assert.IsType<SaveMap>(root["manualRoutes"]);
            Assert.IsType<SaveMap>(Assert.Single(manualRoutes.Entries).Key);
            Assert.Equal(
                "open",
                Assert.IsType<SaveString>(manualRoutes.Entries[0].Value).Value);
            Assert.Equal(
                before,
                await File.ReadAllBytesAsync(path, cancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
