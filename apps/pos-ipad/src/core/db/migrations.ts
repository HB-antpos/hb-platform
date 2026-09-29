import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

export type DatabaseMigration = Readonly<{
  version: number;
  name: string;
  sql: string;
}>;

const M1 = `
CREATE TABLE IF NOT EXISTS schema_migrations (
  version INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  applied_at_iso TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS app_settings (
  setting_key TEXT PRIMARY KEY,
  setting_value TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS cashier_session_cache (
  cache_key TEXT PRIMARY KEY,
  ciphertext BLOB NOT NULL,
  updated_at_iso TEXT NOT NULL,
  expires_at_iso TEXT NULL
);
CREATE TABLE IF NOT EXISTS emergency_login_key_bundles (
  kid TEXT PRIMARY KEY,
  store_code TEXT NOT NULL,
  public_key_pem TEXT NOT NULL,
  not_before_iso TEXT NOT NULL,
  expires_at_iso TEXT NOT NULL,
  fetched_at_iso TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS trusted_time_anchor (
  anchor_id INTEGER PRIMARY KEY CHECK (anchor_id = 1),
  trusted_at_iso TEXT NOT NULL,
  monotonic_elapsed_ms INTEGER NOT NULL,
  updated_at_iso TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_emergency_login_key_bundles_store_expiry
  ON emergency_login_key_bundles (store_code, expires_at_iso);
`;

const M2 = `
CREATE TABLE IF NOT EXISTS catalog_snapshots (
  snapshot_id TEXT PRIMARY KEY,
  -- 同一服务端版本可被 reset/redownload 下载为新的 staging；只能约束 active 状态唯一。
  catalog_version TEXT NOT NULL,
  checksum TEXT NOT NULL,
  state TEXT NOT NULL CHECK (state IN ('staging', 'active', 'retired')),
  downloaded_at_iso TEXT NOT NULL,
  activated_at_iso TEXT NULL
);
-- 目录切换必须在一个事务中先退役旧快照再激活新快照，始终仅有一个活跃版本。
CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_snapshots_single_active
  ON catalog_snapshots (state) WHERE state = 'active';
CREATE TABLE IF NOT EXISTS catalog_items (
  snapshot_id TEXT NOT NULL REFERENCES catalog_snapshots(snapshot_id),
  store_code TEXT NOT NULL,
  -- 服务器的售卖身份是门店 + 规范化售卖码；一个商品可有条码、套装、清仓等多行且价格不同。
  lookup_code_normalized TEXT NOT NULL,
  product_code TEXT NOT NULL,
  reference_code TEXT NULL,
  item_number TEXT NULL,
  barcode TEXT NULL,
  lookup_code TEXT NOT NULL,
  display_name TEXT NOT NULL,
  retail_price_cents INTEGER NOT NULL,
  price_source INTEGER NOT NULL,
  price_source_label TEXT NOT NULL,
  quantity_factor TEXT NOT NULL,
  tax_rate_basis_points INTEGER NULL,
  row_version TEXT NULL,
  product_image TEXT NULL,
  discount_rate TEXT NULL,
  is_special_product INTEGER NOT NULL CHECK (is_special_product IN (0, 1)),
  is_active INTEGER NOT NULL CHECK (is_active IN (0, 1)),
  updated_at_iso TEXT NULL,
  PRIMARY KEY (snapshot_id, store_code, lookup_code_normalized)
);
CREATE TABLE IF NOT EXISTS catalog_promotions (
  snapshot_id TEXT NOT NULL REFERENCES catalog_snapshots(snapshot_id),
  promotion_id TEXT NOT NULL,
  definition_json TEXT NOT NULL,
  valid_from_iso TEXT NULL,
  valid_until_iso TEXT NULL,
  priority INTEGER NOT NULL,
  PRIMARY KEY (snapshot_id, promotion_id)
);
CREATE TABLE IF NOT EXISTS special_products (
  snapshot_id TEXT NOT NULL,
  store_code TEXT NOT NULL,
  lookup_code_normalized TEXT NOT NULL,
  sort_order INTEGER NOT NULL,
  is_marked INTEGER NOT NULL CHECK (is_marked IN (0, 1)),
  updated_at_iso TEXT NULL,
  PRIMARY KEY (snapshot_id, store_code, lookup_code_normalized),
  FOREIGN KEY (snapshot_id, store_code, lookup_code_normalized)
    REFERENCES catalog_items(snapshot_id, store_code, lookup_code_normalized)
);
CREATE INDEX IF NOT EXISTS ix_catalog_items_active_lookup
  ON catalog_items (snapshot_id, store_code, lookup_code_normalized);
CREATE INDEX IF NOT EXISTS ix_catalog_items_active_search
  ON catalog_items (snapshot_id, is_active, display_name COLLATE NOCASE, item_number COLLATE NOCASE, lookup_code_normalized);
CREATE INDEX IF NOT EXISTS ix_special_products_snapshot_sort ON special_products (snapshot_id, sort_order);
`;

const M3 = `
CREATE TABLE IF NOT EXISTS local_orders (
  order_guid TEXT PRIMARY KEY,
  local_sequence INTEGER NOT NULL UNIQUE,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  cashier_id TEXT NOT NULL,
  cashier_name TEXT NOT NULL,
  sold_at_iso TEXT NOT NULL,
  state TEXT NOT NULL,
  total_cents INTEGER NOT NULL,
  discount_cents INTEGER NOT NULL,
  actual_amount_cents INTEGER NOT NULL,
  original_order_guid TEXT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS local_order_lines (
  line_id TEXT PRIMARY KEY,
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid) ON DELETE CASCADE,
  line_sequence INTEGER NOT NULL,
  product_code TEXT NOT NULL,
  item_number TEXT NULL,
  lookup_code TEXT NOT NULL,
  display_name TEXT NOT NULL,
  quantity TEXT NOT NULL,
  unit_price_cents INTEGER NOT NULL,
  discount_cents INTEGER NOT NULL,
  actual_amount_cents INTEGER NOT NULL,
  price_source TEXT NOT NULL,
  line_kind TEXT NOT NULL,
  return_source_key TEXT NULL,
  original_order_guid TEXT NULL,
  original_order_detail_guid TEXT NULL,
  UNIQUE (order_guid, line_sequence)
);
CREATE TABLE IF NOT EXISTS order_tenders (
  tender_guid TEXT PRIMARY KEY,
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid) ON DELETE CASCADE,
  method TEXT NOT NULL,
  amount_cents INTEGER NOT NULL,
  payment_attempt_id TEXT NULL,
  created_at_iso TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS return_capacity (
  return_source_key TEXT PRIMARY KEY,
  original_order_guid TEXT NOT NULL,
  original_order_detail_guid TEXT NULL,
  original_quantity TEXT NOT NULL,
  remaining_quantity TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS held_orders (
  hold_id TEXT PRIMARY KEY,
  local_sequence INTEGER NOT NULL UNIQUE,
  cart_ciphertext BLOB NOT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL
);
-- 同一确认意图即使在进程被杀后重放，也只能对应同一笔本地订单。
CREATE TABLE IF NOT EXISTS cash_checkout_intents (
  checkout_intent_id TEXT PRIMARY KEY,
  request_signature TEXT NOT NULL,
  order_guid TEXT NOT NULL UNIQUE REFERENCES local_orders(order_guid),
  cash_due_cents INTEGER NOT NULL,
  change_cents INTEGER NOT NULL,
  completed_at_iso TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_local_orders_state_sequence ON local_orders (state, local_sequence);
CREATE INDEX IF NOT EXISTS ix_local_orders_store_sold ON local_orders (store_code, sold_at_iso);
CREATE INDEX IF NOT EXISTS ix_local_order_lines_order ON local_order_lines (order_guid, line_sequence);
CREATE INDEX IF NOT EXISTS ix_order_tenders_order ON order_tenders (order_guid);
-- 一次卡/券授权只能被一个 tender 消费；否则 Approved 的恢复判断会被重复绑定污染。
CREATE UNIQUE INDEX IF NOT EXISTS ux_order_tenders_payment_attempt
  ON order_tenders (payment_attempt_id) WHERE payment_attempt_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_cash_checkout_intents_order ON cash_checkout_intents (order_guid);
CREATE INDEX IF NOT EXISTS ix_return_capacity_order ON return_capacity (original_order_guid);
`;

const M4 = `
CREATE TABLE IF NOT EXISTS payment_attempts (
  attempt_id TEXT PRIMARY KEY,
  idempotency_key TEXT NOT NULL UNIQUE,
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid),
  provider TEXT NOT NULL,
  operation TEXT NOT NULL,
  amount_cents INTEGER NOT NULL,
  state TEXT NOT NULL,
  checkout_id TEXT NULL,
  payment_id TEXT NULL,
  session_id TEXT NULL,
  txn_ref TEXT NULL,
  rfn TEXT NULL,
  provider_payload_ciphertext BLOB NULL,
  provider_receipt_ciphertext BLOB NULL,
  provider_response_code TEXT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  last_error_code TEXT NULL
);
CREATE TABLE IF NOT EXISTS outbox_messages (
  message_id TEXT PRIMARY KEY,
  aggregate_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  state TEXT NOT NULL DEFAULT 'pending',
  attempt_count INTEGER NOT NULL DEFAULT 0,
  next_attempt_at_iso TEXT NOT NULL,
  lease_id TEXT NULL,
  lease_expires_at_iso TEXT NULL,
  last_error_code TEXT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS audit_events (
  event_id TEXT PRIMARY KEY,
  event_type TEXT NOT NULL,
  occurred_at_iso TEXT NOT NULL,
  order_guid TEXT NULL REFERENCES local_orders(order_guid),
  correlation_id TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  uploaded_at_iso TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_payment_attempts_order_state ON payment_attempts (order_guid, state);
CREATE INDEX IF NOT EXISTS ix_payment_attempts_approved_recovery
  ON payment_attempts (order_guid, state, attempt_id, amount_cents, provider);
-- 订单补传按 OrderGuid 至多一个 outbox；审计批次允许同一设备/聚合产生多条记录。
CREATE UNIQUE INDEX IF NOT EXISTS ux_outbox_order_sync_aggregate
  ON outbox_messages (aggregate_id) WHERE kind = 'order-sync';
CREATE INDEX IF NOT EXISTS ix_outbox_lease_ready ON outbox_messages (state, next_attempt_at_iso, lease_expires_at_iso);
CREATE INDEX IF NOT EXISTS ix_audit_events_pending ON audit_events (uploaded_at_iso, occurred_at_iso);
`;

const M5 = `
CREATE TABLE IF NOT EXISTS print_jobs (
  job_id TEXT PRIMARY KEY,
  order_guid TEXT NULL REFERENCES local_orders(order_guid),
  state TEXT NOT NULL,
  printer_id TEXT NOT NULL,
  receipt_ciphertext BLOB NOT NULL,
  is_reprint INTEGER NOT NULL DEFAULT 0 CHECK (is_reprint IN (0, 1)),
  retry_count INTEGER NOT NULL DEFAULT 0,
  last_error_code TEXT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS drawer_events (
  event_id TEXT PRIMARY KEY,
  order_guid TEXT NULL REFERENCES local_orders(order_guid),
  print_job_id TEXT NULL REFERENCES print_jobs(job_id),
  state TEXT NOT NULL,
  reason TEXT NOT NULL,
  retry_count INTEGER NOT NULL DEFAULT 0,
  requested_at_iso TEXT NULL,
  completed_at_iso TEXT NULL,
  last_error_code TEXT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_print_jobs_state_created ON print_jobs (state, created_at_iso);
CREATE INDEX IF NOT EXISTS ix_drawer_events_state ON drawer_events (state, requested_at_iso);
`;

const M6 = `
CREATE TABLE IF NOT EXISTS installments (
  installment_id TEXT PRIMARY KEY,
  remote_installment_id TEXT NULL UNIQUE,
  state TEXT NOT NULL,
  customer_ciphertext BLOB NULL,
  note_ciphertext BLOB NULL,
  total_cents INTEGER NOT NULL,
  paid_cents INTEGER NOT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS local_daily_closes (
  close_id TEXT PRIMARY KEY,
  business_date TEXT NOT NULL,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  state TEXT NOT NULL,
  expected_cash_cents INTEGER NOT NULL,
  counted_cash_cents INTEGER NULL,
  variance_cents INTEGER NULL,
  created_at_iso TEXT NOT NULL,
  closed_at_iso TEXT NULL,
  UNIQUE (business_date, store_code, device_code)
);
CREATE TABLE IF NOT EXISTS daily_close_totals (
  close_id TEXT NOT NULL REFERENCES local_daily_closes(close_id) ON DELETE CASCADE,
  tender_method TEXT NOT NULL,
  direction TEXT NOT NULL,
  amount_cents INTEGER NOT NULL,
  PRIMARY KEY (close_id, tender_method, direction)
);
CREATE TABLE IF NOT EXISTS cash_denominations (
  close_id TEXT NOT NULL REFERENCES local_daily_closes(close_id) ON DELETE CASCADE,
  denomination_cents INTEGER NOT NULL,
  quantity INTEGER NOT NULL,
  PRIMARY KEY (close_id, denomination_cents)
);
CREATE TABLE IF NOT EXISTS advertisement_cache_metadata (
  asset_id TEXT PRIMARY KEY,
  local_uri TEXT NOT NULL,
  sha256 TEXT NOT NULL,
  content_type TEXT NOT NULL,
  revision INTEGER NOT NULL,
  expires_at_iso TEXT NULL,
  downloaded_at_iso TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_installments_state ON installments (state, updated_at_iso);
CREATE INDEX IF NOT EXISTS ix_advertisement_cache_revision ON advertisement_cache_metadata (revision);
`;

const M7_PRINTER_COLUMN = "ALTER TABLE drawer_events ADD COLUMN printer_id TEXT NULL;";

const M7 = `
-- 钱箱通过打印机 RJ11 触发，必须永久绑定创建任务时的打印机，不能跟随设置变化。
${M7_PRINTER_COLUMN}

-- 旧版本只保存 print_job_id：仅在原打印任务仍有有效绑定时进行确定性回填。
UPDATE drawer_events
SET printer_id = (
  SELECT print_jobs.printer_id
  FROM print_jobs
  WHERE print_jobs.job_id = drawer_events.print_job_id
    AND TRIM(print_jobs.printer_id) <> ''
)
WHERE printer_id IS NULL
  AND print_job_id IS NOT NULL;

-- 无法确定外设的遗留动作绝不能自动或人工盲目重放。
UPDATE drawer_events
SET state = 'Unknown',
    last_error_code = 'DRAWER_PRINTER_BINDING_MISSING_MIGRATION',
    updated_at_iso = COALESCE(updated_at_iso, created_at_iso)
WHERE (printer_id IS NULL OR TRIM(printer_id) = '')
  AND state IN ('Required', 'Requested', 'Failed');

CREATE TRIGGER IF NOT EXISTS trg_print_jobs_require_printer_id
BEFORE INSERT ON print_jobs
FOR EACH ROW
WHEN NEW.printer_id IS NULL OR TRIM(NEW.printer_id) = ''
BEGIN
  SELECT RAISE(ABORT, 'PRINT_JOB_PRINTER_ID_REQUIRED');
END;

CREATE TRIGGER IF NOT EXISTS trg_print_jobs_printer_binding_immutable
BEFORE UPDATE OF printer_id ON print_jobs
FOR EACH ROW
WHEN NEW.printer_id IS NOT OLD.printer_id
BEGIN
  SELECT RAISE(ABORT, 'PRINT_JOB_PRINTER_BINDING_IMMUTABLE');
END;

CREATE TRIGGER IF NOT EXISTS trg_drawer_events_require_printer_id
BEFORE INSERT ON drawer_events
FOR EACH ROW
WHEN NEW.printer_id IS NULL OR TRIM(NEW.printer_id) = ''
BEGIN
  SELECT RAISE(ABORT, 'DRAWER_PRINTER_ID_REQUIRED');
END;

CREATE TRIGGER IF NOT EXISTS trg_drawer_events_match_print_job_printer
BEFORE INSERT ON drawer_events
FOR EACH ROW
WHEN NEW.print_job_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM print_jobs
    WHERE print_jobs.job_id = NEW.print_job_id
      AND print_jobs.printer_id = NEW.printer_id
  )
BEGIN
  SELECT RAISE(ABORT, 'DRAWER_PRINTER_ID_MISMATCH');
END;

CREATE TRIGGER IF NOT EXISTS trg_drawer_events_printer_binding_immutable
BEFORE UPDATE OF printer_id, print_job_id ON drawer_events
FOR EACH ROW
WHEN NEW.printer_id IS NOT OLD.printer_id
  OR NEW.print_job_id IS NOT OLD.print_job_id
BEGIN
  SELECT RAISE(ABORT, 'DRAWER_PRINTER_BINDING_IMMUTABLE');
END;

CREATE INDEX IF NOT EXISTS ix_drawer_events_printer_state
  ON drawer_events (printer_id, state, created_at_iso);
`;

// applyMigrations 已通过 PRAGMA 精确补列；执行 M7 时仅跳过重复 ALTER。
// 保留完整 M7 SQL 供 fresh schema 契约测试和离线建库脚本顺序执行。
const M7_AFTER_SCHEMA_REPAIR = M7.replace(M7_PRINTER_COLUMN, "");

const M8 = `
-- UI action 必须先于 provider attempt 持久化；attempt 此刻尚不存在，因此不设 attempt FK。
CREATE TABLE IF NOT EXISTS payment_action_bindings (
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid),
  action_id TEXT NOT NULL,
  request_signature TEXT NOT NULL,
  attempt_id TEXT NOT NULL UNIQUE,
  idempotency_key TEXT NOT NULL UNIQUE,
  created_at_iso TEXT NOT NULL,
  PRIMARY KEY (order_guid, action_id),
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (TRIM(action_id) <> '' AND LENGTH(action_id) <= 128),
  CHECK (TRIM(request_signature) <> '' AND LENGTH(request_signature) <= 1024),
  CHECK (TRIM(attempt_id) <> '' AND LENGTH(attempt_id) <= 128),
  CHECK (TRIM(idempotency_key) <> '' AND LENGTH(idempotency_key) <= 256),
  CHECK (TRIM(created_at_iso) <> '' AND LENGTH(created_at_iso) <= 64)
);

CREATE TRIGGER IF NOT EXISTS trg_payment_action_bindings_immutable_update
BEFORE UPDATE ON payment_action_bindings
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ACTION_BINDING_IMMUTABLE');
END;

CREATE TRIGGER IF NOT EXISTS trg_payment_action_bindings_immutable_delete
BEFORE DELETE ON payment_action_bindings
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ACTION_BINDING_IMMUTABLE');
END;

-- reversal 只保存不可变的关联事实；旧负 tender 没有可靠 source 时保持无关联。
CREATE TABLE IF NOT EXISTS payment_tender_reversal_links (
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid),
  action_id TEXT NOT NULL,
  source_tender_guid TEXT NOT NULL REFERENCES order_tenders(tender_guid),
  reversal_tender_guid TEXT NOT NULL REFERENCES order_tenders(tender_guid),
  created_at_iso TEXT NOT NULL,
  PRIMARY KEY (order_guid, action_id),
  UNIQUE (source_tender_guid),
  UNIQUE (reversal_tender_guid),
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (TRIM(action_id) <> '' AND LENGTH(action_id) <= 128),
  CHECK (TRIM(source_tender_guid) <> '' AND LENGTH(source_tender_guid) <= 128),
  CHECK (TRIM(reversal_tender_guid) <> '' AND LENGTH(reversal_tender_guid) <= 128),
  CHECK (TRIM(created_at_iso) <> '' AND LENGTH(created_at_iso) <= 64)
);

CREATE TRIGGER IF NOT EXISTS trg_payment_tender_reversal_links_validate
BEFORE INSERT ON payment_tender_reversal_links
FOR EACH ROW
WHEN NOT EXISTS (
  SELECT 1
  FROM order_tenders source
  INNER JOIN order_tenders reversal
    ON reversal.tender_guid = NEW.reversal_tender_guid
  WHERE source.tender_guid = NEW.source_tender_guid
    AND source.order_guid = NEW.order_guid
    AND reversal.order_guid = NEW.order_guid
    AND source.tender_guid <> reversal.tender_guid
    AND source.method = reversal.method
    AND source.amount_cents > 0
    AND reversal.amount_cents = -source.amount_cents
    AND NOT EXISTS (
      SELECT 1
      FROM payment_tender_reversal_links prior
      WHERE prior.reversal_tender_guid = source.tender_guid
    )
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_TENDER_REVERSAL_LINK_INVALID');
END;

CREATE TRIGGER IF NOT EXISTS trg_payment_tender_reversal_links_immutable_update
BEFORE UPDATE ON payment_tender_reversal_links
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_TENDER_REVERSAL_LINK_IMMUTABLE');
END;

CREATE TRIGGER IF NOT EXISTS trg_payment_tender_reversal_links_immutable_delete
BEFORE DELETE ON payment_tender_reversal_links
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_TENDER_REVERSAL_LINK_IMMUTABLE');
END;
`;

/**
 * V2 挂单和 M3 的 legacy held_orders 并存。V2 保存完整的定价快照，
 * 并以 scope 限制到同一门店、同一设备，绝不把挂单伪装成本地订单或同步消息。
 */
const M9 = `
CREATE TABLE IF NOT EXISTS held_order_records (
  hold_id TEXT PRIMARY KEY,
  local_sequence INTEGER NOT NULL UNIQUE,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  held_by_cashier_id TEXT NOT NULL,
  held_by_cashier_name TEXT NOT NULL,
  status TEXT NOT NULL CHECK (status IN ('Pending', 'Recalling', 'Recalled')),
  payload_version INTEGER NOT NULL CHECK (payload_version = 1),
  payload_ciphertext BLOB NOT NULL,
  item_count INTEGER NOT NULL CHECK (item_count > 0),
  subtotal_cents INTEGER NOT NULL,
  discount_cents INTEGER NOT NULL CHECK (discount_cents >= 0),
  actual_amount_cents INTEGER NOT NULL,
  recalling_at_iso TEXT NULL,
  recall_attempt_id TEXT NULL UNIQUE,
  recalling_cashier_id TEXT NULL,
  recalling_cashier_name TEXT NULL,
  recalled_at_iso TEXT NULL,
  held_at_iso TEXT NOT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  CHECK (
    (status = 'Pending'
      AND recalling_at_iso IS NULL
      AND recall_attempt_id IS NULL
      AND recalling_cashier_id IS NULL
      AND recalling_cashier_name IS NULL
      AND recalled_at_iso IS NULL)
    OR
    (status = 'Recalling'
      AND recalling_at_iso IS NOT NULL
      AND recall_attempt_id IS NOT NULL
      AND recalling_cashier_id IS NOT NULL
      AND recalling_cashier_name IS NOT NULL
      AND recalled_at_iso IS NULL)
    OR
    (status = 'Recalled'
      AND recalling_at_iso IS NOT NULL
      AND recall_attempt_id IS NOT NULL
      AND recalling_cashier_id IS NOT NULL
      AND recalling_cashier_name IS NOT NULL
      AND recalled_at_iso IS NOT NULL)
  ),
  CHECK (TRIM(hold_id) <> '' AND LENGTH(hold_id) <= 128),
  CHECK (TRIM(store_code) <> '' AND LENGTH(store_code) <= 64),
  CHECK (TRIM(device_code) <> '' AND LENGTH(device_code) <= 128),
  CHECK (TRIM(held_by_cashier_id) <> '' AND LENGTH(held_by_cashier_id) <= 128),
  CHECK (TRIM(held_by_cashier_name) <> '' AND LENGTH(held_by_cashier_name) <= 256),
  CHECK (TRIM(held_at_iso) <> '' AND LENGTH(held_at_iso) <= 64),
  CHECK (TRIM(created_at_iso) <> '' AND LENGTH(created_at_iso) <= 64),
  CHECK (TRIM(updated_at_iso) <> '' AND LENGTH(updated_at_iso) <= 64)
);

CREATE INDEX IF NOT EXISTS ix_held_order_records_scope_pending
  ON held_order_records (store_code, device_code, status, local_sequence DESC);
`;

/**
 * M10 只保存终端购物车工作流的耐久栅栏，不保存购物车内容。每个门店/设备
 * scope 最多一个栅栏；hold_id 也只能绑定一个 scope，避免同一挂单被两个
 * 终端同时恢复。现金取单完成前 bound_order_guid 必须保持 NULL。
 */
const M10 = `
CREATE TABLE IF NOT EXISTS terminal_cart_fences (
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  kind TEXT NOT NULL CHECK (kind IN ('HoldClear', 'RecallActive')),
  hold_id TEXT NOT NULL UNIQUE
    REFERENCES held_order_records(hold_id) ON DELETE RESTRICT,
  recall_attempt_id TEXT NULL,
  -- 现金链路必须为 NULL；后续在线支付绑定由对应原子账本再验证订单身份。
  bound_order_guid TEXT NULL,
  created_at_iso TEXT NOT NULL,
  PRIMARY KEY (store_code, device_code),
  CHECK (
    (kind = 'HoldClear'
      AND recall_attempt_id IS NULL
      AND bound_order_guid IS NULL)
    OR
    (kind = 'RecallActive'
      AND recall_attempt_id IS NOT NULL)
  ),
  CHECK (TRIM(store_code) <> '' AND LENGTH(store_code) <= 64),
  CHECK (TRIM(device_code) <> '' AND LENGTH(device_code) <= 128),
  CHECK (TRIM(hold_id) <> '' AND LENGTH(hold_id) <= 128),
  CHECK (
    recall_attempt_id IS NULL
    OR (TRIM(recall_attempt_id) <> '' AND LENGTH(recall_attempt_id) <= 128)
  ),
  CHECK (
    bound_order_guid IS NULL
    OR (TRIM(bound_order_guid) <> '' AND LENGTH(bound_order_guid) <= 128)
  ),
  CHECK (TRIM(created_at_iso) <> '' AND LENGTH(created_at_iso) <= 64),
  FOREIGN KEY (recall_attempt_id)
    REFERENCES held_order_records(recall_attempt_id) ON DELETE RESTRICT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_terminal_cart_fences_recall_attempt
  ON terminal_cart_fences (recall_attempt_id)
  WHERE recall_attempt_id IS NOT NULL;

CREATE TRIGGER IF NOT EXISTS trg_terminal_cart_fences_match_held_order
BEFORE INSERT ON terminal_cart_fences
FOR EACH ROW
WHEN NOT EXISTS (
  SELECT 1
  FROM held_order_records held
  WHERE held.hold_id = NEW.hold_id
    AND held.store_code = NEW.store_code
    AND held.device_code = NEW.device_code
    AND (
      (NEW.kind = 'HoldClear'
        AND held.status = 'Pending'
        AND held.recall_attempt_id IS NULL)
      OR
      (NEW.kind = 'RecallActive'
        AND held.status = 'Recalling'
        AND held.recall_attempt_id = NEW.recall_attempt_id)
    )
)
BEGIN
  SELECT RAISE(ABORT, 'TERMINAL_CART_FENCE_HELD_ORDER_MISMATCH');
END;

CREATE TRIGGER IF NOT EXISTS trg_terminal_cart_fences_update_matches_held_order
BEFORE UPDATE ON terminal_cart_fences
FOR EACH ROW
WHEN NOT EXISTS (
  SELECT 1
  FROM held_order_records held
  WHERE held.hold_id = NEW.hold_id
    AND held.store_code = NEW.store_code
    AND held.device_code = NEW.device_code
    AND (
      (NEW.kind = 'HoldClear'
        AND held.status = 'Pending'
        AND held.recall_attempt_id IS NULL)
      OR
      (NEW.kind = 'RecallActive'
        AND held.status = 'Recalling'
        AND held.recall_attempt_id = NEW.recall_attempt_id)
    )
)
BEGIN
  SELECT RAISE(ABORT, 'TERMINAL_CART_FENCE_HELD_ORDER_MISMATCH');
END;

-- M9 开发构建若在 Recalling 中升级，持久 attempt 是唯一可证明的恢复身份。
-- 同一 scope 若存在多笔 Recalling，唯一约束让迁移整体失败关闭，禁止猜一笔。
INSERT INTO terminal_cart_fences (
  store_code, device_code, kind, hold_id, recall_attempt_id,
  bound_order_guid, created_at_iso
)
SELECT store_code, device_code, 'RecallActive', hold_id, recall_attempt_id,
  NULL, recalling_at_iso
FROM held_order_records
WHERE status = 'Recalling';
`;

/**
 * M11 只增加支付恢复所需的不可变绑定与受保护状态：
 * - mixed cash action 将 actionId 永久绑定到一笔现金 tender；
 * - voucher 明文列只保存不可逆句柄和 attempt/order 幂等身份，完整状态保存在密文；
 * - payment draft binding 保证同一页面确认在崩溃重放后仍复用原 OrderGuid。
 */
