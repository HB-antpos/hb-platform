import { CheckOutlined, CloseOutlined, ReloadOutlined, SearchOutlined, SendOutlined } from '@ant-design/icons'
import { Alert, Button, Input, Modal, Select, Space, Tag, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'

import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  fetchMinorEmploymentDocument,
  getMinorStaffComplianceList,
  minorStaffRequestError,
  revokeMinorGuardianLink,
} from '../../../services/minorEmploymentService'
import type { GuardianLinkSendResult, MinorStaffComplianceItem, MinorStaffComplianceQuery } from '../../../types/minorEmployment'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import { MinorGuardianSendModal, MinorGuardianSendResultModal } from './MinorGuardianSendModal'
import {
  formatSydneyMonthDay,
  formatSydneyMonthDayTime,
  getFormTypeLabel,
  getLastSentChannels,
  getRelationshipLabel,
  getReturnFieldsText,
  getStageMeta,
  getStaffActions,
  shouldShowSendResult,
} from './minorGuardianLinkLogic'

const VERSION_CONFLICT_CODE = 'MINOR_COMPLIANCE_VERSION_CONFLICT'

type StatusFilter = 'needs' | 'all'

interface MinorGuardianLinkPanelProps {
  /** 门店筛选项（复用员工资料页的筛选接口，已按调用者范围裁剪）。 */
  stores: Array<{ storeCode: string; storeName: string }>
  /** 每次列表加载后回传「需要处理」人数，用于页签红点。 */
  onNeedsActionCountChange: (count: number) => void
  /** 「去审核」：切到「待审核 → 未成年用工合规」子页签。 */
  onGoReview: () => void
}

