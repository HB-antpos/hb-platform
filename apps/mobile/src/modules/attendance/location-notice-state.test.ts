import assert from "node:assert/strict";
import {
  resolveLocationNoticeVariant,
  type LocationNoticeHeroVariant,
  type ShiftLocationTrackingState,
} from "@/modules/attendance/location-notice-state";

function resolve(
  heroVariant: LocationNoticeHeroVariant,
  tracking: ShiftLocationTrackingState,
  options: { allowPunch?: boolean; hasClockedOut?: boolean } = {},
) {
  return resolveLocationNoticeVariant({
    heroVariant,
    allowPunch: options.allowPunch ?? true,
    tracking,
    hasClockedOut: options.hasClockedOut ?? false,
  });
}

// 上班前：不管定位状态是否读到，都先给说明卡，让员工在扫码前就能看到。
assert.equal(resolve("before", "inactive"), "notice");
assert.equal(resolve("before", "unknown"), "notice");

// 上班中：只有任务真的在运行才显示「已开启」；读取中或没在采集都不显示，不撒谎。
assert.equal(resolve("working", "active"), "active");
assert.equal(resolve("working", "unknown"), "none");
assert.equal(resolve("working", "inactive"), "none");

// 下班后：确认已停止且今天真的下过班才显示「已停止」。
assert.equal(resolve("summary", "inactive", { hasClockedOut: true }), "stopped");
// 读取中不显示，避免先闪「已停止」；没下过班（例如纯假期、历史空白）也不显示。
assert.equal(resolve("summary", "unknown", { hasClockedOut: true }), "none");
assert.equal(resolve("summary", "inactive", { hasClockedOut: false }), "none");

// 真实在采集优先于其他形态（例如已汇总但任务仍在跑，要如实告知）。
assert.equal(resolve("summary", "active", { hasClockedOut: true }), "active");
assert.equal(resolve("before", "active"), "active");

// 历史日期不显示任何定位说明。
assert.equal(resolve("before", "inactive", { allowPunch: false }), "none");
assert.equal(resolve("summary", "active", { allowPunch: false }), "none");

console.log("location-notice-state.test.ts: ok");
