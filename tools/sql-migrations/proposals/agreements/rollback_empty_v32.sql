-- REVIEW CANDIDATE: reverse expand only BEFORE any agreement/link/file is created.
-- No destructive downgrade is offered after backfill or new user data.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() NOT LIKE N'GmcSmetaPilot[_]Test[_]Agreement[_]%' OR CHARINDEX(N'Cutover',DB_NAME())>0
    THROW 51220, 'Only a dedicated Test_Agreement Pilot copy is allowed.', 1;
IF CONVERT(nvarchar(128), SESSION_CONTEXT(N'AGR10_APPROVED_DATABASE')) IS NULL
   OR CONVERT(nvarchar(128), SESSION_CONTEXT(N'AGR10_APPROVED_DATABASE'))<>DB_NAME()
    THROW 51221, 'A separately approved rehearsal session is required.', 1;
IF @@TRANCOUNT<>0 THROW 51222, 'Run without an outer transaction.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @lock int;
    EXEC @lock=sys.sp_getapplock @Resource=N'GMC-AGR10-schema',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0;
    IF @lock<0 THROW 51223, 'Another agreement migration is active.', 1;
    IF EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE is_user_process=1 AND database_id=DB_ID() AND session_id<>@@SPID)
        THROW 51224, 'Other clients must leave the rehearsal database first.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.meta WITH (UPDLOCK,HOLDLOCK) WHERE [key]=N'schema_version' AND [value]=N'32')
       OR NOT EXISTS (SELECT 1 FROM dbo.meta WHERE [key]=N'agr10_schema_manifest' AND [value]=N'AGR10-expand-1')
        THROW 51225, 'Unexpected agreement schema/manifest.', 1;
    IF EXISTS(SELECT 1 FROM dbo.agreements WITH (TABLOCKX,HOLDLOCK))
       OR EXISTS(SELECT 1 FROM dbo.agreement_files WITH (TABLOCKX,HOLDLOCK))
       OR EXISTS(SELECT 1 FROM dbo.agreement_migration_links WITH (TABLOCKX,HOLDLOCK))
       OR EXISTS(SELECT 1 FROM dbo.contracts WITH (TABLOCKX,HOLDLOCK) WHERE agreement_id IS NOT NULL OR annex_number<>N'')
        THROW 51226, 'New agreement data exists. Destructive downgrade is forbidden.', 1;
    DROP TABLE dbo.agreement_migration_links;
    DROP TABLE dbo.agreement_files;
    ALTER TABLE dbo.contracts DROP CONSTRAINT FK_contracts_agreement_object;
    DROP INDEX IX_contracts_agreement ON dbo.contracts;
    ALTER TABLE dbo.contracts DROP CONSTRAINT DF_contracts_annex_number;
    ALTER TABLE dbo.contracts DROP COLUMN agreement_id,annex_number;
    DROP TABLE dbo.agreements;
    DELETE dbo.meta WHERE [key]=N'agr10_schema_manifest';
    UPDATE dbo.meta SET [value]=N'31' WHERE [key]=N'schema_version';
    IF @@ROWCOUNT<>1 THROW 51227, 'Invalid schema marker.', 1;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
