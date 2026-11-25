import {
  APP_DEVICE_SYSTEM_OPTIONS,
  canEditRegisteredDeviceSystem,
  EDITABLE_DEVICE_SYSTEM_OPTIONS,
  formatDeviceSystemLabel,
  getRegisteredDeviceSystemEditOptions,
  isEnabledLegacyIosPos,
  REGISTERED_DEVICE_SYSTEM_OPTIONS,
  supportsTransactionGate,
} from './deviceSystemOptions'

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${label}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

assertDeepEqual(
  REGISTERED_DEVICE_SYSTEM_OPTIONS,
  ['Windows', 'iPadOS', 'AndroidTablet', 'Other'],
  'Registered device filter should expose the platform categories',
)
assertDeepEqual(
  APP_DEVICE_SYSTEM_OPTIONS,
  ['Android', 'iOS', 'iPadOS', 'Windows', 'Mac'],
  'App usage filter should retain mobile platforms and include iPadOS',
)
assertDeepEqual(
  EDITABLE_DEVICE_SYSTEM_OPTIONS,
  ['Android', 'iOS', 'iPadOS', 'AndroidTablet', 'Windows', 'Mac'],
  'Existing device edit workflow should allow iPadOS and AndroidTablet',
)
assertDeepEqual(
  canEditRegisteredDeviceSystem(-1, 'Windows', 'POS'),
  true,
  'Pending device platform should remain editable',
)
assertDeepEqual(
  canEditRegisteredDeviceSystem(1, 'iOS', 'POS'),
  true,
  'Enabled legacy iOS should allow one-way canonicalization',
)
assertDeepEqual(
  canEditRegisteredDeviceSystem(1, 'iPadOS', 'POS'),
  false,
  'Enabled canonical iPadOS should not be editable',
)
assertDeepEqual(
  canEditRegisteredDeviceSystem(0, 'iOS', 'POS'),
  false,
  'Disabled legacy iOS should remain locked',
)
assertDeepEqual(
  canEditRegisteredDeviceSystem(2, 'iOS', 'POS'),
  false,
  'Locked legacy iOS should remain locked',
)
assertDeepEqual(
  canEditRegisteredDeviceSystem(3, 'iOS', 'POS'),
  false,
  'Unregistered legacy iOS should remain locked',
)
assertDeepEqual(
  canEditRegisteredDeviceSystem(1, 'iOS', 'Mobile'),
  false,
  'Enabled Mobile iOS should not be reclassified as iPadOS',
)
assertDeepEqual(
  getRegisteredDeviceSystemEditOptions(1, 'iOS', 'POS'),
  ['iOS', 'iPadOS'],
  'Enabled legacy iOS should only expose the equivalent canonical platform',
)
assertDeepEqual(
  isEnabledLegacyIosPos(1, 'iOS', 'POS'),
  true,
  'Enabled legacy iOS POS should lock device type during canonicalization',
)
assertDeepEqual(
  isEnabledLegacyIosPos(1, 'iOS', 'Mobile'),
  false,
  'Enabled Mobile iOS should not enter canonicalization mode',
)
assertDeepEqual(
  [
    supportsTransactionGate('iPadOS', 'POS'),
    supportsTransactionGate('AndroidTablet', 'POS'),
    supportsTransactionGate('iOS', 'POS'),
    supportsTransactionGate('Android', 'POS'),
  ],
  [true, true, true, true],
  'iPad POS and iOS or Android handheld POS should support the transaction gate',
)
assertDeepEqual(
  [
    supportsTransactionGate('Windows', 'POS'),
    supportsTransactionGate('Android', 'PDA'),
    supportsTransactionGate('iOS', 'Mobile'),
  ],
  [false, false, false],
  'Windows POS and non-POS registrations should not expose the transaction gate',
)

const fakeT = (key: string) => `t:${key}`
assertDeepEqual(
  [
    formatDeviceSystemLabel('AndroidTablet', fakeT),
    formatDeviceSystemLabel(' AndroidTablet ', fakeT),
    formatDeviceSystemLabel('iPadOS', fakeT),
    formatDeviceSystemLabel('Android', fakeT),
    formatDeviceSystemLabel(null, fakeT),
  ],
  ['t:posAdmin.devices.deviceSystemAndroidTablet', 't:posAdmin.devices.deviceSystemAndroidTablet', 'iPadOS', 'Android', ''],
  'AndroidTablet should use the translated label while other systems keep their raw names',
)

console.log('deviceSystemOptions.test: ok')
