using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// Serializes every test that drives the REAL desktop (launches a window + SendInput/UIA). These share
/// one global resource — the interactive desktop, keyboard focus, and the input queue — so running them
/// in parallel makes clicks/keystrokes land in the wrong window. Classes tagged
/// [Collection("RealDesktopUia")] run one at a time; DisableParallelization keeps the collection from
/// overlapping anything else.
/// </summary>
[CollectionDefinition("RealDesktopUia", DisableParallelization = true)]
public sealed class RealDesktopUiaCollection
{
}
