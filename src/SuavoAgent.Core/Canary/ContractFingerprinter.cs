using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Canary;

namespace SuavoAgent.Core.Canary;

/// Deterministic SHA-256 hashing of contract components.
/// All inputs are sorted before hashing to ensure order-independence.
public static class ContractFingerprinter
{
    public static string HashObjects(IEnumerable<ObservedObject> objects)
    {
        var sorted = objects
            .OrderBy(o => o.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.TableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.ColumnName, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        foreach (var o in sorted)
            sb.Append($"{o.SchemaName}|{o.TableName}|{o.ColumnName}|{o.DataTypeName}|{o.MaxLength}|{o.IsNullable}\n");

        return Sha256Hex(sb.ToString());
    }

    public static string HashStatusMap(IEnumerable<ObservedStatus> statuses)
    {
        var sorted = statuses
            .OrderBy(s => s.Description, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        foreach (var s in sorted)
            sb.Append($"{s.Description}|{s.GuidValue}\n");

        return Sha256Hex(sb.ToString());
    }

    public static string HashQuery(string queryText)
        => Sha256Hex(queryText);

    public static string HashResultShape(IEnumerable<(string Name, string TypeName)> columns)
    {
        var sb = new StringBuilder();
        foreach (var (name, type) in columns)
            sb.Append($"{name}|{type}\n");

        return Sha256Hex(sb.ToString());
    }

    public static string CompositeHash(string objectHash, string statusHash,
        string queryHash, string resultShapeHash)
        => Sha256Hex($"{objectHash}|{statusHash}|{queryHash}|{resultShapeHash}");

    private static string Sha256Hex(string input)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
