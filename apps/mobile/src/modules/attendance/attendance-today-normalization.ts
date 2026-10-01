import type {
  AttendancePunch,
  AttendancePunchSegment,
  AttendancePunchType,
  AttendanceSchedule,
  AttendanceScheduleSession,
  AttendanceStoreHoliday,
  AttendanceStorePunchState,
  AttendanceToday,
} from "./types";

type ApiRecord = Record<string, unknown>;

function isRecord(value: unknown): value is ApiRecord {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function pick(raw: ApiRecord, ...keys: string[]) {
  for (const key of keys) {
    if (raw[key] !== undefined && raw[key] !== null) {
      return raw[key];
    }
  }
  return undefined;
}

function asString(value: unknown, fallback = "") {
  if (typeof value === "string") return value;
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  return fallback;
}

function asOptionalString(value: unknown) {
  const normalized = asString(value).trim();
  return normalized || undefined;
}

function asDateString(value: unknown) {
  const normalized = asString(value);
  return normalized.includes("T") ? normalized.slice(0, 10) : normalized;
}

function asBoolean(value: unknown, fallback = false) {
  if (typeof value === "boolean") return value;
  if (typeof value === "string") {
    if (value.toLowerCase() === "true") return true;
    if (value.toLowerCase() === "false") return false;
  }
  return fallback;
}

function asOptionalBoolean(value: unknown) {
  if (value === undefined || value === null) return undefined;
  return asBoolean(value);
}

function asOptionalNumber(value: unknown) {
  if (typeof value === "number" && Number.isFinite(value)) return value;
  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : undefined;
  }
  return undefined;
}

function records(value: unknown): ApiRecord[] {
  if (Array.isArray(value)) return value.filter(isRecord);
  if (!isRecord(value)) return [];
  const nested = pick(value, "items", "Items", "rows", "Rows", "data", "Data");
  return Array.isArray(nested) ? nested.filter(isRecord) : [];
}

function normalizePunch(raw: ApiRecord, fallbackType?: AttendancePunchType): AttendancePunch {
  const punchType = asString(pick(raw, "punchType", "PunchType"), fallbackType ?? "ClockIn");
  const effectivePunchTime = asOptionalString(
    pick(raw, "effectivePunchTime", "EffectivePunchTime", "requestedPunchTimeLocal", "RequestedPunchTimeLocal"),
  );
  return {
    punchGuid: asString(pick(raw, "punchGuid", "PunchGuid", "guid", "Guid")),
    scheduleGuid: asOptionalString(pick(raw, "scheduleGuid", "ScheduleGuid")),
    storeCode: asOptionalString(pick(raw, "storeCode", "StoreCode")),
    storeName: asOptionalString(pick(raw, "storeName", "StoreName")),
    workDate: asDateString(pick(raw, "workDate", "WorkDate")),
    punchType,
    punchTimeUtc: asOptionalString(pick(raw, "punchTimeUtc", "PunchTimeUtc")),
    punchTimeLocal: asOptionalString(pick(raw, "punchTimeLocal", "PunchTimeLocal")) ?? effectivePunchTime,
    effectivePunchTime,
    status: asString(pick(raw, "status", "Status"), "Normal"),
    statusReason: asOptionalString(pick(raw, "statusReason", "StatusReason", "reason", "Reason")),
    locationLatitude: asOptionalNumber(pick(raw, "locationLatitude", "LocationLatitude")),
    locationLongitude: asOptionalNumber(pick(raw, "locationLongitude", "LocationLongitude")),
    locationAccuracy: asOptionalNumber(pick(raw, "locationAccuracy", "LocationAccuracy")),
    locationPermissionStatus: asOptionalString(pick(raw, "locationPermissionStatus", "LocationPermissionStatus")),
    locationCapturedAtUtc: asOptionalString(pick(raw, "locationCapturedAtUtc", "LocationCapturedAtUtc")),
    userGuid: asOptionalString(pick(raw, "userGuid", "UserGuid")),
    employeeName: asOptionalString(pick(raw, "employeeName", "EmployeeName")),
    posDeviceCode: asOptionalString(pick(raw, "posDeviceCode", "PosDeviceCode")),
    serverTimeUtc: asOptionalString(pick(raw, "serverTimeUtc", "ServerTimeUtc")),
    segmentIndex: asOptionalNumber(pick(raw, "segmentIndex", "SegmentIndex", "segmentNumber", "SegmentNumber")),
    segmentStatus: asOptionalString(pick(raw, "segmentStatus", "SegmentStatus")),
    isBreakBoundary: asBoolean(pick(raw, "isBreakBoundary", "IsBreakBoundary")),
    supersedesPunchGuid: asOptionalString(pick(raw, "supersedesPunchGuid", "SupersedesPunchGuid")),
    adjustmentGuid: asOptionalString(pick(raw, "adjustmentGuid", "AdjustmentGuid")),
    earlyArrivalMinutes: asOptionalNumber(pick(raw, "earlyArrivalMinutes", "EarlyArrivalMinutes")),
    lateMinutes: asOptionalNumber(pick(raw, "lateMinutes", "LateMinutes")),
    earlyLeaveMinutes: asOptionalNumber(pick(raw, "earlyLeaveMinutes", "EarlyLeaveMinutes")),
    lateDepartureMinutes: asOptionalNumber(pick(raw, "lateDepartureMinutes", "LateDepartureMinutes")),
  };
}

