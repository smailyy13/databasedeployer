-- Test fixture: "prod" side. Paired with dev.sql.

IF DB_ID('SchemaDiff_Prod') IS NOT NULL
BEGIN
    ALTER DATABASE SchemaDiff_Prod SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE SchemaDiff_Prod;
END
GO
CREATE DATABASE SchemaDiff_Prod;
GO
USE SchemaDiff_Prod;
GO

CREATE SCHEMA sales;
GO
-- NOTE: no "staging" schema here

CREATE TABLE dbo.Customer
(
    CustomerId  int            IDENTITY(1,1) NOT NULL,
    Name        nvarchar(100)  NOT NULL,
    Email       nvarchar(256)  NULL,
    -- NOTE: no Phone column here
    CreatedAt   datetime2(3)   NOT NULL CONSTRAINT DF_Customer_CreatedAt DEFAULT SYSUTCDATETIME(),
    IsActive    bit            NOT NULL DEFAULT (1),  -- system-named, different auto name than dev
    CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED (CustomerId)
);
GO

-- NOTE: no INCLUDE here
CREATE INDEX IX_Customer_Email ON dbo.Customer (Email);
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

-- CONTROL: same code as dev, but reformatted and commented -> must compare EQUAL
CREATE PROCEDURE dbo.GetCustomer
    @id int
AS
BEGIN
    /* Returns a single customer by primary key.
       Reformatted on purpose to exercise the normalizer. */
    SET NOCOUNT ON;

    SELECT  CustomerId,
            Name,
            Email
    FROM    dbo.Customer
    WHERE   CustomerId = @id;   -- lookup by PK
END
GO

-- DIFF: extra predicate compared to dev
CREATE PROCEDURE dbo.GetOrderTotal @orderId int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Total FROM sales.[Order] WHERE OrderId = @orderId AND Total > 0;
END
GO

-- DIFF: exists only here
CREATE PROCEDURE dbo.LegacyProc
AS
    SELECT 'obsolete' AS Note;
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
