using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Adapters.PioneerRx.Pricing;
using SuavoAgent.Contracts.Pricing;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Pricing;

/// <summary>
/// REAL end-to-end pricing against a live SQL Server engine seeded with a PioneerRx-shaped schema
/// (Inventory.Item / Inventory.ItemPricing / Prescription.RxTransaction[+StatusType]) + Nadim's real
/// NDCs and the real Omeprazole supplier grid. Runs the ACTUAL production classes — schema discovery,
/// the top-500 generator, and the cheapest-supplier lookup — not mocks.
///
/// Gated on SUAVO_PMS_CONN (a SQL connection string); unset → the test no-ops so CI stays green.
/// Locally: point it at the Azure SQL Edge / SQL Server container and it validates the whole SQL path.
/// </summary>
public class PricingSqlE2ETests
{
    private static string? Conn =>
        Environment.GetEnvironmentVariable("SUAVO_PMS_CONN")
        ?? (System.IO.File.Exists("/tmp/suavo_pms_conn.txt")
            ? System.IO.File.ReadAllText("/tmp/suavo_pms_conn.txt").Trim()
            : null);
    private const string Db = "PioneerRxDemo";

    [Fact]
    public async Task Pricing_end_to_end_against_real_sql()
    {
        var baseConn = Conn;
        if (string.IsNullOrWhiteSpace(baseConn))
            return; // no live DB configured — skip (CI)

        var ct = CancellationToken.None;
        await SeedAsync(baseConn, ct);

        var dbConn = new SqlConnectionStringBuilder(baseConn) { InitialCatalog = Db }.ConnectionString;

        // 1) Schema discovery resolves the PioneerRx-shaped catalog from sys.columns.
        await using var conn = new SqlConnection(dbConn);
        await conn.OpenAsync(ct);
        var discovery = new PricingSchemaDiscovery(NullLogger<PricingSchemaDiscovery>.Instance);
        var outcome = await discovery.DiscoverAsync(conn, ct);
        Assert.True(outcome.Ok, outcome.Reason);
        var schema = outcome.Schema!;
        Assert.Equal("ItemPricing", schema.CatalogTable);
        Assert.Equal("Cost", schema.CostColumn);
        Assert.Equal("CostPerUnit", schema.CostPerUnitColumn);
        Assert.NotNull(schema.ItemJoin);
        Assert.Equal("Item", schema.ItemJoin!.ItemTable);
        Assert.Equal("NDC", schema.ItemJoin.NdcColumnInItem);

        // 2) Top-500 generator: ranked by dispensed, generics/Rx/no-schedule only, voids excluded,
        //    outside-window excluded.
        var spec = new TopDispensedSpec(
            ItemTableSchema: "Inventory", ItemTable: "Item",
            ItemIdColumnInItem: "ItemID", NdcColumnInItem: "NDC",
            DrugNameColumn: "Name", StrengthColumn: "Strength",
            BrandGenericColumn: "BrandGeneric", GenericValue: "Generic",
            RxOtcColumn: "RxOtc", RxValue: "Rx",
            ScheduleColumn: "DeaSchedule", NoScheduleValue: "0");
        var gen = new SqlTopDispensedGenerator(
            _ => Task.FromResult(new SqlConnection(dbConn).OpenAndReturn()),
            new[] { "Sold", "Completed" },
            NullLogger<SqlTopDispensedGenerator>.Instance);
        var rows = await gen.GenerateAsync(spec, topN: 500, windowStart: DateTime.UtcNow.AddDays(-90), ct);

        var ndcs = rows.Select(r => r.Ndc).ToList();
        Assert.Equal(6, rows.Count);                                   // brand/OTC/scheduled excluded
        Assert.Equal("60505082901", rows[0].Ndc);                      // Fluticasone 2123 — top
        Assert.Equal("59651000205", rows[1].Ndc);                      // Omeprazole20 1905
        Assert.Equal("60505258008", rows[2].Ndc);                      // Atorvastatin 1300 (200d fill excluded)
        Assert.DoesNotContain("00071015523", ndcs);                    // Lipitor (brand)
        Assert.DoesNotContain("00904582160", ndcs);                    // Aspirin (OTC)
        Assert.DoesNotContain("00527134601", ndcs);                    // Alprazolam (schedule)
        Assert.Equal(2123m, rows[0].TotalDispensed);                   // voided 9999 NOT counted

        // 3) Cheapest supplier per NDC — argmin COST PER UNIT, blank + discontinued excluded.
        var lookup = new SqlSupplierPriceLookup(
            schema, _ => Task.FromResult(conn), NullLogger<SqlSupplierPriceLookup>.Instance);

        var omeprazole = await lookup.FindCheapestSupplierAsync("job", 0, "55111064501", ct);
        Assert.True(omeprazole.Found, omeprazole.ErrorMessage);
        Assert.Equal("McKesson", omeprazole.SupplierName);             // 500ct: cheapest PER UNIT, not pack cost
        Assert.Equal(0.0099m, omeprazole.CostPerUnit);

        var fluticasone = await lookup.FindCheapestSupplierAsync("job", 1, "60505082901", ct);
        Assert.True(fluticasone.Found, fluticasone.ErrorMessage);
        Assert.Equal("Parmed", fluticasone.SupplierName);
    }