const M11 = `
CREATE TABLE IF NOT EXISTS mixed_cash_tender_actions (
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  action_id TEXT NOT NULL,
  amount_cents INTEGER NOT NULL CHECK (amount_cents > 0),
  tender_guid TEXT NOT NULL UNIQUE REFERENCES order_tenders(tender_guid) ON DELETE RESTRICT,
  audit_event_id TEXT NOT NULL UNIQUE REFERENCES audit_events(event_id) ON DELETE RESTRICT,
  created_at_iso TEXT NOT NULL,
  PRIMARY KEY (order_guid, action_id),
  CHECK (TRIM(action_id) <> '' AND LENGTH(action_id) <= 128),
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (TRIM(tender_guid) <> '' AND LENGTH(tender_guid) <= 128),
  CHECK (TRIM(audit_event_id) <> '' AND LENGTH(audit_event_id) <= 128),
  CHECK (TRIM(created_at_iso) <> '' AND LENGTH(created_at_iso) <= 64)
);

CREATE TRIGGER IF NOT EXISTS trg_mixed_cash_tender_actions_immutable_update
BEFORE UPDATE ON mixed_cash_tender_actions
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'MIXED_CASH_ACTION_IMMUTABLE');
END;

CREATE TRIGGER IF NOT EXISTS trg_mixed_cash_tender_actions_immutable_delete
BEFORE DELETE ON mixed_cash_tender_actions
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'MIXED_CASH_ACTION_IMMUTABLE');
END;

CREATE TABLE IF NOT EXISTS voucher_protected_attempt_states (
  protected_reference TEXT PRIMARY KEY,
  attempt_id TEXT NOT NULL UNIQUE REFERENCES payment_attempts(attempt_id) ON DELETE RESTRICT,
  idempotency_key TEXT NOT NULL UNIQUE,
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  state_ciphertext BLOB NOT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  CHECK (
    protected_reference GLOB 'vpr_[A-Za-z0-9_-]*'
    AND LENGTH(protected_reference) BETWEEN 20 AND 128
  ),
  CHECK (TRIM(attempt_id) <> '' AND LENGTH(attempt_id) <= 128),
  CHECK (TRIM(idempotency_key) <> '' AND LENGTH(idempotency_key) <= 256),
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (LENGTH(state_ciphertext) > 0),
  CHECK (TRIM(created_at_iso) <> '' AND LENGTH(created_at_iso) <= 64),
  CHECK (TRIM(updated_at_iso) <> '' AND LENGTH(updated_at_iso) <= 64)
);

CREATE TRIGGER IF NOT EXISTS trg_voucher_protected_state_binding_immutable
BEFORE UPDATE OF protected_reference, attempt_id, idempotency_key, order_guid
ON voucher_protected_attempt_states
FOR EACH ROW
WHEN NEW.protected_reference IS NOT OLD.protected_reference
  OR NEW.attempt_id IS NOT OLD.attempt_id
  OR NEW.idempotency_key IS NOT OLD.idempotency_key
  OR NEW.order_guid IS NOT OLD.order_guid
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_PROTECTED_BINDING_IMMUTABLE');
END;

CREATE TRIGGER IF NOT EXISTS trg_voucher_protected_state_delete_forbidden
BEFORE DELETE ON voucher_protected_attempt_states
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_PROTECTED_STATE_DELETE_FORBIDDEN');
END;

CREATE TABLE IF NOT EXISTS payment_order_draft_bindings (
  draft_id TEXT PRIMARY KEY,
  request_fingerprint TEXT NOT NULL,
  pricing_state_json TEXT NOT NULL,
  order_guid TEXT NOT NULL UNIQUE REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  state TEXT NOT NULL CHECK (state IN ('Active', 'Abandoned')),
  abandon_action_id TEXT NULL UNIQUE,
  abandon_audit_event_id TEXT NULL UNIQUE
    REFERENCES audit_events(event_id) ON DELETE RESTRICT,
  abandoned_at_iso TEXT NULL,
  created_at_iso TEXT NOT NULL,
  CHECK (TRIM(draft_id) <> '' AND LENGTH(draft_id) <= 128),
  CHECK (
    TRIM(request_fingerprint) <> ''
    AND LENGTH(request_fingerprint) <= 1048576
  ),
  CHECK (
    TRIM(pricing_state_json) <> ''
    AND SUBSTR(LTRIM(pricing_state_json), 1, 1) = '{'
    AND LENGTH(pricing_state_json) <= 1048576
  ),
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (TRIM(store_code) <> '' AND LENGTH(store_code) <= 64),
  CHECK (TRIM(device_code) <> '' AND LENGTH(device_code) <= 128),
  CHECK (
    (state = 'Active'
      AND abandon_action_id IS NULL
      AND abandon_audit_event_id IS NULL
      AND abandoned_at_iso IS NULL)
    OR
    (state = 'Abandoned'
      AND abandon_action_id IS NOT NULL
      AND TRIM(abandon_action_id) <> ''
      AND LENGTH(abandon_action_id) <= 128
      AND abandon_audit_event_id IS NOT NULL
      AND abandoned_at_iso IS NOT NULL)
  ),
  CHECK (TRIM(created_at_iso) <> '' AND LENGTH(created_at_iso) <= 64)
);

CREATE INDEX IF NOT EXISTS ix_payment_order_draft_scope
  ON payment_order_draft_bindings (
    store_code, device_code, state, created_at_iso
  );

CREATE TRIGGER IF NOT EXISTS trg_payment_order_draft_binding_identity_immutable
BEFORE UPDATE OF draft_id, request_fingerprint, pricing_state_json, order_guid,
  store_code, device_code, created_at_iso
ON payment_order_draft_bindings
FOR EACH ROW
WHEN NEW.draft_id IS NOT OLD.draft_id
  OR NEW.request_fingerprint IS NOT OLD.request_fingerprint
  OR NEW.pricing_state_json IS NOT OLD.pricing_state_json
  OR NEW.order_guid IS NOT OLD.order_guid
  OR NEW.store_code IS NOT OLD.store_code
  OR NEW.device_code IS NOT OLD.device_code
  OR NEW.created_at_iso IS NOT OLD.created_at_iso
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_IDENTITY_IMMUTABLE');
END;

CREATE TRIGGER IF NOT EXISTS trg_payment_order_draft_binding_abandon_only
BEFORE UPDATE OF state, abandon_action_id, abandon_audit_event_id, abandoned_at_iso
ON payment_order_draft_bindings
FOR EACH ROW
WHEN NOT (
  OLD.state = 'Active'
  AND OLD.abandon_action_id IS NULL
  AND OLD.abandon_audit_event_id IS NULL
  AND OLD.abandoned_at_iso IS NULL
  AND NEW.state = 'Abandoned'
  AND NEW.abandon_action_id IS NOT NULL
  AND TRIM(NEW.abandon_action_id) <> ''
  AND NEW.abandon_audit_event_id IS NOT NULL
  AND NEW.abandoned_at_iso IS NOT NULL
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_INVALID_TRANSITION');
END;

CREATE TRIGGER IF NOT EXISTS trg_payment_order_draft_bindings_immutable_delete
BEFORE DELETE ON payment_order_draft_bindings
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_BINDING_IMMUTABLE');
END;

-- local_order_lines.line_id 是全库主键；同一购物车安全取消后再次结账时，
-- cart line id 可以合法复用，因此必须保存订单内 line id 与原 cart line id 的映射。
CREATE TABLE IF NOT EXISTS payment_order_draft_line_bindings (
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  cart_line_id TEXT NOT NULL,
  order_line_id TEXT NOT NULL UNIQUE
    REFERENCES local_order_lines(line_id) ON DELETE RESTRICT,
  line_sequence INTEGER NOT NULL CHECK (line_sequence > 0),
  PRIMARY KEY (order_guid, cart_line_id),
  UNIQUE (order_guid, line_sequence),
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (TRIM(cart_line_id) <> '' AND LENGTH(cart_line_id) <= 128),
  CHECK (TRIM(order_line_id) <> '' AND LENGTH(order_line_id) <= 512)
);

CREATE TRIGGER IF NOT EXISTS trg_payment_order_draft_line_binding_immutable_update
BEFORE UPDATE ON payment_order_draft_line_bindings
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_LINE_BINDING_IMMUTABLE');
END;

CREATE TRIGGER IF NOT EXISTS trg_payment_order_draft_line_binding_immutable_delete
BEFORE DELETE ON payment_order_draft_line_bindings
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_LINE_BINDING_IMMUTABLE');
END;

CREATE TABLE IF NOT EXISTS voucher_prepared_contexts (
  protected_reference TEXT PRIMARY KEY,
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  action_id TEXT NOT NULL,
  operation TEXT NOT NULL CHECK (operation IN ('purchase', 'refund')),
  attempt_id TEXT NULL UNIQUE,
  idempotency_key TEXT NULL UNIQUE,
  context_ciphertext BLOB NOT NULL,
  created_at_iso TEXT NOT NULL,
  bound_at_iso TEXT NULL,
  UNIQUE (order_guid, action_id),
  CHECK (
    protected_reference GLOB 'vpc_*'
    AND protected_reference NOT GLOB '*[^A-Za-z0-9_-]*'
    AND LENGTH(protected_reference) BETWEEN 20 AND 128
  ),
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (TRIM(action_id) <> '' AND LENGTH(action_id) <= 128),
  CHECK (
    (attempt_id IS NULL AND idempotency_key IS NULL AND bound_at_iso IS NULL)
    OR
    (attempt_id IS NOT NULL
      AND TRIM(attempt_id) <> ''
      AND LENGTH(attempt_id) <= 128
      AND idempotency_key IS NOT NULL
      AND TRIM(idempotency_key) <> ''
      AND LENGTH(idempotency_key) <= 256
      AND bound_at_iso IS NOT NULL)
  ),
  CHECK (LENGTH(context_ciphertext) > 0),
  CHECK (TRIM(created_at_iso) <> '' AND LENGTH(created_at_iso) <= 64)
);

CREATE TRIGGER IF NOT EXISTS trg_voucher_prepared_context_identity_immutable
BEFORE UPDATE OF protected_reference, order_guid, action_id, operation,
  context_ciphertext, created_at_iso
ON voucher_prepared_contexts
FOR EACH ROW
WHEN NEW.protected_reference IS NOT OLD.protected_reference
  OR NEW.order_guid IS NOT OLD.order_guid
  OR NEW.action_id IS NOT OLD.action_id
  OR NEW.operation IS NOT OLD.operation
  OR NEW.context_ciphertext IS NOT OLD.context_ciphertext
  OR NEW.created_at_iso IS NOT OLD.created_at_iso
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_PREPARED_CONTEXT_IMMUTABLE');
END;

CREATE TRIGGER IF NOT EXISTS trg_voucher_prepared_context_bind_once
BEFORE UPDATE OF attempt_id, idempotency_key, bound_at_iso
ON voucher_prepared_contexts
FOR EACH ROW
WHEN NOT (
  OLD.attempt_id IS NULL
  AND OLD.idempotency_key IS NULL
  AND OLD.bound_at_iso IS NULL
  AND NEW.attempt_id IS NOT NULL
  AND TRIM(NEW.attempt_id) <> ''
  AND NEW.idempotency_key IS NOT NULL
  AND TRIM(NEW.idempotency_key) <> ''
  AND NEW.bound_at_iso IS NOT NULL
)
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_PREPARED_CONTEXT_ALREADY_BOUND');
END;

CREATE TRIGGER IF NOT EXISTS trg_voucher_prepared_context_delete_forbidden
BEFORE DELETE ON voucher_prepared_contexts
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_PREPARED_CONTEXT_DELETE_FORBIDDEN');
END;

CREATE INDEX IF NOT EXISTS ix_local_orders_terminal_state_sequence
  ON local_orders (store_code, device_code, state, local_sequence DESC);

-- 一个终端在任一时刻只能有一笔需要恢复的支付。Approved 在被严格匹配的
-- tender 消费前仍是阻塞态；触发器阻止另开订单重扣。
CREATE TRIGGER IF NOT EXISTS trg_payment_attempts_single_terminal_blocking_insert
BEFORE INSERT ON payment_attempts
FOR EACH ROW
WHEN (
  NEW.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
  OR NEW.state = 'Approved'
)
AND EXISTS (
  SELECT 1
  FROM payment_order_draft_bindings incoming_draft
  WHERE incoming_draft.order_guid = NEW.order_guid
    AND incoming_draft.state = 'Active'
)
AND EXISTS (
  SELECT 1
  FROM payment_attempts prior
  INNER JOIN local_orders prior_order
    ON prior_order.order_guid = prior.order_guid
  INNER JOIN local_orders incoming_order
    ON incoming_order.order_guid = NEW.order_guid
  INNER JOIN payment_order_draft_bindings prior_draft
    ON prior_draft.order_guid = prior.order_guid
   AND prior_draft.state = 'Active'
  WHERE prior.order_guid <> NEW.order_guid
    AND prior_order.store_code = incoming_order.store_code
    AND prior_order.device_code = incoming_order.device_code
    AND (
      prior.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
      OR (
        prior.state = 'Approved'
        AND NOT EXISTS (
          SELECT 1
          FROM order_tenders consumed
          WHERE consumed.payment_attempt_id = prior.attempt_id
            AND consumed.order_guid = prior.order_guid
            AND consumed.amount_cents = prior.amount_cents
            AND (
              (prior.provider IN ('square', 'linkly-cloud') AND consumed.method = 'card')
              OR (prior.provider = 'voucher' AND consumed.method = 'voucher')
            )
        )
      )
    )
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_TERMINAL_BLOCKING_ATTEMPT_EXISTS');
END;

CREATE TRIGGER IF NOT EXISTS trg_payment_attempts_single_terminal_blocking_update
BEFORE UPDATE OF state ON payment_attempts
FOR EACH ROW
WHEN (
  NEW.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
  OR NEW.state = 'Approved'
)
AND EXISTS (
  SELECT 1
  FROM payment_order_draft_bindings incoming_draft
  WHERE incoming_draft.order_guid = NEW.order_guid
    AND incoming_draft.state = 'Active'
)
AND EXISTS (
  SELECT 1
  FROM payment_attempts prior
  INNER JOIN local_orders prior_order
    ON prior_order.order_guid = prior.order_guid
  INNER JOIN local_orders incoming_order
    ON incoming_order.order_guid = NEW.order_guid
  INNER JOIN payment_order_draft_bindings prior_draft
    ON prior_draft.order_guid = prior.order_guid
   AND prior_draft.state = 'Active'
  WHERE prior.attempt_id <> OLD.attempt_id
    AND prior.order_guid <> NEW.order_guid
    AND prior_order.store_code = incoming_order.store_code
    AND prior_order.device_code = incoming_order.device_code
    AND (
      prior.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
      OR (
        prior.state = 'Approved'
        AND NOT EXISTS (
          SELECT 1
          FROM order_tenders consumed
          WHERE consumed.payment_attempt_id = prior.attempt_id
            AND consumed.order_guid = prior.order_guid
            AND consumed.amount_cents = prior.amount_cents
            AND (
              (prior.provider IN ('square', 'linkly-cloud') AND consumed.method = 'card')
              OR (prior.provider = 'voucher' AND consumed.method = 'voucher')
            )
        )
      )
    )
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_TERMINAL_BLOCKING_ATTEMPT_EXISTS');
END;

CREATE TRIGGER IF NOT EXISTS trg_payment_attempts_reject_abandoned_draft
BEFORE INSERT ON payment_attempts
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM payment_order_draft_bindings draft
  WHERE draft.order_guid = NEW.order_guid
    AND draft.state = 'Abandoned'
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_ABANDONED');
END;

CREATE TRIGGER IF NOT EXISTS trg_payment_action_bindings_reject_abandoned_draft
BEFORE INSERT ON payment_action_bindings
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM payment_order_draft_bindings draft
  WHERE draft.order_guid = NEW.order_guid
    AND draft.state = 'Abandoned'
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_ABANDONED');
END;
`;

/**
 * M12a 为明确 Cancelled 且没有活动 tender 的支付草稿增加可审计关闭态。
 * M11 表含 state CHECK，SQLite 无法原地扩展，因此在同一迁移事务中无损重建；
 * 订单、行、action binding 和 attempt 均保持追加式事实，不做删除。
 */
const M12 = `
DROP TRIGGER IF EXISTS trg_payment_order_draft_binding_identity_immutable;
DROP TRIGGER IF EXISTS trg_payment_order_draft_binding_abandon_only;
DROP TRIGGER IF EXISTS trg_payment_order_draft_bindings_immutable_delete;
DROP TRIGGER IF EXISTS trg_payment_attempts_single_terminal_blocking_insert;
DROP TRIGGER IF EXISTS trg_payment_attempts_single_terminal_blocking_update;
DROP TRIGGER IF EXISTS trg_payment_attempts_reject_abandoned_draft;
DROP TRIGGER IF EXISTS trg_payment_action_bindings_reject_abandoned_draft;
DROP TRIGGER IF EXISTS trg_payment_attempts_reject_cancelled_closed_draft;
DROP TRIGGER IF EXISTS trg_payment_action_bindings_reject_cancelled_closed_draft;
DROP INDEX IF EXISTS ix_payment_order_draft_scope;

ALTER TABLE payment_order_draft_bindings
  RENAME TO payment_order_draft_bindings_m11;

CREATE TABLE payment_order_draft_bindings (
  draft_id TEXT PRIMARY KEY,
  request_fingerprint TEXT NOT NULL,
  pricing_state_json TEXT NOT NULL,
  order_guid TEXT NOT NULL UNIQUE
    REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  state TEXT NOT NULL
    CHECK (state IN ('Active', 'Abandoned', 'CancelledClosed')),
  abandon_action_id TEXT NULL UNIQUE,
  abandon_audit_event_id TEXT NULL UNIQUE
    REFERENCES audit_events(event_id) ON DELETE RESTRICT,
  abandoned_at_iso TEXT NULL,
  close_action_id TEXT NULL UNIQUE,
  close_attempt_id TEXT NULL UNIQUE
    REFERENCES payment_attempts(attempt_id) ON DELETE RESTRICT,
  close_audit_event_id TEXT NULL UNIQUE
    REFERENCES audit_events(event_id) ON DELETE RESTRICT,
  closed_at_iso TEXT NULL,
  created_at_iso TEXT NOT NULL,
  CHECK (TRIM(draft_id) <> '' AND LENGTH(draft_id) <= 128),
  CHECK (
    TRIM(request_fingerprint) <> ''
    AND LENGTH(request_fingerprint) <= 1048576
  ),
  CHECK (
    TRIM(pricing_state_json) <> ''
    AND SUBSTR(LTRIM(pricing_state_json), 1, 1) = '{'
    AND LENGTH(pricing_state_json) <= 1048576
  ),
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (TRIM(store_code) <> '' AND LENGTH(store_code) <= 64),
  CHECK (TRIM(device_code) <> '' AND LENGTH(device_code) <= 128),
  CHECK (
    (state = 'Active'
      AND abandon_action_id IS NULL
      AND abandon_audit_event_id IS NULL
      AND abandoned_at_iso IS NULL
      AND close_action_id IS NULL
      AND close_attempt_id IS NULL
      AND close_audit_event_id IS NULL
      AND closed_at_iso IS NULL)
    OR
    (state = 'Abandoned'
      AND abandon_action_id IS NOT NULL
      AND TRIM(abandon_action_id) <> ''
      AND LENGTH(abandon_action_id) <= 128
      AND abandon_audit_event_id IS NOT NULL
      AND abandoned_at_iso IS NOT NULL
      AND close_action_id IS NULL
      AND close_attempt_id IS NULL
      AND close_audit_event_id IS NULL
      AND closed_at_iso IS NULL)
    OR
    (state = 'CancelledClosed'
      AND abandon_action_id IS NULL
      AND abandon_audit_event_id IS NULL
      AND abandoned_at_iso IS NULL
      AND close_action_id IS NOT NULL
      AND TRIM(close_action_id) <> ''
      AND LENGTH(close_action_id) <= 128
      AND close_attempt_id IS NOT NULL
      AND close_audit_event_id IS NOT NULL
      AND closed_at_iso IS NOT NULL)
  ),
  CHECK (TRIM(created_at_iso) <> '' AND LENGTH(created_at_iso) <= 64)
);

INSERT INTO payment_order_draft_bindings (
  draft_id, request_fingerprint, pricing_state_json, order_guid,
  store_code, device_code, state, abandon_action_id,
  abandon_audit_event_id, abandoned_at_iso, close_action_id,
  close_attempt_id, close_audit_event_id, closed_at_iso, created_at_iso
)
SELECT
  draft_id, request_fingerprint, pricing_state_json, order_guid,
  store_code, device_code, state, abandon_action_id,
  abandon_audit_event_id, abandoned_at_iso, NULL, NULL, NULL, NULL,
  created_at_iso
FROM payment_order_draft_bindings_m11;

DROP TABLE payment_order_draft_bindings_m11;

CREATE INDEX ix_payment_order_draft_scope
  ON payment_order_draft_bindings (
    store_code, device_code, state, created_at_iso
  );

CREATE TRIGGER trg_payment_order_draft_binding_identity_immutable
BEFORE UPDATE OF draft_id, request_fingerprint, pricing_state_json, order_guid,
  store_code, device_code, created_at_iso
ON payment_order_draft_bindings
FOR EACH ROW
WHEN NEW.draft_id IS NOT OLD.draft_id
  OR NEW.request_fingerprint IS NOT OLD.request_fingerprint
  OR NEW.pricing_state_json IS NOT OLD.pricing_state_json
  OR NEW.order_guid IS NOT OLD.order_guid
  OR NEW.store_code IS NOT OLD.store_code
  OR NEW.device_code IS NOT OLD.device_code
  OR NEW.created_at_iso IS NOT OLD.created_at_iso
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_IDENTITY_IMMUTABLE');
END;

CREATE TRIGGER trg_payment_order_draft_binding_terminal_only
BEFORE UPDATE OF state, abandon_action_id, abandon_audit_event_id,
  abandoned_at_iso, close_action_id, close_attempt_id,
  close_audit_event_id, closed_at_iso
ON payment_order_draft_bindings
FOR EACH ROW
WHEN NOT (
  OLD.state = 'Active'
  AND OLD.abandon_action_id IS NULL
  AND OLD.abandon_audit_event_id IS NULL
  AND OLD.abandoned_at_iso IS NULL
  AND OLD.close_action_id IS NULL
  AND OLD.close_attempt_id IS NULL
  AND OLD.close_audit_event_id IS NULL
  AND OLD.closed_at_iso IS NULL
  AND (
    (NEW.state = 'Abandoned'
      AND NEW.abandon_action_id IS NOT NULL
      AND TRIM(NEW.abandon_action_id) <> ''
      AND NEW.abandon_audit_event_id IS NOT NULL
      AND NEW.abandoned_at_iso IS NOT NULL
      AND NEW.close_action_id IS NULL
      AND NEW.close_attempt_id IS NULL
      AND NEW.close_audit_event_id IS NULL
      AND NEW.closed_at_iso IS NULL)
    OR
    (NEW.state = 'CancelledClosed'
      AND NEW.abandon_action_id IS NULL
      AND NEW.abandon_audit_event_id IS NULL
      AND NEW.abandoned_at_iso IS NULL
      AND NEW.close_action_id IS NOT NULL
      AND TRIM(NEW.close_action_id) <> ''
      AND NEW.close_attempt_id IS NOT NULL
      AND NEW.close_audit_event_id IS NOT NULL
      AND NEW.closed_at_iso IS NOT NULL)
  )
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_INVALID_TRANSITION');
END;

CREATE TRIGGER trg_payment_order_draft_bindings_immutable_delete
BEFORE DELETE ON payment_order_draft_bindings
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_BINDING_IMMUTABLE');
END;

-- 一个终端仍只能存在一笔 active blocking payment。关闭态永远不参与恢复。
CREATE TRIGGER trg_payment_attempts_single_terminal_blocking_insert
BEFORE INSERT ON payment_attempts
FOR EACH ROW
WHEN NEW.state IN ('Created', 'Submitted', 'Pending', 'Approved', 'Unknown')
AND EXISTS (
  SELECT 1
  FROM payment_order_draft_bindings incoming_draft
  WHERE incoming_draft.order_guid = NEW.order_guid
    AND incoming_draft.state = 'Active'
)
AND EXISTS (
  SELECT 1
  FROM payment_attempts prior
  INNER JOIN local_orders prior_order
    ON prior_order.order_guid = prior.order_guid
  INNER JOIN local_orders incoming_order
    ON incoming_order.order_guid = NEW.order_guid
  INNER JOIN payment_order_draft_bindings prior_draft
    ON prior_draft.order_guid = prior.order_guid
   AND prior_draft.state = 'Active'
  WHERE prior.order_guid <> NEW.order_guid
    AND prior_order.store_code = incoming_order.store_code
    AND prior_order.device_code = incoming_order.device_code
    AND (
      prior.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
      OR (
        prior.state = 'Approved'
        AND NOT EXISTS (
          SELECT 1
          FROM order_tenders consumed
          WHERE consumed.payment_attempt_id = prior.attempt_id
            AND consumed.order_guid = prior.order_guid
            AND consumed.amount_cents = prior.amount_cents
            AND (
              (prior.provider IN ('square', 'linkly-cloud')
                AND consumed.method = 'card')
              OR (prior.provider = 'voucher'
                AND consumed.method = 'voucher')
            )
        )
      )
    )
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_TERMINAL_BLOCKING_ATTEMPT_EXISTS');
END;

CREATE TRIGGER trg_payment_attempts_single_terminal_blocking_update
BEFORE UPDATE OF state ON payment_attempts
FOR EACH ROW
WHEN NEW.state IN ('Created', 'Submitted', 'Pending', 'Approved', 'Unknown')
AND EXISTS (
  SELECT 1
  FROM payment_order_draft_bindings incoming_draft
  WHERE incoming_draft.order_guid = NEW.order_guid
    AND incoming_draft.state = 'Active'
)
AND EXISTS (
  SELECT 1
  FROM payment_attempts prior
  INNER JOIN local_orders prior_order
    ON prior_order.order_guid = prior.order_guid
  INNER JOIN local_orders incoming_order
    ON incoming_order.order_guid = NEW.order_guid
  INNER JOIN payment_order_draft_bindings prior_draft
    ON prior_draft.order_guid = prior.order_guid
   AND prior_draft.state = 'Active'
  WHERE prior.attempt_id <> OLD.attempt_id
    AND prior.order_guid <> NEW.order_guid
    AND prior_order.store_code = incoming_order.store_code
    AND prior_order.device_code = incoming_order.device_code
    AND (
      prior.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
      OR (
        prior.state = 'Approved'
        AND NOT EXISTS (
          SELECT 1
          FROM order_tenders consumed
          WHERE consumed.payment_attempt_id = prior.attempt_id
            AND consumed.order_guid = prior.order_guid
            AND consumed.amount_cents = prior.amount_cents
            AND (
              (prior.provider IN ('square', 'linkly-cloud')
                AND consumed.method = 'card')
              OR (prior.provider = 'voucher'
                AND consumed.method = 'voucher')
            )
        )
      )
    )
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_TERMINAL_BLOCKING_ATTEMPT_EXISTS');
END;

CREATE TRIGGER trg_payment_attempts_reject_abandoned_draft
BEFORE INSERT ON payment_attempts
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM payment_order_draft_bindings draft
  WHERE draft.order_guid = NEW.order_guid
    AND draft.state = 'Abandoned'
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_ABANDONED');
END;

CREATE TRIGGER trg_payment_action_bindings_reject_abandoned_draft
BEFORE INSERT ON payment_action_bindings
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM payment_order_draft_bindings draft
  WHERE draft.order_guid = NEW.order_guid
    AND draft.state = 'Abandoned'
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_ABANDONED');
END;

CREATE TRIGGER trg_payment_attempts_reject_cancelled_closed_draft
BEFORE INSERT ON payment_attempts
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM payment_order_draft_bindings draft
  WHERE draft.order_guid = NEW.order_guid
    AND draft.state = 'CancelledClosed'
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_CANCELLED_CLOSED');
END;

CREATE TRIGGER trg_payment_action_bindings_reject_cancelled_closed_draft
BEFORE INSERT ON payment_action_bindings
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM payment_order_draft_bindings draft
  WHERE draft.order_guid = NEW.order_guid
    AND draft.state = 'CancelledClosed'
)
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ORDER_DRAFT_CANCELLED_CLOSED');
END;
`;

/**
 * M13 保存退货外部调用前的完整事实和最终本地订单提交计划。
 * provider reference/recovery key 只能进入 BLOB 密文；普通列仅保存本地不透明 ID。
 */
const M13 = `
CREATE TABLE return_tender_capacities (
  capacity_id TEXT PRIMARY KEY,
  original_order_guid TEXT NOT NULL,
  method TEXT NOT NULL
    CHECK (method IN ('cash', 'card', 'voucher', 'installment')),
  original_amount_cents INTEGER NOT NULL CHECK (original_amount_cents >= 0),
  remaining_amount_cents INTEGER NOT NULL CHECK (
    remaining_amount_cents >= 0
    AND remaining_amount_cents <= original_amount_cents
  ),
  protected_context_ciphertext BLOB NULL,
  observed_at_iso TEXT NOT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  CHECK (TRIM(capacity_id) <> '' AND LENGTH(capacity_id) <= 128),
  CHECK (
    TRIM(original_order_guid) <> ''
    AND LENGTH(original_order_guid) <= 128
  ),
  CHECK (
    method = 'cash'
    OR (
      protected_context_ciphertext IS NOT NULL
      AND LENGTH(protected_context_ciphertext) > 0
    )
  )
);

CREATE TRIGGER trg_return_tender_capacity_identity_immutable
BEFORE UPDATE OF capacity_id, original_order_guid, method,
  original_amount_cents, protected_context_ciphertext, created_at_iso
ON return_tender_capacities
FOR EACH ROW
WHEN NEW.capacity_id IS NOT OLD.capacity_id
  OR NEW.original_order_guid IS NOT OLD.original_order_guid
  OR NEW.method IS NOT OLD.method
  OR NEW.original_amount_cents IS NOT OLD.original_amount_cents
  OR NEW.protected_context_ciphertext IS NOT OLD.protected_context_ciphertext
  OR NEW.created_at_iso IS NOT OLD.created_at_iso
BEGIN
  SELECT RAISE(ABORT, 'RETURN_TENDER_CAPACITY_IDENTITY_IMMUTABLE');
END;

CREATE TRIGGER trg_return_tender_capacity_delete_forbidden
BEFORE DELETE ON return_tender_capacities
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_TENDER_CAPACITY_DELETE_FORBIDDEN');
END;

CREATE TABLE return_actions (
  action_id TEXT PRIMARY KEY,
  request_fingerprint TEXT NOT NULL,
  return_order_guid TEXT NOT NULL UNIQUE,
  action_recovery_token TEXT NOT NULL UNIQUE,
  source_kind TEXT NOT NULL
    CHECK (source_kind IN ('receipt', 'no-receipt')),
  total_refund_cents INTEGER NOT NULL CHECK (total_refund_cents > 0),
  online INTEGER NOT NULL CHECK (online IN (0, 1)),
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  cashier_id TEXT NOT NULL,
  cashier_name TEXT NOT NULL,
  session_epoch TEXT NOT NULL,
  supervisor_grant_id TEXT NULL UNIQUE,
  plan_json TEXT NOT NULL,
  state TEXT NOT NULL
    CHECK (state IN ('processing', 'unknown', 'declined', 'completed')),
  created_at_iso TEXT NOT NULL,
  completed_at_iso TEXT NULL,
  updated_at_iso TEXT NOT NULL,
  CHECK (TRIM(action_id) <> '' AND LENGTH(action_id) <= 128),
  CHECK (
    TRIM(request_fingerprint) <> ''
    AND LENGTH(request_fingerprint) <= 1048576
  ),
  CHECK (
    TRIM(return_order_guid) <> ''
    AND LENGTH(return_order_guid) <= 128
  ),
  CHECK (
    TRIM(action_recovery_token) <> ''
    AND LENGTH(action_recovery_token) <= 128
  ),
  CHECK (TRIM(store_code) <> '' AND LENGTH(store_code) <= 64),
  CHECK (TRIM(device_code) <> '' AND LENGTH(device_code) <= 128),
  CHECK (TRIM(cashier_id) <> '' AND LENGTH(cashier_id) <= 128),
  CHECK (TRIM(cashier_name) <> '' AND LENGTH(cashier_name) <= 256),
  CHECK (TRIM(session_epoch) <> '' AND LENGTH(session_epoch) <= 256),
  CHECK (
    (source_kind = 'receipt' AND supervisor_grant_id IS NULL)
    OR (
      source_kind = 'no-receipt'
      AND supervisor_grant_id IS NOT NULL
      AND TRIM(supervisor_grant_id) <> ''
      AND LENGTH(supervisor_grant_id) <= 256
    )
  ),
  CHECK (
    SUBSTR(LTRIM(plan_json), 1, 1) = '{'
    AND LENGTH(plan_json) <= 1048576
  ),
  CHECK (
    (state = 'completed' AND completed_at_iso IS NOT NULL)
    OR (state <> 'completed' AND completed_at_iso IS NULL)
  )
);

CREATE UNIQUE INDEX ux_return_actions_terminal_blocking
  ON return_actions (store_code, device_code)
  WHERE state IN ('processing', 'unknown');
CREATE INDEX ix_return_actions_order
  ON return_actions (return_order_guid, state);

CREATE TRIGGER trg_return_action_identity_immutable
BEFORE UPDATE OF action_id, request_fingerprint, return_order_guid,
  action_recovery_token, source_kind, total_refund_cents, online,
  store_code, device_code, cashier_id, cashier_name, session_epoch,
  supervisor_grant_id, plan_json, created_at_iso
ON return_actions
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_ACTION_IDENTITY_IMMUTABLE');
END;

CREATE TRIGGER trg_return_action_delete_forbidden
BEFORE DELETE ON return_actions
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_ACTION_DELETE_FORBIDDEN');
END;

CREATE TABLE return_supervisor_grant_consumptions (
  supervisor_grant_id TEXT PRIMARY KEY,
  action_id TEXT NOT NULL UNIQUE
    REFERENCES return_actions(action_id) ON DELETE RESTRICT,
  consumed_at_iso TEXT NOT NULL
);

CREATE TRIGGER trg_return_supervisor_grant_immutable_update
BEFORE UPDATE ON return_supervisor_grant_consumptions
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_SUPERVISOR_GRANT_IMMUTABLE');
END;

CREATE TRIGGER trg_return_supervisor_grant_immutable_delete
BEFORE DELETE ON return_supervisor_grant_consumptions
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_SUPERVISOR_GRANT_IMMUTABLE');
END;

CREATE TABLE return_action_lines (
  action_id TEXT NOT NULL
    REFERENCES return_actions(action_id) ON DELETE RESTRICT,
  line_id TEXT NOT NULL,
  line_index INTEGER NOT NULL CHECK (line_index >= 0),
  selection_key TEXT NOT NULL,
  source_kind TEXT NOT NULL CHECK (
    source_kind IN (
      'receipt', 'no-receipt-product', 'no-receipt-open-item'
    )
  ),
  return_source_key TEXT NOT NULL,
  original_order_guid TEXT NULL,
  original_order_detail_guid TEXT NULL,
  product_code TEXT NOT NULL,
  item_number TEXT NULL,
  lookup_code TEXT NOT NULL,
  display_name TEXT NOT NULL,
  quantity INTEGER NOT NULL CHECK (quantity > 0),
  unit_refund_cents INTEGER NOT NULL CHECK (unit_refund_cents > 0),
  signed_amount_cents INTEGER NOT NULL CHECK (signed_amount_cents < 0),
  available_quantity INTEGER NULL CHECK (
    available_quantity IS NULL OR available_quantity > 0
  ),
  remaining_amount_cents INTEGER NULL CHECK (
    remaining_amount_cents IS NULL OR remaining_amount_cents >= 0
  ),
  PRIMARY KEY (action_id, line_id),
  UNIQUE (action_id, line_index),
  UNIQUE (action_id, return_source_key),
  CHECK (TRIM(line_id) <> '' AND LENGTH(line_id) <= 128),
  CHECK (TRIM(selection_key) <> '' AND LENGTH(selection_key) <= 128),
  CHECK (
    TRIM(return_source_key) <> ''
    AND LENGTH(return_source_key) <= 512
  ),
  CHECK (
    (source_kind = 'receipt'
      AND original_order_guid IS NOT NULL
      AND original_order_detail_guid IS NOT NULL
      AND available_quantity IS NOT NULL
      AND remaining_amount_cents IS NOT NULL)
    OR
    (source_kind <> 'receipt'
      AND original_order_guid IS NULL
      AND original_order_detail_guid IS NULL
      AND available_quantity IS NULL
      AND remaining_amount_cents IS NULL)
  )
);

CREATE TRIGGER trg_return_action_lines_immutable_update
BEFORE UPDATE ON return_action_lines
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_ACTION_LINE_IMMUTABLE');
END;

CREATE TRIGGER trg_return_action_lines_immutable_delete
BEFORE DELETE ON return_action_lines
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_ACTION_LINE_IMMUTABLE');
END;

CREATE TABLE return_amount_capacity (
  return_source_key TEXT PRIMARY KEY,
  original_order_guid TEXT NOT NULL,
  original_order_detail_guid TEXT NOT NULL,
  original_amount_cents INTEGER NOT NULL CHECK (original_amount_cents >= 0),
  remaining_amount_cents INTEGER NOT NULL CHECK (
    remaining_amount_cents >= 0
    AND remaining_amount_cents <= original_amount_cents
  ),
  updated_at_iso TEXT NOT NULL
);

CREATE TABLE return_line_capacity_reservations (
  action_id TEXT NOT NULL,
  line_id TEXT NOT NULL,
  return_source_key TEXT NOT NULL,
  quantity INTEGER NOT NULL CHECK (quantity > 0),
  amount_cents INTEGER NOT NULL CHECK (
    typeof(amount_cents) = 'integer' AND amount_cents > 0
  ),
  state TEXT NOT NULL CHECK (state IN ('Reserved', 'Committed', 'Released')),
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  PRIMARY KEY (action_id, line_id),
  FOREIGN KEY (action_id, line_id)
    REFERENCES return_action_lines(action_id, line_id) ON DELETE RESTRICT,
  FOREIGN KEY (return_source_key)
    REFERENCES return_amount_capacity(return_source_key) ON DELETE RESTRICT
);
CREATE INDEX ix_return_line_capacity_active
  ON return_line_capacity_reservations (return_source_key, state);

CREATE TABLE return_action_allocations (
  action_id TEXT NOT NULL
    REFERENCES return_actions(action_id) ON DELETE RESTRICT,
  allocation_id TEXT NOT NULL,
  allocation_index INTEGER NOT NULL CHECK (allocation_index >= 0),
  execution_kind TEXT NOT NULL
    CHECK (execution_kind IN ('offline-cash', 'online-refund')),
  method TEXT NOT NULL
    CHECK (method IN ('cash', 'card', 'voucher', 'installment')),
  signed_amount_cents INTEGER NOT NULL CHECK (signed_amount_cents < 0),
  capacity_id TEXT NULL
    REFERENCES return_tender_capacities(capacity_id) ON DELETE RESTRICT,
  original_order_guid TEXT NULL,
  offline_evidence_id TEXT NULL,
  offline_evidence_remaining_cents INTEGER NULL,
  external_attempt_id TEXT NULL UNIQUE,
  external_attempt_kind TEXT NULL
    CHECK (
      external_attempt_kind IS NULL
      OR external_attempt_kind IN ('payment-provider', 'hbpos-api')
    ),
  external_action_id TEXT NULL,
  durable_attempt_id TEXT NULL UNIQUE,
  status TEXT NOT NULL
    CHECK (status IN ('created', 'submitted', 'completed', 'declined', 'unknown')),
  protected_recovery_ciphertext BLOB NULL,
  capacity_reservation_state TEXT NOT NULL
    CHECK (capacity_reservation_state IN ('None', 'Reserved', 'Committed', 'Released')),
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  PRIMARY KEY (action_id, allocation_id),
  UNIQUE (action_id, allocation_index),
  UNIQUE (action_id, external_action_id),
  CHECK (TRIM(allocation_id) <> '' AND LENGTH(allocation_id) <= 128),
  CHECK (
    (execution_kind = 'offline-cash'
      AND method = 'cash'
      AND capacity_id IS NOT NULL
      AND original_order_guid IS NOT NULL
      AND offline_evidence_id IS NOT NULL
      AND offline_evidence_remaining_cents IS NOT NULL
      AND external_attempt_id IS NULL)
    OR
    (execution_kind = 'online-refund'
      AND offline_evidence_id IS NULL
      AND offline_evidence_remaining_cents IS NULL
      AND external_attempt_id IS NOT NULL)
  ),
  CHECK (
    (external_attempt_kind IS NULL
      AND external_action_id IS NULL
      AND durable_attempt_id IS NULL)
    OR
    (external_attempt_kind IS NOT NULL
      AND external_action_id IS NOT NULL
      AND durable_attempt_id IS NOT NULL)
  ),
  CHECK (
    (capacity_id IS NULL AND capacity_reservation_state = 'None')
    OR
    (capacity_id IS NOT NULL
      AND capacity_reservation_state IN (
        'Reserved', 'Committed', 'Released'
      ))
  )
);
CREATE INDEX ix_return_allocations_capacity_active
  ON return_action_allocations (capacity_id, capacity_reservation_state);

CREATE TABLE return_api_attempts (
  durable_attempt_id TEXT PRIMARY KEY,
  external_attempt_id TEXT NOT NULL UNIQUE,
  return_order_guid TEXT NOT NULL,
  action_id TEXT NOT NULL,
  allocation_id TEXT NOT NULL,
  external_action_id TEXT NOT NULL,
  idempotency_key TEXT NOT NULL UNIQUE,
  method TEXT NOT NULL CHECK (method IN ('cash', 'voucher', 'installment')),
  signed_amount_cents INTEGER NOT NULL CHECK (signed_amount_cents < 0),
  state TEXT NOT NULL CHECK (
    state IN (
      'Created', 'Submitted', 'Pending', 'Approved',
      'Declined', 'Cancelled', 'Unknown'
    )
  ),
  protected_context_ciphertext BLOB NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  UNIQUE (action_id, allocation_id),
  UNIQUE (return_order_guid, external_action_id),
  FOREIGN KEY (action_id, allocation_id)
    REFERENCES return_action_allocations(action_id, allocation_id)
    ON DELETE RESTRICT
);

CREATE TRIGGER trg_return_api_attempt_identity_immutable
BEFORE UPDATE OF durable_attempt_id, external_attempt_id, return_order_guid,
  action_id, allocation_id, external_action_id, idempotency_key,
  method, signed_amount_cents, protected_context_ciphertext, created_at_iso
ON return_api_attempts
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_API_ATTEMPT_IDENTITY_IMMUTABLE');
END;

CREATE TRIGGER trg_return_api_attempt_delete_forbidden
BEFORE DELETE ON return_api_attempts
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_API_ATTEMPT_DELETE_FORBIDDEN');
END;

CREATE TABLE local_return_records (
  return_detail_guid TEXT PRIMARY KEY,
  action_id TEXT NOT NULL
    REFERENCES return_actions(action_id) ON DELETE RESTRICT,
  return_order_guid TEXT NOT NULL
    REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  original_order_guid TEXT NULL,
  original_order_detail_guid TEXT NULL,
  return_source_key TEXT NOT NULL,
  product_code TEXT NOT NULL,
  return_quantity INTEGER NOT NULL CHECK (return_quantity > 0),
  return_amount_cents INTEGER NOT NULL CHECK (return_amount_cents > 0),
  created_at_iso TEXT NOT NULL,
  UNIQUE (action_id, return_source_key)
);

CREATE TABLE return_tender_attempt_bindings (
  tender_guid TEXT PRIMARY KEY
    REFERENCES order_tenders(tender_guid) ON DELETE RESTRICT,
  action_id TEXT NOT NULL,
  allocation_id TEXT NOT NULL,
  external_attempt_kind TEXT NOT NULL
    CHECK (external_attempt_kind IN ('payment-provider', 'hbpos-api')),
  external_action_id TEXT NOT NULL,
  durable_attempt_id TEXT NOT NULL UNIQUE,
  created_at_iso TEXT NOT NULL,
  UNIQUE (action_id, allocation_id),
  FOREIGN KEY (action_id, allocation_id)
    REFERENCES return_action_allocations(action_id, allocation_id)
    ON DELETE RESTRICT
);

CREATE TRIGGER trg_return_tender_attempt_binding_immutable
BEFORE UPDATE ON return_tender_attempt_bindings
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_TENDER_ATTEMPT_BINDING_IMMUTABLE');
END;

CREATE TRIGGER trg_return_tender_attempt_binding_delete_forbidden
BEFORE DELETE ON return_tender_attempt_bindings
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_TENDER_ATTEMPT_BINDING_DELETE_FORBIDDEN');
END;

CREATE TABLE return_fulfilment_plans (
  action_id TEXT PRIMARY KEY
    REFERENCES return_actions(action_id) ON DELETE RESTRICT,
  return_order_guid TEXT NOT NULL UNIQUE
    REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  print_job_id TEXT NOT NULL UNIQUE,
  drawer_event_id TEXT NULL UNIQUE,
  print_receipt INTEGER NOT NULL CHECK (print_receipt = 1),
  drawer_required INTEGER NOT NULL CHECK (drawer_required IN (0, 1)),
  materialized_at_iso TEXT NULL,
  created_at_iso TEXT NOT NULL,
  CHECK (
    (drawer_required = 0 AND drawer_event_id IS NULL)
    OR (drawer_required = 1 AND drawer_event_id IS NOT NULL)
  )
);

CREATE TRIGGER trg_return_fulfilment_plan_identity_immutable
BEFORE UPDATE OF action_id, return_order_guid, print_job_id, drawer_event_id,
  print_receipt, drawer_required, created_at_iso
ON return_fulfilment_plans
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_FULFILMENT_PLAN_IDENTITY_IMMUTABLE');
END;

CREATE TRIGGER trg_return_fulfilment_plan_materialization_monotonic
BEFORE UPDATE OF materialized_at_iso ON return_fulfilment_plans
FOR EACH ROW
WHEN OLD.materialized_at_iso IS NOT NULL
  OR NEW.materialized_at_iso IS NULL
BEGIN
  SELECT RAISE(ABORT, 'RETURN_FULFILMENT_MATERIALIZATION_IMMUTABLE');
END;

CREATE TRIGGER trg_return_fulfilment_plan_delete_forbidden
BEFORE DELETE ON return_fulfilment_plans
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_FULFILMENT_PLAN_DELETE_FORBIDDEN');
END;
`;

