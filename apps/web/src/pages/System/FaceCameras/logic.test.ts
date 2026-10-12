import assert from 'node:assert/strict'

import { RequestError } from '../../../utils/request'
import {
  CAMERA_STATUS_COLORS,
  CAPTURE_MAX_PAGE,
  SECRET_REVEAL_CLOSED,
  buildCreateRequest,
  buildUpdateRequest,
  canGoNextPage,
  canGoPrevPage,
  clampPage,
  closeSecretReveal,
  copyTextToClipboard,
  countCameraStatuses,
  createLatestGuard,
  formatDateTime,
  getCameraStatus,
  getFaceCameraErrorCode,
  hasParseFailures,
  holdsSecret,
  isForbiddenError,
  maskCameraUrl,
  normalizeOptionalText,
  openSecretReveal,
  toFormValues,
  validateCameraId,
  validateDeviceModel,
  validateFaceCameraForm,
  validateMacAddress,
  validateNote,
  validateStoreCode,
} from './logic'

// 明显的假值：不是任何真实 token。
const FAKE_TOKEN = '0'.repeat(48)
const FAKE_URL = `https://example.invalid/face-ingest/hik/v1/${FAKE_TOKEN}`

// ───────── 状态判定：停用优先，其次在线，否则离线 ─────────
assert.equal(getCameraStatus({ enabled: false, online: true }), 'disabled', '停用优先于在线')
assert.equal(getCameraStatus({ enabled: false, online: false }), 'disabled')
assert.equal(getCameraStatus({ enabled: true, online: true }), 'online')
assert.equal(getCameraStatus({ enabled: true, online: false }), 'offline')
assert.equal(CAMERA_STATUS_COLORS.online, 'green')
assert.equal(CAMERA_STATUS_COLORS.offline, 'orange')
assert.equal(CAMERA_STATUS_COLORS.disabled, 'default')
assert.deepEqual(
  countCameraStatuses([
    { enabled: true, online: true },
    { enabled: true, online: false },
    { enabled: false, online: true },
    { enabled: false, online: false },
  ]),
  { total: 4, online: 1, offline: 1, disabled: 2 },
)
assert.equal(hasParseFailures({ parseFailureCount24h: 0 }), false)
assert.equal(hasParseFailures({ parseFailureCount24h: 3 }), true)

// ───────── CameraId 校验（^[a-z0-9][a-z0-9-]{0,63}$） ─────────
assert.equal(validateCameraId('store1008-door-01'), null)
assert.equal(validateCameraId('a'), null)
assert.equal(validateCameraId('0abc'), null, '可以数字开头')
assert.equal(validateCameraId('a'.repeat(64)), null, '64 位通过')
assert.equal(validateCameraId('a'.repeat(65)), 'faceCameras.validation.cameraIdInvalid', '65 位不通过')
assert.equal(validateCameraId(''), 'faceCameras.validation.cameraIdRequired')
assert.equal(validateCameraId('   '), 'faceCameras.validation.cameraIdRequired')
assert.equal(validateCameraId(undefined), 'faceCameras.validation.cameraIdRequired')
assert.equal(validateCameraId('-abc'), 'faceCameras.validation.cameraIdInvalid', '不能以横线开头')
assert.equal(validateCameraId('Abc'), 'faceCameras.validation.cameraIdInvalid', '不允许大写')
assert.equal(validateCameraId('ab_c'), 'faceCameras.validation.cameraIdInvalid', '不允许下划线')
assert.equal(validateCameraId('ab c'), 'faceCameras.validation.cameraIdInvalid', '中间空格不允许')
assert.equal(validateCameraId('摄像机1'), 'faceCameras.validation.cameraIdInvalid')
assert.equal(validateCameraId(' abc '), null, '首尾空白先 trim')

// ───────── StoreCode 校验 ─────────
assert.equal(validateStoreCode('1008'), null)
assert.equal(validateStoreCode('HQ_01-a'), null)
assert.equal(validateStoreCode('A'.repeat(32)), null)
assert.equal(validateStoreCode('A'.repeat(33)), 'faceCameras.validation.storeCodeTooLong')
assert.equal(validateStoreCode(''), 'faceCameras.validation.storeCodeRequired')
assert.equal(validateStoreCode(null), 'faceCameras.validation.storeCodeRequired')
assert.equal(validateStoreCode('10 08'), 'faceCameras.validation.storeCodeInvalid')
assert.equal(validateStoreCode('门店1'), 'faceCameras.validation.storeCodeInvalid')
assert.equal(validateStoreCode('a.b'), 'faceCameras.validation.storeCodeInvalid')

