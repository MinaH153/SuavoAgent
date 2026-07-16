using System.Text;

namespace SuavoAgent.Setup.Maintenance;

/// <summary>
/// Race-resistant bounded reads for files crossing from LocalService-writable
/// storage into SYSTEM. The open handle denies writes/deletes for the duration,
/// length is checked on that handle, and no API is allowed to allocate or copy
/// beyond the declared maximum.
/// </summary>
internal static class BoundedFile
{
    public static byte[] ReadBytes(string path, long maximumBytes)
    {
        if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (!File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bounded file is missing or is a reparse point.");
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        var length = stream.Length;
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException("Bounded file length is invalid.");
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Bounded file changed during read.");
        return bytes;
    }

    public static string ReadUtf8(string path, int maximumBytes) =>
        new UTF8Encoding(false, true).GetString(ReadBytes(path, maximumBytes));

    public static void CopyAndHashVerify(
        string source,
        string destination,
        long maximumBytes,
        string expectedSha256)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (!File.Exists(source) ||
            (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Copy source is missing or is a reparse point.");
        using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan);
        if (input.Length <= 0 || input.Length > maximumBytes)
            throw new InvalidDataException("Copy source length is invalid.");
        using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.SequentialScan);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException("Copy source exceeded its size limit.");
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Copied file hash mismatch.");
    }
}
