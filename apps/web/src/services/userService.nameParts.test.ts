import assert from 'node:assert/strict'

import { getStoreUsersGrid } from './storeUserService'
import { createUser, updateUser } from './userService'

// 姓名拆分字段（givenName / middleName / familyName / nickname）的请求体与读取规范化契约。

const originalFetch = globalThis.fetch
const calls: Array<{ method: string; pathname: string; body: Record<string, unknown> | undefined }> = []
let nextResponseData: unknown = {}

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const requestUrl = new URL(String(input), 'http://localhost')
  calls.push({
    method: init?.method ?? 'GET',
    pathname: requestUrl.pathname,
    body: typeof init?.body === 'string' ? (JSON.parse(init.body) as Record<string, unknown>) : undefined,
  })
  return new Response(JSON.stringify({ success: true, data: nextResponseData }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

try {
  // 创建：填写的拆分字段按 PascalCase 透传，没填的为 null（不是空串）。
  nextResponseData = { userGUID: 'u1' }
  await createUser({
    username: 'zhang.wei',
    email: 'zhang.wei@example.com',
    password: 'secret-1',
    fullName: '张伟',
    familyName: '张',
    givenName: '伟',
  })
  const createBody = calls[0]?.body
  assert.equal(calls[0]?.method, 'POST')
  assert.equal(calls[0]?.pathname, '/api/Users')
  assert.equal(createBody?.FullName, '张伟', '创建仍然发送 FullName（兼容）')
  assert.equal(createBody?.FamilyName, '张')
  assert.equal(createBody?.GivenName, '伟')
  assert.equal(createBody?.MiddleName, null, '没填的中间名发 null')
  assert.equal(createBody?.Nickname, null, '没填的昵称发 null')

  // 更新：payload 原样作为请求体，调用方只传有变化的字段；没传的字段不会出现在请求体里（= 不修改）。
  nextResponseData = { userGUID: 'u1' }
  await updateUser('u1', {
    username: 'zhang.wei',
    email: 'zhang.wei@example.com',
    fullName: '张伟',
    isActive: true,
    nickname: '',
  })
  const updateBody = calls[1]?.body
  assert.equal(calls[1]?.method, 'PUT')
  assert.equal(calls[1]?.pathname, '/api/Users/guid/u1')
  assert.equal(updateBody?.fullName, '张伟', '更新仍然发送 fullName（兼容）')
  assert.equal(updateBody?.nickname, '', '空串 = 清空，必须原样发出')
  for (const key of ['givenName', 'middleName', 'familyName']) {
    assert.equal(key in (updateBody ?? {}), false, `未变化的 ${key} 不应出现在请求体中`)
  }

  // 读取：店铺员工列表的规范化同时兼容 camelCase / PascalCase，null 规范为 undefined。
  nextResponseData = {
    items: [
      { userGuid: 'a', username: 'a', status: 1, givenName: '伟', familyName: '张', middleName: null, nickname: 'Wei' },
      { UserGuid: 'b', Username: 'b', Status: 1, GivenName: 'Sam', Nickname: null },
      { userGuid: 'c', username: 'c', status: 1 },
    ],
    total: 3,
    page: 1,
    pageSize: 3,
  }
  const grid = await getStoreUsersGrid({ storeCode: '1001' })
  const [first, second, third] = grid.items
  assert.deepEqual(
    { givenName: first?.givenName, familyName: first?.familyName, middleName: first?.middleName, nickname: first?.nickname },
    { givenName: '伟', familyName: '张', middleName: undefined, nickname: 'Wei' },
  )
  assert.equal(second?.givenName, 'Sam', 'PascalCase 字段也要读取')
  assert.equal(second?.nickname, undefined, 'null 昵称规范为 undefined')
  assert.equal(third?.givenName, undefined, '缺省字段保持 undefined')
} finally {
  globalThis.fetch = originalFetch
}
