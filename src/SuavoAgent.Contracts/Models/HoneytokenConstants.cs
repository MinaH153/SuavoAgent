using System;
using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Contracts.Models;

/// <summary>
/// Shared honeytoken constants — the decoy filename + the opaque id derivation — so the Broker (which
/// provisions the file), the Helper watcher (which watches it), and the heartbeat signal (which reports
/// <see cref="ComputeId"/>) all agree without coupling assemblies.
///
/// The decoy is bait: a credential-shaped filename an attacker poking for secrets would open, holding fixed
/// innocuous bytes — agent code NEVER reads it, so any access is suspicious. The NAME is not PHI; the wire
/// only ever carries <see cref="ComputeId"/> (a SHA-256 of the name), never the file path or contents.
/// </summary>
public static class HoneytokenConstants
{
    /// <summary>Sub-directory under %ProgramData%\SuavoAgent\ that holds the decoy(s).</summary>
    public const string DirName = "honeytokens";

    /// <summary>The decoy filename — credential-shaped bait. Fixed; the Broker seeds it, agent code never reads it.</summary>
    public const string TokenFileName = "backup_credentials.dat";

    /// <summary>Fixed innocuous content written into the decoy (never real data).</summary>
    public static readonly byte[] TokenContent = Encoding.ASCII.GetBytes("SUAVO_HONEYTOKEN_DO_NOT_TOUCH\n");

    /// <summary>Opaque, PHI-free id = lowercase-hex SHA-256 of the token filename. Identifies WHICH token tripped without leaking the path.</summary>
    public static string ComputeId() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TokenFileName))).ToLowerInvariant();
}
