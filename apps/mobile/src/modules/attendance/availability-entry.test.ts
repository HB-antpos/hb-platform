import assert from "node:assert/strict";
import {
  ALL_DAY_END,
  ALL_DAY_START,
  buildAvailabilityBatchPayload,
  getAvailabilityDraftError,
  isAllDayAvailability,
  type AvailabilityDraft,
} from "./availability-entry";

const draft: AvailabilityDraft = {
  workDates: ["2026-09-18", "2026-09-14", "2026-09-18"],
  allDay: true,
  startTime: "",
  endTime: "",
  note: "  Available  ",
};
assert.equal(getAvailabilityDraftError(draft), undefined, "全天不要求用户填写时间");
assert.deepEqual(buildAvailabilityBatchPayload(draft), {
  workDates: ["2026-09-14", "2026-09-18"],
  startTime: "00:00",
  endTime: "23:59:59",
  note: "Available",
  isUnavailable: false,
});
assert.equal(isAllDayAvailability("00:00:00", ALL_DAY_END), true);
assert.equal(isAllDayAvailability("00:00:00", "23:59:59.0000000"), true);
assert.equal(isAllDayAvailability("00:00", "23:59"), false, "指定到23:59的记录不能误标全天");
assert.equal(isAllDayAvailability("00:00:30", ALL_DAY_END), false, "有非零秒数的开始时间不能误标全天");
assert.equal(isAllDayAvailability("00:00:00.1", ALL_DAY_END), false);
assert.equal(isAllDayAvailability("09:00", "17:30"), false);
assert.equal(getAvailabilityDraftError({ ...draft, workDates: [] }), "datesRequired");
assert.equal(getAvailabilityDraftError({ ...draft, allDay: false }), "invalidTime");
assert.equal(getAvailabilityDraftError({ ...draft, allDay: false, startTime: "09:00", endTime: "09:00" }), "endAfterStart");
assert.equal(getAvailabilityDraftError({ ...draft, allDay: false, startTime: "17:30", endTime: "09:00" }), "endAfterStart");
assert.equal(getAvailabilityDraftError({ ...draft, allDay: false, startTime: "25:00", endTime: "25:30" }), "invalidTime");
assert.deepEqual(buildAvailabilityBatchPayload({
  ...draft, allDay: false, startTime: "09:15", endTime: "17:45", note: " ",
}), {
  workDates: ["2026-09-14", "2026-09-18"], startTime: "09:15", endTime: "17:45", note: undefined, isUnavailable: false,
});
// 不能上班沿用同一套日期与时段，只多一个类型标记；全天不能上班覆盖当天至最后一秒。
assert.deepEqual(buildAvailabilityBatchPayload({ ...draft, unavailable: true }), {
  workDates: ["2026-09-14", "2026-09-18"], startTime: ALL_DAY_START, endTime: ALL_DAY_END, note: "Available", isUnavailable: true,
});
assert.equal(draft.workDates.length, 3, "构建请求不能更改表单中的日期数组");
console.log("availability-entry.test.ts: ok");