/**
 * M14 将退货履约从“必打退款小票”扩展为 WPF 等价的三种冻结策略。
 * 旧 M13 计划全部是退款回单；通过重建表原子迁移，任何失败都不推进版本号。
 */
const M14 = `
ALTER TABLE return_fulfilment_plans
  RENAME TO return_fulfilment_plans_m13;

CREATE TABLE return_fulfilment_plans (
  action_id TEXT PRIMARY KEY
    REFERENCES return_actions(action_id) ON DELETE RESTRICT,
  return_order_guid TEXT NOT NULL UNIQUE
    REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  print_job_id TEXT NULL UNIQUE,
  drawer_event_id TEXT NULL UNIQUE,
  receipt_kind TEXT NOT NULL DEFAULT 'refund-receipt'
    CHECK (
      receipt_kind IN ('none', 'refund-voucher', 'refund-receipt')
    ),
  print_receipt INTEGER NOT NULL CHECK (print_receipt IN (0, 1)),
  drawer_required INTEGER NOT NULL CHECK (drawer_required IN (0, 1)),
  materialized_at_iso TEXT NULL,
  created_at_iso TEXT NOT NULL,
  CHECK (
    (drawer_required = 0 AND drawer_event_id IS NULL)
    OR (drawer_required = 1 AND drawer_event_id IS NOT NULL)
  ),
  CHECK (
    (
      receipt_kind = 'none'
      AND print_receipt = 0
      AND print_job_id IS NULL
    )
    OR (
      receipt_kind IN ('refund-voucher', 'refund-receipt')
      AND print_receipt = 1
      AND print_job_id IS NOT NULL
      AND TRIM(print_job_id) <> ''
      AND LENGTH(print_job_id) <= 128
    )
  ),
  CHECK (
    drawer_event_id IS NULL
    OR (
      TRIM(drawer_event_id) <> ''
      AND LENGTH(drawer_event_id) <= 128
    )
  )
);

INSERT INTO return_fulfilment_plans (
  action_id, return_order_guid, print_job_id, drawer_event_id,
  receipt_kind, print_receipt, drawer_required,
  materialized_at_iso, created_at_iso
)
SELECT
  action_id, return_order_guid, print_job_id, drawer_event_id,
  'refund-receipt', 1, drawer_required,
  materialized_at_iso, created_at_iso
FROM return_fulfilment_plans_m13;

DROP TABLE return_fulfilment_plans_m13;

CREATE TRIGGER trg_return_fulfilment_plan_identity_immutable
BEFORE UPDATE OF action_id, return_order_guid, print_job_id, drawer_event_id,
  receipt_kind, print_receipt, drawer_required, created_at_iso
ON return_fulfilment_plans
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_FULFILMENT_PLAN_IDENTITY_IMMUTABLE');
END;

CREATE TRIGGER trg_return_fulfilment_plan_materialization_monotonic
BEFORE UPDATE OF materialized_at_iso ON return_fulfilment_plans
FOR EACH ROW
WHEN OLD.materialized_at_iso IS NOT NULL
  OR NEW.materialized_at_iso IS NULL
BEGIN
  SELECT RAISE(ABORT, 'RETURN_FULFILMENT_MATERIALIZATION_IMMUTABLE');
END;

CREATE TRIGGER trg_return_fulfilment_plan_delete_forbidden
BEFORE DELETE ON return_fulfilment_plans
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'RETURN_FULFILMENT_PLAN_DELETE_FORBIDDEN');
END;
`;

/**
 * M15 冻结订单行加入购物车时的服务端售卖身份。
 *
 * 历史订单保持两列均为 NULL，供支持导出和普通本地历史读取；数据库触发器要求
 * 所有新订单写入提供整数枚举来源，补传时绝不能按当前目录反推。
 */
const M15 = `
ALTER TABLE local_order_lines
  ADD COLUMN reference_code TEXT NULL
  CHECK (reference_code IS NULL OR TRIM(reference_code) <> '');

ALTER TABLE local_order_lines
  ADD COLUMN sync_price_source INTEGER NULL
  CHECK (
    sync_price_source IS NULL
    OR (
      typeof(sync_price_source) = 'integer'
      AND sync_price_source IN (0, 1, 2, 3, 4)
    )
  );

CREATE TRIGGER trg_local_order_line_sync_provenance_insert
BEFORE INSERT ON local_order_lines
FOR EACH ROW
WHEN NEW.sync_price_source IS NULL
BEGIN
  SELECT RAISE(ABORT, 'ORDER_LINE_SYNC_PROVENANCE_INCOMPLETE');
END;

CREATE TRIGGER trg_local_order_line_sync_provenance_update
BEFORE UPDATE OF reference_code, sync_price_source ON local_order_lines
FOR EACH ROW
WHEN NEW.reference_code IS NOT OLD.reference_code
  OR NEW.sync_price_source IS NOT OLD.sync_price_source
BEGIN
  SELECT RAISE(ABORT, 'ORDER_LINE_SYNC_PROVENANCE_IMMUTABLE');
END;
`;

/**
 * M16 将 voucher reservation release 与本地负 tender 收口到追加式耐久账本。
 * 明文只保存本地业务身份和非敏感操作原因；券码、reservation token 继续只存在
 * voucher_protected_attempt_states 的二次密文中。
 */
const M16 = `
CREATE TABLE voucher_tender_reversal_actions (
  action_id TEXT PRIMARY KEY,
  order_guid TEXT NOT NULL
    REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  source_tender_guid TEXT NOT NULL UNIQUE
    REFERENCES order_tenders(tender_guid) ON DELETE RESTRICT,
  source_attempt_id TEXT NOT NULL UNIQUE
    REFERENCES payment_attempts(attempt_id) ON DELETE RESTRICT,
  amount_cents INTEGER NOT NULL CHECK (
    typeof(amount_cents) = 'integer' AND amount_cents > 0
  ),
  reason TEXT NOT NULL CHECK (
    reason IN ('SALE', 'CARD_FAILURE_AUTO_RELEASE')
  ),
  state TEXT NOT NULL CHECK (
    state IN ('Prepared', 'Submitted', 'Unknown', 'Reversed', 'Blocked')
  ),
  attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (
    typeof(attempt_count) = 'integer' AND attempt_count >= 0
  ),
  last_error_code TEXT NULL,
  reversal_tender_guid TEXT NULL UNIQUE
    REFERENCES order_tenders(tender_guid) ON DELETE RESTRICT,
  terminal_audit_event_id TEXT NULL UNIQUE
    REFERENCES audit_events(event_id) ON DELETE RESTRICT,
  submitted_at_iso TEXT NULL,
  terminal_at_iso TEXT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  CHECK (TRIM(action_id) <> '' AND LENGTH(action_id) <= 128),
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (
    TRIM(source_tender_guid) <> ''
    AND LENGTH(source_tender_guid) <= 128
  ),
  CHECK (
    TRIM(source_attempt_id) <> ''
    AND LENGTH(source_attempt_id) <= 128
  ),
  CHECK (
    last_error_code IS NULL
    OR (
      TRIM(last_error_code) <> ''
      AND LENGTH(last_error_code) <= 128
    )
  ),
  CHECK (
    (state = 'Prepared'
      AND attempt_count = 0
      AND last_error_code IS NULL
      AND reversal_tender_guid IS NULL
      AND terminal_audit_event_id IS NULL
      AND submitted_at_iso IS NULL
      AND terminal_at_iso IS NULL)
    OR
    (state = 'Submitted'
      AND attempt_count > 0
      AND last_error_code IS NULL
      AND reversal_tender_guid IS NULL
      AND terminal_audit_event_id IS NULL
      AND submitted_at_iso IS NOT NULL
      AND terminal_at_iso IS NULL)
    OR
    (state = 'Unknown'
      AND attempt_count > 0
      AND last_error_code IS NOT NULL
      AND reversal_tender_guid IS NULL
      AND terminal_audit_event_id IS NULL
      AND submitted_at_iso IS NOT NULL
      AND terminal_at_iso IS NULL)
    OR
    (state = 'Blocked'
      AND last_error_code IS NOT NULL
      AND reversal_tender_guid IS NULL
      AND terminal_audit_event_id IS NOT NULL
      AND (
        (attempt_count = 0 AND submitted_at_iso IS NULL)
        OR (attempt_count > 0 AND submitted_at_iso IS NOT NULL)
      )
      AND terminal_at_iso IS NOT NULL)
    OR
    (state = 'Reversed'
      AND attempt_count > 0
      AND last_error_code IS NULL
      AND reversal_tender_guid IS NOT NULL
      AND terminal_audit_event_id IS NOT NULL
      AND submitted_at_iso IS NOT NULL
      AND terminal_at_iso IS NOT NULL)
  )
);

CREATE UNIQUE INDEX ux_voucher_tender_reversal_one_unresolved_order
  ON voucher_tender_reversal_actions (order_guid)
  WHERE state IN ('Prepared', 'Submitted', 'Unknown', 'Blocked');

CREATE INDEX ix_voucher_tender_reversal_order_state
  ON voucher_tender_reversal_actions (order_guid, state, created_at_iso);

CREATE TRIGGER trg_voucher_tender_reversal_validate_insert
BEFORE INSERT ON voucher_tender_reversal_actions
FOR EACH ROW
WHEN NEW.state <> 'Prepared'
  OR NEW.attempt_count <> 0
  OR NEW.last_error_code IS NOT NULL
  OR NEW.reversal_tender_guid IS NOT NULL
  OR NEW.terminal_audit_event_id IS NOT NULL
  OR NEW.submitted_at_iso IS NOT NULL
  OR NEW.terminal_at_iso IS NOT NULL
  OR NOT EXISTS (
    SELECT 1
    FROM local_orders order_row
    INNER JOIN order_tenders source
      ON source.order_guid = order_row.order_guid
    INNER JOIN payment_attempts attempt
      ON attempt.attempt_id = source.payment_attempt_id
     AND attempt.order_guid = source.order_guid
    INNER JOIN voucher_protected_attempt_states protected
      ON protected.attempt_id = attempt.attempt_id
     AND protected.idempotency_key = attempt.idempotency_key
     AND protected.order_guid = attempt.order_guid
    WHERE order_row.order_guid = NEW.order_guid
      AND order_row.state = 'Completing'
      AND source.tender_guid = NEW.source_tender_guid
      AND source.method = 'voucher'
      AND source.amount_cents = NEW.amount_cents
      AND source.amount_cents > 0
      AND attempt.attempt_id = NEW.source_attempt_id
      AND attempt.provider = 'voucher'
      AND attempt.operation = 'purchase'
      AND attempt.state = 'Approved'
      AND attempt.amount_cents = NEW.amount_cents
      AND NOT EXISTS (
        SELECT 1
        FROM payment_tender_reversal_links prior
        WHERE prior.source_tender_guid = source.tender_guid
           OR prior.reversal_tender_guid = source.tender_guid
      )
  )
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_SOURCE_MISMATCH');
END;

CREATE TRIGGER trg_voucher_tender_reversal_identity_immutable
BEFORE UPDATE OF action_id, order_guid, source_tender_guid,
  source_attempt_id, amount_cents, reason, created_at_iso
ON voucher_tender_reversal_actions
FOR EACH ROW
WHEN NEW.action_id IS NOT OLD.action_id
  OR NEW.order_guid IS NOT OLD.order_guid
  OR NEW.source_tender_guid IS NOT OLD.source_tender_guid
  OR NEW.source_attempt_id IS NOT OLD.source_attempt_id
  OR NEW.amount_cents IS NOT OLD.amount_cents
  OR NEW.reason IS NOT OLD.reason
  OR NEW.created_at_iso IS NOT OLD.created_at_iso
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_IDENTITY_IMMUTABLE');
END;

CREATE TRIGGER trg_voucher_tender_reversal_transition
BEFORE UPDATE OF state, attempt_count, last_error_code,
  reversal_tender_guid, terminal_audit_event_id,
  submitted_at_iso, terminal_at_iso, updated_at_iso
ON voucher_tender_reversal_actions
FOR EACH ROW
WHEN NOT (
  (
    OLD.state IN ('Prepared', 'Submitted', 'Unknown')
    AND NEW.state = 'Submitted'
    AND NEW.attempt_count = OLD.attempt_count + 1
    AND NEW.last_error_code IS NULL
    AND NEW.reversal_tender_guid IS NULL
    AND NEW.terminal_audit_event_id IS NULL
    AND NEW.submitted_at_iso IS NOT NULL
    AND NEW.terminal_at_iso IS NULL
  )
  OR
  (
    OLD.state = 'Submitted'
    AND NEW.state = 'Unknown'
    AND NEW.attempt_count = OLD.attempt_count
    AND NEW.last_error_code IS NOT NULL
    AND NEW.reversal_tender_guid IS NULL
    AND NEW.terminal_audit_event_id IS NULL
    AND NEW.submitted_at_iso IS OLD.submitted_at_iso
    AND NEW.terminal_at_iso IS NULL
  )
  OR
  (
    OLD.state IN ('Prepared', 'Submitted', 'Unknown')
    AND NEW.state = 'Blocked'
    AND NEW.attempt_count = OLD.attempt_count
    AND NEW.last_error_code IS NOT NULL
    AND NEW.reversal_tender_guid IS NULL
    AND NEW.terminal_audit_event_id IS NOT NULL
    AND NEW.submitted_at_iso IS OLD.submitted_at_iso
    AND NEW.terminal_at_iso IS NOT NULL
  )
  OR
  (
    OLD.state IN ('Submitted', 'Unknown')
    AND NEW.state = 'Reversed'
    AND NEW.attempt_count = OLD.attempt_count
    AND NEW.last_error_code IS NULL
    AND NEW.reversal_tender_guid IS NOT NULL
    AND NEW.terminal_audit_event_id IS NOT NULL
    AND NEW.submitted_at_iso IS OLD.submitted_at_iso
    AND NEW.terminal_at_iso IS NOT NULL
  )
)
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_INVALID_TRANSITION');
END;

CREATE TRIGGER trg_voucher_tender_reversal_final_facts
BEFORE UPDATE OF state ON voucher_tender_reversal_actions
FOR EACH ROW
WHEN (
  NEW.state = 'Reversed'
  AND NOT EXISTS (
    SELECT 1
    FROM order_tenders reversal
    INNER JOIN payment_tender_reversal_links link
      ON link.order_guid = NEW.order_guid
     AND link.action_id = NEW.action_id
     AND link.source_tender_guid = NEW.source_tender_guid
     AND link.reversal_tender_guid = reversal.tender_guid
    INNER JOIN audit_events audit
      ON audit.event_id = NEW.terminal_audit_event_id
    WHERE reversal.tender_guid = NEW.reversal_tender_guid
      AND reversal.order_guid = NEW.order_guid
      AND reversal.method = 'voucher'
      AND reversal.amount_cents = -NEW.amount_cents
      AND reversal.payment_attempt_id IS NULL
      AND audit.event_type = 'PAYMENT_TENDER_REMOVE'
      AND audit.order_guid = NEW.order_guid
      AND audit.correlation_id = NEW.action_id
      AND json_valid(audit.payload_json) = 1
      AND json_extract(audit.payload_json, '$.action')
        = 'payment-tender-remove'
      AND json_extract(audit.payload_json, '$.outcome') = 'success'
      AND json_extract(audit.payload_json, '$.reason') = NEW.reason
      AND json_extract(audit.payload_json, '$.amountCents')
        = NEW.amount_cents
      AND json_extract(audit.payload_json, '$.sourceTenderGuid')
        = NEW.source_tender_guid
      AND json_extract(audit.payload_json, '$.sourceAttemptId')
        = NEW.source_attempt_id
      AND json_extract(audit.payload_json, '$.reversalTenderGuid')
        = NEW.reversal_tender_guid
  )
)
OR (
  NEW.state = 'Blocked'
  AND NOT EXISTS (
    SELECT 1
    FROM audit_events audit
    WHERE audit.event_id = NEW.terminal_audit_event_id
      AND audit.event_type = 'PAYMENT_TENDER_REMOVE'
      AND audit.order_guid = NEW.order_guid
      AND audit.correlation_id = NEW.action_id
      AND json_valid(audit.payload_json) = 1
      AND json_extract(audit.payload_json, '$.action')
        = 'payment-tender-remove'
      AND json_extract(audit.payload_json, '$.outcome') = 'blocked'
      AND json_extract(audit.payload_json, '$.reason') = NEW.reason
      AND json_extract(audit.payload_json, '$.amountCents')
        = NEW.amount_cents
      AND json_extract(audit.payload_json, '$.sourceTenderGuid')
        = NEW.source_tender_guid
      AND json_extract(audit.payload_json, '$.sourceAttemptId')
        = NEW.source_attempt_id
      AND json_extract(audit.payload_json, '$.errorCode')
        = NEW.last_error_code
  )
)
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_FINAL_FACT_MISMATCH');
END;

CREATE TRIGGER trg_voucher_tender_reversal_delete_forbidden
BEFORE DELETE ON voucher_tender_reversal_actions
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_voucher_tender_reversal_order_state_gate
BEFORE UPDATE OF state ON local_orders
FOR EACH ROW
WHEN NEW.state NOT IN ('Draft', 'Completing')
  AND EXISTS (
    SELECT 1
    FROM voucher_tender_reversal_actions action
    WHERE action.order_guid = OLD.order_guid
      AND action.state IN ('Prepared', 'Submitted', 'Unknown', 'Blocked')
  )
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_ORDER_UNRESOLVED');
END;

CREATE TRIGGER trg_voucher_tender_reversal_positive_tender_gate
BEFORE INSERT ON order_tenders
FOR EACH ROW
WHEN NEW.amount_cents > 0
  AND EXISTS (
    SELECT 1
    FROM voucher_tender_reversal_actions action
    WHERE action.order_guid = NEW.order_guid
      AND action.state IN ('Prepared', 'Submitted', 'Unknown', 'Blocked')
  )
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_ORDER_UNRESOLVED');
END;

CREATE TRIGGER trg_voucher_tender_reversal_action_binding_gate
BEFORE INSERT ON payment_action_bindings
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM voucher_tender_reversal_actions action
  WHERE action.order_guid = NEW.order_guid
    AND action.state IN ('Prepared', 'Submitted', 'Unknown', 'Blocked')
)
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_ORDER_UNRESOLVED');
END;

CREATE TRIGGER trg_voucher_tender_reversal_payment_attempt_gate
BEFORE INSERT ON payment_attempts
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM voucher_tender_reversal_actions action
  WHERE action.order_guid = NEW.order_guid
    AND action.state IN ('Prepared', 'Submitted', 'Unknown', 'Blocked')
)
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_ORDER_UNRESOLVED');
END;

CREATE TRIGGER trg_voucher_tender_reversal_tender_immutable
BEFORE UPDATE ON order_tenders
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM voucher_tender_reversal_actions action
  WHERE action.source_tender_guid = OLD.tender_guid
     OR action.reversal_tender_guid = OLD.tender_guid
)
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_TENDER_IMMUTABLE');
END;

CREATE TRIGGER trg_voucher_tender_reversal_tender_delete_forbidden
BEFORE DELETE ON order_tenders
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM voucher_tender_reversal_actions action
  WHERE action.source_tender_guid = OLD.tender_guid
     OR action.reversal_tender_guid = OLD.tender_guid
)
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_TENDER_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_voucher_tender_reversal_attempt_immutable
BEFORE UPDATE ON payment_attempts
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM voucher_tender_reversal_actions action
  WHERE action.source_attempt_id = OLD.attempt_id
)
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_ATTEMPT_IMMUTABLE');
END;

CREATE TRIGGER trg_voucher_tender_reversal_attempt_delete_forbidden
BEFORE DELETE ON payment_attempts
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM voucher_tender_reversal_actions action
  WHERE action.source_attempt_id = OLD.attempt_id
)
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_ATTEMPT_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_voucher_tender_reversal_protected_state_delete_forbidden
BEFORE DELETE ON voucher_protected_attempt_states
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM voucher_tender_reversal_actions action
  WHERE action.source_attempt_id = OLD.attempt_id
)
BEGIN
  SELECT RAISE(
    ABORT,
    'VOUCHER_TENDER_REVERSAL_PROTECTED_STATE_DELETE_FORBIDDEN'
  );
END;

CREATE TRIGGER trg_voucher_tender_reversal_terminal_protected_state_immutable
BEFORE UPDATE ON voucher_protected_attempt_states
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM voucher_tender_reversal_actions action
  WHERE action.source_attempt_id = OLD.attempt_id
    AND action.state IN ('Reversed', 'Blocked')
)
BEGIN
  SELECT RAISE(
    ABORT,
    'VOUCHER_TENDER_REVERSAL_PROTECTED_STATE_IMMUTABLE'
  );
END;

CREATE TRIGGER trg_voucher_tender_reversal_audit_immutable
BEFORE UPDATE ON audit_events
FOR EACH ROW
WHEN (
  NEW.event_id IS NOT OLD.event_id
  OR NEW.event_type IS NOT OLD.event_type
  OR NEW.occurred_at_iso IS NOT OLD.occurred_at_iso
  OR NEW.order_guid IS NOT OLD.order_guid
  OR NEW.correlation_id IS NOT OLD.correlation_id
  OR NEW.payload_json IS NOT OLD.payload_json
  OR (
    NEW.uploaded_at_iso IS NOT OLD.uploaded_at_iso
    AND NOT (
      OLD.uploaded_at_iso IS NULL
      AND NEW.uploaded_at_iso IS NOT NULL
    )
  )
)
AND EXISTS (
  SELECT 1
  FROM voucher_tender_reversal_actions action
  WHERE action.terminal_audit_event_id = OLD.event_id
)
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_AUDIT_IMMUTABLE');
END;

CREATE TRIGGER trg_voucher_tender_reversal_audit_delete_forbidden
BEFORE DELETE ON audit_events
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM voucher_tender_reversal_actions action
  WHERE action.terminal_audit_event_id = OLD.event_id
)
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_AUDIT_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_return_fulfilment_plan_action_order_insert
BEFORE INSERT ON return_fulfilment_plans
FOR EACH ROW
WHEN NOT EXISTS (
  SELECT 1
  FROM return_actions action
  WHERE action.action_id = NEW.action_id
    AND action.return_order_guid = NEW.return_order_guid
)
BEGIN
  SELECT RAISE(ABORT, 'RETURN_FULFILMENT_PLAN_ACTION_ORDER_MISMATCH');
END;

CREATE TRIGGER trg_return_fulfilment_plan_action_order_update
BEFORE UPDATE OF action_id, return_order_guid ON return_fulfilment_plans
FOR EACH ROW
WHEN NOT EXISTS (
  SELECT 1
  FROM return_actions action
  WHERE action.action_id = NEW.action_id
    AND action.return_order_guid = NEW.return_order_guid
)
BEGIN
  SELECT RAISE(ABORT, 'RETURN_FULFILMENT_PLAN_ACTION_ORDER_MISMATCH');
END;

-- 迁移时用独立 guard 核验既有 M14 行；不能以 no-op UPDATE 触发
-- 校验，因为 M14 已禁止修改这些冻结身份列。
CREATE TABLE m16_return_fulfilment_plan_validation (
  invalid_binding INTEGER NOT NULL
);

CREATE TRIGGER trg_m16_return_fulfilment_plan_validation
BEFORE INSERT ON m16_return_fulfilment_plan_validation
FOR EACH ROW
WHEN NEW.invalid_binding = 1
BEGIN
  SELECT RAISE(ABORT, 'RETURN_FULFILMENT_PLAN_ACTION_ORDER_MISMATCH');
END;

INSERT INTO m16_return_fulfilment_plan_validation (invalid_binding)
SELECT 1
FROM return_fulfilment_plans plan
WHERE NOT EXISTS (
  SELECT 1
  FROM return_actions action
  WHERE action.action_id = plan.action_id
    AND action.return_order_guid = plan.return_order_guid
)
LIMIT 1;

DROP TRIGGER trg_m16_return_fulfilment_plan_validation;
DROP TABLE m16_return_fulfilment_plan_validation;
`;

/**
 * M17 将 M6 的单日唯一日结升级为可重复、不可变的完整归档，同时把 M2
 * 依附目录快照的特殊商品标记迁移为按门店和商品去重的设备本地顺序。
 */
