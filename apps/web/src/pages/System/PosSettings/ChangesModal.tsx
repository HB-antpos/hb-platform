import { Empty, Modal } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { listPosSettingChanges } from '../../../services/posSettingsService'
import type { PosSettingChange } from '../../../types/posSettings'
import { describeChangeScope, EMPTY_CELL, formatDateTime } from './logic'
import { LoadErrorAlert, settingName, type Tr } from './parts'
import { useRequest } from './useRequest'

interface ChangesModalProps {
  open: boolean
  /** 每次打开递增：保证每次打开都重新取最新记录。 */
  seq: number
  /** 门店代码 → 门店名，用来把记录里的门店代码显示成名称。 */
  storeNames: Readonly<Record<string, string>>
  tr: Tr
  errorText: (error: unknown) => string | null
  onClose: () => void
}

function changeValueText(value: boolean | null, nullKey: 'unset' | 'cleared', tr: Tr): string {
  if (value === true) return tr('value.on')
  if (value === false) return tr('value.off')
  return nullKey === 'unset' ? tr('history.unset') : tr('history.cleared')
}

/** 页头「修改记录」：最近 50 条修改（范围、设置项、旧 → 新、操作人）。每次打开都重新取。 */
export default function ChangesModal({ open, seq, storeNames, tr, errorText, onClose }: ChangesModalProps) {
  const request = useRequest<PosSettingChange[]>(
    open ? `changes:${seq}` : null,
    (signal) => listPosSettingChanges({ limit: 50 }, signal),
    open,
  )
  const error = request.error ? errorText(request.error) : null

  const columns: ColumnsType<PosSettingChange> = [
    { title: tr('history.colTime'), key: 'time', width: 150, render: (_, change) => formatDateTime(change.changedAtUtc) },
    {
      title: tr('history.colScope'),
      key: 'scope',
      width: 190,
      render: (_, change) => {
        const scope = describeChangeScope(change, storeNames)
        if (scope.scope === 'global') return tr('history.scopeGlobal')
        if (scope.scope === 'store') return tr('history.scopeStore', { name: scope.label })
        if (scope.scope === 'device') return tr('history.scopeDevice', { code: scope.label })
        return scope.label
      },
    },
    { title: tr('history.colSetting'), key: 'setting', render: (_, change) => settingName(change.settingKey, tr) },
    {
      title: tr('history.colChange'),
      key: 'change',
      width: 170,
      render: (_, change) => (
        <span className="pos-settings-confirm-change">
          <span className="pos-settings-confirm-from">{changeValueText(change.oldValue, 'unset', tr)}</span>
          <span aria-hidden="true">{tr('confirm.arrow')}</span>
          <strong>{changeValueText(change.newValue, 'cleared', tr)}</strong>
        </span>
      ),
    },
    { title: tr('history.colBy'), key: 'by', width: 120, render: (_, change) => change.changedByName || EMPTY_CELL },
  ]

  return (
    <Modal
      open={open}
      title={tr('history.title')}
      footer={null}
      width={900}
      onCancel={onClose}
      destroyOnClose
    >
      <p className="pos-settings-note">{tr('history.hint')}</p>
      {error ? <LoadErrorAlert message={error} tr={tr} onRetry={request.reload} /> : null}
      <MeasuredTable<PosSettingChange>
        metricId="system.pos-settings.changes-table"
        rowKey={(change) => String(change.id)}
        size="small"
        pagination={false}
        loading={request.loading}
        columns={columns}
        dataSource={request.data ?? []}
        scroll={{ x: 760, y: 420 }}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('history.empty')} /> }}
      />
    </Modal>
  )
}
