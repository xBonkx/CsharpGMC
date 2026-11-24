CREATE TABLE access_lists (
	id INTEGER NOT NULL, 
	object_id INTEGER NOT NULL, 
	kind VARCHAR(16) NOT NULL, 
	created_at DATETIME NOT NULL, 
	user VARCHAR(64) NOT NULL, 
	file TEXT NOT NULL, 
	data TEXT NOT NULL, 
	PRIMARY KEY (id), 
	FOREIGN KEY(object_id) REFERENCES objects (id) ON DELETE CASCADE
);
CREATE TABLE act_lines (
	id INTEGER NOT NULL, 
	act_id INTEGER NOT NULL, 
	position_id INTEGER NOT NULL, 
	qty FLOAT NOT NULL, 
	qty_formula TEXT NOT NULL, 
	price FLOAT NOT NULL, 
	work_price FLOAT NOT NULL, 
	PRIMARY KEY (id), 
	FOREIGN KEY(act_id) REFERENCES acts (id) ON DELETE CASCADE, 
	FOREIGN KEY(position_id) REFERENCES positions (id) ON DELETE CASCADE
);
CREATE TABLE acts (
	id INTEGER NOT NULL, 
	contract_id INTEGER NOT NULL, 
	number VARCHAR(32) NOT NULL, 
	date DATE, 
	period_from DATE, 
	period_to DATE, 
	smr_amount FLOAT NOT NULL, 
	smr_by_system BOOLEAN NOT NULL, 
	transport_amount FLOAT NOT NULL, 
	pnr_amount FLOAT NOT NULL, 
	ds_no VARCHAR(32) NOT NULL, 
	ds_date VARCHAR(16) NOT NULL, 
	ds_extra TEXT NOT NULL, 
	rows_exact BOOLEAN NOT NULL, 
	gross_adjustment_cents INTEGER NOT NULL, 
	signed BOOLEAN NOT NULL, 
	note TEXT NOT NULL, 
	created_at DATETIME NOT NULL, 
	PRIMARY KEY (id), 
	FOREIGN KEY(contract_id) REFERENCES contracts (id) ON DELETE CASCADE
);
CREATE TABLE attachments (
	id INTEGER NOT NULL, 
	contract_id INTEGER NOT NULL, 
	name VARCHAR(255) NOT NULL, 
	path TEXT NOT NULL, 
	size INTEGER NOT NULL, 
	role VARCHAR(32) NOT NULL, 
	is_primary BOOLEAN NOT NULL, 
	note TEXT NOT NULL, 
	added_by VARCHAR(64) NOT NULL, 
	added_at DATETIME NOT NULL, 
	PRIMARY KEY (id), 
	FOREIGN KEY(contract_id) REFERENCES contracts (id) ON DELETE CASCADE
);
CREATE TABLE cars (
	id INTEGER NOT NULL, 
	model VARCHAR(128) NOT NULL, 
	plate VARCHAR(32) NOT NULL, 
	note TEXT NOT NULL, 
	active BOOLEAN NOT NULL, 
	created_at DATETIME NOT NULL, 
	sort_order INTEGER NOT NULL, 
	PRIMARY KEY (id)
);
CREATE TABLE catalog_items (
	id INTEGER NOT NULL, 
	"key" VARCHAR(600) NOT NULL, 
	name TEXT NOT NULL, 
	unit VARCHAR(32) NOT NULL, 
	PRIMARY KEY (id), 
	UNIQUE ("key")
);
CREATE TABLE change_log (
	id INTEGER NOT NULL, 
	ts DATETIME NOT NULL, 
	user VARCHAR(64) NOT NULL, 
	entity VARCHAR(32) NOT NULL, 
	entity_id INTEGER, 
	action VARCHAR(32) NOT NULL, 
	details TEXT NOT NULL, 
	contract_id INTEGER, 
	object_id INTEGER, 
	PRIMARY KEY (id)
);
CREATE TABLE companies (
	id INTEGER NOT NULL, 
	name VARCHAR(255) NOT NULL, 
	address TEXT NOT NULL, 
	inn_kpp VARCHAR(255) NOT NULL, 
	phone VARCHAR(255) NOT NULL, 
	email VARCHAR(255) NOT NULL, 
	"order" INTEGER NOT NULL, 
	logo BLOB, 
	vat_rate FLOAT NOT NULL, 
	legal_full TEXT NOT NULL, 
	signer_post VARCHAR(128) NOT NULL, 
	signer_name VARCHAR(128) NOT NULL, 
	PRIMARY KEY (id)
);
CREATE TABLE contracts (
	id INTEGER NOT NULL, 
	object_id INTEGER NOT NULL, 
	kind VARCHAR(16) NOT NULL, 
	name VARCHAR(255) NOT NULL, 
	ks_site_part VARCHAR(255) NOT NULL, 
	number VARCHAR(64) NOT NULL, 
	date VARCHAR(32) NOT NULL, 
	vat_rate FLOAT NOT NULL, 
	smr_amount FLOAT NOT NULL, 
	transport_amount FLOAT NOT NULL, 
	declared_material FLOAT, 
	declared_total FLOAT, 
	source_file VARCHAR(500) NOT NULL, 
	source_sheet VARCHAR(255) NOT NULL, 
	"order" INTEGER NOT NULL, 
	created_at DATETIME NOT NULL, 
	company_id INTEGER, 
	base_contract_id INTEGER, 
	smr_mode VARCHAR(8) NOT NULL, 
	smr_percent FLOAT NOT NULL, 
	note TEXT NOT NULL, 
	kp_phase VARCHAR(24) NOT NULL, 
	stage_of INTEGER, 
	stage_no INTEGER, 
	stage_rest BOOLEAN NOT NULL, 
	stage_sum FLOAT, 
	form VARCHAR(32) NOT NULL, 
	m2_mode VARCHAR(8) NOT NULL, 
	pnr_amount FLOAT NOT NULL, 
	transport_cost FLOAT, 
	transport_note TEXT NOT NULL, 
	pnr_cost FLOAT, 
	consumables_cost FLOAT, 
	other_cost FLOAT, 
	reserve_cost FLOAT, 
	locked BOOLEAN NOT NULL, 
	locked_by VARCHAR(64) NOT NULL, 
	locked_at DATETIME, 
	PRIMARY KEY (id), 
	FOREIGN KEY(object_id) REFERENCES objects (id) ON DELETE CASCADE, 
	FOREIGN KEY(company_id) REFERENCES companies (id) ON DELETE SET NULL, 
	FOREIGN KEY(base_contract_id) REFERENCES contracts (id) ON DELETE SET NULL
);
CREATE TABLE meta (
	"key" VARCHAR(64) NOT NULL, 
	value TEXT NOT NULL, 
	PRIMARY KEY ("key")
);
CREATE TABLE metal_entries (
	id INTEGER NOT NULL, 
	category VARCHAR(32) NOT NULL, 
	material VARCHAR(80) NOT NULL, 
	kind VARCHAR(24) NOT NULL, 
	quantity INTEGER NOT NULL, 
	pieces INTEGER NOT NULL, 
	reservation INTEGER NOT NULL, 
	lot_id INTEGER, 
	supplier VARCHAR(255) NOT NULL, 
	invoice VARCHAR(120) NOT NULL, 
	source VARCHAR(80) NOT NULL, 
	destination VARCHAR(80) NOT NULL, 
	object_name VARCHAR(255) NOT NULL, 
	object_id INTEGER, 
	note TEXT NOT NULL, 
	user VARCHAR(64) NOT NULL, 
	created_at DATETIME NOT NULL, 
	PRIMARY KEY (id), 
	FOREIGN KEY(lot_id) REFERENCES metal_entries (id), 
	FOREIGN KEY(object_id) REFERENCES objects (id) ON DELETE SET NULL
);
CREATE TABLE movements (
	id INTEGER NOT NULL, 
	position_id INTEGER NOT NULL, 
	kind VARCHAR(16) NOT NULL, 
	date DATE, 
	qty FLOAT NOT NULL, 
	doc VARCHAR(255) NOT NULL, 
	shipment_id INTEGER, 
	note TEXT NOT NULL, 
	user VARCHAR(64) NOT NULL, 
	created_at DATETIME NOT NULL, 
	PRIMARY KEY (id), 
	FOREIGN KEY(position_id) REFERENCES positions (id) ON DELETE CASCADE, 
	FOREIGN KEY(shipment_id) REFERENCES shipments (id) ON DELETE CASCADE
);
CREATE TABLE objects (
	id INTEGER NOT NULL, 
	name VARCHAR(255) NOT NULL, 
	customer VARCHAR(255) NOT NULL, 
	address VARCHAR(255) NOT NULL, 
	folder VARCHAR(500) NOT NULL, 
	note TEXT NOT NULL, 
	created_at DATETIME NOT NULL, 
	customer_full TEXT NOT NULL, 
	site_full TEXT NOT NULL, 
	cust_org VARCHAR(255) NOT NULL, 
	cust_post VARCHAR(255) NOT NULL, 
	cust_name VARCHAR(255) NOT NULL, 
	second_role VARCHAR(64) NOT NULL, 
	second_org VARCHAR(255) NOT NULL, 
	second_post VARCHAR(255) NOT NULL, 
	second_name VARCHAR(255) NOT NULL, 
	work_title VARCHAR(255) NOT NULL, 
	archived BOOLEAN NOT NULL, 
	form VARCHAR(32) NOT NULL, 
	archived_on DATE, 
	works_start VARCHAR(32) NOT NULL, 
	PRIMARY KEY (id)
);
CREATE TABLE people (
	id INTEGER NOT NULL, 
	fio VARCHAR(255) NOT NULL, 
	fio_gen VARCHAR(255) NOT NULL, 
	post VARCHAR(128) NOT NULL, 
	post_gen VARCHAR(128) NOT NULL, 
	birth VARCHAR(32) NOT NULL, 
	note TEXT NOT NULL, 
	active BOOLEAN NOT NULL, 
	created_at DATETIME NOT NULL, 
	sort_order INTEGER NOT NULL, 
	PRIMARY KEY (id)
);
CREATE TABLE positions (
	id INTEGER NOT NULL, 
	system_id INTEGER NOT NULL, 
	parent_id INTEGER, 
	catalog_item_id INTEGER, 
	num VARCHAR(32) NOT NULL, 
	basis VARCHAR(64) NOT NULL, 
	name TEXT NOT NULL, 
	unit VARCHAR(32) NOT NULL, 
	qty FLOAT NOT NULL, 
	price FLOAT NOT NULL, 
	purchase_price FLOAT, 
	note TEXT NOT NULL, 
	extra_json TEXT NOT NULL, 
	source_row INTEGER, 
	"order" INTEGER NOT NULL, 
	out_of_kp BOOLEAN NOT NULL, 
	formulas TEXT NOT NULL, 
	stage_src INTEGER, 
	area_manual FLOAT, 
	work_price FLOAT, 
	cost_note TEXT NOT NULL, 
	cost_color VARCHAR(16) NOT NULL, 
	work_sell FLOAT, 
	work_cost FLOAT, 
	PRIMARY KEY (id), 
	FOREIGN KEY(system_id) REFERENCES systems (id) ON DELETE CASCADE, 
	FOREIGN KEY(parent_id) REFERENCES positions (id) ON DELETE CASCADE, 
	FOREIGN KEY(catalog_item_id) REFERENCES catalog_items (id) ON DELETE SET NULL
);
CREATE TABLE presence (
	id INTEGER NOT NULL, 
	user VARCHAR(64) NOT NULL, 
	host VARCHAR(64) NOT NULL, 
	contract_id INTEGER, 
	seen DATETIME NOT NULL, 
	PRIMARY KEY (id)
);
CREATE TABLE price_history (
	id INTEGER NOT NULL, 
	item_id INTEGER NOT NULL, 
	contract_id INTEGER, 
	price FLOAT NOT NULL, 
	purchase_price FLOAT, 
	date VARCHAR(32) NOT NULL, 
	created_at DATETIME NOT NULL, 
	PRIMARY KEY (id), 
	FOREIGN KEY(item_id) REFERENCES catalog_items (id) ON DELETE CASCADE, 
	FOREIGN KEY(contract_id) REFERENCES contracts (id) ON DELETE SET NULL
);
CREATE TABLE procurement (
	id INTEGER NOT NULL, 
	position_id INTEGER NOT NULL, 
	supplier_id INTEGER, 
	source VARCHAR(8) NOT NULL, 
	stage VARCHAR(16) NOT NULL, 
	order_ref VARCHAR(128) NOT NULL, 
	invoice_ref VARCHAR(128) NOT NULL, 
	paid_date DATE, 
	due_date DATE, 
	cost_price FLOAT, 
	responsible VARCHAR(128) NOT NULL, 
	replacement TEXT NOT NULL, 
	direct_to_site BOOLEAN NOT NULL, 
	note TEXT NOT NULL, 
	buy_name TEXT NOT NULL, 
	updated_at DATETIME NOT NULL, 
	updated_by VARCHAR(64) NOT NULL, 
	PRIMARY KEY (id), 
	UNIQUE (position_id), 
	FOREIGN KEY(position_id) REFERENCES positions (id) ON DELETE CASCADE, 
	FOREIGN KEY(supplier_id) REFERENCES suppliers (id) ON DELETE SET NULL
);
CREATE TABLE shipment_lines (
	id INTEGER NOT NULL, 
	shipment_id INTEGER NOT NULL, 
	position_id INTEGER NOT NULL, 
	qty FLOAT NOT NULL, 
	note TEXT NOT NULL, 
	PRIMARY KEY (id), 
	FOREIGN KEY(shipment_id) REFERENCES shipments (id) ON DELETE CASCADE, 
	FOREIGN KEY(position_id) REFERENCES positions (id) ON DELETE CASCADE
);
CREATE TABLE shipments (
	id INTEGER NOT NULL, 
	object_id INTEGER NOT NULL, 
	number VARCHAR(32) NOT NULL, 
	date DATE, 
	recipient VARCHAR(255) NOT NULL, 
	issued_by VARCHAR(255) NOT NULL, 
	note TEXT NOT NULL, 
	fixed BOOLEAN NOT NULL, 
	fixed_at DATETIME, 
	fixed_by VARCHAR(64) NOT NULL, 
	created_at DATETIME NOT NULL, 
	PRIMARY KEY (id), 
	FOREIGN KEY(object_id) REFERENCES objects (id) ON DELETE CASCADE
);
CREATE TABLE smr_costs (
	id INTEGER NOT NULL, 
	contract_id INTEGER NOT NULL, 
	date DATE, 
	kind VARCHAR(64) NOT NULL, 
	who VARCHAR(255) NOT NULL, 
	amount FLOAT NOT NULL, 
	note TEXT NOT NULL, 
	created_at DATETIME NOT NULL, 
	PRIMARY KEY (id), 
	FOREIGN KEY(contract_id) REFERENCES contracts (id) ON DELETE CASCADE
);
CREATE TABLE spec_revisions (
	id INTEGER NOT NULL, 
	contract_id INTEGER NOT NULL, 
	created_at DATETIME NOT NULL, 
	user VARCHAR(64) NOT NULL, 
	file TEXT NOT NULL, 
	title VARCHAR(255) NOT NULL, 
	total_before FLOAT NOT NULL, 
	total_after FLOAT NOT NULL, 
	data TEXT NOT NULL, 
	PRIMARY KEY (id), 
	FOREIGN KEY(contract_id) REFERENCES contracts (id) ON DELETE CASCADE
);
CREATE TABLE suppliers (
	id INTEGER NOT NULL, 
	name VARCHAR(255) NOT NULL, 
	note TEXT NOT NULL, 
	PRIMARY KEY (id), 
	UNIQUE (name)
);
CREATE TABLE systems (
	id INTEGER NOT NULL, 
	contract_id INTEGER NOT NULL, 
	name VARCHAR(255) NOT NULL, 
	"order" INTEGER NOT NULL, 
	parent_id INTEGER, 
	smr_amount FLOAT, 
	heading VARCHAR(255) NOT NULL, 
	source_num VARCHAR(32) NOT NULL, 
	stage_sys_src INTEGER, 
	PRIMARY KEY (id), 
	FOREIGN KEY(contract_id) REFERENCES contracts (id) ON DELETE CASCADE, 
	FOREIGN KEY(parent_id) REFERENCES systems (id) ON DELETE CASCADE
);
CREATE TABLE tags (
	id INTEGER NOT NULL, 
	"key" VARCHAR(32) NOT NULL, 
	name VARCHAR(120) NOT NULL, 
	rules TEXT NOT NULL, 
	note TEXT NOT NULL, 
	PRIMARY KEY (id), 
	UNIQUE ("key")
);
CREATE TABLE trash (
	id INTEGER NOT NULL, 
	kind VARCHAR(16) NOT NULL, 
	title TEXT NOT NULL, 
	object_name TEXT NOT NULL, 
	rows INTEGER NOT NULL, 
	deleted_at DATETIME NOT NULL, 
	deleted_by VARCHAR(64) NOT NULL, 
	data TEXT NOT NULL, 
	PRIMARY KEY (id)
);
