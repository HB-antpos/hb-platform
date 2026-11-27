import assert from "node:assert/strict";
import { test } from "node:test";

import { CashierLoginError } from "./cashier-login-controller";
import {
  buildCashierLoginDiagnostic,
  cashierLoginErrorCode,
  classifyCashierBarcode,
  relationToPreviousBarcode,
  toCashierLoginLogDraft,
} from "./cashier-login-diagnostics";

import { HbposApiError } from "@/core/api/hbpos-api";
import { sanitizeProperties } from "@/core/logging/application-log";

test("条码字符类别只看结构：纯数字、字母数字、含其它字符、内部空白与控制字符", () => {
  assert.deepEqual(classifyCashierBarcode(" 1042701 "), {
    characterClass: "digits",
    hasWhitespace: false,
    hasControlCharacter: false,
  });
  assert.equal(classifyCashierBarcode("HB42AB").characterClass, "alphanumeric");
  assert.equal(classifyCashierBarcode("HB-42").characterClass, "other");
  assert.equal(classifyCashierBarcode("  ").characterClass, "empty");
  assert.equal(classifyCashierBarcode("10 42").hasWhitespace, true);
  assert.equal(classifyCashierBarcode("1042\r").hasControlCharacter, true);
});

test("与上一枪的关系能区分截断、拼接与无关", () => {
  assert.equal(relationToPreviousBarcode("1042701", null), "none");
  assert.equal(relationToPreviousBarcode("1042701", "1042701"), "same");
  assert.equal(relationToPreviousBarcode("1042", "1042701"), "prefix-of-previous");
  assert.equal(relationToPreviousBarcode("10427011042", "1042701"), "extends-previous");
  assert.equal(relationToPreviousBarcode("9910427011", "1042701"), "contains-previous");
  assert.equal(relationToPreviousBarcode("4270", "1042701"), "inside-previous");
  assert.equal(relationToPreviousBarcode("5555", "1042701"), "different");
});

test("错误码只取类型、状态与业务码，不取消息", () => {
  assert.equal(
    cashierLoginErrorCode(
      new HbposApiError("收银员条码无效或已停用 1042701", {
        kind: "http",
        status: 401,
        code: "CASHIER_LOGIN_FAILED",
      }),
    ),
    "http:401:CASHIER_LOGIN_FAILED",
  );
  assert.equal(
    cashierLoginErrorCode(
      new HbposApiError("Network Error", { kind: "transport", networkCode: "ERR_NETWORK" }),
    ),
    "transport:ERR_NETWORK",
  );
  assert.equal(
    cashierLoginErrorCode(new CashierLoginError("RUNTIME_NOT_READY")),
    "RUNTIME_NOT_READY",
  );
  assert.equal(cashierLoginErrorCode(new TypeError("1042701")), "TypeError");
  assert.equal(cashierLoginErrorCode("1042701"), "unknown");
});

test("中心日志草稿为 Warning，属性 key 不会被敏感词过滤吞掉，且不含条码内容", () => {
  const diagnostic = buildCashierLoginDiagnostic({
    outcome: "rejected",
    trigger: "enter",
    submitted: "1042",
    previousSubmitted: "1042701",
    error: new HbposApiError("rejected 1042", {
      kind: "http",
      status: 401,
      code: "CASHIER_LOGIN_FAILED",
    }),
    trace: {
      nativeTextLength: 11,
      staleNativePrefixLength: 7,
      jsMirrorMatched: false,
      inputDurationMs: 42,
      maxCharacterGapMs: 230,
      changeEvents: 4,
    },
  });
  const draft = toCashierLoginLogDraft(diagnostic);
  assert.equal(draft.level, "Warning");
  assert.equal(draft.category, "cashier-login.barcode");

  const properties = sanitizeProperties(draft.properties);
  assert.ok(properties);
  assert.equal(properties["[REDACTED]"], undefined);
  assert.deepEqual(properties, {
    outcome: "rejected",
    trigger: "enter",
    submittedLength: "4",
    characterClass: "digits",
    hasWhitespace: "false",
    hasControlCharacter: "false",
    relationToPrevious: "prefix-of-previous",
    errorCode: "http:401:CASHIER_LOGIN_FAILED",
    nativeTextLength: "11",
    staleNativePrefixLength: "7",
    jsMirrorMatched: "false",
    inputDurationMs: "42",
    maxCharacterGapMs: "230",
    changeEvents: "4",
  });
  assert.doesNotMatch(JSON.stringify(draft), /1042/u);
});

test("路由层隐藏输入 / 蓝牙来源没有输入轨迹时只写结构字段", () => {
  const draft = toCashierLoginLogDraft(
    buildCashierLoginDiagnostic({
      outcome: "rejected",
      trigger: "hid-capture",
      submitted: "HB42",
      previousSubmitted: null,
      error: new Error("boom"),
      trace: null,
    }),
  );
  assert.deepEqual(Object.keys(draft.properties ?? {}).sort(), [
    "characterClass",
    "errorCode",
    "hasControlCharacter",
    "hasWhitespace",
    "outcome",
    "relationToPrevious",
    "submittedLength",
    "trigger",
  ]);
});
