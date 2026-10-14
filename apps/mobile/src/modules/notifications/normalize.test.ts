import assert from "node:assert/strict";
import { normalizeMarkReadResult, normalizeNotification, normalizeNotificationPage, normalizeUnreadCount } from "./normalize";

// ───────── 单条：字段齐全 ─────────
{
  const item = normalizeNotification({
    id: "n1",
    type: "face.unwelcome-hit",
    priority: 1,
    title: "Possible match alert",
    body: "Possible match (similarity 87%).",
    data: { taskId: "t1", storeCode: "1042" },
    storeCode: "1042",
    createdAtUtc: "2026-10-14T01:21:00Z",
    readAtUtc: null,
  });
  assert.equal(item.type, "face.unwelcome-hit");
  assert.equal(item.priority, 1);
  assert.deepEqual(item.data, { taskId: "t1", storeCode: "1042" });
  assert.equal(item.readAtUtc, null);
}

// ───────── 缺失字段与类型漂移：不崩，不编造 ─────────
{
  const item = normalizeNotification({ id: "n2", priority: "high", data: { ok: "1", bad: 5, nested: { a: 1 } }, readAtUtc: "2026-10-14T02:00:00Z" });
  assert.equal(item.type, "");
  assert.equal(item.priority, 0, "优先级不是数字当普通");
  assert.deepEqual(item.data, { ok: "1" }, "Data 只保留字符串值");
  assert.equal(item.storeCode, null);
  assert.equal(item.readAtUtc, "2026-10-14T02:00:00Z");
  assert.equal(normalizeNotification(null).id, "");
}

// ───────── 分页：丢弃没有编号的条目，未读总数不为负 ─────────
{
  const page = normalizeNotificationPage({
    items: [{ id: "a", title: "A" }, { title: "no id" }, { id: "b" }],
    nextCursor: "abc",
    unreadCount: 4,
  });
  assert.deepEqual(page.items.map((item) => item.id), ["a", "b"]);
  assert.equal(page.nextCursor, "abc");
  assert.equal(page.unreadCount, 4);
  const empty = normalizeNotificationPage({ items: [], nextCursor: "", unreadCount: -2 });
  assert.equal(empty.nextCursor, null, "空游标 = 没有更多");
  assert.equal(empty.unreadCount, 0);
  assert.deepEqual(normalizeNotificationPage(undefined).items, []);
}

// ───────── 标记已读结果与未读数 ─────────
assert.deepEqual(normalizeMarkReadResult({ updated: 2, unreadCount: 5 }), { updated: 2, unreadCount: 5 });
assert.deepEqual(normalizeMarkReadResult({}), { updated: 0, unreadCount: 0 });
assert.equal(normalizeUnreadCount(7), 7);
assert.equal(normalizeUnreadCount("7"), 0);
assert.equal(normalizeUnreadCount(-1), 0);

console.log("normalize.test.ts: ok");
