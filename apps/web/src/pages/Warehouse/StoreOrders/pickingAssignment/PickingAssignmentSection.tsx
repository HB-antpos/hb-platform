import { LoadingOutlined, MoreOutlined, PrinterOutlined, ReloadOutlined, TeamOutlined, UndoOutlined } from '@ant-design/icons'
import { Button, Dropdown, Empty, Modal, Progress, Select, Space, Spin, Tag, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'

import { MeasuredTable } from '../../../../components/MeasuredTable'
import {
  clearAssignment,
  getAssignment,
  listPickerCandidates,
  setSegmentPicker,
  type Assignee,
  type AssignmentSummary,
  type PickerCandidate,
} from '../../../../services/warehousePickingAssignmentService'

import AssignPickingModal from './AssignPickingModal'
import { assigneeStatus, formatUtcShort, segmentColor, shouldShowPickingAssignmentSection } from './pickingAssignmentLogic'
import './messages'
import './pickingAssignment.css'

/** 每行负责人：订单明细表“负责人”列用；pickerName 为空表示该段待员工扫码领取。 */
export type LineAssigneeMap = Record<string, { segmentNo: number; pickerName: string | null }>

export function pickingSlipsPath(orderGuids: string[], segmentNo?: number) {
  const params = new URLSearchParams({ orders: orderGuids.join(',') })
  if (segmentNo) params.set('segment', String(segmentNo))
  return `/warehouse/store-order/picking-slips?${params.toString()}`
}

/** 负责人小标签：段号颜色圆点 + 姓名（+ 可选品种数）；没有负责人时显示“待领取”。 */
export function AssigneeChip({ segmentNo, name, lineCount }: { segmentNo: number; name?: string | null; lineCount?: number }) {
  const { t } = useTranslation()
  return (
    <span className={`picking-assign-person-chip${name ? '' : ' is-claimable'}`}>
      <span className="picking-assign-dot" style={{ background: segmentColor(segmentNo) }} />
      {name || t('storeOrders.pickingAssignment.claimable', '待领取')}
      {typeof lineCount === 'number' ? ` ${lineCount}` : ''}
    </span>
  )
}

interface PickingAssignmentSectionProps {
  orderGuid: string
  orderNo?: string | null
  storeName?: string | null
  /** 订单是否还能派单（已提交或配货中）；不能时只展示现有分配与重新打印。 */
  assignable: boolean
  onAssignmentChange?: (lines: LineAssigneeMap) => void
}

/**
 * 订单详情“拣货分配”卡片：每人一行，显示路线、进度、状态与分单条码，可重新打印单人分单；
 * 自带数据加载，详情页只需放在订单头卡片与明细卡片之间。
 * 卡头：标题 + 分配摘要 + 「还有 N 个品种未分配」琥珀胶囊 + 打印全部分单、分配/重新分配，
 * 刷新与撤销分配收进 ⋯（撤销仍要二次确认）。
 */
export default function PickingAssignmentSection({ orderGuid, orderNo, storeName, assignable, onAssignmentChange }: PickingAssignmentSectionProps) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [summary, setSummary] = useState<AssignmentSummary | null>(null)
  const [loading, setLoading] = useState(false)
  const [modalOpen, setModalOpen] = useState(false)
  const [clearing, setClearing] = useState(false)
  const [nowMs, setNowMs] = useState(() => Date.now())
  const [candidates, setCandidates] = useState<PickerCandidate[] | null>(null)
  const [changingSegment, setChangingSegment] = useState<number | null>(null)

  const applySummary = useCallback(
    (next: AssignmentSummary | null) => {
      setSummary(next)
      setNowMs(Date.now())
      const byDetail: LineAssigneeMap = {}
      const names = new Map((next?.assignees ?? []).map((assignee) => [assignee.segmentNo, assignee.pickerName]))
      next?.lines.forEach((line) => {
        byDetail[line.detailGuid] = { segmentNo: line.segmentNo, pickerName: names.get(line.segmentNo) ?? null }
      })
      onAssignmentChange?.(byDetail)
    },
    [onAssignmentChange],
  )

  const load = useCallback(
    async (signal?: AbortSignal) => {
      setLoading(true)
      try {
        applySummary(await getAssignment(orderGuid, signal))
      } catch (error) {
        if (signal?.aborted) return
        message.error(error instanceof Error ? error.message : t('storeOrders.pickingAssignment.loadFailed', '加载拣货分配失败'))
      } finally {
        if (!signal?.aborted) setLoading(false)
      }
    },
    [applySummary, orderGuid, t],
  )

  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    return () => controller.abort()
  }, [load])

  // 撤销会让已打印的分单全部失效：⋯ 菜单里点了也要先确认（原 Popconfirm 的同一句提示）。
  const confirmClear = () => {
    Modal.confirm({
      title: t('storeOrders.pickingAssignment.clearConfirm', '撤销后已打印的分单全部失效，确定撤销？'),
      okText: t('storeOrders.pickingAssignment.clear', '撤销分配'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: () => clear(),
    })
  }

  const clear = async () => {
    setClearing(true)
    try {
      applySummary(await clearAssignment(orderGuid))
      message.success(t('storeOrders.pickingAssignment.cleared', '已撤销拣货分配，旧分单已失效'))
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('storeOrders.pickingAssignment.clearFailed', '撤销失败'))
    } finally {
      setClearing(false)
    }
  }

  // 改领取人下拉第一次展开时才加载员工名单。
  const loadCandidates = () => {
    if (candidates) return
    listPickerCandidates()
      .then(setCandidates)
      .catch((error: unknown) =>
        message.error(error instanceof Error ? error.message : t('storeOrders.pickingAssignment.loadPickersFailed', '加载员工失败')),
      )
  }

  const changeSegmentPicker = async (segmentNo: number, pickerUserGuid: string | null) => {
    setChangingSegment(segmentNo)
    try {
      applySummary(await setSegmentPicker(orderGuid, segmentNo, pickerUserGuid))
      message.success(
        pickerUserGuid
          ? t('storeOrders.pickingAssignment.pickerChanged', '已改领取人，已打印的分单仍有效')
          : t('storeOrders.pickingAssignment.pickerReleased', '已释放，员工可扫分单重新领取'),
      )
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('storeOrders.pickingAssignment.saveFailed', '保存失败'))
    } finally {
      setChangingSegment(null)
    }
  }

  const assignees = summary?.assignees ?? []
  const columns: ColumnsType<Assignee> = [
    {
      title: t('storeOrders.pickingAssignment.assignee', '负责人'),
      key: 'picker',
      width: 260,
      render: (_, assignee) => (
        <Space size={8}>
          <span className="picking-assign-badge" style={{ background: segmentColor(assignee.segmentNo) }}>{assignee.segmentNo}</span>
          {assignable ? (
            // 领错了或需要换人：直接改这一段的领取人，清空即释放为待领取；不改版本，已打印的分单仍有效。
            <Select
              size="small"
              style={{ width: 120 }}
              allowClear
              showSearch
              optionFilterProp="label"
              loading={changingSegment === assignee.segmentNo}
              disabled={changingSegment !== null}
              placeholder={t('storeOrders.pickingAssignment.claimable', '待领取')}
              aria-label={t('storeOrders.pickingAssignment.segmentPicker', '第 {{no}} 段员工', { no: assignee.segmentNo })}
              value={assignee.pickerUserGuid ?? undefined}
              options={[
                ...(assignee.pickerUserGuid && !candidates?.some((candidate) => candidate.pickerUserGuid === assignee.pickerUserGuid)
                  ? [{ value: assignee.pickerUserGuid, label: assignee.pickerName ?? assignee.pickerUserGuid }]
                  : []),
                ...(candidates ?? []).map((candidate) => ({ value: candidate.pickerUserGuid, label: candidate.pickerName })),
              ]}
              onDropdownVisibleChange={(visible) => visible && loadCandidates()}
              onChange={(value?: string) => void changeSegmentPicker(assignee.segmentNo, value ?? null)}
            />
          ) : (
            <span>{assignee.pickerName || t('storeOrders.pickingAssignment.claimable', '待领取')}</span>
          )}
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {t('storeOrders.pickingAssignment.lineCount', '{{count}} 个品种', { count: assignee.lineCount })}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: t('storeOrders.pickingAssignment.route', '路线'),
      key: 'route',
      render: (_, assignee) =>
        assignee.firstLocation ? (
          <span className="picking-assign-mono">{`${assignee.firstLocation} → ${assignee.lastLocation}`}</span>
        ) : (
          <Typography.Text type="secondary">{t('storeOrders.pickingAssignment.noLocationRoute', '无货位，按商品找')}</Typography.Text>
        ),
    },
    {
      title: t('storeOrders.pickingAssignment.progress', '进度'),
      key: 'progress',
      width: 280,
      render: (_, assignee) => {
        const done = (assignee.completedLineCount ?? 0) + (assignee.stockoutLineCount ?? 0)
        return (
          <div>
            <Progress
              percent={assignee.lineCount > 0 ? Math.round((done / assignee.lineCount) * 100) : 0}
              showInfo={false}
              size="small"
              strokeColor={segmentColor(assignee.segmentNo)}
            />
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {t('storeOrders.pickingAssignment.progressText', '已拣 {{lines}}/{{total}} 品种 · {{picked}}/{{pieces}} 件', {
                lines: assignee.completedLineCount ?? 0,
                total: assignee.lineCount,
                picked: assignee.pickedPieces ?? 0,
                pieces: assignee.pieces ?? 0,
              })}
              {assignee.stockoutLineCount
                ? ` · ${t('storeOrders.pickingAssignment.stockoutCount', '没货 {{count}}', { count: assignee.stockoutLineCount })}`
                : ''}
            </Typography.Text>
            {assignee.helpers?.length ? (
              // 帮忙不改负责人，只在这里标出谁来帮过、帮了几个品种。
              <Typography.Text type="secondary" style={{ display: 'block', fontSize: 12 }}>
                {assignee.helpers
                  .map((helper) =>
                    t('storeOrders.pickingAssignment.helpedBy', '{{name}} 帮拣 {{count}} 品种', {
                      name: helper.pickerName,
                      count: helper.lineCount,
                    }),
                  )
                  .join('、')}
              </Typography.Text>
            ) : null}
          </div>
        )
      },
    },
    {
      title: t('storeOrders.pickingAssignment.status', '状态'),
      key: 'status',
      width: 170,
      render: (_, assignee) => {
        if (!assignee.pickerUserGuid) {
          return <Tag color="warning">{t('storeOrders.pickingAssignment.waitingClaim', '待扫码领取')}</Tag>
        }
        const status = assigneeStatus(assignee, nowMs)
        if (status.kind === 'done') {
          return (
            <Tag color="success">
              {status.stockoutLineCount > 0
                ? t('storeOrders.pickingAssignment.doneWithStockout', '已拣完 · {{count}} 行没货', { count: status.stockoutLineCount })
                : t('storeOrders.pickingAssignment.done', '已拣完')}
            </Tag>
          )
        }
        if (status.kind === 'picking') {
          return (
            <Tag color="processing">
              {status.minutesAgo === 0
                ? t('storeOrders.pickingAssignment.pickingJustNow', '拣货中 · 刚刚')
                : t('storeOrders.pickingAssignment.pickingMinutes', '拣货中 · {{count}} 分钟前', { count: status.minutesAgo })}
            </Tag>
          )
        }
        if (status.kind === 'helped') {
          return <Tag color="processing">{t('storeOrders.pickingAssignment.helped', '有人帮拣中')}</Tag>
        }
        return <Tag>{t('storeOrders.pickingAssignment.notStarted', '未开始')}</Tag>
      },
    },
    {
      title: t('storeOrders.pickingAssignment.slip', '分单'),
      key: 'slip',
      width: 200,
      render: (_, assignee) => (
        <Space direction="vertical" size={0}>
          <Typography.Text className="picking-assign-mono" style={{ fontSize: 12 }}>{assignee.slipCode}</Typography.Text>
          <Button
            type="link"
            size="small"
            style={{ padding: 0, height: 22 }}
            onClick={() => navigate(pickingSlipsPath([orderGuid], assignee.segmentNo))}
          >
            {t('storeOrders.pickingAssignment.reprint', '重新打印')}
          </Button>
        </Space>
      ),
    },
  ]

  const hasAssignees = assignees.length > 0
  const unassignedLineCount = summary?.unassignedLineCount ?? 0
  const sectionTitle = t('storeOrders.pickingAssignment.sectionTitle', '拣货分配')

  // 只读订单没有任何分配时整块隐藏（首次加载中也不先闪一张空卡片）。
  // 数据仍照常加载并通过 onAssignmentChange 交给明细表；所有 hooks 都在上面，提前返回不会改变 hooks 顺序。
  if (!shouldShowPickingAssignmentSection({ assigneeCount: assignees.length, assignable })) return null

  return (
    <section className="picking-assign-section" aria-label={sectionTitle}>
      <div className="picking-assign-section-head">
        <h2 className="picking-assign-section-title">{sectionTitle}</h2>
        {summary?.assignedByName ? (
          <span className="picking-assign-section-summary">
            {t('storeOrders.pickingAssignment.assignedBy', '{{name}} 于 {{time}} 分配 · {{count}} 份 · 按 M 型走位分段', {
              name: summary.assignedByName,
              time: formatUtcShort(summary.assignedAtUtc),
              count: assignees.length,
            })}
          </span>
        ) : null}
        {hasAssignees && unassignedLineCount > 0 ? (
          // 原卡片底部的提示改为卡头胶囊；完整原因（加行后未重新分配）放在悬停说明里。
          <span
            className="picking-assign-section-pill"
            title={t('storeOrders.pickingAssignment.unassigned', '还有 {{count}} 个品种没有分配（订单加行后未重新分配）', {
              count: unassignedLineCount,
            })}
          >
            {t('warehouseUi.storeOrderDetail.pickingUnassignedPill', { count: unassignedLineCount })}
          </span>
        ) : null}
        <span className="picking-assign-section-spacer" />
        {hasAssignees ? (
          <Button size="small" icon={<PrinterOutlined />} onClick={() => navigate(pickingSlipsPath([orderGuid]))}>
            {t('storeOrders.pickingAssignment.printAll', '打印全部分单')}
          </Button>
        ) : null}
        {assignable ? (
          <Button size="small" type={hasAssignees ? 'default' : 'primary'} icon={<TeamOutlined />} onClick={() => setModalOpen(true)}>
            {hasAssignees ? t('storeOrders.pickingAssignment.reassign', '重新分配') : t('storeOrders.pickingAssignment.assign', '分配拣货')}
          </Button>
        ) : null}
        <Dropdown
          trigger={['click']}
          placement="bottomRight"
          menu={{
            items: [
              { key: 'refresh', icon: <ReloadOutlined />, label: t('common.refresh', '刷新'), disabled: loading },
              ...(hasAssignees
                ? [
                    {
                      key: 'clear',
                      icon: <UndoOutlined />,
                      danger: true,
                      disabled: clearing,
                      label: t('storeOrders.pickingAssignment.clear', '撤销分配'),
                    },
                  ]
                : []),
            ],
            onClick: ({ key }) => {
              if (key === 'refresh') {
                void load()
              } else if (key === 'clear') {
                confirmClear()
              }
            },
          }}
        >
          <Button
            size="small"
            type="text"
            icon={loading || clearing ? <LoadingOutlined /> : <MoreOutlined />}
            aria-label={t('warehouseUi.storeOrderDetail.pickingMoreActions')}
          />
        </Dropdown>
      </div>
      <div className="picking-assign-section-body">
        {!summary && loading ? (
          <Spin />
        ) : assignees.length === 0 ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            // 走到这里说明订单还能派单（只读且无分配时整块已隐藏）。
            description={t(
              'storeOrders.pickingAssignment.empty',
              '还没有分配拣货。分成几份后打印分单，员工扫码领取自己那一段；也可以直接指定员工。',
            )}
          />
        ) : (
          <MeasuredTable<Assignee>
            metricId="warehouse.store-order-detail.picking-assignment"
            className="picking-assign-section-table"
            rowKey="segmentNo"
            size="small"
            pagination={false}
            columns={columns}
            dataSource={assignees}
            scroll={{ x: 980 }}
          />
        )}
      </div>
      {modalOpen ? (
        <AssignPickingModal
          open
          orderGuid={orderGuid}
          orderNo={orderNo}
          storeName={storeName}
          onClose={() => setModalOpen(false)}
          onSaved={(next, print) => {
            setModalOpen(false)
            applySummary(next)
            if (print) navigate(pickingSlipsPath([orderGuid]))
          }}
        />
      ) : null}
    </section>
  )
}
