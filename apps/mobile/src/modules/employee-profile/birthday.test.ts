import assert from "node:assert/strict";
import { calculateAge, normalizeBirthday, validateBirthday } from "./birthday";

const today = new Date(2026, 9, 4); // 2026-10-04 本地时间

// 后端日期时间字符串只取日期部分，非法值返回空串。
assert.equal(normalizeBirthday("1998-06-21T00:00:00"), "1998-06-21");
assert.equal(normalizeBirthday(" 1998-06-21 "), "1998-06-21");
assert.equal(normalizeBirthday("2023-02-30"), "");
assert.equal(normalizeBirthday(undefined), "");

// 周岁：生日当天满岁，前一天不满。
assert.equal(calculateAge("1998-06-21", today), 28);
assert.equal(calculateAge("1998-10-04", today), 28);
assert.equal(calculateAge("1998-10-05", today), 27);
assert.equal(calculateAge("1998-06-21T00:00:00", today), 28);
assert.equal(calculateAge("", today), null);
assert.equal(calculateAge("2027-01-01", today), null);

// 2 月 29 日出生：平年 2 月 28 日未满岁，3 月 1 日满岁。
assert.equal(calculateAge("2000-02-29", new Date(2026, 1, 28)), 25);
assert.equal(calculateAge("2000-02-29", new Date(2026, 2, 1)), 26);

// 校验：空值不报错，格式、未来日期、过早年份分别报错。
assert.equal(validateBirthday("", today), null);
assert.equal(validateBirthday("1998-06-12", today), null);
assert.equal(validateBirthday("2026-10-04", today), null);
assert.equal(validateBirthday("1998/06/12", today), "format");
assert.equal(validateBirthday("1998-13-01", today), "format");
assert.equal(validateBirthday("2026-10-05", today), "future");
assert.equal(validateBirthday("1899-12-31", today), "tooOld");

console.log("birthday tests passed");