/** 员工个人信息维护 → 未成年用工合规：列出未满 18 岁员工，按阶段发送 / 重发 / 作废家长签字链接。 */
export default function MinorGuardianLinkPanel({ stores, onNeedsActionCountChange, onGoReview }: MinorGuardianLinkPanelProps) {
  const [rows, setRows] = useState<MinorStaffComplianceItem[]>([])
  const [loading, setLoading] = useState(false)
  const [smsAvailable, setSmsAvailable] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [storeCode, setStoreCode] = useState<string | undefined>()
  const [stateCode, setStateCode] = useState<MinorStaffComplianceQuery['stateCode']>()
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('needs')
  const [sendTarget, setSendTarget] = useState<{ item: MinorStaffComplianceItem; resend: boolean } | null>(null)
  const [sendResult, setSendResult] = useState<GuardianLinkSendResult | null>(null)
  const [documentLoadingId, setDocumentLoadingId] = useState<number | null>(null)
  const guardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)
  const countCallbackRef = useRef(onNeedsActionCountChange)

  useLayoutEffect(() => {
    countCallbackRef.current = onNeedsActionCountChange
  })

  useLayoutEffect(() => {
    mountedRef.current = true
    const guard = guardRef.current
    return () => {
      mountedRef.current = false
      guard.invalidate()
    }
  }, [])

  // 关键词只在回车 / 清空 / 刷新时生效，其余筛选变化立即重载；只让最后一次请求写入列表。
  const load = async (overrides: Partial<{ keyword: string; storeCode?: string; stateCode?: MinorStaffComplianceQuery['stateCode']; statusFilter: StatusFilter }> = {}) => {
    const query = { keyword, storeCode, stateCode, statusFilter, ...overrides }
    await runLatestGuardedRequest(
      guardRef.current,
      () => getMinorStaffComplianceList({
        keyword: query.keyword.trim() || undefined,
        storeCode: query.storeCode,
        stateCode: query.stateCode,
        needsActionOnly: query.statusFilter === 'needs',
      }),
      {
        onStart: () => setLoading(true),
        onSuccess: (result) => {
          if (!mountedRef.current) return
          setRows(result.items)
          setSmsAvailable(result.smsAvailable)
          countCallbackRef.current(result.needsActionCount)
        },
        onError: (error) => {
          if (!mountedRef.current) return
          message.error(minorStaffRequestError(error, '未成年员工列表加载失败').message)
        },
        onSettled: () => {
          if (mountedRef.current) setLoading(false)
        },
      },
    )
  }

  useEffect(() => {
    void load()
    // 首次挂载加载一次；之后由筛选控件显式触发。
  }, [])

  const reloadAfterConflict = () => {
    setSendTarget(null)
    void load()
  }

  const handleSent = (result: GuardianLinkSendResult) => {
    setSendTarget(null)
    void load()
    // 有渠道失败或返回了链接：弹「发送结果」给出可复制的链接；全部成功只弹提示。
    if (shouldShowSendResult(result)) {
      setSendResult(result)
      return
    }
    message.success(result.message || '已发送给家长')
  }

  const confirmRevoke = (item: MinorStaffComplianceItem) => {
    if (item.version === undefined || item.revision === undefined) {
      message.error('缺少合规记录版本，请刷新后重试')
      return
    }
    const expected = { expectedVersion: item.version, expectedRevision: item.revision }
    Modal.confirm({
      title: '作废家长签字链接？',
      content: `作废后 ${item.employeeName} 家长手里的链接立即失效；需要时可以再次发送。`,
      okText: '作废链接',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: async () => {
        try {
          await revokeMinorGuardianLink(item.userGUID, expected)
          message.success('链接已作废')
        } catch (error) {
          const { code, message: text } = minorStaffRequestError(error, '作废失败，请稍后重试')
          message.error(code === VERSION_CONFLICT_CODE ? text || '资料已被其他人更新，请刷新后重试' : text)
        } finally {
          void load()
        }
      },
    })
  }

  const openDocument = async (item: MinorStaffComplianceItem) => {
    if (!item.complianceId) return
    setDocumentLoadingId(item.complianceId)
    try {
      const result = await fetchMinorEmploymentDocument(String(item.complianceId))
      const url = URL.createObjectURL(result.blob)
      window.open(url, '_blank', 'noopener,noreferrer')
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
    } catch (error) {
      message.error(error instanceof Error ? error.message : '签署原件暂不可用')
    } finally {
      setDocumentLoadingId(null)
    }
  }

  const renderActions = (item: MinorStaffComplianceItem) => {
    const actions = getStaffActions(item)
    if (actions.length === 0) return <Typography.Text type="secondary">—</Typography.Text>
    return (
      <Space size={4} wrap>
        {actions.map((action) => {
          switch (action) {
            case 'send':
              return (
                <Button key={action} type="primary" icon={<SendOutlined />} onClick={() => setSendTarget({ item, resend: false })}>
                  发送签字链接
                </Button>
              )
            case 'resend':
              // 退回修改只有一个操作，用描边按钮；待家长签字与「作废链接」并排，用链接按钮。
              return item.stage === 'returned' ? (
                <Button key={action} icon={<ReloadOutlined />} onClick={() => setSendTarget({ item, resend: true })}>
                  重新发送
                </Button>
              ) : (
                <Button key={action} type="link" style={{ paddingInline: 4 }} onClick={() => setSendTarget({ item, resend: true })}>
                  重新发送
                </Button>
              )
            case 'revoke':
              return (
                <Button key={action} type="link" danger style={{ paddingInline: 4 }} onClick={() => confirmRevoke(item)}>
                  作废链接
                </Button>
              )
            case 'review':
              return (
                <Button key={action} type="link" style={{ paddingInline: 4 }} onClick={onGoReview}>
                  去审核
                </Button>
              )
            case 'document':
              return (
                <Button
                  key={action}
                  type="link"
                  style={{ paddingInline: 4 }}
                  loading={documentLoadingId === item.complianceId}
                  onClick={() => void openDocument(item)}
                >
                  查看原件
                </Button>
              )
            default:
              return null
          }
        })}
      </Space>
    )
  }

  const secondary = (content: ReactNode) => (
    <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block' }}>{content}</Typography.Text>
  )

  const columns: ColumnsType<MinorStaffComplianceItem> = [
    {
      title: '员工',
      key: 'employee',
      width: 170,
      render: (_, item) => (
        <div>
          <Typography.Text strong>{item.employeeName}</Typography.Text>
          {secondary(`${item.username ? `@${item.username} · ` : ''}${item.age} 岁`)}
        </div>
      ),
    },
    {
      title: '门店 / 州',
      key: 'store',
      width: 150,
      render: (_, item) => (
        <div>
          <span>{item.storeName || item.storeCode || '—'}</span>
          {secondary(item.stateCode === 'UNKNOWN' ? '州未确认' : item.stateCode)}
        </div>
      ),
    },
    {
      title: '同意书',
      key: 'formType',
      width: 160,
      render: (_, item) => getFormTypeLabel(item.formType),
    },
    {
      title: '状态',
      key: 'stage',
      width: 170,
      render: (_, item) => {
        const meta = getStageMeta(item.stage)
        let detail: ReactNode = null
        if (item.stage === 'awaiting_guardian' && item.linkExpiresAtUtc) {
          detail = `${formatSydneyMonthDayTime(item.linkExpiresAtUtc)} 前有效`
        } else if (item.stage === 'link_expired' && item.linkExpiresAtUtc) {
          detail = `${formatSydneyMonthDayTime(item.linkExpiresAtUtc)} 已过期`
        } else if (item.stage === 'pending_review' && item.guardianSignedAtUtc) {
          detail = `家长 ${formatSydneyMonthDay(item.guardianSignedAtUtc)} 签字`
        } else if (item.stage === 'returned') {
          const fields = getReturnFieldsText(item.returnFields)
          detail = [fields, item.reviewComment].filter(Boolean).join(' · ') || null
        }
        return (
          <div>
            <Tag color={meta.color} style={{ marginInlineEnd: 0 }}>{meta.label}</Tag>
            {detail ? <div style={{ marginTop: 2 }}>{secondary(detail)}</div> : null}
          </div>
        )
      },
    },
    {
      title: '监护人',
      key: 'guardian',
      width: 230,
      render: (_, item) => {
        if (!item.guardianName) {
          return (
            <div>
              <Typography.Text type="danger">缺监护人资料</Typography.Text>
              {secondary('发送时在弹窗里填写')}
            </div>
          )
        }
        const relationship = getRelationshipLabel(item.guardianRelationship)
        const contacts = [item.maskedGuardianEmail, item.maskedGuardianPhone].filter(Boolean).join(' · ')
        return (
          <div>
            <span>{item.guardianName}{relationship ? `（${relationship}）` : ''}</span>
            {contacts ? secondary(contacts) : null}
          </div>
        )
      },
    },
    {
      title: '最近发送',
      key: 'lastSent',
      width: 150,
      render: (_, item) => {
        if (!item.lastSentAtUtc) return <Typography.Text type="secondary">—</Typography.Text>
        const channels = getLastSentChannels(item)
        return (
          <div>
            <span>{formatSydneyMonthDayTime(item.lastSentAtUtc)}</span>
            {channels.length > 0
              ? secondary(
                  channels.map((channel, index) => (
                    <span key={channel.label}>
                      {index > 0 ? ' · ' : ''}
                      {channel.label}{' '}
                      {channel.ok
                        ? <CheckOutlined aria-label="送达" />
                        : <CloseOutlined aria-label="失败" style={{ color: '#cf1322' }} />}
                    </span>
                  )),
                )
              : null}
          </div>
        )
      },
    },
    {
      title: '操作',
      key: 'action',
      width: 190,
      fixed: 'right',
      render: (_, item) => renderActions(item),
    },
  ]

  return (
    <>
      <Alert
        type="info"
        showIcon
        message="按出生日期列出未满 18 岁的员工。签字链接由这里或手机「人员管理」发给家长（英文邮件 / 短信），员工本人不会收到提醒。红点 = 还没发或被退回、需要处理的人数。"
        style={{ marginBottom: 14 }}
      />
      <Space wrap style={{ marginBottom: 14 }}>
        <Input
          allowClear
          prefix={<SearchOutlined />}
          placeholder="员工姓名 / 用户名 · 回车查询"
          style={{ width: 240 }}
          value={keyword}
          onChange={(event) => {
            setKeyword(event.target.value)
            // 清空（含点清空按钮）时立即按空关键词重查，其余输入等回车。
            if (!event.target.value) void load({ keyword: '' })
          }}
          onPressEnter={() => void load()}
        />
        <Select
          allowClear
          showSearch
          optionFilterProp="label"
          placeholder="全部门店"
          aria-label="门店"
          style={{ width: 180 }}
          value={storeCode}
          options={stores.map((store) => ({ value: store.storeCode, label: store.storeName || store.storeCode }))}
          onChange={(value?: string) => {
            setStoreCode(value ?? undefined)
            void load({ storeCode: value ?? undefined })
          }}
        />
        <Select
          allowClear
          placeholder="全部州"
          aria-label="州"
          style={{ width: 130 }}
          value={stateCode}
          options={[{ value: 'NSW', label: 'NSW' }, { value: 'QLD', label: 'QLD' }, { value: 'UNKNOWN', label: '州未确认' }]}
          onChange={(value?: MinorStaffComplianceQuery['stateCode']) => {
            setStateCode(value ?? undefined)
            void load({ stateCode: value ?? undefined })
          }}
        />
        <Select
          aria-label="状态"
          style={{ width: 140 }}
          value={statusFilter}
          options={[{ value: 'needs', label: '需要处理' }, { value: 'all', label: '全部' }]}
          onChange={(value: StatusFilter) => {
            setStatusFilter(value)
            void load({ statusFilter: value })
          }}
        />
        <Button icon={<ReloadOutlined />} onClick={() => void load()}>刷新</Button>
      </Space>

      <MeasuredTable
        metricId="system.employee-profiles.minor-guardian-table"
        rowKey="userGUID"
        loading={loading}
        columns={columns}
        dataSource={rows}
        scroll={{ x: 1220 }}
        locale={{ emptyText: statusFilter === 'needs' ? '没有需要处理的未成年员工' : '没有未满 18 岁的员工' }}
        pagination={{ pageSize: 20, showSizeChanger: false, hideOnSinglePage: false, showTotal: (count) => `共 ${count} 条` }}
      />

      {sendTarget ? (
        <MinorGuardianSendModal
          key={sendTarget.item.userGUID}
          item={sendTarget.item}
          smsAvailable={smsAvailable}
          resend={sendTarget.resend}
          onClose={() => setSendTarget(null)}
          onSent={handleSent}
          onConflict={reloadAfterConflict}
        />
      ) : null}
      {sendResult ? <MinorGuardianSendResultModal result={sendResult} onClose={() => setSendResult(null)} /> : null}
    </>
  )
}
