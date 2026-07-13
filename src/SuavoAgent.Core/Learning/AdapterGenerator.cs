using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Generates a LearnedPmsAdapter from an approved POM.
/// Reads the highest-confidence Rx queue candidate and delivery-ready statuses,
/// builds a parameterized detection query, and wires it into the adapter.
/// </summary>
public sealed class AdapterGenerator
{
    private readonly AgentStateDb _db;
    private const double MinConfidence = 0.6;

    // Only word characters (letters, digits, underscore) with exactly one dot separator
    private static readonly Regex SafeTableNamePattern = new(@"^[\w]+\.[\w]+$", RegexOptions.Compiled);
    private static readonly Regex FirstNamePattern = new(
        @"^(first_?name|fname|given_?name)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LastNamePattern = new(
        @"^(last_?name|lname|surname|family_?name|last_?initial)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PhonePattern = new(
        @"^(phone|phone1|primary_?phone|mobile|mobile_?phone|telephone)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Address1Pattern = new(
        @"^(address1|address_?line1|street_?address|street1)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Address2Pattern = new(
        @"^(address2|address_?line2|street2|unit|suite)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CityPattern = new(
        @"^(city|locality)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StatePattern = new(
        @"^(state|state_?code|province)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ZipPattern = new(
        @"^(zip|zip_?code|zipcode|postal_?code)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public AdapterGenerator(AgentStateDb db) => _db = db;

    public LearnedPmsAdapter? Generate(string sessionId, string? connectionString = null,
        ILogger? logger = null)
    {
        var template = Describe(sessionId);
        return template is null
            ? null
            : Generate(template, connectionString, logger);
    }

    /// <summary>
    /// Builds the exact, digest-bound read template that an operator reviews.
    /// The status list is sorted so equivalent observations produce one stable
    /// digest across restarts.
    /// </summary>
    public LearnedPmsAdapterTemplate? Describe(string sessionId)
    {
        var snapshot = _db.GetCompleteDiscoveredSchemaSnapshot(sessionId);
        if (snapshot is null ||
            !IsSha256(snapshot.Value.SourceIdentityDigest) ||
            !IsSha256(snapshot.Value.SchemaContractDigest) ||
            !string.Equals(
                snapshot.Value.SchemaContractDigest,
                _db.ComputeDiscoveredSchemaContractDigest(sessionId),
                StringComparison.Ordinal))
            return null;
        var schema = _db.GetDiscoveredSchemaGraph(sessionId);
        var uniqueColumns = _db.GetDiscoveredUniqueColumns(sessionId);
        var candidates = _db.GetRxQueueCandidates(sessionId);
        var best = candidates.FirstOrDefault(c => c.Confidence >= MinConfidence);

        if (best.PrimaryTable is null)
            return null;

        if (string.IsNullOrEmpty(best.RxNumberColumn) || string.IsNullOrEmpty(best.StatusColumn))
            return null;
        if (!SafeTableNamePattern.IsMatch(best.PrimaryTable)) return null;
        var sourceParts = best.PrimaryTable.Split('.');
        var sourceColumns = schema.Where(column =>
                string.Equals(column.SchemaName, sourceParts[0], StringComparison.Ordinal) &&
                string.Equals(column.TableName, sourceParts[1], StringComparison.Ordinal))
            .ToArray();
        if (!HasExactlyOneColumn(sourceColumns, best.RxNumberColumn) ||
            !HasExactlyOneColumn(sourceColumns, best.StatusColumn) ||
            best.DateColumn is not null && !HasExactlyOneColumn(sourceColumns, best.DateColumn) ||
            !uniqueColumns.Contains($"{best.PrimaryTable}.{best.RxNumberColumn}"))
            return null;
        var rxNumberDataType = sourceColumns.Single(column =>
            string.Equals(column.ColumnName, best.RxNumberColumn, StringComparison.Ordinal)).DataType;
        if (!LearnedPmsAdapter.SupportsCursorDataType(rxNumberDataType)) return null;

        var statuses = _db.GetDiscoveredStatusesForTable(sessionId, best.PrimaryTable);
        var deliveryReady = StatusOrderingEngine.GetDeliveryReadyValues(statuses)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        if (deliveryReady.Length == 0)
            return null;

        var result = BuildDetectionQuery(best.PrimaryTable, best.RxNumberColumn,
            best.StatusColumn, best.DateColumn, deliveryReady, rxNumberDataType);

        if (result is null)
            return null;

        var detectionValidation = BuildDetectionValidation(
            best.PrimaryTable,
            best.RxNumberColumn,
            best.StatusColumn,
            best.DateColumn,
            sourceColumns);
        if (detectionValidation is null) return null;

        var patientLookup = BuildPatientLookupTemplate(
            best.PrimaryTable,
            best.RxNumberColumn,
            best.PatientFkColumn,
            schema,
            uniqueColumns);
        var pmsName = $"Learned-{best.PrimaryTable}";
        var digest = ComputeTemplateDigest(
            sessionId,
            snapshot.Value.SourceIdentityDigest,
            snapshot.Value.DatabaseName,
            snapshot.Value.SchemaContractDigest,
            pmsName,
            result.Value.Query,
            result.Value.Parameters,
            detectionValidation.Value.Query,
            detectionValidation.Value.Parameters,
            best.RxNumberColumn,
            rxNumberDataType,
            best.StatusColumn,
            deliveryReady,
            patientLookup);

        return new LearnedPmsAdapterTemplate(
            SessionId: sessionId,
            TemplateDigest: digest,
            SourceIdentityDigest: snapshot.Value.SourceIdentityDigest,
            DatabaseName: snapshot.Value.DatabaseName,
            SchemaContractDigest: snapshot.Value.SchemaContractDigest,
            PmsName: pmsName,
            DetectionQuery: result.Value.Query,
            StatusParameters: result.Value.Parameters,
            DetectionValidationQuery: detectionValidation.Value.Query,
            DetectionValidationParameters: detectionValidation.Value.Parameters,
            RxNumberColumn: best.RxNumberColumn,
            RxNumberDataType: rxNumberDataType,
            StatusColumn: best.StatusColumn,
            DeliveryReadyStatuses: deliveryReady,
            PatientLookupQuery: patientLookup?.Query,
            PatientLookupValidationQuery: patientLookup?.ValidationQuery,
            PatientLookupValidationParameters: patientLookup?.ValidationParameters);
    }

    public static LearnedPmsAdapter Generate(
        LearnedPmsAdapterTemplate template,
        string? connectionString = null,
        ILogger? logger = null,
        string? sourceIdentitySalt = null,
        bool trustServerCertificate = false,
        string? serverCertificateSha256 = null) =>
        new(
            pmsName: template.PmsName,
            connectionString: connectionString ?? "",
            detectionQuery: template.DetectionQuery,
            statusParameters: template.StatusParameters,
            detectionValidationQuery: template.DetectionValidationQuery,
            detectionValidationParameters: template.DetectionValidationParameters,
            rxNumberColumn: template.RxNumberColumn,
            rxNumberDataType: template.RxNumberDataType,
            statusColumn: template.StatusColumn,
            deliveryReadyStatuses: template.DeliveryReadyStatuses,
            patientLookupQuery: template.PatientLookupQuery,
            patientLookupValidationQuery: template.PatientLookupValidationQuery,
            patientLookupValidationParameters: template.PatientLookupValidationParameters,
            expectedSourceIdentityDigest: template.SourceIdentityDigest,
            expectedDatabaseName: template.DatabaseName,
            sourceIdentitySalt: sourceIdentitySalt,
            trustServerCertificate: trustServerCertificate,
            serverCertificateSha256: serverCertificateSha256,
            logger: logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    private static string ComputeTemplateDigest(
        string sessionId,
        string sourceIdentityDigest,
        string databaseName,
        string schemaContractDigest,
        string pmsName,
        string query,
        IReadOnlyDictionary<string, string> parameters,
        string detectionValidationQuery,
        IReadOnlyDictionary<string, string> detectionValidationParameters,
        string rxNumberColumn,
        string rxNumberDataType,
        string statusColumn,
        IReadOnlyList<string> deliveryReadyStatuses,
        PatientLookupTemplate? patientLookup)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            sessionId,
            sourceIdentityDigest,
            databaseName,
            schemaContractDigest,
            pmsName,
            query,
            parameters = parameters
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new[] { pair.Key, pair.Value }),
            detectionValidationQuery,
            detectionValidationParameters = detectionValidationParameters
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new[] { pair.Key, pair.Value }),
            rxNumberColumn,
            rxNumberDataType,
            statusColumn,
            deliveryReadyStatuses,
            patientLookup,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    /// <summary>
    /// Result of building a parameterized detection query.
    /// Query contains @s0, @s1, ... placeholders; Parameters maps names to values.
    /// </summary>
    public readonly record struct ParameterizedQuery(
        string Query,
        IReadOnlyDictionary<string, string> Parameters);

