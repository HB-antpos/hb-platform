import { RequestError } from '../../../utils/request'
import { readFileSync } from 'node:fs'
import {
  createLatestRequestGuard,
  getExpectedSensitiveRevision,
  getChangedSensitiveFields,
  getReviewChangedFields,
  handleSensitiveReviewFailure,
  isRejectReasonValid,
  isSensitiveRequestReviewable,
  maskSensitiveSummary,
  saveAdminProfileWithPendingConfirmation,
} from './logic'

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

const current = {
  bankBsb: '123-456',
  bankAccountNumber: '12345678',
  superannuationCompanyName: 'Current Super',
  superannuationCompanyCode: 'CUR',
  superannuationAccountNumber: 'SUPER-1111',
  identityType: 'Passport',
  identityId: 'P1234567',
  identityPhotoUrl: 'https://example.com/current.jpg',
}

const proposed = {
  bankBsb: '123-456',
  bankAccountNumber: '87654321',
  superannuationCompanyName: 'Current Super',
  superannuationCompanyCode: 'CUR',
  superannuationAccountNumber: 'SUPER-2222',
  identityType: 'Passport',
  identityId: 'P1234567',
  identityPhotoUrl: 'https://example.com/pending.jpg',
}

const changed = getChangedSensitiveFields(current, proposed)
assertEqual(changed.join(','), 'bankAccountNumber,superannuationAccountNumber,identityPhotoUrl', '应只计算真正变化的敏感字段')
assertEqual(getChangedSensitiveFields(current, { ...current, bankBsb: ' 123-456 ' }).length, 0, '比较前应规范化首尾空白')
assertEqual(
  getChangedSensitiveFields(
    { ...current, birthday: '1998-06-21T00:00:00' },
    { ...current, birthday: '1998-06-21' },
  ).length,
  0,
  '生日只比较日期部分，后端日期时间与表单日期相同时不算变更',
)
assertEqual(
  getChangedSensitiveFields(
    { ...current, birthday: '1998-06-21T00:00:00' },
    { ...current, birthday: '1998-06-12T00:00:00' },
  ).join(','),
  'birthday',
  '生日变化必须进入敏感变更字段',
)
assertEqual(
  getReviewChangedFields(
    { ...current, identityPhotoUrl: 'https://cdn/photo.jpg?signature=old' },
    { ...current, identityPhotoUrl: 'https://cdn/photo.jpg?signature=new' },
    false,
  ).includes('identityPhotoUrl'),
  false,
  '同一证件对象的不同短效签名 URL 不得误判为照片变更',
)
assertEqual(
  getReviewChangedFields(current, current, true).includes('identityPhotoUrl'),
  true,
  '证件照差异必须直接信任后端持久化 changedFields',
)
assertEqual(getExpectedSensitiveRevision({ ...current, sensitiveRevision: 4 }), 4, '后台保存必须携带打开详情时的 revision')
assertEqual(isSensitiveRequestReviewable('Pending'), true, 'Pending 申请应显示审核按钮')
assertEqual(isSensitiveRequestReviewable('Approved'), false, '终态申请刷新后必须禁用审核按钮')

const masked = maskSensitiveSummary('full-sensitive-account-6789')
assertEqual(masked, '****6789', '列表摘要只保留末四位')
assert(!masked.includes('full-sensitive-account'), '列表摘要不得泄露完整账号')

assertEqual(isRejectReasonValid('  '), false, '拒绝原因不得为空')
assertEqual(isRejectReasonValid('资料无法核验'), true, '非空拒绝原因应通过校验')

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((nextResolve) => { resolve = nextResolve })
  return { promise, resolve }
}

const requestGuard = createLatestRequestGuard()
const appliedDetails: string[] = []
const detailA = deferred<string>()
const detailB = deferred<string>()
const runDetailLoad = async (detailPromise: Promise<string>) => {
  const token = requestGuard.begin()
  const detail = await detailPromise
  if (requestGuard.isCurrent(token)) {
    appliedDetails.push(detail)
  }
}
const loadingA = runDetailLoad(detailA.promise)
const loadingB = runDetailLoad(detailB.promise)
detailB.resolve('B')
await loadingB
detailA.resolve('A')
await loadingA
assertEqual(appliedDetails.join(','), 'B', 'A 后于 B 返回时不得覆盖当前审核详情')
const detailAfterClose = deferred<string>()
const loadingAfterClose = runDetailLoad(detailAfterClose.promise)
requestGuard.invalidate()
detailAfterClose.resolve('closed')
await loadingAfterClose
assertEqual(appliedDetails.join(','), 'B', '关闭抽屉后旧响应不得重新写入详情')

