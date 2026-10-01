import { MinusOutlined, PlusOutlined, PrinterOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Modal, Select, Space, Spin, Tag, Typography, message } from 'antd'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import {
  getAssignment,
  listPickerCandidates,
  previewAssignment,
  readAssignmentErrorCode,
  saveAssignment,
  type AssignmentPreview,
  type AssignmentSummary,
  type PickerCandidate,
} from '../../../../services/warehousePickingAssignmentService'

import {
  MAX_SEGMENTS,
  adjustSegmentCount,
  assignSegmentPicker,
  evenSegmentCounts,
  resizeSegmentPickers,
  segmentColor,
} from './pickingAssignmentLogic'
import './messages'
import './pickingAssignment.css'

const PREVIEW_DEBOUNCE_MS = 250
const DEFAULT_SEGMENTS = 2

interface AssignPickingModalProps {
  open: boolean
  orderGuid: string
  orderNo?: string | null
  storeName?: string | null
  onClose: () => void
  /** 保存成功；print 为 true 时调用方接着打开分单打印。 */
  onSaved: (summary: AssignmentSummary, print: boolean) => void
}

/**
 * 分配拣货窗口：先定分几份，后端按 M 型走位把品种切成连续段；每段可指定员工，
 * 不指定的段打印分单后由员工扫码领取。经理可加减每段品种数（只移动相邻两段的边界），
 * 保存的是预览出来的逐行归属。
 */
