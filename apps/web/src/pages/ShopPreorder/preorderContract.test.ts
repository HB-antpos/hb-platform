import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { getActivationProductStores } from '../Warehouse/Preorders/activationProductStores'
import { getActivationStoreChanges, mergeActivationStoreOptions } from '../Warehouse/Preorders/activationStoreSelection'
import { getPreorderDateDisplay } from './preorderDate'
import { resolvePreorderPromptPresentation, resolveShopPreorderNavigation } from './preorderNavigation'
import type { PreorderStoreProductQuantity } from '../../types/preorder'

const read = (path: string) => readFileSync(path, 'utf8')
const readJson = (path: string) => JSON.parse(read(path)) as Record<string, unknown>
const service = read('src/services/preorderService.ts')
const app = read('src/App.tsx')
const routes = read('src/router/routes.tsx')
const layout = read('src/layout/ShopLayout.tsx')
const shopStore = read('src/store/shop.ts')
const drawer = read('src/components/ShopCartDrawer.tsx')
const shopHome = read('src/pages/ShopHome/index.tsx')
const productCard = read('src/pages/ShopHome/components/ProductCard.tsx')
const bestSellers = read('src/pages/ShopHome/components/BestSellersSection.tsx')
const page = read('src/pages/ShopPreorder/index.tsx')
const saveQueue = read('src/pages/ShopPreorder/preorderSaveQueue.ts')
const submitFlow = read('src/pages/ShopPreorder/preorderSubmitFlow.ts')
const viewModel = read('src/pages/ShopPreorder/preorderViewModel.ts')
const observability = read('src/pages/ShopPreorder/preorderObservability.ts')
const styles = read('src/pages/ShopPreorder/styles.css')
const activationDetail = read('src/pages/Warehouse/Preorders/ActivationDetail.tsx')
const adminPage = read('src/pages/Warehouse/Preorders/index.tsx')
const zhLocale = readJson('src/i18n/locales/zh.json')
const enLocale = readJson('src/i18n/locales/en.json')

assert.equal(getPreorderDateDisplay('2026-08-09'), '2026-08-09')
assert.equal(getPreorderDateDisplay('2026-08-09T23:30:00-10:00'), '2026-08-09', '纯日期展示不得经过时区换算')
assert.equal(getPreorderDateDisplay('2024-02-29'), '2024-02-29')
assert.equal(getPreorderDateDisplay('2026-02-29'), null, '非法闰日不得展示或进入编辑载荷')
assert.equal(getPreorderDateDisplay('2026-99-99'), null, '非法年月日不得展示或进入编辑载荷')
assert.equal(getPreorderDateDisplay(null), null)
assert.equal(getPreorderDateDisplay('not-a-date'), null)
assert.deepEqual(
  resolvePreorderPromptPresentation({
    storeCode: 'STORE-A',
    activationGuids: [],
    loading: true,
    error: false,
    bypassed: false,
    onPreorderPage: false,
  }),
  { mode: 'hidden', key: '' },
  'Preorder 后台检查中不得弹窗或显示常驻检查提示',
)
assert.deepEqual(
  resolvePreorderPromptPresentation({
    storeCode: 'STORE-A',
    activationGuids: ['activation-a'],
    loading: false,
    error: false,
    bypassed: false,
    onPreorderPage: false,
  }),
  { mode: 'pending', key: 'pending:STORE-A:activation-a' },
  '只有确认存在有效 Preorder 后才提示门店用户',
)
assert.deepEqual(
  resolveShopPreorderNavigation({
    storeCode: 'STORE-A',
    activationGuid: null,
    loading: false,
    error: true,
  }),
  { action: 'refresh' },
  '门禁检查失败后点击 Preorder 必须保留手动刷新路径',
)

const quantity = (
  overrides: Partial<PreorderStoreProductQuantity>,
): PreorderStoreProductQuantity => ({
  storeGuid: 'store-guid',
  storeCode: 'Store 1',
  storeName: 'Store 1',
  orderStatus: 'Submitted',
  activationItemGuid: 'item-a',
  productCode: 'product-a',
  packCount: 1,
  orderedQuantity: 10,
  ...overrides,
})

const filteredProductStores = getActivationProductStores([
  quantity({ storeGuid: 'positive-1', orderedQuantity: 10 }),
  quantity({ storeGuid: 'positive-2', orderStatus: 'Cancelled', orderedQuantity: 20 }),
  quantity({ storeGuid: 'zero', orderedQuantity: 0 }),
  quantity({ storeGuid: 'other-product', activationItemGuid: 'item-b', orderedQuantity: 30 }),
], 'item-a')
assert.deepEqual(filteredProductStores.map((item) => item.storeGuid), ['positive-1', 'positive-2'])

const sortedProductStores = getActivationProductStores([
  quantity({ storeGuid: 'guid-z', storeCode: 'Store 10', storeName: 'Gamma' }),
  quantity({ storeGuid: 'guid-c', storeCode: 'store 2', storeName: 'beta' }),
  quantity({ storeGuid: 'guid-b', storeCode: 'Store 2', storeName: 'Alpha' }),
  quantity({ storeGuid: 'guid-a', storeCode: 'STORE 2', storeName: 'alpha' }),
], 'item-a')
assert.deepEqual(sortedProductStores.map((item) => item.storeGuid), ['guid-a', 'guid-b', 'guid-c', 'guid-z'])
assert.deepEqual(getActivationProductStores([], 'item-a'), [])