const retryPayloads: Array<Record<string, unknown>> = []
let confirmationCalls = 0
const retryResult = await saveAdminProfileWithPendingConfirmation(
  { userGUID: 'user-guid', bankAccountNumber: 'admin-new', expectedSensitiveRevision: 3 },
  async (payload) => {
    retryPayloads.push(payload)
    if (retryPayloads.length === 1) {
      throw new RequestError('需要确认', 409, {
        errorCode: 'EMPLOYEE_PROFILE_PENDING_CHANGE_CONFIRMATION_REQUIRED',
      })
    }
    return 'saved'
  },
  async () => {
    confirmationCalls += 1
    return true
  },
)
assertEqual(retryResult.status, 'saved', '管理员确认后应重试并保存')
assertEqual(confirmationCalls, 1, '仅在服务端返回 409 后弹出一次确认')
assertEqual(retryPayloads.length, 2, '确认后必须仅重试一次')
assertEqual(
  retryPayloads[1]?.confirmSupersedePendingSensitiveChangeRequest,
  true,
  '重试请求必须携带原子确认标志',
)
assertEqual(retryPayloads[1]?.expectedSensitiveRevision, 3, '确认重试必须保留最初表单 revision')

let detailRefreshes = 0
let listRefreshes = 0
let pendingCountRefreshes = 0
const conflict = new RequestError('版本冲突', 409, {
  success: false,
  errorCode: 'EMPLOYEE_PROFILE_SENSITIVE_VERSION_CONFLICT',
})
const handled = await handleSensitiveReviewFailure(
  conflict,
  async () => { detailRefreshes += 1 },
  async () => { listRefreshes += 1 },
  async () => { pendingCountRefreshes += 1 },
)
assertEqual(handled, 'version', '敏感资料版本冲突应被识别')
assertEqual(detailRefreshes, 1, '版本冲突后应刷新审核详情')
assertEqual(listRefreshes, 1, '版本冲突后应刷新审核列表')
assertEqual(pendingCountRefreshes, 1, '版本冲突后应刷新待审数量')

const terminalHandled = await handleSensitiveReviewFailure(
  new RequestError('申请已处理', 409, { errorCode: 'REQUEST_NOT_PENDING' }),
  async () => { detailRefreshes += 1 },
  async () => { listRefreshes += 1 },
  async () => { pendingCountRefreshes += 1 },
)
assertEqual(terminalHandled, 'terminal', '申请终态冲突应被识别')
assertEqual(detailRefreshes, 2, '终态冲突后应刷新详情使审核按钮失效')
assertEqual(listRefreshes, 2, '终态冲突后应刷新列表')
assertEqual(pendingCountRefreshes, 2, '终态冲突后应刷新待审数量')

const refreshBarrier = deferred<void>()
const controlledRefreshes: string[] = []
const controlledTerminalHandling = handleSensitiveReviewFailure(
  new RequestError('申请已处理', 409, { errorCode: 'REQUEST_NOT_PENDING' }),
  async () => { controlledRefreshes.push('detail'); await refreshBarrier.promise },
  async () => { controlledRefreshes.push('list'); await refreshBarrier.promise },
  async () => { controlledRefreshes.push('pending'); await refreshBarrier.promise },
)
await Promise.resolve()
assertEqual(controlledRefreshes.sort().join(','), 'detail,list,pending', '终态冲突必须并发启动三处刷新')
refreshBarrier.resolve()
assertEqual(await controlledTerminalHandling, 'terminal', '三处刷新完成后才结束冲突处理')

const genericHandled = await handleSensitiveReviewFailure(
  new RequestError('普通错误', 500, { errorCode: 'INTERNAL_SERVER_ERROR' }),
  async () => { detailRefreshes += 1 },
  async () => { listRefreshes += 1 },
  async () => { pendingCountRefreshes += 1 },
)
assertEqual(genericHandled, false, '普通错误不得误判为版本冲突')

const pageSource = readFileSync('src/pages/System/EmployeeProfiles/index.tsx', 'utf8')
const editSource = pageSource.slice(pageSource.indexOf('const handleEdit'), pageSource.indexOf('const handleSubmit'))
assert(!editSource.includes('getAdminSensitiveChangeRequests'), '编辑抽屉打开不得依赖待审列表辅助请求')

console.log('employeeProfiles.logic.test: ok')
