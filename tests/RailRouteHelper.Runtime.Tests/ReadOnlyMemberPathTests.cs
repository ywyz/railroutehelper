using RailRouteHelper.Runtime;

namespace RailRouteHelper.Runtime.Tests;

public sealed class ReadOnlyMemberPathTests
{
    [Fact]
    public void Reads_configured_field_and_property_path_without_writing()
    {
        var root = new FakeRoot();

        var id = ReadOnlyMemberPath.ReadRequired<string>(
            root,
            "_repository.Current.Id");
        var allocation = ReadOnlyMemberPath.ReadRequired<int>(
            root,
            "_repository.Current.Allocation");

        Assert.Equal("node-7", id);
        Assert.Equal(1, allocation);
        Assert.Equal(0, root.WriteCount);
    }

    [Fact]
    public void Null_intermediate_value_returns_null()
    {
        var root = new FakeRoot { Repository = null };

        Assert.Null(ReadOnlyMemberPath.Read(root, "Repository.Current.Id"));
    }

    [Fact]
    public void Methods_and_unknown_members_are_rejected()
    {
        var root = new FakeRoot();

        Assert.Throws<MissingMemberException>(
            () => ReadOnlyMemberPath.Read(root, "Mutate"));
        Assert.Equal(0, root.WriteCount);
    }

    private sealed class FakeRoot
    {
        private readonly FakeRepository _repository = new();

        public FakeRepository? Repository { get; init; } = new();

        public int WriteCount { get; private set; }

        public void Mutate()
        {
            WriteCount++;
        }
    }

    private sealed class FakeRepository
    {
        public FakeNode Current { get; } = new();
    }

    private sealed class FakeNode
    {
        public string Id { get; } = "node-7";

        public byte Allocation = 1;
    }
}
