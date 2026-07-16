using SuavoAgent.Helper.Behavioral;
using Xunit;

namespace SuavoAgent.Helper.Tests.Behavioral;

public sealed class WindowRelativeElementIdentityTests
{
    [Fact]
    public void BuildAnonymousIdentity_RejectsDynamicClassName()
    {
        var identity = WindowRelativeElementIdentity.BuildAnonymousIdentity(
            "Edit",
            "patient_123456789",
            [0, 1],
            null);

        Assert.Null(identity);
    }

    [Fact]
    public void TwoAnonymousSameClassControls_GetDistinctRuntimeBoundIdentities()
    {
        var first = WindowRelativeElementIdentity.BuildAnonymousIdentity(
            "Edit",
            "WindowsForms10.EDIT.app.0.2bf8098_r8_ad1",
            windowRelativeSiblingPath: null,
            runtimeId: new[] { 42, 1001 });
        var second = WindowRelativeElementIdentity.BuildAnonymousIdentity(
            "Edit",
            "WindowsForms10.EDIT.app.0.2bf8098_r8_ad1",
            windowRelativeSiblingPath: null,
            runtimeId: new[] { 42, 1002 });

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain(":-1:-1", first, StringComparison.Ordinal);
        Assert.DoesNotContain(":-1:-1", second, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsForms10.EDIT", first, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsForms10.EDIT", second, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassOnlyWithoutPathOrRuntimeId_IsRejectedAsNonUnique()
    {
        var identity = WindowRelativeElementIdentity.BuildAnonymousIdentity(
            "Button",
            "WindowsForms10.BUTTON",
            windowRelativeSiblingPath: null,
            runtimeId: null);

        Assert.Null(identity);
    }

    [Fact]
    public void IdentityNeverContainsRawRuntimeId()
    {
        var identity = WindowRelativeElementIdentity.BuildAnonymousIdentity(
            "Button",
            "WindowsForms10.BUTTON",
            new[] { 3 },
            new[] { 987654321, 123456789 });

        Assert.NotNull(identity);
        Assert.DoesNotContain("987654321", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", identity, StringComparison.Ordinal);
    }
}
