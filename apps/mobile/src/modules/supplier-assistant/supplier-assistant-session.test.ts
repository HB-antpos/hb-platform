import assert from "node:assert/strict";

import {
  createSupplierAssistantRequest,
  isAllowedSupplierAssistantPath,
  SupplierAssistantRequestError,
  type SupplierAssistantHttpRequest,
} from "./supplier-assistant-session";

async function main() {
  // 1. 代发：去掉 /api 前缀交给 apiClient，带插件版本头，原样回传业务信封。
  {
    const calls: SupplierAssistantHttpRequest[] = [];
    const request = createSupplierAssistantRequest(async (req) => {
      calls.push(req);
      return { status: 200, data: { success: true, data: { profiles: [] }, message: "查询成功" } };
    }, "1.7.1");
    const result = await request("/api/react/v1/browser-extension/supplier-profiles", { method: "GET" });
    assert.equal(calls.length, 1);
    assert.equal(calls[0]!.url, "/react/v1/browser-extension/supplier-profiles");
    assert.equal(calls[0]!.method, "GET");
    assert.equal(calls[0]!.headers["X-HB-Extension-Version"], "1.7.1");
    // 令牌由 apiClient 拦截器加，这里不能自己塞 Authorization。
    assert.equal(calls[0]!.headers.Authorization, undefined);
    assert.equal("data" in calls[0]!, false);
    assert.deepEqual(result, {
      httpStatus: 200,
      ok: true,
      success: true,
      data: { profiles: [] },
      message: "查询成功",
      errorCode: null,
    });
  }

  // 2. POST 带请求体。
  {
    const calls: SupplierAssistantHttpRequest[] = [];
    const request = createSupplierAssistantRequest(async (req) => {
      calls.push(req);
      return { status: 200, data: { success: true, data: { ok: 1 } } };
    }, "1.7.1");
    await request("/api/react/v1/browser-extension/product-purchase-cycles", {
      method: "POST",
      body: { storeCode: "1042", supplierCode: "240", itemNumber: "VD028" },
    });
    assert.deepEqual(calls[0]!.data, { storeCode: "1042", supplierCode: "240", itemNumber: "VD028" });
  }

  // 3. 接口返回 403 / 429 等（axios 抛带 response 的错误）：照实交给插件脚本，不往外抛。
  {
    const request = createSupplierAssistantRequest(async () => {
      throw Object.assign(new Error("Request failed with status code 403"), {
        response: { status: 403, data: { success: false, message: "无权访问", errorCode: "FORBIDDEN" } },
      });
    }, "1.7.1");
    const result = await request("/api/react/v1/browser-extension/stores", { method: "GET" });
    assert.equal(result.httpStatus, 403);
    assert.equal(result.ok, false);
    assert.equal(result.success, false);
    assert.equal(result.message, "无权访问");
    assert.equal(result.errorCode, "FORBIDDEN");
  }

  // 4. 断网（没有 response）照常抛出，由调用方显示「连接失败」。
  {
    const networkError = new Error("Network Error");
    const request = createSupplierAssistantRequest(async () => {
      throw networkError;
    }, "1.7.1");
    await assert.rejects(
      () => request("/api/react/v1/browser-extension/stores", { method: "GET" }),
      (error) => error === networkError,
    );
  }

  // 5. 只允许订货助手接口：网页借道访问员工账号其他接口一律拒绝，且不发请求。
  {
    let called = false;
    const request = createSupplierAssistantRequest(async () => {
      called = true;
      return { status: 200, data: {} };
    }, "1.7.1");
    for (const path of [
      "/api/users/me",
      "/api/react/v1/browser-extension/../../users",
      "/api/react/v1/browser-extension//users",
      "/api/react/v1/browser-extension/stores?x=1",
      "/api/react/v1/browser-extension/stores#x",
      "https://evil.example/api/react/v1/browser-extension/stores",
    ]) {
      assert.equal(isAllowedSupplierAssistantPath(path), false, path);
      await assert.rejects(
        () => request(path, { method: "GET" }),
        (error) => error instanceof SupplierAssistantRequestError && error.code === "PATH_NOT_ALLOWED",
      );
    }
    assert.equal(called, false);
    assert.equal(isAllowedSupplierAssistantPath("/api/react/v1/browser-extension/barcode-lookup"), true);
  }

  console.log("supplier-assistant-session: ok");
}

void main().catch((error) => {
  console.error(error);
  process.exit(1);
});