function normalizeSegmentPunch(value: unknown, punchType: AttendancePunchType) {
  if (isRecord(value)) return normalizePunch(value, punchType);
  const timestamp = asOptionalString(value);
  return timestamp
    ? normalizePunch({ punchType, effectivePunchTime: timestamp }, punchType)
    : undefined;
}

function normalizeSegment(raw: ApiRecord, index: number): AttendancePunchSegment {
  const segmentIndex =
    asOptionalNumber(pick(raw, "segmentIndex", "SegmentIndex", "segmentNumber", "SegmentNumber")) ?? index + 1;
  return {
    segmentIndex,
    segmentNumber: segmentIndex,
    clockIn: normalizeSegmentPunch(pick(raw, "clockIn", "ClockIn"), "ClockIn"),
    clockOut: normalizeSegmentPunch(pick(raw, "clockOut", "ClockOut"), "ClockOut"),
    durationMinutes: asOptionalNumber(pick(raw, "durationMinutes", "DurationMinutes")),
    workedMinutes: asOptionalNumber(pick(raw, "workedMinutes", "WorkedMinutes", "durationMinutes", "DurationMinutes")),
    status: asOptionalString(pick(raw, "status", "Status", "segmentStatus", "SegmentStatus")),
    adjustmentStatus: asOptionalString(pick(raw, "adjustmentStatus", "AdjustmentStatus")),
  };
}

function punchTimestamp(punch?: AttendancePunch) {
  return punch?.effectivePunchTime ?? punch?.punchTimeLocal ?? punch?.punchTimeUtc ?? "";
}

function minutesBetween(start?: AttendancePunch, end?: AttendancePunch) {
  const startValue = punchTimestamp(start);
  const endValue = punchTimestamp(end);
  if (!startValue || !endValue) return undefined;

  const normalize = (value: string) => value.includes("T") ? value : `2000-01-01T${value}`;
  const startTime = new Date(normalize(startValue)).getTime();
  const endTime = new Date(normalize(endValue)).getTime();
  if (!Number.isFinite(startTime) || !Number.isFinite(endTime) || endTime < startTime) return undefined;
  return Math.round((endTime - startTime) / 60_000);
}

function pairPunches(punches: AttendancePunch[]): AttendancePunchSegment[] {
  const sorted = [...punches].sort((left, right) => punchTimestamp(left).localeCompare(punchTimestamp(right)));
  const segments: AttendancePunchSegment[] = [];

  const nextSegmentIndex = () =>
    Math.max(0, ...segments.map((segment) => segment.segmentIndex)) + 1;
  const createSegment = (preferredIndex?: number): AttendancePunchSegment => {
    const segmentIndex = preferredIndex && !segments.some((segment) => segment.segmentIndex === preferredIndex)
      ? preferredIndex
      : nextSegmentIndex();
    const segment = { segmentIndex, segmentNumber: segmentIndex };
    segments.push(segment);
    return segment;
  };

  for (const punch of sorted) {
    if (punch.punchType === "ClockIn") {
      const target = punch.segmentIndex !== undefined
        ? segments.find((segment) => segment.segmentIndex === punch.segmentIndex && !segment.clockIn)
        : undefined;
      const segment = target ?? createSegment(punch.segmentIndex);
      segment.clockIn = punch;
      segment.status = punch.segmentStatus;
      continue;
    }
    if (punch.punchType === "ClockOut") {
      const indexedTarget = punch.segmentIndex !== undefined
        ? segments.find((segment) =>
            segment.segmentIndex === punch.segmentIndex
            && Boolean(segment.clockIn)
            && !segment.clockOut)
        : undefined;
      const latestOpenTarget = punch.segmentIndex === undefined
        ? [...segments].reverse().find((segment) => Boolean(segment.clockIn) && !segment.clockOut)
        : undefined;
      // 已完成班段不可被额外 ClockOut 覆盖；无可接续 open segment 时保留为异常 out-only 段。
      const target = indexedTarget ?? latestOpenTarget ?? createSegment(punch.segmentIndex);
      target.clockOut = punch;
      target.status = target.status ?? punch.segmentStatus;
      target.workedMinutes = minutesBetween(target.clockIn, punch);
      target.durationMinutes = target.workedMinutes;
    }
  }

  return segments;
}

