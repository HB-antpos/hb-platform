import {
  CheckOutlined,
  CloseOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Button,
  Drawer,
  Empty,
  Form,
  Image,
  Input,
  Space,
  Spin,
  Tag,
  Typography,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  approveAdminSensitiveChangeRequest,
  getAdminEmployeeProfile,
  getAdminSensitiveChangeRequest,
  getAdminSensitiveChangeRequests,
  rejectAdminSensitiveChangeRequest,
} from '../../../services/employeeProfileService'
import type {
  EmployeeProfileDetailDto,
  EmployeeProfileSensitiveChangeDetailDto,
  EmployeeProfileSensitiveChangeQueryDto,
  EmployeeProfileSensitiveChangeStatus,
  EmployeeProfileSensitiveChangeSummaryDto,
} from '../../../types/employeeProfile'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'
import {
  getReviewChangedFields,
  handleSensitiveReviewFailure,
  isRejectReasonValid,
  createLatestRequestGuard as createDetailRequestGuard,
  isSensitiveRequestReviewable,
  type SensitiveProfileField,
} from './logic'
import { MeasuredTable } from '../../../components/MeasuredTable'

interface SensitiveChangeReviewPanelProps {
  refreshPendingCount: () => Promise<void>
}

type ReviewListRow = EmployeeProfileSensitiveChangeSummaryDto
type DesiredSensitiveChangeQuery = EmployeeProfileSensitiveChangeQueryDto & {
  page: number
  pageSize: number
}

interface ReviewFormValues {
  reason?: string
}

const STATUS_COLORS: Record<EmployeeProfileSensitiveChangeStatus, string> = {
  Pending: 'gold',
  Approved: 'green',
  Rejected: 'red',
  Superseded: 'default',
  Withdrawn: 'default',
}

function formatSensitiveValue(value: unknown) {
  return typeof value === 'string' && value.trim() ? value : '--'
}

