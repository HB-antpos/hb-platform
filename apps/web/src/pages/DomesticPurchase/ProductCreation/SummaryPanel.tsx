import { CheckCircleOutlined, WarningOutlined } from '@ant-design/icons'
import { Button } from 'antd'
import { useTranslation } from 'react-i18next'
import { MAX_CREATED_ITEMS } from './batchWorkspaceLogic'
import type { DraftIssue, DraftSummary } from './batchWorkspaceLogic'

interface SummaryPanelProps {
  summary: DraftSummary
  issues: DraftIssue[]
  submitting: boolean
  onLocateIssue: (issue: DraftIssue) => void
  onPreview: () => void
  onSubmit: () => void
}

const numberFormat = new Intl.NumberFormat('en-US')

/** 工作台右栏：数量汇总、10,000 上限进度、「需要处理」逐条清单，以及预览/提交。 */
export default function SummaryPanel({ summary, issues, submitting, onLocateIssue, onPreview, onSubmit }: SummaryPanelProps) {
  const { t } = useTranslation()
  const progress = Math.min(100, (summary.expectedItems / MAX_CREATED_ITEMS) * 100)
  const hasIssues = issues.length > 0

  const renderIssue = (issue: DraftIssue, index: number) => {
    const content = (() => {
      if (issue.kind === 'missing_supplier') return <b>{t('productCreation.issueMissingSupplier')}</b>
      if (issue.kind === 'set_without_sub_item') {
        return (
          <>
            <b>{t('productCreation.issueRowLabel', { index: issue.rowIndex })}</b>
            {' '}
            {t('productCreation.issueSetNeedsSubItem')}
          </>
        )
      }
      return (
        <b>{t('productCreation.issueOverLimit', { count: numberFormat.format(issue.expected), limit: numberFormat.format(MAX_CREATED_ITEMS) })}</b>
      )
    })()

    // 超上限没有单一对应的行或控件，只展示不做定位；其余错误都是按钮，点击后工作台把焦点/滚动定位过去。
    if (issue.kind === 'over_limit') {
      return (
        <div key={`${issue.kind}-${index}`} className="pc-issue pc-issue-static" data-testid="product-creation-issue">
          <WarningOutlined />
          <span>{content}</span>
        </div>
      )
    }

    return (
      <button
        type="button"
        key={`${issue.kind}-${'rowKey' in issue ? issue.rowKey : index}`}
        className="pc-issue"
        data-testid="product-creation-issue"
        onClick={() => onLocateIssue(issue)}
      >
        <WarningOutlined />
        <span>{content}</span>
      </button>
    )
  }

  return (
    <aside className="pc-panel pc-summary" data-testid="product-creation-summary" aria-label={t('productCreation.summaryTitle')}>
      <h3 className="pc-panel-title">{t('productCreation.summaryTitle')}</h3>
      <div className="pc-kv"><span>{t('productCreation.normalProduct')}</span><b>{summary.normalCount}</b></div>
      <div className="pc-kv">
        <span>{t('productCreation.set')}</span>
        <b>
          {summary.setCount}
          {summary.pendingSetCount > 0 ? (
            <span className="pc-kv-note">{t('productCreation.pendingSubItems', { count: summary.pendingSetCount })}</span>
          ) : null}
        </b>
      </div>
      <div className="pc-kv"><span>{t('productCreation.expectedItems')}</span><b data-testid="product-creation-expected-items">{numberFormat.format(summary.expectedItems)}</b></div>
      <div>
        <div className="pc-kv pc-kv-tight">
          <span>{t('productCreation.limitLabel')}</span>
          <b className={summary.overLimit ? 'pc-over' : undefined}>
            {numberFormat.format(summary.expectedItems)} / {numberFormat.format(MAX_CREATED_ITEMS)}
          </b>
        </div>
        <div
          className={summary.overLimit ? 'pc-meter pc-meter-over' : 'pc-meter'}
          role="progressbar"
          aria-valuemin={0}
          aria-valuemax={MAX_CREATED_ITEMS}
          aria-valuenow={Math.min(summary.expectedItems, MAX_CREATED_ITEMS)}
        >
          <i style={{ width: `${Math.max(progress, summary.expectedItems > 0 ? 1 : 0)}%` }} />
        </div>
      </div>

      <h3 className="pc-panel-title pc-panel-title-issues">
        {t('productCreation.issuesTitle')}
        {hasIssues ? <span className="pc-count-badge">{issues.length}</span> : null}
      </h3>
      {hasIssues ? (
        <div className="pc-issue-list" data-testid="product-creation-issues">{issues.map(renderIssue)}</div>
      ) : (
        <div className="pc-issue-ok"><CheckCircleOutlined />{t('productCreation.noIssues')}</div>
      )}
      <span className="pc-sub">{t('productCreation.optionalNote')}</span>

      <div className="pc-summary-actions">
        <Button onClick={onPreview}>{t('productCreation.previewCodes')}</Button>
        <Button type="primary" disabled={hasIssues} loading={submitting} onClick={onSubmit}>
          {t('productCreation.submitCreate')}
        </Button>
        <span className="pc-sub pc-center">
          {hasIssues ? t('productCreation.submitBlockedHint') : t('productCreation.submitReadyHint')}
        </span>
      </div>
    </aside>
  )
}
