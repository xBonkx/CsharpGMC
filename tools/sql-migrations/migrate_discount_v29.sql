-- Run explicitly on the selected GMC database after backup, with all clients closed.
-- First run on an isolated test database. No automatic production migration.
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'dbo.meta', N'U') IS NULL OR OBJECT_ID(N'dbo.contracts', N'U') IS NULL
       OR OBJECT_ID(N'dbo.acts', N'U') IS NULL
        THROW 51000, 'Not a GMC-Smeta database.', 1;
    DECLARE @schema nvarchar(32);
    SELECT @schema = CONVERT(nvarchar(32), [value]) FROM dbo.meta WITH (UPDLOCK, HOLDLOCK)
        WHERE [key] = N'schema_version';
    IF @schema IS NULL OR @schema NOT IN (N'28', N'29')
        THROW 51001, 'Expected schema version 28 or 29.', 1;
    IF @schema = N'28'
    BEGIN
        IF COL_LENGTH(N'dbo.contracts', N'discount_percent') IS NULL
            ALTER TABLE dbo.contracts ADD discount_percent FLOAT NOT NULL
                CONSTRAINT DF_contracts_discount_percent DEFAULT (0) WITH VALUES;
        IF COL_LENGTH(N'dbo.acts', N'discount_percent') IS NULL
            ALTER TABLE dbo.acts ADD discount_percent FLOAT NOT NULL
                CONSTRAINT DF_acts_discount_percent DEFAULT (0) WITH VALUES;
        UPDATE dbo.meta SET [value] = N'29' WHERE [key] = N'schema_version';
        IF @@ROWCOUNT <> 1 THROW 51002, 'Invalid schema marker.', 1;
    END;
    IF COL_LENGTH(N'dbo.contracts', N'discount_percent') IS NULL
       OR COL_LENGTH(N'dbo.acts', N'discount_percent') IS NULL
        THROW 51003, 'Missing discount columns.', 1;
    COMMIT TRANSACTION;
    SELECT DB_NAME() AS database_name, N'29' AS schema_version;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
