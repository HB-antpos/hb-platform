import { LockOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Empty, Segmented, Table, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo, useRef } from 'react'
import { useTranslation } from 'react-i18next'

import type { MyobSyncPreview } from '../../../types/myob'

import { summarizeStoreLabels } from './myobEmployeeLogic'
import {
  SYNC_PREVIEW_FILTERS,
  buildPreviewRows,
  checkAllFill,
  countByFilter,
  isSensitiveField,
  listPendingSensitiveEmployees,
  syncFieldLabelKey,
  uncheckAll,
  type SyncCheckedMap,
  type SyncCheckedSummary,
  type SyncPreviewFilter,
  type SyncPreviewRow,
} from './myobSyncLogic'

interface MyobSyncReviewStepProps {
  preview: MyobSyncPreview
  checked: SyncCheckedMap
  summary: SyncCheckedSummary
  filter: SyncPreviewFilter
  onFilterChange: (filter: SyncPreviewFilter) => void
  onCheckedChange: (next: SyncCheckedMap) => void
}

/** 第 2 步：4 个统计卡 + 提示条 + 分段筛选 + 按员工合并行的差异表。头尾由抽屉固定，表格区自己滚动。 */
export default function MyobSyncReviewStep({
  preview,
  checked,
  summary,
  filter,
  onFilterChange,
  onCheckedChange,
}: MyobSyncReviewStepProps) {
  const { t } = useTranslation()
  const wrapRef = useRef<HTMLDivElement>(null)
  const counts = useMemo(() => countByFilter(preview.employees), [preview.employees])
  const rows = useMemo(() => buildPreviewRows(preview.employees, filter), [preview.employees, filter])
  const pending = useMemo(() => listPendingSensitiveEmployees(preview.employees), [preview.employees])
  const { summary: stat } = preview
  const empty = t('myob.sync.step2.empty')

  const columns: ColumnsType<SyncPreviewRow> = [
    {
      title: t('myob.sync.step2.columns.employee'),
      key: 'employee',
      width: 180,
      // 员工列合并行：只有该员工的第一行渲染内容，其余行 rowSpan=0
      onCell: (row) => ({ rowSpan: row.rowSpan }),
      render: (_, row) => (
        <>
          <span className="myob-person-name">{row.employee.userName}</span>
          <span className="myob-sub">{summarizeStoreLabels(row.employee.storeLabels)}</span>
          {row.employee.hasPendingSensitiveRequest && pending.includes(row.employee) ? (
            <Tag color="warning" style={{ marginTop: 4, marginInlineEnd: 0 }}>
              {t('myob.sync.step2.pendingTag')}
            </Tag>
          ) : null}
        </>
      ),
    },
    {
      title: t('myob.sync.step2.columns.field'),
      key: 'field',
      width: 140,
      render: (_, row) => {
        const name = t(syncFieldLabelKey(row.change.field), { defaultValue: row.change.field })
        const sensitive = row.change.sensitive || isSensitiveField(row.change.field)
        return (
          <Checkbox
            checked={!!checked[row.key]}
            aria-label={t('myob.sync.step2.checkRow', { name: row.employee.userName, field: name })}
            onChange={(event) => onCheckedChange({ ...checked, [row.key]: event.target.checked })}
          >
            {name}
            {sensitive ? <LockOutlined className="myob-sync-lock-icon" aria-label={t('myob.sync.step1.sensitive')} /> : null}
          </Checkbox>
        )
      },
    },
    {
      title: t('myob.sync.step2.columns.ours'),
      key: 'ours',
      width: 200,
      render: (_, row) =>
        row.change.ourDisplay ? <span className="myob-mono">{row.change.ourDisplay}</span> : <span className="myob-muted">{empty}</span>,
    },
    { title: '', key: 'arrow', width: 28, className: 'myob-arrow', render: () => <span aria-hidden="true">→</span> },
    {
      title: t('myob.sync.step2.columns.myob'),
      key: 'myob',
      render: (_, row) => {
        const value = row.change.myobDisplay ?? empty
        return (
          <>
            {/* 覆盖项用琥珀色强调；颜色之外右侧还有「覆盖」文字标签 */}
            <span className={`myob-mono${row.change.kind === 'Overwrite' ? ' myob-diff' : ''}`}>{value}</span>
            {row.change.note ? <span className="myob-sub">{row.change.note}</span> : null}
          </>
        )
      },
    },
    {
      title: t('myob.sync.step2.columns.mode'),
      key: 'mode',
      width: 72,
      render: (_, row) => (
        <Tag color={row.change.kind === 'Overwrite' ? 'warning' : 'success'} style={{ marginInlineEnd: 0 }}>
          {t(`myob.sync.step2.kind.${row.change.kind}`)}
        </Tag>
      ),
    },
  ]

  const cards: { key: string; label: string; value: number; unit: string; sub?: string; tone?: 'warn' }[] = [
    { key: 'employees', label: t('myob.sync.step2.cards.employees'), value: stat.employeeCount, unit: t('myob.sync.step2.cards.person') },
    {
      key: 'changes',
      label: t('myob.sync.step2.cards.changes'),
      value: stat.changeCount,
      unit: t('myob.sync.step2.cards.item'),
      sub: t('myob.sync.step2.cards.checked', { count: summary.changeCount }),
    },
    {
      key: 'skipped',
      label: t('myob.sync.step2.cards.skipped'),
      value: stat.skippedNoDiff + stat.skippedMissingCard,
      unit: t('myob.sync.step2.cards.person'),
      sub: t('myob.sync.step2.cards.skippedDetail', { noDiff: stat.skippedNoDiff, missing: stat.skippedMissingCard }),
    },
    {
      key: 'sensitive',
      label: t('myob.sync.step2.cards.sensitive'),
      value: stat.sensitiveCount,
      unit: t('myob.sync.step2.cards.item'),
      tone: stat.sensitiveCount > 0 ? 'warn' : undefined,
    },
  ]

  return (
    <div className="myob-sync-review">
      <div className="myob-sync-review-top">
        <div className="myob-sync-cards4">
          {cards.map((card) => (
            <div key={card.key} className={`myob-sync-stat${card.tone === 'warn' ? ' myob-sync-stat-warn' : ''}`}>
              <span className="myob-sub">{card.label}</span>
              <span>
                <span className="myob-sync-stat-value">{card.value}</span> {card.unit}
                {card.key === 'changes' ? <span className="myob-sub myob-sync-inline"> · {card.sub}</span> : null}
              </span>
              {card.key !== 'changes' && card.sub ? <span className="myob-sub">{card.sub}</span> : null}
            </div>
          ))}
        </div>

        {preview.unavailableFields.length > 0 ? (
          <Alert
            type="info"
            showIcon
            message={t('myob.sync.step2.unavailableBanner', {
              fields: preview.unavailableFields.map((field) => t(syncFieldLabelKey(field), { defaultValue: field })).join('、'),
            })}
          />
        ) : null}
        {stat.sensitiveCount > 0 ? (
          <Alert
            type="warning"
            showIcon
            message={
              <>
                {summary.sensitiveCount > 0
                  ? t('myob.sync.step2.sensitiveBanner', { count: summary.sensitiveCount })
                  : t('myob.sync.step2.sensitiveBannerNone', { total: stat.sensitiveCount })}
                {pending.length > 0 ? (
                  <>
                    <br />
                    {t('myob.sync.step2.pendingBanner', { names: pending.map((employee) => employee.userName).join('、') })}
                  </>
                ) : null}
              </>
            }
          />
        ) : null}

        <div className="myob-sync-toolbar">
          <Segmented
            value={filter}
            options={SYNC_PREVIEW_FILTERS.map((item) => ({
              value: item,
              label: `${t(`myob.sync.step2.filters.${item}`)} ${counts[item]}`,
            }))}
            onChange={(value) => onFilterChange(value as SyncPreviewFilter)}
          />
          <span className="myob-sync-links">
            <Button type="link" size="small" onClick={() => onCheckedChange(checkAllFill(preview.employees, checked))}>
              {t('myob.sync.step2.checkAllFill')}
            </Button>
            <Button type="link" size="small" onClick={() => onCheckedChange(uncheckAll(checked))}>
              {t('myob.sync.step2.uncheckAll')}
            </Button>
          </span>
        </div>
      </div>

      <div className="myob-sync-table-wrap" ref={wrapRef}>
        <Table<SyncPreviewRow>
          size="small"
          rowKey="key"
          columns={columns}
          dataSource={rows}
          pagination={false}
          // 表头固定在表格滚动区顶部（抽屉头尾本身由 Drawer 固定）
          sticky={{ getContainer: () => wrapRef.current ?? document.body }}
          locale={{
            emptyText: (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={preview.employees.length === 0 ? t('myob.sync.step2.emptyPreview') : t('myob.sync.step2.emptyFiltered')}
              />
            ),
          }}
        />
      </div>
    </div>
  )
}
