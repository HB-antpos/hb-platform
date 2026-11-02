import assert from 'node:assert/strict'

import type { MyobCreateDraft, MyobCreateWarning } from '../../../types/myob'

import {
  buildCreateRequest,
  buildCreateReviewRows,
  buildInitialCreateFields,
  countEditedRows,
  getBindExistingCandidate,
  getCreateErrorField,
  getCreateWarningKey,
  isCreateFormMinor,
  isSamePhone,
  stripErrorCodePrefix,
  type MyobCreateFormFields,
  type MyobCreateReviewFormatters,
} from './myobCreateLogic'

// 悉尼「今天」固定，避免用例随日期漂移。
const TODAY = '2026-11-02'

function draft(patch: Partial<MyobCreateDraft> = {}): MyobCreateDraft {
  return {
    connectionGuid: 'conn-orion',
    connectionName: 'HOT BARGAIN ORION PTY LTD',
    card: {
      uid: 'c-erin',
      displayId: null,
      name: 'ERIN CHUNG',
      email: 'erinchung268@gmail.com',
      phoneMasked: '•••• 5678',
      dateOfBirth: '2010-02-23',
      isActive: true,
      employmentStatus: 'Casual',
      hasAnnualEntitlement: false,
      hasSickEntitlement: false,
    },
    username: 'erinchung',
    givenName: 'ERIN',
    familyName: 'CHUNG',
    email: 'erinchung268@gmail.com',
    myobPhone: '+61412345678',
    phone: '0412 345 678',
    birthday: '2010-02-23',
    age: 16,
    isMinor: true,
    employmentType: 'Temporary',
    stores: [
      { storeCode: '1013', storeName: 'Orion', mappedToThisCompany: true },
      { storeCode: '1042', storeName: 'TestStore', mappedToThisCompany: false },
    ],
    suggestedStoreCode: '1013',
    warnings: [],
    ...patch,
  }
}

const format: MyobCreateReviewFormatters = {
  employmentType: (type) => `type:${type}`,
  store: (code) => `store:${code}`,
  relationship: (value) => `rel:${value}`,
  passwordMode: (mode) => `pwd:${mode}`,
}

const guardian = {
  guardianName: 'Mary Chung',
  guardianRelationship: 'Mother',
  guardianPhone: '0400 999 888',
  guardianEmail: 'mary@example.com',
}

// ── 初值：MYOB 有的都预填；分店按映射预选；默认邮件发初始密码 ──
const initial = buildInitialCreateFields(draft())
assert.deepEqual(initial, {
  givenName: 'ERIN',
  familyName: 'CHUNG',
  username: 'erinchung',
  email: 'erinchung268@gmail.com',
  phone: '0412 345 678',
  birthday: '2010-02-23',
  employmentType: 'Temporary',
  storeCode: '1013',
  passwordMode: 'email',
})
const blank = buildInitialCreateFields(draft({ email: null, phone: null, birthday: null, employmentType: null, suggestedStoreCode: null }))
assert.equal(blank.email, '')
assert.equal(blank.phone, '')
assert.equal(blank.birthday, undefined)
assert.equal(blank.employmentType, undefined, 'MYOB 对应不上时不替人选')
assert.equal(blank.storeCode, undefined, '映射多家 / 没有映射时不预选')

// ── 请求：去首尾空白；未成年才带监护人；邮件模式不带密码 ──
const minorFields: MyobCreateFormFields = { ...initial, givenName: ' Erin ', ...guardian, password: 'ignored' }
const minorRequest = buildCreateRequest(draft(), minorFields, TODAY)
assert.equal(minorRequest.givenName, 'Erin')
assert.equal(minorRequest.myobEmployeeUid, 'c-erin')
assert.equal(minorRequest.connectionGuid, 'conn-orion')
assert.equal(minorRequest.employmentType, 'Temporary')
assert.equal(minorRequest.sendPasswordSetupEmail, true)
assert.equal('password' in minorRequest, false, '邮件发初始密码时不提交密码')
assert.deepEqual(minorRequest.guardian, { name: 'Mary Chung', relationship: 'Mother', phone: '0400 999 888', email: 'mary@example.com' })

const adultRequest = buildCreateRequest(draft(), { ...minorFields, birthday: '2004-12-05', passwordMode: 'manual', password: 'Init#2026' }, TODAY)
assert.equal('guardian' in adultRequest, false, '生日改回成年后残留的监护人值不提交')
assert.equal(adultRequest.sendPasswordSetupEmail, false)
assert.equal(adultRequest.password, 'Init#2026')
assert.equal(buildCreateRequest(draft(), { ...initial, employmentType: undefined }, TODAY).employmentType, '', '没选用工类型传空串，由后端拒绝')

assert.equal(isCreateFormMinor({ birthday: '2010-02-23' }, TODAY), true)
assert.equal(isCreateFormMinor({ birthday: '2008-11-02' }, TODAY), false, '今天满 18 岁')
assert.equal(isCreateFormMinor({ birthday: '2008-11-03' }, TODAY), true, '明天才满 18 岁')
assert.equal(isCreateFormMinor({}, TODAY), false)

// ── 电话比较：+61 / 04 / 空格写法视为同一号码 ──
assert.equal(isSamePhone('+61412345678', '0412 345 678'), true)
assert.equal(isSamePhone('+61412345678', '0412 345 679'), false)
assert.equal(isSamePhone('(02) 9876 5432', '0298765432'), true, '座机只比数字')
assert.equal(isSamePhone('', ''), false)

