namespace SuavoAgent.Contracts.Behavioral;

/// <summary>
/// HIPAA-safe keystroke category — never records which key was pressed, only the class.
/// </summary>
public enum KeystrokeCategory
{
    Alpha,
    Digit,
    Tab,
    Enter,
    Escape,
    FunctionKey,
    Navigation,
    Modifier,
    Other
}

/// <summary>
/// Inter-keystroke timing bucket — tempo pattern without exact timing.
/// Rapid: &lt;500ms, Normal: 500ms–2s, Pause: &gt;2s
/// </summary>
public enum TimingBucket
{
    Rapid,
    Normal,
    Pause
}
