using System.Runtime.Versioning;

// SuavoAgent.Helper is the interactive Windows desktop process. Individual
// pure helpers remain executable in cross-platform tests, but the shipped
// assembly itself is never a supported non-Windows product surface.
[assembly: SupportedOSPlatform("windows")]
