import { ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Select, Space, Switch, Tag, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { updateMyobStoreMappings } from '../../../services/myobService'
import type { MyobConnection, MyobStoreMappingItem, MyobStoreMappings } from '../../../types/myob'
import {
  applySuggestions,
  buildMappingChanges,
  buildMappingOptions,
  countApplicableSuggestions,
  extractMyobError,
  filterMappingStores,
  formatAbnDisplay,
  getEffectiveConnectionGuid,
  setMappingDraft,
  type MappingDrafts,
} from './myobLogic'

interface StoreMappingsPanelProps {
  mappings: MyobStoreMappings
  /** 连接页的全部连接（含非 Active），用于给「已映射但已失效」的连接显示公司名。 */
  allConnections: MyobConnection[]
  loading: boolean
  error: string | null
  onReload: () => void
  /** 保存成功后回传后端返回的完整映射表。 */
  onSaved: (mappings: MyobStoreMappings) => void
}

export default function StoreMappingsPanel({
  mappings,
  allConnections,
  loading,
  error,
  onReload,
  onSaved,
}: StoreMappingsPanelProps) {
  const { t } = useTranslation()
  const [rawDrafts, setRawDrafts] = useState<MappingDrafts>({})
  const [showInactive, setShowInactive] = useState(false)
  const [saving, setSaving] = useState(false)

  const selectableGuids = useMemo(
    () => new Set(mappings.connections.map((item) => item.connectionGuid)),
    [mappings.connections],
  )
  const nameByGuid = useMemo(() => {
    const map = new Map<string, string>()
    for (const item of [...allConnections, ...mappings.connections]) {
      map.set(item.connectionGuid, item.businessName || item.businessId)
    }
    return map
  }, [allConnections, mappings.connections])

  // 草稿指向的连接若已不可选（例如刚被断开），丢弃该草稿；移除映射（null）与不存在的分店也一并清理
  const drafts = useMemo<MappingDrafts>(() => {
    const storeCodes = new Set(mappings.stores.map((store) => store.storeCode))
    return Object.fromEntries(
      Object.entries(rawDrafts).filter(
        ([code, guid]) => storeCodes.has(code) && (guid === null || selectableGuids.has(guid)),
      ),
    )
  }, [rawDrafts, mappings.stores, selectableGuids])

  const options = useMemo(
    () => buildMappingOptions(mappings.connections, allConnections, mappings.stores),
    [mappings.connections, allConnections, mappings.stores],
  )
  const rows = useMemo(
    () => filterMappingStores(mappings.stores, showInactive, drafts),
    [mappings.stores, showInactive, drafts],
  )
  const changes = useMemo(() => buildMappingChanges(mappings.stores, drafts), [mappings.stores, drafts])
  // 「一键采用」只针对当前可见的行，隐藏的未启用分店不被悄悄改动
  const applicableCount = countApplicableSuggestions(rows, drafts, selectableGuids)

  const handleChange = (store: MyobStoreMappingItem, connectionGuid: string | null) =>
    setRawDrafts(setMappingDraft(drafts, store, connectionGuid))

  const handleAdoptAll = () => setRawDrafts(applySuggestions(rows, drafts, selectableGuids))

  const handleSave = async () => {
    if (changes.length === 0 || saving) {
      return
    }
    setSaving(true)
    try {
      // 只提交变化的行
      const saved = await updateMyobStoreMappings(changes)
      onSaved(saved)
      setRawDrafts({})
      message.success(t('myob.mappings.saved', { count: changes.length }))
    } catch (err) {
      // 失败保留草稿，方便修正后重试
      message.error(extractMyobError(err, t('myob.mappings.saveFailed')).message)
    } finally {
      setSaving(false)
    }
  }

  const columns: ColumnsType<MyobStoreMappingItem> = [
    {
      title: t('myob.mappings.columns.store'),
      key: 'store',
      width: 240,
      render: (_, store) => (
        <Space size={6} wrap>
          <span className="myob-mono">{store.storeCode}</span>
          <span>{store.storeName}</span>
          {store.isActive ? null : <Tag style={{ marginInlineEnd: 0 }}>{t('myob.mappings.inactive')}</Tag>}
        </Space>
      ),
    },
    {
      title: t('myob.mappings.columns.abn'),
      key: 'abn',
      width: 150,
      render: (_, store) =>
        store.storeAbn ? (
          <span className="myob-mono">{formatAbnDisplay(store.storeAbn)}</span>
        ) : (
          <span className="myob-muted">{t('myob.mappings.noAbn')}</span>
        ),
    },
    {
      title: t('myob.mappings.columns.mapping'),
      key: 'mapping',
      width: 300,
      render: (_, store) => (
        <Select
          allowClear
          showSearch
          optionFilterProp="label"
          style={{ width: '100%', minWidth: 220 }}
          placeholder={t('myob.mappings.select')}
          disabled={saving}
          value={getEffectiveConnectionGuid(store, drafts) ?? undefined}
          options={options.map((option) => ({
            value: option.value,
            label: option.disabled
              ? t('myob.mappings.needsReauthOption', { name: option.label })
              : option.label,
            disabled: option.disabled,
          }))}
          // 清空 → undefined → null（移除映射）
          onChange={(value: string | undefined) => handleChange(store, value ?? null)}
        />
      ),
    },
    {
      title: t('myob.mappings.columns.suggestion'),
      key: 'suggestion',
      width: 280,
      render: (_, store) => {
        const suggested = store.suggestedConnectionGuid
        // 已选（含草稿）后不再显示建议；建议的连接不可选时也不显示
        if (!suggested || !selectableGuids.has(suggested) || getEffectiveConnectionGuid(store, drafts) !== null) {
          return <span className="myob-muted">--</span>
        }
        return (
          <span className="myob-suggestion">
            <span>{t('myob.mappings.suggestion', { name: nameByGuid.get(suggested) ?? suggested })}</span>
            <Button size="small" type="primary" ghost disabled={saving} onClick={() => handleChange(store, suggested)}>
              {t('myob.mappings.adopt')}
            </Button>
          </span>
        )
      },
    },
  ]

  return (
    <section className="myob-card" aria-labelledby="myob-mappings-title">
      <div className="myob-card-head">
        <div>
          <h2 className="myob-card-title" id="myob-mappings-title">
            {t('myob.mappings.title')}
          </h2>
          <div className="myob-card-hint">{t('myob.mappings.hint')}</div>
        </div>
        <Button icon={<ReloadOutlined />} loading={loading} disabled={saving} onClick={onReload}>
          {t('myob.reload')}
        </Button>
      </div>

      {error ? (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 12 }}
          message={t('myob.mappings.loadFailed')}
          description={error}
          action={
            <Button size="small" onClick={onReload}>
              {t('myob.connections.retry')}
            </Button>
          }
        />
      ) : null}

      {!loading && !error && mappings.connections.length === 0 ? (
        <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('myob.mappings.noConnections')} />
      ) : null}

      <div className="myob-toolbar" style={{ marginBottom: 12 }}>
        <Button disabled={applicableCount === 0 || saving} onClick={handleAdoptAll}>
          {t('myob.mappings.adoptAll', { count: applicableCount })}
        </Button>
        <label className="myob-toolbar">
          <Switch size="small" checked={showInactive} onChange={setShowInactive} />
          <span>{t('myob.mappings.showInactive')}</span>
        </label>
        <span className="myob-toolbar-spacer" />
        {changes.length > 0 ? (
          <Typography.Text type="warning">{t('myob.mappings.unsaved', { count: changes.length })}</Typography.Text>
        ) : null}
        <Button disabled={changes.length === 0 || saving} onClick={() => setRawDrafts({})}>
          {t('myob.mappings.discard')}
        </Button>
        <Button type="primary" loading={saving} disabled={changes.length === 0} onClick={() => void handleSave()}>
          {t('myob.mappings.save')}
        </Button>
      </div>

      <MeasuredTable<MyobStoreMappingItem>
        metricId="pos-admin.myob.store-mappings"
        rowKey="storeCode"
        size="middle"
        columns={columns}
        dataSource={rows}
        loading={loading && mappings.stores.length === 0}
        pagination={rows.length > 50 ? { pageSize: 50, showSizeChanger: false } : false}
        scroll={{ x: 'max-content' }}
        rowClassName={(store) => (Object.prototype.hasOwnProperty.call(drafts, store.storeCode) ? 'myob-row-dirty' : '')}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.mappings.empty')} /> }}
      />
    </section>
  )
}
