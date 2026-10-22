import type { ContainerDetail } from '../../../types/container'

export function buildContainerDetailAutoSaveContextKey(containerGuid: string, draftIdentity: string) {
  return containerGuid && draftIdentity ? `${containerGuid}::${draftIdentity}` : ''
}

export function isContainerDetailAutoSaveContextCurrent(
  contextKey: string,
  containerGuid: string,
  draftIdentity: string,
) {
  return Boolean(contextKey) && contextKey === buildContainerDetailAutoSaveContextKey(containerGuid, draftIdentity)
}

export type ContainerDetailAutoSaveLifecycleAction = 'attach' | 'discard' | 'none'

export function resolveContainerDetailAutoSaveLifecycleAction(
  active: boolean,
  contextKey: string,
): ContainerDetailAutoSaveLifecycleAction {
  if (!contextKey) return 'none'
  return active ? 'attach' : 'discard'
}

// 队列在「空闲时被 discard」（切到别的标签）会立刻释放 context，并回调页面删除同 key 的快照；
// 切回来只会重新 attach 队列，不会重建快照。之后再编辑，发送批次就会找不到快照而报「保存上下文已失效」。
// 因此每次 attach 之前都要幂等补齐快照；已存在时保持不动，避免覆盖正在使用的行与草稿元数据。
export function ensureContainerDetailAutoSaveContextSnapshot<T>(
  snapshots: Map<string, T>,
  contextKey: string,
  createSnapshot: () => T,
) {
  if (!contextKey || snapshots.has(contextKey)) return false
  snapshots.set(contextKey, createSnapshot())
  return true
}

export const CONTAINER_DETAIL_AUTO_SAVE_FIELDS = [
  '商品名称',
  '调整浮率',
  '单件装箱数',
  '单件体积',
  '中包数',
  '备注',
] as const

export type ContainerDetailAutoSaveField = typeof CONTAINER_DETAIL_AUTO_SAVE_FIELDS[number]
export type ContainerDetailAutoSavePatch = Partial<Pick<ContainerDetail, ContainerDetailAutoSaveField>>
export type ContainerDetailAutoSavePatchMap = Record<string, ContainerDetailAutoSavePatch>

export function applyContainerDetailAutoSavePatches(
  rows: ContainerDetail[],
  patches: ContainerDetailAutoSavePatchMap,
  buildVisiblePatch: (
    row: ContainerDetail,
    patch: ContainerDetailAutoSavePatch,
  ) => Partial<ContainerDetail> = (_row, patch) => patch,
) {
  return rows.map((row) => {
    const patch = row.hguid ? patches[row.hguid] : undefined
    if (!patch) return row
    return { ...row, ...buildVisiblePatch(row, patch) }
  })
}

export interface ContainerDetailAutoSaveIntent {
  hguid: string
  patch: ContainerDetailAutoSavePatch
  revisions: Partial<Record<ContainerDetailAutoSaveField, number>>
}

export interface ContainerDetailAutoSaveValidationError {
  hguid: string
  field: string
  code: string
  message: string
}

export interface ContainerDetailAutoSaveFailure extends Omit<ContainerDetailAutoSaveValidationError, 'field'> {
  field: ContainerDetailAutoSaveField
  revision: number
}

export interface ContainerDetailAutoSaveSnapshot {
  pendingFieldCount: number
  runningFieldCount: number
  failureCount: number
  unsavedFieldCount: number
  failures: ContainerDetailAutoSaveFailure[]
}

interface ContainerDetailAutoSaveResult {
  totalUpdated?: number
  totalRequested?: number
  validationErrors?: ContainerDetailAutoSaveValidationError[]
}

interface ContainerDetailAutoSaveQueueOptions {
  sendBatch: (
    contextKey: string,
    intents: ContainerDetailAutoSaveIntent[],
  ) => Promise<ContainerDetailAutoSaveResult | void>
  onBatchSuccess?: (contextKey: string, intents: ContainerDetailAutoSaveIntent[]) => void
  onSnapshotChange?: (contextKey: string, snapshot: ContainerDetailAutoSaveSnapshot) => void
  onContextDisposed?: (contextKey: string) => void
  getRequestErrorMessage?: (error: unknown) => string
}

interface RevisionedValue {
  revision: number
  value: ContainerDetailAutoSavePatch[ContainerDetailAutoSaveField]
}

