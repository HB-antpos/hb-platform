import { CloseOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Empty, Input, Skeleton, Tag } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { TFunction } from 'i18next'
import { useTranslation } from 'react-i18next'

import { getMyobBindingCandidates } from '../../../services/myobService'
import type { MyobBindingCandidates, MyobBindingRow, MyobConfirmBindingsResult, MyobMatchCandidate } from '../../../types/myob'
import { Avatar, EmployeeTypeTag, ScoreBadge } from './MyobBadges'
import {
  buildCompareRows,
  buildManualConfirmItem,
  describeEntitlementKey,
  formatBirthday,
  getEmployeeTypeKey,
  getEvidenceWarning,
  isCandidateSelectable,
  joinSubtitle,
} from './myobEmployeeLogic'
import { extractMyobError } from './myobLogic'

interface BindingDrawerProps {
  open: boolean
  connectionGuid: string
  connectionName: string
  /** 要绑定的我方员工；关闭时可为 null。 */
  row: MyobBindingRow | null
  /** 打开时预选的候选卡（「核对并绑定」= 最佳候选）。 */
  preselectUid: string | null
  onClose: () => void
  /** 提交确认；返回服务端结果，全部成功时抽屉关闭，有失败则留在抽屉里并重新拉候选。 */
  onConfirm: (userGuid: string, items: ReturnType<typeof buildManualConfirmItem>[]) => Promise<MyobConfirmBindingsResult | null>
}

const SEARCH_DEBOUNCE_MS = 300

