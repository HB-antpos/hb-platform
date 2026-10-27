import assert from 'node:assert/strict'
import { createInstance } from 'i18next'
import en from '../../../i18n/locales/en.json'
import zh from '../../../i18n/locales/zh.json'
import { createUser } from '../../../services/userService'
import { AUTH_EXPIRED_EVENT, RequestError, unwrapApiData } from '../../../utils/request'
import { getCreateUserErrorFeedback, getLevelDeniedMessage, getUpdateUserFieldError } from './createUserFeedback'

const i18n = createInstance()
await i18n.init({
  lng: 'zh',
  fallbackLng: 'zh',
  resources: { zh: { translation: zh }, en: { translation: en } },
  interpolation: { escapeValue: false },
})

function businessError(payload: Parameters<typeof unwrapApiData>[0]) {
  try {
    unwrapApiData(payload)
  } catch (error) {
    return error
  }
  throw new Error('测试前置条件失败：业务失败响应必须被转换为异常')
}

function feedbackFor(error: unknown) {
  const feedback = getCreateUserErrorFeedback(error, i18n.t)
  assert.ok(feedback, '非认证失效场景应返回创建用户反馈')
  return feedback
}

const emailError = businessError({
  success: false,
  errorCode: 'EMAIL_EXISTS',
  message: '邮箱已存在',
})
assert.equal((emailError as RequestError).status, 200)
assert.deepEqual(feedbackFor(emailError), {
  field: 'email',
  message: '该邮箱已被使用，请更换邮箱后重试。',
})

const usernameError = businessError({
  isSuccess: false,
  code: 'USERNAME_EXISTS',
  message: '用户名已存在',
})
assert.deepEqual(feedbackFor(usernameError), {
  field: 'username',
  message: '该用户名已被使用，请更换用户名后重试。',
})

// 「保存基本信息」：用户名 / 邮箱被其他账号占用时落到对应字段，其余错误交还调用方走通用提示。
assert.deepEqual(getUpdateUserFieldError(usernameError, i18n.t), {
  field: 'username',
  message: '该用户名已被使用，请更换用户名后再保存。',
})
assert.deepEqual(
  getUpdateUserFieldError(businessError({ success: false, errorCode: 'email_exists', message: '邮箱已被其他用户使用' }), i18n.t),
  { field: 'email', message: '该邮箱已被其他账号使用，请更换邮箱后再保存。' },
)
assert.equal(
  getUpdateUserFieldError(new RequestError('x', 200, { errorCode: 'UPDATE_USER_FAILED' }), i18n.t),
  null,
)
assert.equal(getUpdateUserFieldError(new Error('Network Error'), i18n.t), null)
assert.equal(
  getUpdateUserFieldError(usernameError, i18n.getFixedT('en'))?.message,
  'This username is already in use. Please use a different username and save again.',
)

for (const [code, status, expected] of [
  ['ADMIN_REQUIRED', 403, /没有.*权限.*管理员/],
  ['STORE_SCOPE_DENIED', 403, /分店.*管理范围.*调整/],
  ['VALIDATION_ERROR', 400, /检查|校验/],
  ['CREATE_USER_FAILED', 200, /稍后重试.*管理员/],
] as const) {
  const feedback = feedbackFor(
    new RequestError('internal-debug-text', status, { errorCode: code }),
  )
  assert.equal(feedback.field, undefined)
  assert.equal(feedback.resultUnconfirmed, undefined)
  assert.match(feedback.message, expected)
  assert.ok(!feedback.message.includes(code))
  assert.ok(!feedback.message.includes('internal-debug-text'))
}

for (const [status, expected] of [
  [403, /权限|管理员/],
  [400, /检查|校验/],
  [429, /频繁|稍后|稍候/],
] as const) {
  const feedback = feedbackFor(new RequestError('internal-debug-text', status))
  assert.match(feedback.message, expected)
  assert.ok(!feedback.message.includes('internal-debug-text'))
}

