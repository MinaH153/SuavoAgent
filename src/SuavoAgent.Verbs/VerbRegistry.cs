using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Verbs;

/// <summary>
/// Default in-memory registry. Verbs are added via constructor so a hosted
/// service can compose the registry at startup from a static list or a
/// signed catalog bundle.
/// </summary>
public sealed class VerbRegistry : IVerbRegistry
{
    private readonly Dictionary<(string Name, string Version), IVerb> _verbs;
    private readonly Dictionary<(string Name, string Version), string> _schemaHashes;

    public VerbRegistry(IEnumerable<IVerb> verbs)
    {
        _verbs = new Dictionary<(string, string), IVerb>();
        _schemaHashes = new Dictionary<(string, string), string>();

        foreach (var v in verbs)
        {
            var key = (v.Metadata.Name, v.Metadata.Version);
            if (_verbs.ContainsKey(key))
                throw new InvalidOperationException(
                    $"duplicate verb registration: {v.Metadata.Name}@{v.Metadata.Version}");
            _verbs[key] = v;
            _schemaHashes[key] = ComputeSchemaHash(v.Metadata);
        }
    }

    public IVerb? Resolve(string name, string version) =>
        _verbs.TryGetValue((name, version), out var v) ? v : null;

    public string SchemaHash(string name, string version) =>
        _schemaHashes.TryGetValue((name, version), out var h) ? h : "";

    public IEnumerable<VerbMetadata> AllMetadata() => _verbs.Values.Select(v => v.Metadata);

    internal static string ComputeSchemaHash(VerbMetadata md)
    {
        // Hash the semantically stable parts of the metadata. Changes to
        // description or justification DO NOT change the hash — only changes
        // to the parameter + output schemas + BAA scope + risk tier do.
        var canonical = new
        {
            md.Name,
            md.Version,
            md.RiskTier,
            BaaScope = md.BaaScope.GetType().Name,
            md.IsMutation,
            md.IsDestructive,
            Parameters = md.Parameters.Required.Select(p => new
            {
                p.Name,
                Type = p.ClrType.FullName,
                p.ValidationHint
            }),
            Output = md.Output.Fields.Select(f => new
            {
                f.Name,
                Type = f.ClrType.FullName
            })
        };

        var json = JsonSerializer.Serialize(canonical);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
