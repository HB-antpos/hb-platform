import { DownloadOutlined, PrinterOutlined, WarningOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Input, Modal, Segmented, Select, Spin, message } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { useTranslation } from 'react-i18next'
import { getPlan, getPlanVersion, listPlanVersions } from '../../../../services/planogramService'
import { formatUpdatedAt, planogramErrorText, resolvePlanogramError } from '../planogramLogic'
import { usePlanogramRequest } from '../usePlanogramRequest'
import { downloadPlanogramPdf, printPlanogramPdf, waitForPrintStageReady } from './exportPlanogramPdf'
import PlanogramPrintPages from './PlanogramPrintPages'
import { loadPdfImages } from './sheetImages'
import {
  buildHeaderPreviewLine,
  buildPdfFileName,
  buildPdfModel,
  DEFAULT_PDF_OPTIONS,
  hasPdfContent,
  makePdfText,
  normalizePdfFileName,
  pageFrameOf,
  PDF_PAPERS,
  planPdfPages,
  sourceFromDetail,
  sourceFromSnapshot,
  type PdfLang,
  type PdfModel,
  type PdfOptions,
  type PdfPaper,
  type PdfPlan,
  type PdfScaleMode,
} from './sheetLogic'
import { translatePdf } from './sheetText'
import './exportModal.css'

/** 缩略图宽度（px）：把第 1 页按纸张等比缩到这个宽度。 */
const THUMB_WIDTH = 276
const NO_IMAGES: ReadonlyMap<string, string> = new Map()

interface ExportPdfModalProps {
  planGuid: string
  planName: string
  /** 设计器里有未保存改动：「当前已保存版本」置灰（导出的是服务端已保存的内容），提示先保存。 */
  dirty?: boolean
  /** 预选的已发布版本（版本抽屉里点「导出 PDF」传入）；缺省 = 当前已保存版本。 */
  initialVersion?: number | null
  onClose: () => void
}

interface ExportJob {
  mode: 'download' | 'print'
  model: PdfModel
  plan: PdfPlan
  options: PdfOptions
  images: ReadonlyMap<string, string>
  fileName: string
  failedImages: number
}

type Phase = 'idle' | 'images' | 'render'

/**
 * 导出版面图 PDF 弹窗（设计图 04）：导出版本 / 纸张 / 比例 / 内容 / 语言 / 页眉预览 / 文件名 + 右侧第 1 页缩略图与合计；「下载 PDF」「打印」。
 * 页面 DOM 与缩略图是同一个 PlanogramPrintPages；出图时把全部页面挂到屏幕外容器，交给 printUtils 的分页 PDF（html2canvas / jspdf 动态加载，不进首屏）。
 * 数据来源统一是发布快照形状：已发布版本取 getPlanVersion；「当前已保存版本」取 getPlan 再组装成同形状。
 */
