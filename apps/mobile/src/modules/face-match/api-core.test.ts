import assert from "node:assert/strict";
import type { AxiosRequestConfig } from "axios";
import { fromByteArray } from "base64-js";
import { CREATE_ENTRY_TIMEOUT_MS, IMAGE_TIMEOUT_MS, createFaceMatchApi, detectImageMime, type FaceMatchHttp } from "./api-core";
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
