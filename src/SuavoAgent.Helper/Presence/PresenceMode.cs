using System;

namespace SuavoAgent.Helper.Presence;

public enum PresenceMode { Idle, Driving, Observing }

/// <summary>Pure mode evaluation. Human input within the observe window AND at least as recent as
/// agent activity ⇒ Observing (the takeover/learning state). Else recent agent ⇒ Driving. Else Idle.</summary>
public static class PresenceModeLogic
{
    public static PresenceMode Evaluate(
        DateTimeOffset? lastAgent, DateTimeOffset? lastHuman, DateTimeOffset now,
        TimeSpan drivingWindow, TimeSpan observeWindow)
    {
        var humanFresh = lastHuman is { } h && now - h <= observeWindow;
        var agentFresh = lastAgent is { } a && now - a <= drivingWindow;
        if (humanFresh && (lastAgent is null || lastHuman!.Value >= lastAgent.Value)) return PresenceMode.Observing;
        if (agentFresh) return PresenceMode.Driving;
        return PresenceMode.Idle;
    }
}
