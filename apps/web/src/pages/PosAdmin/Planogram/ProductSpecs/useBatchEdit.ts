import { useCallback, useEffect, useMemo, useRef, useState, type Dispatch, type SetStateAction } from 'react'
import { useTranslation } from 'react-i18next'
import { batchUpdateProductSpecs } from '../../../../services/planogramService'
import type { ProductSpecBatchRequest, ProductSpecBatchResultDto } from '../../../../types/planogram'
import {
  batchConfirmBlock,
  batchRequestSignature,
  batchTargetTooMany,
  buildBatchRequest,
  canStartBatchPreview,
  createBatchEditState,
  IDLE_BATCH_PREVIEW,
  isBatchPreviewStale,
  resolveBatchError,
  setBatchField,
  usableBatchPreview,
  validateBatchEdit,
  type BatchConfirmBlock,
  type BatchContext,
  type BatchEditState,
  type BatchErrorView,
  type BatchFieldKey,
  type BatchPreviewState,
  type BatchTargetSpec,
  type BatchValidation,
} from './batchLogic'

export interface UseBatchEditOptions {
  /** 目标快照：打开弹窗时定下（勾选的货号 / 当前筛选），弹窗打开期间不变。 */
  target: BatchTargetSpec
  /** 可用于设置的挂钩型号 guid（启用的型号）；不传 = 不限制。 */
  hookGuids?: ReadonlySet<string>
  /** 发请求的函数；默认 planogramService.batchUpdateProductSpecs（测试里换成替身）。 */
  run?: (request: ProductSpecBatchRequest, signal?: AbortSignal) => Promise<ProductSpecBatchResultDto>
}

export interface BatchEditApi {
  state: BatchEditState
  setState: Dispatch<SetStateAction<BatchEditState>>
  /** 改某个字段（含「本次修改」勾选）：等价于 setState(setBatchField(...))。 */
  setField: <K extends Exclude<BatchFieldKey, 'createMissing'>>(key: K, patch: Partial<BatchEditState[K]>) => void
  setCreateMissing: (value: boolean) => void

  validation: BatchValidation
  /** 目标数超过 2000。 */
  tooMany: boolean

  /** 预览请求在途。 */
  previewing: boolean
  /** 当前输入下有效的预览结果；没预览过、预览已过期、还在加载、失败时为 null。 */
  preview: ProductSpecBatchResultDto | null
  /** 预览之后又改了输入：界面应隐藏旧预览并提示「内容已修改，请重新预览」。 */
  previewStale: boolean
  /** 正在执行：界面应锁住全部输入并禁用按钮。 */
  executing: boolean
  /** 执行成功后的结果（dryRun=false）。 */
  result: ProductSpecBatchResultDto | null
  /** 最近一次预览 / 执行的失败提示；改动输入后自动消失（失败时输入原样保留）。 */
  error: BatchErrorView | null

  canPreview: boolean
  /** 不能「确认执行」的原因；可以执行时为 null。 */
  confirmBlock: BatchConfirmBlock | null

  /** 预览（dryRun=true）：校验与计数，不写库。 */
  runPreview: () => Promise<void>
  /** 确认执行（dryRun=false）：必须有当前输入下的有效预览；执行中重复调用无效。 */
  execute: () => Promise<void>
}

interface FailureRecord {
  signature: string
  view: BatchErrorView
}

/**
 * 批量修改弹窗的流程控制（不含界面）：
 * - 预览之后只要「将提交的请求」变了（batchRequestSignature），旧预览立刻失效，必须重新预览才能执行；
 * - 在途的预览遇到输入变化会被中止，迟到的响应丢弃；
 * - 执行用同步标记防重复点击（state 还没提交时的第二次点击也挡得住）；执行成功后同一弹窗不再允许重复执行；
 * - 失败时输入原样保留，错误按错误码给出明确提示（resolveBatchError），改动输入后提示消失。
 */