interface PendingRow {
  hguid: string
  fields: Map<ContainerDetailAutoSaveField, RevisionedValue>
}

interface ContextState {
  key: string
  nextRevision: number
  pending: Map<string, PendingRow>
  running: ContainerDetailAutoSaveIntent[]
  latest: Map<string, RevisionedValue>
  failures: Map<string, ContainerDetailAutoSaveFailure>
  processPromise: Promise<void> | null
  owner: symbol | null
  sendBatch: ContainerDetailAutoSaveQueueOptions['sendBatch'] | null
  onBatchSuccess: ContainerDetailAutoSaveQueueOptions['onBatchSuccess'] | null
  onSnapshotChange: ContainerDetailAutoSaveQueueOptions['onSnapshotChange'] | null
  onContextDisposed: ContainerDetailAutoSaveQueueOptions['onContextDisposed'] | null
  getRequestErrorMessage: ContainerDetailAutoSaveQueueOptions['getRequestErrorMessage'] | null
  discarded: boolean
}

const autoSaveFieldSet = new Set<string>(CONTAINER_DETAIL_AUTO_SAVE_FIELDS)
const EMPTY_SNAPSHOT: ContainerDetailAutoSaveSnapshot = {
  pendingFieldCount: 0,
  runningFieldCount: 0,
  failureCount: 0,
  unsavedFieldCount: 0,
  failures: [],
}
// 只在当前应用会话内保存自动保存意图；页面组件重建时可按 context 重新 attach，clean 后立即释放。
const sharedAutoSaveContexts = new Map<string, ContextState>()

function fieldKey(hguid: string, field: ContainerDetailAutoSaveField) {
  return `${hguid}:${field}`
}

function getIntentFields(intent: ContainerDetailAutoSaveIntent) {
  return Object.keys(intent.patch).filter(
    (field): field is ContainerDetailAutoSaveField => autoSaveFieldSet.has(field),
  )
}

function countIntentFields(intents: ContainerDetailAutoSaveIntent[]) {
  return intents.reduce((count, intent) => count + getIntentFields(intent).length, 0)
}

function buildSnapshot(state: ContextState): ContainerDetailAutoSaveSnapshot {
  const unsavedKeys = new Set<string>()
  state.pending.forEach((row) => {
    row.fields.forEach((_value, field) => unsavedKeys.add(fieldKey(row.hguid, field)))
  })
  state.running.forEach((intent) => {
    getIntentFields(intent).forEach((field) => unsavedKeys.add(fieldKey(intent.hguid, field)))
  })
  state.failures.forEach((_failure, key) => unsavedKeys.add(key))

  return {
    pendingFieldCount: Array.from(state.pending.values()).reduce((count, row) => count + row.fields.size, 0),
    runningFieldCount: countIntentFields(state.running),
    failureCount: state.failures.size,
    unsavedFieldCount: unsavedKeys.size,
    failures: Array.from(state.failures.values()),
  }
}

function buildUnsettledPatches(state: ContextState): ContainerDetailAutoSavePatchMap {
  const unsettledFields = new Map<string, {
    hguid: string
    field: ContainerDetailAutoSaveField
  }>()
  const addField = (hguid: string, field: ContainerDetailAutoSaveField) => {
    unsettledFields.set(fieldKey(hguid, field), { hguid, field })
  }

  state.pending.forEach((row) => {
    row.fields.forEach((_value, field) => addField(row.hguid, field))
  })
  state.running.forEach((intent) => {
    getIntentFields(intent).forEach((field) => addField(intent.hguid, field))
  })
  state.failures.forEach((failure) => addField(failure.hguid, failure.field))

  const latestFields = Array.from(unsettledFields.entries())
    .map(([key, owner]) => ({ ...owner, revisionedValue: state.latest.get(key) }))
    .filter((item): item is typeof item & { revisionedValue: RevisionedValue } => Boolean(item.revisionedValue))
    .sort((left, right) => left.revisionedValue.revision - right.revisionedValue.revision)

  return latestFields.reduce<ContainerDetailAutoSavePatchMap>((patches, item) => {
    const patch = patches[item.hguid] ?? {}
    Object.assign(patch, { [item.field]: item.revisionedValue.value })
    patches[item.hguid] = patch
    return patches
  }, {})
}