const mergedStoreOptions = mergeActivationStoreOptions([
  { storeGuid: 'active-b', storeCode: 'Store 2', storeName: 'Beta' },
  { storeGuid: 'current-active', storeCode: 'Store 1', storeName: 'Alpha' },
], [
  { storeGuid: 'current-inactive', storeCode: 'Store 3', storeName: 'Gamma' },
  { storeGuid: 'current-active', storeCode: 'Store 1', storeName: 'Old Alpha' },
])
assert.deepEqual(mergedStoreOptions.map((store) => [store.storeGuid, store.isActive]), [
  ['current-active', true],
  ['active-b', true],
  ['current-inactive', false],
])
assert.deepEqual(
  getActivationStoreChanges(['current-active', 'current-inactive'], ['current-active', 'active-b']),
  { addedCount: 1, removedCount: 1 },
)
const caseInsensitiveStoreOptions = mergeActivationStoreOptions([
  { storeGuid: ' GUID-A ', storeCode: 'Store 1', storeName: 'Current Active Name' },
  { storeGuid: 'guid-b', storeCode: 'Store 2', storeName: 'Beta' },
  { storeGuid: 'GUID-B', storeCode: 'Store 2', storeName: 'Duplicate Beta' },
], [
  { storeGuid: 'guid-a', storeCode: 'Store 1', storeName: 'Current Stored Name' },
])
assert.deepEqual(caseInsensitiveStoreOptions.map((store) => store.storeGuid), ['guid-a', 'guid-b'])
assert.equal(caseInsensitiveStoreOptions.find((store) => store.storeGuid === 'guid-a')?.isActive, true)
assert.deepEqual(
  getActivationStoreChanges([' GUID-A ', 'guid-b'], ['guid-a', ' GUID-B ', 'guid-b']),
  { addedCount: 0, removedCount: 0 },
)

function getLocaleValue(locale: Record<string, unknown>, key: string) {
  return key.split('.').reduce<unknown>((value, segment) => {
    if (!value || typeof value !== 'object') return undefined
    return (value as Record<string, unknown>)[segment]
  }, locale)
}

