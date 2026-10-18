import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createInstance } from "i18next";
import {
  arrivalInsightText,
  buildArrivalDatePatch,
  containerStatusLabel,
  countSheetFilters,
  dateRangeFieldLabel,
  describeContainerError,
  describeDateRange,
  diffDays,
  findInvalidDateRange,
  formatShortDate,
  getArrivalInsight,
  toDateOnly,
  type ContainerTranslate,
} from "./container-list-logic";

const TODAY = "2026-10-01";

// 用真实语言包建独立 i18next 实例，分别取中文 / 英文的 containerDetail 翻译函数。
const localesDir = join(dirname(fileURLToPath(import.meta.url)), "../../locales");
const readLocale = (path: string) => JSON.parse(readFileSync(join(localesDir, path), "utf8")) as Record<string, unknown>;
const i18n = createInstance();
// 资源内联，initAsync: false 让 init 同步完成（测试按 CJS 运行，不能用顶层 await）。
void i18n.init({
  initAsync: false,
  lng: "zh",
  fallbackLng: false,
  ns: ["containerDetail", "common"],
  defaultNS: "containerDetail",
  resources: {
    zh: { containerDetail: readLocale("zh/screens/containerDetail.json"), common: readLocale("zh/common.json") },
    en: { containerDetail: readLocale("en/screens/containerDetail.json"), common: readLocale("en/common.json") },
  },
  interpolation: { escapeValue: false },
  compatibilityJSON: "v4",
  returnNull: false,
});
const zhT: ContainerTranslate = i18n.getFixedT("zh", "containerDetail");
const enT: ContainerTranslate = i18n.getFixedT("en", "containerDetail");
const textOf = (insight: ReturnType<typeof getArrivalInsight>, t: ContainerTranslate) =>
  insight ? arrivalInsightText(insight, t) : null;

// 接口日期只取日期部分，不经 Date 解析。
assert.equal(toDateOnly("2026-10-08T00:00:00"), "2026-10-08");
assert.equal(toDateOnly(undefined), "");
assert.equal(formatShortDate("2026-10-08T00:00:00"), "10-08");
assert.equal(diffDays("2026-09-28", "2026-10-01"), 3);
assert.equal(diffDays("2026-10-01", "2026-09-28"), -3);

// 未登记实际到库：逾期 / 今天 / 几天后。
const overdue = getArrivalInsight({ 预计到岸日期: "2026-09-28T00:00:00", 状态: 1 }, TODAY);
assert.deepEqual(overdue, { kind: "overdue", days: 3, tone: "warning", target: "estimated" });
assert.equal(textOf(overdue, zhT), "已逾期 3 天");
assert.equal(textOf(overdue, enT), "3 days overdue");
const dueToday = getArrivalInsight({ 预计到岸日期: "2026-10-01", 状态: 0 }, TODAY);
assert.deepEqual(dueToday, { kind: "today", days: 0, tone: "accent", target: "estimated" });
assert.equal(textOf(dueToday, zhT), "今天到库");
assert.equal(textOf(dueToday, enT), "Due today");
const upcoming = getArrivalInsight({ 预计到岸日期: "2026-10-08", 状态: 1 }, TODAY);
assert.deepEqual(upcoming, { kind: "upcoming", days: 7, tone: "muted", target: "estimated" });
assert.equal(textOf(upcoming, zhT), "7 天后");
assert.equal(textOf(upcoming, enT), "in 7 days");
assert.equal(textOf(getArrivalInsight({ 预计到岸日期: "2026-10-02", 状态: 1 }, TODAY), enT), "in 1 day");
assert.equal(textOf(getArrivalInsight({ 预计到岸日期: "2026-09-30", 状态: 1 }, TODAY), enT), "1 day overdue");
// 已完成、已取消、没有预计日期都不提示逾期。
assert.equal(getArrivalInsight({ 预计到岸日期: "2026-09-01", 状态: 2 }, TODAY), null);
assert.equal(getArrivalInsight({ 预计到岸日期: "2026-09-01", 状态: 7 }, TODAY), null);
assert.equal(getArrivalInsight({ 状态: 1 }, TODAY), null);

// 已登记实际到库：与预计比较晚到/早到，准时不提示。
const late = getArrivalInsight({ 预计到岸日期: "2026-09-22", 实际到货日期: "2026-09-24T00:00:00", 状态: 2 }, TODAY);
assert.deepEqual(late, { kind: "late", days: 2, tone: "warning", target: "actual" });
assert.equal(textOf(late, zhT), "晚 2 天");
assert.equal(textOf(late, enT), "2 days late");
const early = getArrivalInsight({ 预计到岸日期: "2026-09-22", 实际到货日期: "2026-09-21", 状态: 1 }, TODAY);
assert.deepEqual(early, { kind: "early", days: 1, tone: "muted", target: "actual" });
assert.equal(textOf(early, zhT), "早 1 天");
assert.equal(textOf(early, enT), "1 day early");
assert.equal(getArrivalInsight({ 预计到岸日期: "2026-09-22", 实际到货日期: "2026-09-22" }, TODAY), null);
assert.equal(getArrivalInsight({ 实际到货日期: "2026-09-22" }, TODAY), null);

