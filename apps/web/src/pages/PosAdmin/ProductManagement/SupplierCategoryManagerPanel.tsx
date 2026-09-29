import { ReloadOutlined, SyncOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Popconfirm, Space, Spin, Tree, Typography, theme } from 'antd'
import dayjs from 'dayjs'
import { useMemo, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import {
  HOT_BARGAIN_SUPPLIER_CODE,
  type LocalSupplierCategoryNode,
  type LocalSupplierCategorySummary,
} from '../../../types/localSupplierCategory'
import SupplierCategoryTreeNodeTitle from './SupplierCategoryTreeNodeTitle'
import type { SupplierCategoryTreeEntry } from './supplierCategoryTreeCache'

const I18N = 'posAdmin.products.supplierCategory'

export interface SupplierCategoryManagerPanelProps {
  /** summary 原始列表（含 200 行，面板内过滤）。 */
  summaries: LocalSupplierCategorySummary[]
  summaryLoading: boolean
  summaryFailed?: boolean
  selectedSupplierCode?: string
  onSelectSupplier: (supplierCode: string) => void
  treeEntry?: SupplierCategoryTreeEntry
  canManage: boolean
  pendingCategoryGuids: ReadonlySet<string>
  onTogglePromotional: (node: LocalSupplierCategoryNode) => void
  onRefresh: () => void
  onResolve: () => void
  resolving: boolean
}

type TreeDataNode = { key: string; title: ReactNode; children: TreeDataNode[] }

/** 只保留网站采集的供应商：200 的供应商分类就是仓库分类，在仓库分类里维护。 */
export function filterWebsiteSupplierSummaries(summaries: LocalSupplierCategorySummary[]): LocalSupplierCategorySummary[] {
  return summaries.filter((summary) => summary.supplierCode !== HOT_BARGAIN_SUPPLIER_CODE && summary.sourceKind !== 'warehouse')
}

function formatDateTime(value: string | undefined): string | undefined {
  if (!value) return undefined
  const parsed = dayjs(value)
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm') : undefined
}

/**
 * 「供应商分类管理」弹窗内容（纯展示，数据与回调由弹窗容器提供）：
 * 左侧供应商列表带统计，右侧该供应商的分类树，可切换促销标记与重新解析。
 */
export default function SupplierCategoryManagerPanel({
  summaries,
  summaryLoading,
  summaryFailed,
  selectedSupplierCode,
  onSelectSupplier,
  treeEntry,
  canManage,
  pendingCategoryGuids,
  onTogglePromotional,
  onRefresh,
  onResolve,
  resolving,
}: SupplierCategoryManagerPanelProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const websiteSummaries = useMemo(() => filterWebsiteSupplierSummaries(summaries), [summaries])
  const selectedSummary = websiteSummaries.find((summary) => summary.supplierCode === selectedSupplierCode)

  const treeData = useMemo(() => {
    const build = (nodes: LocalSupplierCategoryNode[]): TreeDataNode[] => nodes.map((node) => ({
      key: node.categoryGuid,
      title: (
        <SupplierCategoryTreeNodeTitle
          node={node}
          canManage={canManage}
          pending={pendingCategoryGuids.has(node.categoryGuid)}
          onTogglePromotional={onTogglePromotional}
        />
      ),
      children: build(node.children ?? []),
    }))
    return build(treeEntry?.nodes ?? [])
  }, [canManage, onTogglePromotional, pendingCategoryGuids, treeEntry?.nodes])

  const renderSupplierList = () => {
    if (!websiteSummaries.length) {
      return summaryLoading ? null : (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description={summaryFailed
            ? t(`${I18N}.summaryLoadFailed`, '供应商分类统计加载失败')
            : t(`${I18N}.noSuppliers`, '暂无供应商分类数据')}
        />
      )
    }
    return websiteSummaries.map((summary) => {
      const selected = summary.supplierCode === selectedSupplierCode
      const lastCaptured = formatDateTime(summary.lastCapturedAt)
      return (
        <button
          key={summary.supplierCode}
          type="button"
          data-supplier-code={summary.supplierCode}
          aria-pressed={selected}
          onClick={() => onSelectSupplier(summary.supplierCode)}
          style={{
            display: 'block',
            width: '100%',
            textAlign: 'left',
            padding: '8px 10px',
            border: 'none',
            borderRadius: token.borderRadius,
            background: selected ? token.controlItemBgActive : 'transparent',
            color: token.colorText,
            cursor: 'pointer',
          }}
        >
          <div style={{ fontWeight: selected ? 600 : 400 }}>
            {summary.supplierName ? `${summary.supplierName} (${summary.supplierCode})` : summary.supplierCode}
          </div>
          <div style={{ fontSize: 12, color: token.colorTextSecondary }}>
            {t(`${I18N}.statsCategories`, '分类')} {summary.categoryCount}
            {' · '}
            {t(`${I18N}.statsAssigned`, '已归类')} {summary.assignedCount}/{summary.productCount}
            {' · '}
            {t(`${I18N}.statsManual`, '人工')} {summary.manualCount}
            {summary.promotionalCount > 0 ? ` · ${t(`${I18N}.promotional`, '促销')} ${summary.promotionalCount}` : ''}
          </div>
          <div style={{ fontSize: 12, color: token.colorTextTertiary }}>
            {lastCaptured
              ? `${t(`${I18N}.lastCaptured`, '最近采集')} ${lastCaptured}`
              : t(`${I18N}.neverCaptured`, '尚未采集')}
          </div>
        </button>
      )
    })
  }

  const renderTree = () => {
    if (!selectedSupplierCode || !selectedSummary) {
      return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t(`${I18N}.selectSupplierHint`, '请选择左侧供应商')} />
    }
    if (!treeEntry?.loaded) {
      if (treeEntry?.status === 'error') {
        return (
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t(`${I18N}.loadFailed`, '供应商分类加载失败')}>
            <Button onClick={onRefresh}>{t(`${I18N}.retry`, '重试')}</Button>
          </Empty>
        )
      }
      return <div style={{ padding: 48, textAlign: 'center' }}><Spin /></div>
    }
    if (!treeData.length) {
      return (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description={t(`${I18N}.treeEmpty`, '暂无网站分类。请在浏览器扩展中打开该供应商网站采集。')}
        />
      )
    }
    return (
      <Spin spinning={treeEntry.status === 'loading'}>
        <Tree
          key={selectedSupplierCode}
          blockNode
          selectable={false}
          defaultExpandAll
          height={440}
          treeData={treeData}
        />
      </Spin>
    )
  }

  return (
    <div style={{ display: 'flex', gap: 16, minHeight: 480 }}>
      <div style={{ flex: '0 0 300px', width: 300, display: 'flex', flexDirection: 'column', gap: 8, minWidth: 0 }}>
        <Alert
          type="info"
          showIcon
          message={t(`${I18N}.hotBargainNote`, 'Hot Bargain（200）的供应商分类即仓库分类，请在仓库分类中维护。')}
        />
        <Spin spinning={summaryLoading}>
          <div style={{ maxHeight: 440, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 2 }}>
            {renderSupplierList()}
          </div>
        </Spin>
      </div>
      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 8 }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8 }}>
          <Typography.Text strong>
            {selectedSummary
              ? (selectedSummary.supplierName ? `${selectedSummary.supplierName} (${selectedSummary.supplierCode})` : selectedSummary.supplierCode)
              : t(`${I18N}.manage`, '供应商分类管理')}
          </Typography.Text>
          <Space>
            <Button icon={<ReloadOutlined />} onClick={onRefresh}>
              {t(`${I18N}.refresh`, '刷新')}
            </Button>
            {canManage && selectedSummary ? (
              <Popconfirm
                title={t(`${I18N}.resolve`, '重新解析')}
                description={t(`${I18N}.resolveConfirm`, '按已采集数据重新计算该供应商全部商品的自动归类，人工指定不受影响。确定继续？')}
                onConfirm={onResolve}
                okText={t('common.confirm', '确定')}
                cancelText={t('common.cancel', '取消')}
              >
                <Button icon={<SyncOutlined />} loading={resolving}>
                  {t(`${I18N}.resolve`, '重新解析')}
                </Button>
              </Popconfirm>
            ) : null}
          </Space>
        </div>
        <div style={{ flex: 1, minHeight: 0 }}>{renderTree()}</div>
      </div>
    </div>
  )
}
