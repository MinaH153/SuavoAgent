using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Adapters.PioneerRx.Pricing;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Adapters;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Pricing;

public sealed record TopDispensedWorklistBuildResult(
    bool Ok,
    string? WorkbookPath,
    int ItemCount,
    string? ErrorCode)
{
    public static TopDispensedWorklistBuildResult Success(
        string path,
        int count) => new(true, path, count, null);

    public static TopDispensedWorklistBuildResult Fail(string errorCode) =>
        new(false, null, 0, errorCode);
}

public interface ITopDispensedWorklistBuilder
{
    Task<TopDispensedWorklistBuildResult> BuildAsync(
        string commandId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Builds the versioned pricing input entirely on the approved workstation. The SQL query returns
/// aggregate drug/NDC counts only; the resulting workbook never leaves the protected local data
/// root and is handed directly to the existing signed pricing executor.
/// </summary>
public sealed class TopDispensedWorklistBuilder : ITopDispensedWorklistBuilder
{
    public const int MaximumItems = 500;
    private const int MaximumWindowDays = 3660;

    private readonly AgentOptions _options;
    private readonly ExcelTop500Writer _writer;
    private readonly ExcelPricingReader _reader;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TopDispensedWorklistBuilder> _logger;

    public TopDispensedWorklistBuilder(
        IOptions<AgentOptions> options,
        ExcelTop500Writer writer,
        ExcelPricingReader reader,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _writer = writer;
        _reader = reader;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TopDispensedWorklistBuilder>();
    }

    public async Task<TopDispensedWorklistBuildResult> BuildAsync(
        string commandId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(commandId, "D", out _))
            return TopDispensedWorklistBuildResult.Fail(
                "pricing_worklist_validation_failed");

        var settings = _options.TopDispensed;
        if (settings.TopN is < 1 or > MaximumItems ||
            settings.WindowDays is < 1 or > MaximumWindowDays ||
            settings.DispensedStatusNames.Count == 0)
        {
            return TopDispensedWorklistBuildResult.Fail(
                "pricing_worklist_source_unavailable");
        }

        var root = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent"));
        if (!InstalledDataRootVerifier.IsSafe(root))
            return TopDispensedWorklistBuildResult.Fail(
                "pricing_worklist_source_unavailable");

        var outputDirectory = Path.Combine(root, "pricing", "generated");
        var outputPath = Path.Combine(outputDirectory, $"{commandId}.xlsx");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            if (DirectoryTreeContainsReparsePoint(root, outputDirectory))
                return TopDispensedWorklistBuildResult.Fail(
                    "pricing_worklist_source_unavailable");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "core.pricing_worklist.directory_unavailable exception_type={ExceptionType}",
                exception.GetType().Name);
            return TopDispensedWorklistBuildResult.Fail(
                "pricing_worklist_source_unavailable");
        }

        if (File.Exists(outputPath))
            return ValidatePublished(outputPath, settings.TopN);

        var pharmacy = SelectPharmacy();
        if (pharmacy is null || string.IsNullOrWhiteSpace(pharmacy.SqlServer))
            return TopDispensedWorklistBuildResult.Fail(
                "pricing_worklist_source_unavailable");

        await using var connection = new SqlConnection(BuildConnectionString(pharmacy));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var discovery = new PricingSchemaDiscovery(
                _loggerFactory.CreateLogger<PricingSchemaDiscovery>());
            var spec = await discovery.DiscoverTopDispensedSpecAsync(
                    connection,
                    ColumnOverrides(settings),
                    cancellationToken)
                .ConfigureAwait(false);
            if (spec is null)
                return TopDispensedWorklistBuildResult.Fail(
                    "pricing_worklist_source_unavailable");

            var generator = new SqlTopDispensedGenerator(
                _ => Task.FromResult(connection),
                settings.DispensedStatusNames,
                _loggerFactory.CreateLogger<SqlTopDispensedGenerator>());
            var generated = await generator.GenerateVerifiedAsync(
                    spec,
                    settings.TopN,
                    DateTime.UtcNow.Date.AddDays(-settings.WindowDays),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!generated.Ok)
            {
                _logger.LogWarning(
                    "core.pricing_worklist.generation_failed code={Code}",
                    generated.ErrorCode);
                return TopDispensedWorklistBuildResult.Fail(
                    "pricing_worklist_generation_failed");
            }

