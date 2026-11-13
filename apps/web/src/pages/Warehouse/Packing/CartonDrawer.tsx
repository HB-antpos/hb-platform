import { PrinterOutlined } from '@ant-design/icons'
import { Button, Drawer, Popconfirm, Spin, Tabs, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  getPackCarton,
  listPackCartonEvents,
  reopenPackCarton,
  updatePackCartonHeader,
  voidPackCarton,
} from '../../../services/warehousePackingService'
import type { PackCartonDetail, PackCartonItem, PackEvent } from '../../../types/warehousePacking'
import { PACK_CARTON_STATUS } from '../../../types/warehousePacking'

import { DestinationModal } from './DestinationSelect'
import type { PackDestinationValue } from './packingLogic'
import { destinationToRequest, destinationToValue, formatPackTime } from './packingLogic'
import {
  CartonStatusTag,
  DestinationText,
  EventList,
  PackLabelPreview,
  showPackingError,
  usePackingPage,
} from './packingShared'
import { buildCartonLabelFields, printPackLabels } from './packLabel'

interface CartonDrawerProps {
  cartonGuid: string | null
  onClose: () => void
}

/** 箱详情抽屉：明细、操作记录、改去向、补打标签、重新开箱、作废。 */
export default function CartonDrawer({ cartonGuid, onClose }: CartonDrawerProps) {
  const { t } = useTranslation()
  const { notifyChanged, openPallet } = usePackingPage()
  const [carton, setCarton] = useState<PackCartonDetail | null>(null)
  const [events, setEvents] = useState<PackEvent[]>([])
  const [loading, setLoading] = useState(false)
  const [acting, setActing] = useState(false)
  const [destinationOpen, setDestinationOpen] = useState(false)

  const load = useCallback(async (guid: string) => {
    setLoading(true)
    try {
      const [detail, eventList] = await Promise.all([getPackCarton(guid), listPackCartonEvents(guid)])
      setCarton(detail)
      setEvents(eventList)
    } catch (error) {
      showPackingError(error)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    setCarton(null)
    setEvents([])
    if (cartonGuid) {
      void load(cartonGuid)
    }
  }, [cartonGuid, load])

  /** 执行一个会改动箱的操作；失败时照实提示后端原因（如 403「只有仓库经理可以重新开箱」）。 */
  const act = async (action: () => Promise<PackCartonDetail>, successText: string) => {
    setActing(true)
    try {
      const detail = await action()
      setCarton(detail)
      void message.success(successText)
      notifyChanged()
      void listPackCartonEvents(detail.cartonGuid).then(setEvents).catch(() => undefined)
    } catch (error) {
      showPackingError(error)
    } finally {
      setActing(false)
    }
  }

  const submitDestination = async (value: PackDestinationValue) => {
    if (!carton) {
      return
    }
    try {
      const detail = await updatePackCartonHeader(carton.cartonGuid, {
        ...destinationToRequest(value),
        orderGuid: carton.orderGuid ?? null,
        weightKg: carton.weightKg ?? null,
        remark: carton.remark ?? null,
      })
      setCarton(detail)
      setDestinationOpen(false)
      notifyChanged()
      void message.success(t('warehouseUi.packing.message.destinationSaved'))
      void listPackCartonEvents(detail.cartonGuid).then(setEvents).catch(() => undefined)
    } catch (error) {
      showPackingError(error)
      throw error
    }
  }

  const itemColumns = useMemo<ColumnsType<PackCartonItem>>(
    () => [
      {
        title: t('warehouseUi.packing.columns.product'),
        dataIndex: 'productName',
        ellipsis: true,
        render: (value: string | null, record) => value || record.productCode,
      },
      {
        title: t('warehouseUi.packing.columns.itemNumber'),
        dataIndex: 'itemNumber',
        width: 130,
        render: (value: string | null) => <span className="wh-packing-mono">{value || '—'}</span>,
      },
      {
        title: t('warehouseUi.packing.columns.barcode'),
        dataIndex: 'barcode',
        width: 150,
        render: (value: string | null) => <span className="wh-packing-mono">{value || '—'}</span>,
      },
      { title: t('warehouseUi.packing.columns.quantity'), dataIndex: 'quantity', width: 80, align: 'right' },
    ],
    [t],
  )

  const labelFields = useMemo(() => (carton ? buildCartonLabelFields(carton) : null), [carton])
  const isPacking = carton?.status === PACK_CARTON_STATUS.Packing
  const isSealed = carton?.status === PACK_CARTON_STATUS.Sealed
  const isVoided = carton?.status === PACK_CARTON_STATUS.Voided

  return (
    <Drawer
      open={cartonGuid !== null}
      width={640}
      onClose={onClose}
      destroyOnClose
      title={
        <span className="wh-packing-drawer-title">
          {t('warehouseUi.packing.carton')}
          <span className="wh-packing-mono">{carton?.cartonNo ?? ''}</span>
          {carton ? <CartonStatusTag status={carton.status} t={t} /> : null}
        </span>
      }
      footer={
        carton && !isVoided ? (
          <div className="wh-packing-drawer-footer">
            {isPacking ? (
              <Popconfirm
                title={t('warehouseUi.packing.confirm.voidCarton')}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() => act(() => voidPackCarton(carton.cartonGuid), t('warehouseUi.packing.message.cartonVoided'))}
              >
                <Button danger disabled={acting}>
                  {t('warehouseUi.packing.action.void')}
                </Button>
              </Popconfirm>
            ) : null}
            {isSealed ? (
              <Popconfirm
                title={t('warehouseUi.packing.confirm.reopenCarton')}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() =>
                  act(() => reopenPackCarton(carton.cartonGuid), t('warehouseUi.packing.message.cartonReopened'))
                }
              >
                <Button disabled={acting}>{t('warehouseUi.packing.action.reopenCarton')}</Button>
              </Popconfirm>
            ) : null}
            <Button disabled={acting} onClick={() => setDestinationOpen(true)}>
              {t('warehouseUi.packing.action.changeDestination')}
            </Button>
            <Button
              type="primary"
              icon={<PrinterOutlined />}
              onClick={() => labelFields && printPackLabels([labelFields])}
            >
              {isSealed ? t('warehouseUi.packing.action.reprintLabel') : t('warehouseUi.packing.action.printLabel')}
            </Button>
          </div>
        ) : null
      }
    >
      <Spin spinning={loading}>
        {carton ? (
          <div className="wh-packing-drawer-body">
            <div className="wh-packing-desc">
              <div>
                <span>{t('warehouseUi.packing.columns.destination')}</span>
                <DestinationText destination={carton.destination} t={t} />
              </div>
              <div>
                <span>{t('warehouseUi.packing.columns.order')}</span>
                {carton.orderNo ? <span className="wh-packing-mono">{carton.orderNo}</span> : '—'}
              </div>
              <div>
                <span>{t('warehouseUi.packing.columns.pallet')}</span>
                {carton.palletGuid ? (
                  <Button type="link" size="small" className="wh-packing-mono" onClick={() => openPallet(carton.palletGuid!)}>
                    {carton.palletNo}
                  </Button>
                ) : (
                  t('warehouseUi.packing.loose')
                )}
              </div>
              <div>
                <span>{t('warehouseUi.packing.columns.location')}</span>
                {carton.locationCode || '—'}
              </div>
              <div>
                <span>{t('warehouseUi.packing.columns.total')}</span>
                {t('warehouseUi.packing.summary.skuPieces', { sku: carton.skuCount, pieces: carton.pieceCount })}
              </div>
              <div>
                <span>{t('warehouseUi.packing.columns.weight')}</span>
                {carton.weightKg === null || carton.weightKg === undefined ? '—' : `${carton.weightKg} kg`}
              </div>
              <div>
                <span>{t('warehouseUi.packing.columns.createdBy')}</span>
                {carton.createdByName} · {formatPackTime(carton.createdAtUtc)}
              </div>
              <div>
                <span>{t('warehouseUi.packing.columns.sealedBy')}</span>
                {carton.sealedAtUtc ? `${carton.sealedByName ?? ''} · ${formatPackTime(carton.sealedAtUtc)}` : '—'}
              </div>
            </div>

            <Tabs
              size="small"
              items={[
                {
                  key: 'items',
                  label: t('warehouseUi.packing.tab.itemsCount', { count: carton.items.length }),
                  children: (
                    <MeasuredTable<PackCartonItem>
                      metricId="warehouse.packing.carton-drawer-items"
                      size="small"
                      rowKey="productCode"
                      columns={itemColumns}
                      dataSource={carton.items}
                      pagination={false}
                    />
                  ),
                },
                {
                  key: 'events',
                  label: t('warehouseUi.packing.tab.eventsCount', { count: events.length }),
                  children: <EventList events={events} t={t} />,
                },
                {
                  key: 'label',
                  label: t('warehouseUi.packing.tab.label'),
                  children: labelFields ? <PackLabelPreview fields={labelFields} scale={0.9} /> : null,
                },
              ]}
            />

            {isSealed ? (
              <Typography.Paragraph type="secondary" className="wh-packing-drawer-note">
                {t('warehouseUi.packing.note.reopenCarton')}
              </Typography.Paragraph>
            ) : null}
          </div>
        ) : (
          <div className="wh-packing-drawer-placeholder" />
        )}
      </Spin>

      <DestinationModal
        open={destinationOpen}
        title={t('warehouseUi.packing.action.changeDestination')}
        initialValue={destinationToValue(carton?.destination)}
        hint={isSealed ? t('warehouseUi.packing.note.reprintAfterDestination') : undefined}
        onCancel={() => setDestinationOpen(false)}
        onSubmit={submitDestination}
      />
    </Drawer>
  )
}
