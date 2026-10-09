import { useState } from 'react'
import { Checkbox } from 'antd'
import {
  buildExtraStoreOptions,
  getStoreSelectAllState,
  type StoreScopeOption,
} from './batchExecuteConfirm'

export interface BatchExecuteStorePickerLabels {
  title: string
  /** 本单分店（锁定勾选）的标签，例如「1009 - Lake Haven（本单）」。 */
  currentStoreLabel: string
  selectAll: string
  hint: string
}

interface BatchExecuteStorePickerProps {
  currentStoreCode?: string | null
  /** 全部 POS 启用分店选项（含本单分店时会自动剔除）。 */
  options: StoreScopeOption[]
  labels: BatchExecuteStorePickerLabels
  /** 勾选变化时回调额外分店编码（不含本单分店）。 */
  onChange: (extraStoreCodes: string[]) => void
}

/**
 * 「更新进货价」确认框里的分店范围选择：本单分店默认勾选且不可取消，其余 POS 启用分店可逐个勾选或一键全选。
 * 确认框由 Modal.confirm 静态渲染，勾选结果通过 onChange 写回调用方的可变对象。
 */
export function BatchExecuteStorePicker({
  currentStoreCode,
  options,
  labels,
  onChange,
}: BatchExecuteStorePickerProps) {
  const extraOptions = buildExtraStoreOptions(options, currentStoreCode)
  const allCodes = extraOptions.map((option) => option.value)
  const [selected, setSelected] = useState<string[]>([])
  const selectAll = getStoreSelectAllState(selected, allCodes)

  const update = (next: string[]) => {
    setSelected(next)
    onChange(next)
  }

  return (
    <div className="lsi-wb-store-scope">
      <div className="lsi-wb-price-choice-title">{labels.title}</div>
      <div className="lsi-wb-store-scope-toolbar">
        <Checkbox checked disabled>
          {labels.currentStoreLabel}
        </Checkbox>
        <Checkbox
          checked={selectAll.checked}
          indeterminate={selectAll.indeterminate}
          disabled={selectAll.disabled}
          onChange={(event) => update(event.target.checked ? allCodes : [])}
        >
          {labels.selectAll}
        </Checkbox>
      </div>
      {extraOptions.length > 0 && (
        <Checkbox.Group
          className="lsi-wb-store-scope-list"
          value={selected}
          options={extraOptions}
          onChange={(values) => update(values.map(String))}
        />
      )}
      <div className="lsi-wb-sync-hq-hint">{labels.hint}</div>
    </div>
  )
}
