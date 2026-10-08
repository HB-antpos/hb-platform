/**
 * useContainerSetCode — 货柜明细套装码弹窗状态管理
 *
 * 职责边界：
 * - 管理套装码弹窗的开/关状态
 * - 加载套装码明细数据（含 AbortController 竞态处理）
 * - 管理套装码价格编辑与自动进货价分摊计算
 * - 保存套装码价格变更
 * - 提供 setCodeColumns 表格列定义
 * - 不渲染任何 UI（Modal 由父组件使用返回值渲染）
 */

import { useMemo, useRef, useState } from 'react'
import { message } from 'antd'
import type { ColumnsType, TableProps } from 'antd/es/table'
import { InputNumber } from 'antd'
import { useTranslation } from 'react-i18next'
import { useSnapshotSort } from '../../../hooks/useSnapshotSort'
import type { ContainerDetail } from '../../../types/container'
import type { ContainerDomesticSetCodeItem } from '../../../types/container'
import { getContainerDomesticSetCodes, updateContainerDomesticSetCodePrices } from '../../../services/containerService'
import {
  calculateContainerSetCodePurchasePrice,
  getContainerDetailProductCode,
} from './containerDetailLogic'

// ---- 类型 ----

/** 套装码价格编辑临时快照：key 为 setProductCode/barcode/setItemNumber。 */
type ContainerSetCodePriceEdits = Record<string, { retailPrice?: number | null; purchasePrice?: number | null }>

interface UseContainerSetCodeOptions {
  /** 当前用户权限 */
  canEditContainer: boolean
}

interface UseContainerSetCodeReturn {
  /** 弹窗是否打开 */
  setCodeModalOpen: boolean
  /** 弹窗对应的货柜明细行 */
  setCodeModalRow: ContainerDetail | null
  /** 套装码明细列表 */
  setCodeItems: ContainerDomesticSetCodeItem[]
  /** 套装码明细按列头排序后的展示顺序（表格 dataSource 用它，而不是 setCodeItems） */
  setCodeDisplayItems: ContainerDomesticSetCodeItem[]
  /** 表格 onChange：处理列头排序 */
  onSetCodeTableChange: NonNullable<TableProps<ContainerDomesticSetCodeItem>['onChange']>
  /** 是否正在加载套装码数据 */
  setCodeLoading: boolean
  /** 是否正在保存套装码价格 */
  setCodeSaving: boolean
  /** 价格编辑快照 */
  setCodePriceEdits: ContainerSetCodePriceEdits
  /** 手动设置过进货价的 key 集合 */
  setCodeManualPurchasePriceKeys: Set<string>
  /** 有变更的价格项 */
  changedSetCodePriceItems: Array<{ setProductCode: string; retailPrice?: number | null; purchasePrice?: number | null }>
  /** 套装码表格列定义 */
  setCodeColumns: ColumnsType<ContainerDomesticSetCodeItem>
  /** 打开套装码弹窗 */
  openSetCodeModal: (row: ContainerDetail) => void
  /** 关闭套装码弹窗 */
  closeSetCodeModal: () => void
  /** 修改零售价 */
  patchSetCodeRetailPriceEdit: (item: ContainerDomesticSetCodeItem, retailPrice: number | null) => void
  /** 修改进货价 */
  patchSetCodePriceEdit: (item: ContainerDomesticSetCodeItem, patch: { retailPrice?: number | null; purchasePrice?: number | null }) => void
  /** 保存套装码价格 */
  saveSetCodePrices: () => Promise<void>
}

// ---- 工具函数 ----

function getSetCodeRowKey(item: ContainerDomesticSetCodeItem) {
  return item.setProductCode || item.barcode || item.setItemNumber || ''
}

/**
 * 按零售价比例自动分摊进货价。
 * 主商品进货价（货柜明细行进口价）按各子项零售价占比分配。
 */
function buildSetCodeAutoPurchasePriceEdits(
  items: ContainerDomesticSetCodeItem[],
  mainPurchasePrice: number | null | undefined,
  baseEdits: ContainerSetCodePriceEdits = {},
  manualPurchasePriceKeys: Set<string> = new Set(),
): ContainerSetCodePriceEdits {
  const totalRetailPrice = items.reduce((sum, item) => {
    const key = getSetCodeRowKey(item)
    const edit = key ? baseEdits[key] : undefined
    const retailPrice = edit?.retailPrice !== undefined ? edit.retailPrice : item.retailPrice
    return typeof retailPrice === 'number' && Number.isFinite(retailPrice) && retailPrice > 0 ? sum + retailPrice : sum
  }, 0)

  return items.reduce<ContainerSetCodePriceEdits>((nextEdits, item) => {
    const key = getSetCodeRowKey(item)
    if (!key || manualPurchasePriceKeys.has(key)) return nextEdits

    const edit = nextEdits[key]
    const nextRetailPrice = edit?.retailPrice !== undefined ? edit.retailPrice : item.retailPrice
    const nextPurchasePrice = calculateContainerSetCodePurchasePrice(mainPurchasePrice, nextRetailPrice, totalRetailPrice)
    if (nextPurchasePrice === undefined) {
      if (edit && 'purchasePrice' in edit) {
        const rest = { ...edit }
        delete rest.purchasePrice
        if (Object.keys(rest).length > 0) {
          nextEdits[key] = rest
        } else {
          delete nextEdits[key]
        }
      }
      return nextEdits
    }

    nextEdits[key] = {
      ...edit,
      purchasePrice: nextPurchasePrice,
    }
    return nextEdits
  }, { ...baseEdits })
}

