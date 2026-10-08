import { CopyOutlined, PlusOutlined } from '@ant-design/icons'
import {
  Alert,
  Button,
  Input,
  InputNumber,
  Modal,
  Popconfirm,
  Radio,
  Space,
  Tag,
  Tooltip,
  message,
  theme,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { ClipboardEvent as ReactClipboardEvent } from 'react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../../components/MeasuredTable'
import { useSnapshotSort } from '../../../../hooks/useSnapshotSort'
import {
  getStoreProductCodePage,
  getStoreProductMaintenanceDetail,
  saveStoreProductSetCodeSnapshot,
  type ProductCodeMode,
  type StoreProductMaintenanceDetail,
} from '../../../../services/storeProductSetCodeMaintenanceService'
import { copyTextToClipboard } from '../../../../utils/clipboard'
import {
  applySetCodeColumnPaste,
  validateSetCodeDrafts,
  type SetCodeColumnPasteResult,
  type SetCodeDraftEdits,
  type SetCodeDraftRow,
  type SetCodePasteField,
} from '../../ProductManagement/setCodeColumnPaste'
import {
  resolveProductSetCodeMaintenanceLoadState,
  type MaintenanceSetCodeDraftRow,
} from './productSetCodeMaintenanceLoadState'

type ProductSetCodeMaintenanceModalProps = {
  open: boolean
  productCode?: string
  storeCode?: string
  onClose: () => void
  onSaved?: () => void | Promise<void>
}

const PAGE_SIZE = 100
const MAX_PAGE_COUNT = 100
const MAX_PASTE_ROWS = PAGE_SIZE * MAX_PAGE_COUNT

function getRowId(row: SetCodeDraftRow) {
  return row.id || row._rowId || ''
}

function createTemporaryRow(
  productCode: string,
  mode: ProductCodeMode,
  index: number,
): MaintenanceSetCodeDraftRow {
  return {
    _rowId: `invoice_set_code_${Date.now()}_${index}_${Math.random().toString(36).slice(2)}`,
    productCode,
    setBarcode: '',
    setRetailPrice: mode === 2 ? 0 : undefined,
    isActive: true,
    sourceSetType: mode,
  }
}

async function loadAllSetCodeRows(
  productCode: string,
  storeCode: string,
  type: ProductCodeMode,
): Promise<SetCodeDraftRow[]> {
  const rowsById = new Map<string, SetCodeDraftRow>()
  const seenPageSignatures = new Set<string>()
  let expectedTotal = 0

  for (let page = 1; page <= MAX_PAGE_COUNT; page += 1) {
    const result = await getStoreProductCodePage(productCode, {
      storeCode,
      type,
      page,
      pageSize: PAGE_SIZE,
    })
    expectedTotal = result.totalCount
    const signature = JSON.stringify(result.items.map((row) => [row.setCodeId, row.barcode, row.retailPrice]))
    if (seenPageSignatures.has(signature)) throw new Error('商品条码分页未向后推进')
    seenPageSignatures.add(signature)

    result.items.forEach((row) => {
      if (!row.setCodeId) throw new Error('商品条码缺少唯一标识')
      if (row.setType !== type) throw new Error('商品条码类型与查询类型不一致')
      rowsById.set(row.setCodeId, {
        id: row.setCodeId,
        _rowId: row.setCodeId,
        productCode: row.productCode,
        setBarcode: row.barcode ?? '',
        setPurchasePrice: row.purchasePrice ?? undefined,
        setRetailPrice: row.retailPrice ?? undefined,
        isActive: row.isActive,
      })
    })

    if (!result.hasMore) break
    if (page === MAX_PAGE_COUNT) throw new Error('商品条码分页超过安全上限')
  }

  if (rowsById.size !== expectedTotal) {
    throw new Error(`商品条码分页数量不一致：应有 ${expectedTotal} 条，实际读取 ${rowsById.size} 条`)
  }
  return Array.from(rowsById.values())
}

export default function ProductSetCodeMaintenanceModal({
  open,
  productCode,
  storeCode,
  onClose,
  onSaved,
}: ProductSetCodeMaintenanceModalProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const normalizedProductCode = productCode?.trim() ?? ''
  const normalizedStoreCode = storeCode?.trim() ?? ''
  const [product, setProduct] = useState<StoreProductMaintenanceDetail | null>(null)
  const [mode, setMode] = useState<ProductCodeMode>(2)
  const [draftRows, setRows] = useState<MaintenanceSetCodeDraftRow[]>([])
  const [baselineProductType, setBaselineProductType] = useState<number | null>(null)
  const [baselineRows, setBaselineRows] = useState<MaintenanceSetCodeDraftRow[]>([])
  const [edits, setEdits] = useState<SetCodeDraftEdits>({})
  const [selectedPasteField, setSelectedPasteField] = useState<SetCodePasteField | null>(null)
  const [canSwitchMode, setCanSwitchMode] = useState(false)
  const [integrityError, setIntegrityError] = useState<string | null>(null)
  const [repairMultiCodeCount, setRepairMultiCodeCount] = useState(0)
  const [loading, setLoading] = useState(false)
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const requestSequenceRef = useRef(0)
  const saveInFlightRef = useRef(false)
  // 点列头排一次（输入时不会实时重排）；按「编辑后的值」排，和屏幕上看到的一致。
  // 粘贴、保存、校验和「第 N 行」提示都基于排序后的 rows，保证与屏幕上的序号一致。
  const {
    displayItems: rows,
    sortOrderOf,
    onTableChange,
    resetSort,
  } = useSnapshotSort({
    items: draftRows,
    getId: getRowId,
    getValue: (row, field) => {
      const rowId = getRowId(row)
      if (field === 'setBarcode') return edits[rowId]?.setBarcode ?? row.setBarcode
      if (field === 'setRetailPrice') {
        return Object.prototype.hasOwnProperty.call(edits[rowId] ?? {}, 'setRetailPrice')
          ? edits[rowId].setRetailPrice
          : row.setRetailPrice
      }
      return (row as unknown as Record<string, unknown>)[field]
    },
  })

  const loadLatestData = useCallback(async (showErrorMessage = true) => {
    const requestSequence = requestSequenceRef.current + 1
    requestSequenceRef.current = requestSequence
    setLoading(true)
    setReady(false)
    resetSort()
    setLoadError(null)
    setIntegrityError(null)
    setRepairMultiCodeCount(0)

    if (!normalizedProductCode || !normalizedStoreCode) {
      if (requestSequence !== requestSequenceRef.current) return false
      setRows([])
      setBaselineProductType(null)
      setBaselineRows([])
      setProduct(null)
      setCanSwitchMode(false)
      setLoading(false)
      if (showErrorMessage) {
        message.error(t('posAdmin.invoiceDetail.setCodeMaintenanceNeedsStore', '当前进货单缺少分店，无法维护商品多码/套装'))
      }
      return false
    }

    try {
      const [detail, typeOneRows, typeTwoRows] = await Promise.all([
        getStoreProductMaintenanceDetail(normalizedProductCode, normalizedStoreCode),
        loadAllSetCodeRows(normalizedProductCode, normalizedStoreCode, 1),
        loadAllSetCodeRows(normalizedProductCode, normalizedStoreCode, 2),
      ])
      if (requestSequence !== requestSequenceRef.current) return false

      const loadState = resolveProductSetCodeMaintenanceLoadState({
        productType: detail.productType,
        typeOneRows,
        typeTwoRows,
      })

      setProduct(detail)
      setMode(loadState.mode)
      setRows(loadState.rows)
      setBaselineProductType(detail.productType ?? null)
      setBaselineRows(loadState.baselineRows)
      setEdits({})
      setSelectedPasteField(null)
      setCanSwitchMode(loadState.canSwitchMode)
      setRepairMultiCodeCount(loadState.repairMultiCodeCount)
      if (loadState.hasIntegrityError) {
        setIntegrityError(t(
          'posAdmin.invoiceDetail.setCodeTypeMismatch',
          '商品类型与现有条码类型不一致，已停止编辑；请先在商品管理中核对数据。',
        ))
      } else {
        setIntegrityError(null)
      }
      setReady(loadState.ready)
      return loadState.ready
    } catch {
      if (requestSequence !== requestSequenceRef.current) return false
      const errorMessage = t('posAdmin.invoiceDetail.loadSetCodeMaintenanceFailed', '加载商品多码/套装数据失败')
      // 加载失败时清空上一轮数据，避免把旧快照误当成当前商品的可编辑状态。
      setProduct(null)
      setRows([])
      setBaselineProductType(null)
      setBaselineRows([])
      setEdits({})
      setSelectedPasteField(null)
      setCanSwitchMode(false)
      setRepairMultiCodeCount(0)
      setReady(false)
      setLoadError(errorMessage)
      if (showErrorMessage) {
        message.error(errorMessage)
      }
      return false
    } finally {
      if (requestSequence === requestSequenceRef.current) setLoading(false)
    }
  }, [normalizedProductCode, normalizedStoreCode, resetSort, t])

  useEffect(() => {
    if (!open) {
      requestSequenceRef.current += 1
      setProduct(null)
      setRows([])
      setBaselineProductType(null)
      setBaselineRows([])
      setEdits({})
      setSelectedPasteField(null)
      setCanSwitchMode(false)
      setIntegrityError(null)
      setRepairMultiCodeCount(0)
      setReady(false)
      setLoadError(null)
      setLoading(false)
      return
    }
    void loadLatestData()
  }, [loadLatestData, open])

  const updateRowEdit = (row: SetCodeDraftRow, patch: SetCodeDraftEdits[string]) => {
    const rowId = getRowId(row)
    if (!rowId) return
    setEdits((current) => ({ ...current, [rowId]: { ...current[rowId], ...patch } }))
  }

  const reportPasteResult = (result: SetCodeColumnPasteResult) => {
    if (result.error === 'multiple_columns') {
      message.warning(t('posAdmin.products.pasteMultipleColumns', '一次只能粘贴一列 Excel 数据'))
      return false
    }
    if (result.error === 'missing_target') {
      setSelectedPasteField(null)
      message.warning(t('posAdmin.products.pasteTargetMissing', '粘贴起始行已变化，请重新选择'))
      return false
    }
    if (result.error === 'too_many_rows') {
      message.warning(t('posAdmin.products.pasteTooManyRows', '粘贴后条码行数不能超过 {{max}} 行，本次粘贴未生效', { max: MAX_PASTE_ROWS }))
      return false
    }
    if (result.error === 'duplicate_barcode') {
      message.error(t('posAdmin.products.duplicateBarcodeAtRows', '条码 {{barcode}} 在第 {{rows}} 行重复', {
        barcode: result.duplicateBarcode || '-',
        rows: result.duplicateRowNumbers?.join('、') || '-',
      }))
      return false
    }
    if (result.invalidCount > 0) {
      message.warning(t('posAdmin.products.pasteInvalidRetailPrices', '已跳过 {{count}} 个无效零售价，原值保持不变', { count: result.invalidCount }))
    } else if (result.appliedCount > 0) {
      message.success(t('posAdmin.products.setCodePasteResult', '已粘贴 {{applied}} 个值，跳过 {{blank}} 个空单元格，自动新增 {{added}} 行', {
        applied: result.appliedCount,
        blank: result.skippedBlankCount,
        added: result.addedCount,
      }))
    }
    return true
  }

  const handlePaste = (
    event: ReactClipboardEvent<HTMLElement>,
    field: SetCodePasteField,
    startRowId?: string,
  ) => {
    if (!product || !ready || loading || saving || (mode === 2 && field === 'setRetailPrice')) return
    event.preventDefault()
    setSelectedPasteField(field)
    const result = applySetCodeColumnPaste({
      rows,
      edits,
      startRowId,
      field,
      clipboardText: event.clipboardData.getData('text/plain'),
      createRow: (index) => createTemporaryRow(product.productCode, mode, index),
      maxRows: MAX_PASTE_ROWS,
    })
    if (!reportPasteResult(result)) return
    setRows(result.rows as MaintenanceSetCodeDraftRow[])
    setEdits(result.edits)
  }

  const renderPasteHeader = (label: string, field: SetCodePasteField, enabled = true) => {
    if (!enabled) return label
    const selected = selectedPasteField === field
    return (
      <button
        type="button"
        aria-pressed={selected}
        aria-label={t('posAdmin.invoiceDetail.selectExcelPasteColumn', '选择 {{column}} Excel 粘贴列', { column: label })}
        disabled={!ready || loading || saving}
        // 点列头文字是选中粘贴列，不能冒泡成排序；排序由列头空白处/箭头触发。
        onClick={(event) => {
          event.stopPropagation()
          setSelectedPasteField(field)
        }}
        onPaste={(event) => handlePaste(event, field)}
        style={{
          appearance: 'none',
          border: `1px solid ${selected ? token.colorPrimaryBorder : 'transparent'}`,
          borderRadius: token.borderRadiusSM,
          background: selected ? token.colorPrimaryBg : 'transparent',
          color: 'inherit',
          cursor: !ready || loading || saving ? 'not-allowed' : 'copy',
          font: 'inherit',
          lineHeight: 'inherit',
          margin: '-2px -6px',
          padding: '2px 6px',
        }}
      >
        {label}
      </button>
    )
  }

  const addRow = () => {
    if (!product || !ready || loading || saving) return
    const row = createTemporaryRow(product.productCode, mode, rows.length)
    setRows((current) => [...current, row])
    setEdits((current) => ({
      ...current,
      [getRowId(row)]: { setBarcode: '', setRetailPrice: row.setRetailPrice },
    }))
  }

  const deleteRow = (row: SetCodeDraftRow) => {
    const rowId = getRowId(row)
    if (!rowId) return
    setRows((current) => current.filter((item) => getRowId(item) !== rowId))
    setEdits((current) => {
      const next = { ...current }
      delete next[rowId]
      return next
    })
  }

  const closeModal = () => {
    if (!saveInFlightRef.current) onClose()
  }

  const handleSave = async () => {
    if (!product || !ready || integrityError || loading || saveInFlightRef.current || !normalizedStoreCode) return
    const rowsSnapshot = rows.map((row) => ({ ...row }))
    const editsSnapshot: SetCodeDraftEdits = Object.fromEntries(
      Object.entries(edits).map(([rowId, edit]) => [rowId, { ...edit }]),
    )
    const validationRows = mode === 2
      ? rowsSnapshot.map((row) => ({ ...row, setRetailPrice: 0 }))
      : rowsSnapshot
    const validationEdits = mode === 2
      ? Object.fromEntries(validationRows.map((row) => [getRowId(row), { ...editsSnapshot[getRowId(row)], setRetailPrice: 0 }]))
      : editsSnapshot
    const validation = validateSetCodeDrafts(validationRows, validationEdits)
    if (!validation.valid) {
      if (validation.reason === 'barcode_required') {
        message.error(t('posAdmin.products.barcodeRequiredAtRow', '第 {{row}} 行条码不能为空', { row: validation.rowNumbers[0] }))
      } else if (validation.reason === 'retail_price_required') {
        message.error(t('posAdmin.products.retailPriceRequiredAtRow', '第 {{row}} 行零售价不能为空', { row: validation.rowNumbers[0] }))
      } else {
        message.error(t('posAdmin.products.duplicateBarcodeAtRows', '条码 {{barcode}} 在第 {{rows}} 行重复', {
          barcode: validation.barcode || '-',
          rows: validation.rowNumbers.join('、'),
        }))
      }
      return
    }

    if (mode === 1) {
      const zeroPriceRowIndex = rowsSnapshot.findIndex((row) => {
        const edit = editsSnapshot[getRowId(row)]
        const retailPrice = edit && Object.prototype.hasOwnProperty.call(edit, 'setRetailPrice')
          ? edit.setRetailPrice
          : row.setRetailPrice
        return retailPrice !== undefined && retailPrice !== null && retailPrice <= 0
      })
      if (zeroPriceRowIndex >= 0) {
        message.error(t(
          'posAdmin.invoiceDetail.retailPriceMustBePositive',
          '第 {{row}} 行套装零售价必须大于 0',
          { row: zeroPriceRowIndex + 1 },
        ))
        return
      }
    }

    const readEffectiveRow = (row: SetCodeDraftRow) => {
      const edit = editsSnapshot[getRowId(row)] ?? {}
      return {
        barcode: String(edit.setBarcode ?? row.setBarcode ?? '').trim(),
        retailPrice: mode === 1
          ? (Object.prototype.hasOwnProperty.call(edit, 'setRetailPrice') ? edit.setRetailPrice : row.setRetailPrice) ?? undefined
          : undefined,
        isActive: row.isActive !== false,
      }
    }

    const targetItems = rowsSnapshot.map((row) => {
      const effective = readEffectiveRow(row)
      return {
        setCodeId: row.id,
        barcode: effective.barcode,
        retailPrice: effective.retailPrice,
        setType: mode,
        isActive: effective.isActive,
      }
    })
    const expectedSnapshot = targetItems
      .map((item) => [item.barcode, mode === 1 ? item.retailPrice : null, item.isActive])
      .sort((left, right) => JSON.stringify(left).localeCompare(JSON.stringify(right)))
    const matchesTargetSnapshot = (
      productType: number | null | undefined,
      actualItems: Array<{ barcode?: string | null; retailPrice?: number | null; isActive: boolean }>,
    ) => {
      const actualSnapshot = actualItems
        .map((item) => [
          String(item.barcode ?? '').trim(),
          mode === 1 ? item.retailPrice : null,
          item.isActive,
        ])
        .sort((left, right) => JSON.stringify(left).localeCompare(JSON.stringify(right)))
      return productType === mode
        && JSON.stringify(actualSnapshot) === JSON.stringify(expectedSnapshot)
    }

    saveInFlightRef.current = true
    setSaving(true)
    try {
      const saved = await saveStoreProductSetCodeSnapshot({
        productCode: product.productCode,
        storeCode: normalizedStoreCode,
        expectedProductType: baselineProductType,
        productType: mode,
        expectedItems: baselineRows.map((row) => ({
          setCodeId: row.id,
          barcode: String(row.setBarcode ?? '').trim(),
          retailPrice: row.sourceSetType === 1 ? row.setRetailPrice : undefined,
          setType: row.sourceSetType,
          isActive: row.isActive !== false,
        })),
        items: targetItems,
      })
      const snapshotMatches = saved.productCode === product.productCode
        && saved.storeCode === normalizedStoreCode
        && matchesTargetSnapshot(saved.productType, saved.items)
      const stateReloaded = await loadLatestData(false)
      if (!snapshotMatches || !stateReloaded) {
        message.warning(t(
          'posAdmin.invoiceDetail.saveSetCodeMaintenanceReadbackFailed',
          '保存请求已完成，但回读校验失败；请刷新后核对。',
        ))
        return
      }
      message.success(t('posAdmin.invoiceDetail.saveSetCodeMaintenanceSuccess', '商品多码/套装已保存'))
      onClose()
      await onSaved?.()
    } catch {
      let readbackConfirmed = false
      try {
        const [verifiedDetail, verifiedRows] = await Promise.all([
          getStoreProductMaintenanceDetail(product.productCode, normalizedStoreCode),
          loadAllSetCodeRows(product.productCode, normalizedStoreCode, mode),
        ])
        readbackConfirmed = matchesTargetSnapshot(
          verifiedDetail.productType,
          verifiedRows.map((row) => ({
            barcode: row.setBarcode,
            retailPrice: row.setRetailPrice,
            isActive: row.isActive !== false,
          })),
        )
      } catch {
        readbackConfirmed = false
      }
      const stateReloaded = await loadLatestData(false)
      if (readbackConfirmed) {
        message.success(t('posAdmin.invoiceDetail.saveSetCodeMaintenanceSuccess', '商品多码/套装已保存'))
        onClose()
        await onSaved?.()
        return
      }
      if (stateReloaded) {
        message.warning(t(
          'posAdmin.invoiceDetail.saveSetCodeMaintenanceUnconfirmed',
          '保存结果未确认；已重新加载最新数据，请核对后再决定是否重试。',
        ))
      } else {
        message.warning(t(
          'posAdmin.invoiceDetail.saveSetCodeMaintenanceUnconfirmedReloadFailed',
          '保存结果未确认，且最新数据加载失败；请先重试加载并核对，再决定是否保存。',
        ))
      }
    } finally {
      saveInFlightRef.current = false
      setSaving(false)
    }
  }

  const columns = useMemo<ColumnsType<MaintenanceSetCodeDraftRow>>(() => [
    {
      title: t('posAdmin.invoiceDetail.seqNo', '序号'),
      width: 60,
      align: 'right',
      render: (_value, _row, index) => index + 1,
    },
    {
      title: renderPasteHeader(
        mode === 1 ? t('posAdmin.products.setBarcodeLabel', '套装条码 *') : t('posAdmin.products.multiCodeBarcodeLabel', '多码条码 *'),
        'setBarcode',
      ),
      dataIndex: 'setBarcode',
      sorter: true,
      sortOrder: sortOrderOf('setBarcode'),
      width: 260,
      render: (_value, row) => {
        const rowId = getRowId(row)
        const value = edits[rowId]?.setBarcode ?? row.setBarcode ?? ''
        return (
          <Space.Compact style={{ width: '100%' }}>
            <Input
              value={value}
              disabled={!ready || saving}
              style={{ flex: 1, background: selectedPasteField === 'setBarcode' ? token.colorPrimaryBg : undefined }}
              placeholder={t('posAdmin.products.inputBarcode', '请输入条码')}
              onPaste={(event) => handlePaste(event, 'setBarcode', rowId)}
              onChange={(event) => updateRowEdit(row, { setBarcode: event.target.value })}
            />
            <Tooltip title={t('common.copy', '复制')}>
              <Button
                icon={<CopyOutlined />}
                aria-label={t('common.copyValue', 'Copy {{value}}', { value })}
                disabled={!value}
                onClick={() => {
                  void copyTextToClipboard(value, {
                    successMessage: t('common.copySuccess', '复制成功'),
                    failureMessage: t('common.copyFailed', '复制失败'),
                  })
                }}
              />
            </Tooltip>
          </Space.Compact>
        )
      },
    },
    {
      title: t('posAdmin.invoiceDetail.purchasePrice', '进货价'),
      dataIndex: 'setPurchasePrice',
      // 多码模式下进货价是「跟随主条码」文字，没有可排序的值。
      sorter: mode === 1,
      sortOrder: sortOrderOf('setPurchasePrice'),
      width: 150,
      render: (_value, row) => mode === 2
        ? t('posAdmin.invoiceDetail.followMainBarcodePrice', '跟随主条码')
        : row.setPurchasePrice == null
          ? t('posAdmin.invoiceDetail.allocatedAfterSave', '保存后按整组分摊')
          : `$${row.setPurchasePrice.toFixed(2)}`,
    },
    {
      title: renderPasteHeader(t('posAdmin.invoiceDetail.retailPrice', '零售价'), 'setRetailPrice', mode === 1),
      dataIndex: 'setRetailPrice',
      sorter: mode === 1,
      sortOrder: sortOrderOf('setRetailPrice'),
      width: 150,
      render: (_value, row) => {
        if (mode === 2) return t('posAdmin.invoiceDetail.followMainBarcodePrice', '跟随主条码')
        const rowId = getRowId(row)
        const value = Object.prototype.hasOwnProperty.call(edits[rowId] ?? {}, 'setRetailPrice')
          ? edits[rowId].setRetailPrice
          : row.setRetailPrice
        return (
          <InputNumber
            value={value}
            min={0.01}
            precision={2}
            prefix="$"
            disabled={!ready || saving}
            style={{ width: '100%', background: selectedPasteField === 'setRetailPrice' ? token.colorPrimaryBg : undefined }}
            onPaste={(event) => handlePaste(event, 'setRetailPrice', rowId)}
            onChange={(nextValue) => updateRowEdit(row, { setRetailPrice: nextValue })}
          />
        )
      },
    },
    {
      title: t('posAdmin.cashierUsers.status', '状态'),
      dataIndex: 'isActive',
      width: 90,
      align: 'center',
      render: (value?: boolean) => (
        <Tag color={value === false ? 'red' : 'green'}>
          {value === false ? t('posAdmin.products.disable', '停用') : t('posAdmin.products.enable', '启用')}
        </Tag>
      ),
    },
    {
      title: t('column.action', '操作'),
      width: 90,
      fixed: 'right',
      render: (_value, row) => (
        <Popconfirm title={t('posAdmin.products.confirmDelete', '确认删除？')} onConfirm={() => deleteRow(row)}>
          <Button type="link" danger size="small" disabled={saving}>{t('common.delete', '删除')}</Button>
        </Popconfirm>
      ),
    },
  ], [edits, loading, mode, ready, rows, saving, selectedPasteField, sortOrderOf, t, token.colorPrimaryBg])

  const isChangingNormalProduct = product != null && product.productType !== 1 && product.productType !== 2

  return (
    <Modal
      open={open}
      title={t('posAdmin.invoiceDetail.setCodeMaintenanceTitle', '商品多码/套装维护 - {{code}}', { code: normalizedProductCode || '-' })}
      width={960}
      destroyOnHidden
      confirmLoading={saving}
      okButtonProps={{ disabled: !ready || loading || Boolean(integrityError) }}
      cancelButtonProps={{ disabled: saving }}
      onCancel={closeModal}
      onOk={() => void handleSave()}
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Space wrap size={12}>
          <span style={{ fontWeight: 600 }}>{product?.productName || '-'}</span>
          <Radio.Group
            value={mode}
            buttonStyle="solid"
            disabled={!ready || loading || saving || !canSwitchMode}
            onChange={(event) => {
              setMode(event.target.value as ProductCodeMode)
              setSelectedPasteField(null)
              setRows([])
              setEdits({})
            }}
          >
            <Radio.Button value={2}>{t('posAdmin.products.multiBarcodeProduct', '多条码商品')}</Radio.Button>
            <Radio.Button value={1}>{t('posAdmin.products.setProduct', '套装商品')}</Radio.Button>
          </Radio.Group>
        </Space>

        {integrityError ? <Alert type="error" showIcon message={integrityError} /> : null}
        {loadError ? (
          <Alert
            type="error"
            showIcon
            message={loadError}
            action={<Button size="small" onClick={() => void loadLatestData()}>{t('common.retry', '重试')}</Button>}
          />
        ) : null}
        {repairMultiCodeCount > 0 && !integrityError ? (
          <Alert
            type="warning"
            showIcon
            message={t(
              'posAdmin.invoiceDetail.setCodeRepairMultiCodeHint',
              '检测到 {{count}} 条历史多条码子项，已载入套装草稿。请核对或粘贴每行零售价；保存后将统一转换为套装。',
              { count: repairMultiCodeCount },
            )}
          />
        ) : null}
        {isChangingNormalProduct && !integrityError ? (
          <Alert type="info" showIcon message={t('posAdmin.invoiceDetail.normalProductModeChangeHint', '当前为普通商品，保存后将切换为所选商品类型')} />
        ) : null}

        <Space wrap>
          <Button type="dashed" icon={<PlusOutlined />} disabled={!ready || loading || saving} onClick={addRow}>
            {t('posAdmin.products.addBarcodeBtn', '添加条码')}
          </Button>
          <span style={{ color: token.colorTextSecondary, fontSize: 12 }}>
            {mode === 1
              ? t('posAdmin.invoiceDetail.setCodePasteHint', '套装商品可点击“套装条码”或“零售价”列头后粘贴 Excel 单列，也可从任意单元格开始')
              : t('posAdmin.invoiceDetail.multiCodePasteHint', '多条码商品价格跟随主条码；点击“多码条码”列头后可粘贴 Excel 单列')}
          </span>
        </Space>

        {!loadError ? (
          <MeasuredTable<MaintenanceSetCodeDraftRow>
            metricId="pos-admin.local-supplier-invoices.invoice-edit.set-code-maintenance"
            rowKey={getRowId}
            loading={loading}
            dataSource={rows}
            onChange={onTableChange}
            showSorterTooltip={false}
            columns={columns}
            pagination={false}
            size="small"
            scroll={{ x: 780, y: 420 }}
            locale={{ emptyText: t('posAdmin.invoiceDetail.noSetCodeRows', '暂无多码/套装条码，请添加或直接粘贴 Excel 列') }}
          />
        ) : null}
      </Space>
    </Modal>
  )
}
