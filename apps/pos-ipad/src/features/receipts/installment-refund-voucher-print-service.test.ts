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
