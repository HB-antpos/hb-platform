import { ExclamationCircleOutlined, ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Input, Segmented, Tag, Tooltip } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../../components/MeasuredTable'
import ProductListImage from '../../../../components/ProductListImage'
import { listProductSpecs } from '../../../../services/planogramService'
import type {
  HookModelDto,
  PlanogramPaged,
  ProductSpecListItemDto,
  ProductSpecListQuery,
  ProductSpecStatusFilter,
} from '../../../../types/planogram'
import {
  DEFAULT_PAGE_SIZE,
  effectivePage,
  EMPTY_CELL,
  formatDimensions,
  formatNumber,
  formatUpdatedAt,
  listHookQty,
  PAGE_SIZE_OPTIONS,
  planogramErrorText,
  resolvePlanogramError,
  SEARCH_DEBOUNCE_MS,
  type PagingState,
} from '../planogramLogic'
import { useDebouncedValue, usePlanogramRequest } from '../usePlanogramRequest'
import ProductSpecDrawer from './ProductSpecDrawer'

interface ProductSpecsProps {
  /** Planogram.Edit：补充 / 修改参数；否则只读查看。 */
  canEdit: boolean
  /** Planogram.Manage：清除参数。 */
  canManage: boolean
  /** 全部挂钩型号（含停用），由页面统一加载。 */
  hooks: HookModelDto[]
  hooksReady: boolean
  hooksFailed: boolean
  onReloadHooks: () => void
}

/** 未设参数的行把「包装 … 每钩挂数」五列并成一格，显示一句提示。 */
const UNSET_SPAN = 5

