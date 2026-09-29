import { Button, Modal, message } from 'antd'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  getLocalSupplierCategorySummary,
  resolveLocalSupplierCategories,
  setLocalSupplierCategoryPromotional,
} from '../../../services/localSupplierCategoryService'
import type { LocalSupplierCategoryNode, LocalSupplierCategorySummary } from '../../../types/localSupplierCategory'
import SupplierCategoryManagerPanel, { filterWebsiteSupplierSummaries } from './SupplierCategoryManagerPanel'
import type { SupplierCategoryTreesApi } from './useSupplierCategoryTrees'

const I18N = 'posAdmin.products.supplierCategory'

export interface SupplierCategoryManagerModalProps {
  open: boolean
  /** 写操作（切换促销、重新解析）需要商品管理权限；只读用户仍可查看统计与分类树。 */
  canManage: boolean
  trees: SupplierCategoryTreesApi
  /** changed=true 表示弹窗内改动过归类，页面需要重新加载商品列表。 */
  onClose: (changed: boolean) => void
}

function getErrorMessage(error: unknown, fallback: string) {
  return error instanceof Error && error.message ? error.message : fallback
}

/**
 * 商品管理页「工具 → 供应商分类管理」弹窗：负责拉取统计、按需加载分类树并提交写操作，
 * 展示交给 SupplierCategoryManagerPanel。分类树与页面顶部级联框共用同一份缓存。
 */