function buildIntent(row: PendingRow): ContainerDetailAutoSaveIntent {
  const patch: ContainerDetailAutoSavePatch = {}
  const revisions: ContainerDetailAutoSaveIntent['revisions'] = {}
  row.fields.forEach((revisionedValue, field) => {
    Object.assign(patch, { [field]: revisionedValue.value })
    revisions[field] = revisionedValue.revision
  })
  return { hguid: row.hguid, patch, revisions }
}

function defaultRequestErrorMessage(error: unknown) {
  return error instanceof Error && error.message ? error.message : '货柜明细保存失败，请稍后重试'
}

export class ContainerDetailAutoSaveDrainError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'ContainerDetailAutoSaveDrainError'
  }
}

export function createContainerDetailAutoSaveQueue(options: ContainerDetailAutoSaveQueueOptions) {
  const owner = Symbol('container-detail-auto-save-client')
  const contexts = sharedAutoSaveContexts

  const getOrCreateState = (contextKey: string) => {
    const current = contexts.get(contextKey)
    if (current) return current
    const next: ContextState = {
      key: contextKey,
      nextRevision: 0,
      pending: new Map(),
      running: [],
      latest: new Map(),
      failures: new Map(),
      processPromise: null,
      owner: null,
      sendBatch: null,
      onBatchSuccess: null,
      onSnapshotChange: null,
      onContextDisposed: null,
      getRequestErrorMessage: null,
      discarded: false,
    }
    contexts.set(contextKey, next)
    return next
  }

  const bindState = (state: ContextState) => {
    state.owner = owner
    state.sendBatch = options.sendBatch
    state.onBatchSuccess = options.onBatchSuccess ?? null
    state.onSnapshotChange = options.onSnapshotChange ?? null
    state.onContextDisposed = options.onContextDisposed ?? null
    state.getRequestErrorMessage = options.getRequestErrorMessage ?? null
    state.discarded = false
  }

  const detachUiCallbacks = (state: ContextState) => {
    state.onBatchSuccess = null
    state.onSnapshotChange = null
  }

  const detachIdleTransport = (state: ContextState) => {
    state.sendBatch = null
    state.getRequestErrorMessage = null
  }

  const disposeState = (state: ContextState) => {
    if (contexts.get(state.key) !== state) return
    const onContextDisposed = state.onContextDisposed
    contexts.delete(state.key)
    state.pending.clear()
    state.running = []
    state.latest.clear()
    state.failures.clear()
    state.owner = null
    state.sendBatch = null
    state.onBatchSuccess = null
    state.onSnapshotChange = null
    state.onContextDisposed = null
    state.getRequestErrorMessage = null
    onContextDisposed?.(state.key)
  }

  const notify = (state: ContextState) => {
    if (!state.discarded) {
      state.onSnapshotChange?.(state.key, buildSnapshot(state))
    }
  }

  const isCurrentRevision = (
    state: ContextState,
    hguid: string,
    field: ContainerDetailAutoSaveField,
    revision: number,
  ) => state.latest.get(fieldKey(hguid, field))?.revision === revision

  const markFailure = (
    state: ContextState,
    intent: ContainerDetailAutoSaveIntent,
    field: ContainerDetailAutoSaveField,
    error: Omit<ContainerDetailAutoSaveValidationError, 'hguid' | 'field'>,
  ) => {
    const revision = intent.revisions[field]
    if (revision == null || !isCurrentRevision(state, intent.hguid, field, revision)) return
    state.failures.set(fieldKey(intent.hguid, field), {
      hguid: intent.hguid,
      field,
      revision,
      ...error,
    })
  }

  const markRequestFailure = (
    state: ContextState,
    intents: ContainerDetailAutoSaveIntent[],
    error: unknown,
    getRequestErrorMessage?: ContainerDetailAutoSaveQueueOptions['getRequestErrorMessage'],
  ) => {
    const message = (getRequestErrorMessage ?? defaultRequestErrorMessage)(error)
    intents.forEach((intent) => {
      getIntentFields(intent).forEach((field) => {
        markFailure(state, intent, field, { code: 'REQUEST_FAILED', message })
      })
    })
  }

  const markValidationFailures = (
    state: ContextState,
    intents: ContainerDetailAutoSaveIntent[],
    errors: ContainerDetailAutoSaveValidationError[],
  ) => {
    const intentByHguid = new Map(intents.map((intent) => [intent.hguid, intent]))
    errors.forEach((error) => {
      const intent = intentByHguid.get(error.hguid)
      if (!intent) return
      const intentFields = getIntentFields(intent)
      const fields = autoSaveFieldSet.has(error.field)
        ? [error.field as ContainerDetailAutoSaveField]
        : intentFields
      fields.forEach((field) => {
        markFailure(state, intent, field, { code: error.code, message: error.message })
      })
    })
  }

  const markIncompleteBatchFailures = (
    state: ContextState,
    intents: ContainerDetailAutoSaveIntent[],
    totalUpdated: number,
    totalRequested: number,
  ) => {
    intents.forEach((intent) => {
      getIntentFields(intent).forEach((field) => {
        markFailure(state, intent, field, {
          code: 'INCOMPLETE_BATCH',
          message: `批量保存仅完成 ${totalUpdated}/${totalRequested} 条，请重试未确认项`,
        })
      })
    })
  }

  const processState = async (state: ContextState) => {
    while (state.pending.size > 0 && state.failures.size === 0) {
      const pending = state.pending
      state.pending = new Map()
      const intents = Array.from(pending.values(), buildIntent)
      const sendBatch = state.sendBatch
      const getRequestErrorMessage = state.getRequestErrorMessage ?? undefined
      state.running = intents
      notify(state)

      if (!sendBatch) {
        markRequestFailure(
          state,
          intents,
          new Error('货柜明细保存上下文已失效'),
          getRequestErrorMessage,
        )
      } else {
        try {
          const result = await sendBatch(state.key, intents)
          // running patch 在此回调完成前仍覆盖查询结果；先失效旧 item query，再允许清除成功意图。
          if (!state.discarded) {
            try {
              state.onBatchSuccess?.(state.key, intents)
            } catch {
              // 查询失效属于展示收尾，不能把已经提交成功的写入误标记为失败。
            }
          }
          const validationErrors = result?.validationErrors ?? []
          if (validationErrors.length > 0) {
            // 有明确字段错误时只保留对应字段，避免把同批已成功字段无谓标记为失败。
            markValidationFailures(state, intents, validationErrors)
          } else if (
            typeof result?.totalUpdated === 'number'
            && typeof result.totalRequested === 'number'
            && result.totalUpdated < result.totalRequested
          ) {
            // 服务没有逐行成功清单；静默少更新时只能 fail-closed 保留本批当前 revision。
            markIncompleteBatchFailures(state, intents, result.totalUpdated, result.totalRequested)
          }
        } catch (error) {
          markRequestFailure(state, intents, error, getRequestErrorMessage)
        }
      }
      state.running = []
      notify(state)

      // 当前 revision 失败后必须暂停依赖字段；否则后续派生值可能基于尚未落库的页面值。
      if (state.failures.size > 0) break
    }
  }

  const ensureProcessing = (state: ContextState) => {
    if (
      state.processPromise
      || state.pending.size === 0
      || state.failures.size > 0
      || !state.sendBatch
    ) return
    const processPromise = processState(state).finally(() => {
      if (state.processPromise === processPromise) {
        state.processPromise = null
      }
      if (state.discarded) {
        if (state.pending.size === 0 && state.failures.size === 0) {
          disposeState(state)
        } else {
          // 失败意图只保留数据，不继续持有已经卸载组件的 transport/UI callbacks。
          detachIdleTransport(state)
        }
        return
      }
      // Promise 收尾期间可能有新输入入队；继续启动同一上下文的串行 drain。
      if (state.pending.size > 0) {
        ensureProcessing(state)
      }
    })
    state.processPromise = processPromise
  }

  const enqueue = (
    contextKey: string,
    hguid: string,
    patch: ContainerDetailAutoSavePatch,
  ) => {
    if (!contextKey || !hguid) return
    const fields = Object.keys(patch).filter(
      (field): field is ContainerDetailAutoSaveField => autoSaveFieldSet.has(field),
    )
    if (!fields.length) return

    const state = getOrCreateState(contextKey)
    if (state.owner === null) {
      bindState(state)
    } else if (state.owner !== owner || state.discarded) {
      // 只有 attach 可以接管既有 context；旧组件迟到事件不得抢回 owner 或重绑 callbacks。
      return
    }
    const pendingRow = state.pending.get(hguid) ?? { hguid, fields: new Map() }
    fields.forEach((field) => {
      const revision = state.nextRevision + 1
      state.nextRevision = revision
      const revisionedValue: RevisionedValue = {
        revision,
        value: patch[field],
      }
      pendingRow.fields.set(field, revisionedValue)
      state.latest.set(fieldKey(hguid, field), revisionedValue)
      state.failures.delete(fieldKey(hguid, field))
    })
    state.pending.set(hguid, pendingRow)
    notify(state)
    ensureProcessing(state)
  }

  const drain = async (contextKey: string) => {
    const state = contexts.get(contextKey)
    if (!state || state.discarded || state.owner !== owner) return
    ensureProcessing(state)
    while (state.processPromise) {
      await state.processPromise
    }
    if (state.failures.size > 0) {
      const firstFailure = state.failures.values().next().value as ContainerDetailAutoSaveFailure | undefined
      throw new ContainerDetailAutoSaveDrainError(
        firstFailure?.message ?? '货柜明细存在未保存项，请重试',
      )
    }
  }

  const retryFailed = (contextKey: string) => {
    const state = contexts.get(contextKey)
    if (!state || state.discarded || state.owner !== owner || state.failures.size === 0) return

    // 先把全部失败字段原子合并回既有 pending，再统一启动 transport，避免首字段提前出队。
    Array.from(state.failures.values()).forEach((failure) => {
      const current = state.latest.get(fieldKey(failure.hguid, failure.field))
      state.failures.delete(fieldKey(failure.hguid, failure.field))
      if (!current || current.revision !== failure.revision) return

      const pendingRow = state.pending.get(failure.hguid) ?? {
        hguid: failure.hguid,
        fields: new Map<ContainerDetailAutoSaveField, RevisionedValue>(),
      }
      const pendingField = pendingRow.fields.get(failure.field)
      if (!pendingField || pendingField.revision < current.revision) {
        pendingRow.fields.set(failure.field, current)
      }
      state.pending.set(failure.hguid, pendingRow)
    })
    notify(state)
    ensureProcessing(state)
  }

  const clearFailures = (
    contextKey: string,
    hguid: string,
    fields: ContainerDetailAutoSaveField[],
  ) => {
    const state = contexts.get(contextKey)
    if (!state || state.discarded || state.owner !== owner) return
    fields.forEach((field) => state.failures.delete(fieldKey(hguid, field)))
    notify(state)
  }

  const discardContext = (contextKey: string) => {
    const state = contexts.get(contextKey)
    if (!state || state.owner !== owner) return
    // detach 只停止 UI 回调；已入队写入必须继续串行发送，避免切页时静默丢数据。
    state.discarded = true
    detachUiCallbacks(state)
    if (!state.processPromise && state.pending.size === 0 && state.failures.size === 0) {
      disposeState(state)
      return
    }

    // 未结算 context 由共享 store 自己持有；不要让旧组件的 dispose callback 随失败值长期存活。
    state.onContextDisposed = null
    if (state.pending.size > 0 && state.failures.size === 0) {
      ensureProcessing(state)
    } else if (!state.processPromise) {
      detachIdleTransport(state)
    }
  }

  const attachContext = (contextKey: string) => {
    if (!contextKey) return EMPTY_SNAPSHOT
    const state = getOrCreateState(contextKey)
    bindState(state)
    ensureProcessing(state)
    const snapshot = buildSnapshot(state)
    notify(state)
    return snapshot
  }

  const getSnapshot = (contextKey: string) => {
    const state = contexts.get(contextKey)
    return state && !state.discarded && state.owner === owner ? buildSnapshot(state) : EMPTY_SNAPSHOT
  }

  const getUnsettledPatches = (contextKey: string) => {
    const state = contexts.get(contextKey)
    return state ? buildUnsettledPatches(state) : {}
  }

  return {
    enqueue,
    drain,
    retryFailed,
    clearFailures,
    discardContext,
    attachContext,
    getSnapshot,
    getUnsettledPatches,
  }
}

export type ContainerDetailAutoSaveQueue = ReturnType<typeof createContainerDetailAutoSaveQueue>
