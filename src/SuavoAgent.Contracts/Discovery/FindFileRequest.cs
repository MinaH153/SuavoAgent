namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// IPC request sent Core → Helper asking the agent to discover a file
/// matching a <see cref="FileDiscoverySpec"/>. The Helper runs
/// <c>FileLocatorService</c> in the interactive user session (required —
/// Core runs as LocalSystem and can't see the user's Desktop/Documents).
/// </summary>
/// <param name="JobId">Stable correlation id. Used to tie the discovery
/// back to the cloud signed command that initiated it.</param>
/// <param name="Spec">What to find — vertical-neutral spec carrying pack priors.</param>
public sealed record FindFileRequest(string JobId, FileDiscoverySpec Spec);
