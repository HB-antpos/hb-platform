import assert from "node:assert/strict";
import test from "node:test";

import { getLeadingZeroBarcodeVariant } from "./barcode-variants";
import {
  PENDING_SCAN_CHOICE_CAPACITY,
  PendingScanChoiceQueue,
} from "./pending-scan-choice-queue";

test("前导 0 变体：12 位补 0、0 开头 13 位去 0，其它不回退", () => {
  assert.equal(getLeadingZeroBarcodeVariant("810812024527"), "0810812024527");
  assert.equal(getLeadingZeroBarcodeVariant("0810812024527"), "810812024527");
  assert.equal(getLeadingZeroBarcodeVariant(" 810812024527 "), "0810812024527");
  assert.equal(getLeadingZeroBarcodeVariant("9300000000013"), null);
  assert.equal(getLeadingZeroBarcodeVariant("12345678901"), null);
  assert.equal(getLeadingZeroBarcodeVariant("01234567890123"), null);
  assert.equal(getLeadingZeroBarcodeVariant("81081202452A"), null);
  assert.equal(getLeadingZeroBarcodeVariant(""), null);
});

test("待选队列先进先出，同码两次扫码是两个条目并标记疑似重复", () => {
  let now = 1_000;
  const queue = new PendingScanChoiceQueue<string>({ now: () => now });
  const first = queue.tryEnqueue(" 5237 ", "a");
  now = 1_500;
  const second = queue.tryEnqueue("5237", "b");
  const third = queue.tryEnqueue("9999", "c");

  assert.ok(first && second && third);
  assert.equal(first.lookupCode, "5237");
  assert.equal(first.sameLookupCodeAsPrevious, false);
  assert.equal(second.sameLookupCodeAsPrevious, true);
  assert.equal(third.sameLookupCodeAsPrevious, false);
  assert.equal(queue.size, 3);
  assert.equal(queue.current?.payload, "a");

  now = 4_000;
  assert.equal(queue.waitedMs(first), 3_000);
  const snapshotBefore = queue.snapshot();
  assert.equal(queue.remove(first.id)?.payload, "a");
  assert.notEqual(queue.snapshot(), snapshotBefore);
  assert.equal(queue.current?.payload, "b");
  // 已移除的条目再次移除不应影响队列（重复点击）。
  assert.equal(queue.remove(first.id), null);
  assert.equal(queue.size, 2);

  const cleared = queue.clear();
  assert.deepEqual(
    cleared.map((entry) => entry.payload),
    ["b", "c"],
  );
  assert.equal(queue.size, 0);
  assert.equal(queue.current, null);
});

test("待选队列满了拒收，不静默丢弃已有条目", () => {
  const queue = new PendingScanChoiceQueue<number>();
  for (let index = 0; index < PENDING_SCAN_CHOICE_CAPACITY; index += 1) {
    assert.ok(queue.tryEnqueue(`code-${index}`, index));
  }
  assert.equal(queue.tryEnqueue("overflow", -1), null);
  assert.equal(queue.size, PENDING_SCAN_CHOICE_CAPACITY);
  assert.equal(queue.current?.payload, 0);
  assert.throws(() => new PendingScanChoiceQueue({ capacity: 0 }), RangeError);
});
