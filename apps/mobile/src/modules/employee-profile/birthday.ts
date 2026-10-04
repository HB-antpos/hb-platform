// 生日属于敏感资料：员工端只在敏感资料申请里修改，这里集中处理格式、校验和年龄计算。

const DATE_ONLY_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

export type BirthdayValidationError = "format" | "future" | "tooOld";

/** 最早允许的出生年份，防止误输入如 0998 之类的年份。 */
const MIN_BIRTH_YEAR = 1900;

function parseDateOnly(value: string) {
  const match = DATE_ONLY_PATTERN.exec(value);
  if (!match) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  // 用 UTC 回读校验，排除 2023-02-30 这类会被 Date 自动进位的非法日期。
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) {
    return null;
  }
  return { year, month, day };
}

/** 后端返回 1998-06-21T00:00:00，统一取日期部分；无法识别时返回空串。 */
export function normalizeBirthday(value: string | null | undefined) {
  const datePart = (value ?? "").trim().slice(0, 10);
  return parseDateOnly(datePart) ? datePart : "";
}

function toDateParts(today: Date) {
  return { year: today.getFullYear(), month: today.getMonth() + 1, day: today.getDate() };
}

/** 按本地日期计算周岁：今年生日当天即满一岁，2 月 29 日出生者在平年 3 月 1 日满岁。 */
export function calculateAge(birthday: string | null | undefined, today: Date = new Date()) {
  const parsed = parseDateOnly(normalizeBirthday(birthday));
  if (!parsed) return null;
  const now = toDateParts(today);
  let age = now.year - parsed.year;
  const birthdayPassed = now.month > parsed.month || (now.month === parsed.month && now.day >= parsed.day);
  if (!birthdayPassed) age -= 1;
  return age >= 0 ? age : null;
}

/** 空值视为「不填」，由调用方决定是否允许；只校验非空输入。 */
export function validateBirthday(
  value: string,
  today: Date = new Date()
): BirthdayValidationError | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const parsed = parseDateOnly(trimmed);
  if (!parsed) return "format";
  if (parsed.year < MIN_BIRTH_YEAR) return "tooOld";
  const now = toDateParts(today);
  const isFuture = parsed.year > now.year
    || (parsed.year === now.year && (parsed.month > now.month || (parsed.month === now.month && parsed.day > now.day)));
  return isFuture ? "future" : null;
}