const M17 = `
ALTER TABLE local_daily_closes RENAME TO local_daily_closes_m6;
ALTER TABLE daily_close_totals RENAME TO daily_close_totals_m6;
ALTER TABLE cash_denominations RENAME TO cash_denominations_m6;

CREATE TABLE local_daily_closes (
  close_id TEXT PRIMARY KEY,
  business_date TEXT NOT NULL,
  period_from_iso TEXT NOT NULL,
  period_to_iso TEXT NOT NULL,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  saved_cashier_id TEXT NOT NULL,
  saved_cashier_name TEXT NOT NULL,
  order_count INTEGER NOT NULL CHECK (
    typeof(order_count) = 'integer' AND order_count >= 0
  ),
  return_quantity TEXT NOT NULL,
  expected_cash_cents INTEGER NOT NULL CHECK (
    typeof(expected_cash_cents) = 'integer'
  ),
  counted_cash_cents INTEGER NOT NULL CHECK (
    typeof(counted_cash_cents) = 'integer' AND counted_cash_cents >= 0
  ),
  notes_subtotal_cents INTEGER NOT NULL CHECK (
    typeof(notes_subtotal_cents) = 'integer' AND notes_subtotal_cents >= 0
  ),
  coins_subtotal_cents INTEGER NOT NULL CHECK (
    typeof(coins_subtotal_cents) = 'integer' AND coins_subtotal_cents >= 0
  ),
  variance_cents INTEGER NOT NULL CHECK (
    typeof(variance_cents) = 'integer'
  ),
  terminal_audit_event_id TEXT NOT NULL UNIQUE
    REFERENCES audit_events(event_id) ON DELETE RESTRICT,
  saved_at_iso TEXT NOT NULL,
  source_kind TEXT NOT NULL CHECK (source_kind IN ('legacy', 'native')),
  state TEXT NOT NULL CHECK (state IN ('Preparing', 'Archived')),
  CHECK (TRIM(close_id) <> '' AND LENGTH(close_id) <= 128),
  CHECK (TRIM(business_date) <> '' AND LENGTH(business_date) <= 32),
  CHECK (TRIM(period_from_iso) <> '' AND LENGTH(period_from_iso) <= 64),
  CHECK (TRIM(period_to_iso) <> '' AND LENGTH(period_to_iso) <= 64),
  CHECK (period_from_iso < period_to_iso),
  CHECK (TRIM(store_code) <> '' AND LENGTH(store_code) <= 128),
  CHECK (TRIM(device_code) <> '' AND LENGTH(device_code) <= 128),
  CHECK (
    TRIM(saved_cashier_id) <> '' AND LENGTH(saved_cashier_id) <= 128
  ),
  CHECK (
    TRIM(saved_cashier_name) <> '' AND LENGTH(saved_cashier_name) <= 256
  ),
  CHECK (TRIM(return_quantity) <> '' AND LENGTH(return_quantity) <= 128),
  CHECK (TRIM(saved_at_iso) <> '' AND LENGTH(saved_at_iso) <= 64)
);

CREATE TABLE daily_close_totals (
  close_id TEXT NOT NULL
    REFERENCES local_daily_closes(close_id) ON DELETE RESTRICT,
  tender_method TEXT NOT NULL CHECK (
    tender_method IN ('cash', 'card', 'voucher')
  ),
  sales_cents INTEGER NOT NULL CHECK (
    typeof(sales_cents) = 'integer' AND sales_cents >= 0
  ),
  refund_cents INTEGER NOT NULL CHECK (
    typeof(refund_cents) = 'integer' AND refund_cents <= 0
  ),
  net_cents INTEGER NOT NULL CHECK (
    typeof(net_cents) = 'integer'
    AND net_cents = sales_cents + refund_cents
  ),
  PRIMARY KEY (close_id, tender_method)
);

CREATE TABLE cash_denominations (
  close_id TEXT NOT NULL
    REFERENCES local_daily_closes(close_id) ON DELETE RESTRICT,
  denomination_cents INTEGER NOT NULL CHECK (
    typeof(denomination_cents) = 'integer'
    AND denomination_cents IN (
      10000, 5000, 2000, 1000, 500, 200, 100, 50, 20, 10, 5
    )
  ),
  quantity INTEGER NOT NULL CHECK (
    typeof(quantity) = 'integer' AND quantity >= 0
  ),
  subtotal_cents INTEGER NOT NULL CHECK (
    typeof(subtotal_cents) = 'integer'
    AND subtotal_cents = denomination_cents * quantity
  ),
  PRIMARY KEY (close_id, denomination_cents)
);

CREATE INDEX ix_local_daily_closes_scope_saved
  ON local_daily_closes (
    store_code, device_code, business_date, saved_at_iso DESC, close_id
  );

-- 遗留 M6 没有完整收银员、期间或审计事实；建立确定性的迁移审计，
-- 保留可确认的 closeId、日期和金额，其余字段使用明确的安全默认值。
INSERT INTO audit_events (
  event_id, event_type, occurred_at_iso, order_guid,
  correlation_id, payload_json, uploaded_at_iso
)
SELECT
  '__m17_daily_close__:' || hex(close_id),
  'DAILY_CLOSE_MIGRATED',
  CASE
    WHEN LENGTH(TRIM(COALESCE(closed_at_iso, ''))) <= 64
      AND INSTR(COALESCE(closed_at_iso, ''), char(0)) = 0
      AND julianday(closed_at_iso) IS NOT NULL
    THEN TRIM(closed_at_iso)
    WHEN LENGTH(TRIM(COALESCE(created_at_iso, ''))) <= 64
      AND INSTR(COALESCE(created_at_iso, ''), char(0)) = 0
      AND julianday(created_at_iso) IS NOT NULL
    THEN TRIM(created_at_iso)
    ELSE '1970-01-01T00:00:00.000Z'
  END,
  NULL,
  close_id,
  json_object(
    'action', 'daily-close-migrate',
    'closeId', close_id,
    'sourceVersion', 6
  ),
  NULL
FROM local_daily_closes_m6;

INSERT INTO local_daily_closes (
  close_id, business_date, period_from_iso, period_to_iso,
  store_code, device_code, saved_cashier_id, saved_cashier_name,
  order_count, return_quantity, expected_cash_cents, counted_cash_cents,
  notes_subtotal_cents, coins_subtotal_cents, variance_cents,
  terminal_audit_event_id, saved_at_iso, source_kind, state
)
SELECT
  close_id,
  CASE
    WHEN business_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
      AND INSTR(business_date, char(0)) = 0
      AND date(business_date) = business_date
    THEN business_date
    ELSE '1970-01-01'
  END,
  CASE
    WHEN business_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
      AND INSTR(business_date, char(0)) = 0
      AND date(business_date) = business_date
    THEN business_date || 'T00:00:00.000Z'
    ELSE '1970-01-01T00:00:00.000Z'
  END,
  CASE
    WHEN business_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
      AND INSTR(business_date, char(0)) = 0
      AND date(business_date) = business_date
    THEN strftime('%Y-%m-%dT00:00:00.000Z', business_date, '+1 day')
    ELSE '1970-01-02T00:00:00.000Z'
  END,
  CASE
    WHEN TRIM(store_code) <> ''
      AND LENGTH(TRIM(store_code)) <= 128
      AND INSTR(store_code, char(0)) = 0
      AND store_code NOT GLOB (
        '*[' || char(1) || '-' || char(31) || char(127) || ']*'
      )
    THEN TRIM(store_code)
    ELSE 'legacy-unknown-store'
  END,
  CASE
    WHEN TRIM(device_code) <> ''
      AND LENGTH(TRIM(device_code)) <= 128
      AND INSTR(device_code, char(0)) = 0
      AND device_code NOT GLOB (
        '*[' || char(1) || '-' || char(31) || char(127) || ']*'
      )
    THEN TRIM(device_code)
    ELSE 'legacy-unknown-device'
  END,
  'legacy-migration',
  'Legacy migration',
  0,
  '0',
  CASE
    WHEN typeof(expected_cash_cents) = 'integer'
      AND expected_cash_cents BETWEEN
        -9007199254740991 AND 9007199254740991
    THEN expected_cash_cents
    ELSE 0
  END,
  CASE
    WHEN typeof(counted_cash_cents) = 'integer'
      AND counted_cash_cents BETWEEN 0 AND 9007199254740991
    THEN counted_cash_cents
    ELSE 0
  END,
  CASE
    WHEN typeof(counted_cash_cents) = 'integer'
      AND counted_cash_cents BETWEEN 0 AND 9007199254740991
    THEN counted_cash_cents
    ELSE 0
  END,
  0,
  CASE
    WHEN typeof(variance_cents) = 'integer'
      AND variance_cents BETWEEN -9007199254740991 AND 9007199254740991
    THEN variance_cents
    ELSE
      (CASE
        WHEN typeof(counted_cash_cents) = 'integer'
          AND counted_cash_cents BETWEEN 0 AND 9007199254740991
        THEN counted_cash_cents
        ELSE 0
      END)
      -
      (CASE
        WHEN typeof(expected_cash_cents) = 'integer'
          AND expected_cash_cents BETWEEN
            -9007199254740991 AND 9007199254740991
        THEN expected_cash_cents
        ELSE 0
      END)
  END,
  '__m17_daily_close__:' || hex(close_id),
  CASE
    WHEN LENGTH(TRIM(COALESCE(closed_at_iso, ''))) <= 64
      AND INSTR(COALESCE(closed_at_iso, ''), char(0)) = 0
      AND julianday(closed_at_iso) IS NOT NULL
    THEN TRIM(closed_at_iso)
    WHEN LENGTH(TRIM(COALESCE(created_at_iso, ''))) <= 64
      AND INSTR(COALESCE(created_at_iso, ''), char(0)) = 0
      AND julianday(created_at_iso) IS NOT NULL
    THEN TRIM(created_at_iso)
    ELSE '1970-01-01T00:00:00.000Z'
  END,
  'legacy',
  'Archived'
FROM local_daily_closes_m6;

WITH methods(tender_method) AS (
  VALUES ('cash'), ('card'), ('voucher')
),
legacy_totals AS (
  SELECT
    close_id,
    LOWER(TRIM(tender_method)) AS tender_method,
    SUM(
      CASE
        WHEN LOWER(TRIM(direction)) IN ('sale', 'sales')
          AND typeof(amount_cents) = 'integer'
          AND amount_cents BETWEEN
            -9007199254740991 AND 9007199254740991
        THEN ABS(amount_cents)
        ELSE 0
      END
    ) AS sales_cents,
    -SUM(
      CASE
        WHEN LOWER(TRIM(direction)) IN ('refund', 'return', 'returns')
          AND typeof(amount_cents) = 'integer'
          AND amount_cents BETWEEN
            -9007199254740991 AND 9007199254740991
        THEN ABS(amount_cents)
        ELSE 0
      END
    ) AS refund_cents
  FROM daily_close_totals_m6
  WHERE LOWER(TRIM(tender_method)) IN ('cash', 'card', 'voucher')
  GROUP BY close_id, LOWER(TRIM(tender_method))
)
INSERT INTO daily_close_totals (
  close_id, tender_method, sales_cents, refund_cents, net_cents
)
SELECT
  close_row.close_id,
  methods.tender_method,
  CASE
    WHEN COALESCE(legacy.sales_cents, 0)
      BETWEEN 0 AND 9007199254740991
    THEN COALESCE(legacy.sales_cents, 0)
    ELSE 0
  END,
  CASE
    WHEN COALESCE(legacy.refund_cents, 0)
      BETWEEN -9007199254740991 AND 0
    THEN COALESCE(legacy.refund_cents, 0)
    ELSE 0
  END,
  (CASE
    WHEN COALESCE(legacy.sales_cents, 0)
      BETWEEN 0 AND 9007199254740991
    THEN COALESCE(legacy.sales_cents, 0)
    ELSE 0
  END)
  +
  (CASE
    WHEN COALESCE(legacy.refund_cents, 0)
      BETWEEN -9007199254740991 AND 0
    THEN COALESCE(legacy.refund_cents, 0)
    ELSE 0
  END)
FROM local_daily_closes_m6 close_row
CROSS JOIN methods
LEFT JOIN legacy_totals legacy
  ON legacy.close_id = close_row.close_id
 AND legacy.tender_method = methods.tender_method;

WITH denominations(denomination_cents) AS (
  VALUES (10000), (5000), (2000), (1000), (500), (200), (100),
    (50), (20), (10), (5)
)
INSERT INTO cash_denominations (
  close_id, denomination_cents, quantity, subtotal_cents
)
SELECT
  close_row.close_id,
  denominations.denomination_cents,
  CASE
    WHEN typeof(legacy.quantity) = 'integer'
      AND legacy.quantity BETWEEN 0
        AND 9007199254740991 / denominations.denomination_cents
    THEN legacy.quantity
    ELSE 0
  END,
  denominations.denomination_cents
      * CASE
        WHEN typeof(legacy.quantity) = 'integer'
          AND legacy.quantity BETWEEN 0
            AND 9007199254740991 / denominations.denomination_cents
        THEN legacy.quantity
        ELSE 0
      END
FROM local_daily_closes_m6 close_row
CROSS JOIN denominations
LEFT JOIN cash_denominations_m6 legacy
  ON legacy.close_id = close_row.close_id
 AND legacy.denomination_cents = denominations.denomination_cents;

DROP TABLE daily_close_totals_m6;
DROP TABLE cash_denominations_m6;
DROP TABLE local_daily_closes_m6;

CREATE TRIGGER trg_daily_close_native_insert
BEFORE INSERT ON local_daily_closes
FOR EACH ROW
WHEN NEW.source_kind <> 'native' OR NEW.state <> 'Preparing'
BEGIN
  SELECT RAISE(ABORT, 'DAILY_CLOSE_INSERT_STATE_INVALID');
END;

CREATE TRIGGER trg_daily_close_archive_immutable
BEFORE UPDATE OF
  close_id, business_date, period_from_iso, period_to_iso,
  store_code, device_code, saved_cashier_id, saved_cashier_name,
  order_count, return_quantity, expected_cash_cents, counted_cash_cents,
  notes_subtotal_cents, coins_subtotal_cents, variance_cents,
  terminal_audit_event_id, saved_at_iso, source_kind
ON local_daily_closes
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'DAILY_CLOSE_ARCHIVE_IMMUTABLE');
END;

CREATE TRIGGER trg_daily_close_archive_transition
BEFORE UPDATE OF state ON local_daily_closes
FOR EACH ROW
WHEN NOT (
  OLD.state = 'Preparing'
  AND NEW.state = 'Archived'
  AND NEW.source_kind = 'native'
  AND (
    SELECT COUNT(*)
    FROM daily_close_totals totals
    WHERE totals.close_id = NEW.close_id
  ) = 3
  AND NOT EXISTS (
    SELECT 1
    FROM (
      SELECT 'cash' AS tender_method
      UNION ALL SELECT 'card'
      UNION ALL SELECT 'voucher'
    ) required
    WHERE NOT EXISTS (
      SELECT 1
      FROM daily_close_totals totals
      WHERE totals.close_id = NEW.close_id
        AND totals.tender_method = required.tender_method
    )
  )
  AND (
    SELECT COUNT(*)
    FROM cash_denominations counts
    WHERE counts.close_id = NEW.close_id
  ) = 11
  AND (
    SELECT COALESCE(SUM(subtotal_cents), 0)
    FROM cash_denominations counts
    WHERE counts.close_id = NEW.close_id
      AND counts.denomination_cents >= 500
  ) = NEW.notes_subtotal_cents
  AND (
    SELECT COALESCE(SUM(subtotal_cents), 0)
    FROM cash_denominations counts
    WHERE counts.close_id = NEW.close_id
      AND counts.denomination_cents < 500
  ) = NEW.coins_subtotal_cents
  AND NEW.counted_cash_cents
    = NEW.notes_subtotal_cents + NEW.coins_subtotal_cents
  AND NEW.variance_cents
    = NEW.counted_cash_cents - NEW.expected_cash_cents
  AND NEW.expected_cash_cents = (
    SELECT net_cents
    FROM daily_close_totals totals
    WHERE totals.close_id = NEW.close_id
      AND totals.tender_method = 'cash'
  )
  AND EXISTS (
    SELECT 1
    FROM audit_events audit
    WHERE audit.event_id = NEW.terminal_audit_event_id
      AND audit.event_type = 'DAILY_CLOSE_SAVE'
      AND audit.order_guid IS NULL
      AND audit.correlation_id = NEW.close_id
      AND audit.occurred_at_iso = NEW.saved_at_iso
      AND json_valid(audit.payload_json) = 1
  )
)
BEGIN
  SELECT RAISE(ABORT, 'DAILY_CLOSE_ARCHIVE_INCOMPLETE');
END;

CREATE TRIGGER trg_daily_close_delete_forbidden
BEFORE DELETE ON local_daily_closes
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'DAILY_CLOSE_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_daily_close_total_insert_gate
BEFORE INSERT ON daily_close_totals
FOR EACH ROW
WHEN NOT EXISTS (
  SELECT 1
  FROM local_daily_closes close_row
  WHERE close_row.close_id = NEW.close_id
    AND close_row.state = 'Preparing'
    AND close_row.source_kind = 'native'
)
BEGIN
  SELECT RAISE(ABORT, 'DAILY_CLOSE_TOTAL_INSERT_FORBIDDEN');
END;

CREATE TRIGGER trg_daily_close_total_immutable
BEFORE UPDATE ON daily_close_totals
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'DAILY_CLOSE_TOTAL_IMMUTABLE');
END;

CREATE TRIGGER trg_daily_close_total_delete_forbidden
BEFORE DELETE ON daily_close_totals
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'DAILY_CLOSE_TOTAL_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_daily_close_denomination_insert_gate
BEFORE INSERT ON cash_denominations
FOR EACH ROW
WHEN NOT EXISTS (
  SELECT 1
  FROM local_daily_closes close_row
  WHERE close_row.close_id = NEW.close_id
    AND close_row.state = 'Preparing'
    AND close_row.source_kind = 'native'
)
BEGIN
  SELECT RAISE(ABORT, 'DAILY_CLOSE_DENOMINATION_INSERT_FORBIDDEN');
END;

CREATE TRIGGER trg_daily_close_denomination_immutable
BEFORE UPDATE ON cash_denominations
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'DAILY_CLOSE_DENOMINATION_IMMUTABLE');
END;

CREATE TRIGGER trg_daily_close_denomination_delete_forbidden
BEFORE DELETE ON cash_denominations
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'DAILY_CLOSE_DENOMINATION_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_daily_close_audit_immutable
BEFORE UPDATE ON audit_events
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM local_daily_closes close_row
  WHERE close_row.terminal_audit_event_id = OLD.event_id
)
AND (
  NEW.event_id IS NOT OLD.event_id
  OR NEW.event_type IS NOT OLD.event_type
  OR NEW.occurred_at_iso IS NOT OLD.occurred_at_iso
  OR NEW.order_guid IS NOT OLD.order_guid
  OR NEW.correlation_id IS NOT OLD.correlation_id
  OR NEW.payload_json IS NOT OLD.payload_json
  OR (
    NEW.uploaded_at_iso IS NOT OLD.uploaded_at_iso
    AND NOT (
      OLD.uploaded_at_iso IS NULL
      AND NEW.uploaded_at_iso IS NOT NULL
    )
  )
)
BEGIN
  SELECT RAISE(ABORT, 'DAILY_CLOSE_AUDIT_IMMUTABLE');
END;

CREATE TRIGGER trg_daily_close_audit_delete_forbidden
BEFORE DELETE ON audit_events
FOR EACH ROW
WHEN EXISTS (
  SELECT 1
  FROM local_daily_closes close_row
  WHERE close_row.terminal_audit_event_id = OLD.event_id
)
BEGIN
  SELECT RAISE(ABORT, 'DAILY_CLOSE_AUDIT_DELETE_FORBIDDEN');
END;

-- 保留 M2 与 catalog snapshot 同生命周期的 special_products，避免旧目录下载、
-- staging 丢弃和 active 切换失去兼容；设备本地顺序使用独立的商品级集合。
CREATE TABLE local_special_products (
  store_code TEXT NOT NULL,
  product_code TEXT NOT NULL,
  reference_code TEXT NULL,
  item_number TEXT NULL,
  display_name TEXT NOT NULL,
  barcode TEXT NULL,
  lookup_code TEXT NOT NULL,
  retail_price_cents INTEGER NOT NULL CHECK (
    typeof(retail_price_cents) = 'integer'
  ),
  price_source INTEGER NOT NULL CHECK (
    typeof(price_source) = 'integer'
    AND price_source IN (0, 1, 2, 3, 4)
  ),
  quantity_factor TEXT NOT NULL,
  product_image TEXT NULL,
  discount_rate TEXT NULL,
  sort_order INTEGER NOT NULL CHECK (
    typeof(sort_order) = 'integer' AND sort_order >= 0
  ),
  PRIMARY KEY (store_code, product_code),
  UNIQUE (store_code, sort_order),
  CHECK (TRIM(store_code) <> '' AND LENGTH(store_code) <= 128),
  CHECK (TRIM(product_code) <> '' AND LENGTH(product_code) <= 128),
  CHECK (TRIM(display_name) <> '' AND LENGTH(display_name) <= 512),
  CHECK (TRIM(lookup_code) <> '' AND LENGTH(lookup_code) <= 256),
  CHECK (TRIM(quantity_factor) <> '' AND LENGTH(quantity_factor) <= 128)
);

CREATE INDEX ix_local_special_products_store_sort
  ON local_special_products (store_code, sort_order, product_code);
CREATE INDEX ix_local_special_products_store_search
  ON local_special_products (
    store_code, display_name COLLATE NOCASE,
    item_number COLLATE NOCASE, lookup_code COLLATE NOCASE
  );

WITH candidates AS (
  SELECT
    TRIM(legacy.store_code) AS store_code,
    TRIM(item.product_code) AS product_code,
    CASE
      WHEN item.reference_code IS NULL
        OR TRIM(item.reference_code) = ''
      THEN NULL
      WHEN LENGTH(TRIM(item.reference_code)) <= 256
        AND INSTR(item.reference_code, char(0)) = 0
        AND item.reference_code NOT GLOB (
          '*[' || char(1) || '-' || char(31) || char(127) || ']*'
        )
      THEN TRIM(item.reference_code)
      ELSE NULL
    END AS reference_code,
    CASE
      WHEN item.item_number IS NULL
        OR TRIM(item.item_number) = ''
      THEN NULL
      WHEN LENGTH(TRIM(item.item_number)) <= 256
        AND INSTR(item.item_number, char(0)) = 0
        AND item.item_number NOT GLOB (
          '*[' || char(1) || '-' || char(31) || char(127) || ']*'
        )
      THEN TRIM(item.item_number)
      ELSE NULL
    END AS item_number,
    TRIM(item.display_name) AS display_name,
    CASE
      WHEN item.barcode IS NULL
        OR TRIM(item.barcode) = ''
      THEN NULL
      WHEN LENGTH(TRIM(item.barcode)) <= 256
        AND INSTR(item.barcode, char(0)) = 0
        AND item.barcode NOT GLOB (
          '*[' || char(1) || '-' || char(31) || char(127) || ']*'
        )
      THEN TRIM(item.barcode)
      ELSE NULL
    END AS barcode,
    TRIM(item.lookup_code) AS lookup_code,
    CASE
      WHEN typeof(item.retail_price_cents) = 'integer'
        AND item.retail_price_cents BETWEEN
          -9007199254740991 AND 9007199254740991
      THEN item.retail_price_cents
      ELSE 0
    END AS retail_price_cents,
    CASE
      WHEN typeof(item.price_source) = 'integer'
        AND item.price_source IN (0, 1, 2, 3, 4)
      THEN item.price_source
      ELSE 0
    END AS price_source,
    CASE
      WHEN INSTR(COALESCE(item.quantity_factor, ''), char(0)) = 0
        AND json_valid(
        '[' || TRIM(COALESCE(item.quantity_factor, '')) || ']'
      ) = 1
      THEN CASE
        WHEN json_type(
          '[' || TRIM(item.quantity_factor) || ']', '$[0]'
        ) IN ('integer', 'real')
          AND CAST(json_extract(
            '[' || TRIM(item.quantity_factor) || ']', '$[0]'
          ) AS REAL) > 0
          AND CAST(json_extract(
            '[' || TRIM(item.quantity_factor) || ']', '$[0]'
          ) AS REAL) <= 1.7976931348623157e308
        THEN CAST(json_extract(
          '[' || TRIM(item.quantity_factor) || ']', '$[0]'
        ) AS TEXT)
        ELSE '1'
      END
      ELSE '1'
    END AS quantity_factor,
    CASE
      WHEN item.product_image IS NULL
        OR TRIM(item.product_image) = ''
      THEN NULL
      WHEN LENGTH(TRIM(item.product_image)) <= 2048
        AND INSTR(item.product_image, char(0)) = 0
        AND item.product_image NOT GLOB (
          '*[' || char(1) || '-' || char(31) || char(127) || ']*'
        )
      THEN TRIM(item.product_image)
      ELSE NULL
    END AS product_image,
    CASE
      WHEN item.discount_rate IS NULL
        OR TRIM(item.discount_rate) = ''
      THEN NULL
      WHEN INSTR(item.discount_rate, char(0)) = 0
        AND json_valid(
        '[' || TRIM(item.discount_rate) || ']'
      ) = 1
      THEN CASE
        WHEN json_type(
          '[' || TRIM(item.discount_rate) || ']', '$[0]'
        ) IN ('integer', 'real')
          AND ABS(CAST(json_extract(
            '[' || TRIM(item.discount_rate) || ']', '$[0]'
          ) AS REAL)) <= 1.7976931348623157e308
        THEN CAST(json_extract(
          '[' || TRIM(item.discount_rate) || ']', '$[0]'
        ) AS TEXT)
        ELSE NULL
      END
      ELSE NULL
    END AS discount_rate,
    CASE
      WHEN typeof(legacy.sort_order) = 'integer'
        AND legacy.sort_order BETWEEN 0 AND 9007199254740991
      THEN legacy.sort_order
      ELSE 9007199254740991
    END AS legacy_sort_order,
    ROW_NUMBER() OVER (
      PARTITION BY TRIM(legacy.store_code), TRIM(item.product_code)
      ORDER BY
        CASE WHEN snapshot.state = 'active' THEN 0 ELSE 1 END,
        CASE
          WHEN typeof(legacy.sort_order) = 'integer'
            AND legacy.sort_order BETWEEN 0 AND 9007199254740991
          THEN legacy.sort_order
          ELSE 9007199254740991
        END,
        item.lookup_code COLLATE NOCASE,
        item.lookup_code_normalized
    ) AS product_rank
  FROM special_products legacy
  INNER JOIN catalog_items item
    ON item.snapshot_id = legacy.snapshot_id
   AND item.store_code = legacy.store_code
   AND item.lookup_code_normalized = legacy.lookup_code_normalized
  INNER JOIN catalog_snapshots snapshot
    ON snapshot.snapshot_id = item.snapshot_id
  WHERE legacy.is_marked = 1
    AND snapshot.state = 'active'
    AND item.is_active = 1
    AND TRIM(legacy.store_code) <> ''
    AND LENGTH(TRIM(legacy.store_code)) <= 128
    AND INSTR(legacy.store_code, char(0)) = 0
    AND legacy.store_code NOT GLOB (
      '*[' || char(1) || '-' || char(31) || char(127) || ']*'
    )
    AND TRIM(item.product_code) <> ''
    AND LENGTH(TRIM(item.product_code)) <= 128
    AND INSTR(item.product_code, char(0)) = 0
    AND item.product_code NOT GLOB (
      '*[' || char(1) || '-' || char(31) || char(127) || ']*'
    )
    AND TRIM(item.display_name) <> ''
    AND LENGTH(TRIM(item.display_name)) <= 512
    AND INSTR(item.display_name, char(0)) = 0
    AND item.display_name NOT GLOB (
      '*[' || char(1) || '-' || char(31) || char(127) || ']*'
    )
    AND TRIM(item.lookup_code) <> ''
    AND LENGTH(TRIM(item.lookup_code)) <= 256
    AND INSTR(item.lookup_code, char(0)) = 0
    AND item.lookup_code NOT GLOB (
      '*[' || char(1) || '-' || char(31) || char(127) || ']*'
    )
),
deduplicated AS (
  SELECT *
  FROM candidates
  WHERE product_rank = 1
),
ordered AS (
  SELECT
    *,
    ROW_NUMBER() OVER (
      PARTITION BY store_code
      ORDER BY legacy_sort_order, product_code COLLATE NOCASE, lookup_code
    ) - 1 AS migrated_sort_order
  FROM deduplicated
)
INSERT INTO local_special_products (
  store_code, product_code, reference_code, item_number, display_name,
  barcode, lookup_code, retail_price_cents, price_source, quantity_factor,
  product_image, discount_rate, sort_order
)
SELECT
  store_code, product_code, reference_code, item_number, display_name,
  barcode, lookup_code, retail_price_cents, price_source, quantity_factor,
  product_image, discount_rate, migrated_sort_order
FROM ordered;
`;

const M18 = `
-- M6 installments 是早期业务草表，缺少门店复合身份和完整只读字段。
-- 新缓存表独立演进：敏感展示字段仅进入二次加密 BLOB，不回填或复制旧草表。
CREATE TABLE installment_snapshots (
  store_code TEXT NOT NULL,
  installment_guid TEXT NOT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  total_cents INTEGER NOT NULL CHECK (
    typeof(total_cents) = 'integer'
    AND total_cents BETWEEN 0 AND 9007199254740991
  ),
  down_payment_cents INTEGER NOT NULL CHECK (
    typeof(down_payment_cents) = 'integer'
    AND down_payment_cents BETWEEN 0 AND 9007199254740991
  ),
  paid_cents INTEGER NOT NULL CHECK (
    typeof(paid_cents) = 'integer'
    AND paid_cents BETWEEN 0 AND 9007199254740991
  ),
  balance_cents INTEGER NOT NULL CHECK (
    typeof(balance_cents) = 'integer'
    AND balance_cents BETWEEN 0 AND 9007199254740991
  ),
  status TEXT NOT NULL CHECK (
    status IN ('Active', 'PaidOff', 'PickedUp', 'Cancelled')
  ),
  encrypted_sensitive_revision INTEGER NOT NULL CHECK (
    typeof(encrypted_sensitive_revision) = 'integer'
    AND encrypted_sensitive_revision = 1
  ),
  sensitive_payload_ciphertext BLOB NOT NULL CHECK (
    typeof(sensitive_payload_ciphertext) = 'blob'
    AND LENGTH(sensitive_payload_ciphertext) > 0
  ),
  PRIMARY KEY (store_code, installment_guid),
  CHECK (
    store_code = TRIM(store_code)
    AND LENGTH(store_code) BETWEEN 1 AND 128
    AND INSTR(store_code, char(0)) = 0
    AND store_code NOT GLOB (
      '*[' || char(1) || '-' || char(31) || char(127) || ']*'
    )
  ),
  CHECK (
    installment_guid = LOWER(installment_guid)
    AND LENGTH(installment_guid) = 36
    AND SUBSTR(installment_guid, 9, 1) = '-'
    AND SUBSTR(installment_guid, 14, 1) = '-'
    AND SUBSTR(installment_guid, 15, 1) GLOB '[1-8]'
    AND SUBSTR(installment_guid, 19, 1) = '-'
    AND SUBSTR(installment_guid, 20, 1) GLOB '[89ab]'
    AND SUBSTR(installment_guid, 24, 1) = '-'
    AND LENGTH(REPLACE(installment_guid, '-', '')) = 32
    AND REPLACE(installment_guid, '-', '') NOT GLOB '*[^0-9a-f]*'
  ),
  CHECK (
    LENGTH(created_at_iso) = 24
    AND created_at_iso = STRFTIME(
      '%Y-%m-%dT%H:%M:%fZ', created_at_iso
    )
  ),
  CHECK (
    LENGTH(updated_at_iso) = 24
    AND updated_at_iso = STRFTIME(
      '%Y-%m-%dT%H:%M:%fZ', updated_at_iso
    )
  )
);

CREATE INDEX ix_installment_snapshots_store_page
  ON installment_snapshots (
    store_code, created_at_iso DESC, installment_guid ASC
  );
`;

const M19 = `
-- M1 的 emergency_login_key_bundles / trusted_time_anchor 属于早期合同，
-- 缺少完整终端授权 scope，必须原样保留但不得复用于新版考勤安全缓存。
CREATE TABLE attendance_qr_provisioning_cache (
  scope_hash TEXT PRIMARY KEY CHECK (
    LENGTH(scope_hash) = 64
    AND scope_hash = LOWER(scope_hash)
    AND scope_hash NOT GLOB '*[^0-9a-f]*'
  ),
  api_partition TEXT NOT NULL,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  payload_revision INTEGER NOT NULL CHECK (
    typeof(payload_revision) = 'integer' AND payload_revision = 1
  ),
  provisioning_ciphertext BLOB NOT NULL CHECK (
    typeof(provisioning_ciphertext) = 'blob'
    AND LENGTH(provisioning_ciphertext) > 0
  ),
  updated_at_iso TEXT NOT NULL,
  CHECK (
    api_partition = TRIM(api_partition)
    AND LENGTH(api_partition) BETWEEN 1 AND 2048
    AND INSTR(api_partition, char(0)) = 0
  ),
  CHECK (
    store_code = TRIM(store_code)
    AND LENGTH(store_code) BETWEEN 1 AND 50
    AND INSTR(store_code, char(0)) = 0
  ),
  CHECK (
    device_code = TRIM(device_code)
    AND LENGTH(device_code) BETWEEN 1 AND 128
    AND INSTR(device_code, char(0)) = 0
  ),
  CHECK (
    LENGTH(updated_at_iso) = 24
    AND updated_at_iso = STRFTIME(
      '%Y-%m-%dT%H:%M:%fZ', updated_at_iso
    )
  )
);

CREATE TABLE emergency_public_key_package_cache (
  scope_hash TEXT PRIMARY KEY CHECK (
    LENGTH(scope_hash) = 64
    AND scope_hash = LOWER(scope_hash)
    AND scope_hash NOT GLOB '*[^0-9a-f]*'
  ),
  api_partition TEXT NOT NULL,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  package_version INTEGER NOT NULL CHECK (
    typeof(package_version) = 'integer'
    AND package_version BETWEEN 0 AND 9007199254740991
  ),
  generated_at_epoch_ms INTEGER NOT NULL CHECK (
    typeof(generated_at_epoch_ms) = 'integer'
    AND generated_at_epoch_ms BETWEEN 0 AND 9007199254740991
  ),
  active_key_id TEXT NULL CHECK (
    active_key_id IS NULL
    OR (
      LENGTH(active_key_id) BETWEEN 1 AND 32
      AND active_key_id NOT GLOB '*[^A-Za-z0-9]*'
    )
  ),
  keys_json TEXT NOT NULL CHECK (
    typeof(keys_json) = 'text'
    AND LENGTH(keys_json) BETWEEN 2 AND 1048576
  ),
  updated_at_iso TEXT NOT NULL,
  CHECK (
    api_partition = TRIM(api_partition)
    AND LENGTH(api_partition) BETWEEN 1 AND 2048
    AND INSTR(api_partition, char(0)) = 0
  ),
  CHECK (
    store_code = TRIM(store_code)
    AND LENGTH(store_code) BETWEEN 1 AND 50
    AND INSTR(store_code, char(0)) = 0
  ),
  CHECK (
    device_code = TRIM(device_code)
    AND LENGTH(device_code) BETWEEN 1 AND 128
    AND INSTR(device_code, char(0)) = 0
  ),
  CHECK (
    LENGTH(updated_at_iso) = 24
    AND updated_at_iso = STRFTIME(
      '%Y-%m-%dT%H:%M:%fZ', updated_at_iso
    )
  )
);

CREATE TABLE emergency_trusted_time_cache (
  scope_hash TEXT PRIMARY KEY CHECK (
    LENGTH(scope_hash) = 64
    AND scope_hash = LOWER(scope_hash)
    AND scope_hash NOT GLOB '*[^0-9a-f]*'
  ),
  api_partition TEXT NOT NULL,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  payload_revision INTEGER NOT NULL CHECK (
    typeof(payload_revision) = 'integer' AND payload_revision = 1
  ),
  trusted_time_ciphertext BLOB NOT NULL CHECK (
    typeof(trusted_time_ciphertext) = 'blob'
    AND LENGTH(trusted_time_ciphertext) > 0
  ),
  updated_at_iso TEXT NOT NULL,
  CHECK (
    api_partition = TRIM(api_partition)
    AND LENGTH(api_partition) BETWEEN 1 AND 2048
    AND INSTR(api_partition, char(0)) = 0
  ),
  CHECK (
    store_code = TRIM(store_code)
    AND LENGTH(store_code) BETWEEN 1 AND 50
    AND INSTR(store_code, char(0)) = 0
  ),
  CHECK (
    device_code = TRIM(device_code)
    AND LENGTH(device_code) BETWEEN 1 AND 128
    AND INSTR(device_code, char(0)) = 0
  ),
  CHECK (
    LENGTH(updated_at_iso) = 24
    AND updated_at_iso = STRFTIME(
      '%Y-%m-%dT%H:%M:%fZ', updated_at_iso
    )
  )
);
`;

const M20 = `
-- 支付 action 是耐久恢复事实；客户、购物车和冻结命令只进入二次加密 BLOB。
CREATE TABLE installment_actions (
  action_id TEXT PRIMARY KEY,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  installment_guid TEXT NOT NULL,
  action_kind TEXT NOT NULL CHECK (
    action_kind IN ('create', 'repayment', 'cancel-refund')
  ),
  idempotency_key TEXT NOT NULL UNIQUE,
  payment_guid TEXT NULL,
  payment_method TEXT NULL CHECK (
    payment_method IS NULL OR payment_method IN ('cash', 'card', 'voucher')
  ),
  amount_cents INTEGER NULL CHECK (
    amount_cents IS NULL
    OR (
      typeof(amount_cents) = 'integer'
      AND amount_cents BETWEEN 1 AND 9007199254740991
    )
  ),
  state TEXT NOT NULL CHECK (
    state IN (
      'Created', 'ProviderPending', 'Unknown', 'Approved', 'BackendPending'
    )
  ),
  resolution TEXT NULL CHECK (
    resolution IS NULL OR resolution IN ('Declined', 'Completed')
  ),
  payload_revision INTEGER NOT NULL CHECK (
    typeof(payload_revision) = 'integer' AND payload_revision = 1
  ),
  command_ciphertext BLOB NOT NULL CHECK (
    typeof(command_ciphertext) = 'blob'
    AND LENGTH(command_ciphertext) > 0
  ),
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  resolved_at_iso TEXT NULL,
  CHECK (idempotency_key = action_id),
  CHECK (
    (
      action_kind = 'cancel-refund'
      AND payment_guid IS NULL
      AND payment_method IS NULL
      AND amount_cents IS NULL
    )
    OR (
      action_kind IN ('create', 'repayment')
      AND payment_guid IS NOT NULL
      AND payment_method IS NOT NULL
      AND amount_cents IS NOT NULL
    )
  ),
  CHECK (
    (resolution IS NULL AND resolved_at_iso IS NULL)
    OR (resolution IS NOT NULL AND resolved_at_iso IS NOT NULL)
  ),
  CHECK (
    resolution IS NULL
    OR (
      resolution = 'Declined'
      AND state IN ('ProviderPending', 'Unknown')
    )
    OR (
      resolution = 'Completed'
      AND state = 'BackendPending'
    )
  ),
  CHECK (
    store_code = TRIM(store_code)
    AND LENGTH(store_code) BETWEEN 1 AND 50
    AND INSTR(store_code, char(0)) = 0
  ),
  CHECK (
    device_code = TRIM(device_code)
    AND LENGTH(device_code) BETWEEN 1 AND 128
    AND INSTR(device_code, char(0)) = 0
  ),
  CHECK (
    action_id = LOWER(action_id)
    AND LENGTH(action_id) = 36
    AND SUBSTR(action_id, 9, 1) = '-'
    AND SUBSTR(action_id, 14, 1) = '-'
    AND SUBSTR(action_id, 15, 1) GLOB '[1-5]'
    AND SUBSTR(action_id, 19, 1) = '-'
    AND SUBSTR(action_id, 20, 1) GLOB '[89ab]'
    AND SUBSTR(action_id, 24, 1) = '-'
    AND LENGTH(REPLACE(action_id, '-', '')) = 32
    AND REPLACE(action_id, '-', '') NOT GLOB '*[^0-9a-f]*'
  ),
  CHECK (
    installment_guid = LOWER(installment_guid)
    AND LENGTH(installment_guid) = 36
    AND SUBSTR(installment_guid, 9, 1) = '-'
    AND SUBSTR(installment_guid, 14, 1) = '-'
    AND SUBSTR(installment_guid, 15, 1) GLOB '[1-5]'
    AND SUBSTR(installment_guid, 19, 1) = '-'
    AND SUBSTR(installment_guid, 20, 1) GLOB '[89ab]'
    AND SUBSTR(installment_guid, 24, 1) = '-'
    AND LENGTH(REPLACE(installment_guid, '-', '')) = 32
    AND REPLACE(installment_guid, '-', '') NOT GLOB '*[^0-9a-f]*'
  ),
  CHECK (
    payment_guid IS NULL
    OR (
      payment_guid = LOWER(payment_guid)
      AND LENGTH(payment_guid) = 36
      AND SUBSTR(payment_guid, 9, 1) = '-'
      AND SUBSTR(payment_guid, 14, 1) = '-'
      AND SUBSTR(payment_guid, 15, 1) GLOB '[1-5]'
      AND SUBSTR(payment_guid, 19, 1) = '-'
      AND SUBSTR(payment_guid, 20, 1) GLOB '[89ab]'
      AND SUBSTR(payment_guid, 24, 1) = '-'
      AND LENGTH(REPLACE(payment_guid, '-', '')) = 32
      AND REPLACE(payment_guid, '-', '') NOT GLOB '*[^0-9a-f]*'
    )
  ),
  CHECK (
    LENGTH(created_at_iso) = 24
    AND created_at_iso = STRFTIME(
      '%Y-%m-%dT%H:%M:%fZ', created_at_iso
    )
  ),
  CHECK (
    LENGTH(updated_at_iso) = 24
    AND updated_at_iso = STRFTIME(
      '%Y-%m-%dT%H:%M:%fZ', updated_at_iso
    )
  ),
  CHECK (
    resolved_at_iso IS NULL
    OR (
      LENGTH(resolved_at_iso) = 24
      AND resolved_at_iso = STRFTIME(
        '%Y-%m-%dT%H:%M:%fZ', resolved_at_iso
      )
    )
  )
);

CREATE UNIQUE INDEX ux_installment_actions_terminal_blocking
  ON installment_actions (store_code, device_code)
  WHERE resolution IS NULL;

CREATE INDEX ix_installment_actions_terminal_history
  ON installment_actions (
    store_code, device_code, created_at_iso DESC, action_id
  );
`;

const M21 = `
-- action identity、冻结密文和创建事实一经插入不可更换。
CREATE TRIGGER trg_installment_actions_immutable
BEFORE UPDATE ON installment_actions
FOR EACH ROW
WHEN
  NEW.action_id IS NOT OLD.action_id
  OR NEW.store_code IS NOT OLD.store_code
  OR NEW.device_code IS NOT OLD.device_code
  OR NEW.installment_guid IS NOT OLD.installment_guid
  OR NEW.action_kind IS NOT OLD.action_kind
  OR NEW.idempotency_key IS NOT OLD.idempotency_key
  OR NEW.payment_guid IS NOT OLD.payment_guid
  OR NEW.payment_method IS NOT OLD.payment_method
  OR NEW.amount_cents IS NOT OLD.amount_cents
  OR NEW.payload_revision IS NOT OLD.payload_revision
  OR NEW.command_ciphertext IS NOT OLD.command_ciphertext
  OR NEW.created_at_iso IS NOT OLD.created_at_iso
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_ACTION_IMMUTABLE');
END;

CREATE TRIGGER trg_installment_actions_state_transition
BEFORE UPDATE OF state ON installment_actions
FOR EACH ROW
WHEN
  NEW.state IS NOT OLD.state
  AND NOT (
    (OLD.state = 'Created' AND NEW.state = 'ProviderPending')
    OR (
      OLD.state = 'ProviderPending'
      AND NEW.state IN ('Unknown', 'Approved')
    )
    OR (OLD.state = 'Unknown' AND NEW.state = 'Approved')
    OR (OLD.state = 'Approved' AND NEW.state = 'BackendPending')
  )
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_ACTION_STATE_TRANSITION_INVALID');
END;

CREATE TRIGGER trg_installment_actions_resolution_transition
BEFORE UPDATE OF resolution, resolved_at_iso ON installment_actions
FOR EACH ROW
WHEN
  (
    OLD.resolution IS NOT NULL
    AND (
      NEW.resolution IS NOT OLD.resolution
      OR NEW.resolved_at_iso IS NOT OLD.resolved_at_iso
    )
  )
  OR (
    OLD.resolution IS NULL
    AND NEW.resolution IS NULL
    AND NEW.resolved_at_iso IS NOT NULL
  )
  OR (
    OLD.resolution IS NULL
    AND NEW.resolution IS NOT NULL
    AND NOT (
      (
        NEW.resolution = 'Declined'
        AND OLD.state IN ('ProviderPending', 'Unknown')
        AND NEW.state = OLD.state
        AND NEW.resolved_at_iso IS NOT NULL
      )
      OR (
        NEW.resolution = 'Completed'
        AND OLD.state = 'BackendPending'
        AND NEW.state = OLD.state
        AND NEW.resolved_at_iso IS NOT NULL
      )
    )
  )
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_ACTION_RESOLUTION_INVALID');
END;

CREATE TRIGGER trg_installment_actions_no_delete
BEFORE DELETE ON installment_actions
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_ACTION_DELETE_FORBIDDEN');
END;
`;

