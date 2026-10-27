import { CloseOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Select, Skeleton, message } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { getMyobPayrollEmployerDetail, saveMyobPayrollEmployers } from '../../../services/myobService'
import type { MyobPayrollEmployerDetail } from '../../../types/myob'
import {
  buildPayrollDraft,
  buildPayrollSaveItems,
  findUnboundPayrollCompanies,
  formatStoreLabel,
  hasPayrollDraftChanges,
  normalizeAllowedEffectiveFrom,
  resolveEffectiveFrom,
  type PayrollDraft,
} from './myobEmployeeLogic'
import { extractMyobError, formatMyobTime } from './myobLogic'

/** 下拉里「按分店」选项的取值（Select 不接受 null 作为 value）。 */
const BY_STORE = '__by_store__'

interface PayrollEmployerDrawerProps {
  /** 要编辑的员工；null 表示关闭。 */
  userGuid: string | null
  onClose: () => void
  /** 保存成功（父组件刷新列表并关闭）。 */
  onSaved: () => void
  /** 去绑定：没有绑定权限时为 undefined（不显示按钮）。 */
  onGoBind?: (connectionGuid: string) => void
}

export default function PayrollEmployerDrawer({ userGuid, onClose, onSaved, onGoBind }: PayrollEmployerDrawerProps) {
  const { t } = useTranslation()
  const [detail, setDetail] = useState<MyobPayrollEmployerDetail | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [draft, setDraft] = useState<PayrollDraft>({})
  const [effectiveFrom, setEffectiveFrom] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const requestSeq = useRef(0)

  const load = useCallback(async () => {
    if (!userGuid) {
      return
    }
    const seq = ++requestSeq.current
    setLoading(true)
    try {
      const data = await getMyobPayrollEmployerDetail(userGuid)
      if (seq === requestSeq.current) {
        setDetail(data)
        setDraft(buildPayrollDraft(data))
        // effectiveFrom 默认当前期（可选列表第一项）
        setEffectiveFrom(resolveEffectiveFrom(null, data.allowedEffectiveFrom))
        setError(null)
      }
    } catch (err) {
      if (seq === requestSeq.current) {
        setError(extractMyobError(err, t('myob.payrollDrawer.loadFailed')).message)
      }
    } finally {
      if (seq === requestSeq.current) {
        setLoading(false)
      }
    }
  }, [t, userGuid])

  useEffect(() => {
    // 打开 / 换人：清空旧详情再加载，避免短暂显示上一个人的数据
    setDetail(null)
    setError(null)
    if (userGuid) {
      void load()
    } else {
      requestSeq.current += 1
    }
  }, [userGuid, load])

  const allowed = useMemo(() => normalizeAllowedEffectiveFrom(detail?.allowedEffectiveFrom), [detail])
  const companyName = useCallback(
    (guid: string | null) => detail?.companies.find((item) => item.connectionGuid === guid)?.name ?? '',
    [detail],
  )
  const unboundGuids = useMemo(
    () => (detail ? findUnboundPayrollCompanies(draft, detail.bindingStates) : []),
    [detail, draft],
  )
  const changed = detail ? hasPayrollDraftChanges(detail, draft) : false
  const canSave = Boolean(detail) && changed && Boolean(effectiveFrom) && !saving

  const handleSave = async () => {
    if (!detail || !userGuid || !effectiveFrom) {
      return
    }
    setSaving(true)
    try {
      await saveMyobPayrollEmployers(userGuid, { effectiveFrom, items: buildPayrollSaveItems(detail, draft) })
      message.success(t('myob.payrollDrawer.saved', { name: detail.userName }))
      onSaved()
    } catch (err) {
      message.error(extractMyobError(err, t('myob.payrollDrawer.saveFailed')).message)
    } finally {
      setSaving(false)
    }
  }

  return (
    <Drawer
      open={userGuid !== null}
      width={660}
      rootClassName="myob-drawer"
      destroyOnClose
      onClose={onClose}
      // 关闭按钮放在标题栏右侧（antd 默认在左侧）
      closable={false}
      extra={<Button type="text" icon={<CloseOutlined />} aria-label={t('myob.payrollDrawer.close')} onClick={onClose} />}
      title={
        <div>
          <div>{t('myob.payrollDrawer.title', { name: detail?.userName ?? '' })}</div>
          <span className="myob-sub" style={{ fontWeight: 400 }}>
            {t('myob.payrollDrawer.subtitle')}
          </span>
        </div>
      }
      footer={
        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
          <Button onClick={onClose}>{t('myob.payrollDrawer.cancel')}</Button>
          <Button type="primary" disabled={!canSave} loading={saving} onClick={() => void handleSave()}>
            {t('myob.payrollDrawer.save')}
          </Button>
        </div>
      }
    >
      {error ? (
        <Alert
          type="error"
          showIcon
          message={t('myob.payrollDrawer.loadFailed')}
          description={error}
          action={
            <Button size="small" onClick={() => void load()}>
              {t('myob.payrollDrawer.retry')}
            </Button>
          }
        />
      ) : null}

      {loading && !detail ? <Skeleton active paragraph={{ rows: 8 }} /> : null}

      {detail ? (
        <div className="myob-drawer-body">
          {/* 每个上班分店一行：默认公司 + 改由其他公司发 */}
          <table className="myob-compare">
            <thead>
              <tr>
                <th scope="col">{t('myob.payrollDrawer.columns.store')}</th>
                <th scope="col">{t('myob.payrollDrawer.columns.defaultCompany')}</th>
                <th scope="col" style={{ width: 240 }}>
                  {t('myob.payrollDrawer.columns.override')}
                </th>
              </tr>
            </thead>
            <tbody>
              {detail.rows.map((row) => {
                const storeLabel = formatStoreLabel(row.storeCode, row.storeName)
                const defaultName = row.defaultConnectionName ?? ''
                const current = draft[row.storeCode] ?? null
                return (
                  <tr key={row.storeCode}>
                    <th scope="row" style={{ color: 'inherit', fontWeight: 600 }}>
                      {storeLabel}
                    </th>
                    <td>{defaultName || <span className="myob-muted">{t('myob.payrollDrawer.noDefault')}</span>}</td>
                    <td>
                      <Select
                        style={{ width: '100%' }}
                        aria-label={`${storeLabel} ${t('myob.payrollDrawer.columns.override')}`}
                        value={current ?? BY_STORE}
                        options={[
                          {
                            value: BY_STORE,
                            label: defaultName
                              ? t('myob.payrollDrawer.byStoreOption', { company: defaultName })
                              : t('myob.payrollDrawer.byStoreNoCompany'),
                          },
                          // 已是默认公司的不再列为「改由其他公司」
                          ...detail.companies
                            .filter((company) => company.connectionGuid !== row.defaultConnectionGuid)
                            .map((company) => ({ value: company.connectionGuid, label: company.name })),
                        ]}
                        onChange={(value) =>
                          setDraft((currentDraft) => ({ ...currentDraft, [row.storeCode]: value === BY_STORE ? null : value }))
                        }
                      />
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>

          {/* 生效日期：只能选后端给出的工资周期起始日 */}
          <div className="myob-field-row">
            <span className="myob-field-label" id="myob-effective-from-label">
              {t('myob.payrollDrawer.effectiveFrom')}
            </span>
            <Select
              style={{ minWidth: 240 }}
              aria-labelledby="myob-effective-from-label"
              value={effectiveFrom ?? undefined}
              disabled={allowed.length === 0}
              options={allowed.map((date, index) => ({
                value: date,
                label: index === 0 ? t('myob.payrollDrawer.currentPeriod', { date }) : t('myob.payrollDrawer.periodStart', { date }),
              }))}
              onChange={setEffectiveFrom}
            />
            <span className="myob-sub" style={{ flex: '1 1 220px' }}>
              {t('myob.payrollDrawer.effectiveHint')}
            </span>
          </div>
          {allowed.length === 0 ? <Alert type="warning" showIcon message={t('myob.payrollDrawer.noAllowedDates')} /> : null}

          {/* 新发薪公司里还没绑定这个员工：提示并提供「去绑定」 */}
          {unboundGuids.map((guid) => (
            <Alert
              key={guid}
              type="warning"
              showIcon
              message={
                <span>
                  <b>{t('myob.payrollDrawer.unbound.title', { company: companyName(guid), name: detail.userName })}</b>{' '}
                  {t('myob.payrollDrawer.unbound.body')}
                </span>
              }
              action={
                onGoBind ? (
                  <Button size="small" onClick={() => onGoBind(guid)}>
                    {t('myob.payrollDrawer.unbound.go')} →
                  </Button>
                ) : undefined
              }
            />
          ))}

          <div>
            <h3 className="myob-section-title">{t('myob.payrollDrawer.history')}</h3>
            {detail.history.length === 0 ? (
              <span className="myob-muted">{t('myob.payrollDrawer.historyEmpty')}</span>
            ) : (
              <ol className="myob-timeline">
                {detail.history.map((item, index) => {
                  const store = formatStoreLabel(item.storeCode, item.storeName)
                  return (
                    <li key={`${item.storeCode}:${item.effectiveFrom}:${item.createdAt ?? index}`} className="myob-timeline-item">
                      <b>
                        {item.connectionGuid
                          ? t('myob.payrollDrawer.historyOverride', {
                              date: item.effectiveFrom,
                              store,
                              company: item.connectionName || companyName(item.connectionGuid),
                            })
                          : t('myob.payrollDrawer.historyDefault', { date: item.effectiveFrom, store })}
                      </b>
                      <span className="myob-sub">
                        {t('myob.payrollDrawer.historyBy', {
                          who: item.createdByName || '--',
                          time: item.createdAt ? formatMyobTime(item.createdAt) : '--',
                        })}
                      </span>
                    </li>
                  )
                })}
              </ol>
            )}
          </div>
        </div>
      ) : null}
    </Drawer>
  )
}
