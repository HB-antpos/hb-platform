import { Alert, Button, Modal } from 'antd'
import { useTranslation } from 'react-i18next'
import type { PlanogramStatsDto } from '../../../../types/planogram'
import { formatMoneyTotal } from '../planogramLogic'

/** 发布失败（有冲突）时列在弹窗里的一条：条目 guid（「定位」用）、标题（商品名 + 位置）、原因（冲突文案）。 */
export interface PublishConflictEntry {
  guid: string
  title: string
  reason: string
}

interface PublishModalProps {
  /** 将生成的版本号（= 当前草稿的版本号）。 */
  version: number
  /** 上一个已发布版本（没有 = null）。 */
  previousVersion: number | null
  /** 统计：商品 / 挂钩 / 挂货 / 零售总值 / 板面利用。 */
  stats: PlanogramStatsDto
  /** 不为 null = 不能发布，值是原因（有冲突 / 没有商品）。 */
  blockedReason: string | null
  busy: boolean
  /** 服务端拒绝发布（PLANOGRAM_HAS_CONFLICTS）后的冲突列表；非 null 时弹窗切换成「发布失败」视图。 */
  failure: { total: number; entries: PublishConflictEntry[] } | null
  onConfirm: () => void
  onClose: () => void
  /** 点「定位」：关闭弹窗并在画布上选中该条目。 */
  onLocate: (guid: string) => void
}

/** 列出的冲突条数上限（其余折叠成「还有 N 条」）。 */
const MAX_LISTED = 8

/** 发布确认弹窗：统计 + 「将生成版本 vN」；服务端因冲突拒绝时列出前几条并可「定位」。 */
export default function PublishModal({ version, previousVersion, stats, blockedReason, busy, failure, onConfirm, onClose, onLocate }: PublishModalProps) {
  const { t } = useTranslation()
  if (failure) {
    const listed = failure.entries.slice(0, MAX_LISTED)
    const rest = Math.max(0, failure.total - listed.length)
    return (
      <Modal
        open
        title={t('planogram.publish.failedTitle')}
        onCancel={onClose}
        footer={<Button type="primary" onClick={onClose}>{t('planogram.common.close')}</Button>}
        maskClosable={false}
        className="planogram-publish-modal"
      >
        <Alert type="error" showIcon message={t('planogram.publish.failedConflicts', { count: failure.total })} />
        <ul className="planogram-publish-conflicts">
          {listed.map((entry, index) => (
            <li key={`${entry.guid}-${index}`}>
              <div className="planogram-publish-conflict-text">
                <div className="planogram-publish-conflict-title">{entry.title}</div>
                <div className="planogram-muted-text planogram-small">{entry.reason}</div>
              </div>
              <Button size="small" type="link" onClick={() => onLocate(entry.guid)}>{t('planogram.publish.locate')}</Button>
            </li>
          ))}
        </ul>
        {rest > 0 ? <div className="planogram-muted-text planogram-small">{t('planogram.publish.moreConflicts', { count: rest })}</div> : null}
      </Modal>
    )
  }
  return (
    <Modal
      open
      title={t('planogram.publish.title')}
      okText={t('planogram.publish.confirm', { version })}
      cancelText={t('planogram.common.cancel')}
      okButtonProps={{ disabled: blockedReason !== null }}
      confirmLoading={busy}
      onOk={onConfirm}
      onCancel={busy ? undefined : onClose}
      maskClosable={false}
      keyboard={!busy}
      className="planogram-publish-modal"
    >
      {blockedReason ? <Alert type="warning" showIcon message={blockedReason} className="planogram-modal-alert" /> : null}
      <div className="planogram-publish-version">{t('planogram.publish.willCreate', { version })}</div>
      <dl className="planogram-publish-stats">
        <div><dt>{t('planogram.publish.stat.sku')}</dt><dd>{stats.skuCount}</dd></div>
        <div><dt>{t('planogram.publish.stat.hooks')}</dt><dd>{stats.itemCount}</dd></div>
        <div><dt>{t('planogram.publish.stat.units')}</dt><dd>{stats.unitCount}</dd></div>
        <div><dt>{t('planogram.publish.stat.retail')}</dt><dd>{formatMoneyTotal(stats.retailValue)}</dd></div>
        <div><dt>{t('planogram.publish.stat.fill')}</dt><dd>{`${stats.fillPercent}%`}</dd></div>
      </dl>
      <div className="planogram-muted-text planogram-small">
        {previousVersion ? t('planogram.publish.keepPrevious', { version: previousVersion }) : t('planogram.publish.firstTime')}
      </div>
    </Modal>
  )
}
