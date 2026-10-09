import { readFileSync } from 'node:fs'
import enLocale from '../../../i18n/locales/en.json'
import zhLocale from '../../../i18n/locales/zh.json'
import { buildAccess } from '../../../utils/access'
import { buildWebRoleMenuPreview } from '../../../utils/webMenuPreview'
import type { CurrentUser } from '../../../types/auth'
import { P } from '../../../types/permissions'
import {
  buildLinklyCredentialPayload,
  buildCreateLinklyTerminalPayload,
  buildUpdateLinklyTerminalPayload,
  buildLinklyActivationChecklist,
  buildSquareTokenPayload,
  canActivateLinklyConfiguration,
  createLinklyCredentialFormValues,
  createLinklyTerminalFormValues,
  createSquareTokenFormValues,
  describeElapsedSince,
  getEnvironmentStatus,
  getLinklyTerminalAssignmentOwner,
  getTerminalDeviceCodes,
  getTerminalHealthTone,
  resolvePaymentTerminalSettingsErrorMessage,
  suggestNextLaneNo,
} from './pageLogic'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

function createCurrentUser(overrides: Partial<CurrentUser> = {}): CurrentUser {
  return {
    userGUID: 'payment-terminal-settings-user',
    username: 'tester',
    email: 'tester@example.com',
    permissions: [],
    roleNames: [],
    storeNames: [],
    ...overrides,
  }
}

const access = buildAccess(createCurrentUser({ permissions: [P.System.ManageSettings] }))
assertEqual(access.canManageSystemSettings, true, 'System.ManageSettings should unlock payment terminal settings')

const menu = buildWebRoleMenuPreview(access, (key) => key)
const paymentMenu = menu
  .find((node) => node.path === '/system')
  ?.children?.find((node) => node.path === '/system/payment-terminal-settings')

assert(paymentMenu, 'system menu should include payment terminal settings')
assertEqual(
  paymentMenu?.permissionCodes.join(','),
  P.System.ManageSettings,
  'payment terminal settings menu should use System.ManageSettings',
)

const squareForm = createSquareTokenFormValues()
assertEqual(squareForm.accessToken, '', 'Square token input should start empty')
assertEqual(squareForm.clearToken, false, 'Square clear switch should default false')

const keepSquare = buildSquareTokenPayload('Production', { accessToken: '   ', clearToken: false })
assertEqual(keepSquare.environment, 'Production', 'Square payload should include environment')
assert('accessToken' in keepSquare === false, 'blank Square token should be omitted to keep existing token')

const clearSquare = buildSquareTokenPayload('Sandbox', { accessToken: 'new-secret', clearToken: true })
assertEqual(clearSquare.clearToken, true, 'clear Square payload should set clearToken=true')
assert('accessToken' in clearSquare === false, 'clear Square payload should not send accessToken')

const saveSquare = buildSquareTokenPayload('Sandbox', { accessToken: ' sandbox-secret ', clearToken: false })
assertEqual(saveSquare.accessToken, 'sandbox-secret', 'Square token should be trimmed before submit')

const linklyForm = createLinklyCredentialFormValues({
  storeCode: '001',
  environment: 'Production',
  username: 'existing-user',
  hasPassword: true,
})
assertEqual(linklyForm.password, '', 'Linkly password input should not be hydrated')
assertEqual(linklyForm.username, 'existing-user', 'Linkly username should be hydrated')

const keepLinkly = buildLinklyCredentialPayload('001', 'Production', {
  username: ' new-user ',
  password: ' ',
  clearCredential: false,
})
assertEqual(keepLinkly.username, 'new-user', 'Linkly username should be trimmed')
assert('password' in keepLinkly === false, 'blank Linkly password should be omitted to keep existing password')

const clearLinkly = buildLinklyCredentialPayload('001', 'Sandbox', {
  username: 'ignored',
  password: 'ignored',
  clearCredential: true,
})
assertEqual(clearLinkly.clearCredential, true, 'clear Linkly payload should set clearCredential=true')
assert('password' in clearLinkly === false, 'clear Linkly payload should not send password')

