import { ArrowLeftOutlined, PlusOutlined } from '@ant-design/icons'
import { Alert, App, Button, Input, Segmented, Select, Switch, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'

import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import {
  getSupplierPreorderActiveStores,
  getSupplierPreorderOrderers,
  removeSupplierPreorderOrderer,
  saveSupplierPreorderOrderer,
} from '../../../services/supplierPreorderService'
import type { SupplierPreorderOrderer, SupplierPreorderOrderersOverview, SupplierPreorderStoreRef } from '../../../types/supplierPreorder'
import AuthLogsTable from '../Admin/AuthLogsTable'
import { formatSydneyDateTime, getErrorText, isAbortError } from '../Admin/supplierPreorderAdmin.logic'
import '../Admin/styles.css'

import OrdererEditModal from './OrdererEditModal'
import {
  countCanViewOtherStores,
  filterOrderers,
  getOrdererDisplayName,
  getPersonInitial,
  type OrdererViewFilter,
} from './orderers.logic'
import './registerSpOrderersMessages'
import './styles.css'

const EMPTY_OVERVIEW: SupplierPreorderOrderersOverview = { orderers: [], storesWithoutOrderer: [] }

/** 下单人员授权（/supplier-preorders/orderers）：独立于人员管理，只认这里的授权。 */
export default function SupplierPreorderOrderersPage() {
  const { t } = useTranslation()
  const { message, modal } = App.useApp()
  const navigate = useNavigate()
  const [overview, setOverview] = useState<SupplierPreorderOrderersOverview>(EMPTY_OVERVIEW)
  const [loading, setLoading] = useState(false)
  const [stores, setStores] = useState<SupplierPreorderStoreRef[]>([])
  const [storesLoading, setStoresLoading] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [storeFilter, setStoreFilter] = useState<string | undefined>()
  const [viewFilter, setViewFilter] = useState<OrdererViewFilter>('all')
  const [savingGuid, setSavingGuid] = useState<string | null>(null)
  const [modalOpen, setModalOpen] = useState(false)
  const [editing, setEditing] = useState<SupplierPreorderOrderer | null>(null)
  const [logsRefreshKey, setLogsRefreshKey] = useState(0)

  const load = useCallback(
    (signal?: AbortSignal) => {
      setLoading(true)
      getSupplierPreorderOrderers(signal)
        .then((result) => {
          if (!signal?.aborted) setOverview(result)
        })
        .catch((error) => {
          if (signal?.aborted || isAbortError(error)) return
          message.error(getErrorText(error, t('spOrderers.common.loadFailed')))
        })
        .finally(() => {
          if (!signal?.aborted) setLoading(false)
        })
    },
    [message, t],
  )

  useEffect(() => {
    const controller = new AbortController()
    load(controller.signal)
    return () => controller.abort()
  }, [load])

  useEffect(() => {
    setStoresLoading(true)
    getSupplierPreorderActiveStores()
      .then(setStores)
      .catch((error) => message.error(getErrorText(error, t('spOrderers.common.storesLoadFailed'))))
      .finally(() => setStoresLoading(false))
  }, [message, t])

  const afterChange = useCallback(() => {
    // 授权变更会影响「无人可下单的分店」提示和操作记录，统一重新拉取
    load()
    setLogsRefreshKey((value) => value + 1)
  }, [load])

  const handleToggleView = useCallback(
    async (person: SupplierPreorderOrderer, checked: boolean) => {
      setSavingGuid(person.userGuid)
      try {
        await saveSupplierPreorderOrderer(person.userGuid, {
          storeGuids: person.stores.map((store) => store.storeGuid),
          canViewOtherStores: checked,
        })
        message.success(t('spOrderers.table.viewSaved', { name: getOrdererDisplayName(person) }))
        afterChange()
      } catch (error) {
        message.error(getErrorText(error, t('spOrderers.common.saveFailed')))
      } finally {
        setSavingGuid(null)
      }
    },
    [afterChange, message, t],
  )

  const handleRemove = useCallback(
    (person: SupplierPreorderOrderer) => {
      modal.confirm({
        title: t('spOrderers.table.removeConfirmTitle', { name: getOrdererDisplayName(person) }),
        content: t('spOrderers.table.removeConfirmContent', {
          stores: person.stores.map((store) => store.storeName).join(t('spOrderers.common.listSep')) || '—',
        }),
        okText: t('spOrderers.table.remove'),
        okButtonProps: { danger: true },
        cancelText: t('spOrderers.common.cancel'),
        onOk: async () => {
          try {
            await removeSupplierPreorderOrderer(person.userGuid)
            message.success(t('spOrderers.table.removed'))
            afterChange()
          } catch (error) {
            message.error(getErrorText(error, t('spOrderers.common.saveFailed')))
          }
        },
      })
    },
    [afterChange, message, modal, t],
  )

  const openAdd = () => {
    setEditing(null)
    setModalOpen(true)
  }

  const openEdit = useCallback((person: SupplierPreorderOrderer) => {
    setEditing(person)
    setModalOpen(true)
  }, [])

  const filtered = useMemo(
    () => filterOrderers(overview.orderers, { keyword, storeGuid: storeFilter, view: viewFilter }),
    [keyword, overview.orderers, storeFilter, viewFilter],
  )

  const columns = useMemo<ColumnsType<SupplierPreorderOrderer>>(
    () => [
      {
        title: t('spOrderers.table.colPerson'),
        key: 'person',
        width: 200,
        render: (_, person) => {
          const name = getOrdererDisplayName(person)
          return (
            <div className="sp-orderers-person">
              <span className="sp-orderers-avatar" aria-hidden="true">
                {getPersonInitial(name)}
              </span>
              <div className="sp-admin-cell-stack">
                <span className="sp-admin-strong">
                  {name}
                  {!person.isActive ? (
                    <Tag className="sp-orderers-inactive" bordered={false}>
                      {t('spOrderers.table.inactive')}
                    </Tag>
                  ) : null}
                </span>
                <span className="sp-admin-muted sp-admin-small">{person.userName}</span>
              </div>
            </div>
          )
        },
      },
      {
        title: t('spOrderers.table.colPrimaryStore'),
        dataIndex: 'primaryStoreName',
        width: 150,
        render: (value?: string | null) => (
          <span className="sp-admin-muted">{value ? t('spOrderers.table.primaryStore', { store: value }) : t('spOrderers.table.noPrimaryStore')}</span>
        ),
      },
      {
        title: t('spOrderers.table.colStores'),
        key: 'stores',
        render: (_, person) =>
          person.stores.length ? (
            <div className="sp-orderers-tags">
              {person.stores.map((store) => (
                <Tag key={store.storeGuid} color="blue" bordered={false}>
                  {store.storeName}
                </Tag>
              ))}
            </div>
          ) : (
            <span className="sp-admin-danger">{t('spOrderers.table.noStores')}</span>
          ),
      },
      {
        title: (
          <div className="sp-admin-cell-stack">
            <span>{t('spOrderers.table.colCanView')}</span>
            <span className="sp-admin-muted sp-admin-small">{t('spOrderers.table.colCanViewHint')}</span>
          </div>
        ),
        key: 'canView',
        width: 130,
        render: (_, person) => (
          <Switch
            size="small"
            checked={person.canViewOtherStores}
            loading={savingGuid === person.userGuid}
            disabled={savingGuid !== null && savingGuid !== person.userGuid}
            aria-label={t('spOrderers.table.colCanView')}
            onChange={(checked) => void handleToggleView(person, checked)}
          />
        ),
      },
      {
        title: t('spOrderers.table.colPermission'),
        key: 'permission',
        width: 220,
        render: (_, person) =>
          person.hasOrderEntryPermission ? (
            <span className="sp-admin-muted">{t('spOrderers.table.permissionOk')}</span>
          ) : (
            <span className="sp-admin-danger sp-admin-small">{t('spOrderers.table.permissionMissing')}</span>
          ),
      },
      {
        title: t('spOrderers.table.colUpdated'),
        key: 'updated',
        width: 170,
        render: (_, person) => (
          <span className="sp-admin-small sp-admin-number">
            {person.updatedBy ? `${person.updatedBy} · ` : ''}
            {formatSydneyDateTime(person.updatedAtUtc)}
          </span>
        ),
      },
      {
        title: t('spOrderers.table.colActions'),
        key: 'actions',
        width: 120,
        fixed: 'right',
        render: (_, person) => (
          <div className="sp-admin-row-actions">
            <Button type="link" size="small" onClick={() => openEdit(person)}>
              {t('spOrderers.table.edit')}
            </Button>
            <Button type="link" size="small" danger onClick={() => handleRemove(person)}>
              {t('spOrderers.table.remove')}
            </Button>
          </div>
        ),
      },
    ],
    [handleRemove, handleToggleView, openEdit, savingGuid, t],
  )

  const missingStores = overview.storesWithoutOrderer

  return (
    <PageContainer
      title={t('spOrderers.title')}
      subtitle={t('spOrderers.subtitle')}
      extra={
        <div className="sp-admin-actions">
          <Button icon={<ArrowLeftOutlined />} onClick={() => navigate('/supplier-preorders')}>
            {t('spOrderers.backToForms')}
          </Button>
          <Button type="primary" icon={<PlusOutlined />} onClick={openAdd}>
            {t('spOrderers.add')}
          </Button>
        </div>
      }
    >
      <div className="sp-admin-card">
        <div className="sp-admin-toolbar">
          <Input.Search
            className="sp-admin-search"
            allowClear
            placeholder={t('spOrderers.searchPlaceholder')}
            onChange={(event) => setKeyword(event.target.value)}
            onSearch={setKeyword}
          />
          <Select<string>
            className="sp-orderers-store-filter"
            allowClear
            showSearch
            optionFilterProp="label"
            value={storeFilter}
            placeholder={t('spOrderers.storeFilter')}
            loading={storesLoading}
            options={stores.map((store) => ({ value: store.storeGuid, label: store.storeName }))}
            onChange={setStoreFilter}
          />
          <Segmented<OrdererViewFilter>
            value={viewFilter}
            onChange={setViewFilter}
            options={[
              { value: 'all', label: t('spOrderers.filterAll', { count: overview.orderers.length }) },
              { value: 'canView', label: t('spOrderers.filterCanView', { count: countCanViewOtherStores(overview.orderers) }) },
            ]}
          />
          <span className="sp-admin-toolbar-spacer" />
          {missingStores.length ? (
            <span className="sp-orderers-missing">
              {t('spOrderers.storesWithoutOrderer', {
                count: missingStores.length,
                stores: missingStores.map((store) => store.storeName).join(t('spOrderers.common.listSep')),
              })}
            </span>
          ) : null}
        </div>
        <MeasuredTable<SupplierPreorderOrderer>
          metricId="supplier-preorder-orderers"
          className="sp-admin-table"
          rowKey="userGuid"
          size="middle"
          loading={loading}
          columns={columns}
          dataSource={filtered}
          scroll={{ x: 1150 }}
          pagination={false}
          locale={{ emptyText: overview.orderers.length ? t('spOrderers.emptyFiltered') : t('spOrderers.empty') }}
        />
      </div>

      <Alert
        className="sp-orderers-rules"
        type="warning"
        message={t('spOrderers.rulesTitle')}
        description={
          <ol className="sp-orderers-rule-list">
            <li>{t('spOrderers.rule1')}</li>
            <li>{t('spOrderers.rule2')}</li>
            <li>{t('spOrderers.rule3')}</li>
            <li>{t('spOrderers.rule4')}</li>
          </ol>
        }
      />

      <div className="sp-admin-card">
        <div className="sp-admin-toolbar">
          <h3 className="sp-admin-section-title">{t('spOrderers.logsTitle')}</h3>
        </div>
        <AuthLogsTable refreshKey={logsRefreshKey} />
      </div>

      <OrdererEditModal
        open={modalOpen}
        editing={editing}
        orderers={overview.orderers}
        stores={stores}
        storesLoading={storesLoading}
        onClose={() => setModalOpen(false)}
        onSaved={() => {
          setModalOpen(false)
          afterChange()
        }}
      />
    </PageContainer>
  )
}
