-- AGR-10 REVIEW CANDIDATE, NOT REGISTERED IN SqlMigrationPlan OR RELEASE BUILDER.
-- Only after separate owner approval, verified COPY_ONLY/CHECKSUM backup and restored Pilot copy.
-- Version 32 is a proposal. The current application deliberately rejects it.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() NOT LIKE N'GmcSmetaPilot[_]Test[_]Agreement[_]%'
   AND NOT (DB_NAME()=N'GmcSmetaPilot_Cutover_20261125' AND CONVERT(nvarchar(128),SERVERPROPERTY('ServerName'))=N'DIMA\SQLEXPRESS')
    THROW 51200, 'Only a dedicated Test_Agreement Pilot copy is allowed.', 1;
IF CONVERT(nvarchar(128), SESSION_CONTEXT(N'AGR10_APPROVED_DATABASE')) IS NULL
   OR CONVERT(nvarchar(128), SESSION_CONTEXT(N'AGR10_APPROVED_DATABASE')) <> DB_NAME()
    THROW 51201, 'A separately approved rehearsal session is required.', 1;
DECLARE @owns_transaction bit=CASE WHEN @@TRANCOUNT=0 THEN 1 ELSE 0 END;
IF EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE is_user_process=1 AND database_id=DB_ID() AND session_id<>@@SPID)
    THROW 51203, 'Other clients must leave the rehearsal database first.', 1;
BEGIN TRY
    IF @owns_transaction=1 BEGIN TRANSACTION;
    DECLARE @lock int;
    EXEC @lock=sys.sp_getapplock @Resource=N'GMC-AGR10-schema', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=0;
    IF @lock<0 THROW 51204, 'Another agreement migration is active.', 1;
    DECLARE @version nvarchar(32);
    SELECT @version=CONVERT(nvarchar(32),[value]) FROM dbo.meta WITH (UPDLOCK,HOLDLOCK) WHERE [key]=N'schema_version';
    IF @version=N'32'
    BEGIN
        IF OBJECT_ID(N'dbo.agreements',N'U') IS NULL OR OBJECT_ID(N'dbo.agreement_files',N'U') IS NULL
           OR OBJECT_ID(N'dbo.agreement_migration_links',N'U') IS NULL
           OR COL_LENGTH(N'dbo.contracts',N'agreement_id') IS NULL
           OR COL_LENGTH(N'dbo.contracts',N'annex_number') IS NULL
           OR NOT EXISTS (SELECT 1 FROM dbo.meta WHERE [key]=N'agr10_schema_manifest' AND [value]=N'AGR10-expand-1')
            THROW 51205, 'Version 32 does not match this migration manifest.', 1;
        IF @owns_transaction=1 COMMIT TRANSACTION;
        RETURN;
    END;
    IF @version IS NULL OR @version<>N'31' THROW 51206, 'Expected a separately accepted schema 31 copy.', 1;
    IF OBJECT_ID(N'dbo.agreements') IS NOT NULL OR OBJECT_ID(N'dbo.agreement_files') IS NOT NULL
       OR OBJECT_ID(N'dbo.agreement_migration_links') IS NOT NULL
       OR COL_LENGTH(N'dbo.contracts',N'agreement_id') IS NOT NULL
       OR COL_LENGTH(N'dbo.contracts',N'annex_number') IS NOT NULL
        THROW 51207, 'Conflicting pre-existing agreement schema; manual audit required.', 1;
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id IN
            (OBJECT_ID(N'dbo.objects'),OBJECT_ID(N'dbo.companies'),OBJECT_ID(N'dbo.contracts'))
            AND name=N'id' AND system_type_id=127)<>3
        THROW 51208, 'Pilot key types differ from the reviewed bigint model.', 1;

    CREATE TABLE dbo.agreements (
        id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_agreements PRIMARY KEY,
        object_id bigint NOT NULL CONSTRAINT FK_agreements_object REFERENCES dbo.objects(id),
        number nvarchar(200) NOT NULL CONSTRAINT DF_agreements_number DEFAULT N'',
        agreement_date date NULL,
        company_id bigint NULL CONSTRAINT FK_agreements_company REFERENCES dbo.companies(id),
        contractor_full nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_contractor DEFAULT N'',
        contractor_post nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_contractor_post DEFAULT N'',
        contractor_name nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_contractor_name DEFAULT N'',
        subject nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_subject DEFAULT N'',
        works_start date NULL, works_end date NULL,
        customer_full nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_customer DEFAULT N'',
        customer_address nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_customer_address DEFAULT N'',
        cust_org nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_cust_org DEFAULT N'',
        cust_post nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_cust_post DEFAULT N'',
        cust_name nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_cust_name DEFAULT N'',
        second_role nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_second_role DEFAULT N'',
        second_org nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_second_org DEFAULT N'',
        second_post nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_second_post DEFAULT N'',
        second_name nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_second_name DEFAULT N'',
        work_title nvarchar(2000) NOT NULL CONSTRAINT DF_agreements_work_title DEFAULT N'',
        archived bit NOT NULL CONSTRAINT DF_agreements_archived DEFAULT 0,
        created_utc datetime2 NOT NULL CONSTRAINT DF_agreements_created DEFAULT SYSUTCDATETIME(),
        row_version rowversion NOT NULL,
        CONSTRAINT UQ_agreements_object UNIQUE(id,object_id),
        CONSTRAINT CK_agreements_dates CHECK(works_end IS NULL OR works_start IS NULL OR works_end>=works_start)
    );
    ALTER TABLE dbo.contracts ADD agreement_id bigint NULL,
        annex_number nvarchar(200) NOT NULL CONSTRAINT DF_contracts_annex_number DEFAULT N'' WITH VALUES;
    -- Composite FK prevents assigning an annex to an agreement on a different construction object.
    ALTER TABLE dbo.contracts ADD CONSTRAINT FK_contracts_agreement_object
        FOREIGN KEY(agreement_id,object_id) REFERENCES dbo.agreements(id,object_id);
    CREATE INDEX IX_contracts_agreement ON dbo.contracts(agreement_id,object_id);
    CREATE TABLE dbo.agreement_files (
        id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_agreement_files PRIMARY KEY,
        agreement_id bigint NOT NULL CONSTRAINT FK_agreement_files_agreement REFERENCES dbo.agreements(id),
        role nvarchar(32) NOT NULL, original_name nvarchar(255) NOT NULL,
        file_address nvarchar(2000) NOT NULL, sha256 binary(32) NOT NULL,
        signed bit NOT NULL, created_utc datetime2 NOT NULL CONSTRAINT DF_agreement_files_created DEFAULT SYSUTCDATETIME()
    );
    CREATE TABLE dbo.agreement_migration_links (
        document_id bigint NOT NULL CONSTRAINT PK_agreement_migration_links PRIMARY KEY,
        agreement_id bigint NOT NULL,
        plan_sha256 binary(32) NOT NULL, source_sha256 binary(32) NOT NULL,
        approved_by nvarchar(200) NOT NULL, mapped_utc datetime2 NOT NULL CONSTRAINT DF_agreement_mapped DEFAULT SYSUTCDATETIME()
    );
    INSERT dbo.meta([key],[value]) VALUES(N'agr10_schema_manifest',N'AGR10-expand-1');
    UPDATE dbo.meta SET [value]=N'32' WHERE [key]=N'schema_version';
    IF @@ROWCOUNT<>1 THROW 51209, 'Invalid schema marker.', 1;
    -- No records are merged or inferred; explicit approved mapping is a separate operation.
    IF @owns_transaction=1 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