// 日期补丁只带变化字段；清空发送显式标记，不发送空字符串。
assert.equal(
  buildArrivalDatePatch(
    { estimated: "2026-10-08T00:00:00", actual: "" },
    { estimated: "2026-10-08", actual: "" },
  ),
  null,
);
assert.deepEqual(
  buildArrivalDatePatch({ estimated: "2026-10-08T00:00:00", actual: "" }, { estimated: "2026-10-08", actual: "2026-10-01" }),
  { 实际到货日期: "2026-10-01" },
);
assert.deepEqual(
  buildArrivalDatePatch({ estimated: "2026-10-08", actual: "2026-10-09" }, { estimated: "", actual: "" }),
  { ClearEstimatedArrivalDate: true, ClearActualArrivalDate: true },
);
assert.deepEqual(
  buildArrivalDatePatch({ estimated: "", actual: "" }, { estimated: "2026-10-12", actual: "" }),
  { 预计到岸日期: "2026-10-12" },
);
// 补丁里绝不会同时出现同一日期的赋值与清空（后端会 400）。
const patch = buildArrivalDatePatch({ estimated: "2026-10-08", actual: "2026-10-09" }, { estimated: "2026-10-10", actual: "" });
assert.deepEqual(patch, { 预计到岸日期: "2026-10-10", ClearActualArrivalDate: true });

// 区间校验：单边允许，开始晚于结束报对应区间名。
assert.equal(findInvalidDateRange({ loadingDateStart: "2026-09-01" }), null);
assert.equal(findInvalidDateRange({ loadingDateStart: "2026-09-10", loadingDateEnd: "2026-09-01" }), "loading");
assert.equal(
  findInvalidDateRange({ actualArrivalDateStart: "2026-10-02", actualArrivalDateEnd: "2026-10-01" }),
  "actual",
);
assert.equal(dateRangeFieldLabel("loading", zhT), "装柜日期");
assert.equal(dateRangeFieldLabel("estimated", zhT), "预计到库");
assert.equal(dateRangeFieldLabel("actual", zhT), "实际到库");
assert.equal(dateRangeFieldLabel("loading", enT), "Loading date");
assert.equal(dateRangeFieldLabel("estimated", enT), "ETA");
assert.equal(dateRangeFieldLabel("actual", enT), "Arrived");

// 角标计数：货号、每个日期区间、非默认排序各算一项。
assert.equal(countSheetFilters({}), 0);
assert.equal(countSheetFilters({ dateType: "预计到岸日期", containerNumberFilter: "HB", statuses: [1] }), 0);
assert.equal(
  countSheetFilters({
    itemNumberFilter: " A1 ",
    loadingDateStart: "2026-09-01",
    loadingDateEnd: "2026-09-30",
    actualArrivalDateEnd: "2026-10-01",
    dateType: "装柜日期",
  }),
  4,
);

assert.equal(describeDateRange(zhT, "2026-09-01", "2026-09-30"), "2026-09-01 至 2026-09-30");
assert.equal(describeDateRange(zhT, "2026-09-01", undefined), "2026-09-01 起");
assert.equal(describeDateRange(zhT, undefined, "2026-09-30"), "截至 2026-09-30");
assert.equal(describeDateRange(zhT), "");
assert.equal(describeDateRange(enT, "2026-09-01", "2026-09-30"), "2026-09-01 to 2026-09-30");
assert.equal(describeDateRange(enT, "2026-09-01", undefined), "from 2026-09-01");
assert.equal(describeDateRange(enT, undefined, "2026-09-30"), "until 2026-09-30");
assert.equal(describeDateRange(enT), "");

// 状态名：中文与改动前一致，英文与货柜明细页共用。
assert.deepEqual([0, 1, 2, 7].map((status) => containerStatusLabel(status, zhT)), ["已装柜", "在途", "已完成", "已取消"]);
assert.deepEqual([0, 1, 2, 7].map((status) => containerStatusLabel(status, enT)), ["Loaded", "In transit", "Completed", "Cancelled"]);
assert.equal(containerStatusLabel(5, zhT), "状态 5");
assert.equal(containerStatusLabel(undefined, zhT), "状态 --");
assert.equal(containerStatusLabel(undefined, enT), "Status --");

// 错误提示：中文照旧显示原文；英文不显示中文原文，按 code / fallback / 通用错误翻译。
const serverError = new Error("货柜编号已存在");
assert.equal(describeContainerError(serverError, "创建货柜失败", "zh", zhT), "货柜编号已存在");
assert.equal(describeContainerError("boom", "创建货柜失败", "zh", zhT), "创建货柜失败");
assert.equal(describeContainerError(serverError, "Couldn't create the container.", "en", enT), "Couldn't create the container.");
assert.equal(describeContainerError(new Error("Enter a container no."), "x", "en", enT), "Enter a container no.");
const noChanges = Object.assign(new Error("没有需要保存的修改"), { code: "CONTAINER_EDIT_NO_CHANGES" });
assert.equal(describeContainerError(noChanges, "保存明细失败", "zh", zhT), "没有需要保存的修改");
assert.equal(describeContainerError(noChanges, "Failed to save the row", "en", enT), "No changes to save.");
const invalidNumber = Object.assign(new Error("编辑字段存在无效数字"), { code: "CONTAINER_EDIT_INVALID_NUMBER" });
assert.equal(describeContainerError(invalidNumber, "Failed to save the row", "en", enT), "Some fields have invalid numbers. Check and try again.");
// 本地轮询超时（中文原文含「超时」）在英文下归为通用超时提示。
assert.equal(describeContainerError(new Error("货柜任务轮询超时"), "x", "en", enT), enT("common:errors.timeout"));
// 没有 fallback 时英文给通用失败提示，而不是空白或中文。
assert.equal(describeContainerError(new Error("查询货柜明细失败"), "", "en", enT), enT("common:errors.requestFailed"));
assert.doesNotMatch(enT("common:errors.requestFailed"), /[\u3400-\u9fff]/);

console.log("container-list-logic.test.ts: ok");
