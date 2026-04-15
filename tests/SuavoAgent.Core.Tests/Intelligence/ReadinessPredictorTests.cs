using SuavoAgent.Core.Intelligence;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Intelligence;

public class ReadinessPredictorTests
{
    [Fact]
    public void PredictReadiness_InsufficientData_ReturnsNull()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-pred-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var predictor = new ReadinessPredictor(db);
            var result = predictor.PredictReadiness();
            Assert.Null(result); // no samples = no prediction
        }
        finally { File.Delete(dbPath); }
    }

    [Fact]
    public void PredictReadiness_WithSufficientData_ReturnsPrediction()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-pred2-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var now = DateTimeOffset.UtcNow;

            // Insert 15 samples for current day/hour
            for (int i = 0; i < 15; i++)
            {
                db.InsertReadinessSample("s1", $"rx{i}",
                    now.AddMinutes(-20), now.AddMinutes(-10), now.AddMinutes(-3), now, null,
                    18.0 + (i % 5), (int)now.DayOfWeek, now.Hour, false, 3);
            }

            var predictor = new ReadinessPredictor(db);
            var result = predictor.PredictReadiness();

            Assert.NotNull(result);
            Assert.InRange(result!.PredictedMinutes, 15, 25);
            Assert.Equal(50, result.ConfidencePct); // 15 samples = low confidence
            Assert.True(result.DispatchAtMinute < result.PredictedMinutes);
        }
        finally { File.Delete(dbPath); }
    }

    [Fact]
    public void PredictReadiness_ControlledSubstance_TakesLonger()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-pred3-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var now = DateTimeOffset.UtcNow;

            for (int i = 0; i < 15; i++)
            {
                db.InsertReadinessSample("s1", $"rx{i}",
                    now.AddMinutes(-20), now.AddMinutes(-10), now.AddMinutes(-3), now, null,
                    20.0, (int)now.DayOfWeek, now.Hour, false, 3);
            }

            var predictor = new ReadinessPredictor(db);
            var normal = predictor.PredictReadiness(isControlled: false);
            var controlled = predictor.PredictReadiness(isControlled: true);

            Assert.NotNull(normal);
            Assert.NotNull(controlled);
            Assert.True(controlled!.PredictedMinutes > normal!.PredictedMinutes);
        }
        finally { File.Delete(dbPath); }
    }

    [Fact]
    public void GetOptimalDispatchMinute_ReturnsLessThanPredicted()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-pred4-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var now = DateTimeOffset.UtcNow;

            for (int i = 0; i < 15; i++)
            {
                db.InsertReadinessSample("s1", $"rx{i}",
                    now.AddMinutes(-25), now.AddMinutes(-15), now.AddMinutes(-5), now, null,
                    25.0, (int)now.DayOfWeek, now.Hour, false, 5);
            }

            var predictor = new ReadinessPredictor(db);
            var dispatch = predictor.GetOptimalDispatchMinute();

            Assert.NotNull(dispatch);
            Assert.True(dispatch!.Value < 25); // dispatch before predicted ready time
            Assert.True(dispatch.Value >= 0); // never negative
        }
        finally { File.Delete(dbPath); }
    }
}
