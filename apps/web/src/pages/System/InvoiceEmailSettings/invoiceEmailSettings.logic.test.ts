import { readFileSync } from 'node:fs'
import enLocale from '../../../i18n/locales/en.json'
import zhLocale from '../../../i18n/locales/zh.json'
import { buildAccess } from '../../../utils/access'
import { buildWebRoleMenuPreview } from '../../../utils/webMenuPreview'
import type { CurrentUser } from '../../../types/auth'
import { P } from '../../../types/permissions'
import { RequestError } from '../../../utils/request'
import {
  buildInvoiceEmailSettingsSavePayload,
  buildInvoiceEmailSettingsTestPayload,
  collectInvoiceEmailAccountErrorIndexes,
  createInvoiceEmailSettingsFormValues,
  createNewInvoiceEmailAccountFormValue,
  ensureInvoiceEmailDefaultAccount,
  resolveInvoiceEmailSettingsErrorMessage,
  setInvoiceEmailDefaultAccount,
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
    userGUID: 'invoice-settings-user',
    username: 'tester',
    email: 'tester@example.com',
    permissions: [],
    roleNames: [],
    storeNames: [],
    ...overrides,
  }
}

const permissionAccess = buildAccess(
  createCurrentUser({
    permissions: [P.System.ManageSettings],
  }),
)
assertEqual(permissionAccess.canManageSystemSettings, true, 'System.ManageSettings 应解锁系统设置管理访问')

const deniedAccess = buildAccess(createCurrentUser())
assertEqual(deniedAccess.canManageSystemSettings, false, '缺少 System.ManageSettings 时不应解锁系统设置管理访问')

const translate = (key: string, fallback?: string) => fallback ?? key
const visibleSystemMenu = buildWebRoleMenuPreview(permissionAccess, translate)
const invoiceEmailMenu = visibleSystemMenu
  .find((node) => node.path === '/system')
  ?.children?.find((node) => node.path === '/system/invoice-email-settings')

assert(invoiceEmailMenu, '拥有 System.ManageSettings 时系统菜单应显示发票邮箱配置')
assertEqual(
  invoiceEmailMenu?.permissionCodes.join(','),
  P.System.ManageSettings,
  '发票邮箱配置菜单应展示 System.ManageSettings 权限',
)

const hiddenSystemMenu = buildWebRoleMenuPreview(deniedAccess, translate, { includeHidden: true })
const hiddenInvoiceEmailMenu = hiddenSystemMenu
  .find((node) => node.path === '/system')
  ?.children?.find((node) => node.path === '/system/invoice-email-settings')

assertEqual(hiddenInvoiceEmailMenu?.visible, false, '缺少权限时发票邮箱配置菜单应保持隐藏')

assertEqual(zhLocale.menu.invoiceEmailSettings, '发票邮箱配置', '中文菜单文案应存在')
assertEqual(enLocale.menu.invoiceEmailSettings, 'Invoice Email Settings', '英文菜单文案应存在')
assertEqual(zhLocale.invoiceEmailSettings.testToEmail, '测试收件邮箱', '中文页面文案应存在')
assertEqual(enLocale.invoiceEmailSettings.testToEmail, 'Test recipient email', '英文页面文案应存在')
assertEqual(zhLocale.invoiceEmailSettings.addAccount, '新增发件账号', '中文新增账号文案应存在')
assertEqual(enLocale.invoiceEmailSettings.defaultAccount, 'Default account', '英文默认账号文案应存在')
assertEqual(zhLocale.invoiceEmailSettings.maxAttachmentBytes, '附件大小上限（MB）', '附件大小中文文案应按 MB 显示')

const formValues = createInvoiceEmailSettingsFormValues({
  accounts: [
    {
      id: 'primary',
      name: 'Primary',
      host: 'smtp.initial.test',
      port: 465,
      useSsl: true,
      checkCertificateRevocation: true,
      username: 'initial-user',
      hasPassword: true,
      fromEmail: 'from@test.com',
      fromName: 'Initial Sender',
      maxAttachmentBytes: 2097152,
      isDefault: true,
    },
    {
      id: 'backup',
      name: 'Backup',
      host: 'smtp.backup.test',
      port: 587,
      useSsl: false,
      checkCertificateRevocation: false,
      username: 'backup-user',
      hasPassword: false,
      fromEmail: 'backup@test.com',
      fromName: 'Backup Sender',
      maxAttachmentBytes: 4096,
      isDefault: false,
    },
  ],
})

assertEqual(formValues.accounts.length, 2, '初始化表单应保留所有账号')
assertEqual(formValues.accounts[0].password, '', '初始化表单时不应回填密码明文')
assertEqual(formValues.accounts[0].clearPassword, false, '初始化表单时 clearPassword 应默认为 false')
assertEqual(formValues.accounts[0].isDefault, true, '初始化表单应保留默认账号')
assertEqual(formValues.accounts[0].maxAttachmentMegabytes, 2, '附件大小应按 MB 显示')
assertEqual(formValues.accounts[1].maxAttachmentMegabytes, 0.01, '小于 0.01MB 的附件上限应按最小步进显示')