export default function ExportPdfModal({ planGuid, planName, dirty = false, initialVersion = null, onClose }: ExportPdfModalProps) {
  const { t, i18n } = useTranslation()
  const tRef = useRef(t)
  tRef.current = t
  const language = i18n.language

  // ---- 数据：当前已保存版本 + 已发布版本列表 + 选中的已发布版本快照 ----
  const currentRequest = usePlanogramRequest(`export-current:${planGuid}`, (signal) => getPlan(planGuid, signal))
  const versionsRequest = usePlanogramRequest(`export-versions:${planGuid}`, (signal) => listPlanVersions(planGuid, signal))
  const versions = useMemo(() => versionsRequest.data ?? [], [versionsRequest.data])

  const [choice, setChoice] = useState<'current' | number>(initialVersion ?? 'current')
  // 有未保存改动时不能选「当前已保存版本」：版本列表到了就自动换成最近一次发布的版本
  useEffect(() => {
    if (dirty && choice === 'current' && versions.length > 0) setChoice(versions[0].version)
  }, [choice, dirty, versions])
  const snapshotRequest = usePlanogramRequest(typeof choice === 'number' ? `export-snapshot:${planGuid}:${choice}` : null, (signal) => getPlanVersion(planGuid, choice as number, signal))

  const source = useMemo(() => {
    if (choice === 'current') return currentRequest.data ? sourceFromDetail(currentRequest.data) : null
    return snapshotRequest.data ? sourceFromSnapshot(snapshotRequest.data) : null
  }, [choice, currentRequest.data, snapshotRequest.data])
  const model = useMemo(() => (source ? buildPdfModel(source) : null), [source])

  const activeRequest = choice === 'current' ? currentRequest : snapshotRequest
  const loadError = useMemo(() => {
    if (!activeRequest.error) return null
    const resolved = resolvePlanogramError(activeRequest.error)
    return resolved.kind === 'error' ? planogramErrorText(resolved, language, (key) => t(key)) : null
  }, [activeRequest.error, language, t])

  // ---- 选项 ----
  const [options, setOptions] = useState<PdfOptions>(DEFAULT_PDF_OPTIONS)
  const patch = useCallback((next: Partial<PdfOptions>) => setOptions((current) => ({ ...current, ...next })), [])
  const plan = useMemo(() => (model ? planPdfPages(model, options) : null), [model, options])
  const text = useMemo(() => makePdfText(options.lang, translatePdf), [options.lang])
  const headerLine = useMemo(() => (model ? buildHeaderPreviewLine(model, text) : ''), [model, text])

  // 文件名：没改过就跟着版本 / 语言变化；用户改过就保持用户的
  const [fileNameDraft, setFileNameDraft] = useState<string | null>(null)
  const defaultFileName = useMemo(
    () => buildPdfFileName({ planName: model?.source.snapshot.plan.name ?? planName, version: model?.source.snapshot.version ?? 1, lang: options.lang }),
    [model, options.lang, planName],
  )
  const fileName = fileNameDraft ?? defaultFileName

  // ---- 出图 ----
  const [busy, setBusy] = useState<null | 'download' | 'print'>(null)
  const [phase, setPhase] = useState<Phase>('idle')
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null)
  const [job, setJob] = useState<ExportJob | null>(null)
  const stageRef = useRef<HTMLDivElement | null>(null)
  const abortRef = useRef<AbortController | null>(null)
  useEffect(() => () => abortRef.current?.abort(), [])

  const finish = useCallback(() => {
    setJob(null)
    setBusy(null)
    setPhase('idle')
    setProgress(null)
  }, [])

  const runExport = async (mode: 'download' | 'print') => {
    if (!model || !plan || busy || !hasPdfContent(options)) return
    setBusy(mode)
    const controller = new AbortController()
    abortRef.current = controller
    try {
      let images: ReadonlyMap<string, string> = NO_IMAGES
      let failedImages = 0
      if (options.board && options.images) {
        setPhase('images')
        const result = await loadPdfImages(model.source.snapshot.products, { signal: controller.signal, onProgress: (done, total) => setProgress({ done, total }) })
        images = result.images
        failedImages = result.failed.length
      }
      if (controller.signal.aborted) return
      setPhase('render')
      setJob({ mode, model, plan, options, images, fileName: normalizePdfFileName(fileName, defaultFileName), failedImages })
    } catch {
      message.error(t('planogram.export.errors.generate'))
      finish()
    }
  }

  useEffect(() => {
    if (!job) return undefined
    let cancelled = false
    void (async () => {
      try {
        await waitForPrintStageReady()
        const stage = stageRef.current
        if (!stage || cancelled) return
        const args = {
          stage,
          paper: job.plan.paper,
          layoutNotReadyErrorMessage: tRef.current('planogram.export.errors.notReady'),
          createCanvasContextErrorMessage: tRef.current('planogram.export.errors.canvas'),
        }
        if (job.mode === 'download') {
          await downloadPlanogramPdf({ ...args, fileName: job.fileName })
          if (!cancelled) message.success(tRef.current('planogram.export.downloaded', { name: job.fileName }))
        } else {
          await printPlanogramPdf(args)
        }
        if (!cancelled && job.failedImages > 0) message.warning(tRef.current('planogram.export.imagesFailed', { count: job.failedImages }))
      } catch (error) {
        console.error(error)
        if (!cancelled) message.error(tRef.current('planogram.export.errors.generate'))
      } finally {
        if (!cancelled) finish()
      }
    })()
    return () => {
      cancelled = true
    }
  }, [finish, job])

  const handleCancel = () => {
    abortRef.current?.abort()
    onClose()
  }

  // ---- 派生展示 ----
  // 缩略图：第 1 页（版面图页或清单页，两者逻辑尺寸可能不同）按宽度等比缩到 THUMB_WIDTH
  const firstFrame = plan && plan.pages[0] ? pageFrameOf(plan.paper, plan.pages[0].kind) : PDF_PAPERS[options.paper].board
  const thumbScale = THUMB_WIDTH / firstFrame.widthPx
  const conflictCount = model?.errorCount ?? 0
  const currentVersionLabel = currentRequest.data ? t('planogram.export.versionCurrent', { version: currentRequest.data.header.version }) : t('planogram.export.versionCurrentLoading')
  const versionOptions = [
    {
      value: 'current' as const,
      disabled: dirty,
      label: currentVersionLabel,
    },
    ...versions.map((entry) => ({
      value: entry.version,
      label: t('planogram.export.versionPublished', { version: entry.version, date: formatUpdatedAt(entry.publishedAt, true), by: entry.publishedBy || '—' }),
    })),
  ]
  const contentMissing = !hasPdfContent(options)
  const blockedByDirty = dirty && versions.length === 0 && !versionsRequest.loading
  const canExport = Boolean(model && plan) && !contentMissing && !blockedByDirty && !busy

  const boardRange = plan && plan.boardPageCount > 0 ? [1, plan.boardPageCount] : null
  const checklistRange = plan && plan.checklistPageCount > 0 ? [plan.boardPageCount + 1, plan.pageCount] : null
  const rangeText = (key: 'board' | 'checklist', range: number[]) => {
    const one = range[0] === range[1]
    if (key === 'board') return one ? t('planogram.export.previewBoardOne', { page: range[0] }) : t('planogram.export.previewBoard', { from: range[0], to: range[1] })
    return one
      ? t('planogram.export.previewChecklistOne', { page: range[0], rows: plan?.rowCount ?? 0 })
      : t('planogram.export.previewChecklist', { from: range[0], to: range[1], rows: plan?.rowCount ?? 0 })
  }

  const busyText = phase === 'images' ? t('planogram.export.progressImages', { done: progress?.done ?? 0, total: progress?.total ?? 0 }) : t('planogram.export.progressRender')

  return (
    <Modal
      open
      width={900}
      title={t('planogram.export.title', { name: planName })}
      onCancel={handleCancel}
      maskClosable={false}
      keyboard={!busy}
      destroyOnHidden
      className="planogram-export-modal"
      footer={
        <div className="planogram-export-footer">
          <span className="planogram-export-footer-note">
            {conflictCount > 0 ? (
              <span className="planogram-export-conflict"><WarningOutlined aria-hidden="true" /> {t('planogram.export.conflictWarn', { count: conflictCount })}</span>
            ) : null}
          </span>
          <Button onClick={handleCancel}>{t('planogram.common.cancel')}</Button>
          <Button icon={<PrinterOutlined />} disabled={!canExport} loading={busy === 'print'} onClick={() => void runExport('print')}>{t('planogram.export.print')}</Button>
          <Button type="primary" icon={<DownloadOutlined />} disabled={!canExport} loading={busy === 'download'} onClick={() => void runExport('download')}>{t('planogram.export.download')}</Button>
        </div>
      }
    >
      <div className="planogram-export">
        <div className="planogram-export-form">
          {dirty ? <Alert className="planogram-export-alert" type="warning" showIcon message={blockedByDirty ? t('planogram.export.dirtyNoVersion') : t('planogram.export.dirtyHint')} /> : null}
          {loadError ? <Alert className="planogram-export-alert" type="error" showIcon message={loadError} action={<a onClick={activeRequest.reload}>{t('planogram.common.retry')}</a>} /> : null}

          <div className="planogram-export-row">
            <label className="planogram-export-label" htmlFor="planogram-export-version">{t('planogram.export.version')}</label>
            <Select
              id="planogram-export-version"
              className="planogram-export-version"
              value={choice}
              onChange={(value) => setChoice(value)}
              options={versionOptions}
              loading={versionsRequest.loading || currentRequest.loading}
              disabled={Boolean(busy)}
            />
          </div>

          <div className="planogram-export-row">
            <span className="planogram-export-label">{t('planogram.export.paper')}</span>
            <Segmented<PdfPaper>
              value={options.paper}
              onChange={(value) => patch({ paper: value })}
              disabled={Boolean(busy)}
              aria-label={t('planogram.export.paper')}
              options={[
                { label: t('planogram.export.paperA3L'), value: 'a3-landscape' },
                { label: t('planogram.export.paperA4L'), value: 'a4-landscape' },
                { label: t('planogram.export.paperA4P'), value: 'a4-portrait' },
              ]}
            />
          </div>

          <div className="planogram-export-row">
            <span className="planogram-export-label">{t('planogram.export.scale')}</span>
            <Segmented<PdfScaleMode>
              value={options.scale}
              onChange={(value) => patch({ scale: value })}
              disabled={Boolean(busy) || !options.board}
              aria-label={t('planogram.export.scale')}
              options={[
                { label: options.scale === 'auto' && plan ? t('planogram.export.scaleAuto', { ratio: plan.boardLayout.ratio }) : t('planogram.export.scaleAutoShort'), value: 'auto' },
                { label: t('planogram.export.scale5'), value: '1:5' },
                { label: t('planogram.export.scale10'), value: '1:10' },
              ]}
            />
            {plan && options.board ? <span className="planogram-export-muted">{t('planogram.export.baysPerPage', { count: plan.boardLayout.baysPerPage })}</span> : null}
          </div>
          {plan && options.board && plan.boardLayout.adjusted ? (
            <div className="planogram-export-row is-note">
              <span className="planogram-export-label" />
              <span className="planogram-export-warn">{t('planogram.export.scaleAdjusted', { ratio: plan.boardLayout.ratio })}</span>
            </div>
          ) : null}

          <div className="planogram-export-row is-top">
            <span className="planogram-export-label">{t('planogram.export.content')}</span>
            <div className="planogram-export-checks" role="group" aria-label={t('planogram.export.content')}>
              <Checkbox checked={options.board} disabled={Boolean(busy)} onChange={(event) => patch({ board: event.target.checked })}>{t('planogram.export.contentBoard')}</Checkbox>
              <Checkbox checked={options.checklist} disabled={Boolean(busy)} onChange={(event) => patch({ checklist: event.target.checked })}>{t('planogram.export.contentChecklist')}</Checkbox>
              <Checkbox checked={options.bubbles} disabled={Boolean(busy) || !options.board} onChange={(event) => patch({ bubbles: event.target.checked })}>{t('planogram.export.contentBubbles')}</Checkbox>
              <Checkbox checked={options.prices} disabled={Boolean(busy) || !options.board} onChange={(event) => patch({ prices: event.target.checked })}>{t('planogram.export.contentPrices')}</Checkbox>
              <Checkbox checked={options.coords} disabled={Boolean(busy) || !options.board} onChange={(event) => patch({ coords: event.target.checked })}>{t('planogram.export.contentCoords')}</Checkbox>
              <Checkbox checked={options.images} disabled={Boolean(busy) || !options.board} onChange={(event) => patch({ images: event.target.checked })}>{t('planogram.export.contentImages')}</Checkbox>
              <Checkbox checked={options.barcode} disabled={Boolean(busy) || !options.checklist} onChange={(event) => patch({ barcode: event.target.checked })}>{t('planogram.export.contentBarcode')}</Checkbox>
              <Checkbox checked={options.checkColumn} disabled={Boolean(busy) || !options.checklist} onChange={(event) => patch({ checkColumn: event.target.checked })}>{t('planogram.export.contentCheck')}</Checkbox>
            </div>
          </div>
          {contentMissing ? (
            <div className="planogram-export-row is-note">
              <span className="planogram-export-label" />
              <span className="planogram-export-warn">{t('planogram.export.contentNeedOne')}</span>
            </div>
          ) : null}

          <div className="planogram-export-row">
            <span className="planogram-export-label">{t('planogram.export.lang')}</span>
            <Segmented<PdfLang>
              value={options.lang}
              onChange={(value) => patch({ lang: value })}
              disabled={Boolean(busy)}
              aria-label={t('planogram.export.lang')}
              options={[
                { label: t('planogram.export.langZh'), value: 'zh' },
                { label: t('planogram.export.langEn'), value: 'en' },
                { label: t('planogram.export.langBi'), value: 'bi' },
              ]}
            />
          </div>

          <div className="planogram-export-row is-top">
            <span className="planogram-export-label">{t('planogram.export.header')}</span>
            <span className="planogram-export-muted planogram-export-header-line">{headerLine || '—'}</span>
          </div>

          <div className="planogram-export-row">
            <label className="planogram-export-label" htmlFor="planogram-export-file">{t('planogram.export.fileName')}</label>
            <Input
              id="planogram-export-file"
              value={fileName}
              disabled={Boolean(busy)}
              maxLength={160}
              onChange={(event) => setFileNameDraft(event.target.value)}
              onBlur={() => {
                if (fileNameDraft !== null && !fileNameDraft.trim()) setFileNameDraft(null)
              }}
            />
          </div>

          <div className="planogram-export-note">{t('planogram.export.note')}</div>
        </div>

        <aside className="planogram-export-side" aria-label={t('planogram.export.previewAria')}>
          <div className="planogram-export-preview-card">
            <div className="planogram-export-preview-title">{t('planogram.export.previewTitle', { total: plan?.pageCount ?? 0 })}</div>
            {model && plan && plan.pageCount > 0 ? (
              <div className="planogram-export-thumb" style={{ width: THUMB_WIDTH, height: firstFrame.heightPx * thumbScale }} aria-hidden="true">
                <div style={{ width: firstFrame.widthPx, height: firstFrame.heightPx, transform: `scale(${thumbScale})`, transformOrigin: 'top left' }}>
                  <PlanogramPrintPages model={model} plan={plan} options={options} images={NO_IMAGES} translate={translatePdf} pageFilter={(page) => page.pageNo === 1} />
                </div>
              </div>
            ) : (
              <div className="planogram-export-thumb is-empty" style={{ width: THUMB_WIDTH, height: firstFrame.heightPx * thumbScale }}>
                {activeRequest.loading || (!model && !loadError) ? <Spin /> : <span className="planogram-export-muted">{t('planogram.export.previewEmpty')}</span>}
              </div>
            )}
          </div>
          {plan ? (
            <div className="planogram-export-preview-card is-plain">
              {boardRange ? <div>{rangeText('board', boardRange)}</div> : null}
              {checklistRange ? <div>{rangeText('checklist', checklistRange)}</div> : null}
            </div>
          ) : null}
          {model ? (
            <div className="planogram-export-stats">
              {t('planogram.export.previewStats', { sku: model.stats.skuCount, hooks: model.stats.itemCount, units: model.stats.unitCount })}
            </div>
          ) : null}
        </aside>
      </div>

      {busy ? (
        <div className="planogram-export-busy" role="status" aria-live="polite">
          <Spin size="small" /> <span>{busyText}</span>
        </div>
      ) : null}

      {/* 出图用的屏幕外容器：只在导出期间挂载（全部页面），截图完成立即卸载 */}
      {job
        ? createPortal(
            <div ref={stageRef} className="planogram-pdf-stage" aria-hidden="true">
              <PlanogramPrintPages model={job.model} plan={job.plan} options={job.options} images={job.images} translate={translatePdf} />
            </div>,
            document.body,
          )
        : null}
    </Modal>
  )
}
