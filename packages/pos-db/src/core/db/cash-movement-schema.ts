/**
 * 现金存入 / 取出（Cash In / Out）的本地库结构。DDL 作为共享常量放在这里，
 * iPad（M61）与手持（M58）的 migrations.ts 引用同一份，保证两端字节一致。
 *
 * 1. local_cash_movements：每笔一行，写入后不可改（除上传状态列）、不可删。
 *    CHECK 约束把服务端会永久拒绝的情况挡在库里：金额 1..100,000,000 分、方向与原因码自然方向一致
 *    （冲正相反）、OtherIn / PaidOut 普通记录备注 >= 2 字。
 *    voids_movement_guid 带 UNIQUE：每条原记录只能被冲正一次（兜底，应用层也会先查）。
 * 2. 冲正插入守卫触发器：原记录必须存在、本身不是冲正、同原因码同金额反方向、同店同设备同营业日，
 *    且该营业日没有在原记录发生之后（含同一时刻）保存过日结。
 * 3. local_daily_closes 新增 cash_in_cents / cash_out_cents（旧存档 DEFAULT 0，口径不变，且不可变，不回填）。
 * 4. 日结归档触发器必须 DROP 后重建：不可变清单加入新列；归档转换里的应有现金公式
 *    「订单现金净额 + 分期现金净额 + 进位」改成 「… + cash_in − cash_out」，其余条件与 M56 原样保留。
 * 整个迁移在 applyMigrations 的独占事务内执行，要么全成功要么全回滚；不可回退。
 */
export const CASH_MOVEMENT_TABLE_SQL = `
CREATE TABLE local_cash_movements (
  movement_guid TEXT PRIMARY KEY,
  direction TEXT NOT NULL CHECK (direction IN ('In', 'Out')),
  reason_code TEXT NOT NULL CHECK (
    reason_code IN ('ChangeTopUp', 'OtherIn', 'SafeDrop', 'PaidOut')
  ),
  amount_cents INTEGER NOT NULL CHECK (
    typeof(amount_cents) = 'integer'
    AND amount_cents > 0
    AND amount_cents <= 100000000
  ),
  note TEXT NULL,
  voids_movement_guid TEXT NULL UNIQUE
    REFERENCES local_cash_movements(movement_guid) ON DELETE RESTRICT,
  business_date TEXT NOT NULL,
  occurred_at_iso TEXT NOT NULL,
  cashier_id TEXT NOT NULL,
  cashier_name TEXT NOT NULL,
  authorized_by_id TEXT NULL,
  authorized_by_name TEXT NULL,
  display_number TEXT NOT NULL UNIQUE,
  store_code TEXT NOT NULL,
  device_code TEXT NOT NULL,
  audit_event_id TEXT NOT NULL UNIQUE
    REFERENCES audit_events(event_id) ON DELETE RESTRICT,
  upload_state TEXT NOT NULL DEFAULT 'pending'
    CHECK (upload_state IN ('pending', 'uploading', 'synced', 'rejected')),
  upload_attempts INTEGER NOT NULL DEFAULT 0
    CHECK (typeof(upload_attempts) = 'integer' AND upload_attempts >= 0),
  next_attempt_at_iso TEXT NULL,
  last_attempt_at_iso TEXT NULL,
  lease_expires_at_iso TEXT NULL,
  upload_error_code TEXT NULL,
  upload_error_message TEXT NULL,
  uploaded_at_iso TEXT NULL,
  created_at_iso TEXT NOT NULL,
  CHECK (TRIM(movement_guid) <> '' AND LENGTH(movement_guid) <= 64),
  CHECK (
    business_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
  ),
  CHECK (TRIM(occurred_at_iso) <> '' AND LENGTH(occurred_at_iso) <= 64),
  CHECK (TRIM(cashier_id) <> '' AND LENGTH(cashier_id) <= 64),
  CHECK (TRIM(cashier_name) <> '' AND LENGTH(cashier_name) <= 128),
  CHECK (
    (authorized_by_id IS NULL AND authorized_by_name IS NULL)
    OR (
      authorized_by_id IS NOT NULL AND authorized_by_name IS NOT NULL
      AND LENGTH(authorized_by_id) <= 64 AND LENGTH(authorized_by_name) <= 128
    )
  ),
  CHECK (LENGTH(display_number) <= 32),
  CHECK (TRIM(store_code) <> '' AND LENGTH(store_code) <= 128),
  CHECK (TRIM(device_code) <> '' AND LENGTH(device_code) <= 128),
  CHECK (note IS NULL OR (LENGTH(note) <= 200 AND LENGTH(TRIM(note)) > 0)),
  CHECK (voids_movement_guid IS NULL OR voids_movement_guid <> movement_guid),
  -- 普通记录方向 = 原因码的自然方向；冲正记录方向相反。
  CHECK (
    (
      voids_movement_guid IS NULL
      AND (
        (reason_code IN ('ChangeTopUp', 'OtherIn') AND direction = 'In')
        OR (reason_code IN ('SafeDrop', 'PaidOut') AND direction = 'Out')
      )
    )
    OR (
      voids_movement_guid IS NOT NULL
      AND (
        (reason_code IN ('ChangeTopUp', 'OtherIn') AND direction = 'Out')
        OR (reason_code IN ('SafeDrop', 'PaidOut') AND direction = 'In')
      )
    )
  ),
  -- OtherIn / PaidOut 的普通记录备注至少 2 字（服务端 NOTE_REQUIRED）。
  CHECK (
    voids_movement_guid IS NOT NULL
    OR reason_code NOT IN ('OtherIn', 'PaidOut')
    OR COALESCE(LENGTH(TRIM(note)), 0) >= 2
  )
);

CREATE INDEX ix_local_cash_movements_scope_time
  ON local_cash_movements (store_code, device_code, occurred_at_iso);
CREATE INDEX ix_local_cash_movements_upload_due
  ON local_cash_movements (
    upload_state, store_code, device_code, next_attempt_at_iso, occurred_at_iso
  );

-- 记录写入后不可改：只有上传状态列可以 UPDATE；一律不可删除。
CREATE TRIGGER trg_cash_movement_immutable
BEFORE UPDATE OF
  movement_guid, direction, reason_code, amount_cents, note,
  voids_movement_guid, business_date, occurred_at_iso,
  cashier_id, cashier_name, authorized_by_id, authorized_by_name,
  display_number, store_code, device_code, audit_event_id, created_at_iso
ON local_cash_movements
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'CASH_MOVEMENT_IMMUTABLE');
END;

CREATE TRIGGER trg_cash_movement_no_delete
BEFORE DELETE ON local_cash_movements
FOR EACH ROW
BEGIN
  SELECT RAISE(ABORT, 'CASH_MOVEMENT_DELETE_FORBIDDEN');
END;

-- 冲正守卫（服务端不校验这些规则，客户端必须自己守）：原记录存在、不是冲正、同原因码同金额反方向、
-- 同店同设备同营业日。
CREATE TRIGGER trg_cash_movement_void_insert_guard
BEFORE INSERT ON local_cash_movements
FOR EACH ROW
WHEN NEW.voids_movement_guid IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM local_cash_movements original
    WHERE original.movement_guid = NEW.voids_movement_guid
      AND original.voids_movement_guid IS NULL
      AND original.reason_code = NEW.reason_code
      AND original.amount_cents = NEW.amount_cents
      AND original.direction <> NEW.direction
      AND original.store_code = NEW.store_code
      AND original.device_code = NEW.device_code
      AND original.business_date = NEW.business_date
  )
BEGIN
  SELECT RAISE(ABORT, 'CASH_MOVEMENT_VOID_INVALID');
END;

-- 同营业日在原记录发生之后（含同一时刻）已保存过日结：该日结已经把原记录算进去，不能再冲正。
CREATE TRIGGER trg_cash_movement_void_after_close_guard
BEFORE INSERT ON local_cash_movements
FOR EACH ROW
WHEN NEW.voids_movement_guid IS NOT NULL
  AND EXISTS (
    SELECT 1
    FROM local_cash_movements original
    INNER JOIN local_daily_closes closes
      ON closes.store_code = NEW.store_code
      AND closes.device_code = NEW.device_code
      AND closes.business_date = NEW.business_date
      AND closes.state = 'Archived'
    WHERE original.movement_guid = NEW.voids_movement_guid
      AND julianday(closes.saved_at_iso) >= julianday(original.occurred_at_iso)
  )
BEGIN
  SELECT RAISE(ABORT, 'CASH_MOVEMENT_VOID_AFTER_DAILY_CLOSE');
END;
`;

