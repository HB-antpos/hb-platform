import assert from 'node:assert/strict'

import { resolveLoginToggleErrorKind } from '../pages/System/Users/usersPageLogic'
import { getUserByGuid, getUsers, updateUserLoginEnabled } from './userService'

// 管理员开关「允许登录」：请求形状、成功解包，以及失败时错误码能被页面逻辑归类。

const originalFetch = globalThis.fetch
const calls: Array<{ method: string; pathname: string; body: Record<string, unknown> | undefined }> = []
let nextStatus = 200
let nextPayload: unknown = { success: true, data: true }

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const requestUrl = new URL(String(input), 'http://localhost')
  calls.push({
    method: init?.method ?? 'GET',
    pathname: requestUrl.pathname,
    body: typeof init?.body === 'string' ? (JSON.parse(init.body) as Record<string, unknown>) : undefined,
  })
  return new Response(JSON.stringify(nextPayload), {
    status: nextStatus,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

/** 只取指向目标接口的调用：请求层在非 2xx 时可能额外上报日志，不能干扰断言。 */
const callsTo = (pathname: string) => calls.filter((call) => call.pathname === pathname)

try {
  // 关闭：PUT /api/Users/guid/{guid}/login-enabled，body 只有 loginEnabled（camelCase、布尔值）。
  const disabled = await updateUserLoginEnabled('u1', false)
  assert.equal(disabled, true)
  const [first] = callsTo('/api/Users/guid/u1/login-enabled')
  assert.equal(first?.method, 'PUT')
  assert.deepEqual(first?.body, { loginEnabled: false }, '关闭必须明确发 false，而不是省略字段（后端缺字段会 400）')

  // 开启：同一路径，发 true。
  calls.length = 0
  await updateUserLoginEnabled('u1', true)
  assert.deepEqual(callsTo('/api/Users/guid/u1/login-enabled')[0]?.body, { loginEnabled: true })

  // guid 做 URL 编码，不能让特殊字符改变路径。
  calls.length = 0
  await updateUserLoginEnabled('a/b c', true)
  assert.equal(calls[0]?.pathname, '/api/Users/guid/a%2Fb%20c/login-enabled')

  // 业务失败（HTTP 200 + success:false）：抛 RequestError，页面逻辑可按 errorCode 归类。
  nextStatus = 200
  nextPayload = { success: false, errorCode: 'SELF_PROFILE_FIELDS_DENIED', message: '不能关闭本人账号的登录权限' }
  const selfError = await updateUserLoginEnabled('me', false).then(() => undefined, (error: unknown) => error)
  assert.ok(selfError, '业务失败必须抛错，不能让页面误报成功')
  assert.equal(resolveLoginToggleErrorKind(selfError), 'self')

  // HTTP 4xx + 业务码：同样归类。
  nextStatus = 403
  nextPayload = { success: false, errorCode: 'USER_SCOPE_DENIED', message: '无权操作该用户' }
  const scopeError = await updateUserLoginEnabled('other', false).then(() => undefined, (error: unknown) => error)
  assert.equal(resolveLoginToggleErrorKind(scopeError), 'scopeDenied')

  nextStatus = 403
  nextPayload = { message: 'Forbidden' }
  const forbiddenError = await updateUserLoginEnabled('other', false).then(() => undefined, (error: unknown) => error)
  assert.equal(resolveLoginToggleErrorKind(forbiddenError), 'forbidden', '没有业务码的 403 视为没有 Users.Edit 权限')

  // 读取：详情与列表项里的 loginEnabled 原样透传；缺省保持 undefined（不被补成 true）。
  nextStatus = 200
  nextPayload = { success: true, data: { userGUID: 'u1', username: 'kevin', loginEnabled: false } }
  assert.equal((await getUserByGuid('u1')).loginEnabled, false)

  nextPayload = {
    success: true,
    data: {
      items: [
        { userGUID: 'a', username: 'a', loginEnabled: false },
        { userGUID: 'b', username: 'b', loginEnabled: true },
        { userGUID: 'c', username: 'c' },
      ],
      total: 3,
      page: 1,
      pageSize: 10,
    },
  }
  const list = await getUsers({ page: 1, pageSize: 10 })
  assert.deepEqual(list.items.map((item) => item.loginEnabled), [false, true, undefined])
} finally {
  globalThis.fetch = originalFetch
}
