import { ArrowLeftOutlined, ExpandOutlined, FilePdfOutlined, MinusOutlined, PlusOutlined } from '@ant-design/icons'
import { Alert, Button } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { PlanogramSnapshotDto } from '../../../../types/planogram'
import { buildPlaced } from '../engine/geometry'
import { buildPdfModel, sourceFromSnapshot } from '../export/sheetLogic'
import { formatUpdatedAt } from '../planogramLogic'
import BoardCanvas, { type BoardCanvasHandle, type BoardCanvasLabels } from './BoardCanvas'
import { clampZoom, MAX_ZOOM, MIN_ZOOM, ZOOM_STEP } from './designerLogic'
import type { DisplayConflict } from './useDesignerLayout'

interface VersionPreviewProps {
  snapshot: PlanogramSnapshotDto
  language: string
  /** 与设计器主画布同一套画布文案。 */
  labels: BoardCanvasLabels
  conflictText: (conflict: DisplayConflict) => string
  onBack: () => void
  onExportPdf: () => void
}

const noop = () => undefined
const noHint = () => ({ ok: true, line1: '' })

/**
 * 历史版本只读预览：用 getPlanVersion 的快照数据，在同一个 BoardCanvas 的只读模式里显示（占位几何与设计器同一套）。
 * 不进入编辑、不碰当前草稿的 store（设计器主体只是被隐藏，返回后原样还在）；顶部横幅说明版本信息，可返回当前草稿或导出该版本 PDF。
 */
export default function VersionPreview({ snapshot, language, labels, conflictText, onBack, onExportPdf }: VersionPreviewProps) {
  const { t } = useTranslation()
  // 快照 → 画布输入：与 PDF 同一个模型（布局上下文、商品行、冲突、序号），不再另组一份
  const model = useMemo(() => buildPdfModel(sourceFromSnapshot(snapshot)), [snapshot])
  const placed = useMemo(() => (model.ctx ? buildPlaced(model.ctx, model.items) : []), [model])
  const [zoom, setZoom] = useState(40)
  const [selectedGuid, setSelectedGuid] = useState<string | null>(null)
  const canvasRef = useRef<BoardCanvasHandle | null>(null)

  const fit = useCallback(() => {
    const value = canvasRef.current?.fitZoom()
    if (value !== undefined) setZoom(clampZoom(value))
  }, [])
  // 首次显示后适应窗口（容器此刻必须可见，fitZoom 才有尺寸）
  useEffect(() => {
    const frame = window.requestAnimationFrame(fit)
    return () => window.cancelAnimationFrame(frame)
  }, [fit, snapshot])

  return (
    <div className="planogram-designer-preview">
      <Alert
        type="info"
        showIcon
        message={t('planogram.versions.previewBanner', { version: snapshot.version, time: formatUpdatedAt(snapshot.publishedAt, true), by: snapshot.publishedBy || '—' })}
        description={t('planogram.versions.previewNote')}
        action={
          <span className="planogram-designer-preview-actions">
            <Button size="small" icon={<FilePdfOutlined />} onClick={onExportPdf}>{t('planogram.versions.exportThis')}</Button>
            <Button size="small" type="primary" icon={<ArrowLeftOutlined />} onClick={onBack}>{t('planogram.versions.backToDraft')}</Button>
          </span>
        }
      />
      <div className="planogram-designer-toolbar" role="toolbar" aria-label={t('planogram.designer.toolbar.aria')}>
        <span className="planogram-designer-toolbar-label">{t('planogram.designer.toolbar.zoom')}</span>
        <span className="planogram-designer-stepper" role="group" aria-label={t('planogram.designer.toolbar.zoom')}>
          <button type="button" disabled={zoom <= MIN_ZOOM} aria-label={t('planogram.designer.decrease', { name: t('planogram.designer.toolbar.zoom') })} onClick={() => setZoom((value) => clampZoom(value - ZOOM_STEP))}><MinusOutlined aria-hidden="true" /></button>
          <output aria-live="polite">{`${zoom}%`}</output>
          <button type="button" disabled={zoom >= MAX_ZOOM} aria-label={t('planogram.designer.increase', { name: t('planogram.designer.toolbar.zoom') })} onClick={() => setZoom((value) => clampZoom(value + ZOOM_STEP))}><PlusOutlined aria-hidden="true" /></button>
        </span>
        <Button size="small" type="text" icon={<ExpandOutlined />} onClick={fit}>{t('planogram.designer.toolbar.fit')}</Button>
        <span className="planogram-designer-toolbar-end planogram-muted-text planogram-small">
          {t('planogram.versions.previewStats', { sku: snapshot.stats.skuCount, hooks: snapshot.stats.itemCount, units: snapshot.stats.unitCount })}
        </span>
      </div>
      <div className="planogram-designer-canvas-card">
        <BoardCanvas
          ref={canvasRef}
          ctx={model.ctx}
          items={model.items}
          placed={placed}
          specs={model.specs}
          conflictsByGuid={model.conflictsByGuid}
          selectedGuid={selectedGuid}
          zoom={zoom}
          showHoles
          showRulers
          showTags
          snapMode="hole"
          readOnly
          language={language}
          labels={labels}
          conflictText={conflictText}
          externalHint={null}
          describeHint={noHint}
          onSelect={setSelectedGuid}
          onMoveItem={noop}
          onRejectMove={noop}
          onCopyRight={noop}
          onDistribute={noop}
          onRemove={noop}
          onZoomWheel={(direction) => setZoom((value) => clampZoom(value + direction * ZOOM_STEP))}
          onTabSelect={() => false}
          emptyText={t('planogram.designer.canvas.noShelf')}
        />
      </div>
    </div>
  )
}
