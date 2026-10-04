import type {
  InvoiceEmailAccountDto,
  InvoiceEmailAccountSaveRequest,
  InvoiceEmailSettingsDto,
  InvoiceEmailSettingsSaveRequest,
  InvoiceEmailSettingsTestRequest,
} from '../../../types/invoiceEmailSettings'

export interface InvoiceEmailAccountFormValues {
  id?: string
  name: string
  host: string
  port: number
  useSsl: boolean
  checkCertificateRevocation: boolean
  username: string
  password: string
  clearPassword: boolean
  fromEmail: string
  fromName: string
  maxAttachmentMegabytes: number
  isDefault: boolean
  hasPassword: boolean
  testToEmail: string
}

export interface InvoiceEmailSettingsFormValues {
  accounts: InvoiceEmailAccountFormValues[]
}

const BYTES_PER_MEGABYTE = 1024 * 1024

export function createNewInvoiceEmailAccountFormValue(
  index: number,
  labels: { defaultName: string; accountNamePrefix: string } = {
    defaultName: '默认发件账号',
    accountNamePrefix: '发件账号',
  },
): InvoiceEmailAccountFormValues {
  return {
    name: index <= 0 ? labels.defaultName : `${labels.accountNamePrefix} ${index + 1}`,
    host: '',
    port: 25,
    useSsl: true,
    checkCertificateRevocation: true,
    username: '',
    password: '',
    clearPassword: false,
    fromEmail: '',
    fromName: '',
    maxAttachmentMegabytes: 10,
    isDefault: index === 0,
    hasPassword: false,
    testToEmail: '',
  }
}

export function createInvoiceEmailSettingsFormValues(settings: InvoiceEmailSettingsDto): InvoiceEmailSettingsFormValues {
  const accounts = settings.accounts.length > 0
    ? settings.accounts.map(createAccountFormValues)
    : [createNewInvoiceEmailAccountFormValue(0)]

  return {
    accounts: ensureInvoiceEmailDefaultAccount(accounts),
  }
}

export function ensureInvoiceEmailDefaultAccount(
  accounts: InvoiceEmailAccountFormValues[],
  preferredIndex = 0,
): InvoiceEmailAccountFormValues[] {
  if (accounts.length === 0) {
    return [createNewInvoiceEmailAccountFormValue(0)]
  }

  const currentDefaultIndex = accounts.findIndex((account) => account.isDefault)
  const defaultIndex = currentDefaultIndex >= 0
    ? currentDefaultIndex
    : Math.min(Math.max(preferredIndex, 0), accounts.length - 1)

  // 页面上只允许一个默认账号，删除默认账号后自动把剩余账号里的一个设为默认。
  return accounts.map((account, index) => ({
    ...account,
    isDefault: index === defaultIndex,
  }))
}

export function setInvoiceEmailDefaultAccount(
  accounts: InvoiceEmailAccountFormValues[],
  defaultIndex: number,
): InvoiceEmailAccountFormValues[] {
  return ensureInvoiceEmailDefaultAccount(
    accounts.map((account, index) => ({
      ...account,
      isDefault: index === defaultIndex,
    })),
    defaultIndex,
  )
}

export function buildInvoiceEmailSettingsSavePayload(
  values: InvoiceEmailSettingsFormValues,
): InvoiceEmailSettingsSaveRequest {
  return {
    accounts: ensureInvoiceEmailDefaultAccount(values.accounts).map(normalizeAccountPayload),
  }
}

export function buildInvoiceEmailSettingsTestPayload(
  account: InvoiceEmailAccountFormValues,
): InvoiceEmailSettingsTestRequest {
  return {
    ...normalizeAccountPayload(account),
    testToEmail: account.testToEmail.trim(),
  }
}

/**
 * 从表单校验错误里找出出错的账号序号（升序去重）。
 * 页面一次只展示一个账号，保存失败时要靠它跳到第一个出错账号并在左侧列表标红。
 */
export function collectInvoiceEmailAccountErrorIndexes(
  errorFields: { name: (string | number)[]; errors?: string[] }[],
): number[] {
  const indexes = new Set<number>()
  for (const field of errorFields) {
    if (field.errors && field.errors.length === 0) {
      continue
    }
    const [listName, index] = field.name
    if (listName === 'accounts' && typeof index === 'number') {
      indexes.add(index)
    }
  }
  return [...indexes].sort((left, right) => left - right)
}

export function resolveInvoiceEmailSettingsErrorMessage(error: unknown, fallback: string) {
  // 请求层会把后端 ApiResponse.message 放入 Error.message，这里优先展示具体 SMTP/TLS 失败原因。
  return error instanceof Error && error.message.trim() ? error.message : fallback
}

function createAccountFormValues(account: InvoiceEmailAccountDto): InvoiceEmailAccountFormValues {
  return {
    id: account.id,
    name: account.name,
    host: account.host ?? '',
    port: account.port,
    useSsl: account.useSsl,
    checkCertificateRevocation: account.checkCertificateRevocation,
    username: account.username ?? '',
    password: '',
    clearPassword: false,
    fromEmail: account.fromEmail ?? '',
    fromName: account.fromName ?? '',
    maxAttachmentMegabytes: bytesToMegabytes(account.maxAttachmentBytes),
    isDefault: account.isDefault,
    hasPassword: account.hasPassword,
    testToEmail: '',
  }
}

function normalizeAccountPayload(account: InvoiceEmailAccountFormValues): InvoiceEmailAccountSaveRequest {
  const trimmedPassword = account.password.trim()
  const trimmedId = account.id?.trim()

  const payload: InvoiceEmailAccountSaveRequest = {
    name: account.name.trim(),
    host: account.host.trim(),
    port: account.port,
    useSsl: account.useSsl,
    checkCertificateRevocation: account.checkCertificateRevocation,
    username: account.username.trim(),
    clearPassword: account.clearPassword,
    fromEmail: account.fromEmail.trim(),
    fromName: account.fromName.trim(),
    maxAttachmentBytes: megabytesToBytes(account.maxAttachmentMegabytes),
    isDefault: account.isDefault,
  }

  if (trimmedId) {
    payload.id = trimmedId
  }

  // 后端约定 clearPassword=true 时只清空原密码，不接受新密码。
  if (!account.clearPassword && trimmedPassword) {
    payload.password = trimmedPassword
  }

  return payload
}

function bytesToMegabytes(bytes: number): number {
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return 1
  }

  return Math.max(0.01, Number((bytes / BYTES_PER_MEGABYTE).toFixed(2)))
}

function megabytesToBytes(megabytes: number): number {
  if (!Number.isFinite(megabytes) || megabytes <= 0) {
    return BYTES_PER_MEGABYTE
  }

  return Math.round(megabytes * BYTES_PER_MEGABYTE)
}
