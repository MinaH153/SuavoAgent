// src/SuavoAgent.Setup/Verify/VerifyGate.cs
namespace SuavoAgent.Setup.Verify;

/// <summary>Outcome of one self-verify gate. Fail blocks Success; Warn/Skip do not.</summary>
public enum GateState { Ok, Fail, Warn, Skip }

public sealed record GateResult(string Name, GateState State, string Detail);
