import { CashierLoginError } from "./cashier-login-controller";

import { HbposApiError } from "@/core/api/hbpos-api";
import type { ApplicationLogDraft } from "@/core/logging/application-log";

/**
 * 收银员扫码登录诊断。
 *
 * 用途：服务端只能看到「条码查不到」（CashierService 日志 StoreMs=0），看不到 iPad 实际提交了什么；
 * 这里把「提交上去的条码长什么样」脱敏后写进中心日志，用来区分截断、与上一枪拼接、JS 镜像与原生文本不一致等情况。
 *
 * 安全约束：绝不记录条码原文、片段、哈希或指纹（员工码位数少，哈希可被穷举还原），
 * 只记长度、字符类别、与上一次提交的「前缀 / 包含」关系这类结构信息。
 */

/** 本次登录由什么触发。enter=扫码枪 / 键盘回车；scan-idle=扫码节奏停顿自动提交；hid-capture / ble=路由层隐藏输入或蓝牙。 */
export type CashierLoginTrigger =
  | "enter"
  | "scan-idle"
  | "button"
  | "hid-capture"
  | "ble";

/** 本次提交与上一次提交的关系，只描述结构，不含内容。 */
export type CashierLoginRelation =
  | "none"
  | "same"
  | "prefix-of-previous"
  | "extends-previous"
  | "contains-previous"
  | "inside-previous"
  | "different";

export type CashierLoginCharacterClass = "empty" | "digits" | "alphanumeric" | "other";

export type CashierLoginInputTrace = Readonly<{
  /** 原生输入框回传的完整文本长度（含未清掉的旧前缀）。 */
  nativeTextLength: number;
  /** 原生文本里属于上一次提交、清空被原生丢弃后残留的前缀长度。 */
  staleNativePrefixLength: number;
  /** JS 侧显示镜像是否与本次提交内容一致；不一致说明原生与 JS 已错位。 */
  jsMirrorMatched: boolean;
  /** 从首个字符到提交的耗时；扫码枪通常 < 150ms。 */
  inputDurationMs: number;
  /** 相邻两次 onChangeText 的最大间隔；偏大说明一枪被拆成多段送达。 */
  maxCharacterGapMs: number;
  /** 本次提交前收到的 onChangeText 次数。 */
  changeEvents: number;
}>;

export type CashierLoginDiagnostic = Readonly<{
  outcome: "rejected" | "accepted-after-mismatch";
  trigger: CashierLoginTrigger;
  submittedLength: number;
  characterClass: CashierLoginCharacterClass;
  hasWhitespace: boolean;
  hasControlCharacter: boolean;
  relationToPrevious: CashierLoginRelation;
  errorCode: string;
  trace: CashierLoginInputTrace | null;
}>;

export function classifyCashierBarcode(value: string): Readonly<{
  characterClass: CashierLoginCharacterClass;
  hasWhitespace: boolean;
  hasControlCharacter: boolean;
}> {
  const trimmed = value.trim();
  const characterClass: CashierLoginCharacterClass =
    trimmed.length === 0
      ? "empty"
      : /^[0-9]+$/u.test(trimmed)
        ? "digits"
        : /^[0-9A-Za-z]+$/u.test(trimmed)
          ? "alphanumeric"
          : "other";
  return {
    characterClass,
    // 只看两端以外的空白：首尾空白会被 controller trim 掉，不影响结果。
    hasWhitespace: /\s/u.test(trimmed),
    hasControlCharacter: /[\u0000-\u001f\u007f]/u.test(value),
  };
}

/** 比较本次与上一次提交（均为已 trim 的值），判断是不是截断或拼接。 */
export function relationToPreviousBarcode(
  current: string,
  previous: string | null,
): CashierLoginRelation {
  if (previous === null || previous.length === 0) return "none";
  if (current === previous) return "same";
  if (current.length === 0) return "different";
  if (previous.startsWith(current)) return "prefix-of-previous";
  if (current.startsWith(previous)) return "extends-previous";
  if (current.includes(previous)) return "contains-previous";
  if (previous.includes(current)) return "inside-previous";
  return "different";
}

/** 只取错误码，不取错误消息（消息里可能拼了请求内容）。 */
export function cashierLoginErrorCode(error: unknown): string {
  if (error instanceof CashierLoginError) return error.code;
  if (error instanceof HbposApiError) {
    return [
      error.kind,
      error.status === undefined ? null : String(error.status),
      error.code ?? error.networkCode ?? null,
    ]
      .filter((part): part is string => part !== null && part.length > 0)
      .join(":");
  }
  return error instanceof Error ? error.name : "unknown";
}

export function buildCashierLoginDiagnostic(input: Readonly<{
  outcome: CashierLoginDiagnostic["outcome"];
  trigger: CashierLoginTrigger;
  submitted: string;
  previousSubmitted: string | null;
  error: unknown;
  trace: CashierLoginInputTrace | null;
}>): CashierLoginDiagnostic {
  const trimmed = input.submitted.trim();
  return Object.freeze({
    outcome: input.outcome,
    trigger: input.trigger,
    submittedLength: trimmed.length,
    ...classifyCashierBarcode(input.submitted),
    relationToPrevious: relationToPreviousBarcode(
      trimmed,
      input.previousSubmitted,
    ),
    errorCode: input.error === null ? "none" : cashierLoginErrorCode(input.error),
    trace: input.trace,
  });
}

/**
 * 中心日志只收 Warning 及以上，所以诊断统一按 Warning 写。
 * 属性 key 避开 application-log 的敏感词过滤（含 card / pin / token 等会被整体替换成 [REDACTED]）。
 */
export function toCashierLoginLogDraft(
  diagnostic: CashierLoginDiagnostic,
): ApplicationLogDraft {
  const trace = diagnostic.trace;
  return {
    level: "Warning",
    message:
      diagnostic.outcome === "rejected"
        ? "Cashier barcode sign-in was rejected."
        : "Cashier barcode sign-in succeeded after native/JS input mismatch.",
    category: "cashier-login.barcode",
    properties: {
      outcome: diagnostic.outcome,
      trigger: diagnostic.trigger,
      submittedLength: diagnostic.submittedLength,
      characterClass: diagnostic.characterClass,
      hasWhitespace: diagnostic.hasWhitespace,
      hasControlCharacter: diagnostic.hasControlCharacter,
      relationToPrevious: diagnostic.relationToPrevious,
      errorCode: diagnostic.errorCode,
      ...(trace
        ? {
            nativeTextLength: trace.nativeTextLength,
            staleNativePrefixLength: trace.staleNativePrefixLength,
            jsMirrorMatched: trace.jsMirrorMatched,
            inputDurationMs: trace.inputDurationMs,
            maxCharacterGapMs: trace.maxCharacterGapMs,
            changeEvents: trace.changeEvents,
          }
        : {}),
    },
  };
}
