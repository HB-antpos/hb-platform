import {
  ArrowRightOutlined,
  CopyOutlined,
  EditOutlined,
  LockOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Avatar,
  Badge,
  Button,
  Card,
  DatePicker,
  Drawer,
  Form,
  Image,
  Input,
  Modal,
  Select,
  Space,
  Tabs,
  Tag,
  Tooltip,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { Dayjs } from 'dayjs'
import dayjs from 'dayjs'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import PageContainer from '../../../components/PageContainer'
import {
  getAdminEmployeeProfile,
  getAdminSensitiveChangeRequests,
  getManagedEmployeeProfileFilterOptions,
  getManagedEmployeeProfiles,
  saveAdminEmployeeProfile,
} from '../../../services/employeeProfileService'
import { useAuthStore } from '../../../store/auth'
import type {
  EmployeeEmploymentType,
  EmployeeProfileDetailDto,
  EmployeeProfileFilterOptions,
  EmployeeProfileGender,
  EmployeeProfileQueryDto,
  EmployeeProfileSummaryDto,
} from '../../../types/employeeProfile'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'
import ManagedEmployeeProfileEditor from './ManagedEmployeeProfileEditor'
import SensitiveChangeReviewPanel from './SensitiveChangeReviewPanel'
import MinorEmploymentReviewPage from '../MinorEmploymentReview'
import MinorGuardianLinkPanel from './MinorGuardianLinkPanel'
import { getMinorStaffComplianceList } from '../../../services/minorEmploymentService'
import {
  countChangedFormFields,
  getExpectedSensitiveRevision,
  getProfileCompletion,
  getProfileInitials,
  getStablePaletteIndex,
  maskSensitiveSummary,
  saveAdminProfileWithPendingConfirmation,
  shortenIdentifier,
} from './logic'
import { MeasuredTable } from '../../../components/MeasuredTable'
import './employeeProfiles.css'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import employeeProfilesMessagesEn from './employeeProfilesMessages.en.json'
import employeeProfilesMessagesZh from './employeeProfilesMessages.zh.json'

// 页面级文案随页面代码块懒加载，不进首屏 i18n 包（首屏 gzip 预算很紧，见仓库约定）。
registerPageMessages({ zh: employeeProfilesMessagesZh, en: employeeProfilesMessagesEn })

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

type SectionKey = 'basic' | 'bankSuper' | 'identity'

const SECTION_KEYS: SectionKey[] = ['basic', 'bankSuper', 'identity']

// 「已修改 N 项」只统计管理员可编辑的字段，GUID / 用户 ID 只读不参与。
const PROFILE_FORM_FIELDS = [
  'username',
  'displayName',
  'employmentType',
  'gender',
  'address',
  'avatarUrl',
  'bankBsb',
  'bankAccountNumber',
  'superannuationCompanyName',
  'superannuationCompanyCode',
  'superannuationAccountNumber',
  'birthday',
  'identityType',
  'identityId',
  'identityPhotoUrl',
] as const

const AVATAR_TONES = [
  { background: '#e3edff', color: '#1554c0' },
  { background: '#efe7ff', color: '#5a3fc0' },
  { background: '#e1f5ef', color: '#0b7a6c' },
  { background: '#fff0dd', color: '#a35a00' },
  { background: '#ffe9ec', color: '#c4283c' },
]

const EMPLOYMENT_TAG_COLORS: Record<EmployeeEmploymentType, string> = {
  fullTime: 'blue',
  partTime: 'cyan',
  casual: 'orange',
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

// 列表里日期与时间分两行展示，既保持列宽窄又不丢精度。
function formatDateParts(value?: string) {
  if (!value) {
    return null
  }

  const date = dayjs(value)
  return date.isValid() ? { date: date.format('YYYY-MM-DD'), time: date.format('HH:mm') } : { date: value, time: '' }
}

function getProfileKey(record: Pick<EmployeeProfileSummaryDto, 'id' | 'userGUID' | 'userId' | 'username'>) {
  return record.id || record.userGUID || record.userId || record.username || ''
}

function getAvatarTone(seed: string) {
  return AVATAR_TONES[getStablePaletteIndex(seed, AVATAR_TONES.length)]
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
  // 后台完整编辑器（含银行 / 公积金 / 证件等敏感资料）走 admin 接口：同时要求管理员角色与 EmployeeProfiles.Edit，
  // 管理员天然拥有全部权限，因此只按角色门控。
  // 其余账号（如店长）走 /managed 接口：只能查看 / 修改管理分店员工的非敏感资料，范围由后端强制。
  const access = useAuthStore((state) => state.access)
  const canEditProfiles = access.isAdmin
  const canEditManagedProfiles = access.canEditEmployeeProfiles
  const [searchParams] = useSearchParams()
  const [loading, setLoading] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [storeGuid, setStoreGuid] = useState<string | undefined>(undefined)
  const [roleGuid, setRoleGuid] = useState<string | undefined>(undefined)
  const [filterOptions, setFilterOptions] = useState<EmployeeProfileFilterOptions>({ stores: [], roles: [] })
  const [managedEditingUser, setManagedEditingUser] = useState<EmployeeProfileSummaryDto | null>(null)
  const managedDirtyRef = useRef(0)
  const [data, setData] = useState<EmployeeProfileSummaryDto[]>([])
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [total, setTotal] = useState(0)
  const [hasLoaded, setHasLoaded] = useState(false)
  const [activeTab, setActiveTab] = useState('profiles')
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)
  const desiredListQueryRef = useRef<DesiredEmployeeProfileQuery>({
    keyword: keyword || undefined,
    storeGuid,
    roleGuid,
    page,
    pageSize,
  })
  const [editOpen, setEditOpen] = useState(false)
  const [editLoading, setEditLoading] = useState(false)
  const [editingProfile, setEditingProfile] = useState<EmployeeProfileDetailDto | null>(null)
  const [pendingCount, setPendingCount] = useState(0)
  // 「未成年用工合规」页签红点（未发送 / 链接过期 / 退回修改的人数）与「待审核」子页签（受控，供「去审核」跳转）。
  const [minorNeedsActionCount, setMinorNeedsActionCount] = useState(0)
  const [pendingSubTab, setPendingSubTab] = useState('sensitive')
  const [activeSection, setActiveSection] = useState<SectionKey>('basic')
  const [dirtyCount, setDirtyCount] = useState(0)
  const [form] = Form.useForm<EmployeeProfileFormValues>()
  const initialFormValuesRef = useRef<EmployeeProfileFormValues>({})
  const formBodyRef = useRef<HTMLDivElement | null>(null)
  const sectionRefs = useRef<Partial<Record<SectionKey, HTMLElement | null>>>({})
  // 点击导航触发的程序化滚动期间暂停滚动联动：内容较短时只能滚到底，否则「滚到底选最后一项」会抢走刚点的高亮。
  const jumpLockUntilRef = useRef(0)

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

  const loadData = async (overrides: Partial<DesiredEmployeeProfileQuery> = {}) => {
    if (!mountedRef.current) {
      return
    }

    const query: DesiredEmployeeProfileQuery = {
      keyword: keyword || undefined,
      storeGuid,
      roleGuid,
      page,
      pageSize,
      ...overrides,
    }
    // 分页请求 begin 前保存目标查询，避免保存操作晚完成后退回旧页。
    desiredListQueryRef.current = query

    await runLatestGuardedRequest(listRequestGuardRef.current, () => getManagedEmployeeProfiles(query), {
      onStart: () => setLoading(true),
      onSuccess: (result) => {
        setData(result.items)
        setTotal(result.total)
        setPage(result.page)
        setPageSize(result.pageSize)
        setHasLoaded(true)
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
    // 待审核（敏感资料变更 / 未成年用工合规）只有管理员能查：非管理员调这些 admin 接口只会 403。
    if (!canEditProfiles) {
      return
    }
    try {
      const result = await getAdminSensitiveChangeRequests({ page: 1, pageSize: 1, status: 'Pending' })
      setPendingCount(result.total)
    } catch {
      // 数量标记是辅助信息，列表页已有独立错误态，这里保持主页面可用。
    }
  }

  // 页签内容懒渲染，红点数字要在进入页签前就有：挂载时单独取一次（与「待审核」同一门禁）。
  const loadMinorNeedsActionCount = async () => {
    if (!canEditProfiles) {
      return
    }
    try {
      const result = await getMinorStaffComplianceList({ needsActionOnly: true })
      if (mountedRef.current) {
        setMinorNeedsActionCount(result.needsActionCount)
      }
    } catch {
      // 红点只是辅助信息，失败不打扰主页面；进入页签后会重新加载并给出错误提示。
    }
  }

  useEffect(() => {
    void loadData({ page: 1, pageSize })
    void loadPendingCount()
    void loadMinorNeedsActionCount()
    // 筛选下拉由后端按调用者范围裁剪（店长只有管理分店，且不含上级角色）。
    void getManagedEmployeeProfileFilterOptions()
      .then((options) => {
        if (mountedRef.current) {
          setFilterOptions(options)
        }
      })
      .catch((error) => console.error(error))
  }, [])

  // 从「用户管理」抽屉跳来时带 ?keyword=用户名：页面被 keepAlive 缓存，需要监听参数变化而不只是首次挂载。
  const keywordParam = searchParams.get('keyword')
  useEffect(() => {
    if (keywordParam === null) {
      return
    }
    setKeyword(keywordParam)
    void loadData({ keyword: keywordParam || undefined, page: 1, pageSize })
  }, [keywordParam])

  // 支持 ?tab=minor 直接打开「未成年用工合规」页签（无权限时忽略）。
  const tabParam = searchParams.get('tab')
  useEffect(() => {
    if (tabParam === 'minor' && canEditProfiles) {
      setActiveTab('minor')
    }
  }, [tabParam, canEditProfiles])

  // 抽屉内的分区导航跟随滚动高亮：取「顶边已越过吸顶导航」的最后一个分区；滚到底时强制选中最后一个。
  useEffect(() => {
    if (!editOpen || !editingProfile) {
      return undefined
    }

    const container = formBodyRef.current?.closest('.ant-drawer-body') as HTMLElement | null
    if (!container) {
      return undefined
    }

    const handleScroll = () => {
      if (Date.now() < jumpLockUntilRef.current) {
        return
      }
      const threshold = container.getBoundingClientRect().top + 64
      let current: SectionKey = 'basic'
      for (const key of SECTION_KEYS) {
        const element = sectionRefs.current[key]
        if (element && element.getBoundingClientRect().top <= threshold) {
          current = key
        }
      }
      if (container.scrollTop + container.clientHeight >= container.scrollHeight - 2) {
        current = SECTION_KEYS[SECTION_KEYS.length - 1]
      }
      setActiveSection(current)
    }

    container.addEventListener('scroll', handleScroll, { passive: true })
    return () => container.removeEventListener('scroll', handleScroll)
  }, [editOpen, editingProfile])

  const closeEditDrawer = () => {
    setEditOpen(false)
    setEditingProfile(null)
    setDirtyCount(0)
    form.resetFields()
  }

  // 非管理员（如店长）：打开只含非敏感项的轻量抽屉；关闭前若有未保存修改先确认。
  const closeManagedDrawer = () => {
    if (managedDirtyRef.current > 0) {
      Modal.confirm({
        title: t('common.confirmDiscardTitle', '放弃未保存的修改？'),
        okText: t('common.confirm', '确定'),
        cancelText: t('common.cancel'),
        onOk: () => {
          managedDirtyRef.current = 0
          setManagedEditingUser(null)
        },
      })
      return
    }
    setManagedEditingUser(null)
  }

  // 按身份分流：管理员走完整编辑器（含敏感资料），其他人走 /managed 轻量编辑器。
  const handleOpenRecord = (record: EmployeeProfileSummaryDto) => {
    if (canEditProfiles) {
      void handleEdit(record)
      return
    }
    if (!getProfileKey(record)) {
      message.error(t('system.employeeProfiles.missingRecordId'))
      return
    }
    managedDirtyRef.current = 0
    setManagedEditingUser(record)
  }

  const handleEdit = async (record: EmployeeProfileSummaryDto) => {
    const profileKey = getProfileKey(record)
    if (!profileKey) {
      message.error(t('system.employeeProfiles.missingRecordId'))
      return
    }

    setEditOpen(true)
    setEditLoading(true)
    setEditingProfile(null)
    setActiveSection('basic')
    setDirtyCount(0)
    form.resetFields()

    try {
      const detail = await getAdminEmployeeProfile(profileKey)
      const formValues = mapProfileToFormValues(detail)
      setEditingProfile(detail)
      form.setFieldsValue(formValues)
      // 记录打开时的初始值，底栏据此统计「已修改 N 项」。
      initialFormValuesRef.current = formValues
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
      closeEditDrawer()
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

  const handleFormValuesChange = () => {
    setDirtyCount(countChangedFormFields(initialFormValuesRef.current, form.getFieldsValue(), PROFILE_FORM_FIELDS))
  }

  const handleJumpToSection = (key: SectionKey) => {
    setActiveSection(key)
    jumpLockUntilRef.current = Date.now() + 900
    // 尊重系统「减少动态效果」设置：开启时直接跳转不做平滑滚动。
    const reduceMotion = typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches
    sectionRefs.current[key]?.scrollIntoView({ block: 'start', behavior: reduceMotion ? 'auto' : 'smooth' })
  }

  const handleCopyIdentifier = async (value: string) => {
    try {
      await navigator.clipboard.writeText(value)
      message.success(t('system.employeeProfiles.drawer.copied'))
    } catch {
      message.error(t('system.employeeProfiles.drawer.copyFailed'))
    }
  }

  const renderEmploymentTag = (value?: EmployeeEmploymentType) =>
    value ? (
      <Tag bordered={false} color={EMPLOYMENT_TAG_COLORS[value]} style={{ marginInlineEnd: 0 }}>
        {employmentTypeLabelMap[value] || value}
      </Tag>
    ) : null

  const columns: ColumnsType<EmployeeProfileSummaryDto> = [
    {
      title: t('system.employeeProfiles.employee'),
      key: 'employee',
      width: 260,
      render: (_, record) => {
        const displayName = record.displayName || record.username || '--'
        const tone = getAvatarTone(getProfileKey(record) || displayName)
        return (
          <div className="sys-emp-who">
            <Avatar size={34} src={record.avatarUrl || undefined} style={{ ...tone, flex: 'none', fontWeight: 600 }}>
              {getProfileInitials(record.displayName, record.username)}
            </Avatar>
            <div className="sys-emp-who-text">
              <span className="sys-emp-name">{displayName}</span>
              {record.displayName && record.username ? <span className="sys-emp-sub">@{record.username}</span> : null}
            </div>
          </div>
        )
      },
    },
    {
      title: t('system.employeeProfiles.columns.store'),
      key: 'stores',
      width: 170,
      render: (_, record) => {
        const names = record.storeNames ?? []
        if (names.length === 0) {
          return <span className="sys-emp-faint">--</span>
        }
        // 多分店最多显示 2 个，其余折叠成 +N，完整名单放 Tooltip。
        const shown = names.slice(0, 2)
        const hidden = names.slice(2)
        return (
          <Space size={[4, 4]} wrap>
            {shown.map((name) => (
              <Tag key={name} bordered={false} color="blue" style={{ marginInlineEnd: 0 }}>
                {name}
              </Tag>
            ))}
            {hidden.length > 0 ? (
              <Tooltip title={hidden.join(' · ')}>
                <Tag bordered={false} style={{ marginInlineEnd: 0 }}>
                  +{hidden.length}
                </Tag>
              </Tooltip>
            ) : null}
          </Space>
        )
      },
    },
    {
      title: t('system.employeeProfiles.columns.role'),
      key: 'roles',
      width: 130,
      render: (_, record) => {
        const names = record.roleNames ?? []
        if (names.length === 0) {
          return <span className="sys-emp-faint">--</span>
        }
        return (
          <Space size={[4, 4]} wrap>
            {names.map((name) => (
              <Tag key={name} bordered={false} style={{ marginInlineEnd: 0 }}>
                {name}
              </Tag>
            ))}
          </Space>
        )
      },
    },
    {
      title: t('system.employeeProfiles.employmentType'),
      dataIndex: 'employmentType',
      width: 100,
      render: (value: EmployeeEmploymentType | undefined) =>
        renderEmploymentTag(value) ?? <span className="sys-emp-faint">{t('system.employeeProfiles.notSet')}</span>,
    },
    {
      title: t('system.employeeProfiles.bankAccount'),
      key: 'bankAccount',
      width: 170,
      render: (_, record) => {
        // 未建档用户没有任何资料，用淡色占位；已建档但没填才用琥珀色提醒。
        if (record.hasProfile === false) {
          return <span className="sys-emp-faint">--</span>
        }
        if (!record.bankBsb && !record.bankAccountNumber) {
          return <span className="sys-emp-missing">{t('system.employeeProfiles.notFilled')}</span>
        }
        return (
          <div className="sys-emp-two">
            <span className="sys-emp-mono sys-emp-strong">{record.bankBsb || '--'}</span>
            <span className="sys-emp-mono sys-emp-sub">{maskSensitiveSummary(record.bankAccountNumber)}</span>
          </div>
        )
      },
    },
    {
      title: t('system.employeeProfiles.superannuation'),
      key: 'superannuation',
      width: 230,
      render: (_, record) => {
        if (record.hasProfile === false) {
          return <span className="sys-emp-faint">--</span>
        }
        if (!record.superannuationCompanyName && !record.superannuationAccountNumber) {
          return <span className="sys-emp-missing">{t('system.employeeProfiles.notFilled')}</span>
        }
        return (
          <div className="sys-emp-two">
            <span className="sys-emp-strong">{record.superannuationCompanyName || '--'}</span>
            <span className="sys-emp-sub">
              {record.superannuationCompanyCode ? <span className="sys-emp-mono">{record.superannuationCompanyCode} · </span> : null}
              <span className="sys-emp-mono">{maskSensitiveSummary(record.superannuationAccountNumber)}</span>
            </span>
          </div>
        )
      },
    },
    {
      title: t('system.employeeProfiles.completion.title'),
      key: 'completion',
      width: 130,
      render: (_, record) => {
        const completion = getProfileCompletion(record)
        if (completion.status === 'noProfile') {
          return <span className="sys-emp-status sys-emp-status-none">{t('system.employeeProfiles.completion.noProfile')}</span>
        }
        if (completion.status === 'complete') {
          return <span className="sys-emp-status sys-emp-status-complete">{t('system.employeeProfiles.completion.complete')}</span>
        }
        return (
          <div className="sys-emp-two">
            <span className="sys-emp-status sys-emp-status-warn">{t('system.employeeProfiles.completion.incomplete')}</span>
            <span className="sys-emp-sub">
              {completion.missing
                .map((part) => t(part === 'bank' ? 'system.employeeProfiles.completion.missingBank' : 'system.employeeProfiles.completion.missingSuper'))
                .join(' · ')}
            </span>
          </div>
        )
      },
    },
    {
      title: t('column.updateTime'),
      dataIndex: 'updatedAt',
      width: 120,
      render: (value: string | undefined) => {
        const parts = formatDateParts(value)
        return parts ? (
          <div className="sys-emp-datetime">
            <span>{parts.date}</span>
            {parts.time ? <span className="sys-emp-sub">{parts.time}</span> : null}
          </div>
        ) : (
          <span className="sys-emp-faint">--</span>
        )
      },
    },
    {
      title: t('column.action'),
      key: 'action',
      width: 90,
      fixed: 'right',
      align: 'right',
      render: (_, record) => (
        // 管理员 = 完整编辑；其余账号只有 EditManagedStore 才是「编辑」，否则是只读「查看」。
        <Button type="link" icon={<EditOutlined />} onClick={() => handleOpenRecord(record)}>
          {canEditProfiles || canEditManagedProfiles ? t('common.edit') : t('common.view', '查看')}
        </Button>
      ),
    },
  ]

  const editingDisplayName = editingProfile
    ? editingProfile.displayName || editingProfile.username || editingProfile.userGUID || editingProfile.id || ''
    : ''
  const editingIdentifier = editingProfile ? editingProfile.userGUID || editingProfile.userId || editingProfile.id || '' : ''
  // 头像随「头像链接」输入实时预览；尚未触碰表单时用详情里的已保存值。
  const headerAvatarUrl = avatarUrl !== undefined ? avatarUrl : editingProfile?.avatarUrl
  const sectionTitles: Record<SectionKey, string> = {
    basic: t('system.employeeProfiles.drawer.nav.basic'),
    bankSuper: t('system.employeeProfiles.drawer.nav.bankSuper'),
    identity: t('system.employeeProfiles.drawer.nav.identity'),
  }
  const sensitiveBadge = (
    <span className="sys-emp-sensitive">
      <LockOutlined style={{ fontSize: 11 }} />
      {t('system.employeeProfiles.drawer.sensitive')}
    </span>
  )

  return (
    <PageContainer
      compact
      title={t('system.employeeProfiles.pageTitle')}
      subtitle={hasLoaded ? t('system.employeeProfiles.totalCount', { count: total }) : undefined}
      extra={
        canEditProfiles && pendingCount > 0 ? (
          <Button className="sys-emp-pending-entry" icon={<LockOutlined />} onClick={() => setActiveTab('pending')}>
            {t('system.employeeProfiles.pendingEntry', { count: pendingCount })}
            <ArrowRightOutlined />
          </Button>
        ) : undefined
      }
    >
      <Card>
        <Tabs
          activeKey={activeTab}
          onChange={setActiveTab}
          items={[
            {
              key: 'profiles',
              label: t('system.employeeProfiles.tabs.profiles'),
              children: (
                <>
                  <div className="sys-emp-toolbar">
                    <Input
                      allowClear
                      prefix={<SearchOutlined />}
                      placeholder={`${t('system.employeeProfiles.searchPlaceholder')} · ${t('common.listToolbar.searchEnterHint', '回车查询')}`}
                      style={{ width: 340 }}
                      value={keyword}
                      onChange={(event) => setKeyword(event.target.value)}
                      onPressEnter={() => void loadData({ page: 1, pageSize })}
                    />
                    <Select
                      allowClear
                      showSearch
                      optionFilterProp="label"
                      style={{ width: 200 }}
                      placeholder={t('system.employeeProfiles.filters.allStores')}
                      aria-label={t('system.employeeProfiles.filters.store')}
                      value={storeGuid}
                      options={filterOptions.stores.map((store) => ({ value: store.storeGuid, label: store.storeName }))}
                      onChange={(value) => {
                        const next = value ?? undefined
                        setStoreGuid(next)
                        void loadData({ storeGuid: next, page: 1, pageSize })
                      }}
                    />
                    <Select
                      allowClear
                      showSearch
                      optionFilterProp="label"
                      style={{ width: 160 }}
                      placeholder={t('system.employeeProfiles.filters.allRoles')}
                      aria-label={t('system.employeeProfiles.filters.role')}
                      value={roleGuid}
                      options={filterOptions.roles.map((role) => ({ value: role.roleGuid, label: role.roleName }))}
                      onChange={(value) => {
                        const next = value ?? undefined
                        setRoleGuid(next)
                        void loadData({ roleGuid: next, page: 1, pageSize })
                      }}
                    />
                    <Button type="primary" onClick={() => void loadData({ page: 1, pageSize })}>
                      {t('common.query')}
                    </Button>
                    <Tooltip title={t('common.refresh')}>
                      <Button
                        icon={<ReloadOutlined />}
                        aria-label={t('common.refresh')}
                        onClick={() => void refreshDesiredList()}
                      />
                    </Tooltip>
                    <span className="sys-emp-toolbar-spacer" />
                    {canEditProfiles ? (
                      <span className="sys-emp-mask-hint">
                        <LockOutlined />
                        {t('system.employeeProfiles.maskedHint')}
                      </span>
                    ) : null}
                  </div>

                  <MeasuredTable metricId="system.employee-profiles.table-1"
                    className="sys-emp-table"
                    rowKey={(record) => getProfileKey(record)}
                    loading={loading}
                    columns={columns}
                    dataSource={data}
                    scroll={{ x: 1400 }}
                    rowClassName={() => 'sys-emp-row-clickable'}
                    onRow={(record) => ({
                      // 整行可点开编辑；行内按钮、头像预览等自带交互的元素不触发，避免重复打开。
                      onClick: (event) => {
                        if ((event.target as HTMLElement).closest('button, a, .ant-image')) {
                          return
                        }
                        handleOpenRecord(record)
                      },
                    })}
                    pagination={{
                      current: page,
                      pageSize,
                      total,
                      showTotal: (count) => t('common.total', { count }),
                      onChange: (nextPage, nextPageSize) => {
                        void loadData({ page: nextPage, pageSize: nextPageSize })
                      },
                    }}
                  />
                </>
              ),
            },
            // 未成年用工合规：给未满 18 岁员工的家长发送签字链接；与「待审核」同一门禁（店长在手机「人员管理」里发送）。
            ...(canEditProfiles ? [{
              key: 'minor',
              label: (
                <Space size={6}>
                  {t('system.employeeProfiles.minorTab')}
                  <Badge count={minorNeedsActionCount} overflowCount={99} />
                </Space>
              ),
              children: (
                <MinorGuardianLinkPanel
                  stores={filterOptions.stores}
                  onNeedsActionCountChange={setMinorNeedsActionCount}
                  onGoReview={() => {
                    setPendingSubTab('minor-employment')
                    setActiveTab('pending')
                  }}
                />
              ),
            }] : []),
            // 待审核标签读的是管理员专用接口，非管理员不显示（店长的敏感资料审核在手机端「档案审核」）。
            ...(canEditProfiles ? [{
              key: 'pending',
              label: (
                <Space size={6}>
                  {t('system.employeeProfiles.tabs.pending')}
                  <Badge count={pendingCount} overflowCount={99} />
                </Space>
              ),
              children: <Tabs
                activeKey={pendingSubTab}
                onChange={setPendingSubTab}
                items={[
                  { key: 'sensitive', label: '敏感资料变更', children: <SensitiveChangeReviewPanel refreshPendingCount={loadPendingCount} /> },
                  { key: 'minor-employment', label: '未成年用工合规', children: <MinorEmploymentReviewPage /> },
                ]}
              />,
            }] : []),
          ]}
        />
      </Card>

      <Drawer
        width={640}
        destroyOnHidden
        rootClassName="sys-emp-drawer"
        open={managedEditingUser !== null}
        onClose={closeManagedDrawer}
        closable={{ placement: 'end' }}
        title={
          managedEditingUser ? (
            <div className="sys-emp-drawer-head">
              <Avatar
                size={46}
                src={managedEditingUser.avatarUrl || undefined}
                style={{ ...getAvatarTone(getProfileKey(managedEditingUser)), flex: 'none', fontWeight: 600 }}
              >
                {getProfileInitials(managedEditingUser.displayName, managedEditingUser.username)}
              </Avatar>
              <div className="sys-emp-drawer-head-main">
                <div className="sys-emp-drawer-title">
                  <span>{managedEditingUser.displayName || managedEditingUser.username}</span>
                </div>
                <div className="sys-emp-drawer-meta">
                  {managedEditingUser.username ? <span>@{managedEditingUser.username}</span> : null}
                </div>
              </div>
            </div>
          ) : (
            t('system.employeeProfiles.managed.drawerTitle')
          )
        }
      >
        {managedEditingUser ? (
          <ManagedEmployeeProfileEditor
            key={getProfileKey(managedEditingUser)}
            userGuid={managedEditingUser.userGUID || getProfileKey(managedEditingUser)}
            canEdit={canEditManagedProfiles}
            onDirtyChange={(count) => {
              managedDirtyRef.current = count
            }}
            onSaved={() => {
              void refreshDesiredList()
            }}
          />
        ) : null}
      </Drawer>

      <Drawer
        width={760}
        destroyOnHidden
        rootClassName="sys-emp-drawer"
        open={editOpen}
        onClose={closeEditDrawer}
        closable={{ placement: 'end' }}
        title={
          editingProfile ? (
            <div className="sys-emp-drawer-head">
              <Avatar
                size={46}
                src={headerAvatarUrl || undefined}
                style={{ ...getAvatarTone(editingIdentifier || editingDisplayName), flex: 'none', fontWeight: 600 }}
              >
                {getProfileInitials(editingProfile.displayName, editingProfile.username)}
              </Avatar>
              <div className="sys-emp-drawer-head-main">
                <div className="sys-emp-drawer-title">
                  <span>{editingDisplayName}</span>
                  {renderEmploymentTag(editingProfile.employmentType)}
                </div>
                <div className="sys-emp-drawer-meta">
                  {editingProfile.username ? <span>@{editingProfile.username}</span> : null}
                  {editingIdentifier ? (
                    <span>
                      <span className="sys-emp-mono">ID {shortenIdentifier(editingIdentifier)}</span>
                      <Tooltip title={t('system.employeeProfiles.drawer.copyId')}>
                        <Button
                          type="text"
                          size="small"
                          className="sys-emp-copy-btn"
                          icon={<CopyOutlined />}
                          aria-label={t('system.employeeProfiles.drawer.copyId')}
                          onClick={() => void handleCopyIdentifier(editingIdentifier)}
                        />
                      </Tooltip>
                    </span>
                  ) : null}
                  {editingProfile.createdAt ? (
                    <span>{t('system.employeeProfiles.drawer.createdOn', { date: formatDateTime(editingProfile.createdAt, i18n.language).slice(0, 10) })}</span>
                  ) : null}
                  <span>{t('system.employeeProfiles.drawer.updatedOn', { date: formatDateTime(editingProfile.updatedAt, i18n.language) })}</span>
                </div>
              </div>
            </div>
          ) : (
            t('system.employeeProfiles.editTitleShort')
          )
        }
        footer={
          editingProfile ? (
            <div className="sys-emp-drawer-footer">
              {dirtyCount > 0 ? <span className="sys-emp-dirty">{t('system.employeeProfiles.drawer.dirty', { count: dirtyCount })}</span> : null}
              <span className="sys-emp-drawer-footer-spacer" />
              <Button onClick={closeEditDrawer}>{t('common.cancel')}</Button>
              <Button type="primary" loading={editLoading} disabled={!canEditProfiles} onClick={() => void handleSubmit()}>
                {t('common.save')}
              </Button>
            </div>
          ) : null
        }
      >
        {editLoading && !editingProfile ? (
          <span className="sys-emp-sub">{t('system.employeeProfiles.loadingDetail')}</span>
        ) : !editingProfile ? (
          <span style={{ color: '#d4380d' }}>{t('system.employeeProfiles.notFound')}</span>
        ) : (
          <div ref={formBodyRef}>
            <nav className="sys-emp-anchor" aria-label={t('system.employeeProfiles.editTitleShort')}>
              {SECTION_KEYS.map((key) => (
                <button
                  key={key}
                  type="button"
                  aria-current={activeSection === key ? 'true' : undefined}
                  onClick={() => handleJumpToSection(key)}
                >
                  {sectionTitles[key]}
                </button>
              ))}
            </nav>

            <Form
              form={form}
              layout="vertical"
              preserve={false}
              requiredMark={false}
              onValuesChange={handleFormValuesChange}
            >
              <section className="sys-emp-section" ref={(element) => { sectionRefs.current.basic = element }}>
                <h4 className="sys-emp-section-title">{sectionTitles.basic}</h4>
                <div className="sys-emp-grid-2">
                  <Form.Item name="username" label={t('system.employeeProfiles.username')}>
                    <Input placeholder={t('system.employeeProfiles.placeholders.username')} />
                  </Form.Item>
                  <Form.Item name="displayName" label={t('system.employeeProfiles.displayName')}>
                    <Input placeholder={t('system.employeeProfiles.placeholders.displayName')} />
                  </Form.Item>
                  <Form.Item name="employmentType" label={t('system.employeeProfiles.employmentType')}>
                    <Select allowClear options={employmentTypeOptions} placeholder={t('system.employeeProfiles.placeholders.employmentType')} />
                  </Form.Item>
                  <Form.Item name="gender" label={t('system.employeeProfiles.gender')}>
                    <Select allowClear options={genderOptions} placeholder={t('system.employeeProfiles.placeholders.gender')} />
                  </Form.Item>
                  <Form.Item className="sys-emp-span-all" name="address" label={t('system.employeeProfiles.address')}>
                    <Input.TextArea autoSize={{ minRows: 2, maxRows: 4 }} placeholder={t('system.employeeProfiles.placeholders.address')} />
                  </Form.Item>
                  <Form.Item className="sys-emp-span-all" name="avatarUrl" label={t('system.employeeProfiles.avatarUrl')}>
                    <Input placeholder={t('system.employeeProfiles.placeholders.avatarUrl')} />
                  </Form.Item>
                </div>
              </section>

              <section className="sys-emp-section" ref={(element) => { sectionRefs.current.bankSuper = element }}>
                <h4 className="sys-emp-section-title">
                  {sectionTitles.bankSuper}
                  {sensitiveBadge}
                </h4>
                <div className="sys-emp-grid-2">
                  <Form.Item name="bankBsb" label={t('system.employeeProfiles.bankBsb')}>
                    <Input className="sys-emp-mono" placeholder={t('system.employeeProfiles.placeholders.bankBsb')} />
                  </Form.Item>
                  <Form.Item name="bankAccountNumber" label={t('system.employeeProfiles.bankAccountNumber')}>
                    <Input className="sys-emp-mono" placeholder={t('system.employeeProfiles.placeholders.bankAccountNumber')} />
                  </Form.Item>
                  <Form.Item className="sys-emp-span-all" name="superannuationCompanyName" label={t('system.employeeProfiles.superannuationCompanyName')}>
                    <Input placeholder={t('system.employeeProfiles.placeholders.superannuationCompanyName')} />
                  </Form.Item>
                  <Form.Item name="superannuationCompanyCode" label={t('system.employeeProfiles.superannuationCompanyCode')}>
                    <Input className="sys-emp-mono" placeholder={t('system.employeeProfiles.placeholders.superannuationCompanyCode')} />
                  </Form.Item>
                  <Form.Item name="superannuationAccountNumber" label={t('system.employeeProfiles.superannuationAccountNumber')}>
                    <Input className="sys-emp-mono" placeholder={t('system.employeeProfiles.placeholders.superannuationAccountNumber')} />
                  </Form.Item>
                </div>
              </section>

              <section className="sys-emp-section" ref={(element) => { sectionRefs.current.identity = element }}>
                <h4 className="sys-emp-section-title">
                  {sectionTitles.identity}
                  {sensitiveBadge}
                </h4>
                <div className="sys-emp-grid-3">
                  <Form.Item name="birthday" label={t('system.employeeProfiles.birthday')}>
                    <DatePicker style={{ width: '100%' }} placeholder={t('system.employeeProfiles.placeholders.birthday')} />
                  </Form.Item>
                  <Form.Item name="identityType" label={t('system.employeeProfiles.identityType')}>
                    <Input placeholder={t('system.employeeProfiles.placeholders.identityType')} />
                  </Form.Item>
                  <Form.Item name="identityId" label={t('system.employeeProfiles.identityId')}>
                    <Input className="sys-emp-mono" placeholder={t('system.employeeProfiles.placeholders.identityId')} />
                  </Form.Item>
                  <Form.Item className="sys-emp-span-2" name="identityPhotoUrl" label={t('system.employeeProfiles.identityPhotoUrl')}>
                    <Input className="sys-emp-mono" placeholder={t('system.employeeProfiles.placeholders.identityPhotoUrl')} />
                  </Form.Item>
                  <Form.Item label={t('system.employeeProfiles.identityPhotoPreview')}>
                    <div className="sys-emp-photo-preview">
                      {identityPhotoUrl ? (
                        <Image
                          src={identityPhotoUrl}
                          height={56}
                          style={{ objectFit: 'cover' }}
                          fallback="data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs="
                        />
                      ) : (
                        t('system.employeeProfiles.noImage')
                      )}
                    </div>
                  </Form.Item>
                </div>
              </section>

              <Alert
                type="info"
                showIcon
                message={t('system.employeeProfiles.drawer.sensitiveNotice')}
                style={{ margin: '4px 0 8px' }}
              />
            </Form>
          </div>
        )}
      </Drawer>
    </PageContainer>
  )
}
