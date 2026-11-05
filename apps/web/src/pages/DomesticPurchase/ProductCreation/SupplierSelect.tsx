import { Select } from 'antd'
import type { GetRef, SelectProps } from 'antd'
import { forwardRef, useMemo } from 'react'
import type { Ref } from 'react'

export type SupplierSelectRef = GetRef<typeof Select>

export interface SupplierOption {
  supplierCode: string
  supplierName: string
}

type SupplierSelectProps = Omit<SelectProps<string>, 'options' | 'showSearch' | 'filterOption' | 'optionRender'> & {
  suppliers: readonly SupplierOption[]
}

/** 供应商下拉：选中后只显示名称，列表里在名称旁附编码；搜索同时匹配编码与名称。 */
function SupplierSelectInner({ suppliers, ...selectProps }: SupplierSelectProps, ref: Ref<SupplierSelectRef>) {
  const options = useMemo(
    () => suppliers.map((supplier) => ({
      value: supplier.supplierCode,
      label: supplier.supplierName || supplier.supplierCode,
      code: supplier.supplierCode,
    })),
    [suppliers],
  )

  return (
    <Select<string>
      ref={ref}
      showSearch
      filterOption={(input, option) => {
        const keyword = input.trim().toLowerCase()
        if (!keyword) return true
        return `${option?.label ?? ''}`.toLowerCase().includes(keyword) || `${option?.code ?? ''}`.toLowerCase().includes(keyword)
      }}
      optionRender={(option) => {
        const code = (option.data as { code?: string }).code
        return (
          <span className="pc-supplier-option">
            <span className="pc-supplier-option-name">{option.label}</span>
            {/* 没有名称（无「查看国内供应商名称」权限）时名称位已是编码，旁边不再重复。 */}
            {code !== option.label ? <span className="pc-supplier-option-code">{code}</span> : null}
          </span>
        )
      }}
      {...selectProps}
      options={options}
    />
  )
}

const SupplierSelect = forwardRef(SupplierSelectInner)
SupplierSelect.displayName = 'SupplierSelect'

export default SupplierSelect
