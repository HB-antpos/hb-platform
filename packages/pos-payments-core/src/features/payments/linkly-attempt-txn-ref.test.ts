import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  canonicalLinklyAttemptGuid,
  createLinklyAttemptTxnRef,
  deriveLinklyAttemptTxnRef,
} from "./linkly-attempt-txn-ref";

type Vector = Readonly<{
  transactionType: "P" | "R";
  attemptGuid: string;
  txnRef: string;
}>;

function loadVectors(): readonly Vector[] {
  const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "..", "..");
  const file = join(root, "test-fixtures", "linkly-attempt-txnref", "vectors.json");
  return (JSON.parse(readFileSync(file, "utf8")) as { cases: Vector[] }).cases;
}

test("TxnRef 派生与 C# LinklyAttemptTxnRef 共用黄金向量逐位一致", () => {
  const vectors = loadVectors();
  assert.ok(vectors.length >= 10);
  for (const vector of vectors) {
    assert.equal(
      createLinklyAttemptTxnRef(vector.transactionType, vector.attemptGuid),
      vector.txnRef,
      `${vector.transactionType}|${vector.attemptGuid}`,
    );
    assert.match(vector.txnRef, /^[PR][0-9A-HJKMNP-TV-Z]{15}$/u);
  }
});

test("大写 GUID 与小写 GUID 派生同一值（对齐 Guid.ToString(\"D\")）", () => {
  const lower = "3b241101-e2bb-4255-8caf-4136c566a962";
  assert.equal(
    createLinklyAttemptTxnRef("P", lower.toUpperCase()),
    createLinklyAttemptTxnRef("P", lower),
  );
});

test("非标准 GUID 与全零 GUID 拒绝派生；deriveLinklyAttemptTxnRef 对非 GUID attemptId 返回 null", () => {
  assert.throws(() => createLinklyAttemptTxnRef("P", "attempt-1"), /LINKLY_ATTEMPT_GUID_INVALID/u);
  assert.throws(
    () => createLinklyAttemptTxnRef("P", "00000000-0000-0000-0000-000000000000"),
    /LINKLY_ATTEMPT_GUID_INVALID/u,
  );
  assert.equal(canonicalLinklyAttemptGuid("attempt-1"), null);
  assert.equal(deriveLinklyAttemptTxnRef({ attemptId: "attempt-1", operation: "purchase" }), null);
  const guid = "89bd9d8d-69a2-4803-8f8d-830ec69be3a0";
  assert.equal(
    deriveLinklyAttemptTxnRef({ attemptId: guid, operation: "purchase" }),
    createLinklyAttemptTxnRef("P", guid),
  );
  assert.equal(
    deriveLinklyAttemptTxnRef({ attemptId: guid, operation: "refund" }),
    createLinklyAttemptTxnRef("R", guid),
  );
  assert.notEqual(
    deriveLinklyAttemptTxnRef({ attemptId: guid, operation: "refund" }),
    deriveLinklyAttemptTxnRef({ attemptId: guid, operation: "purchase" }),
  );
});
