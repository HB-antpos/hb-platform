import assert from "node:assert/strict";
import test from "node:test";

import type { SqliteConnectionPort, SqlValue } from "@hb/pos-db/core/db/types";

import { DEFAULT_PAYMENT_METHOD_SETTINGS, PaymentMethodSettingsRepository, normalizePaymentMethodSettings } from "./payment-method-settings";

test("损坏、旧配置或未知字段均保持手动刷卡和购物券关闭", () => {
  for (const value of [undefined, null, [], {}, { useManualCard: "true", giftCardEnabled: true }, { useManualCard: true, giftCardEnabled: true, cardNumber: "secret" }]) {
    assert.deepEqual(normalizePaymentMethodSettings(value), DEFAULT_PAYMENT_METHOD_SETTINGS);
  }
});

test("支付方式使用独立持久化键并能重载，关闭手动不清除购物券或provider", async () => {
  const values = new Map<string, unknown>([["payment_provider_v1", "linkly"]]);
  let failed = false;
  const db: SqliteConnectionPort = {
    exec: async () => undefined,
    close: async () => undefined,
    getAll: async () => [],
    getFirst: async <T extends object>(_sql: string, args: readonly SqlValue[] = []) => {
      const value = values.get(String(args[0]));
      return value === undefined ? null : { setting_value: value } as T;
    },
    run: async (_sql, args = []) => {
      if (failed) throw new Error("disk unavailable");
      values.set(String(args[0]), args[1]);
      return { changes: 1, lastInsertRowId: 0 };
    },
    withExclusiveTransaction: (operation) => operation(db),
  };
  const create = () => new PaymentMethodSettingsRepository(db, () => "2026-09-15T00:00:00Z");
  assert.deepEqual(await create().load(), DEFAULT_PAYMENT_METHOD_SETTINGS);
  await create().save({ useManualCard: true, giftCardEnabled: true });
  assert.deepEqual(await create().load(), { useManualCard: true, giftCardEnabled: true });
  await create().save({ useManualCard: false, giftCardEnabled: true });
  assert.deepEqual(await create().load(), { useManualCard: false, giftCardEnabled: true });
  assert.equal(values.get("payment_provider_v1"), "linkly");
  failed = true;
  await assert.rejects(create().save({ useManualCard: true, giftCardEnabled: false }));
  assert.deepEqual(await create().load(), { useManualCard: false, giftCardEnabled: true });
  values.set("payment_methods_v1", "bad-json");
  assert.deepEqual(await create().load(), DEFAULT_PAYMENT_METHOD_SETTINGS);
});
