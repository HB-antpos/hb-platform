import { CheckCircleOutlined, SearchOutlined } from '@ant-design/icons'
import { Button, Empty, Grid, Input, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import type { MyobBindingCounts, MyobBindingRow } from '../../../types/myob'
import { BasisBadges, EmployeeTypeTag, ScoreBadge } from './MyobBadges'
import {
  BINDING_FILTERS,
  BINDING_FILTER_LABEL_KEYS,
  BINDING_STATUS_BADGES,
  buildBatchConfirmItems,
  filterBindingRows,
  formatBirthday,
  formatShortDateTime,
  getBindingRowActions,
  getFilterCount,
  joinSubtitle,
  type BindingFilter,
  type BindingRowAction,
  summarizeStoreLabels,
} from './myobEmployeeLogic'

const PAGE_SIZE = 20

interface BindingListViewProps {
  connectionName: string
  counts: MyobBindingCounts
  rows: MyobBindingRow[]
  filter: BindingFilter
  keyword: string
  /** 正在处理的员工 guid（行内按钮 loading / 禁用）。 */
  busyUserGuids: ReadonlySet<string>
  /** 批量确认进行中。 */
  batchBusy: boolean
  onFilterChange: (filter: BindingFilter) => void
  onKeywordChange: (keyword: string) => void
  onRowAction: (action: BindingRowAction, row: MyobBindingRow) => void
  onBatchConfirm: () => void
}

export default function BindingListView({
  connectionName,
  counts,
  rows,
  filter,
  keyword,
  busyUserGuids,
  batchBusy,
  onFilterChange,
  onKeywordChange,
  onRowAction,
  onBatchConfirm,
}: BindingListViewProps) {
  const { t } = useTranslation()
  // 窄屏（<768px）不固定操作列：固定列会占掉大半屏宽，其余列只能被挤成一条缝，改成整表横向滚动
  const wide = Grid.useBreakpoint().md !== false
  const filtered = useMemo(() => filterBindingRows(rows, filter, keyword), [rows, filter, keyword])
  // 批量条数 = 实际会提交的高分建议条数（取全部行，不受当前筛选 / 搜索影响）
  const batchCount = useMemo(() => buildBatchConfirmItems(rows).length, [rows])

  const columns: ColumnsType<MyobBindingRow> = [
    {
      title: t('myob.employees.columns.ours'),
      key: 'ours',
      width: 270,
      render: (_, row) => (
        <>
          <span className="myob-person-name">{row.userName}</span>
          <span className="myob-person-tags">
            <EmployeeTypeTag type={row.employeeType} />
            {row.isCrossCompany ? <Tag color="blue">{t('myob.employees.cross')}</Tag> : null}
          </span>
          <span className="myob-sub">{joinSubtitle([row.email, summarizeStoreLabels(row.storeLabels)])}</span>
        </>
      ),
    },
    {
      title: '',
      key: 'arrow',
      width: 32,
      className: 'myob-arrow',
      render: () => <span aria-hidden="true">→</span>,
    },
    {
      title: t('myob.employees.columns.myobCard', { name: connectionName }),
      key: 'card',
      width: 280,
      render: (_, row) => renderCardCell(row, t),
    },
    {
      title: t('myob.employees.columns.basis'),
      key: 'basis',
      width: 240,
      render: (_, row) =>
        row.candidate ? (
          <>
            <BasisBadges basis={row.candidate.basis} />
            {row.candidate.notes.length > 0 ? <span className="myob-notes">{row.candidate.notes.join('；')}</span> : null}
          </>
        ) : null,
    },
    {
      title: t('myob.employees.columns.score'),
      key: 'score',
      width: 70,
      render: (_, row) => (row.candidate && row.status !== 'Bound' ? <ScoreBadge score={row.candidate.score} /> : null),
    },
    {
      title: t('myob.employees.columns.status'),
      key: 'status',
      width: 210,
      render: (_, row) => {
        const badge = BINDING_STATUS_BADGES[row.status]
        return (
          <>
            <Tag color={badge.color} style={{ marginInlineEnd: 0 }}>
              {t(badge.labelKey)}
            </Tag>
            {row.status === 'Bound' && row.link ? (
              <>
                <span className="myob-status-note">
                  {t('myob.employees.status.boundBy', {
                    who: row.link.boundByName || '--',
                    time: formatShortDateTime(row.link.boundAt),
                  })}
                </span>
                {row.link.cardMissing ? <span className="myob-status-alert">{t('myob.employees.status.cardMissing')}</span> : null}
              </>
            ) : null}
            {row.status === 'Conflict' && row.candidate?.boundToUserName ? (
              <span className="myob-status-note">
                {t('myob.employees.status.conflictBoundTo', { name: row.candidate.boundToUserName })}
              </span>
            ) : null}
          </>
        )
      },
    },
    {
      title: t('myob.employees.columns.actions'),
      key: 'actions',
      fixed: wide ? 'right' : undefined,
      width: 200,
      render: (_, row) => {
        const busy = busyUserGuids.has(row.userGuid)
        return (
          <div className="myob-actions" style={{ flexWrap: 'nowrap', alignItems: 'center' }}>
            {getBindingRowActions(row.status).map((action, index) => (
              <RowActionButton
                key={action}
                action={action}
                primary={index === 0}
                busy={busy}
                disabled={batchBusy}
                onClick={() => onRowAction(action, row)}
              />
            ))}
          </div>
        )
      },
    },
  ]

  return (
    <div>
      <div className="myob-chips" role="group" aria-label={t('myob.employees.subtabs.list')}>
        {BINDING_FILTERS.map((item) => (
          <button
            key={item}
            type="button"
            aria-pressed={filter === item}
            className={`myob-chip${filter === item ? ' myob-chip-on' : ''}`}
            onClick={() => onFilterChange(item)}
          >
            {t(BINDING_FILTER_LABEL_KEYS[item])}
            <span className="myob-chip-count">{getFilterCount(counts, item)}</span>
          </button>
        ))}
        <Input
          allowClear
          className="myob-chips-search"
          prefix={<SearchOutlined />}
          value={keyword}
          placeholder={t('myob.employees.search')}
          aria-label={t('myob.employees.search')}
          onChange={(event) => onKeywordChange(event.target.value)}
        />
      </div>

      {batchCount > 0 ? (
        <div className="myob-batch" role="region" aria-label={t('myob.employees.batch.title', { count: batchCount })}>
          <CheckCircleOutlined className="myob-batch-icon" aria-hidden="true" />
          <div className="myob-batch-text">
            <span className="myob-batch-title">{t('myob.employees.batch.title', { count: batchCount })}</span>
            <span className="myob-batch-hint">{t('myob.employees.batch.hint')}</span>
          </div>
          <div className="myob-batch-actions">
            <Button onClick={() => onFilterChange('suggested')}>{t('myob.employees.batch.view', { count: batchCount })}</Button>
            <Button type="primary" loading={batchBusy} onClick={onBatchConfirm}>
              {t('myob.employees.batch.confirm', { count: batchCount })}
            </Button>
          </div>
        </div>
      ) : null}

      <MeasuredTable<MyobBindingRow>
        metricId="pos-admin.myob.employee-bindings"
        rowKey="userGuid"
        size="middle"
        columns={columns}
        dataSource={filtered}
        // 窄屏横向滚动，操作列固定在右侧
        scroll={{ x: 'max-content' }}
        rowClassName={(row) =>
          row.status === 'Suggested' ? 'myob-row-suggested' : row.status === 'NeedsReview' ? 'myob-row-review' : ''
        }
        pagination={{
          pageSize: PAGE_SIZE,
          showSizeChanger: false,
          // 「共 N 人 · 本页 M 条」放在分页条左侧；筛选条件变化时 antd 会自动回到第一页
          showTotal: (total, range) => t('myob.employees.total', { total, page: range[1] - range[0] + 1 }),
        }}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.employees.empty')} /> }}
      />
    </div>
  )
}

function renderCardCell(row: MyobBindingRow, t: ReturnType<typeof useTranslation>['t']) {
  // 已绑定：显示绑定时记下的 MYOB 姓名
  if (row.status === 'Bound' && row.link) {
    return <span className="myob-person-name">{row.link.myobName || row.link.myobEmployeeUid}</span>
  }
  const candidate = row.candidate
  if (!candidate) {
    return <span className="myob-muted">{t('myob.employees.noCandidate')}</span>
  }
  const card = candidate.card
  const birthday = formatBirthday(card.dateOfBirth) ?? '—'
  return (
    <>
      <span className={candidate.basis.name === 'mismatch' ? 'myob-diff' : undefined}>{card.name}</span>
      <span className="myob-sub">
        <span className={candidate.basis.email === 'mismatch' ? 'myob-diff' : undefined}>{card.email ?? '—'}</span>
        {' · '}
        {t('myob.employees.birthdayLine', { birthday })}
      </span>
    </>
  )
}

interface RowActionButtonProps {
  action: BindingRowAction
  primary: boolean
  busy: boolean
  disabled: boolean
  onClick: () => void
}

const ACTION_LABEL_KEYS: Record<BindingRowAction, string> = {
  confirm: 'myob.employees.actions.confirm',
  review: 'myob.employees.actions.review',
  change: 'myob.employees.actions.change',
  searchOther: 'myob.employees.actions.searchOther',
  searchMyob: 'myob.employees.actions.searchMyob',
  unbind: 'myob.employees.actions.unbind',
}

function RowActionButton({ action, primary, busy, disabled, onClick }: RowActionButtonProps) {
  const { t } = useTranslation()
  const label = t(ACTION_LABEL_KEYS[action])
  // 确认绑定 = 主按钮；「换一个」「解除绑定」是链接；其余为默认按钮
  if (action === 'change') {
    return (
      <Button type="link" size="small" disabled={busy || disabled} onClick={onClick}>
        {label}
      </Button>
    )
  }
  if (action === 'unbind') {
    return (
      <Button type="link" size="small" className="myob-link-muted" loading={busy} disabled={disabled} onClick={onClick}>
        {label}
      </Button>
    )
  }
  return (
    <Button size="small" type={action === 'confirm' && primary ? 'primary' : 'default'} loading={busy} disabled={disabled} onClick={onClick}>
      {label}
    </Button>
  )
}
