import assert from 'node:assert/strict'
import {
  GUARDIAN_RELATIONSHIP_OPTIONS,
  buildNewStaffPayloadFields,
  calculateAge,
  getNewStaffErrorField,
  isAustralianMobile,
  isBirthdayInRange,
  isMinorAge,
  isMinorBirthday,
  isSameContactPhone,
  isSameEmail,
  isStaffPhoneValid,
  normalizeAustralianMobile,
} from './newStaffProfileRules'

const today = '2026-10-30'

// 年龄：按悉尼当天算周岁，今年未到生日减 1。
assert.equal(calculateAge('2011-03-02', today), 15)
assert.equal(calculateAge('2008-10-30', today), 18, '18 岁生日当天已成年')
assert.equal(calculateAge('2008-10-31', today), 17, '18 岁生日前一天仍未成年')
assert.equal(calculateAge('1990-01-01', today), 36)
assert.equal(calculateAge('', today), null)
assert.equal(calculateAge('2011-02-30', today), null, '非法日历日期')
assert.equal(calculateAge('2011/03/02', today), null)
// 2 月 29 日生日：平年 2-28 仍未满，3-1 才满（与后端 AgeOn 一致）。
assert.equal(calculateAge('2008-02-29', '2026-02-28'), 17)
assert.equal(calculateAge('2008-02-29', '2026-03-01'), 18)

assert.equal(isMinorAge(17), true)
assert.equal(isMinorAge(18), false)
assert.equal(isMinorAge(0), true)
assert.equal(isMinorAge(null), false)

// 生日范围：1940-01-01 ~ 今天。
assert.equal(isBirthdayInRange('1940-01-01', today), true)
assert.equal(isBirthdayInRange('1939-12-31', today), false)
assert.equal(isBirthdayInRange(today, today), true)
assert.equal(isBirthdayInRange('2026-10-31', today), false)
assert.equal(isBirthdayInRange(undefined, today), false)

assert.equal(isMinorBirthday('2011-03-02', today), true)
assert.equal(isMinorBirthday('2000-03-02', today), false)
assert.equal(isMinorBirthday('2026-11-01', today), false, '未来日期不算未成年')

// 澳洲手机：与后端 AustralianPhoneNumber.TryNormalize 同口径。
for (const [raw, expected] of [
  ['0412 345 678', '+61412345678'],
  ['0412-345-678', '+61412345678'],
  ['(0412) 345.678', '+61412345678'],
  ['+61 412 345 678', '+61412345678'],
  ['+61 0412 345 678', '+61412345678'],
  ['0061 412 345 678', '+61412345678'],
  ['61412345678', '+61412345678'],
  ['0298765432', null], // 座机
  ['412345678', null], // 缺前导 0
  ['04123456789', null], // 位数不对
  ['+86 138 0000 0000', null],
  ['0412 345 678 ext', null],
  ['04+12345678', null],
  ['', null],
  [null, null],
] as const) {
  assert.equal(normalizeAustralianMobile(raw), expected, `号码 ${raw}`)
  assert.equal(isAustralianMobile(raw), expected !== null)
}

// 员工电话：8~15 位数字即可（可以是座机）。
assert.equal(isStaffPhoneValid('02 9876 5432'), true)
assert.equal(isStaffPhoneValid('1234567'), false)
assert.equal(isStaffPhoneValid('1234567890123456'), false)

// 重复判断：员工电话是澳洲手机时按规范化号码比，否则按纯数字比；邮箱忽略大小写和首尾空格。
assert.equal(isSameContactPhone('0423 111 222', '+61423111222'), true)
assert.equal(isSameContactPhone('0423 111 222', '0412 345 678'), false)
assert.equal(isSameContactPhone('02 9876 5432', '(02) 98765432'), true)
assert.equal(isSameContactPhone('', '0412 345 678'), false)
assert.equal(isSameEmail(' Mia@Example.com ', 'mia@example.com'), true)
assert.equal(isSameEmail('mia@example.com', 'linh@example.com'), false)
assert.equal(isSameEmail('', ''), false)

// 关系选项：提交英文值。
assert.deepEqual(GUARDIAN_RELATIONSHIP_OPTIONS.map((option) => option.value), ['Mother', 'Father', 'Legal guardian', 'Other'])

// 组装请求体：未成年带 guardian（去首尾空格）；成年不带，即使表单里残留了监护人值。
const guardianValues = {
  email: 'mia.nguyen@gmail.com',
  phone: ' 0423 111 222 ',
  guardianName: ' Linh Nguyen ',
  guardianRelationship: 'Mother',
  guardianPhone: '0412 345 678',
  guardianEmail: ' linh.nguyen@gmail.com ',
}
assert.deepEqual(buildNewStaffPayloadFields({ ...guardianValues, birthday: '2011-03-02' }, today), {
  phone: '0423 111 222',
  birthday: '2011-03-02',
  guardian: { name: 'Linh Nguyen', relationship: 'Mother', phone: '0412 345 678', email: 'linh.nguyen@gmail.com' },
})
assert.deepEqual(buildNewStaffPayloadFields({ ...guardianValues, birthday: '1999-03-02' }, today), {
  phone: '0423 111 222',
  birthday: '1999-03-02',
})

// 后端错误码 → 表单字段。
assert.equal(getNewStaffErrorField('NEW_STAFF_BIRTHDAY_REQUIRED'), 'birthday')
assert.equal(getNewStaffErrorField('NEW_STAFF_BIRTHDAY_INVALID'), 'birthday')
assert.equal(getNewStaffErrorField('NEW_STAFF_PHONE_REQUIRED'), 'phone')
assert.equal(getNewStaffErrorField('NEW_STAFF_EMAIL_REQUIRED'), 'email')
assert.equal(getNewStaffErrorField('NEW_STAFF_GUARDIAN_REQUIRED'), 'guardianName')
assert.equal(getNewStaffErrorField('NEW_STAFF_GUARDIAN_PHONE_INVALID'), 'guardianPhone')
assert.equal(getNewStaffErrorField('NEW_STAFF_GUARDIAN_EMAIL_INVALID'), 'guardianEmail')
assert.equal(getNewStaffErrorField('NEW_STAFF_GUARDIAN_DUPLICATE', '监护人手机不能和员工本人的电话相同'), 'guardianPhone')
assert.equal(getNewStaffErrorField('NEW_STAFF_GUARDIAN_DUPLICATE', '监护人邮箱不能和员工本人的邮箱相同'), 'guardianEmail')
assert.equal(getNewStaffErrorField('EMAIL_EXISTS'), null)
assert.equal(getNewStaffErrorField(undefined), null)

console.log('newStaffProfileRules.test: ok')
