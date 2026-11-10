import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  classifyZellerCall,
  classifyZellerLateRecheck,
  classifyZellerVerification,
  type ZellerAttemptExpectation,
  type ZellerCallDecision,
  type ZellerFailureKind,
  type ZellerUnknownReason,
  type ZellerVerificationClassification,
  type ZellerVerificationDecision,
} from "./zeller-result-classifier";
import {
  ZELLER_ERROR_TYPE_CODES,
  zellerErrorTypeFromSdkCode,
  type ZellerCallResult,
  type ZellerQueryResult,
} from "./zeller-terminal-models";

type VerificationExpected =
  | Readonly<{ throws: true }>
  | Readonly<{
      decision: ZellerVerificationDecision;
      unknownReason: ZellerUnknownReason;
      transactionIndex: number | null;
    }>;

type Vectors = Readonly<{
  call: readonly {
    name: string;
    expectation: ZellerAttemptExpectation;
    result: ZellerCallResult;
    expected: {
      decision: ZellerCallDecision;
      failureKind: ZellerFailureKind;
      unknownReason: ZellerUnknownReason;
      rebuildTerminalFirst: boolean;
      serialGateBreached: boolean;
      returnsTransaction: boolean;
    };
  }[];
  verification: readonly {
    name: string;
    pending: ZellerCallDecision;
    expectation: ZellerAttemptExpectation;
    query: ZellerQueryResult;
    expected: VerificationExpected;
  }[];
  lateRecheck: readonly {
    name: string;
    expectation: ZellerAttemptExpectation;
    query: ZellerQueryResult;
    expected: VerificationExpected;
  }[];
}>;

function loadVectors(): Vectors {
  const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "..", "..", "..");
  const file = join(root, "test-fixtures", "zeller", "result-classifier-vectors.json");
  return JSON.parse(readFileSync(file, "utf8")) as Vectors;
}

const vectors = loadVectors();

function assertVerification(
  actual: ZellerVerificationClassification,
  query: ZellerQueryResult,
  expected: Exclude<VerificationExpected, { throws: true }>,
  name: string,
): void {
  assert.equal(actual.decision, expected.decision, name);
  assert.equal(actual.unknownReason, expected.unknownReason, name);
  // 结论带出的必须是查询结果里的同一笔（按引用比较），不是内容相同的另一笔。
  const expectedTransaction =
    expected.transactionIndex === null ? null : query.transactions[expected.transactionIndex];
  assert.notEqual(expectedTransaction, undefined, name);
  assert.equal(actual.transaction, expectedTransaction, name);
}

test("调用结束后的分类与 C# ZellerResultClassifier 一致（共用向量）", () => {
  assert.ok(vectors.call.length >= 120);
  for (const vector of vectors.call) {
    const actual = classifyZellerCall(vector.result, vector.expectation);
    assert.deepEqual(
      {
        decision: actual.decision,
        failureKind: actual.failureKind,
        unknownReason: actual.unknownReason,
        rebuildTerminalFirst: actual.rebuildTerminalFirst,
        serialGateBreached: actual.serialGateBreached,
        returnsTransaction: actual.transaction !== null,
      },
      vector.expected,
      vector.name,
    );
    if (actual.transaction !== null) {
      assert.equal(actual.transaction, vector.result.transaction, vector.name);
    }
  }
});

test("每一种 SDK 错误类型在向量里都有明确的期望，新增类型不会悄悄落到默认分支", () => {
  const covered = new Set(
    vectors.call
      .filter((vector) => vector.result.outcome === "Error" && vector.expectation.type === "Purchase")
      .map((vector) => vector.result.errorType),
  );
  for (const errorType of [...Object.keys(ZELLER_ERROR_TYPE_CODES), "Unrecognised"]) {
    assert.ok(covered.has(errorType as never), errorType);
  }
  assert.equal(Object.keys(ZELLER_ERROR_TYPE_CODES).length, 26);
});

test("错误类型的数值与 SDK 一致，适配层可以按数值转换", () => {
  assert.equal(ZELLER_ERROR_TYPE_CODES.NetworkFailure, 0);
  assert.equal(ZELLER_ERROR_TYPE_CODES.OperationBusy, 3);
  assert.equal(ZELLER_ERROR_TYPE_CODES.ConnectionFailure, 7);
  assert.equal(ZELLER_ERROR_TYPE_CODES.Cancelled, 12);
  assert.equal(ZELLER_ERROR_TYPE_CODES.InactivityTimeout, 13);
  assert.equal(ZELLER_ERROR_TYPE_CODES.TransactionNotFound, 19);
  assert.equal(ZELLER_ERROR_TYPE_CODES.TransactionFailure, 25);
  assert.deepEqual(
    Object.values(ZELLER_ERROR_TYPE_CODES),
    Array.from({ length: 26 }, (_, index) => index),
  );

  assert.equal(zellerErrorTypeFromSdkCode(12), "Cancelled");
  assert.equal(zellerErrorTypeFromSdkCode(25), "TransactionFailure");
  for (const unrecognised of [26, -1, 1.5, "12", null, undefined]) {
    assert.equal(zellerErrorTypeFromSdkCode(unrecognised), "Unrecognised");
  }
});

test("NetworkFailure 永远不能当成未付款（真机：一次已扣、一次没扣）", () => {
  const actual = classifyZellerCall(
    { outcome: "Error", errorType: "NetworkFailure", reachedTerminal: true },
    { type: "Purchase", amountCents: 100, reference: "HBZ-799287a33dc24df1bca3d33eea19b35a" },
  );
  assert.equal(actual.decision, "Unknown");
  assert.equal(actual.unknownReason, "SdkError");
});

test("按 reference 查询之后的分类与 C# 一致（共用向量）", () => {
  assert.ok(vectors.verification.length >= 20);
  for (const vector of vectors.verification) {
    if ("throws" in vector.expected) {
      assert.throws(
        () => classifyZellerVerification(vector.pending, vector.query, vector.expectation),
        /ZELLER_VERIFICATION_DECISION_NOT_PENDING/u,
        vector.name,
      );
      continue;
    }
    assertVerification(
      classifyZellerVerification(vector.pending, vector.query, vector.expectation),
      vector.query,
      vector.expected,
      vector.name,
    );
  }
});

test("未付款收尾后的复核与 C# 一致（共用向量）", () => {
  assert.ok(vectors.lateRecheck.length >= 5);
  for (const vector of vectors.lateRecheck) {
    assert.ok(!("throws" in vector.expected), vector.name);
    assertVerification(
      classifyZellerLateRecheck(vector.query, vector.expectation),
      vector.query,
      vector.expected,
      vector.name,
    );
  }
});