// ── 核对行：来源标记 ──
const untouched = buildCreateReviewRows(draft(), { ...initial, ...guardian }, TODAY, format)
const sourceOf = (rows: ReturnType<typeof buildCreateReviewRows>, key: string) => rows.find((row) => row.key === key)?.source
assert.equal(sourceOf(untouched, 'givenName'), 'myob')
assert.equal(sourceOf(untouched, 'username'), 'system', '沿用建议用户名')
assert.equal(sourceOf(untouched, 'phone'), 'myob', '+61… 规范成 04… 不算人工修改')
assert.equal(untouched.find((row) => row.key === 'phone')?.myobValue, '+61412345678', '原值显示 MYOB 里的写法')
assert.equal(sourceOf(untouched, 'employmentType'), 'myob')
assert.equal(untouched.find((row) => row.key === 'employmentType')?.myobValue, 'Casual')
assert.equal(untouched.find((row) => row.key === 'employmentType')?.value, 'type:Temporary')
assert.equal(sourceOf(untouched, 'store'), 'system', '按映射预选的分店')
assert.equal(untouched.find((row) => row.key === 'store')?.value, 'store:1013')
assert.equal(sourceOf(untouched, 'guardianName'), 'filled', '监护人只能人工填写')
assert.equal(untouched.find((row) => row.key === 'guardianRelationship')?.value, 'rel:Mother')
assert.equal(sourceOf(untouched, 'password'), 'system')
assert.equal(untouched.find((row) => row.key === 'password')?.value, 'pwd:email')
assert.equal(countEditedRows(untouched), 0)

const edited = buildCreateReviewRows(
  draft(),
  {
    ...initial,
    givenName: 'Erin',
    username: 'erin.c',
    email: 'ERINCHUNG268@GMAIL.COM',
    phone: '0412 000 000',
    birthday: '2004-12-05',
    employmentType: 'PartTime',
    storeCode: '1042',
    passwordMode: 'manual',
  },
  TODAY,
  format,
)
assert.equal(sourceOf(edited, 'givenName'), 'edited', '大小写不同也算人工修改（姓名按原样写入）')
assert.equal(sourceOf(edited, 'username'), 'edited')
assert.equal(sourceOf(edited, 'email'), 'myob', '邮箱忽略大小写')
assert.equal(sourceOf(edited, 'phone'), 'edited')
assert.equal(sourceOf(edited, 'birthday'), 'edited')
assert.equal(sourceOf(edited, 'employmentType'), 'edited')
assert.equal(sourceOf(edited, 'store'), 'filled')
assert.equal(sourceOf(edited, 'password'), 'filled')
assert.equal(edited.some((row) => row.key.startsWith('guardian')), false, '成年不显示监护人行')
assert.equal(countEditedRows(edited), 5)

const missing = buildCreateReviewRows(
  draft({ email: null, myobPhone: null, phone: null, employmentType: null, card: { ...draft().card, employmentStatus: 'Other' } }),
  { ...initial, email: 'new@example.com', phone: '0400 111 222', employmentType: 'FullTime' },
  TODAY,
  format,
)
assert.equal(sourceOf(missing, 'email'), 'filled')
assert.equal(sourceOf(missing, 'phone'), 'filled')
assert.equal(sourceOf(missing, 'employmentType'), 'filled', 'MYOB 对应不上，人工选的')

// ── 提醒与错误 ──
const warning = (patch: Partial<MyobCreateWarning>): MyobCreateWarning => ({
  code: 'EMAIL_IN_USE',
  message: 'm',
  userGuid: 'u1',
  userName: 'Erin',
  userDeleted: false,
  ...patch,
})
assert.equal(getCreateWarningKey(warning({})), 'myob.create.warnings.emailInUse')
assert.equal(getCreateWarningKey(warning({ userDeleted: true })), 'myob.create.warnings.emailInUseDeleted')
assert.equal(getCreateWarningKey(warning({ code: 'MISSING_FIELDS' })), 'myob.create.warnings.missingFields')
assert.equal(getCreateWarningKey(warning({ code: 'SOMETHING_NEW' })), null, '未知码退回后端文案')
assert.equal(getBindExistingCandidate([warning({ code: 'PHONE_IN_USE' }), warning({})])?.code, 'EMAIL_IN_USE')
assert.equal(getBindExistingCandidate([warning({ userDeleted: true })]), null, '已删除账号不能改为绑定')
assert.equal(getBindExistingCandidate([warning({ userGuid: null })]), null)

assert.equal(getCreateErrorField('USERNAME_EXISTS'), 'username')
assert.equal(getCreateErrorField('EMAIL_EXISTS'), 'email')
assert.equal(getCreateErrorField('MYOB_CREATE_INVALID_EMPLOYMENT_TYPE'), 'employmentType')
assert.equal(getCreateErrorField('NEW_STAFF_GUARDIAN_PHONE_INVALID'), 'guardianPhone')
assert.equal(getCreateErrorField('NEW_STAFF_GUARDIAN_DUPLICATE', '监护人邮箱不能和员工本人的邮箱相同'), 'guardianEmail')
assert.equal(getCreateErrorField('MYOB_BIND_CARD_ALREADY_BOUND'), null, '与字段无关的错误停在核对页')
assert.equal(getCreateErrorField(undefined), null)

assert.equal(stripErrorCodePrefix('USERNAME_EXISTS: 用户名已存在', 'USERNAME_EXISTS'), '用户名已存在')
assert.equal(stripErrorCodePrefix('用户名已存在', 'USERNAME_EXISTS'), '用户名已存在', '没有前缀原样返回')
assert.equal(stripErrorCodePrefix('OTHER: x', 'USERNAME_EXISTS'), 'OTHER: x', '只剥本条错误码')
assert.equal(stripErrorCodePrefix('USERNAME_EXISTS: x', null), 'USERNAME_EXISTS: x')

console.log('myobCreateLogic.test: ok')