const M22 = `
CREATE TABLE installment_voucher_intents (
  action_id TEXT PRIMARY KEY,
  installment_guid TEXT NOT NULL,
  payment_guid TEXT NOT NULL,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  cashier_id TEXT NOT NULL,
  amount_cents INTEGER NOT NULL CHECK (
    typeof(amount_cents) = 'integer' AND amount_cents > 0
  ),
  payload_revision INTEGER NOT NULL CHECK (payload_revision = 1),
  intent_ciphertext BLOB NOT NULL,
  created_at_iso TEXT NOT NULL
);

-- action ledger 先于 provider plan 建立；plan 本身只是一条不可变绑定事实。
CREATE TABLE installment_provider_plans (
  action_id TEXT PRIMARY KEY
    REFERENCES installment_actions(action_id),
  created_at_iso TEXT NOT NULL
);

CREATE TABLE installment_provider_attempts (
  attempt_id TEXT PRIMARY KEY,
  action_id TEXT NOT NULL
    REFERENCES installment_provider_plans(action_id),
  payment_guid TEXT NOT NULL,
  source_payment_guid TEXT NULL,
  original_tender_evidence_id TEXT NOT NULL,
  source_attempt_id TEXT NULL,
  sequence INTEGER NOT NULL CHECK (
    typeof(sequence) = 'integer' AND sequence >= 0
  ),
  provider TEXT NOT NULL CHECK (
    provider IN ('square', 'linkly-cloud', 'voucher')
  ),
  operation TEXT NOT NULL CHECK (
    operation IN ('purchase', 'refund')
  ),
  amount_cents INTEGER NOT NULL CHECK (
    typeof(amount_cents) = 'integer'
    AND amount_cents != 0
    AND (
      (operation = 'purchase' AND amount_cents > 0)
      OR (operation = 'refund' AND amount_cents < 0)
    )
  ),
  state TEXT NOT NULL CHECK (
    state IN (
      'Created', 'Submitted', 'Pending', 'Approved',
      'Declined', 'Cancelled', 'Unknown'
    )
  ),
  idempotency_key TEXT NOT NULL UNIQUE,
  payload_revision INTEGER NOT NULL CHECK (payload_revision = 1),
  protected_payload_ciphertext BLOB NOT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  CHECK (
    (operation = 'purchase'
      AND source_payment_guid IS NULL
      AND source_attempt_id IS NULL)
    OR
    (operation = 'refund'
      AND source_payment_guid IS NOT NULL
      AND source_attempt_id IS NOT NULL)
  ),
  UNIQUE (action_id, sequence),
  UNIQUE (action_id, payment_guid)
);

CREATE INDEX ix_installment_provider_attempts_action_state
  ON installment_provider_attempts (action_id, state, sequence);

CREATE TABLE installment_cash_settlements (
  settlement_id TEXT PRIMARY KEY,
  action_id TEXT NOT NULL
    REFERENCES installment_provider_plans(action_id),
  payment_guid TEXT NOT NULL,
  source_payment_guid TEXT NULL,
  original_tender_evidence_id TEXT NOT NULL,
  source_attempt_id TEXT NULL,
  sequence INTEGER NOT NULL CHECK (
    typeof(sequence) = 'integer' AND sequence >= 0
  ),
  operation TEXT NOT NULL CHECK (
    operation IN ('purchase', 'refund')
  ),
  amount_cents INTEGER NOT NULL CHECK (
    typeof(amount_cents) = 'integer' AND amount_cents > 0
  ),
  idempotency_key TEXT NOT NULL,
  state TEXT NOT NULL CHECK (state IN ('Prepared', 'Approved')),
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  CHECK (
    (operation = 'purchase'
      AND source_payment_guid IS NULL
      AND source_attempt_id IS NULL)
    OR
    (operation = 'refund'
      AND source_payment_guid IS NOT NULL
      AND source_attempt_id IS NOT NULL)
  ),
  UNIQUE (action_id, sequence),
  UNIQUE (action_id, payment_guid)
);

CREATE INDEX ix_installment_cash_settlements_action_state
  ON installment_cash_settlements (action_id, state, sequence);

CREATE TABLE installment_approved_materials (
  attempt_id TEXT PRIMARY KEY
    REFERENCES installment_provider_attempts(attempt_id),
  payload_revision INTEGER NOT NULL CHECK (payload_revision = 1),
  material_ciphertext BLOB NOT NULL,
  created_at_iso TEXT NOT NULL
);

-- 本地 purchase 与 Hbpos 受保护导入共用描述符；原卡引用和券材料只在密文。
CREATE TABLE installment_original_tender_evidence (
  evidence_id TEXT PRIMARY KEY,
  origin_action_id TEXT NULL
    REFERENCES installment_actions(action_id),
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  installment_guid TEXT NOT NULL,
  payment_guid TEXT NOT NULL,
  source_attempt_id TEXT NOT NULL,
  method TEXT NOT NULL CHECK (method IN ('cash', 'card', 'voucher')),
  amount_cents INTEGER NOT NULL CHECK (
    typeof(amount_cents) = 'integer' AND amount_cents > 0
  ),
  provider TEXT NULL CHECK (
    provider IS NULL OR provider IN ('square', 'linkly-cloud', 'voucher')
  ),
  provenance TEXT NOT NULL CHECK (
    provenance IN ('local-approved-attempt', 'hbpos-protected-details')
  ),
  payload_revision INTEGER NOT NULL CHECK (payload_revision = 1),
  protected_payload_ciphertext BLOB NOT NULL,
  created_at_iso TEXT NOT NULL,
  CHECK (
    (method = 'cash' AND provider IS NULL)
    OR (method = 'voucher' AND provider = 'voucher')
    OR (method = 'card' AND provider IN ('square', 'linkly-cloud'))
  ),
  CHECK (
    (provenance = 'local-approved-attempt' AND origin_action_id IS NOT NULL)
    OR
    (provenance = 'hbpos-protected-details' AND origin_action_id IS NULL)
  )
);

CREATE INDEX ix_installment_original_tender_installment
  ON installment_original_tender_evidence (
    store_code, installment_guid, created_at_iso
  );

CREATE INDEX ix_installment_original_tender_payment
  ON installment_original_tender_evidence (
    payment_guid, source_attempt_id
  );

CREATE TABLE installment_refund_provenance_snapshots (
  refund_action_id TEXT PRIMARY KEY
    REFERENCES installment_actions(action_id),
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  installment_guid TEXT NOT NULL,
  paid_amount_cents INTEGER NOT NULL CHECK (
    typeof(paid_amount_cents) = 'integer' AND paid_amount_cents > 0
  ),
  created_at_iso TEXT NOT NULL
);

CREATE TABLE installment_refund_provenance_items (
  refund_action_id TEXT NOT NULL
    REFERENCES installment_refund_provenance_snapshots(refund_action_id),
  sequence INTEGER NOT NULL CHECK (
    typeof(sequence) = 'integer' AND sequence >= 0
  ),
  evidence_id TEXT NOT NULL UNIQUE
    REFERENCES installment_original_tender_evidence(evidence_id),
  source_payment_guid TEXT NOT NULL UNIQUE,
  source_attempt_id TEXT NOT NULL UNIQUE,
  PRIMARY KEY (refund_action_id, sequence)
);

CREATE TABLE installment_voucher_protected_states (
  protected_reference TEXT PRIMARY KEY,
  attempt_id TEXT NOT NULL UNIQUE
    REFERENCES installment_provider_attempts(attempt_id),
  action_id TEXT NOT NULL
    REFERENCES installment_actions(action_id),
  idempotency_key TEXT NOT NULL,
  payload_revision INTEGER NOT NULL CHECK (payload_revision = 1),
  state_ciphertext BLOB NOT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL
);

CREATE TRIGGER trg_installment_voucher_intents_immutable
BEFORE UPDATE ON installment_voucher_intents
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_VOUCHER_INTENT_IMMUTABLE');
END;

CREATE TRIGGER trg_installment_voucher_intents_no_delete
BEFORE DELETE ON installment_voucher_intents
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_VOUCHER_INTENT_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_installment_provider_plans_immutable
BEFORE UPDATE ON installment_provider_plans
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_PROVIDER_PLAN_IMMUTABLE');
END;

CREATE TRIGGER trg_installment_provider_plans_no_delete
BEFORE DELETE ON installment_provider_plans
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_PROVIDER_PLAN_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_installment_provider_attempts_insert_guard
BEFORE INSERT ON installment_provider_attempts
FOR EACH ROW
WHEN NEW.state != 'Created'
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_PROVIDER_ATTEMPT_INITIAL_STATE_INVALID');
END;

CREATE TRIGGER trg_installment_provider_attempts_update_guard
BEFORE UPDATE ON installment_provider_attempts
FOR EACH ROW
WHEN
  NEW.attempt_id != OLD.attempt_id
  OR NEW.action_id != OLD.action_id
  OR NEW.payment_guid != OLD.payment_guid
  OR NEW.source_payment_guid IS NOT OLD.source_payment_guid
  OR NEW.original_tender_evidence_id != OLD.original_tender_evidence_id
  OR NEW.source_attempt_id IS NOT OLD.source_attempt_id
  OR NEW.sequence != OLD.sequence
  OR NEW.provider != OLD.provider
  OR NEW.operation != OLD.operation
  OR NEW.amount_cents != OLD.amount_cents
  OR NEW.idempotency_key != OLD.idempotency_key
  OR NEW.payload_revision != OLD.payload_revision
  OR NEW.created_at_iso != OLD.created_at_iso
  OR NOT (
    NEW.state = OLD.state
    OR (OLD.state = 'Created'
      AND NEW.state IN ('Submitted', 'Cancelled'))
    OR (OLD.state = 'Submitted'
      AND NEW.state IN (
        'Pending', 'Approved', 'Declined', 'Cancelled', 'Unknown'
      ))
    OR (OLD.state = 'Pending'
      AND NEW.state IN ('Approved', 'Declined', 'Cancelled', 'Unknown'))
    OR (OLD.state = 'Unknown'
      AND NEW.state IN ('Pending', 'Approved', 'Declined', 'Cancelled'))
  )
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_PROVIDER_ATTEMPT_UPDATE_INVALID');
END;

CREATE TRIGGER trg_installment_provider_attempts_approved_material
BEFORE UPDATE OF state ON installment_provider_attempts
FOR EACH ROW
WHEN NEW.state = 'Approved' AND (
  NOT EXISTS (
    SELECT 1
    FROM installment_approved_materials material
    WHERE material.attempt_id = NEW.attempt_id
  )
  OR (
    NEW.operation = 'purchase'
    AND NOT EXISTS (
      SELECT 1
      FROM installment_original_tender_evidence evidence
      WHERE evidence.evidence_id = NEW.original_tender_evidence_id
        AND evidence.origin_action_id = NEW.action_id
        AND evidence.payment_guid = NEW.payment_guid
        AND evidence.source_attempt_id = NEW.attempt_id
        AND evidence.amount_cents = ABS(NEW.amount_cents)
    )
  )
)
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_PROVIDER_APPROVED_MATERIAL_REQUIRED');
END;

CREATE TRIGGER trg_installment_provider_attempts_no_delete
BEFORE DELETE ON installment_provider_attempts
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_PROVIDER_ATTEMPT_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_installment_cash_settlements_insert_guard
BEFORE INSERT ON installment_cash_settlements
FOR EACH ROW
WHEN NEW.state != 'Prepared'
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_CASH_INITIAL_STATE_INVALID');
END;

CREATE TRIGGER trg_installment_cash_settlements_update_guard
BEFORE UPDATE ON installment_cash_settlements
FOR EACH ROW
WHEN
  NEW.settlement_id != OLD.settlement_id
  OR NEW.action_id != OLD.action_id
  OR NEW.payment_guid != OLD.payment_guid
  OR NEW.source_payment_guid IS NOT OLD.source_payment_guid
  OR NEW.original_tender_evidence_id != OLD.original_tender_evidence_id
  OR NEW.source_attempt_id IS NOT OLD.source_attempt_id
  OR NEW.sequence != OLD.sequence
  OR NEW.operation != OLD.operation
  OR NEW.amount_cents != OLD.amount_cents
  OR NEW.idempotency_key != OLD.idempotency_key
  OR NEW.created_at_iso != OLD.created_at_iso
  OR NOT (
    NEW.state = OLD.state
    OR (OLD.state = 'Prepared' AND NEW.state = 'Approved')
  )
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_CASH_UPDATE_INVALID');
END;

CREATE TRIGGER trg_installment_cash_settlements_approved_evidence
BEFORE UPDATE OF state ON installment_cash_settlements
FOR EACH ROW
WHEN NEW.state = 'Approved'
  AND NEW.operation = 'purchase'
  AND NOT EXISTS (
    SELECT 1
    FROM installment_original_tender_evidence evidence
    WHERE evidence.evidence_id = NEW.original_tender_evidence_id
      AND evidence.origin_action_id = NEW.action_id
      AND evidence.payment_guid = NEW.payment_guid
      AND evidence.source_attempt_id = NEW.settlement_id
      AND evidence.amount_cents = NEW.amount_cents
      AND evidence.method = 'cash'
  )
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_CASH_APPROVED_EVIDENCE_REQUIRED');
END;

CREATE TRIGGER trg_installment_cash_settlements_no_delete
BEFORE DELETE ON installment_cash_settlements
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_CASH_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_installment_approved_materials_immutable
BEFORE UPDATE ON installment_approved_materials
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_APPROVED_MATERIAL_IMMUTABLE');
END;

CREATE TRIGGER trg_installment_approved_materials_no_delete
BEFORE DELETE ON installment_approved_materials
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_APPROVED_MATERIAL_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_installment_original_tender_evidence_immutable
BEFORE UPDATE ON installment_original_tender_evidence
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_ORIGINAL_TENDER_IMMUTABLE');
END;

CREATE TRIGGER trg_installment_original_tender_evidence_no_delete
BEFORE DELETE ON installment_original_tender_evidence
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_ORIGINAL_TENDER_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_installment_refund_snapshot_immutable
BEFORE UPDATE ON installment_refund_provenance_snapshots
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_REFUND_SNAPSHOT_IMMUTABLE');
END;

CREATE TRIGGER trg_installment_refund_snapshot_no_delete
BEFORE DELETE ON installment_refund_provenance_snapshots
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_REFUND_SNAPSHOT_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_installment_refund_item_immutable
BEFORE UPDATE ON installment_refund_provenance_items
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_REFUND_ITEM_IMMUTABLE');
END;

CREATE TRIGGER trg_installment_refund_item_binding
BEFORE INSERT ON installment_refund_provenance_items
FOR EACH ROW
WHEN NOT EXISTS (
  SELECT 1
  FROM installment_original_tender_evidence evidence
  WHERE evidence.evidence_id = NEW.evidence_id
    AND evidence.payment_guid = NEW.source_payment_guid
    AND evidence.source_attempt_id = NEW.source_attempt_id
)
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_REFUND_ITEM_BINDING_INVALID');
END;

CREATE TRIGGER trg_installment_refund_item_no_delete
BEFORE DELETE ON installment_refund_provenance_items
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_REFUND_ITEM_DELETE_FORBIDDEN');
END;

CREATE TRIGGER trg_installment_voucher_state_update_guard
BEFORE UPDATE ON installment_voucher_protected_states
FOR EACH ROW
WHEN
  NEW.protected_reference != OLD.protected_reference
  OR NEW.attempt_id != OLD.attempt_id
  OR NEW.action_id != OLD.action_id
  OR NEW.idempotency_key != OLD.idempotency_key
  OR NEW.payload_revision != OLD.payload_revision
  OR NEW.created_at_iso != OLD.created_at_iso
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_VOUCHER_STATE_REBIND_FORBIDDEN');
END;

CREATE TRIGGER trg_installment_voucher_state_no_delete
BEFORE DELETE ON installment_voucher_protected_states
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_VOUCHER_STATE_DELETE_FORBIDDEN');
END;
`;

const M23 = `
-- 在线精确查询只写增量覆盖，不修改带校验和的不可变 active 快照。
-- base_snapshot_id 也允许保存无 active 快照时的内部代次哨兵。
CREATE TABLE catalog_lookup_overlays (
  base_snapshot_id TEXT NOT NULL CHECK (TRIM(base_snapshot_id) <> ''),
  store_code TEXT NOT NULL CHECK (TRIM(store_code) <> ''),
  lookup_code_normalized TEXT NOT NULL CHECK (
    TRIM(lookup_code_normalized) <> ''
    AND lookup_code_normalized = UPPER(TRIM(lookup_code_normalized))
  ),
  record_kind TEXT NOT NULL CHECK (
    record_kind IN ('item', 'tombstone')
  ),
  product_code TEXT NULL,
  reference_code TEXT NULL,
  item_number TEXT NULL,
  display_name TEXT NULL,
  barcode TEXT NULL,
  lookup_code TEXT NULL,
  retail_price_cents INTEGER NULL,
  price_source INTEGER NULL,
  price_source_label TEXT NULL,
  quantity_factor TEXT NULL,
  tax_rate_basis_points INTEGER NULL,
  updated_at_iso TEXT NULL,
  row_version TEXT NULL,
  product_image TEXT NULL,
  discount_rate TEXT NULL,
  is_special_product INTEGER NULL CHECK (
    is_special_product IS NULL OR is_special_product IN (0, 1)
  ),
  verified_at_iso TEXT NOT NULL CHECK (TRIM(verified_at_iso) <> ''),
  PRIMARY KEY (
    base_snapshot_id,
    store_code,
    lookup_code_normalized
  ),
  CHECK (
    (
      record_kind = 'tombstone'
      AND product_code IS NULL
      AND reference_code IS NULL
      AND item_number IS NULL
      AND display_name IS NULL
      AND barcode IS NULL
      AND lookup_code IS NULL
      AND retail_price_cents IS NULL
      AND price_source IS NULL
      AND price_source_label IS NULL
      AND quantity_factor IS NULL
      AND tax_rate_basis_points IS NULL
      AND updated_at_iso IS NULL
      AND row_version IS NULL
      AND product_image IS NULL
      AND discount_rate IS NULL
      AND is_special_product IS NULL
    )
    OR
    (
      record_kind = 'item'
      AND product_code IS NOT NULL
      AND TRIM(product_code) <> ''
      AND display_name IS NOT NULL
      AND TRIM(display_name) <> ''
      AND lookup_code IS NOT NULL
      AND TRIM(lookup_code) <> ''
      AND typeof(retail_price_cents) = 'integer'
      AND price_source IN (0, 1, 2, 3, 4)
      AND price_source_label IS NOT NULL
      AND TRIM(price_source_label) <> ''
      AND quantity_factor IS NOT NULL
      AND is_special_product IN (0, 1)
    )
  )
);
CREATE INDEX ix_catalog_lookup_overlays_search
  ON catalog_lookup_overlays (
    base_snapshot_id,
    store_code,
    record_kind,
    display_name COLLATE NOCASE,
    item_number COLLATE NOCASE,
    lookup_code_normalized
  );
`;

const M24 = `
-- mixed cash action 同时冻结入账、实收和找零；M23 及更早的历史行保持
-- nullable，并由读取层按 tendered=amount/change=0 兼容。
ALTER TABLE mixed_cash_tender_actions
  ADD COLUMN tendered_cents INTEGER NULL CHECK (
    tendered_cents IS NULL
    OR (
      typeof(tendered_cents) = 'integer'
      AND tendered_cents BETWEEN 1 AND 9007199254740991
      AND tendered_cents >= amount_cents
    )
  );

ALTER TABLE mixed_cash_tender_actions
  ADD COLUMN change_cents INTEGER NULL CHECK (
    change_cents IS NULL
    OR (
      typeof(change_cents) = 'integer'
      AND change_cents BETWEEN 0 AND 9007199254740991
    )
  );

-- 升级不会改写旧的不可变 action；升级后的新 action 必须持久化完整三值，
-- 不能依赖审计 JSON 或 UI 内存重新推导支付事实。
CREATE TRIGGER trg_mixed_cash_tender_action_amounts_insert
BEFORE INSERT ON mixed_cash_tender_actions
FOR EACH ROW
WHEN
  NEW.tendered_cents IS NULL
  OR NEW.change_cents IS NULL
  OR NEW.tendered_cents < NEW.amount_cents
  OR NEW.tendered_cents - NEW.amount_cents != NEW.change_cents
BEGIN
  SELECT RAISE(ABORT, 'MIXED_CASH_ACTION_AMOUNTS_INVALID');
END;
`;

const M25 = `
-- generation_id 是逻辑目录代次；delta 激活时物理 snapshot_id 保持不变，
-- 从而无需复制或重写数十万条 active 商品。
ALTER TABLE catalog_snapshots
  ADD COLUMN generation_id TEXT NULL CHECK (
    generation_id IS NULL OR TRIM(generation_id) <> ''
  );

ALTER TABLE catalog_snapshots
  ADD COLUMN sync_mode TEXT NOT NULL DEFAULT 'full' CHECK (
    sync_mode IN ('full', 'delta')
  );

ALTER TABLE catalog_snapshots
  ADD COLUMN base_snapshot_id TEXT NULL CHECK (
    base_snapshot_id IS NULL OR TRIM(base_snapshot_id) <> ''
  );

ALTER TABLE catalog_snapshots
  ADD COLUMN base_catalog_version TEXT NULL CHECK (
    base_catalog_version IS NULL OR TRIM(base_catalog_version) <> ''
  );

UPDATE catalog_snapshots
SET generation_id = snapshot_id
WHERE generation_id IS NULL;

-- 旧版 app 不认识 generation_id；触发器让其新增的 full staging 仍获得安全代次。
CREATE TRIGGER trg_catalog_snapshots_generation_default
AFTER INSERT ON catalog_snapshots
FOR EACH ROW
WHEN NEW.generation_id IS NULL
BEGIN
  UPDATE catalog_snapshots
  SET generation_id = NEW.snapshot_id
  WHERE snapshot_id = NEW.snapshot_id
    AND generation_id IS NULL;
END;

CREATE TABLE catalog_delta_deletions (
  snapshot_id TEXT NOT NULL
    REFERENCES catalog_snapshots(snapshot_id) ON DELETE CASCADE,
  store_code TEXT NOT NULL CHECK (TRIM(store_code) <> ''),
  lookup_code_normalized TEXT NOT NULL CHECK (
    TRIM(lookup_code_normalized) <> ''
    AND lookup_code_normalized = UPPER(TRIM(lookup_code_normalized))
  ),
  PRIMARY KEY (snapshot_id, store_code, lookup_code_normalized)
);
`;

const M26 = `
-- 员工操作事实仍保留在 audit_events；投递状态单独推进，绝不影响订单/支付原子写入。
ALTER TABLE audit_events
  ADD COLUMN delivery_state TEXT NOT NULL DEFAULT 'pending' CHECK (
    delivery_state IN ('pending', 'uploaded', 'rejected')
  );

ALTER TABLE audit_events
  ADD COLUMN attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0);

ALTER TABLE audit_events
  ADD COLUMN next_attempt_at_iso TEXT NULL;

ALTER TABLE audit_events
  ADD COLUMN last_error_code TEXT NULL;

UPDATE audit_events
SET next_attempt_at_iso = occurred_at_iso
WHERE next_attempt_at_iso IS NULL;

CREATE INDEX ix_audit_events_delivery_ready
  ON audit_events (delivery_state, next_attempt_at_iso, occurred_at_iso);

-- 程序日志与员工审计严格分表，避免运行错误改变员工行为的可追溯语义。
CREATE TABLE application_log_outbox (
  event_id TEXT PRIMARY KEY,
  occurred_at_iso TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  delivery_state TEXT NOT NULL DEFAULT 'pending' CHECK (
    delivery_state IN ('pending', 'rejected')
  ),
  attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
  next_attempt_at_iso TEXT NOT NULL,
  last_error_code TEXT NULL,
  created_at_iso TEXT NOT NULL
);

CREATE INDEX ix_application_log_outbox_ready
  ON application_log_outbox (delivery_state, next_attempt_at_iso, occurred_at_iso);
`;

const M27 = `
-- 旧支付草稿曾把 payment:<OrderGuid>:<序号> 当作订单行 Guid 保存。
-- 只恢复确认由支付草稿绑定、且因此被 ORDER_LINE_INVALID 拒绝的订单；
-- 行主键保持不变，由同步适配器稳定映射，避免破坏不可变绑定和退货引用。
UPDATE local_orders
SET state = 'PendingSync',
  updated_at_iso = (
    SELECT ob.updated_at_iso
    FROM outbox_messages ob
    WHERE ob.aggregate_id = local_orders.order_guid
      AND ob.kind = 'order-sync'
    LIMIT 1
  )
WHERE state = 'Rejected'
  AND EXISTS (
    SELECT 1
    FROM outbox_messages ob
    WHERE ob.aggregate_id = local_orders.order_guid
      AND ob.kind = 'order-sync'
      AND ob.state = 'rejected'
      AND ob.last_error_code = 'ORDER_LINE_INVALID'
  )
  AND EXISTS (
    SELECT 1
    FROM local_order_lines line
    WHERE line.order_guid = local_orders.order_guid
  )
  AND NOT EXISTS (
    SELECT 1
    FROM local_order_lines line
    LEFT JOIN payment_order_draft_line_bindings binding
      ON binding.order_guid = line.order_guid
     AND binding.order_line_id = line.line_id
     AND binding.line_sequence = line.line_sequence
    WHERE line.order_guid = local_orders.order_guid
      AND (
        line.line_id <> (
          'payment:' || local_orders.order_guid || ':' ||
          CAST(line.line_sequence AS TEXT)
        )
        OR binding.order_line_id IS NULL
      )
  );

UPDATE outbox_messages
SET state = 'pending',
  lease_id = NULL,
  lease_expires_at_iso = NULL,
  last_error_code = NULL
WHERE kind = 'order-sync'
  AND state = 'rejected'
  AND last_error_code = 'ORDER_LINE_INVALID'
  AND EXISTS (
    SELECT 1
    FROM local_orders orders
    WHERE orders.order_guid = outbox_messages.aggregate_id
      AND orders.state = 'PendingSync'
      AND EXISTS (
        SELECT 1
        FROM local_order_lines line
        WHERE line.order_guid = orders.order_guid
      )
      AND NOT EXISTS (
        SELECT 1
        FROM local_order_lines line
        LEFT JOIN payment_order_draft_line_bindings binding
          ON binding.order_guid = line.order_guid
         AND binding.order_line_id = line.line_id
         AND binding.line_sequence = line.line_sequence
        WHERE line.order_guid = orders.order_guid
          AND (
            line.line_id <> (
              'payment:' || orders.order_guid || ':' ||
              CAST(line.line_sequence AS TEXT)
            )
            OR binding.order_line_id IS NULL
          )
      )
  );

-- 本地小票退货会把旧支付订单的本地行号冻结为 originalOrderDetailGuid。
-- 仅当本单每一行都能追溯到同一旧支付行及其不可变 binding 时恢复。
UPDATE local_orders
SET state = 'PendingSync',
  updated_at_iso = (
    SELECT ob.updated_at_iso
    FROM outbox_messages ob
    WHERE ob.aggregate_id = local_orders.order_guid
      AND ob.kind = 'order-sync'
    LIMIT 1
  )
WHERE state = 'Rejected'
  AND EXISTS (
    SELECT 1
    FROM outbox_messages ob
    WHERE ob.aggregate_id = local_orders.order_guid
      AND ob.kind = 'order-sync'
      AND ob.state = 'rejected'
      AND ob.last_error_code = 'ORDER_RETURN_REFERENCE_INVALID'
  )
  AND EXISTS (
    SELECT 1
    FROM local_order_lines line
    WHERE line.order_guid = local_orders.order_guid
  )
  AND NOT EXISTS (
    SELECT 1
    FROM local_order_lines line
    WHERE line.order_guid = local_orders.order_guid
      AND (
        line.original_order_guid IS NULL
        OR line.original_order_detail_guid IS NULL
        OR NOT EXISTS (
          SELECT 1
          FROM local_order_lines original
          INNER JOIN payment_order_draft_line_bindings binding
            ON binding.order_guid = original.order_guid
           AND binding.order_line_id = original.line_id
           AND binding.line_sequence = original.line_sequence
          WHERE original.order_guid = line.original_order_guid
            AND original.line_id = line.original_order_detail_guid
            AND original.line_id = (
              'payment:' || original.order_guid || ':' ||
              CAST(original.line_sequence AS TEXT)
            )
        )
      )
  );

UPDATE outbox_messages
SET state = 'pending',
  lease_id = NULL,
  lease_expires_at_iso = NULL,
  last_error_code = NULL
WHERE kind = 'order-sync'
  AND state = 'rejected'
  AND last_error_code = 'ORDER_RETURN_REFERENCE_INVALID'
  AND EXISTS (
    SELECT 1
    FROM local_orders orders
    WHERE orders.order_guid = outbox_messages.aggregate_id
      AND orders.state = 'PendingSync'
      AND EXISTS (
        SELECT 1
        FROM local_order_lines line
        WHERE line.order_guid = orders.order_guid
      )
      AND NOT EXISTS (
        SELECT 1
        FROM local_order_lines line
        WHERE line.order_guid = orders.order_guid
          AND (
            line.original_order_guid IS NULL
            OR line.original_order_detail_guid IS NULL
            OR NOT EXISTS (
              SELECT 1
              FROM local_order_lines original
              INNER JOIN payment_order_draft_line_bindings binding
                ON binding.order_guid = original.order_guid
               AND binding.order_line_id = original.line_id
               AND binding.line_sequence = original.line_sequence
              WHERE original.order_guid = line.original_order_guid
                AND original.line_id = line.original_order_detail_guid
                AND original.line_id = (
                  'payment:' || original.order_guid || ':' ||
                  CAST(original.line_sequence AS TEXT)
                )
            )
          )
      )
  );
`;

const M28 = `
-- 已批准支付也可能跨员工登录与进程重启后才完成；actor 与 action binding 同事务冻结。
-- NULL 仅兼容 M28 前已有绑定，运行时会整体回退订单员工且 userGuid 置空。
ALTER TABLE payment_action_bindings
  ADD COLUMN audit_actor_json TEXT NULL CHECK (
    audit_actor_json IS NULL
    OR (
      json_valid(audit_actor_json) = 1
      AND json_type(
        audit_actor_json,
        '$.requestingCashierId'
      ) IS NOT NULL
      AND json_type(audit_actor_json, '$.requestingCashierId') = 'text'
      AND LENGTH(TRIM(
        json_extract(audit_actor_json, '$.requestingCashierId')
      )) BETWEEN 1 AND 256
      AND json_type(
        audit_actor_json,
        '$.requestingCashierName'
      ) IS NOT NULL
      AND json_type(
        audit_actor_json,
        '$.requestingCashierName'
      ) IN ('text', 'null')
      AND (
        json_type(audit_actor_json, '$.requestingCashierName') = 'null'
        OR LENGTH(TRIM(
          json_extract(audit_actor_json, '$.requestingCashierName')
        )) BETWEEN 1 AND 256
      )
      AND json_type(
        audit_actor_json,
        '$.requestingUserGuid'
      ) IS NOT NULL
      AND json_type(
        audit_actor_json,
        '$.requestingUserGuid'
      ) IN ('text', 'null')
      AND (
        json_type(audit_actor_json, '$.requestingUserGuid') = 'null'
        OR LENGTH(TRIM(
          json_extract(audit_actor_json, '$.requestingUserGuid')
        )) BETWEEN 1 AND 256
      )
    )
  );

CREATE TRIGGER trg_payment_action_binding_actor_required
BEFORE INSERT
ON payment_action_bindings
FOR EACH ROW
WHEN NEW.audit_actor_json IS NULL
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ACTION_BINDING_ACTOR_REQUIRED');
END;

CREATE TRIGGER trg_payment_action_binding_actor_immutable
BEFORE UPDATE OF audit_actor_json
ON payment_action_bindings
FOR EACH ROW
WHEN NEW.audit_actor_json IS NOT OLD.audit_actor_json
BEGIN
  SELECT RAISE(ABORT, 'PAYMENT_ACTION_BINDING_IMMUTABLE');
END;

-- 撤券可能跨重启恢复；actor 必须与 action 一起冻结，不能在终态回读当前登录员工。
-- NULL 仅兼容 M28 前已经存在的未决 action，运行时会整体回退订单员工且 userGuid 置空。
ALTER TABLE voucher_tender_reversal_actions
  ADD COLUMN audit_actor_json TEXT NULL CHECK (
    audit_actor_json IS NULL
    OR (
      json_valid(audit_actor_json) = 1
      AND json_type(
        audit_actor_json,
        '$.requestingCashierId'
      ) IS NOT NULL
      AND json_type(audit_actor_json, '$.requestingCashierId') = 'text'
      AND LENGTH(TRIM(
        json_extract(audit_actor_json, '$.requestingCashierId')
      )) BETWEEN 1 AND 256
      AND json_type(
        audit_actor_json,
        '$.requestingCashierName'
      ) IS NOT NULL
      AND json_type(
        audit_actor_json,
        '$.requestingCashierName'
      ) IN ('text', 'null')
      AND (
        json_type(audit_actor_json, '$.requestingCashierName') = 'null'
        OR LENGTH(TRIM(
          json_extract(audit_actor_json, '$.requestingCashierName')
        )) BETWEEN 1 AND 256
      )
      AND json_type(
        audit_actor_json,
        '$.requestingUserGuid'
      ) IS NOT NULL
      AND json_type(
        audit_actor_json,
        '$.requestingUserGuid'
      ) IN ('text', 'null')
      AND (
        json_type(audit_actor_json, '$.requestingUserGuid') = 'null'
        OR LENGTH(TRIM(
          json_extract(audit_actor_json, '$.requestingUserGuid')
        )) BETWEEN 1 AND 256
      )
    )
  );

CREATE TRIGGER trg_voucher_tender_reversal_actor_immutable
BEFORE UPDATE OF audit_actor_json
ON voucher_tender_reversal_actions
FOR EACH ROW
WHEN NEW.audit_actor_json IS NOT OLD.audit_actor_json
BEGIN
  SELECT RAISE(
    ABORT,
    'VOUCHER_TENDER_REVERSAL_ACTOR_IMMUTABLE'
  );
END;
`;

const M29 = `
CREATE TRIGGER trg_voucher_tender_reversal_actor_required
BEFORE INSERT
ON voucher_tender_reversal_actions
FOR EACH ROW
WHEN NEW.audit_actor_json IS NULL
BEGIN
  SELECT RAISE(ABORT, 'VOUCHER_TENDER_REVERSAL_ACTOR_REQUIRED');
END;
`;

/**
 * 员工审计的门店/设备身份必须在本地事实入库时冻结。
 *
 * 已存在的 audit_events 无法从可靠来源证明范围，因此保持 NULL；投递查询会
 * fail-closed 排除它们，绝不在设备重新注册后用当前身份改写旧事实。
 */
