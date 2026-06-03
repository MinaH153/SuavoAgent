using SuavoAgent.Contracts.Annotations;

namespace SuavoAgent.Analyzers.IntegrationTest;

/// <summary>
/// THIS FILE INTENTIONALLY FAILS THE BUILD. Run via run-integration.sh which
/// expects the build to fail with SUAVO0001 diagnostic on PatientName.
/// If the analyzer is broken or unwired, this file would compile silently —
/// the integration test catches that case.
/// </summary>
[OutboundPayload]
public class CanaryLeak
{
    [PhiDirect]
    public string PatientName { get; set; } = "";
}