function normalizeScheduleBase(raw: ApiRecord): AttendanceSchedule {
  return {
    scheduleGuid: asString(pick(raw, "scheduleGuid", "ScheduleGuid", "guid", "Guid")),
    storeCode: asString(pick(raw, "storeCode", "StoreCode")),
    storeName: asOptionalString(pick(raw, "storeName", "StoreName")),
    userGuid: asString(pick(raw, "userGuid", "UserGuid", "employeeGuid", "EmployeeGuid")),
    employeeName: asOptionalString(pick(raw, "employeeName", "EmployeeName", "userName", "UserName")),
    workDate: asDateString(pick(raw, "workDate", "WorkDate", "date", "Date")),
    startTime: asString(pick(raw, "startTime", "StartTime")),
    endTime: asString(pick(raw, "endTime", "EndTime")),
    status: asString(pick(raw, "status", "Status"), "Scheduled"),
    remark: asOptionalString(pick(raw, "remark", "Remark", "note", "Note")),
    isMine: asBoolean(pick(raw, "isMine", "IsMine", "mine", "Mine")),
    holidayName: asOptionalString(pick(raw, "holidayName", "HolidayName")),
    holidayBusinessStatus: asOptionalString(pick(raw, "holidayBusinessStatus", "HolidayBusinessStatus")),
    scheduleState: asOptionalString(pick(raw, "scheduleState", "ScheduleState")),
    segmentLimit: asOptionalNumber(pick(raw, "segmentLimit", "SegmentLimit")),
    completedSegmentCount: asOptionalNumber(pick(raw, "completedSegmentCount", "CompletedSegmentCount")),
    workedMinutes: asOptionalNumber(pick(raw, "workedMinutes", "WorkedMinutes")),
    breakMinutes: asOptionalNumber(pick(raw, "breakMinutes", "BreakMinutes")),
    hasOpenSegment: asBoolean(pick(raw, "hasOpenSegment", "HasOpenSegment")),
    hasMissingClockOut: asBoolean(pick(raw, "hasMissingClockOut", "HasMissingClockOut")),
    earlyOvertimeMinutes: asOptionalNumber(pick(raw, "earlyOvertimeMinutes", "EarlyOvertimeMinutes")),
    lateOvertimeMinutes: asOptionalNumber(pick(raw, "lateOvertimeMinutes", "LateOvertimeMinutes")),
    candidateOvertimeMinutes: asOptionalNumber(
      pick(raw, "candidateOvertimeMinutes", "CandidateOvertimeMinutes", "overtimeCandidateMinutes", "OvertimeCandidateMinutes"),
    ),
    approvedOvertimeMinutes: asOptionalNumber(
      pick(raw, "approvedOvertimeMinutes", "ApprovedOvertimeMinutes", "approvedMinutes", "ApprovedMinutes"),
    ),
    overtimeApprovalStatus: asOptionalString(
      pick(raw, "overtimeApprovalStatus", "OvertimeApprovalStatus", "adjustmentStatus", "AdjustmentStatus"),
    ),
    leaveType: asOptionalString(pick(raw, "leaveType", "LeaveType")),
    leaveGuid: asOptionalString(pick(raw, "leaveGuid", "LeaveGuid")),
  };
}

