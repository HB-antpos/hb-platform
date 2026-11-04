import { Alert, Empty, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../../components/MeasuredTable'
import { deleteShelfModel, listShelfModels, updateShelfModel } from '../../../../services/planogramService'
import type { ShelfModelDto } from '../../../../types/planogram'
import {
  DEFAULT_PAGE_SIZE,
  formatDimensions,
  formatNumber,
  PAGE_SIZE_OPTIONS,
  planogramErrorText,
  resolvePlanogramError,
  shelfToggleInput,
  type ModelDrawerTarget,
} from '../planogramLogic'
import { ActiveStatus, ModelActions, ModelToolbar, useModelList, useModelMutations } from './modelList'
import ShelfModelDrawer from './ShelfModelDrawer'

interface ShelfModelsProps {
  /** Planogram.Manage：新建 / 编辑 / 复制 / 启停 / 删除；否则只读查看。 */
  canManage: boolean
}

/** 货架型号：列表（搜索 / 状态筛选 / 分页）+ 右侧抽屉编辑。 */
export default function ShelfModels({ canManage }: ShelfModelsProps) {
  const { t, i18n } = useTranslation()
  const list = useModelList<ShelfModelDto>((query, signal) => listShelfModels(query, signal))
  const [drawer, setDrawer] = useState<ModelDrawerTarget<ShelfModelDto> | null>(null)
  const { busyGuid, toggleActive, deleteModel } = useModelMutations<ShelfModelDto>({
    toggle: (model) => updateShelfModel(model.guid, shelfToggleInput(model)),
    remove: (model) => deleteShelfModel(model.guid),
    reload: list.request.reload,
  })

  const columns = useMemo<ColumnsType<ShelfModelDto>>(() => [
    { title: t('planogram.shelf.col.name'), dataIndex: 'name', key: 'name', render: (value: string) => <strong>{value}</strong> },
    { title: t('planogram.shelf.col.code'), dataIndex: 'code', key: 'code', render: (value: string) => <span className="planogram-mono">{value}</span> },
    {
      title: t('planogram.shelf.col.sideType'),
      dataIndex: 'sideType',
      key: 'sideType',
      render: (value: number) => <Tag>{value === 2 ? t('planogram.shelf.sideType.double') : t('planogram.shelf.sideType.single')}</Tag>,
    },
    {
      title: t('planogram.shelf.col.size'),
      key: 'size',
      render: (_: unknown, row) => formatDimensions([row.bayWidthMm, row.totalHeightMm]),
    },
    { title: t('planogram.shelf.col.baseDepth'), key: 'baseDepth', render: (_: unknown, row) => `${formatNumber(row.baseDepthMm)} mm` },
    {
      title: t('planogram.shelf.col.pitch'),
      key: 'pitch',
      render: (_: unknown, row) => `${formatNumber(row.holePitchXMm)} / ${formatNumber(row.holePitchYMm)}`,
    },
    { title: t('planogram.shelf.col.holes'), key: 'holes', render: (_: unknown, row) => `${row.holeCols} × ${row.holeRows}` },
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
        createLabel={canManage ? t('planogram.shelf.create') : undefined}
        onCreate={() => setDrawer({ mode: 'create' })}
      />
      {errorText ? <Alert type="error" showIcon message={errorText} action={<a onClick={list.request.reload}>{t('planogram.common.retry')}</a>} /> : null}
      <MeasuredTable<ShelfModelDto>
        metricId="pos-admin.planogram.shelf-models"
        rowKey="guid"
        size="middle"
        columns={columns}
        dataSource={data?.items ?? []}
        loading={list.request.loading}
        scroll={{ x: 'max-content' }}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('planogram.shelf.empty')} /> }}
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
      <ShelfModelDrawer
        target={drawer}
        readOnly={!canManage}
        onClose={() => setDrawer(null)}
        onSaved={() => {
          setDrawer(null)
          list.request.reload()
        }}
      />
    </div>
  )
}
