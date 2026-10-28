import assert from "node:assert/strict";
import {
  DEFAULT_SHIFT,
  SHIFT_PRESETS,
  buildCopyPayload,
  buildMoveUpdatePayload,
  buildPresetPayload,
  buildTransferCreatePayload,
  collectPunchScheduleGuids,
  getAvailabilityConflict,
  getMealBadge,
  getPlacementVerdict,
  isScheduleLocked,
  rangesOverlap,
} from "./schedule-placement";
import type { PlacementSource } from "./schedule-placement";
import type { AttendanceAvailability, AttendanceSchedule } from "./types";

const schedule = (extra: Partial<AttendanceSchedule> = {}): AttendanceSchedule => ({
  scheduleGuid: "s-1",
  storeCode: "1013",
  userGuid: "u-sean",
  workDate: "2026-10-26T00:00:00",
  startTime: "09:00:00",
  endTime: "17:30:00",
  status: "Draft",
  remark: "备注",
  isMine: false,
  ...extra,
});

const availability = (
  workDate: string,
  startTime: string,
  endTime: string,
  extra: Partial<AttendanceAvailability> = {},
): AttendanceAvailability => ({
  availabilityGuid: `${workDate}-${startTime}-${endTime}`,
  userGuid: "u-staff",
  workDate,
  startTime,
  endTime,
  status: "Submitted",
  ...extra,
});

// 固定常用班次与默认时段（与 Web 一致）
assert.deepEqual(
  SHIFT_PRESETS.map((item) => `${item.startTime}-${item.endTime}`),
  ["09:00-13:00", "16:00-17:30", "09:00-17:00", "09:00-21:00", "09:00-19:00"],
);
assert.deepEqual(DEFAULT_SHIFT, { startTime: "09:00", endTime: "17:30" });

// 时段重叠：首尾相接不算；带秒 / 脏数据
assert.equal(rangesOverlap("09:00", "13:00", "12:00", "17:00"), true);
assert.equal(rangesOverlap("09:00", "13:00", "13:00", "17:00"), false);
assert.equal(rangesOverlap("09:00:00", "17:30:00", "16:00", "17:30"), true);
assert.equal(rangesOverlap("17:00", "09:00", "09:00", "17:00"), false);
assert.equal(rangesOverlap("", "09:00", "09:00", "17:00"), false);

// 员工上报冲突：不能上班（含全天 00:00–23:59:59）→ unavailable；超出可上班 → outsideAvailable；没上报 / 已取消 → 无
assert.equal(getAvailabilityConflict({ startTime: "09:00", endTime: "17:30" }, undefined), undefined);
assert.equal(
  getAvailabilityConflict({ startTime: "09:00", endTime: "17:30" }, [availability("2026-10-27", "00:00:00", "23:59:59", { isUnavailable: true })]),
  "unavailable",
);
assert.equal(
  getAvailabilityConflict({ startTime: "09:00", endTime: "17:30" }, [availability("2026-10-27", "18:00", "22:00", { isUnavailable: true })]),
  undefined,
);
assert.equal(
  getAvailabilityConflict({ startTime: "08:00", endTime: "12:00" }, [availability("2026-10-30", "09:00", "13:00")]),
  "outsideAvailable",
);
assert.equal(
  getAvailabilityConflict({ startTime: "09:00", endTime: "13:00" }, [availability("2026-10-30", "09:00", "13:00")]),
  undefined,
);
// 可上班两段首尾相接合并后可整段覆盖
assert.equal(
  getAvailabilityConflict({ startTime: "09:00", endTime: "15:00" }, [availability("d", "09:00", "12:00"), availability("d", "12:00", "17:00")]),
  undefined,
);
assert.equal(
  getAvailabilityConflict({ startTime: "09:00", endTime: "17:30" }, [availability("d", "09:00", "17:30", { status: "Cancelled" }), availability("d", "09:00", "12:00")]),
  "outsideAvailable",
);
// 只上报不能上班且不重叠：不判断
assert.equal(
  getAvailabilityConflict({ startTime: "09:00", endTime: "17:00" }, [availability("d", "18:00", "22:00", { isUnavailable: true })]),
  undefined,
);
// 重叠优先于超出
assert.equal(
  getAvailabilityConflict({ startTime: "09:00", endTime: "17:30" }, [availability("d", "09:00", "12:00"), availability("d", "13:00", "14:00", { isUnavailable: true })]),
  "unavailable",
);
// 跨午夜班次不判断
assert.equal(getAvailabilityConflict({ startTime: "20:00", endTime: "02:00" }, [availability("d", "00:00", "23:59", { isUnavailable: true })]), undefined);

