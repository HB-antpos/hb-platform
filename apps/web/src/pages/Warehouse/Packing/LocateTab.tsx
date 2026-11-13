import { BarcodeOutlined, EnvironmentOutlined, PictureOutlined } from '@ant-design/icons'
import { Button, Empty, Input, Typography } from 'antd'
import type { InputRef } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { useBarcodeScanner } from '../../../hooks/useBarcodeScanner'
import {
  getPackCarton,
  getPackPallet,
  locatePackProduct,
  readPackingError,
  resolvePackCode,
} from '../../../services/warehousePackingService'
import type {
  PackCartonDetail,
  PackCartonItem,
  PackLocateResult,
  PackLocateRow,
  PackPalletDetail,
  PackPalletProduct,
} from '../../../types/warehousePacking'

import { formatPackTime, groupLocateRows, looksLikePackNo } from './packingLogic'
import { CartonStatusTag, DestinationText, PalletStatusTag, usePackingPage } from './packingShared'

/** 查询结果三种形态：商品在哪些位置；或输入箱号 / 托盘号时该箱 / 托盘里有什么。 */
type LocateView =
  | { kind: 'product'; result: PackLocateResult }
  | { kind: 'carton'; carton: PackCartonDetail }
  | { kind: 'pallet'; pallet: PackPalletDetail }

/** 位置表的行：分组行（货位 + 小计）与明细行（一个箱里的这个商品）。 */
type LocateTableRow =
  | { key: string; group: true; locationCode: string; quantity: number }
  | { key: string; group: false; row: PackLocateRow }

