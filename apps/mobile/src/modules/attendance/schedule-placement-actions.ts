import {
  buildCopyPayload,
  buildMoveUpdatePayload,
  buildPresetPayload,
  buildTransferCreatePayload,
} from "./schedule-placement";
import type { PlacementSource } from "./schedule-placement";
import type {
  AttendanceSchedule,
  AttendanceSchedulePayload,
  AttendanceScheduleUpdatePayload,
} from "./types";

/**
 * 「点选目标格」放下之后怎么落地（与 Web 周排班的拖放同一套流程，API 以参数注入，便于单测）：
 * - 常用班次添加 / 复制：新建草稿；
 * - 同员工换日期：调用「更新排班」，保持原状态；
 * - 换员工：后端更新接口不改员工，所以「新建 + 删除原班次」。新建接口一律建成草稿，
 *   有效班次要在新建后再补一次更新改回有效；撤销时恢复已删除排班（会变草稿）再改回有效。
 */

export interface PlacementApi {
  createSchedule: (payload: AttendanceSchedulePayload) => Promise<AttendanceSchedule>;
  updateSchedule: (scheduleGuid: string, payload: AttendanceScheduleUpdatePayload) => Promise<AttendanceSchedule>;
  deleteSchedule: (scheduleGuid: string) => Promise<void>;
  restoreSchedule: (scheduleGuid: string) => Promise<void>;
}

export interface PlacementTargetCell {
  storeCode: string;
  userGuid: string;
  /** YYYY-MM-DD */
  workDate: string;
}

export type PlacementOutcome =
  /** 同员工换日期（草稿） */
  | "moved"
  /** 移动了已发布（有效）的班次，直接生效 */
  | "movedPublished"
  | "copied"
  | "added"
  /** 换员工：已在新位置创建，但原班次删除失败，需要手动删除原班次 */
  | "transferDeleteFailed";

export interface PlacementResult {
  outcome: PlacementOutcome;
  /** 有效班次换员工后新班次没能改回有效（停在草稿，需要发布本周）。 */
  downgraded: boolean;
  /** 撤销；没有可撤销内容（如删除原班次失败）时为空。 */
  undo?: () => Promise<void>;
}

export async function executePlacement(
  api: PlacementApi,
  source: PlacementSource,
  target: PlacementTargetCell,
): Promise<PlacementResult> {
  const origin = source.schedule;

  if (source.kind === "preset") {
    const created = await api.createSchedule(
      buildPresetPayload(target.storeCode, target.userGuid, target.workDate, {
        startTime: source.startTime,
        endTime: source.endTime,
      }),
    );
    return { outcome: "added", downgraded: false, undo: () => api.deleteSchedule(created.scheduleGuid) };
  }
  if (!origin) throw new Error("placement source has no schedule");

  if (source.kind === "copy") {
    const created = await api.createSchedule(buildCopyPayload(origin, target.userGuid, target.workDate));
    return { outcome: "copied", downgraded: false, undo: () => api.deleteSchedule(created.scheduleGuid) };
  }

  const originDate = origin.workDate.slice(0, 10);
  const published = origin.status.toLowerCase() === "active";

  if (origin.userGuid === target.userGuid) {
    await api.updateSchedule(origin.scheduleGuid, buildMoveUpdatePayload(origin, target.workDate));
    return {
      outcome: published ? "movedPublished" : "moved",
      downgraded: false,
      undo: async () => {
        await api.updateSchedule(origin.scheduleGuid, buildMoveUpdatePayload(origin, originDate));
      },
    };
  }

  // 换员工：先新建，再（有效班次）补状态，最后删除原班次——任何一步失败都不会丢班次
  const created = await api.createSchedule(buildTransferCreatePayload(origin, target.userGuid, target.workDate));
  let downgraded = false;
  if (published) {
    try {
      const { storeCode: _storeCode, userGuid: _userGuid, ...updatePayload } = buildTransferCreatePayload(
        origin,
        target.userGuid,
        target.workDate,
      );
      await api.updateSchedule(created.scheduleGuid, updatePayload);
    } catch {
      downgraded = true;
    }
  }
  try {
    await api.deleteSchedule(origin.scheduleGuid);
  } catch {
    return { outcome: "transferDeleteFailed", downgraded };
  }
  return {
    outcome: published && !downgraded ? "movedPublished" : "moved",
    downgraded,
    undo: async () => {
      // 恢复已删除排班会变成草稿（后端不留原状态），有效班次要再改回有效
      await api.deleteSchedule(created.scheduleGuid);
      await api.restoreSchedule(origin.scheduleGuid);
      if (published) {
        await api.updateSchedule(origin.scheduleGuid, buildMoveUpdatePayload(origin, originDate));
      }
    },
  };
}
