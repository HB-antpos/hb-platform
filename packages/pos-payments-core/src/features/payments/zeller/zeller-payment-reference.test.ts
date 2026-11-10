import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  ZELLER_PAYMENT_PREFIX,
  ZELLER_REFUND_PREFIX,
  createZellerPurchaseReference,
  createZellerRefundReference,
  deriveZellerAttemptReference,
  formatZellerPayment,
  formatZellerRefund,
  formatZellerUnverifiedPayment,
  formatZellerUnverifiedRefund,
  isUnverifiedZellerReference,
  isZellerReference,
  parseZellerPayment,
  parseZellerRefund,
} from "./zeller-payment-reference";

type FormatKind = "payment" | "refund" | "unverifiedPayment" | "unverifiedRefund";

type Vectors = Readonly<{
  attempts: readonly { attemptGuid: string; purchaseReference: string; refundReference: string }[];
  format: readonly {
    kind: FormatKind;
    reference: string;
    transactionUuid: string | null;
    formatted: string;
  }[];
  formatRejects: readonly { kind: FormatKind; reference: string; transactionUuid: string | null }[];
  parse: readonly {
    kind: "payment" | "refund";
    input: string | null;
    parts: { reference: string; transactionUuid: string } | null;
  }[];
  isZeller: readonly { input: string | null; expected: boolean }[];
  isUnverified: readonly { input: string | null; expected: boolean }[];
}>;

function loadVectors(): Vectors {
  const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "..", "..", "..");
  const file = join(root, "test-fixtures", "zeller", "payment-reference-vectors.json");
  return JSON.parse(readFileSync(file, "utf8")) as Vectors;
}

const vectors = loadVectors();

function format(kind: FormatKind, reference: string, transactionUuid: string | null): string {
  switch (kind) {
    case "payment":
      return formatZellerPayment(reference, transactionUuid ?? "");
    case "refund":
      return formatZellerRefund(reference, transactionUuid ?? "");
    case "unverifiedPayment":
      return formatZellerUnverifiedPayment(reference);
    case "unverifiedRefund":
      return formatZellerUnverifiedRefund(reference);
  }
}

test("reference 由 attempt 派生：HBZ- / HBZR- 加 32 位小写十六进制（共用向量）", () => {
  assert.ok(vectors.attempts.length >= 4);
  for (const vector of vectors.attempts) {
    const purchase = createZellerPurchaseReference(vector.attemptGuid);
    const refund = createZellerRefundReference(vector.attemptGuid);
    assert.equal(purchase, vector.purchaseReference, vector.attemptGuid);
    assert.equal(refund, vector.refundReference, vector.attemptGuid);
    assert.equal(purchase.length, 36);
    assert.equal(refund.length, 37);
  }
});

test("没有真实 attempt 就不能生成 reference", () => {
  for (const invalid of ["", "not-a-guid", "00000000-0000-0000-0000-000000000000", "attempt-1"]) {
    assert.throws(() => createZellerPurchaseReference(invalid), /ZELLER_ATTEMPT_GUID_INVALID/u);
    assert.throws(() => createZellerRefundReference(invalid), /ZELLER_ATTEMPT_GUID_INVALID/u);
  }
});

test("由 attempt 身份派生 reference：扣款 HBZ-、退款 HBZR-；不是 GUID 时为 null", () => {
  const attemptId = "799287A3-3DC2-4DF1-BCA3-D33EEA19B35A";
  assert.equal(
    deriveZellerAttemptReference({ attemptId, operation: "purchase" }),
    "HBZ-799287a33dc24df1bca3d33eea19b35a",
  );
  assert.equal(
    deriveZellerAttemptReference({ attemptId, operation: "refund" }),
    "HBZR-799287a33dc24df1bca3d33eea19b35a",
  );
  // 派生结果与 createZeller*Reference 一致，并且能被订单引用格式接受。
  assert.equal(
    deriveZellerAttemptReference({ attemptId, operation: "purchase" }),
    createZellerPurchaseReference(attemptId),
  );
  for (const invalid of ["attempt-1", "", "00000000-0000-0000-0000-000000000000"]) {
    assert.equal(deriveZellerAttemptReference({ attemptId: invalid, operation: "purchase" }), null);
  }
});

test("不同 attempt 的 reference 一定不同（Zeller 不去重，复用会重复扣款）", () => {
  assert.notEqual(
    createZellerPurchaseReference("799287a3-3dc2-4df1-bca3-d33eea19b35a"),
    createZellerPurchaseReference("799287a3-3dc2-4df1-bca3-d33eea19b35b"),
  );
});

test("订单引用的格式化与 C# 逐字一致（共用向量）", () => {
  assert.ok(vectors.format.length >= 5);
  for (const vector of vectors.format) {
    assert.equal(
      format(vector.kind, vector.reference, vector.transactionUuid),
      vector.formatted,
      `${vector.kind}|${vector.reference}`,
    );
  }
});

test("格式化拒绝种类不对的 reference 和缺失的 transactionUuid（共用向量）", () => {
  assert.ok(vectors.formatRejects.length >= 10);
  for (const vector of vectors.formatRejects) {
    assert.throws(
      () => format(vector.kind, vector.reference, vector.transactionUuid),
      /ZELLER_(REFERENCE|TRANSACTION_UUID)_INVALID/u,
      `${vector.kind}|${vector.reference}|${vector.transactionUuid}`,
    );
  }
});

test("解析订单引用：只认格式完整的 Zeller 引用（共用向量）", () => {
  assert.ok(vectors.parse.length >= 15);
  for (const vector of vectors.parse) {
    const parsed =
      vector.kind === "payment" ? parseZellerPayment(vector.input) : parseZellerRefund(vector.input);
    assert.deepEqual(parsed, vector.parts, `${vector.kind}|${vector.input}`);
  }
});

test("格式化后的引用能原样解析回来", () => {
  const purchase = createZellerPurchaseReference("799287a3-3dc2-4df1-bca3-d33eea19b35a");
  const uuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c";
  assert.deepEqual(parseZellerPayment(formatZellerPayment(purchase, uuid)), {
    reference: purchase,
    transactionUuid: uuid,
  });
});

test("损坏的 Zeller 引用仍然算 Zeller，不能回退到别的刷卡通道（共用向量）", () => {
  for (const vector of vectors.isZeller) {
    assert.equal(isZellerReference(vector.input), vector.expected, String(vector.input));
  }
  assert.equal(isZellerReference(undefined), false);
  assert.equal(isZellerReference(42), false);
});

test("只有格式完整、没有 transactionUuid 的引用才算未核实（共用向量）", () => {
  for (const vector of vectors.isUnverified) {
    assert.equal(isUnverifiedZellerReference(vector.input), vector.expected, String(vector.input));
  }
});

test("Zeller 前缀不会被现有的 SQ / ANZ / MANUAL 前缀判断误认", () => {
  for (const prefix of [ZELLER_PAYMENT_PREFIX, ZELLER_REFUND_PREFIX]) {
    assert.equal(/^(SQ|ANZ|MANUAL)/iu.test(prefix), false);
  }
});