const M30 = `
ALTER TABLE audit_events
  ADD COLUMN scope_store_code TEXT NULL;

ALTER TABLE audit_events
  ADD COLUMN scope_device_code TEXT NULL;

CREATE INDEX ix_audit_events_scope_delivery_ready
  ON audit_events (
    scope_store_code,
    scope_device_code,
    delivery_state,
    next_attempt_at_iso,
    occurred_at_iso,
    event_id
  );

-- 旧的订单内审计写入点没有显式 scope 参数，但订单账本本身是可信事实。
-- 仅在 INSERT 当下从同一库的订单复制一次；找不到订单或非订单记录仍保持 NULL。
CREATE TRIGGER trg_audit_events_freeze_scope_from_order
AFTER INSERT ON audit_events
FOR EACH ROW
WHEN NEW.scope_store_code IS NULL
  AND NEW.scope_device_code IS NULL
  AND NEW.order_guid IS NOT NULL
BEGIN
  UPDATE audit_events
  SET scope_store_code = (
        SELECT store_code FROM local_orders
        WHERE order_guid = NEW.order_guid
      ),
      scope_device_code = (
        SELECT device_code FROM local_orders
        WHERE order_guid = NEW.order_guid
      )
  WHERE event_id = NEW.event_id;
END;
`;

/**
 * M31 将 M30 的 scope 冻结规则下沉到 SQLite：
 * - 新事实只能是完整非空 scope，或无法证明身份的 legacy NULL,NULL；
 * - 已冻结 scope 永不允许改写；
 * - 唯一例外是 M30 对新插入订单审计的 NULL,NULL -> 订单账本精确回填。
 */
const M31 = `
CREATE TRIGGER trg_audit_events_scope_insert_valid
BEFORE INSERT ON audit_events
FOR EACH ROW
WHEN (
  (NEW.scope_store_code IS NULL AND NEW.scope_device_code IS NOT NULL)
  OR (NEW.scope_store_code IS NOT NULL AND NEW.scope_device_code IS NULL)
  OR (
    NEW.scope_store_code IS NOT NULL
    AND (
      LENGTH(TRIM(NEW.scope_store_code)) = 0
      OR LENGTH(TRIM(NEW.scope_device_code)) = 0
    )
  )
)
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_SCOPE_INVALID');
END;

CREATE TRIGGER trg_audit_events_scope_update_valid
BEFORE UPDATE OF scope_store_code, scope_device_code ON audit_events
FOR EACH ROW
WHEN (
  (NEW.scope_store_code IS NULL AND NEW.scope_device_code IS NOT NULL)
  OR (NEW.scope_store_code IS NOT NULL AND NEW.scope_device_code IS NULL)
  OR (
    NEW.scope_store_code IS NOT NULL
    AND (
      LENGTH(TRIM(NEW.scope_store_code)) = 0
      OR LENGTH(TRIM(NEW.scope_device_code)) = 0
    )
  )
)
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_SCOPE_INVALID');
END;

CREATE TRIGGER trg_audit_events_scope_immutable
BEFORE UPDATE OF scope_store_code, scope_device_code ON audit_events
FOR EACH ROW
WHEN (
  NEW.scope_store_code IS NOT OLD.scope_store_code
  OR NEW.scope_device_code IS NOT OLD.scope_device_code
)
AND NOT (
  -- 无效 scope 交给上方 trigger 返回明确错误，绝不能作为回填例外。
  (NEW.scope_store_code IS NULL AND NEW.scope_device_code IS NOT NULL)
  OR (NEW.scope_store_code IS NOT NULL AND NEW.scope_device_code IS NULL)
  OR (
    NEW.scope_store_code IS NOT NULL
    AND (
      LENGTH(TRIM(NEW.scope_store_code)) = 0
      OR LENGTH(TRIM(NEW.scope_device_code)) = 0
    )
  )
  OR (
    OLD.scope_store_code IS NULL
    AND OLD.scope_device_code IS NULL
    AND NEW.scope_store_code IS NOT NULL
    AND NEW.scope_device_code IS NOT NULL
    AND EXISTS (
      SELECT 1
      FROM local_orders
      WHERE order_guid = OLD.order_guid
        AND store_code = NEW.scope_store_code
        AND device_code = NEW.scope_device_code
    )
  )
)
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_SCOPE_IMMUTABLE');
END;
`;

/**
 * M32 收紧 M31 的订单 scope 回填例外：只有 audit_events INSERT 本身建立的
 * 一次性 guard 才能执行 NULL,NULL -> 订单账本精确 scope。guard 在同一条
 * INSERT 的 AFTER trigger 内立即删除；任一步失败都会随外层语句原子回滚。
 *
 * guard 外键与“父行已存在即拒绝”共同保证普通 SQL 不能为 legacy audit 行
 * 补造授权；无论 recursive_triggers 开关，scope UPDATE 都仍经过 immutable
 * trigger。已经升级到 M30/M31 的 NULL scope 事实继续 fail-closed。
 */
const M32 = `
-- SQLite 在 BEFORE INSERT 阶段会把“未指定 rowid”和显式 -1 都暴露为
-- NEW.rowid=-1，无法在该阶段可靠区分。先把历史保留值搬到确定未占用的
-- 正整数，再由 AFTER INSERT 在 SQLite 完成隐式分配后只拒绝仍为 -1 的行。
CREATE TRIGGER trg_audit_events_m32_rowid_rehome_overflow
BEFORE UPDATE ON audit_events
FOR EACH ROW
WHEN OLD.rowid = -1
  AND EXISTS (
    SELECT 1 FROM audit_events WHERE rowid = 9223372036854775807
  )
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_ROWID_REHOME_OVERFLOW');
END;

UPDATE audit_events
SET rowid = CASE
  -- 避免先计算 max+1 而退化为 REAL；上方 trigger 会用稳定错误原子中止。
  WHEN EXISTS (
    SELECT 1 FROM audit_events WHERE rowid = 9223372036854775807
  ) THEN -1
  ELSE MAX(
    0,
    COALESCE((
      SELECT MAX(existing.rowid)
      FROM audit_events existing
      WHERE existing.rowid <> -1
    ), 0)
  ) + 1
END
WHERE rowid = -1;

DROP TRIGGER trg_audit_events_m32_rowid_rehome_overflow;

CREATE TRIGGER trg_audit_events_reserved_rowid_insert_rejected
AFTER INSERT ON audit_events
FOR EACH ROW
WHEN NEW.rowid = -1
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_ROWID_RESERVED');
END;

-- SQLite rowid 表的 TEXT PRIMARY KEY 不隐含 NOT NULL，TEXT affinity 也仍可
-- 保存 BLOB。遗留非文本/空 ID 先退出投递队列但保留全部业务事实；后续写入
-- 由 trigger 失败关闭。
UPDATE audit_events
SET delivery_state = 'rejected',
    last_error_code = 'AUDIT_EVENT_ID_INVALID'
WHERE delivery_state = 'pending'
  AND (
    TYPEOF(event_id) <> 'text'
    OR LENGTH(TRIM(event_id)) = 0
  );

CREATE TRIGGER trg_audit_events_event_id_insert_valid
BEFORE INSERT ON audit_events
FOR EACH ROW
WHEN TYPEOF(NEW.event_id) <> 'text'
  OR LENGTH(TRIM(NEW.event_id)) = 0
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_EVENT_ID_INVALID');
END;

CREATE TRIGGER trg_audit_events_event_id_update_valid
BEFORE UPDATE OF event_id ON audit_events
FOR EACH ROW
WHEN TYPEOF(NEW.event_id) <> 'text'
  OR LENGTH(TRIM(NEW.event_id)) = 0
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_EVENT_ID_INVALID');
END;

CREATE TABLE audit_scope_insert_guard (
  event_id TEXT NOT NULL PRIMARY KEY
    REFERENCES audit_events(event_id) ON DELETE CASCADE,
  scope_store_code TEXT NOT NULL CHECK (
    LENGTH(TRIM(scope_store_code)) > 0
  ),
  scope_device_code TEXT NOT NULL CHECK (
    LENGTH(TRIM(scope_device_code)) > 0
  )
);

-- guard 只能在 audit_events 的 BEFORE INSERT（父行尚不存在）中创建。
-- 已存在的 legacy/new audit 行均不能由普通 SQL 借用 guard 解封。
CREATE TRIGGER trg_audit_scope_guard_reject_existing
BEFORE INSERT ON audit_scope_insert_guard
FOR EACH ROW
WHEN EXISTS (
  SELECT 1 FROM audit_events WHERE event_id = NEW.event_id
)
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_SCOPE_GUARD_FORBIDDEN');
END;

CREATE TRIGGER trg_audit_scope_guard_immutable
BEFORE UPDATE ON audit_scope_insert_guard
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_SCOPE_GUARD_IMMUTABLE');
END;

-- REPLACE/UPSERT 都会先执行 BEFORE INSERT；必须在冲突处理删除或改写
-- legacy 行之前主动阻断，不能依赖 recursive_triggers 控制的隐式 DELETE。
CREATE TRIGGER trg_audit_events_legacy_fact_insert_protected
BEFORE INSERT ON audit_events
FOR EACH ROW
WHEN TYPEOF(NEW.event_id) = 'text'
  AND LENGTH(TRIM(NEW.event_id)) > 0
  AND EXISTS (
  SELECT 1
  FROM audit_events existing
  WHERE existing.event_id IS NEW.event_id
    AND existing.scope_store_code IS NULL
    AND existing.scope_device_code IS NULL
)
BEGIN
  -- 保持普通重复 eventId 的既有主键错误语义；RAISE(ABORT) 不会被
  -- INSERT OR REPLACE / REPLACE / UPSERT 的冲突策略吞掉。
  SELECT RAISE(ABORT, 'UNIQUE constraint failed: audit_events.event_id');
END;

-- 未显式提供 rowid 时 SQLite 在 BEFORE INSERT 中暴露 NEW.rowid=-1；对
-- 其余显式正/负 rowid 的精确 legacy 冲突做保护，避免误杀普通 INSERT。
CREATE TRIGGER trg_audit_events_legacy_rowid_insert_protected
BEFORE INSERT ON audit_events
FOR EACH ROW
WHEN TYPEOF(NEW.event_id) = 'text'
  AND LENGTH(TRIM(NEW.event_id)) > 0
  AND NEW.rowid <> -1
  AND EXISTS (
    SELECT 1
    FROM audit_events existing
    WHERE existing.rowid = NEW.rowid
      AND existing.scope_store_code IS NULL
      AND existing.scope_device_code IS NULL
  )
BEGIN
  SELECT RAISE(ABORT, 'UNIQUE constraint failed: audit_events.rowid');
END;

CREATE TRIGGER trg_audit_events_legacy_fact_delete_protected
BEFORE DELETE ON audit_events
FOR EACH ROW
WHEN OLD.scope_store_code IS NULL
  AND OLD.scope_device_code IS NULL
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_LEGACY_FACT_IMMUTABLE');
END;

-- UPDATE OR REPLACE 在 recursive_triggers=OFF 时不会为被抢占行触发 DELETE。
-- 因此既冻结 legacy 自身 ID，也在 UPDATE 冲突处理前保护目标 legacy ID。
CREATE TRIGGER trg_audit_events_legacy_identity_update_protected
BEFORE UPDATE OF event_id ON audit_events
FOR EACH ROW
WHEN (
  OLD.scope_store_code IS NULL
  AND OLD.scope_device_code IS NULL
  AND NEW.event_id IS NOT OLD.event_id
)
OR EXISTS (
  SELECT 1
  FROM audit_events existing
  WHERE existing.event_id IS NEW.event_id
    AND existing.scope_store_code IS NULL
    AND existing.scope_device_code IS NULL
    AND existing.rowid <> OLD.rowid
)
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_LEGACY_FACT_IMMUTABLE');
END;

CREATE TRIGGER trg_audit_events_legacy_rowid_update_protected
BEFORE UPDATE ON audit_events
FOR EACH ROW
WHEN (
  OLD.scope_store_code IS NULL
  AND OLD.scope_device_code IS NULL
  AND NEW.rowid IS NOT OLD.rowid
)
OR EXISTS (
  SELECT 1
  FROM audit_events existing
  WHERE existing.rowid = NEW.rowid
    AND existing.scope_store_code IS NULL
    AND existing.scope_device_code IS NULL
    AND existing.rowid <> OLD.rowid
)
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_LEGACY_FACT_IMMUTABLE');
END;

DROP TRIGGER trg_audit_events_freeze_scope_from_order;
DROP TRIGGER trg_audit_events_scope_immutable;

-- BEFORE/AFTER 阶段是稳定边界，不依赖同阶段多个 trigger 的创建顺序。
CREATE TRIGGER trg_audit_events_prepare_scope_guard
BEFORE INSERT ON audit_events
FOR EACH ROW
WHEN NEW.scope_store_code IS NULL
  AND NEW.scope_device_code IS NULL
  AND TYPEOF(NEW.event_id) = 'text'
  AND LENGTH(TRIM(NEW.event_id)) > 0
  AND NEW.order_guid IS NOT NULL
  -- 重复 eventId 仍交给 audit_events 原主键返回 UNIQUE，保持既有回滚语义。
  AND NOT EXISTS (
    SELECT 1 FROM audit_events WHERE event_id IS NEW.event_id
  )
  AND EXISTS (
    SELECT 1 FROM local_orders WHERE order_guid = NEW.order_guid
  )
BEGIN
  INSERT INTO audit_scope_insert_guard (
    event_id, scope_store_code, scope_device_code
  )
  SELECT NEW.event_id, store_code, device_code
  FROM local_orders
  WHERE order_guid = NEW.order_guid;
END;

CREATE TRIGGER trg_audit_events_scope_immutable
BEFORE UPDATE OF scope_store_code, scope_device_code ON audit_events
FOR EACH ROW
WHEN (
  NEW.scope_store_code IS NOT OLD.scope_store_code
  OR NEW.scope_device_code IS NOT OLD.scope_device_code
)
AND NOT (
  -- 无效 scope 交给 validation trigger 返回明确错误。
  (NEW.scope_store_code IS NULL AND NEW.scope_device_code IS NOT NULL)
  OR (NEW.scope_store_code IS NOT NULL AND NEW.scope_device_code IS NULL)
  OR (
    NEW.scope_store_code IS NOT NULL
    AND (
      LENGTH(TRIM(NEW.scope_store_code)) = 0
      OR LENGTH(TRIM(NEW.scope_device_code)) = 0
    )
  )
  OR (
    OLD.scope_store_code IS NULL
    AND OLD.scope_device_code IS NULL
    AND NEW.scope_store_code IS NOT NULL
    AND NEW.scope_device_code IS NOT NULL
    AND EXISTS (
      SELECT 1
      FROM audit_scope_insert_guard guard
      JOIN local_orders orders
        ON orders.order_guid = OLD.order_guid
       AND orders.store_code = guard.scope_store_code
       AND orders.device_code = guard.scope_device_code
      WHERE guard.event_id = OLD.event_id
        AND guard.scope_store_code = NEW.scope_store_code
        AND guard.scope_device_code = NEW.scope_device_code
    )
  )
)
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_SCOPE_IMMUTABLE');
END;

CREATE TRIGGER trg_audit_events_freeze_scope_from_order
AFTER INSERT ON audit_events
FOR EACH ROW
WHEN EXISTS (
  SELECT 1 FROM audit_scope_insert_guard WHERE event_id = NEW.event_id
)
BEGIN
  UPDATE audit_events
  SET scope_store_code = (
        SELECT scope_store_code
        FROM audit_scope_insert_guard
        WHERE event_id = NEW.event_id
      ),
      scope_device_code = (
        SELECT scope_device_code
        FROM audit_scope_insert_guard
        WHERE event_id = NEW.event_id
      )
  WHERE event_id = NEW.event_id;

  DELETE FROM audit_scope_insert_guard WHERE event_id = NEW.event_id;
END;
`;

/**
 * 跨终端远程历史订单不属于本机订单账本。重打任务只保存独立外部订单身份，
 * 绝不向 local_orders 写入伪造订单，也不放宽原有本机订单外键。
 */
const M33 = `
ALTER TABLE print_jobs
  ADD COLUMN external_order_guid TEXT NULL;

ALTER TABLE audit_events
  ADD COLUMN external_order_guid TEXT NULL;

CREATE TRIGGER trg_print_jobs_external_order_insert_valid
BEFORE INSERT ON print_jobs
FOR EACH ROW
WHEN NEW.external_order_guid IS NOT NULL
  AND (
    NEW.order_guid IS NOT NULL
    OR NEW.is_reprint <> 1
    OR TYPEOF(NEW.external_order_guid) <> 'text'
    OR LENGTH(TRIM(NEW.external_order_guid)) = 0
    OR LENGTH(NEW.external_order_guid) > 128
  )
BEGIN
  SELECT RAISE(ABORT, 'PRINT_JOB_EXTERNAL_ORDER_INVALID');
END;

CREATE TRIGGER trg_print_jobs_order_identity_immutable
BEFORE UPDATE OF order_guid, external_order_guid, is_reprint ON print_jobs
FOR EACH ROW
WHEN NEW.order_guid IS NOT OLD.order_guid
  OR NEW.external_order_guid IS NOT OLD.external_order_guid
  OR NEW.is_reprint IS NOT OLD.is_reprint
BEGIN
  SELECT RAISE(ABORT, 'PRINT_JOB_ORDER_IDENTITY_IMMUTABLE');
END;

CREATE TRIGGER trg_audit_events_external_order_insert_valid
BEFORE INSERT ON audit_events
FOR EACH ROW
WHEN NEW.external_order_guid IS NOT NULL
  AND (
    NEW.order_guid IS NOT NULL
    OR TYPEOF(NEW.external_order_guid) <> 'text'
    OR LENGTH(TRIM(NEW.external_order_guid)) = 0
    OR LENGTH(NEW.external_order_guid) > 128
    OR NEW.scope_store_code IS NULL
    OR NEW.scope_device_code IS NULL
    OR NEW.event_type <> 'RECEIPT_REPRINT'
    OR json_extract(NEW.payload_json, '$.source') <> 'remote-history'
    OR json_extract(NEW.payload_json, '$.action') <> 'reprint-history-receipt'
  )
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_EXTERNAL_ORDER_INVALID');
END;

CREATE TRIGGER trg_audit_events_order_identity_immutable
BEFORE UPDATE OF order_guid, external_order_guid ON audit_events
FOR EACH ROW
WHEN (
  OLD.external_order_guid IS NOT NULL
  AND (
    NEW.order_guid IS NOT OLD.order_guid
    OR NEW.external_order_guid IS NOT OLD.external_order_guid
  )
)
OR (
  OLD.external_order_guid IS NULL
  AND NEW.external_order_guid IS NOT NULL
)
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_ORDER_IDENTITY_IMMUTABLE');
END;
`;

/**
 * M33 已在生产库记录，不能原地改写。分期历史同样使用独立外部订单身份，
 * 因此以后续迁移重建审计触发器，并继续严格拒绝其他来源或动作。
 */
const M34 = `
DROP TRIGGER IF EXISTS trg_audit_events_external_order_insert_valid;

CREATE TRIGGER trg_audit_events_external_order_insert_valid
BEFORE INSERT ON audit_events
FOR EACH ROW
WHEN NEW.external_order_guid IS NOT NULL
  AND (
    NEW.order_guid IS NOT NULL
    OR TYPEOF(NEW.external_order_guid) <> 'text'
    OR LENGTH(TRIM(NEW.external_order_guid)) = 0
    OR LENGTH(NEW.external_order_guid) > 128
    OR NEW.scope_store_code IS NULL
    OR NEW.scope_device_code IS NULL
    OR NEW.event_type <> 'RECEIPT_REPRINT'
    OR COALESCE(json_extract(NEW.payload_json, '$.source'), '') NOT IN (
      'remote-history',
      'installment-history'
    )
    OR COALESCE(json_extract(NEW.payload_json, '$.action'), '') <> 'reprint-history-receipt'
  )
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_EXTERNAL_ORDER_INVALID');
END;
`;

/**
 * 分期继续付款完成页仍使用 PrintLast 授权，但订单身份来自已严格核验的服务端分期详情。
 * 仅允许该精确语义作为外部订单；普通付款成功和其他授权组合仍保持本机订单外键。
 */
const M35 = `
DROP TRIGGER IF EXISTS trg_audit_events_external_order_insert_valid;

CREATE TRIGGER trg_audit_events_external_order_insert_valid
BEFORE INSERT ON audit_events
FOR EACH ROW
WHEN NEW.external_order_guid IS NOT NULL
  AND (
    NEW.order_guid IS NOT NULL
    OR TYPEOF(NEW.external_order_guid) <> 'text'
    OR LENGTH(TRIM(NEW.external_order_guid)) = 0
    OR LENGTH(NEW.external_order_guid) > 128
    OR NEW.scope_store_code IS NULL
    OR NEW.scope_device_code IS NULL
    OR NEW.event_type <> 'RECEIPT_REPRINT'
    OR NOT (
      (
        COALESCE(json_extract(NEW.payload_json, '$.source'), '') IN (
          'remote-history',
          'installment-history'
        )
        AND COALESCE(json_extract(NEW.payload_json, '$.action'), '') =
          'reprint-history-receipt'
      )
      OR (
        COALESCE(json_extract(NEW.payload_json, '$.source'), '') =
          'payment-success'
        AND COALESCE(json_extract(NEW.payload_json, '$.action'), '') =
          'reprint-current-receipt'
      )
    )
  )
BEGIN
  SELECT RAISE(ABORT, 'AUDIT_EXTERNAL_ORDER_INVALID');
END;
`;

/**
 * 作废/提货在远端提交前冻结完整命令；回包丢失或 App 重启后只允许回放同一身份。
 * 明文列仅保留索引与 scope，收银员、备注、原因、原始设备等完整指纹均在认证密文内。
 */
const M36 = `
CREATE TABLE installment_lifecycle_actions (
  operation_guid TEXT PRIMARY KEY,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  installment_guid TEXT NOT NULL,
  action_kind TEXT NOT NULL CHECK (action_kind IN ('void', 'pickup')),
  idempotency_key TEXT NOT NULL UNIQUE,
  resolution TEXT NULL CHECK (resolution IS NULL OR resolution = 'Completed'),
  payload_revision INTEGER NOT NULL CHECK (payload_revision = 1),
  command_ciphertext BLOB NOT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  resolved_at_iso TEXT NULL,
  CHECK (idempotency_key = operation_guid),
  CHECK (
    (resolution IS NULL AND resolved_at_iso IS NULL)
    OR (resolution = 'Completed' AND resolved_at_iso IS NOT NULL)
  )
);

CREATE UNIQUE INDEX ux_installment_lifecycle_terminal_blocking
  ON installment_lifecycle_actions (store_code, device_code)
  WHERE resolution IS NULL;

CREATE INDEX ix_installment_lifecycle_terminal_history
  ON installment_lifecycle_actions (
    store_code, device_code, created_at_iso, operation_guid
  );

CREATE TRIGGER trg_installment_lifecycle_immutable
BEFORE UPDATE ON installment_lifecycle_actions
FOR EACH ROW
WHEN
  NEW.operation_guid <> OLD.operation_guid
  OR NEW.store_code <> OLD.store_code
  OR NEW.device_code <> OLD.device_code
  OR NEW.installment_guid <> OLD.installment_guid
  OR NEW.action_kind <> OLD.action_kind
  OR NEW.idempotency_key <> OLD.idempotency_key
  OR NEW.payload_revision <> OLD.payload_revision
  OR NEW.command_ciphertext <> OLD.command_ciphertext
  OR NEW.created_at_iso <> OLD.created_at_iso
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_LIFECYCLE_IMMUTABLE');
END;

CREATE TRIGGER trg_installment_lifecycle_resolution_transition
BEFORE UPDATE OF resolution, resolved_at_iso ON installment_lifecycle_actions
FOR EACH ROW
WHEN NOT (
  OLD.resolution IS NULL
  AND OLD.resolved_at_iso IS NULL
  AND NEW.resolution = 'Completed'
  AND NEW.resolved_at_iso IS NOT NULL
)
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_LIFECYCLE_RESOLUTION_INVALID');
END;

CREATE TRIGGER trg_installment_lifecycle_no_delete
BEFORE DELETE ON installment_lifecycle_actions
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_LIFECYCLE_DELETE_FORBIDDEN');
END;
`;

/**
 * Created 阶段收到服务端确定性 Card 不支持时，保留专用终结原因与审计事实。
 * 旧 resolution 继续使用 Declined，以避免重建已被外键引用的 action 表。
 */
const M37 = `
ALTER TABLE installment_actions
ADD COLUMN resolution_code TEXT NULL CHECK (
  resolution_code IS NULL OR resolution_code = 'PaymentMethodUnsupported'
);

CREATE TRIGGER trg_installment_actions_resolution_code_transition
BEFORE UPDATE OF resolution_code ON installment_actions
FOR EACH ROW
WHEN NOT (
  OLD.resolution_code IS NULL
  AND NEW.resolution_code = 'PaymentMethodUnsupported'
  AND OLD.resolution IS NULL
  AND NEW.resolution = 'Declined'
  AND OLD.state = 'ProviderPending'
  AND NEW.state = 'ProviderPending'
  AND OLD.resolved_at_iso IS NULL
  AND NEW.resolved_at_iso IS NOT NULL
)
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_ACTION_RESOLUTION_CODE_INVALID');
END;
`;

/**
 * 部分开发设备曾把版本 36 记录为 M36_shared_held_order_claims，导致当前 M36
 * 被按版本号跳过。M38 以幂等 DDL 补齐分期生命周期账本，同时兼容已正确执行
 * 当前 M36 的数据库，禁止通过清库掩盖迁移冲突。
 */
const M38 = `
CREATE TABLE IF NOT EXISTS installment_lifecycle_actions (
  operation_guid TEXT PRIMARY KEY,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  installment_guid TEXT NOT NULL,
  action_kind TEXT NOT NULL CHECK (action_kind IN ('void', 'pickup')),
  idempotency_key TEXT NOT NULL UNIQUE,
  resolution TEXT NULL CHECK (resolution IS NULL OR resolution = 'Completed'),
  payload_revision INTEGER NOT NULL CHECK (payload_revision = 1),
  command_ciphertext BLOB NOT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  resolved_at_iso TEXT NULL,
  CHECK (idempotency_key = operation_guid),
  CHECK (
    (resolution IS NULL AND resolved_at_iso IS NULL)
    OR (resolution = 'Completed' AND resolved_at_iso IS NOT NULL)
  )
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_installment_lifecycle_terminal_blocking
  ON installment_lifecycle_actions (store_code, device_code)
  WHERE resolution IS NULL;

CREATE INDEX IF NOT EXISTS ix_installment_lifecycle_terminal_history
  ON installment_lifecycle_actions (
    store_code, device_code, created_at_iso, operation_guid
  );

CREATE TRIGGER IF NOT EXISTS trg_installment_lifecycle_immutable
BEFORE UPDATE ON installment_lifecycle_actions
FOR EACH ROW
WHEN
  NEW.operation_guid <> OLD.operation_guid
  OR NEW.store_code <> OLD.store_code
  OR NEW.device_code <> OLD.device_code
  OR NEW.installment_guid <> OLD.installment_guid
  OR NEW.action_kind <> OLD.action_kind
  OR NEW.idempotency_key <> OLD.idempotency_key
  OR NEW.payload_revision <> OLD.payload_revision
  OR NEW.command_ciphertext <> OLD.command_ciphertext
  OR NEW.created_at_iso <> OLD.created_at_iso
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_LIFECYCLE_IMMUTABLE');
END;

CREATE TRIGGER IF NOT EXISTS trg_installment_lifecycle_resolution_transition
BEFORE UPDATE OF resolution, resolved_at_iso ON installment_lifecycle_actions
FOR EACH ROW
WHEN NOT (
  OLD.resolution IS NULL
  AND OLD.resolved_at_iso IS NULL
  AND NEW.resolution = 'Completed'
  AND NEW.resolved_at_iso IS NOT NULL
)
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_LIFECYCLE_RESOLUTION_INVALID');
END;

CREATE TRIGGER IF NOT EXISTS trg_installment_lifecycle_no_delete
BEFORE DELETE ON installment_lifecycle_actions
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'INSTALLMENT_LIFECYCLE_DELETE_FORBIDDEN');
END;
`;

/**
 * M24 的 CHECK/trigger 只能表达「实收不少于入账」，无法保存澳洲最终现金
 * 舍入的不可变事实（例如入账 1002、实收 1000）。M39 重建 action 表而不改写
 * 旧 M24：历史行原样复制，原有外键、唯一索引和不可变语义全部保留。
 */
const M39 = `
DROP TRIGGER IF EXISTS trg_mixed_cash_tender_action_amounts_insert;
DROP TRIGGER IF EXISTS trg_mixed_cash_tender_actions_immutable_update;
DROP TRIGGER IF EXISTS trg_mixed_cash_tender_actions_immutable_delete;

CREATE TABLE mixed_cash_tender_actions_m39 (
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  action_id TEXT NOT NULL,
  amount_cents INTEGER NOT NULL CHECK (amount_cents > 0),
  tendered_cents INTEGER NULL CHECK (
    tendered_cents IS NULL
    OR (
      typeof(tendered_cents) = 'integer'
      AND tendered_cents BETWEEN 0 AND 9007199254740991
    )
  ),
  change_cents INTEGER NULL CHECK (
    change_cents IS NULL
    OR (
      typeof(change_cents) = 'integer'
      AND change_cents BETWEEN 0 AND 9007199254740991
    )
  ),
  tender_guid TEXT NOT NULL UNIQUE REFERENCES order_tenders(tender_guid) ON DELETE RESTRICT,
  audit_event_id TEXT NOT NULL UNIQUE REFERENCES audit_events(event_id) ON DELETE RESTRICT,
  created_at_iso TEXT NOT NULL,
  PRIMARY KEY (order_guid, action_id),
  CHECK (TRIM(action_id) <> '' AND LENGTH(action_id) <= 128),
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (TRIM(tender_guid) <> '' AND LENGTH(tender_guid) <= 128),
  CHECK (TRIM(audit_event_id) <> '' AND LENGTH(audit_event_id) <= 128),
  CHECK (TRIM(created_at_iso) <> '' AND LENGTH(created_at_iso) <= 64)
);

INSERT INTO mixed_cash_tender_actions_m39 (
  order_guid, action_id, amount_cents, tendered_cents, change_cents,
  tender_guid, audit_event_id, created_at_iso
)
SELECT
  order_guid, action_id, amount_cents, tendered_cents, change_cents,
  tender_guid, audit_event_id, created_at_iso
FROM mixed_cash_tender_actions;

DROP TABLE mixed_cash_tender_actions;
ALTER TABLE mixed_cash_tender_actions_m39 RENAME TO mixed_cash_tender_actions;

CREATE TRIGGER trg_mixed_cash_tender_actions_immutable_update
BEFORE UPDATE ON mixed_cash_tender_actions
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'MIXED_CASH_ACTION_IMMUTABLE');
END;

CREATE TRIGGER trg_mixed_cash_tender_actions_immutable_delete
BEFORE DELETE ON mixed_cash_tender_actions
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'MIXED_CASH_ACTION_IMMUTABLE');
END;

-- 新 action 必须持久化完整结算。直接/部分现金采用入账差额；最终现金才可
-- 按 5 分规则采用 cashDue。通过排除本 action tender 反推插入前 remaining，
-- 使部分现金无法伪装成舍入形态。
CREATE TRIGGER trg_mixed_cash_tender_action_amounts_insert
BEFORE INSERT ON mixed_cash_tender_actions
FOR EACH ROW
WHEN
  NEW.tendered_cents IS NULL
  OR NEW.change_cents IS NULL
  OR NOT EXISTS (
    SELECT 1
    FROM order_tenders AS tender
    WHERE tender.tender_guid = NEW.tender_guid
      AND tender.order_guid = NEW.order_guid
      AND tender.method = 'cash'
      AND tender.amount_cents = NEW.amount_cents
  )
  OR NOT (
    (
      -- 入账差额只能用于严格未结清的部分现金；amount 等于 remaining 时
      -- 必须走下方 cashDue 分支，避免绕过最终现金的 5 分舍入事实。
      NEW.amount_cents < COALESCE((
        SELECT order_row.actual_amount_cents - COALESCE(SUM(
          CASE
            WHEN tender.tender_guid <> NEW.tender_guid THEN tender.amount_cents
            ELSE 0
          END
        ), 0)
        FROM local_orders AS order_row
        LEFT JOIN order_tenders AS tender
          ON tender.order_guid = order_row.order_guid
        WHERE order_row.order_guid = NEW.order_guid
      ), -1)
      AND NEW.tendered_cents >= NEW.amount_cents
      AND NEW.change_cents = NEW.tendered_cents - NEW.amount_cents
    )
    OR (
      NEW.amount_cents = COALESCE((
        SELECT order_row.actual_amount_cents - COALESCE(SUM(
          CASE
            WHEN tender.tender_guid <> NEW.tender_guid THEN tender.amount_cents
            ELSE 0
          END
        ), 0)
        FROM local_orders AS order_row
        LEFT JOIN order_tenders AS tender
          ON tender.order_guid = order_row.order_guid
        WHERE order_row.order_guid = NEW.order_guid
      ), -1)
      AND NEW.tendered_cents >= (
        NEW.amount_cents - (NEW.amount_cents % 5)
        + CASE WHEN NEW.amount_cents % 5 >= 3 THEN 5 ELSE 0 END
      )
      -- 最终现金持久化的是运行时已规范化的实收，零实收同样合法。
      AND NEW.tendered_cents % 5 = 0
      AND NEW.change_cents = NEW.tendered_cents - (
        NEW.amount_cents - (NEW.amount_cents % 5)
        + CASE WHEN NEW.amount_cents % 5 >= 3 THEN 5 ELSE 0 END
      )
    )
  )
BEGIN
  SELECT RAISE(ABORT, 'MIXED_CASH_ACTION_AMOUNTS_INVALID');
END;
`;

/**
 * M40 挂单共享发布状态 + 跨终端取单声明：
 * - held_order_records 旧行默认 NeedsEvaluation，未来新挂单由整合层同事务写
 *   PendingPublish。迁移不解析、不解密、不猜测旧 payload_ciphertext。
 * - shared_held_order_claim_records 保存终端取单声明，payload 一律只留密文；
 *   每 store+device 至多一个 Prepared/Active（partial unique fence）。
 * - order_held_order_sources 是不可变订单来源：与本地订单/outbox 同一事务写入，
 *   RemoteClaim(1) 必须带 claim_guid，OfflineOrigin(2) 必须为空；普通订单不写入。
 */
