-- Synthetic large database generator for benchmarking and UI demos.
--
-- Usage:
--   sqlcmd -S localhost -E -C -b -i generate-large.sql ^
--          -v DbName="SchemaDiff_Big1" Tables=3000 Procs=5000 Views=1500 Funcs=500 Drift=0
--
-- Drift=1 produces a near-identical database with a controlled set of differences
-- covering EVERY change category, so the UI can be exercised end to end:
--
--   Add Table / Delete Table          tables missing on one side
--   Add Column / Alter Column / Delete Column
--   Add Index / Delete Index
--   Add View / Delete View / Alter View
--   Add Procedure / Delete Procedure / Alter Procedure
--   Add Function / Delete Function
--   Delete Trigger
--
-- Direction assumed by the demo: source = Drift 0, target = Drift 1.
-- So "only in Drift 0" shows up as Add, "only in Drift 1" shows up as Delete.

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

SET NOCOUNT ON;

DECLARE @tables int = $(Tables),
        @procs  int = $(Procs),
        @views  int = $(Views),
        @funcs  int = $(Funcs),
        @drift  int = $(Drift);

DECLARE @i int, @n nvarchar(10), @t nvarchar(10), @sql nvarchar(max);

------------------------------------------------------------------ tables
SET @i = 1;
WHILE @i <= @tables
BEGIN
    -- DRIFT: son 10 tablo burada YOK -> karsi tarafta "Add Table" olarak gorunur.
    -- View'lar en fazla T1501'e referans verdigi icin bu araligi atlamak guvenli.
    IF NOT (@drift = 1 AND @i > @tables - 10)
    BEGIN
        SET @n = CAST(@i AS nvarchar(10));

        SET @sql = N'CREATE TABLE dbo.T' + @n + N' (
            Id int IDENTITY(1,1) NOT NULL,
            Code varchar(32) NOT NULL,
            Name nvarchar(200) NULL,
            Amount decimal(18,4) NOT NULL CONSTRAINT DF_T' + @n + N'_Amount DEFAULT (0),
            CreatedAt datetime2(3) NOT NULL,
            UpdatedAt datetime2(3) NULL,
            Flag1 bit NOT NULL,
            Flag2 bit NULL,
            RefId int NULL,
            Notes nvarchar(max) NULL,'
            -- DRIFT: her 60. tabloda fazladan kolon -> "Delete Column"
            + CASE WHEN @drift = 1 AND @i % 60 = 0
                   THEN N' ExtraColumn nvarchar(50) NULL,' ELSE N'' END
            -- DRIFT: her 75. tabloda kolon daraltiliyor -> "Alter Column" + veri kaybi riski
            + CASE WHEN @drift = 1 AND @i % 75 = 0
                   THEN N' Widened varchar(200) NULL,' ELSE N' Widened varchar(400) NULL,' END
            -- DRIFT: her 90. tabloda kolon eksik -> "Add Column"
            + CASE WHEN @drift = 1 AND @i % 90 = 0
                   THEN N'' ELSE N' TenantId int NULL,' END
            + N'
            CONSTRAINT PK_T' + @n + N' PRIMARY KEY CLUSTERED (Id),
            CONSTRAINT CK_T' + @n + N'_Amount CHECK (Amount >= 0));';
        EXEC sp_executesql @sql;

        -- DRIFT: her 150. tabloda bu index yok -> "Add Index"
        IF NOT (@drift = 1 AND @i % 150 = 0)
        BEGIN
            SET @sql = N'CREATE INDEX IX_T' + @n + N'_Code ON dbo.T' + @n + N' (Code) INCLUDE (Name);';
            EXEC sp_executesql @sql;
        END

        SET @sql = N'CREATE INDEX IX_T' + @n + N'_Created ON dbo.T' + @n + N' (CreatedAt DESC, Flag1);';
        EXEC sp_executesql @sql;

        -- DRIFT: her 100. tabloda fazladan index -> "Delete Index"
        IF @drift = 1 AND @i % 100 = 0
        BEGIN
            SET @sql = N'CREATE INDEX IX_T' + @n + N'_Extra ON dbo.T' + @n + N' (Flag2, RefId);';
            EXEC sp_executesql @sql;
        END
    END

    SET @i += 1;
END

-- DRIFT: yalnizca burada olan tablolar -> "Delete Table"
IF @drift = 1
BEGIN
    SET @i = 1;
    WHILE @i <= 10
    BEGIN
        SET @sql = N'CREATE TABLE dbo.ObsoleteTable' + CAST(@i AS nvarchar(10)) + N' (
            Id int IDENTITY(1,1) NOT NULL,
            LegacyCode varchar(40) NULL,
            CONSTRAINT PK_ObsoleteTable' + CAST(@i AS nvarchar(10)) + N' PRIMARY KEY CLUSTERED (Id));';
        EXEC sp_executesql @sql;
        SET @i += 1;
    END