// 没有可靠结果时，提示先查列表，避免网络恢复后盲目重复创建。
for (const error of [
  new TypeError('Failed to fetch'),
  new Error('unexpected internal-debug-text'),
  new RequestError('SQL internal-debug-text', 500, { errorCode: 'INTERNAL_SERVER_ERROR' }),
  new RequestError('gateway internal-debug-text', 502, '<html>internal-debug-text</html>'),
  undefined,
  null,
]) {
  const feedback = feedbackFor(error)
  assert.equal(feedback.field, undefined)
  assert.equal(feedback.resultUnconfirmed, true)
  assert.match(feedback.message, /无法确认创建结果/)
  assert.match(feedback.message, /用户列表.*是否已创建/)
  assert.ok(!feedback.message.includes('internal-debug-text'))
  assert.ok(!feedback.message.includes('Failed to fetch'))
}

await i18n.changeLanguage('en')
for (const error of [emailError, usernameError, new RequestError('', 403), new TypeError('Failed to fetch')]) {
  const feedback = feedbackFor(error)
  assert.ok(feedback.message.length > 20)
  assert.ok(feedback.message.includes(' '))
  assert.doesNotMatch(feedback.message, /[\u4e00-\u9fff]|system\.users\.|EMAIL_EXISTS|USERNAME_EXISTS/)
}
assert.equal(feedbackFor(emailError).field, 'email')
assert.equal(feedbackFor(usernameError).field, 'username')

// 通过真实服务与请求层验证：认证失效交给统一登录跳转，不依赖即将卸载的表单提示。
const originalFetch = globalThis.fetch
const originalWindow = globalThis.window
const requests: string[] = []
const events: string[] = []
let redirectedTo = ''
Object.defineProperty(globalThis, 'window', {
  configurable: true,
  value: {
    location: { pathname: '/system/users', search: '', replace: (url: string) => { redirectedTo = url } },
    dispatchEvent: (event: Event) => { events.push(event.type); return true },
    sessionStorage: {
      getItem: () => JSON.stringify({ ip: '8.8.8.88', expiresAt: Date.now() + 60_000 }),
      setItem: () => undefined,
    },
    setTimeout,
    clearTimeout,
  },
})
globalThis.fetch = async (input) => {
  requests.push(String(input))
  return new Response(JSON.stringify({ success: false, message: 'unauthorized' }), {
    status: 401, headers: { 'Content-Type': 'application/json' },
  })
}
try {
  await assert.rejects(() => createUser({
    username: 'preview-user', email: 'preview@example.test', password: 'test-only-123', passwordFormat: 'raw',
  }), (error: unknown) => {
    assert.ok(error instanceof RequestError)
    assert.equal(error.status, 401)
    assert.equal(getCreateUserErrorFeedback(error, i18n.t), null)
    return true
  })
  assert.deepEqual(requests, ['/api/Users', '/api/Auth/session/refresh'])
  assert.deepEqual(events, [AUTH_EXPIRED_EVENT])
  assert.equal(redirectedTo, '/login?redirect=%2Fsystem%2Fusers')
} finally {
  globalThis.fetch = originalFetch
  Object.defineProperty(globalThis, 'window', { configurable: true, value: originalWindow })
}

console.log('创建用户错误反馈测试通过：业务错误、字段定位、中英文、结果未确认和真实认证失效链路')

// 角色等级：后端 TARGET_LEVEL_DENIED / ROLE_LEVEL_DENIED 映射为明确的中文原因，其他错误不接管。
// 前面的用例会切换 i18n 语言，这里显式固定语言，不依赖前序状态。
const tZh = i18n.getFixedT('zh')
const tEn = i18n.getFixedT('en')
assert.equal(
  getLevelDeniedMessage(
    businessError({ success: false, errorCode: 'TARGET_LEVEL_DENIED', message: 'x' }),
    tZh,
  ),
  '该账号的角色等级不低于你，无权修改。',
)
assert.equal(
  getLevelDeniedMessage(
    businessError({ success: false, errorCode: 'role_level_denied', message: 'x' }),
    tZh,
  ),
  '不能授予等级不低于你自己的角色。',
)
assert.equal(
  getLevelDeniedMessage(
    businessError({ success: false, errorCode: 'USER_SCOPE_DENIED', message: 'x' }),
    tZh,
  ),
  null,
)
assert.equal(getLevelDeniedMessage(new Error('boom'), tZh), null)
assert.equal(
  getLevelDeniedMessage(
    businessError({ success: false, errorCode: 'ROLE_LEVEL_DENIED', message: 'x' }),
    tEn,
  ),
  'You cannot grant a role at or above your own level.',
)
