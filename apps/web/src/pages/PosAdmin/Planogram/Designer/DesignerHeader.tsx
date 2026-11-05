import { ArrowLeftOutlined, CloudUploadOutlined, CopyOutlined, EllipsisOutlined, FilePdfOutlined, HistoryOutlined, RedoOutlined, SaveOutlined, UndoOutlined } from '@ant-design/icons'
import { Button, Dropdown, Tag, Tooltip } from 'antd'
import { useTranslation } from 'react-i18next'
import type { PlanogramListItemDto } from '../../../../types/planogram'
import { planStatusView } from '../PlanList/planListLogic'
import { formatUpdatedAt } from '../planogramLogic'

interface DesignerHeaderProps {
  header: PlanogramListItemDto
  /** 正在编辑的方案名（未保存的名字也实时显示）。 */
  name: string
  dirty: boolean
  /** 页面级只读（已归档 / 没有编辑权限 / 窄屏）：不显示撤销重做和保存。 */
  readOnly: boolean
  canEdit: boolean
  /** Planogram.View：版本历史、导出 PDF。 */
  canView: boolean
  /** Planogram.Manage 且未归档：显示「发布」。 */
  canPublish: boolean
  /** 不为 null = 「发布」禁用，值是禁用原因（悬浮提示）。 */
  publishDisabledReason: string | null
  /** 正在预览某个历史版本：编辑类按钮（撤销 / 重做 / 另存为 / 保存 / 发布）全部隐藏。 */
  previewing: boolean
  canUndo: boolean
  canRedo: boolean
  saving: boolean
  exportingJson: boolean
  onBack: () => void
  onUndo: () => void
  onRedo: () => void
  onSaveAs: () => void
  onSave: () => void
  onPublish: () => void
  onVersions: () => void
  onExportPdf: () => void
  onExportJson: () => void
}

/**
 * 状态标签（颜色之外一定有文字）：草稿 / 已发布 vN / 已发布 vN · 有未发布修改 / 已归档。
 * 判定与方案列表共用 planStatusView（用 hasUnpublishedChanges：已发布方案被保存后回到草稿）。
 */
export function StatusTag({ header }: { header: Pick<PlanogramListItemDto, 'status' | 'publishedVersion' | 'hasUnpublishedChanges' | 'version'> }) {
  const { t } = useTranslation()
  const view = planStatusView(header)
  const color = view.tone === 'green' ? 'green' : view.tone === 'orange' ? 'orange' : undefined
  return <Tag color={color}>{t(view.longKey, { version: view.version ?? 0 })}</Tag>
}

/**
 * 设计器页头：返回、方案名 + 状态 / 版本标签、最后保存信息；
 * 右侧撤销 / 重做 / 另存为 / 版本 / 导出 PDF / 发布 / 保存 / 「⋯」（导出 JSON、版本、导出 PDF）。
 * 发布仅 Manage 且未归档；版本与导出 PDF 看查看权限；导出 JSON 看编辑权限。
 */
export default function DesignerHeader(props: DesignerHeaderProps) {
  const { header, name, dirty, readOnly, canEdit, canView, canPublish, publishDisabledReason, previewing, canUndo, canRedo, saving, exportingJson } = props
  const { onBack, onUndo, onRedo, onSaveAs, onSave, onPublish, onVersions, onExportPdf, onExportJson } = props
  const { t } = useTranslation()
  const title = name.trim() || t('planogram.designer.untitled')
  const status = planStatusView(header)
  // 已发布且没有未发布修改时，版本号已经写在状态标签里（已发布 v3），不再重复；草稿显示正在编辑的版本号
  const showVersionTag = header.status !== 1 && header.status !== 2
  const more = [
    canEdit ? { key: 'json', label: t('planogram.transfer.exportJson'), disabled: exportingJson, onClick: onExportJson } : null,
    canView ? { key: 'versions', label: t('planogram.versions.open'), onClick: onVersions } : null,
    canView ? { key: 'pdf', label: t('planogram.export.open'), onClick: onExportPdf } : null,
  ].filter((item): item is NonNullable<typeof item> => item !== null)
  const publishButton = (
    <Button type="default" icon={<CloudUploadOutlined />} disabled={publishDisabledReason !== null} onClick={onPublish}>
      {t('planogram.publish.button')}
    </Button>
  )
  return (
    <div className="planogram-designer-head">
      <div className="planogram-designer-title">
        <Tooltip title={t('planogram.designer.backToList')}>
          <Button type="text" icon={<ArrowLeftOutlined />} aria-label={t('planogram.designer.backToList')} onClick={onBack} />
        </Tooltip>
        <div className="planogram-designer-title-text">
          <h1>
            <span className="planogram-designer-title-name">{t('planogram.designer.titlePrefix', { name: title })}</span>
            <StatusTag header={header} />
            {showVersionTag ? <Tag>{`v${header.version}`}</Tag> : null}
            {dirty ? <Tag color="gold">{t('planogram.designer.unsavedTag')}</Tag> : null}
          </h1>
          <div className="planogram-muted-text planogram-small">
            {header.updatedAt
              ? t('planogram.designer.lastSaved', { time: formatUpdatedAt(header.updatedAt, true), by: header.updatedBy || '—' })
              : t('planogram.designer.neverSaved')}
            {dirty ? <span className="planogram-warn-text"> · {t('planogram.designer.hasUnsaved')}</span> : null}
            {status.hasUnpublishedChanges ? <span className="planogram-warn-text"> · {t('planogram.publish.hasUnpublishedHint', { version: status.version ?? 0 })}</span> : null}
          </div>
        </div>
      </div>
      <div className="planogram-designer-head-actions">
        {readOnly || previewing ? null : (
          <>
            <Tooltip title={`${t('planogram.designer.undo')} (Ctrl+Z)`}>
              <Button type="text" icon={<UndoOutlined />} disabled={!canUndo} aria-label={t('planogram.designer.undo')} onClick={onUndo} />
            </Tooltip>
            <Tooltip title={`${t('planogram.designer.redo')} (Ctrl+Shift+Z)`}>
              <Button type="text" icon={<RedoOutlined />} disabled={!canRedo} aria-label={t('planogram.designer.redo')} onClick={onRedo} />
            </Tooltip>
          </>
        )}
        {canEdit && !previewing ? <Button icon={<CopyOutlined />} onClick={onSaveAs}>{t('planogram.designer.saveAs')}</Button> : null}
        {canView ? <Button icon={<HistoryOutlined />} onClick={onVersions}>{t('planogram.versions.button')}</Button> : null}
        {canView ? <Button icon={<FilePdfOutlined />} onClick={onExportPdf}>{t('planogram.export.button')}</Button> : null}
        {canPublish && !previewing ? (
          publishDisabledReason !== null ? <Tooltip title={publishDisabledReason}><span>{publishButton}</span></Tooltip> : publishButton
        ) : null}
        {readOnly || previewing ? null : (
          <Button type="primary" icon={<SaveOutlined />} loading={saving} disabled={!dirty} onClick={onSave}>
            {t('planogram.designer.save')}
          </Button>
        )}
        {more.length > 0 ? (
          <Dropdown trigger={['click']} menu={{ items: more.map(({ key, label, disabled, onClick }) => ({ key, label, disabled, onClick })) }}>
            <Button type="text" icon={<EllipsisOutlined />} aria-label={t('planogram.designer.more')} />
          </Dropdown>
        ) : null}
      </div>
    </div>
  )
}
