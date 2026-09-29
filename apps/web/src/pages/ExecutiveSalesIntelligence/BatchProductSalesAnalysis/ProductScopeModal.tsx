import { CheckCircleFilled, UploadOutlined } from '@ant-design/icons'
import { Alert, Button, Input, Modal, Tag } from 'antd'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { parsePastedItemNumbers, readItemNumberFile, type ImportResult } from './import'
import styles from './index.module.css'

const PREVIEW_ITEM_LIMIT = 100

interface Props {
  initialText: string
  initialResult: ImportResult
  maxItems: number
  onCancel: () => void
  onApply: (text: string, result: ImportResult) => void
}

/** 弹窗草稿与已确认范围隔离；取消及关闭后不允许异步文件结果回写。 */
export default function ProductScopeModal({ initialText, initialResult, maxItems, onCancel, onApply }: Props) {
  const { t } = useTranslation()
  const [text, setText] = useState(initialText)
  const [result, setResult] = useState(initialResult)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)
  const generation = useRef(0)
  useEffect(() => () => { generation.current += 1 }, [])

  const changeText = (value: string) => {
    generation.current += 1
    setText(value); setResult(parsePastedItemNumbers(value)); setLoading(false); setError(false)
  }
  const readFile = async (file: File) => {
    const request = ++generation.current
    setLoading(true); setError(false)
    try {
      const next = await readItemNumberFile(file)
      if (request !== generation.current) return
      setResult(next); setText(next.itemNumbers.join('\n'))
    } catch {
      if (request === generation.current) setError(true)
    } finally {
      if (request === generation.current) setLoading(false)
    }
  }

  return <Modal open title={t('batchProductSalesAnalysis.selectScope')} width={820} centered
    onCancel={onCancel} maskClosable={false}
    okText={t('batchProductSalesAnalysis.confirmItems', { count: result.itemNumbers.length })}
    cancelText={t('batchProductSalesAnalysis.cancel')}
    okButtonProps={{ disabled: loading || !result.itemNumbers.length || result.itemNumbers.length > maxItems }}
    onOk={() => onApply(text, result)}>
    <p className={styles.modalHint}>{t('batchProductSalesAnalysis.scopeHint', { count: maxItems })}</p>
    <div className={styles.scopeEditor}>
      <label className={styles.pasteField}>
        <span>{t('batchProductSalesAnalysis.paste')}</span>
        <Input.TextArea autoFocus aria-label={t('batchProductSalesAnalysis.paste')} value={text} rows={7}
          placeholder={t('batchProductSalesAnalysis.importHint')} onChange={event => changeText(event.target.value)} />
      </label>
      <div className={styles.uploadZone}>
        <UploadOutlined aria-hidden />
        <Button icon={<UploadOutlined />} loading={loading} onClick={() => fileRef.current?.click()}>{t('batchProductSalesAnalysis.importFile')}</Button>
        <span>{t('batchProductSalesAnalysis.importHint')}</span>
        <input ref={fileRef} type="file" accept=".csv,.xlsx" hidden onChange={event => {
          const file = event.target.files?.[0]
          if (file) void readFile(file)
          event.currentTarget.value = ''
        }} />
      </div>
    </div>
    <div className={styles.importSummary} role="status"><CheckCircleFilled /> {t('batchProductSalesAnalysis.sourceSummary', {
      rows: result.sourceRowCount, duplicates: result.duplicateCount, valid: result.itemNumbers.length,
    })}</div>
    {error ? <Alert type="error" showIcon message={t('batchProductSalesAnalysis.fileError')} /> : null}
    <section className={styles.scopePreview}>
      <h3>{t('batchProductSalesAnalysis.validItems', { count: result.itemNumbers.length })}</h3>
      <div tabIndex={0} aria-label={t('batchProductSalesAnalysis.validItems', { count: result.itemNumbers.length })}>
        {result.itemNumbers.length ? result.itemNumbers.slice(0, PREVIEW_ITEM_LIMIT).map(item => <Tag key={item}>{item}</Tag>) : <span className={styles.hint}>{t('batchProductSalesAnalysis.noImportedItems')}</span>}
      </div>
      {result.itemNumbers.length > PREVIEW_ITEM_LIMIT ? <p className={styles.hint}>{t('batchProductSalesAnalysis.previewLimited', { count: PREVIEW_ITEM_LIMIT })}</p> : null}
    </section>
    {result.issues.length ? <details className={styles.details}><summary>{t('batchProductSalesAnalysis.issues', { count: result.issues.length })}</summary>
      <div className={styles.importIssues}>{result.issues.map((issue, index) => <p key={index}>{issue.row || '—'} · {issue.value || '—'} · {issue.reason}</p>)}</div>
    </details> : null}
  </Modal>
}