assert(service.includes("const ADMIN_BASE = '/api/react/v1/preorders/admin'"))
assert(service.includes("const SHOP_BASE = '/api/react/v1/preorders'"))
assert(service.includes('return normalizePreorderActiveResult(result)'))
assert(service.includes('export function isPreorderDraftConflictError'))
assert(service.includes('export function isPreorderActivationStoresChangedError'))
assert(service.includes('PREORDER_ACTIVATION_STORES_CHANGED'))
assert(service.includes('`${ADMIN_BASE}/activations/${activationGuid}/stores`'))
assert(service.includes('export function isPreorderActivationArrivalDateChangedError'))
assert(service.includes('PREORDER_ACTIVATION_ARRIVAL_DATE_CHANGED'))
assert(service.includes('`${ADMIN_BASE}/activations/${activationGuid}/estimated-arrival-date`'))
assert(service.includes("'X-Preorder-Submission-Id': submissionId"), 'submissionId 必须通过统一可选 header 发送')
assert(service.includes('activePreorderSingleFlight.run(storeCode'), '所有 active gate 触发源必须按门店复用同一请求')
assert(service.includes('export function advanceActivePreorderFreshEpoch(storeCode: string)'))
assert(page.includes('activationItemGuid: item.activationItemGuid'))
assert(app.includes('path="preorders/:activationGuid"'))
assert(app.includes('createBrowserRouter'))
assert(app.includes('<RouterProvider router={router} />'))
assert(app.includes('<ShopPreorderLeaveProvider>'))
assert(app.includes('<RouteLoadBoundary resetKey="shop-layout"><ShopLayout /></RouteLoadBoundary>'))
assert(routes.includes("path: '/warehouse/preorders'"))
assert(routes.includes("path: '/warehouse/preorders/activations/:activationGuid'"))
assert(!layout.includes('preorderRequestVersionRef'), 'ShopLayout 不得再使用私有 gate request version')
assert(layout.includes('const requestToken = beginPreorderGateRequest()'))
assert(layout.includes('isPreorderGateRequestCurrent(requestToken)'))
assert(shopStore.includes('preorderGateRequestVersion: number'))
assert(shopStore.includes('beginPreorderGateRequest: () => number'))
assert(shopStore.includes('isPreorderGateRequestCurrent: (token: number) => boolean'))
assert(shopStore.includes('preorderGateRequestVersion: state.preorderGateRequestVersion + 1'), '切店和 reset 必须使共享 token 失效')
assert(shopStore.includes('// 切店立即清除上一家分店的阻塞和错误'))
assert(shopStore.includes('preorderBlocked: false'), '切店、检查异常和 reset 均不得保留普通订单阻塞态')
assert(layout.includes('const handleStoreChange = async'))
assert(layout.includes('await changeStoreAfterDurableLeave(value, requestPreorderDurableLeave'))
const logoutBody = layout.slice(layout.indexOf('const handleLogout = async'), layout.indexOf('const handleSearch'))
assert(logoutBody.includes('await runAfterDurableLeave(requestPreorderDurableLeave'))
assert(logoutBody.indexOf('await runAfterDurableLeave') < logoutBody.indexOf('await logout()'))
assert.equal((layout.match(/onChange=\{\(value\) => void handleStoreChange\(value\)\}/g) ?? []).length, 2)
assert(layout.includes("window.addEventListener('focus', refreshFocusedCart)"))
assert(layout.includes('void refreshPreorderGate()'))
const shopHomeNavIndex = layout.indexOf("t('shop.shopHome', 'Shop Home')", layout.indexOf('shop-orange-menu'))
const preorderNavIndex = layout.indexOf("t('shop.preorder.navigation', 'Preorder')", shopHomeNavIndex)
const bestSellersNavIndex = layout.indexOf("t('shop.bestSellers', 'Best Sellers')", shopHomeNavIndex)
assert(shopHomeNavIndex >= 0 && preorderNavIndex > shopHomeNavIndex && preorderNavIndex < bestSellersNavIndex)
assert(layout.includes('const handleOpenPreorder = useCallback'))
assert(layout.includes('onClick={handleOpenPreorder}'))
assert(layout.includes("location.pathname.startsWith('/shop/preorders/')"))
assert(layout.includes('resolvePreorderPromptPresentation({'))
assert(layout.includes('open={preorderPromptOpen}'))
assert(layout.includes("const preorderPromptOpen = preorderPrompt.mode === 'pending'"), '检查中和检查失败不得用模态框阻塞页面')
assert(layout.includes('const showPreorderGateAlert = Boolean('))
assert(layout.includes('!preorderGateLoading'))
assert(layout.includes('!preorderGateError'))
assert(layout.includes('preorderActivations.length > 0'))
assert(!layout.includes("message.info(t('shop.preorder.gateChecking'))"), '后台检查中不得弹出提示 toast')
assert(layout.includes('resolveEffectivePreorderGateBlocked(\n    preorderBlocked,'), 'loading/error 不得并入普通订单阻塞态')
assert(layout.includes('preorderBlocked: false, preorderGateLoading: true'), '检查开始必须先清除旧分店阻塞态')
assert(layout.includes('preorderBlocked: false, preorderGateLoading: false, preorderGateError: true'), '检查失败必须 fail-open')
assert(layout.includes('const PREORDER_GATE_TIMEOUT_MS = 8_000'), 'Preorder 门禁检查必须有明确超时')
assert(layout.includes('const controller = new AbortController()'))
assert(layout.includes('getActivePreorders(storeCode, controller.signal)'))
assert(layout.includes('window.setTimeout(() => controller.abort(), PREORDER_GATE_TIMEOUT_MS)'))
assert(layout.includes("t('shop.preorder.enterPreorder')"))
assert(
  layout.includes('bypassed: preorderGateBypassed'),
  'WarehouseStaff / 仓库订单管理员绕过门禁后不应再出现强制 Preorder 弹窗',
)
assert(drawer.includes('if (!cart?.storeCode || preorderBlocked)'))
assert(drawer.includes('removeStoreOrderCartItem'))
assert(drawer.includes('clearActiveStoreOrderCart'))
// Preorder 只拦截最终提交，普通扫码、加购和数量修改必须保持可用。
const cartUpdateBody = drawer.slice(drawer.indexOf('const handleUpdateQuantity'), drawer.indexOf('const handleSubmitOrder'))
const cartSubmitBody = drawer.slice(drawer.indexOf('const handleSubmitOrder'), drawer.indexOf('const handleClearCart'))
assert(!cartUpdateBody.includes('preorderBlocked'))
assert(cartSubmitBody.includes('preorderBlocked'))
assert(drawer.includes('disabled={!canSubmitCart || preorderBlocked || cartMutating}'))
assert(!drawer.includes('preorderGateError'), '购物车不应显示门禁检查失败提醒')
assert(!drawer.includes('onRetryPreorderGate'), '购物车不应保留门禁检查失败重试入口')
assert(!layout.includes("t('shop.preorder.gateUnavailable')"), '页面顶部不应显示门禁检查失败提醒')
assert(!layout.includes('preorderGateError={'), '购物车不应再接收门禁检查失败状态')
for (const key of ['shop.preorder.gateUnavailable', 'shop.preorder.gateErrorDescription']) {
  assert.equal(getLocaleValue(zhLocale, key), undefined, `中文不应保留已移除的提醒文案 ${key}`)
  assert.equal(getLocaleValue(enLocale, key), undefined, `英文不应保留已移除的提醒文案 ${key}`)
}
assert(!shopHome.includes('preorderBlocked'))
assert(!productCard.includes('orderLocked'))
assert(!bestSellers.includes('preorderBlocked'))
assert(layout.includes("t('shop.preorder.gateBlockedDescription')"))
assert(drawer.includes("message.warning(t('shop.preorder.submitRequiredWarning'"))
assert(drawer.indexOf("message.warning(t('shop.preorder.submitRequiredWarning'") < drawer.indexOf('await onPreorderRequired?.()'))
assert(page.includes('contextGenerationRef.current += 1'))
assert(page.includes('const isEditable = canEditPreorderQuantities({'))
assert(page.includes('submitting,') && page.includes('resolvingConflict: draftConflictResolving'))
const updatePackCountBody = page.slice(page.indexOf('const updatePackCount'), page.indexOf('const refreshGateAndContinue'))
assert(updatePackCountBody.includes('if (!isEditable ||'), '数量 handler 必须在提交/冲突期间 fail-closed')
assert(updatePackCountBody.includes('submittingContextRef.current'), 'handler 必须同步检查提交上下文 ref')
assert(page.includes('activeContextRef.current = context'))
assert(page.includes('const controller = new AbortController()'))
assert(page.includes('useBlocker('), '站内导航必须在 effect cleanup 前阻塞并保存')
assert(page.includes('preparePreorderNavigation({'))
assert(page.includes('blocker.proceed()'))
assert(page.includes('blocker.reset()'))
assert(page.includes('markPreorderContextDiscarded(discardedContextKeysRef.current, context.key)'))
assert(page.includes('consumePreorderContextPersistence(discardedContextKeysRef.current, context.key)'))
assert(page.includes('clearCurrentOwnerPendingDrafts(context, queue)'))
assert(page.includes('controller.abort()'))
assert(page.includes('setDetail(null)') && page.includes('setItems([])'))
assert(page.includes('isSamePreorderRequestContext(activeContextRef.current, context)'))
assert(page.includes('saveShopPreorderDraft(requestedContext.activationGuid'))
assert(page.includes('isPreorderDraftConflictError(error)'))
assert(page.includes('resolveOnlinePreorderDraftConflict(refreshed, newestLocalSnapshot, choice)'))
assert(page.includes('if (terminalResolution.forcedServer)'))
assert(page.includes("t('shop.preorder.draftAlreadyResponded')"))
const terminalConflictBody = page.slice(
  page.indexOf('if (terminalResolution.forcedServer)'),
  page.indexOf('const choice = await new Promise', page.indexOf('if (terminalResolution.forcedServer)')),
)
assert(terminalConflictBody.includes('clearPendingPreorderDraft(requestedContext, queue.ownerId, conflictWriteId)'))
assert(!terminalConflictBody.includes('readPendingPreorderDrafts'), '终态竞态不能清除其他 tab owner 的 journal')
assert(page.includes('queue.draftRevision = refreshed.draftRevision'))
assert(submitFlow.includes('const reconciliation = resolvePreorderSubmitReconciliation('))
assert(submitFlow.includes('detail.orderStatus, isConflict(error)'))
assert(page.includes("t('shop.preorder.onlineDraftConflictDescription')"))
assert(page.includes("t('shop.preorder.draftConflictSubmitCancelled')"))
const initialConflictCancelBody = page.slice(
  page.indexOf('onCancel: () => {', page.indexOf("t('shop.preorder.draftConflictTitle')")),
  page.indexOf('},', page.indexOf('onCancel: () => {', page.indexOf("t('shop.preorder.draftConflictTitle')"))) + 2,
)
assert(initialConflictCancelBody.includes('clearPendingPreorderDraft(context, selectedCandidate.ownerId, selectedCandidate.writeId)'))
assert(initialConflictCancelBody.includes('selectedCandidate.ownerId === queue.ownerId'))
assert(!initialConflictCancelBody.includes('readPendingPreorderDrafts'), '采用服务器草稿时不能清除其他 tab owner 的 journal')
assert(page.includes('storeCode: requestedContext.storeCode'))
assert(page.includes('const queue = saveQueueRef.current'))
assert(page.includes('queue.pending = snapshot'))
assert(page.includes('flushDetachedSaveQueue(queue, snapshot)'))
assert(page.includes('storeCode: queue.context.storeCode'))
assert(page.includes('writePendingPreorderDraft(queue.context, snapshot, {'))
assert(page.includes('readPendingPreorderDrafts(context)'))
assert(page.includes('replacePendingPreorderDraftWriteForOwner(queue, writeId)'))
assert(page.includes('clearPendingPreorderDraftForOwner(queue.context, queue)'))
assert(page.includes('resolvePendingPreorderDraftRecovery'))
assert(page.includes("t('shop.preorder.draftConflictTitle')"))
assert(page.includes("t('shop.preorder.recoverLocalDraft')"))
assert(page.includes("t('shop.preorder.useServerDraft')"))
assert(page.includes("t('shop.preorder.draftConflictDescription', { count: recovery.candidateCount })"))
assert(page.includes('createDebouncedTask(() => void saveDraft(snapshot, context), 500)'))
assert(page.includes('submitShopPreorder(context.activationGuid'))
assert(page.includes('storeCode: context.storeCode'))
const submitBody = page.slice(page.indexOf('const submit = async'), page.indexOf('const submitWithConfirm'))
assert(submitBody.includes('isConflict: isPreorderDraftConflictError'))
assert(submitBody.includes('runPreorderSubmit({'))
assert(submitBody.includes('await saveDraft(resolution.items, context)'))
assert.equal((submitBody.match(/getShopPreorderActivation\(/g) ?? []).length, 1, 'submit 冲突流程只能声明一次 detail GET')
assert(submitBody.includes('queue.draftRevision = refreshed.draftRevision'))
assert(submitBody.indexOf('queue.draftRevision = refreshed.draftRevision') < submitBody.indexOf('await saveDraft(resolution.items, context)'), '已知冲突不得用 stale revision PUT')
assert(!submitBody.includes('readPendingPreorderDrafts(context).forEach'), '提交成功不能清除其他 tab owner journal')
const submitConfirmBody = page.slice(page.indexOf('const submitWithConfirm'), page.indexOf('const abandonWithConfirm'))
assert(submitConfirmBody.includes('submittingContextRef.current = context'), '打开提交确认时必须立即锁定份数编辑')
assert(submitConfirmBody.includes('setSubmitting(true)'))
assert(submitConfirmBody.includes('onCancel: () => {'), '取消确认后必须释放编辑锁')
assert(submitConfirmBody.includes('const submissionId = createPreorderSubmissionId()'))
assert(submitConfirmBody.includes('submit(false, context, snapshot, submissionId)'), '主按钮必须复用确认时生成的 submissionId')
const abandonConfirmBody = page.slice(page.indexOf('const abandonWithConfirm'), page.indexOf('const selectedCount'))
assert(abandonConfirmBody.includes("t('shop.preorder.noDemandConfirmationPhrase')"))
assert(abandonConfirmBody.includes('createPreorderNoDemandSnapshot(items)'), '放弃必须构造全零 snapshot')
assert(abandonConfirmBody.includes('disabled: !isNoDemandConfirmationMatch(noDemandConfirmationText, confirmationPhrase)'), '确认短语匹配前必须保持禁用')
assert(abandonConfirmBody.includes('<Input'), '放弃确认框必须提供短语输入框')
assert(abandonConfirmBody.includes('const submissionId = createPreorderSubmissionId()'))
assert(abandonConfirmBody.includes('submit(true, context, snapshot, submissionId)'), '放弃必须复用确认时生成的 submissionId')
assert(page.includes('if (!isSamePreorderRequestContext(activeContextRef.current, context)) return'))
assert(page.includes('isSamePreorderRequestContext(submittingContextRef.current, requestedContext)'))
assert(page.includes('await awaitSaveDrain(queue)'), '普通 autosave/durable leave 必须继续等待完整 drain')
assert(submitBody.includes('await freezeSaveQueueForSubmission(queue)'), '提交只能等待当前单次 PUT 并丢弃 pending')
assert(!page.includes('waitForSaveQueueToDrain'), '保存等待不得继续使用 25ms polling')
assert(page.includes('currentRequestPromise: Promise<PreorderSaveRequestResult<PreorderActivationDetail>> | null'))
assert(saveQueue.includes('drainPromise: Promise<boolean> | null'))
assert(saveQueue.includes("export type PreorderSaveRequestResult<TDetail> ="))
assert(saveQueue.includes('state.pending = null') && saveQueue.includes('state.stopAfterCurrentRequest = Boolean(state.currentRequestPromise)'))
assert(submitFlow.includes('initialConflictDetail?: PreorderActivationDetail'))
assert(submitFlow.includes('if (initialConflictDetail)'))
assert(submitFlow.includes('await coordinateConflict(initialConflictDetail)'))
assert(submitFlow.includes('await onTerminal?.(initialConflictDetail)'))
assert(submitBody.includes("currentSaveResult.status === 'conflict' ? currentSaveResult.detail : undefined"))
assert(submitBody.includes("if (currentSaveResult.status === 'failed')"), '非冲突 PUT 失败必须继续 fail-closed')
assert(page.includes('nextSnapshot = takeNextPendingSave(queue)'), '每次 PUT 后必须由冻结感知 helper 决定是否继续 drain')
assert(submitBody.includes('debouncedSaveRef.current?.cancel()'), '提交前必须取消尚未启动的 debounce')
const confirmLogIndex = submitBody.indexOf("observability.record('confirm'")
const waitSaveStartLogIndex = submitBody.indexOf("observability.record('wait-save-start'")
const waitSaveAwaitIndex = submitBody.indexOf('await freezeSaveQueueForSubmission(queue)')
const waitSaveEndLogIndex = submitBody.indexOf("observability.record('wait-save-end'")
const postStartLogIndex = submitBody.indexOf("observability.record('post-start'")
const postRequestIndex = submitBody.indexOf('await submitShopPreorder(')
const postEndLogIndex = submitBody.indexOf("observability.record('post-end'")
assert(confirmLogIndex >= 0 && confirmLogIndex < waitSaveStartLogIndex)
assert(waitSaveStartLogIndex < waitSaveAwaitIndex && waitSaveAwaitIndex < waitSaveEndLogIndex)
assert(postStartLogIndex < postRequestIndex && postRequestIndex < postEndLogIndex)
assert(submitBody.includes("observability.record('success-feedback'"))
assert(page.includes("observability.record('background-active-refresh-finish'"))
assert(submitBody.includes('...measurePreorderSubmitPayload(createPayload())'))
assert(observability.includes("console.info('[shop-preorder-submit]', payload)"))
assert(!observability.includes('sendCenterLog'))
assert(!observability.includes('fetch('), 'telemetry 不得产生额外 HTTP 请求')
assert(observability.includes("'draftPut' | 'submitPost' | 'detailGet' | 'activeGet'"))
assert(observability.includes('requestCounts: { ...requestCounts }'))
assert(submitBody.includes("observability.incrementRequest('submitPost')"))
assert(submitBody.includes("observability.incrementRequest('detailGet')"))
assert(page.includes("observability.incrementRequest('activeGet')"))
assert(page.includes("queue.submissionObservability?.incrementRequest('draftPut')"))
assert(page.includes("queue.submissionObservability?.incrementRequest('detailGet')"))
const emittedLogBody = observability.slice(observability.indexOf('emit({'), observability.indexOf('})', observability.indexOf('emit({')))
assert(!emittedLogBody.includes('items') && !emittedLogBody.toLowerCase().includes('token'), '结构化日志白名单不得包含商品明细或 token')
assert(page.includes("message.warning(t('shop.preorder.submitRefreshFailed'))"))
assert(page.includes('className="shop-preorder-actions"'))
assert(page.indexOf('onClick={abandonWithConfirm}') < page.indexOf('onClick={submitWithConfirm}'), '桌面端危险按钮应位于主按钮左侧')
assert(page.includes('disabled={!canStartPreorderSubmission(isEditable, selectedCount)}'), '零选中时主提交按钮必须禁用')
assert(page.includes("type PreorderSubmittingAction = 'submit' | 'abandon' | null"))
assert(submitConfirmBody.includes("setSubmittingAction('submit')"))
assert(abandonConfirmBody.includes("setSubmittingAction('abandon')"))
assert(page.includes("loading={submitting && submittingAction === 'abandon'}"), '放弃时只能让危险按钮显示 loading')
assert(page.includes("loading={submitting && submittingAction === 'submit'}"), '有需求提交时只能让主按钮显示 loading')
assert(page.includes('setSubmittingAction(null)'), '取消、提交完成或上下文重置时必须清理 action')
assert(!page.includes("disabled={!isEditable || saveState === 'saving'}"), 'autosave in-flight 时放弃按钮必须仍可进入等待路径')
assert(!page.includes("disabled={!isEditable || saveState === 'saving' || selectedCount === 0}"), 'autosave in-flight 时提交按钮必须仍可进入等待路径')
assert(page.includes('disabled={!canStartPreorderSubmission(isEditable, 1)}'))
assert(page.includes('disabled={!canStartPreorderSubmission(isEditable, selectedCount)}'))
assert(styles.includes('.shop-preorder-summary > .shop-preorder-actions { display: flex; min-width: 0;'), '桌面操作区必须覆盖 summary 直系 div 的 grid 样式')
assert(styles.includes('.shop-preorder-summary > .shop-preorder-actions { grid-column: 1 / -1; display: grid;'), '移动端必须以同等 specificity 恢复双列 grid')
assert(page.includes('packCount * item.minimumOrderQuantity'))
assert(page.includes("renderMode === 'desktop' ?"), '断点只能渲染桌面 table 或移动 cards 其中一棵树')
assert(page.includes('() => summarizePreorderItems(items)'))
assert(viewModel.includes('items.reduce<PreorderItemsSummary>'), '三个汇总值必须由一次 reduce 产生')
assert(page.includes('beginPostSubmitGateRefresh({'))
const terminalContinuationBody = page.slice(page.indexOf('const continueAfterTerminalSubmit'), page.indexOf('const submit = async'))
assert(terminalContinuationBody.includes('advanceActivePreorderFreshEpoch(context.storeCode)'))
assert(
  terminalContinuationBody.indexOf('advanceActivePreorderFreshEpoch(context.storeCode)') < terminalContinuationBody.indexOf('beginPostSubmitGateRefresh({'),
  '所有已确认终态路径必须先推进 fresh epoch，再启动 background active refresh',
)
assert(page.includes('knownActivations: useShopStore.getState().preorderActivations'))
assert(page.includes("getCurrentStoreCode: () => useShopStore.getState().selectedStore?.storeCode ?? null"))
assert(page.includes('loadGate: (signal) => getActivePreorders(context.storeCode, signal, () => {'))
assert(page.includes('claimRequestToken: useShopStore.getState().beginPreorderGateRequest'))
assert(page.includes('isRequestCurrent: useShopStore.getState().isPreorderGateRequestCurrent'))
assert(!page.includes('await refreshGateAndContinue(context)'), '终态后不得等待 gate 刷新才结束提交')
assert(styles.includes('@media (max-width: 900px)'))
assert(styles.includes('.shop-preorder-page { padding: 12px 12px 190px; }'))
const compactSummaryRules = styles.slice(styles.indexOf('@media (max-width: 900px)'), styles.indexOf('@media (max-width: 720px)'))
assert(compactSummaryRules.includes('.shop-preorder-summary { display: grid; grid-template-columns: repeat(3, 1fr);'))
assert(compactSummaryRules.includes('.shop-preorder-summary > .shop-preorder-actions { grid-column: 1 / -1; display: grid;'))
const cardRules = styles.slice(styles.indexOf('@media (max-width: 720px)'))
assert(cardRules.includes('.shop-preorder-table { display: none; }'))
assert(!cardRules.includes('.shop-preorder-summary {'), '<=720 只保留 card 切换，不得重复覆盖 summary 布局')
assert(page.includes('getPreorderActivationReadOnlyReason(detail'))
assert(page.includes("t(`shop.preorder.readOnlyReason.${activationReadOnlyReason}`)"))
assert(service.includes('storeProductQuantities: statistics.storeProductQuantities ?? []'))
assert(activationDetail.includes('statistics?.storeProductQuantities ?? []'))
assert(activationDetail.includes('productImage: snapshot?.productImage'))
const productSummaryTable = activationDetail.slice(
  activationDetail.indexOf("{ key: 'products'"),
  activationDetail.indexOf("{ key: 'orders'"),
)
assert(productSummaryTable.includes("dataIndex: 'totalImportAmount'"), '商品汇总必须保留进口金额')
assert(!productSummaryTable.includes("dataIndex: 'totalRetailAmount'"), '商品汇总不应显示零售金额')
assert(productSummaryTable.includes('<Button type="link"'), '订货分店数量必须使用可聚焦链接按钮')
assert(productSummaryTable.includes('setSelectedProduct(row)'), '订货分店链接必须打开对应商品明细')
assert(activationDetail.includes('<Modal') && activationDetail.includes('selectedProductStores'))
assert(!service.includes("productImage: item.productImage ?? ''"))
assert(service.includes('getPreorderTemplate(templateGuid: string, signal?: AbortSignal)'))
assert(service.includes('getTemplateActivations(templateGuid: string, signal?: AbortSignal)'))
assert(adminPage.includes('beginModalRequest(editorRequestGuardRef.current)'))
assert(adminPage.includes('invalidateModalRequest(editorRequestGuardRef.current)'))
assert(adminPage.includes('isCurrentModalRequest(editorRequestGuardRef.current, requestToken)'))
assert(adminPage.includes('beginModalRequest(activationRequestGuardRef.current)'))
assert(adminPage.includes('isCurrentModalRequest(activationRequestGuardRef.current, requestToken)'))
assert(adminPage.includes('beginModalRequest(historyRequestGuardRef.current)'))
assert(adminPage.includes('isCurrentModalRequest(historyRequestGuardRef.current, requestToken)'))
// 模板切换或粘贴内容变更后，旧的解析响应不得回写当前弹窗。
assert(service.includes('resolvePreorderItems(rows: PreorderPasteRow[], signal?: AbortSignal)'))
assert(adminPage.includes('const pasteRequestGuardRef = useRef(createModalRequestGuard())'))
assert(adminPage.includes('resolvePreorderItems(parsed.rows, requestToken.signal)'))
assert(adminPage.includes('isCurrentModalRequest(pasteRequestGuardRef.current, requestToken)'))
assert(adminPage.includes('invalidateModalRequest(pasteRequestGuardRef.current)'))
assert(activationDetail.includes('getActivationActionAvailability'))
assert(activationDetail.includes('{canClose ? <Popconfirm'))
assert(
  activationDetail.includes("const activationGuid = useStableRouteContext()?.params.activationGuid ?? ''"),
  '后台批次详情必须从稳定路由上下文读取 activationGuid',
)
assert(!activationDetail.includes('useParams'), '后台批次详情脱离 Route 渲染时不能依赖 useParams')
const activationDetailLoadBody = activationDetail.slice(
  activationDetail.indexOf('const load = useCallback'),
  activationDetail.indexOf('useEffect(() =>', activationDetail.indexOf('const load = useCallback')),
)
const emptyActivationGuardIndex = activationDetailLoadBody.indexOf('if (!activationGuid) {')
const beginActivationRequestIndex = activationDetailLoadBody.indexOf('beginActivationDetailRequest')
assert(emptyActivationGuardIndex >= 0 && emptyActivationGuardIndex < beginActivationRequestIndex, '空批次参数必须在创建请求前失败关闭')
const emptyActivationGuardBody = activationDetailLoadBody.slice(emptyActivationGuardIndex, beginActivationRequestIndex)
assert(emptyActivationGuardBody.includes('invalidateActivationDetailRequest(requestGuardRef.current)'))
assert(emptyActivationGuardBody.includes('setDetail(null)') && emptyActivationGuardBody.includes('setStatistics(null)'))
assert(emptyActivationGuardBody.includes('setExtendOpen(false)') && emptyActivationGuardBody.includes('setNextEndAt(null)'))
assert(emptyActivationGuardBody.includes('setLoading(false)') && emptyActivationGuardBody.includes('return'), '空批次参数不得继续发起请求')
assert(activationDetail.includes("currentDetail.status !== 'Scheduled' && currentDetail.status !== 'Active'"))
assert(activationDetail.includes('getStores({ page, pageSize: 100, isActive: true'))
assert(activationDetail.includes('mergeActivationStoreOptions(activeStores, currentDetail.stores)'))
assert(activationDetail.includes('expectedStoreGuids, storeGuids: nextStoreGuids'))
assert(activationDetail.includes('isPreorderActivationStoresChangedError(error)'))
assert(activationDetail.includes('storeOptionsLoadFailed || storeOptionsLoading'))
assert(activationDetail.includes('invalidateModalRequest(storeEditorRequestGuardRef.current)'))
assert(activationDetail.includes('storeConfirmDestroyRef.current?.()'))
assert(activationDetail.includes('if (storeConfirmSessionRef.current !== null) return'))
assert(activationDetail.includes('storeConfirmSessionRef.current !== confirmSession'))
assert(activationDetail.includes('currentActivationGuidRef.current !== targetActivationGuid'))
assert(activationDetail.includes('onCancel: releaseConfirmation'))
assert(activationDetail.includes('confirmation.update({'))
assert(activationDetail.includes('keyboard: false'))
assert(activationDetail.includes('keyboard={!storeSaving}'))
assert(adminPage.includes("estimatedArrivalDate: values.estimatedArrivalDate?.format('YYYY-MM-DD') ?? null"))
assert(!adminPage.includes('values.estimatedArrivalDate?.toISOString()'), '预计到货日不得转换为 UTC 时间')
assert(adminPage.includes('getPreorderDateDisplay(value) ??'))
assert(activationDetail.includes('const estimatedArrivalDate = getPreorderDateDisplay(detail.estimatedArrivalDate)'))
assert(activationDetail.includes('expectedEstimatedArrivalDate: getPreorderDateDisplay(currentDetail.estimatedArrivalDate)'))
assert(activationDetail.includes('isPreorderActivationArrivalDateChangedError(error)'))
assert(activationDetail.includes('invalidateModalRequest(arrivalEditorRequestGuardRef.current)'))
assert(activationDetail.includes('arrivalSavingRef.current'))
assert(activationDetail.includes('keyboard={!arrivalSaving}'))
assert(layout.includes('getPreorderDateDisplay(item.estimatedArrivalDate)'))
assert(layout.includes('estimatedArrivalDate ?'))
assert(page.includes('getPreorderDateDisplay(detail.estimatedArrivalDate)'))
assert(page.includes('estimatedArrivalDate ?'))

for (const key of [
  'warehouse.preorders.activationDetail.changeStores',
  'warehouse.preorders.activationDetail.storeChangeSummary',
  'warehouse.preorders.activationDetail.storeRemovalWarning',
  'warehouse.preorders.activationDetail.storeOptionsLoadFailed',
  'warehouse.preorders.activationDetail.storesConflictRefreshed',
  'warehouse.preorders.activationDetail.changeArrivalDate',
  'warehouse.preorders.activationDetail.arrivalDateUpdated',
  'warehouse.preorders.activationDetail.arrivalDateConflictRefreshed',
]) {
  assert.equal(typeof getLocaleValue(zhLocale, key), 'string', `缺少中文文案 ${key}`)
  assert.equal(typeof getLocaleValue(enLocale, key), 'string', `缺少英文文案 ${key}`)
}

const requiredShopPreorderKeys = [
  'shop.preorder.pendingTitle',
  'shop.preorder.deadline',
  'shop.preorder.estimatedArrivalDate',
  'shop.preorder.enterPreorder',
  'shop.preorder.submitRequiredWarning',
  'shop.preorder.detailLoadFailed',
  'shop.preorder.submitSuccess',
  'shop.preorder.noDemandSuccess',
  'shop.preorder.confirmSubmitTitle',
  'shop.preorder.confirmNoDemandTitle',
  'shop.preorder.abandonCurrent',
  'shop.preorder.product',
  'shop.preorder.packCount',
  'shop.preorder.totalUnits',
  'shop.preorder.selectStoreFirst',
  'shop.preorder.searchPlaceholder',
  'shop.preorder.submitCurrent',
  'shop.preorder.orderStatus.Draft',
  'shop.preorder.orderStatus.Submitted',
  'shop.preorder.orderStatus.ReturnedForRevision',
  'shop.preorder.orderStatus.NoDemand',
  'shop.preorder.orderStatus.Processing',
  'shop.preorder.orderStatus.Completed',
  'shop.preorder.orderStatus.Cancelled',
]

const requiredWarehousePreorderKeys = [
  'warehouse.preorders.title',
  'warehouse.preorders.createTemplate',
  'warehouse.preorders.estimatedArrivalDate',
  'warehouse.preorders.activationDetail.title',
  'warehouse.preorders.activationStatus.Active',
  'warehouse.preorders.orderStatus.Submitted',
  'warehouse.preorders.orderStatus.ReturnedForRevision',
  'warehouse.preorders.paste.invalidMoq',
]

assert(activationDetail.includes("['Submitted', 'ReturnedForRevision', 'Processing'"), '已提交订单下拉必须提供退回修改')
assert(activationDetail.includes("['NoDemand', 'ReturnedForRevision']"), '无需求订单下拉必须提供退回修改')
assert(activationDetail.includes('row.status, row.draftRevision'), '仓库状态更新必须携带当前状态和 revision 做 CAS')
assert(service.includes('expectedStatus') && service.includes('expectedDraftRevision'), '状态更新 DTO 必须向后端发送 CAS 基线')
const returnOnOkStart = activationDetail.indexOf('onOk: async () => {', activationDetail.indexOf("status === 'ReturnedForRevision'"))
const returnOnOkBody = activationDetail.slice(returnOnOkStart, activationDetail.indexOf('},\n      })', returnOnOkStart))
assert(returnOnOkBody.indexOf('isPreorderReturnContextCurrent') < returnOnOkBody.indexOf('updatePreorderOrderStatus'), '退回请求前必须再次校验当前批次上下文')
assert(activationDetail.includes('returnConfirmDestroyRef.current?.()'), '切换批次或卸载时必须销毁旧退回确认框')
assert(returnOnOkBody.includes('isPreorderStatusTransitionConflictError(error)'))
assert(returnOnOkBody.includes('confirmation.destroy()') && returnOnOkBody.includes('await load()'), 'CAS 冲突后必须关闭旧弹窗并刷新服务器事实')

for (const source of [layout, drawer, page]) {
  for (const match of source.matchAll(/t\('(shop\.preorder\.[^']+)'/g)) {
    requiredShopPreorderKeys.push(match[1])
  }
}

for (const source of [adminPage, activationDetail]) {
  for (const match of source.matchAll(/t\('(warehouse\.preorders\.[^']+)'/g)) {
    requiredWarehousePreorderKeys.push(match[1])
  }
}

for (const key of new Set(requiredShopPreorderKeys)) {
  assert.equal(typeof getLocaleValue(zhLocale, key), 'string', `zh 缺少 ${key}`)
  assert.equal(typeof getLocaleValue(enLocale, key), 'string', `en 缺少 ${key}`)
}

assert.equal(getLocaleValue(zhLocale, 'shop.preorder.noDemandConfirmationPhrase'), '放弃本次预定')
assert.equal(getLocaleValue(enLocale, 'shop.preorder.noDemandConfirmationPhrase'), 'Abandon this Preorder')
assert.equal(getLocaleValue(zhLocale, 'warehouse.preorders.activationDetail.totalQuantity'), '数量')
assert.equal(getLocaleValue(enLocale, 'warehouse.preorders.activationDetail.totalQuantity'), 'Quantity')


for (const key of new Set(requiredWarehousePreorderKeys)) {
  assert.equal(typeof getLocaleValue(zhLocale, key), 'string', `zh 缺少 ${key}`)
  assert.equal(typeof getLocaleValue(enLocale, key), 'string', `en 缺少 ${key}`)
}

assert(adminPage.includes('const { t, i18n } = useTranslation()'))
assert(activationDetail.includes('const { t, i18n } = useTranslation()'))
for (const copy of ['模板名称', '创建模板', '解析并预览', '激活新一期']) {
  assert(!adminPage.includes(`'${copy}'`) && !adminPage.includes(`>${copy}<`), `Preorder 管理页仍硬编码文案：${copy}`)
}
for (const copy of ['批次详情加载失败', '订单状态已更新', '导出 Excel', '延长有效期']) {
  assert(!activationDetail.includes(`'${copy}'`) && !activationDetail.includes(`>${copy}<`), `Preorder 批次页仍硬编码文案：${copy}`)
}

const forbiddenVisibleCopy = [
  'Preorder 详情加载失败',
  '本期已确认无需求',
  '提交失败，请刷新后核对本期状态',
  '确认提交本期 Preorder？',
  '确认本期无需求？',
  '请先选择分店',
  '搜索货号或名称',
  '提交本期 Preorder',
]

for (const copy of forbiddenVisibleCopy) {
  assert(!page.includes(copy), `ShopPreorder 仍硬编码文案：${copy}`)
}

assert(page.includes('const { t, i18n } = useTranslation()'))
assert(page.includes('Intl.DateTimeFormat'))
assert(layout.includes('Intl.DateTimeFormat'))
assert(!layout.includes('new Date(item.endAtUtc).toLocaleString()'))
assert(!layout.includes('有 ${preorderActivations.length} 期 Preorder 待处理'))
assert(!drawer.includes('请先完成所有有效 Preorder 后再提交普通订单'))

console.log('preorderContract tests passed')
