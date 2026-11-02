import {
  normalizeRemoteMaintenanceDevice,
  normalizeRemoteMaintenanceDevicePage,
  normalizeRemoteMaintenanceManifest,
} from './remoteMaintenanceService'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
}

const device = normalizeRemoteMaintenanceDevice({
  id: 'device-guid',
  deviceRegistrationId: 123,
  storeCode: 'S001',
  deviceCode: 'POS-01',
  computerName: 'POS-01',
  rustdeskId: '123456789',
  onlineStatus: 'offline',
  serviceStatus: 'stopped',
  isStale: true,
})
assertEqual(device.deviceRegistrationId, '123', 'numeric registration IDs should be safe display strings')
assertEqual(device.onlineStatus, 'offline', 'offline status should be preserved')
assertEqual(device.serviceStatus, 'stopped', 'service status should be preserved while offline')
assertEqual(device.isStale, true, 'stale marker should be preserved')

const unknown = normalizeRemoteMaintenanceDevice({ onlineStatus: 'unexpected', serviceStatus: 'unexpected' })
assertEqual(unknown.onlineStatus, 'never', 'unknown online values must fail closed to never')
assertEqual(unknown.serviceStatus, 'checkFailed', 'unknown service values must fail closed to checkFailed')
// 旧版后端不返回实际版本：不报偏离也不报缺失。
assertEqual(unknown.observedRustDeskVersion, null, 'missing observed version should stay null')
assertEqual(unknown.rustDeskVersionStatus, 'unknown', 'missing version status should be unknown')

const drifted = normalizeRemoteMaintenanceDevice({ observedRustDeskVersion: ' 1.5.0 ', rustDeskVersionStatus: 'drift' })
assertEqual(drifted.observedRustDeskVersion, '1.5.0', 'observed version should be trimmed')
assertEqual(drifted.rustDeskVersionStatus, 'drift', 'drift status should be preserved')

const missingExecutable = normalizeRemoteMaintenanceDevice({ observedRustDeskVersion: '', rustDeskVersionStatus: 'missing' })
// 空串表示程序文件读不到，不能被当成“没上报”。
assertEqual(missingExecutable.observedRustDeskVersion, '', 'empty observed version must stay an empty string')
assertEqual(missingExecutable.rustDeskVersionStatus, 'missing', 'missing status should be preserved')
assertEqual(
  normalizeRemoteMaintenanceDevice({ rustDeskVersionStatus: 'whatever' }).rustDeskVersionStatus,
  'unknown',
  'unexpected version status must fail closed to unknown',
)

const page = normalizeRemoteMaintenanceDevicePage({
  items: [{ id: '1', deviceRegistrationId: 1 }],
  total: 1,
  page: 2,
  pageSize: 20,
  serverTimeUtc: '2026-01-01T00:00:00Z',
})
assertEqual(page.items.length, 1, 'ordinary DTO page should be read without an envelope')
assertEqual(page.page, 2, 'page metadata should be preserved')

const manifest = normalizeRemoteMaintenanceManifest({
  idServer: 'https://id.example',
  relayServer: 'https://relay.example',
  publicKey: 'public-key',
  rustdesk: { version: '1.0.0', fileName: 'rustdesk.exe', sizeBytes: 10 },
  statusAgent: { version: '1.0.0', fileName: 'agent.exe', sizeBytes: 20 },
})
assertEqual(manifest.rustdesk.fileName, 'rustdesk.exe', 'manifest artifact should be normalized')
assertEqual(manifest.statusAgent.fileName, 'agent.exe', 'status agent artifact should use statusAgent DTO field')

console.log('remoteMaintenanceService.test: ok')