// ---- Hook ----

export default function useContainerSetCode({
  canEditContainer,
}: UseContainerSetCodeOptions): UseContainerSetCodeReturn {
  const { t } = useTranslation()
  const [setCodeModalOpen, setSetCodeModalOpen] = useState(false)
  const [setCodeModalRow, setSetCodeModalRow] = useState<ContainerDetail | null>(null)
  const [setCodeItems, setSetCodeItems] = useState<ContainerDomesticSetCodeItem[]>([])
  const [setCodeLoading, setSetCodeLoading] = useState(false)
  const [setCodeSaving, setSetCodeSaving] = useState(false)
  const [setCodePriceEdits, setSetCodePriceEdits] = useState<ContainerSetCodePriceEdits>({})
  const [setCodeManualPurchasePriceKeys, setSetCodeManualPurchasePriceKeys] = useState<Set<string>>(() => new Set())
  const setCodeAbortControllerRef = useRef<AbortController | null>(null)
  // 点列头排一次，价格编辑不会实时重排；价格列按「编辑后的值」排，和屏幕上看到的一致。
  const {
    displayItems: setCodeDisplayItems,
    sortOrderOf,
    onTableChange: onSetCodeTableChange,
    resetSort: resetSetCodeSort,
  } = useSnapshotSort({
    items: setCodeItems,
    getId: getSetCodeRowKey,
    getValue: (item, field) => {
      const edit = setCodePriceEdits[getSetCodeRowKey(item)]
      if (field === 'retailPrice' && edit?.retailPrice !== undefined) return edit.retailPrice
      if (field === 'purchasePrice' && edit?.purchasePrice !== undefined) return edit.purchasePrice
      return (item as unknown as Record<string, unknown>)[field]
    },
  })

  // ---- 加载套装码数据 ----

  const loadSetCodeItems = async (row: ContainerDetail, manualPurchasePriceKeys: Set<string> = new Set()) => {
    const productCode = getContainerDetailProductCode(row)
    if (!productCode) {
      message.warning(t('containers.setCode.missingProductCode'))
      return
    }

    setCodeAbortControllerRef.current?.abort()
    const abortController = new AbortController()
    setCodeAbortControllerRef.current = abortController
    setSetCodeLoading(true)
    setSetCodePriceEdits({})
    setSetCodeManualPurchasePriceKeys(manualPurchasePriceKeys)
    try {
      const items = await getContainerDomesticSetCodes(productCode, abortController.signal)
      setSetCodeItems(items)
      setSetCodePriceEdits(buildSetCodeAutoPurchasePriceEdits(items, row.进口价格, {}, manualPurchasePriceKeys))
    } catch (error) {
      if ((error as DOMException)?.name !== 'AbortError') {
        message.error(error instanceof Error ? error.message : t('containers.setCode.loadFailed'))
      }
    } finally {
      if (setCodeAbortControllerRef.current === abortController) {
        setSetCodeLoading(false)
        setCodeAbortControllerRef.current = null
      }
    }
  }

  // ---- 弹窗控制 ----

  const openSetCodeModal = (row: ContainerDetail) => {
    setSetCodeModalRow(row)
    setSetCodeModalOpen(true)
    setSetCodeItems([])
    resetSetCodeSort()
    void loadSetCodeItems(row)
  }

  const closeSetCodeModal = () => {
    setCodeAbortControllerRef.current?.abort()
    setCodeAbortControllerRef.current = null
    setSetCodeModalOpen(false)
    setSetCodeModalRow(null)
    setSetCodeItems([])
    resetSetCodeSort()
    setSetCodePriceEdits({})
    setSetCodeManualPurchasePriceKeys(new Set())
    setSetCodeLoading(false)
  }

  // ---- 价格编辑 ----

  const patchSetCodeRetailPriceEdit = (item: ContainerDomesticSetCodeItem, retailPrice: number | null) => {
    const key = getSetCodeRowKey(item)
    if (!key) return
    setSetCodePriceEdits((current) => {
      const nextEdits: ContainerSetCodePriceEdits = {
        ...current,
        [key]: {
          ...current[key],
          retailPrice,
        },
      }
      const mainPurchasePrice = setCodeModalRow?.进口价格
      return buildSetCodeAutoPurchasePriceEdits(setCodeItems, mainPurchasePrice, nextEdits, setCodeManualPurchasePriceKeys)
    })
  }

  const patchSetCodePriceEdit = (
    item: ContainerDomesticSetCodeItem,
    patch: { retailPrice?: number | null; purchasePrice?: number | null },
  ) => {
    const key = getSetCodeRowKey(item)
    if (!key) return
    if ('purchasePrice' in patch) {
      setSetCodeManualPurchasePriceKeys((current) => new Set(current).add(key))
    }
    setSetCodePriceEdits((current) => ({
      ...current,
      [key]: {
        ...current[key],
        ...patch,
      },
    }))
  }

  // ---- 保存 ----

  const saveSetCodePrices = async () => {
    const productCode = setCodeModalRow ? getContainerDetailProductCode(setCodeModalRow) : undefined
    if (!productCode || changedSetCodePriceItems.length === 0) return

    setSetCodeSaving(true)
    try {
      await updateContainerDomesticSetCodePrices(productCode, changedSetCodePriceItems)
      message.success(t('containers.setCode.saveSuccess'))
      if (setCodeModalRow) {
        await loadSetCodeItems(setCodeModalRow, setCodeManualPurchasePriceKeys)
      }
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('containers.setCode.saveFailed'))
    } finally {
      setSetCodeSaving(false)
    }
  }

  // ---- 派生状态 ----

  const changedSetCodePriceItems = useMemo(() => setCodeItems.flatMap((item) => {
    const key = getSetCodeRowKey(item)
    const edit = setCodePriceEdits[key]
    if (!item.setProductCode || !edit) return []
    const nextRetailPrice = edit.retailPrice !== undefined ? edit.retailPrice : item.retailPrice
    const nextPurchasePrice = edit.purchasePrice !== undefined ? edit.purchasePrice : item.purchasePrice
    if (nextRetailPrice === item.retailPrice && nextPurchasePrice === item.purchasePrice) return []
    return [{
      setProductCode: item.setProductCode,
      retailPrice: nextRetailPrice,
      purchasePrice: nextPurchasePrice,
    }]
  }), [setCodeItems, setCodePriceEdits])

  // ---- 表格列定义 ----

  const setCodeColumns: ColumnsType<ContainerDomesticSetCodeItem> = useMemo(() => [
    {
      title: t('containers.setCode.itemNumber'),
      dataIndex: 'setItemNumber',
      sorter: true,
      sortOrder: sortOrderOf('setItemNumber'),
      width: 140,
      render: (value) => value || '--',
    },
    {
      title: t('containers.setCode.barcode'),
      dataIndex: 'barcode',
      sorter: true,
      sortOrder: sortOrderOf('barcode'),
      width: 170,
      render: (value) => value || '--',
    },
    {
      title: t('containers.setCode.retailPrice'),
      dataIndex: 'retailPrice',
      sorter: true,
      sortOrder: sortOrderOf('retailPrice'),
      width: 120,
      align: 'right',
      render: (_, item) => {
        const key = getSetCodeRowKey(item)
        const edit = setCodePriceEdits[key]
        return (
          <InputNumber
            min={0}
            prefix="$"
            precision={2}
            disabled={!canEditContainer}
            style={{ width: 104 }}
            value={edit?.retailPrice !== undefined ? edit.retailPrice : item.retailPrice}
            onChange={(value) => patchSetCodeRetailPriceEdit(item, value == null ? null : Number(value))}
          />
        )
      },
    },
    {
      title: t('containers.setCode.purchasePrice'),
      dataIndex: 'purchasePrice',
      sorter: true,
      sortOrder: sortOrderOf('purchasePrice'),
      width: 120,
      align: 'right',
      render: (_, item) => {
        const key = getSetCodeRowKey(item)
        const edit = setCodePriceEdits[key]
        return (
          <InputNumber
            min={0}
            prefix="$"
            precision={2}
            disabled={!canEditContainer}
            style={{ width: 104 }}
            value={edit?.purchasePrice !== undefined ? edit.purchasePrice : item.purchasePrice}
            onChange={(value) => patchSetCodePriceEdit(item, { purchasePrice: value == null ? null : Number(value) })}
          />
        )
      },
    },
  ], [canEditContainer, setCodePriceEdits, sortOrderOf, t])

  return {
    setCodeModalOpen,
    setCodeModalRow,
    setCodeItems,
    setCodeDisplayItems,
    onSetCodeTableChange,
    setCodeLoading,
    setCodeSaving,
    setCodePriceEdits,
    setCodeManualPurchasePriceKeys,
    changedSetCodePriceItems,
    setCodeColumns,
    openSetCodeModal,
    closeSetCodeModal,
    patchSetCodeRetailPriceEdit,
    patchSetCodePriceEdit,
    saveSetCodePrices,
  }
}