const M40 = `
ALTER TABLE held_order_records
ADD COLUMN share_state TEXT NOT NULL DEFAULT 'NeedsEvaluation'
  CHECK (share_state IN ('NeedsEvaluation','PendingPublish','Published','Blocked'));

ALTER TABLE held_order_records
ADD COLUMN remote_revision INTEGER NULL
  CHECK (remote_revision IS NULL OR (typeof(remote_revision) = 'integer' AND remote_revision >= 0));

ALTER TABLE held_order_records
ADD COLUMN publish_attempt_count INTEGER NOT NULL DEFAULT 0
  CHECK (typeof(publish_attempt_count) = 'integer' AND publish_attempt_count >= 0);

ALTER TABLE held_order_records
ADD COLUMN next_publish_at_iso TEXT NULL;

ALTER TABLE held_order_records
ADD COLUMN publish_error_code TEXT NULL;

ALTER TABLE held_order_records
ADD COLUMN publish_block_reason TEXT NULL;

ALTER TABLE held_order_records
ADD COLUMN remote_updated_at_iso TEXT NULL;

-- 远端 prepare 创建的 synthetic 行标记；release/supersede 只清理此类行，
-- 真实本地挂单副本恢复为 Pending，绝不被删除。
ALTER TABLE held_order_records
ADD COLUMN is_synthetic_shared_claim INTEGER NOT NULL DEFAULT 0
  CHECK (is_synthetic_shared_claim IN (0, 1));

-- 本地订单是否来自共享挂单取单完成；同步材料解析器据此决定是否查询来源表，
-- 普通订单保持默认 0，绝不查询或写入来源表。
ALTER TABLE local_orders
ADD COLUMN is_shared_held_origin INTEGER NOT NULL DEFAULT 0
  CHECK (is_shared_held_origin IN (0, 1));

CREATE TABLE IF NOT EXISTS shared_held_order_claim_records (
  claim_guid TEXT PRIMARY KEY,
  -- hold_guid 语义上对应 held_order_records.hold_id；暂不设外键，允许远程声明先于本地挂单到达。
  hold_guid TEXT NOT NULL,
  -- 本机取单 attempt：与 terminal_cart_fences.recall_attempt_id 一致，声明身份不可变。
  recall_attempt_id TEXT NOT NULL UNIQUE,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  source TEXT NOT NULL CHECK (source IN ('RemoteClaim','OfflineOrigin')),
  state TEXT NOT NULL CHECK (state IN ('Prepared','Active','Completed','Released','Superseded')),
  prepare_idempotency_key TEXT NOT NULL,
  activate_idempotency_key TEXT NULL,
  release_idempotency_key TEXT NULL,
  supersede_idempotency_key TEXT NULL,
  payload_version INTEGER NOT NULL CHECK (payload_version = 1),
  payload_ciphertext BLOB NOT NULL,
  server_revision INTEGER NULL
    CHECK (server_revision IS NULL OR (typeof(server_revision) = 'integer' AND server_revision >= 0)),
  prepared_expires_at_iso TEXT NOT NULL,
  -- 本机 held 汇总事实：与 prepare 输入严格一致，用于幂等比对和 synthetic 行重建。
  held_at_iso TEXT NOT NULL,
  held_by_cashier_id TEXT NOT NULL,
  held_by_cashier_name TEXT NOT NULL,
  bound_order_guid TEXT NULL,
  created_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  CHECK (TRIM(claim_guid) <> '' AND LENGTH(claim_guid) <= 128),
  CHECK (TRIM(hold_guid) <> '' AND LENGTH(hold_guid) <= 128),
  CHECK (TRIM(recall_attempt_id) <> '' AND LENGTH(recall_attempt_id) <= 128),
  CHECK (TRIM(store_code) <> '' AND LENGTH(store_code) <= 64),
  CHECK (TRIM(device_code) <> '' AND LENGTH(device_code) <= 128),
  CHECK (TRIM(prepare_idempotency_key) <> '' AND LENGTH(prepare_idempotency_key) <= 256),
  CHECK (activate_idempotency_key IS NULL OR (TRIM(activate_idempotency_key) <> '' AND LENGTH(activate_idempotency_key) <= 256)),
  CHECK (release_idempotency_key IS NULL OR (TRIM(release_idempotency_key) <> '' AND LENGTH(release_idempotency_key) <= 256)),
  CHECK (supersede_idempotency_key IS NULL OR (TRIM(supersede_idempotency_key) <> '' AND LENGTH(supersede_idempotency_key) <= 256)),
  CHECK (TRIM(prepared_expires_at_iso) <> '' AND LENGTH(prepared_expires_at_iso) <= 64),
  CHECK (TRIM(held_at_iso) <> '' AND LENGTH(held_at_iso) <= 64),
  CHECK (TRIM(held_by_cashier_id) <> '' AND LENGTH(held_by_cashier_id) <= 128),
  CHECK (TRIM(held_by_cashier_name) <> '' AND LENGTH(held_by_cashier_name) <= 256),
  CHECK (bound_order_guid IS NULL OR (TRIM(bound_order_guid) <> '' AND LENGTH(bound_order_guid) <= 128)),
  CHECK (TRIM(created_at_iso) <> '' AND LENGTH(created_at_iso) <= 64),
  CHECK (TRIM(updated_at_iso) <> '' AND LENGTH(updated_at_iso) <= 64),
  CHECK (
    (state = 'Prepared'
      AND activate_idempotency_key IS NULL
      AND release_idempotency_key IS NULL
      AND supersede_idempotency_key IS NULL
      AND bound_order_guid IS NULL)
    OR (state = 'Active'
      AND activate_idempotency_key IS NOT NULL
      AND release_idempotency_key IS NULL)
    OR (state = 'Completed'
      AND activate_idempotency_key IS NOT NULL
      AND release_idempotency_key IS NOT NULL
      AND supersede_idempotency_key IS NULL
      AND bound_order_guid IS NOT NULL)
    OR (state = 'Released'
      AND release_idempotency_key IS NOT NULL
      AND supersede_idempotency_key IS NULL
      AND bound_order_guid IS NULL)
    OR (state = 'Superseded'
      AND release_idempotency_key IS NULL
      AND supersede_idempotency_key IS NOT NULL
      AND bound_order_guid IS NULL)
  )
);

-- 每 store+device 至多一个 Prepared/Active 的本地 fence（唯一赢家）。
CREATE UNIQUE INDEX IF NOT EXISTS ux_shared_held_order_claim_terminal_fence
  ON shared_held_order_claim_records (store_code, device_code)
  WHERE state IN ('Prepared','Active');

CREATE UNIQUE INDEX IF NOT EXISTS ux_shared_held_order_claim_prepare_key
  ON shared_held_order_claim_records (prepare_idempotency_key);

CREATE UNIQUE INDEX IF NOT EXISTS ux_shared_held_order_claim_activate_key
  ON shared_held_order_claim_records (activate_idempotency_key)
  WHERE activate_idempotency_key IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_shared_held_order_claim_release_key
  ON shared_held_order_claim_records (release_idempotency_key)
  WHERE release_idempotency_key IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_shared_held_order_claim_supersede_key
  ON shared_held_order_claim_records (supersede_idempotency_key)
  WHERE supersede_idempotency_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_shared_held_order_claim_scope
  ON shared_held_order_claim_records (store_code, device_code, state, created_at_iso, claim_guid);

CREATE INDEX IF NOT EXISTS ix_shared_held_order_claim_hold
  ON shared_held_order_claim_records (hold_guid, recall_attempt_id, claim_guid);

CREATE INDEX IF NOT EXISTS ix_shared_held_order_claim_recall
  ON shared_held_order_claim_records (recall_attempt_id);

-- 不可变列保护：声明身份、scope、源、recall attempt、prepare key、payload 与时间戳不可改写。
CREATE TRIGGER IF NOT EXISTS trg_shared_held_order_claim_immutable
BEFORE UPDATE ON shared_held_order_claim_records
FOR EACH ROW
WHEN
  NEW.claim_guid <> OLD.claim_guid
  OR NEW.hold_guid <> OLD.hold_guid
  OR NEW.recall_attempt_id <> OLD.recall_attempt_id
  OR NEW.store_code <> OLD.store_code
  OR NEW.device_code <> OLD.device_code
  OR NEW.source <> OLD.source
  OR NEW.prepare_idempotency_key <> OLD.prepare_idempotency_key
  OR NEW.payload_version <> OLD.payload_version
  OR NEW.payload_ciphertext <> OLD.payload_ciphertext
  OR NEW.held_at_iso <> OLD.held_at_iso
  OR NEW.held_by_cashier_id <> OLD.held_by_cashier_id
  OR NEW.held_by_cashier_name <> OLD.held_by_cashier_name
  OR NEW.created_at_iso <> OLD.created_at_iso
  OR NEW.prepared_expires_at_iso <> OLD.prepared_expires_at_iso
BEGIN
  SELECT RAISE(ABORT, 'SHARED_HELD_ORDER_CLAIM_IMMUTABLE');
END;

-- 状态机：Prepared->Active/Released/Superseded；Active->Active(绑定)/Completed/Released；
-- 终态不可再转移；绑定订单后只能 complete，不能 release。
CREATE TRIGGER IF NOT EXISTS trg_shared_held_order_claim_transition
BEFORE UPDATE OF state, activate_idempotency_key, release_idempotency_key, supersede_idempotency_key, bound_order_guid, server_revision
ON shared_held_order_claim_records
FOR EACH ROW
WHEN NOT (
  (OLD.state = 'Prepared' AND NEW.state = 'Active'
    AND OLD.activate_idempotency_key IS NULL AND NEW.activate_idempotency_key IS NOT NULL
    AND NEW.release_idempotency_key IS NULL
    AND NEW.supersede_idempotency_key IS NULL
    AND NEW.bound_order_guid IS NULL)
  OR (OLD.state = 'Prepared' AND NEW.state = 'Released'
    AND NEW.activate_idempotency_key IS NULL
    AND OLD.release_idempotency_key IS NULL AND NEW.release_idempotency_key IS NOT NULL
    AND NEW.supersede_idempotency_key IS NULL
    AND NEW.bound_order_guid IS NULL)
  OR (OLD.state = 'Prepared' AND NEW.state = 'Superseded'
    AND NEW.activate_idempotency_key IS NULL
    AND NEW.release_idempotency_key IS NULL
    AND OLD.supersede_idempotency_key IS NULL AND NEW.supersede_idempotency_key IS NOT NULL
    AND NEW.bound_order_guid IS NULL)
  OR (OLD.state = 'Active' AND NEW.state = 'Superseded'
    AND NEW.activate_idempotency_key = OLD.activate_idempotency_key
    AND NEW.activate_idempotency_key IS NOT NULL
    AND NEW.release_idempotency_key IS NULL
    AND OLD.supersede_idempotency_key IS NULL AND NEW.supersede_idempotency_key IS NOT NULL
    AND OLD.bound_order_guid IS NULL AND NEW.bound_order_guid IS NULL)
  OR (OLD.state = 'Active' AND NEW.state = 'Active'
    AND NEW.activate_idempotency_key = OLD.activate_idempotency_key
    AND OLD.release_idempotency_key IS NULL AND NEW.release_idempotency_key IS NULL
    AND NEW.supersede_idempotency_key IS NULL
    AND OLD.bound_order_guid IS NULL AND NEW.bound_order_guid IS NOT NULL
    AND NEW.server_revision = OLD.server_revision)
  OR (OLD.state = 'Active' AND NEW.state = 'Completed'
    AND NEW.activate_idempotency_key = OLD.activate_idempotency_key
    AND OLD.release_idempotency_key IS NULL AND NEW.release_idempotency_key IS NOT NULL
    AND NEW.supersede_idempotency_key IS NULL
    AND OLD.bound_order_guid IS NOT NULL AND NEW.bound_order_guid = OLD.bound_order_guid)
  OR (OLD.state = 'Active' AND NEW.state = 'Released'
    AND NEW.activate_idempotency_key = OLD.activate_idempotency_key
    AND OLD.release_idempotency_key IS NULL AND NEW.release_idempotency_key IS NOT NULL
    AND NEW.supersede_idempotency_key IS NULL
    AND NEW.bound_order_guid IS NULL AND OLD.bound_order_guid IS NULL)
)
BEGIN
  SELECT RAISE(ABORT, 'SHARED_HELD_ORDER_CLAIM_TRANSITION_INVALID');
END;

-- 不可变订单来源表：一笔订单至多一个共享挂单来源，与本地订单/outbox 同事务写入，
-- 之后任何列都不可改写（付款/恢复竞态只能整体回滚，不能事后修补来源）。
CREATE TABLE IF NOT EXISTS order_held_order_sources (
  order_guid TEXT PRIMARY KEY
    REFERENCES local_orders(order_guid) ON DELETE CASCADE,
  hold_guid TEXT NOT NULL,
  claim_guid TEXT NULL
    REFERENCES shared_held_order_claim_records(claim_guid) ON DELETE RESTRICT,
  source_kind INTEGER NOT NULL CHECK (source_kind IN (1, 2)),
  created_at_iso TEXT NOT NULL,
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (TRIM(hold_guid) <> '' AND LENGTH(hold_guid) <= 128),
  CHECK (claim_guid IS NULL OR (TRIM(claim_guid) <> '' AND LENGTH(claim_guid) <= 128)),
  CHECK (TRIM(created_at_iso) <> '' AND LENGTH(created_at_iso) <= 64),
  CHECK (
    (source_kind = 1 AND claim_guid IS NOT NULL)
    OR (source_kind = 2 AND claim_guid IS NULL)
  )
);

CREATE INDEX IF NOT EXISTS ix_order_held_order_sources_hold
  ON order_held_order_sources (hold_guid);

CREATE TRIGGER IF NOT EXISTS trg_order_held_order_sources_immutable
BEFORE UPDATE ON order_held_order_sources
FOR EACH ROW
WHEN
  NEW.order_guid <> OLD.order_guid
  OR NEW.hold_guid <> OLD.hold_guid
  OR NEW.claim_guid <> OLD.claim_guid
  OR NEW.source_kind <> OLD.source_kind
  OR NEW.created_at_iso <> OLD.created_at_iso
BEGIN
  SELECT RAISE(ABORT, 'ORDER_HELD_ORDER_SOURCE_IMMUTABLE');
END;
`;

/**
 * M41 共享意图闸门：共享必须由本机一次性明确请求，迁移只回填已有发布事实。
 * 不重建 M40 表，避免破坏旧设备上的密文、触发器与索引；LOCAL_DELETE_PENDING
 * 是删除补偿路径，允许它把尚未请求的 NeedsEvaluation 置为 Blocked。
 */
const M41 = `
ALTER TABLE held_order_records
ADD COLUMN share_requested_at_iso TEXT NULL
  CHECK (
    share_requested_at_iso IS NULL
    OR (TRIM(share_requested_at_iso) <> '' AND LENGTH(share_requested_at_iso) <= 64)
  );

UPDATE held_order_records
SET share_requested_at_iso = COALESCE(updated_at_iso, created_at_iso)
WHERE share_state IN ('PendingPublish', 'Published', 'Blocked')
  AND share_requested_at_iso IS NULL;

CREATE TRIGGER IF NOT EXISTS trg_held_order_share_request_required
BEFORE UPDATE OF share_state, publish_block_reason, share_requested_at_iso ON held_order_records
FOR EACH ROW
WHEN NEW.share_state IN ('PendingPublish', 'Published', 'Blocked')
  AND NEW.share_requested_at_iso IS NULL
  AND NOT (
    NEW.share_state = 'Blocked'
    AND COALESCE(NEW.publish_block_reason, '') = 'LOCAL_DELETE_PENDING'
  )
BEGIN
  SELECT RAISE(ABORT, 'HELD_ORDER_SHARE_REQUEST_REQUIRED');
END;

CREATE TRIGGER IF NOT EXISTS trg_held_order_share_request_required_insert
BEFORE INSERT ON held_order_records
FOR EACH ROW
WHEN NEW.share_state IN ('PendingPublish', 'Published', 'Blocked')
  AND NEW.share_requested_at_iso IS NULL
  AND NOT (
    NEW.share_state = 'Blocked'
    AND COALESCE(NEW.publish_block_reason, '') = 'LOCAL_DELETE_PENDING'
  )
BEGIN
  SELECT RAISE(ABORT, 'HELD_ORDER_SHARE_REQUEST_REQUIRED');
END;

CREATE TRIGGER IF NOT EXISTS trg_held_order_share_request_immutable
BEFORE UPDATE OF share_requested_at_iso ON held_order_records
FOR EACH ROW
WHEN OLD.share_requested_at_iso IS NOT NULL
  AND (
    NEW.share_requested_at_iso IS NULL
    OR NEW.share_requested_at_iso <> OLD.share_requested_at_iso
  )
BEGIN
  SELECT RAISE(ABORT, 'HELD_ORDER_SHARE_REQUEST_IMMUTABLE');
END;

CREATE INDEX IF NOT EXISTS ix_held_order_records_share_request
  ON held_order_records (
    store_code,
    device_code,
    status,
    share_state,
    share_requested_at_iso,
    local_sequence DESC
  );
`;

/**
 * M42 共享 claim wire 版本：保留既有 payload_version=1（本地加密封装版本）
 * 与全部旧密文，新增独立 V1/V2 wire 判别列。旧记录默认 V1；新记录按实际
 * SharedSaleCart 版本写入，且版本事实不可在写后改动。
 */
const M42 = `
ALTER TABLE shared_held_order_claim_records
ADD COLUMN wire_payload_version INTEGER NOT NULL DEFAULT 1
  CHECK (
    typeof(wire_payload_version) = 'integer'
    AND wire_payload_version IN (1, 2)
  );

CREATE TRIGGER IF NOT EXISTS trg_shared_held_order_claim_wire_version_immutable
BEFORE UPDATE OF wire_payload_version ON shared_held_order_claim_records
FOR EACH ROW
WHEN NEW.wire_payload_version <> OLD.wire_payload_version
BEGIN
  SELECT RAISE(ABORT, 'SHARED_HELD_ORDER_CLAIM_WIRE_VERSION_IMMUTABLE');
END;
`;

/**
 * M43 publication wire 版本：首次网络发送前写入，响应丢失后的重试必须复用同一版本；
 * 旧 PendingPublish 行留空，下一次实际发送时才按当时 capability 安全锁定。
 */
const M43 = `
ALTER TABLE held_order_records
ADD COLUMN wire_payload_version INTEGER NULL
  CHECK (
    wire_payload_version IS NULL OR (
      typeof(wire_payload_version) = 'integer'
      AND wire_payload_version IN (1, 2)
    )
  );

CREATE TRIGGER IF NOT EXISTS trg_held_order_publication_wire_version_immutable
BEFORE UPDATE OF wire_payload_version ON held_order_records
FOR EACH ROW
WHEN OLD.wire_payload_version IS NOT NULL
  AND NEW.wire_payload_version IS NOT OLD.wire_payload_version
BEGIN
  SELECT RAISE(ABORT, 'HELD_ORDER_PUBLICATION_WIRE_VERSION_IMMUTABLE');
END;
`;

/**
 * M44 将 Linkly 的最终会话确认绑定到原 payment_attempt：终态和 marker 在同一
 * durable record 上，进程在 provider ACK 后崩溃时仍能安全重试原 ACK。旧记录没有
 * 可信环境，运行时必须 fail-closed，不能用当前环境猜测确认目标。
 */
const M44 = `
ALTER TABLE payment_attempts ADD COLUMN provider_environment TEXT NULL;
ALTER TABLE payment_attempts ADD COLUMN provider_acknowledged_at_iso TEXT NULL;
ALTER TABLE installment_provider_attempts ADD COLUMN provider_environment TEXT NULL;
ALTER TABLE installment_provider_attempts ADD COLUMN provider_session_id TEXT NULL;
ALTER TABLE installment_provider_attempts ADD COLUMN provider_acknowledged_at_iso TEXT NULL;
CREATE INDEX IF NOT EXISTS ix_payment_attempts_linkly_ack_pending
  ON payment_attempts (provider, provider_environment, provider_acknowledged_at_iso, state);
CREATE INDEX IF NOT EXISTS ix_installment_provider_attempts_linkly_ack_pending
  ON installment_provider_attempts (provider, provider_environment, provider_session_id, provider_acknowledged_at_iso, state);
`;

/**
 * M45 支付恢复中心：异常订单从当前收银车移交后仍以原 order/attempt 为唯一身份。
 * case 保存当前投影，action 保存不可变人工判定历史；人工结论不能覆盖 provider 账本。
 */
const M45 = `
CREATE TABLE payment_recovery_cases (
  record_id TEXT PRIMARY KEY,
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  attempt_id TEXT NOT NULL UNIQUE REFERENCES payment_attempts(attempt_id) ON DELETE RESTRICT,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  state TEXT NOT NULL CHECK (state IN (
    'pending', 'manual-paid', 'manual-unpaid', 'manual-uncertain',
    'provider-recovered', 'review-required'
  )),
  is_parked INTEGER NOT NULL CHECK (is_parked IN (0, 1)),
  park_action_id TEXT NOT NULL UNIQUE,
  opened_at_iso TEXT NOT NULL,
  updated_at_iso TEXT NOT NULL,
  CHECK (TRIM(record_id) <> '' AND LENGTH(record_id) <= 128),
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (TRIM(attempt_id) <> '' AND LENGTH(attempt_id) <= 128),
  CHECK (TRIM(store_code) <> '' AND LENGTH(store_code) <= 64),
  CHECK (TRIM(device_code) <> '' AND LENGTH(device_code) <= 128)
);
-- 人工结论必须绑定一次对原 provider attempt 的只读对账；仅 paid 使用，且不能换绑。
CREATE TABLE payment_recovery_reconciliations (
  reconciliation_id TEXT PRIMARY KEY,
  record_id TEXT NOT NULL REFERENCES payment_recovery_cases(record_id) ON DELETE RESTRICT,
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  attempt_id TEXT NOT NULL REFERENCES payment_attempts(attempt_id) ON DELETE RESTRICT,
  provider TEXT NOT NULL CHECK (provider IN ('square', 'linkly-cloud')),
  observed_state TEXT NOT NULL CHECK (observed_state IN ('Submitted', 'Pending', 'Unknown')),
  observed_at_iso TEXT NOT NULL,
  CHECK (TRIM(reconciliation_id) <> '' AND LENGTH(reconciliation_id) <= 128),
  CHECK (TRIM(order_guid) <> '' AND LENGTH(order_guid) <= 128),
  CHECK (TRIM(attempt_id) <> '' AND LENGTH(attempt_id) <= 128)
);
CREATE TABLE payment_recovery_authorizations (
  authorization_id TEXT PRIMARY KEY,
  record_id TEXT NOT NULL REFERENCES payment_recovery_cases(record_id) ON DELETE RESTRICT,
  order_guid TEXT NOT NULL REFERENCES local_orders(order_guid) ON DELETE RESTRICT,
  attempt_id TEXT NOT NULL REFERENCES payment_attempts(attempt_id) ON DELETE RESTRICT,
  action_id TEXT NOT NULL UNIQUE,
  finding TEXT NOT NULL CHECK (finding IN ('paid', 'unpaid', 'uncertain')),
  requesting_actor_json TEXT NOT NULL,
  supervisor_actor_json TEXT NOT NULL,
  created_at_iso TEXT NOT NULL,
  CHECK (TRIM(authorization_id) <> '' AND LENGTH(authorization_id) <= 128),
  CHECK (TRIM(action_id) <> '' AND LENGTH(action_id) <= 128)
);
CREATE TABLE payment_recovery_actions (
  action_id TEXT PRIMARY KEY,
  record_id TEXT NOT NULL REFERENCES payment_recovery_cases(record_id) ON DELETE RESTRICT,
  request_signature TEXT NOT NULL,
  finding TEXT NOT NULL CHECK (finding IN ('paid', 'unpaid', 'uncertain')),
  verified_amount_cents INTEGER NULL,
  evidence_reference TEXT NOT NULL,
  note TEXT NOT NULL,
  authorization_id TEXT NOT NULL REFERENCES payment_recovery_authorizations(authorization_id) ON DELETE RESTRICT,
  supervisor_actor_json TEXT NOT NULL,
  requesting_actor_json TEXT NOT NULL,
  reconciliation_id TEXT NULL UNIQUE REFERENCES payment_recovery_reconciliations(reconciliation_id) ON DELETE RESTRICT,
  attempt_state_snapshot TEXT NOT NULL,
  created_at_iso TEXT NOT NULL,
  CHECK (TRIM(action_id) <> '' AND LENGTH(action_id) <= 128),
  CHECK (TRIM(request_signature) <> '' AND LENGTH(request_signature) <= 2048),
  CHECK (TRIM(evidence_reference) <> '' AND LENGTH(evidence_reference) <= 256),
  CHECK (TRIM(note) <> '' AND LENGTH(note) <= 1000),
  CHECK (TRIM(authorization_id) <> '' AND LENGTH(authorization_id) <= 128),
  CHECK ((finding = 'paid' AND verified_amount_cents IS NOT NULL AND verified_amount_cents > 0)
    OR (finding <> 'paid' AND verified_amount_cents IS NULL))
);
CREATE TABLE payment_recovery_park_actions (
  action_id TEXT PRIMARY KEY,
  record_id TEXT NOT NULL REFERENCES payment_recovery_cases(record_id) ON DELETE RESTRICT,
  actor_json TEXT NOT NULL,
  created_at_iso TEXT NOT NULL,
  CHECK (TRIM(action_id) <> '' AND LENGTH(action_id) <= 128)
);
-- 人工已收款 tender 的来源独立于 provider attempt；同步端只能由该不可变绑定识别 MANUAL_CARD。
CREATE TABLE manual_payment_tender_bindings (
  tender_guid TEXT PRIMARY KEY REFERENCES order_tenders(tender_guid) ON DELETE RESTRICT,
  record_id TEXT NOT NULL REFERENCES payment_recovery_cases(record_id) ON DELETE RESTRICT,
  action_id TEXT NOT NULL UNIQUE REFERENCES payment_recovery_actions(action_id) ON DELETE RESTRICT,
  attempt_id TEXT NOT NULL UNIQUE REFERENCES payment_attempts(attempt_id) ON DELETE RESTRICT,
  created_at_iso TEXT NOT NULL,
  CHECK (TRIM(tender_guid) <> '' AND LENGTH(tender_guid) <= 128),
  CHECK (TRIM(record_id) <> '' AND LENGTH(record_id) <= 128),
  CHECK (TRIM(action_id) <> '' AND LENGTH(action_id) <= 128),
  CHECK (TRIM(attempt_id) <> '' AND LENGTH(attempt_id) <= 128)
);
CREATE INDEX ix_payment_recovery_cases_scope_state
  ON payment_recovery_cases (store_code, device_code, is_parked, state, updated_at_iso DESC);
CREATE INDEX ix_payment_recovery_actions_record_time
  ON payment_recovery_actions (record_id, created_at_iso, action_id);
CREATE INDEX ix_payment_recovery_reconciliations_record_time
  ON payment_recovery_reconciliations (record_id, observed_at_iso DESC);
-- 已移交的异常 attempt 不再占用整个 iPad；同一支付提供方仍视为同一物理通道，
-- 在没有可持久化 lane identity 前继续失败关闭，避免同一刷卡机重复扣款。
DROP TRIGGER trg_payment_attempts_single_terminal_blocking_insert;
DROP TRIGGER trg_payment_attempts_single_terminal_blocking_update;
CREATE TRIGGER trg_payment_attempts_single_terminal_blocking_insert
BEFORE INSERT ON payment_attempts
FOR EACH ROW
WHEN NEW.state IN ('Created', 'Submitted', 'Pending', 'Approved', 'Unknown')
AND EXISTS (
  SELECT 1 FROM payment_order_draft_bindings incoming_draft
  WHERE incoming_draft.order_guid = NEW.order_guid AND incoming_draft.state = 'Active'
)
AND EXISTS (
  SELECT 1
  FROM payment_attempts prior
  INNER JOIN local_orders prior_order ON prior_order.order_guid = prior.order_guid
  INNER JOIN local_orders incoming_order ON incoming_order.order_guid = NEW.order_guid
  INNER JOIN payment_order_draft_bindings prior_draft
    ON prior_draft.order_guid = prior.order_guid AND prior_draft.state = 'Active'
  WHERE prior.order_guid <> NEW.order_guid
    AND prior_order.store_code = incoming_order.store_code
    AND prior_order.device_code = incoming_order.device_code
    AND (
      prior.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
      OR (prior.state = 'Approved' AND NOT EXISTS (
        SELECT 1 FROM order_tenders consumed
        WHERE consumed.payment_attempt_id = prior.attempt_id
          AND consumed.order_guid = prior.order_guid
          AND consumed.amount_cents = prior.amount_cents
          AND ((prior.provider IN ('square', 'linkly-cloud') AND consumed.method = 'card')
            OR (prior.provider = 'voucher' AND consumed.method = 'voucher'))
      ))
    )
    -- 已由主管明确分类并恢复到原订单上下文的旧 attempt 不再永久占用其他订单。
    AND NOT EXISTS (
      SELECT 1 FROM payment_recovery_cases released
      WHERE released.attempt_id = prior.attempt_id
        AND released.is_parked = 0
        AND released.state IN ('manual-unpaid', 'manual-uncertain')
    )
    AND (
      NOT EXISTS (
        SELECT 1 FROM payment_recovery_cases parked
        WHERE parked.attempt_id = prior.attempt_id AND parked.is_parked = 1
      )
      OR (prior.provider = NEW.provider AND NOT EXISTS (
        SELECT 1 FROM payment_recovery_cases released
        WHERE released.attempt_id = prior.attempt_id
          AND released.is_parked = 1
          AND released.state = 'manual-unpaid'
      ))
    )
)
BEGIN SELECT RAISE(ABORT, 'PAYMENT_TERMINAL_BLOCKING_ATTEMPT_EXISTS'); END;
CREATE TRIGGER trg_payment_attempts_single_terminal_blocking_update
BEFORE UPDATE OF state ON payment_attempts
FOR EACH ROW
WHEN NEW.state IN ('Created', 'Submitted', 'Pending', 'Approved', 'Unknown')
AND NOT EXISTS (
  SELECT 1 FROM payment_recovery_cases incoming_case
  WHERE incoming_case.attempt_id = NEW.attempt_id
)
AND EXISTS (
  SELECT 1 FROM payment_order_draft_bindings incoming_draft
  WHERE incoming_draft.order_guid = NEW.order_guid AND incoming_draft.state = 'Active'
)
AND EXISTS (
  SELECT 1
  FROM payment_attempts prior
  INNER JOIN local_orders prior_order ON prior_order.order_guid = prior.order_guid
  INNER JOIN local_orders incoming_order ON incoming_order.order_guid = NEW.order_guid
  INNER JOIN payment_order_draft_bindings prior_draft
    ON prior_draft.order_guid = prior.order_guid AND prior_draft.state = 'Active'
  WHERE prior.attempt_id <> OLD.attempt_id
    AND prior.order_guid <> NEW.order_guid
    AND prior_order.store_code = incoming_order.store_code
    AND prior_order.device_code = incoming_order.device_code
    AND (
      prior.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
      OR (prior.state = 'Approved' AND NOT EXISTS (
        SELECT 1 FROM order_tenders consumed
        WHERE consumed.payment_attempt_id = prior.attempt_id
          AND consumed.order_guid = prior.order_guid
          AND consumed.amount_cents = prior.amount_cents
          AND ((prior.provider IN ('square', 'linkly-cloud') AND consumed.method = 'card')
            OR (prior.provider = 'voucher' AND consumed.method = 'voucher'))
      ))
    )
    -- 恢复后的人工未扣款/仍未知结论保留 provider 原事实，但释放跨订单全局阻断。
    AND NOT EXISTS (
      SELECT 1 FROM payment_recovery_cases released
      WHERE released.attempt_id = prior.attempt_id
        AND released.is_parked = 0
        AND released.state IN ('manual-unpaid', 'manual-uncertain')
    )
    AND (
      NOT EXISTS (
        SELECT 1 FROM payment_recovery_cases parked
        WHERE parked.attempt_id = prior.attempt_id AND parked.is_parked = 1
      )
      OR (prior.provider = NEW.provider AND NOT EXISTS (
        SELECT 1 FROM payment_recovery_cases released
        WHERE released.attempt_id = prior.attempt_id
          AND released.is_parked = 1
          AND released.state = 'manual-unpaid'
      ))
    )
)
BEGIN SELECT RAISE(ABORT, 'PAYMENT_TERMINAL_BLOCKING_ATTEMPT_EXISTS'); END;
-- Parked case 可释放购物车，但 Linkly 最终结果的 ACK 仍占用物理 lane。
CREATE TRIGGER trg_payment_attempts_linkly_ack_lane_insert
BEFORE INSERT ON payment_attempts
FOR EACH ROW
WHEN NEW.state IN ('Created', 'Submitted')
AND EXISTS (
  SELECT 1
  FROM local_orders incoming_order
  INNER JOIN payment_order_draft_bindings incoming_draft
    ON incoming_draft.order_guid = incoming_order.order_guid AND incoming_draft.state = 'Active'
  INNER JOIN payment_attempts ack_pending ON 1 = 1
  INNER JOIN local_orders ack_order ON ack_order.order_guid = ack_pending.order_guid
  WHERE incoming_order.order_guid = NEW.order_guid
    AND ack_order.store_code = incoming_order.store_code
    AND ack_order.device_code = incoming_order.device_code
    AND ack_pending.provider = 'linkly-cloud'
    AND ack_pending.state IN ('Approved', 'Declined', 'Cancelled')
    AND ack_pending.provider_environment IS NOT NULL
    AND ack_pending.provider_acknowledged_at_iso IS NULL
    AND ack_pending.session_id IS NOT NULL
)
BEGIN SELECT RAISE(ABORT, 'PAYMENT_LINKLY_ACK_PENDING'); END;
CREATE TRIGGER trg_payment_attempts_linkly_ack_lane_update
BEFORE UPDATE OF state ON payment_attempts
FOR EACH ROW
WHEN NEW.state IN ('Created', 'Submitted')
AND EXISTS (
  SELECT 1
  FROM local_orders incoming_order
  INNER JOIN payment_attempts ack_pending ON 1 = 1
  INNER JOIN local_orders ack_order ON ack_order.order_guid = ack_pending.order_guid
  WHERE incoming_order.order_guid = NEW.order_guid
    AND ack_pending.attempt_id <> OLD.attempt_id
    AND ack_order.store_code = incoming_order.store_code
    AND ack_order.device_code = incoming_order.device_code
    AND ack_pending.provider = 'linkly-cloud'
    AND ack_pending.state IN ('Approved', 'Declined', 'Cancelled')
    AND ack_pending.provider_environment IS NOT NULL
    AND ack_pending.provider_acknowledged_at_iso IS NULL
    AND ack_pending.session_id IS NOT NULL
)
BEGIN SELECT RAISE(ABORT, 'PAYMENT_LINKLY_ACK_PENDING'); END;
CREATE TRIGGER trg_payment_recovery_case_identity_immutable
BEFORE UPDATE ON payment_recovery_cases
FOR EACH ROW WHEN NEW.record_id <> OLD.record_id
  OR NEW.order_guid <> OLD.order_guid OR NEW.attempt_id <> OLD.attempt_id
  OR NEW.store_code <> OLD.store_code OR NEW.device_code <> OLD.device_code
  OR NEW.park_action_id <> OLD.park_action_id OR NEW.opened_at_iso <> OLD.opened_at_iso
BEGIN SELECT RAISE(ABORT, 'PAYMENT_RECOVERY_CASE_IDENTITY_IMMUTABLE'); END;
CREATE TRIGGER trg_payment_recovery_actions_immutable
BEFORE UPDATE ON payment_recovery_actions
BEGIN SELECT RAISE(ABORT, 'PAYMENT_RECOVERY_ACTION_IMMUTABLE'); END;
CREATE TRIGGER trg_payment_recovery_actions_no_delete
BEFORE DELETE ON payment_recovery_actions
BEGIN SELECT RAISE(ABORT, 'PAYMENT_RECOVERY_ACTION_IMMUTABLE'); END;
CREATE TRIGGER trg_payment_recovery_authorizations_immutable
BEFORE UPDATE ON payment_recovery_authorizations
BEGIN SELECT RAISE(ABORT, 'PAYMENT_RECOVERY_AUTHORIZATION_IMMUTABLE'); END;
CREATE TRIGGER trg_payment_recovery_authorizations_no_delete
BEFORE DELETE ON payment_recovery_authorizations
BEGIN SELECT RAISE(ABORT, 'PAYMENT_RECOVERY_AUTHORIZATION_IMMUTABLE'); END;
CREATE TRIGGER trg_payment_recovery_reconciliations_immutable
BEFORE UPDATE ON payment_recovery_reconciliations
BEGIN SELECT RAISE(ABORT, 'PAYMENT_RECOVERY_RECONCILIATION_IMMUTABLE'); END;
CREATE TRIGGER trg_payment_recovery_reconciliations_no_delete
BEFORE DELETE ON payment_recovery_reconciliations
BEGIN SELECT RAISE(ABORT, 'PAYMENT_RECOVERY_RECONCILIATION_IMMUTABLE'); END;
CREATE TRIGGER trg_payment_recovery_park_actions_immutable
BEFORE UPDATE ON payment_recovery_park_actions
BEGIN SELECT RAISE(ABORT, 'PAYMENT_RECOVERY_PARK_ACTION_IMMUTABLE'); END;
CREATE TRIGGER trg_payment_recovery_park_actions_no_delete
BEFORE DELETE ON payment_recovery_park_actions
BEGIN SELECT RAISE(ABORT, 'PAYMENT_RECOVERY_PARK_ACTION_IMMUTABLE'); END;
CREATE TRIGGER trg_manual_payment_tender_bindings_immutable
BEFORE UPDATE ON manual_payment_tender_bindings
BEGIN SELECT RAISE(ABORT, 'MANUAL_PAYMENT_TENDER_BINDING_IMMUTABLE'); END;
CREATE TRIGGER trg_manual_payment_tender_bindings_no_delete
BEFORE DELETE ON manual_payment_tender_bindings
BEGIN SELECT RAISE(ABORT, 'MANUAL_PAYMENT_TENDER_BINDING_IMMUTABLE'); END;
`;

