using SuavoAgent.Core.Ipc;
using Xunit;

namespace SuavoAgent.Core.Tests.Ipc;

public sealed class IpcPeerVerifierTests
{
    [Fact]
    public void Verify_AcceptsHelperUnderInstallRoot()
    {
        var result = IpcPeerVerifier.Verify(
            processName: "SuavoAgent.Helper",
            processId: 15512,
            executablePath: @"C:\Program Files\Suavo\Agent\SuavoAgent.Helper.exe",
            coreBaseDirectory: @"C:\Program Files\Suavo\Agent\",
            brokerEvidence: null,
            isBrokerAttestedHelper: _ => false);

        Assert.True(result.Accepted);
        Assert.False(result.AcceptedByBrokerAttestation);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public void Verify_AcceptsBrokerAttestedHelperWhenPathUnreadable()
    {
        var result = IpcPeerVerifier.Verify(
            processName: "SuavoAgent.Helper",
            processId: 15512,
            executablePath: null,
            coreBaseDirectory: @"C:\Program Files\Suavo\Agent\",
            brokerEvidence: Evidence(),
            isBrokerAttestedHelper: evidence => evidence == Evidence());

        Assert.True(result.Accepted);
        Assert.True(result.AcceptedByBrokerAttestation);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public void Verify_RejectsPathUnreadableHelperWithoutBrokerAttestation()
    {
        var result = IpcPeerVerifier.Verify(
            processName: "SuavoAgent.Helper",
            processId: 15512,
            executablePath: null,
            coreBaseDirectory: @"C:\Program Files\Suavo\Agent\",
            brokerEvidence: Evidence(),
            isBrokerAttestedHelper: _ => false);

        Assert.False(result.Accepted);
        Assert.Equal("image_path_unreadable", result.RejectionReason);
    }

    [Fact]
    public void Verify_RejectsPidOnlyAttestationWithoutExactProcessEvidence()
    {
        var result = IpcPeerVerifier.Verify(
            processName: "SuavoAgent.Helper",
            processId: 15512,
            executablePath: null,
            coreBaseDirectory: @"C:\Program Files\Suavo\Agent\",
            brokerEvidence: null,
            isBrokerAttestedHelper: _ => true);

        Assert.False(result.Accepted);
        Assert.Equal("image_path_unreadable", result.RejectionReason);
    }

    [Fact]
    public void Verify_DoesNotLetAttestationBypassProcessName()
    {
        var result = IpcPeerVerifier.Verify(
            processName: "notepad",
            processId: 15512,
            executablePath: null,
            coreBaseDirectory: @"C:\Program Files\Suavo\Agent\",
            brokerEvidence: Evidence(),
            isBrokerAttestedHelper: _ => true);

        Assert.False(result.Accepted);
        Assert.Equal("unauthorized_process:notepad", result.RejectionReason);
    }

    [Fact]
    public void Verify_RejectsExecutableOutsideInstallRoot()
    {
        var result = IpcPeerVerifier.Verify(
            processName: "SuavoAgent.Helper",
            processId: 15512,
            executablePath: @"C:\Users\queen\Downloads\SuavoAgent.Helper.exe",
            coreBaseDirectory: @"C:\Program Files\Suavo\Agent\",
            brokerEvidence: Evidence(),
            isBrokerAttestedHelper: _ => true);

        Assert.False(result.Accepted);
        Assert.Equal("path_outside_install_dir", result.RejectionReason);
    }

    [Fact]
    public void Verify_RejectsSiblingDirectoryUnderSuavoParent()
    {
        var result = IpcPeerVerifier.Verify(
            processName: "SuavoAgent.Helper",
            processId: 15512,
            executablePath: @"C:\Program Files\Suavo\Other\SuavoAgent.Helper.exe",
            coreBaseDirectory: @"C:\Program Files\Suavo\Agent\",
            brokerEvidence: null,
            isBrokerAttestedHelper: _ => false);

        Assert.False(result.Accepted);
        Assert.Equal("path_outside_install_dir", result.RejectionReason);
    }

    [Fact]
    public void Program_UsesShortBrokerAttestationWindow()
    {
        var source = ReadRepoFile("src/SuavoAgent.Core/CoreRuntimeServiceRegistration.cs");

        Assert.Contains("IpcPeerAttestationStore.ContainsHelper", source);
        Assert.Contains("TimeSpan.FromMinutes(5)", source);
        Assert.DoesNotContain("TimeSpan.FromDays(7)", source);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath}");
    }

    private static IpcBrokerAttestationEvidence Evidence() => new(
        15512,
        1,
        DateTimeOffset.Parse("2026-07-12T18:00:00Z"),
        new string('a', 64));
}
