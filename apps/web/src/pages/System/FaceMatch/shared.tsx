import { LeftOutlined, RightOutlined } from '@ant-design/icons'
import { Button, Input, Select, Space, Typography } from 'antd'
import type { CSSProperties } from 'react'
import { useTranslation } from 'react-i18next'

import type { StoreOption } from '../../../services/storeService'
import type { FaceMatchConfig } from '../../../types/faceMatch'
import { canGoNext, canGoPrev } from './logic'

// 各页签共用的小组件与上下文类型。

/** 页面传给每个页签的上下文：配置（含权限标志）、分店选项、写入是否被禁用。 */
export interface FaceMatchTabContext {
  config: FaceMatchConfig
  storeOptions: StoreOption[]
  /** 分店列表加载失败（例如账号没有分店查看权限）：降级成手填分店编码。 */
  storeOptionsFailed: boolean
  /** 功能未开启时只能查看，写入类操作禁用。 */
  writeBlocked: boolean
}

export const NUMERIC_STYLE = { fontVariantNumeric: 'tabular-nums' } as const

interface StoreSelectProps {
  options: StoreOption[]
  failed: boolean
  value?: string | null
  onChange: (value: string | null) => void
  allowClear?: boolean
  disabled?: boolean
  placeholder?: string
  style?: CSSProperties
}

/** 单选分店：加载失败降级为手填编码。 */
export function StoreSelect({ options, failed, value, onChange, allowClear = true, disabled, placeholder, style }: StoreSelectProps) {
  if (failed) {
    return (
      <Input
        value={value ?? ''}
        allowClear={allowClear}
        disabled={disabled}
        placeholder={placeholder}
        style={style}
        maxLength={32}
        onChange={(event) => onChange(event.target.value.trim() || null)}
      />
    )
  }
  return (
    <Select
      showSearch
      value={value ?? undefined}
      allowClear={allowClear}
      disabled={disabled}
      placeholder={placeholder}
      style={style}
      optionFilterProp="label"
      options={options.map((option) => ({
        value: option.value,
        label: option.label && option.label !== option.value ? `${option.value} · ${option.label}` : option.value,
      }))}
      onChange={(next: string | undefined) => onChange(next ?? null)}
    />
  )
}

interface StoreMultiSelectProps {
  options: StoreOption[]
  failed: boolean
  value?: string[]
  onChange?: (value: string[]) => void
  disabled?: boolean
  placeholder?: string
}

/** 多选分店（名单生效范围）：加载失败降级为可手填编码的标签输入。 */
export function StoreMultiSelect({ options, failed, value, onChange, disabled, placeholder }: StoreMultiSelectProps) {
  return (
    <Select
      mode={failed ? 'tags' : 'multiple'}
      value={value}
      disabled={disabled}
      placeholder={placeholder}
      optionFilterProp="label"
      tokenSeparators={[',', ' ']}
      options={options.map((option) => ({
        value: option.value,
        label: option.label && option.label !== option.value ? `${option.value} · ${option.label}` : option.value,
      }))}
      onChange={(next: string[]) => onChange?.(next)}
    />
  )
}

interface PagerProps {
  page: number
  hasMore: boolean
  loading?: boolean
  onChange: (page: number) => void
}

/** 后端分页没有总数：只提供上一页 / 下一页。 */
export function Pager({ page, hasMore, loading, onChange }: PagerProps) {
  const { t } = useTranslation()
  return (
    <Space style={{ justifyContent: 'flex-end', width: '100%' }}>
      <Button size="small" icon={<LeftOutlined />} disabled={!canGoPrev(page) || loading} onClick={() => onChange(page - 1)}>
        {t('faceMatch.common.prevPage')}
      </Button>
      <Typography.Text type="secondary" style={NUMERIC_STYLE}>
        {t('faceMatch.common.pageNumber', { page })}
      </Typography.Text>
      <Button size="small" disabled={!canGoNext(hasMore) || loading} onClick={() => onChange(page + 1)}>
        {t('faceMatch.common.nextPage')}
        <RightOutlined />
      </Button>
    </Space>
  )
}
