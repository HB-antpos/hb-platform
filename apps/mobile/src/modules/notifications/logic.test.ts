import assert from "node:assert/strict";
import {
  NOTIFICATION_TYPE_FACE_UNWELCOME_HIT,
  applyMarkAllRead,
  applyMarkRead,
  currentUnreadCount,
  describeNotificationTime,
  isHighPriority,
  isUnread,
  mergeNotificationPages,
  resolveNotificationTarget,
  type NotificationInfiniteData,
} from "./logic";
import type { NotificationItem, NotificationPage } from "./types";

const item = (id: string, patch: Partial<NotificationItem> = {}): NotificationItem => ({
  id,
  type: "system.test",
  priority: 0,
  title: `T${id}`,
  body: `B${id}`,
  data: {},
  storeCode: null,
  createdAtUtc: "2026-10-14T01:00:00Z",
  readAtUtc: null,
  ...patch,
});

const page = (items: NotificationItem[], unreadCount: number, nextCursor: string | null = null): NotificationPage => ({ items, nextCursor, unreadCount });

// ───────── 已读 / 优先级 ─────────
assert.equal(isUnread(item("a")), true);
assert.equal(isUnread(item("a", { readAtUtc: "2026-10-14T02:00:00Z" })), false);
assert.equal(isHighPriority(item("a", { priority: 1 })), true);
assert.equal(isHighPriority(item("a", { priority: 0 })), false);

// ───────── 点击去向：疑似命中进名单页，其余未知类型只标已读 ─────────
assert.equal(NOTIFICATION_TYPE_FACE_UNWELCOME_HIT, "face.unwelcome-hit");
assert.deepEqual(resolveNotificationTarget({ type: "face.unwelcome-hit" }, true), { kind: "route", routeName: "face-unwelcome" });
assert.deepEqual(resolveNotificationTarget({ type: "face.unwelcome-hit" }, false), { kind: "none" }, "打不开名单页的账号不跳转");
for (const type of ["system.test", "face.unwelcome-expiring", "approval.submitted", "", "FACE.UNWELCOME-HIT", "face.unwelcome-hit.v2"]) {
  assert.deepEqual(resolveNotificationTarget({ type }, true), { kind: "none" }, `${type || "(空)"} 按未知类型兜底`);
}

// ───────── 已读状态写回缓存 ─────────
{
  const data: NotificationInfiniteData = {
    pages: [page([item("a"), item("b", { readAtUtc: "2026-10-13T00:00:00Z" })], 3, "c2"), page([item("c")], 3)],
    pageParams: [null, "c2"],
  };
  const next = applyMarkRead(data, ["a", "b", "zzz"], 2, "2026-10-14T05:00:00Z")!;
  assert.equal(next.pages[0].items[0].readAtUtc, "2026-10-14T05:00:00Z", "未读的被标成已读");
  assert.equal(next.pages[0].items[1].readAtUtc, "2026-10-13T00:00:00Z", "已读的保留原已读时间");
  assert.equal(next.pages[1].items[0].readAtUtc, null, "没点的不变");
  assert.deepEqual(next.pages.map((p) => p.unreadCount), [2, 2], "每页的未读总数以服务端返回为准");
  assert.deepEqual(next.pageParams, [null, "c2"]);
  assert.equal(data.pages[0].items[0].readAtUtc, null, "不修改原缓存对象");
  assert.equal(applyMarkRead(undefined, ["a"], 0, "x"), undefined);

  const all = applyMarkAllRead(data, 0, "2026-10-14T06:00:00Z")!;
  assert.deepEqual(all.pages.flatMap((p) => p.items.map((i) => i.readAtUtc)), ["2026-10-14T06:00:00Z", "2026-10-13T00:00:00Z", "2026-10-14T06:00:00Z"]);
  assert.deepEqual(all.pages.map((p) => p.unreadCount), [0, 0]);
  assert.equal(applyMarkAllRead(undefined, 0, "x"), undefined);
}

// ───────── 分页合并去重 / 未读数 ─────────
assert.deepEqual(mergeNotificationPages([page([item("a"), item("b")], 0), page([item("b"), item("c")], 0)]).map((i) => i.id), ["a", "b", "c"]);
assert.equal(currentUnreadCount(undefined), 0);
assert.equal(currentUnreadCount([]), 0);
assert.equal(currentUnreadCount([page([], 6), page([], 4)]), 6, "以最新一页的服务端未读总数为准");

// ───────── 时间：当天 HH:mm、昨天、更早 MM-dd、跨年带年份 ─────────
{
  const now = new Date(2026, 9, 14, 12, 0, 0);
  assert.deepEqual(describeNotificationTime(new Date(2026, 9, 14, 9, 21, 5).toISOString(), now), { kind: "time", text: "09:21" });
  assert.deepEqual(describeNotificationTime(new Date(2026, 9, 13, 23, 59, 0).toISOString(), now), { kind: "yesterday" });
  assert.deepEqual(describeNotificationTime(new Date(2026, 9, 1, 8, 0, 0).toISOString(), now), { kind: "date", text: "10-01" });
  assert.deepEqual(describeNotificationTime(new Date(2025, 11, 31, 8, 0, 0).toISOString(), now), { kind: "date", text: "2025-12-31" });
  assert.equal(describeNotificationTime("", now), null);
  assert.equal(describeNotificationTime("bad", now), null);
  // 跨月边界：11 月 1 日的「昨天」是 10 月 31 日
  assert.deepEqual(describeNotificationTime(new Date(2026, 9, 31, 20, 0, 0).toISOString(), new Date(2026, 10, 1, 7, 0, 0)), { kind: "yesterday" });
}

console.log("logic.test.ts: ok");
