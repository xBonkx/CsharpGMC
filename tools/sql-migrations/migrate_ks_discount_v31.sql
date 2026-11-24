-- Explicitly selected GMC database only; backed up and restored to a test copy first.
-- Upgrade v28, v29 or v30 without changing any saved act money.
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
    IF @schema IS NULL OR @schema NOT IN (N'28', N'29', N'30', N'31')
        THROW 51001, 'Expected schema version 28 through 31.', 1;
    IF @schema IN (N'28', N'29', N'30')
    BEGIN
        IF COL_LENGTH(N'dbo.contracts', N'discount_percent') IS NULL
            ALTER TABLE dbo.contracts ADD discount_percent FLOAT NOT NULL
                CONSTRAINT DF_contracts_discount_percent DEFAULT (0) WITH VALUES;
        IF COL_LENGTH(N'dbo.acts', N'discount_percent') IS NULL
            ALTER TABLE dbo.acts ADD discount_percent FLOAT NOT NULL
                CONSTRAINT DF_acts_discount_percent DEFAULT (0) WITH VALUES;
        IF COL_LENGTH(N'dbo.acts', N'section_amounts_json') IS NULL
            ALTER TABLE dbo.acts ADD section_amounts_json NVARCHAR(MAX) NOT NULL
                CONSTRAINT DF_acts_section_amounts DEFAULT (N'') WITH VALUES;
        IF COL_LENGTH(N'dbo.acts', N'discount_override') IS NULL
            ALTER TABLE dbo.acts ADD discount_override BIT NOT NULL
                CONSTRAINT DF_acts_discount_override DEFAULT (0) WITH VALUES;
        IF COL_LENGTH(N'dbo.acts', N'manual_total_cents') IS NULL
            ALTER TABLE dbo.acts ADD manual_total_cents BIGINT NOT NULL
                CONSTRAINT DF_acts_manual_total_cents DEFAULT (-1) WITH VALUES;
        UPDATE dbo.meta SET [value] = N'31' WHERE [key] = N'schema_version';
        IF @@ROWCOUNT <> 1 THROW 51002, 'Invalid schema marker.', 1;
    END;
    IF COL_LENGTH(N'dbo.contracts', N'discount_percent') IS NULL
       OR COL_LENGTH(N'dbo.acts', N'discount_percent') IS NULL
       OR COL_LENGTH(N'dbo.acts', N'section_amounts_json') IS NULL
       OR COL_LENGTH(N'dbo.acts', N'discount_override') IS NULL
       OR COL_LENGTH(N'dbo.acts', N'manual_total_cents') IS NULL
        THROW 51003, 'Missing required columns.', 1;
    COMMIT TRANSACTION;
    SELECT DB_NAME() AS database_name, N'31' AS schema_version;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
