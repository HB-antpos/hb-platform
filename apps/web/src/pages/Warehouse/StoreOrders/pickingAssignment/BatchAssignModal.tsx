import { MinusOutlined, PlusOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Modal, Select, Space, Tag, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../../components/MeasuredTable'
import {
  assignOrdersEvenly,
  listPickerCandidates,
  type Assignee,
  type BatchAssignItem,
  type PickerCandidate,
} from '../../../../services/warehousePickingAssignmentService'
import { StoreOrderFlowStatus, type StoreOrderListItem } from '../../../../types/storeOrder'

import { MAX_SEGMENTS, evenSegmentCounts } from './pickingAssignmentLogic'
import { AssigneeChip } from './PickingAssignmentSection'
import './messages'
import './pickingAssignment.css'

interface BatchAssignModalProps {
  open: boolean
  orders: StoreOrderListItem[]
  /** 列表已加载的各单现有分配（用于提示“会覆盖现有分配”）。 */
  existing: Record<string, Assignee[]>
  onClose: () => void
  /** 分配完成；printOrderGuids 为成功且需要打印的订单。 */
  onDone: (items: BatchAssignItem[], printOrderGuids: string[]) => void
}

function isPickable(order: StoreOrderListItem) {
  return order.flowStatus === StoreOrderFlowStatus.Submitted || order.flowStatus === StoreOrderFlowStatus.Picking
}

/**
 * 批量分配拣货：选好员工，每张订单都按品种数平均分给他们（服务端按 M 型走位切段）。
 * 列表没有品种数，预览只列出每张单会怎么处理；逐张结果以服务端返回为准。
 */
export default function BatchAssignModal({ open, orders, existing, onClose, onDone }: BatchAssignModalProps) {
  const { t } = useTranslation()
  const [candidates, setCandidates] = useState<PickerCandidate[]>([])
  const [pickers, setPickers] = useState<string[]>([])
  // 不选员工时按份数分，每份打印分单后由员工扫码领取；选了员工就按人数分。
  const [segmentCount, setSegmentCount] = useState(2)
  const effectiveSegments = pickers.length > 0 ? pickers.length : segmentCount
  const [print, setPrint] = useState(true)
  const [submitting, setSubmitting] = useState(false)
  const [results, setResults] = useState<BatchAssignItem[] | null>(null)

  useEffect(() => {
    if (!open) return
    const controller = new AbortController()
    setResults(null)
    listPickerCandidates(controller.signal)
      .then(setCandidates)
      .catch((error: unknown) => {
        if (!controller.signal.aborted) {
          message.error(error instanceof Error ? error.message : t('storeOrders.pickingAssignment.loadPickersFailed', '加载员工失败'))
        }
      })
    return () => controller.abort()
  }, [open, t])

  const names = useMemo(() => new Map(candidates.map((candidate) => [candidate.pickerUserGuid, candidate.pickerName])), [candidates])
  const pickableOrders = orders.filter(isPickable)
  const resultByOrder = new Map((results ?? []).map((item) => [item.orderGuid, item]))

  const submit = async () => {
    setSubmitting(true)
    try {
      const items = await assignOrdersEvenly(pickableOrders.map((order) => order.orderGUID), pickers, segmentCount)
      setResults(items)
      const succeeded = items.filter((item) => item.success)
      if (succeeded.length > 0) {
        message.success(t('storeOrders.pickingAssignment.batchDone', '已分配 {{count}} 张订单', { count: succeeded.length }))
      }
      onDone(items, print ? succeeded.map((item) => item.orderGuid) : [])
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('storeOrders.pickingAssignment.saveFailed', '保存失败'))
    } finally {
      setSubmitting(false)
    }
  }

  const columns: ColumnsType<StoreOrderListItem> = [
    {
      title: t('storeOrders.pickingAssignment.order', '订单'),
      key: 'order',
      render: (_, order) => (
        <div>
          <div className="picking-assign-mono">{order.orderNo}</div>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>{order.storeName || order.storeCode}</Typography.Text>
        </div>
      ),
    },
    {
      title: t('storeOrders.pickingAssignment.result', '分配结果'),
      key: 'plan',
      render: (_, order) =>
        isPickable(order) ? (
          <Space size={4} wrap>
            {Array.from({ length: effectiveSegments }, (_, index) => {
              const guid = pickers[index]
              return <AssigneeChip key={index} segmentNo={index + 1} name={guid ? names.get(guid) ?? guid : null} />
            })}
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {t('storeOrders.pickingAssignment.evenHint', '按品种数平均分')}
            </Typography.Text>
          </Space>
        ) : (
          '—'
        ),
    },
    {
      title: t('storeOrders.pickingAssignment.note', '说明'),
      key: 'note',
      width: 220,
      render: (_, order) => {
        const result = resultByOrder.get(order.orderGUID)
        if (result) {
          return result.success ? (
            <Tag color="success">{t('storeOrders.pickingAssignment.assigned', '已分配')}</Tag>
          ) : (
            <Tag color="error">{result.message || t('storeOrders.pickingAssignment.failed', '分配失败')}</Tag>
          )
        }
        if (!isPickable(order)) {
          return <Tag>{t('storeOrders.pickingAssignment.notPickable', '当前状态不能拣货，跳过')}</Tag>
        }
        const current = existing[order.orderGUID]
        if (current?.length) {
          return (
            <Tag color="warning">
              {t('storeOrders.pickingAssignment.willReplace', '覆盖现有分配（{{names}}）', {
                names: current.map((item) => item.pickerName).join('、'),
              })}
            </Tag>
          )
        }
        return <Tag color="blue">{t('storeOrders.pickingAssignment.assignable', '可分配')}</Tag>
      },
    },
  ]

  // 例：32 个品种 2 人时的分法，给经理一个直观参考。
  const example = evenSegmentCounts(32, effectiveSegments).join(' / ')

  return (
    <Modal
      open={open}
      width={760}
      title={t('storeOrders.pickingAssignment.batchTitle', '批量分配拣货')}
      onCancel={onClose}
      destroyOnClose
      footer={
        results ? (
          <Button type="primary" onClick={onClose}>{t('common.close', '关闭')}</Button>
        ) : (
          <Space>
            <Button onClick={onClose}>{t('common.cancel', '取消')}</Button>
            <Button type="primary" loading={submitting} disabled={pickableOrders.length === 0} onClick={() => void submit()}>
              {print
                ? t('storeOrders.pickingAssignment.batchSubmitPrint', '分配 {{count}} 张订单并打印', { count: pickableOrders.length })
                : t('storeOrders.pickingAssignment.batchSubmit', '分配 {{count}} 张订单', { count: pickableOrders.length })}
            </Button>
          </Space>
        )
      }
    >
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        <Typography.Text type="secondary">
          {t('storeOrders.pickingAssignment.batchIntro', '已选 {{count}} 张订单 · 每张都按品种数平均分成几份，按 M 型走位连续切段', { count: orders.length })}
        </Typography.Text>
        <Space size={8} align="center">
          <strong>{t('storeOrders.pickingAssignment.splitInto', '分成')}</strong>
          <span className="picking-assign-stepper">
            <Button
              size="small"
              aria-label={t('storeOrders.pickingAssignment.fewerSegments', '少分一份')}
              icon={<MinusOutlined />}
              disabled={Boolean(results) || pickers.length > 0 || segmentCount <= 1}
              onClick={() => setSegmentCount((value) => value - 1)}
            />
            <span className="picking-assign-count">{effectiveSegments}</span>
            <Button
              size="small"
              aria-label={t('storeOrders.pickingAssignment.moreSegments', '多分一份')}
              icon={<PlusOutlined />}
              disabled={Boolean(results) || pickers.length > 0 || segmentCount >= MAX_SEGMENTS}
              onClick={() => setSegmentCount((value) => value + 1)}
            />
          </span>
          <strong>{t('storeOrders.pickingAssignment.segmentsUnit', '份')}</strong>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {pickers.length > 0
              ? t('storeOrders.pickingAssignment.batchByPickers', '已选员工，按人数分')
              : t('storeOrders.pickingAssignment.batchClaimAll', '不选员工时每份都由员工扫分单领取')}
          </Typography.Text>
        </Space>
        <div>
          <div style={{ fontWeight: 600, marginBottom: 6 }}>{t('storeOrders.pickingAssignment.batchPickers', '直接指定员工（可选，按顺序分段）')}</div>
          <Select
            mode="multiple"
            style={{ width: '100%' }}
            showSearch
            optionFilterProp="label"
            disabled={Boolean(results)}
            placeholder={t('storeOrders.pickingAssignment.searchPicker', '搜索姓名')}
            value={pickers}
            onChange={(value: string[]) => setPickers(value.slice(0, 10))}
            options={candidates.map((candidate) => ({
              value: candidate.pickerUserGuid,
              label: candidate.pickerName,
            }))}
            optionRender={(option) => {
              const candidate = candidates.find((item) => item.pickerUserGuid === option.value)
              return (
                <Space>
                  <span>{candidate?.pickerName}</span>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {candidate?.roleLabel || ''} · {t('storeOrders.pickingAssignment.activeOrders', '在拣 {{count}} 单', { count: candidate?.activeOrderCount ?? 0 })}
                  </Typography.Text>
                </Space>
              )
            }}
          />
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {t('storeOrders.pickingAssignment.evenExample', '例如 32 个品种分成 {{count}} 份：{{split}}', { count: effectiveSegments, split: example })}
          </Typography.Text>
        </div>
        <MeasuredTable<StoreOrderListItem> metricId="warehouse.store-orders.batch-assign-picking" rowKey="orderGUID" size="small" pagination={false} columns={columns} dataSource={orders} />
        {!results ? (
          <Checkbox checked={print} onChange={(event) => setPrint(event.target.checked)}>
            {t('storeOrders.pickingAssignment.printAfter', '分配后打印分单拣货单（每张订单每份一页）')}
          </Checkbox>
        ) : null}
        <Alert type="info" showIcon message={t('storeOrders.pickingAssignment.batchAdjustHint', '需要调整某张单的品种数时，进订单详情单独分配。')} />
      </Space>
    </Modal>
  )
}
