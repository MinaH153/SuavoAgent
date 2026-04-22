// SuavoAgent.Recon.Interpret — reads the JSON produced by nadim-pricing-schema-recon.ps1
// and runs it through PricingSchemaResolver to produce an immediately actionable report:
//   - can we build the SQL? yes/no + confidence
//   - what does the query look like (so Joshua can paste into SSMS for a side-by-side
//     against the Pricing tab)
//   - sample NDCs from the recon, ready for the 5/5 tripwire check
//
// Run:
//   dotnet run --project docs/recon/InterpretRecon -- /path/to/pioneer-pricing-recon-*.json
//
// Exit codes:
//   0 = resolver succeeded AND confidence >= 0.70
//   1 = resolver failed (schema mismatch)
//   2 = resolver succeeded but confidence below tripwire — manual review needed
//   3 = usage error / I/O error

using System.Text.Json;
using SuavoAgent.Adapters.PioneerRx.Pricing;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Recon.Interpret;

public static class Program
{
    private const double ConfidenceTripwire = 0.70;

    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: interpret-recon <recon.json>");
            return 3;
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 3;
        }

        ReconPayload payload;
        try
        {
            var json = File.ReadAllText(path);
            payload = ReconParser.Parse(json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Parse error: {ex.Message}");
            return 3;
        }

        Console.WriteLine("=== Recon summary ===");
        Console.WriteLine($"  Inventory tables         : {payload.InventoryTables.Count}");
        Console.WriteLine($"  Inventory columns        : {payload.InventoryColumns.Count}");
        Console.WriteLine($"  Supplier/Vendor tables   : {string.Join(", ", payload.SupplierTables.DefaultIfEmpty("(none)"))}");
        Console.WriteLine($"  Sample NDCs              : {payload.NdcSamples.Count}");
        Console.WriteLine();

        var outcome = PricingSchemaResolver.Resolve(payload.InventoryColumns);

        Console.WriteLine("=== Resolver outcome ===");
        Console.WriteLine($"  Ok                       : {outcome.Ok}");
        if (!outcome.Ok)
        {
            Console.WriteLine($"  Reason                   : {outcome.Reason}");
            PrintNotes(outcome.Notes);
            Console.WriteLine();
            Console.WriteLine("⚠ Saturday pivot: Tier-2 SQL batch not safe. Run Wedge-A with UIA-only narrow demo.");
            return 1;
        }

        var schema = outcome.Schema!;
        Console.WriteLine($"  Catalog table            : {schema.CatalogSchema}.{schema.CatalogTable}");
        Console.WriteLine($"  Cost column              : {schema.CostColumn}");
        Console.WriteLine($"  CostPerUnit column       : {schema.CostPerUnitColumn ?? "(falls back to Cost)"}");
        Console.WriteLine($"  NDC location             : {(schema.NdcColumn != null ? $"{schema.CatalogTable}.{schema.NdcColumn}" : $"{schema.ItemJoin!.ItemTable}.{schema.ItemJoin.NdcColumnInItem} (joined)")}");
        Console.WriteLine($"  Supplier resolution      : {schema.SupplierSource.Resolution}");
        Console.WriteLine($"  Status column            : {schema.StatusColumn ?? "(none — no status filter)"}");
        Console.WriteLine($"  Confidence               : {schema.ConfidenceScore:F2}");
        Console.WriteLine();

        Console.WriteLine("=== Generated SQL ===");
        var sql = SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema);
        Console.WriteLine(sql);
        Console.WriteLine();

        Console.WriteLine("=== Tripwire verification steps (Joshua, Thursday 23:59 PT gate) ===");
        Console.WriteLine("Paste the SQL above into SSMS. Run it 5 times, one per NDC below.");
        Console.WriteLine("Compare the returned (SupplierName, Cost) tuple to the top-of-Supplier-Catalog");
        Console.WriteLine("row in PioneerRx's Edit Rx Item → Pricing tab. 5/5 match = ship Tier 2.");
        Console.WriteLine();
        foreach (var ndc in payload.NdcSamples.Take(5))
            Console.WriteLine($"  - {ndc.NdcRaw,-20}  (ItemID={ndc.ItemId}, {ndc.ItemName})");
        Console.WriteLine();

        Console.WriteLine("=== Resolver diagnostic notes ===");
        PrintNotes(outcome.Notes);
        Console.WriteLine();

        if (schema.ConfidenceScore < ConfidenceTripwire)
        {
            Console.WriteLine($"⚠ Confidence {schema.ConfidenceScore:F2} below tripwire ({ConfidenceTripwire:F2}).");
            Console.WriteLine("   Manual review required before shipping the Tier-2 SQL batch.");
            return 2;
        }

        Console.WriteLine($"✓ Confidence {schema.ConfidenceScore:F2} passes tripwire. Ship Tier 2 after 5/5 NDC match.");
        return 0;
    }

    private static void PrintNotes(IReadOnlyList<string> notes)
    {
        if (notes.Count == 0)
        {
            Console.WriteLine("  (no notes)");
            return;
        }
        foreach (var note in notes)
            Console.WriteLine($"  - {note}");
    }
}

