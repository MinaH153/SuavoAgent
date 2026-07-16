using System.Text.Json;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public partial class HeartbeatWorkerTests
{
    [Fact]
    public async Task SameVersionUpdate_InvalidOtaSignatureNeverConfirmsOrCreatesNoopProof()
    {
        const string commandId = "77777777-7777-4777-8777-777777777777";
        var hash = new string('a', 64);
        const string baseUrl =
            "https://github.com/SuavoLLC/MKM/releases/download/v3.9.2";
        var manifest = $"{baseUrl}/SuavoAgent.Core.exe|{hash}|" +
                       $"{baseUrl}/SuavoAgent.Broker.exe|{hash}|" +
                       $"{baseUrl}/SuavoAgent.Helper.exe|{hash}|" +
                       "3.9.2|net8.0|win-x64|" +
                       $"{baseUrl}/SuavoAgent.Watchdog.exe|{hash}";
        var data = new
        {
            manifest,
            manifestSignature = new string('0', 128),
            commandId,
            channel = "stable",
        };
        var dataJson = JsonSerializer.Serialize(data);
        var command = Sign("update", dataJson);

        await InvokeProcessAsync(BuildResponseJson(command, data));

        var replay = _db.RegisterUpdateCommandReceipt(
            commandId,
            command.Nonce,
            command.DataHash,
            "3.9.2");
        Assert.True(replay.Accepted);
        Assert.True(replay.IsReplay);
        Assert.Equal("pending_stage", replay.State);
        Assert.Null(_db.GetReleaseNoopDeviceReceipt(commandId));
    }
}
