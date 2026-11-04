import { CheckCircleFilled, CheckOutlined, EyeOutlined, WarningFilled } from '@ant-design/icons'
import { Alert, Button, Checkbox, Input, InputNumber, Modal, Radio, Segmented, Select, Spin, Switch, Tag } from 'antd'
import { useMemo, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { listProductSpecs } from '../../../../services/planogramService'
import type {
  HookModelDto,
  PlanogramHangHoleType,
  PlanogramSpecSource,
  ProductSpecBatchFilter,
  ProductSpecBatchResultDto,
} from '../../../../types/planogram'
import { batchFilterToQuery } from '../categoryFilter/categoryFilterLogic'
import { EMPTY_CELL } from '../planogramLogic'
import { usePlanogramRequest } from '../usePlanogramRequest'
import {
  BATCH_MAX_HOOK_QTY,
  BATCH_MAX_NOTES_LENGTH,
  BATCH_MAX_TARGETS,
  batchErrorKey,
  batchTargetFromFilter,
  batchTargetFromSelection,
  batchTargetHasNoUnset,
  changeLabelKey,
  formatChangeLines,
  selectionCounts,
  skipReasonKey,
  skippedItemsTruncated,
  type BatchEditState,
  type BatchFieldKey,
  type BatchHookQtyMode,
  type BatchSelection,
  type BatchTargetSpec,
  type ChangeTextLookup,
} from './batchLogic'
import { useBatchEdit } from './useBatchEdit'

interface BatchEditModalProps {
  open: boolean
  /** 打开时默认的目标；没有勾选任何商品时只能是「当前筛选」。 */
  initialTarget: 'selected' | 'filter'
  /** 勾选集合（打开时的快照，跨页保留）。 */
  selection: BatchSelection
  /** 当前筛选（打开时的快照）与它匹配的商品总数。 */
  filter: ProductSpecBatchFilter
  filterTotal: number
  /** 全部挂钩型号（含停用）。 */
  hooks: HookModelDto[]
  hooksReady: boolean
  hooksFailed: boolean
  onReloadHooks: () => void
  /** 关闭弹窗；executed = 这次执行成功过（调用方据此刷新列表并清空勾选）。 */
  onClose: (executed: boolean) => void
}

type TargetKind = 'selected' | 'filter'

/** 弹窗里的「要修改的字段」行（带「本次修改」开关）。 */
function FieldRow({ label, enabled, locked, onToggle, hint, required, error, children }: {
  label: string
  enabled: boolean
  locked: boolean
  onToggle: (value: boolean) => void
  /** 打开后右侧提示列的内容（「定挂数」说明，或可清空字段的「清空」勾选）；关闭时固定显示「不改」。 */
  hint?: ReactNode
  /** 打开了但还没填：控件下方给一行橙色提醒（不是红色错误）。 */
  required?: boolean
  error?: string | null
  children: ReactNode
}) {
  const { t } = useTranslation()
  return (
    <div className={`planogram-batch-row${enabled ? '' : ' is-off'}`}>
      <Switch size="small" checked={enabled} disabled={locked} onChange={onToggle} aria-label={t('planogram.spec.batch.switchAria', { name: label })} />
      <span className="planogram-batch-label">{label}</span>
      <div className="planogram-batch-control">
        {children}
        {error ? <div className="planogram-batch-error" role="alert">{error}</div> : null}
        {!error && required ? <div className="planogram-batch-required">{t('planogram.spec.batch.hint.required')}</div> : null}
      </div>
      <span className="planogram-batch-hint">{enabled ? hint : t('planogram.spec.batch.hint.off')}</span>
    </div>
  )
}

function Tile({ value, label, tone }: { value: number; label: string; tone?: 'blue' | 'green' | 'orange' }) {
  return (
    <div className="planogram-batch-tile">
      <b className={tone ? `is-${tone}` : undefined}>{value}</b>
      <span>{label}</span>
    </div>
  )
}

/** 一个数字输入（宽 / 高 / 厚 / 吊孔偏移 / 单件重）：开关打开才可输入，灰显时不可改。 */
function NumberControl({ value, onChange, disabled, unit, min, max, label, status }: {
  value: number | null
  onChange: (value: number | null) => void
  disabled: boolean
  unit: string
  min: number
  max?: number
  label: string
  status?: boolean
}) {
  return (
    <InputNumber
      className="planogram-batch-number"
      size="small"
      controls={false}
      min={min}
      max={max}
      precision={0}
      value={value}
      disabled={disabled}
      status={status ? 'error' : undefined}
      aria-label={label}
      suffix={<span className="planogram-unit">{unit}</span>}
      onChange={(next) => onChange(typeof next === 'number' ? next : null)}
    />
  )
}

/** 批量修改陈列参数（设计图方案 1：一个弹窗，左边勾字段，右边看预览）。 */
export default function BatchEditModal({
  open, initialTarget, selection, filter, filterTotal, hooks, hooksReady, hooksFailed, onReloadHooks, onClose,
}: BatchEditModalProps) {
  const { t } = useTranslation()
  const counts = useMemo(() => selectionCounts(selection), [selection])
  const [targetKind, setTargetKind] = useState<TargetKind>(initialTarget === 'selected' && selection.size > 0 ? 'selected' : 'filter')

  // 按当前筛选 + 状态=全部时，再查一次「已设参数」数（pageSize=1 只取 total）：未设数 = 总数 − 已设数
  const needSetCount = targetKind === 'filter' && filter.status === 'all' && filterTotal > 0
  const setCountRequest = usePlanogramRequest<number>(
    `batch-set-count:${JSON.stringify(filter)}`,
    (signal) => listProductSpecs(batchFilterToQuery(filter, { status: 'set', page: 1, pageSize: 1 }), signal).then((result) => result.total),
    needSetCount,
  )
  const target = useMemo<BatchTargetSpec>(
    () => (targetKind === 'selected'
      ? batchTargetFromSelection(selection)
      : batchTargetFromFilter(filter, filterTotal, setCountRequest.data ?? null)),
    [filter, filterTotal, selection, setCountRequest.data, targetKind],
  )

  const activeHooks = useMemo(() => hooks.filter((hook) => hook.isActive), [hooks])
  const hooksByGuid = useMemo(() => new Map(hooks.map((hook) => [hook.guid, hook])), [hooks])
  const hookGuids = useMemo(() => new Set(activeHooks.map((hook) => hook.guid)), [activeHooks])
  const edit = useBatchEdit({ target, hookGuids })
  const { state, validation } = edit
  const locked = edit.executing || edit.result !== null
  const targetCount = target.kind === 'selected' ? target.productCodes.length : target.total

  const lookup = useMemo<ChangeTextLookup>(() => ({
    label: (key) => t(changeLabelKey(key)),
    hangType: (value) => t(HANG_TYPE_KEYS[value]),
    source: (value) => t(SOURCE_KEYS[value]),
    hookName: (guid) => hooksByGuid.get(guid)?.name ?? guid.slice(0, 8),
    none: EMPTY_CELL,
    cleared: t('planogram.spec.batch.change.cleared'),
    noOverrides: t('planogram.spec.batch.change.noOverrides'),
  }), [hooksByGuid, t])

  // 字段下方的错误：本地校验（required 只在提示列里用橙色提醒，不画红字）优先，其次是服务端按字段名给的错误
  const fieldError = (key: BatchFieldKey): string | null => {
    const local = validation.errors[key]
    if (local && local.code !== 'required') return t(batchErrorKey(local.code), local.params)
    return edit.error?.fieldErrors[key] ?? null
  }
  const isRequired = (key: BatchFieldKey): boolean => validation.errors[key]?.code === 'required'

  const set = <K extends Exclude<BatchFieldKey, 'createMissing'>>(key: K, patch: Partial<BatchEditState[K]>) => edit.setField(key, patch)
  const requestClose = () => {
    if (edit.executing) return
    onClose(edit.result !== null)
  }

  // ---------- 目标条 ----------
  const countsText = target.setCount === null || target.unsetCount === null
    ? t('planogram.spec.batch.target.countsUnknown')
    : t('planogram.spec.batch.target.counts', { set: target.setCount, unset: target.unsetCount })

  // ---------- 右栏：每钩挂数覆盖 ----------
  const hookQtyChoice: 'none' | BatchHookQtyMode = state.hookQty.enabled ? state.hookQty.mode : 'none'
  const changeHookQtyChoice = (choice: 'none' | BatchHookQtyMode) => {
    if (choice === 'none') set('hookQty', { enabled: false })
    else set('hookQty', { enabled: true, mode: choice })
  }

  // ---------- 右栏：预览 / 结果 ----------
  const preview = edit.preview
  const shown: ProductSpecBatchResultDto | null = edit.result ?? preview
  const tooManyText = edit.tooMany ? t('planogram.errors.batchTooManyCount', { count: targetCount, max: BATCH_MAX_TARGETS }) : null
  const createReady = !validation.errors.createMissing
  const noUnset = batchTargetHasNoUnset(target)

  const previewBusy = edit.previewing
  const confirmCount = preview ? preview.willCreate + preview.willUpdate : 0

  return (
    <Modal
      open={open}
      width={880}
      centered={false}
      style={{ top: 40, maxWidth: 'calc(100vw - 32px)' }}
      className="planogram-batch-modal"
      title={t('planogram.spec.batch.title')}
      destroyOnHidden
      maskClosable={false}
      keyboard={!edit.executing}
      closable={!edit.executing}
      onCancel={requestClose}
      footer={(
        <div className="planogram-batch-footer">
          <span className={`planogram-batch-footer-hint${edit.previewStale ? ' is-warn' : ''}`}>
            {edit.result ? '' : edit.previewStale ? t('planogram.spec.batch.footer.stale') : t('planogram.spec.batch.footer.hint')}
          </span>
          {edit.result ? (
            <>
              <Button onClick={() => onClose(true)}>{t('planogram.common.close')}</Button>
              <Button type="primary" icon={<CheckOutlined />} onClick={() => onClose(true)}>{t('planogram.spec.batch.footer.refresh')}</Button>
            </>
          ) : (
            <>
              <Button disabled={edit.executing} onClick={requestClose}>{t('planogram.common.cancel')}</Button>
              <Button icon={<EyeOutlined />} loading={previewBusy} disabled={!edit.canPreview} onClick={() => { void edit.runPreview() }}>
                {preview || edit.previewStale ? t('planogram.spec.batch.footer.rePreview') : t('planogram.spec.batch.footer.preview')}
              </Button>
              <Button
                type="primary"
                icon={<CheckOutlined />}
                loading={edit.executing}
                disabled={edit.confirmBlock !== null}
                onClick={() => { void edit.execute() }}
              >
                {preview ? t('planogram.spec.batch.footer.confirmCount', { count: confirmCount }) : t('planogram.spec.batch.footer.confirm')}
              </Button>
            </>
          )}
        </div>
      )}
    >
      <div className="planogram-batch">
        {/* 预览 / 执行失败：输入原样保留；冲突用黄色提示重新预览，其余红色；字段错误同时落在对应字段下方 */}
        {edit.error ? (
          <Alert className="planogram-batch-alert" type={edit.error.kind === 'conflict' ? 'warning' : 'error'} showIcon message={edit.error.message} />
        ) : null}
        {tooManyText ? <Alert className="planogram-batch-alert" type="warning" showIcon message={tooManyText} /> : null}
        {hooksFailed ? (
          <Alert
            className="planogram-batch-alert"
            type="warning"
            showIcon
            message={t('planogram.spec.hooksFailed')}
            action={<Button size="small" onClick={onReloadHooks}>{t('planogram.common.retry')}</Button>}
          />
        ) : null}

        <div className="planogram-batch-target">
          <b>{t('planogram.spec.batch.target.label')}</b>
          <Segmented<TargetKind>
            value={targetKind}
            disabled={locked}
            onChange={setTargetKind}
            options={[
              { label: t('planogram.spec.batch.target.selected', { count: counts.total }), value: 'selected', disabled: counts.total === 0 },
              { label: t('planogram.spec.batch.target.filter', { count: filterTotal }), value: 'filter' },
            ]}
          />
          <span className="planogram-muted-text">{countsText}</span>
        </div>

        <div className="planogram-batch-cols">
          <section className="planogram-batch-fields" aria-label={t('planogram.spec.batch.fields.title')}>
            <div className="planogram-batch-sec">
              {t('planogram.spec.batch.fields.title')}
              <span className="planogram-muted-text planogram-small">{t('planogram.spec.batch.fields.hint')}</span>
            </div>
            <FieldRow label={t('planogram.spec.field.width')} enabled={state.width.enabled} locked={locked} onToggle={(enabled) => set('width', { enabled })} error={fieldError('width')} required={isRequired('width')}>
              <NumberControl label={t('planogram.spec.field.width')} unit="mm" min={1} max={10000} value={state.width.value} disabled={!state.width.enabled || locked} status={Boolean(fieldError('width'))} onChange={(value) => set('width', { value })} />
            </FieldRow>
            <FieldRow label={t('planogram.spec.field.height')} enabled={state.height.enabled} locked={locked} onToggle={(enabled) => set('height', { enabled })} error={fieldError('height')} required={isRequired('height')}>
              <NumberControl label={t('planogram.spec.field.height')} unit="mm" min={1} max={10000} value={state.height.value} disabled={!state.height.enabled || locked} status={Boolean(fieldError('height'))} onChange={(value) => set('height', { value })} />
            </FieldRow>
            <FieldRow label={t('planogram.spec.field.depth')} enabled={state.depth.enabled} locked={locked} onToggle={(enabled) => set('depth', { enabled })} hint={t('planogram.spec.batch.hint.qty')} error={fieldError('depth')} required={isRequired('depth')}>
              <NumberControl label={t('planogram.spec.field.depth')} unit="mm" min={1} max={10000} value={state.depth.value} disabled={!state.depth.enabled || locked} status={Boolean(fieldError('depth'))} onChange={(value) => set('depth', { value })} />
            </FieldRow>
            <FieldRow label={t('planogram.spec.field.hangOffset')} enabled={state.hangOffset.enabled} locked={locked} onToggle={(enabled) => set('hangOffset', { enabled })} error={fieldError('hangOffset')} required={isRequired('hangOffset')}>
              <NumberControl label={t('planogram.spec.field.hangOffset')} unit="mm" min={0} value={state.hangOffset.value} disabled={!state.hangOffset.enabled || locked} status={Boolean(fieldError('hangOffset'))} onChange={(value) => set('hangOffset', { value })} />
            </FieldRow>
            <FieldRow label={t('planogram.spec.field.hangType')} enabled={state.hangType.enabled} locked={locked} onToggle={(enabled) => set('hangType', { enabled })}>
              <Select<PlanogramHangHoleType>
                size="small"
                className="planogram-batch-select"
                value={state.hangType.value}
                disabled={!state.hangType.enabled || locked}
                aria-label={t('planogram.spec.field.hangType')}
                onChange={(value) => set('hangType', { value })}
                options={[
                  { value: 2, label: t('planogram.spec.hangType.euro') },
                  { value: 1, label: t('planogram.spec.hangType.round') },
                  { value: 0, label: t('planogram.spec.hangType.none') },
                ]}
              />
            </FieldRow>
            <FieldRow label={t('planogram.spec.field.weight')} enabled={state.weight.enabled} locked={locked} onToggle={(enabled) => set('weight', { enabled })} hint={<Checkbox checked={state.weight.clear} disabled={locked} onChange={(event) => set('weight', { clear: event.target.checked })}>{t('planogram.spec.batch.clear')}</Checkbox>} error={fieldError('weight')} required={isRequired('weight')}>
              <NumberControl label={t('planogram.spec.field.weight')} unit="g" min={1} max={1000000} value={state.weight.value} disabled={!state.weight.enabled || state.weight.clear || locked} status={Boolean(fieldError('weight'))} onChange={(value) => set('weight', { value })} />
            </FieldRow>
            <FieldRow label={t('planogram.spec.col.defaultHook')} enabled={state.defaultHook.enabled} locked={locked || !hooksReady} onToggle={(enabled) => set('defaultHook', { enabled })} hint={<Checkbox checked={state.defaultHook.clear} disabled={locked} onChange={(event) => set('defaultHook', { clear: event.target.checked })}>{t('planogram.spec.batch.clear')}</Checkbox>} error={fieldError('defaultHook')} required={isRequired('defaultHook')}>
              <Select<string>
                size="small"
                className="planogram-batch-select is-wide"
                value={state.defaultHook.value ?? undefined}
                placeholder={activeHooks.length === 0 ? t('planogram.spec.batch.hookQty.empty') : undefined}
                disabled={!state.defaultHook.enabled || state.defaultHook.clear || locked}
                aria-label={t('planogram.spec.col.defaultHook')}
                status={fieldError('defaultHook') ? 'error' : undefined}
                onChange={(value) => set('defaultHook', { value })}
                options={activeHooks.map((hook) => ({ value: hook.guid, label: hook.name }))}
              />
            </FieldRow>
            <FieldRow label={t('planogram.spec.section.source')} enabled={state.source.enabled} locked={locked} onToggle={(enabled) => set('source', { enabled })}>
              <Select<PlanogramSpecSource>
                size="small"
                className="planogram-batch-select is-wide"
                value={state.source.value}
                disabled={!state.source.enabled || locked}
                aria-label={t('planogram.spec.section.source')}
                onChange={(value) => set('source', { value })}
                options={[
                  { value: 1, label: t('planogram.spec.source.manual') },
                  { value: 2, label: t('planogram.spec.source.supplier') },
                  { value: 3, label: t('planogram.spec.source.set') },
                ]}
              />
            </FieldRow>
            <FieldRow label={t('planogram.spec.field.notes')} enabled={state.notes.enabled} locked={locked} onToggle={(enabled) => set('notes', { enabled })} hint={<Checkbox checked={state.notes.clear} disabled={locked} onChange={(event) => set('notes', { clear: event.target.checked })}>{t('planogram.spec.batch.clear')}</Checkbox>} error={fieldError('notes')} required={isRequired('notes')}>
              <Input
                size="small"
                className="planogram-batch-text"
                maxLength={BATCH_MAX_NOTES_LENGTH + 100}
                value={state.notes.value}
                disabled={!state.notes.enabled || state.notes.clear || locked}
                aria-label={t('planogram.spec.field.notes')}
                status={fieldError('notes') ? 'error' : undefined}
                onChange={(event) => set('notes', { value: event.target.value })}
              />
            </FieldRow>
          </section>

          <section className="planogram-batch-side">
            <div className="planogram-batch-sec">{t('planogram.spec.batch.hookQty.title')}</div>
            <Radio.Group value={hookQtyChoice} disabled={locked || !hooksReady} onChange={(event) => changeHookQtyChoice(event.target.value as 'none' | BatchHookQtyMode)}>
              <Radio value="none">{t('planogram.spec.batch.hookQty.none')}</Radio>
              <Radio value="merge">{t('planogram.spec.batch.hookQty.merge')}</Radio>
              <Radio value="replace">{t('planogram.spec.batch.hookQty.replace')}</Radio>
              <Radio value="clearAll">{t('planogram.spec.batch.hookQty.clearAll')}</Radio>
            </Radio.Group>
            {hookQtyChoice === 'merge' || hookQtyChoice === 'replace' ? (
              <div className="planogram-batch-hookqty">
                <div className="planogram-muted-text planogram-small">
                  {hookQtyChoice === 'merge' ? t('planogram.spec.batch.hookQty.mergeHint') : t('planogram.spec.batch.hookQty.replaceHint')}
                </div>
                {activeHooks.length === 0 ? <div className="planogram-muted-text">{t('planogram.spec.batch.hookQty.empty')}</div> : activeHooks.map((hook) => (
                  <label key={hook.guid} className="planogram-batch-hookrow">
                    <span className="planogram-batch-hookname">{hook.name}</span>
                    <InputNumber
                      size="small"
                      controls={false}
                      min={1}
                      max={BATCH_MAX_HOOK_QTY}
                      precision={0}
                      placeholder={EMPTY_CELL}
                      disabled={locked}
                      value={state.hookQty.values[hook.guid] ?? null}
                      aria-label={t('planogram.spec.hookTable.manualAria', { name: hook.name })}
                      onChange={(value) => set('hookQty', { values: { ...state.hookQty.values, [hook.guid]: typeof value === 'number' ? value : null } })}
                    />
                  </label>
                ))}
              </div>
            ) : null}
            {hookQtyChoice === 'clearAll' ? <div className="planogram-muted-text planogram-small planogram-batch-note">{t('planogram.spec.batch.hookQty.clearHint')}</div> : null}
            {fieldError('hookQty') ? <div className="planogram-batch-error" role="alert">{fieldError('hookQty')}</div> : null}

            <div className="planogram-batch-sec is-gap">
              {target.unsetCount === null
                ? t('planogram.spec.batch.create.title')
                : t('planogram.spec.batch.create.titleCount', { count: target.unsetCount })}
            </div>
            <Radio.Group
              value={state.createMissing ? 'create' : 'skip'}
              disabled={locked || noUnset}
              onChange={(event) => edit.setCreateMissing(event.target.value === 'create')}
            >
              <Radio value="skip">{t('planogram.spec.batch.create.skip')}</Radio>
              <Radio value="create">{t('planogram.spec.batch.create.create')}</Radio>
            </Radio.Group>
            {noUnset ? (
              <div className="planogram-muted-text planogram-small planogram-batch-note">{t('planogram.spec.batch.create.noUnset')}</div>
            ) : state.createMissing ? (
              <div className={`planogram-batch-note planogram-small ${createReady ? 'is-ok' : 'is-warn'}`} role={createReady ? undefined : 'alert'}>
                {createReady ? <CheckCircleFilled aria-hidden="true" /> : <WarningFilled aria-hidden="true" />}{' '}
                {createReady ? t('planogram.spec.batch.create.ready') : fieldError('createMissing') ?? t('planogram.spec.batch.error.needDimensions')}
              </div>
            ) : null}

            <div className="planogram-batch-sec is-gap">
              {edit.result ? t('planogram.spec.batch.result.title') : t('planogram.spec.batch.preview.title')}
              {!edit.result && preview ? <Tag color="green" className="planogram-mini-tag">{t('planogram.spec.batch.preview.latest')}</Tag> : null}
              {!edit.result && edit.previewStale ? <Tag color="orange" className="planogram-mini-tag">{t('planogram.spec.batch.preview.stale')}</Tag> : null}
            </div>
            {edit.previewing ? (
              <div className="planogram-batch-placeholder"><Spin size="small" /> {t('planogram.spec.batch.preview.loading')}</div>
            ) : shown ? (
              <BatchResultPanel result={shown} lookup={lookup} executed={Boolean(edit.result)} />
            ) : (
              <div className="planogram-batch-placeholder">{t('planogram.spec.batch.preview.empty')}</div>
            )}
          </section>
        </div>
      </div>
    </Modal>
  )
}

const HANG_TYPE_KEYS: Record<PlanogramHangHoleType, string> = {
  0: 'planogram.spec.hangType.none',
  1: 'planogram.spec.hangType.round',
  2: 'planogram.spec.hangType.euro',
}
const SOURCE_KEYS: Record<PlanogramSpecSource, string> = {
  1: 'planogram.spec.source.manual',
  2: 'planogram.spec.source.supplier',
  3: 'planogram.spec.source.set',
}

/** 预览 / 执行结果：四张计数卡片 + 改前改后明细 + 跳过明细。 */
function BatchResultPanel({ result, lookup, executed }: {
  result: ProductSpecBatchResultDto
  lookup: ChangeTextLookup
  executed: boolean
}) {
  const { t } = useTranslation()
  const created = executed ? result.created : result.willCreate
  const updated = executed ? result.updated : result.willUpdate
  const unchanged = executed ? result.unchanged : result.willStayUnchanged
  return (
    <div className="planogram-batch-result">
      {executed ? (
        <Alert
          className="planogram-batch-alert"
          type="success"
          showIcon
          message={t('planogram.spec.batch.result.done', { created, updated, unchanged, skipped: result.skipped })}
        />
      ) : null}
      <div className="planogram-batch-tiles">
        <Tile value={created} label={executed ? t('planogram.spec.batch.result.created') : t('planogram.spec.batch.preview.willCreate')} tone="blue" />
        <Tile value={updated} label={executed ? t('planogram.spec.batch.result.updated') : t('planogram.spec.batch.preview.willUpdate')} tone="green" />
        <Tile value={unchanged} label={t('planogram.spec.batch.preview.unchanged')} />
        <Tile value={result.skipped} label={t('planogram.spec.batch.preview.skipped')} tone="orange" />
      </div>
      {!executed && created + updated === 0 ? <div className="planogram-muted-text planogram-small planogram-batch-note">{t('planogram.spec.batch.preview.nothing')}</div> : null}

      {result.changedItems.length > 0 ? (
        <>
          <div className="planogram-batch-caption">
            {t('planogram.spec.batch.changes.title')}
            {result.changedTotal > result.changedItems.length
              ? <span className="planogram-muted-text">{t('planogram.spec.batch.changes.truncated', { total: result.changedTotal, shown: result.changedItems.length })}</span>
              : null}
          </div>
          <div className="planogram-batch-scroll">
            <table className="planogram-batch-table">
              <thead>
                <tr>
                  <th scope="col">{t('planogram.spec.batch.changes.col.item')}</th>
                  <th scope="col">{t('planogram.spec.batch.changes.col.before')}</th>
                  <th scope="col">{t('planogram.spec.batch.changes.col.after')}</th>
                </tr>
              </thead>
              <tbody>
                {result.changedItems.map((item) => {
                  // 每个字段一行；新建的 after 里省掉与缺省值相同的吊孔偏移 / 类型 / 来源（弹窗里已写明缺省）
                  const before = formatChangeLines(item.before, lookup, 'before')
                  const after = formatChangeLines(item.after, lookup, 'after', { omitCreateDefaults: item.action === 'create' })
                  return (
                    <tr key={item.productCode}>
                      <td className="planogram-mono" title={item.productName ?? undefined}>{item.itemNumber || item.productCode}</td>
                      <td className={before.length > 0 ? undefined : 'planogram-muted-text'}>
                        {before.length > 0 ? before.map((line) => <div key={line}>{line}</div>) : t('planogram.spec.batch.changes.unset')}
                      </td>
                      <td>
                        {after.map((line) => <div key={line}>{line}</div>)}
                        {item.action === 'create' ? <div className="planogram-muted-text">{t('planogram.spec.batch.changes.created')}</div> : null}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        </>
      ) : null}

      {result.skippedItems.length > 0 ? (
        <>
          <div className="planogram-batch-caption">
            {t('planogram.spec.batch.skip.title', { count: result.skipped })}
            {skippedItemsTruncated(result)
              ? <span className="planogram-muted-text">{t('planogram.spec.batch.skip.truncated', { shown: result.skippedItems.length })}</span>
              : null}
          </div>
          <div className="planogram-batch-scroll">
            <table className="planogram-batch-table is-skipped">
              <thead>
                <tr>
                  <th scope="col">{t('planogram.spec.batch.changes.col.item')}</th>
                  <th scope="col">{t('planogram.spec.batch.skip.col.name')}</th>
                  <th scope="col">{t('planogram.spec.batch.skip.col.reason')}</th>
                </tr>
              </thead>
              <tbody>
                {result.skippedItems.map((item) => {
                  const key = skipReasonKey(item.reason)
                  return (
                    <tr key={`${item.productCode}:${item.reason}`}>
                      <td className="planogram-mono">{item.itemNumber || item.productCode}</td>
                      <td className="planogram-batch-skipname">{item.productName || EMPTY_CELL}</td>
                      <td className="planogram-batch-reason">{key ? t(key) : item.reason}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        </>
      ) : null}
    </div>
  )
}
