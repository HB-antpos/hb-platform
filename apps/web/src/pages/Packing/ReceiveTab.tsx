import { BarcodeOutlined, CheckCircleFilled, CheckOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Input, InputNumber, Modal, Select, Tag, Typography, message } from 'antd'
import type { InputRef } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../components/MeasuredTable'
import { useBarcodeScanner } from '../../hooks/useBarcodeScanner'
import { lookupPackReceive, readPackingError, receivePackCarton, receivePackPallet } from '../../services/packingService'
import type { PackCartonSummary, PackIncomingItem, PackReceiveLookup } from '../../types/packing'
import { PACK_CARTON_STATUS, PACK_ERROR_CODE } from '../../types/packing'
import { isScanFeedbackUnlocked, playScanFeedback, unlockScanFeedback } from '../../utils/scanFeedback'

import LocationSelect from './LocationSelect'
import OrderReceiveDrawer from './OrderReceiveDrawer'
import {
  checkScannedCarton,
  collectOrderReceiveEntries,
  formatPackTime,
  incomingState,
  isCartonCountConfirmed,
  pendingPalletCartons,
  resolveReceiveBlock,
  selectCheckedCartons,
} from './packingLogic'
import { showPackingError, usePackingPage } from './packingShared'

type IncomingRow = PackIncomingItem & { section: 'pending' | 'recent' }

/**
 * 到货签收：扫托盘标签或箱标签（托盘标签丢了，扫托盘上任意一只箱的标签同样打开整个托盘），
 * 右侧核对面板逐箱勾选；整托签收先确认实到箱数，对不上改逐箱核对，只签已核对的箱。
 */
