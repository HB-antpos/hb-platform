import { DownOutlined, PlusOutlined } from '@ant-design/icons'
import { Button, Input, InputNumber, Popover, Radio, message } from 'antd'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ProductCreationType } from '../../../types/domesticProductCreation'
import type { BatchAddMode } from './batchCreateRules'
import type { BatchRenameMode } from './batchWorkspaceLogic'

export interface BatchAddValues {
  type: ProductCreationType
  count: number
  price: number | null
  mode: BatchAddMode
}

interface BatchGridToolbarProps {
  rowCount: number
  onAddNormal: () => void
  onAddSet: () => void
  onBatchAdd: (values: BatchAddValues) => void
  onBatchRename: (mode: BatchRenameMode, value: string) => void
}

const DEFAULT_BATCH_ADD: BatchAddValues = { type: ProductCreationType.NORMAL, count: 5, price: null, mode: 'append' }

/**
 * 网格工具栏：＋普通商品、＋套装、批量添加▾、批量命名▾。
 * 批量添加/批量命名原先是嵌套弹窗，这里收成下拉面板，表单状态随面板开合重置。
 */
export default function BatchGridToolbar({ rowCount, onAddNormal, onAddSet, onBatchAdd, onBatchRename }: BatchGridToolbarProps) {
  const { t } = useTranslation()
  const [addOpen, setAddOpen] = useState(false)
  const [addValues, setAddValues] = useState<BatchAddValues>(DEFAULT_BATCH_ADD)
  const [renameOpen, setRenameOpen] = useState(false)
  const [renameMode, setRenameMode] = useState<BatchRenameMode>('replace')
  const [renameValue, setRenameValue] = useState('')

  const handleAddOpenChange = (open: boolean) => {
    if (open) setAddValues(DEFAULT_BATCH_ADD)
    setAddOpen(open)
  }

  const handleRenameOpenChange = (open: boolean) => {
    if (open) {
      setRenameMode('replace')
      setRenameValue('')
    }
    setRenameOpen(open)
  }

  const submitBatchAdd = () => {
    onBatchAdd(addValues)
    setAddOpen(false)
  }

  const submitBatchRename = () => {
    if (!renameValue.trim()) {
      message.warning(t('productCreation.enterName'))
      return
    }
    onBatchRename(renameMode, renameValue)
    setRenameOpen(false)
  }

  const addPanel = (
    <div className="pc-popover" data-testid="product-creation-batch-add-panel">
      <div className="pc-popover-row">
        <span className="pc-popover-label">{t('productCreation.type')}</span>
        <Radio.Group
          size="small"
          optionType="button"
          value={addValues.type}
          onChange={(event) => setAddValues((current) => ({ ...current, type: event.target.value }))}
          options={[
            { label: t('productCreation.normal'), value: ProductCreationType.NORMAL },
            { label: t('productCreation.set'), value: ProductCreationType.SET },
          ]}
        />
      </div>
      <div className="pc-popover-row">
        <span className="pc-popover-label">{t('productCreation.quantity')}</span>
        <InputNumber
          size="small"
          min={1}
          max={100}
          precision={0}
          value={addValues.count}
          onChange={(value) => setAddValues((current) => ({ ...current, count: value || 1 }))}
        />
      </div>
      <div className="pc-popover-row">
        <span className="pc-popover-label">{t('productCreation.uniformPrice')}</span>
        <InputNumber
          size="small"
          min={0}
          precision={2}
          placeholder={t('productCreation.optional')}
          value={addValues.price}
          onChange={(value) => setAddValues((current) => ({ ...current, price: value }))}
        />
      </div>
      <div className="pc-popover-row">
        <span className="pc-popover-label">{t('productCreation.mode')}</span>
        <Radio.Group
          size="small"
          value={addValues.mode}
          onChange={(event) => setAddValues((current) => ({ ...current, mode: event.target.value }))}
          options={[
            { label: t('productCreation.appendCount'), value: 'append' },
            { label: t('productCreation.adjustToCount'), value: 'overwrite' },
          ]}
        />
      </div>
      {addValues.mode === 'overwrite' ? (
        <div className="pc-popover-warn">{t('productCreation.batchAddOverwriteHint', { count: rowCount })}</div>
      ) : null}
      <div className="pc-popover-actions">
        <Button size="small" onClick={() => setAddOpen(false)}>{t('common.cancel')}</Button>
        <Button size="small" type="primary" danger={addValues.mode === 'overwrite'} onClick={submitBatchAdd}>
          {t('productCreation.applyBatchAdd')}
        </Button>
      </div>
    </div>
  )

  const renamePanel = (
    <div className="pc-popover" data-testid="product-creation-batch-rename-panel">
      <div className="pc-popover-row">
        <span className="pc-popover-label">{t('productCreation.mode')}</span>
        <Radio.Group
          size="small"
          value={renameMode}
          onChange={(event) => setRenameMode(event.target.value)}
          options={[
            { label: t('productCreation.replace'), value: 'replace' },
            { label: t('productCreation.addPrefix'), value: 'prefix' },
            { label: t('productCreation.addSuffix'), value: 'suffix' },
          ]}
        />
      </div>
      <div className="pc-popover-row">
        <span className="pc-popover-label">{t('productCreation.value')}</span>
        <Input
          size="small"
          value={renameValue}
          placeholder={t('productCreation.enterName')}
          onChange={(event) => setRenameValue(event.target.value)}
          onPressEnter={submitBatchRename}
        />
      </div>
      <div className="pc-popover-hint">{t('productCreation.applyToAll')}</div>
      <div className="pc-popover-actions">
        <Button size="small" onClick={() => setRenameOpen(false)}>{t('common.cancel')}</Button>
        <Button size="small" type="primary" onClick={submitBatchRename}>{t('productCreation.applyBatchRename')}</Button>
      </div>
    </div>
  )

  return (
    <div className="pc-grid-toolbar" data-testid="product-creation-grid-toolbar">
      <Button size="small" icon={<PlusOutlined />} onClick={onAddNormal}>{t('productCreation.normalProduct')}</Button>
      <Button size="small" icon={<PlusOutlined />} onClick={onAddSet}>{t('productCreation.set')}</Button>
      <Popover
        trigger="click"
        placement="bottomLeft"
        open={addOpen}
        onOpenChange={handleAddOpenChange}
        content={addPanel}
        destroyOnHidden
      >
        <Button size="small" icon={<DownOutlined />} iconPosition="end">{t('productCreation.batchAdd')}</Button>
      </Popover>
      <Popover
        trigger="click"
        placement="bottomLeft"
        open={renameOpen}
        onOpenChange={handleRenameOpenChange}
        content={renamePanel}
        destroyOnHidden
      >
        <Button size="small" icon={<DownOutlined />} iconPosition="end">{t('productCreation.batchName')}</Button>
      </Popover>
      <span className="pc-grid-toolbar-spacer" />
      <span className="pc-sub">{t('productCreation.rowCount', { count: rowCount })}</span>
    </div>
  )
}
