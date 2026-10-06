import { CheckCircleFilled, CloseCircleFilled, CopyOutlined, ExclamationCircleFilled } from '@ant-design/icons'
import { Alert, Button, Modal } from 'antd'
import { useRef } from 'react'
import { useTranslation } from 'react-i18next'
import { copyTextToClipboard } from '../../../utils/clipboard'
import { buildFailureClipboardText, parseSyncFailure, type SyncResultView } from './chinaSuppliersLogic'
import './chinaSuppliers.css'

interface SyncResultModalProps {
  /** 为 null 表示弹窗关闭；关闭动画期间沿用上一次的内容，避免内容先空掉再淡出。 */
  result: SyncResultView | null
  onClose: () => void
}

/**
 * 「同步到销售库」结果弹窗：共处理 / 新增 / 更新 / 失败 + 失败明细。
 * 有失败或未找到的条目时标题和图标一律用警告 / 错误色，绝不再用成功色（旧页面丢弃了 errors[]，失败也提示成功）。
 */
export default function SyncResultModal({ result, onClose }: SyncResultModalProps) {
  const { t } = useTranslation()
  const lastResultRef = useRef<SyncResultView | null>(result)
  if (result) {
    lastResultRef.current = result
  }
  const shown = result ?? lastResultRef.current

  const titles = {
    success: t('chinaSuppliers.syncTitleSuccess'),
    partial: t('chinaSuppliers.syncTitlePartial'),
    failed: t('chinaSuppliers.syncTitleFailed'),
  }
  const outcome = shown?.outcome ?? 'success'
  const titleIcon =
    outcome === 'success' ? (
      <CheckCircleFilled style={{ color: '#12a150' }} />
    ) : outcome === 'partial' ? (
      <ExclamationCircleFilled />
    ) : (
      <CloseCircleFilled />
    )
  const titleClass =
    outcome === 'partial' ? 'china-sup-sync-title china-sup-sync-title-warn' : outcome === 'failed' ? 'china-sup-sync-title china-sup-sync-title-error' : 'china-sup-sync-title'

  const handleCopy = () => {
    if (!shown) {
      return
    }
    void copyTextToClipboard(buildFailureClipboardText(shown), {
      successMessage: t('common.copySuccess'),
      failureMessage: t('common.copyFailed'),
    })
  }

  return (
    <Modal
      title={(
        <span className={titleClass}>
          {titleIcon}
          <span>{titles[outcome]}</span>
        </span>
      )}
      open={result !== null}
      width={560}
      destroyOnHidden
      // 结果只此一次，点遮罩误关会丢失失败明细；必须点「知道了」或右上角关闭。
      maskClosable={false}
      onCancel={onClose}
      data-testid="china-suppliers-sync-result"
      footer={(
        <div className="china-sup-sync-footer">
          {shown && shown.errors.length > 0 ? (
            <Button icon={<CopyOutlined />} onClick={handleCopy}>
              {t('chinaSuppliers.syncCopyFailures')}
            </Button>
          ) : (
            <span />
          )}
          <Button type="primary" onClick={onClose}>
            {t('chinaSuppliers.syncAcknowledge')}
          </Button>
        </div>
      )}
    >
      {shown ? (
        <>
          <div className="china-sup-sync-stats">
            <div data-testid="china-suppliers-sync-total">
              <div className="china-sup-sync-stat-key">{t('chinaSuppliers.syncStatTotal')}</div>
              <div className="china-sup-sync-stat-value">{shown.totalProcessed}</div>
            </div>
            <div data-testid="china-suppliers-sync-inserted">
              <div className="china-sup-sync-stat-key">{t('chinaSuppliers.syncStatInserted')}</div>
              <div className={shown.insertedCount > 0 ? 'china-sup-sync-stat-value china-sup-sync-stat-new' : 'china-sup-sync-stat-value'}>{shown.insertedCount}</div>
            </div>
            <div data-testid="china-suppliers-sync-updated">
              <div className="china-sup-sync-stat-key">{t('chinaSuppliers.syncStatUpdated')}</div>
              <div className={shown.updatedCount > 0 ? 'china-sup-sync-stat-value china-sup-sync-stat-updated' : 'china-sup-sync-stat-value'}>{shown.updatedCount}</div>
            </div>
            <div data-testid="china-suppliers-sync-failed">
              <div className="china-sup-sync-stat-key">{t('chinaSuppliers.syncStatFailed')}</div>
              <div className={shown.failCount > 0 ? 'china-sup-sync-stat-value china-sup-sync-stat-failed' : 'china-sup-sync-stat-value'}>{shown.failCount}</div>
            </div>
          </div>

          <div className="china-sup-sync-notices">
            {shown.outcome === 'success' ? (
              <Alert type="success" showIcon message={t('chinaSuppliers.syncAllSucceeded')} />
            ) : null}

            {shown.errors.length > 0 ? (
              <Alert
                type="error"
                showIcon
                role="alert"
                data-testid="china-suppliers-sync-failures"
                message={(
                  <ul className="china-sup-sync-failures">
                    {shown.errors.map((line, index) => {
                      const { label, reason } = parseSyncFailure(line)
                      return (
                        // 明细来自后端原文，同一供应商理论上只出现一次；带上下标兜底重复行。
                        <li key={`${index}-${line}`}>
                          {label ? (
                            <>
                              <strong>{label}</strong>
                              {reason ? t('chinaSuppliers.syncFailureSeparator') : null}
                            </>
                          ) : null}
                          {reason}
                        </li>
                      )
                    })}
                  </ul>
                )}
              />
            ) : shown.failCount > 0 ? (
              // 接口只给了失败数、没给明细：明确告知，而不是什么都不说。
              <Alert type="error" showIcon role="alert" message={t('chinaSuppliers.syncNoDetail', { count: shown.failCount })} />
            ) : null}

            {shown.unmatchedCount > 0 ? (
              <Alert type="warning" showIcon message={t('chinaSuppliers.syncUnmatched', { count: shown.unmatchedCount })} />
            ) : null}
          </div>
        </>
      ) : null}
    </Modal>
  )
}
