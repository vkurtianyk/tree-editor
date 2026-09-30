using TreeEditor.Api.Apply;

namespace TreeEditor.Api.Tests.Apply;

public sealed class ApplyLocksTests
{
    private static readonly Guid Home = Guid.Parse("019b76da-a801-7926-9a3c-17b775163b37");
    private static readonly Guid Garden = Guid.Parse("019b76da-a803-7b84-87fc-49be8a32415c");

    [Fact]
    public void Root_keys_are_taken_once_each_in_ascending_order()
    {
        var keys = ApplyLocks.KeysFor([Home, Garden, Home], renamesRoot: false);

        Assert.Equal(new[] { ApplyLocks.KeyOf(Home), ApplyLocks.KeyOf(Garden) }.Order(), keys);
    }

    [Fact]
    public void The_roots_lock_comes_before_every_root_lock()
    {
        var keys = ApplyLocks.KeysFor([Garden, Home], renamesRoot: true);

        Assert.Equal(3, keys.Count);
        Assert.Equal(ApplyLocks.RootsKey, keys[0]);
        Assert.Equal(new[] { ApplyLocks.KeyOf(Home), ApplyLocks.KeyOf(Garden) }.Order(), keys.Skip(1));
    }

    [Fact]
    public void Keys_depend_on_the_whole_id()
    {
        // GUID v7s made within one millisecond share their first bytes; the key still tells them apart.
        var sameStart = Guid.Parse("019b76da-a801-7926-9a3c-000000000001");
        var sameEnd = Guid.Parse("019b76da-a802-7926-9a3c-17b775163b37");

        Assert.NotEqual(ApplyLocks.KeyOf(Home), ApplyLocks.KeyOf(sameStart));
        Assert.NotEqual(ApplyLocks.KeyOf(Home), ApplyLocks.KeyOf(sameEnd));
        Assert.Equal(ApplyLocks.KeyOf(Home), ApplyLocks.KeyOf(Guid.Parse(Home.ToString())));
    }

    [Fact]
    public void No_roots_means_no_locks()
    {
        Assert.Empty(ApplyLocks.KeysFor([], renamesRoot: false));
    }
}