// ───────── 其余长度 ─────────
assert.equal(validateNote('x'.repeat(200)), null)
assert.equal(validateNote('x'.repeat(201)), 'faceCameras.validation.noteTooLong')
assert.equal(validateNote(undefined), null)
assert.equal(validateDeviceModel('x'.repeat(64)), null)
assert.equal(validateDeviceModel('x'.repeat(65)), 'faceCameras.validation.deviceModelTooLong')
assert.equal(validateMacAddress('x'.repeat(32)), null)
assert.equal(validateMacAddress('x'.repeat(33)), 'faceCameras.validation.macAddressTooLong')

// 整表：新增校验 cameraId；编辑不校验（不可改）
assert.deepEqual(validateFaceCameraForm({ cameraId: 'ok-1', storeCode: '1008' }, 'create'), {})
assert.deepEqual(validateFaceCameraForm({ cameraId: 'BAD', storeCode: '' }, 'create'), {
  cameraId: 'faceCameras.validation.cameraIdInvalid',
  storeCode: 'faceCameras.validation.storeCodeRequired',
})
assert.deepEqual(validateFaceCameraForm({ cameraId: 'BAD', storeCode: '1008' }, 'edit'), {})
assert.deepEqual(
  validateFaceCameraForm({ storeCode: '1008', note: 'x'.repeat(201), deviceModel: 'y'.repeat(65), macAddress: 'z'.repeat(33) }, 'edit'),
  {
    note: 'faceCameras.validation.noteTooLong',
    deviceModel: 'faceCameras.validation.deviceModelTooLong',
    macAddress: 'faceCameras.validation.macAddressTooLong',
  },
)

// ───────── 请求体规范化：trim，空串 / 全空白 → null ─────────
assert.equal(normalizeOptionalText('  abc  '), 'abc')
assert.equal(normalizeOptionalText(''), null)
assert.equal(normalizeOptionalText('   '), null)
assert.equal(normalizeOptionalText(undefined), null)
assert.deepEqual(
  buildCreateRequest({ cameraId: '  cam-1 ', storeCode: ' 1008 ', note: '  ', deviceModel: ' DS-2CD ', macAddress: '' }),
  { cameraId: 'cam-1', storeCode: '1008', note: null, deviceModel: 'DS-2CD', macAddress: null },
)
assert.deepEqual(
  buildUpdateRequest({ cameraId: 'cam-1', storeCode: '1008 ', note: ' 门口 ', deviceModel: null, macAddress: undefined }, false),
  { storeCode: '1008', note: '门口', deviceModel: null, macAddress: null, enabled: false },
)
assert.equal('cameraId' in buildUpdateRequest({ cameraId: 'cam-1', storeCode: '1008' }, true), false, '更新请求不带 cameraId')
// 编辑回填：null → 空串；再规范化回 null（开关只改 enabled 时其余字段原样往返）
const sampleCamera = {
  cameraId: 'cam-1',
  storeCode: '1008',
  storeName: null,
  deviceModel: null,
  macAddress: null,
  note: '门口',
  enabled: true,
  createdAtUtc: '2026-10-12T00:00:00Z',
  updatedAtUtc: '2026-10-12T00:00:00Z',
  lastSeenAtUtc: null,
  online: false,
  captureCount24h: 0,
  faceCount24h: 0,
  parseFailureCount24h: 0,
  lastEventAtUtc: null,
}
assert.deepEqual(toFormValues(sampleCamera), { cameraId: 'cam-1', storeCode: '1008', note: '门口', deviceModel: '', macAddress: '' })
assert.deepEqual(buildUpdateRequest(toFormValues(sampleCamera), false), {
  storeCode: '1008',
  note: '门口',
  deviceModel: null,
  macAddress: null,
  enabled: false,
})

// ───────── 地址脱敏：不保留任何 token 字符 ─────────
const masked = maskCameraUrl(FAKE_URL)
assert.equal(masked.startsWith('https://example.invalid/face-ingest/hik/v1/'), true)
assert.equal(masked.includes('0000'), false, '脱敏结果不含 token 片段')
assert.equal(masked.length < FAKE_URL.length, true)
assert.equal(maskCameraUrl(''), maskCameraUrl(undefined))
assert.equal(maskCameraUrl('https://example.invalid/'), maskCameraUrl(null), '末段为空时整体掩码')
assert.equal(maskCameraUrl('notaurl').includes('notaurl'), false, '不是地址时整体掩码')

