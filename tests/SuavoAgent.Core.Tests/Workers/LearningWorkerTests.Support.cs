using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public partial class LearningWorkerTests
{
    // ────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────

    private static SeedResponse CreateFakeSeedResponse(
        string digest, string phase, bool withCorrelations = false)
    {
        var queryShapes = new[]
        {
            new SeedQueryShape("qs-hash-1", "SELECT * FROM Rx WHERE Status = @p0",
                new[] { "Rx" }, 0.9, 3)
        };

        var statusMappings = new[]
        {
            new SeedStatusMapping("Rx.Status", "guid-1", "Ready for Pickup", 3)
        };

        IReadOnlyList<SeedCorrelation>? correlations = withCorrelations
            ? new[]
            {
                new SeedCorrelation("tree1|elem1", "tree1", "elem1", "Button", "qsh1",
                    0.85, 0.9, 5, 0.5),
                new SeedCorrelation("tree2|elem2", "tree2", "elem2", "ListItem", "qsh2",
                    0.75, 0.85, 3, 0.4),
            }
            : null;

        return new SeedResponse(digest, 1, phase,
            new[] { "all" }, null, correlations,
            queryShapes, statusMappings, null);
    }
}
