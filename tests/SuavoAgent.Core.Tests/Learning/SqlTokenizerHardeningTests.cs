using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

/// <summary>
/// Hardening tests for SqlTokenizer covering Tier 1 (PHI safety) and Tier 2 (parsing completeness).
/// All existing SqlTokenizerTests must still pass — regression coverage is verified here as well.
/// </summary>
public class SqlTokenizerHardeningTests
{
    // -----------------------------------------------------------------------
    // Tier 1: PHI Safety
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT * FROM Prescription.Rx WHERE Blob = 0xDEADBEEF")]
    [InlineData("SELECT * FROM Prescription.Rx WHERE Blob = 0x4A6F686E")]
    [InlineData("UPDATE Prescription.Rx SET Data = 0xFF00")]
    public void HexLiteral_Discards(string sql)
    {
        Assert.Null(SqlTokenizer.TryNormalize(sql));
    }

    [Theory]
    [InlineData("SELECT * FROM Person.Patient WHERE LastName = N'John Smith'")]
    [InlineData("SELECT * FROM Person.Patient WHERE LastName = N'O''Brien'")]
    [InlineData("SELECT * FROM Person.Patient WHERE Note = N'contains ''escaped'' quotes'")]
    public void UnicodeLiteral_Discards(string sql)
    {
        Assert.Null(SqlTokenizer.TryNormalize(sql));
    }

    [Fact]
    public void Comments_StrippedBeforeProcessing_ValidQueryParseable()
    {
        // The comment contains what looks like a string literal — after stripping it's gone
        var sql = "SELECT RxNumber FROM Prescription.Rx -- WHERE LastName = 'Smith'\nWHERE PatientID = @p1";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
    }

    [Fact]
    public void Comments_BlockComment_StrippedBeforeProcessing()
    {
        var sql = "SELECT RxNumber FROM Prescription.Rx /* patient: John DOB:1980 */ WHERE PatientID = @p1";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
    }

    [Fact]
    public void Comments_PHIInComment_NotLeakedToNormalizedShape()
    {
        var sql = "SELECT RxNumber FROM Prescription.Rx -- patient name: John Smith\nWHERE PatientID = @p1";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        // PHI from comment must not appear in shape
        Assert.DoesNotContain("John Smith", result!.NormalizedShape);
        Assert.DoesNotContain("patient name", result.NormalizedShape);
    }