function normalizeSession(raw: ApiRecord, allPunches: AttendancePunch[]): AttendanceScheduleSession {
  const schedule = normalizeScheduleBase(raw);
  const explicitSegments = records(pick(raw, "segments", "Segments"));
  const matchingPunches = allPunches.filter((punch) =>
    schedule.scheduleGuid
      ? punch.scheduleGuid === schedule.scheduleGuid
      : Boolean(schedule.storeCode) && punch.storeCode === schedule.storeCode,
  );
  const segments = explicitSegments.length
    ? explicitSegments.map(normalizeSegment)
    : pairPunches(matchingPunches);
  const earlyOvertimeMinutes = schedule.earlyOvertimeMinutes ?? 0;
  const lateOvertimeMinutes = schedule.lateOvertimeMinutes ?? 0;

  return {
    ...schedule,
    segments,
    overtimeRawMinutes: asOptionalNumber(pick(raw, "overtimeRawMinutes", "OvertimeRawMinutes"))
      ?? earlyOvertimeMinutes + lateOvertimeMinutes,
    overtimeCandidateMinutes: schedule.candidateOvertimeMinutes,
    adjustmentStatus: asOptionalString(pick(raw, "adjustmentStatus", "AdjustmentStatus"))
      ?? schedule.overtimeApprovalStatus,
  };
}

function buildOrphanSessions(
  punches: AttendancePunch[],
  scheduledGuids: Set<string>,
): AttendanceScheduleSession[] {
  const grouped = new Map<string, AttendancePunch[]>();
  for (const punch of punches) {
    if (punch.scheduleGuid && scheduledGuids.has(punch.scheduleGuid)) continue;
    const key = `${punch.storeCode ?? "unknown"}:${punch.scheduleGuid ?? "no-schedule"}`;
    grouped.set(key, [...(grouped.get(key) ?? []), punch]);
  }

  return [...grouped.entries()].map(([key, orphanPunches]) => {
    const firstPunch = orphanPunches[0];
    return {
      scheduleGuid: `orphan:${key}`,
      storeCode: firstPunch?.storeCode ?? "",
      storeName: firstPunch?.storeName,
      userGuid: firstPunch?.userGuid ?? "",
      employeeName: firstPunch?.employeeName,
      workDate: firstPunch?.workDate ?? "",
      startTime: "",
      endTime: "",
      status: "NoSchedule",
      isMine: true,
      scheduleState: "NoSchedule",
      segments: pairPunches(orphanPunches),
    };
  });
}

function normalizeHoliday(raw: ApiRecord): AttendanceStoreHoliday {
  return {
    holidayGuid: asString(pick(raw, "holidayGuid", "HolidayGuid", "guid", "Guid")),
    storeCode: asString(pick(raw, "storeCode", "StoreCode")),
    storeName: asOptionalString(pick(raw, "storeName", "StoreName")),
    holidayDate: asDateString(pick(raw, "holidayDate", "HolidayDate", "date", "Date")),
    holidayName: asString(pick(raw, "holidayName", "HolidayName", "name", "Name")),
    businessStatus: asString(pick(raw, "businessStatus", "BusinessStatus"), "Open"),
    openTime: asOptionalString(pick(raw, "openTime", "OpenTime")),
    closeTime: asOptionalString(pick(raw, "closeTime", "CloseTime")),
    isPaidHoliday: asBoolean(pick(raw, "isPaidHoliday", "IsPaidHoliday")),
    remark: asOptionalString(pick(raw, "remark", "Remark", "note", "Note")),
  };
}

function groupSessionsByStore(
  sessions: AttendanceScheduleSession[],
  explicitStates: ApiRecord[],
): AttendanceStorePunchState[] {
  const explicitByStore = new Map<string, ApiRecord[]>();
  for (const raw of explicitStates) {
    const storeCode = asString(pick(raw, "storeCode", "StoreCode"));
    explicitByStore.set(storeCode, [...(explicitByStore.get(storeCode) ?? []), raw]);
  }
  const grouped = new Map<string, AttendanceScheduleSession[]>();
  for (const session of sessions) {
    const key = session.storeCode || "unknown";
    grouped.set(key, [...(grouped.get(key) ?? []), session]);
  }
  for (const storeCode of explicitByStore.keys()) {
    if (!grouped.has(storeCode)) grouped.set(storeCode, []);
  }

  return [...grouped].map(([storeCode, scheduleSessions]) => {
    const raws = explicitByStore.get(storeCode) ?? [];
    const stateRows = raws
      .map((raw) => asOptionalString(pick(raw, "state", "State")))
      .filter((state): state is string => Boolean(state));
    const hasOpenValues = raws.map((raw) => asOptionalBoolean(
      pick(raw, "hasOpenSegment", "HasOpenSegment"),
    ));
    const hasMissingValues = raws.map((raw) => asOptionalBoolean(
      pick(raw, "hasMissingClockOut", "HasMissingClockOut"),
    ));
    const hasOpenSegment = hasOpenValues.some((value) => value === true)
      ? true
      : hasOpenValues.some((value) => value !== undefined)
        ? false
        : undefined;
    const hasMissingClockOut = hasMissingValues.some((value) => value === true)
      ? true
      : hasMissingValues.some((value) => value !== undefined)
        ? false
        : undefined;
    // 旧后端可能按排班重复返回同一门店；异常状态必须 any 合并，不能由最后一条覆盖。
    const state = hasMissingClockOut
      ? "MissingClockOut"
      : hasOpenSegment
        ? "Working"
        : stateRows.every((value) => value.toLowerCase() === "completed") && stateRows.length > 0
          ? "Completed"
          : stateRows[0];
    const firstValue = (selector: (raw: ApiRecord) => string | undefined) =>
      raws.map(selector).find((value): value is string => Boolean(value));
    return {
      storeCode,
      storeName: firstValue((raw) => asOptionalString(pick(raw, "storeName", "StoreName")))
        ?? scheduleSessions[0]?.storeName,
      state,
      hasOpenSegment,
      hasMissingClockOut,
      scheduleGuid: firstValue((raw) => asOptionalString(pick(raw, "scheduleGuid", "ScheduleGuid"))),
      relatedReminder: firstValue((raw) => asOptionalString(
        pick(raw, "relatedReminder", "RelatedReminder", "relatedStoreReminder", "RelatedStoreReminder"),
      )),
      scheduleSessions,
    };
  });
}

