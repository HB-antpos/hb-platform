// 导出排班图片弹窗：选内容（午饭标识 / 工时 / 草稿）+ 实时预览 + 下载 PNG / 复制图片。
// 图片由导出模型直接排版（见 scheduleExportImage.ts），年龄等管理者信息在模型里就不存在，这里也没有对应开关。
import { useMemo, useState } from 'react'
import { Alert, Button, Checkbox, Modal, Space, message } from 'antd'
import { CopyOutlined, DownloadOutlined, LockOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'

import { DEFAULT_EXPORT_OPTIONS } from './scheduleExportLogic'
import type { ExportModel, ExportOptions } from './scheduleExportLogic'
import {
  canCopyImage,
  copyPngToClipboard,
  createCanvasMeasure,
  downloadBlob,
  pickScale,
  renderScheduleExportSvg,
  renderSvgToPngBlob,
  svgToDataUrl,
} from './scheduleExportImage'

interface ScheduleExportModalProps {
  open: boolean
  onClose: () => void
  /** 按当前选项生成导出模型（名字、颜色、文案由页面提供）。 */
  buildModel: (options: ExportOptions) => ExportModel
  fileName: string
  /** 当前「昵称 / 全名」开关对应的文字，用来告诉用户名字跟随它。 */
  nameModeLabel: string
}

export default function ScheduleExportModal({ open, onClose, buildModel, fileName, nameModeLabel }: ScheduleExportModalProps) {
  const { t } = useTranslation()
  const [options, setOptions] = useState<ExportOptions>(DEFAULT_EXPORT_OPTIONS)
  const [busy, setBusy] = useState<'download' | 'copy' | null>(null)
  const measure = useMemo(() => createCanvasMeasure(), [])

  // 弹窗关着时不排版；选项一变就重排并刷新预览
  const model = useMemo(() => (open ? buildModel(options) : null), [open, buildModel, options])
  const rendered = useMemo(() => (model ? renderScheduleExportSvg(model, measure) : null), [model, measure])
  const previewUrl = useMemo(() => (rendered ? svgToDataUrl(rendered.svg) : ''), [rendered])
  const empty = model ? model.days.every((day) => day.cards.length === 0) : true
  const scale = rendered ? pickScale(rendered.width, rendered.height) : 2

  const makePng = async () => {
    if (!rendered) throw new Error('nothing to export')
    return renderSvgToPngBlob(rendered.svg, rendered.width, rendered.height)
  }
  const handleDownload = async () => {
    setBusy('download')
    try {
      downloadBlob(await makePng(), fileName)
      message.success(t('posAdmin.scheduleAttendance.export.downloaded'))
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.export.failed'))
    } finally {
      setBusy(null)
    }
  }
  const handleCopy = async () => {
    setBusy('copy')
    try {
      await copyPngToClipboard(await makePng())
      message.success(t('posAdmin.scheduleAttendance.export.copied'))
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.export.copyFailed'))
    } finally {
      setBusy(null)
    }
  }
  const setOption = (patch: Partial<ExportOptions>) => setOptions((prev) => ({ ...prev, ...patch }))

  return (
    <Modal
      open={open}
      onCancel={onClose}
      title={t('posAdmin.scheduleAttendance.export.title')}
      width={920}
      destroyOnClose
      footer={(
        <div className="sce-export-footer">
          <span className="sce-export-size">
            {rendered ? t('posAdmin.scheduleAttendance.export.sizeNote', { width: Math.round(rendered.width * scale), height: Math.round(rendered.height * scale) }) : null}
          </span>
          <Space>
            {canCopyImage() ? (
              <Button icon={<CopyOutlined />} disabled={empty} loading={busy === 'copy'} onClick={() => void handleCopy()}>
                {t('posAdmin.scheduleAttendance.export.copy')}
              </Button>
            ) : null}
            <Button type="primary" icon={<DownloadOutlined />} disabled={empty} loading={busy === 'download'} onClick={() => void handleDownload()}>
              {t('posAdmin.scheduleAttendance.export.download')}
            </Button>
          </Space>
        </div>
      )}
    >
      <div className="sce-export-body">
        <div className="sce-export-options">
          <div className="sce-export-group">
            <h4>{t('posAdmin.scheduleAttendance.export.content')}</h4>
            <Checkbox checked={options.showMeal} onChange={(event) => setOption({ showMeal: event.target.checked })}>
              {t('posAdmin.scheduleAttendance.export.optMeal')}
            </Checkbox>
            <Checkbox checked={options.showHours} onChange={(event) => setOption({ showHours: event.target.checked })}>
              {t('posAdmin.scheduleAttendance.export.optHours')}
              <small>{t('posAdmin.scheduleAttendance.export.optHoursHint')}</small>
            </Checkbox>
            <Checkbox
              checked={options.includeDrafts}
              disabled={(model?.draftCount ?? 0) === 0}
              onChange={(event) => setOption({ includeDrafts: event.target.checked })}
            >
              {t('posAdmin.scheduleAttendance.export.optDrafts')}
              <small>{(model?.draftCount ?? 0) === 0 ? t('posAdmin.scheduleAttendance.export.noDrafts') : t('posAdmin.scheduleAttendance.export.optDraftsHint', { count: model?.draftCount ?? 0 })}</small>
            </Checkbox>
          </div>
          <div className="sce-export-group">
            <h4>{t('posAdmin.scheduleAttendance.export.name')}</h4>
            <span>{t('posAdmin.scheduleAttendance.export.nameFollow', { mode: nameModeLabel })}</span>
          </div>
          <div className="sce-export-privacy">
            <LockOutlined />
            <span>{t('posAdmin.scheduleAttendance.export.privacy')}</span>
          </div>
        </div>
        <div className="sce-export-preview">
          {empty ? <Alert type="info" showIcon message={t('posAdmin.scheduleAttendance.export.empty')} /> : (
            <img src={previewUrl} alt={t('posAdmin.scheduleAttendance.export.title')} data-testid="schedule-export-preview" />
          )}
        </div>
      </div>
    </Modal>
  )
}
