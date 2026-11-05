import { Alert, Empty, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../../components/MeasuredTable'
import { deleteHookModel, listHookModels, updateHookModel } from '../../../../services/planogramService'
import type { HookModelDto } from '../../../../types/planogram'
import { formatLimitKg } from '../engine/hookQty'
import {
  DEFAULT_PAGE_SIZE,
  EMPTY_CELL,
  formatDimensions,
  formatNumber,
  hookToggleInput,
  PAGE_SIZE_OPTIONS,
  planogramErrorText,
  resolvePlanogramError,
  type ModelDrawerTarget,
} from '../planogramLogic'
import HookModelDrawer from './HookModelDrawer'
import { ActiveStatus, ModelActions, ModelToolbar, useModelList, useModelMutations } from './modelList'

interface HookModelsProps {
  canManage: boolean
  /** 挂钩型号有增删改 / 启停后通知（商品参数页的挂钩目录要跟着刷新）。 */
  onChanged: () => void
}

/** 挂钩型号：列表（搜索 / 状态筛选 / 分页）+ 右侧抽屉编辑。 */
export default function HookModels({ canManage, onChanged }: HookModelsProps) {
  const { t, i18n } = useTranslation()
  const list = useModelList<HookModelDto>((query, signal) => listHookModels(query, signal))
  const [drawer, setDrawer] = useState<ModelDrawerTarget<HookModelDto> | null>(null)
  const { busyGuid, toggleActive, deleteModel } = useModelMutations<HookModelDto>({
    toggle: (model) => updateHookModel(model.guid, hookToggleInput(model)),
    remove: (model) => deleteHookModel(model.guid),
    reload: list.request.reload,
    onChanged,
  })

  const columns = useMemo<ColumnsType<HookModelDto>>(() => [
    { title: t('planogram.hook.col.name'), dataIndex: 'name', key: 'name', render: (value: string) => <strong>{value}</strong> },
    { title: t('planogram.hook.col.code'), dataIndex: 'code', key: 'code', render: (value: string) => <span className="planogram-mono">{value}</span> },
    {
      title: t('planogram.hook.col.prong'),
      dataIndex: 'prongCount',
      key: 'prong',
      render: (value: number) => <Tag>{value === 1 ? t('planogram.hook.prong.single') : t('planogram.hook.prong.double')}</Tag>,
    },
    { title: t('planogram.hook.col.arm'), key: 'arm', render: (_: unknown, row) => `${formatNumber(row.armLengthMm)} mm` },
    // 钩高 H = 挂货臂相对挂孔的下落，单钩 / 双钩都参与商品定位，所以一律显示（设计图里单钩示例显示「—」，这里以数据为准）
    { title: t('planogram.hook.col.drop'), key: 'drop', render: (_: unknown, row) => `${formatNumber(row.dropMm)} mm` },
    {
      title: t('planogram.hook.col.tag'),
      key: 'tag',
      render: (_: unknown, row) => (row.hasPriceTagArm ? formatDimensions([row.tagWidthMm, row.tagHeightMm], '') : t('planogram.hook.tagArm.none')),
    },
    { title: t('planogram.hook.col.span'), key: 'span', render: (_: unknown, row) => `${row.holeSpanX} × ${row.holeSpanY}` },
    {
      title: t('planogram.hook.col.load'),
      key: 'load',
      render: (_: unknown, row) => (row.loadKg ? `${formatLimitKg(row.loadKg * 1000)} kg` : EMPTY_CELL),
    },
    { title: t('planogram.common.col.referenced'), dataIndex: 'referencedPlanCount', key: 'referenced', align: 'right' },
    { title: t('planogram.common.col.status'), key: 'status', render: (_: unknown, row) => <ActiveStatus active={row.isActive} /> },
    {
      title: t('planogram.common.col.actions'),
      key: 'actions',
      fixed: 'right',
      render: (_: unknown, row) => (
        <ModelActions
          model={row}
          canManage={canManage}
          busy={busyGuid === row.guid}
          onOpen={(model) => setDrawer({ mode: 'edit', model })}
          onCopy={(model) => setDrawer({ mode: 'copy', model })}
          onToggle={toggleActive}
          onDelete={deleteModel}
        />
      ),
    },
  ], [busyGuid, canManage, deleteModel, t, toggleActive])

  const data = list.request.data
  const errorText = useMemo(() => {
    if (!list.request.error) return null
    const resolved = resolvePlanogramError(list.request.error)
    return resolved.kind === 'error' ? planogramErrorText(resolved, i18n.language, (key) => t(key)) : null
  }, [i18n.language, list.request.error, t])

  return (
    <div className="planogram-pane">
      <ModelToolbar
        list={list}
        searchPlaceholder={t('planogram.common.searchModels')}
        createLabel={canManage ? t('planogram.hook.create') : undefined}
        onCreate={() => setDrawer({ mode: 'create' })}
      />
      {errorText ? <Alert type="error" showIcon message={errorText} action={<a onClick={list.request.reload}>{t('planogram.common.retry')}</a>} /> : null}
      <MeasuredTable<HookModelDto>
        metricId="pos-admin.planogram.hook-models"
        rowKey="guid"
        size="middle"
        columns={columns}
        dataSource={data?.items ?? []}
        loading={list.request.loading}
        scroll={{ x: 'max-content' }}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('planogram.hook.empty')} /> }}
        pagination={{
          current: list.page,
          pageSize: list.pageSize || DEFAULT_PAGE_SIZE,
          total: data?.total ?? 0,
          showSizeChanger: true,
          pageSizeOptions: PAGE_SIZE_OPTIONS,
          showTotal: (total) => t('planogram.common.total', { total }),
          onChange: (page, pageSize) => list.setPaging(page, pageSize),
        }}
      />
      <HookModelDrawer
        target={drawer}
        readOnly={!canManage}
        onClose={() => setDrawer(null)}
        onSaved={() => {
          setDrawer(null)
          list.request.reload()
          onChanged()
        }}
      />
    </div>
  )
}
