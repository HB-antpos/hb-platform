import { EditOutlined, ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import {
  Badge,
  Button,
  Card,
  Col,
  DatePicker,
  Drawer,
  Form,
  Image,
  Input,
  Modal,
  Row,
  Select,
  Space,
  Tabs,
  Tag,
  Typography,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { Dayjs } from 'dayjs'
import dayjs from 'dayjs'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import PageContainer from '../../../components/PageContainer'
import {
  getAdminEmployeeProfile,
  getAdminEmployeeProfiles,
  getAdminSensitiveChangeRequests,
  saveAdminEmployeeProfile,
} from '../../../services/employeeProfileService'
import { useAuthStore } from '../../../store/auth'
import type {
  EmployeeEmploymentType,
  EmployeeProfileDetailDto,
  EmployeeProfileGender,
  EmployeeProfileQueryDto,
  EmployeeProfileSummaryDto,
} from '../../../types/employeeProfile'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'
import SensitiveChangeReviewPanel from './SensitiveChangeReviewPanel'
import MinorEmploymentReviewPage from '../MinorEmploymentReview'
import {
  getExpectedSensitiveRevision,
  maskSensitiveSummary,
  saveAdminProfileWithPendingConfirmation,
} from './logic'
import { MeasuredTable } from '../../../components/MeasuredTable'

interface EmployeeProfileFormValues {
  userGUID?: string
  userId?: string
  username?: string
  displayName?: string
  bankBsb?: string
  bankAccountNumber?: string
  superannuationCompanyName?: string
  superannuationCompanyCode?: string
  superannuationAccountNumber?: string
  birthday?: Dayjs | null
  gender?: EmployeeProfileGender
  employmentType?: EmployeeEmploymentType
  avatarUrl?: string
  identityId?: string
  identityType?: string
  identityPhotoUrl?: string
  address?: string
}

type DesiredEmployeeProfileQuery = EmployeeProfileQueryDto & {
  page: number
  pageSize: number
}

function formatDateTime(value?: string, language?: string) {
  if (!value) {
    return '--'
  }

  const date = dayjs(value)
  if (!date.isValid()) {
    return value
  }

  return date.locale(language?.startsWith('zh') ? 'zh-cn' : 'en').format('YYYY-MM-DD HH:mm')
}

function formatDate(value?: string) {
  if (!value) {
    return '--'
  }

  const date = dayjs(value)
  return date.isValid() ? date.format('YYYY-MM-DD') : value
}

function joinSummary(parts: Array<string | undefined>) {
  const values = parts.map((item) => item?.trim()).filter(Boolean)
  return values.length > 0 ? values.join(' / ') : '--'
}

function getProfileKey(record: Pick<EmployeeProfileSummaryDto, 'id' | 'userGUID' | 'userId' | 'username'>) {
  return record.id || record.userGUID || record.userId || record.username || ''
}

function mapProfileToFormValues(profile: EmployeeProfileDetailDto): EmployeeProfileFormValues {
  return {
    userGUID: profile.userGUID,
    userId: profile.userId,
    username: profile.username,
    displayName: profile.displayName,
    bankBsb: profile.bankBsb,
    bankAccountNumber: profile.bankAccountNumber,
    superannuationCompanyName: profile.superannuationCompanyName,
    superannuationCompanyCode: profile.superannuationCompanyCode,
    superannuationAccountNumber: profile.superannuationAccountNumber,
    birthday: profile.birthday ? dayjs(profile.birthday) : null,
    gender: profile.gender,
    employmentType: profile.employmentType,
    avatarUrl: profile.avatarUrl,
    identityId: profile.identityId,
    identityType: profile.identityType,
    identityPhotoUrl: profile.identityPhotoUrl,
    address: profile.address,
  }
}

export default function SystemEmployeeProfilesPage() {
  const { t, i18n } = useTranslation()
  // 后台编辑接口同时要求管理员角色与 EmployeeProfiles.Edit，管理员天然拥有全部权限，因此只按角色门控。
  const access = useAuthStore((state) => state.access)
  const canEditProfiles = access.isAdmin
  const [loading, setLoading] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [data, setData] = useState<EmployeeProfileSummaryDto[]>([])
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [total, setTotal] = useState(0)
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)
  const desiredListQueryRef = useRef<DesiredEmployeeProfileQuery>({
    keyword: keyword || undefined,
    page,
    pageSize,
  })
  const [editOpen, setEditOpen] = useState(false)
  const [editLoading, setEditLoading] = useState(false)
  const [editingProfile, setEditingProfile] = useState<EmployeeProfileDetailDto | null>(null)
  const [pendingCount, setPendingCount] = useState(0)
  const [form] = Form.useForm<EmployeeProfileFormValues>()

  const avatarUrl = Form.useWatch('avatarUrl', form)
  const identityPhotoUrl = Form.useWatch('identityPhotoUrl', form)

  const employmentTypeOptions = useMemo(
    () => [
      { label: t('system.employeeProfiles.employmentTypes.fullTime'), value: 'fullTime' },
      { label: t('system.employeeProfiles.employmentTypes.partTime'), value: 'partTime' },
      { label: t('system.employeeProfiles.employmentTypes.casual'), value: 'casual' },
    ] satisfies Array<{ label: string; value: EmployeeEmploymentType }>,
    [t],
  )

  const genderOptions = useMemo(
    () => [
      { label: t('system.employeeProfiles.genders.male'), value: 'male' },
      { label: t('system.employeeProfiles.genders.female'), value: 'female' },
      { label: t('system.employeeProfiles.genders.other'), value: 'other' },
      { label: t('system.employeeProfiles.genders.unknown'), value: 'unknown' },
    ] satisfies Array<{ label: string; value: EmployeeProfileGender }>,
    [t],
  )

  const employmentTypeLabelMap = useMemo(
    () =>
      employmentTypeOptions.reduce<Record<string, string>>((acc, item) => {
        acc[item.value] = item.label
        return acc
      }, {}),
    [employmentTypeOptions],
  )

  const genderLabelMap = useMemo(
    () =>
      genderOptions.reduce<Record<string, string>>((acc, item) => {
        acc[item.value] = item.label
        return acc
      }, {}),
    [genderOptions],
  )

  const loadData = async (overrides: Partial<DesiredEmployeeProfileQuery> = {}) => {
    if (!mountedRef.current) {
      return
    }

    const query: DesiredEmployeeProfileQuery = {
      keyword: keyword || undefined,
      page,
      pageSize,
      ...overrides,
    }
    // 分页请求 begin 前保存目标查询，避免保存操作晚完成后退回旧页。
    desiredListQueryRef.current = query

    await runLatestGuardedRequest(listRequestGuardRef.current, () => getAdminEmployeeProfiles(query), {
      onStart: () => setLoading(true),
      onSuccess: (result) => {
        setData(result.items)
        setTotal(result.total)
        setPage(result.page)
        setPageSize(result.pageSize)
      },
      onError: (error) => {
        console.error(error)
        message.error(t('system.employeeProfiles.loadListFailed'))
      },
      onSettled: () => setLoading(false),
    })
  }

  const latestLoadDataRef = useRef(loadData)

  useLayoutEffect(() => {
    latestLoadDataRef.current = loadData
  })

  const refreshDesiredList = (overrides: Partial<DesiredEmployeeProfileQuery> = {}) =>
    latestLoadDataRef.current({ ...desiredListQueryRef.current, ...overrides })

  useLayoutEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      listRequestGuardRef.current.invalidate()
    }
  }, [])

  const loadPendingCount = async () => {
    try {
      const result = await getAdminSensitiveChangeRequests({ page: 1, pageSize: 1, status: 'Pending' })
      setPendingCount(result.total)
    } catch {
      // 数量标记是辅助信息，列表页已有独立错误态，这里保持主页面可用。
    }
  }

  useEffect(() => {
    void loadData({ page: 1, pageSize })
    void loadPendingCount()
  }, [])

  const handleEdit = async (record: EmployeeProfileSummaryDto) => {
    const profileKey = getProfileKey(record)
    if (!profileKey) {
      message.error(t('system.employeeProfiles.missingRecordId'))
      return
    }

    setEditOpen(true)
    setEditLoading(true)
    setEditingProfile(null)
    form.resetFields()

    try {
      const detail = await getAdminEmployeeProfile(profileKey)
      setEditingProfile(detail)
      form.setFieldsValue(mapProfileToFormValues(detail))
    } catch (error) {
      console.error(error)
      message.error(t('system.employeeProfiles.loadDetailFailed'))
      setEditOpen(false)
    } finally {
      setEditLoading(false)
    }
  }

  const handleSubmit = async () => {
    if (!editingProfile) {
      return
    }

    try {
      const values = await form.validateFields()
      const formPayload = {
        id: editingProfile.id,
        userGUID: editingProfile.userGUID ?? values.userGUID,
        userId: editingProfile.userId ?? values.userId,
        username: values.username?.trim() || undefined,
        displayName: values.displayName?.trim() || undefined,
        bankBsb: values.bankBsb?.trim() || undefined,
        bankAccountNumber: values.bankAccountNumber?.trim() || undefined,
        superannuationCompanyName: values.superannuationCompanyName?.trim() || undefined,
        superannuationCompanyCode: values.superannuationCompanyCode?.trim() || undefined,
        superannuationAccountNumber: values.superannuationAccountNumber?.trim() || undefined,
        birthday: values.birthday ? values.birthday.format('YYYY-MM-DD') : undefined,
        gender: values.gender,
        employmentType: values.employmentType,
        avatarUrl: values.avatarUrl?.trim() || undefined,
        identityId: values.identityId?.trim() || undefined,
        identityType: values.identityType?.trim() || undefined,
        identityPhotoUrl: values.identityPhotoUrl?.trim() || undefined,
        address: values.address?.trim() || undefined,
      }
      const payload = {
        ...formPayload,
        expectedSensitiveRevision: getExpectedSensitiveRevision(editingProfile),
      }
      setEditLoading(true)
      const result = await saveAdminProfileWithPendingConfirmation(
        payload,
        saveAdminEmployeeProfile,
        () => new Promise<boolean>((resolve) => {
          Modal.confirm({
            title: t('system.employeeProfiles.pendingSupersede.title'),
            content: t('system.employeeProfiles.pendingSupersede.content'),
            okText: t('system.employeeProfiles.pendingSupersede.confirm'),
            cancelText: t('common.cancel'),
            onOk: () => resolve(true),
            onCancel: () => resolve(false),
          })
        }),
      )
      if (result.status === 'cancelled') {
        return
      }
      message.success(t('system.employeeProfiles.saveSuccess'))
      setEditOpen(false)
      setEditingProfile(null)
      form.resetFields()
      void refreshDesiredList()
      void loadPendingCount()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) {
        return
      }
      console.error(error)
      message.error(t('system.employeeProfiles.saveFailed'))
    } finally {
      setEditLoading(false)
    }
  }

  const columns: ColumnsType<EmployeeProfileSummaryDto> = [
    {
      title: t('system.employeeProfiles.account'),
      key: 'account',
      width: 220,
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{record.username || '--'}</Typography.Text>
          <Typography.Text type="secondary">{record.userGUID || record.userId || record.id || '--'}</Typography.Text>
        </Space>
      ),
    },
    {
      title: t('system.employeeProfiles.displayName'),
      dataIndex: 'displayName',
      width: 160,
      render: (value: string | undefined) => value || '--',
    },
    {
      title: t('system.employeeProfiles.employmentType'),
      dataIndex: 'employmentType',
      width: 120,
      render: (value: EmployeeEmploymentType | undefined) =>
        value ? <Tag color="blue">{employmentTypeLabelMap[value] || value}</Tag> : '--',
    },
    {
      title: t('system.employeeProfiles.bankSummary'),
      key: 'bankSummary',
      width: 220,
      render: (_, record) => joinSummary([record.bankBsb, maskSensitiveSummary(record.bankAccountNumber)]),
    },
    {
      title: t('system.employeeProfiles.superSummary'),
      key: 'superSummary',
      width: 260,
      render: (_, record) =>
        joinSummary([
          record.superannuationCompanyName,
          record.superannuationCompanyCode,
          maskSensitiveSummary(record.superannuationAccountNumber),
        ]),
    },
    {
      title: t('column.updateTime'),
      dataIndex: 'updatedAt',
      width: 160,
      render: (value: string | undefined) => formatDateTime(value, i18n.language),
    },
    {
      title: t('column.action'),
      key: 'action',
      width: 120,
      render: (_, record) => (
        <Button type="link" icon={<EditOutlined />} disabled={!canEditProfiles} onClick={() => void handleEdit(record)}>
          {t('common.edit')}
        </Button>
      ),
    },
  ]

  return (
    <PageContainer
      title={t('system.employeeProfiles.pageTitle')}
      subtitle={t('system.employeeProfiles.pageSubtitle')}
    >
      <Card>
        <Tabs
          items={[
            {
              key: 'profiles',
              label: t('system.employeeProfiles.tabs.profiles'),
              children: (
                <>
                  <Space wrap style={{ marginBottom: 16 }}>
                    <Input
                      allowClear
                      prefix={<SearchOutlined />}
                      placeholder={t('system.employeeProfiles.searchPlaceholder')}
                      style={{ width: 300 }}
                      value={keyword}
                      onChange={(event) => setKeyword(event.target.value)}
                      onPressEnter={() => void loadData({ page: 1, pageSize })}
                    />
                    <Button type="primary" onClick={() => void loadData({ page: 1, pageSize })}>
                      {t('common.query')}
                    </Button>
                    <Button icon={<ReloadOutlined />} onClick={() => void refreshDesiredList()}>
                      {t('common.refresh')}
                    </Button>
                  </Space>

                  <MeasuredTable metricId="system.employee-profiles.table-1"
                    rowKey={(record) => getProfileKey(record)}
                    loading={loading}
                    columns={columns}
                    dataSource={data}
                    scroll={{ x: 1180 }}
                    pagination={{
                      current: page,
                      pageSize,
                      total,
                      onChange: (nextPage, nextPageSize) => {
                        void loadData({ page: nextPage, pageSize: nextPageSize })
                      },
                    }}
                  />
                </>
              ),
            },
            {
              key: 'pending',
              label: (
                <Space size={6}>
                  {t('system.employeeProfiles.tabs.pending')}
                  <Badge count={pendingCount} overflowCount={99} />
                </Space>
              ),
              children: <Tabs
                items={[
                  { key: 'sensitive', label: '敏感资料变更', children: <SensitiveChangeReviewPanel refreshPendingCount={loadPendingCount} /> },
                  { key: 'minor-employment', label: '未成年用工合规', children: <MinorEmploymentReviewPage /> },
                ]}
              />,
            },
          ]}
        />
      </Card>

      <Drawer
        width={960}
        destroyOnHidden
        open={editOpen}
        onClose={() => {
          setEditOpen(false)
          setEditingProfile(null)
          form.resetFields()
        }}
        title={
          editingProfile
            ? t('system.employeeProfiles.editTitle', {
                name: editingProfile.displayName || editingProfile.username || editingProfile.userGUID || editingProfile.id,
              })
            : t('system.employeeProfiles.editTitleShort')
        }
        extra={
          <Space>
            <Button onClick={() => setEditOpen(false)}>{t('common.cancel')}</Button>
            <Button type="primary" loading={editLoading} disabled={!canEditProfiles} onClick={() => void handleSubmit()}>
              {t('common.save')}
            </Button>
          </Space>
        }
      >
        {editLoading && !editingProfile ? (
          <Typography.Text type="secondary">{t('system.employeeProfiles.loadingDetail')}</Typography.Text>
        ) : !editingProfile ? (
          <Typography.Text type="danger">{t('system.employeeProfiles.notFound')}</Typography.Text>
        ) : (
          <Form form={form} layout="vertical" preserve={false}>
            <Row gutter={16}>
              <Col xs={24} md={12}>
                <Card size="small" title={t('system.employeeProfiles.sections.account')}>
                  <Row gutter={12}>
                    <Col span={12}>
                      <Form.Item name="username" label={t('system.employeeProfiles.username')}>
                        <Input placeholder={t('system.employeeProfiles.placeholders.username')} />
                      </Form.Item>
                    </Col>
                    <Col span={12}>
                      <Form.Item name="displayName" label={t('system.employeeProfiles.displayName')}>
                        <Input placeholder={t('system.employeeProfiles.placeholders.displayName')} />
                      </Form.Item>
                    </Col>
                    <Col span={12}>
                      <Form.Item name="userGUID" label={t('system.employeeProfiles.userGuid')}>
                        <Input disabled />
                      </Form.Item>
                    </Col>
                    <Col span={12}>
                      <Form.Item name="userId" label={t('system.employeeProfiles.userId')}>
                        <Input disabled />
                      </Form.Item>
                    </Col>
                    <Col span={12}>
                      <Form.Item name="employmentType" label={t('system.employeeProfiles.employmentType')}>
                        <Select allowClear options={employmentTypeOptions} placeholder={t('system.employeeProfiles.placeholders.employmentType')} />
                      </Form.Item>
                    </Col>
                    <Col span={12}>
                      <Form.Item name="gender" label={t('system.employeeProfiles.gender')}>
                        <Select allowClear options={genderOptions} placeholder={t('system.employeeProfiles.placeholders.gender')} />
                      </Form.Item>
                    </Col>
                    <Col span={12}>
                      <Form.Item name="birthday" label={t('system.employeeProfiles.birthday')}>
                        <DatePicker style={{ width: '100%' }} placeholder={t('system.employeeProfiles.placeholders.birthday')} />
                      </Form.Item>
                    </Col>
                    <Col span={12}>
                      <Form.Item name="identityType" label={t('system.employeeProfiles.identityType')}>
                        <Input placeholder={t('system.employeeProfiles.placeholders.identityType')} />
                      </Form.Item>
                    </Col>
                    <Col span={12}>
                      <Form.Item name="identityId" label={t('system.employeeProfiles.identityId')}>
                        <Input placeholder={t('system.employeeProfiles.placeholders.identityId')} />
                      </Form.Item>
                    </Col>
                    <Col span={24}>
                      <Typography.Text type="secondary">
                        {t('system.employeeProfiles.lastUpdated')}: {formatDateTime(editingProfile.updatedAt, i18n.language)}
                      </Typography.Text>
                    </Col>
                  </Row>
                </Card>
              </Col>

              <Col xs={24} md={12}>
                <Card size="small" title={t('system.employeeProfiles.sections.media')}>
                  <Row gutter={12}>
                    <Col span={24}>
                      <Form.Item name="avatarUrl" label={t('system.employeeProfiles.avatarUrl')}>
                        <Input placeholder={t('system.employeeProfiles.placeholders.avatarUrl')} />
                      </Form.Item>
                    </Col>
                    <Col span={24}>
                      <Form.Item name="identityPhotoUrl" label={t('system.employeeProfiles.identityPhotoUrl')}>
                        <Input placeholder={t('system.employeeProfiles.placeholders.identityPhotoUrl')} />
                      </Form.Item>
                    </Col>
                    <Col span={12}>
                      <Space direction="vertical" size={8} style={{ width: '100%' }}>
                        <Typography.Text strong>{t('system.employeeProfiles.avatarPreview')}</Typography.Text>
                        {avatarUrl ? (
                          <Image
                            src={avatarUrl}
                            width={120}
                            height={120}
                            style={{ objectFit: 'cover', borderRadius: 6 }}
                            fallback="data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs="
                          />
                        ) : (
                          <Typography.Text type="secondary">{t('system.employeeProfiles.noImage')}</Typography.Text>
                        )}
                      </Space>
                    </Col>
                    <Col span={12}>
                      <Space direction="vertical" size={8} style={{ width: '100%' }}>
                        <Typography.Text strong>{t('system.employeeProfiles.identityPhotoPreview')}</Typography.Text>
                        {identityPhotoUrl ? (
                          <Image
                            src={identityPhotoUrl}
                            width={120}
                            height={120}
                            style={{ objectFit: 'cover', borderRadius: 6 }}
                            fallback="data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs="
                          />
                        ) : (
                          <Typography.Text type="secondary">{t('system.employeeProfiles.noImage')}</Typography.Text>
                        )}
                      </Space>
                    </Col>
                  </Row>
                </Card>
              </Col>

              <Col xs={24} md={12}>
                <Card size="small" title={t('system.employeeProfiles.sections.banking')} style={{ marginTop: 16 }}>
                  <Row gutter={12}>
                    <Col span={12}>
                      <Form.Item name="bankBsb" label={t('system.employeeProfiles.bankBsb')}>
                        <Input placeholder={t('system.employeeProfiles.placeholders.bankBsb')} />
                      </Form.Item>
                    </Col>
                    <Col span={12}>
                      <Form.Item name="bankAccountNumber" label={t('system.employeeProfiles.bankAccountNumber')}>
                        <Input placeholder={t('system.employeeProfiles.placeholders.bankAccountNumber')} />
                      </Form.Item>
                    </Col>
                    <Col span={24}>
                      <Form.Item name="address" label={t('system.employeeProfiles.address')}>
                        <Input.TextArea rows={3} placeholder={t('system.employeeProfiles.placeholders.address')} />
                      </Form.Item>
                    </Col>
                  </Row>
                </Card>
              </Col>

              <Col xs={24} md={12}>
                <Card size="small" title={t('system.employeeProfiles.sections.superannuation')} style={{ marginTop: 16 }}>
                  <Row gutter={12}>
                    <Col span={24}>
                      <Form.Item name="superannuationCompanyName" label={t('system.employeeProfiles.superannuationCompanyName')}>
                        <Input placeholder={t('system.employeeProfiles.placeholders.superannuationCompanyName')} />
                      </Form.Item>
                    </Col>
                    <Col span={12}>
                      <Form.Item name="superannuationCompanyCode" label={t('system.employeeProfiles.superannuationCompanyCode')}>
                        <Input placeholder={t('system.employeeProfiles.placeholders.superannuationCompanyCode')} />
                      </Form.Item>
                    </Col>
                    <Col span={12}>
                      <Form.Item name="superannuationAccountNumber" label={t('system.employeeProfiles.superannuationAccountNumber')}>
                        <Input placeholder={t('system.employeeProfiles.placeholders.superannuationAccountNumber')} />
                      </Form.Item>
                    </Col>
                  </Row>
                </Card>
              </Col>
            </Row>

            <Card size="small" title={t('system.employeeProfiles.sections.audit')} style={{ marginTop: 16 }}>
              <Row gutter={12}>
                <Col xs={24} md={12}>
                  <Typography.Text>{t('system.employeeProfiles.createdAt')}: {formatDateTime(editingProfile.createdAt, i18n.language)}</Typography.Text>
                </Col>
                <Col xs={24} md={12}>
                  <Typography.Text>{t('system.employeeProfiles.updatedAt')}: {formatDateTime(editingProfile.updatedAt, i18n.language)}</Typography.Text>
                </Col>
                <Col xs={24} md={12} style={{ marginTop: 8 }}>
                  <Typography.Text>{t('system.employeeProfiles.birthday')}: {formatDate(editingProfile.birthday)}</Typography.Text>
                </Col>
                <Col xs={24} md={12} style={{ marginTop: 8 }}>
                  <Typography.Text>{t('system.employeeProfiles.gender')}: {editingProfile.gender ? (genderLabelMap[editingProfile.gender] || editingProfile.gender) : '--'}</Typography.Text>
                </Col>
              </Row>
            </Card>
          </Form>
        )}
      </Drawer>
    </PageContainer>
  )
}
