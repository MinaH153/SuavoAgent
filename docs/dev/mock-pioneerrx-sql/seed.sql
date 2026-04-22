/*
  Mock PioneerRx Inventory schema for local price-shopper testing.

  Mirrors what we expect at Nadim's based on:
    - The Edit Rx Item → Pricing tab screenshot (columns: Supplier, SupplierItemNumber,
      Manufacturer, Cost, CostPerUnit, RebateCost, AWP, MAC, BOH, OnOrder, Status)
    - Prescription.* tables documented in pioneerrx-schema-ground-truth.md
    - Our agent's existing Inventory.Item contract (ItemID, ItemName, NDC, DeaSchedule)

  Shape choice: denormalized catalog (SupplierName on the catalog row) because the UI shows
  strings like "Mckesson 869640" and "Mckesson 340b" that look string-typed. When CRD recon
  comes back Thursday with the real shape, update this seed to match before the first
  integration-test run against the mock.

  Everything below is READ-ONLY reference data. No patients, no Rx, no PHI. Pure supplier catalog.
*/

USE [master];
GO

IF DB_ID('PioneerPharmacySystem') IS NULL
    CREATE DATABASE [PioneerPharmacySystem];
GO

USE [PioneerPharmacySystem];
GO

IF SCHEMA_ID('Inventory') IS NULL EXEC('CREATE SCHEMA Inventory');
IF SCHEMA_ID('Prescription') IS NULL EXEC('CREATE SCHEMA Prescription');
GO

IF OBJECT_ID('Inventory.ItemPricing', 'U') IS NOT NULL DROP TABLE Inventory.ItemPricing;
IF OBJECT_ID('Inventory.Item', 'U') IS NOT NULL DROP TABLE Inventory.Item;
GO

CREATE TABLE Inventory.Item (
    ItemID        INT IDENTITY(1,1) PRIMARY KEY,
    ItemName      NVARCHAR(200) NOT NULL,
    NDC           VARCHAR(20)   NULL,    -- stored hyphenated per on-label form (dominant pattern observed)
    DeaSchedule   INT           NULL     -- NULL for non-controlled
);
GO

CREATE TABLE Inventory.ItemPricing (
    ItemPricingID      INT IDENTITY(1,1) PRIMARY KEY,
    ItemID             INT           NOT NULL REFERENCES Inventory.Item(ItemID),
    NDC                VARCHAR(20)   NOT NULL,  -- denormalized for direct NDC lookups
    SupplierName       NVARCHAR(120) NOT NULL,
    SupplierItemNumber NVARCHAR(50)  NULL,
    Manufacturer       NVARCHAR(120) NULL,
    Cost               DECIMAL(18,4) NOT NULL,  -- pack cost
    CostPerUnit        DECIMAL(18,6) NOT NULL,
    RebateCost         DECIMAL(18,4) NULL,
    AWP                DECIMAL(18,4) NULL,
    MAC                DECIMAL(18,4) NULL,
    BOH                DECIMAL(18,2) NOT NULL DEFAULT 0,
    OnOrder            DECIMAL(18,2) NOT NULL DEFAULT 0,
    Status             VARCHAR(30)   NOT NULL DEFAULT 'Available'
);
GO

CREATE INDEX IX_ItemPricing_NDC_Cost ON Inventory.ItemPricing(NDC, Cost);
GO

-- Match the Inventory.Item rows Joshua referenced in screenshots
INSERT INTO Inventory.Item (ItemName, NDC, DeaSchedule) VALUES
    ('OMEPRAZOLE DR CP 40 MG',      '55111-0645-01', NULL),
    ('METFORMIN TB 500 MG',         '00093-5124-01', NULL),
    ('LISINOPRIL TB 10 MG',         '16714-0234-01', NULL),
    ('ATORVASTATIN TB 20 MG',       '50242-0041-21', NULL),
    ('TRAMADOL TB 50 MG',           '00093-0058-01', 4),
    ('HYDROCODONE APAP TB 5-325',   '00406-0365-01', 2);
GO

-- Per NDC, 4-8 supplier rows at varying costs so "cheapest" + status filter both get exercised.
DECLARE @omepItem INT = (SELECT ItemID FROM Inventory.Item WHERE NDC = '55111-0645-01');
DECLARE @metItem  INT = (SELECT ItemID FROM Inventory.Item WHERE NDC = '00093-5124-01');
DECLARE @linItem  INT = (SELECT ItemID FROM Inventory.Item WHERE NDC = '16714-0234-01');
DECLARE @atoItem  INT = (SELECT ItemID FROM Inventory.Item WHERE NDC = '50242-0041-21');
DECLARE @traItem  INT = (SELECT ItemID FROM Inventory.Item WHERE NDC = '00093-0058-01');
DECLARE @hydItem  INT = (SELECT ItemID FROM Inventory.Item WHERE NDC = '00406-0365-01');

INSERT INTO Inventory.ItemPricing
    (ItemID, NDC, SupplierName, SupplierItemNumber, Manufacturer, Cost, CostPerUnit, AWP, MAC, BOH, OnOrder, Status)
