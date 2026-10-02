import type { AttendanceAvailabilityBatchPayload } from "./types";
import { normalizeAvailabilityDates } from "./availability-dates";

export const ALL_DAY_START = "00:00";
export const ALL_DAY_END = "23:59:59";

export interface AvailabilityDraft {
  workDates: string[];
  allDay: boolean;
  startTime: string;
  endTime: string;
  note: string;
  /** true＝填写的是不能上班时间。 */
  unavailable?: boolean;
}

export function isAllDayAvailability(startTime: string, endTime: string) {
  return /^00:00(?::00(?:\.0+)?)?$/.test(startTime)
    && /^23:59:59(?:\.0+)?$/.test(endTime);
}

export function getAvailabilityDraftError(draft: AvailabilityDraft) {
  if (!normalizeAvailabilityDates(draft.workDates).length) return "datesRequired";
  if (draft.allDay) return undefined;
  const timePattern = /^(?:[01]\d|2[0-3]):[0-5]\d$/;
  if (!timePattern.test(draft.startTime) || !timePattern.test(draft.endTime)) {
    return "invalidTime";
  }
  if (draft.endTime <= draft.startTime) return "endAfterStart";
  return undefined;
}

export function buildAvailabilityBatchPayload(draft: AvailabilityDraft): AttendanceAvailabilityBatchPayload {
  const error = getAvailabilityDraftError(draft);
  if (error) throw new Error(error);
  return {
    workDates: normalizeAvailabilityDates(draft.workDates),
    // 沿用服务端的时间段字段，全天覆盖当天至最后一秒，用户无需输入时间。
    startTime: draft.allDay ? ALL_DAY_START : draft.startTime,
    endTime: draft.allDay ? ALL_DAY_END : draft.endTime,
    note: draft.note.trim() || undefined,
    isUnavailable: Boolean(draft.unavailable),
  };
}
