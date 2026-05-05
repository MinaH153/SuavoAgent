namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// The virtual keys this agent is allowed to press via SendInput. The list
/// is intentionally narrow — we only need what sandbox workflows demand.
/// PioneerRx-specific verbs (HIGH risk, BAA amendment required) live in a
/// separate namespace and ship in Phase 5.3, gated on patent + N=2/3.
/// </summary>
public enum VirtualKey : ushort
{
    Backspace = 0x08,
    Tab = 0x09,
    Enter = 0x0D,
    Shift = 0x10,
    Control = 0x11,
    Alt = 0x12,
    CapsLock = 0x14,
    Escape = 0x1B,
    Space = 0x20,
    PageUp = 0x21,
    PageDown = 0x22,
    End = 0x23,
    Home = 0x24,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    Delete = 0x2E,

    Digit0 = 0x30, Digit1 = 0x31, Digit2 = 0x32, Digit3 = 0x33, Digit4 = 0x34,
    Digit5 = 0x35, Digit6 = 0x36, Digit7 = 0x37, Digit8 = 0x38, Digit9 = 0x39,

    A = 0x41, B = 0x42, C = 0x43, D = 0x44, E = 0x45, F = 0x46, G = 0x47, H = 0x48,
    I = 0x49, J = 0x4A, K = 0x4B, L = 0x4C, M = 0x4D, N = 0x4E, O = 0x4F, P = 0x50,
    Q = 0x51, R = 0x52, S = 0x53, T = 0x54, U = 0x55, V = 0x56, W = 0x57, X = 0x58,
    Y = 0x59, Z = 0x5A,

    LWin = 0x5B,

    F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75,
    F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,
}

/// <summary>
/// A chord = an optional set of modifier keys held while one main key is
/// pressed and released. Examples: "Ctrl+S", "Ctrl+Shift+Esc", "Enter".
/// </summary>
public sealed record KeyChord(IReadOnlyList<VirtualKey> Modifiers, VirtualKey MainKey)
{
    public static bool TryParse(string raw, out KeyChord? chord)
    {
        chord = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var modifiers = new List<VirtualKey>(parts.Length - 1);
        VirtualKey? main = null;

        for (var i = 0; i < parts.Length; i++)
        {
            var token = parts[i];
            if (!TryMapToken(token, out var vk)) return false;

            if (i == parts.Length - 1)
            {
                main = vk;
            }
            else
            {
                if (!IsModifier(vk)) return false;
                modifiers.Add(vk);
            }
        }

        if (main is null) return false;
        // Reject "Ctrl+", "Ctrl+Shift", etc. — a chord must end in a non-modifier key.
        if (IsModifier(main.Value)) return false;
        chord = new KeyChord(modifiers, main.Value);
        return true;
    }

    private static bool IsModifier(VirtualKey vk) =>
        vk == VirtualKey.Control || vk == VirtualKey.Shift || vk == VirtualKey.Alt || vk == VirtualKey.LWin;

    private static bool TryMapToken(string token, out VirtualKey vk)
    {
        // Common aliases first — operators write "Ctrl" not "Control".
        switch (token.ToLowerInvariant())
        {
            case "ctrl": case "control": vk = VirtualKey.Control; return true;
            case "shift": vk = VirtualKey.Shift; return true;
            case "alt": vk = VirtualKey.Alt; return true;
            case "win": case "meta": case "lwin": vk = VirtualKey.LWin; return true;
            case "esc": case "escape": vk = VirtualKey.Escape; return true;
            case "enter": case "return": vk = VirtualKey.Enter; return true;
            case "tab": vk = VirtualKey.Tab; return true;
            case "space": vk = VirtualKey.Space; return true;
            case "back": case "backspace": vk = VirtualKey.Backspace; return true;
            case "delete": case "del": vk = VirtualKey.Delete; return true;
            case "home": vk = VirtualKey.Home; return true;
            case "end": vk = VirtualKey.End; return true;
            case "pageup": case "pgup": vk = VirtualKey.PageUp; return true;
            case "pagedown": case "pgdn": vk = VirtualKey.PageDown; return true;
            case "left": vk = VirtualKey.Left; return true;
            case "right": vk = VirtualKey.Right; return true;
            case "up": vk = VirtualKey.Up; return true;
            case "down": vk = VirtualKey.Down; return true;
        }

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c >= 'A' && c <= 'Z') { vk = (VirtualKey)c; return true; }
            if (c >= '0' && c <= '9') { vk = (VirtualKey)c; return true; }
        }

        if (token.Length >= 2 && (token[0] == 'F' || token[0] == 'f') && int.TryParse(token[1..], out var n) && n >= 1 && n <= 12)
        {
            vk = (VirtualKey)(0x70 + n - 1);
            return true;
        }

        vk = default;
        return false;
    }
}
