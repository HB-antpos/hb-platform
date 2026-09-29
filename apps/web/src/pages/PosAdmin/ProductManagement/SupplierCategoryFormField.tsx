import { Button, Cascader, Space, Typography } from 'antd'
import { useEffect, useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import { HOT_BARGAIN_SUPPLIER_CODE, type SupplierCategorySource } from '../../../types/localSupplierCategory'
import { SUPPLIER_CATEGORY_RESTORE_AUTO_VALUE, formatCascaderDisplayLabels } from './supplierCategoryFilter'
import {
  findSupplierCategoryGuidPath,
  mapSupplierTreeToCascaderOptions,
  type SupplierCategoryCascaderOption,
} from './supplierCategoryOptions'
import type { SupplierCategoryTreeEntry } from './supplierCategoryTreeCache'

const I18N = 'posAdmin.products.supplierCategory'

export interface SupplierCategoryFormFieldProps {
  /** Form.Item 注入：单个编辑为分类 GUID；批量编辑还可能是「恢复自动归类」伪值。 */
  value?: string
  onChange?: (value: string | undefined) => void
  /** 表单里当前选择的澳洲供应商（随表单实时变化）。 */
  supplierCode?: string
  treeEntry?: SupplierCategoryTreeEntry
  onEnsureTree: (supplierCode: string) => void
  /** 分类树加载失败时点击「重试」。 */
  onRetryTree?: (supplierCode: string) => void
  mode?: 'single' | 'batch'
  /** 200 时展示的仓库分类路径（只读）。 */
  warehousePath?: string
  /** 单个编辑：打开弹窗时商品原有的分类与来源，用于提示保存后的效果。 */
  originalGuid?: string
  originalSource?: SupplierCategorySource
  /** 批量编辑：不可设置时的原因（所选商品跨供应商、为 200 等）。 */
  unavailableReason?: string
}

/**
 * 编辑/批量编辑表单里的「供应商分类」：
 * - 没有供应商：禁用并提示先选供应商；
 * - 200：只读展示「随仓库分类」；
 * - 其他：该供应商的单棵分类树，可搜索；下方提示当前来源与保存后的效果。
 * 禁用一律写成 `条件 || undefined`，不显式传 false，避免覆盖保存期间 Form 的整体禁用。
 */
export default function SupplierCategoryFormField({
  value,
  onChange,
  supplierCode,
  treeEntry,
  onEnsureTree,
  onRetryTree,
  mode = 'single',
  warehousePath,
  originalGuid,
  originalSource,
  unavailableReason,
}: SupplierCategoryFormFieldProps) {
  const { t } = useTranslation()
  const normalizedSupplierCode = supplierCode?.trim() || undefined
  const isHotBargain = normalizedSupplierCode === HOT_BARGAIN_SUPPLIER_CODE
  const needsTree = Boolean(normalizedSupplierCode) && !isHotBargain && !unavailableReason

  useEffect(() => {
    if (needsTree && normalizedSupplierCode) onEnsureTree(normalizedSupplierCode)
    // 只在供应商变化时加载；onEnsureTree 由页面每次渲染重新创建，不作为依赖。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [needsTree, normalizedSupplierCode])

  const nodes = treeEntry?.nodes
  const options = useMemo<SupplierCategoryCascaderOption[]>(() => {
    const categories = mapSupplierTreeToCascaderOptions(nodes ?? [])
    if (mode !== 'batch') return categories
    return [
      { value: SUPPLIER_CATEGORY_RESTORE_AUTO_VALUE, label: t(`${I18N}.restoreAuto`, '恢复自动归类'), kind: 'unassigned', isLeaf: true },
      ...categories,
    ]
  }, [mode, nodes, t])

  if (isHotBargain && !unavailableReason) {
    return (
      <Typography.Text type="secondary">
        {warehousePath
          ? `${t(`${I18N}.followsWarehouse`, '随仓库分类')}：${warehousePath}`
          : t(`${I18N}.followsWarehouseUnset`, '随仓库分类（当前未设置仓库分类）')}
      </Typography.Text>
    )
  }

  const treeLoading = needsTree && !treeEntry?.loaded && treeEntry?.status !== 'error'
  const treeFailed = needsTree && !treeEntry?.loaded && treeEntry?.status === 'error'
  const unavailable = !normalizedSupplierCode || Boolean(unavailableReason) || treeLoading || treeFailed
  const placeholder = !normalizedSupplierCode && mode === 'single'
    ? t(`${I18N}.selectSupplierFirst`, '请先选择澳洲供应商')
    : treeLoading
      ? t(`${I18N}.loadingTree`, '正在加载分类…')
      : treeFailed
        ? t(`${I18N}.loadFailed`, '供应商分类加载失败')
        : mode === 'batch'
          ? t('posAdmin.products.leaveEmpty', '留空不修改')
          : t(`${I18N}.fieldPlaceholder`, '选择供应商分类')

  const cascaderValue = value === SUPPLIER_CATEGORY_RESTORE_AUTO_VALUE
    ? [SUPPLIER_CATEGORY_RESTORE_AUTO_VALUE]
    : findSupplierCategoryGuidPath(nodes ?? [], value) ?? (value ? [value] : undefined)

  const hint = unavailableReason ?? (mode === 'single'
    ? describeSingleHint({ value, originalGuid, originalSource, hasSupplier: Boolean(normalizedSupplierCode), t })
    : undefined)

  return (
    <Space direction="vertical" size={4} style={{ width: '100%' }}>
      <Cascader
        allowClear
        changeOnSelect
        showSearch={{ filter: (input, path) => path.some((option) => String(option.label ?? '').toLowerCase().includes(input.trim().toLowerCase())) }}
        options={unavailable ? [] : options}
        value={unavailable ? undefined : cascaderValue}
        onChange={(nextValue) => {
          const path = Array.isArray(nextValue) ? nextValue.map(String) : []
          onChange?.(path.length ? path[path.length - 1] : undefined)
        }}
        displayRender={(labels) => formatCascaderDisplayLabels(labels.map(String))}
        disabled={unavailable || undefined}
        placeholder={placeholder}
        style={{ width: '100%' }}
      />
      {treeFailed && normalizedSupplierCode && onRetryTree ? (
        <Button type="link" size="small" style={{ padding: 0, height: 'auto' }} onClick={() => onRetryTree(normalizedSupplierCode)}>
          {t(`${I18N}.retry`, '重试')}
        </Button>
      ) : null}
      {hint && !treeFailed ? (
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>{hint}</Typography.Text>
      ) : null}
    </Space>
  )
}

function describeSingleHint({
  value,
  originalGuid,
  originalSource,
  hasSupplier,
  t,
}: {
  value?: string
  originalGuid?: string
  originalSource?: SupplierCategorySource
  hasSupplier: boolean
  t: ReturnType<typeof useTranslation>['t']
}): string | undefined {
  if (!hasSupplier) return undefined
  const unchanged = (value ?? '').toLowerCase() === (originalGuid ?? '').toLowerCase()
  if (unchanged) {
    if (!originalGuid) return t(`${I18N}.hintUnassigned`, '当前未归类；选择分类并保存后将锁定为人工指定')
    if (originalSource === 'manual') return t(`${I18N}.hintManual`, '当前为人工指定，采集不会覆盖；清空并保存可恢复自动归类')
    return t(`${I18N}.hintWebsite`, '当前按网站采集自动归类；改选并保存后将锁定为人工指定')
  }
  return value
    ? t(`${I18N}.hintWillLock`, '保存后将锁定为人工指定，采集不会覆盖')
    : t(`${I18N}.hintWillRestore`, '保存后将恢复按采集数据自动归类')
}