export default function ReceiveTab({ active }: { active: boolean }) {
  const { t } = useTranslation()
  const { incoming, reloadIncoming, notifyChanged, openCarton, openPallet, dataVersion } = usePackingPage()
  const [lookup, setLookup] = useState<PackReceiveLookup | null>(null)
  const [checked, setChecked] = useState<Set<string>>(new Set())
  const [location, setLocation] = useState<string | null>(null)
  const [scanText, setScanText] = useState('')
  const [fromFilter, setFromFilter] = useState<string | null>(null)
  const [looking, setLooking] = useState(false)
  const [receiving, setReceiving] = useState(false)
  const [scanError, setScanError] = useState<string | null>(null)
  const [countOpen, setCountOpen] = useState(false)
  const [countInput, setCountInput] = useState<number | null>(0)
  /** 正在按订单签收的订单（抽屉打开时为非空）。 */
  const [orderReceive, setOrderReceive] = useState<{ orderGuid: string; orderNo: string } | null>(null)
  const scanInputRef = useRef<InputRef>(null)
  const lookupRef = useRef<PackReceiveLookup | null>(null)
  lookupRef.current = lookup

  // 切到本标签或别处改了数据时刷新待签收。
  useEffect(() => {
    if (active) {
      reloadIncoming()
    }
  }, [active, dataVersion, reloadIncoming])

  useEffect(() => {
    if (active && !countOpen && !orderReceive) {
      scanInputRef.current?.focus()
    }
  }, [active, countOpen, orderReceive])

  const selectLookup = useCallback((next: PackReceiveLookup | null, resetChecks: boolean) => {
    setLookup(next)
    setScanError(null)
    if (resetChecks) {
      setChecked(new Set())
    }
  }, [])

  /** 扫托盘标签 / 箱标签（或点列表里的「签收」）：打开对应的核对面板；已打开的托盘上再扫一只箱就是勾选它。 */
  const handleScan = useCallback(
    async (raw: string) => {
      const code = raw.trim()
      if (!code) {
        return
      }
      if (!isScanFeedbackUnlocked()) {
        void unlockScanFeedback().catch(() => undefined)
      }
      setLooking(true)
      try {
        const result = await lookupPackReceive(code)
        const current = lookupRef.current
        const samePallet = result.kind === 'pallet' && current?.kind === 'pallet' && current.pallet?.palletGuid === result.pallet?.palletGuid
        selectLookup(result, !samePallet)
        if (result.kind === 'pallet' && result.pallet && result.scannedCartonNo) {
          // 扫的是托盘上的一只箱：这只箱就在手上，直接勾上。
          setChecked((previous) => {
            const next = new Set(samePallet ? previous : [])
            checkScannedCarton(result.pallet!, result.scannedCartonNo!, next)
            return next
          })
        }
        const blocked = resolveReceiveBlock(result.blockReason)
        playScanFeedback(blocked ? 'blocked' : 'success')
        if (blocked && result.canReceive === false) {
          setScanError(blocked === 'notForYou' ? t('packingUi.receive.notForYou') : t('packingUi.receive.notInTransit'))
        }
      } catch (error) {
        const info = readPackingError(error)
        playScanFeedback(info.errorCode === PACK_ERROR_CODE.CodeNotFound ? 'not-found' : 'error')
        setScanError(`${code}：${info.message}`)
      } finally {
        setLooking(false)
      }
    },
    [selectLookup, t],
  )

  // 数箱弹窗和按订单签收抽屉打开时不接管扫码枪。
  useBarcodeScanner({ enabled: active && !countOpen && !orderReceive, onScan: (value) => void handleScan(value) })

  const submitScanInput = () => {
    const value = scanText
    setScanText('')
    void handleScan(value)
  }

  // ---- 核对面板的数据 ----
  const pallet = lookup?.kind === 'pallet' ? lookup.pallet ?? null : null
  const singleCarton = lookup?.kind === 'carton' ? lookup.carton ?? null : null
  const pendingCartons = useMemo(() => (pallet ? pendingPalletCartons(pallet) : []), [pallet])
  const pendingCount = pendingCartons.length
  const checkedGuids = useMemo(() => (pallet ? selectCheckedCartons(pallet, checked) : []), [checked, pallet])
  const blocked = lookup ? resolveReceiveBlock(lookup.blockReason) : null
  const canReceive = Boolean(lookup?.canReceive)

  const toggleCarton = (carton: PackCartonSummary) => {
    if (carton.status !== PACK_CARTON_STATUS.InTransit || !canReceive) {
      return
    }
    setChecked((previous) => {
      const next = new Set(previous)
      if (next.has(carton.cartonGuid)) {
        next.delete(carton.cartonGuid)
      } else {
        next.add(carton.cartonGuid)
      }
      return next
    })
  }

  const afterReceive = (text: string, fullyDone: boolean, refreshed?: PackReceiveLookup | null) => {
    void message.success(text)
    playScanFeedback('success')
    notifyChanged()
    reloadIncoming()
    setLocation(null)
    if (fullyDone) {
      selectLookup(null, true)
    } else if (refreshed) {
      selectLookup(refreshed, false)
      setChecked(new Set())
    }
  }

  /** 整托签收：必须带实到箱数；系统记录对不上（COUNT_MISMATCH）时提示并引导逐箱核对。 */
  const receiveWholePallet = async () => {
    if (!pallet || !lookup) {
      return
    }
    setReceiving(true)
    try {
      const result = await receivePackPallet(pallet.palletGuid, {
        expectedCartonCount: countInput,
        locationCode: location,
        viaCartonNo: lookup.viaCartonLabel ? lookup.scannedCartonNo ?? null : null,
      })
      setCountOpen(false)
      afterReceive(t('packingUi.receive.palletReceived', { no: result.palletNo }), true)
    } catch (error) {
      const info = readPackingError<{ actualInTransitCartonCount?: number | null }>(error)
      if (info.errorCode === PACK_ERROR_CODE.CountMismatch) {
        setCountOpen(false)
        Modal.warning({
          title: t('packingUi.receive.countMismatchTitle'),
          content: t('packingUi.receive.countMismatchBody', {
            actual: countInput ?? 0,
            expected: info.data?.actualInTransitCartonCount ?? pendingCount,
          }),
          okText: t('packingUi.receive.countMismatchOk'),
        })
        // 系统记录可能已变（别处签收了一部分）：重新读取托盘，再逐箱核对。
        void handleScan(pallet.palletNo)
      } else {
        showPackingError(error)
      }
    } finally {
      setReceiving(false)
    }
  }

  /** 只签已核对的箱（逐箱核对）；没到的箱留在待签收里。 */
  const receiveChecked = async () => {
    if (!pallet || !lookup || checkedGuids.length === 0) {
      return
    }
    setReceiving(true)
    try {
      const result = await receivePackPallet(pallet.palletGuid, {
        cartonGuids: checkedGuids,
        locationCode: location,
        viaCartonNo: lookup.viaCartonLabel ? lookup.scannedCartonNo ?? null : null,
      })
      const remaining = result.inTransitCartonCount
      afterReceive(
        remaining > 0
          ? t('packingUi.receive.partialReceived', { count: checkedGuids.length, remaining })
          : t('packingUi.receive.palletReceived', { no: result.palletNo }),
        remaining === 0,
        { ...lookup, pallet: result, inTransitCartonCount: remaining, canReceive: remaining > 0 },
      )
    } catch (error) {
      showPackingError(error)
    } finally {
      setReceiving(false)
    }
  }

  /** 签收一只散箱。 */
  const receiveSingleCarton = async () => {
    if (!singleCarton) {
      return
    }
    setReceiving(true)
    try {
      const result = await receivePackCarton(singleCarton.cartonGuid, location)
      afterReceive(t('packingUi.receive.cartonReceived', { no: result.cartonNo }), true)
    } catch (error) {
      showPackingError(error)
    } finally {
      setReceiving(false)
    }
  }

  /** 面板里的箱按订单分组的「按订单签收」入口：同一订单只一个；只有能签收时才给。 */
  const orderEntries = useMemo(
    () => (canReceive ? collectOrderReceiveEntries(pallet ? pallet.cartons : singleCarton ? [singleCarton] : []) : []),
    [canReceive, pallet, singleCarton],
  )

  /** 按订单签收成功（或发现已无在途箱）：刷新待签收清单，并重新读取当前打开的托盘 / 箱，没有可签收的就收起面板。 */
  const afterOrderReceive = async () => {
    playScanFeedback('success')
    notifyChanged()
    reloadIncoming()
    const current = lookupRef.current
    const code = current?.kind === 'pallet' ? current.pallet?.palletNo : current?.carton?.cartonNo
    if (!code) {
      return
    }
    try {
      const refreshed = await lookupPackReceive(code)
      if (refreshed.canReceive) {
        selectLookup(refreshed, false)
        setChecked(new Set())
      } else {
        selectLookup(null, true)
      }
    } catch {
      selectLookup(null, true)
    }
  }

  // ---- 左侧列表 ----
  const pendingRows: IncomingRow[] = incoming.pending.map((item) => ({ ...item, section: 'pending' }))
  const recentRows: IncomingRow[] = incoming.recent.map((item) => ({ ...item, section: 'recent' }))
  const originOptions = useMemo(
    () => [...new Set(incoming.pending.map((item) => item.originName))].map((name) => ({ value: name, label: name })),
    [incoming.pending],
  )
  const visiblePending = fromFilter ? pendingRows.filter((row) => row.originName === fromFilter) : pendingRows
  const pendingCartonTotal = pendingRows.reduce((sum, row) => sum + (row.cartonCount - row.receivedCartonCount), 0)

  const columns = useMemo<ColumnsType<IncomingRow>>(
    () => [
      {
        title: t('packingUi.receive.columns.no'),
        dataIndex: 'no',
        width: 150,
        render: (value: string, record) => (
          <b className={record.section === 'pending' ? 'wh-packing-mono' : 'wh-packing-mono wh-packing-muted'}>{value}</b>
        ),
      },
      {
        title: t('packingUi.receive.columns.from'),
        dataIndex: 'originName',
        ellipsis: true,
      },
      {
        title: t('packingUi.receive.columns.how'),
        key: 'how',
        width: 140,
        ellipsis: true,
        render: (_value, record) => record.carrierName || t('packingUi.receive.howSelf'),
      },
      { title: t('packingUi.columns.cartons'), dataIndex: 'cartonCount', width: 60, align: 'right' },
      {
        title: t('packingUi.receive.columns.dispatchedAt'),
        key: 'time',
        width: 150,
        render: (_value, record) => (
          <Typography.Text type="secondary">
            {formatPackTime(record.section === 'pending' ? record.dispatchedAtUtc : record.receivedAtUtc)}
          </Typography.Text>
        ),
      },
      {
        title: t('common.status'),
        key: 'state',
        width: 130,
        render: (_value, record) => {
          const state = incomingState(record)
          if (state.kind === 'received') {
            return <Tag color="green">{t('packingUi.status.receivedShort')}</Tag>
          }
          if (state.kind === 'partial') {
            return <Tag color="orange">{t('packingUi.receive.partial', { received: state.received, total: state.total })}</Tag>
          }
          return <Tag color="purple">{t('packingUi.status.transit')}</Tag>
        },
      },
      {
        title: t('packingUi.columns.actions'),
        key: 'actions',
        width: 80,
        render: (_value, record) =>
          record.section === 'pending' ? (
            <Button type="link" size="small" onClick={() => void handleScan(record.no)}>
              {t('packingUi.receive.receive')}
            </Button>
          ) : (
            <Button
              type="link"
              size="small"
              onClick={() => (record.kind === 'pallet' ? openPallet(record.guid) : openCarton(record.guid))}
            >
              {t('common.view')}
            </Button>
          ),
      },
    ],
    [handleScan, openCarton, openPallet, t],
  )

  // ---- 渲染 ----
  const panelCartons: PackCartonSummary[] = pallet?.cartons ?? []
  const origin = pallet?.originName ?? singleCarton?.originName ?? ''
  const carrier = pallet?.destination.carrierName ?? singleCarton?.destination.carrierName
  const consignmentNo = pallet?.consignmentNo

  return (
    <div className="wh-packing-receive">
      <div className="wh-packing-panel wh-packing-receive-list">
        <div className="wh-packing-filters">
          <Input
            ref={scanInputRef}
            className="wh-packing-receive-scan"
            allowClear
            autoComplete="off"
            prefix={<BarcodeOutlined />}
            placeholder={t('packingUi.receive.scanPlaceholder')}
            value={scanText}
            disabled={looking}
            onChange={(event) => setScanText(event.target.value)}
            onPressEnter={submitScanInput}
          />
          <Select
            allowClear
            className="wh-packing-receive-from"
            placeholder={t('packingUi.receive.fromAll')}
            value={fromFilter ?? undefined}
            options={originOptions}
            onChange={(value) => setFromFilter(value ?? null)}
          />
          <span className="wh-packing-filters-spacer" />
          <Typography.Text type="secondary">
            {t('packingUi.receive.inTransitTotal', { batches: pendingRows.length, cartons: pendingCartonTotal })}
          </Typography.Text>
        </div>
        {scanError ? <Alert className="wh-packing-receive-alert" type="error" showIcon message={scanError} /> : null}
        <MeasuredTable<IncomingRow>
          metricId="packing.receive-incoming"
          size="middle"
          rowKey={(record) => `${record.section}:${record.guid}`}
          columns={columns}
          dataSource={[...visiblePending, ...recentRows]}
          pagination={false}
          rowClassName={(record) =>
            record.guid === (pallet?.palletGuid ?? singleCarton?.cartonGuid) && record.section === 'pending' ? 'wh-packing-row-highlight' : ''
          }
          locale={{ emptyText: t('packingUi.receive.nothing') }}
        />
        {recentRows.length > 0 ? <Typography.Text type="secondary" className="wh-packing-receive-recent-note">{t('packingUi.receive.recentNote')}</Typography.Text> : null}
      </div>

      <div className="wh-packing-panel wh-packing-receive-side">
        {lookup && (pallet || singleCarton) ? (
          <>
            <div className="wh-packing-receive-head">
              <div className="wh-packing-side-head">
                <span className="wh-packing-side-title">
                  {t('packingUi.receive.title')}
                  <span className="wh-packing-mono">{pallet?.palletNo ?? singleCarton?.cartonNo}</span>
                </span>
                {pallet ? (
                  <Tag color="orange">
                    {t('packingUi.receive.checked', { checked: checkedGuids.length, total: pendingCount })}
                  </Tag>
                ) : null}
              </div>
              <div className="wh-packing-muted wh-packing-receive-from-line">
                {[t('packingUi.receive.fromLine', { name: origin }), carrier, consignmentNo].filter(Boolean).join(' · ')}
              </div>
            </div>

            <div className="wh-packing-receive-body">
              {lookup.viaCartonLabel ? (
                <Alert
                  type="info"
                  showIcon
                  message={t('packingUi.receive.viaCarton', { carton: lookup.scannedCartonNo ?? '' })}
                />
              ) : null}
              {blocked ? (
                <Alert
                  type="warning"
                  showIcon
                  message={blocked === 'notForYou' ? t('packingUi.receive.notForYou') : t('packingUi.receive.notInTransit')}
                />
              ) : null}

              {orderEntries.length > 0 ? (
                <div className="wh-packing-receive-orders">
                  {orderEntries.map((entry) => (
                    <div className="wh-packing-receive-order" key={entry.orderGuid}>
                      <span className="wh-packing-receive-order-info">
                        <b className="wh-packing-mono">{t('packingUi.orderReceive.entryOrder', { no: entry.orderNo })}</b>
                        <span className="wh-packing-muted">{t('packingUi.orderReceive.entryCartons', { count: entry.cartonCount })}</span>
                      </span>
                      <Button
                        size="small"
                        onClick={() => setOrderReceive({ orderGuid: entry.orderGuid, orderNo: entry.orderNo })}
                      >
                        {t('packingUi.orderReceive.entry')}
                      </Button>
                    </div>
                  ))}
                </div>
              ) : null}

              {pallet ? (
                <div className="wh-packing-receive-cartons">
                  {panelCartons.map((carton) => {
                    const pending = carton.status === PACK_CARTON_STATUS.InTransit
                    const isChecked = !pending || checked.has(carton.cartonGuid)
                    return (
                      <button
                        type="button"
                        key={carton.cartonGuid}
                        className="wh-packing-chk"
                        disabled={!pending || !canReceive}
                        onClick={() => toggleCarton(carton)}
                        aria-pressed={isChecked}
                      >
                        {isChecked ? (
                          <CheckCircleFilled className="wh-packing-chk-on" />
                        ) : (
                          <span className="wh-packing-chk-off" />
                        )}
                        <span className="wh-packing-mono">{carton.cartonNo}</span>
                        {carton.orderNo ? <span className="wh-packing-mono wh-packing-muted">{carton.orderNo}</span> : null}
                        <span className="wh-packing-mini-spacer" />
                        <span className="wh-packing-muted">
                          {pending
                            ? t('packingUi.summary.skuPieces', { sku: carton.skuCount, pieces: carton.pieceCount })
                            : t('packingUi.receive.alreadyReceived')}
                        </span>
                      </button>
                    )
                  })}
                </div>
              ) : singleCarton ? (
                <div className="wh-packing-receive-cartons">
                  <div className="wh-packing-chk wh-packing-chk-static">
                    <CheckCircleFilled className="wh-packing-chk-on" />
                    <span className="wh-packing-mono">{singleCarton.cartonNo}</span>
                    {singleCarton.orderNo ? <span className="wh-packing-mono wh-packing-muted">{singleCarton.orderNo}</span> : null}
                    <span className="wh-packing-mini-spacer" />
                    <span className="wh-packing-muted">
                      {t('packingUi.summary.skuPieces', { sku: singleCarton.skuCount, pieces: singleCarton.pieceCount })}
                    </span>
                  </div>
                </div>
              ) : null}

              {canReceive ? (
                <div className="wh-packing-kv">
                  <span>{t('packingUi.receive.putAt')}</span>
                  <LocationSelect className="wh-packing-location-select" value={location} onChange={setLocation} />
                </div>
              ) : null}
              {pallet && canReceive ? (
                <Alert type="info" showIcon className="wh-packing-receive-hint" message={t('packingUi.receive.lostLabelHint')} />
              ) : null}
            </div>

            <div className="wh-packing-receive-foot">
              {pallet ? (
                <>
                  <Button
                    disabled={!canReceive || checkedGuids.length === 0 || receiving}
                    loading={receiving}
                    onClick={() => void receiveChecked()}
                  >
                    {t('packingUi.receive.receiveChecked', { count: checkedGuids.length })}
                  </Button>
                  <Button
                    type="primary"
                    icon={<CheckOutlined />}
                    disabled={!canReceive || receiving}
                    onClick={() => {
                      setCountInput(0)
                      setCountOpen(true)
                    }}
                  >
                    {t('packingUi.receive.receiveWhole', { count: pendingCount })}
                  </Button>
                </>
              ) : (
                <Button
                  type="primary"
                  icon={<CheckOutlined />}
                  block
                  disabled={!canReceive || receiving}
                  loading={receiving}
                  onClick={() => void receiveSingleCarton()}
                >
                  {t('packingUi.receive.receiveCarton')}
                </Button>
              )}
            </div>
          </>
        ) : (
          <div className="wh-packing-receive-empty">
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('packingUi.receive.empty')} />
            <Alert type="info" showIcon className="wh-packing-receive-hint" message={t('packingUi.receive.lostLabelHint')} />
          </div>
        )}
      </div>

      <Modal
        open={countOpen}
        title={t('packingUi.receive.countTitle')}
        width={420}
        okText={t('packingUi.receive.countOk', { count: pendingCount })}
        cancelText={t('common.cancel')}
        okButtonProps={{ disabled: !isCartonCountConfirmed(countInput, pendingCount) }}
        confirmLoading={receiving}
        onOk={() => void receiveWholePallet()}
        onCancel={() => setCountOpen(false)}
        destroyOnClose
      >
        <Typography.Paragraph>{t('packingUi.receive.countBody')}</Typography.Paragraph>
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
              void receiveWholePallet()
            }
          }}
          addonAfter={t('packingUi.receive.countUnit')}
        />
        <Typography.Paragraph type="secondary" className="wh-packing-count-hint">
          {t('packingUi.receive.countMismatchHint')}
        </Typography.Paragraph>
      </Modal>

      <OrderReceiveDrawer
        orderGuid={orderReceive?.orderGuid ?? null}
        orderNo={orderReceive?.orderNo}
        onClose={() => setOrderReceive(null)}
        onReceived={() => void afterOrderReceive()}
      />
    </div>
  )
}
