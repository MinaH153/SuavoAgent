// tests/SuavoAgent.Core.Tests/Learning/SqlSchemaObserverTests.cs
using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class SqlSchemaObserverTests
{
    [Theory]
    [InlineData("PatientID", "identifier")]
    [InlineData("RxTransactionStatusTypeID", "identifier")]
    [InlineData("DateFilled", "temporal")]
    [InlineData("CreatedAt", "temporal")]
    [InlineData("NPI", "regulatory")]
    [InlineData("DEANumber", "regulatory")]
    [InlineData("TotalPrice", "amount")]
    [InlineData("DispensedQuantity", "amount")]
    [InlineData("PatientName", "name")]
    [InlineData("FirstName", "name")]
    [InlineData("RandomColumn", "unknown")]
    public void InferColumnPurpose_ClassifiesCorrectly(string columnName, string expected)
    {
        Assert.Equal(expected, SqlSchemaObserver.InferColumnPurpose(columnName));
    }

    [Theory]
    [InlineData("RxTransactionID", true)]
    [InlineData("PatientID", true)]
    [InlineData("prescription_id", true)]
    [InlineData("MedicationDescription", false)]
    [InlineData("Status", false)]
    public void IsLikelyForeignKey_ByName(string columnName, bool expected)
    {
        Assert.Equal(expected, SqlSchemaObserver.IsLikelyForeignKey(columnName));
    }
}