/** 日结存档新增两列，并重建不可变 / 归档转换触发器（应有现金公式带存入与取出）。 */
export const CASH_MOVEMENT_DAILY_CLOSE_SQL = `
ALTER TABLE local_daily_closes ADD COLUMN cash_in_cents INTEGER NOT NULL DEFAULT 0
  CHECK (typeof(cash_in_cents) = 'integer' AND cash_in_cents >= 0);
ALTER TABLE local_daily_closes ADD COLUMN cash_out_cents INTEGER NOT NULL DEFAULT 0
  CHECK (typeof(cash_out_cents) = 'integer' AND cash_out_cents >= 0);

DROP TRIGGER trg_daily_close_archive_immutable;
DROP TRIGGER trg_daily_close_archive_transition;

CREATE TRIGGER trg_daily_close_archive_immutable
BEFORE UPDATE OF
  close_id, business_date, period_from_iso, period_to_iso,
  store_code, device_code, saved_cashier_id, saved_cashier_name,
  order_count, return_quantity, expected_cash_cents, counted_cash_cents,
  notes_subtotal_cents, coins_subtotal_cents, variance_cents,
  terminal_audit_event_id, saved_at_iso, source_kind, cash_rounding_cents,
  installment_payment_count,
  installment_cash_sales_cents, installment_cash_refund_cents,
  installment_card_sales_cents, installment_card_refund_cents,
  installment_voucher_sales_cents, installment_voucher_refund_cents,
  cash_in_cents, cash_out_cents
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
  ) + NEW.installment_cash_sales_cents + NEW.installment_cash_refund_cents
    + NEW.cash_rounding_cents + NEW.cash_in_cents - NEW.cash_out_cents
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
`;

/** 两端迁移的完整 SQL：先建明细表（触发器引用 local_daily_closes，已存在），再改日结存档。 */
export const CASH_MOVEMENT_MIGRATION_SQL = `${CASH_MOVEMENT_TABLE_SQL}
${CASH_MOVEMENT_DAILY_CLOSE_SQL}`;