export default function SupplierCategoryManagerModal({ open, canManage, trees, onClose }: SupplierCategoryManagerModalProps) {
  const { t } = useTranslation()
  const [summaries, setSummaries] = useState<LocalSupplierCategorySummary[]>([])
  const [summaryLoading, setSummaryLoading] = useState(false)
  const [summaryFailed, setSummaryFailed] = useState(false)
  const [selectedSupplierCode, setSelectedSupplierCode] = useState<string | undefined>(undefined)
  const [pendingCategoryGuids, setPendingCategoryGuids] = useState<ReadonlySet<string>>(() => new Set())
  const [resolving, setResolving] = useState(false)
  // 弹窗期间是否改动过归类；关闭时据此决定是否刷新商品列表，避免在弹窗背后反复重载大表。
  const changedRef = useRef(false)
  const summaryRequestSeqRef = useRef(0)
  const { ensure: ensureTree, reload: reloadTree, patchNode } = trees

  const loadSummary = useCallback(async () => {
    const requestSeq = summaryRequestSeqRef.current + 1
    summaryRequestSeqRef.current = requestSeq
    setSummaryLoading(true)
    try {
      const result = await getLocalSupplierCategorySummary()
      if (requestSeq !== summaryRequestSeqRef.current) return
      setSummaries(result)
      setSummaryFailed(false)
      // 已选中的供应商仍在列表里就保持，否则默认选第一个网站采集供应商。
      const websiteSummaries = filterWebsiteSupplierSummaries(result)
      setSelectedSupplierCode((current) => (
        current && websiteSummaries.some((summary) => summary.supplierCode === current)
          ? current
          : websiteSummaries[0]?.supplierCode
      ))
    } catch {
      if (requestSeq !== summaryRequestSeqRef.current) return
      setSummaryFailed(true)
      message.error(t(`${I18N}.summaryLoadFailed`, '供应商分类统计加载失败'))
    } finally {
      if (requestSeq === summaryRequestSeqRef.current) setSummaryLoading(false)
    }
  }, [t])

  useEffect(() => {
    if (!open) return
    changedRef.current = false
    void loadSummary()
  }, [open, loadSummary])

  useEffect(() => {
    if (!open || !selectedSupplierCode) return
    ensureTree(selectedSupplierCode).catch(() => {
      message.error(t(`${I18N}.loadFailed`, '供应商分类加载失败'))
    })
  }, [ensureTree, open, selectedSupplierCode, t])

  const handleRefresh = useCallback(() => {
    void loadSummary()
    if (selectedSupplierCode) {
      reloadTree(selectedSupplierCode).catch(() => {
        message.error(t(`${I18N}.loadFailed`, '供应商分类加载失败'))
      })
    }
  }, [loadSummary, reloadTree, selectedSupplierCode, t])

  const handleTogglePromotional = useCallback(async (node: LocalSupplierCategoryNode) => {
    if (!canManage || !selectedSupplierCode) return
    const supplierCode = selectedSupplierCode
    const nextIsPromotional = !node.isPromotional
    setPendingCategoryGuids((current) => new Set(current).add(node.categoryGuid))
    // 乐观更新：先改树上的标记，失败再回滚成原值。
    patchNode(supplierCode, node.categoryGuid, { isPromotional: nextIsPromotional, promotionalSource: 'manual' })
    try {
      const result = await setLocalSupplierCategoryPromotional(node.categoryGuid, nextIsPromotional)
      changedRef.current = true
      message.success(t(`${I18N}.promotionalDone`, '已更新促销标记：重新归类 {{reassigned}} 个商品，清除 {{cleared}} 个', {
        reassigned: result.reassigned,
        cleared: result.cleared,
      }))
      // 重新归类会改变各分类商品数，刷新树与左侧统计。
      reloadTree(supplierCode).catch(() => undefined)
      void loadSummary()
    } catch (error) {
      patchNode(supplierCode, node.categoryGuid, { isPromotional: node.isPromotional, promotionalSource: node.promotionalSource })
      message.error(getErrorMessage(error, t(`${I18N}.promotionalFailed`, '更新促销标记失败')))
    } finally {
      setPendingCategoryGuids((current) => {
        const next = new Set(current)
        next.delete(node.categoryGuid)
        return next
      })
    }
  }, [canManage, loadSummary, patchNode, reloadTree, selectedSupplierCode, t])

  const handleResolve = useCallback(async () => {
    if (!canManage || !selectedSupplierCode || resolving) return
    const supplierCode = selectedSupplierCode
    setResolving(true)
    try {
      const result = await resolveLocalSupplierCategories(supplierCode)
      changedRef.current = true
      message.success(t(
        `${I18N}.resolveDone`,
        '重新解析完成：扫描 {{scanned}}，新归类 {{assigned}}，更新 {{updated}}，清除 {{cleared}}，未变 {{unchanged}}，跳过人工 {{manualSkipped}}',
        {
          scanned: result.productsScanned,
          assigned: result.assigned,
          updated: result.updated,
          cleared: result.cleared,
          unchanged: result.unchanged,
          manualSkipped: result.manualSkipped,
        },
      ))
      reloadTree(supplierCode).catch(() => undefined)
      void loadSummary()
    } catch (error) {
      message.error(getErrorMessage(error, t(`${I18N}.resolveFailed`, '重新解析失败')))
    } finally {
      setResolving(false)
    }
  }, [canManage, loadSummary, reloadTree, resolving, selectedSupplierCode, t])

  const handleClose = () => {
    onClose(changedRef.current)
  }

  return (
    <Modal
      open={open}
      title={t(`${I18N}.manage`, '供应商分类管理')}
      onCancel={handleClose}
      footer={[
        <Button key="close" onClick={handleClose}>
          {t('posAdmin.products.close', '关闭')}
        </Button>,
      ]}
      width={980}
      destroyOnHidden
    >
      <SupplierCategoryManagerPanel
        summaries={summaries}
        summaryLoading={summaryLoading}
        summaryFailed={summaryFailed}
        selectedSupplierCode={selectedSupplierCode}
        onSelectSupplier={setSelectedSupplierCode}
        treeEntry={selectedSupplierCode ? trees.get(selectedSupplierCode) : undefined}
        canManage={canManage}
        pendingCategoryGuids={pendingCategoryGuids}
        onTogglePromotional={handleTogglePromotional}
        onRefresh={handleRefresh}
        onResolve={handleResolve}
        resolving={resolving}
      />
    </Modal>
  )
}
