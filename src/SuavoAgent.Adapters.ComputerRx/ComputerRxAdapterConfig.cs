using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Adapters;

namespace SuavoAgent.Adapters.ComputerRx;

/// <summary>
/// Adapter config for Computer-Rx — the canary second PMS. The detection engine is not built
/// yet (P2), so this config is NOT registered in production; it exists to prove a real second
/// adapter declares a registry-valid config (non-empty PHI policy, distinct AdapterType) and to
/// be wired into <c>AddAdapterRegistry</c> the moment a Computer-Rx engine lands.
/// </summary>
public static class ComputerRxAdapterConfig
{
    public const string AdapterType = "computerrx";

    public static AdapterConfig Create() => new()
    {
        AdapterType = AdapterType,
        DisplayName = "Computer-Rx",
        Process = new ProcessIdentity("ComputerRx", "Computer-Rx"),
        Phi = PhiPolicy.Create(
            new HashSet<string>(
                new[] { "PatientName", "PatientDOB", "SSN", "PatientAddress", "PatientPhone", "PatientEmail" },
                StringComparer.OrdinalIgnoreCase),
            new[] { "patient", "ssn", "dob", "birth", "phone", "address", "email", "person" }),
        Sql = new SqlProfile(DefaultCatalog: "ComputerRx", SchemaSignature: "computerrx.sql.metadata.v1"),
    };
}
