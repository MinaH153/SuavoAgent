using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresencePreferenceStoreTests
{
    [Fact]
    public void SetVisible_TogglesCursorVisible_AndRaisesChanged()
    {
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault());
        PresencePreferences? last = null;
        store.Changed += p => last = p;

        store.SetVisible(false);

        Assert.False(store.Current.CursorVisible);
        Assert.NotNull(last);
        Assert.False(last!.CursorVisible);
    }

    [Fact]
    public void Replace_SwapsAllAndRaisesChanged()
    {
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault());
        var raised = 0;
        store.Changed += _ => raised++;

        store.Replace(PresencePreferences.SafeDefault() with { GlowIntensity = 0.2 });

        Assert.Equal(0.2, store.Current.GlowIntensity);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SetVisible_NoChange_DoesNotRaise()
    {
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault()); // visible by default
        var raised = 0;
        store.Changed += _ => raised++;

        store.SetVisible(true); // already visible

        Assert.Equal(0, raised);
    }
}
