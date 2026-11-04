import { ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Button, Input, Modal, Popconfirm, Segmented, Space, Tooltip, message } from 'antd'
import { useCallback, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import StatusPill from '../../../../components/listToolbar/StatusPill'
import type { ModelListQuery, PlanogramPaged } from '../../../../types/planogram'
import {
  DEFAULT_PAGE_SIZE,
  effectivePage,
  planogramErrorText,
  resolvePlanogramError,
  SEARCH_DEBOUNCE_MS,
  type PagingState,
} from '../planogramLogic'
import { useDebouncedValue, usePlanogramRequest, type PlanogramRequestState } from '../usePlanogramRequest'

/** 型号列表行的公共字段（货架 / 挂钩型号都有）。 */
export interface BaseModel {
  guid: string
  code: string
  name: string
  isActive: boolean
  referencedPlanCount: number
}

export type ModelStatusFilter = 'all' | 'active' | 'inactive'

export interface ModelListState<T> {
  keywordDraft: string
  setKeywordDraft: (value: string) => void
  status: ModelStatusFilter
  setStatus: (value: ModelStatusFilter) => void
  page: number
  pageSize: number
  setPaging: (page: number, pageSize: number) => void
  request: PlanogramRequestState<PlanogramPaged<T>>
}

/**
 * 型号列表的查询状态与取数：搜索（防抖）、启用状态筛选、分页；筛选条件一变就回到第 1 页。
 * 默认状态筛选「全部」，停用的型号也要能看到并重新启用。
 */
export function useModelList<T>(
  load: (query: ModelListQuery, signal: AbortSignal) => Promise<PlanogramPaged<T>>,
  enabled = true,
): ModelListState<T> {
  const [keywordDraft, setKeywordDraftState] = useState('')
  const [status, setStatusState] = useState<ModelStatusFilter>('all')
  const keyword = useDebouncedValue(keywordDraft.trim(), SEARCH_DEBOUNCE_MS)
  // 关键字（防抖后）或状态变化 → 有效页码回到第 1 页（见 effectivePage）
  const filterKey = `${keyword}|${status}`
  const [paging, setPagingState] = useState<PagingState>({ page: 1, pageSize: DEFAULT_PAGE_SIZE, filterKey })
  const page = effectivePage(paging, filterKey)

  const query = useMemo<ModelListQuery>(
    () => ({
      keyword: keyword || undefined,
      isActive: status === 'all' ? undefined : status === 'active',
      page,
      pageSize: paging.pageSize,
    }),
    [keyword, page, paging.pageSize, status],
  )
  const request = usePlanogramRequest<PlanogramPaged<T>>(JSON.stringify(query), (signal) => load(query, signal), enabled)

  const setKeywordDraft = useCallback((value: string) => setKeywordDraftState(value), [])
  const setStatus = useCallback((value: ModelStatusFilter) => setStatusState(value), [])
  const setPaging = useCallback(
    (nextPage: number, pageSize: number) => setPagingState({ page: nextPage, pageSize, filterKey }),
    [filterKey],
  )
  return { keywordDraft, setKeywordDraft, status, setStatus, page, pageSize: paging.pageSize, setPaging, request }
}

interface ModelToolbarProps<T> {
  list: ModelListState<T>
  searchPlaceholder: string
  /** 「新建」按钮文案；没有型号维护权限时不传（不显示按钮）。 */
  createLabel?: string
  onCreate?: () => void
}

/** 筛选栏：搜索框、状态筛选、刷新，右侧新建按钮。 */
export function ModelToolbar<T>({ list, searchPlaceholder, createLabel, onCreate }: ModelToolbarProps<T>) {
  const { t } = useTranslation()
  return (
    <div className="planogram-toolbar">
      <Input
        className="planogram-search"
        allowClear
        prefix={<SearchOutlined aria-hidden="true" />}
        placeholder={searchPlaceholder}
        aria-label={searchPlaceholder}
        value={list.keywordDraft}
        onChange={(event) => list.setKeywordDraft(event.target.value)}
      />
      <Segmented<ModelStatusFilter>
        value={list.status}
        onChange={list.setStatus}
        aria-label={t('planogram.common.statusLabel')}
        options={[
          { label: t('planogram.common.statusAll'), value: 'all' },
          { label: t('planogram.common.statusActive'), value: 'active' },
          { label: t('planogram.common.statusInactive'), value: 'inactive' },
        ]}
      />
      <div className="planogram-toolbar-actions">
        <Tooltip title={t('planogram.common.refresh')}>
          <Button
            icon={<ReloadOutlined />}
            aria-label={t('planogram.common.refresh')}
            loading={list.request.loading}
            onClick={list.request.reload}
          />
        </Tooltip>
        {createLabel && onCreate ? (
          <Button type="primary" onClick={onCreate}>{createLabel}</Button>
        ) : null}
      </div>
    </div>
  )
}

/** 启用 / 停用状态胶囊（颜色之外还有文字，不只靠颜色区分）。 */
export function ActiveStatus({ active }: { active: boolean }) {
  const { t } = useTranslation()
  return (
    <StatusPill tone={active ? 'green' : 'gray'}>
      {active ? t('planogram.common.statusActive') : t('planogram.common.statusInactive')}
    </StatusPill>
  )
}

interface ModelMutationOptions<T extends BaseModel> {
  toggle: (model: T) => Promise<unknown>
  remove: (model: T) => Promise<void>
  reload: () => void
  /** 启停 / 删除成功后额外通知（如刷新挂钩型号目录）。 */
  onChanged?: () => void
}

/**
 * 启停与删除：
 * - 停用 / 启用用整条数据 + 翻转 isActive 提交（PUT 是整体保存）；
 * - 删除被方案引用时后端返回 PLANOGRAM_MODEL_IN_USE，改为提示「已被引用，只能停用」，启用中的型号直接给「改为停用」。
 */
export function useModelMutations<T extends BaseModel>({ toggle, remove, reload, onChanged }: ModelMutationOptions<T>) {
  const { t, i18n } = useTranslation()
  const [busyGuid, setBusyGuid] = useState<string | null>(null)

  const showError = useCallback((error: unknown) => {
    const resolved = resolvePlanogramError(error)
    if (resolved.kind === 'abort') return
    message.error(planogramErrorText(resolved, i18n.language, (key) => t(key)))
  }, [i18n.language, t])

  const toggleActive = useCallback(async (model: T) => {
    setBusyGuid(model.guid)
    try {
      await toggle(model)
      message.success(model.isActive ? t('planogram.common.disabledOk') : t('planogram.common.enabledOk'))
      reload()
      onChanged?.()
    } catch (error) {
      showError(error)
    } finally {
      setBusyGuid(null)
    }
  }, [onChanged, reload, showError, t, toggle])

  const deleteModel = useCallback(async (model: T) => {
    setBusyGuid(model.guid)
    try {
      await remove(model)
      message.success(t('planogram.common.deleted'))
      reload()
      onChanged?.()
    } catch (error) {
      const resolved = resolvePlanogramError(error)
      if (resolved.kind === 'error' && resolved.code === 'PLANOGRAM_MODEL_IN_USE') {
        if (model.isActive) {
          Modal.confirm({
            title: t('planogram.common.inUse.title'),
            content: t('planogram.common.inUse.content', { name: model.name }),
            okText: t('planogram.common.inUse.disable'),
            cancelText: t('planogram.common.cancel'),
            onOk: () => toggleActive(model),
          })
        } else {
          message.warning(t('planogram.common.inUse.alreadyDisabled', { name: model.name }))
        }
      } else {
        showError(error)
      }
    } finally {
      setBusyGuid(null)
    }
  }, [onChanged, reload, remove, showError, t, toggleActive])

  return { busyGuid, toggleActive, deleteModel }
}

interface ModelActionsProps<T extends BaseModel> {
  model: T
  canManage: boolean
  busy: boolean
  onOpen: (model: T) => void
  onCopy: (model: T) => void
  onToggle: (model: T) => void
  onDelete: (model: T) => void
}

/** 行操作：有型号维护权限 = 编辑 / 复制 / 停用·启用 / 删除；只读账号只有「查看」。 */
export function ModelActions<T extends BaseModel>({ model, canManage, busy, onOpen, onCopy, onToggle, onDelete }: ModelActionsProps<T>) {
  const { t } = useTranslation()
  if (!canManage) {
    return (
      <Button type="link" size="small" onClick={() => onOpen(model)}>
        {t('planogram.common.view')}
      </Button>
    )
  }
  const inUse = model.referencedPlanCount > 0
  return (
    <Space size={2} wrap={false} className="planogram-row-actions">
      <Button type="link" size="small" onClick={() => onOpen(model)}>{t('planogram.common.edit')}</Button>
      <Button type="link" size="small" onClick={() => onCopy(model)}>{t('planogram.common.copy')}</Button>
      {model.isActive ? (
        <Popconfirm
          title={t('planogram.common.disableConfirm', { name: model.name })}
          okText={t('planogram.common.disable')}
          cancelText={t('planogram.common.cancel')}
          onConfirm={() => onToggle(model)}
        >
          <Button type="link" size="small" danger loading={busy}>{t('planogram.common.disable')}</Button>
        </Popconfirm>
      ) : (
        <Button type="link" size="small" loading={busy} onClick={() => onToggle(model)}>{t('planogram.common.enable')}</Button>
      )}
      {inUse ? (
        // 已被方案引用：直接禁用并说明原因，只能停用
        <Tooltip title={t('planogram.common.deleteBlocked', { count: model.referencedPlanCount })}>
          <Button type="link" size="small" danger disabled>{t('planogram.common.delete')}</Button>
        </Tooltip>
      ) : (
        <Popconfirm
          title={t('planogram.common.deleteConfirm', { name: model.name })}
          okText={t('planogram.common.delete')}
          okButtonProps={{ danger: true }}
          cancelText={t('planogram.common.cancel')}
          onConfirm={() => onDelete(model)}
        >
          <Button type="link" size="small" danger disabled={busy}>{t('planogram.common.delete')}</Button>
        </Popconfirm>
      )}
    </Space>
  )
}
