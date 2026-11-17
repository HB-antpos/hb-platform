import assert from 'node:assert/strict'
import { normalizePayRateSyncApplyResult, normalizePayRateSyncPreview } from './myobPayRateSyncService'

// 预览：生效日截成日期、数字只认有限数、不认识的类别按 Skip（保守，绝不误写）
const preview = normalizePayRateSyncPreview({
  connectionGuid: 'c1',
  connectionName: 'Hot Bargain Pty Ltd',
  storeCode: '1005',
  effectiveFrom: '2026-11-23T00:00:00',
  fetchedAt: '2026-11-17T01:02:03Z',
  notBoundCount: 2,
  rows: [
    {
      userGuid: 'u1', userName: 'Amy', employmentType: 'Casual', kind: 'Create', myobHourlyRate: 28.4567, rounded: true, hash: 'h1',
      weekday: { ourRate: null, suggested: 28.46, canApply: true, defaultSelected: true },
      saturday: { suggested: 34.15, multiplier: 1.2, canApply: true },
      sunday: { suggested: null, canApply: true, unavailableReason: 'CATEGORY_NOT_MAPPED' },
      publicHoliday: { suggested: 56.92, multiplier: 2, canApply: true, defaultSelected: true },
    },
    { userGuid: 'u2', userName: 'Bob', kind: 'FutureKind', skipReason: 'MYOB_CARD_MISSING', myobHourlyRate: 'x' },
    { userName: '没有 guid 的行被丢弃' },
  ],
})
assert.equal(preview.effectiveFrom, '2026-11-23')
assert.equal(preview.notBoundCount, 2)
assert.equal(preview.rows.length, 2)
const [amy, bob] = preview.rows
assert.equal(amy.kind, 'Create')
assert.equal(amy.weekday.suggested, 28.46)
assert.equal(amy.weekday.canApply, true)
assert.equal(amy.weekday.defaultSelected, true)
assert.equal(amy.saturday.multiplier, 1.2)
assert.equal(amy.sunday.suggested, null)
assert.equal(amy.sunday.canApply, false, '没有建议值的格不可能可写，即使后端字段异常')
assert.equal(amy.sunday.unavailableReason, 'CATEGORY_NOT_MAPPED')
assert.equal(bob.kind, 'Skip')
assert.equal(bob.myobHourlyRate, null)
assert.equal(bob.weekday.canApply, false)

// 缺字段不崩
assert.deepEqual(normalizePayRateSyncPreview(null).rows, [])

// apply 结果
const result = normalizePayRateSyncApplyResult({
  effectiveFrom: '2026-11-23T00:00:00',
  written: [{ userGuid: 'u1', userName: 'Amy', rateGuid: 'r1', cells: ['weekday', 'bogus', 'publicHoliday'] }],
  failed: [{ userGuid: 'u2', userName: 'Bob', code: 'PERIOD_LOCKED', message: '该周期已锁定' }],
})
assert.equal(result.effectiveFrom, '2026-11-23')
assert.deepEqual(result.written[0].cells, ['weekday', 'publicHoliday'])
assert.equal(result.failed[0].code, 'PERIOD_LOCKED')
assert.deepEqual(normalizePayRateSyncApplyResult(undefined), { effectiveFrom: '', written: [], failed: [] })

console.log('myobPayRateSyncService tests passed')
