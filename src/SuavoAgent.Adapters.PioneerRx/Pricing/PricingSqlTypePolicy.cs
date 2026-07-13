using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Adapters.PioneerRx.Pricing;

/// <summary>Exact SQL Server type admission and parameter binding for money/classification reads.</summary>
internal static class PricingSqlTypePolicy
{
    internal static bool IsExactNumeric(PricingSqlColumnShape? shape) =>
        shape is not null && shape.DataType.ToLowerInvariant() is
            "decimal" or "numeric" or "money" or "smallmoney";

    internal static bool TryGetBoundedTextParameter(
        PricingSqlColumnShape? shape,
        int minimumCharacters,
        int maximumCharacters,
        out SqlDbType type,
        out int size)
    {
        type = default;
        size = 0;
        if (shape is null || shape.MaxLength is not > 0) return false;

        var dataType = shape.DataType.ToLowerInvariant();
        var bytes = shape.MaxLength.Value;
        switch (dataType)
        {
            case "varchar": type = SqlDbType.VarChar; size = bytes; break;
            case "char": type = SqlDbType.Char; size = bytes; break;
            case "nvarchar" when bytes % 2 == 0: type = SqlDbType.NVarChar; size = bytes / 2; break;
            case "nchar" when bytes % 2 == 0: type = SqlDbType.NChar; size = bytes / 2; break;
            default: return false;
        }

        return size >= minimumCharacters && size <= maximumCharacters;
    }

    internal static bool TryAddClassificationParameter(
        SqlCommand command,
        string name,
        string value,
        PricingSqlColumnShape? shape,
        int maximumCharacters)
    {
        if (!TryGetBoundedTextParameter(
                shape,
                minimumCharacters: value.Length,
                maximumCharacters,
                out var type,
                out var size))
            return false;
        var parameter = command.Parameters.Add(name, type, size);
        parameter.Value = value;
        return true;
    }

    internal static bool TryAddTextOrIntegerParameter(
        SqlCommand command,
        string name,
        string value,
        PricingSqlColumnShape? shape,
        int maximumCharacters)
    {
        if (!TryGetTextOrIntegerParameter(
                value,
                shape,
                maximumCharacters,
                out var type,
                out var size,
                out var parsed))
            return false;
        var parameter = size > 0
            ? command.Parameters.Add(name, type, size)
            : command.Parameters.Add(name, type);
        parameter.Value = parsed;
        return true;
    }

    internal static bool TryGetTextOrIntegerParameter(
        string value,
        PricingSqlColumnShape? shape,
        int maximumCharacters,
        out SqlDbType type,
        out int size,
        out object parsed)
    {
        if (TryGetBoundedTextParameter(
                shape,
                value.Length,
                maximumCharacters,
                out type,
                out size))
        {
            parsed = value;
            return true;
        }
        parsed = value;
        size = 0;
        if (shape is null) return false;

        switch (shape.DataType.ToLowerInvariant())
        {
            case "tinyint" when byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var tiny):
                parsed = tiny; type = SqlDbType.TinyInt; break;
            case "smallint" when short.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var small):
                parsed = small; type = SqlDbType.SmallInt; break;
            case "int" when int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer):
                parsed = integer; type = SqlDbType.Int; break;
            case "bigint" when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var big):
                parsed = big; type = SqlDbType.BigInt; break;
            default:
                return false;
        }
        return true;
    }
}
