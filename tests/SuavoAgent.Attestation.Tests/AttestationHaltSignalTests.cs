using SuavoAgent.Attestation;
using Xunit;

namespace SuavoAgent.Attestation.Tests;

public class AttestationHaltSignalTests
{
    [Fact]
    public void DefaultState_IsNotHalted()
    {
        var s = new AttestationHaltSignal();
        Assert.False(s.IsHalted);
        Assert.Null(s.HaltReason);
        Assert.Null(s.HaltedAt);
    }

    [Fact]
    public void Halt_SetsFlags()
    {
        var s = new AttestationHaltSignal();
        s.Halt("test reason");
        Assert.True(s.IsHalted);
        Assert.Equal("test reason", s.HaltReason);
        Assert.NotNull(s.HaltedAt);
    }

    [Fact]
    public void Clear_ResetsFlags()
    {
        var s = new AttestationHaltSignal();
        s.Halt("initial");
        s.Clear("joshua");
        Assert.False(s.IsHalted);
        Assert.Contains("joshua", s.HaltReason);
    }

    [Fact]
    public void RepeatHalt_UpdatesReason_PreservesHaltedAt()
    {
        var s = new AttestationHaltSignal();
        s.Halt("first");
        var firstHaltedAt = s.HaltedAt;
        Thread.Sleep(10);
        s.Halt("second");
        Assert.Equal("second", s.HaltReason);
        Assert.Equal(firstHaltedAt, s.HaltedAt); // preserved across repeat halts
    }
}
