-- Test fixture: "dev" side.
-- Paired with prod.sql. Every difference between the two files is intentional
-- and listed in test/fixtures/expected.md.

IF DB_ID('SchemaDiff_Dev') IS NOT NULL
BEGIN
    ALTER DATABASE SchemaDiff_Dev SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SchemaDiff_Dev;
END
GO
CREATE DATABASE SchemaDiff_Dev;
GO
USE SchemaDiff_Dev;
GO

CREATE SCHEMA sales;
GO
-- DIFF: schema exists only here
CREATE SCHEMA staging;
GO

CREATE TABLE dbo.Customer
(
    CustomerId  int            IDENTITY(1,1) NOT NULL,
    Name        nvarchar(100)  NOT NULL,
    Email       nvarchar(256)  NULL,
    Phone       varchar(32)    NULL,                  -- DIFF: column exists only here
    CreatedAt   datetime2(3)   NOT NULL CONSTRAINT DF_Customer_CreatedAt DEFAULT SYSUTCDATETIME(),
    IsActive    bit            NOT NULL DEFAULT (1),  -- system-named default: must NOT be reported
    CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED (CustomerId)
);
GO

-- DIFF: INCLUDE (Name) exists only here
CREATE INDEX IX_Customer_Email ON dbo.Customer (Email) INCLUDE (Name);
GO

CREATE TABLE sales.[Order]
(
    OrderId     int             IDENTITY(1,1) NOT NULL,
    CustomerId  int             NOT NULL,
    Total       decimal(18,2)   NOT NULL,
    CONSTRAINT PK_Order PRIMARY KEY CLUSTERED (OrderId),
    CONSTRAINT FK_Order_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer (CustomerId),
    CONSTRAINT CK_Order_Total CHECK (Total >= 0)
);
GO

CREATE TABLE dbo.AuditLog
(
    Id      int             IDENTITY(1,1) NOT NULL,
    Message nvarchar(max)   NULL,
    CONSTRAINT PK_AuditLog PRIMARY KEY CLUSTERED (Id)
);
GO

CREATE VIEW dbo.vwActiveCustomer
AS
    SELECT CustomerId, Name FROM dbo.Customer WHERE IsActive = 1;
GO

-- CONTROL: identical to prod except for whitespace and comments -> must compare EQUAL
CREATE PROCEDURE dbo.GetCustomer @id int AS
BEGIN
    SET NOCOUNT ON;
    SELECT CustomerId, Name, Email FROM dbo.Customer WHERE CustomerId = @id;
END
GO

-- DIFF: body differs from prod
CREATE PROCEDURE dbo.GetOrderTotal @orderId int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Total FROM sales.[Order] WHERE OrderId = @orderId;
END
GO

-- DIFF: exists only here
CREATE PROCEDURE dbo.NewFeature
AS
    SELECT 1 AS Placeholder;
GO

CREATE FUNCTION dbo.FormatName (@name nvarchar(100))
RETURNS nvarchar(100)
AS
BEGIN
    RETURN UPPER(LTRIM(RTRIM(@name)));
END
GO

CREATE SEQUENCE dbo.OrderNumber AS bigint START WITH 1000 INCREMENT BY 1;
GO

CREATE TRIGGER dbo.trg_Customer_Audit ON dbo.Customer AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.AuditLog (Message)
    SELECT CONCAT('new customer ', CustomerId) FROM inserted;
END
GO

CREATE SYNONYM dbo.CustomerAlias FOR dbo.Customer;
GO