    internal readonly record struct PatientLookupTemplate(
        string Query,
        string ValidationQuery,
        IReadOnlyDictionary<string, string> ValidationParameters);

    /// <summary>
    /// Bracket-escapes a SQL identifier by replacing ] with ]] and wrapping in [].
    /// </summary>
    internal static string BracketEscape(string identifier)
        => $"[{identifier.Replace("]", "]]")}]";

    internal static ParameterizedQuery? BuildDetectionQuery(string table, string rxNumberColumn,
        string statusColumn, string? dateColumn, IReadOnlyList<string> statusValues,
        string rxNumberDataType = "nvarchar")
    {
        // Validate table name: must be schema.table with only word characters
        if (!SafeTableNamePattern.IsMatch(table))
            return null;
        if (statusValues.Count is 0 or > 128) return null;

        var parts = table.Split('.');
        var safeTable = $"{BracketEscape(parts[0])}.{BracketEscape(parts[1])}";

        var sb = new StringBuilder();
        if (!LearnedPmsAdapter.SupportsCursorDataType(rxNumberDataType)) return null;

        sb.AppendLine("SELECT TOP (@pageSize)");
        sb.AppendLine($"    {BracketEscape(rxNumberColumn)}, {BracketEscape(statusColumn)}");
        if (dateColumn != null)
            sb.AppendLine($"    , {BracketEscape(dateColumn)}");
        sb.AppendLine($"FROM {safeTable}");

        // Generate parameter placeholders instead of inline values
        var parameters = new Dictionary<string, string>(statusValues.Count);
        var placeholders = new string[statusValues.Count];
        for (var i = 0; i < statusValues.Count; i++)
        {
            var paramName = $"@s{i}";
            placeholders[i] = paramName;
            parameters[paramName] = statusValues[i];
        }

        sb.Append($"WHERE {BracketEscape(statusColumn)} IN (");
        sb.AppendJoin(", ", placeholders);
        sb.AppendLine(")");
        sb.AppendLine($"  AND (@cursor IS NULL OR {BracketEscape(rxNumberColumn)} > @cursor)");
        sb.Append($"ORDER BY {BracketEscape(rxNumberColumn)} ASC");

        return new ParameterizedQuery(sb.ToString(), parameters);
    }

    private static ParameterizedQuery? BuildDetectionValidation(
        string sourceTable,
        string rxNumberColumn,
        string statusColumn,
        string? dateColumn,
        IReadOnlyList<(string SchemaName, string TableName, string ColumnName,
            string DataType, bool IsPk, bool IsFk, string? FkTargetTable,
            string? FkTargetColumn, string? InferredPurpose)> sourceColumns)
    {
        if (!SafeTableNamePattern.IsMatch(sourceTable)) return null;
        var parts = sourceTable.Split('.');
        var requiredNames = new[] { rxNumberColumn, statusColumn }
            .Concat(dateColumn is null ? [] : [dateColumn])
            .ToArray();
        var required = requiredNames.Select(name => sourceColumns.SingleOrDefault(column =>
                string.Equals(column.ColumnName, name, StringComparison.Ordinal)))
            .ToArray();
        if (required.Any(column => string.IsNullOrWhiteSpace(column.ColumnName))) return null;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["@sourceSchema"] = parts[0],
            ["@sourceTable"] = parts[1],
            ["@sourceRxColumn"] = rxNumberColumn,
        };
        var predicates = new List<string>();
        for (var i = 0; i < required.Length; i++)
        {
            parameters[$"@sourceColumn{i}"] = required[i].ColumnName;
            parameters[$"@sourceType{i}"] = required[i].DataType.ToLowerInvariant();
            predicates.Add(
                $"(source_column.name = @sourceColumn{i} AND LOWER(TYPE_NAME(source_column.system_type_id)) = @sourceType{i})");
        }

        var query = $"""
            SELECT CASE WHEN
              (SELECT COUNT(*)
               FROM sys.columns AS source_column
               WHERE source_column.object_id = OBJECT_ID(
                   QUOTENAME(@sourceSchema) + '.' + QUOTENAME(@sourceTable))
                 AND ({string.Join(" OR ", predicates)})) = {required.Length}
              AND EXISTS (
                SELECT 1
                FROM sys.indexes AS source_unique
                INNER JOIN sys.index_columns AS source_unique_column
                  ON source_unique_column.object_id = source_unique.object_id
                 AND source_unique_column.index_id = source_unique.index_id
                 AND source_unique_column.key_ordinal > 0
                WHERE source_unique.object_id = OBJECT_ID(
                    QUOTENAME(@sourceSchema) + '.' + QUOTENAME(@sourceTable))
                  AND source_unique.is_unique = 1
                  AND source_unique.is_disabled = 0
                  AND source_unique.is_hypothetical = 0
                  AND source_unique.has_filter = 0
                  AND COL_NAME(source_unique_column.object_id, source_unique_column.column_id) = @sourceRxColumn
                  AND 1 = (
                    SELECT COUNT(*) FROM sys.index_columns AS source_key_component
                    WHERE source_key_component.object_id = source_unique.object_id
                      AND source_key_component.index_id = source_unique.index_id
                      AND source_key_component.key_ordinal > 0))
              THEN 1 ELSE 0 END
            """;
        return new ParameterizedQuery(query, parameters);
    }

    /// <summary>
    /// Builds a one-Rx, parameterized patient query only when the learned schema contains an
    /// exact SQL Server foreign-key edge from the approved Rx table to one unambiguous patient
    /// table. Column-name heuristics may label fields only after that database-enforced edge is
    /// proven; heuristics alone can never authorize a PHI-bearing join.
    /// </summary>
    internal static PatientLookupTemplate? BuildPatientLookupTemplate(
        string rxTable,
        string rxNumberColumn,
        string? patientFkColumn,
        IReadOnlyList<(string SchemaName, string TableName, string ColumnName,
            string DataType, bool IsPk, bool IsFk, string? FkTargetTable,
            string? FkTargetColumn, string? InferredPurpose)> schema,
        IReadOnlySet<string> uniqueColumns)
    {
        if (!SafeTableNamePattern.IsMatch(rxTable) ||
            string.IsNullOrWhiteSpace(patientFkColumn))
            return null;

        var rxParts = rxTable.Split('.');
        var foreignKeys = schema.Where(column =>
                string.Equals(column.SchemaName, rxParts[0], StringComparison.Ordinal) &&
                string.Equals(column.TableName, rxParts[1], StringComparison.Ordinal) &&
                string.Equals(column.ColumnName, patientFkColumn, StringComparison.Ordinal) &&
                column.IsFk &&
                !string.IsNullOrWhiteSpace(column.FkTargetTable) &&
                !string.IsNullOrWhiteSpace(column.FkTargetColumn))
            .ToArray();
        if (foreignKeys.Length != 1 ||
            !SafeTableNamePattern.IsMatch(foreignKeys[0].FkTargetTable!))
            return null;

        var targetParts = foreignKeys[0].FkTargetTable!.Split('.');
        var allTargetColumns = schema.Where(column =>
                string.Equals(column.SchemaName, targetParts[0], StringComparison.Ordinal) &&
                string.Equals(column.TableName, targetParts[1], StringComparison.Ordinal))
            .ToArray();
        if (!allTargetColumns.Any(column =>
                string.Equals(column.ColumnName, foreignKeys[0].FkTargetColumn, StringComparison.Ordinal)))
            return null;
        if (!uniqueColumns.Contains($"{rxTable}.{rxNumberColumn}") ||
            !uniqueColumns.Contains(
                $"{foreignKeys[0].FkTargetTable}.{foreignKeys[0].FkTargetColumn}"))
            return null;
        var targetColumns = allTargetColumns.Where(column => IsTextType(column.DataType)).ToArray();

        var firstName = MatchUnique(targetColumns, FirstNamePattern);
        var lastName = MatchUnique(targetColumns, LastNamePattern);
        var phone = MatchUnique(targetColumns, PhonePattern);
        var address1 = MatchUnique(targetColumns, Address1Pattern);
        var address2 = MatchUnique(targetColumns, Address2Pattern, optional: true);
        var city = MatchUnique(targetColumns, CityPattern);
        var state = MatchUnique(targetColumns, StatePattern);
        var zip = MatchUnique(targetColumns, ZipPattern);
        if (firstName is null || firstName == AmbiguousColumn ||
            lastName is null || lastName == AmbiguousColumn ||
            phone is null || phone == AmbiguousColumn ||
            address1 is null || address1 == AmbiguousColumn ||
            city is null || city == AmbiguousColumn ||
            state is null || state == AmbiguousColumn ||
            zip is null || zip == AmbiguousColumn ||
            address2 == AmbiguousColumn)
            return null;

        var safeRxTable = $"{BracketEscape(rxParts[0])}.{BracketEscape(rxParts[1])}";
        var safePatientTable = $"{BracketEscape(targetParts[0])}.{BracketEscape(targetParts[1])}";
        var lastExpression = LastNamePattern.IsMatch(lastName) &&
                             lastName.Contains("initial", StringComparison.OrdinalIgnoreCase)
            ? $"patient.{BracketEscape(lastName)}"
            : $"LEFT(patient.{BracketEscape(lastName)}, 1)";
        var address2Expression = address2 is null
            ? "CAST(NULL AS nvarchar(1))"
            : $"patient.{BracketEscape(address2)}";

        var query = $"""
            SELECT TOP 2
                patient.{BracketEscape(firstName)} AS [FirstName],
                {lastExpression} AS [LastInitial],
                patient.{BracketEscape(phone)} AS [Phone],
                patient.{BracketEscape(address1)} AS [Address1],
                {address2Expression} AS [Address2],
                patient.{BracketEscape(city)} AS [City],
                patient.{BracketEscape(state)} AS [State],
                patient.{BracketEscape(zip)} AS [Zip]
            FROM {safeRxTable} AS rx
            INNER JOIN {safePatientTable} AS patient
                ON rx.{BracketEscape(patientFkColumn)} = patient.{BracketEscape(foreignKeys[0].FkTargetColumn!)}
            WHERE rx.{BracketEscape(rxNumberColumn)} = @rx
            """;

        var validationParameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["@sourceSchema"] = rxParts[0],
            ["@sourceTable"] = rxParts[1],
            ["@sourceRxColumn"] = rxNumberColumn,
            ["@sourceFkColumn"] = patientFkColumn,
            ["@targetSchema"] = targetParts[0],
            ["@targetTable"] = targetParts[1],
            ["@targetKeyColumn"] = foreignKeys[0].FkTargetColumn!,
        };
        var mappedColumns = new[] { firstName, lastName, phone, address1, city, state, zip }
            .Concat(address2 is null ? [] : [address2])
            .Select(name => allTargetColumns.Single(column =>
                string.Equals(column.ColumnName, name, StringComparison.Ordinal)))
            .ToArray();
        var mappedPredicates = new List<string>();
        for (var i = 0; i < mappedColumns.Length; i++)
        {
            validationParameters[$"@targetField{i}"] = mappedColumns[i].ColumnName;
            validationParameters[$"@targetType{i}"] = mappedColumns[i].DataType.ToLowerInvariant();
            mappedPredicates.Add(
                $"(target_column.name = @targetField{i} AND LOWER(TYPE_NAME(target_column.system_type_id)) = @targetType{i})");
        }
        var validationQuery = $"""
            SELECT CASE WHEN
              EXISTS (
                SELECT 1
                FROM sys.foreign_key_columns AS fkc
                INNER JOIN sys.foreign_keys AS fk ON fk.object_id = fkc.constraint_object_id
                WHERE fk.is_disabled = 0
                  AND fk.is_not_trusted = 0
                  AND fkc.parent_object_id = OBJECT_ID(
                      QUOTENAME(@sourceSchema) + '.' + QUOTENAME(@sourceTable))
                  AND fkc.referenced_object_id = OBJECT_ID(
                      QUOTENAME(@targetSchema) + '.' + QUOTENAME(@targetTable))
                  AND COL_NAME(fkc.parent_object_id, fkc.parent_column_id) = @sourceFkColumn
                  AND COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) = @targetKeyColumn
                  AND 1 = (SELECT COUNT(*) FROM sys.foreign_key_columns AS fk_component
                           WHERE fk_component.constraint_object_id = fkc.constraint_object_id))
              AND EXISTS (
                SELECT 1 FROM sys.indexes AS source_unique
                INNER JOIN sys.index_columns AS source_unique_column
                  ON source_unique_column.object_id = source_unique.object_id
                 AND source_unique_column.index_id = source_unique.index_id
                 AND source_unique_column.key_ordinal > 0
                WHERE source_unique.object_id = OBJECT_ID(
                    QUOTENAME(@sourceSchema) + '.' + QUOTENAME(@sourceTable))
                  AND source_unique.is_unique = 1
                  AND source_unique.is_disabled = 0
                  AND source_unique.is_hypothetical = 0
                  AND source_unique.has_filter = 0
                  AND COL_NAME(source_unique_column.object_id, source_unique_column.column_id) = @sourceRxColumn
                  AND 1 = (SELECT COUNT(*) FROM sys.index_columns AS source_component
                           WHERE source_component.object_id = source_unique.object_id
                             AND source_component.index_id = source_unique.index_id
                             AND source_component.key_ordinal > 0))
              AND EXISTS (
                SELECT 1 FROM sys.indexes AS target_unique
                INNER JOIN sys.index_columns AS target_unique_column
                  ON target_unique_column.object_id = target_unique.object_id
                 AND target_unique_column.index_id = target_unique.index_id
                 AND target_unique_column.key_ordinal > 0
                WHERE target_unique.object_id = OBJECT_ID(
                    QUOTENAME(@targetSchema) + '.' + QUOTENAME(@targetTable))
                  AND target_unique.is_unique = 1
                  AND target_unique.is_disabled = 0
                  AND target_unique.is_hypothetical = 0
                  AND target_unique.has_filter = 0
                  AND COL_NAME(target_unique_column.object_id, target_unique_column.column_id) = @targetKeyColumn
                  AND 1 = (SELECT COUNT(*) FROM sys.index_columns AS target_component
                           WHERE target_component.object_id = target_unique.object_id
                             AND target_component.index_id = target_unique.index_id
                             AND target_component.key_ordinal > 0))
              AND (SELECT COUNT(*) FROM sys.columns AS target_column
                   WHERE target_column.object_id = OBJECT_ID(
                       QUOTENAME(@targetSchema) + '.' + QUOTENAME(@targetTable))
                     AND ({string.Join(" OR ", mappedPredicates)})) = {mappedColumns.Length}
              THEN 1 ELSE 0 END
            """;
        return new PatientLookupTemplate(query, validationQuery, validationParameters);
    }

    private const string AmbiguousColumn = "\0ambiguous";

    private static string? MatchUnique(
        IReadOnlyList<(string SchemaName, string TableName, string ColumnName,
            string DataType, bool IsPk, bool IsFk, string? FkTargetTable,
            string? FkTargetColumn, string? InferredPurpose)> columns,
        Regex pattern,
        bool optional = false)
    {
        var matches = columns
            .Where(column => pattern.IsMatch(column.ColumnName))
            .Select(column => column.ColumnName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 when optional => null,
            0 => null,
            _ => AmbiguousColumn,
        };
    }

    private static bool IsTextType(string dataType) => dataType.ToLowerInvariant() is
        "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext";

    private static bool HasExactlyOneColumn(
        IReadOnlyList<(string SchemaName, string TableName, string ColumnName,
            string DataType, bool IsPk, bool IsFk, string? FkTargetTable,
            string? FkTargetColumn, string? InferredPurpose)> columns,
        string name) =>
        columns.Count(column => string.Equals(column.ColumnName, name, StringComparison.Ordinal)) == 1;

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Immutable learned SQL read template. Its digest includes the learning
/// session and every query-affecting value.
/// </summary>
public sealed record LearnedPmsAdapterTemplate(
    string SessionId,
    string TemplateDigest,
    string SourceIdentityDigest,
    string DatabaseName,
    string SchemaContractDigest,
    string PmsName,
    string DetectionQuery,
    IReadOnlyDictionary<string, string> StatusParameters,
    string DetectionValidationQuery,
    IReadOnlyDictionary<string, string> DetectionValidationParameters,
    string RxNumberColumn,
    string RxNumberDataType,
    string StatusColumn,
    IReadOnlyList<string> DeliveryReadyStatuses,
    string? PatientLookupQuery,
    string? PatientLookupValidationQuery,
    IReadOnlyDictionary<string, string>? PatientLookupValidationParameters);
