import { SearchOutlined } from '@ant-design/icons'
import { Button, Empty, Input, Space, Tag, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo, useState } from 'react'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { savePosSettingsGlobal } from '../../../services/posSettingsService'
import type { PosSettingDefinition, PosSettingLayer, PosSettingStoreSummary } from '../../../types/posSettings'
import ConfirmChangesModal from './ConfirmChangesModal'
import {
  applyProgress,
  buildSaveValues,
  countOverriddenStores,
  EMPTY_CELL,
  filterStores,
  globalEffective,
  storeOverrideEntries,
} from './logic'
import { SettingNameCell, settingName, TriControl, type Tr } from './parts'
import { useSettingsDraft } from './useSettingsDraft'

interface GlobalStoresTabProps {
  definitions: readonly PosSettingDefinition[]
  global: PosSettingLayer
  stores: readonly PosSettingStoreSummary[]
  canManage: boolean
  tr: Tr
  /** 请求异常 → 可展示文案；取消的请求返回 null（不提示）。 */
  errorText: (error: unknown) => string | null
  onOpenStore: (storeCode: string) => void
  /** 全局层保存成功后刷新页面数据。 */
  onChanged: () => void
}

/**
 * 「全局与门店」页签：左边是全局默认（未设置 / 开 / 关 三态），右边是各门店与全局不同的覆盖项。
 * 全局若只能开 / 关，上线就会把收银机本机已开的设置覆盖掉，所以全局层也必须能「未设置」（= 不下发）。
 */
