using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class SqlTokenizerTests
{
    [Fact]
    public void Normalize_ParameterizedQuery_ExtractsShape()
    {
        var sql = "SELECT RxNumber, Status FROM Prescription.Rx WHERE PatientID = @p1 AND DateFilled > @p2";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
        Assert.DoesNotContain("@p1", result.NormalizedShape);
    }

    [Fact]
    public void Normalize_LiteralValues_Discards()
    {
        // Fail-closed: literals may contain PHI (patient names, DOBs)
        var sql = "SELECT * FROM Person.Patient WHERE LastName = 'Smith'";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.Null(result); // DISCARD — contains string literal
    }

    [Fact]
    public void Normalize_NumericLiteral_Discards()
    {
        var sql = "SELECT * FROM Prescription.Rx WHERE RxNumber = 12345";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.Null(result); // DISCARD — contains numeric literal that could be MRN/Rx
    }

    [Fact]
    public void Normalize_SelectStar_ExtractsTables()
    {
        var sql = "SELECT * FROM Prescription.RxTransaction rt JOIN RxLocal.ActiveRx a ON rt.RxID = a.RxID";
        // This has no literals, so it should pass
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.RxTransaction", result!.TablesReferenced);
        Assert.Contains("RxLocal.ActiveRx", result.TablesReferenced);
    }

    [Fact]
    public void Normalize_InsertStatement_ExtractsTableAndType()
    {
        var sql = "INSERT INTO Prescription.Rx (Col1) VALUES (@p1)";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
        Assert.True(result.IsWrite);
    }

    [Fact]
    public void Normalize_MalformedSql_ReturnsNull()
    {
        Assert.Null(SqlTokenizer.TryNormalize("NOT VALID SQL AT ALL !!!"));
        Assert.Null(SqlTokenizer.TryNormalize(""));
        Assert.Null(SqlTokenizer.TryNormalize(null!));
    }

    [Fact]
    public void Normalize_DdlStatement_Discards()
    {
        Assert.Null(SqlTokenizer.TryNormalize("DROP TABLE Prescription.Rx"));
        Assert.Null(SqlTokenizer.TryNormalize("CREATE TABLE Test (id INT)"));
        Assert.Null(SqlTokenizer.TryNormalize("ALTER TABLE Rx ADD Col INT"));
    }

    [Fact]
    public void Normalize_ExecStatement_Discards()
    {
        Assert.Null(SqlTokenizer.TryNormalize("EXEC sp_GetPatient @id = 123"));
    }
}
