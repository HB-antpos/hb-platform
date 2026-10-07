import { readFileSync } from 'node:fs'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

const pageSource = readFileSync('src/pages/Warehouse/ContainerDetail/index.tsx', 'utf8')
const styleSource = readFileSync('src/pages/Warehouse/ContainerDetail/index.css', 'utf8')
// 重设计后保存状态条的新文案在页面级消息文件里（warehouseUi.containerDetail），原中文语义保持不变。
const pageMessagesZh = JSON.parse(readFileSync('src/pages/Warehouse/ContainerDetail/containerDetailPageMessages.zh.json', 'utf8'))
  .warehouseUi.containerDetail as Record<string, string>
const restoreDraftSource = pageSource.slice(
  pageSource.indexOf('const restorePendingDetailDraft = () => {'),
  pageSource.indexOf('useEffect(() => {', pageSource.indexOf('const restorePendingDetailDraft = () => {')),
)
const resetDetailSource = pageSource.slice(
  pageSource.indexOf('const isContainerChange = detailRowsContainerGuidRef.current !== containerGuid'),
  pageSource.indexOf('setSelectedRowKeys([])', pageSource.indexOf('const isContainerChange = detailRowsContainerGuidRef.current !== containerGuid')),
)

assertEqual(
  pageSource.includes("const currentUserGuid = useAuthStore((state) => state.currentUser?.userGUID ?? '')")
    && pageSource.includes('readContainerDetailDraft(')
    && pageSource.includes('writeContainerDetailDraft(')
    && pageSource.includes('currentUserGuid,')
    && pageSource.includes('containerGuid,'),
  true,
  '页面应按当前用户和货柜恢复与持久化 localStorage 草稿',
)
assertEqual(
  !restoreDraftSource.includes('detailRowsContainerGuidRef.current = containerGuid')
    && restoreDraftSource.includes("detailRowsContainerGuidRef.current = ''")
    && restoreDraftSource.includes('setRows([])')
    && resetDetailSource.includes('if (!isContainerChange) return')
    && resetDetailSource.includes('setRows([])')
    && pageSource.includes('scopeContainerDetailRowsToContainer(rows, detailRowsContainerGuidRef.current, containerGuid)'),
  true,
  '草稿恢复不得伪造行已切换；A 到 B 时应立即隐藏并清空 A 行，B 加载失败也不得继续编辑 A',
)
assertEqual(
  pageSource.includes('}, [active, activeLoadQueryKey, currentUserGuid])'),
  true,
  '重新登录后即使路由货柜未变，也应重新加载服务端行并叠加该用户的草稿',
)
assertEqual(
  pageSource.includes('settleContainerDetailDraftSaveSuccess(')
    && pageSource.includes('markContainerDetailDraftSaveFailure(')
    && pageSource.includes("'服务器保存失败，未保存字段已保留在本地草稿'")
    && !pageSource.slice(
      pageSource.indexOf('} catch (error) {', pageSource.indexOf('const executePendingDetailSavePlan')),
      pageSource.indexOf('} finally {', pageSource.indexOf('const executePendingDetailSavePlan')),
    ).includes('reloadCurrentDetailRef.current'),
  true,
  '200 应按字段结算，500 应保留全部草稿且不触发覆盖性重载',
)
assertEqual(
  pageSource.includes('getContainerDetailDraftFieldFailure(')
    && pageSource.includes("status={saveFailure || concurrencyConflict ? 'error' : undefined}")
    && pageSource.includes('aria-invalid={Boolean(saveFailure || concurrencyConflict)}')
    && pageSource.includes('title={concurrencyConflict?.message ?? saveFailure?.message}'),
  true,
  '价格输入框应显示字段级失败状态和可访问提示',
)
assertEqual(
  pageSource.includes('locateFirstPendingDetailField')
    && pageSource.includes('clearPendingDetailDraft')
    && pageSource.includes('pendingDetailFieldCount')
    && pageSource.includes('项未保存')
    && pageSource.includes("t('warehouseUi.containerDetail.draftPending', { count: manualDraftSummary.total })")
    && pageSource.includes("onClick={() => locateFirstPendingDetailField()}")
    && pageSource.includes("onClick={clearPendingDetailDraft}")
    && pageMessagesZh.locateDraftTitle === '定位未保存项'
    && pageMessagesZh.discardDraft === '丢弃草稿'
    && pageSource.includes('清空草稿'),
  true,
  '保存状态条应提供待保存字段数、定位和显式丢弃草稿入口（确认弹窗仍是原「清空草稿」）',
)
assertEqual(
  styleSource.includes('.container-detail-draft-meta')
    && styleSource.includes('white-space: nowrap'),
  true,
  '草稿操作应保持高密度单行布局',
)
assertEqual(
  pageSource.includes("t('warehouseUi.containerDetail.draftMemoryOnly')")
    && pageSource.includes('{isDetailDraftMemoryOnly ? (')
    && pageMessagesZh.draftMemoryOnly === '仅当前页面内存保存，刷新或关闭页面会丢失'
    && pageSource.includes('clearContainerDetailDraftFieldsIfVersionMatches')
    && pageSource.includes("window.addEventListener('storage', onStorage)"),
  true,
  '持久化失败必须持续提示刷新风险，并按版本清理和同步跨标签页草稿',
)
assertEqual(
  pageSource.includes('if (hasDetailDraftFilter)')
    && pageSource.includes('setSelectedTagFilters([])')
    && pageSource.includes('setColumnFilters({})')
    && pageSource.includes('createContainerDetailDraftLocateResetPlan')
    && pageSource.includes('shouldConsumePendingContainerDetailLocate')
    && pageSource.includes('const locateFirstPendingDetailField = (scanFromFirstPage = true) =>')
    && pageSource.includes("&& detailLoadMode === 'paged'")
    && pageSource.includes('&& scanFromFirstPage')
    && pageSource.includes('&& detailPageNumber !== 1')
    && pageSource.includes('pageNumber: 1')
    && pageSource.includes('void loadNextDetailChunk()')
    && pageSource.includes('void locateFirstPendingDetailField(false)')
    && pageSource.includes('未找到未保存项，可能已被删除或保存'),
  true,
  '定位草稿应先清除筛选，再从第 1 页逐页查找，耗尽数据后才提示可能已删除',
)
assertEqual(
  pageSource.includes('降级提示是 sticky')
    && pageSource.includes('isDetailDraftMemoryOnlyRef.current = true')
    && pageSource.includes('if (isDetailDraftMemoryOnlyRef.current) return'),
  true,
  '任一字段持久化失败后必须持续告警，并忽略会覆盖未落盘内存草稿的跨标签页事件',
)
assertEqual(
  pageSource.includes('getContainerDetailEditingPresence')
    && pageSource.includes('heartbeatContainerDetailEditingPresence')
    && pageSource.includes('leaveContainerDetailEditingPresence')
    && pageSource.includes("const editingPresenceState: 'viewing' | 'editing' = hasPendingConcurrencyConflicts || pendingDetailFieldCount > 0 || autoSaveSnapshot.unsavedFieldCount > 0 || isContainerDetailFieldFocused ? 'editing' : 'viewing'")
    && pageSource.includes('onFocusCapture={() => setIsContainerDetailFieldFocused(true)}')
    && pageSource.includes('onBlurCapture={(event) => {')
    && pageSource.includes("if (!event.currentTarget.contains(event.relatedTarget as Node | null))")
    && pageSource.includes('formatContainerDetailPresenceUser')
    && pageSource.includes('getContainerDetailPresenceTitle')
    && styleSource.includes('.container-detail-editing-presence'),
  true,
  '页面应以不阻断编辑的短租约心跳显示其他查看者和编辑者',
)
assertEqual(
  pageSource.includes('currentServerFieldToken')
    && pageSource.includes('采用服务器值')
    && pageSource.includes('保留我的值')
    && pageSource.includes('批量采用服务器值')
    && pageSource.includes('批量保留我的值')
    && pageSource.includes('acceptAllServerConflicts')
    && pageSource.includes('keepAllMineConflicts')
    && pageSource.includes('newerFieldVersionKeys')
    && pageSource.includes('并发冲突')
    && pageSource.includes('<Drawer'),
  true,
  '字段冲突应在紧凑工具栏显示数量，并通过抽屉提供采用服务器或明确覆盖操作',
)
assertEqual(
  pageSource.includes('getContainerDetailConflictServerValue(serverRow, field)')
    && pageSource.includes('currentTokens.get(key) === pendingDetailFieldBaselineTokensRef.current[key]')
    && pageSource.includes('renderConcurrentEditableField(row, \'单件装箱数\'')
    && pageSource.includes('renderConcurrentEditableField(row, \'单件体积\'')
    && pageSource.includes('renderConcurrentEditableField(row, \'调整浮率\'')
    && pageSource.includes('renderConcurrentEditableField(row, \'中包数\'')
    && pageSource.includes('renderConcurrentEditableField(row, \'商品名称\'')
    && pageSource.includes('renderConcurrentEditableField(row, \'备注\'')
    && styleSource.includes('.container-detail-concurrent-field-badge'),
  true,
  '恢复草稿应立即标记令牌变化，且所有自动保存编辑列都必须显示服务器更新红框与徽标',
)
assertEqual(
  pageSource.includes('handleExpiredContainerDetailActionPreview')
    && pageSource.includes("'apply-float-rate', scope, parameters")
    && pageSource.includes("'apply-prices', scope, prices")
    && pageSource.includes("'recalculate-costs', scope, {}")
    && pageSource.includes("'delete-details', scope, {}")
    && pageSource.includes('绝不在用户未再次确认时重放写请求'),
  true,
  '批量预览 409 只能刷新摘要并等待再次确认，不能自动重放写入',
)
assertEqual(
  pageSource.includes('buildContainerDetailOverrideAcknowledgements(')
    && pageSource.includes('clearContainerDetailOverrideAcknowledgements(')
    && pageSource.includes('fieldVersion,')
    && pageSource.includes('首次编辑的 expected token 绝不能前移')
    && pageSource.includes('delete pendingDetailOverrideAcknowledgementsRef.current[`${update.hguid}:${field}`]')
    && pageSource.includes('pendingDetailOverrideAcknowledgementsRef.current = {}'),
  true,
  '覆盖确认必须绑定草稿版本；再次编辑、保存结算、采用服务器值和用户或货柜切换均不得复用旧 ack',
)
assertEqual(
  pageSource.includes('旧草稿缺少服务器基线')
    && pageSource.includes('Object.values(pendingDetailPatchesRef.current).flatMap')
    && pageSource.includes("'set-status', scope, parameters")
    && pageSource.includes("'assign-category', scope, parameters")
    && pageSource.includes('queuePendingDetailUpdates(writableUpdates)')
    && pageSource.includes('queuePendingDetailUpdates([{ hguid: rowCategoryEditingRow.hguid, ProductCategoryGUID: rowTargetCategoryGuid }])'),
  true,
  '旧版草稿与匹配/分类/上下架动作都必须经过字段令牌或受保护的 scoped preview 链路',
)
assertEqual(
  pageSource.includes('Object.values(pendingDetailPatchesRef.current)')
    && pageSource.includes('Promise<PendingContainerDetailSaveExecutionResult>')
    && pageSource.includes('filterSuccessfullySavedContainerDetailUpdates(')
    && pageSource.includes('saveResult.successfulFieldKeys')
    && pageSource.includes('saveResult.successfulFieldKeys.includes(`${rowCategoryEditingRow.hguid}:ProductCategoryGUID`)'),
  true,
  '同步进入草稿 ref 的匹配和分类操作必须立即构造计划，并只在目标字段实际保存后回显成功',
)
assertEqual(
  pageSource.includes('删除持久化失败时必须回滚被清空字段')
    && pageSource.includes('setPendingDetailPatches(recoveredPatches)')
    && pageSource.includes('applyPendingContainerDetailPatches(items, recoveredPatches)')
    && pageSource.includes('const previousRowFailureKey = `${hguid}:*`')
    && pageSource.includes('failedPendingDetailSaveKeysRef.current = new Set(Object.keys(reconciledRecoveredFailures))'),
  true,
  '唯一字段清空删除失败时必须恢复 pending/version/失败键并将旧值叠回受控行显示',
)
assertEqual(
  pageSource.includes('清空草稿字段表示取消该本地编辑')
    && pageSource.includes('} else {\n        if (reloadRemovedFieldBaseline && Object.keys(removedFieldVersions).length > 0)')
    && pageSource.includes('void reloadCurrentDetailRef.current()')
    && pageSource.includes('}, true, updates, true)'),
  true,
  '草稿字段取消并成功删除持久化记录后，应回读服务端基线并重新叠加其余草稿',
)
