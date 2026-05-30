using SuavoAgent.Adapters.PioneerRx;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests;

/// <summary>
/// PioneerRxAdapterConfig.Create() is the single source that lifts PioneerRx's Core-facing
/// config out of scattered literals. The characterization theory pins HIPAA invariant #2:
/// the migrated PhiPolicy classifies columns identically to the legacy PioneerRxConstants.IsPhiColumn.
/// </summary>
public class PioneerRxAdapterConfigTests
{
    [Fact]
    public void Create_HasPioneerRxIdentityAndSqlProfile()
    {
        var c = PioneerRxAdapterConfig.Create();

        Assert.Equal("pioneerrx", c.AdapterType);
        Assert.Equal("PioneerRx", c.DisplayName);
        Assert.Equal(PioneerRxConstants.ProcessName, c.Process.ProcessName);
        Assert.Equal(PioneerRxConstants.DefaultWindowTitle, c.Process.DefaultWindowTitle);
        Assert.Equal("PioneerPharmacySystem", c.Sql.DefaultCatalog);
        Assert.Equal("pioneerrx.sql.metadata.v1", c.Sql.SchemaSignature);
    }

    [Fact]
    public void Create_PhiBlocklistIsSourcedFromConstants()
    {
        var c = PioneerRxAdapterConfig.Create();
        Assert.Same(PioneerRxConstants.PhiColumnBlocklist, c.Phi.ColumnBlocklist);
    }

    [Theory]
    [InlineData("PatientName")]
    [InlineData("SSN")]
    [InlineData("PatientID")]
    [InlineData("DiagnosisCode")]
    [InlineData("patientname")]   // case-insensitive blocklist
    [InlineData("ssn")]
    [InlineData("PatientSSN")]    // substring pattern: ssn
    [InlineData("HomeAddress")]   // substring pattern: address
    [InlineData("CellPhone")]     // substring pattern: phone
    [InlineData("GuardianName")]  // substring pattern: guardian
    [InlineData("PrescriberFax")] // substring pattern: fax
    [InlineData("RxNumber")]      // negative
    [InlineData("Ndc")]           // negative
    [InlineData("Quantity")]      // negative
    [InlineData("DrugName")]      // negative (no "name" pattern)
    [InlineData("StatusGuid")]    // negative
    public void PhiPolicy_ClassifiesIdenticallyToLegacy(string column)
    {
        var phi = PioneerRxAdapterConfig.Create().Phi;
        Assert.Equal(PioneerRxConstants.IsPhiColumn(column), phi.IsPhiColumn(column));
    }

    [Fact]
    public void PhiPolicy_AbsoluteSanity_PatientNameIsPhi_RxNumberIsNot()
    {
        var phi = PioneerRxAdapterConfig.Create().Phi;
        Assert.True(phi.IsPhiColumn("PatientName"));
        Assert.False(phi.IsPhiColumn("RxNumber"));
    }
}