const terminalForm = createLinklyTerminalFormValues({
  terminalId: 'terminal-1',
  storeCode: '001',
  environment: 'Production',
  laneNo: 2,
  displayName: 'Back Counter',
  usernameMasked: '7457•••••002',
  hasPassword: true,
  pairingState: 'Ready',
  selectedDeviceCount: 1,
  updatedAtUtc: '2026-09-02T00:00:00Z',
})
assertEqual(terminalForm.laneNo, 2, 'terminal edit form should hydrate lane')
assertEqual(terminalForm.displayName, 'Back Counter', 'terminal edit form should hydrate display name')
assertEqual(terminalForm.username, '', 'terminal edit form must not hydrate masked username')
assertEqual(terminalForm.password, '', 'terminal edit form must not hydrate password')

const createTerminal = buildCreateLinklyTerminalPayload('001', 'Production', {
  laneNo: 3,
  displayName: ' Side Counter ',
  username: ' test-user-003 ',
  password: ' terminal-secret ',
})
assertEqual(createTerminal.displayName, 'Side Counter', 'terminal display name should be trimmed')
assertEqual(createTerminal.username, 'test-user-003', 'terminal username should be trimmed')
assertEqual(createTerminal.password, 'terminal-secret', 'terminal password should be trimmed')

const updateTerminal = buildUpdateLinklyTerminalPayload('001', 'Sandbox', {
  laneNo: 3,
  displayName: 'Side Counter',
  username: ' ',
  password: ' ',
})
assert('username' in updateTerminal === false, 'blank edit username should preserve server credential')
assert('password' in updateTerminal === false, 'blank edit password should preserve server credential')

const activationBase = {
  storeCode: '001',
  environment: 'Production' as const,
  mode: 'Draft' as const,
  terminals: [{
    terminalId: 'terminal-1',
    storeCode: '001',
    environment: 'Production' as const,
    laneNo: 1,
    displayName: 'Front Counter',
    usernameMasked: '7457•••••001',
    hasPassword: true,
    pairingState: 'Ready' as const,
    selectedDeviceCount: 1,
    updatedAtUtc: '2026-09-02T00:00:00Z',
  }],
  devices: [{
    deviceCode: 'POS-01',
    deviceSystem: 'Windows',
    enabled: true,
    deviceMissing: false,
    terminalId: 'terminal-1',
    revision: 1,
  }],
}
assertEqual(canActivateLinklyConfiguration(activationBase), true, 'ready terminal and complete selections can activate')
assertEqual(
  canActivateLinklyConfiguration({ ...activationBase, devices: [{ ...activationBase.devices[0], terminalId: null }] }),
  true,
  'enabled devices using other connection methods do not need a Cloud selection',
)
assertEqual(
  canActivateLinklyConfiguration({
    ...activationBase,
    devices: [activationBase.devices[0], { ...activationBase.devices[0], deviceCode: 'POS-LOCAL', terminalId: null }],
  }),
  true,
  'Cloud and non-Cloud POS devices can coexist during activation',
)
assertEqual(
  canActivateLinklyConfiguration({ ...activationBase, terminals: [] }),
  false,
  'activation still requires a ready Cloud terminal',
)
assertEqual(
  canActivateLinklyConfiguration({
    ...activationBase,
    devices: [{ ...activationBase.devices[0], terminalId: 'unpaired-terminal' }],
  }),
  false,
  'a selected terminal must still be ready',
)
assertEqual(
  canActivateLinklyConfiguration({ ...activationBase, mode: 'Active' }),
  false,
  'an active configuration cannot be activated again',
)
assertEqual(
  canActivateLinklyConfiguration({
    ...activationBase,
    devices: [
      activationBase.devices[0],
      {
        ...activationBase.devices[0],
        deviceCode: 'POS-DISABLED',
        enabled: false,
        deviceMissing: false,
        terminalId: null,
      },
    ],
  }),
  true,
  'disabled device without selection should not block activation',
)
assertEqual(
  canActivateLinklyConfiguration({
    ...activationBase,
    devices: [
      activationBase.devices[0],
      {
        ...activationBase.devices[0],
        deviceCode: 'POS-02',
      },
    ],
  }),
  false,
  'two enabled POS devices cannot activate with the same terminal',
)
assertEqual(
  getLinklyTerminalAssignmentOwner(activationBase, 'terminal-1', 'POS-01'),
  null,
  'the current POS keeps its own terminal option',
)
assertEqual(
  getLinklyTerminalAssignmentOwner(activationBase, 'terminal-1', 'POS-02'),
  'POS-01',
  'another POS sees the terminal owner',
)
assertEqual(
  getLinklyTerminalAssignmentOwner(activationBase, 'terminal-unassigned', 'POS-02'),
  null,
  'an unassigned terminal stays available',
)

