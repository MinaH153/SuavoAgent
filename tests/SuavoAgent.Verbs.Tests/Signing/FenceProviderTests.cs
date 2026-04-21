using SuavoAgent.Verbs.Signing;
using Xunit;

namespace SuavoAgent.Verbs.Tests.Signing;

public class FenceProviderTests
{
    [Fact]
    public void CurrentFenceId_ReturnsInitialValue()
    {
        var id = Guid.NewGuid();
        var p = new FenceProvider(id);
        Assert.Equal(id, p.CurrentFenceId);
    }

    [Fact]
    public void Rotate_UpdatesCurrentId()
    {
        var p = new FenceProvider(Guid.NewGuid());
        var newId = Guid.NewGuid();
        p.Rotate(newId);
        Assert.Equal(newId, p.CurrentFenceId);
    }

    [Fact]
    public void Rotate_IsThreadSafe()
    {
        var p = new FenceProvider(Guid.NewGuid());
        var ids = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();

        Parallel.For(0, 100, i => p.Rotate(ids[i]));

        // After 100 parallel rotations, the current ID must be one of the
        // rotated IDs (no torn values).
        Assert.Contains(p.CurrentFenceId, ids);
    }
}
