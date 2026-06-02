using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// Producer side of the post-OTA restart contract. The Watchdog (SuavoAgent.Watchdog.Tests)
/// asserts the consumer side; these lock the on-disk shape so the two stay in sync: schemaVersion 1,
/// a round-trippable requestedAt, and the exact ["SuavoAgent.Broker"] allowlist the Watchdog enforces.
/// </summary>
public class WatchdogRestartRequestWriterTests
{
    [Fact]
    public void Queue_WritesValidRequestTheWatchdogAccepts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"suavo_restart_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            WatchdogRestartRequestWriter.Queue(dir, "3.18.3", NullLogger.Instance);

            var path = Path.Combine(dir, WatchdogRestartRequestWriter.FileName);
            Assert.True(File.Exists(path));

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("3.18.3", root.GetProperty("version").GetString());

            var services = root.GetProperty("services");
            Assert.Equal(1, services.GetArrayLength());
            Assert.Equal("SuavoAgent.Broker", services[0].GetString());

            // requestedAt must be a round-trippable timestamp the Watchdog can TTL-check.
            Assert.True(DateTimeOffset.TryParse(
                root.GetProperty("requestedAt").GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out _));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Queue_OverwritesExistingRequest()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"suavo_restart_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, WatchdogRestartRequestWriter.FileName);
            File.WriteAllText(path, "{\"stale\":true}");

            WatchdogRestartRequestWriter.Queue(dir, "3.18.4", NullLogger.Instance);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("3.18.4", doc.RootElement.GetProperty("version").GetString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
