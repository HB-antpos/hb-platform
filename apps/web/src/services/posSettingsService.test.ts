import assert from 'node:assert/strict'
import { resolvePosSettingsError } from '../pages/System/PosSettings/logic'
import {
  getPosSettingDevice,
  getPosSettingsCatalog,
  getPosSettingsGlobal,
  getPosSettingStore,
  listPosSettingChanges,
  listPosSettingDevices,
  listPosSettingStores,
  savePosSettingDevice,
  savePosSettingsGlobal,
  savePosSettingStore,
} from './posSettingsService'

// 收银端设置接口封装：路径与方法、查询参数、请求体（null 表示清除要原样发出）、信封解包、缺字段兜底、
// 非 2xx 带后端 message 抛错。

interface Captured { url: URL; init?: RequestInit }
const originalFetch = globalThis.fetch
const requests: Captured[] = []
let nextStatus = 200
let nextBody: unknown = {}

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = new URL(String(input), 'http://localhost')
  // 失败请求会异步上报中心日志，这些请求不计入断言。
  if (!url.pathname.startsWith('/api/react/v1/pos-settings')) {
    return new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } })
  }
  requests.push({ url, init })
  return new Response(JSON.stringify(nextBody), { status: nextStatus, headers: { 'content-type': 'application/json' } })
}) as typeof fetch

function last(): Captured {
  const request = requests[requests.length - 1]
  assert.ok(request, '应发出请求')
  return request
}

function ok(data: unknown) {
  nextStatus = 200
  nextBody = { success: true, data }
}

try {
  // 1) catalog：缺省值缺失按 null（沿用本机），不能当成 false。
  ok({ definitions: [{ key: 'cashMovement.enabled', clientKinds: ['wpf'], defaultWhenUnset: false, isNew: true }, { key: 'returns.rescan' }] })
  const catalog = await getPosSettingsCatalog()
  assert.equal(last().url.pathname, '/api/react/v1/pos-settings/catalog')
  assert.equal(last().init?.method, 'GET')
  assert.deepEqual(catalog.definitions.map((item) => item.defaultWhenUnset), [false, null])
  assert.deepEqual(catalog.definitions[1].clientKinds, [])
  assert.equal(catalog.definitions[1].isNew, false)

  // 2) global：值字典缺失给空对象；保存请求体里的 null（清除）必须原样发出。
  ok({ scope: 'global', scopeKey: '*' })
  const emptyGlobal = await getPosSettingsGlobal()
  assert.equal(last().url.pathname, '/api/react/v1/pos-settings/global')
  assert.deepEqual(emptyGlobal.values, {})
  ok({ scope: 'global', scopeKey: '*', values: { 'returns.rescan': true } })
  const saved = await savePosSettingsGlobal({ values: { 'returns.rescan': true, 'payment.voucher': null } })
  assert.equal(last().init?.method, 'PUT')
  assert.deepEqual(JSON.parse(String(last().init?.body)), { values: { 'returns.rescan': true, 'payment.voucher': null } })
  assert.deepEqual(saved.values, { 'returns.rescan': true })

  // 3) stores：列表兜底；详情路径里的门店代码要编码；effective 的 value 缺失按 null。
  ok([{ storeCode: 'ORI', storeName: 'Orion' }])
  const stores = await listPosSettingStores()
  assert.equal(last().url.pathname, '/api/react/v1/pos-settings/stores')
  assert.deepEqual(stores[0].overrides, {})
  assert.equal(stores[0].deviceTotal, 0)
  ok({ storeCode: 'A/B', storeName: 'X', effective: { 'returns.rescan': { source: 'none' } } })
  const detail = await getPosSettingStore('A/B')
  assert.equal(last().url.pathname, '/api/react/v1/pos-settings/stores/A%2FB')
  assert.deepEqual(detail.effective['returns.rescan'], { value: null, source: 'none' })
  assert.deepEqual(detail.devices, [])
  await savePosSettingStore('ORI', { values: { 'cashMovement.enabled': false } })
  assert.equal(last().url.pathname, '/api/react/v1/pos-settings/stores/ORI')
  assert.equal(last().init?.method, 'PUT')
  assert.deepEqual(JSON.parse(String(last().init?.body)), { values: { 'cashMovement.enabled': false } })

  // 4) devices：门店代码走查询参数；设备详情兜底。
  ok([{ deviceCode: 'ORI-01', storeCode: 'ORI' }])
  const devices = await listPosSettingDevices('ORI')
  assert.equal(last().url.pathname, '/api/react/v1/pos-settings/devices')
  assert.equal(last().url.searchParams.get('storeCode'), 'ORI')
  assert.equal(devices[0].status, 'never', '没有状态按尚未同步')
  assert.equal(devices[0].offlineOverrideCount, 0)
  ok({ status: { deviceCode: 'ORI/01', storeCode: 'ORI' }, storeName: 'Orion' })
  const device = await getPosSettingDevice('ORI/01')
  assert.equal(last().url.pathname, '/api/react/v1/pos-settings/devices/ORI%2F01')
  assert.deepEqual(device.reported, {}, '设备没回报过 = 空对象，不是全关')
  await savePosSettingDevice('ORI/01', { values: { 'payment.voucher': null } })
  assert.equal(last().init?.method, 'PUT')
  assert.deepEqual(JSON.parse(String(last().init?.body)), { values: { 'payment.voucher': null } })

  // 5) changes：默认 limit 50；scope / scopeKey 可省略。
  ok([{ id: 1, scope: 'global', scopeKey: '*', settingKey: 'returns.rescan', oldValue: null, newValue: true, changedAtUtc: '2026-11-14T00:00:00Z' }])
  const changes = await listPosSettingChanges()
  assert.equal(last().url.pathname, '/api/react/v1/pos-settings/changes')
  assert.equal(last().url.searchParams.get('limit'), '50')
  assert.equal(last().url.searchParams.has('scope'), false)
  assert.equal(changes.length, 1)
  await listPosSettingChanges({ scope: 'store', scopeKey: 'ORI', limit: 10 })
  assert.equal(last().url.searchParams.get('scope'), 'store')
  assert.equal(last().url.searchParams.get('scopeKey'), 'ORI')
  assert.equal(last().url.searchParams.get('limit'), '10')

  // 6) 403：非管理员改全局层，错误里带后端 message，页面据此显示。
  nextStatus = 403
  nextBody = { success: false, message: '全局设置只有管理员可以修改', errorCode: 'FORBIDDEN' }
  await assert.rejects(savePosSettingsGlobal({ values: { 'returns.rescan': true } }), (error: unknown) => {
    const resolved = resolvePosSettingsError(error)
    assert.equal(resolved.kind, 'error')
    if (resolved.kind === 'error') {
      assert.equal(resolved.status, 403)
      assert.equal(resolved.serverMessage, '全局设置只有管理员可以修改')
    }
    return true
  })

  // 7) HTTP 200 但 success=false 也要抛错，不能当成保存成功。
  nextStatus = 200
  nextBody = { success: false, message: '设置项不存在', errorCode: 'POS_SETTING_UNKNOWN_KEY' }
  await assert.rejects(savePosSettingStore('ORI', { values: { nope: true } }))

  console.log('posSettingsService.test: ok')
} finally {
  globalThis.fetch = originalFetch
}