export default function AssignPickingModal({ open, orderGuid, orderNo, storeName, onClose, onSaved }: AssignPickingModalProps) {
  const { t } = useTranslation()
  const [candidates, setCandidates] = useState<PickerCandidate[]>([])
  const [knownNames, setKnownNames] = useState<Record<string, string>>({})
  const [loadingCandidates, setLoadingCandidates] = useState(false)
  // 每段的员工（按段号顺序），null 表示待扫码领取。
  const [segmentPickers, setSegmentPickers] = useState<(string | null)[]>([])
  // null 表示按品种数平均分；经理加减后才变成具体各段品种数。
  const [counts, setCounts] = useState<readonly number[] | null>(null)
  const [preview, setPreview] = useState<AssignmentPreview | null>(null)
  const [previewing, setPreviewing] = useState(false)
  const [previewError, setPreviewError] = useState<string | null>(null)
  const [saving, setSaving] = useState<false | 'save' | 'print'>(false)
  const [previewNonce, setPreviewNonce] = useState(0)
  const previewAbortRef = useRef<AbortController | null>(null)

  // 打开时加载候选员工与现有分配；已有完整分配时带出原来的份数、各段员工与各段品种数。
  useEffect(() => {
    if (!open) return
    const controller = new AbortController()
    setLoadingCandidates(true)
    setPreview(null)
    setPreviewError(null)
    Promise.all([listPickerCandidates(controller.signal), getAssignment(orderGuid, controller.signal)])
      .then(([pickers, summary]) => {
        setCandidates(pickers)
        const names: Record<string, string> = {}
        pickers.forEach((picker) => (names[picker.pickerUserGuid] = picker.pickerName))
        summary?.assignees.forEach((assignee) => {
          if (assignee.pickerUserGuid && assignee.pickerName) names[assignee.pickerUserGuid] ??= assignee.pickerName
        })
        setKnownNames(names)
        const assignees = [...(summary?.assignees ?? [])].sort((a, b) => a.segmentNo - b.segmentNo)
        if (assignees.length > 0) {
          setSegmentPickers(assignees.map((assignee) => assignee.pickerUserGuid ?? null))
          setCounts(summary?.unassignedLineCount === 0 ? assignees.map((assignee) => assignee.lineCount) : null)
        } else {
          setSegmentPickers(resizeSegmentPickers([], DEFAULT_SEGMENTS))
          setCounts(null)
        }
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        message.error(error instanceof Error ? error.message : t('storeOrders.pickingAssignment.loadPickersFailed', '加载员工失败'))
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoadingCandidates(false)
      })
    return () => controller.abort()
  }, [open, orderGuid, t])

  // 份数、各段员工或品种数变化后防抖重新预览；后发的请求会取消先发的，避免旧结果覆盖新结果。
  useEffect(() => {
    if (!open || segmentPickers.length === 0) return
    previewAbortRef.current?.abort()
    const controller = new AbortController()
    previewAbortRef.current = controller
    const timer = window.setTimeout(() => {
      setPreviewing(true)
      previewAssignment(
        orderGuid,
        segmentPickers.map((pickerUserGuid, index) => ({ pickerUserGuid, lineCount: counts ? counts[index] : null })),
        controller.signal,
      )
        .then((result) => {
          setPreview(result)
          setPreviewError(null)
        })
        .catch((error: unknown) => {
          if (controller.signal.aborted) return
          if (readAssignmentErrorCode(error) === 'ASSIGN_COUNTS_INVALID' && counts) {
            // 订单品种数变了：回到平均分重新预览。
            setCounts(null)
            return
          }
          setPreview(null)
          setPreviewError(error instanceof Error ? error.message : t('storeOrders.pickingAssignment.previewFailed', '预览失败'))
        })
        .finally(() => {
          if (!controller.signal.aborted) setPreviewing(false)
        })
    }, PREVIEW_DEBOUNCE_MS)
    return () => {
      window.clearTimeout(timer)
      controller.abort()
    }
  }, [counts, open, orderGuid, previewNonce, segmentPickers, t])

  const pickerOptions = useMemo(
    () =>
      candidates.map((candidate) => ({
        value: candidate.pickerUserGuid,
        label: candidate.pickerName,
        hint: `${candidate.roleLabel || '—'} · ${t('storeOrders.pickingAssignment.activeOrders', '在拣 {{count}} 单', { count: candidate.activeOrderCount })}`,
      })),
    [candidates, t],
  )

  const currentCounts = counts ?? preview?.segments.map((segment) => segment.lineCount) ?? []
  const segments = preview?.segments ?? []
  const assignedTotal = segments.reduce((sum, segment) => sum + segment.lineCount, 0)
  const lineCount = preview?.lineCount ?? 0
  // 各段恰好等于平均分时显示“平均分”（打开已有分配时也一样），否则显示“已调整”。
  const isEvenSplit = currentCounts.join(',') === evenSegmentCounts(lineCount, currentCounts.length).join(',')
  const claimableCount = segmentPickers.filter((picker) => !picker).length

  const changeSegmentCount = (next: number) => {
    setSegmentPickers((current) => resizeSegmentPickers(current, next))
    setCounts(null)
  }

  const step = (index: number, delta: 1 | -1) => {
    const next = adjustSegmentCount(currentCounts, index, delta)
    if (next !== currentCounts) setCounts(next)
  }

  const save = async (print: boolean) => {
    if (!preview) return
    setSaving(print ? 'print' : 'save')
    try {
      const summary = await saveAssignment(
        orderGuid,
        preview.segments
          .filter((segment) => segment.detailGuids.length > 0)
          .map((segment) => ({ pickerUserGuid: segment.pickerUserGuid ?? null, detailGuids: segment.detailGuids })),
      )
      message.success(t('storeOrders.pickingAssignment.saved', '已保存拣货分配'))
      onSaved(summary, print)
    } catch (error) {
      if (readAssignmentErrorCode(error) === 'ASSIGN_LINES_INVALID') {
        message.warning(t('storeOrders.pickingAssignment.linesChanged', '订单明细刚有变化，已重新预览，请确认后再保存'))
        setPreviewNonce((value) => value + 1)
      } else {
        message.error(error instanceof Error ? error.message : t('storeOrders.pickingAssignment.saveFailed', '保存失败'))
      }
    } finally {
      setSaving(false)
    }
  }

  const irregularSegments = segments
    .map((segment, index) => ({ segment, segmentNo: index + 1 }))
    .filter(({ segment }) => segment.unlocatedLineCount > 0 || segment.irregularLineCount > 0)

  return (
    <Modal
      open={open}
      width={920}
      title={
        <div>
          <div>{t('storeOrders.pickingAssignment.title', '分配拣货')}</div>
          <Typography.Text type="secondary" style={{ fontSize: 12, fontWeight: 400 }}>
            {[orderNo, storeName, preview ? t('storeOrders.pickingAssignment.orderSize', '{{lines}} 个品种 · {{pieces}} 件', { lines: preview.lineCount, pieces: preview.pieces }) : null]
              .filter(Boolean)
              .join(' · ')}
          </Typography.Text>
        </div>
      }
      onCancel={onClose}
      destroyOnClose
      footer={
        <div className="picking-assign-footer">
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {t('storeOrders.pickingAssignment.replaceHint', '保存后整单替换原有分配；已打印的旧分单会失效。')}
          </Typography.Text>
          <Space>
            <Button onClick={onClose}>{t('common.cancel', '取消')}</Button>
            <Button loading={saving === 'save'} disabled={!preview || previewing || Boolean(saving)} onClick={() => void save(false)}>
              {t('storeOrders.pickingAssignment.saveOnly', '仅保存')}
            </Button>
            <Button
              type="primary"
              icon={<PrinterOutlined />}
              loading={saving === 'print'}
              disabled={!preview || previewing || Boolean(saving)}
              onClick={() => void save(true)}
            >
              {t('storeOrders.pickingAssignment.saveAndPrint', '保存并打印分单')}
            </Button>
          </Space>
        </div>
      }
    >
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        <Alert
          type="info"
          showIcon
          message={t(
            'storeOrders.pickingAssignment.intro',
            '按 M 型走位顺序把品种平均切成连续的几段。可以给某段直接指定员工，不指定的段打印分单后由员工扫码领取。分配只做引导，其他人仍可一起拣。',
          )}
        />
        {loadingCandidates ? (
          <div className="picking-assign-center"><Spin /></div>
        ) : (
          <div className="picking-assign-preview">
            <div className="picking-assign-heading">
              <Space size={8} align="center">
                <strong>{t('storeOrders.pickingAssignment.splitInto', '分成')}</strong>
                <span className="picking-assign-stepper">
                  <Button
                    size="small"
                    aria-label={t('storeOrders.pickingAssignment.fewerSegments', '少分一份')}
                    icon={<MinusOutlined />}
                    disabled={segmentPickers.length <= 1}
                    onClick={() => changeSegmentCount(segmentPickers.length - 1)}
                  />
                  <span className="picking-assign-count">{segmentPickers.length}</span>
                  <Button
                    size="small"
                    aria-label={t('storeOrders.pickingAssignment.moreSegments', '多分一份')}
                    icon={<PlusOutlined />}
                    disabled={segmentPickers.length >= MAX_SEGMENTS}
                    onClick={() => changeSegmentCount(segmentPickers.length + 1)}
                  />
                </span>
                <strong>{t('storeOrders.pickingAssignment.segmentsUnit', '份')}</strong>
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {claimableCount > 0
                    ? t('storeOrders.pickingAssignment.claimableCount', '{{count}} 份待员工扫码领取', { count: claimableCount })
                    : t('storeOrders.pickingAssignment.allAssigned', '每份都已指定员工')}
                </Typography.Text>
              </Space>
              <Space size={4}>
                <Tag color={isEvenSplit ? 'blue' : 'orange'}>
                  {isEvenSplit ? t('storeOrders.pickingAssignment.even', '平均分') : t('storeOrders.pickingAssignment.adjusted', '已调整')}
                </Tag>
                <Button type="link" size="small" disabled={isEvenSplit} onClick={() => setCounts(null)}>
                  {t('storeOrders.pickingAssignment.resetEven', '恢复平均')}
                </Button>
              </Space>
            </div>

            {previewError ? (
              <Alert type="error" showIcon message={previewError} />
            ) : !preview ? (
              <div className="picking-assign-center"><Spin /></div>
            ) : preview.lineCount === 0 ? (
              <Empty description={t('storeOrders.pickingAssignment.noLines', '订单没有明细')} />
            ) : (
              <Spin spinning={previewing}>
                <div className="picking-assign-bar" role="img" aria-label={t('storeOrders.pickingAssignment.barLabel', '各段品种数占比')}>
                  {segments.map((segment, index) => (
                    <div
                      key={index}
                      style={{
                        flexGrow: Math.max(segment.lineCount, 0.0001),
                        background: segmentColor(index + 1),
                        display: segment.lineCount > 0 ? 'flex' : 'none',
                      }}
                    >
                      {index + 1} {segment.pickerName || t('storeOrders.pickingAssignment.claimable', '待领取')} · {segment.lineCount}
                    </div>
                  ))}
                </div>
                <div className="picking-assign-segments">
                  {segments.map((segment, index) => {
                    const color = segmentColor(index + 1)
                    const canAdd = adjustSegmentCount(currentCounts, index, 1) !== currentCounts
                    const canRemove = adjustSegmentCount(currentCounts, index, -1) !== currentCounts
                    const pickerUserGuid = segmentPickers[index] ?? null
                    return (
                      <div key={index} className="picking-assign-segment">
                        <span className="picking-assign-edge" style={{ background: color }} />
                        <span className="picking-assign-badge" style={{ background: color }}>{index + 1}</span>
                        <Select
                          className="picking-assign-segment-picker"
                          allowClear
                          showSearch
                          optionFilterProp="label"
                          placeholder={t('storeOrders.pickingAssignment.claimPlaceholder', '扫码领取（不指定）')}
                          aria-label={t('storeOrders.pickingAssignment.segmentPicker', '第 {{no}} 段员工', { no: index + 1 })}
                          value={pickerUserGuid ?? undefined}
                          options={pickerOptions}
                          optionRender={(option) => (
                            <Space>
                              <span>{option.label}</span>
                              <Typography.Text type="secondary" style={{ fontSize: 12 }}>{option.data.hint}</Typography.Text>
                            </Space>
                          )}
                          labelRender={(option) => knownNames[String(option.value)] ?? option.label}
                          onChange={(value?: string) => setSegmentPickers((current) => assignSegmentPicker(current, index, value ?? null))}
                        />
                        <span className="picking-assign-stepper">
                          <Button
                            size="small"
                            aria-label={t('storeOrders.pickingAssignment.removeOne', '减一个品种')}
                            icon={<MinusOutlined />}
                            disabled={!canRemove || previewing}
                            onClick={() => step(index, -1)}
                          />
                          <span className="picking-assign-count">{segment.lineCount}</span>
                          <Button
                            size="small"
                            aria-label={t('storeOrders.pickingAssignment.addOne', '加一个品种')}
                            icon={<PlusOutlined />}
                            disabled={!canAdd || previewing}
                            onClick={() => step(index, 1)}
                          />
                        </span>
                        <span className="picking-assign-meta">
                          {t('storeOrders.pickingAssignment.segmentPieces', '品种 · {{pieces}} 件', { pieces: segment.pieces })}
                        </span>
                        <span className="picking-assign-route">
                          {segment.firstLocation ? `${segment.firstLocation} → ${segment.lastLocation}` : t('storeOrders.pickingAssignment.noLocationRoute', '无货位，按商品找')}
                        </span>
                      </div>
                    )
                  })}
                </div>
                {irregularSegments.map(({ segment, segmentNo }) => (
                  <Alert
                    key={segmentNo}
                    type="warning"
                    showIcon
                    style={{ marginTop: 8 }}
                    message={t(
                      'storeOrders.pickingAssignment.irregularWarning',
                      '第 {{segmentNo}} 段含 {{unlocated}} 个无货位、{{irregular}} 个编码不规范的品种，需要按商品找货',
                      { segmentNo, unlocated: segment.unlocatedLineCount, irregular: segment.irregularLineCount },
                    )}
                  />
                ))}
                <div className={`picking-assign-total${assignedTotal === lineCount ? ' is-complete' : ''}`}>
                  {t('storeOrders.pickingAssignment.total', '合计 {{assigned}} / {{total}} 个品种', { assigned: assignedTotal, total: lineCount })}
                  <Typography.Text type="secondary" style={{ fontSize: 12, marginLeft: 8 }}>
                    {t('storeOrders.pickingAssignment.stepHint', '加减会与相邻一段交换边界上的品种，合计不变')}
                  </Typography.Text>
                </div>
              </Spin>
            )}
          </div>
        )}
      </Space>
    </Modal>
  )
}