export function normalizeAttendanceToday(payload: unknown): AttendanceToday {
  const raw = isRecord(payload) ? payload : {};
  const punches = records(pick(raw, "punches", "Punches")).map((item) => normalizePunch(item));
  const holidays = records(pick(raw, "holidays", "Holidays")).map(normalizeHoliday);
  const primaryHoliday = holidays[0];
  const explicitStates = records(pick(raw, "storePunchStates", "StorePunchStates"));
  const nestedSessionRows = explicitStates.flatMap((state) =>
    records(pick(state, "scheduleSessions", "ScheduleSessions", "schedules", "Schedules")).map((session) => ({
      ...session,
      storeCode: pick(session, "storeCode", "StoreCode") ?? pick(state, "storeCode", "StoreCode"),
      storeName: pick(session, "storeName", "StoreName") ?? pick(state, "storeName", "StoreName"),
    })),
  );
  const scheduleRows = nestedSessionRows.length
    ? nestedSessionRows
    : records(pick(raw, "scheduleSessions", "ScheduleSessions", "schedules", "Schedules"));
  const normalizedScheduledSessions = scheduleRows.map((item) => normalizeSession(item, punches));
  const scheduledGuids = new Set(
    normalizedScheduledSessions.map((session) => session.scheduleGuid).filter(Boolean),
  );
  const scheduleSessions = [
    ...normalizedScheduledSessions,
    ...buildOrphanSessions(punches, scheduledGuids),
  ];
  const schedules: AttendanceSchedule[] = scheduleSessions.map((session) => session);
  const storePunchStates = groupSessionsByStore(scheduleSessions, explicitStates);
  const latestPunch = [...punches].sort((left, right) => punchTimestamp(left).localeCompare(punchTimestamp(right))).at(-1);
  const fallbackNextPunchType: AttendancePunchType = latestPunch?.punchType === "ClockIn" ? "ClockOut" : "ClockIn";
  const topReminders = records(pick(raw, "relatedStoreReminders", "RelatedStoreReminders"))
    .map((item) => asOptionalString(pick(item, "message", "Message", "reminder", "Reminder")))
    .filter((item): item is string => Boolean(item));
  const directReminders = Array.isArray(pick(raw, "relatedStoreReminders", "RelatedStoreReminders"))
    ? (pick(raw, "relatedStoreReminders", "RelatedStoreReminders") as unknown[])
        .map(asOptionalString)
        .filter((item): item is string => Boolean(item))
    : [];
  const relatedStoreReminders = [...new Set([
    ...topReminders,
    ...directReminders,
    ...storePunchStates.map((item) => item.relatedReminder).filter((item): item is string => Boolean(item)),
  ])];

  return {
    workDate: asDateString(pick(raw, "workDate", "WorkDate")),
    storeTimeZone: asOptionalString(pick(raw, "storeTimeZone", "StoreTimeZone")),
    holidayName: asOptionalString(pick(raw, "holidayName", "HolidayName")) ?? primaryHoliday?.holidayName,
    holidayBusinessStatus: asOptionalString(pick(raw, "holidayBusinessStatus", "HolidayBusinessStatus"))
      ?? primaryHoliday?.businessStatus,
    holidays,
    schedules,
    punches,
    nextPunchType: asString(pick(raw, "nextPunchType", "NextPunchType"), fallbackNextPunchType) as AttendancePunchType,
    canClockIn: asBoolean(pick(raw, "canClockIn", "CanClockIn"), fallbackNextPunchType === "ClockIn"),
    canClockOut: asBoolean(pick(raw, "canClockOut", "CanClockOut"), fallbackNextPunchType === "ClockOut"),
    storePunchStates,
    scheduleSessions,
    relatedStoreReminders,
    canRequestAdjustment: asOptionalBoolean(
      pick(raw, "canRequestAdjustment", "CanRequestAdjustment"),
    ),
  };
}