export default function SensitiveChangeReviewPanel({
  refreshPendingCount,
}: SensitiveChangeReviewPanelProps) {
  const { t, i18n } = useTranslation()
  const [loading, setLoading] = useState(false)
  const [listError, setListError] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [rows, setRows] = useState<ReviewListRow[]>([])
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [total, setTotal] = useState(0)
  const [reviewOpen, setReviewOpen] = useState(false)
  const [detailLoading, setDetailLoading] = useState(false)
  const [reviewLoading, setReviewLoading] = useState(false)
  const [detailError, setDetailError] = useState(false)
  const [reviewDetail, setReviewDetail] = useState<EmployeeProfileSensitiveChangeDetailDto | null>(null)
  const [currentProfile, setCurrentProfile] = useState<EmployeeProfileDetailDto | null>(null)
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const detailRequestGuardRef = useRef(createDetailRequestGuard())
  const activeRequestIdRef = useRef<number | null>(null)
  const mountedRef = useRef(false)
  const desiredListQueryRef = useRef<DesiredSensitiveChangeQuery>({
    page,
    pageSize,
    status: 'Pending',
    keyword: keyword || undefined,
  })
  const [reviewForm] = Form.useForm<ReviewFormValues>()

  const formatDateTime = (value?: string) => {
    if (!value) return '--'
    return new Intl.DateTimeFormat(i18n.language, {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    }).format(new Date(value))
  }

  const fieldLabel = (field: SensitiveProfileField) =>
    t(`system.employeeProfiles.review.fields.${field}`)

  const loadList = async (overrides: Partial<DesiredSensitiveChangeQuery> = {}) => {
    if (!mountedRef.current) {
      return
    }

    const query: DesiredSensitiveChangeQuery = {
      page,
      pageSize,
      status: 'Pending',
      keyword: keyword || undefined,
      ...overrides,
    }
    // 审核 mutation 完成时始终复用已经 begin 的目标页和搜索条件。
    desiredListQueryRef.current = query

    await runLatestGuardedRequest(listRequestGuardRef.current, () => getAdminSensitiveChangeRequests(query), {
      onStart: () => {
        setLoading(true)
        setListError(false)
      },
      onSuccess: (result) => {
        // 列表仅消费服务端安全字段标识，禁止在打开审核抽屉前请求任何完整敏感详情。
        setRows(result.items)
        setTotal(result.total)
        setPage(result.page)
        setPageSize(result.pageSize)
      },
      onError: () => {
        setListError(true)
        message.error(t('system.employeeProfiles.review.loadListFailed'))
      },
      onSettled: () => setLoading(false),
    })
  }

  const latestLoadListRef = useRef(loadList)

  useLayoutEffect(() => {
    latestLoadListRef.current = loadList
  })

  const refreshDesiredList = (overrides: Partial<DesiredSensitiveChangeQuery> = {}) =>
    latestLoadListRef.current({ ...desiredListQueryRef.current, ...overrides })

  useLayoutEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      listRequestGuardRef.current.invalidate()
      detailRequestGuardRef.current.invalidate()
      activeRequestIdRef.current = null
    }
  }, [])

  useEffect(() => {
    void loadList({ page: 1, pageSize })
  }, [])

  const loadReviewDetail = async (requestId: number) => {
    const requestToken = detailRequestGuardRef.current.begin()
    setDetailLoading(true)
    setDetailError(false)
    try {
      const detail = await getAdminSensitiveChangeRequest(requestId)
      const profile = await getAdminEmployeeProfile(detail.userGuid)
      if (!detailRequestGuardRef.current.isCurrent(requestToken)
        || activeRequestIdRef.current !== requestId) {
        return
      }
      setReviewDetail(detail)
      setCurrentProfile(profile)
    } catch {
      if (!detailRequestGuardRef.current.isCurrent(requestToken)
        || activeRequestIdRef.current !== requestId) {
        return
      }
      setDetailError(true)
      message.error(t('system.employeeProfiles.review.loadDetailFailed'))
    } finally {
      if (detailRequestGuardRef.current.isCurrent(requestToken)
        && activeRequestIdRef.current === requestId) {
        setDetailLoading(false)
      }
    }
  }

  const openReview = (record: ReviewListRow) => {
    activeRequestIdRef.current = record.requestId
    setReviewOpen(true)
    setReviewDetail(null)
    setCurrentProfile(null)
    reviewForm.resetFields()
    void loadReviewDetail(record.requestId)
  }

  const closeReview = () => {
    activeRequestIdRef.current = null
    detailRequestGuardRef.current.invalidate()
    setReviewOpen(false)
    setDetailLoading(false)
    setReviewDetail(null)
    setCurrentProfile(null)
    setDetailError(false)
    reviewForm.resetFields()
  }

  const submitReview = async (action: 'approve' | 'reject') => {
    if (!reviewDetail || !isSensitiveRequestReviewable(reviewDetail.status)) {
      return
    }

    const reason = reviewForm.getFieldValue('reason')?.trim()
    if (action === 'reject' && !isRejectReasonValid(reason)) {
      reviewForm.setFields([{ name: 'reason', errors: [t('system.employeeProfiles.review.reasonRequired')] }])
      return
    }
    reviewForm.setFields([{ name: 'reason', errors: [] }])

    const requestId = reviewDetail.requestId
    setReviewLoading(true)
    try {
      if (action === 'approve') {
        await approveAdminSensitiveChangeRequest(requestId, { reason: reason || undefined })
      } else {
        await rejectAdminSensitiveChangeRequest(requestId, { reason: reason! })
      }
      message.success(t(`system.employeeProfiles.review.${action}Success`))
      closeReview()
      await Promise.all([refreshDesiredList(), refreshPendingCount()])
    } catch (error) {
      const conflictKind = await handleSensitiveReviewFailure(
        error,
        () => loadReviewDetail(requestId),
        () => refreshDesiredList(),
        refreshPendingCount,
      )
      message[conflictKind ? 'warning' : 'error'](
        t(conflictKind
          ? `system.employeeProfiles.review.${conflictKind === 'terminal' ? 'terminalConflict' : 'versionConflict'}`
          : `system.employeeProfiles.review.${action}Failed`),
      )
    } finally {
      setReviewLoading(false)
    }
  }

  const columns: ColumnsType<ReviewListRow> = [
    {
      title: t('system.employeeProfiles.review.employee'),
      key: 'employee',
      render: (_, record) => (
        <Typography.Text strong>{record.username || '--'}</Typography.Text>
      ),
    },
    {
      title: t('system.employeeProfiles.review.changedFields'),
      dataIndex: 'changedFields',
      render: (fields: SensitiveProfileField[]) => fields.length > 0
          ? <Space size={[4, 4]} wrap>{fields.map((field) => <Tag key={field}>{fieldLabel(field)}</Tag>)}</Space>
          : '--',
    },
    {
      title: t('system.employeeProfiles.review.submittedAt'),
      dataIndex: 'submittedAt',
      width: 180,
      render: (value: string) => formatDateTime(value),
    },
    {
      title: t('system.employeeProfiles.review.status'),
      dataIndex: 'status',
      width: 110,
      render: (status: EmployeeProfileSensitiveChangeStatus) => (
        <Tag color={STATUS_COLORS[status]}>{t(`system.employeeProfiles.review.statuses.${status}`)}</Tag>
      ),
    },
    {
      title: t('column.action'),
      key: 'action',
      width: 100,
      render: (_, record) => (
        <Button type="link" onClick={() => openReview(record)}>
          {t('system.employeeProfiles.review.reviewAction')}
        </Button>
      ),
    },
  ]

  const comparisonRows = useMemo(() => {
    if (!reviewDetail || !currentProfile) {
      return []
    }
    const definitions: Array<{
      key: SensitiveProfileField
      current: unknown
      proposed: unknown
    }> = [
      // 生日只展示日期部分，与员工端填报格式一致。
      { key: 'birthday', current: currentProfile.birthday?.slice(0, 10), proposed: reviewDetail.birthday?.slice(0, 10) },
      { key: 'bankBsb', current: currentProfile.bankBsb, proposed: reviewDetail.bankBsb },
      { key: 'bankAccountNumber', current: currentProfile.bankAccountNumber, proposed: reviewDetail.bankAccountNumber },
      { key: 'superannuationCompanyName', current: currentProfile.superannuationCompanyName, proposed: reviewDetail.superannuationCompanyName },
      { key: 'superannuationCompanyCode', current: currentProfile.superannuationCompanyCode, proposed: reviewDetail.superannuationCompanyCode },
      { key: 'superannuationAccountNumber', current: currentProfile.superannuationAccountNumber, proposed: reviewDetail.superannuationAccountNumber },
      { key: 'identityType', current: currentProfile.identityType, proposed: reviewDetail.identityType },
      { key: 'identityId', current: currentProfile.identityId, proposed: reviewDetail.identityId },
      { key: 'identityPhotoUrl', current: currentProfile.identityPhotoUrl, proposed: reviewDetail.identityPhotoUrl },
    ]
    const changed = new Set(getReviewChangedFields(
      currentProfile,
      reviewDetail,
      reviewDetail.changedFields.includes('identityPhotoUrl'),
    ))
    return definitions.map((item) => ({ ...item, changed: changed.has(item.key) }))
  }, [currentProfile, reviewDetail])

  return (
    <>
      <Space wrap style={{ marginBottom: 16 }}>
        <Input
          allowClear
          prefix={<SearchOutlined />}
          placeholder={t('system.employeeProfiles.review.searchPlaceholder')}
          style={{ width: 300 }}
          value={keyword}
          onChange={(event) => setKeyword(event.target.value)}
          onPressEnter={() => void loadList({ page: 1, pageSize })}
        />
        <Button type="primary" onClick={() => void loadList({ page: 1, pageSize })}>{t('common.query')}</Button>
        <Button icon={<ReloadOutlined />} onClick={() => void refreshDesiredList()}>{t('common.refresh')}</Button>
      </Space>

      {listError ? (
        <Alert
          type="error"
          showIcon
          message={t('system.employeeProfiles.review.loadListFailed')}
          action={<Button size="small" onClick={() => void refreshDesiredList()}>{t('common.retry')}</Button>}
          style={{ marginBottom: 16 }}
        />
      ) : null}

      <MeasuredTable metricId="system.employee-profiles.sensitive-change-review-panel.table-1"
        rowKey="requestId"
        loading={loading}
        columns={columns}
        dataSource={rows}
        locale={{ emptyText: <Empty description={t('system.employeeProfiles.review.empty')} /> }}
        scroll={{ x: 880 }}
        pagination={{
          current: page,
          pageSize,
          total,
          onChange: (nextPage, nextPageSize) => void loadList({ page: nextPage, pageSize: nextPageSize }),
        }}
      />

      <Drawer
        width={920}
        destroyOnHidden
        open={reviewOpen}
        onClose={closeReview}
        title={t('system.employeeProfiles.review.drawerTitle', {
          name: reviewDetail?.username || reviewDetail?.userGuid || '',
        })}
        extra={isSensitiveRequestReviewable(reviewDetail?.status) ? (
          <Space>
            <Button
              danger
              icon={<CloseOutlined />}
              loading={reviewLoading}
              onClick={() => void submitReview('reject')}
            >
              {t('system.employeeProfiles.review.reject')}
            </Button>
            <Button
              type="primary"
              icon={<CheckOutlined />}
              loading={reviewLoading}
              onClick={() => void submitReview('approve')}
            >
              {t('system.employeeProfiles.review.approve')}
            </Button>
          </Space>
        ) : null}
      >
        {detailLoading && !reviewDetail ? (
          <Spin tip={t('system.employeeProfiles.review.loadingDetail')} />
        ) : detailError || !reviewDetail || !currentProfile ? (
          <Alert type="error" showIcon message={t('system.employeeProfiles.review.detailUnavailable')} />
        ) : (
          <Space direction="vertical" size={16} style={{ width: '100%' }}>
            <Alert
              type="info"
              showIcon
              message={t('system.employeeProfiles.review.authorizedDetailNotice')}
            />
            <MeasuredTable metricId="system.employee-profiles.sensitive-change-review-panel.table-2"
              rowKey="key"
              size="small"
              bordered
              pagination={false}
              dataSource={comparisonRows}
              columns={[
                {
                  title: t('system.employeeProfiles.review.field'),
                  dataIndex: 'key',
                  width: 210,
                  render: (field: SensitiveProfileField, record) => (
                    <Space>
                      <Typography.Text>{fieldLabel(field)}</Typography.Text>
                      {record.changed ? <Tag color="gold">{t('system.employeeProfiles.review.changed')}</Tag> : null}
                    </Space>
                  ),
                },
                {
                  title: t('system.employeeProfiles.review.currentValue'),
                  dataIndex: 'current',
                  render: (value: unknown, record) => record.key === 'identityPhotoUrl'
                    ? (value ? t('system.employeeProfiles.review.hasPhoto') : t('system.employeeProfiles.review.noPhoto'))
                    : <Typography.Text>{formatSensitiveValue(value)}</Typography.Text>,
                },
                {
                  title: t('system.employeeProfiles.review.proposedValue'),
                  dataIndex: 'proposed',
                  render: (value: unknown, record) => record.key === 'identityPhotoUrl'
                    ? (value ? t('system.employeeProfiles.review.hasPhoto') : t('system.employeeProfiles.review.noPhoto'))
                    : <Typography.Text strong={record.changed}>{formatSensitiveValue(value)}</Typography.Text>,
                },
              ]}
            />

            {(currentProfile.identityPhotoUrl || reviewDetail.identityPhotoUrl) ? (
              <Space align="start" size={24} wrap>
                <Space direction="vertical">
                  <Typography.Text strong>{t('system.employeeProfiles.review.currentPhoto')}</Typography.Text>
                  {currentProfile.identityPhotoUrl
                    ? <Image src={currentProfile.identityPhotoUrl} width={220} style={{ borderRadius: 6 }} />
                    : <Typography.Text type="secondary">{t('system.employeeProfiles.review.noPhoto')}</Typography.Text>}
                </Space>
                <Space direction="vertical">
                  <Typography.Text strong>{t('system.employeeProfiles.review.proposedPhoto')}</Typography.Text>
                  {reviewDetail.identityPhotoUrl
                    ? <Image src={reviewDetail.identityPhotoUrl} width={220} style={{ borderRadius: 6 }} />
                    : <Typography.Text type="secondary">{t('system.employeeProfiles.review.noPhoto')}</Typography.Text>}
                </Space>
              </Space>
            ) : null}

            <Form form={reviewForm} layout="vertical">
              <Form.Item
                name="reason"
                label={t('system.employeeProfiles.review.reason')}
                extra={t('system.employeeProfiles.review.reasonHint')}
              >
                <Input.TextArea rows={3} maxLength={1000} showCount />
              </Form.Item>
            </Form>
          </Space>
        )}
      </Drawer>
    </>
  )
}
