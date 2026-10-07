import assert from "node:assert/strict";
import test from "node:test";

import { SqliteInstallmentRefundVoucherPrintMaterial } from "@/core/db/sqlite-installment-refund-voucher-print-material";
import type { VoucherProtectedAttemptState } from "@/features/payments/voucher/voucher-payment-adapter";

import { InstallmentRefundVoucherPrintService } from "./installment-refund-voucher-print-service";

const INSTALLMENT_GUID = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d77";
const decoder = new TextDecoder();

function voucherState(
  patch: Partial<VoucherProtectedAttemptState> = {},
): VoucherProtectedAttemptState {
  return {
    protectedReference: "vpr_1",
    attemptId: "attempt-1",
    idempotencyKey: "op:refund:payment-1",
    orderGuid: INSTALLMENT_GUID,
    operation: "refund",
    phase: "approved",
    storeCode: "S001",
    cashierId: "cashier-1",
    voucherCode: "RF-1",
    reservationToken: null,
    amountCents: -1_000,
    expiresAtIso: null,
    ...patch,
  };
}

test("退款券材料只取已完成取消退款的已批准退款券，金额转正", async () => {
  const queries: (readonly unknown[])[] = [];
  const states = new Map<string, VoucherProtectedAttemptState>([
    ["vpr_1", voucherState()],
    ["vpr_2", voucherState({ protectedReference: "vpr_2", voucherCode: "RF-2", amountCents: -250 })],
    // 未批准、非退款、其他门店的 state 不出券面。
    ["vpr_3", voucherState({ protectedReference: "vpr_3", phase: "refund-submitted" as VoucherProtectedAttemptState["phase"] })],
    ["vpr_4", voucherState({ protectedReference: "vpr_4", operation: "purchase" })],
    ["vpr_5", voucherState({ protectedReference: "vpr_5", storeCode: "S002" })],
  ]);
  const material = new SqliteInstallmentRefundVoucherPrintMaterial(
    {
      async getAll<T extends object>(sql: string, parameters: readonly unknown[] = []) {
        assert.match(sql, /action_kind = 'cancel-refund'/u);
        assert.match(sql, /resolution = 'Completed'/u);
        queries.push(parameters);
        return [...states.keys()].map((key) => ({ protected_reference: key })) as unknown as T[];
      },
    },
    {
      async resolve(reference) {
        return states.get(reference) ?? null;
      },
    },
  );

  const vouchers = await material.listApprovedRefundVouchers(INSTALLMENT_GUID, "S001");

  assert.deepEqual(queries, [[INSTALLMENT_GUID, "S001"]]);
  assert.deepEqual(vouchers, [
    { voucherCode: "RF-1", amountCents: 1_000 },
    { voucherCode: "RF-2", amountCents: 250 },
  ]);
});

test("取消后逐张出券面并按分期单派生的任务号幂等入队；无券或未启用打印时不入队", async () => {
  const enqueued: { jobId: string; installmentGuid: string; printerId: string; receiptBytes: Uint8Array }[] = [];
  let drains = 0;
  let vouchers: readonly { voucherCode: string; amountCents: number }[] = [
    { voucherCode: "RF-A", amountCents: 1_000 },
    { voucherCode: "RF-B", amountCents: 500 },
  ];
  let printEnabled = true;
  const service = new InstallmentRefundVoucherPrintService({
    materials: {
      async listApprovedRefundVouchers(installmentGuid, storeCode) {
        assert.equal(installmentGuid, INSTALLMENT_GUID);
        assert.equal(storeCode, "S001");
        return vouchers;
      },
    },
    settings: {
      async getFrozenReturnReceiptSettings() {
        return printEnabled
          ? {
              printerId: "printer-1",
              paper: "80mm",
              locale: "en",
              store: {
                brandName: "Hot Bargain",
                storeName: "Main",
                address: "",
                phone: "",
                abn: "",
                returnPolicy: "",
              },
            }
          : null;
      },
    },
    printQueue: {
      async enqueueInstallmentRefundVoucherPrintJob(input) {
        enqueued.push(input);
        return enqueued.length === 1 ? "created" : "existing";
      },
    },
    trustedStoreCode: "S001",
    now: () => new Date(2026, 9, 6, 10, 0, 0),
    async requestPrintDrain() {
      drains += 1;
    },
  });

  assert.equal(await service.printAfterCancel(INSTALLMENT_GUID, "IP-S001-1"), "queued");
  const job = enqueued[0]!;
  assert.equal(job.jobId, `installment-refund-voucher:${INSTALLMENT_GUID}`);
  assert.equal(job.printerId, "printer-1");
  const text = decoder.decode(job.receiptBytes);
  assert.match(text, /Voucher: RF-A/u);
  assert.match(text, /Amount: \$10\.00/u);
  assert.match(text, /Voucher: RF-B/u);
  assert.match(text, /Order: IP-S001-1/u);
  assert.equal(drains, 1);

  assert.equal(await service.printAfterCancel(INSTALLMENT_GUID, "IP-S001-1"), "existing");

  vouchers = [];
  assert.equal(await service.printAfterCancel(INSTALLMENT_GUID, "IP-S001-1"), "none");
  vouchers = [{ voucherCode: "RF-A", amountCents: 1_000 }];
  printEnabled = false;
  assert.equal(await service.printAfterCancel(INSTALLMENT_GUID, "IP-S001-1"), "none");
  assert.equal(enqueued.length, 2);
});

