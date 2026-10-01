import assert from "node:assert/strict";
import test from "node:test";
import { DEFAULT_PAYMENT_METHOD_SETTINGS, normalizePaymentMethodSettings, PaymentMethodSettingsRepository } from "./payment-method-settings";

test("手动刷卡默认关闭且损坏配置安全回退", () => {
  for (const value of [undefined, null, [], {}, { useManualCard: "true" }, { useManualCard: true, cardNumber: "secret" }]) {
    assert.deepEqual(normalizePaymentMethodSettings(value), DEFAULT_PAYMENT_METHOD_SETTINGS);
  }
  assert.deepEqual(normalizePaymentMethodSettings({ useManualCard: true }), { useManualCard: true });
});

test("手动刷卡开关可保存、重载并明确关闭", async () => {
  const rows = new Map<string, string>();
  const db: any = {
    getFirst: async (_sql: string, args: string[]) => rows.has(String(args[0])) ? { setting_value: rows.get(String(args[0])) } : undefined,
    withExclusiveTransaction: async (fn: (tx: any) => Promise<void>) => fn({ run: async (_sql: string, args: string[]) => rows.set(String(args[0]), String(args[1])) }),
  };
  const create = () => new PaymentMethodSettingsRepository(db, () => "2026-09-29T00:00:00Z");
  assert.deepEqual(await create().load(), DEFAULT_PAYMENT_METHOD_SETTINGS);
  await create().save({ useManualCard: true });
  assert.deepEqual(await create().load(), { useManualCard: true });
  await create().save({ useManualCard: false });
  assert.deepEqual(await create().load(), { useManualCard: false });
});