            var rows = Canonicalize(generated.Rows, settings.TopN);
            if (rows is null)
                return TopDispensedWorklistBuildResult.Fail(
                    "pricing_worklist_validation_failed");
            if (rows.Count == 0)
                return TopDispensedWorklistBuildResult.Fail(
                    "pricing_worklist_empty");
            if (!_writer.WriteAtomically(outputPath, rows))
                return TopDispensedWorklistBuildResult.Fail(
                    "pricing_worklist_generation_failed");

            return ValidatePublished(outputPath, settings.TopN);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "core.pricing_worklist.source_failed exception_type={ExceptionType}",
                exception.GetType().Name);
            return TopDispensedWorklistBuildResult.Fail(
                "pricing_worklist_source_unavailable");
        }
    }

    private TopDispensedWorklistBuildResult ValidatePublished(
        string path,
        int maximumItems)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return TopDispensedWorklistBuildResult.Fail(
                    "pricing_worklist_validation_failed");
            var read = _reader.Read(path);
            if (!read.Success || read.Invalid.Count != 0 ||
                read.Rows.Count is < 1 || read.Rows.Count > maximumItems)
            {
                return TopDispensedWorklistBuildResult.Fail(
                    "pricing_worklist_validation_failed");
            }
            return TopDispensedWorklistBuildResult.Success(path, read.Rows.Count);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "core.pricing_worklist.validation_failed exception_type={ExceptionType}",
                exception.GetType().Name);
            return TopDispensedWorklistBuildResult.Fail(
                "pricing_worklist_validation_failed");
        }
    }

    internal static IReadOnlyList<TopDispensedRow>? Canonicalize(
        IReadOnlyList<TopDispensedRow> rows,
        int maximumItems)
    {
        var normalized = rows.Select(row => new
        {
            Row = row,
            Ndc = NdcNormalizer.Normalize(row.Ndc),
        }).ToArray();
        if (normalized.Any(value => !value.Ndc.Ok || value.Ndc.Canonical11 is null))
            return null;

        return normalized
            .GroupBy(value => value.Ndc.Canonical11!, StringComparer.Ordinal)
            .Select(group => new TopDispensedRow(
                group.First().Row.DrugName,
                group.First().Row.Strength,
                group.Key,
                group.Sum(value => value.Row.TotalDispensed)))
            .OrderByDescending(row => row.TotalDispensed)
            .ThenBy(row => row.Ndc, StringComparer.Ordinal)
            .Take(maximumItems)
            .ToArray();
    }

    private PharmacyConfig? SelectPharmacy()
    {
        var pharmacies = _options.GetEffectivePharmacies()
            .Where(pharmacy => pharmacy.Enabled)
            .ToArray();
        return pharmacies.FirstOrDefault(pharmacy => string.Equals(
                   pharmacy.PharmacyId,
                   _options.PharmacyId,
                   StringComparison.OrdinalIgnoreCase))
               ?? pharmacies.FirstOrDefault();
    }

    private string BuildConnectionString(PharmacyConfig pharmacy)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = pharmacy.SqlServer,
            InitialCatalog = AdapterCatalog.Resolve(
                pharmacy.SqlDatabase,
                PioneerRxAdapterConfig.Create()),
            ApplicationName = "SuavoAgent.PricingWorklist",
            ConnectTimeout = 30,
            MaxPoolSize = 1,
            MinPoolSize = 0,
        };
        SqlConnectionSecurity.Apply(builder, _options);
        if (string.IsNullOrWhiteSpace(pharmacy.SqlUser))
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = pharmacy.SqlUser;
            builder.Password = pharmacy.SqlPassword;
        }
        return builder.ConnectionString;
    }

    private static TopDispensedColumnOverrides ColumnOverrides(
        TopDispensedOptions options) => new(
        DrugNameColumn: options.DrugNameColumn,
        StrengthColumn: options.StrengthColumn,
        BrandGenericColumn: options.BrandGenericColumn,
        RxOtcColumn: options.RxOtcColumn,
        ScheduleColumn: options.ScheduleColumn,
        GenericValue: options.GenericValue,
        RxValue: options.RxValue,
        NoScheduleValue: options.NoScheduleValue);

    private static bool DirectoryTreeContainsReparsePoint(
        string root,
        string leaf)
    {
        var current = new DirectoryInfo(leaf);
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            if (string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    rootPath,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            current = current.Parent;
        }
        return true;
    }
}