internal sealed record ReconPayload(
    IReadOnlyList<(string Schema, string Table)> InventoryTables,
    IReadOnlyList<InventoryColumnInfo> InventoryColumns,
    IReadOnlyList<NdcSample> NdcSamples,
    IReadOnlyList<string> SupplierTables);

internal sealed record NdcSample(int ItemId, string ItemName, string NdcRaw);

/// <summary>
/// Parses the recon JSON produced by <c>docs/recon/nadim-pricing-schema-recon.ps1</c>. That script
/// emits a dictionary keyed by query name with a <c>rows</c> array of tab-separated strings — we
/// split each TSV line and build the typed records <see cref="PricingSchemaResolver"/> expects.
/// </summary>
internal static class ReconParser
{
    public static ReconPayload Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tables = ParseTsvQuery(root, "inventory_schema_tables")
            .Where(r => r.Length >= 2)
            .Select(r => (r[0].Trim(), r[1].Trim()))
            .ToList();

        var columns = ParseTsvQuery(root, "inventory_schema_columns")
            .Where(r => r.Length >= 4)
            .Select(r => new InventoryColumnInfo(
                SchemaName: r[0].Trim(),
                TableName: r[1].Trim(),
                ColumnName: r[2].Trim(),
                DataType: r[3].Trim(),
                IsNullable: r.Length >= 5 && ParseBoolLike(r[4])))
            .ToList();

        var samples = ParseTsvQuery(root, "ndc_samples")
            .Where(r => r.Length >= 3)
            .Select(r => new NdcSample(
                ItemId: int.TryParse(r[0].Trim(), out var id) ? id : 0,
                ItemName: r[1].Trim(),
                NdcRaw: r[2].Trim()))
            .ToList();

        var supplierTables = ParseTsvQuery(root, "supplier_or_vendor_tables")
            .Where(r => r.Length >= 1)
            .Select(r => r[0].Trim())
            .ToList();

        return new ReconPayload(tables, columns, samples, supplierTables);
    }

    private static IEnumerable<string[]> ParseTsvQuery(JsonElement root, string queryName)
    {
        if (!root.TryGetProperty(queryName, out var queryBlock)) yield break;
        if (!queryBlock.TryGetProperty("rows", out var rows)) yield break;
        if (rows.ValueKind != JsonValueKind.Array) yield break;

        bool firstDataRow = true;
        foreach (var row in rows.EnumerateArray())
        {
            var line = row.GetString();
            if (string.IsNullOrWhiteSpace(line)) continue;
            // sqlcmd -W strips trailing spaces but emits a ---- separator under headers; skip it.
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;
            if (firstDataRow)
            {
                // Header row from sqlcmd — skip.
                firstDataRow = false;
                continue;
            }
            yield return line.Split('\t');
        }
    }

    private static bool ParseBoolLike(string token)
    {
        var t = token.Trim();
        return t == "1" || t.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
