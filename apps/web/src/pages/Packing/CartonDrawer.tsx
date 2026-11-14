import { PrinterOutlined, SendOutlined } from '@ant-design/icons'
import { Button, Drawer, Popconfirm, Spin, Tabs, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../components/MeasuredTable'
import {
  dispatchPackCarton,
  getPackCarton,
  listPackCartonEvents,
  reopenPackCarton,
  setPackCartonLocation,
  updatePackCartonHeader,
  voidPackCarton,
} from '../../services/packingService'
import type { PackCartonDetail, PackCartonItem, PackEvent } from '../../types/packing'
import { PACK_CARTON_STATUS } from '../../types/packing'

import { DestinationModal } from './DestinationSelect'
import LocationSelect from './LocationSelect'
import type { PackDestinationValue } from './packingLogic'
import {
  canDispatchCarton,
  cartonStateKind,
  destinationPlaceName,
  destinationToRequest,
  destinationToValue,
  formatPackTime,
  isReadOnlyForOwner,
} from './packingLogic'
import {
  DestinationText,
  EventList,
  PackLabelPreview,
  ReceivedPlace,
  StateTag,
  showPackingError,
  usePackingPage,
} from './packingShared'
import { buildCartonLabelFields, printPackLabels } from './packLabel'

interface CartonDrawerProps {
  cartonGuid: string | null
  onClose: () => void
}

/** 箱详情抽屉：明细、操作记录、改去向、设置存放位置、发出、补打标签、重新开箱、作废；在路上或已被对方签收的只读。 */
export default function CartonDrawer({ cartonGuid, onClose }: CartonDrawerProps) {
  const { t } = useTranslation()
  const { ownerCode, isManager, notifyChanged, openPallet } = usePackingPage()
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

  /** 执行一个会改动箱的操作；失败时照实提示后端原因（如 403「只有店长或仓库经理可以重新开箱」）。 */
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
      void message.success(t('packingUi.message.destinationSaved'))
      void listPackCartonEvents(detail.cartonGuid).then(setEvents).catch(() => undefined)
    } catch (error) {
      showPackingError(error)
      throw error
    }
  }

  const itemColumns = useMemo<ColumnsType<PackCartonItem>>(
    () => [
      {
        title: t('packingUi.columns.product'),
        dataIndex: 'productName',
        ellipsis: true,
        render: (value: string | null, record) => value || record.productCode,
      },
      {
        title: t('packingUi.columns.itemNumber'),
        dataIndex: 'itemNumber',
        width: 130,
        render: (value: string | null) => <span className="wh-packing-mono">{value || '—'}</span>,
      },
      {
        title: t('packingUi.columns.barcode'),
        dataIndex: 'barcode',
        width: 150,
        render: (value: string | null) => <span className="wh-packing-mono">{value || '—'}</span>,
      },
      { title: t('packingUi.columns.quantity'), dataIndex: 'quantity', width: 80, align: 'right' },
    ],
    [t],
  )

  const labelFields = useMemo(() => (carton ? buildCartonLabelFields(carton) : null), [carton])
  const isPacking = carton?.status === PACK_CARTON_STATUS.Packing
  const isSealed = carton?.status === PACK_CARTON_STATUS.Sealed
  const isVoided = carton?.status === PACK_CARTON_STATUS.Voided
  const readOnly = carton ? isReadOnlyForOwner(carton, ownerCode) : true
  const stateKind = carton ? cartonStateKind(carton, ownerCode) : 'voided'
  // 已封箱、在自己手里、不在托盘上：可以设置存放位置（在托盘上的箱位置跟随托盘）。
  const canSetLocation = Boolean(carton && isSealed && !readOnly && !carton.palletGuid)
  const placeName = carton ? destinationPlaceName(carton.destination) : null

  return (
    <Drawer
      open={cartonGuid !== null}
      width={640}
      onClose={onClose}
      destroyOnClose
      title={
        <span className="wh-packing-drawer-title">
          {t('packingUi.carton')}
          <span className="wh-packing-mono">{carton?.cartonNo ?? ''}</span>
          {carton ? <StateTag kind={stateKind} t={t} /> : null}
        </span>
      }
      footer={
        carton && !isVoided ? (
          <div className="wh-packing-drawer-footer">
            {isPacking && !readOnly ? (
              <Popconfirm
                title={t('packingUi.confirm.voidCarton')}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() => act(() => voidPackCarton(carton.cartonGuid), t('packingUi.message.cartonVoided'))}
              >
                <Button danger disabled={acting}>
                  {t('packingUi.action.void')}
                </Button>
              </Popconfirm>
            ) : null}
            {isSealed && !readOnly && isManager ? (
              <Popconfirm
                title={t('packingUi.confirm.reopenCarton')}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() => act(() => reopenPackCarton(carton.cartonGuid), t('packingUi.message.cartonReopened'))}
              >
                <Button disabled={acting}>{t('packingUi.action.reopenCarton')}</Button>
              </Popconfirm>
            ) : null}
            {!readOnly ? (
              <Button disabled={acting || Boolean(carton.palletGuid)} onClick={() => setDestinationOpen(true)}>
                {t('packingUi.action.changeDestination')}
              </Button>
            ) : null}
            {canDispatchCarton(carton, ownerCode) ? (
              <Popconfirm
                title={t('packingUi.confirm.dispatchCarton', { name: placeName ?? '' })}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() => act(() => dispatchPackCarton(carton.cartonGuid), t('packingUi.message.cartonDispatched'))}
              >
                <Button type="primary" icon={<SendOutlined />} disabled={acting}>
                  {t('packingUi.action.dispatch')}
                </Button>
              </Popconfirm>
            ) : null}
            <Button
              type={canDispatchCarton(carton, ownerCode) ? 'default' : 'primary'}
              icon={<PrinterOutlined />}
              onClick={() => labelFields && printPackLabels([labelFields])}
            >
              {isSealed || readOnly ? t('packingUi.action.reprintLabel') : t('packingUi.action.printLabel')}
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
                <span>{t('packingUi.columns.destination')}</span>
                {stateKind === 'received' ? (
                  <ReceivedPlace ownerName={carton.ownerName} />
                ) : (
                  <DestinationText destination={carton.destination} t={t} />
                )}
              </div>
              <div>
                <span>{t('packingUi.columns.owner')}</span>
                {carton.ownerName}
              </div>
              <div>
                <span>{t('packingUi.columns.origin')}</span>
                {carton.originName || '—'}
              </div>
              <div>
                <span>{t('packingUi.columns.pallet')}</span>
                {carton.palletGuid ? (
                  <Button type="link" size="small" className="wh-packing-mono" onClick={() => openPallet(carton.palletGuid!)}>
                    {carton.palletNo}
                  </Button>
                ) : (
                  t('packingUi.loose')
                )}
              </div>
              <div className="wh-packing-desc-location">
                <span>{t('packingUi.columns.location')}</span>
                {canSetLocation ? (
                  <LocationSelect
                    className="wh-packing-location-select"
                    value={carton.locationCode}
                    disabled={acting}
                    onChange={(value) =>
                      void act(() => setPackCartonLocation(carton.cartonGuid, value), t('packingUi.message.locationSaved'))
                    }
                  />
                ) : (
                  carton.locationCode || '—'
                )}
              </div>
              <div>
                <span>{t('packingUi.columns.total')}</span>
                {t('packingUi.summary.skuPieces', { sku: carton.skuCount, pieces: carton.pieceCount })}
              </div>
              <div>
                <span>{t('packingUi.columns.weight')}</span>
                {carton.weightKg === null || carton.weightKg === undefined ? '—' : `${carton.weightKg} kg`}
              </div>
              <div>
                <span>{t('packingUi.columns.createdBy')}</span>
                {carton.createdByName} · {formatPackTime(carton.createdAtUtc)}
              </div>
              <div>
                <span>{t('packingUi.columns.sealedBy')}</span>
                {carton.sealedAtUtc ? `${carton.sealedByName ?? ''} · ${formatPackTime(carton.sealedAtUtc)}` : '—'}
              </div>
              <div>
                <span>{t('packingUi.columns.dispatchedBy')}</span>
                {carton.dispatchedAtUtc ? `${carton.dispatchedByName ?? ''} · ${formatPackTime(carton.dispatchedAtUtc)}` : '—'}
              </div>
              {carton.receivedAtUtc ? (
                <div>
                  <span>{t('packingUi.columns.receivedBy')}</span>
                  {`${carton.receivedByName ?? ''} · ${formatPackTime(carton.receivedAtUtc)}`}
                </div>
              ) : null}
            </div>

            <Tabs
              size="small"
              items={[
                {
                  key: 'items',
                  label: t('packingUi.tab.itemsCount', { count: carton.items.length }),
                  children: (
                    <MeasuredTable<PackCartonItem>
                      metricId="packing.carton-drawer-items"
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
                  label: t('packingUi.tab.eventsCount', { count: events.length }),
                  children: <EventList events={events} t={t} />,
                },
                {
                  key: 'label',
                  label: t('packingUi.tab.label'),
                  children: labelFields ? <PackLabelPreview fields={labelFields} scale={0.9} /> : null,
                },
              ]}
            />

            {stateKind === 'transit' ? (
              <Typography.Paragraph type="secondary" className="wh-packing-drawer-note">
                {t('packingUi.note.cartonInTransit')}
              </Typography.Paragraph>
            ) : stateKind === 'received' ? (
              <Typography.Paragraph type="secondary" className="wh-packing-drawer-note">
                {t('packingUi.note.cartonReceived')}
              </Typography.Paragraph>
            ) : isSealed && isManager ? (
              <Typography.Paragraph type="secondary" className="wh-packing-drawer-note">
                {t('packingUi.note.reopenCarton')}
              </Typography.Paragraph>
            ) : null}
          </div>
        ) : (
          <div className="wh-packing-drawer-placeholder" />
        )}
      </Spin>

      <DestinationModal
        open={destinationOpen}
        title={t('packingUi.action.changeDestination')}
        initialValue={destinationToValue(carton?.destination)}
        hint={isSealed ? t('packingUi.note.reprintAfterDestination') : undefined}
        onCancel={() => setDestinationOpen(false)}
        onSubmit={submitDestination}
      />
    </Drawer>
  )
}