VALUES
    -- Omeprazole: cheapest is Anda (0.0120). Mckesson 340b is flagged Discontinued.
    (@omepItem, '55111-0645-01', 'Mckesson 869640', '1583772', 'DR.REDDY''S LAB', 3.1600, 0.031600, 7.3956, 0.0000, 78.58, 0,  'Available'),
    (@omepItem, '55111-0645-01', 'Mckesson 340b',   '1583772', 'DR.REDDY''S LAB', 3.1600, 0.031600, 7.3956, 0.0000, 78.58, 0,  'Discontinued'),
    (@omepItem, '55111-0645-01', 'Real Value Rx',   '755511106', 'DR.REDDY''S LAB', 3.2800, 0.032800, 7.3956, 0.0000, 78.58, 0, 'Available'),
    (@omepItem, '55111-0645-01', 'keysource',       '117387',  'DR.REDDY''S LAB', 3.2800, 0.032800, 7.3956, 0.0000, 78.58, 0,  'Available'),
    (@omepItem, '55111-0645-01', 'Anda',            '322642',  'DR.REDDY''S LAB', 1.2000, 0.012000, 7.3956, 0.0000, 78.58, 0,  'Available'),
    (@omepItem, '55111-0645-01', 'Prescription Supply', '844837', 'DR.REDDY''S LAB', 3.4400, 0.034400, 7.3956, 0.0000, 78.58, 0, 'Available'),

    -- Metformin: cheapest is Amerisource (0.0098).
    (@metItem, '00093-5124-01', 'McKesson',         '700123', 'TEVA',          5.0000, 0.050000, 9.5400, 0.0500, 100.00, 0, 'Available'),
    (@metItem, '00093-5124-01', 'Amerisource',      '700124', 'TEVA',          0.9800, 0.009800, 9.5400, 0.0500, 100.00, 0, 'Available'),
    (@metItem, '00093-5124-01', 'Cardinal',         '700125', 'TEVA',          1.2500, 0.012500, 9.5400, 0.0500, 100.00, 0, 'Available'),

    -- Lisinopril
    (@linItem, '16714-0234-01', 'Mckesson',         '800101', 'NORTHSTAR',     0.9000, 0.009000, 2.1800, 0.0000, 50.00, 0, 'Available'),
    (@linItem, '16714-0234-01', 'Anda',             '800102', 'NORTHSTAR',     0.7500, 0.007500, 2.1800, 0.0000, 50.00, 0, 'Available'),
    (@linItem, '16714-0234-01', 'keysource',        '800103', 'NORTHSTAR',     1.1000, 0.011000, 2.1800, 0.0000, 50.00, 0, 'Expired'),

    -- Atorvastatin
    (@atoItem, '50242-0041-21', 'Amerisource',      '900201', 'GENENTECH',     2.1000, 0.021000, 8.9000, 0.0500, 30.00, 0, 'Available'),
    (@atoItem, '50242-0041-21', 'Cardinal',         '900202', 'GENENTECH',     2.0500, 0.020500, 8.9000, 0.0500, 30.00, 0, 'Available'),

    -- Tramadol (C-IV) — Schedule triggers priority path in agent
    (@traItem, '00093-0058-01', 'McKesson',         '111001', 'TEVA',          1.8000, 0.018000, 3.9000, 0.0400, 40.00, 0, 'Available'),
    (@traItem, '00093-0058-01', 'Anda',             '111002', 'TEVA',          2.1500, 0.021500, 3.9000, 0.0400, 40.00, 0, 'Available'),

    -- Hydrocodone (C-II)
    (@hydItem, '00406-0365-01', 'Mckesson 340b',    '222001', 'MALLINCKRODT',  4.0000, 0.040000, 12.5000, 0.0900, 20.00, 0, 'Available'),
    (@hydItem, '00406-0365-01', 'Cardinal',         '222002', 'MALLINCKRODT',  4.7500, 0.047500, 12.5000, 0.0900, 20.00, 0, 'Available');
GO

-- Prescription schema fingerprint rows — required by PioneerRxSqlEngine.VerifyPioneerRxSchemaAsync
-- so the agent accepts this as a legitimate PioneerRx DB (not a LAN impostor per H-4).
IF OBJECT_ID('Prescription.Rx', 'U') IS NOT NULL DROP TABLE Prescription.Rx;
IF OBJECT_ID('Prescription.RxTransaction', 'U') IS NOT NULL DROP TABLE Prescription.RxTransaction;
IF OBJECT_ID('Prescription.RxTransactionStatusType', 'U') IS NOT NULL DROP TABLE Prescription.RxTransactionStatusType;
GO

CREATE TABLE Prescription.Rx (
    RxID        UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    RxNumber    INT              NOT NULL
);
CREATE TABLE Prescription.RxTransactionStatusType (
    RxTransactionStatusTypeID UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    Description               NVARCHAR(100)    NOT NULL
);
CREATE TABLE Prescription.RxTransaction (
    RxTransactionID           UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    RxID                      UNIQUEIDENTIFIER NOT NULL REFERENCES Prescription.Rx(RxID),
    DateFilled                DATETIME         NULL,
    DispensedQuantity         DECIMAL(18,2)    NULL,
    RxTransactionStatusTypeID UNIQUEIDENTIFIER NULL REFERENCES Prescription.RxTransactionStatusType(RxTransactionStatusTypeID),
    DispensedItemID           INT              NULL REFERENCES Inventory.Item(ItemID)
);
GO

INSERT INTO Prescription.RxTransactionStatusType (Description)
VALUES ('Waiting for Pick up'), ('Waiting for Delivery'), ('To Be Put in Bin'),
       ('Out for Delivery'),    ('Completed');
GO

PRINT 'Mock PioneerRx seed complete — 6 items, 17 supplier rows, 5 status types.';
