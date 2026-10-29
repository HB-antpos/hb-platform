import assert from "node:assert/strict";
import { executePlacement } from "./schedule-placement-actions";
import type { PlacementApi } from "./schedule-placement-actions";
import type { PlacementSource } from "./schedule-placement";
import type { AttendanceSchedule } from "./types";

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

// 记录每次调用的假 API；可指定某个动作失败
function fakeApi(fail: Partial<Record<"create" | "update" | "delete" | "restore", boolean>> = {}) {
  const calls: string[] = [];
  let seq = 100;
  const api: PlacementApi = {
    createSchedule: async (payload) => {
      calls.push(`create ${payload.userGuid} ${payload.workDate} ${payload.status} meal=${payload.mealBreakCount ?? "auto"}`);
      if (fail.create) throw new Error("create failed");
      return schedule({ scheduleGuid: `new-${seq++}`, userGuid: payload.userGuid, workDate: payload.workDate, status: "Draft" });
    },
    updateSchedule: async (guid, payload) => {
      calls.push(`update ${guid} ${payload.workDate} ${payload.status}`);
      if (fail.update) throw new Error("update failed");
      return schedule({ scheduleGuid: guid });
    },
    deleteSchedule: async (guid) => {
      calls.push(`delete ${guid}`);
      if (fail.delete) throw new Error("delete failed");
    },
    restoreSchedule: async (guid) => {
      calls.push(`restore ${guid}`);
      if (fail.restore) throw new Error("restore failed");
    },
  };
  return { api, calls };
}

const target = { storeCode: "1013", userGuid: "u-staff", workDate: "2026-10-28" };
const move = (extra: Partial<AttendanceSchedule> = {}): PlacementSource => ({ kind: "move", schedule: schedule(extra), startTime: "09:00", endTime: "17:30" });

async function main() {
  // 托盘 / 常用班次添加：新建草稿；撤销 = 删除新建的
  {
    const { api, calls } = fakeApi();
    const result = await executePlacement(api, { kind: "preset", startTime: "16:00", endTime: "17:30" }, target);
    assert.equal(result.outcome, "added");
    assert.deepEqual(calls, ["create u-staff 2026-10-28 Draft meal=auto"]);
    await result.undo?.();
    assert.equal(calls[1], "delete new-100");
  }

  // 复制：新建草稿（有效班次也是草稿），手动用餐次数带过去
  {
    const { api, calls } = fakeApi();
    const result = await executePlacement(api, { ...move({ status: "Active", mealBreakCount: 0 }), kind: "copy" }, target);
    assert.equal(result.outcome, "copied");
    assert.deepEqual(calls, ["create u-staff 2026-10-28 Draft meal=0"]);
  }

  // 同员工换日期：一次更新，保持原状态；撤销改回原日期
  {
    const { api, calls } = fakeApi();
    const draft = await executePlacement(api, move(), { ...target, userGuid: "u-sean" });
    assert.equal(draft.outcome, "moved");
    assert.deepEqual(calls, ["update s-1 2026-10-28 Draft"]);
    await draft.undo?.();
    assert.equal(calls[1], "update s-1 2026-10-26 Draft");

    const active = fakeApi();
    const published = await executePlacement(active.api, move({ status: "Active" }), { ...target, userGuid: "u-sean" });
    assert.equal(published.outcome, "movedPublished");
    assert.deepEqual(active.calls, ["update s-1 2026-10-28 Active"]);
  }

  // 换员工（草稿）：新建 + 删除，不需要补状态；撤销 = 删新建 + 恢复原班次
  {
    const { api, calls } = fakeApi();
    const result = await executePlacement(api, move(), target);
    assert.equal(result.outcome, "moved");
    assert.deepEqual(calls, ["create u-staff 2026-10-28 Draft meal=auto", "delete s-1"]);
    await result.undo?.();
    assert.deepEqual(calls.slice(2), ["delete new-100", "restore s-1"]);
  }

  // 换员工（有效）：新建一律是草稿 → 补一次更新改回有效 → 删除原班次；撤销多一步把恢复出来的草稿改回有效
  {
    const { api, calls } = fakeApi();
    const result = await executePlacement(api, move({ status: "Active", mealBreakCount: 2 }), target);
    assert.equal(result.outcome, "movedPublished");
    assert.equal(result.downgraded, false);
    assert.deepEqual(calls, ["create u-staff 2026-10-28 Active meal=2", "update new-100 2026-10-28 Active", "delete s-1"]);
    await result.undo?.();
    assert.deepEqual(calls.slice(3), ["delete new-100", "restore s-1", "update s-1 2026-10-26 Active"]);
  }

  // 换员工（有效）补状态失败：新班次停在草稿（downgraded），仍会删除原班次并如实标记
  {
    const { api, calls } = fakeApi({ update: true });
    const result = await executePlacement(api, move({ status: "Active" }), target);
    assert.equal(result.downgraded, true);
    assert.equal(result.outcome, "moved");
    assert.deepEqual(calls.filter((item) => item.startsWith("delete")), ["delete s-1"]);
  }

  // 换员工删除原班次失败：保留两条、没有撤销，结果标记 transferDeleteFailed
  {
    const { api, calls } = fakeApi({ delete: true });
    const result = await executePlacement(api, move(), target);
    assert.equal(result.outcome, "transferDeleteFailed");
    assert.equal(result.undo, undefined);
    assert.equal(calls.length, 2);
  }

  // 新建失败：直接抛出，不删除原班次
  {
    const { api, calls } = fakeApi({ create: true });
    await assert.rejects(() => executePlacement(api, move(), target), /create failed/);
    assert.deepEqual(calls, ["create u-staff 2026-10-28 Draft meal=auto"]);
  }
  console.log("schedule-placement-actions tests passed");
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
