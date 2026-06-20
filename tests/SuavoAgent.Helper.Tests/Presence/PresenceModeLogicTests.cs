using System;
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresenceModeLogicTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Drive = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Observe = TimeSpan.FromSeconds(8);

    [Fact] public void RecentAgent_NoHuman_IsDriving()
        => Assert.Equal(PresenceMode.Driving, PresenceModeLogic.Evaluate(Now.AddSeconds(-1), null, Now, Drive, Observe));

    [Fact] public void RecentHuman_IsObserving_EvenWithRecentAgent()
        => Assert.Equal(PresenceMode.Observing, PresenceModeLogic.Evaluate(Now.AddSeconds(-1), Now.AddSeconds(-0.2), Now, Drive, Observe));

    [Fact] public void StaleEverything_IsIdle()
        => Assert.Equal(PresenceMode.Idle, PresenceModeLogic.Evaluate(Now.AddSeconds(-30), Now.AddSeconds(-30), Now, Drive, Observe));

    [Fact] public void HumanWithinObserveButOlderThanAgent_StaysDriving()
        => Assert.Equal(PresenceMode.Driving, PresenceModeLogic.Evaluate(Now.AddSeconds(-0.2), Now.AddSeconds(-2), Now, Drive, Observe));

    [Fact] public void Nulls_AreIdle()
        => Assert.Equal(PresenceMode.Idle, PresenceModeLogic.Evaluate(null, null, Now, Drive, Observe));
}
