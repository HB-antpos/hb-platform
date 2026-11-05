import assert from "node:assert/strict";
import {
  getPasswordResetRuleState,
  isResetCodeFormatValid,
  isValidResetEmail,
  normalizeResetCode,
  runWithPreAuthGateOpen,
} from "./password-reset";

assert.equal(isValidResetEmail(" staff@example.com "), true);
assert.equal(isValidResetEmail("staff@example"), false);
assert.equal(isValidResetEmail(""), false);

// 从邮件复制的验证码可能带空格或横线，只保留 6 位数字。
assert.equal(normalizeResetCode("12 34-56"), "123456");
assert.equal(normalizeResetCode("1234567"), "123456");
assert.equal(isResetCodeFormatValid("12345"), false);
assert.equal(isResetCodeFormatValid("123 456"), true);

assert.deepEqual(getPasswordResetRuleState("abc", ""), { minLength: false, confirmed: false });
assert.deepEqual(getPasswordResetRuleState(" MyOwn456 ", "MyOwn456"), { minLength: true, confirmed: true });
assert.deepEqual(getPasswordResetRuleState("MyOwn456", "MyOwn45"), { minLength: true, confirmed: false });

async function testPreAuthGate() {
  const calls: string[] = [];
  const gate = (closed: boolean) => ({
    isClosed: () => closed,
    open: () => calls.push("open"),
    close: () => calls.push("close"),
  });

  // iOS 正式包未登录：请求期间打开，结束后恢复。
  assert.equal(await runWithPreAuthGateOpen(gate(true), async () => {
    calls.push("request");
    return "sent";
  }), "sent");
  assert.deepEqual(calls.splice(0), ["open", "request", "close"]);

  // 请求失败也要恢复，错误原样抛给页面。
  await assert.rejects(runWithPreAuthGateOpen(gate(true), async () => {
    calls.push("request");
    throw new Error("boom");
  }), /boom/);
  assert.deepEqual(calls.splice(0), ["open", "request", "close"]);

  // 守卫本来开着（安卓、开发包、已存登录状态）：不能碰，否则真实会话会被关回本地通道。
  await runWithPreAuthGateOpen(gate(false), async () => {
    calls.push("request");
  });
  assert.deepEqual(calls.splice(0), ["request"]);
}

void testPreAuthGate().then(() => console.log("password-reset tests passed"));
