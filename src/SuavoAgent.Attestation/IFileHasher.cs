namespace SuavoAgent.Attestation;

public interface IFileHasher
{
    /// <summary>
    /// SHA-256 of the file at the given path as lowercase hex. Returns null
    /// if file missing or unreadable.
    /// </summary>
    string? Sha256(string path);
}
