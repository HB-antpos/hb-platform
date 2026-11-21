import assert from "node:assert/strict";
import test, { afterEach } from "node:test";

import {
  beginNativeCallTrace,
  describeSql,
  endNativeCallTrace,
  isNativeCallTracing,
  recordNativeCall,
  traceNativeCall,
} from "./native-call-trace";

afterEach(() => {
  // 清掉可能残留的追踪，避免用例互相污染。
  endNativeCallTrace();
});

test("只记录动词与表名，不含 SQL 全文与参数；WITH 查询取第一个 FROM 的表", () => {
  assert.equal(
    describeSql("getFirst", "SELECT * FROM catalog_items WHERE code = '93000123'"),
    "getFirst:SELECT:catalog_items",
  );
  assert.equal(
    describeSql("run", "INSERT INTO audit_events (event_id) VALUES (?)"),
    "run:INSERT:audit_events",
  );
  assert.equal(
    describeSql("getFirst", "WITH active_rows AS (SELECT 1 FROM catalog_snapshots) SELECT 1"),
    "getFirst:WITH:catalog_snapshots",
  );
  assert.equal(describeSql("exec", "BEGIN IMMEDIATE;"), "exec:BEGIN");
  assert.equal(describeSql("exec", "  commit;"), "exec:COMMIT");
});

test("时间线按起点偏移与时长输出；相邻调用的空隙即排队 / JS 线程忙", () => {
  let clock = 1_000;
  beginNativeCallTrace(() => clock);
  assert.equal(isNativeCallTracing(), true);

  recordNativeCall("getFirst", "SELECT 1 FROM catalog_items", 1_005, 1_215);
  recordNativeCall("run", "INSERT INTO audit_events VALUES (?)", 1_220, 1_430);
  clock = 1_500;

  assert.equal(
    endNativeCallTrace(),
    "getFirst:SELECT:catalog_items@5+210 | run:INSERT:audit_events@220+210",
  );
  assert.equal(isNativeCallTracing(), false);
});

test("未开启追踪时 traceNativeCall 直接透传且不记录；结束后再次 end 返回 null", async () => {
  let called = 0;
  const value = await traceNativeCall("getFirst", "SELECT 1", async () => {
    called += 1;
    return "row";
  });
  assert.equal(value, "row");
  assert.equal(called, 1);
  assert.equal(endNativeCallTrace(), null);

  // 开启但没有任何调用：返回 null，而不是空字符串。
  beginNativeCallTrace(() => 0);
  assert.equal(endNativeCallTrace(), null);
});

test("traceNativeCall 在成功与失败两种结果下都记录耗时，且不改变原结果", async () => {
  let clock = 0;
  const now = () => clock;
  beginNativeCallTrace(now);

  const ok = await traceNativeCall(
    "getAll",
    "SELECT * FROM orders",
    async () => {
      clock += 30;
      return [1, 2];
    },
    now,
  );
  assert.deepEqual(ok, [1, 2]);

  await assert.rejects(
    traceNativeCall(
      "run",
      "UPDATE orders SET x = 1",
      async () => {
        clock += 70;
        throw new Error("boom");
      },
      now,
    ),
    /boom/u,
  );

  assert.equal(
    endNativeCallTrace(),
    "getAll:SELECT:orders@0+30 | run:UPDATE:orders@30+70",
  );
});

test("超过 30 条时丢弃多余调用并标注条数，输出不超过 900 字符", () => {
  beginNativeCallTrace(() => 0);
  for (let index = 0; index < 45; index += 1) {
    recordNativeCall("getFirst", "SELECT 1 FROM catalog_lookup_overlays", index, index + 1);
  }
  const text = endNativeCallTrace();
  assert.ok(text);
  assert.ok(text.length <= 900);
  assert.ok(text.includes("…"));
  assert.ok(text.startsWith("getFirst:SELECT:catalog_lookup_overlays@0+1"));
});
