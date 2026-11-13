import assert from "node:assert/strict";
import { resolvePosSettingsErrorMessage } from "./errors";

const t = (key: string) => `T(${key})`;

// 非管理员改全局层：后端 403 + message，直接显示后端文案（不能被通用「登录失效」盖住）
{
  const error = { response: { status: 403, data: { success: false, message: "只有管理员可以修改全局收银端设置。" } } };
  assert.equal(resolvePosSettingsErrorMessage(error, { t, language: "zh" }), "只有管理员可以修改全局收银端设置。");
  assert.equal(resolvePosSettingsErrorMessage(error, { t, language: "en" }), "只有管理员可以修改全局收银端设置。", "英文界面同样显示后端 message");
}
// 按项目约定 details 优先于 message
assert.equal(
  resolvePosSettingsErrorMessage({ response: { status: 400, data: { message: "校验失败", details: { values: ["未知设置项 x"] } } } }, { t, language: "zh" }),
  "未知设置项 x",
);
// 200 + success=false 抛出的业务错误
{
  const error = Object.assign(new Error("门店不存在"), { apiBusinessError: true });
  assert.equal(resolvePosSettingsErrorMessage(error, { t, language: "en" }), "门店不存在");
}
// 没有业务文案：网络 / 超时走通用文案，其余走兜底键
assert.equal(resolvePosSettingsErrorMessage({ code: "ERR_NETWORK", message: "Network Error" }, { t, language: "zh" }), "T(common:errors.network)");
assert.equal(
  resolvePosSettingsErrorMessage({ response: { status: 500, data: {} } }, { t, language: "zh" }),
  "T(common:errors.server)",
);
assert.equal(
  resolvePosSettingsErrorMessage({ response: { status: 403, data: {} } }, { t, language: "en", fallbackKey: "posSettings:confirm.saveFailed" }),
  "T(common:errors.unauthorized)",
  "403 但后端没给文案时才退回通用提示",
);
assert.equal(
  resolvePosSettingsErrorMessage(undefined, { t, language: "en", fallbackKey: "posSettings:confirm.saveFailed" }),
  "T(posSettings:confirm.saveFailed)",
);

console.log("errors.test.ts: ok");
