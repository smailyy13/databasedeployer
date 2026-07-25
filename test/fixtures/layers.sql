-- Test fixture: EDW benzeri çok katmanlı yapı + deployment riski taşıyan tablo değişiklikleri.
--
-- Usage:
--   sqlcmd -S <server> -E -C -b -i layers.sql -v DbName="EDWSTG" Variant="DEV"
--   sqlcmd -S <server> -E -C -b -i layers.sql -v DbName="EDWSTG" Variant="PROD"
--
-- DEV  = kaynak ("olması gereken")
-- PROD = hedef ("şu anki canlı")
--
-- FactSale  : PROD'da VERİ VAR  -> riskli değişiklikler deployment'ı bloklamalı
-- DimEmpty  : PROD'da VERİ YOK  -> aynı değişiklikler bloklamamalı
-- Aradaki farkı ayırt edebilmek, bu analizin tüm değeri.

IF DB_ID('$(DbName)') IS NOT NULL
BEGIN
    ALTER DATABASE [$(DbName)] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$(DbName)];
END
GO
CREATE DATABASE [$(DbName)];
GO
USE [$(DbName)];
GO

DECLARE @variant sysname = '$(Variant)';

IF @variant = 'DEV'
BEGIN
    EXEC(N'
    CREATE TABLE dbo.FactSale (
        SaleId      bigint          IDENTITY(1,1) NOT NULL,
        Code        varchar(50)     NOT NULL,          -- PROD: varchar(20)   -> genisletme
        Description nvarchar(100)   NULL,              -- PROD: nvarchar(500) -> DARALTMA
        Amount      decimal(18,2)   NOT NULL,          -- PROD: decimal(18,4) -> HASSASIYET DUSUYOR
        Quantity    int             NOT NULL,          -- PROD: smallint      -> genisletme
        Category    nvarchar(50)    NOT NULL,          -- PROD: NULL kabul ediyor -> NOT NULL
        TenantId    int             NOT NULL,          -- PROD: yok, DEFAULT da yok
        Note        nvarchar(200)   NULL,              -- PROD: yok, nullable -> guvenli
        CONSTRAINT PK_FactSale PRIMARY KEY CLUSTERED (SaleId));');

    EXEC(N'
    CREATE TABLE dbo.DimEmpty (
        DimId       int             IDENTITY(1,1) NOT NULL,
        Code        varchar(50)     NOT NULL,
        Description nvarchar(100)   NULL,
        Amount      decimal(18,2)   NOT NULL,
        Quantity    int             NOT NULL,
        Category    nvarchar(50)    NOT NULL,
        TenantId    int             NOT NULL,
        Note        nvarchar(200)   NULL,
        CONSTRAINT PK_DimEmpty PRIMARY KEY CLUSTERED (DimId));');
END
ELSE
BEGIN
    EXEC(N'
    CREATE TABLE dbo.FactSale (
        SaleId       bigint          IDENTITY(1,1) NOT NULL,
        Code         varchar(20)     NOT NULL,
        Description  nvarchar(500)   NULL,
        Amount       decimal(18,4)   NOT NULL,
        Quantity     smallint        NOT NULL,
        Category     nvarchar(50)    NULL,
        ObsoleteCode varchar(10)     NULL,             -- DEV: yok -> SILINECEK
        CONSTRAINT PK_FactSale PRIMARY KEY CLUSTERED (SaleId));');

    EXEC(N'
    CREATE TABLE dbo.DimEmpty (
        DimId        int             IDENTITY(1,1) NOT NULL,
        Code         varchar(20)     NOT NULL,
        Description  nvarchar(500)   NULL,
        Amount       decimal(18,4)   NOT NULL,
        Quantity     smallint        NOT NULL,
        Category     nvarchar(50)    NULL,
        ObsoleteCode varchar(10)     NULL,
        CONSTRAINT PK_DimEmpty PRIMARY KEY CLUSTERED (DimId));');
END
GO

-- Iki tarafta ayni: fark uretmemeli.
CREATE TABLE dbo.DimCalendar
(
    DateKey int          NOT NULL,
    [Year]  smallint     NOT NULL,
    [Month] tinyint      NOT NULL,
    DayName nvarchar(20) NOT NULL,
    CONSTRAINT PK_DimCalendar PRIMARY KEY CLUSTERED (DateKey)
);
GO

CREATE INDEX IX_FactSale_Code ON dbo.FactSale (Code);
GO

CREATE PROCEDURE dbo.LoadFactSale
AS
BEGIN
    SET NOCOUNT ON;
    SELECT COUNT(*) AS RowCountValue FROM dbo.FactSale;
END
GO

CREATE TRIGGER dbo.trg_FactSale_Guard ON dbo.FactSale AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
END
GO

-- PROD tarafinda: FactSale'e veri koy, DimEmpty'yi bos birak, trigger'i pasiflestir.
DECLARE @variant2 sysname = '$(Variant)';

-- Dinamik SQL sart: batch derlenirken DEV tarafinda olmayan kolonlara referans
-- verilemez; IF blogu icinde olmasi derlemeyi engellemez.
IF @variant2 = 'PROD'
BEGIN
    EXEC(N'
    INSERT dbo.FactSale (Code, Description, Amount, Quantity, Category, ObsoleteCode)
    SELECT TOP (5000)
           LEFT(CONVERT(varchar(36), NEWID()), 20),
           N''row'',
           123.4567,
           1,
           N''general'',
           NULL
    FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b;

    DISABLE TRIGGER dbo.trg_FactSale_Guard ON dbo.FactSale;');
END
GO

SELECT DB_NAME() AS DatabaseName,
       (SELECT COUNT(*) FROM dbo.FactSale) AS FactSaleRows,
       (SELECT COUNT(*) FROM dbo.DimEmpty) AS DimEmptyRows;
GO