export interface AttendanceTodayDisplaySegment extends AttendancePunchSegment {
  isBreakAfter: boolean;
  showClockInException: boolean;
  showClockOutException: boolean;
}

export interface AttendanceTodayDisplaySession extends AttendanceScheduleSession {
  workedMinutes: number;
  segments: AttendanceTodayDisplaySegment[];
  overtime: {
    rawMinutes: number;
    candidateMinutes: number;
    approvedMinutes: number;
    status?: string;
  };
}

export function resolveAttendancePunchExceptionMinutes(
  punch: Pick<
    AttendancePunch,
    | "status"
    | "earlyArrivalMinutes"
    | "lateMinutes"
    | "earlyLeaveMinutes"
    | "lateDepartureMinutes"
  >,
) {
  switch (punch.status.trim().toLowerCase()) {
    case "late":
      return punch.lateMinutes;
    case "early":
      return punch.earlyArrivalMinutes;
    case "earlyleave":
      return punch.earlyLeaveMinutes;
    case "lateleave":
      return punch.lateDepartureMinutes;
    case "normal":
    case "break":
      return undefined;
    default:
      // 未知状态只能采用明确的正分钟；不能让不适用字段的 0 抢先覆盖有效值。
      return [
        punch.lateMinutes,
        punch.earlyArrivalMinutes,
        punch.earlyLeaveMinutes,
        punch.lateDepartureMinutes,
      ].find((minutes) => minutes !== undefined && minutes > 0);
  }
}

export function buildAttendanceTodayDisplay(today?: AttendanceToday) {
  const stores = (today?.storePunchStates ?? [])
    .filter((store) => store.scheduleSessions.length > 0)
    .map((store) => ({
    ...store,
    sessions: store.scheduleSessions.map((session): AttendanceTodayDisplaySession => {
      const segments = session.segments.map((segment, index) => ({
        ...segment,
        isBreakAfter: Boolean(segment.clockOut) && index < session.segments.length - 1,
        showClockInException: index === 0,
        showClockOutException: index === session.segments.length - 1,
      }));
      const workedMinutes = session.workedMinutes
        ?? segments.reduce(
          (sum, segment) => sum + (segment.workedMinutes ?? segment.durationMinutes ?? minutesBetween(segment.clockIn, segment.clockOut) ?? 0),
          0,
        );
      return {
        ...session,
        segments,
        workedMinutes,
        overtime: {
          rawMinutes: session.overtimeRawMinutes ?? (session.earlyOvertimeMinutes ?? 0) + (session.lateOvertimeMinutes ?? 0),
          candidateMinutes: session.candidateOvertimeMinutes ?? session.overtimeCandidateMinutes ?? 0,
          approvedMinutes: session.approvedOvertimeMinutes ?? 0,
          status: session.overtimeApprovalStatus ?? session.adjustmentStatus,
        },
      };
    }),
    }));
  const missingStores = (today?.storePunchStates ?? []).filter((store) => store.hasMissingClockOut);
  const activeStores = (today?.storePunchStates ?? []).filter((store) => store.hasOpenSegment);
  const relatedStoreAlerts = missingStores.flatMap((missingStore) =>
    activeStores
      .filter((activeStore) => activeStore.storeCode !== missingStore.storeCode)
      .map((activeStore) => ({
        missingStoreCode: missingStore.storeCode,
        missingStoreName: missingStore.storeName,
        activeStoreCode: activeStore.storeCode,
        activeStoreName: activeStore.storeName,
      })),
  );

  return {
    stores,
    relatedStoreReminders: today?.relatedStoreReminders ?? [],
    relatedStoreAlerts,
  };
}
