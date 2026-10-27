import assert from 'node:assert/strict'
import { normalizeMyobConnection, normalizeMyobConnections, normalizeMyobStatus, normalizeMyobStoreMappings } from './myobService'

// 契约：MyobDtos.cs，camelCase；后端字段缺失 / 类型异常时前端不崩。

// 完整连接
const full = normalizeMyobConnection({
  connectionGuid: 'c1',
  businessId: 'b-1',
  businessName: 'Hot Bargain Pty Ltd',
  abn: '12345678901',
  status: 'Active',
  authorizedByUserGuid: 'u1',
  authorizedByName: 'Sean',
  authorizedAt: '2026-10-27T01:02:03',
  lastRefreshedAt: '2026-10-27T02:00:00Z',
  lastError: null,
  storeCodes: ['1001', '1002'],
})
assert.deepEqual(full, {
  connectionGuid: 'c1',
  businessId: 'b-1',
  businessName: 'Hot Bargain Pty Ltd',
  abn: '12345678901',
  status: 'Active',
  authorizedByUserGuid: 'u1',
  authorizedByName: 'Sean',
  authorizedAt: '2026-10-27T01:02:03',
  lastRefreshedAt: '2026-10-27T02:00:00Z',
  lastError: null,
  storeCodes: ['1001', '1002'],
})

// 缺字段 / 空串 → null 或空数组；没有 connectionGuid 的行被丢弃
const sparse = normalizeMyobConnection({ connectionGuid: 'c2', abn: '', lastError: '  ', storeCodes: [1, 'a', ''] })
assert.equal(sparse?.abn, null)
assert.equal(sparse?.lastError, null)
assert.deepEqual(sparse?.storeCodes, ['a'])
assert.equal(sparse?.businessName, '')
assert.equal(normalizeMyobConnection({ businessName: 'x' }), null)
assert.equal(normalizeMyobConnection(null), null)
assert.deepEqual(normalizeMyobConnections('nope'), [])
assert.equal(normalizeMyobConnections([{ connectionGuid: 'a' }, {}, null]).length, 1)

// 状态：未知值按需重新授权，不当作可用连接
assert.equal(normalizeMyobStatus('Active'), 'Active')
assert.equal(normalizeMyobStatus('NeedsReauth'), 'NeedsReauth')
assert.equal(normalizeMyobStatus('Disconnected'), 'Disconnected')
assert.equal(normalizeMyobStatus('Weird'), 'NeedsReauth')
assert.equal(normalizeMyobStatus(undefined), 'NeedsReauth')

// 分店映射
const mappings = normalizeMyobStoreMappings({
  stores: [
    { storeCode: '1001', storeName: 'A', storeAbn: '12345678901', isActive: true, connectionGuid: 'c1', suggestedConnectionGuid: null, mappedAt: '2026-10-27T00:00:00' },
    { storeCode: '1002', storeName: 'B', isActive: false, connectionGuid: '', suggestedConnectionGuid: 'c1' },
    { storeCode: '1003', storeName: 'C' },
    { storeName: 'no code' },
  ],
  connections: [{ connectionGuid: 'c1', businessName: 'X', status: 'Active' }, {}],
})
assert.deepEqual(mappings.stores.map((item) => item.storeCode), ['1001', '1002', '1003'])
assert.equal(mappings.stores[0].connectionGuid, 'c1')
assert.equal(mappings.stores[1].connectionGuid, null)
assert.equal(mappings.stores[1].suggestedConnectionGuid, 'c1')
assert.equal(mappings.stores[1].isActive, false)
assert.equal(mappings.stores[2].isActive, true, '后端没给 isActive 时按启用处理')
assert.equal(mappings.stores[2].storeAbn, null)
assert.equal(mappings.connections.length, 1)
assert.deepEqual(normalizeMyobStoreMappings(undefined), { stores: [], connections: [] })

console.log('myobService.test: ok')
