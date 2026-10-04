import {
  getInvoiceEmailSenderAccounts,
  getInvoiceEmailSettings,
  saveInvoiceEmailSettings,
  sendInvoiceEmailSettingsTestEmail,
} from './invoiceEmailSettingsService'

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

function readBody(call: { init?: RequestInit }) {
  return JSON.parse(String(call.init?.body)) as {
    accounts?: Array<Record<string, unknown>>
    [key: string]: unknown
  }
}

const originalFetch = globalThis.fetch
const calls: Array<{ url: string; init?: RequestInit }> = []

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = String(input)
  calls.push({ url, init })

  if (url.endsWith('/sender-accounts')) {
    return new Response(
      JSON.stringify({
        success: true,
        data: [
          { id: 'primary', name: 'Primary', fromEmail: 'from@test.com', fromName: null, isDefault: true },
          { id: 'backup', name: 'Backup', fromEmail: 'backup@test.com', fromName: null, isDefault: false },
        ],
      }),
      { status: 200, headers: { 'content-type': 'application/json' } },
    )
  }

  if (url.endsWith('/test')) {
    return new Response(
      JSON.stringify({
        success: true,
        data: {
          success: true,
          message: '测试邮件发送成功',
        },
      }),
      { status: 200, headers: { 'content-type': 'application/json' } },
    )
  }

  if (init?.method === 'PUT') {
    return new Response(
      JSON.stringify({
        success: true,
        data: {
          accounts: [
            {
              id: 'primary',
              name: 'Primary',
              host: 'smtp.changed.test',
              port: 587,
              useSsl: true,
              checkCertificateRevocation: false,
              username: 'changed-user',
              hasPassword: true,
              fromEmail: 'invoice@test.com',
              fromName: 'HB Invoice',
              maxAttachmentBytes: 10485760,
              isDefault: true,
            },
          ],
        },
      }),
      { status: 200, headers: { 'content-type': 'application/json' } },
    )
  }

  return new Response(
    JSON.stringify({
      success: true,
      data: {
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
        ],
      },
    }),
    { status: 200, headers: { 'content-type': 'application/json' } },
  )
}) as typeof fetch

try {
  const current = await getInvoiceEmailSettings()
  assertEqual(calls[0]?.url, '/api/react/v1/invoice-email-settings', '读取配置接口路径应正确')
  assertEqual(calls[0]?.init?.method, 'GET', '读取配置接口应使用 GET')
  assertEqual(current.accounts[0]?.host, 'smtp.initial.test', '读取配置应返回账号 host')
  assertEqual(current.accounts[0]?.hasPassword, true, '读取配置应返回账号 hasPassword')

  const saved = await saveInvoiceEmailSettings({
    accounts: [
      {
        id: 'primary',
        name: 'Primary',
        host: 'smtp.changed.test',
        port: 587,
        useSsl: true,
        checkCertificateRevocation: false,
        username: 'changed-user',
        password: '',
        clearPassword: false,
        fromEmail: 'invoice@test.com',
        fromName: 'HB Invoice',
        maxAttachmentBytes: 10485760,
        isDefault: true,
      },
    ],
  })
  assertEqual(calls[1]?.url, '/api/react/v1/invoice-email-settings', '保存配置接口路径应正确')
  assertEqual(calls[1]?.init?.method, 'PUT', '保存配置接口应使用 PUT')
  assertEqual(readBody(calls[1]).accounts?.[0]?.host, 'smtp.changed.test', '保存配置应传递账号 host')
  assertEqual(readBody(calls[1]).accounts?.[0]?.password, '', '保存配置应保留前端传入的 password 值')
  assertEqual(saved.accounts[0]?.host, 'smtp.changed.test', '保存配置应返回最新账号 host')

  const sendResult = await sendInvoiceEmailSettingsTestEmail({
    id: 'primary',
    name: 'Primary',
    host: 'smtp.changed.test',
    port: 587,
    useSsl: true,
    checkCertificateRevocation: false,
    username: 'changed-user',
    password: 'top-secret',
    clearPassword: false,
    fromEmail: 'invoice@test.com',
    fromName: 'HB Invoice',
    maxAttachmentBytes: 10485760,
    isDefault: true,
    testToEmail: 'qa@test.com',
  })
  assertEqual(calls[2]?.url, '/api/react/v1/invoice-email-settings/test', '测试邮件接口路径应正确')
  assertEqual(calls[2]?.init?.method, 'POST', '测试邮件接口应使用 POST')
  assertEqual(readBody(calls[2]).testToEmail, 'qa@test.com', '测试邮件应传递测试邮箱')
  assertEqual(readBody(calls[2]).password, 'top-secret', '测试邮件应允许携带临时密码')
  assert(sendResult.success, '测试邮件接口应返回成功结果')

  const senderAccounts = await getInvoiceEmailSenderAccounts()
  assertEqual(calls[3]?.url, '/api/react/v1/invoice-email-settings/sender-accounts', '发件账号列表接口路径应正确')
  assertEqual(calls[3]?.init?.method, 'GET', '发件账号列表接口应使用 GET')
  assertEqual(senderAccounts.length, 2, '发件账号列表应返回全部账号')
  assertEqual(senderAccounts[1]?.id, 'backup', '发件账号列表应保留账号 ID')

  console.log('invoiceEmailSettingsService.test: ok')
} finally {
  globalThis.fetch = originalFetch
}
