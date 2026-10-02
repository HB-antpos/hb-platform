import { normalizeAvailabilityDates, startOfAvailabilityWeek } from "./availability-dates";
import type {
  AttendanceAvailability,
  AttendanceAvailabilityBatchPayload,
} from "./types";

export interface AvailabilityBatchSegment {
  availableDate: string;
  startTime: string;
  endTime: string;
  remark?: string;
  /** true＝不能上班。 */
  isUnavailable?: boolean;
}

export interface AvailabilityBatchWeekPayload {
  storeCode?: string;
  weekStartDate: string;
  segments: AvailabilityBatchSegment[];
}

export interface AvailabilityBatchDependencies {
  getWeek: (storeCode?: string, weekStartDate?: string) => Promise<AttendanceAvailability[]>;
  createWeek: (payload: AvailabilityBatchWeekPayload) => Promise<AttendanceAvailability[]>;
}

export interface AvailabilityBatchSaveErrorFields {
  savedDates: string[];
  remainingDates: string[];
  uncertainDates: string[];
}

export class AvailabilityBatchSaveError extends Error {
  readonly savedDates: string[];
  readonly remainingDates: string[];
  readonly uncertainDates: string[];

  constructor(
    message: string,
    fields: AvailabilityBatchSaveErrorFields,
    cause?: unknown,
  ) {
    super(message);
    this.name = "AvailabilityBatchSaveError";
    this.savedDates = [...fields.savedDates];
    this.remainingDates = [...fields.remainingDates];
    this.uncertainDates = [...fields.uncertainDates];
    if (cause !== undefined) {
      this.cause = cause;
    }
  }
}

interface AvailabilityWeek {
  weekStartDate: string;
  dates: string[];
}

function canonicalDate(value: string) {
  return value.trim().slice(0, 10);
}

/** 后端有的响应带秒、有的响应不带秒，比较时统一到秒精度。 */
function canonicalTime(value: string) {
  const normalized = value.trim().split(".")[0];
  return /^\d{2}:\d{2}$/.test(normalized) ? `${normalized}:00` : normalized;
}

function canonicalNote(value?: string) {
  return value?.trim() ?? "";
}

function isUsableAvailability(row: AttendanceAvailability, storeCode?: string) {
  if (!row.availabilityGuid.trim()) return false;
  const status = row.status.trim().toLowerCase();
  if (status === "cancelled" || status === "deleted") return false;
  if (storeCode !== undefined && row.storeCode?.trim() !== storeCode.trim()) return false;
  return true;
}

function matchesAvailability(
  row: AttendanceAvailability,
  segment: AvailabilityBatchSegment,
  storeCode?: string,
) {
  return isUsableAvailability(row, storeCode)
    && canonicalDate(row.workDate) === segment.availableDate
    && canonicalTime(row.startTime) === canonicalTime(segment.startTime)
    && canonicalTime(row.endTime) === canonicalTime(segment.endTime)
    && canonicalNote(row.note) === canonicalNote(segment.remark)
    // 类型也要一致：旧后端会把「不能上班」存成可上班，回读不一致时按未确认处理，绝不当作已保存。
    && Boolean(row.isUnavailable) === Boolean(segment.isUnavailable);
}

function groupPayloadByWeek(payload: AttendanceAvailabilityBatchPayload): AvailabilityWeek[] {
  const dates = normalizeAvailabilityDates(payload.workDates);
  const grouped = new Map<string, string[]>();
  for (const date of dates) {
    const weekStartDate = startOfAvailabilityWeek(date);
    grouped.set(weekStartDate, [...(grouped.get(weekStartDate) ?? []), date]);
  }

  return [...grouped.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([weekStartDate, weekDates]) => ({
      weekStartDate,
      dates: [...weekDates].sort((left, right) => left.localeCompare(right)),
    }));
}

function getSegment(payload: AttendanceAvailabilityBatchPayload, date: string): AvailabilityBatchSegment {
  return {
    availableDate: date,
    startTime: payload.startTime,
    endTime: payload.endTime,
    remark: payload.note,
    isUnavailable: Boolean(payload.isUnavailable),
  };
}

