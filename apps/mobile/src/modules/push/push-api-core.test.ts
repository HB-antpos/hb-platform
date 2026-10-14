// 推送设备接口：路径、请求体字段、令牌不进 URL、静默请求头、账号绑定。
import assert from "node:assert/strict";
import type { AxiosRequestConfig } from "axios";
import { EXPECTED_ACCOUNT_HEADER } from "@/shared/api/account-bound-request-header";
import { createPushDevicesApi, type PushDevicesHttp } from "./push-api-core";
import { PUSH_DEVICES_PATH } from "./push-types";

interface Recorded {
  method: "post" | "delete";
  url: string;
  data?: unknown;
  config?: AxiosRequestConfig;
}

function fakeHttp(onCall?: (call: Recorded) => void) {
  const calls: Recorded[] = [];
  const http: PushDevicesHttp = {
    post: async (url, data, config) => {
      const call: Recorded = { method: "post", url, data, config };
      calls.push(call);
      onCall?.(call);
      return { data: { pushDeviceId: "d1" } };
    },
    delete: async (url, config) => {
      const call: Recorded = { method: "delete", url, data: config?.data, config };
      calls.push(call);
      onCall?.(call);
      return { data: null };
    },
  };
  return { http, calls };
}

const TOKEN = "ExponentPushToken[abcdEFGH0123456789]";
const ACCOUNT = "11111111-2222-3333-4444-555555555555";

async function run() {
  assert.equal(PUSH_DEVICES_PATH, "/react/v1/notifications/push-devices", "apiClient 的 baseURL 已带 /api，路径不再加前缀");

  // ───────── 上报：字段齐全 ─────────
  {
    const { http, calls } = fakeHttp();
    await createPushDevicesApi(http).register(
      { expoPushToken: TOKEN, platform: "android", appVersion: "1.0.11", runtimeVersion: "1.0.11" },
      ACCOUNT,
      { timeoutMs: 15_000 },
    );
    assert.equal(calls.length, 1);
    assert.equal(calls[0].method, "post");
    assert.equal(calls[0].url, PUSH_DEVICES_PATH);
    assert.deepEqual(calls[0].data, { expoPushToken: TOKEN, platform: "android", appVersion: "1.0.11", runtimeVersion: "1.0.11" });
    assert.equal(calls[0].config?.timeout, 15_000);
    const headers = calls[0].config?.headers as Record<string, string>;
    assert.equal(headers["X-Skip-Center-Log"], "1", "失败（含 404）不进中心日志");
    assert.equal(headers[EXPECTED_ACCOUNT_HEADER], ACCOUNT, "请求绑定发起时的账号，期间换号会在本地被拒");
  }

  // ───────── 上报：可选字段缺失时不出现在请求体里 ─────────
  {
    const { http, calls } = fakeHttp();
    await createPushDevicesApi(http).register({ expoPushToken: TOKEN, platform: "ios", appVersion: "", runtimeVersion: undefined }, ACCOUNT);
    assert.deepEqual(calls[0].data, { expoPushToken: TOKEN, platform: "ios" });
    assert.equal("timeout" in (calls[0].config ?? {}), false, "不传 timeoutMs 就不覆盖 apiClient 默认超时");
  }

  // ───────── 注销：DELETE + 令牌在请求体里 ─────────
  {
    const { http, calls } = fakeHttp();
    const controller = new AbortController();
    await createPushDevicesApi(http).unregister(TOKEN, ACCOUNT, { signal: controller.signal, timeoutMs: 3000 });
    assert.equal(calls[0].method, "delete");
    assert.equal(calls[0].url, PUSH_DEVICES_PATH);
    assert.deepEqual(calls[0].data, { expoPushToken: TOKEN });
    assert.equal(calls[0].config?.timeout, 3000);
    assert.equal(calls[0].config?.signal, controller.signal, "超时时可中止请求");
    const headers = calls[0].config?.headers as Record<string, string>;
    assert.equal(headers["X-Skip-Center-Log"], "1");
    assert.equal(headers[EXPECTED_ACCOUNT_HEADER], ACCOUNT);
  }

  // ───────── 令牌绝不进 URL / 查询串 ─────────
  {
    const { http, calls } = fakeHttp();
    const api = createPushDevicesApi(http);
    await api.register({ expoPushToken: TOKEN, platform: "ios" }, ACCOUNT);
    await api.unregister(TOKEN, ACCOUNT);
    for (const call of calls) {
      assert.equal(call.url.includes("ExponentPushToken") || call.url.includes(encodeURIComponent(TOKEN)), false, "URL 里不能出现令牌");
      assert.equal(call.url.includes("?"), false, "没有查询串");
      assert.equal(call.config?.params, undefined, "不使用 params 传令牌");
    }
  }

  // ───────── 失败原样抛出，由调用方静默 ─────────
  {
    const http: PushDevicesHttp = {
      post: async () => {
        throw Object.assign(new Error("Request failed with status code 404"), { response: { status: 404 } });
      },
      delete: async () => {
        throw new Error("Network Error");
      },
    };
    const api = createPushDevicesApi(http);
    await assert.rejects(api.register({ expoPushToken: TOKEN, platform: "ios" }, ACCOUNT), /404/);
    await assert.rejects(api.unregister(TOKEN, ACCOUNT), /Network Error/);
  }

  // ───────── 账号为空：本地拒绝，不发请求 ─────────
  {
    const { http, calls } = fakeHttp();
    await assert.rejects(createPushDevicesApi(http).register({ expoPushToken: TOKEN, platform: "ios" }, "  "), /ACCOUNT_SESSION_CHANGED/);
    assert.equal(calls.length, 0);
  }

  console.log("push-api-core.test.ts: ok");
}

void run();
