import { EyeOutlined, FilePdfOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Empty, Spin, Tag } from 'antd'
import { useTranslation } from 'react-i18next'
import { listPlanVersions } from '../../../../services/planogramService'
import type { PlanogramVersionSummaryDto } from '../../../../types/planogram'
import { formatUpdatedAt, planogramErrorText, resolvePlanogramError } from '../planogramLogic'
import { usePlanogramRequest } from '../usePlanogramRequest'

interface VersionsDrawerProps {
  planGuid: string
  /** 当前对外的已发布版本（草稿里有未发布修改时，对外的仍是它）：标「当前发布」。 */
  publishedVersion: number | null
  /** 每变一次就重新取一遍（发布成功后版本列表要更新）。 */
  refreshKey: number
  /** 正在预览的版本（高亮）。 */
  previewVersion: number | null
  /** 正在加载哪个版本的快照（该行按钮转圈）。 */
  loadingVersion: number | null
  onPreview: (version: number) => void
  onExportPdf: (version: number) => void
  onClose: () => void
}

/** 版本历史抽屉：版本、发布时间、发布人、商品 / 挂钩 / 挂货；每行「预览」（只读，不影响当前草稿）与「导出 PDF」。 */
export default function VersionsDrawer({ planGuid, publishedVersion, refreshKey, previewVersion, loadingVersion, onPreview, onExportPdf, onClose }: VersionsDrawerProps) {
  const { t, i18n } = useTranslation()
  const request = usePlanogramRequest<PlanogramVersionSummaryDto[]>(`versions:${planGuid}:${refreshKey}`, (signal) => listPlanVersions(planGuid, signal))
  const versions = request.data ?? []
  const errorText = (() => {
    if (!request.error) return null
    const resolved = resolvePlanogramError(request.error)
    return resolved.kind === 'error' ? planogramErrorText(resolved, i18n.language, (key) => t(key)) : null
  })()
  return (
    <Drawer open title={t('planogram.versions.title')} width={440} onClose={onClose} destroyOnHidden className="planogram-versions-drawer">
      <p className="planogram-muted-text planogram-small planogram-versions-hint">{t('planogram.versions.hint')}</p>
      {errorText ? <Alert type="error" showIcon message={errorText} action={<a onClick={request.reload}>{t('planogram.common.retry')}</a>} /> : null}
      {request.loading && versions.length === 0 ? <div className="planogram-versions-loading"><Spin /></div> : null}
      {!request.loading && !errorText && versions.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('planogram.versions.empty')} /> : null}
      <ul className="planogram-versions-list">
        {versions.map((entry) => (
          <li key={entry.version} className={previewVersion === entry.version ? 'is-active' : undefined}>
            <button type="button" className="planogram-versions-main" onClick={() => onPreview(entry.version)} aria-label={t('planogram.versions.previewAria', { version: entry.version })}>
              <span className="planogram-versions-head">
                <b>{`v${entry.version}`}</b>
                {publishedVersion === entry.version ? <Tag color="green" className="planogram-mini-tag">{t('planogram.versions.current')}</Tag> : null}
                {previewVersion === entry.version ? <Tag color="blue" className="planogram-mini-tag">{t('planogram.versions.previewing')}</Tag> : null}
              </span>
              <span className="planogram-muted-text planogram-small">{t('planogram.versions.publishedAt', { time: formatUpdatedAt(entry.publishedAt, true), by: entry.publishedBy || '—' })}</span>
              <span className="planogram-muted-text planogram-small planogram-mono">{t('planogram.versions.counts', { sku: entry.skuCount, hooks: entry.itemCount, units: entry.unitCount.toLocaleString() })}</span>
            </button>
            <span className="planogram-versions-actions">
              <Button size="small" type="link" icon={<EyeOutlined />} loading={loadingVersion === entry.version} onClick={() => onPreview(entry.version)}>{t('planogram.versions.preview')}</Button>
              <Button size="small" type="link" icon={<FilePdfOutlined />} onClick={() => onExportPdf(entry.version)}>{t('planogram.versions.exportPdf')}</Button>
            </span>
          </li>
        ))}
      </ul>
    </Drawer>
  )
}