// 落点判定
const existing = (startTime: string, endTime: string, status = "Draft", scheduleGuid = "x") => ({ scheduleGuid, startTime, endTime, status });
const move = (extra: Partial<AttendanceSchedule> = {}): PlacementSource => ({
  kind: "move",
  schedule: schedule(extra),
  startTime: "09:00",
  endTime: "17:30",
});
assert.deepEqual(getPlacementVerdict(move(), { userGuid: "u-staff", workDate: "2026-10-28", schedules: [] }), { state: "ok" });
// 移动回原格：noop
assert.deepEqual(getPlacementVerdict(move(), { userGuid: "u-sean", workDate: "2026-10-26", schedules: [existing("09:00", "17:30", "Draft", "s-1")] }), { state: "noop" });
// 复制到原格：与自己重叠 → bad
assert.deepEqual(
  getPlacementVerdict({ ...move(), kind: "copy" }, { userGuid: "u-sean", workDate: "2026-10-26", schedules: [existing("09:00", "17:30", "Draft", "s-1")] }),
  { state: "bad", reason: "overlap" },
);
// 与目标格已有班次重叠 → bad；已取消的不算；同一天不重叠的可并排
assert.deepEqual(
  getPlacementVerdict(move(), { userGuid: "u-staff", workDate: "2026-10-29", schedules: [existing("09:00", "17:00")] }),
  { state: "bad", reason: "overlap" },
);
assert.deepEqual(
  getPlacementVerdict(move(), { userGuid: "u-staff", workDate: "2026-10-29", schedules: [existing("09:00", "17:00", "Cancelled")] }),
  { state: "ok" },
);
assert.deepEqual(
  getPlacementVerdict({ kind: "preset", startTime: "16:00", endTime: "17:30" }, { userGuid: "u-staff", workDate: "2026-10-29", schedules: [existing("09:00", "13:00")] }),
  { state: "ok" },
);
// 同一天内移动：忽略它自己
assert.deepEqual(
  getPlacementVerdict(
    { kind: "move", schedule: schedule({ startTime: "09:00", endTime: "13:00" }), startTime: "09:00", endTime: "13:00" },
    { userGuid: "u-sean", workDate: "2026-10-27", schedules: [existing("09:00", "13:00", "Draft", "s-1")] },
  ),
  { state: "ok" },
);
// 提醒（只提醒不阻止），重叠优先
const off = [availability("2026-10-27", "00:00", "23:59", { isUnavailable: true })];
assert.deepEqual(
  getPlacementVerdict(move(), { userGuid: "u-staff", workDate: "2026-10-27", schedules: [], availability: off }),
  { state: "warn", reason: "unavailable" },
);
assert.deepEqual(
  getPlacementVerdict(move(), { userGuid: "u-staff", workDate: "2026-10-27", schedules: [existing("10:00", "12:00")], availability: off }),
  { state: "bad", reason: "overlap" },
);

// 锁定：有打卡 → 锁；看得到打卡时未来 / 今天没打卡的不锁；看不到时今天及以前保守锁
const punchGuids = collectPunchScheduleGuids([{ scheduleGuid: "s-1" }, { scheduleGuid: undefined }, { scheduleGuid: null }, { scheduleGuid: "s-2" }]);
assert.deepEqual(Array.from(punchGuids).sort(), ["s-1", "s-2"]);
const lock = (over: Partial<Parameters<typeof isScheduleLocked>[0]>) => isScheduleLocked({
  scheduleGuid: "s-9", workDate: "2026-10-29T00:00:00", today: "2026-10-28", punchScheduleGuids: punchGuids, punchesKnown: true, ...over,
});
assert.equal(lock({ scheduleGuid: "s-1" }), true);
assert.equal(lock({}), false);
assert.equal(lock({ workDate: "2026-10-28" }), false);
assert.equal(lock({ punchesKnown: false, workDate: "2026-10-28" }), true);
assert.equal(lock({ punchesKnown: false, workDate: "2026-10-26T00:00:00" }), true);
assert.equal(lock({ punchesKnown: false, workDate: "2026-10-29" }), false);

// 各操作的载荷
const manual = schedule({ status: "Active", mealBreakCount: 0 });
assert.deepEqual(buildMoveUpdatePayload(manual, "2026-10-29"), {
  workDate: "2026-10-29", startTime: "09:00", endTime: "17:30", status: "Active", remark: "备注",
});
assert.deepEqual(buildTransferCreatePayload(manual, "u-staff", "2026-10-28"), {
  storeCode: "1013", userGuid: "u-staff", workDate: "2026-10-28", startTime: "09:00", endTime: "17:30", status: "Active", remark: "备注", mealBreakCount: 0,
});
assert.equal(buildTransferCreatePayload(schedule(), "u-staff", "2026-10-28").mealBreakCount, undefined);
const copy = buildCopyPayload(manual, "u-staff", "2026-10-28");
assert.equal(copy.status, "Draft");
assert.equal(copy.mealBreakCount, 0);
assert.deepEqual(buildPresetPayload("1013", "u-staff", "2026-10-28", { startTime: "16:00", endTime: "17:30" }), {
  storeCode: "1013", userGuid: "u-staff", workDate: "2026-10-28", startTime: "16:00", endTime: "17:30", status: "Draft",
});

// 用餐标记：只有手动指定（含 0）才有
assert.deepEqual(getMealBadge(0), { count: 0 });
assert.deepEqual(getMealBadge(2), { count: 2 });
assert.equal(getMealBadge(undefined), undefined);
assert.equal(getMealBadge(null), undefined);

console.log("schedule-placement tests passed");
