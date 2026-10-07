type AttendancePunchTimeSource = {
  punchTimeUtc?: string;
  punchTimeLocal?: string;
  effectivePunchTime?: string;
};

function padTimePart(value: number) {
  return String(value).padStart(2, "0");
}

function normalizeUtcInstant(value: string) {
  const trimmed = value.trim();
  if (!trimmed) return "";
  const milliseconds = trimmed.replace(/(\.\d{3})\d+/, "$1");
  return /(?:Z|[+-]\d{2}:\d{2})$/i.test(milliseconds)
    ? milliseconds
    : `${milliseconds}Z`;
}

/** API 的 UTC instant 转为毫秒时间戳；后端从数据库读出的 UTC 列不带 Z，同样按 UTC 解析。无效时返回 undefined。 */
export function toAttendanceUtcMillis(value?: string | null) {
  if (!value?.trim()) return undefined;
  const millis = new Date(normalizeUtcInstant(value)).getTime();
  return Number.isFinite(millis) ? millis : undefined;
}

/** API 的 UTC instant 转为手机所在时区的无 offset wall time。 */
export function toAttendanceDeviceLocalTime(value?: string | null) {
  if (!value?.trim()) return "";
  const instant = new Date(normalizeUtcInstant(value));
  if (!Number.isFinite(instant.getTime())) return value.trim();
  return `${instant.getFullYear()}-${padTimePart(instant.getMonth() + 1)}-${padTimePart(instant.getDate())}`
    + `T${padTimePart(instant.getHours())}:${padTimePart(instant.getMinutes())}:${padTimePart(instant.getSeconds())}`;
}

/** 新响应只信任 UTC；仅在 UTC 缺失时兼容旧 local/effective 字段。 */
export function resolveAttendancePunchDisplayTime(punch?: AttendancePunchTimeSource) {
  if (!punch) return "";
  if (punch.punchTimeUtc?.trim()) {
    return toAttendanceDeviceLocalTime(punch.punchTimeUtc);
  }
  return punch.punchTimeLocal?.trim() || punch.effectivePunchTime?.trim() || "";
}

/** 补卡输入是手机本地 wall time，边界处严格校验后转换成 UTC instant。 */
export function toAttendancePunchTimeUtc(value: string) {
  const trimmed = value.trim();
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/.exec(trimmed);
  if (!match) return "";
  const [, yearText, monthText, dayText, hourText, minuteText, secondText = "00"] = match;
  const parts = [yearText, monthText, dayText, hourText, minuteText, secondText].map(Number);
  const [year, month, day, hour, minute, second] = parts;
  const local = new Date(year, month - 1, day, hour, minute, second, 0);
  if (
    local.getFullYear() !== year
    || local.getMonth() !== month - 1
    || local.getDate() !== day
    || local.getHours() !== hour
    || local.getMinutes() !== minute
    || local.getSeconds() !== second
  ) return "";

  // DST 回拨期间同一 wall time 可对应两个 UTC instant；补卡不能静默选择其中一个。
  for (let deltaMinutes = -240; deltaMinutes <= 240; deltaMinutes += 1) {
    if (deltaMinutes === 0) continue;
    const candidate = new Date(local.getTime() + deltaMinutes * 60_000);
    if (
      candidate.getFullYear() === year
      && candidate.getMonth() === month - 1
      && candidate.getDate() === day
      && candidate.getHours() === hour
      && candidate.getMinutes() === minute
      && candidate.getSeconds() === second
    ) return "";
  }
  return local.toISOString();
}
