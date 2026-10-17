import assert from "node:assert/strict";
import test from "node:test";
import { assertCurrentUserAuthorized, createRejectedError, parseRefreshResponse } from "./raw-auth-parse";
import { getHttpStatus, isRejectedByServer } from "./switch-flow";

test("刷新成功：兼容带 data 信封与直接返回两种形状", () => {
  const pair = { accessToken: "at", refreshToken: "rt" };
  assert.deepEqual(parseRefreshResponse({ success: true, data: pair }), pair);
  assert.deepEqual(parseRefreshResponse(pair), pair);
});

test("刷新被拒：HTTP 200 + success:false 要识别为「服务器明确拒绝」（线上真实行为）", () => {
  for (const body of [
    { success: false, message: "刷新令牌无效或已过期" },
    { isSuccess: false, message: "x" },
    { success: true, data: { accessToken: "", refreshToken: "" } },
    { success: true },
    null,
    "oops",
  ]) {
    let caught: unknown;
    try {
      parseRefreshResponse(body);
    } catch (error) {
      caught = error;
    }
    assert.ok(caught, `应当抛错：${JSON.stringify(body)}`);
    assert.equal(getHttpStatus(caught), 401);
    assert.equal(isRejectedByServer(caught), true);
  }
});

test("被拒错误保留服务器给的文案", () => {
  assert.equal(createRejectedError("刷新令牌无效或已过期").message, "刷新令牌无效或已过期");
  assert.equal(createRejectedError("").message, "Rejected by server");
});

test("读取当前用户：未授权信封视为被拒绝，其它情况放行交给调用方", () => {
  const isUnauth = (body: unknown) => (body as { code?: number } | null)?.code === 401;
  assert.throws(() => assertCurrentUserAuthorized({ success: false, code: 401, message: "未登录" }, isUnauth), (error) =>
    isRejectedByServer(error)
  );
  assert.doesNotThrow(() => assertCurrentUserAuthorized({ success: true, data: {} }, isUnauth));
});

test("网络 / 5xx 错误不算被拒绝", () => {
  assert.equal(isRejectedByServer(new Error("Network Error")), false);
  assert.equal(isRejectedByServer(Object.assign(new Error("x"), { response: { status: 503 } })), false);
});
