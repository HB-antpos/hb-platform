import { CheckOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, InputNumber, Modal, Spin, Tag, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../components/MeasuredTable'
import { getPackOrderReceive, readPackingError, receivePackOrder } from '../../services/packingService'
import type { PackOrderReceive, PackOrderReceiveCarton, PackOrderReceiveProduct } from '../../types/packing'
import { PACK_CARTON_STATUS } from '../../types/packing'

import {
  classifyOrderReceiveFailure,
  isCartonCountConfirmed,
  isOrderShipMismatch,
  pendingOrderCartons,
  resolveActualInTransitCount,
} from './packingLogic'
import { showPackingError } from './packingShared'

interface OrderReceiveDrawerProps {
  /** 要按订单签收的订货单；为空时抽屉关闭。 */
  orderGuid: string | null
  /** 入口上已知的订单号（接口返回前先显示）。 */
  orderNo?: string | null
  onClose: () => void
  /** 签收成功（或发现已无在途箱）后通知上层刷新待签收清单。 */
  onReceived: () => void
}

/**
 * 分店「按订单签收」抽屉：该订单发给本店的箱（含托盘上的）与商品对照（订货 / 配货 / 本次发出，发出≠配货高亮）。
 * 「签收全部 N 箱」要先录入实到箱数，和系统记录一致才会一次签收；对不上（409 COUNT_MISMATCH）时一只都不签，
 * 提示实际在途箱数并刷新。
 */
export default function OrderReceiveDrawer({ orderGuid, orderNo, onClose, onReceived }: OrderReceiveDrawerProps) {
  const { t } = useTranslation()
  const [order, setOrder] = useState<PackOrderReceive | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [receiving, setReceiving] = useState(false)
  const [countOpen, setCountOpen] = useState(false)
  const [countInput, setCountInput] = useState<number | null>(0)

  const load = useCallback(async (guid: string, signal?: AbortSignal) => {
    setLoading(true)
    try {
      const next = await getPackOrderReceive(guid, signal)
      if (!signal?.aborted) {
        setOrder(next)
        setLoadError(null)
      }
    } catch (error) {
      if (!signal?.aborted) {
        setLoadError(readPackingError(error).message)
      }
    } finally {
      if (!signal?.aborted) {
        setLoading(false)
      }
    }
  }, [])

  useEffect(() => {
    setOrder(null)
    setLoadError(null)
    setCountOpen(false)
    if (!orderGuid) {
      return undefined
    }
    const controller = new AbortController()
    void load(orderGuid, controller.signal)
    return () => controller.abort()
  }, [load, orderGuid])

  const pendingCount = useMemo(() => (order ? pendingOrderCartons(order).length : 0), [order])
  const displayOrderNo = order?.orderNo || orderNo || ''

  /** 签收全部：必须带实到箱数；数量不符（COUNT_MISMATCH）一只都不签，提示实际在途数并刷新。 */
  const receiveAll = async () => {
    if (!orderGuid || countInput === null) {
      return
    }
    const entered = countInput
    setReceiving(true)
    try {
      const result = await receivePackOrder(orderGuid, entered)
      setCountOpen(false)
      setOrder(result)
      void message.success(t('packingUi.orderReceive.received', { no: result.orderNo || displayOrderNo, count: entered }))
      onReceived()
    } catch (error) {
      const info = readPackingError<{ actualInTransitCartonCount?: number | null }>(error)
      const failure = classifyOrderReceiveFailure(info.errorCode)
      if (failure === 'countMismatch') {
        setCountOpen(false)
        Modal.warning({
          title: t('packingUi.orderReceive.countMismatchTitle'),
          content: t('packingUi.orderReceive.countMismatchBody', {
            entered,
            actual: resolveActualInTransitCount(info.data, pendingCount),
          }),
          okText: t('packingUi.orderReceive.countMismatchOk'),
        })
        // 系统记录可能已变（别处签收了一部分）：重新读取，让对照表与「签收全部 N 箱」跟上实际。
        void load(orderGuid)
      } else if (failure === 'notInTransit') {
        setCountOpen(false)
        void message.warning(t('packingUi.orderReceive.notInTransit'))
        void load(orderGuid)
        onReceived()
      } else {
        showPackingError(error)
      }
    } finally {
      setReceiving(false)
    }
  }

  const cartonColumns = useMemo<ColumnsType<PackOrderReceiveCarton>>(
    () => [
      {
        title: t('packingUi.orderReceive.columns.cartonNo'),
        dataIndex: 'cartonNo',
        width: 150,
        render: (value: string) => <b className="wh-packing-mono">{value}</b>,
      },
      {
        title: t('packingUi.orderReceive.columns.pallet'),
        dataIndex: 'palletNo',
        width: 150,
        render: (value?: string | null) => (value ? <span className="wh-packing-mono">{value}</span> : t('packingUi.loose')),
      },
      {
        title: t('packingUi.columns.sku'),
        dataIndex: 'skuCount',
        width: 70,
        align: 'right',
      },
      {
        title: t('packingUi.columns.pieces'),
        dataIndex: 'pieceCount',
        width: 70,
        align: 'right',
      },
      {
        title: t('common.status'),
        dataIndex: 'status',
        width: 100,
        render: (status: number) =>
          status === PACK_CARTON_STATUS.InTransit ? (
            <Tag color="purple">{t('packingUi.status.transit')}</Tag>
          ) : (
            <Tag color="green">{t('packingUi.status.receivedShort')}</Tag>
          ),
      },
    ],
    [t],
  )

  const productColumns = useMemo<ColumnsType<PackOrderReceiveProduct>>(
    () => [
      {
        title: t('packingUi.orderReceive.columns.itemNumber'),
        dataIndex: 'itemNumber',
        width: 110,
        render: (value?: string | null) => value || '—',
      },
      {
        title: t('packingUi.orderReceive.columns.product'),
        key: 'product',
        ellipsis: true,
        render: (_value, product) => product.productName || product.productCode,
      },
      { title: t('packingUi.orderReceive.columns.ordered'), dataIndex: 'orderedPieces', width: 72, align: 'right' },
      {
        title: t('packingUi.orderReceive.columns.alloc'),
        dataIndex: 'allocPieces',
        width: 72,
        align: 'right',
        render: (value?: number | null) => (value === null || value === undefined ? '—' : value),
      },
      {
        title: t('packingUi.orderReceive.columns.shipped'),
        dataIndex: 'shippedPieces',
        width: 90,
        align: 'right',
        render: (value: number, product) =>
          isOrderShipMismatch(product) ? (
            <b className="wh-packing-over" title={t('packingUi.orderReceive.shipMismatch')}>
              {value}
            </b>
          ) : (
            value
          ),
      },
    ],
    [t],
  )

  return (
    <Drawer
      open={orderGuid !== null}
      width={640}
      onClose={onClose}
      destroyOnClose
      title={
        <span className="wh-packing-drawer-title">
          {t('packingUi.orderReceive.title')}
          <span className="wh-packing-mono">{displayOrderNo}</span>
        </span>
      }
      footer={
        order ? (
          <div className="wh-packing-drawer-footer">
            <Button
              type="primary"
              icon={<CheckOutlined />}
              disabled={pendingCount === 0 || receiving || loading}
              onClick={() => {
                setCountInput(0)
                setCountOpen(true)
              }}
            >
              {t('packingUi.orderReceive.receiveAll', { count: pendingCount })}
            </Button>
          </div>
        ) : null
      }
    >
      <Spin spinning={loading}>
        {loadError ? <Alert type="error" showIcon message={loadError} /> : null}
        {order ? (
          <div className="wh-packing-drawer-body">
            <Typography.Text type="secondary">
              {t('packingUi.orderReceive.summary', { transit: order.inTransitCount, received: order.receivedCount })}
            </Typography.Text>
            {pendingCount === 0 ? <Alert type="info" showIcon message={t('packingUi.orderReceive.nothingPending')} /> : null}
            <div>
              <Typography.Text strong>{t('packingUi.orderReceive.cartonsTitle')}</Typography.Text>
              <MeasuredTable<PackOrderReceiveCarton>
                metricId="packing.order-receive-cartons"
                size="small"
                rowKey="cartonGuid"
                columns={cartonColumns}
                dataSource={order.cartons}
                pagination={false}
                locale={{ emptyText: t('packingUi.orderReceive.cartonsEmpty') }}
              />
            </div>
            <div>
              <Typography.Text strong>{t('packingUi.orderReceive.productsTitle')}</Typography.Text>
              <MeasuredTable<PackOrderReceiveProduct>
                metricId="packing.order-receive-products"
                size="small"
                rowKey="productCode"
                columns={productColumns}
                dataSource={order.products}
                pagination={false}
                locale={{ emptyText: t('packingUi.orderReceive.productsEmpty') }}
                rowClassName={(product) => (isOrderShipMismatch(product) ? 'wh-packing-row-highlight' : '')}
              />
            </div>
          </div>
        ) : (
          <div className="wh-packing-drawer-placeholder" />
        )}
      </Spin>

      <Modal
        open={countOpen}
        title={t('packingUi.orderReceive.countTitle')}
        width={420}
        okText={t('packingUi.orderReceive.countOk', { count: pendingCount })}
        cancelText={t('common.cancel')}
        okButtonProps={{ disabled: !isCartonCountConfirmed(countInput, pendingCount) }}
        confirmLoading={receiving}
        onOk={() => void receiveAll()}
        onCancel={() => setCountOpen(false)}
        destroyOnClose
      >
        <Typography.Paragraph>{t('packingUi.orderReceive.countBody')}</Typography.Paragraph>
        <InputNumber
          className="wh-packing-count-input"
          size="large"
          min={0}
          max={9999}
          precision={0}
          value={countInput}
          autoFocus
          onChange={(value) => setCountInput(typeof value === 'number' ? value : null)}
          onPressEnter={() => {
            if (isCartonCountConfirmed(countInput, pendingCount) && !receiving) {
              void receiveAll()
            }
          }}
          addonAfter={t('packingUi.receive.countUnit')}
        />
      </Modal>
    </Drawer>
  )
}
