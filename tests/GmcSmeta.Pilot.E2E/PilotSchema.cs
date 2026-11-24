// Test-only, synthetic SQL Server schema v28 surface needed by the existing public APIs.
// It is deliberately NOT presented as a production migration.
internal static class PilotSchema
{
    public const string Ddl = """
CREATE TABLE dbo.__pilot_provenance(
    token nvarchar(40) NOT NULL PRIMARY KEY,
    database_name sysname NOT NULL,
    server_name nvarchar(256) NOT NULL,
    created_at datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE TABLE dbo.meta([key] nvarchar(64) NOT NULL PRIMARY KEY, [value] nvarchar(max) NOT NULL);
INSERT dbo.meta([key],[value]) VALUES (N'schema_version',N'28'),(N'rev',N'pilot-initial');
CREATE TABLE dbo.objects (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    name nvarchar(255) NOT NULL, customer nvarchar(255) NOT NULL,
    address nvarchar(255) NOT NULL, folder nvarchar(500) NOT NULL,
    note nvarchar(max) NOT NULL, created_at datetime2 NOT NULL,
    customer_full nvarchar(max) NOT NULL, site_full nvarchar(max) NOT NULL,
    cust_org nvarchar(255) NOT NULL, cust_post nvarchar(255) NOT NULL,
    cust_name nvarchar(255) NOT NULL, second_role nvarchar(64) NOT NULL,
    second_org nvarchar(255) NOT NULL, second_post nvarchar(255) NOT NULL,
    second_name nvarchar(255) NOT NULL, work_title nvarchar(255) NOT NULL,
    archived bit NOT NULL, form nvarchar(32) NOT NULL,
    archived_on date NULL, works_start nvarchar(32) NOT NULL
);
CREATE TABLE dbo.companies (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    name nvarchar(255) NOT NULL DEFAULT N'', vat_rate float NOT NULL DEFAULT 22
);
CREATE TABLE dbo.contracts (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    object_id bigint NOT NULL REFERENCES dbo.objects(id),
    kind nvarchar(16) NOT NULL, name nvarchar(255) NOT NULL,
    ks_site_part nvarchar(255) NOT NULL, number nvarchar(64) NOT NULL,
    [date] nvarchar(32) NOT NULL, vat_rate float NOT NULL,
    smr_amount float NOT NULL, transport_amount float NOT NULL,
    declared_material float NULL, declared_total float NULL,
    source_file nvarchar(500) NOT NULL, source_sheet nvarchar(255) NOT NULL,
    [order] bigint NOT NULL, created_at datetime2 NOT NULL,
    company_id bigint NULL, base_contract_id bigint NULL,
    smr_mode nvarchar(8) NOT NULL, smr_percent float NOT NULL,
    note nvarchar(max) NOT NULL, kp_phase nvarchar(24) NOT NULL,
    stage_of bigint NULL, stage_no int NULL, stage_rest bit NOT NULL,
    stage_sum float NULL, form nvarchar(32) NOT NULL,
    m2_mode nvarchar(8) NOT NULL, pnr_amount float NOT NULL,
    transport_cost float NULL, transport_note nvarchar(max) NOT NULL,
    pnr_cost float NULL, consumables_cost float NULL, other_cost float NULL,
    reserve_cost float NULL, locked bit NOT NULL, locked_by nvarchar(64) NOT NULL,
    locked_at datetime2 NULL
);
CREATE TABLE dbo.systems (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    contract_id bigint NOT NULL REFERENCES dbo.contracts(id),
    name nvarchar(255) NOT NULL, [order] int NOT NULL,
    parent_id bigint NULL, smr_amount float NULL,
    heading nvarchar(255) NOT NULL, source_num nvarchar(32) NOT NULL,
    stage_sys_src bigint NULL
);
CREATE TABLE dbo.catalog_items (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [key] nvarchar(600) NOT NULL UNIQUE,
    name nvarchar(max) NOT NULL, unit nvarchar(32) NOT NULL
);
CREATE TABLE dbo.positions (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    system_id bigint NOT NULL REFERENCES dbo.systems(id),
    parent_id bigint NULL, catalog_item_id bigint NULL,
    num nvarchar(32) NOT NULL DEFAULT N'', basis nvarchar(64) NOT NULL DEFAULT N'',
    name nvarchar(max) NOT NULL DEFAULT N'', unit nvarchar(32) NOT NULL DEFAULT N'шт',
    qty float NOT NULL DEFAULT 1, price float NOT NULL DEFAULT 0,
    purchase_price float NULL, note nvarchar(max) NOT NULL DEFAULT N'',
    extra_json nvarchar(max) NOT NULL DEFAULT N'', source_row bigint NULL,
    [order] int NOT NULL DEFAULT 0, out_of_kp bit NOT NULL DEFAULT 0,
    formulas nvarchar(max) NOT NULL DEFAULT N'', stage_src bigint NULL,
    area_manual float NULL, work_price float NULL,
    cost_note nvarchar(max) NOT NULL DEFAULT N'', cost_color nvarchar(16) NOT NULL DEFAULT N'',
    work_sell float NULL, work_cost float NULL
);
CREATE INDEX IX_pilot_positions_system ON dbo.positions(system_id,[order]);
CREATE TABLE dbo.acts (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    contract_id bigint NOT NULL REFERENCES dbo.contracts(id),
    number nvarchar(32) NOT NULL DEFAULT N'', [date] date NULL,
    period_from date NULL, period_to date NULL,
    smr_amount float NOT NULL DEFAULT 0, smr_by_system bit NOT NULL DEFAULT 0,
    transport_amount float NOT NULL DEFAULT 0, pnr_amount float NOT NULL DEFAULT 0,
    ds_no nvarchar(32) NOT NULL DEFAULT N'', ds_date nvarchar(16) NOT NULL DEFAULT N'',
    ds_extra nvarchar(max) NOT NULL DEFAULT N'', rows_exact bit NOT NULL DEFAULT 0,
    gross_adjustment_cents bigint NOT NULL DEFAULT 0, signed bit NOT NULL DEFAULT 0,
    note nvarchar(max) NOT NULL DEFAULT N'', created_at datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE TABLE dbo.act_lines (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    act_id bigint NOT NULL REFERENCES dbo.acts(id),
    position_id bigint NOT NULL REFERENCES dbo.positions(id),
    qty float NOT NULL DEFAULT 0, qty_formula nvarchar(max) NOT NULL DEFAULT N'',
    price float NOT NULL DEFAULT 0, work_price float NOT NULL DEFAULT 0
);
CREATE TABLE dbo.change_log (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ts datetime2 NOT NULL, [user] nvarchar(64) NOT NULL,
    entity nvarchar(32) NOT NULL, entity_id bigint NULL,
    action nvarchar(32) NOT NULL, details nvarchar(max) NOT NULL,
    contract_id bigint NULL, object_id bigint NULL
);
-- Additional synthetic v28 surface for REAL procurement, issuance and act readers.
-- Still a subset: these CREATE TABLE statements are NOT a production migration.
CREATE TABLE dbo.tags (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [key] nvarchar(32) NOT NULL UNIQUE,
    name nvarchar(120) NOT NULL DEFAULT N'',
    rules nvarchar(max) NOT NULL DEFAULT N'',
    note nvarchar(max) NOT NULL DEFAULT N''
);
CREATE TABLE dbo.suppliers (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    name nvarchar(255) NOT NULL UNIQUE,
    note nvarchar(max) NOT NULL DEFAULT N''
);
CREATE TABLE dbo.procurement (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    position_id bigint NOT NULL UNIQUE REFERENCES dbo.positions(id),
    supplier_id bigint NULL REFERENCES dbo.suppliers(id),
    source nvarchar(8) NOT NULL DEFAULT N'buy',
    stage nvarchar(16) NOT NULL DEFAULT N'',
    order_ref nvarchar(128) NOT NULL DEFAULT N'',
    invoice_ref nvarchar(128) NOT NULL DEFAULT N'',
    paid_date date NULL, due_date date NULL, cost_price float NULL,
    responsible nvarchar(128) NOT NULL DEFAULT N'',
    replacement nvarchar(max) NOT NULL DEFAULT N'',
    direct_to_site bit NOT NULL DEFAULT 0,
    note nvarchar(max) NOT NULL DEFAULT N'',
    buy_name nvarchar(max) NOT NULL DEFAULT N'',
    updated_at datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_by nvarchar(64) NOT NULL DEFAULT N''
);
CREATE TABLE dbo.shipments (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    object_id bigint NOT NULL REFERENCES dbo.objects(id),
    number nvarchar(32) NOT NULL DEFAULT N'',
    [date] date NULL, recipient nvarchar(255) NOT NULL DEFAULT N'',
    issued_by nvarchar(255) NOT NULL DEFAULT N'',
    note nvarchar(max) NOT NULL DEFAULT N'',
    fixed bit NOT NULL DEFAULT 0, fixed_at datetime2 NULL,
    fixed_by nvarchar(64) NOT NULL DEFAULT N'',
    created_at datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE TABLE dbo.shipment_lines (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    shipment_id bigint NOT NULL REFERENCES dbo.shipments(id),
    position_id bigint NOT NULL REFERENCES dbo.positions(id),
    qty float NOT NULL, note nvarchar(max) NOT NULL DEFAULT N'',
    CONSTRAINT UQ_pilot_shipment_position UNIQUE (shipment_id,position_id)
);
CREATE TABLE dbo.movements (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    position_id bigint NOT NULL REFERENCES dbo.positions(id),
    kind nvarchar(16) NOT NULL, [date] date NULL,
    qty float NOT NULL, doc nvarchar(255) NOT NULL DEFAULT N'',
    shipment_id bigint NULL REFERENCES dbo.shipments(id),
    note nvarchar(max) NOT NULL DEFAULT N'',
    [user] nvarchar(64) NOT NULL DEFAULT N'',
    created_at datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_pilot_movements_position ON dbo.movements(position_id,kind);
""";
}
