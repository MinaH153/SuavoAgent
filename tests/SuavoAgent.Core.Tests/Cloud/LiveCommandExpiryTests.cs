using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class LiveCommandExpiryTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-12T18:00:00.000Z");
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly MutableTimeProvider _clock = new(Now);
    private readonly SignedCommandVerifier _verifier;

    public LiveCommandExpiryTests()
    {
        var publicKey = Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo());
        _verifier = new SignedCommandVerifier(
            new Dictionary<string, string> { ["live-v1"] = publicKey },
            "agent-live", "fingerprint-live", _clock);
    }

    [Fact]
    public void Verify_LiveCommandWithBoundedFutureExpiry_Succeeds()
    {
        var command = Sign("show_cursor", Now.AddMinutes(5));

        Assert.True(_verifier.Verify(command).IsValid);
    }

    [Theory]
    [InlineData("fetch_patient")]
    [InlineData("delivery_writeback")]
    [InlineData("run_pricing_job")]
    [InlineData("run_workflow")]
    [InlineData("navigate_app")]
    [InlineData("navigate_pricing")]
    [InlineData("run_learned_template")]
    [InlineData("replay_skill")]
    [InlineData("export_pioneerrx_shadow_fixture")]
    [InlineData("set_reasoning_config")]
    [InlineData("restart_helper")]
    [InlineData("force_restart")]
    [InlineData("repair")]
    [InlineData("repair_agent")]
    [InlineData("self_uninstall")]
    public void Verify_SideEffectCommandWithoutExpiry_FailsClosed(string name)
    {
        var command = Sign(name, expiresAt: null);

        var result = _verifier.Verify(command);

        Assert.False(result.IsValid);
        Assert.Equal("Live command expiry missing", result.Reason);
    }

    [Fact]
    public void Verify_ExpiryEqualToCurrentInstant_IsExpired()
    {
        var command = Sign("find_and_run_pricing_job", Now);

        var result = _verifier.Verify(command);

        Assert.False(result.IsValid);
        Assert.Equal("Live command authority expired", result.Reason);
    }

    [Fact]
    public void Verify_ExpiryBeyondFiveMinuteAuthority_IsRejected()
    {
        var command = Sign("show_cursor", Now.AddMinutes(5).AddTicks(1));

        var result = _verifier.Verify(command);

        Assert.False(result.IsValid);
        Assert.Equal("Live command expiry out of bounds", result.Reason);
    }

    [Fact]
    public void Executor_RechecksExpiryAfterSuccessfulSignatureVerification()
    {
        var expiresAt = Now.AddSeconds(1);
        var command = Sign("self_uninstall", expiresAt);
        Assert.True(_verifier.Verify(command, consumeNonce: false).IsValid);

        _clock.SetUtcNow(expiresAt);
        var result = _verifier.VerifyExecutionAuthority(command);

        Assert.False(result.IsValid);
        Assert.Equal("Live command authority expired", result.Reason);
    }

    [Fact]
    public void Verify_ReadOnlyDiagnosticsCommand_DoesNotRequireExpiry()
    {
        var command = Sign("fetch_diagnostics", expiresAt: null);

        Assert.True(_verifier.Verify(command).IsValid);
        Assert.True(_verifier.VerifyExecutionAuthority(command).IsValid);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("approve_pom")]
    [InlineData("install_pioneerrx_process_approval")]
    [InlineData("set_vision_config")]
    [InlineData("install_pricing_cost_basis_approval")]
    [InlineData("revoke_pricing_cost_basis_approval")]
    public void Verify_DurableOutboxUsesItsPurposeBuiltContract_NotLiveExpiry(
        string name)
    {
        var command = Sign(name, expiresAt: null);

        Assert.Equal(
            SignedCommandAuthorityClass.DurableOutbox,
            SignedCommandVerifier.ClassifyCommand(name));
        Assert.True(_verifier.Verify(command).IsValid);
    }

    [Fact]
    public void Classifier_ExplicitlyCoversEveryReachableHeartbeatCommand()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src", "SuavoAgent.Core", "Workers",
            "HeartbeatWorker.SignedCommands.cs"));
        var reachableCommands = Regex.Matches(
                source,
                "case\\s+\"(?<command>[^\"]+)\"|" +
                "string\\.Equals\\(\\s*cmd\\.Command,\\s*\"(?<command>[^\"]+)\"",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["command"].Value)
            .Where(command => command.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        reachableCommands.Add(PioneerRxApprovalInstallCommandContract.CommandName);
        reachableCommands.Add(SelfUninstallContract.CommandName);

        Assert.All(
            reachableCommands,
            command => Assert.True(
                SignedCommandVerifier.IsExplicitlyClassified(command),
                $"Heartbeat command '{command}' has no explicit authority class."));
    }

    [Fact]
    public void Classifier_UnknownNewCommandFailsClosedAsLiveMutator()
    {
        const string commandName = "future_mutator_not_yet_reviewed";

        Assert.False(SignedCommandVerifier.IsExplicitlyClassified(commandName));
        Assert.Equal(
            SignedCommandAuthorityClass.LiveMutator,
            SignedCommandVerifier.ClassifyCommand(commandName));
        var result = _verifier.Verify(Sign(commandName, expiresAt: null));
        Assert.False(result.IsValid);
        Assert.Equal("Live command expiry missing", result.Reason);
    }

    public void Dispose() => _signer.Dispose();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SuavoAgent.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate SuavoAgent.sln.");
    }

    private SignedCommand Sign(string command, DateTimeOffset? expiresAt)
    {
        var timestamp = Now.ToString("o");
        var nonce = Guid.NewGuid().ToString();
        var expiryText = expiresAt?.ToString("o");
        var dataJson = expiryText is null
            ? null
            : $$"""{"expiresAt":"{{expiryText}}"}""";
        var dataHash = SignedCommandVerifier.ComputeDataHash(dataJson);
        var canonical = $"{command}|agent-live|fingerprint-live|{timestamp}|{nonce}|{dataHash}";
        var signature = Convert.ToBase64String(_signer.SignData(
            Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
        return new SignedCommand(
            command, "agent-live", "fingerprint-live", timestamp, nonce,
            "live-v1", signature, dataHash, expiryText);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void SetUtcNow(DateTimeOffset value) => _now = value;
    }
}
