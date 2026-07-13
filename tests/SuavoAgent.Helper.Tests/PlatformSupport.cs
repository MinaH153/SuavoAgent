using System.Runtime.Versioning;

// Analyzer context only: the suite validates a Windows-only product assembly.
// This attribute does not skip tests; headless/pure tests continue to execute on
// macOS and Linux, while tests requiring Windows keep their explicit runtime skips.
[assembly: SupportedOSPlatform("windows")]