// ───────── 一次性 token 展示状态机：打开 → 关闭清理 ─────────
assert.equal(holdsSecret(SECRET_REVEAL_CLOSED), false)
const opened = openSecretReveal('created', { camera: { cameraId: 'cam-1' }, cameraUrl: FAKE_URL })
assert.deepEqual(opened, { open: true, kind: 'created', cameraId: 'cam-1', cameraUrl: FAKE_URL })
assert.equal(holdsSecret(opened), true)
assert.equal(JSON.stringify(opened).includes('token'), false, '状态里没有单独的 token 字段')
const rotated = openSecretReveal('rotated', { camera: { cameraId: 'cam-2' }, cameraUrl: FAKE_URL })
assert.equal(rotated.open && rotated.kind, 'rotated')
const closed = closeSecretReveal()
assert.equal(closed, SECRET_REVEAL_CLOSED)
assert.equal(holdsSecret(closed), false)
assert.equal(JSON.stringify(closed).includes(FAKE_TOKEN), false, '关闭后状态里不留地址')
assert.equal(Object.isFrozen(SECRET_REVEAL_CLOSED), true, '清空态是冻结常量，不会被意外写入')
// openSecretReveal 即使收到带 token 字段的结果也不会带走它
const leaky = openSecretReveal('created', { camera: { cameraId: 'cam-3' }, cameraUrl: FAKE_URL, token: FAKE_TOKEN } as never)
assert.equal(Object.keys(leaky).includes('token'), false)

// ───────── 复制到剪贴板：成功 / 失败降级 / 不可用 ─────────
let copiedText = ''
assert.equal(await copyTextToClipboard('abc', { writeText: async (text) => { copiedText = text } }), true)
assert.equal(copiedText, 'abc')
assert.equal(await copyTextToClipboard('abc', { writeText: async () => { throw new Error('denied') } }), false, '被拒绝时返回 false 而不是抛出')
assert.equal(await copyTextToClipboard('abc', {}), false)
assert.equal(await copyTextToClipboard('abc', undefined), false)

// ───────── 错误码识别 ─────────
assert.equal(getFaceCameraErrorCode(new RequestError('x', 409, { success: false, errorCode: 'FACE_CAMERA_ID_EXISTS' })), 'FACE_CAMERA_ID_EXISTS')
assert.equal(getFaceCameraErrorCode(new RequestError('x', 404, { code: 'FACE_CAMERA_NOT_FOUND' })), 'FACE_CAMERA_NOT_FOUND')
assert.equal(getFaceCameraErrorCode(new RequestError('x', 400, { errorCode: 'SOMETHING_ELSE' })), null)
assert.equal(getFaceCameraErrorCode(new RequestError('x', 500, 'text')), null)
assert.equal(getFaceCameraErrorCode(new Error('FACE_CAMERA_ID_EXISTS')), null, '普通错误不按消息文本识别')
assert.equal(isForbiddenError(new RequestError('x', 403, {})), true)
assert.equal(isForbiddenError(new RequestError('x', 404, {})), false)

// ───────── 时间 ─────────
assert.equal(formatDateTime(null), '-')
assert.equal(formatDateTime(''), '-')
assert.equal(formatDateTime('not-a-date'), '-')
assert.match(formatDateTime('2026-10-12T01:02:03Z'), /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/)

// ───────── 分页：没有总数，hasMore 控制下一页，页码最大 100 ─────────
assert.equal(canGoPrevPage(1), false)
assert.equal(canGoPrevPage(2), true)
assert.equal(canGoNextPage(1, false), false)
assert.equal(canGoNextPage(1, true), true)
assert.equal(canGoNextPage(CAPTURE_MAX_PAGE, true), false, '到最大页码后不能再往后翻')
assert.equal(clampPage(0), 1)
assert.equal(clampPage(101), 100)
assert.equal(clampPage(3.7), 3)
assert.equal(clampPage(Number.NaN), 1)

// 只认最后一次请求
const guard = createLatestGuard()
const first = guard.begin()
const second = guard.begin()
assert.equal(guard.isLatest(first), false)
assert.equal(guard.isLatest(second), true)
guard.invalidate()
assert.equal(guard.isLatest(second), false)

console.log('FaceCameras logic.test: ok')