export default function LocateTab({ active }: { active: boolean }) {
  const { t } = useTranslation()
  const { openCarton, openPallet, dataVersion } = usePackingPage()
  const [text, setText] = useState('')
  const [queried, setQueried] = useState('')
  const [view, setView] = useState<LocateView | null>(null)
  const [errorText, setErrorText] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const inputRef = useRef<InputRef>(null)
  const requestSeqRef = useRef(0)

  const query = useCallback(async (raw: string) => {
    const code = raw.trim()
    if (!code) {
      return
    }
    const sequence = ++requestSeqRef.current
    setQueried(code)
    setLoading(true)
    setErrorText(null)
    try {
      let next: LocateView | null = null
      // 箱号 / 托盘号先交给后端识别；不是的话按商品码查位置。
      const resolved = looksLikePackNo(code) ? await resolvePackCode(code) : null
      if (resolved?.kind === 'carton') {
        next = { kind: 'carton', carton: await getPackCarton(resolved.guid) }
      } else if (resolved?.kind === 'pallet') {
        next = { kind: 'pallet', pallet: await getPackPallet(resolved.guid) }
      } else {
        next = { kind: 'product', result: await locatePackProduct(code) }
      }
      if (sequence === requestSeqRef.current) {
        setView(next)
      }
    } catch (error) {
      if (sequence === requestSeqRef.current) {
        setView(null)
        setErrorText(readPackingError(error).message)
      }
    } finally {
      if (sequence === requestSeqRef.current) {
        setLoading(false)
      }
    }
  }, [])

  const submit = (value: string) => {
    setText('')
    void query(value)
  }

  useEffect(() => {
    if (active) {
      inputRef.current?.focus()
    }
  }, [active])

  // 别处改了箱 / 托盘（移出、重新开箱等）后，重查当前条件。
  const seenVersionRef = useRef(dataVersion)
  useEffect(() => {
    if (seenVersionRef.current !== dataVersion) {
      seenVersionRef.current = dataVersion
      if (queried) {
        void query(queried)
      }
    }
  }, [dataVersion, queried, query])

  // 焦点不在输入框时扫码枪也能直接查。
  useBarcodeScanner({ enabled: active, onScan: (value) => void query(value) })

  const locateRows = useMemo<LocateTableRow[]>(() => {
    if (view?.kind !== 'product') {
      return []
    }
    return groupLocateRows(view.result.rows).flatMap((group) => [
      { key: `group:${group.locationCode}`, group: true as const, locationCode: group.locationCode, quantity: group.quantity },
      ...group.rows.map((row) => ({ key: `row:${row.cartonGuid}`, group: false as const, row })),
    ])
  }, [view])

  const locateColumns = useMemo<ColumnsType<LocateTableRow>>(
    () => [
      {
        title: t('warehouseUi.packing.columns.location'),
        key: 'location',
        width: 200,
        onCell: (record) => ({ colSpan: record.group ? 3 : 1 }),
        render: (_value, record) =>
          record.group ? (
            <b className="wh-packing-locate-group">
              <EnvironmentOutlined />
              {record.locationCode || t('warehouseUi.packing.locate.noLocation')}
            </b>
          ) : (
            <span className="wh-packing-locate-indent wh-packing-muted">{record.row.locationCode || '—'}</span>
          ),
      },
      {
        title: t('warehouseUi.packing.columns.pallet'),
        key: 'pallet',
        width: 150,
        onCell: (record) => ({ colSpan: record.group ? 0 : 1 }),
        render: (_value, record) =>
          record.group ? null : record.row.palletGuid ? (
            <Button
              type="link"
              size="small"
              className="wh-packing-mono wh-packing-link-cell"
              onClick={() => openPallet(record.row.palletGuid!)}
            >
              {record.row.palletNo}
            </Button>
          ) : (
            <Typography.Text type="secondary">{t('warehouseUi.packing.loose')}</Typography.Text>
          ),
      },
      {
        title: t('warehouseUi.packing.columns.cartonNo'),
        key: 'carton',
        width: 150,
        onCell: (record) => ({ colSpan: record.group ? 0 : 1 }),
        render: (_value, record) => (record.group ? null : <span className="wh-packing-mono">{record.row.cartonNo}</span>),
      },
      {
        title: t('warehouseUi.packing.columns.quantity'),
        key: 'quantity',
        width: 90,
        align: 'right',
        render: (_value, record) => <b>{record.group ? record.quantity : record.row.quantity}</b>,
      },
      {
        title: t('warehouseUi.packing.columns.destination'),
        key: 'destination',
        ellipsis: true,
        onCell: (record) => ({ colSpan: record.group ? 5 : 1 }),
        render: (_value, record) => (record.group ? null : <DestinationText destination={record.row.destination} t={t} />),
      },
      {
        title: t('common.status'),
        key: 'status',
        width: 90,
        onCell: (record) => ({ colSpan: record.group ? 0 : 1 }),
        render: (_value, record) => (record.group ? null : <CartonStatusTag status={record.row.cartonStatus} t={t} />),
      },
      {
        title: t('warehouseUi.packing.columns.order'),
        key: 'order',
        width: 130,
        onCell: (record) => ({ colSpan: record.group ? 0 : 1 }),
        render: (_value, record) =>
          record.group ? null : record.row.orderNo ? <span className="wh-packing-mono">{record.row.orderNo}</span> : '—',
      },
      {
        title: t('warehouseUi.packing.columns.updatedAt'),
        key: 'updated',
        width: 110,
        onCell: (record) => ({ colSpan: record.group ? 0 : 1 }),
        render: (_value, record) =>
          record.group ? null : (
            <Typography.Text type="secondary">{formatPackTime(record.row.updatedAtUtc)}</Typography.Text>
          ),
      },
      {
        title: t('warehouseUi.packing.columns.actions'),
        key: 'actions',
        width: 100,
        onCell: (record) => ({ colSpan: record.group ? 0 : 1 }),
        render: (_value, record) =>
          record.group ? null : (
            <Button type="link" size="small" onClick={() => openCarton(record.row.cartonGuid)}>
              {t('warehouseUi.packing.action.cartonItems')}
            </Button>
          ),
      },
    ],
    [openCarton, openPallet, t],
  )

  const contentColumns = useMemo<ColumnsType<PackCartonItem | PackPalletProduct>>(
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
        width: 150,
        render: (value: string | null) => <span className="wh-packing-mono">{value || '—'}</span>,
      },
      {
        title: t('warehouseUi.packing.columns.barcode'),
        dataIndex: 'barcode',
        width: 170,
        render: (value: string | null) => <span className="wh-packing-mono">{value || '—'}</span>,
      },
      { title: t('warehouseUi.packing.columns.quantity'), dataIndex: 'quantity', width: 100, align: 'right' },
      {
        title: t('warehouseUi.packing.columns.cartons'),
        key: 'cartonCount',
        width: 80,
        align: 'right',
        render: (_value, record) => ('cartonCount' in record ? record.cartonCount : 1),
      },
    ],
    [t],
  )

  const renderProduct = (result: PackLocateResult) => (
    <>
      <div className="wh-packing-locate-summary">
        <div className="wh-packing-panel wh-packing-locate-product">
          <div className="wh-packing-thumb wh-packing-thumb-lg">
            {result.product.productImage ? <img src={result.product.productImage} alt="" /> : <PictureOutlined />}
          </div>
          <div className="wh-packing-last-body">
            <div className="wh-packing-last-name">{result.product.productName || result.product.productCode}</div>
            <div className="wh-packing-mono wh-packing-muted">
              {[
                result.product.itemNumber,
                result.product.barcode,
                result.middlePackageQuantity
                  ? t('warehouseUi.packing.station.middlePack', { count: result.middlePackageQuantity })
                  : null,
                result.packingQuantity ? t('warehouseUi.packing.station.packingSize', { count: result.packingQuantity }) : null,
              ]
                .filter(Boolean)
                .join(' · ')}
            </div>
          </div>
        </div>
        <div className="wh-packing-panel wh-packing-stat">
          <div className="wh-packing-muted">{t('warehouseUi.packing.locate.packedTotal')}</div>
          <div className="wh-packing-stat-number">
            {result.packedQuantity}
            <small>{t('warehouseUi.packing.unit.piece')}</small>
          </div>
        </div>
        <div className="wh-packing-panel wh-packing-stat">
          <div className="wh-packing-muted">{t('warehouseUi.packing.locate.spread')}</div>
          <div className="wh-packing-stat-number">
            {result.cartonCount}
            <small>{t('warehouseUi.packing.locate.spreadUnit', { pallets: result.palletCount })}</small>
          </div>
        </div>
        <div className="wh-packing-panel wh-packing-stat">
          <div className="wh-packing-muted">{t('warehouseUi.packing.locate.stock')}</div>
          <div className="wh-packing-stat-number">
            {result.stockQuantity ?? '—'}
            {result.stockQuantity === null || result.stockQuantity === undefined ? null : (
              <small>{t('warehouseUi.packing.unit.piece')}</small>
            )}
          </div>
        </div>
      </div>
      <div className="wh-packing-panel wh-packing-list">
        <MeasuredTable<LocateTableRow>
          metricId="warehouse.packing.locate-rows"
          size="middle"
          rowKey="key"
          columns={locateColumns}
          dataSource={locateRows}
          pagination={false}
          scroll={{ x: 1150 }}
          rowClassName={(record) => (record.group ? 'wh-packing-row-group' : '')}
          locale={{ emptyText: t('warehouseUi.packing.locate.notPacked') }}
        />
      </div>
    </>
  )

  const renderContent = (current: Extract<LocateView, { kind: 'carton' | 'pallet' }>) => {
    const isCarton = current.kind === 'carton'
    const rows: (PackCartonItem | PackPalletProduct)[] = isCarton ? current.carton.items : current.pallet.products
    const summary = isCarton ? current.carton : current.pallet
    return (
      <>
        <div className="wh-packing-panel wh-packing-locate-content-head">
          <span className="wh-packing-side-title">
            {isCarton ? t('warehouseUi.packing.carton') : t('warehouseUi.packing.pallet')}
            <span className="wh-packing-mono">{isCarton ? current.carton.cartonNo : current.pallet.palletNo}</span>
          </span>
          {isCarton ? (
            <CartonStatusTag status={current.carton.status} t={t} />
          ) : (
            <PalletStatusTag status={current.pallet.status} t={t} />
          )}
          <DestinationText destination={summary.destination} t={t} />
          <span>
            {t('warehouseUi.packing.columns.location')}：
            {summary.locationCode || (isCarton && current.carton.palletNo ? current.carton.palletNo : '—')}
          </span>
          <b>
            {isCarton
              ? t('warehouseUi.packing.summary.skuPieces', {
                  sku: current.carton.skuCount,
                  pieces: current.carton.pieceCount,
                })
              : t('warehouseUi.packing.summary.palletTotal', {
                  cartons: current.pallet.cartonCount,
                  sku: current.pallet.skuCount,
                  pieces: current.pallet.pieceCount,
                })}
          </b>
          <span className="wh-packing-filters-spacer" />
          <Button
            onClick={() => (isCarton ? openCarton(current.carton.cartonGuid) : openPallet(current.pallet.palletGuid))}
          >
            {t('common.detail')}
          </Button>
        </div>
        <div className="wh-packing-panel wh-packing-list">
          <MeasuredTable<PackCartonItem | PackPalletProduct>
            metricId="warehouse.packing.locate-content"
            size="middle"
            rowKey="productCode"
            columns={contentColumns}
            dataSource={rows}
            pagination={false}
          />
        </div>
      </>
    )
  }

  return (
    <div className="wh-packing-locate">
      <div className="wh-packing-filters wh-packing-filters-plain">
        <Input.Search
          ref={inputRef}
          className="wh-packing-locate-input"
          size="large"
          allowClear
          autoComplete="off"
          loading={loading}
          prefix={<BarcodeOutlined />}
          placeholder={t('warehouseUi.packing.locate.placeholder')}
          value={text}
          onChange={(event) => setText(event.target.value)}
          onSearch={submit}
        />
        {queried ? (
          <Typography.Text type="secondary">
            {t('warehouseUi.packing.locate.queried')}
            <span className="wh-packing-mono"> {queried}</span>
          </Typography.Text>
        ) : null}
        <span className="wh-packing-filters-spacer" />
        <Typography.Text type="secondary">{t('warehouseUi.packing.locate.hint')}</Typography.Text>
      </div>

      {errorText ? (
        <div className="wh-packing-panel wh-packing-locate-empty">
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={`${queried}：${errorText}`} />
        </div>
      ) : view === null ? (
        <div className="wh-packing-panel wh-packing-locate-empty">
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('warehouseUi.packing.locate.empty')} />
        </div>
      ) : view.kind === 'product' ? (
        renderProduct(view.result)
      ) : (
        renderContent(view)
      )}
    </div>
  )
}