export default function BindingDrawer({
  open,
  connectionGuid,
  connectionName,
  row,
  preselectUid,
  onClose,
  onConfirm,
}: BindingDrawerProps) {
  const { t } = useTranslation()
  const [keyword, setKeyword] = useState('')
  // 防抖后的关键字：输入停顿 300ms 才真正请求
  const [debouncedKeyword, setDebouncedKeyword] = useState('')
  const [data, setData] = useState<MyobBindingCandidates | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selectedUid, setSelectedUid] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  // 只认最后一次请求，防止慢响应覆盖新结果
  const requestSeq = useRef(0)
  const userGuid = row?.userGuid ?? ''

  // 打开 / 换人时重置输入与选择；预选只在打开时生效一次
  useEffect(() => {
    if (!open) {
      return
    }
    setKeyword('')
    setDebouncedKeyword('')
    setData(null)
    setError(null)
    setSelectedUid(preselectUid)
  }, [open, userGuid, preselectUid])

  useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedKeyword(keyword), SEARCH_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [keyword])

  const load = useCallback(
    async (kw: string) => {
      if (!userGuid) {
        return
      }
      const seq = ++requestSeq.current
      setLoading(true)
      try {
        const result = await getMyobBindingCandidates(connectionGuid, userGuid, kw)
        if (seq === requestSeq.current) {
          setData(result)
          setError(null)
        }
      } catch (err) {
        if (seq === requestSeq.current) {
          setError(extractMyobError(err, t('myob.drawer.loadFailed')).message)
        }
      } finally {
        if (seq === requestSeq.current) {
          setLoading(false)
        }
      }
    },
    [connectionGuid, t, userGuid],
  )

  useEffect(() => {
    if (open && userGuid) {
      void load(debouncedKeyword)
    }
  }, [open, userGuid, debouncedKeyword, load])

  // 候选列表：打开时预选的最佳候选若没出现在服务端返回里（关键字为空时极少见），补在最前面，保证预选可见
  const candidates = useMemo<MyobMatchCandidate[]>(() => {
    const list = data?.candidates ?? []
    const best = row?.candidate
    if (!debouncedKeyword.trim() && preselectUid && best && best.card.uid === preselectUid && !list.some((item) => item.card.uid === preselectUid)) {
      return [best, ...list]
    }
    return list
  }, [data, debouncedKeyword, preselectUid, row])

  const selected = candidates.find((item) => item.card.uid === selectedUid) ?? null
  // 选中的卡被别人绑定（刷新后状态变化）时视为未选
  const confirmable = selected !== null && isCandidateSelectable(selected)

  const handleConfirm = async () => {
    if (!selected || !confirmable || !row) {
      return
    }
    setSubmitting(true)
    try {
      const result = await onConfirm(row.userGuid, [buildManualConfirmItem(row.userGuid, selected.card)])
      // 有失败（例如这张卡刚被别人绑定）：留在抽屉里并刷新候选，让用户改选
      if (result && result.failed.length > 0) {
        setSelectedUid(null)
        void load(debouncedKeyword)
      }
    } finally {
      setSubmitting(false)
    }
  }

  const summary = data ?? null
  const ourName = summary?.userName || row?.userName || ''
  const ourEmail = summary ? summary.email : (row?.email ?? null)
  const ourType = summary ? summary.employeeType : (row?.employeeType ?? null)
  const ourStores = summary ? summary.storeLabels : (row?.storeLabels ?? [])
  const ourPhone = summary?.phoneMasked ?? null
  const ourBirthday = formatBirthday(summary?.birthday)
  const notFilled = t('myob.drawer.notFilled')

  return (
    <Drawer
      open={open}
      width={640}
      rootClassName="myob-drawer"
      destroyOnClose
      onClose={onClose}
      // 关闭按钮放在标题栏右侧（antd 默认在左侧）
      closable={false}
      extra={<Button type="text" icon={<CloseOutlined />} aria-label={t('myob.drawer.close')} onClick={onClose} />}
      title={
        <div>
          <div>{t('myob.drawer.title', { name: ourName })}</div>
          <span className="myob-sub" style={{ fontWeight: 400 }}>
            {t('myob.drawer.subtitle')}
          </span>
        </div>
      }
      footer={
        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
          <Button onClick={onClose}>{t('myob.drawer.cancel')}</Button>
          <Button type="primary" disabled={!confirmable} loading={submitting} onClick={() => void handleConfirm()}>
            {selected ? t('myob.drawer.confirm', { name: selected.card.name }) : t('myob.employees.actions.confirm')}
          </Button>
        </div>
      }
    >
      <div className="myob-drawer-body">
        {/* 我方员工摘要 */}
        <div className="myob-summary">
          <div className="myob-summary-row">
            <Avatar name={ourName} />
            <div style={{ minWidth: 0 }}>
              <span className="myob-person-name">{ourName}</span>
              <span className="myob-person-tags">
                <EmployeeTypeTag type={ourType} />
                {ourStores.slice(0, 6).map((store) => (
                  <Tag key={store}>{store}</Tag>
                ))}
                {ourStores.length > 6 ? <Tag>+{ourStores.length - 6}</Tag> : null}
              </span>
              <span className="myob-sub">
                {joinSubtitle([
                  ourEmail,
                  summary ? t('myob.drawer.phone', { value: ourPhone ?? notFilled }) : null,
                  summary ? t('myob.drawer.birthday', { value: ourBirthday ?? notFilled }) : null,
                ])}
              </span>
            </div>
          </div>
        </div>

        <Input.Search
          allowClear
          value={keyword}
          placeholder={t('myob.drawer.search', { company: connectionName })}
          aria-label={t('myob.drawer.search', { company: connectionName })}
          onChange={(event) => setKeyword(event.target.value)}
          onSearch={(value) => setDebouncedKeyword(value)}
        />

        {error ? (
          <Alert
            type="error"
            showIcon
            message={t('myob.drawer.loadFailed')}
            description={error}
            action={
              <Button size="small" onClick={() => void load(debouncedKeyword)}>
                {t('myob.drawer.retry')}
              </Button>
            }
          />
        ) : null}

        {/* 候选单选列表：已被别人绑定的显示「不可选」并禁用 */}
        {loading && !data ? (
          <Skeleton active paragraph={{ rows: 3 }} />
        ) : candidates.length === 0 && !error ? (
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.drawer.empty')} />
        ) : (
          <ul className="myob-candidates" role="radiogroup" aria-label={t('myob.drawer.candidatesTitle')} aria-busy={loading}>
            {candidates.map((candidate) => {
              const selectable = isCandidateSelectable(candidate)
              const on = candidate.card.uid === selectedUid
              return (
                <li key={candidate.card.uid}>
                  <button
                    type="button"
                    role="radio"
                    aria-checked={on}
                    disabled={!selectable}
                    className={`myob-candidate${on ? ' myob-candidate-on' : ''}`}
                    onClick={() => setSelectedUid(candidate.card.uid)}
                  >
                    <span className="myob-radio" aria-hidden="true" />
                    <Avatar name={candidate.card.name} />
                    <span style={{ flex: 1, minWidth: 0 }}>
                      <span className="myob-person-name">{candidate.card.name}</span>
                      <span className="myob-sub">
                        {selectable
                          ? joinSubtitle([
                              candidate.card.email,
                              candidate.card.phoneMasked ? t('myob.drawer.phone', { value: candidate.card.phoneMasked }) : null,
                            ])
                          : t('myob.drawer.boundTo', { name: candidate.boundToUserName ?? '' })}
                      </span>
                    </span>
                    {selectable ? <ScoreBadge score={candidate.score} /> : <Tag>{t('myob.drawer.unselectable')}</Tag>}
                  </button>
                </li>
              )
            })}
          </ul>
        )}

        {/* 选中后：逐项对比 + 证据不足提示 */}
        {selected ? (
          <>
            <div>
              <h3 className="myob-section-title">{t('myob.drawer.compareTitle')}</h3>
              <CompareTable candidate={selected} ourName={ourName} ourEmail={ourEmail} ourPhone={ourPhone} ourBirthday={ourBirthday} ourType={ourType} />
            </div>
            {getEvidenceWarning(selected.basis).messageKey ? (
              <Alert type="warning" showIcon message={t(getEvidenceWarning(selected.basis).messageKey as string)} />
            ) : null}
          </>
        ) : null}
      </div>
    </Drawer>
  )
}

