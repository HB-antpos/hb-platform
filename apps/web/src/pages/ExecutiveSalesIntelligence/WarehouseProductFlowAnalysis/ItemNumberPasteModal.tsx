import { Alert, Button, Input, Modal, Radio } from 'antd'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import type { WarehouseProductFlowItemNumberMatchMode } from '../../../types/warehouseProductFlowAnalysis'

import styles from './index.module.css'
import { WAREHOUSE_PRODUCT_FLOW_MAX_ITEM_NUMBERS, parsePastedItemNumbers, type ParsedPastedItemNumbers } from './logic'

const PREVIEW_COUNT = 12

export interface ItemNumberPasteResult {
  text: string
  parsed: ParsedPastedItemNumbers
  matchMode: WarehouseProductFlowItemNumberMatchMode
}

interface ItemNumberPasteModalProps {
  open: boolean
  initialText: string
  initialMatchMode: WarehouseProductFlowItemNumberMatchMode
  onCancel: () => void
  onApply: (result: ItemNumberPasteResult) => void
}

/** 批量货号粘贴弹窗：实时解析 Excel 粘贴内容，显示统计与预览，确认后交给页面应用并查询。 */
export default function ItemNumberPasteModal({ open, initialText, initialMatchMode, onCancel, onApply }: ItemNumberPasteModalProps) {
  const { t } = useTranslation()
  const [text, setText] = useState(initialText)
  const [matchMode, setMatchMode] = useState<WarehouseProductFlowItemNumberMatchMode>(initialMatchMode)

  // 每次打开都从已应用的条件开始编辑；取消不会污染已应用的条件。
  useEffect(() => {
    if (!open) return
    setText(initialText)
    setMatchMode(initialMatchMode)
  }, [initialMatchMode, initialText, open])

  const parsed = useMemo(() => parsePastedItemNumbers(text), [text])
  const count = parsed.itemNumbers.length
  const tooMany = count > WAREHOUSE_PRODUCT_FLOW_MAX_ITEM_NUMBERS
  const preview = parsed.itemNumbers.slice(0, PREVIEW_COUNT)

  return <Modal
    open={open}
    width={640}
    title={t('warehouseProductFlowAnalysis.itemNumbers.modalTitle')}
    onCancel={onCancel}
    destroyOnHidden
    footer={<div className={styles.itemNumberModalFooter}>
      <span>{t('warehouseProductFlowAnalysis.itemNumbers.footerHint')}</span>
      <span>
        <Button onClick={() => setText('')} disabled={!text}>{t('warehouseProductFlowAnalysis.itemNumbers.clear')}</Button>
        <Button onClick={onCancel}>{t('common.cancel')}</Button>
        <Button type="primary" disabled={!count || tooMany} onClick={() => onApply({ text, parsed, matchMode })}>{t('warehouseProductFlowAnalysis.itemNumbers.apply', { count })}</Button>
      </span>
    </div>}
  >
    <p className={styles.itemNumberTip}>{t('warehouseProductFlowAnalysis.itemNumbers.tip', { max: WAREHOUSE_PRODUCT_FLOW_MAX_ITEM_NUMBERS })}</p>
    <Input.TextArea
      autoFocus
      value={text}
      onChange={(event) => setText(event.target.value)}
      placeholder={t('warehouseProductFlowAnalysis.itemNumbers.placeholder')}
      aria-label={t('warehouseProductFlowAnalysis.itemNumbers.modalTitle')}
      className={styles.itemNumberTextarea}
      autoSize={{ minRows: 8, maxRows: 8 }}
      spellCheck={false}
    />
    <div className={styles.itemNumberStats}>
      <div><span>{t('warehouseProductFlowAnalysis.itemNumbers.lineCount')}</span><strong>{parsed.lineCount}</strong></div>
      <div><span>{t('warehouseProductFlowAnalysis.itemNumbers.recognized')}</span><strong>{count}</strong></div>
      <div className={parsed.skippedCount ? styles.itemNumberStatWarn : undefined}><span>{t('warehouseProductFlowAnalysis.itemNumbers.skipped')}</span><strong>{parsed.skippedCount}</strong></div>
    </div>
    {tooMany ? <Alert type="error" showIcon className={styles.itemNumberAlert} message={t('warehouseProductFlowAnalysis.itemNumbers.tooMany', { count, max: WAREHOUSE_PRODUCT_FLOW_MAX_ITEM_NUMBERS })} /> : null}
    <div className={styles.itemNumberMode}>
      <span>{t('warehouseProductFlowAnalysis.itemNumbers.matchMode')}</span>
      <Radio.Group value={matchMode} onChange={(event) => setMatchMode(event.target.value)} options={[
        { value: 'itemNumber', label: t('warehouseProductFlowAnalysis.itemNumbers.modeItemNumber') },
        { value: 'any', label: t('warehouseProductFlowAnalysis.itemNumbers.modeAny') },
      ]} />
    </div>
    {count ? <div className={styles.itemNumberPreview}>
      <span>{t('warehouseProductFlowAnalysis.itemNumbers.preview', { count: Math.min(count, PREVIEW_COUNT) })}</span>
      <div>{preview.map((itemNumber) => <code key={itemNumber}>{itemNumber}</code>)}{count > PREVIEW_COUNT ? <code>+{count - PREVIEW_COUNT}</code> : null}</div>
    </div> : null}
  </Modal>
}