    private static async Task SeedAsync(string baseConn, CancellationToken ct)
    {
        // Create DB on master, then run schema+data batches on the DB.
        var masterConn = new SqlConnectionStringBuilder(baseConn) { InitialCatalog = "master" }.ConnectionString;
        await using (var m = new SqlConnection(masterConn))
        {
            await m.OpenAsync(ct);
            await Exec(m, $"IF DB_ID('{Db}') IS NULL CREATE DATABASE [{Db}]", ct);
        }
        var dbConn = new SqlConnectionStringBuilder(baseConn) { InitialCatalog = Db }.ConnectionString;
        await using var c = new SqlConnection(dbConn);
        await c.OpenAsync(ct);
        foreach (var batch in SeedBatches) await Exec(c, batch, ct);
    }

    private static async Task Exec(SqlConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static readonly string[] SeedBatches =
    {
        "IF SCHEMA_ID('Inventory') IS NULL EXEC('CREATE SCHEMA Inventory')",
        "IF SCHEMA_ID('Prescription') IS NULL EXEC('CREATE SCHEMA Prescription')",
        "DROP TABLE IF EXISTS Inventory.ItemPricing; DROP TABLE IF EXISTS Prescription.RxTransaction; DROP TABLE IF EXISTS Prescription.RxTransactionStatusType; DROP TABLE IF EXISTS Inventory.Item",
        @"CREATE TABLE Inventory.Item (ItemID INT PRIMARY KEY, NDC VARCHAR(20) NOT NULL, Name NVARCHAR(120) NOT NULL,
            Strength NVARCHAR(60) NULL, BrandGeneric VARCHAR(20) NOT NULL, RxOtc VARCHAR(10) NOT NULL, DeaSchedule VARCHAR(5) NOT NULL)",
        @"CREATE TABLE Inventory.ItemPricing (ItemPricingID INT IDENTITY(1,1) PRIMARY KEY, ItemID INT NOT NULL,
            SupplierName NVARCHAR(80) NOT NULL, Cost DECIMAL(12,4) NULL, CostPerUnit DECIMAL(12,4) NULL, Status VARCHAR(20) NOT NULL)",
        "CREATE TABLE Prescription.RxTransactionStatusType (RxTransactionStatusTypeID INT PRIMARY KEY, Description NVARCHAR(40) NOT NULL)",
        @"CREATE TABLE Prescription.RxTransaction (RxTransactionID INT IDENTITY(1,1) PRIMARY KEY, DispensedItemID INT NOT NULL,
            DispensedQuantity DECIMAL(12,3) NOT NULL, DateFilled DATETIME NOT NULL, RxTransactionStatusTypeID INT NOT NULL)",
        "INSERT INTO Prescription.RxTransactionStatusType VALUES (1,'Sold'),(2,'Completed'),(3,'Voided'),(4,'Reversed')",
        @"INSERT INTO Inventory.Item (ItemID,NDC,Name,Strength,BrandGeneric,RxOtc,DeaSchedule) VALUES
            (1,'60505082901','Fluticasone Prop 50 Mcg Spray','50 mcg/actuation','Generic','Rx','0'),
            (2,'59651000205','Omeprazole Dr 20 Mg Capsule','20 mg','Generic','Rx','0'),
            (3,'55111064501','Omeprazole Dr 40 Mg Capsule','40 mg','Generic','Rx','0'),
            (4,'60505258008','Atorvastatin 40 Mg Tablet','40 mg','Generic','Rx','0'),
            (5,'00093512401','Metformin 500 Mg Tablet','500 mg','Generic','Rx','0'),
            (6,'16714033401','Lisinopril 20 Mg Tablet','20 mg','Generic','Rx','0'),
            (7,'00071015523','Lipitor 40 Mg Tablet','40 mg','Brand','Rx','0'),
            (8,'00904582160','Aspirin 81 Mg Tablet','81 mg','Generic','OTC','0'),
            (9,'00527134601','Alprazolam 1 Mg Tablet','1 mg','Generic','Rx','4')",
        @"INSERT INTO Inventory.ItemPricing (ItemID,SupplierName,Cost,CostPerUnit,Status) VALUES
            (3,'Mckesson 869640',NULL,NULL,'Available'),(3,'Mckesson 340b',NULL,NULL,'Available'),
            (3,'Real Value Rx',3.1600,0.0316,'Available'),(3,'keysource',3.2800,0.0328,'Available'),
            (3,'Prescription Supply',3.4400,0.0344,'Available'),(3,'Anda',3.5400,0.0354,'Available'),
            (3,'McKesson',4.9500,0.0099,'Available'),(3,'Mckesson Geri',13.8900,0.1389,'Available'),
            (3,'CheapButGone',1.0000,0.0010,'Discontinued'),
            (1,'Parmed',2.6000,0.1625,'Available'),(1,'McKesson',3.1000,0.1938,'Available'),(1,'Anda',3.4000,0.2125,'Available'),
            (2,'Oak Drugs',2.9900,0.0299,'Available'),(2,'McKesson',3.5000,0.0350,'Available'),
            (4,'Parmed',5.0000,0.0500,'Available'),(4,'Anda',5.5000,0.0550,'Available'),
            (5,'McKesson',2.0000,0.0040,'Available'),(6,'Anda',1.8000,0.0180,'Available'),
            (7,'McKesson',120.0000,1.2000,'Available'),(8,'McKesson',1.5000,0.0150,'Available'),(9,'Anda',8.0000,0.0800,'Available')",
        @"INSERT INTO Prescription.RxTransaction (DispensedItemID,DispensedQuantity,DateFilled,RxTransactionStatusTypeID) VALUES
            (1,1523,DATEADD(day,-10,GETUTCDATE()),1),(1,600,DATEADD(day,-40,GETUTCDATE()),2),
            (2,1405,DATEADD(day,-12,GETUTCDATE()),1),(2,500,DATEADD(day,-50,GETUTCDATE()),2),
            (4,1300,DATEADD(day,-8,GETUTCDATE()),1),(5,1100,DATEADD(day,-15,GETUTCDATE()),1),
            (3,300,DATEADD(day,-20,GETUTCDATE()),1),(6,250,DATEADD(day,-25,GETUTCDATE()),2),
            (1,9999,DATEADD(day,-5,GETUTCDATE()),3),(2,9999,DATEADD(day,-5,GETUTCDATE()),4),
            (7,5000,DATEADD(day,-3,GETUTCDATE()),1),(8,5000,DATEADD(day,-3,GETUTCDATE()),1),(9,5000,DATEADD(day,-3,GETUTCDATE()),1),
            (4,8000,DATEADD(day,-200,GETUTCDATE()),1)",
    };
}

internal static class SqlConnExt
{
    public static SqlConnection OpenAndReturn(this SqlConnection c) { c.Open(); return c; }
}
