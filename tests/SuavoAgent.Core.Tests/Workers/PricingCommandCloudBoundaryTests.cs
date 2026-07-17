using Xunit;
using SuavoAgent.Core.Pricing;
using System.Text.Json;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class PricingCommandCloudBoundaryTests
{
    [Fact]
    public void Pricing_commands_keep_paths_and_workbook_metadata_device_local()
    {
        var source = ReadPricingCommandSource();

        Assert.DoesNotContain("TryGetProperty(\"excelPath\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetProperty(\"ndcColumn\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetProperty(\"supplierColumn\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetProperty(\"costColumn\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("discoveredFileName", source, StringComparison.Ordinal);
        Assert.DoesNotContain("discoveryReason", source, StringComparison.Ordinal);
        Assert.DoesNotContain("columnHeaders", source, StringComparison.Ordinal);
        Assert.DoesNotContain("samplerError", source, StringComparison.Ordinal);
        Assert.Contains("TryResolvePricingDiscoveryCandidate", source, StringComparison.Ordinal);
        Assert.Contains("pricing_candidate_token_required", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Ambiguous_or_missing_workbook_is_not_acknowledged_as_completed()
    {
        var source = ReadPricingCommandSource().Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("PricingTerminalAck.NotFound()", source, StringComparison.Ordinal);
        Assert.Contains("PricingTerminalAck.LocalConfirmation(candidateCount)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("status = \"needs_confirmation\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("discoveryResolution =", source, StringComparison.Ordinal);

        var notFound = PricingTerminalAck.NotFound();
        Assert.Equal(PricingTerminalAck.NotFoundResult, notFound.ResultKind);
        Assert.Equal("pricing_workbook_not_found", notFound.ErrorCode);
        var notFoundJson = JsonSerializer.Serialize(notFound.BuildResult());
        Assert.Contains("\"status\":\"needs_input\"", notFoundJson, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"pricing_workbook_not_found\"", notFoundJson, StringComparison.Ordinal);
        Assert.Contains("\"recovery\":\"place_local_workbook\"", notFoundJson, StringComparison.Ordinal);

        var confirmation = PricingTerminalAck.LocalConfirmation(candidateCount: 2);
        Assert.Equal(PricingTerminalAck.LocalConfirmationResult, confirmation.ResultKind);
        Assert.Equal("pricing_local_confirmation_required", confirmation.ErrorCode);
        Assert.Equal(2, confirmation.CandidateCount);
    }

    private static string ReadPricingCommandSource()
    {
        for (var cursor = new DirectoryInfo(AppContext.BaseDirectory);
             cursor is not null;
             cursor = cursor.Parent)
        {
            var candidate = Path.Combine(
                cursor.FullName,
                "src",
                "SuavoAgent.Core",
                "Workers",
                "HeartbeatWorker.PricingCommands.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("HeartbeatWorker.PricingCommands.cs not found");
    }
}