export function useBatchEdit(options: UseBatchEditOptions): BatchEditApi {
  const { t, i18n } = useTranslation()
  const run = options.run ?? batchUpdateProductSpecs
  const [state, setState] = useState<BatchEditState>(createBatchEditState)
  const [previewState, setPreviewState] = useState<BatchPreviewState>(IDLE_BATCH_PREVIEW)
  const [executing, setExecuting] = useState(false)
  const [result, setResult] = useState<ProductSpecBatchResultDto | null>(null)
  const [failure, setFailure] = useState<FailureRecord | null>(null)

  const context = useMemo<BatchContext>(() => ({ hookGuids: options.hookGuids }), [options.hookGuids])
  const validation = useMemo(() => validateBatchEdit(state, context), [context, state])
  const signature = useMemo(() => batchRequestSignature(options.target, state, context), [context, options.target, state])
  const tooMany = batchTargetTooMany(options.target)

  // 异步回调读最新值，避免闭包拿到旧输入
  const latest = useRef({ state, target: options.target, context, signature, run, language: i18n.language, t })
  latest.current = { state, target: options.target, context, signature, run, language: i18n.language, t }
  const previewRef = useRef(previewState)
  previewRef.current = previewState
  const resultRef = useRef(result)
  resultRef.current = result

  const aliveRef = useRef(true)
  const controllerRef = useRef<AbortController | null>(null)
  const previewSignatureRef = useRef<string | null>(null)
  const executingRef = useRef(false)

  useEffect(() => {
    aliveRef.current = true
    return () => {
      aliveRef.current = false
      controllerRef.current?.abort()
    }
  }, [])

  // 输入变了：在途的预览作废（中止请求，迟到的响应在 runPreview 里按 aborted 丢弃）
  useEffect(() => {
    if (controllerRef.current && previewSignatureRef.current !== signature) {
      controllerRef.current.abort()
      controllerRef.current = null
      setPreviewState((current) => (current.status === 'loading' ? IDLE_BATCH_PREVIEW : current))
    }
  }, [signature])

  const setField = useCallback(<K extends Exclude<BatchFieldKey, 'createMissing'>>(key: K, patch: Partial<BatchEditState[K]>) => {
    setState((current) => setBatchField(current, key, patch))
  }, [])
  const setCreateMissing = useCallback((value: boolean) => setState((current) => ({ ...current, createMissing: value })), [])

  const runPreview = useCallback(async () => {
    const current = latest.current
    if (!canStartBatchPreview({
      validation: validateBatchEdit(current.state, current.context),
      tooMany: batchTargetTooMany(current.target),
      previewing: controllerRef.current !== null,
      executing: executingRef.current,
    })) return
    const controller = new AbortController()
    controllerRef.current = controller
    previewSignatureRef.current = current.signature
    setFailure(null)
    setPreviewState({ status: 'loading', signature: current.signature })
    try {
      const response = await current.run(buildBatchRequest(current.target, current.state, true, current.context), controller.signal)
      if (controller.signal.aborted || !aliveRef.current) return
      setPreviewState({ status: 'ready', signature: current.signature, result: response })
    } catch (error) {
      if (controller.signal.aborted || !aliveRef.current) return
      const view = resolveBatchError(error, current.language, current.t)
      if (view.kind === 'abort') return
      setPreviewState({ status: 'error', signature: current.signature, error })
      setFailure({ signature: current.signature, view })
    } finally {
      if (controllerRef.current === controller) controllerRef.current = null
    }
  }, [])

  const execute = useCallback(async () => {
    if (executingRef.current) return
    const current = latest.current
    const block = batchConfirmBlock({
      preview: previewRef.current,
      signature: current.signature,
      validation: validateBatchEdit(current.state, current.context),
      tooMany: batchTargetTooMany(current.target),
      executing: false,
      executed: resultRef.current !== null,
    })
    if (block) return
    executingRef.current = true
    setExecuting(true)
    setFailure(null)
    try {
      const response = await current.run(buildBatchRequest(current.target, current.state, false, current.context))
      if (aliveRef.current) setResult(response)
    } catch (error) {
      if (!aliveRef.current) return
      const view = resolveBatchError(error, current.language, current.t)
      if (view.kind !== 'abort') setFailure({ signature: current.signature, view })
      // 并发冲突（整批已回滚）：预览基于的数据已经过期，必须重新预览才能再执行
      if (view.kind === 'conflict') setPreviewState(IDLE_BATCH_PREVIEW)
    } finally {
      executingRef.current = false
      if (aliveRef.current) setExecuting(false)
    }
  }, [])

  const previewing = previewState.status === 'loading' && previewState.signature === signature
  const confirmBlock = batchConfirmBlock({ preview: previewState, signature, validation, tooMany, executing, executed: result !== null })

  return {
    state,
    setState,
    setField,
    setCreateMissing,
    validation,
    tooMany,
    previewing,
    preview: usableBatchPreview(previewState, signature),
    previewStale: isBatchPreviewStale(previewState, signature),
    executing,
    result,
    error: failure && failure.signature === signature ? failure.view : null,
    canPreview: canStartBatchPreview({ validation, tooMany, previewing, executing }) && result === null,
    confirmBlock,
    runPreview,
    execute,
  }
}