const sandboxStatus = getEnvironmentStatus(
  [
    { environment: 'Production', configured: false, enabled: false },
    { environment: 'Sandbox', configured: true, enabled: true },
  ],
  'Sandbox',
)
assertEqual(sandboxStatus?.configured, true, 'environment status helper should select Sandbox')

assertEqual(
  resolvePaymentTerminalSettingsErrorMessage(new Error('backend detail'), 'fallback'),
  'backend detail',
  'error resolver should prefer backend detail',
)
assertEqual(
  resolvePaymentTerminalSettingsErrorMessage(new Error(''), 'fallback'),
  'fallback',
  'error resolver should fallback when message is blank',
)

assertEqual(zhLocale.menu.paymentTerminalSettings, '支付终端配置', 'Chinese menu text should exist')
assertEqual(enLocale.menu.paymentTerminalSettings, 'Payment Terminal Settings', 'English menu text should exist')
assertEqual(zhLocale.paymentTerminalSettings.squareTitle, 'Square Token', 'Chinese page text should exist')
assertEqual(enLocale.paymentTerminalSettings.linklyTitle, 'Linkly Cloud Credential', 'English page text should exist')

const routeSource = readFileSync('src/router/routes.tsx', 'utf8')
assert(routeSource.includes("path: '/system/payment-terminal-settings'"), 'route should include payment terminal path')
assert(routeSource.includes("title: 'menu.paymentTerminalSettings'"), 'route should include menu key')
assert(routeSource.includes("accessKey: 'canManageSystemSettings'"), 'route should use system settings access')

// 启用检查清单：必须与 canActivateLinklyConfiguration 同一口径，并能点名到具体设备。
const unpairedTerminal = {
  ...activationBase.terminals[0],
  terminalId: 'terminal-2',
  laneNo: 2,
  displayName: 'Back Counter',
  pairingState: 'Unpaired' as const,
  selectedDeviceCount: 0,
}
const checklistFixtures = [
  activationBase,
  { ...activationBase, terminals: [] },
  { ...activationBase, mode: 'Active' as const },
  { ...activationBase, devices: [activationBase.devices[0], { ...activationBase.devices[0], deviceCode: 'POS-02' }] },
  {
    ...activationBase,
    terminals: [...activationBase.terminals, unpairedTerminal],
    devices: [activationBase.devices[0], { ...activationBase.devices[0], deviceCode: 'POS-02', terminalId: 'terminal-2' }],
  },
]
for (const fixture of checklistFixtures) {
  assertEqual(
    buildLinklyActivationChecklist(fixture).canActivate,
    canActivateLinklyConfiguration(fixture),
    'activation checklist must share the activation rule',
  )
}

const readyChecklist = buildLinklyActivationChecklist(activationBase)
assertEqual(readyChecklist.terminalCount, 1, 'checklist counts terminals')
assertEqual(readyChecklist.readyTerminalCount, 1, 'checklist counts ready terminals')
assertEqual(readyChecklist.assignedDeviceCount, 1, 'checklist counts enabled POS with a Cloud selection')
assertEqual(readyChecklist.conflicts.length, 0, 'a single selection is not a conflict')

const conflictChecklist = buildLinklyActivationChecklist(checklistFixtures[3])
assertEqual(conflictChecklist.conflicts.length, 1, 'two enabled POS on one terminal is one conflict')
assertEqual(conflictChecklist.conflicts[0].deviceCodes.join(','), 'POS-01,POS-02', 'conflict names every POS involved')

const notReadyChecklist = buildLinklyActivationChecklist(checklistFixtures[4])
assertEqual(notReadyChecklist.notReadySelections.length, 1, 'selecting an unpaired terminal is reported')
assertEqual(notReadyChecklist.notReadySelections[0].deviceCode, 'POS-02', 'not-ready issue names the POS')

const disabledConflictChecklist = buildLinklyActivationChecklist({
  ...activationBase,
  devices: [activationBase.devices[0], { ...activationBase.devices[0], deviceCode: 'POS-OFF', enabled: false }],
})
assertEqual(disabledConflictChecklist.conflicts.length, 0, 'disabled POS selections do not block activation')

