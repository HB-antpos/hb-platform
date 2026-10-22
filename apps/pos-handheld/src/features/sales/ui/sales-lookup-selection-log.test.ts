import assert from "node:assert/strict";
import test from "node:test";

import {
  bindSalesLookupSelectionLog,
  toLogDraft,
} from "./sales-lookup-selection-log";
import type { SalesLookupSelectionLogEvent } from "./sales-presenter";

const created: SalesLookupSelectionLogEvent = {
  action: "created",
  choiceId: 3,
  selectionId: "lookup-9",
  source: "hid",
  lookupCodeTail: "1470",
  lookupCodeLength: 13,
  candidateCount: 2,
  candidateProducts: "P-FLY,P-FLOWER",
  queueLength: 1,
  waitedMs: null,
  sameLookupCodeAsPrevious: false,
  reason: null,
  productCode: null,
};

test("待选日志映射到中心程序日志：创建记 Information，空字段不写入", () => {
  const draft = toLogDraft(created);
  assert.equal(draft.level, "Information");
  assert.equal(draft.category, "sales.scan-choice");
  assert.equal(draft.message, "Pending scan choice created.");
  assert.deepEqual(draft.properties, {
    action: "created",
    selectionId: "lookup-9",
    source: "hid",
    lookupCodeTail: "1470",
    lookupCodeLength: 13,
    candidateCount: 2,
    candidateProducts: "P-FLY,P-FLOWER",
    queueLength: 1,
    choiceId: 3,
    sameLookupCodeAsPrevious: false,
  });
});

test("放弃与拒收记 Warning；日志写入失败不影响 presenter 回调", () => {
  let emit: ((event: SalesLookupSelectionLogEvent) => void) | null = null;
  const levels: string[] = [];
  const unsubscribe = bindSalesLookupSelectionLog(
    {
      subscribeLookupSelectionLog(listener) {
        emit = listener;
        return () => {
          emit = null;
        };
      },
    },
    {
      record(draft) {
        levels.push(draft.level);
        throw new Error("outbox unavailable");
      },
    },
  );

  const send = emit as ((event: SalesLookupSelectionLogEvent) => void) | null;
  send?.({ ...created, action: "abandoned", reason: "cart-cleared", waitedMs: 1_200 });
  send?.({ ...created, action: "rejected", choiceId: null, reason: "queue-full" });
  assert.deepEqual(levels, ["Warning", "Warning"]);
  unsubscribe();
  assert.equal(emit, null);
  // 没有日志服务时绑定为空操作。
  assert.doesNotThrow(() => bindSalesLookupSelectionLog({ subscribeLookupSelectionLog: () => () => undefined }, null)());
});