interface CompareTableProps {
  candidate: MyobMatchCandidate
  ourName: string
  ourEmail: string | null
  ourPhone: string | null
  ourBirthday: string | null
  ourType: string | null
}

function CompareTable({ candidate, ourName, ourEmail, ourPhone, ourBirthday, ourType }: CompareTableProps) {
  const { t } = useTranslation()
  const rows = buildCompareRows(candidate, ourType)
  const dash = '—'
  const notFilled = t('myob.drawer.notFilled')
  const card = candidate.card
  // 两侧取值（与 buildCompareRows 的行顺序一一对应）
  const values: Record<string, { ours: string; theirs: string; mono?: boolean }> = {
    name: { ours: ourName || dash, theirs: card.name || dash },
    email: { ours: ourEmail ?? notFilled, theirs: card.email ?? dash },
    phone: { ours: ourPhone ?? notFilled, theirs: card.phoneMasked ?? dash, mono: true },
    birthday: { ours: ourBirthday ?? notFilled, theirs: formatBirthday(card.dateOfBirth) ?? dash },
    type: {
      ours: ourType ? ourTypeLabel(t, ourType) : notFilled,
      theirs: t(describeEntitlementKey(card.hasAnnualEntitlement, card.hasSickEntitlement)),
    },
  }
  return (
    <table className="myob-compare">
      <thead>
        <tr>
          <th scope="col" />
          <th scope="col">{t('myob.drawer.compare.ours')}</th>
          <th scope="col">{t('myob.drawer.compare.theirs')}</th>
          <th scope="col" style={{ width: 90 }}>
            {t('myob.drawer.compare.result')}
          </th>
        </tr>
      </thead>
      <tbody>
        {rows.map((item) => {
          const value = values[item.field]
          const color = item.result === 'same' ? 'success' : item.result === 'diff' ? 'error' : 'default'
          return (
            <tr key={item.field}>
              <th scope="row">{t(item.labelKey)}</th>
              <td className={value.mono ? 'myob-mono' : undefined}>{value.ours}</td>
              <td className={value.mono ? 'myob-mono' : undefined}>
                {item.highlightTheirs ? <span className="myob-diff">{value.theirs}</span> : value.theirs}
              </td>
              <td>
                <Tag color={color} style={{ marginInlineEnd: 0 }}>
                  {t(item.resultKey)}
                </Tag>
              </td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}

function ourTypeLabel(t: TFunction, type: string): string {
  const key = getEmployeeTypeKey(type)
  return key ? t(key) : type
}