// 门店当地（悉尼夏令时 UTC+11）2027-01-05 23:59:59 = 12:59:59Z，服务端新规则（发券日 + 90 天、取整到当天结束）的典型到期时刻。
const NEW_RULE_EXPIRY = "2027-01-05T12:59:59.000Z";
// 旧规则（12 个月）发出的历史券，到期时刻是任意时刻。
const OLD_RULE_EXPIRY = "2027-03-09T04:17:31.000Z";

test("退款券材料带上这张券自己的到期时刻；null、缺失或格式异常时只是不带，仍照常出券", async () => {
  const states = new Map<string, VoucherProtectedAttemptState>([
    ["vpr_1", voucherState({ protectedReference: "vpr_1", voucherCode: "RF-NEW", expiresAtIso: NEW_RULE_EXPIRY })],
    ["vpr_2", voucherState({ protectedReference: "vpr_2", voucherCode: "RF-OLD", amountCents: -250, expiresAtIso: OLD_RULE_EXPIRY })],
    ["vpr_3", voucherState({ protectedReference: "vpr_3", voucherCode: "RF-NULL", amountCents: -300, expiresAtIso: null })],
    ["vpr_4", voucherState({ protectedReference: "vpr_4", voucherCode: "RF-BAD", amountCents: -400, expiresAtIso: "not-a-date" })],
    ["vpr_5", voucherState({ protectedReference: "vpr_5", voucherCode: "RF-OFFSET", amountCents: -500, expiresAtIso: "2027-01-05T12:59:59+00:00" })],
  ]);
  const material = new SqliteInstallmentRefundVoucherPrintMaterial(
    {
      async getAll<T extends object>() {
        return [...states.keys()].map((key) => ({ protected_reference: key })) as unknown as T[];
      },
    },
    {
      async resolve(reference) {
        return states.get(reference) ?? null;
      },
    },
  );

  assert.deepEqual(await material.listApprovedRefundVouchers(INSTALLMENT_GUID, "S001"), [
    { voucherCode: "RF-NEW", amountCents: 1_000, expiresAtIso: NEW_RULE_EXPIRY },
    { voucherCode: "RF-OLD", amountCents: 250, expiresAtIso: OLD_RULE_EXPIRY },
    { voucherCode: "RF-NULL", amountCents: 300 },
    { voucherCode: "RF-BAD", amountCents: 400 },
    { voucherCode: "RF-OFFSET", amountCents: 500 },
  ]);
});