/** 商品陈列参数：列表（搜索 / 已设·未设 / 分页，带缩略图）+ 右侧抽屉编辑。 */
export default function ProductSpecs({ canEdit, canManage, hooks, hooksReady, hooksFailed, onReloadHooks }: ProductSpecsProps) {
  const { t, i18n } = useTranslation()
  const [keywordDraft, setKeywordDraft] = useState('')
  const [status, setStatus] = useState<ProductSpecStatusFilter>('all')
  const [drawer, setDrawer] = useState<ProductSpecListItemDto | null>(null)
  const keyword = useDebouncedValue(keywordDraft.trim(), SEARCH_DEBOUNCE_MS)
  // 关键字（防抖后）或状态变化 → 有效页码回到第 1 页（见 effectivePage）
  const filterKey = `${keyword}|${status}`
  const [paging, setPaging] = useState<PagingState>({ page: 1, pageSize: DEFAULT_PAGE_SIZE, filterKey })
  const page = effectivePage(paging, filterKey)

  const query = useMemo<ProductSpecListQuery>(
    () => ({ keyword: keyword || undefined, status, page, pageSize: paging.pageSize }),
    [keyword, page, paging.pageSize, status],
  )
  const request = usePlanogramRequest<PlanogramPaged<ProductSpecListItemDto>>(
    JSON.stringify(query),
    (signal) => listProductSpecs(query, signal),
  )

  const hooksByGuid = useMemo(() => new Map(hooks.map((hook) => [hook.guid, hook])), [hooks])

  const openDrawer = useCallback((item: ProductSpecListItemDto) => {
    // 打开时顺手刷新挂钩型号目录：型号可能刚在另一个页签改过
    onReloadHooks()
    // 复制一份：同一行再次打开也是新的 target 对象，抽屉表单据此重建
    setDrawer({ ...item })
  }, [onReloadHooks])

  const columns = useMemo<ColumnsType<ProductSpecListItemDto>>(() => {
    // 未设参数的行：第一个并格列显示提示，后面四列让位
    const spanCell = (index: number) => (row: ProductSpecListItemDto) => ({
      colSpan: row.hasSpec ? 1 : index === 0 ? UNSET_SPAN : 0,
    })
    const quantity = (item: ProductSpecListItemDto) => {
      const resolved = listHookQty(item, hooksByGuid)
      if (!resolved) return <span className="planogram-muted-text">{EMPTY_CELL}</span>
      const { result } = resolved
      const effective = result.effective > 0 ? result.effective : EMPTY_CELL
      if (result.source === 'override') {
        return (
          <span>
            <strong>{effective}</strong>
            <Tag color="gold" className="planogram-mini-tag">{t('planogram.spec.manualTag')}</Tag>
            {result.auto !== result.effective ? (
              <span className="planogram-muted-text">{t('planogram.spec.autoValue', { value: result.auto })}</span>
            ) : null}
          </span>
        )
      }
      return <span><strong>{effective}</strong> <span className="planogram-muted-text">{t('planogram.spec.autoTag')}</span></span>
    }
    return [
      {
        title: t('planogram.spec.col.image'),
        key: 'image',
        width: 56,
        render: (_: unknown, row) => (
          row.productImage
            ? <ProductListImage src={row.productImage} size={36} radius={6} />
            : <div className="planogram-thumb">{t('planogram.spec.noImage')}</div>
        ),
      },
      {
        title: t('planogram.spec.col.code'),
        key: 'code',
        render: (_: unknown, row) => (
          <div>
            <div className="planogram-mono">{row.productCode}</div>
            <div className="planogram-mono planogram-muted-text planogram-small">
              {[row.itemNumber && row.itemNumber !== row.productCode ? row.itemNumber : null, row.barcode].filter(Boolean).join(' · ') || EMPTY_CELL}
            </div>
          </div>
        ),
      },
      {
        title: t('planogram.spec.col.name'),
        key: 'name',
        render: (_: unknown, row) => (
          <div>
            <div>{row.productName}</div>
            {row.englishName ? <div className="planogram-muted-text planogram-small">{row.englishName}</div> : null}
          </div>
        ),
      },
      {
        title: t('planogram.spec.col.size'),
        key: 'size',
        onCell: spanCell(0),
        render: (_: unknown, row) => (
          row.hasSpec && row.spec
            ? formatDimensions([row.spec.widthMm, row.spec.heightMm, row.spec.depthMm])
            : (
              <span className="planogram-unset-text">
                <ExclamationCircleOutlined aria-hidden="true" /> {t('planogram.spec.unsetRow')}
              </span>
            )
        ),
      },
      {
        title: t('planogram.spec.col.hangOffset'),
        key: 'hangOffset',
        onCell: spanCell(1),
        render: (_: unknown, row) => (row.spec ? `${formatNumber(row.spec.hangHoleOffsetMm)} mm` : null),
      },
      {
        title: t('planogram.spec.col.weight'),
        key: 'weight',
        onCell: spanCell(2),
        render: (_: unknown, row) => (row.spec?.unitWeightG ? `${row.spec.unitWeightG} g` : EMPTY_CELL),
      },
      {
        title: t('planogram.spec.col.defaultHook'),
        key: 'defaultHook',
        onCell: spanCell(3),
        render: (_: unknown, row) => (row.spec?.defaultHookGuid ? hooksByGuid.get(row.spec.defaultHookGuid)?.name ?? EMPTY_CELL : EMPTY_CELL),
      },
      {
        title: t('planogram.spec.col.qty'),
        key: 'qty',
        onCell: spanCell(4),
        render: (_: unknown, row) => quantity(row),
      },
      { title: t('planogram.common.col.referenced'), dataIndex: 'referencedPlanCount', key: 'referenced', align: 'right' },
      {
        title: t('planogram.spec.col.updated'),
        key: 'updated',
        render: (_: unknown, row) => <span className="planogram-muted-text planogram-small">{row.hasSpec ? formatUpdatedAt(row.updatedAt) : EMPTY_CELL}</span>,
      },
      {
        title: t('planogram.common.col.actions'),
        key: 'actions',
        fixed: 'right',
        render: (_: unknown, row) => {
          if (!row.hasSpec) {
            // 未设参数：有编辑权限才给「补充参数」；只读账号没有可做的事
            return canEdit
              ? <Button type="link" size="small" onClick={() => openDrawer(row)}>{t('planogram.spec.addSpec')}</Button>
              : <span className="planogram-muted-text">{EMPTY_CELL}</span>
          }
          return (
            <Button type="link" size="small" onClick={() => openDrawer(row)}>
              {canEdit ? t('planogram.common.edit') : t('planogram.common.view')}
            </Button>
          )
        },
      },
    ]
  }, [canEdit, hooksByGuid, openDrawer, t])

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
          placeholder={t('planogram.spec.searchPlaceholder')}
          aria-label={t('planogram.spec.searchPlaceholder')}
          value={keywordDraft}
          onChange={(event) => setKeywordDraft(event.target.value)}
        />
        <Segmented<ProductSpecStatusFilter>
          value={status}
          onChange={setStatus}
          aria-label={t('planogram.spec.statusLabel')}
          options={[
            { label: t('planogram.spec.status.all'), value: 'all' },
            { label: t('planogram.spec.status.set'), value: 'set' },
            { label: t('planogram.spec.status.unset'), value: 'unset' },
          ]}
        />
        <div className="planogram-toolbar-actions">
          <Tooltip title={t('planogram.common.refresh')}>
            <Button icon={<ReloadOutlined />} aria-label={t('planogram.common.refresh')} loading={request.loading} onClick={request.reload} />
          </Tooltip>
        </div>
      </div>
      {errorText ? <Alert type="error" showIcon message={errorText} action={<a onClick={request.reload}>{t('planogram.common.retry')}</a>} /> : null}
      <MeasuredTable<ProductSpecListItemDto>
        metricId="pos-admin.planogram.product-specs"
        rowKey="productCode"
        size="middle"
        columns={columns}
        dataSource={data?.items ?? []}
        loading={request.loading}
        scroll={{ x: 'max-content' }}
        rowClassName={(row) => (row.hasSpec ? '' : 'planogram-row-unset')}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('planogram.spec.empty')} /> }}
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
      <ProductSpecDrawer
        target={drawer}
        hooks={hooks}
        hooksReady={hooksReady}
        hooksFailed={hooksFailed}
        onReloadHooks={onReloadHooks}
        canEdit={canEdit}
        canManage={canManage}
        onClose={() => setDrawer(null)}
        onSaved={() => {
          setDrawer(null)
          request.reload()
        }}
      />
    </div>
  )
}