// 历史明文终端（hasPassword=false）即使没被任何 POS 选中，后端激活闸门也会拒绝；清单必须点名并阻止启用。
const legacyPlaintextTerminal = {
  ...unpairedTerminal,
  hasPassword: false,
}
const legacyUnselected = {
  ...activationBase,
  terminals: [...activationBase.terminals, legacyPlaintextTerminal],
}
assertEqual(
  canActivateLinklyConfiguration(legacyUnselected),
  false,
  'an unused terminal without a protected password blocks activation like the backend gate',
)
const legacyChecklist = buildLinklyActivationChecklist(legacyUnselected)
assertEqual(legacyChecklist.canActivate, false, 'checklist shares the credential gate')
assertEqual(
  legacyChecklist.missingCredentialTerminals.map((terminal) => terminal.displayName).join(','),
  'Back Counter',
  'checklist names the terminal that needs its password re-entered',
)
assertEqual(readyChecklist.missingCredentialTerminals.length, 0, 'complete credentials add no blocking item')

const orphanManagement = {
  ...activationBase,
  devices: [
    activationBase.devices[0],
    { deviceCode: 'POS-GONE', deviceSystem: '', enabled: false, deviceMissing: true, terminalId: 'terminal-1', revision: 3 },
  ],
}
const orphanChecklist = buildLinklyActivationChecklist(orphanManagement)
assertEqual(orphanChecklist.orphanSelections.length, 1, 'missing device that still holds a terminal is reported')
assertEqual(orphanChecklist.canActivate, true, 'missing device selections do not block activation')
assertEqual(
  getTerminalDeviceCodes(orphanManagement, 'terminal-1').join(','),
  'POS-01,POS-GONE',
  'terminal device list includes missing devices that still hold it',
)
assertEqual(buildLinklyActivationChecklist(null).canActivate, false, 'no management data cannot activate')

assertEqual(suggestNextLaneNo([]), 1, 'first terminal defaults to lane 1')
assertEqual(suggestNextLaneNo([{ laneNo: 1 }, { laneNo: 2 }, { laneNo: 4 }]), 3, 'new terminal fills the first unused lane')
assertEqual(suggestNextLaneNo([{ laneNo: 2 }]), 1, 'lane 1 is reused when free')

assertEqual(getTerminalHealthTone('Healthy'), 'healthy', 'Healthy maps to healthy')
assertEqual(getTerminalHealthTone(' unhealthy '), 'unhealthy', 'health tone ignores case and spaces')
assertEqual(getTerminalHealthTone('Degraded'), 'other', 'unknown health values are shown as-is')
assertEqual(getTerminalHealthTone(null), 'unchecked', 'missing health means not checked yet')

const healthNow = new Date('2026-10-06T10:00:00Z')
assertEqual(describeElapsedSince(null, healthNow).kind, 'never', 'missing timestamp is never')
assertEqual(describeElapsedSince('2026-10-06T09:59:40Z', healthNow).kind, 'justNow', 'under a minute is just now')
const minutesAgo = describeElapsedSince('2026-10-06 09:55:00', healthNow)
assertEqual(minutesAgo.kind === 'minutes' ? minutesAgo.count : -1, 5, 'SQL timestamps without Z are treated as UTC')
const hoursAgo = describeElapsedSince('2026-10-06T07:10:00Z', healthNow)
assertEqual(hoursAgo.kind === 'hours' ? hoursAgo.count : -1, 2, 'hours are floored')
const daysAgo = describeElapsedSince('2026-10-03T09:00:00Z', healthNow)
assertEqual(daysAgo.kind === 'days' ? daysAgo.count : -1, 3, 'days are floored')

// 页面结构契约：Square 是按环境的全局设置，不能再挂在门店选择器之下。
const pageSource = readFileSync('src/pages/System/PaymentTerminalSettings/index.tsx', 'utf8')
const squareCardIndex = pageSource.indexOf('className="pts-square"')
const storeSelectIndex = pageSource.indexOf('onChange={handleStoreChange}')
assert(squareCardIndex > 0 && storeSelectIndex > 0, 'page should render the Square card and the store selector')
assert(squareCardIndex < storeSelectIndex, 'Square card should render before (outside) the Linkly store selector')
assert(
  pageSource.includes("environment === 'Sandbox'") && pageSource.includes('pts-env-banner'),
  'sandbox environment should show a banner',
)
assert(
  pageSource.includes('buildLinklyActivationChecklist(linklyManagement)'),
  'activation button state should come from the shared checklist',
)

console.log('paymentTerminalSettings.logic.test: ok')