function printService(input: Readonly<{
  vouchers: readonly { voucherCode: string; amountCents: number; expiresAtIso?: string | null }[];
  businessTimeZone?: string;
  enqueued: { jobId: string; receiptBytes: Uint8Array }[];
}>): InstallmentRefundVoucherPrintService {
  return new InstallmentRefundVoucherPrintService({
    materials: {
      async listApprovedRefundVouchers() {
        return input.vouchers;
      },
    },
    settings: {
      async getFrozenReturnReceiptSettings() {
        return {
          printerId: "printer-1",
          paper: "58mm",
          locale: "en",
          store: { brandName: "Hot Bargain", storeName: "Main", address: "", phone: "", abn: "", returnPolicy: "" },
        };
      },
    },
    printQueue: {
      async enqueueInstallmentRefundVoucherPrintJob(job) {
        input.enqueued.push(job);
        return input.enqueued.length === 1 ? "created" : "existing";
      },
    },
    trustedStoreCode: "S001",
    now: () => new Date(2026, 9, 6, 10, 0, 0),
    async requestPrintDrain() {},
    ...(input.businessTimeZone !== undefined ? { businessTimeZone: input.businessTimeZone } : {}),
  });
}

test("分期取消后打印：每张券印自己的 Valid until（新旧规则各异），并带使用说明，任务号与幂等不变", async () => {
  const enqueued: { jobId: string; receiptBytes: Uint8Array }[] = [];
  const service = printService({
    vouchers: [
      { voucherCode: "RF-NEW", amountCents: 1_000, expiresAtIso: NEW_RULE_EXPIRY },
      { voucherCode: "RF-OLD", amountCents: 500, expiresAtIso: OLD_RULE_EXPIRY },
    ],
    businessTimeZone: "Australia/Brisbane",
    enqueued,
  });

  assert.equal(await service.printAfterCancel(INSTALLMENT_GUID, "IP-S001-1"), "queued");
  assert.equal(enqueued[0]!.jobId, `installment-refund-voucher:${INSTALLMENT_GUID}`);
  const text = decoder.decode(enqueued[0]!.receiptBytes);
  const newIndex = text.indexOf("Voucher: RF-NEW");
  const oldIndex = text.indexOf("Voucher: RF-OLD");

  assert.ok(newIndex >= 0 && oldIndex > newIndex);
  assert.ok(text.indexOf("Valid until: 2027-01-05") > newIndex && text.indexOf("Valid until: 2027-01-05") < oldIndex);
  // 04:17:31Z 在布里斯班是 14:17，仍是 3 月 9 日。
  assert.ok(text.indexOf("Valid until: 2027-03-09") > oldIndex);
  assert.equal(text.match(/VOUCHER TERMS/gu)?.length, 2);
  assert.match(text, /Not redeemable for cash\./u);

  // 同一任务号重放仍视为已入队，不比对新字节、不重复出票。
  assert.equal(await service.printAfterCancel(INSTALLMENT_GUID, "IP-S001-1"), "existing");
});

test("分期取消后打印：到期日缺失或 businessTimeZone 缺省/非法时只省略 Valid until，出票与入队照常", async () => {
  const bytesFor = async (
    expiresAtIso: string | null | undefined,
    businessTimeZone: string | undefined,
  ): Promise<string> => {
    const enqueued: { jobId: string; receiptBytes: Uint8Array }[] = [];
    const service = printService({
      vouchers: [{ voucherCode: "RF-1", amountCents: 1_000, ...(expiresAtIso !== undefined ? { expiresAtIso } : {}) }],
      ...(businessTimeZone !== undefined ? { businessTimeZone } : {}),
      enqueued,
    });
    assert.equal(await service.printAfterCancel(INSTALLMENT_GUID, "IP-S001-1"), "queued");
    return decoder.decode(enqueued[0]!.receiptBytes);
  };

  for (const missing of [undefined, null, "not-a-date"]) {
    const text = await bytesFor(missing, "Australia/Brisbane");
    assert.doesNotMatch(text, /Valid until/u, `expiresAtIso=${String(missing)}`);
    assert.match(text, /Voucher: RF-1/u);
    assert.match(text, /VOUCHER TERMS/u);
  }
  // 时区缺省沿用 Australia/Brisbane；非法时区名不抛错，只省略到期行。
  assert.match(await bytesFor(NEW_RULE_EXPIRY, undefined), /Valid until: 2027-01-05/u);
  assert.match(await bytesFor(NEW_RULE_EXPIRY, "Pacific/Auckland"), /Valid until: 2027-01-06/u);
  const invalidZone = await bytesFor(NEW_RULE_EXPIRY, "Not/A_Zone");
  assert.doesNotMatch(invalidZone, /Valid until/u);
  assert.match(invalidZone, /VOUCHER TERMS/u);
});