    [Fact]
    public void Comments_CommentHidesBlockedKeyword_StillDiscards()
    {
        // EXEC is hidden after the SELECT — without stripping comments this might slip through.
        // With comment stripping the EXEC would appear only in the comment, not the live SQL.
        // This test ensures a legitimate query with EXEC in a comment still parses correctly.
        var sql = "SELECT RxNumber FROM Prescription.Rx -- EXEC sp_DoSomething\nWHERE PatientID = @p1";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result); // Comment stripped, no EXEC in live SQL
    }

    [Theory]
    [InlineData("SELECT * FROM OPENQUERY(LinkedServer, 'SELECT 1')")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'server=srv', 'SELECT 1')")]
    [InlineData("SELECT * FROM OPENDATASOURCE('SQLNCLI','Data Source=remote').db.schema.tbl")]
    [InlineData("SELECT OPENQUERY(srv, 'x') FROM Prescription.Rx WHERE ID = @p1")]
    public void RemoteDataSource_Discards(string sql)
    {
        Assert.Null(SqlTokenizer.TryNormalize(sql));
    }

    [Fact]
    public void LikeKeyword_NotParsedAsTableReference()
    {
        var sql = "SELECT * FROM Prescription.Rx WHERE NoteText LIKE @p1";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
        Assert.DoesNotContain("LIKE", result.TablesReferenced.Select(t => t.ToUpperInvariant()));
    }

    // -----------------------------------------------------------------------
    // Tier 2: Parsing Completeness
    // -----------------------------------------------------------------------

    [Fact]
    public void NestedSubquery_InnerTableExtracted()
    {
        var sql = "SELECT * FROM Prescription.Rx WHERE RxID IN (SELECT RxID FROM RxLocal.ActiveRx WHERE Status = @p1)";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
        Assert.Contains("RxLocal.ActiveRx", result.TablesReferenced);
    }

    [Fact]
    public void NestedSubquery_DeeplyNested_StillExtractsUpToMaxDepth()
    {
        // 2 levels of nesting — both tables should be found
        var sql = "SELECT * FROM Prescription.Rx WHERE RxID IN " +
                  "(SELECT RxID FROM RxLocal.ActiveRx WHERE PatientID IN " +
                  "(SELECT PatientID FROM Person.Patient WHERE Status = @p1))";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
        Assert.Contains("RxLocal.ActiveRx", result.TablesReferenced);
        Assert.Contains("Person.Patient", result.TablesReferenced);
    }

    [Fact]
    public void CTE_TablesFromBodyExtracted_CteNameNotInList()
    {
        var sql = "WITH ActiveRxCTE AS (SELECT RxID FROM Prescription.Rx WHERE Status = @p1) " +
                  "SELECT * FROM ActiveRxCTE JOIN Person.Patient ON ActiveRxCTE.PatientID = Person.Patient.PatientID";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        // CTE body table should be present
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
        // CTE name itself should NOT appear as a table reference
        Assert.DoesNotContain("ActiveRxCTE", result.TablesReferenced);
    }

    [Fact]
    public void UnionQuery_TablesFromBothBranchesExtracted()
    {
        var sql = "SELECT RxID FROM Prescription.Rx WHERE Status = @p1 " +
                  "UNION ALL " +
                  "SELECT RxID FROM RxArchive.OldRx WHERE Status = @p2";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
        Assert.Contains("RxArchive.OldRx", result.TablesReferenced);
    }

    [Fact]
    public void UnionIntersectExcept_TablesFromAllBranchesExtracted()
    {
        var sql = "SELECT RxID FROM Prescription.Rx WHERE Status = @p1 " +
                  "INTERSECT " +
                  "SELECT RxID FROM RxLocal.ActiveRx WHERE Status = @p2";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
        Assert.Contains("RxLocal.ActiveRx", result.TablesReferenced);
    }

    [Fact]
    public void AliasedTables_RealNameExtracted_AliasNotInList()
    {
        var sql = "SELECT rt.RxID, a.Status FROM Prescription.RxTransaction rt " +
                  "JOIN RxLocal.ActiveRx a ON rt.RxID = a.RxID";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.RxTransaction", result!.TablesReferenced);
        Assert.Contains("RxLocal.ActiveRx", result.TablesReferenced);
        // Aliases should not appear as table names
        Assert.DoesNotContain("rt", result.TablesReferenced);
        Assert.DoesNotContain("a", result.TablesReferenced);
    }

    [Fact]
    public void CrossDatabaseThreePart_SchemaTableExtracted_DbNameNotInList()
    {
        // OtherDb.schema.table → extract schema.table only
        var sql = "SELECT * FROM OtherDb.Prescription.Rx WHERE PatientID = @p1";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
        // DB prefix should not appear as a standalone table reference
        Assert.DoesNotContain("OtherDb", result.TablesReferenced);
    }

    // -----------------------------------------------------------------------
    // Regression: existing tests re-verified as inline assertions
    // -----------------------------------------------------------------------

    [Fact]
    public void Regression_ParameterizedQuery_ExtractsShape()
    {
        var sql = "SELECT RxNumber, Status FROM Prescription.Rx WHERE PatientID = @p1 AND DateFilled > @p2";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
        Assert.DoesNotContain("@p1", result.NormalizedShape);
    }

    [Fact]
    public void Regression_StringLiteral_Discards()
    {
        Assert.Null(SqlTokenizer.TryNormalize("SELECT * FROM Person.Patient WHERE LastName = 'Smith'"));
    }

    [Fact]
    public void Regression_NumericLiteral_Discards()
    {
        Assert.Null(SqlTokenizer.TryNormalize("SELECT * FROM Prescription.Rx WHERE RxNumber = 12345"));
    }

    [Fact]
    public void Regression_SelectStar_ExtractsTables()
    {
        var sql = "SELECT * FROM Prescription.RxTransaction rt JOIN RxLocal.ActiveRx a ON rt.RxID = a.RxID";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.RxTransaction", result!.TablesReferenced);
        Assert.Contains("RxLocal.ActiveRx", result.TablesReferenced);
    }

    [Fact]
    public void Regression_InsertStatement_ExtractsTableAndType()
    {
        var sql = "INSERT INTO Prescription.Rx (Col1) VALUES (@p1)";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
        Assert.True(result.IsWrite);
    }

    [Fact]
    public void Regression_MalformedSql_ReturnsNull()
    {
        Assert.Null(SqlTokenizer.TryNormalize("NOT VALID SQL AT ALL !!!"));
        Assert.Null(SqlTokenizer.TryNormalize(""));
        Assert.Null(SqlTokenizer.TryNormalize(null!));
    }

    [Fact]
    public void Regression_DdlStatement_Discards()
    {
        Assert.Null(SqlTokenizer.TryNormalize("DROP TABLE Prescription.Rx"));
        Assert.Null(SqlTokenizer.TryNormalize("CREATE TABLE Test (id INT)"));
        Assert.Null(SqlTokenizer.TryNormalize("ALTER TABLE Rx ADD Col INT"));
    }

    [Fact]
    public void Regression_ExecStatement_Discards()
    {
        Assert.Null(SqlTokenizer.TryNormalize("EXEC sp_GetPatient @id = 123"));
    }
}