const newAccount = createNewInvoiceEmailAccountFormValue(1, {
  defaultName: 'Default sender',
  accountNamePrefix: 'Sender',
})
assertEqual(newAccount.name, 'Sender 2', '新增账号应支持本地化默认名称')
assertEqual(newAccount.isDefault, false, '新增非首个账号不应自动成为默认账号')

const switchedDefaults = setInvoiceEmailDefaultAccount(formValues.accounts, 1)
assertEqual(switchedDefaults[0].isDefault, false, '设为默认时旧默认账号应取消默认')
assertEqual(switchedDefaults[1].isDefault, true, '设为默认时目标账号应成为默认')

const repairedDefaults = ensureInvoiceEmailDefaultAccount([
  { ...formValues.accounts[0], isDefault: false },
  { ...formValues.accounts[1], isDefault: false },
], 1)
assertEqual(repairedDefaults[1].isDefault, true, '缺少默认账号时应支持按指定位置修复默认账号')

const defaultAfterDelete = ensureInvoiceEmailDefaultAccount([
  { ...formValues.accounts[0], isDefault: false },
  { ...formValues.accounts[1], isDefault: false },
], 0)
assertEqual(defaultAfterDelete[0].isDefault, true, '删除默认账号后页面应把剩余第一项设为默认账号')

const keepPasswordPayload = buildInvoiceEmailSettingsSavePayload({
  accounts: [
    {
      ...formValues.accounts[0],
      host: 'smtp.changed.test',
      clearPassword: false,
      password: '   ',
    },
  ],
})

assertEqual(keepPasswordPayload.accounts.length, 1, '保存 payload 应携带账号列表')
assert('password' in keepPasswordPayload.accounts[0] === false, '空白密码保存时应省略 password 字段以保留原密码')
assertEqual(keepPasswordPayload.accounts[0].clearPassword, false, '保留原密码时 clearPassword 应为 false')
assertEqual(keepPasswordPayload.accounts[0].maxAttachmentBytes, 2097152, '保存 payload 应把 MB 转回字节')

const clearPasswordPayload = buildInvoiceEmailSettingsSavePayload({
  accounts: [
    {
      ...formValues.accounts[0],
      clearPassword: true,
      password: 'new-secret',
    },
  ],
})

assert('password' in clearPasswordPayload.accounts[0] === false, '清空密码时不应同时发送 password')
assertEqual(clearPasswordPayload.accounts[0].clearPassword, true, '清空密码时应发送 clearPassword=true')

const updatePasswordPayload = buildInvoiceEmailSettingsSavePayload({
  accounts: [
    {
      ...formValues.accounts[0],
      clearPassword: false,
      password: 'new-secret',
    },
  ],
})

assertEqual(updatePasswordPayload.accounts[0].password, 'new-secret', '填写新密码时应提交 password')

const testPayload = buildInvoiceEmailSettingsTestPayload({
  ...formValues.accounts[0],
  host: 'smtp.changed.test',
  testToEmail: 'qa@test.com',
  password: 'temporary-secret',
})

assertEqual(testPayload.id, 'primary', '测试邮件 payload 应携带账号 ID')
assertEqual(testPayload.testToEmail, 'qa@test.com', '测试邮件 payload 应携带测试收件邮箱')
assertEqual(testPayload.password, 'temporary-secret', '测试邮件 payload 应允许携带当前输入的密码')

const smtpFailure = new RequestError(
  '发票邮件 TLS 握手失败，请检查 SMTP 证书或 InvoiceEmail.CheckCertificateRevocation 配置',
  400,
)
assertEqual(
  resolveInvoiceEmailSettingsErrorMessage(smtpFailure, '发送测试邮件失败'),
  smtpFailure.message,
  '测试邮件失败时应优先展示后端返回的具体原因',
)
assertEqual(
  resolveInvoiceEmailSettingsErrorMessage(new Error(''), '发送测试邮件失败'),
  '发送测试邮件失败',
  '错误消息为空时应回退到页面默认文案',
)

assertEqual(
  collectInvoiceEmailAccountErrorIndexes([
    { name: ['accounts', 2, 'host'], errors: ['请输入 SMTP 主机'] },
    { name: ['accounts', 0, 'fromEmail'], errors: ['请输入发件邮箱'] },
    { name: ['accounts', 2, 'port'], errors: ['请输入 SMTP 端口'] },
    { name: ['accounts', 1, 'name'], errors: [] },
    { name: ['other', 3], errors: ['x'] },
  ]).join(','),
  '0,2',
  '出错账号序号应升序去重，并忽略无错误字段与非账号字段',
)

const routeSource = readFileSync('src/router/routes.tsx', 'utf8')
assert(
  routeSource.includes("path: '/system/invoice-email-settings'"),
  '路由源码中应包含发票邮箱配置路径，避免后续误删',
)
assert(
  routeSource.includes("title: 'menu.invoiceEmailSettings'"),
  '路由源码中应包含发票邮箱配置菜单标题 key',
)
assert(
  routeSource.includes("accessKey: 'canManageSystemSettings'"),
  '路由源码中应使用 canManageSystemSettings 控制发票邮箱配置访问',
)

console.log('invoiceEmailSettings.logic.test: ok')
