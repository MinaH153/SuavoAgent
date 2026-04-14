namespace SuavoAgent.Core.Config;

/// <summary>
/// BAA behavioral observation clauses and installer disclosure text.
/// </summary>
public static class BehavioralDisclosure
{
    public const string InstallerConsentText =
        "Behavioral Learning: During the learning period, SuavoAgent observes the structure " +
        "of your pharmacy software's screens and the patterns of how it's used (which buttons " +
        "are clicked, which screens are visited, what types of data are entered). It does NOT " +
        "capture what you type, patient information, or screen contents. A low-level keyboard " +
        "classification hook is active only when your pharmacy software is in the foreground. " +
        "Your endpoint protection software may detect this hook — it is expected behavior.";

    public static readonly string[] BaaClauses = new[]
    {
        "UI Automation Observation: Agent observes the structural properties (control type, " +
        "automation identifier, class name, bounding rectangle) of user interface elements in " +
        "pharmacy management software windows. Element content, values, and text are never captured.",

        "Element Name Hashing: The Name property of UI elements, which may incidentally contain " +
        "patient-contextual information, is cryptographically hashed using a per-pharmacy keyed " +
        "hash (HMAC-SHA256) before storage. The raw Name value is never persisted, transmitted, or logged.",

        "Keyboard Category Monitoring: When the pharmacy management software window has foreground " +
        "focus, the agent classifies keystrokes into categories (alphabetic, numeric, navigation, function) " +
        "to detect data entry patterns. Individual key codes, characters, and typed content are never " +
        "captured. Numeric digit sequences are capped at a count of three to prevent reconstruction of identifiers.",

        "SQL Query Shape Observation: When database server permissions allow, the agent observes the " +
        "structural shape of SQL queries executed by the pharmacy management software. All literal values " +
        "(strings, numbers, identifiers) are stripped before storage. Queries that cannot be safely " +
        "normalized are discarded entirely.",

        "Low-Level Keyboard Hook Disclosure: The agent uses the Windows SetWindowsHookEx(WH_KEYBOARD_LL) " +
        "API to classify keystroke categories. This system-level hook is installed only when the pharmacy " +
        "management software has foreground window focus and is immediately uninstalled when focus is lost. " +
        "Endpoint protection software may detect this hook installation. The hook captures keystroke " +
        "categories only, never individual key codes or characters.",
    };
}
