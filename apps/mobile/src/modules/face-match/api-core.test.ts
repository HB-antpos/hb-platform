import assert from "node:assert/strict";
import type { AxiosRequestConfig } from "axios";
import { fromByteArray } from "base64-js";
import { CREATE_ENTRY_TIMEOUT_MS, IMAGE_TIMEOUT_MS, createFaceMatchApi, detectImageMime, type FaceMatchHttp } from "./api-core";
import { THUMBNAIL_BATCH_MAX } from "./constants";
import { FACE_HIT_NOTIFICATION_TYPE } from "./hit-unread";
import { buildCameraEnabledRequest } from "./cameras-logic";
import type { CreateEntryInput } from "./entry-form";
import { classifyReviewImageError } from "./errors";
import { normalizeFaceStoreProfile } from "./normalize";
import { buildProfileUpdateRequest, profileToEditValues } from "./profile-form";

interface Recorded {
  method: "get" | "post" | "put";
  url: string;
  data?: unknown;
  config?: AxiosRequestConfig;
}

function fakeHttp(reply: (call: Recorded) => unknown | Promise<unknown>) {
  const calls: Recorded[] = [];
  const handle = async (call: Recorded) => {
    calls.push(call);
    return { data: await reply(call) };
  };
  const http: FaceMatchHttp = {
    get: (url, config) => handle({ method: "get", url, config }),
    post: (url, data, config) => handle({ method: "post", url, data, config }),
    put: (url, data, config) => handle({ method: "put", url, data, config }),
  };
  return { http, calls };
}

const JPEG = Uint8Array.from([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46]);
const PNG = Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00]);