function matchesCreateResponse(
  rows: AttendanceAvailability[],
  segments: AvailabilityBatchSegment[],
  storeCode?: string,
) {
  if (rows.length !== segments.length) return false;

  const guids = new Set<string>();
  const unmatched = [...segments];
  for (const row of rows) {
    const guid = row.availabilityGuid.trim();
    if (guids.has(guid)) return false;
    guids.add(guid);
    const matchIndex = unmatched.findIndex((segment) => matchesAvailability(row, segment, storeCode));
    if (matchIndex < 0) return false;
    unmatched.splice(matchIndex, 1);
  }
  return unmatched.length === 0;
}

function datesNotSaved(allDates: string[], savedDates: Set<string>) {
  return allDates.filter((date) => !savedDates.has(date));
}

function saveError(
  message: string,
  allDates: string[],
  savedDates: Set<string>,
  uncertainDates: string[],
  cause?: unknown,
) {
  return new AvailabilityBatchSaveError(
    message,
    {
      savedDates: allDates.filter((date) => savedDates.has(date)),
      remainingDates: datesNotSaved(allDates, savedDates),
      uncertainDates: [...uncertainDates],
    },
    cause,
  );
}

async function runAvailabilityBatch(
  payload: AttendanceAvailabilityBatchPayload,
  dependencies: AvailabilityBatchDependencies,
  verifyOnly: boolean,
) {
  const weeks = groupPayloadByWeek(payload);
  const allDates = weeks.flatMap((week) => week.dates);
  const savedDates = new Set<string>();
  const savedRows = new Map<string, AttendanceAvailability>();

  for (const week of weeks) {
    let existing: AttendanceAvailability[];
    try {
      existing = await dependencies.getWeek(payload.storeCode, week.weekStartDate);
    } catch (error) {
      if (verifyOnly) throw error;
      throw saveError(
        "读取已有可上班时间失败，未发送保存请求",
        allDates,
        savedDates,
        [],
        error,
      );
    }

    const segments = week.dates.map((date) => getSegment(payload, date));
    const pendingSegments: AvailabilityBatchSegment[] = [];
    for (const segment of segments) {
      const existingRow = existing.find((row) => matchesAvailability(row, segment, payload.storeCode));
      if (existingRow) {
        savedDates.add(segment.availableDate);
        savedRows.set(segment.availableDate, existingRow);
      } else {
        pendingSegments.push(segment);
      }
    }

    if (verifyOnly || pendingSegments.length === 0) continue;

    let created: AttendanceAvailability[];
    try {
      created = await dependencies.createWeek({
        storeCode: payload.storeCode,
        weekStartDate: week.weekStartDate,
        segments: pendingSegments,
      });
      if (matchesCreateResponse(created, pendingSegments, payload.storeCode)) {
        for (const row of created) {
          savedDates.add(canonicalDate(row.workDate));
          savedRows.set(canonicalDate(row.workDate), row);
        }
        continue;
      }
      throw new Error("Availability create response did not match the request");
    } catch (postError) {
      let confirmed: AttendanceAvailability[];
      try {
        confirmed = await dependencies.getWeek(payload.storeCode, week.weekStartDate);
      } catch (readbackError) {
        throw saveError(
          "可上班时间保存结果未知，暂时无法核对",
          allDates,
          savedDates,
          pendingSegments.map((segment) => segment.availableDate),
          readbackError,
        );
      }

      const unconfirmedDates: string[] = [];
      for (const segment of pendingSegments) {
        const confirmedRow = confirmed.find((row) => matchesAvailability(row, segment, payload.storeCode));
        if (confirmedRow) {
          savedDates.add(segment.availableDate);
          savedRows.set(segment.availableDate, confirmedRow);
        } else {
          unconfirmedDates.push(segment.availableDate);
        }
      }

      if (unconfirmedDates.length) {
        throw saveError(
          "可上班时间保存结果未知，请先核对未确认日期",
          allDates,
          savedDates,
          unconfirmedDates,
          postError,
        );
      }
    }
  }

  return allDates.map((date) => savedRows.get(date)).filter(
    (row): row is AttendanceAvailability => Boolean(row),
  );
}

export function createAvailabilityBatch(
  payload: AttendanceAvailabilityBatchPayload,
  dependencies: AvailabilityBatchDependencies,
): Promise<AttendanceAvailability[]> {
  return runAvailabilityBatch(payload, dependencies, false);
}

export async function verifyAvailabilityBatch(
  payload: AttendanceAvailabilityBatchPayload,
  dependencies: AvailabilityBatchDependencies,
): Promise<string[]> {
  const rows = await runAvailabilityBatch(payload, dependencies, true);
  return rows.map((row) => canonicalDate(row.workDate));
}
