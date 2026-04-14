using SuavoAgent.Core.Intelligence;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Intelligence;

public class ContextAssemblerTests
{
    [Fact]
    public void AssembleContext_ProducesValidPacket()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-ctx-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var assembler = new ContextAssembler(db);
            var context = assembler.AssembleContext("test-pharmacy");
            Assert.Equal("test-pharmacy", context.BusinessId);
            Assert.NotNull(context.StationInfo);
        }
        finally { File.Delete(dbPath); }
    }

    [Fact]
    public void SerializeAndValidate_ReturnsCleanJson()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-ctx2-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var assembler = new ContextAssembler(db);
            var context = assembler.AssembleContext("test-pharmacy");
            var json = assembler.SerializeAndValidate(context);
            Assert.NotNull(json);
            Assert.Contains("test-pharmacy", json);
        }
        finally { File.Delete(dbPath); }
    }

    [Fact]
    public void SerializeAndValidate_Under8KB()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-ctx3-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var assembler = new ContextAssembler(db);
            var json = assembler.SerializeAndValidate(assembler.AssembleContext("test"));
            Assert.NotNull(json);
            Assert.True(json!.Length < 8192);
        }
        finally { File.Delete(dbPath); }
    }
}