async function run() {
  // ───────── 文件头识别 ─────────
  assert.equal(detectImageMime(JPEG), "image/jpeg");
  assert.equal(detectImageMime(PNG), "image/png");
  assert.equal(detectImageMime(Uint8Array.from([0x47, 0x49, 0x46, 0x38])), null, "GIF 不是服务端会返回的类型");
  assert.equal(detectImageMime(new TextEncoder().encode('{"success":false}')), null, "JSON 错误体不会被当成图片");
  assert.equal(detectImageMime(new Uint8Array(0)), null);

  // ───────── 配置、档案 ─────────
  {
    const { http, calls } = fakeHttp(() => ({ enabled: true, permissions: { unwelcomeReview: true } }));
    const api = createFaceMatchApi(http);
    const config = await api.getConfig();
    assert.equal(config.permissions.unwelcomeReview, true);
    assert.equal(calls[0].url, "/react/v1/face-match/config");
    assert.equal(calls[0].method, "get");
    assert.equal((calls[0].config?.headers as Record<string, string>)["Cache-Control"], "no-store", "所有请求带 no-store");
  }
  {
    const { http, calls } = fakeHttp(() => ({ storeCode: "10/08", mode: 1 }));
    const api = createFaceMatchApi(http);
    await api.getProfile("10/08");
    assert.equal(calls[0].url, "/react/v1/face-match/profiles/10%2F08", "门店编码做 URL 编码");
    const profile = normalizeFaceStoreProfile({ storeCode: "1008", mode: 2, piaReference: "PIA-1", legalSignoffAt: "2026-09-20T03:00:00Z" });
    const request = buildProfileUpdateRequest(profile, { ...profileToEditValues(profile), mode: 0 });
    await api.updateProfile("1008", request);
    assert.equal(calls[1].method, "put");
    assert.equal(calls[1].url, "/react/v1/face-match/profiles/1008");
    assert.deepEqual(calls[1].data, request, "PUT 请求体原样发送");
    assert.equal((calls[1].data as typeof request).piaReference, "PIA-1", "未编辑的合规字段随请求带回");
  }

  // ───────── 列表查询参数与 Web 一致 ─────────
  {
    const { http, calls } = fakeHttp(() => ({ items: [{ entryId: "e1" }], page: 2, pageSize: 20, hasMore: true }));
    const api = createFaceMatchApi(http);
    const page = await api.listEntries({ status: 0, storeCode: "1008", page: 2, pageSize: 20 });
    assert.deepEqual(calls[0].config?.params, { status: "0", storeCode: "1008", page: 2, pageSize: 20 });
    assert.equal(calls[0].url, "/react/v1/face-match/unwelcome/entries");
    assert.equal(page.items[0].entryId, "e1");
    assert.equal(page.hasMore, true);
    await api.listTasks({ page: 1, pageSize: 20 });
    assert.equal(calls[1].url, "/react/v1/face-match/unwelcome/tasks");
    assert.deepEqual(calls[1].config?.params, { status: undefined, storeCode: undefined, page: 1, pageSize: 20 });
  }

  // ───────── 批准 / 撤回 / 备注 / 复核 ─────────
  {
    const { http, calls } = fakeHttp(() => ({ entryId: "e/1", taskId: "t1" }));
    const api = createFaceMatchApi(http);
    await api.approveEntry("e/1");
    assert.equal(calls[0].url, "/react/v1/face-match/unwelcome/entries/e%2F1/approve");
    assert.equal(calls[0].data, undefined);
    await api.withdrawEntry("e1", "录入有误");
    assert.equal(calls[1].url, "/react/v1/face-match/unwelcome/entries/e1/withdraw");
    assert.deepEqual(calls[1].data, { reason: "录入有误" });
    await api.setDisputeNote("e1", "已联系");
    assert.equal(calls[2].url, "/react/v1/face-match/unwelcome/entries/e1/dispute-note");
    assert.deepEqual(calls[2].data, { note: "已联系" });
    await api.reviewTask("t1", "notMatch");
    assert.equal(calls[3].url, "/react/v1/face-match/unwelcome/tasks/t1/review");
    assert.deepEqual(calls[3].data, { verdict: "notMatch" });
    await api.reviewTask("t1", "match");
    assert.deepEqual(calls[4].data, { verdict: "match" });
  }

  // ───────── 提交条目：multipart 字节、长超时、禁止 axios 再序列化 ─────────
  {
    const { http, calls } = fakeHttp(() => ({ entryId: "new1", status: 0 }));
    const api = createFaceMatchApi(http);
    const input: CreateEntryInput = {
      incidentCategory: 1,
      categoryNote: null,
      evidenceRef: "INC-1",
      storeScope: ["1008"],
      expiresAtUtc: "2026-12-30T13:00:00.000Z",
      photos: [{ key: "k", base64: fromByteArray(JPEG), byteSize: JPEG.length, width: 1, height: 1, previewDataUri: "data:image/jpeg;base64,AA==" }],
    };
    const created = await api.createEntry(input);
    assert.equal(created.entryId, "new1");
    const call = calls[0];
    assert.equal(call.url, "/react/v1/face-match/unwelcome/entries");
    assert.ok(call.data instanceof ArrayBuffer, "请求体是内存里的 ArrayBuffer，不是文件地址");
    const headers = call.config?.headers as Record<string, string>;
    assert.match(headers["Content-Type"], /^multipart\/form-data; boundary=----HbFaceMatch[0-9a-f]{16}$/);
    assert.equal(call.config?.timeout, CREATE_ENTRY_TIMEOUT_MS);
    assert.equal(call.config?.transformRequest instanceof Array, true);
    const identity = (call.config?.transformRequest as ((data: unknown) => unknown)[])[0];
    assert.equal(identity(call.data), call.data, "transformRequest 原样透传");
    const text = Buffer.from(call.data as ArrayBuffer).toString("latin1");
    assert.ok(text.includes('name="photos"; filename="photo-1.jpg"'));
    assert.ok(text.includes('name="storeScope"\r\n\r\n1008\r\n'));
  }

  // ───────── 图片：带登录态的请求取字节 → data URI；不拼进 URL 查询串；关闭中心日志 ─────────
  {
    const { http, calls } = fakeHttp((call) => (call.url.endsWith("/image") ? JPEG.buffer.slice(0) : PNG.buffer.slice(0)));
    const api = createFaceMatchApi(http);
    const capture = await api.fetchTaskImage("task-123");
    assert.equal(capture, `data:image/jpeg;base64,${fromByteArray(JPEG)}`);
    const reference = await api.fetchReferenceImage("task-123", 2);
    assert.equal(reference, `data:image/png;base64,${fromByteArray(PNG)}`);
    assert.equal(calls[0].url, "/react/v1/face-match/unwelcome/tasks/task-123/image");
    assert.equal(calls[1].url, "/react/v1/face-match/unwelcome/tasks/task-123/reference/2");
    for (const call of calls) {
      assert.equal(call.url.includes("?"), false, "图片地址不带任何查询串");
      assert.equal(call.config?.responseType, "arraybuffer");
      assert.equal(call.config?.params, undefined);
      const headers = call.config?.headers as Record<string, string>;
      assert.equal(headers["Cache-Control"], "no-store");
      assert.equal(headers["X-Skip-Center-Log"], "1", "图片请求不进中心日志上报");
      assert.equal(headers.Accept, "image/jpeg, image/png");
      assert.equal(call.config?.timeout, IMAGE_TIMEOUT_MS);
    }
  }

  // 返回的不是图片字节：当作失败，不当图片显示
  {
    const { http } = fakeHttp(() => new TextEncoder().encode('{"success":true}').buffer);
    const api = createFaceMatchApi(http);
    await assert.rejects(
      () => api.fetchTaskImage("t1"),
      (error) => (error as { code?: string }).code === "FACE_MATCH_IMAGE_UNSUPPORTED" && classifyReviewImageError(error) === "failed",
    );
  }

  // 路径里的编号先校验（第二道防线）：非法编号不发请求
  {
    const { http, calls } = fakeHttp(() => JPEG.buffer.slice(0));
    const api = createFaceMatchApi(http);
    for (const bad of ["../../etc", "a/b", "", "x".repeat(65), "t1?x=1", "t 1"]) {
      await assert.rejects(() => api.fetchTaskImage(bad), (error) => classifyReviewImageError(error) === "gone", bad);
      await assert.rejects(() => api.fetchReferenceImage(bad, 0), (error) => classifyReviewImageError(error) === "gone", bad);
    }
    for (const badIndex of [-1, 10, 1.5, Number.NaN]) {
      await assert.rejects(() => api.fetchReferenceImage("t1", badIndex), (error) => classifyReviewImageError(error) === "gone");
    }
    assert.equal(calls.length, 0, "校验失败不会向服务端发任何请求");
  }

  // 失败响应是二进制 JSON：错误码仍然能被识别（响应体没有被篡改）
  {
    const body = new TextEncoder().encode('{"success":false,"errorCode":"FACE_MATCH_FORBIDDEN"}').buffer;
    const http: FaceMatchHttp = {
      get: async () => {
        throw Object.assign(new Error("x"), { response: { status: 403, data: body } });
      },
      post: async () => ({ data: null }),
      put: async () => ({ data: null }),
    };
    const api = createFaceMatchApi(http);
    await assert.rejects(() => api.fetchTaskImage("t1"), (error) => classifyReviewImageError(error) === "forbidden");
  }

  // ───────── 抓拍与比对结果：列表查询参数；图片接口只有编号、不带查询串 ─────────
  {
    const { http, calls } = fakeHttp(() => ({
      items: [{ captureId: "c1", storeCode: "1042", faceImageId: "f1", sceneImageId: "s1", evaluated: true, decision: "staff" }],
      page: 1,
      pageSize: 20,
      hasMore: false,
    }));
    const api = createFaceMatchApi(http);
    const page = await api.listCaptureResults({
      storeCode: "1042",
      decision: "newVisitor",
      hitOnly: false,
      fromUtc: "2026-10-13T13:00:00.000Z",
      toUtc: "2026-10-14T13:00:00.000Z",
      page: 1,
      pageSize: 20,
    });
    assert.equal(calls[0].url, "/react/v1/face-match/results");
    assert.deepEqual(calls[0].config?.params, {
      storeCode: "1042",
      decision: "newVisitor",
      hitOnly: undefined,
      fromUtc: "2026-10-13T13:00:00.000Z",
      toUtc: "2026-10-14T13:00:00.000Z",
      page: 1,
      pageSize: 20,
    });
    assert.equal((calls[0].config?.headers as Record<string, string>)["Cache-Control"], "no-store");
    assert.equal(page.items[0].faceImageId, "f1");
    await api.listCaptureResults({ hitOnly: true, page: 2, pageSize: 20 });
    assert.deepEqual(calls[1].config?.params, {
      storeCode: undefined,
      decision: undefined,
      hitOnly: true,
      fromUtc: undefined,
      toUtc: undefined,
      page: 2,
      pageSize: 20,
    });
  }
  {
    const { http, calls } = fakeHttp(() => JPEG.buffer.slice(0));
    const api = createFaceMatchApi(http);
    const uri = await api.fetchCaptureImage("a1B2c3", "img9");
    assert.equal(uri, `data:image/jpeg;base64,${fromByteArray(JPEG)}`);
    assert.equal(calls[0].url, "/react/v1/face-match/results/a1B2c3/images/img9");
    assert.equal(calls[0].url.includes("?"), false, "图片地址不带任何查询串");
    assert.equal(calls[0].config?.responseType, "arraybuffer");
    assert.equal(calls[0].config?.params, undefined);
    const headers = calls[0].config?.headers as Record<string, string>;
    assert.equal(headers["Cache-Control"], "no-store");
    assert.equal(headers["X-Skip-Center-Log"], "1", "图片请求不进中心日志上报");
    assert.equal(calls[0].config?.timeout, IMAGE_TIMEOUT_MS);
    // 编号只允许 1–32 位字母数字（与服务端 IdPattern 一致），非法编号不发请求
    for (const bad of ["", "a/b", "../x", "a-b", "x".repeat(33), "t 1", "t?x=1"]) {
      await assert.rejects(() => api.fetchCaptureImage(bad, "img9"), (error) => classifyReviewImageError(error) === "gone", bad);
      await assert.rejects(() => api.fetchCaptureImage("a1B2c3", bad), (error) => classifyReviewImageError(error) === "gone", bad);
    }
    assert.equal(calls.length, 1, "校验失败不会向服务端发任何请求");
  }

  // ───────── 黑名单命中流：列表参数 / 汇总 / 标记已读 / 命中图 / 全部已读 ─────────
  {
    const { http, calls } = fakeHttp(() => ({
      items: [{ taskId: "a".repeat(32), captureId: null, unread: true, notificationId: "n1", canReview: true }, { storeCode: "no-task-id" }],
      page: 1,
      pageSize: 20,
      hasMore: true,
    }));
    const api = createFaceMatchApi(http);
    const page = await api.listHits({ storeCode: "1042", hitStatus: "unread", fromUtc: "2026-10-13T13:00:00.000Z", toUtc: "2026-10-14T13:00:00.000Z", page: 1, pageSize: 20 });
    assert.equal(calls[0].method, "get");
    assert.equal(calls[0].url, "/react/v1/face-match/results/hits");
    assert.deepEqual(calls[0].config?.params, {
      storeCode: "1042",
      hitStatus: "unread",
      fromUtc: "2026-10-13T13:00:00.000Z",
      toUtc: "2026-10-14T13:00:00.000Z",
      page: 1,
      pageSize: 20,
    });
    assert.equal((calls[0].config?.headers as Record<string, string>)["Cache-Control"], "no-store");
    assert.equal(page.items.length, 1, "没有任务编号的行被丢弃");
    assert.equal(page.items[0].unread, true);
    assert.equal(page.items[0].canReview, true);
    assert.equal(page.hasMore, true);
    await api.listHits({ hitStatus: "all", page: 2, pageSize: 20 });
    assert.deepEqual(calls[1].config?.params, { storeCode: undefined, hitStatus: undefined, fromUtc: undefined, toUtc: undefined, page: 2, pageSize: 20 }, "全部状态是默认值，不发");
  }
  {
    const { http, calls } = fakeHttp(() => ({ total: 5, unread: 3, pending: 2, autoConfirmed: 2, confirmed: 0, dismissed: 1 }));
    const api = createFaceMatchApi(http);
    const summary = await api.getHitSummary({ storeCode: "1042", fromUtc: "2026-10-13T13:00:00.000Z", toUtc: "2026-10-14T13:00:00.000Z" });
    assert.equal(calls[0].url, "/react/v1/face-match/results/hits/summary");
    assert.deepEqual(calls[0].config?.params, { storeCode: "1042", fromUtc: "2026-10-13T13:00:00.000Z", toUtc: "2026-10-14T13:00:00.000Z" }, "汇总只带门店与时间");
    assert.deepEqual(summary, { total: 5, unread: 3, pending: 2, autoConfirmed: 2, confirmed: 0, dismissed: 1 });
    await api.getHitSummary({});
    assert.deepEqual(calls[1].config?.params, { storeCode: undefined, fromUtc: undefined, toUtc: undefined });
  }
  {
    const taskId = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    const { http, calls } = fakeHttp(() => ({ unreadCount: 2 }));
    const api = createFaceMatchApi(http);
    const unreadCount = await api.markHitRead(taskId);
    assert.equal(unreadCount, 2, "返回该类型的剩余未读数");
    assert.equal(calls[0].method, "post");
    assert.equal(calls[0].url, `/react/v1/face-match/results/hits/${taskId}/read`);
    assert.equal(calls[0].data, undefined, "标记已读没有请求体");
    assert.equal((calls[0].config?.headers as Record<string, string>)["Cache-Control"], "no-store");
    // 编号校验：非法编号不发请求
    for (const bad of ["", "a/b", "../x", "a b", "t?x=1", "x".repeat(65)]) {
      await assert.rejects(() => api.markHitRead(bad), (error) => (error as { code?: string }).code === "FACE_MATCH_INVALID_ID", JSON.stringify(bad));
    }
    assert.equal(calls.length, 1, "校验失败不会向服务端发任何请求");
  }
  {
    const taskId = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    const { http, calls } = fakeHttp(() => JPEG.buffer.slice(0));
    const api = createFaceMatchApi(http);
    const uri = await api.fetchHitImage(taskId);
    assert.equal(uri, `data:image/jpeg;base64,${fromByteArray(JPEG)}`);
    assert.equal(calls[0].method, "get");
    assert.equal(calls[0].url, `/react/v1/face-match/results/hits/${taskId}/image`);
    assert.equal(calls[0].url.includes("?"), false, "图片地址不带任何查询串");
    assert.equal(calls[0].config?.responseType, "arraybuffer");
    assert.equal(calls[0].config?.params, undefined);
    const headers = calls[0].config?.headers as Record<string, string>;
    assert.equal(headers["Cache-Control"], "no-store");
    assert.equal(headers["X-Skip-Center-Log"], "1", "命中图请求不进中心日志上报");
    assert.equal(calls[0].config?.timeout, IMAGE_TIMEOUT_MS);
    for (const bad of ["", "a/b", "../x", "a b", "t?x=1", "x".repeat(65)]) {
      await assert.rejects(() => api.fetchHitImage(bad), (error) => classifyReviewImageError(error) === "gone", JSON.stringify(bad));
    }
    assert.equal(calls.length, 1, "校验失败不会向服务端发任何请求");
    // 响应不是图片：不当图片显示
    const html = fakeHttp(() => new TextEncoder().encode("<html>").buffer.slice(0));
    await assert.rejects(() => createFaceMatchApi(html.http).fetchHitImage(taskId), (error) => classifyReviewImageError(error) === "failed");
  }
  {
    // 「全部标为已读」：POST /notifications/read-all?type=face.unwelcome-hit（只标命中通知），返回条数与该类型的剩余未读数
    const { http, calls } = fakeHttp(() => ({ updated: 3, unreadCount: 0 }));
    const api = createFaceMatchApi(http);
    const result = await api.markAllHitsRead();
    assert.deepEqual(result, { updated: 3, unreadCount: 0 });
    assert.equal(FACE_HIT_NOTIFICATION_TYPE, "face.unwelcome-hit");
    assert.equal(calls[0].method, "post");
    assert.equal(calls[0].url, "/react/v1/notifications/read-all");
    assert.deepEqual(calls[0].config?.params, { type: "face.unwelcome-hit" });
    assert.equal(calls[0].data, undefined);
  }

  // ───────── 缩略图批量接口：POST /results/thumbnails，≤30 项，编号只放请求体 ─────────
  {
    const { http, calls } = fakeHttp((call) => ({
      items: (call.data as { items: { kind: string; id: string }[] }).items.map((item) => ({
        kind: item.kind,
        id: item.id,
        status: item.id === "gone1" ? "gone" : item.id === "none1" ? "none" : "ok",
        imageBase64: item.id === "gone1" || item.id === "none1" ? null : "/9j/AAAA",
      })),
    }));
    const api = createFaceMatchApi(http);
    const controller = new AbortController();
    const results = await api.fetchThumbnails(
      [
        { kind: "capture", id: "c1" },
        { kind: "hit", id: "0a1b2c3d4e5f60718293a4b5c6d7e8f9" },
        { kind: "capture", id: "gone1" },
        { kind: "capture", id: "none1" },
      ],
      controller.signal,
    );
    assert.equal(calls.length, 1);
    assert.equal(calls[0].method, "post");
    assert.equal(calls[0].url, "/react/v1/face-match/results/thumbnails");
    assert.equal(calls[0].url.includes("?"), false);
    assert.deepEqual(calls[0].data, {
      items: [
        { kind: "capture", id: "c1" },
        { kind: "hit", id: "0a1b2c3d4e5f60718293a4b5c6d7e8f9" },
        { kind: "capture", id: "gone1" },
        { kind: "capture", id: "none1" },
      ],
    });
    const headers = calls[0].config?.headers as Record<string, string>;
    assert.equal(headers["Cache-Control"], "no-store");
    assert.equal(headers["X-Skip-Center-Log"], "1", "缩略图批请求不进中心日志上报");
    assert.equal(calls[0].config?.timeout, IMAGE_TIMEOUT_MS);
    assert.equal(calls[0].config?.signal, controller.signal, "清空时可中止在途请求");
    assert.deepEqual(results.map((item) => [item.kind, item.id, item.status]), [
      ["capture", "c1", "ok"],
      ["hit", "0a1b2c3d4e5f60718293a4b5c6d7e8f9", "ok"],
      ["capture", "gone1", "gone"],
      ["capture", "none1", "none"],
    ]);
    assert.equal(results[0].imageBase64, "/9j/AAAA");
    assert.equal(results[2].imageBase64, null);
  }
  {
    // 恰好 30 项可以发；31 项是调用方的编程错误，直接抛错而不是悄悄截断，也不发请求
    const { http, calls } = fakeHttp(() => ({ items: [] }));
    const api = createFaceMatchApi(http);
    assert.equal(THUMBNAIL_BATCH_MAX, 30);
    const thirty = Array.from({ length: 30 }, (_, index) => ({ kind: "capture" as const, id: `c${index}` }));
    await api.fetchThumbnails(thirty);
    assert.equal(calls.length, 1);
    assert.equal((calls[0].data as { items: unknown[] }).items.length, 30);
    await assert.rejects(() => api.fetchThumbnails([...thirty, { kind: "capture", id: "c30" }]), (error) => (error as { code?: string }).code === "FACE_MATCH_THUMBNAIL_BATCH_TOO_LARGE");
    assert.equal(calls.length, 1, "超限不发请求");
    // 空入参不发请求
    assert.deepEqual(await api.fetchThumbnails([]), []);
    assert.equal(calls.length, 1);
  }
  {
    // 编号格式不对的项不发给服务端，按 denied 处理（保持入参顺序）；其余项照常请求
    const { http, calls } = fakeHttp((call) => ({
      items: (call.data as { items: { kind: string; id: string }[] }).items.map((item) => ({ kind: item.kind, id: item.id, status: "ok", imageBase64: "/9j/AAAA" })),
    }));
    const api = createFaceMatchApi(http);
    const results = await api.fetchThumbnails([
      { kind: "capture", id: "a/b" },
      { kind: "capture", id: "ok1" },
      { kind: "hit", id: "../x" },
      { kind: "capture", id: "x".repeat(33) },
    ]);
    assert.deepEqual((calls[0].data as { items: unknown[] }).items, [{ kind: "capture", id: "ok1" }], "只发合法的项");
    assert.deepEqual(results.map((item) => [item.id, item.status]), [["a/b", "denied"], ["ok1", "ok"], ["../x", "denied"], ["x".repeat(33), "denied"]]);
    assert.equal(results[0].imageBase64, null);
    // 全都不合法：不发请求
    const none = fakeHttp(() => ({ items: [] }));
    const denied = await createFaceMatchApi(none.http).fetchThumbnails([{ kind: "hit", id: "" }]);
    assert.equal(none.calls.length, 0);
    assert.deepEqual(denied, [{ kind: "hit", id: "", status: "denied", imageBase64: null }]);
    // 请求体只含 kind + id，入参里多带的字段不会被发出去
    const extra = fakeHttp(() => ({ items: [] }));
    await createFaceMatchApi(extra.http).fetchThumbnails([{ kind: "capture", id: "c1", note: "x", imageBase64: "leak" } as never]);
    assert.deepEqual(extra.calls[0].data, { items: [{ kind: "capture", id: "c1" }] });
  }
  {
    // 服务端少回了一项：结果里不编造它（调用方把缺失项当作没有图）；多回了不在请求里的项：忽略
    const { http } = fakeHttp(() => ({ items: [{ kind: "capture", id: "c1", status: "ok", imageBase64: "/9j/AAAA" }, { kind: "capture", id: "stranger", status: "ok", imageBase64: "/9j/BBBB" }] }));
    const results = await createFaceMatchApi(http).fetchThumbnails([{ kind: "capture", id: "c1" }, { kind: "capture", id: "c2" }]);
    assert.deepEqual(results.map((item) => item.id), ["c1"]);
  }

  // ───────── 摄像头：只读列表 + 启停（整体替换时带回其余字段） ─────────
  {
    const { http, calls } = fakeHttp((call) => (call.method === "get" ? { items: [{ cameraId: "c1", storeCode: "1008", enabled: true }] } : { cameraId: "c1", storeCode: "1008", enabled: false }));
    const api = createFaceMatchApi(http);
    const list = await api.listCameras();
    assert.equal(calls[0].url, "/react/v1/face-cameras");
    assert.equal(list.items.length, 1);
    const camera = list.items[0];
    const updated = await api.updateCamera("c/1", buildCameraEnabledRequest({ ...camera, note: "Front", deviceModel: "M1", macAddress: "AA" }, false));
    assert.equal(calls[1].method, "put");
    assert.equal(calls[1].url, "/react/v1/face-cameras/c%2F1");
    assert.deepEqual(calls[1].data, { storeCode: "1008", note: "Front", deviceModel: "M1", macAddress: "AA", enabled: false });
    assert.equal(updated.enabled, false);
    // 不存在新建摄像头 / 重置令牌 / 查看令牌的方法
    const methods = Object.keys(api);
    assert.equal(methods.some((name) => /rotate|token|secret|createCamera/i.test(name)), false);
  }
}

run().then(
  () => console.log("api-core.test.ts: ok"),
  (error) => {
    console.error(error);
    process.exit(1);
  },
);