export default function GlobalStoresTab({
  definitions,
  global,
  stores,
  canManage,
  tr,
  errorText,
  onOpenStore,
  onChanged,
}: GlobalStoresTabProps) {
  const { draft, changes, setTri, reset } = useSettingsDraft(definitions, global.values)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [keyword, setKeyword] = useState('')
  const visibleStores = useMemo(() => filterStores(stores, keyword), [keyword, stores])

  const save = async () => {
    try {
      await savePosSettingsGlobal({ values: buildSaveValues(changes) })
    } catch (error) {
      // 403（非管理员）等失败：把后端 message 带回确认弹窗，用户可以直接取消或重试。
      throw new Error(errorText(error) ?? tr('errors.unknown'))
    }
    message.success(tr('saved'))
    setConfirmOpen(false)
    onChanged()
  }

  const globalColumns: ColumnsType<PosSettingDefinition> = [
    {
      title: tr('global.colSetting'),
      key: 'setting',
      render: (_, definition) => <SettingNameCell definition={definition} tr={tr} />,
    },
    {
      title: tr('global.colValue'),
      key: 'value',
      width: 210,
      render: (_, definition) => {
        const tri = draft[definition.key] ?? 'inherit'
        const fallback = globalEffective(definition, {})
        return (
          <div className="pos-settings-control-cell">
            <TriControl
              layer="global"
              value={tri}
              onChange={(next) => setTri(definition.key, next)}
              disabled={!canManage}
              label={settingName(definition.key, tr)}
              tr={tr}
            />
            {tri === 'inherit' ? (
              <span className="pos-settings-desc">
                {fallback.value === null
                  ? tr('global.hintNative')
                  : tr('global.hintDefault', { value: fallback.value ? tr('value.on') : tr('value.off') })}
              </span>
            ) : null}
          </div>
        )
      },
    },
  ]

  const storeColumns: ColumnsType<PosSettingStoreSummary> = [
    {
      title: tr('stores.colStore'),
      key: 'store',
      width: 170,
      render: (_, store) => (
        <span className="pos-settings-store-name">
          <strong>{store.storeName}</strong>
          <span className="pos-settings-muted">{store.storeCode}</span>
        </span>
      ),
    },
    {
      title: tr('stores.colOverrides'),
      key: 'overrides',
      render: (_, store) => {
        const entries = storeOverrideEntries(definitions, store.overrides)
        if (entries.length === 0) return <span className="pos-settings-muted">{tr('stores.followGlobal')}</span>
        return (
          <Space size={[4, 4]} wrap>
            {entries.map((entry) => (
              <Tag key={entry.key} color={entry.value ? 'green' : undefined} className="pos-settings-override-tag">
                {settingName(entry.key, tr)}: {entry.value ? tr('value.on') : tr('value.off')}
              </Tag>
            ))}
          </Space>
        )
      },
    },
    {
      title: tr('stores.colDeviceOverrides'),
      key: 'deviceOverrides',
      width: 96,
      render: (_, store) => (store.deviceOverrideCount > 0
        ? <Tag color="purple">{tr('stores.deviceCount', { count: store.deviceOverrideCount })}</Tag>
        : <span className="pos-settings-muted">{EMPTY_CELL}</span>),
    },
    {
      title: tr('stores.colApplied'),
      key: 'applied',
      width: 104,
      render: (_, store) => {
        const progress = applyProgress(store)
        if (progress.kind === 'none') return <span className="pos-settings-muted">{EMPTY_CELL}</span>
        // 颜色之外带 ✓ / ◷ 符号，不只靠颜色区分「全部已应用」与「还有待同步」。
        return progress.kind === 'all'
          ? <Tag color="green">{`✓ ${progress.applied}/${progress.total}`}</Tag>
          : <Tag color="orange">{`◷ ${progress.applied}/${progress.total}`}</Tag>
      },
    },
    {
      title: '',
      key: 'actions',
      width: 76,
      render: (_, store) => (
        <Button size="small" onClick={() => onOpenStore(store.storeCode)}>
          {canManage ? tr('actions.edit') : tr('actions.view')}
        </Button>
      ),
    },
  ]

  return (
    <div className="pos-settings-grid">
      <section className="pos-settings-panel" aria-label={tr('global.title')}>
        <div className="pos-settings-panel-head">
          <h2 className="pos-settings-panel-title">{tr('global.title')}</h2>
          <span className="pos-settings-note">{tr('global.subtitle')}</span>
        </div>
        <MeasuredTable<PosSettingDefinition>
          metricId="system.pos-settings.global-table"
          rowKey={(definition) => definition.key}
          size="small"
          pagination={false}
          columns={globalColumns}
          dataSource={[...definitions]}
          scroll={{ x: 520 }}
          rowClassName={(definition) => (definition.isNew ? 'pos-settings-row-new' : '')}
        />
        {canManage ? (
          <div className="pos-settings-actions">
            <Typography.Text type="secondary">{tr('global.adminOnly')}</Typography.Text>
            <Space>
              <Button onClick={reset} disabled={changes.length === 0}>{tr('actions.reset')}</Button>
              <Button type="primary" onClick={() => setConfirmOpen(true)} disabled={changes.length === 0}>
                {tr('actions.save')}
              </Button>
            </Space>
          </div>
        ) : null}
      </section>

      <section className="pos-settings-panel" aria-label={tr('stores.tableLabel')}>
        <div className="pos-settings-panel-head">
          <h2 className="pos-settings-panel-title">{tr('stores.title')}</h2>
          <span className="pos-settings-note">{tr('stores.subtitle')}</span>
          <Input
            className="pos-settings-search"
            allowClear
            prefix={<SearchOutlined />}
            placeholder={tr('stores.searchPlaceholder')}
            value={keyword}
            onChange={(event) => setKeyword(event.target.value)}
          />
        </div>
        <MeasuredTable<PosSettingStoreSummary>
          metricId="system.pos-settings.stores-table"
          rowKey={(store) => store.storeCode}
          size="small"
          pagination={false}
          columns={storeColumns}
          dataSource={visibleStores}
          scroll={{ x: 640 }}
          locale={{
            emptyText: (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={stores.length === 0 ? tr('stores.empty') : tr('stores.noMatch')}
              />
            ),
          }}
        />
        <p className="pos-settings-footer">
          {tr('stores.footer', { total: stores.length, overridden: countOverriddenStores(stores) })}
        </p>
      </section>

      {/* 保存前一律先确认：列出每项「旧 → 新」。 */}
      <ConfirmChangesModal
        open={confirmOpen}
        title={tr('confirm.titleGlobal')}
        layer="global"
        changes={changes}
        tr={tr}
        onConfirm={save}
        onCancel={() => setConfirmOpen(false)}
      />
    </div>
  )
}