END

------------------------------------------------------------------ views
SET @i = 1;
WHILE @i <= @views
BEGIN
    -- DRIFT: ilk 5 view burada yok -> "Add View"
    IF NOT (@drift = 1 AND @i <= 5)
    BEGIN
        SET @n = CAST(@i AS nvarchar(10));
        SET @t = CAST((@i % @tables) + 1 AS nvarchar(10));

        SET @sql = N'CREATE VIEW dbo.V' + @n + N' AS
            SELECT Id, Code, Name, Amount, CreatedAt
            FROM dbo.T' + @t + N'
            WHERE Flag1 = 1'
            -- DRIFT: her 50. view'in govdesi farkli -> "Alter View"
            + CASE WHEN @drift = 1 AND @i % 50 = 0 THEN N' AND Amount > 0' ELSE N'' END
            + N';';
        EXEC sp_executesql @sql;
    END

    SET @i += 1;
END

-- DRIFT: yalnizca burada olan view'lar -> "Delete View"
IF @drift = 1
BEGIN
    SET @i = 1;
    WHILE @i <= 5
    BEGIN
        SET @sql = N'CREATE VIEW dbo.ObsoleteView' + CAST(@i AS nvarchar(10))
                 + N' AS SELECT Id, Code FROM dbo.T1;';
        EXEC sp_executesql @sql;
        SET @i += 1;
    END
END

------------------------------------------------------------------ procedures
SET @i = 1;
WHILE @i <= @procs
BEGIN
    -- DRIFT: ilk 25 prosedur burada yok -> "Add Procedure"
    IF NOT (@drift = 1 AND @i <= 25)
    BEGIN
        SET @n = CAST(@i AS nvarchar(10));
        SET @t = CAST((@i % @tables) + 1 AS nvarchar(10));

        SET @sql = N'CREATE PROCEDURE dbo.P' + @n + N'
            @id int,
            @code varchar(32) = NULL
        AS
        BEGIN
            SET NOCOUNT ON;

            SELECT Id, Code, Name, Amount, CreatedAt, UpdatedAt
            FROM dbo.T' + @t + N'
            WHERE Id = @id
              AND (@code IS NULL OR Code = @code)'
            -- DRIFT: her 40. prosedurun govdesi farkli -> "Alter Procedure"
            + CASE WHEN @drift = 1 AND @i % 40 = 0 THEN N' AND Amount > 0' ELSE N'' END
            + N'
            ORDER BY Id;
        END';
        EXEC sp_executesql @sql;
    END

    SET @i += 1;
END

-- DRIFT: yalnizca burada olan prosedurler -> "Delete Procedure"
IF @drift = 1
BEGIN
    SET @i = 1;
    WHILE @i <= 25
    BEGIN
        SET @sql = N'CREATE PROCEDURE dbo.ObsoleteProc' + CAST(@i AS nvarchar(10))
                 + N' AS SELECT ' + CAST(@i AS nvarchar(10)) + N' AS Placeholder;';
        EXEC sp_executesql @sql;
        SET @i += 1;
    END
END

------------------------------------------------------------------ functions
SET @i = 1;
WHILE @i <= @funcs
BEGIN
    -- DRIFT: son 5 fonksiyon burada yok -> "Add Scalar Function"
    IF NOT (@drift = 1 AND @i > @funcs - 5)
    BEGIN
        SET @n = CAST(@i AS nvarchar(10));
        SET @sql = N'CREATE FUNCTION dbo.F' + @n + N' (@value decimal(18,4))
            RETURNS decimal(18,4)
            AS
            BEGIN
                RETURN ROUND(ISNULL(@value, 0) * ' + @n + N', 2);
            END';
        EXEC sp_executesql @sql;
    END
    SET @i += 1;
END

-- DRIFT: yalnizca burada olan fonksiyonlar ve trigger -> "Delete ..."
IF @drift = 1
BEGIN
    SET @i = 1;
    WHILE @i <= 5
    BEGIN
        SET @sql = N'CREATE FUNCTION dbo.ObsoleteFn' + CAST(@i AS nvarchar(10))
                 + N' () RETURNS int AS BEGIN RETURN ' + CAST(@i AS nvarchar(10)) + N'; END';
        EXEC sp_executesql @sql;
        SET @i += 1;
    END

    EXEC(N'CREATE TRIGGER dbo.trg_T1_Obsolete ON dbo.T1 AFTER INSERT
           AS BEGIN SET NOCOUNT ON; END');
END
GO

SELECT COUNT(*) AS UserObjects
FROM sys.objects
WHERE is_ms_shipped = 0 AND type IN ('U','V','P','FN','IF','TF','TR','SN','SO');
GO