// 保留 M45 恢复中心的移交与通道保护，仅扩展已消费手动刷卡的识别。
const M46 = `
DROP TRIGGER trg_payment_attempts_single_terminal_blocking_insert;
DROP TRIGGER trg_payment_attempts_single_terminal_blocking_update;
CREATE TRIGGER trg_payment_attempts_single_terminal_blocking_insert
BEFORE INSERT ON payment_attempts
FOR EACH ROW
WHEN NEW.state IN ('Created', 'Submitted', 'Pending', 'Approved', 'Unknown')
AND EXISTS (
  SELECT 1 FROM payment_order_draft_bindings incoming_draft
  WHERE incoming_draft.order_guid = NEW.order_guid AND incoming_draft.state = 'Active'
)
AND EXISTS (
  SELECT 1
  FROM payment_attempts prior
  INNER JOIN local_orders prior_order ON prior_order.order_guid = prior.order_guid
  INNER JOIN local_orders incoming_order ON incoming_order.order_guid = NEW.order_guid
  INNER JOIN payment_order_draft_bindings prior_draft
    ON prior_draft.order_guid = prior.order_guid AND prior_draft.state = 'Active'
  WHERE prior.order_guid <> NEW.order_guid
    AND prior_order.store_code = incoming_order.store_code
    AND prior_order.device_code = incoming_order.device_code
    AND (
      prior.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
      OR (prior.state = 'Approved' AND NOT EXISTS (
        SELECT 1 FROM order_tenders consumed
        WHERE consumed.payment_attempt_id = prior.attempt_id
          AND consumed.order_guid = prior.order_guid
          AND consumed.amount_cents = prior.amount_cents
          AND ((prior.provider IN ('square', 'linkly-cloud', 'manual-card') AND consumed.method = 'card')
            OR (prior.provider = 'voucher' AND consumed.method = 'voucher'))
      ))
    )
    -- 已由主管明确分类并恢复到原订单上下文的旧 attempt 不再永久占用其他订单。
    AND NOT EXISTS (
      SELECT 1 FROM payment_recovery_cases released
      WHERE released.attempt_id = prior.attempt_id
        AND released.is_parked = 0
        AND released.state IN ('manual-unpaid', 'manual-uncertain')
    )
    AND (
      NOT EXISTS (
        SELECT 1 FROM payment_recovery_cases parked
        WHERE parked.attempt_id = prior.attempt_id AND parked.is_parked = 1
      )
      OR (prior.provider = NEW.provider AND NOT EXISTS (
        SELECT 1 FROM payment_recovery_cases released
        WHERE released.attempt_id = prior.attempt_id
          AND released.is_parked = 1
          AND released.state = 'manual-unpaid'
      ))
    )
)
BEGIN SELECT RAISE(ABORT, 'PAYMENT_TERMINAL_BLOCKING_ATTEMPT_EXISTS'); END;
CREATE TRIGGER trg_payment_attempts_single_terminal_blocking_update
BEFORE UPDATE OF state ON payment_attempts
FOR EACH ROW
WHEN NEW.state IN ('Created', 'Submitted', 'Pending', 'Approved', 'Unknown')
AND NOT EXISTS (
  SELECT 1 FROM payment_recovery_cases incoming_case
  WHERE incoming_case.attempt_id = NEW.attempt_id
)
AND EXISTS (
  SELECT 1 FROM payment_order_draft_bindings incoming_draft
  WHERE incoming_draft.order_guid = NEW.order_guid AND incoming_draft.state = 'Active'
)
AND EXISTS (
  SELECT 1
  FROM payment_attempts prior
  INNER JOIN local_orders prior_order ON prior_order.order_guid = prior.order_guid
  INNER JOIN local_orders incoming_order ON incoming_order.order_guid = NEW.order_guid
  INNER JOIN payment_order_draft_bindings prior_draft
    ON prior_draft.order_guid = prior.order_guid AND prior_draft.state = 'Active'
  WHERE prior.attempt_id <> OLD.attempt_id
    AND prior.order_guid <> NEW.order_guid
    AND prior_order.store_code = incoming_order.store_code
    AND prior_order.device_code = incoming_order.device_code
    AND (
      prior.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
      OR (prior.state = 'Approved' AND NOT EXISTS (
        SELECT 1 FROM order_tenders consumed
        WHERE consumed.payment_attempt_id = prior.attempt_id
          AND consumed.order_guid = prior.order_guid
          AND consumed.amount_cents = prior.amount_cents
          AND ((prior.provider IN ('square', 'linkly-cloud', 'manual-card') AND consumed.method = 'card')
            OR (prior.provider = 'voucher' AND consumed.method = 'voucher'))
      ))
    )
    -- 恢复后的人工未扣款/仍未知结论保留 provider 原事实，但释放跨订单全局阻断。
    AND NOT EXISTS (
      SELECT 1 FROM payment_recovery_cases released
      WHERE released.attempt_id = prior.attempt_id
        AND released.is_parked = 0
        AND released.state IN ('manual-unpaid', 'manual-uncertain')
    )
    AND (
      NOT EXISTS (
        SELECT 1 FROM payment_recovery_cases parked
        WHERE parked.attempt_id = prior.attempt_id AND parked.is_parked = 1
      )
      OR (prior.provider = NEW.provider AND NOT EXISTS (
        SELECT 1 FROM payment_recovery_cases released
        WHERE released.attempt_id = prior.attempt_id
          AND released.is_parked = 1
          AND released.state = 'manual-unpaid'
      ))
    )
)
BEGIN SELECT RAISE(ABORT, 'PAYMENT_TERMINAL_BLOCKING_ATTEMPT_EXISTS'); END;
`;

const M47 = `
-- 一码多商品候选：服务端目录每个查询码只保留一个胜出商品，这里按门店保存落到
-- 多个不同商品的全部候选（每个商品一行，candidate_order 保留服务端决胜顺序）。
-- 独立于 catalog_snapshots 子树，快照激活、退役与分批清理均不触及本表。
CREATE TABLE catalog_code_conflicts (
  store_code TEXT NOT NULL CHECK (TRIM(store_code) <> ''),
  lookup_code_normalized TEXT NOT NULL CHECK (
    TRIM(lookup_code_normalized) <> ''
    AND lookup_code_normalized = UPPER(TRIM(lookup_code_normalized))
  ),
  product_code TEXT NOT NULL CHECK (TRIM(product_code) <> ''),
  candidate_order INTEGER NOT NULL CHECK (
    typeof(candidate_order) = 'integer' AND candidate_order >= 0
  ),
  reference_code TEXT NULL,
  item_number TEXT NULL,
  display_name TEXT NOT NULL CHECK (TRIM(display_name) <> ''),
  barcode TEXT NULL,
  lookup_code TEXT NOT NULL CHECK (TRIM(lookup_code) <> ''),
  retail_price_cents INTEGER NOT NULL CHECK (
    typeof(retail_price_cents) = 'integer'
  ),
  price_source INTEGER NOT NULL CHECK (price_source IN (0, 1, 2, 3, 4)),
  price_source_label TEXT NOT NULL CHECK (TRIM(price_source_label) <> ''),
  quantity_factor TEXT NOT NULL,
  tax_rate_basis_points INTEGER NULL,
  updated_at_iso TEXT NULL,
  row_version TEXT NULL,
  product_image TEXT NULL,
  discount_rate TEXT NULL,
  is_special_product INTEGER NOT NULL CHECK (is_special_product IN (0, 1)),
  -- 主键前缀 (store_code, lookup_code_normalized) 即扫码点查索引。
  PRIMARY KEY (store_code, lookup_code_normalized, product_code)
);
`;

export const POS_DATABASE_MIGRATIONS: readonly DatabaseMigration[] = [
  { version: 1, name: "M1_security_and_time", sql: M1 },
  { version: 2, name: "M2_catalog", sql: M2 },
  { version: 3, name: "M3_orders", sql: M3 },
  { version: 4, name: "M4_payment_sync_audit", sql: M4 },
  { version: 5, name: "M5_printing_and_drawer", sql: M5 },
  { version: 6, name: "M6_operations", sql: M6 },
  { version: 7, name: "M7_peripheral_binding", sql: M7 },
  { version: 8, name: "M8_payment_action_and_reversal_links", sql: M8 },
  { version: 9, name: "M9_held_order_records", sql: M9 },
  { version: 10, name: "M10_terminal_cart_fences", sql: M10 },
  { version: 11, name: "M11_payment_persistence", sql: M11 },
  { version: 12, name: "M12_payment_draft_cancel_close", sql: M12 },
  { version: 13, name: "M13_durable_return_ledger", sql: M13 },
  { version: 14, name: "M14_return_fulfilment_receipt_policy", sql: M14 },
  { version: 15, name: "M15_order_line_sync_provenance", sql: M15 },
  { version: 16, name: "M16_voucher_tender_reversal_ledger", sql: M16 },
  { version: 17, name: "M17_daily_close_and_special_products", sql: M17 },
  { version: 18, name: "M18_installment_snapshot_cache", sql: M18 },
  { version: 19, name: "M19_attendance_security_cache", sql: M19 },
  { version: 20, name: "M20_installment_action_ledger", sql: M20 },
  { version: 21, name: "M21_installment_action_guards", sql: M21 },
  { version: 22, name: "M22_installment_payment_ledger", sql: M22 },
  { version: 23, name: "M23_catalog_lookup_overlays", sql: M23 },
  { version: 24, name: "M24_mixed_cash_amount_facts", sql: M24 },
  { version: 25, name: "M25_catalog_delta_generations", sql: M25 },
  { version: 26, name: "M26_log_delivery_outboxes", sql: M26 },
  { version: 27, name: "M27_payment_order_line_guid_recovery", sql: M27 },
  { version: 28, name: "M28_payment_actor_snapshots", sql: M28 },
  { version: 29, name: "M29_voucher_actor_required", sql: M29 },
  { version: 30, name: "M30_audit_scope_delivery", sql: M30 },
  { version: 31, name: "M31_audit_scope_immutability", sql: M31 },
  { version: 32, name: "M32_audit_scope_insert_guard", sql: M32 },
  { version: 33, name: "M33_remote_receipt_reprint_identity", sql: M33 },
  { version: 34, name: "M34_installment_receipt_reprint_identity", sql: M34 },
  { version: 35, name: "M35_installment_payment_success_external_order", sql: M35 },
  { version: 36, name: "M36_installment_lifecycle_actions", sql: M36 },
  { version: 37, name: "M37_installment_action_resolution_code", sql: M37 },
  { version: 38, name: "M38_repair_installment_lifecycle_actions", sql: M38 },
  { version: 39, name: "M39_mixed_cash_final_rounding", sql: M39 },
  { version: 40, name: "M40_shared_held_order_share_state", sql: M40 },
  { version: 41, name: "M41_shared_held_order_share_request", sql: M41 },
  { version: 42, name: "M42_shared_held_order_claim_wire_version", sql: M42 },
  { version: 43, name: "M43_shared_held_order_publication_wire_version", sql: M43 },
  { version: 44, name: "M44_linkly_provider_acknowledgement", sql: M44 },
  { version: 45, name: "M45_payment_recovery_center", sql: M45 },
  { version: 46, name: "M46_manual_card_terminal_blocking", sql: M46 },
  { version: 47, name: "M47_catalog_code_conflicts", sql: M47 },
];

export async function applyMigrations(
  database: SqliteConnectionPort,
  nowIso: () => string,
  migrations: readonly DatabaseMigration[] = POS_DATABASE_MIGRATIONS,
): Promise<void> {
  await database.withExclusiveTransaction(async (transaction) => {
    await transaction.exec(`
      CREATE TABLE IF NOT EXISTS schema_migrations (
        version INTEGER PRIMARY KEY,
        name TEXT NOT NULL,
        applied_at_iso TEXT NOT NULL
      );
    `);
    const applied = await transaction.getAll<{ version: number }>("SELECT version FROM schema_migrations");
    const appliedVersions = new Set(applied.map((row) => row.version));
    const catalogMigrationWasAlreadyApplied = appliedVersions.has(2);

    if (appliedVersions.has(8) && !appliedVersions.has(7)) {
      throw new Error("DRAWER_EVENTS_SCHEMA_INVALID:M8_REQUIRES_M7");
    }
    if (appliedVersions.has(7)) {
      // 部分开发构建曾提前记录 M7，但 drawer_events 仍是更早的窄表；
      // 每次开库都先原地修复并核验，绝不通过删除 App 数据规避旧订单。
      await ensureLegacyM7Columns(transaction, nowIso);
      await assertDrawerEventsSchema(transaction);
    }
    if (appliedVersions.has(17)) {
      await ensureCatalogSchemaForM17(transaction);
      await ensureLocalSpecialProductsSchema(transaction);
    }

    for (const migration of migrations) {
      if (appliedVersions.has(migration.version)) {
        continue;
      }

      if (migration.version === 8 && !appliedVersions.has(7)) {
        throw new Error("DRAWER_EVENTS_SCHEMA_INVALID:M8_REQUIRES_M7");
      }
      if (migration.version === 7 || migration.version === 8) {
        await ensureLegacyM7Columns(transaction, nowIso);
      }
      if (migration.version === 17 && catalogMigrationWasAlreadyApplied) {
        await ensureCatalogSchemaForM17(transaction);
      }
      // 版本记录与 DDL 在同一独占事务中，任何失败都会回滚且不推进版本号。
      await transaction.exec(
        migration.version === 7 && migration.sql === M7
          ? M7_AFTER_SCHEMA_REPAIR
          : migration.sql,
      );
      if (migration.version === 7 || migration.version === 8) {
        // M7/M8 只有在钱箱生产读写所需结构完整时才允许落版本号。
        await assertDrawerEventsSchema(transaction);
      }
      if (migration.version === 17 && catalogMigrationWasAlreadyApplied) {
        await assertCurrentCatalogSchema(transaction);
      }
      if (migration.version === 25) {
        await assertCurrentCatalogSchema(transaction);
      }
      await transaction.run(
        "INSERT INTO schema_migrations (version, name, applied_at_iso) VALUES (?, ?, ?)",
        [migration.version, migration.name, nowIso()],
      );
      appliedVersions.add(migration.version);
    }
  });
}

async function ensureLegacyM7Columns(
  transaction: SqliteConnectionPort,
  nowIso: () => string,
): Promise<void> {
  const columns = await readTableColumns(transaction, "drawer_events");
  if (!columns.length) {
    throw new Error("DRAWER_EVENTS_SCHEMA_INVALID:TABLE_MISSING");
  }
  const eventId = columns.find((column) => column.name === "event_id");
  if (
    !eventId ||
    normalizeSqliteType(eventId.type) !== "TEXT" ||
    Number(eventId.pk) !== 1
  ) {
    // 没有稳定主键时无法无损识别和修复旧动作，必须整笔事务回滚。
    throw new Error("DRAWER_EVENTS_SCHEMA_INVALID:EVENT_ID_PRIMARY_KEY");
  }

  const names = new Set(columns.map((column) => column.name));
  const migratedAtIso = nowIso();
  const missingColumns = [
    ["printer_id", "printer_id TEXT NULL"],
    ["order_guid", "order_guid TEXT NULL"],
    ["print_job_id", "print_job_id TEXT NULL"],
    ["state", "state TEXT NULL"],
    ["reason", "reason TEXT NULL"],
    ["retry_count", "retry_count INTEGER NULL DEFAULT 0"],
    ["requested_at_iso", "requested_at_iso TEXT NULL"],
    ["completed_at_iso", "completed_at_iso TEXT NULL"],
    ["last_error_code", "last_error_code TEXT NULL"],
    ["created_at_iso", "created_at_iso TEXT NULL"],
    ["updated_at_iso", "updated_at_iso TEXT NULL"],
  ] as const;

  for (const [name, definition] of missingColumns) {
    if (names.has(name)) continue;
    await transaction.exec(`ALTER TABLE drawer_events ADD COLUMN ${definition}`);
    names.add(name);
  }

  // 只回填无法供当前生产 store 安全读取的空值；已有有效历史值原样保留。
  await transaction.run(
    `UPDATE drawer_events
     SET created_at_iso = COALESCE(NULLIF(TRIM(updated_at_iso), ''), ?)
     WHERE created_at_iso IS NULL OR TRIM(created_at_iso) = ''`,
    [migratedAtIso],
  );
  await transaction.run(
    `UPDATE drawer_events
     SET updated_at_iso = COALESCE(NULLIF(TRIM(created_at_iso), ''), ?)
     WHERE updated_at_iso IS NULL OR TRIM(updated_at_iso) = ''`,
    [migratedAtIso],
  );
  await transaction.run(
    `UPDATE drawer_events
     SET reason = 'legacy-unknown'
     WHERE reason IS NULL OR TRIM(reason) = ''`,
  );
  await transaction.run(
    `UPDATE drawer_events
     SET retry_count = 0
     WHERE retry_count IS NULL
       OR TYPEOF(retry_count) <> 'integer'
       OR retry_count < 0`,
  );
  await transaction.run(
    `UPDATE drawer_events
     SET state = 'Unknown',
         last_error_code = COALESCE(
           NULLIF(TRIM(last_error_code), ''),
           'DRAWER_STATE_INVALID_MIGRATION'
         ),
         updated_at_iso = ?
     WHERE state IS NULL
        OR TRIM(state) NOT IN ('Required', 'Requested', 'Completed', 'Failed', 'Unknown')`,
    [migratedAtIso],
  );

  const printColumns = new Set(
    (await readTableColumns(transaction, "print_jobs")).map(
      (column) => column.name,
    ),
  );
  if (
    printColumns.has("job_id") &&
    printColumns.has("order_guid") &&
    printColumns.has("printer_id")
  ) {
    // 仅使用同一 print_job 的不可变绑定回填，绝不根据当前设置猜测历史外设或订单。
    await transaction.run(
      `UPDATE drawer_events
       SET order_guid = (
             SELECT print_jobs.order_guid
             FROM print_jobs
             WHERE print_jobs.job_id = drawer_events.print_job_id
               AND print_jobs.order_guid IS NOT NULL
               AND TRIM(print_jobs.order_guid) <> ''
           ),
           printer_id = COALESCE(
             NULLIF(TRIM(printer_id), ''),
             (
               SELECT print_jobs.printer_id
               FROM print_jobs
               WHERE print_jobs.job_id = drawer_events.print_job_id
                 AND print_jobs.printer_id IS NOT NULL
                 AND TRIM(print_jobs.printer_id) <> ''
             )
           )
       WHERE print_job_id IS NOT NULL
         AND (
           order_guid IS NULL OR TRIM(order_guid) = ''
           OR printer_id IS NULL OR TRIM(printer_id) = ''
         )`,
    );
  } else if (
    printColumns.has("job_id") &&
    printColumns.has("printer_id")
  ) {
    await transaction.run(
      `UPDATE drawer_events
       SET printer_id = (
         SELECT print_jobs.printer_id
         FROM print_jobs
         WHERE print_jobs.job_id = drawer_events.print_job_id
           AND print_jobs.printer_id IS NOT NULL
           AND TRIM(print_jobs.printer_id) <> ''
       )
       WHERE (printer_id IS NULL OR TRIM(printer_id) = '')
         AND print_job_id IS NOT NULL`,
    );
  }

  // 无法确定外设或订单身份的旧 Required/Requested/Failed 动作可能已经执行，
  // 统一停为 Unknown，禁止迁移后自动或人工重放、重复开箱或伪造审计归属。
  await transaction.run(
    `UPDATE drawer_events
     SET state = 'Unknown',
         last_error_code = 'DRAWER_PRINTER_BINDING_MISSING_MIGRATION',
         updated_at_iso = ?
     WHERE (printer_id IS NULL OR TRIM(printer_id) = '')
       AND state IN ('Required', 'Requested', 'Failed')`,
    [migratedAtIso],
  );
  await transaction.run(
    `UPDATE drawer_events
     SET state = 'Unknown',
         last_error_code = 'DRAWER_ORDER_BINDING_MISSING_MIGRATION',
         updated_at_iso = ?
     WHERE (order_guid IS NULL OR TRIM(order_guid) = '')
       AND state IN ('Required', 'Requested', 'Failed')`,
    [migratedAtIso],
  );
}

type SqliteColumnInfo = Readonly<{
  name: string;
  type: unknown;
  pk: unknown;
}>;

const catalogTableNames = [
  "catalog_snapshots",
  "catalog_items",
  "catalog_barcodes",
  "catalog_prices",
  "catalog_promotions",
  "special_products",
] as const;

type CatalogTableName = (typeof catalogTableNames)[number];
type ExpectedSqliteColumn = readonly [
  name: string,
  type: string,
  primaryKeyOrder: number,
];
type CatalogSchemaSnapshot = Readonly<
  Record<CatalogTableName, readonly SqliteColumnInfo[]>
>;

const currentCatalogSchema = {
  catalog_snapshots: [
    ["snapshot_id", "TEXT", 1],
    ["catalog_version", "TEXT", 0],
    ["checksum", "TEXT", 0],
    ["state", "TEXT", 0],
    ["downloaded_at_iso", "TEXT", 0],
    ["activated_at_iso", "TEXT", 0],
  ],
  catalog_items: [
    ["snapshot_id", "TEXT", 1],
    ["store_code", "TEXT", 2],
    ["lookup_code_normalized", "TEXT", 3],
    ["product_code", "TEXT", 0],
    ["reference_code", "TEXT", 0],
    ["item_number", "TEXT", 0],
    ["barcode", "TEXT", 0],
    ["lookup_code", "TEXT", 0],
    ["display_name", "TEXT", 0],
    ["retail_price_cents", "INTEGER", 0],
    ["price_source", "INTEGER", 0],
    ["price_source_label", "TEXT", 0],
    ["quantity_factor", "TEXT", 0],
    ["tax_rate_basis_points", "INTEGER", 0],
    ["row_version", "TEXT", 0],
    ["product_image", "TEXT", 0],
    ["discount_rate", "TEXT", 0],
    ["is_special_product", "INTEGER", 0],
    ["is_active", "INTEGER", 0],
    ["updated_at_iso", "TEXT", 0],
  ],
  catalog_barcodes: [],
  catalog_prices: [],
  catalog_promotions: [
    ["snapshot_id", "TEXT", 1],
    ["promotion_id", "TEXT", 2],
    ["definition_json", "TEXT", 0],
    ["valid_from_iso", "TEXT", 0],
    ["valid_until_iso", "TEXT", 0],
    ["priority", "INTEGER", 0],
  ],
  special_products: [
    ["snapshot_id", "TEXT", 1],
    ["store_code", "TEXT", 2],
    ["lookup_code_normalized", "TEXT", 3],
    ["sort_order", "INTEGER", 0],
    ["is_marked", "INTEGER", 0],
    ["updated_at_iso", "TEXT", 0],
  ],
} as const satisfies Record<
  CatalogTableName,
  readonly ExpectedSqliteColumn[]
>;

const m25CatalogSchema = {
  ...currentCatalogSchema,
  catalog_snapshots: [
    ...currentCatalogSchema.catalog_snapshots,
    ["generation_id", "TEXT", 0],
    ["sync_mode", "TEXT", 0],
    ["base_snapshot_id", "TEXT", 0],
    ["base_catalog_version", "TEXT", 0],
  ],
} as const satisfies Record<
  CatalogTableName,
  readonly ExpectedSqliteColumn[]
>;

const m25DeltaDeletionSchema = [
  ["snapshot_id", "TEXT", 1],
  ["store_code", "TEXT", 2],
  ["lookup_code_normalized", "TEXT", 3],
] as const satisfies readonly ExpectedSqliteColumn[];

const legacyM2SingleKeyCatalogSchema = {
  catalog_snapshots: currentCatalogSchema.catalog_snapshots,
  catalog_items: [
    ["product_code", "TEXT", 1],
    ["snapshot_id", "TEXT", 0],
    ["item_number", "TEXT", 0],
    ["display_name", "TEXT", 0],
    ["department_code", "TEXT", 0],
    ["tax_rate_basis_points", "INTEGER", 0],
    ["is_active", "INTEGER", 0],
    ["updated_at_iso", "TEXT", 0],
  ],
  catalog_barcodes: [
    ["barcode", "TEXT", 1],
    ["product_code", "TEXT", 0],
    ["barcode_type", "TEXT", 0],
    ["updated_at_iso", "TEXT", 0],
  ],
  catalog_prices: [
    ["price_id", "TEXT", 1],
    ["product_code", "TEXT", 0],
    ["price_cents", "INTEGER", 0],
    ["valid_from_iso", "TEXT", 0],
    ["valid_until_iso", "TEXT", 0],
    ["source_version", "TEXT", 0],
  ],
  catalog_promotions: [
    ["promotion_id", "TEXT", 1],
    ["snapshot_id", "TEXT", 0],
    ["definition_json", "TEXT", 0],
    ["valid_from_iso", "TEXT", 0],
    ["valid_until_iso", "TEXT", 0],
    ["priority", "INTEGER", 0],
  ],
  special_products: [
    ["product_code", "TEXT", 1],
    ["snapshot_id", "TEXT", 0],
    ["sort_order", "INTEGER", 0],
    ["is_marked", "INTEGER", 0],
    ["updated_at_iso", "TEXT", 0],
  ],
} as const satisfies Record<
  CatalogTableName,
  readonly ExpectedSqliteColumn[]
>;

const legacyM2SnapshotKeyCatalogSchema = {
  catalog_snapshots: currentCatalogSchema.catalog_snapshots,
  catalog_items: [
    ["snapshot_id", "TEXT", 1],
    ["product_code", "TEXT", 2],
    ["item_number", "TEXT", 0],
    ["display_name", "TEXT", 0],
    ["department_code", "TEXT", 0],
    ["tax_rate_basis_points", "INTEGER", 0],
    ["is_active", "INTEGER", 0],
    ["updated_at_iso", "TEXT", 0],
  ],
  catalog_barcodes: [
    ["snapshot_id", "TEXT", 1],
    ["barcode", "TEXT", 2],
    ["product_code", "TEXT", 0],
    ["barcode_type", "TEXT", 0],
    ["updated_at_iso", "TEXT", 0],
  ],
  catalog_prices: [
    ["snapshot_id", "TEXT", 1],
    ["price_id", "TEXT", 2],
    ["product_code", "TEXT", 0],
    ["price_cents", "INTEGER", 0],
    ["valid_from_iso", "TEXT", 0],
    ["valid_until_iso", "TEXT", 0],
    ["source_version", "TEXT", 0],
  ],
  catalog_promotions: currentCatalogSchema.catalog_promotions,
  special_products: [
    ["snapshot_id", "TEXT", 1],
    ["product_code", "TEXT", 2],
    ["sort_order", "INTEGER", 0],
    ["is_marked", "INTEGER", 0],
    ["updated_at_iso", "TEXT", 0],
  ],
} as const satisfies Record<
  CatalogTableName,
  readonly ExpectedSqliteColumn[]
>;

async function ensureCatalogSchemaForM17(
  transaction: SqliteConnectionPort,
): Promise<void> {
  const schema = await readCatalogSchema(transaction);
  if (
    matchesCatalogSchema(schema, currentCatalogSchema) ||
    matchesCatalogSchema(schema, m25CatalogSchema)
  ) {
    await assertCurrentCatalogSchema(transaction, schema);
    return;
  }
  const isKnownLegacySchema = [
    legacyM2SingleKeyCatalogSchema,
    legacyM2SnapshotKeyCatalogSchema,
  ].some((expected) => matchesCatalogSchema(schema, expected));
  if (!isKnownLegacySchema) {
    throw new Error("CATALOG_SCHEMA_INVALID:UNSUPPORTED_SHAPE");
  }

  // 这六张表只保存可重新下载的目录缓存。按外键子表到父表的顺序重建，
  // 且与 M17 和版本记录共用独占事务；后续任一步失败都会恢复旧 schema 和缓存。
  await transaction.exec(`
    DROP TABLE special_products;
    DROP TABLE catalog_barcodes;
    DROP TABLE catalog_prices;
    DROP TABLE catalog_promotions;
    DROP TABLE catalog_items;
    DROP TABLE catalog_snapshots;
    ${M2}
  `);
  await assertCurrentCatalogSchema(transaction);
}

/** 特殊商品本地表的权威列集（与 M17 建表保持一致，大小写不敏感比较）。 */
const localSpecialProductsColumns = [
  "store_code",
  "product_code",
  "reference_code",
  "item_number",
  "display_name",
  "barcode",
  "lookup_code",
  "retail_price_cents",
  "price_source",
  "quantity_factor",
  "product_image",
  "discount_rate",
  "sort_order",
] as const;

/** 与 M17 同构的建表与索引 DDL；仅用于表缺失时的幂等自愈，不迁移旧数据。 */
const localSpecialProductsDdl = `
CREATE TABLE IF NOT EXISTS local_special_products (
  store_code TEXT NOT NULL,
  product_code TEXT NOT NULL,
  reference_code TEXT NULL,
  item_number TEXT NULL,
  display_name TEXT NOT NULL,
  barcode TEXT NULL,
  lookup_code TEXT NOT NULL,
  retail_price_cents INTEGER NOT NULL CHECK (
    typeof(retail_price_cents) = 'integer'
  ),
  price_source INTEGER NOT NULL CHECK (
    typeof(price_source) = 'integer'
    AND price_source IN (0, 1, 2, 3, 4)
  ),
  quantity_factor TEXT NOT NULL,
  product_image TEXT NULL,
  discount_rate TEXT NULL,
  sort_order INTEGER NOT NULL CHECK (
    typeof(sort_order) = 'integer' AND sort_order >= 0
  ),
  PRIMARY KEY (store_code, product_code),
  UNIQUE (store_code, sort_order),
  CHECK (TRIM(store_code) <> '' AND LENGTH(store_code) <= 128),
  CHECK (TRIM(product_code) <> '' AND LENGTH(product_code) <= 128),
  CHECK (TRIM(display_name) <> '' AND LENGTH(display_name) <= 512),
  CHECK (TRIM(lookup_code) <> '' AND LENGTH(lookup_code) <= 256),
  CHECK (TRIM(quantity_factor) <> '' AND LENGTH(quantity_factor) <= 128)
);
CREATE INDEX IF NOT EXISTS ix_local_special_products_store_sort
  ON local_special_products (store_code, sort_order, product_code);
CREATE INDEX IF NOT EXISTS ix_local_special_products_store_search
  ON local_special_products (
    store_code, display_name COLLATE NOCASE,
    item_number COLLATE NOCASE, lookup_code COLLATE NOCASE
  );
`;

/**
 * 特殊商品本地表自愈：M17 已应用后，若 local_special_products 缺失则按当前
 * 权威 schema 幂等重建（本地特殊商品可从远程重新下载，重建不丢失业务价值）；
 * 若列不完整则抛出明确错误码，便于上层区分"表缺失"与"结构异常"。
 */
async function ensureLocalSpecialProductsSchema(
  transaction: SqliteConnectionPort,
): Promise<void> {
  const table = await transaction.getFirst<{ name: unknown }>(
    `SELECT name FROM sqlite_master
     WHERE type = 'table' AND name = 'local_special_products'`,
  );
  if (table === null) {
    await transaction.exec(localSpecialProductsDdl);
    return;
  }
  const columns = await transaction.getAll<{ name: unknown }>(
    "PRAGMA table_info('local_special_products')",
  );
  const present = new Set(
    columns
      .map((column) =>
        typeof column.name === "string" ? column.name.toLowerCase() : "",
      )
      .filter(Boolean),
  );
  const missing = localSpecialProductsColumns.filter(
    (name) => !present.has(name),
  );
  if (missing.length > 0) {
    throw new Error(
      `LOCAL_SPECIAL_PRODUCTS_SCHEMA_INVALID:MISSING_COLUMNS=${missing.join(",")}`,
    );
  }
}

async function assertCurrentCatalogSchema(
  transaction: SqliteConnectionPort,
  schema: CatalogSchemaSnapshot | null = null,
): Promise<void> {
  const actual = schema ?? (await readCatalogSchema(transaction));
  const isM25Schema = matchesCatalogSchema(actual, m25CatalogSchema);
  if (
    !matchesCatalogSchema(actual, currentCatalogSchema) &&
    !isM25Schema
  ) {
    throw new Error("CATALOG_SCHEMA_INVALID:CURRENT_SCHEMA_INCOMPLETE");
  }
  if (isM25Schema) {
    const deltaDeletionColumns = await readTableColumns(
      transaction,
      "catalog_delta_deletions",
    );
    if (
      !matchesTableColumns(deltaDeletionColumns, m25DeltaDeletionSchema)
    ) {
      throw new Error("CATALOG_SCHEMA_INVALID:M25_DELTA_DELETIONS_INCOMPLETE");
    }
  }
  const activeIndex = await transaction.getFirst<{ name: unknown }>(
    `SELECT name
     FROM sqlite_master
     WHERE type = 'index'
       AND name = 'ux_catalog_snapshots_single_active'
     LIMIT 1`,
  );
  if (activeIndex?.name !== "ux_catalog_snapshots_single_active") {
    throw new Error("CATALOG_SCHEMA_INVALID:ACTIVE_INDEX_MISSING");
  }
}

async function readCatalogSchema(
  transaction: SqliteConnectionPort,
): Promise<CatalogSchemaSnapshot> {
  const entries = await Promise.all(
    catalogTableNames.map(async (tableName) => [
      tableName,
      await readTableColumns(transaction, tableName),
    ] as const),
  );
  return Object.fromEntries(entries) as CatalogSchemaSnapshot;
}

function matchesCatalogSchema(
  actual: CatalogSchemaSnapshot,
  expected: Readonly<
    Record<CatalogTableName, readonly ExpectedSqliteColumn[]>
  >,
): boolean {
  return catalogTableNames.every((tableName) => {
    return matchesTableColumns(actual[tableName], expected[tableName]);
  });
}

function matchesTableColumns(
  actualColumns: readonly SqliteColumnInfo[],
  expectedColumns: readonly ExpectedSqliteColumn[],
): boolean {
  if (actualColumns.length !== expectedColumns.length) {
    return false;
  }
  const actualByName = new Map(
    actualColumns.map((column) => [column.name, column]),
  );
  return expectedColumns.every(([name, type, primaryKeyOrder]) => {
    const column = actualByName.get(name);
    return (
      normalizeSqliteType(column?.type) === type &&
      Number(column?.pk) === primaryKeyOrder
    );
  });
}

const requiredDrawerColumns = [
  ["event_id", "TEXT"],
  ["order_guid", "TEXT"],
  ["printer_id", "TEXT"],
  ["print_job_id", "TEXT"],
  ["state", "TEXT"],
  ["reason", "TEXT"],
  ["retry_count", "INTEGER"],
  ["requested_at_iso", "TEXT"],
  ["completed_at_iso", "TEXT"],
  ["last_error_code", "TEXT"],
  ["created_at_iso", "TEXT"],
  ["updated_at_iso", "TEXT"],
] as const;

async function assertDrawerEventsSchema(
  transaction: SqliteConnectionPort,
): Promise<void> {
  const columns = await readTableColumns(transaction, "drawer_events");
  const byName = new Map(columns.map((column) => [column.name, column]));
  for (const [name, expectedType] of requiredDrawerColumns) {
    const column = byName.get(name);
    if (!column) {
      throw new Error(`DRAWER_EVENTS_SCHEMA_INVALID:MISSING_${name}`);
    }
    if (normalizeSqliteType(column.type) !== expectedType) {
      throw new Error(`DRAWER_EVENTS_SCHEMA_INVALID:TYPE_${name}`);
    }
  }
  if (Number(byName.get("event_id")?.pk) !== 1) {
    throw new Error("DRAWER_EVENTS_SCHEMA_INVALID:EVENT_ID_PRIMARY_KEY");
  }
}

async function readTableColumns(
  transaction: SqliteConnectionPort,
  tableName:
    | "drawer_events"
    | "print_jobs"
    | "catalog_delta_deletions"
    | CatalogTableName,
): Promise<readonly SqliteColumnInfo[]> {
  const rows = await transaction.getAll<{
    name: unknown;
    type: unknown;
    pk: unknown;
  }>(`PRAGMA table_info('${tableName}')`);
  return rows.map((row) => ({
    name: typeof row.name === "string" ? row.name : "",
    type: row.type,
    pk: row.pk,
  }));
}

function normalizeSqliteType(value: unknown): string {
  return typeof value === "string" ? value.trim().toUpperCase() : "";
}
