import { EllipsisOutlined, ImportOutlined, PlusOutlined, ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Alert, Button, Dropdown, Empty, Input, Modal, Segmented, Select, Space, Tag, Tooltip, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type ChangeEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { MeasuredTable } from '../../../../components/MeasuredTable'
import StatusPill, { type StatusPillTone } from '../../../../components/listToolbar/StatusPill'
import { archivePlan, deletePlan, duplicatePlan, listPlans, listShelfModels, restorePlan } from '../../../../services/planogramService'
import type { PlanogramExportDto, PlanogramListItemDto, PlanogramListQuery, PlanogramPaged, PlanogramStatus } from '../../../../types/planogram'
import {
  DEFAULT_PAGE_SIZE,
  effectivePage,
  EMPTY_CELL,
  formatUpdatedAt,
  PAGE_SIZE_OPTIONS,
  planogramDesignerPath,
  planogramErrorText,
  resolvePlanogramError,
  SEARCH_DEBOUNCE_MS,
  type PagingState,
} from '../planogramLogic'
import { useDebouncedValue, usePlanogramRequest } from '../usePlanogramRequest'
import { MAX_IMPORT_BYTES, parseImportFile } from '../transferLogic'
import ImportPlanModal from './ImportPlanModal'
import NewPlanModal from './NewPlanModal'
import { defaultCopyName, planRowActions, planStatusView, statusFilterToQuery, type PlanStatusFilter } from './planListLogic'

// 导出 PDF 弹窗（含 PDF 页面与版面图打印模式）是低频功能：独立异步块，方案列表首次打开时不加载
const ExportPdfModal = lazy(() => import('../export/ExportPdfModal'))

const STATUS_TONES: Record<PlanogramStatus, StatusPillTone> = { 0: 'orange', 1: 'green', 2: 'gray' }

interface PlanListProps {
  /** Planogram.View：导出 PDF（含已归档方案）。 */
  canView?: boolean
  /** Planogram.Edit：新建 / 复制 / 打开编辑 / 导入方案 JSON。 */
  canEdit: boolean
  /** Planogram.Manage：归档 / 恢复 / 删除。 */
  canManage: boolean
  /** 每变一次就重新取一遍列表（从设计器回到本页时刷新，保存后版本 / 统计才是最新的）。 */
  refreshSignal: number
}

/** 陈列方案列表：搜索（方案名称 / 商品货号）、货架型号与状态筛选（不绑定分店）、分页、新建 / 打开 / 复制 / 归档 / 恢复 / 删除。 */
export default function PlanList({ canView = true, canEdit, canManage, refreshSignal }: PlanListProps) {
  const { t, i18n } = useTranslation()
  const navigate = useNavigate()
  const [keywordDraft, setKeywordDraft] = useState('')
  const [shelfGuid, setShelfGuid] = useState<string | undefined>(undefined)
  const [status, setStatus] = useState<PlanStatusFilter>('all')
  const keyword = useDebouncedValue(keywordDraft.trim(), SEARCH_DEBOUNCE_MS)
  // 任一筛选条件变化 → 有效页码回到第 1 页（见 effectivePage）
  const filterKey = `${keyword}|${shelfGuid ?? ''}|${status}`
  const [paging, setPaging] = useState<PagingState>({ page: 1, pageSize: DEFAULT_PAGE_SIZE, filterKey })
  const page = effectivePage(paging, filterKey)

  const query = useMemo<PlanogramListQuery>(
    () => ({ keyword: keyword || undefined, shelfModelGuid: shelfGuid, status: statusFilterToQuery(status), page, pageSize: paging.pageSize }),
    [keyword, page, paging.pageSize, shelfGuid, status],
  )
  const request = usePlanogramRequest<PlanogramPaged<PlanogramListItemDto>>(JSON.stringify(query), (signal) => listPlans(query, signal))

  // 货架型号目录：筛选下拉用全部（含停用，已有方案可能还在用），新建弹窗只用启用的
  const shelfRequest = usePlanogramRequest('plans-shelf-models', (signal) => listShelfModels({ page: 1, pageSize: 100 }, signal))

  // 从设计器回来、或从别的页签切回「陈列方案」：方案列表与货架型号目录一起刷新一次（跳过首次渲染）。
  // 联调发现：货架型号是在同一页面的「货架与挂钩型号」页签里新建的，这里只在首次挂载时取过目录，
  // 页面又被保活，不刷新的话新建方案弹窗会一直提示「还没有启用的货架型号」。
  const firstSignal = useRef(refreshSignal)
  const { reload } = request
  const { reload: reloadShelfModels } = shelfRequest
  useEffect(() => {
    if (refreshSignal === firstSignal.current) return
    reload()
    reloadShelfModels()
  }, [refreshSignal, reload, reloadShelfModels])
  const shelfModels = shelfRequest.data?.items ?? []
  const activeShelfModels = useMemo(() => shelfModels.filter((model) => model.isActive), [shelfModels])

  const [busyGuid, setBusyGuid] = useState<string | null>(null)
  const [newModalKey, setNewModalKey] = useState<number | null>(null)
  const newModalCounter = useRef(0)

  // 导出 PDF / 导入 JSON
  const [exportTarget, setExportTarget] = useState<{ guid: string; name: string } | null>(null)
  const [importState, setImportState] = useState<{ file: PlanogramExportDto; fileName: string } | null>(null)
  const fileInputRef = useRef<HTMLInputElement | null>(null)

  const [copyTarget, setCopyTarget] = useState<PlanogramListItemDto | null>(null)
  const [copyName, setCopyName] = useState('')
  const [copyBusy, setCopyBusy] = useState(false)

  const showError = useCallback((error: unknown) => {
    const resolved = resolvePlanogramError(error)
    if (resolved.kind === 'abort') return
    message.error(planogramErrorText(resolved, i18n.language, (key) => t(key)))
  }, [i18n.language, t])

  const openPlan = useCallback((plan: PlanogramListItemDto) => navigate(planogramDesignerPath(plan.guid)), [navigate])

  /** 选好 JSON 文件：先在本地检查大小 / 是不是 JSON / 像不像导出文件，明确提示原因；通过后打开导入弹窗。 */
  const handleImportFile = async (event: ChangeEvent<HTMLInputElement>) => {
    const input = event.target
    const picked = input.files?.[0]
    // 同一个文件再次选择也要能触发 change：读完立刻清空 input 的值
    input.value = ''
    if (!picked) return
    if (picked.size > MAX_IMPORT_BYTES) {
      message.error(t('planogram.transfer.errors.tooLarge', { size: Math.round(MAX_IMPORT_BYTES / 1024 / 1024) }))
      return
    }
    let content: string
    try {
      content = await picked.text()
    } catch {
      message.error(t('planogram.transfer.errors.unreadable'))
      return
    }
    const parsed = parseImportFile(content, picked.size)
    if (!parsed.ok) {
      const key = {
        tooLarge: 'planogram.transfer.errors.tooLarge',
        empty: 'planogram.transfer.errors.empty',
        notJson: 'planogram.transfer.errors.notJson',
        notPlanogram: 'planogram.transfer.errors.notPlanogram',
      }[parsed.reason]
      message.error(t(key, { size: Math.round(MAX_IMPORT_BYTES / 1024 / 1024) }))
      return
    }
    setImportState({ file: parsed.file, fileName: picked.name })
  }

  const runMutation = useCallback(async (plan: PlanogramListItemDto, action: () => Promise<unknown>, successKey: string) => {
    setBusyGuid(plan.guid)
    try {
      await action()
      message.success(t(successKey))
      reload()
    } catch (error) {
      showError(error)
    } finally {
      setBusyGuid(null)
    }
  }, [reload, showError, t])

  const submitCopy = async () => {
    const target = copyTarget
    const name = copyName.trim()
    if (!target || !name || copyBusy) return
    setCopyBusy(true)
    try {
      await duplicatePlan(target.guid, name)
      message.success(t('planogram.plans.copied', { name }))
      setCopyTarget(null)
      reload()
    } catch (error) {
      showError(error)
    } finally {
      setCopyBusy(false)
    }
  }

  const columns = useMemo<ColumnsType<PlanogramListItemDto>>(() => [
    {
      title: t('planogram.plans.col.plan'),
      key: 'plan',
      render: (_: unknown, row) => (
        <div>
          <div className="planogram-plan-name">{row.name}</div>
          {row.notes ? <div className="planogram-muted-text planogram-small planogram-clamp">{row.notes}</div> : null}
        </div>
      ),
    },
    {
      title: t('planogram.plans.col.shelf'),
      key: 'shelf',
      render: (_: unknown, row) => (
        <div>
          <div>{row.shelfModelName || EMPTY_CELL}</div>
          <div className="planogram-muted-text planogram-small">{t('planogram.designer.bays', { count: row.bayCount })}</div>
        </div>
      ),
    },
    {
      title: <Tooltip title={t('planogram.plans.col.countsTip')}><span>{t('planogram.plans.col.counts')}</span></Tooltip>,
      key: 'counts',
      render: (_: unknown, row) => <span className="planogram-mono">{`${row.skuCount} / ${row.itemCount} / ${row.unitCount.toLocaleString()}`}</span>,
    },
    {
      title: t('planogram.plans.col.fill'),
      key: 'fill',
      align: 'right',
      render: (_: unknown, row) => <span className="planogram-mono">{`${row.fillPercent}%`}</span>,
    },
    {
      title: t('planogram.plans.col.status'),
      key: 'status',
      render: (_: unknown, row) => {
        const status = planStatusView(row)
        return (
          <Space size={4} wrap>
            <StatusPill tone={STATUS_TONES[row.status]}>{t(status.shortKey)}</StatusPill>
            {/* 已发布方案被保存后回到草稿：提示有未发布的修改，并说明对外的是哪个已发布版本 */}
            {status.hasUnpublishedChanges ? <Tag color="orange" className="planogram-mini-tag">{t('planogram.plans.unpublishedTag', { version: status.version ?? 0 })}</Tag> : null}
            {row.conflictCount > 0 ? <Tag color="red" className="planogram-mini-tag">{t('planogram.plans.conflictTag', { count: row.conflictCount })}</Tag> : null}
          </Space>
        )
      },
    },
    {
      title: t('planogram.plans.col.version'),
      key: 'version',
      render: (_: unknown, row) => <span className="planogram-mono">{`v${row.version}`}</span>,
    },
    {
      title: t('planogram.plans.col.updated'),
      key: 'updated',
      render: (_: unknown, row) => (
        <span className="planogram-muted-text planogram-small">
          {formatUpdatedAt(row.updatedAt)}
          <br />
          {row.updatedBy || EMPTY_CELL}
        </span>
      ),
    },
    {
      title: t('planogram.common.col.actions'),
      key: 'actions',
      fixed: 'right',
      render: (_: unknown, row) => {
        const actions = planRowActions(row, { canEdit, canManage, canView })
        const busy = busyGuid === row.guid
        const more = [
          actions.canArchive ? { key: 'archive', label: t('planogram.plans.archive') } : null,
          actions.canRestore ? { key: 'restore', label: t('planogram.plans.restore') } : null,
          actions.canDelete ? { key: 'delete', label: t('planogram.common.delete'), danger: true } : null,
        ].filter((item): item is NonNullable<typeof item> => item !== null)
        return (
          <Space size={2} wrap={false} className="planogram-row-actions">
            <Button type="link" size="small" onClick={() => openPlan(row)}>
              {actions.primary === 'open' ? t('planogram.plans.open') : t('planogram.common.view')}
            </Button>
            {actions.canDuplicate ? (
              <Button type="link" size="small" onClick={() => { setCopyTarget(row); setCopyName(defaultCopyName(row.name, t('planogram.common.copySuffix'))) }}>
                {t('planogram.common.copy')}
              </Button>
            ) : null}
            {actions.canExportPdf ? (
              <Button type="link" size="small" onClick={() => setExportTarget({ guid: row.guid, name: row.name })}>
                {t('planogram.export.listAction')}
              </Button>
            ) : null}
            {actions.canRestore ? (
              <Button type="link" size="small" loading={busy} onClick={() => void runMutation(row, () => restorePlan(row.guid), 'planogram.plans.restored')}>
                {t('planogram.plans.restore')}
              </Button>
            ) : null}
            {more.some((item) => item.key !== 'restore') ? (
              <Dropdown
                trigger={['click']}
                menu={{
                  items: more.filter((item) => item.key !== 'restore'),
                  onClick: ({ key }) => {
                    if (key === 'archive') {
                      Modal.confirm({
                        title: t('planogram.plans.archiveConfirm', { name: row.name }),
                        content: t('planogram.plans.archiveHint'),
                        okText: t('planogram.plans.archive'),
                        cancelText: t('planogram.common.cancel'),
                        onOk: () => runMutation(row, () => archivePlan(row.guid), 'planogram.plans.archived'),
                      })
                    } else if (key === 'delete') {
                      Modal.confirm({
                        title: t('planogram.plans.deleteConfirm', { name: row.name }),
                        content: t('planogram.plans.deleteHint'),
                        okText: t('planogram.common.delete'),
                        okButtonProps: { danger: true },
                        cancelText: t('planogram.common.cancel'),
                        onOk: () => runMutation(row, () => deletePlan(row.guid), 'planogram.common.deleted'),
                      })
                    }
                  },
                }}
              >
                <Button type="link" size="small" icon={<EllipsisOutlined />} aria-label={t('planogram.plans.more')} loading={busy} />
              </Dropdown>
            ) : null}
          </Space>
        )
      },
    },
  ], [busyGuid, canEdit, canManage, canView, openPlan, runMutation, t])

  const data = request.data
  const errorText = useMemo(() => {
    if (!request.error) return null
    const resolved = resolvePlanogramError(request.error)
    return resolved.kind === 'error' ? planogramErrorText(resolved, i18n.language, (key) => t(key)) : null
  }, [i18n.language, request.error, t])

  return (
    <div className="planogram-pane">
      <div className="planogram-toolbar">
        <Input
          className="planogram-search is-wide"
          allowClear
          prefix={<SearchOutlined aria-hidden="true" />}
          placeholder={t('planogram.plans.searchPlaceholder')}
          aria-label={t('planogram.plans.searchPlaceholder')}
          value={keywordDraft}
          onChange={(event) => setKeywordDraft(event.target.value)}
        />
        <Select
          className="planogram-plan-shelf-filter"
          allowClear
          showSearch
          optionFilterProp="label"
          value={shelfGuid}
          onChange={(value) => setShelfGuid(value)}
          placeholder={t('planogram.plans.shelfFilter')}
          aria-label={t('planogram.plans.shelfFilter')}
          loading={shelfRequest.loading}
          options={shelfModels.map((model) => ({ value: model.guid, label: model.name }))}
        />
        <Segmented<PlanStatusFilter>
          value={status}
          onChange={setStatus}
          aria-label={t('planogram.common.statusLabel')}
          options={[
            { label: t('planogram.common.statusAll'), value: 'all' },
            { label: t('planogram.plans.status.draft'), value: 'draft' },
            { label: t('planogram.plans.status.published'), value: 'published' },
            { label: t('planogram.plans.status.archived'), value: 'archived' },
          ]}
        />
        <div className="planogram-toolbar-actions">
          <Tooltip title={t('planogram.common.refresh')}>
            <Button icon={<ReloadOutlined />} aria-label={t('planogram.common.refresh')} loading={request.loading} onClick={request.reload} />
          </Tooltip>
          {canEdit ? (
            <>
              <input
                ref={fileInputRef}
                type="file"
                accept=".json,application/json"
                hidden
                aria-label={t('planogram.transfer.import')}
                onChange={(event) => void handleImportFile(event)}
              />
              <Button icon={<ImportOutlined />} onClick={() => fileInputRef.current?.click()}>
                {t('planogram.transfer.import')}
              </Button>
            </>
          ) : null}
          {canEdit ? (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              onClick={() => {
                reloadShelfModels()
                newModalCounter.current += 1
                setNewModalKey(newModalCounter.current)
              }}
            >
              {t('planogram.plans.create')}
            </Button>
          ) : null}
        </div>
      </div>
      {errorText ? <Alert type="error" showIcon message={errorText} action={<a onClick={request.reload}>{t('planogram.common.retry')}</a>} /> : null}
      <MeasuredTable<PlanogramListItemDto>
        metricId="pos-admin.planogram.plans"
        rowKey="guid"
        size="middle"
        columns={columns}
        dataSource={data?.items ?? []}
        loading={request.loading}
        scroll={{ x: 'max-content' }}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={canEdit ? t('planogram.plans.emptyCanCreate') : t('planogram.plans.empty')} /> }}
        pagination={{
          current: page,
          pageSize: paging.pageSize,
          total: data?.total ?? 0,
          showSizeChanger: true,
          pageSizeOptions: PAGE_SIZE_OPTIONS,
          showTotal: (total) => t('planogram.common.total', { total }),
          onChange: (nextPage, pageSize) => setPaging({ page: nextPage, pageSize, filterKey }),
        }}
      />
      {exportTarget ? (
        <Suspense fallback={null}>
          <ExportPdfModal planGuid={exportTarget.guid} planName={exportTarget.name} onClose={() => setExportTarget(null)} />
        </Suspense>
      ) : null}
      {importState ? (
        <ImportPlanModal
          file={importState.file}
          fileName={importState.fileName}
          onClose={() => setImportState(null)}
          onImported={(detail) => {
            setImportState(null)
            navigate(planogramDesignerPath(detail.header.guid))
          }}
        />
      ) : null}
      {newModalKey !== null ? (
        <NewPlanModal
          key={newModalKey}
          open
          shelfModels={activeShelfModels}
          shelfModelsLoading={shelfRequest.loading}
          onClose={() => setNewModalKey(null)}
          onCreated={(detail) => {
            setNewModalKey(null)
            navigate(planogramDesignerPath(detail.header.guid))
          }}
        />
      ) : null}
      <Modal
        open={copyTarget !== null}
        title={t('planogram.plans.copyTitle')}
        okText={t('planogram.common.copy')}
        cancelText={t('planogram.common.cancel')}
        confirmLoading={copyBusy}
        okButtonProps={{ disabled: !copyName.trim() }}
        onOk={() => void submitCopy()}
        onCancel={() => setCopyTarget(null)}
        destroyOnHidden
        maskClosable={false}
      >
        <div className="planogram-field-note">{t('planogram.plans.copyHint', { name: copyTarget?.name ?? '' })}</div>
        <Input
          autoFocus
          value={copyName}
          maxLength={100}
          aria-label={t('planogram.plans.newModal.name')}
          onChange={(event) => setCopyName(event.target.value)}
          onPressEnter={() => void submitCopy()}
        />
      </Modal>
    </div>
  )
}
