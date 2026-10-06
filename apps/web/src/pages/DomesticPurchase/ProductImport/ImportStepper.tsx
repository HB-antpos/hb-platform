import { CheckOutlined } from '@ant-design/icons'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import type { ImportStepKey, ImportStepSummary, ImportStepView } from './importGridLogic'

interface ImportStepperProps {
  steps: ImportStepView[]
  /** 第 1 步「供应商」直接内嵌下拉：选择即完成，不再单独占一行。 */
  supplierControl: ReactNode
  /** 最近一次检测完成的时间戳，用于第 3 步摘要。 */
  detectedAt: number | null
}

const STEP_LABEL_KEYS: Record<ImportStepKey, string> = {
  supplier: 'productImport.stepSupplier',
  entry: 'productImport.stepEntry',
  detect: 'productImport.stepDetect',
  commit: 'productImport.stepCommit',
  container: 'productImport.stepContainer',
}

function formatClock(timestamp: number | null): string {
  if (timestamp === null) return ''
  const date = new Date(timestamp)
  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`
}

/** 导入流程的 5 步分段：当前步高亮，已完成步带结果摘要；有改动后「检测匹配」回到当前步并给出提示。 */
export function ImportStepper({ steps, supplierControl, detectedAt }: ImportStepperProps) {
  const { t } = useTranslation()

  const renderSummary = (summary: ImportStepSummary): string => {
    switch (summary.kind) {
      case 'rows':
        return t('productImport.stepEntryRows', '{{count}} 行', { count: summary.count })
      case 'detectedAt':
        return t('productImport.stepDetectedAt', '{{time}} 完成', { time: formatClock(detectedAt) })
      case 'needRedetect':
        return t('productImport.stepNeedRedetect', '有改动，需重新检测')
      case 'duplicates':
        return t('productImport.stepDuplicates', '有 {{count}} 组重复货号', { count: summary.count })
      case 'pendingCommit':
        return t('productImport.stepPendingCommit', '待入库 {{count}} 条', { count: summary.count })
      case 'committed':
        return t('productImport.stepCommitted', '已入库 {{count}} 条', { count: summary.count })
      case 'nothingToCommit':
        return t('productImport.stepNothingToCommit', '无需入库')
      case 'sent':
        return t('productImport.stepSent', '已发送 {{count}} 条', { count: summary.count })
      case 'optional':
        return t('productImport.stepOptional', '可选')
    }
  }

  return (
    <div className="pi-steps" role="list" aria-label={t('productImport.stepperAriaLabel', '导入步骤')} data-testid="product-import-steps">
      {steps.map((step, index) => (
        <div className="pi-step-slot" key={step.key} role="presentation">
          {index > 0 ? <span className={steps[index - 1].state === 'done' ? 'pi-step-line is-done' : 'pi-step-line'} aria-hidden="true" /> : null}
          <div
            className={`pi-step is-${step.state}${step.warn ? ' is-warn' : ''}`}
            role="listitem"
            aria-current={step.state === 'current' ? 'step' : undefined}
            data-step={step.key}
          >
            <span className="pi-step-badge">{step.state === 'done' ? <CheckOutlined /> : index + 1}</span>
            <span className="pi-step-label">{t(STEP_LABEL_KEYS[step.key])}</span>
            {step.key === 'supplier' ? supplierControl : step.summary ? <span className="pi-step-summary">{renderSummary(step.summary)}</span> : null}
          </div>
        </div>
      ))}
    </div>
  )
}
