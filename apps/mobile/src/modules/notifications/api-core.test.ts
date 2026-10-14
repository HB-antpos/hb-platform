import assert from "node:assert/strict";
import type { AxiosRequestConfig } from "axios";
import { MAX_MARK_READ_IDS, NOTIFICATIONS_API_BASE, createNotificationsApi, type NotificationsHttp } from "./api-core";

interface Recorded {
  method: "get" | "post";
  url: string;
  data?: unknown;
  config?: AxiosRequestConfig;
}

function fakeHttp(reply: (call: Recorded) => unknown) {
  const calls: Recorded[] = [];
  const http: NotificationsHttp = {
    get: async (url, config) => {
      const call: Recorded = { method: "get", url, config };
      calls.push(call);
      return { data: reply(call) };
    },
    post: async (url, data, config) => {
      const call: Recorded = { method: "post", url, data, config };
      calls.push(call);
      return { data: reply(call) };
    },
  };
  return { http, calls };
}

const envelope = (data: unknown) => ({ success: true, message: "", data });

async function run() {
  assert.equal(NOTIFICATIONS_API_BASE, "/react/v1/notifications");

  // ───────── 列表：游标 + take，不是页码；空值不带 ─────────
  {
    const { http, calls } = fakeHttp(() =>
      envelope({ items: [{ id: "n1", type: "system.test", title: "T", body: "B", createdAtUtc: "2026-10-14T01:00:00Z" }], nextCursor: "c2", unreadCount: 3 }),
    );
    const api = createNotificationsApi(http);
    const page = await api.list({ cursor: null, take: 20 });
    assert.equal(calls[0].method, "get");
    assert.equal(calls[0].url, "/react/v1/notifications");
    assert.deepEqual(calls[0].config?.params, { cursor: undefined, take: 20, unreadOnly: undefined });
    assert.equal(page.items[0].id, "n1");
    assert.equal(page.nextCursor, "c2");
    assert.equal(page.unreadCount, 3);
    await api.list({ cursor: "c2", take: 20, unreadOnly: true });
    assert.deepEqual(calls[1].config?.params, { cursor: "c2", take: 20, unreadOnly: true });
    const params = calls[1].config?.params as Record<string, unknown>;
    assert.equal("page" in params || "pageSize" in params, false, "通知分页没有页码参数");
  }

  // ───────── 未读数：data 是整数；type 可选（缺省 = 全部，不带任何查询参数） ─────────
  {
    const { http, calls } = fakeHttp(() => envelope(5));
    const api = createNotificationsApi(http);
    assert.equal(await api.unreadCount(), 5);
    assert.equal(calls[0].url, "/react/v1/notifications/unread-count");
    assert.equal(calls[0].config?.params, undefined, "不传 type 就不带查询参数（原行为不变）");
    assert.equal(await api.unreadCount("face.unwelcome-hit"), 5);
    assert.deepEqual(calls[1].config?.params, { type: "face.unwelcome-hit" }, "按类型统计命中通知的未读数");
    const controller = new AbortController();
    await api.unreadCount("face.unwelcome-hit", controller.signal);
    assert.equal(calls[2].config?.signal, controller.signal, "取消信号透传");
  }

  // 未读数宽松解析：不是数字（字符串、null、对象、缺失）一律当 0，负数当 0，小数取整
  for (const [raw, expected] of [["7", 0], [null, 0], [{ count: 3 }, 0], [undefined, 0], [-2, 0], [4.8, 4]] as const) {
    const { http } = fakeHttp(() => envelope(raw));
    assert.equal(await createNotificationsApi(http).unreadCount("face.unwelcome-hit"), expected, `data=${JSON.stringify(raw)}`);
  }

  // ───────── 标记已读：POST /read { ids }，去重去空 ─────────
  {
    const { http, calls } = fakeHttp(() => envelope({ updated: 2, unreadCount: 1 }));
    const api = createNotificationsApi(http);
    const result = await api.markRead(["n1", " n2 ", "n1", ""]);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].method, "post");
    assert.equal(calls[0].url, "/react/v1/notifications/read");
    assert.deepEqual(calls[0].data, { ids: ["n1", "n2"] });
    assert.deepEqual(result, { updated: 2, unreadCount: 1 });
    const none = await api.markRead([]);
    assert.equal(calls.length, 1, "没有编号不发请求");
    assert.deepEqual(none, { updated: 0, unreadCount: 0 });
  }

  // 超过后端单次上限（200）时分批发送，更新数累加、未读数取最后一批
  {
    let batch = 0;
    const { http, calls } = fakeHttp(() => {
      batch += 1;
      return envelope({ updated: batch === 1 ? MAX_MARK_READ_IDS : 5, unreadCount: batch === 1 ? 9 : 4 });
    });
    const api = createNotificationsApi(http);
    const ids = Array.from({ length: MAX_MARK_READ_IDS + 5 }, (_, index) => `n${index}`);
    const result = await api.markRead(ids);
    assert.equal(calls.length, 2);
    assert.equal((calls[0].data as { ids: string[] }).ids.length, MAX_MARK_READ_IDS);
    assert.equal((calls[1].data as { ids: string[] }).ids.length, 5);
    assert.deepEqual(result, { updated: MAX_MARK_READ_IDS + 5, unreadCount: 4 });
  }

  // ───────── 全部已读：POST /read-all，无请求体 ─────────
  {
    const { http, calls } = fakeHttp(() => envelope({ updated: 7, unreadCount: 0 }));
    const api = createNotificationsApi(http);
    const result = await api.markAllRead();
    assert.equal(calls[0].method, "post");
    assert.equal(calls[0].url, "/react/v1/notifications/read-all");
    assert.equal(calls[0].data, undefined);
    assert.deepEqual(result, { updated: 7, unreadCount: 0 });
  }

  // ───────── 业务失败（success=false）抛出带错误码的错误 ─────────
  {
    const { http } = fakeHttp(() => ({ success: false, message: "bad", errorCode: "BAD_REQUEST" }));
    const api = createNotificationsApi(http);
    await assert.rejects(() => api.list(), (error) => (error as { code?: string }).code === "BAD_REQUEST");
  }
}

run().then(
  () => console.log("api-core.test.ts: ok"),
  (error) => {
    console.error(error);
    process.exit(1);
  },
);
