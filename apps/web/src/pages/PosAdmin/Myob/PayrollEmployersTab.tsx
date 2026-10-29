import { ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Grid, Input, Select, Switch, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getMyobPayrollEmployers } from '../../../services/myobService'
import type { MyobPayrollEmployerRow } from '../../../types/myob'
import PayrollEmployerDrawer from './PayrollEmployerDrawer'
import { describePayrollEmployer, formatStoreLabel, getOtherStoresOfUser } from './myobEmployeeLogic'
import { extractMyobError } from './myobLogic'

const SEARCH_DEBOUNCE_MS = 300
const PAGE_SIZE = 20

interface PayrollEmployersTabProps {
  /** 抽屉里「去绑定」：跳到员工绑定页签并预选该公司；没有绑定权限时不传（不显示按钮）。 */
  onGoBind?: (connectionGuid: string) => void
}

export default function PayrollEmployersTab({ onGoBind }: PayrollEmployersTabProps) {
  const { t } = useTranslation()
  // 窄屏（<768px）不固定操作列：固定列会占掉大半屏宽，其余列只能被挤成一条缝，改成整表横向滚动
  const wide = Grid.useBreakpoint().md !== false
  const [keyword, setKeyword] = useState('')
  const [debouncedKeyword, setDebouncedKeyword] = useState('')
  const [storeCode, setStoreCode] = useState<string | undefined>(undefined)
  const [crossOnly, setCrossOnly] = useState(false)
  const [rows, setRows] = useState<MyobPayrollEmployerRow[]>([])
  // 分店下拉选项：累积每次返回里出现过的分店，筛选后选项不会缩水
  const [storeOptions, setStoreOptions] = useState<Array<{ value: string; label: string }>>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const requestSeq = useRef(0)

  useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedKeyword(keyword), SEARCH_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [keyword])

  const load = useCallback(async () => {
    const seq = ++requestSeq.current
    setLoading(true)
    try {
      const data = await getMyobPayrollEmployers({ keyword: debouncedKeyword, storeCode, crossCompanyOnly: crossOnly })
      if (seq === requestSeq.current) {
        setRows(data.rows)
        setError(null)
        setStoreOptions((current) => {
          const map = new Map(current.map((item) => [item.value, item.label]))
          for (const row of data.rows) {
            map.set(row.storeCode, formatStoreLabel(row.storeCode, row.storeName))
          }
          return [...map.entries()].sort(([a], [b]) => a.localeCompare(b, undefined, { numeric: true })).map(([value, label]) => ({ value, label }))
        })
      }
    } catch (err) {
      if (seq === requestSeq.current) {
        setError(extractMyobError(err, t('myob.payroll.loadFailed')).message)
      }
    } finally {
      if (seq === requestSeq.current) {
        setLoading(false)
      }
    }
  }, [crossOnly, debouncedKeyword, storeCode, t])

  useEffect(() => {
    void load()
  }, [load])

  const columns: ColumnsType<MyobPayrollEmployerRow> = [
    {
      title: t('myob.payroll.columns.employee'),
      key: 'employee',
      width: 200,
      render: (_, row) => (
        <>
          <span className="myob-person-name">{row.userName}</span>
          {row.isCrossCompany ? (
            <span className="myob-person-tags">
              <Tag color="blue">{t('myob.payroll.cross')}</Tag>
            </span>
          ) : null}
        </>
      ),
    },
    {
      title: t('myob.payroll.columns.store'),
      key: 'store',
      width: 200,
      render: (_, row) => {
        const others = row.isCrossCompany ? getOtherStoresOfUser(rows, row) : []
        return (
          <>
            {formatStoreLabel(row.storeCode, row.storeName)}
            {others.length > 0 ? (
              <span className="myob-sub">
                {t('myob.payroll.otherStores', {
                  stores: others.map((item) => (item.companyName ? `${item.label}（${item.companyName}）` : item.label)).join(' / '),
                })}
              </span>
            ) : null}
          </>
        )
      },
    },
    {
      title: t('myob.payroll.columns.defaultCompany'),
      key: 'default',
      width: 200,
      render: (_, row) => row.defaultConnectionName || <span className="myob-muted">{t('myob.payroll.unset')}</span>,
    },
    {
      title: t('myob.payroll.columns.effectiveCompany'),
      key: 'effective',
      width: 220,
      render: (_, row) => {
        const cell = describePayrollEmployer(row)
        return (
          <>
            {cell.name ? (
              <Tag color={cell.kind === 'override' ? 'warning' : 'success'}>{cell.name}</Tag>
            ) : (
              <span className="myob-muted">{t('myob.payroll.unset')}</span>
            )}
            <span className="myob-muted">{cell.kind === 'override' ? t('myob.payroll.override') : t('myob.payroll.byStore')}</span>
          </>
        )
      },
    },
    {
      title: t('myob.payroll.columns.effectiveFrom'),
      key: 'effectiveFrom',
      width: 120,
      render: (_, row) => (row.effectiveFrom ? <span className="myob-mono">{row.effectiveFrom}</span> : <span className="myob-muted">—</span>),
    },
    {
      title: t('myob.payroll.columns.actions'),
      key: 'actions',
      fixed: wide ? 'right' : undefined,
      width: 100,
      render: (_, row) => (
        <Button
          size="small"
          type={row.isOverride ? 'primary' : 'link'}
          aria-label={t('myob.payroll.editAria', { name: row.userName, store: formatStoreLabel(row.storeCode, row.storeName) })}
          onClick={() => setEditing(row.userGuid)}
        >
          {row.isOverride ? t('myob.payroll.edit') : t('myob.payroll.set')}
        </Button>
      ),
    },
  ]

  return (
    <div className="myob-tabpane">
      <section className="myob-card" aria-labelledby="myob-payroll-title">
        <div className="myob-card-head">
          <div>
            <h2 className="myob-card-title" id="myob-payroll-title">
              {t('myob.payroll.title')}
            </h2>
            <div className="myob-card-hint">{t('myob.payroll.hint')}</div>
          </div>
        </div>

        <div className="myob-payroll-filters">
          <Input
            allowClear
            className="myob-search"
            prefix={<SearchOutlined />}
            value={keyword}
            placeholder={t('myob.payroll.search')}
            aria-label={t('myob.payroll.search')}
            onChange={(event) => setKeyword(event.target.value)}
          />
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            className="myob-store-select"
            value={storeCode}
            options={storeOptions}
            placeholder={t('myob.payroll.allStores')}
            aria-label={t('myob.payroll.allStores')}
            onChange={(value) => setStoreCode(value ?? undefined)}
          />
          <label className="myob-switch-label">
            <Switch checked={crossOnly} onChange={setCrossOnly} />
            {t('myob.payroll.crossOnly')}
          </label>
          <Button icon={<ReloadOutlined />} loading={loading} onClick={() => void load()}>
            {t('myob.payroll.reload')}
          </Button>
        </div>

        {error ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 12 }}
            message={t('myob.payroll.loadFailed')}
            description={error}
            action={
              <Button size="small" onClick={() => void load()}>
                {t('myob.payroll.retry')}
              </Button>
            }
          />
        ) : null}

        <MeasuredTable<MyobPayrollEmployerRow>
          metricId="pos-admin.myob.payroll-employers"
          rowKey={(row) => `${row.userGuid}:${row.storeCode}`}
          size="middle"
          columns={columns}
          dataSource={rows}
          loading={loading}
          scroll={{ x: 'max-content' }}
          pagination={{
            pageSize: PAGE_SIZE,
            showSizeChanger: false,
            hideOnSinglePage: true,
            showTotal: (total) => t('myob.payroll.total', { total }),
          }}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.payroll.empty')} /> }}
        />
      </section>

      <PayrollEmployerDrawer
        userGuid={editing}
        onClose={() => setEditing(null)}
        onSaved={() => {
          setEditing(null)
          void load()
        }}
        onGoBind={
          onGoBind
            ? (connectionGuid) => {
                setEditing(null)
                onGoBind(connectionGuid)
              }
            : undefined
        }
      />
    </div>
  )
}
