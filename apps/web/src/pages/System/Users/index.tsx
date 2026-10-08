import {
  CheckCircleOutlined,
  CloseOutlined,
  DollarOutlined,
  EditOutlined,
  EyeOutlined,
  HistoryOutlined,
  LockOutlined,
  MoreOutlined,
  PlusOutlined,
  QrcodeOutlined,
  ReloadOutlined,
  SafetyCertificateOutlined,
  SaveOutlined,
  SearchOutlined,
  ShopOutlined,
  StopOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Checkbox,
  Descriptions,
  Drawer,
  Dropdown,
  Empty,
  Form,
  Input,
  List,
  Modal,
  Segmented,
  Select,
  Skeleton,
  Space,
  Spin,
  Switch,
  Tabs,
  Tag,
  Tooltip,
  Transfer,
  Tree,
  Typography,
  message,
} from 'antd'
import type { MenuProps, TabsProps } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { DataNode } from 'antd/es/tree'
import dayjs from 'dayjs'
import type { Dispatch, Key, ReactNode, SetStateAction } from 'react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { HasPermission } from '../../../components/Access'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { P } from '../../../types/permissions'
import {
  assignRolesToUser,
  assignStoresToUser,
  assignPermissionsToUser,
  createUser,
  deleteUserStorePosTerminalPermissions,
  getUserByGuid,
  getUserAccessPermissions,
  getUserLoginRecords,
  getUserRoles,
  getUserStorePosTerminalPermissions,
  getUserStores,
  getUsers,
  updateUser,
  updateUserPassword,
  updateUserStorePosTerminalPermissions,
} from '../../../services/userService'
import { getActiveRoles } from '../../../services/roleService'
import { getStores } from '../../../services/storeService'
import type { CreateUserDto, UpdateUserDto, UserDetailDto, UserDto, UserLoginRecordDto, UserPermissionStateDto, UserStoreDto, UserStorePosTerminalPermissionsResponse } from '../../../types/user'
import type { RoleOptionDto, PermissionCategoryDto } from '../../../types/role'
import type { StoreDto } from '../../../types/store'
import { getRoleColor } from '../../../utils/userTableColors'
import { useAuthStore } from '../../../store/auth'
import {
  areRoleGuidsAllowedForScopedManager,
  buildScopedStoreAssignments,
  filterRoleOptionsForScopedManager,
  filterStoresForManager,
  filterUsersVisibleToScopedManager,
  getManagedStores,
  getScopedStoreGuidsForQuery,
  hasForbiddenRoleForScopedManager,
  isScopedStoreManager,
  isStoreVisibleToManager,
  mergeUsersByGuid,
} from './userScope'
import {
  arePermissionSetsEqual,
  buildGrantedPosPermissionCodes,
  buildDirectPermissionPayload,
  buildEffectivePermissionCodes,
  buildPosPermissionSections,
  buildPermissionSourceMap,
  canMutateLoadedAssignment,
  getEditablePosPermissionCodes,
  getPosPermissionGroupSelectionState,
  isCurrentPosPermissionRequest,
  isInheritedPosPermissionMode,
  mergeVisibleDirectPermissionSelection,
  setPosPermissionGroupSelection,
  shouldEnablePosPermissionSave,
  splitPermissionCategoriesByPlatform,
} from './userPermissions'
import type { AssignmentLoadStatus, PosPermissionRequestTarget } from './userPermissions'
import { getCreateUserErrorFeedback } from './createUserFeedback'
import { formatUserLocalDateTime, parseUserUtcTimestamp } from './time'
import {
  STALE_LOGIN_DAYS,
  USER_BATCH_SELECTION_LIMIT,
  describeLastLogin,
  diffAssignmentKeys,
  diffStoreAssignment,
  getUserDisplayName,
  getUserInitial,
  getUserSecondaryLine,
  mergeSelectedUsers,
  splitVisibleStores,
  toIsActiveQuery,
} from './usersPageLogic'
import type { UserStatusFilter } from './usersPageLogic'
import {
  DEFAULT_SYSTEM_LIST_PAGE_SIZE,
  createLatestRequestGuard,
  resolveSystemListPagination,
  runLatestGuardedRequest,
} from '../listPagination'
import { MeasuredTable } from '../../../components/MeasuredTable'
import UserMobileMenuPermissionManager from './UserMobileMenuPermissionManager'
import UserCashierBarcodeModal from './UserCashierBarcodeModal'
import UserBatchActions from './UserBatchActions'
import { NeutralChip, PendingChangesBar, RoleChip, StatusDot } from '../accessAdminUi'
import usersPageMessagesEn from './usersPageMessages.en.json'
import usersPageMessagesZh from './usersPageMessages.zh.json'
import './usersPage.css'

registerPageMessages({ zh: usersPageMessagesZh, en: usersPageMessagesEn })

type PermissionPlatform = 'web' | 'pos'

/** 统一用户抽屉的标签页；登录记录与收银权限也在抽屉内，不再单开弹窗。 */
type UserDrawerTab = 'info' | 'assignments' | 'permissions' | 'mobile-menu' | 'login-records' | 'pos'

type UserRowAction = 'login-records' | 'pos' | 'cashier-qr' | 'reset-password' | 'toggle-active'

export default function SystemUsersPage() {
  const { t } = useTranslation()
  const currentUser = useAuthStore((state) => state.currentUser)
  const access = useAuthStore((state) => state.access)
  const refreshCurrentUserSilently = useAuthStore((state) => state.refreshCurrentUserSilently)
  const [loading, setLoading] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [data, setData] = useState<UserDto[]>([])
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_SYSTEM_LIST_PAGE_SIZE)
  const [total, setTotal] = useState(0)
  const mainListRequestGuardRef = useRef(createLatestRequestGuard())
  const loginRecordsRequestGuardRef = useRef(createLatestRequestGuard())
  const editUserRequestGuardRef = useRef(createLatestRequestGuard())
  const editSessionGuardRef = useRef(createLatestRequestGuard())
  const currentEditSessionIdRef = useRef<number | null>(null)
  const roleRequestGuardRef = useRef(createLatestRequestGuard())
  const storeRequestGuardRef = useRef(createLatestRequestGuard())
  const permissionRequestGuardRef = useRef(createLatestRequestGuard())
  const permissionSaveGuardRef = useRef(createLatestRequestGuard())
  const editingUserGuidRef = useRef<string | null>(null)

  const [selectedStoreGuid, setSelectedStoreGuid] = useState<string | undefined>(undefined)
  const [selectedRoleGuid, setSelectedRoleGuid] = useState<string | undefined>(undefined)
  const [statusFilter, setStatusFilter] = useState<UserStatusFilter>('all')
  // 批量操作的勾选：跨页保留，筛选条件或关键字变化时清空，避免对看不见的账号误操作。
  const [selectedUsers, setSelectedUsers] = useState<UserDto[]>([])

  const [sortBy, setSortBy] = useState<string | undefined>(undefined)
  const [sortOrder, setSortOrder] = useState<'ascend' | 'descend' | null>(null)

  const [storeOptions, setStoreOptions] = useState<{ label: string; value: string }[]>([])
  const [roleOptions, setRoleOptions] = useState<{ label: string; value: string; roleName: string }[]>([])

  const [cashierBarcodeUser, setCashierBarcodeUser] = useState<UserDto | null>(null)
  const [detailOpen, setDetailOpen] = useState(false)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailUser, setDetailUser] = useState<UserDetailDto | null>(null)
  const [detailStores, setDetailStores] = useState<UserStoreDto[]>([])

  // 抽屉打开时对应的列表行；登录记录、收银权限等标签页按它懒加载。
  const [drawerUser, setDrawerUser] = useState<UserDto | null>(null)

  const [loginRecordsLoading, setLoginRecordsLoading] = useState(false)
  const [loginRecordsUser, setLoginRecordsUser] = useState<UserDto | null>(null)
  const [loginRecords, setLoginRecords] = useState<UserLoginRecordDto[]>([])
  const [loginRecordsPage, setLoginRecordsPage] = useState(1)
  const [loginRecordsPageSize, setLoginRecordsPageSize] = useState(10)
  const [loginRecordsTotal, setLoginRecordsTotal] = useState(0)

  const [editOpen, setEditOpen] = useState(false)
  const [editLoading, setEditLoading] = useState(false)
  const [editingUser, setEditingUser] = useState<UserDetailDto | null>(null)
  const [editTab, setEditTab] = useState<UserDrawerTab>('info')
  const [form] = Form.useForm<UpdateUserDto>()

  const [allRoles, setAllRoles] = useState<RoleOptionDto[]>([])
  const [roleTargetKeys, setRoleTargetKeys] = useState<string[]>([])
  // 已保存的角色/分店基线：与草稿对比得出「未保存的更改」，只在读取成功或写入成功后更新。
  const [roleBaselineKeys, setRoleBaselineKeys] = useState<string[]>([])
  const [roleLoadStatus, setRoleLoadStatus] = useState<AssignmentLoadStatus>('idle')
  const roleLoading = roleLoadStatus === 'loading'
  const [roleSaving, setRoleSaving] = useState(false)

  const [allStores, setAllStores] = useState<StoreDto[]>([])
  const [storeTargetKeys, setStoreTargetKeys] = useState<string[]>([])
  const [storeManageableKeys, setStoreManageableKeys] = useState<string[]>([])
  const [storeBaselineKeys, setStoreBaselineKeys] = useState<string[]>([])
  const [storeManageableBaselineKeys, setStoreManageableBaselineKeys] = useState<string[]>([])
  const [storeLoadStatus, setStoreLoadStatus] = useState<AssignmentLoadStatus>('idle')
  const [addStoreSelectOpen, setAddStoreSelectOpen] = useState(false)
  const [addStoreSearch, setAddStoreSearch] = useState('')
  const storeLoading = storeLoadStatus === 'loading'
  const [storeSaving, setStoreSaving] = useState(false)

  const [permCategories, setPermCategories] = useState<PermissionCategoryDto[]>([])
  const [permissionState, setPermissionState] = useState<UserPermissionStateDto | null>(null)
  const [directPermKeys, setDirectPermKeys] = useState<string[]>([])
  const [originalDirectPermKeys, setOriginalDirectPermKeys] = useState<string[]>([])
  const [permLoading, setPermLoading] = useState(false)
  const [permSaving, setPermSaving] = useState(false)
  const [permLoadError, setPermLoadError] = useState<string | null>(null)
  const [permissionPlatform, setPermissionPlatform] = useState<PermissionPlatform>('web')

  const [posPermissionUser, setPosPermissionUser] = useState<UserDto | null>(null)
  const [posPermissionStores, setPosPermissionStores] = useState<UserStoreDto[]>([])
  const [selectedPosPermissionStoreGuid, setSelectedPosPermissionStoreGuid] = useState<string>()
  const [posPermissionState, setPosPermissionState] = useState<UserStorePosTerminalPermissionsResponse | null>(null)
  const [selectedPosPermissionCodes, setSelectedPosPermissionCodes] = useState<string[]>([])
  const [originalPosPermissionCodes, setOriginalPosPermissionCodes] = useState<string[]>([])
  const [posPermissionStoresLoading, setPosPermissionStoresLoading] = useState(false)
  const [posPermissionLoading, setPosPermissionLoading] = useState(false)
  const [posPermissionSaving, setPosPermissionSaving] = useState(false)
  const [posPermissionRestoring, setPosPermissionRestoring] = useState(false)
  const [posPermissionError, setPosPermissionError] = useState<string | null>(null)
  const posPermissionRequestRef = useRef<PosPermissionRequestTarget | null>(null)

  const [resetPwdLoading, setResetPwdLoading] = useState(false)
  const [resetPwdOpen, setResetPwdOpen] = useState(false)
  // 重置密码可从抽屉或列表「更多」菜单发起，目标用户单独记录，不依赖抽屉是否已加载完。
  const [resetPwdTarget, setResetPwdTarget] = useState<Pick<UserDto, 'userGUID' | 'username'> | null>(null)
  const [resetPwdForm] = Form.useForm<{ newPassword: string }>()

  const [createOpen, setCreateOpen] = useState(false)
  const [createLoading, setCreateLoading] = useState(false)
  const [createFeedback, setCreateFeedback] = useState<ReturnType<typeof getCreateUserErrorFeedback> | null>(null)
  const [createTab, setCreateTab] = useState('info')
  const [createForm] = Form.useForm<CreateUserDto & { confirmPassword: string }>()

  const [createRoleTargetKeys, setCreateRoleTargetKeys] = useState<string[]>([])
  const [createRoleLoading, setCreateRoleLoading] = useState(false)

  const [createStoreTargetKeys, setCreateStoreTargetKeys] = useState<string[]>([])
  const [createStoreManageableKeys, setCreateStoreManageableKeys] = useState<string[]>([])
  const [createStoreLoading, setCreateStoreLoading] = useState(false)

  const clearCreateFormState = () => {
    createForm.resetFields()
    setCreateFeedback(null)
    setCreateRoleTargetKeys([])
    setCreateStoreTargetKeys([])
    setCreateStoreManageableKeys([])
  }

  useEffect(() => {
    if (!createOpen || !createFeedback?.field) return
    createForm.scrollToField(createFeedback.field, { block: 'center', focus: true })
  }, [createFeedback, createForm, createOpen])

  const sortedStores = useMemo(
    () => [...allStores].sort((a, b) => a.storeName.localeCompare(b.storeName)),
    [allStores],
  )

  const isCurrentUserScoped = isScopedStoreManager(currentUser, access)
  const managedStores = useMemo(() => getManagedStores(currentUser, access), [access, currentUser])
  const managedStoreKey = managedStores.map((store) => store.storeGUID).join('|')
  const canLoadRoleOptions = access.canReadRole || access.hasPermission(P.Users.ManageRoles)
  const canManageUserPermissions = access.hasPermission(P.Users.ManageRoles)
  const canManagePosTerminalPermissions = access.hasPermission(P.Users.ManagePosTerminalPermissions)
  // 有编辑权的操作者打开可编辑抽屉；否则同一抽屉以只读资料 + 登录记录呈现。
  const canEditUsers = access.hasPermission(P.Users.Edit)
  const canResetUserPassword = access.hasPermission(P.Users.ResetPassword)
  // 后端只允许管理员维护用户角色（ADMIN_REQUIRED），接口本身还要求 Roles.ManageUsers。
  const canBatchManageRoles = access.isAdmin && access.hasPermission(P.Roles.ManageUsers)
  const canBatchSelectUsers = canEditUsers || canBatchManageRoles
  const canEditUserPermissions = canManageUserPermissions || (
    isCurrentUserScoped && canManagePosTerminalPermissions
  )
  const isCurrentEditingSession = (userGuid: string, sessionId: number | null) => Boolean(
    sessionId !== null &&
    editingUserGuidRef.current === userGuid &&
    editSessionGuardRef.current.isLatest(sessionId),
  )

  const visibleStoreOptions = useMemo(() => {
    if (isCurrentUserScoped) {
      return managedStores.map((store) => ({
        label: `${store.storeName} (${store.storeCode})`,
        value: store.storeGUID,
      }))
    }

    return storeOptions
  }, [isCurrentUserScoped, managedStores, storeOptions])

  const managedStoreDetails = useMemo<StoreDto[]>(
    () => managedStores.map((store) => ({
      storeGUID: store.storeGUID,
      storeName: store.storeName,
      storeCode: store.storeCode,
      isActive: true,
      createdAt: '',
      updatedAt: '',
    })),
    [managedStores],
  )

  const sortStoreGuidsFromStores = (keys: string[], stores: StoreDto[]) =>
    [...keys].sort((a, b) => {
      const storeA = stores.find((item) => item.storeGUID === a)
      const storeB = stores.find((item) => item.storeGUID === b)
      if (!storeA || !storeB) return 0
      return storeA.storeName.localeCompare(storeB.storeName)
    })

  const sortStoreGuids = (keys: string[]) => sortStoreGuidsFromStores(keys, sortedStores)

  const sortScopedUsers = (
    users: UserDto[],
    currentSortBy?: string,
    currentSortOrder?: 'ascend' | 'descend' | null,
  ) => {
    if (!currentSortBy || !currentSortOrder) {
      return users
    }

    const direction = currentSortOrder === 'ascend' ? 1 : -1
    return [...users].sort((left, right) => {
      const leftValue = currentSortBy === 'roleNames' || currentSortBy === 'storeNames'
        ? (left[currentSortBy] ?? []).join(',')
        : String(left[currentSortBy as keyof UserDto] ?? '')
      const rightValue = currentSortBy === 'roleNames' || currentSortBy === 'storeNames'
        ? (right[currentSortBy] ?? []).join(',')
        : String(right[currentSortBy as keyof UserDto] ?? '')
      return leftValue.localeCompare(rightValue) * direction
    })
  }

  const handleStoreTargetChange = (nextTargetKeys: Key[]) => {
    const next = sortStoreGuids(nextTargetKeys.map(String))
    setStoreTargetKeys(next)
    setStoreManageableKeys((current) => current.filter((storeGUID) => next.includes(storeGUID)))
  }

  const handleCreateStoreTargetChange = (nextTargetKeys: Key[]) => {
    const next = sortStoreGuids(nextTargetKeys.map(String))
    setCreateStoreTargetKeys(next)
    setCreateStoreManageableKeys((current) => current.filter((storeGUID) => next.includes(storeGUID)))
  }

  const toggleStoreManageable = (
    storeGUID: string,
    checked: boolean,
    setter: Dispatch<SetStateAction<string[]>>,
  ) => {
    setter((current) => {
      const next = new Set(current)
      if (checked) {
        next.add(storeGUID)
      } else {
        next.delete(storeGUID)
      }
      return sortStoreGuids(Array.from(next))
    })
  }

  const renderManageableStoreControls = (
    targetKeys: string[],
    manageableKeys: string[],
    setter: Dispatch<SetStateAction<string[]>>,
  ) => {
    const selectedStores = targetKeys
      .map((storeGUID) => sortedStores.find((item) => item.storeGUID === storeGUID))
      .filter((item): item is StoreDto => Boolean(item))

    if (!selectedStores.length) {
      return null
    }

    return (
      <List
        size="small"
        style={{ marginTop: 12 }}
        dataSource={selectedStores}
        renderItem={(store) => (
          <List.Item
            actions={[
              <Switch
                key="manageable"
                checked={manageableKeys.includes(store.storeGUID)}
                onChange={(checked) => toggleStoreManageable(store.storeGUID, checked, setter)}
              />,
            ]}
          >
            <Space>
              <Typography.Text>{`${store.storeName} (${store.storeCode})`}</Typography.Text>
              {manageableKeys.includes(store.storeGUID) ? (
                <Tag color="processing">{t('system.users.manageableStore', '可管理')}</Tag>
              ) : (
                <Tag>{t('system.users.linkedOnlyStore', '普通关联')}</Tag>
              )}
            </Space>
          </List.Item>
        )}
      />
    )
  }

  const loadData = async (nextPage = page, nextPageSize = pageSize, currentSortBy?: string, currentSortOrder?: 'ascend' | 'descend' | null) => {
    await runLatestGuardedRequest(mainListRequestGuardRef.current, async () => {
      if (isCurrentUserScoped) {
        const scopedStoreGuids = getScopedStoreGuidsForQuery(selectedStoreGuid, managedStores)

        if (!scopedStoreGuids.length) {
          return { items: [], total: 0, page: nextPage, pageSize: nextPageSize }
        }

        const queryBase = {
          search: keyword || undefined,
          roleGuid: selectedRoleGuid,
          isActive: toIsActiveQuery(statusFilter),
          sortBy: currentSortBy || undefined,
          sortDirection: currentSortOrder === 'ascend' ? 'asc' : currentSortOrder === 'descend' ? 'desc' : undefined,
        }
        const scopedStorePageSize = 500
        const loadAllUsersForStore = async (storeGuid: string) => {
          const firstPage = await getUsers({
            page: 1,
            pageSize: scopedStorePageSize,
            storeGuid,
            ...queryBase,
          })
          const pageCount = Math.ceil(firstPage.total / (firstPage.pageSize || scopedStorePageSize))
          if (pageCount <= 1) {
            return firstPage.items
          }

          const restPages = await Promise.all(
            Array.from({ length: pageCount - 1 }, (_, index) =>
              getUsers({
                page: index + 2,
                pageSize: scopedStorePageSize,
                storeGuid,
                ...queryBase,
              }),
            ),
          )
          return [firstPage, ...restPages].flatMap((result) => result.items)
        }

        const results = await Promise.all(
          scopedStoreGuids.map((storeGuid) => loadAllUsersForStore(storeGuid)),
        )
        const mergedUsers = sortScopedUsers(
          filterUsersVisibleToScopedManager(mergeUsersByGuid(results.flat())),
          currentSortBy,
          currentSortOrder,
        )
        const startIndex = (nextPage - 1) * nextPageSize
        return {
          items: mergedUsers.slice(startIndex, startIndex + nextPageSize),
          total: mergedUsers.length,
          page: nextPage,
          pageSize: nextPageSize,
        }
      }

      return getUsers({
        page: nextPage,
        pageSize: nextPageSize,
        search: keyword || undefined,
        storeGuid: selectedStoreGuid,
        roleGuid: selectedRoleGuid,
        isActive: toIsActiveQuery(statusFilter),
        sortBy: currentSortBy || undefined,
        sortDirection: currentSortOrder === 'ascend' ? 'asc' : currentSortOrder === 'descend' ? 'desc' : undefined,
      })
    }, {
      onStart: () => setLoading(true),
      onSuccess: (result) => {
        setData(result.items)
        setTotal(result.total)
        setPage(result.page)
        setPageSize(result.pageSize)
      },
      onError: (error) => {
        console.error(error)
        message.error(t('system.users.loadListFailed', '加载用户列表失败'))
      },
      // 旧请求结束时不能关闭较新请求的 loading。
      onSettled: () => setLoading(false),
    })
  }

  useEffect(() => () => {
    mainListRequestGuardRef.current.invalidate()
    loginRecordsRequestGuardRef.current.invalidate()
    editUserRequestGuardRef.current.invalidate()
    editSessionGuardRef.current.invalidate()
    roleRequestGuardRef.current.invalidate()
    storeRequestGuardRef.current.invalidate()
    permissionRequestGuardRef.current.invalidate()
    permissionSaveGuardRef.current.invalidate()
  }, [])

  const loadLoginRecords = async (
    user: UserDto,
    nextPage = loginRecordsPage,
    nextPageSize = loginRecordsPageSize,
  ) => {
    await runLatestGuardedRequest(loginRecordsRequestGuardRef.current, () => getUserLoginRecords(user.userGUID, {
        page: nextPage,
        pageSize: nextPageSize,
      }), {
      onStart: () => setLoginRecordsLoading(true),
      onSuccess: (result) => {
        setLoginRecords(result.items)
        setLoginRecordsTotal(result.total)
        setLoginRecordsPage(result.page)
        setLoginRecordsPageSize(result.pageSize)
      },
      onError: (error) => {
        console.error(error)
        message.error(t('system.users.loadLoginRecordsFailed', '加载登录记录失败'))
      },
      onSettled: () => setLoginRecordsLoading(false),
    })
  }

  const handleOpenLoginRecords = async (record: UserDto) => {
    setLoginRecordsUser(record)
    setLoginRecords([])
    setLoginRecordsTotal(0)
    setLoginRecordsPage(1)
    setLoginRecordsPageSize(10)
    await loadLoginRecords(record, 1, 10)
  }

  const closeLoginRecords = () => {
    loginRecordsRequestGuardRef.current.invalidate()
    setLoginRecordsLoading(false)
    setLoginRecordsUser(null)
    setLoginRecords([])
    setLoginRecordsTotal(0)
    setLoginRecordsPage(1)
    setLoginRecordsPageSize(10)
  }

  useEffect(() => {
    if (!currentUser) {
      return
    }

    void loadData(1, pageSize, undefined, undefined)
  }, [currentUser?.userGUID, isCurrentUserScoped, managedStoreKey])

  // 筛选即查询：下拉与状态分段立即生效，关键字防抖 300ms；记录上一次的取值，避免首屏与 StrictMode 重复请求。
  const filterQueryKey = `${selectedStoreGuid ?? ''}|${selectedRoleGuid ?? ''}|${statusFilter}`
  const lastFilterQueryKeyRef = useRef(filterQueryKey)
  const lastKeywordRef = useRef(keyword)

  useEffect(() => {
    if (lastFilterQueryKeyRef.current === filterQueryKey) return
    lastFilterQueryKeyRef.current = filterQueryKey
    setSelectedUsers([])
    void loadData(1, pageSize, sortBy, sortOrder)
  }, [filterQueryKey])

  useEffect(() => {
    if (lastKeywordRef.current === keyword) return
    const timer = window.setTimeout(() => {
      lastKeywordRef.current = keyword
      setSelectedUsers([])
      void loadData(1, pageSize, sortBy, sortOrder)
    }, 300)
    return () => window.clearTimeout(timer)
  }, [keyword])

  useEffect(() => {
    void (async () => {
      if (isCurrentUserScoped) {
        setStoreOptions(managedStores.map((store) => ({
          label: `${store.storeName} (${store.storeCode})`,
          value: store.storeGUID,
        })))
      } else {
        try {
          const storeResult = await getStores({ page: 1, pageSize: 200, sortField: 'storeName', sortOrder: 'asc' })
          setStoreOptions(storeResult.items.map((s) => ({
            label: `${s.storeName} (${s.storeCode})`,
            value: s.storeGUID,
          })))
        } catch (error) {
          console.error(error)
          setStoreOptions([])
        }
      }

      if (canLoadRoleOptions) {
        try {
          const roles = await getActiveRoles()
          const nextRoles = isCurrentUserScoped ? filterRoleOptionsForScopedManager(roles) : roles
          setRoleOptions(nextRoles.map((r) => ({ label: r.roleName, value: r.roleGUID, roleName: r.roleName })))
        } catch (error) {
          console.error(error)
          setRoleOptions([])
        }
      } else {
        setRoleOptions([])
      }
    })()
  }, [canLoadRoleOptions, isCurrentUserScoped, managedStoreKey])

  useEffect(() => {
    if (isCurrentUserScoped) {
      if (selectedStoreGuid && !isStoreVisibleToManager(selectedStoreGuid, managedStores)) {
        setSelectedStoreGuid(undefined)
      }
      return
    }

    if (selectedStoreGuid && !visibleStoreOptions.some((option) => option.value === selectedStoreGuid)) {
      setSelectedStoreGuid(undefined)
    }
  }, [isCurrentUserScoped, managedStoreKey, managedStores, selectedStoreGuid, visibleStoreOptions])

  useEffect(() => {
    if (selectedRoleGuid && !roleOptions.some((option) => option.value === selectedRoleGuid)) {
      setSelectedRoleGuid(undefined)
    }
  }, [roleOptions, selectedRoleGuid])

  const reloadUserDetail = async (userGuid: string) => {
    const [detail, stores] = await Promise.all([getUserByGuid(userGuid), getUserStores(userGuid).catch(() => [])])
    setDetailUser(detail)
    setDetailStores(isCurrentUserScoped ? filterStoresForManager(stores, managedStores) : stores)
    return detail
  }

  const handleViewDetail = async (record: UserDto) => {
    if (isCurrentUserScoped && (!data.some((item) => item.userGUID === record.userGUID) || hasForbiddenRoleForScopedManager(record))) {
      message.error(t('system.users.detailOutOfScope', '无权查看该用户详情'))
      return
    }

    setDetailOpen(true)
    setDetailLoading(true)
    setDetailUser(null)
    setDetailStores([])
    try {
      const [detail, stores] = await Promise.all([
        getUserByGuid(record.userGUID),
        getUserStores(record.userGUID).catch(() => []),
      ])
      if (isCurrentUserScoped && hasForbiddenRoleForScopedManager(detail)) {
        message.error(t('system.users.detailOutOfScope', '无权查看该用户详情'))
        setDetailOpen(false)
        return
      }
      setDetailUser(detail)
      setDetailStores(isCurrentUserScoped ? filterStoresForManager(stores, managedStores) : stores)
    } catch (error) {
      console.error(error)
      message.error(t('system.users.loadDetailFailed', '加载用户详情失败'))
      setDetailOpen(false)
    } finally {
      setDetailLoading(false)
    }
  }

  const loadRoleData = async (userGuid: string) => {
    const editSessionId = currentEditSessionIdRef.current
    if (!isCurrentEditingSession(userGuid, editSessionId)) return

    await runLatestGuardedRequest(
      roleRequestGuardRef.current,
      async () => {
        if (!canLoadRoleOptions) {
          return { roles: [] as RoleOptionDto[], targetKeys: [] as string[] }
        }

        const [roles, userRoles] = await Promise.all([getActiveRoles(), getUserRoles(userGuid)])
        return {
          roles: isCurrentUserScoped ? filterRoleOptionsForScopedManager(roles) : roles,
          targetKeys: userRoles.map((item) => item.roleGUID),
        }
      },
      {
        onStart: () => setRoleLoadStatus('loading'),
        onSuccess: ({ roles, targetKeys }) => {
          if (!isCurrentEditingSession(userGuid, editSessionId)) return
          setAllRoles(roles)
          setRoleTargetKeys(targetKeys)
          setRoleBaselineKeys(targetKeys)
          setRoleLoadStatus('ready')
        },
        onError: (error) => {
          if (!isCurrentEditingSession(userGuid, editSessionId)) return
          setRoleLoadStatus('error')
          console.error(error)
          message.error(t('system.users.loadRolesFailed', '加载角色数据失败'))
        },
      },
    )
  }

  const retryRoleData = () => {
    const userGuid = editingUserGuidRef.current
    const editSessionId = currentEditSessionIdRef.current
    if (!userGuid || !isCurrentEditingSession(userGuid, editSessionId)) return
    void loadRoleData(userGuid)
  }

  const loadStoreData = async (userGuid: string) => {
    const editSessionId = currentEditSessionIdRef.current
    if (!isCurrentEditingSession(userGuid, editSessionId)) return

    await runLatestGuardedRequest(
      storeRequestGuardRef.current,
      async () => {
        if (isCurrentUserScoped) {
          const userStores = await getUserStores(userGuid)
          const scopedUserStores = filterStoresForManager(userStores, managedStores)
          return {
            stores: managedStoreDetails,
            targetKeys: sortStoreGuidsFromStores(
              scopedUserStores.map((item) => item.storeGUID),
              managedStoreDetails,
            ),
            manageableKeys: sortStoreGuidsFromStores(
              scopedUserStores.filter((item) => item.isManageable).map((item) => item.storeGUID),
              managedStoreDetails,
            ),
          }
        }

        const [stores, userStores] = await Promise.all([
          getStores({ page: 1, pageSize: 200, sortField: 'storeName', sortOrder: 'asc' }),
          getUserStores(userGuid),
        ])
        return {
          stores: stores.items,
          // 使用本次请求的门店名称排序，避免首次打开时读取尚未更新的 allStores。
          targetKeys: sortStoreGuidsFromStores(
            userStores.map((item) => item.storeGUID),
            stores.items,
          ),
          manageableKeys: sortStoreGuidsFromStores(
            userStores.filter((item) => item.isManageable).map((item) => item.storeGUID),
            stores.items,
          ),
        }
      },
      {
        onStart: () => setStoreLoadStatus('loading'),
        onSuccess: ({ stores, targetKeys, manageableKeys }) => {
          if (!isCurrentEditingSession(userGuid, editSessionId)) return
          setAllStores(stores)
          setStoreTargetKeys(targetKeys)
          setStoreManageableKeys(manageableKeys)
          setStoreBaselineKeys(targetKeys)
          setStoreManageableBaselineKeys(manageableKeys)
          setStoreLoadStatus('ready')
        },
        onError: (error) => {
          if (!isCurrentEditingSession(userGuid, editSessionId)) return
          setStoreLoadStatus('error')
          console.error(error)
          message.error(t('system.users.loadStoresFailed', '加载分店数据失败'))
        },
      },
    )
  }

  const retryStoreData = () => {
    const userGuid = editingUserGuidRef.current
    const editSessionId = currentEditSessionIdRef.current
    if (!userGuid || !isCurrentEditingSession(userGuid, editSessionId)) return
    void loadStoreData(userGuid)
  }

  const loadPermData = async (userGuid: string) => {
    // 角色保存等旧异步流程可能晚到；只有当前编辑会话可以启动权限读取。
    if (editingUserGuidRef.current !== userGuid) return

    await runLatestGuardedRequest(
      permissionRequestGuardRef.current,
      () => getUserAccessPermissions(userGuid),
      {
        onStart: () => {
          setPermLoading(true)
          setPermLoadError(null)
        },
        onSuccess: ({ categories, state }) => {
          setPermCategories(categories)
          setPermissionState(state)
          setDirectPermKeys(state.directPermissionCodes)
          setOriginalDirectPermKeys(state.directPermissionCodes)
          setPermLoadError(null)
        },
        onError: (error) => {
          console.error(error)
          setPermLoadError(t('system.users.permissionLoadErrorState', '无法取得该用户的权限范围，请稍后重试。'))
          message.error(t('system.users.loadPermsFailed', '加载权限数据失败'))
        },
        onSettled: () => setPermLoading(false),
      },
    )
  }

  const handleEdit = async (record: UserDto, initialTab: UserDrawerTab = 'info') => {
    if (isCurrentUserScoped && (!data.some((item) => item.userGUID === record.userGUID) || hasForbiddenRoleForScopedManager(record))) {
      message.error(t('system.users.editOutOfScope', '无权编辑该用户'))
      return
    }

    currentEditSessionIdRef.current = editSessionGuardRef.current.begin()
    roleRequestGuardRef.current.invalidate()
    storeRequestGuardRef.current.invalidate()
    setEditOpen(true)
    editingUserGuidRef.current = record.userGUID
    setEditingUser(null)
    setEditTab(initialTab)
    setPermissionPlatform(isCurrentUserScoped ? 'pos' : 'web')
    permissionRequestGuardRef.current.invalidate()
    permissionSaveGuardRef.current.invalidate()
    setAllRoles([])
    setRoleTargetKeys([])
    setRoleBaselineKeys([])
    setRoleLoadStatus('idle')
    setAllStores([])
    setStoreTargetKeys([])
    setStoreManageableKeys([])
    setStoreBaselineKeys([])
    setStoreManageableBaselineKeys([])
    setStoreLoadStatus('idle')
    setPermCategories([])
    setPermissionState(null)
    setDirectPermKeys([])
    setOriginalDirectPermKeys([])
    setPermLoading(false)
    setPermSaving(false)
    setPermLoadError(null)
    setRoleSaving(false)
    setStoreSaving(false)
    form.resetFields()
    await runLatestGuardedRequest(
      editUserRequestGuardRef.current,
      () => getUserByGuid(record.userGUID),
      {
        onStart: () => setEditLoading(true),
        onSuccess: (detail) => {
          if (isCurrentUserScoped && hasForbiddenRoleForScopedManager(detail)) {
            message.error(t('system.users.editOutOfScope', '无权编辑该用户'))
            editingUserGuidRef.current = null
            currentEditSessionIdRef.current = null
            editSessionGuardRef.current.invalidate()
            setEditOpen(false)
            return
          }
          setEditingUser(detail)
          form.setFieldsValue({
            username: detail.username,
            email: detail.email,
            fullName: detail.fullName,
            isActive: detail.isActive,
          })
          void loadRoleData(record.userGUID)
          void loadStoreData(record.userGUID)
          if (canEditUserPermissions) {
            void loadPermData(record.userGUID)
          }
        },
        onError: (error) => {
          console.error(error)
          message.error(t('system.users.loadEditFailed', '加载用户编辑数据失败'))
          editingUserGuidRef.current = null
          currentEditSessionIdRef.current = null
          editSessionGuardRef.current.invalidate()
          setEditOpen(false)
        },
        onSettled: () => setEditLoading(false),
      },
    )
  }

  const handleEditSubmit = async () => {
    if (!editingUser) return
    const targetUserGuid = editingUser.userGUID
    const editSessionId = currentEditSessionIdRef.current
    if (editSessionId === null) return

    try {
      const values = await form.validateFields()
      if (!isCurrentEditingSession(targetUserGuid, editSessionId)) return

      setEditLoading(true)
      const updated = await updateUser(targetUserGuid, values)
      void loadData(page, pageSize, sortBy, sortOrder)
      if (!isCurrentEditingSession(targetUserGuid, editSessionId)) return

      message.success(t('system.users.updateSuccess', '用户信息已更新'))
      setEditingUser(updated)
      if (detailUser?.userGUID === updated.userGUID) {
        setDetailUser(updated)
      }
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      if (isCurrentEditingSession(targetUserGuid, editSessionId)) {
        console.error(error)
        message.error(t('system.users.updateFailed', '更新用户失败'))
      }
    } finally {
      if (isCurrentEditingSession(targetUserGuid, editSessionId)) {
        setEditLoading(false)
      }
    }
  }

  const handleSaveRoles = async () => {
    if (!editingUser || !canMutateLoadedAssignment(roleLoadStatus, roleSaving)) return
    const targetUserGuid = editingUser.userGUID
    const editSessionId = currentEditSessionIdRef.current
    if (editSessionId === null) return
    if (isCurrentUserScoped && !areRoleGuidsAllowedForScopedManager(roleTargetKeys, allRoles)) {
      message.error(t('system.users.roleAssignForbidden', '店长不能分配管理员、店长或仓库经理角色'))
      return
    }
    // 记录发起保存时的草稿，写入成功后作为新的已保存基线。
    const savedRoleKeys = [...roleTargetKeys]
    setRoleSaving(true)
    try {
      try {
        await assignRolesToUser(targetUserGuid, { roleGuids: roleTargetKeys })
      } catch (error) {
        if (isCurrentEditingSession(targetUserGuid, editSessionId)) {
          console.error(error)
          message.error(t('system.users.roleAssignFailed', '角色分配失败'))
        }
        return
      }

      void loadData(page, pageSize, sortBy, sortOrder)
      if (!isCurrentEditingSession(targetUserGuid, editSessionId)) return

      setRoleBaselineKeys(savedRoleKeys)
      message.success(t('system.users.roleAssignSuccess', '角色分配成功'))
      try {
        const updated = await getUserByGuid(targetUserGuid)
        if (!isCurrentEditingSession(targetUserGuid, editSessionId)) return

        void loadPermData(targetUserGuid)
        setEditingUser(updated)
        if (detailUser?.userGUID === updated.userGUID) setDetailUser(updated)
      } catch (error) {
        if (isCurrentEditingSession(targetUserGuid, editSessionId)) {
          console.error(error)
          message.warning(t(
            'system.users.permissionRefreshFailed',
            '角色已保存，但最新用户状态刷新失败；请关闭后重新打开抽屉。',
          ))
        }
      }
    } finally {
      if (isCurrentEditingSession(targetUserGuid, editSessionId)) {
        setRoleSaving(false)
      }
    }
  }

  const handleSavePermissions = async () => {
    if (
      !editingUser ||
      !canEditUserPermissions ||
      permissionState?.isSuperAdmin ||
      permissionState?.implicitAllPermissions
    ) return

    const targetUserGuid = editingUser.userGUID
    const saveRequestId = permissionSaveGuardRef.current.begin()
    const isCurrentSaveSession = () => permissionSaveGuardRef.current.isLatest(saveRequestId)
    const permissions = buildDirectPermissionPayload(directPermKeys)
    setPermSaving(true)

    try {
      try {
        await assignPermissionsToUser(targetUserGuid, { permissions })
      } catch (error) {
        if (isCurrentSaveSession()) {
          console.error(error)
          message.error(t('system.users.permissionAssignFailed', '用户直接权限保存失败'))
        }
        return
      }

      if (!isCurrentSaveSession()) return

      setOriginalDirectPermKeys(permissions)
      message.success(t('system.users.permissionAssignSuccess', '用户直接权限已保存'))
      void loadData(page, pageSize, sortBy, sortOrder)

      try {
        const [{ categories, state }, updated] = await Promise.all([
          getUserAccessPermissions(targetUserGuid),
          getUserByGuid(targetUserGuid),
        ])
        if (!isCurrentSaveSession()) return

        setPermCategories(categories)
        setPermissionState(state)
        setDirectPermKeys(state.directPermissionCodes)
        setOriginalDirectPermKeys(state.directPermissionCodes)
        setPermLoadError(null)
        setEditingUser(updated)
        if (detailUser?.userGUID === updated.userGUID) setDetailUser(updated)

        if (currentUser?.userGUID === updated.userGUID) {
          await refreshCurrentUserSilently()
          if (!isCurrentSaveSession()) return
        }
      } catch (error) {
        if (isCurrentSaveSession()) {
          console.error(error)
          message.warning(t(
            'system.users.permissionRefreshFailed',
            '权限已保存，但最新权限状态刷新失败；请关闭后重新打开抽屉。',
          ))
        }
      }
    } finally {
      if (isCurrentSaveSession()) {
        setPermSaving(false)
      }
    }
  }

  const handleSaveStores = async () => {
    if (!editingUser || !canMutateLoadedAssignment(storeLoadStatus, storeSaving)) return
    const targetUserGuid = editingUser.userGUID
    const editSessionId = currentEditSessionIdRef.current
    if (editSessionId === null) return
    const savedStoreKeys = [...storeTargetKeys]
    const savedManageableKeys = [...storeManageableKeys]
    setStoreSaving(true)
    try {
      try {
        const storeAssignments = isCurrentUserScoped
          ? buildScopedStoreAssignments(
            await getUserStores(targetUserGuid),
            storeTargetKeys,
            storeManageableKeys,
            managedStores,
          )
          : storeTargetKeys.map((storeGUID) => ({
            storeGUID,
            accessLevel: 'ReadWrite',
            isManageable: storeManageableKeys.includes(storeGUID),
          }))

        if (!isCurrentEditingSession(targetUserGuid, editSessionId)) return

        await assignStoresToUser(
          targetUserGuid,
          storeAssignments,
        )
      } catch (error) {
        if (isCurrentEditingSession(targetUserGuid, editSessionId)) {
          console.error(error)
          message.error(
            error instanceof Error
              ? error.message
              : t('system.users.storeAssignFailed', '分店分配失败'),
          )
        }
        return
      }

      void loadData(page, pageSize, sortBy, sortOrder)
      if (!isCurrentEditingSession(targetUserGuid, editSessionId)) return

      setStoreBaselineKeys(savedStoreKeys)
      setStoreManageableBaselineKeys(savedManageableKeys)
      message.success(t('system.users.storeAssignSuccess', '分店分配成功'))
      try {
        const updated = await getUserByGuid(targetUserGuid)
        if (!isCurrentEditingSession(targetUserGuid, editSessionId)) return

        setEditingUser(updated)
        if (detailUser?.userGUID === updated.userGUID) {
          void reloadUserDetail(updated.userGUID)
        }
      } catch (error) {
        if (isCurrentEditingSession(targetUserGuid, editSessionId)) {
          console.error(error)
          message.warning(t(
            'system.users.permissionRefreshFailed',
            '分店已保存，但最新用户状态刷新失败；请关闭后重新打开抽屉。',
          ))
        }
      }
    } finally {
      if (isCurrentEditingSession(targetUserGuid, editSessionId)) {
        setStoreSaving(false)
      }
    }
  }

  const handleResetPassword = async () => {
    const target = resetPwdTarget
    if (!target) return
    try {
      const values = await resetPwdForm.validateFields()
      setResetPwdLoading(true)
      await updateUserPassword(target.userGUID, {
        newPassword: values.newPassword,
        passwordFormat: 'raw',
      })
      message.success(t('system.users.resetPasswordSuccess', '密码重置成功'))
      setResetPwdOpen(false)
      setResetPwdTarget(null)
      resetPwdForm.resetFields()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      console.error(error)
      message.error(t('system.users.resetPasswordFailed', '密码重置失败'))
    } finally {
      setResetPwdLoading(false)
    }
  }

  const openResetPassword = (target: Pick<UserDto, 'userGUID' | 'username'>) => {
    resetPwdForm.resetFields()
    setResetPwdTarget({ userGUID: target.userGUID, username: target.username })
    setResetPwdOpen(true)
  }

  const roleAssignmentDiff = diffAssignmentKeys(roleBaselineKeys, roleTargetKeys)
  const storeAssignmentDiff = diffStoreAssignment({
    baselineStores: storeBaselineKeys,
    draftStores: storeTargetKeys,
    baselineManageable: storeManageableBaselineKeys,
    draftManageable: storeManageableKeys,
  })
  const hasRoleAssignmentChanges = roleAssignmentDiff.added.length + roleAssignmentDiff.removed.length > 0
  const hasStoreAssignmentChanges =
    storeAssignmentDiff.added.length + storeAssignmentDiff.removed.length + storeAssignmentDiff.manageableChanged.length > 0
  const assignmentChangeCount =
    roleAssignmentDiff.added.length +
    roleAssignmentDiff.removed.length +
    storeAssignmentDiff.added.length +
    storeAssignmentDiff.removed.length +
    storeAssignmentDiff.manageableChanged.length

  /**
   * 「角色与分店」共用一个保存入口：只保存有变化的一侧，依次复用各自的保存流程，
   * 各自的会话守卫、写入失败与回读失败提示保持不变；一侧失败时另一侧的草稿仍保留。
   */
  const handleSaveAssignments = async () => {
    if (hasRoleAssignmentChanges) {
      await handleSaveRoles()
    }
    if (hasStoreAssignmentChanges) {
      await handleSaveStores()
    }
  }

  const discardAssignmentChanges = () => {
    setRoleTargetKeys(roleBaselineKeys)
    setStoreTargetKeys(storeBaselineKeys)
    setStoreManageableKeys(storeManageableBaselineKeys)
  }

  const resetPosPermissionState = () => {
    // 递增序号让在途的收银权限请求全部失效，再清空该标签页状态。
    posPermissionRequestRef.current = {
      sequence: (posPermissionRequestRef.current?.sequence ?? 0) + 1,
      userGuid: '',
      storeGuid: '',
    }
    setPosPermissionUser(null)
    setPosPermissionStores([])
    setSelectedPosPermissionStoreGuid(undefined)
    setPosPermissionState(null)
    setPosPermissionError(null)
    setPosPermissionStoresLoading(false)
    setPosPermissionLoading(false)
    setPosPermissionSaving(false)
    setPosPermissionRestoring(false)
  }

  /** 打开统一用户抽屉：有编辑权走可编辑流程，否则只读查看；切换用户时先清掉上一位的登录记录与收银权限。 */
  const openUserDrawer = (record: UserDto, tab: UserDrawerTab = 'info') => {
    closeLoginRecords()
    resetPosPermissionState()
    setDrawerUser(record)
    if (canEditUsers) {
      void handleEdit(record, tab)
      return
    }
    setEditTab(tab === 'login-records' || tab === 'pos' ? tab : 'info')
    void handleViewDetail(record)
  }

  const handleToggleUserActive = (record: UserDto) => {
    if (isCurrentUserScoped && (!data.some((item) => item.userGUID === record.userGUID) || hasForbiddenRoleForScopedManager(record))) {
      message.error(t('system.users.editOutOfScope', '无权编辑该用户'))
      return
    }

    const nextActive = !record.isActive
    const name = getUserDisplayName(record)
    Modal.confirm({
      title: nextActive
        ? t('system.usersWorkspace.enableUserTitle', '启用账号「{{name}}」？', { name })
        : t('system.usersWorkspace.disableUserTitle', '停用账号「{{name}}」？', { name }),
      content: nextActive
        ? t('system.usersWorkspace.enableUserContent', '启用后该账号可以重新登录。')
        : t('system.usersWorkspace.disableUserContent', '停用后该账号将无法登录，已分配的角色与分店保持不变。'),
      okText: nextActive
        ? t('system.usersWorkspace.enableUser', '启用账号')
        : t('system.usersWorkspace.disableUser', '停用账号'),
      okButtonProps: { danger: !nextActive },
      cancelText: t('common.cancel', '取消'),
      onOk: async () => {
        try {
          // 只改启停状态：其余字段沿用列表里的当前值，与资料表单保存的字段一致。
          const updated = await updateUser(record.userGUID, {
            username: record.username,
            email: record.email,
            fullName: record.fullName,
            isActive: nextActive,
          })
          message.success(nextActive
            ? t('system.usersWorkspace.enableUserSuccess', '账号已启用')
            : t('system.usersWorkspace.disableUserSuccess', '账号已停用'))
          void loadData(page, pageSize, sortBy, sortOrder)
          if (editingUserGuidRef.current === updated.userGUID) {
            setEditingUser(updated)
            form.setFieldValue('isActive', updated.isActive)
          }
          if (detailUser?.userGUID === updated.userGUID) setDetailUser(updated)
        } catch (error) {
          console.error(error)
          message.error(t('system.users.updateFailed', '更新用户失败'))
        }
      },
    })
  }

  const handleRowAction = (record: UserDto, action: UserRowAction) => {
    if (action === 'login-records' || action === 'pos') {
      openUserDrawer(record, action)
    } else if (action === 'cashier-qr') {
      setCashierBarcodeUser(record)
    } else if (action === 'reset-password') {
      openResetPassword(record)
    } else if (action === 'toggle-active') {
      handleToggleUserActive(record)
    }
  }

  const applyPosPermissionState = (nextState: UserStorePosTerminalPermissionsResponse) => {
    const editableCodes = getEditablePosPermissionCodes(
      nextState.effectivePermissionCodes,
      nextState.assignablePermissions,
    )
    setPosPermissionState(nextState)
    setSelectedPosPermissionCodes(editableCodes)
    setOriginalPosPermissionCodes(editableCodes)
  }

  const loadPosPermissionState = async (userGuid: string, storeGuid: string) => {
    const requestTarget = {
      sequence: (posPermissionRequestRef.current?.sequence ?? 0) + 1,
      userGuid,
      storeGuid,
    }
    posPermissionRequestRef.current = requestTarget
    setPosPermissionLoading(true)
    setPosPermissionError(null)
    setPosPermissionState(null)
    setSelectedPosPermissionCodes([])
    setOriginalPosPermissionCodes([])
    try {
      const nextState = await getUserStorePosTerminalPermissions(userGuid, storeGuid)
      if (!isCurrentPosPermissionRequest(requestTarget, posPermissionRequestRef.current)) return
      applyPosPermissionState(nextState)
    } catch (error) {
      if (!isCurrentPosPermissionRequest(requestTarget, posPermissionRequestRef.current)) return
      console.error(error)
      setPosPermissionError(t('system.users.loadPosPermissionsFailed', '加载收银权限失败，请重试'))
    } finally {
      if (isCurrentPosPermissionRequest(requestTarget, posPermissionRequestRef.current)) {
        setPosPermissionLoading(false)
      }
    }
  }

  const handleOpenPosPermissions = async (record: UserDto) => {
    if (!canManagePosTerminalPermissions) return
    if (isCurrentUserScoped && (!data.some((item) => item.userGUID === record.userGUID) || hasForbiddenRoleForScopedManager(record))) {
      message.error(t('system.users.posPermissionOutOfScope', '无权管理该用户的收银权限'))
      return
    }

    setPosPermissionUser(record)
    setPosPermissionStores([])
    setSelectedPosPermissionStoreGuid(undefined)
    setPosPermissionState(null)
    setPosPermissionError(null)
    setPosPermissionLoading(false)
    const storeListRequestTarget = {
      sequence: (posPermissionRequestRef.current?.sequence ?? 0) + 1,
      userGuid: record.userGUID,
      storeGuid: '',
    }
    posPermissionRequestRef.current = storeListRequestTarget
    setPosPermissionStoresLoading(true)
    try {
      const userStores = await getUserStores(record.userGUID)
      // 店长只看“自己可管理分店”和“目标用户关联分店”的交集；管理员保留目标用户全部分店。
      const scopedStores = isCurrentUserScoped
        ? filterStoresForManager(userStores, managedStores)
        : userStores
      const sortedScopedStores = [...scopedStores].sort((left, right) =>
        left.storeName.localeCompare(right.storeName, 'zh-CN', { numeric: true }),
      )
      if (!isCurrentPosPermissionRequest(storeListRequestTarget, posPermissionRequestRef.current)) return
      setPosPermissionStores(sortedScopedStores)
      const firstStoreGuid = sortedScopedStores[0]?.storeGUID
      setSelectedPosPermissionStoreGuid(firstStoreGuid)
      setPosPermissionStoresLoading(false)
      if (firstStoreGuid) {
        await loadPosPermissionState(record.userGUID, firstStoreGuid)
      }
    } catch (error) {
      if (!isCurrentPosPermissionRequest(storeListRequestTarget, posPermissionRequestRef.current)) return
      console.error(error)
      setPosPermissionError(t('system.users.loadPosPermissionStoresFailed', '加载目标用户分店失败，请重试'))
    } finally {
      if (isCurrentPosPermissionRequest(storeListRequestTarget, posPermissionRequestRef.current)) {
        setPosPermissionStoresLoading(false)
      }
    }
  }

  const handlePosPermissionStoreChange = async (storeGuid: string) => {
    if (!posPermissionUser) return
    setSelectedPosPermissionStoreGuid(storeGuid)
    await loadPosPermissionState(posPermissionUser.userGUID, storeGuid)
  }

  const handleSavePosPermissions = async () => {
    if (!posPermissionUser || !selectedPosPermissionStoreGuid || !posPermissionState) return
    const requestTarget = posPermissionRequestRef.current
    setPosPermissionSaving(true)
    try {
      const grantedPermissionCodes = buildGrantedPosPermissionCodes(
        selectedPosPermissionCodes,
        posPermissionState.assignablePermissions,
      )
      const nextState = await updateUserStorePosTerminalPermissions(
        posPermissionUser.userGUID,
        selectedPosPermissionStoreGuid,
        { grantedPermissionCodes },
      )
      if (requestTarget && isCurrentPosPermissionRequest(requestTarget, posPermissionRequestRef.current)) {
        applyPosPermissionState(nextState)
        message.success(t('system.users.savePosPermissionsSuccess', '分店收银权限已保存'))
      }
    } catch (error) {
      if (requestTarget && isCurrentPosPermissionRequest(requestTarget, posPermissionRequestRef.current)) {
        console.error(error)
        message.error(t('system.users.savePosPermissionsFailed', '保存分店收银权限失败'))
      }
    } finally {
      setPosPermissionSaving(false)
    }
  }

  const restorePosPermissionInheritance = async (
    userGuid: string,
    storeGuid: string,
    requestTarget: PosPermissionRequestTarget | null,
  ) => {
    setPosPermissionRestoring(true)
    try {
      const nextState = await deleteUserStorePosTerminalPermissions(
        userGuid,
        storeGuid,
      )
      if (requestTarget && isCurrentPosPermissionRequest(requestTarget, posPermissionRequestRef.current)) {
        applyPosPermissionState(nextState)
        message.success(t('system.users.restorePosPermissionsSuccess', '已恢复账号权限继承'))
      }
    } catch (error) {
      if (requestTarget && isCurrentPosPermissionRequest(requestTarget, posPermissionRequestRef.current)) {
        console.error(error)
        message.error(t('system.users.restorePosPermissionsFailed', '恢复账号权限继承失败'))
      }
    } finally {
      setPosPermissionRestoring(false)
    }
  }

  const handleRestorePosPermissionInheritance = () => {
    if (!posPermissionUser || !selectedPosPermissionStoreGuid) return
    // 确认框打开时固定目标，避免用户切换分店后误删其他分店覆盖。
    const userGuid = posPermissionUser.userGUID
    const storeGuid = selectedPosPermissionStoreGuid
    const requestTarget = posPermissionRequestRef.current
    Modal.confirm({
      title: t('system.users.restorePosPermissionsTitle', '恢复账号权限继承'),
      content: t('system.users.restorePosPermissionsConfirm', '恢复后将删除当前分店覆盖，并重新使用账号级有效权限。'),
      okText: t('system.users.restoreInheritance', '恢复继承'),
      cancelText: t('common.cancel', '取消'),
      onOk: () => restorePosPermissionInheritance(userGuid, storeGuid, requestTarget),
    })
  }

  const handleOpenCreate = async () => {
    setCreateOpen(true)
    setCreateTab('info')
    createForm.resetFields()
    setCreateFeedback(null)
    setCreateRoleTargetKeys([])
    setCreateStoreTargetKeys([])
    setCreateStoreManageableKeys([])
    setCreateRoleLoading(true)
    setCreateStoreLoading(true)
    try {
      const [roles, stores] = await Promise.all([
        canLoadRoleOptions ? getActiveRoles() : Promise.resolve([]),
        isCurrentUserScoped
          ? Promise.resolve(managedStoreDetails)
          : getStores({ page: 1, pageSize: 200, sortField: 'storeName', sortOrder: 'asc' }).then((result) => result.items),
      ])
      setAllRoles(isCurrentUserScoped ? filterRoleOptionsForScopedManager(roles) : roles)
      setAllStores(stores)
    } catch (error) {
      console.error(error)
    } finally {
      setCreateRoleLoading(false)
      setCreateStoreLoading(false)
    }
  }

  const handleCreateSubmit = async () => {
    setCreateFeedback(null)
    try {
      const values = await createForm.validateFields()
      if (isCurrentUserScoped && !areRoleGuidsAllowedForScopedManager(createRoleTargetKeys, allRoles)) {
        setCreateFeedback({
          message: t('system.users.roleAssignForbidden', '店长不能分配管理员、店长或仓库经理角色'),
        })
        return
      }
      setCreateLoading(true)
      const payload: CreateUserDto = {
        username: values.username,
        email: values.email,
        password: values.password,
        passwordFormat: 'raw',
        fullName: values.fullName,
        isActive: values.isActive ?? true,
        roleGuids: createRoleTargetKeys,
        storeGuids: createStoreTargetKeys,
      }
      const created = await createUser(payload)
      if (createStoreTargetKeys.length > 0) {
        try {
          await assignStoresToUser(
            created.userGUID,
            createStoreTargetKeys.map((storeGUID) => ({
              storeGUID,
              accessLevel: 'ReadWrite',
              isManageable: createStoreManageableKeys.includes(storeGUID),
            })),
          )
        } catch (error) {
          console.error(error)
          message.warning({
            content: t(
              'system.users.createUserStoreAssignmentUnconfirmed',
              '用户已创建，但未能确认分店权限是否保存。请在用户列表中编辑该用户，检查分店设置。',
            ),
            duration: 8,
          })
          setCreateOpen(false)
          clearCreateFormState()
          void loadData(1, pageSize, sortBy, sortOrder)
          return
        }
      }
      message.success(t('system.users.createUserSuccess', '用户创建成功'))
      setCreateOpen(false)
      clearCreateFormState()
      void loadData(1, pageSize, sortBy, sortOrder)
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      console.error(error)
      const feedback = getCreateUserErrorFeedback(error, t)
      if (!feedback) return
      if (feedback.field) {
        createForm.setFields([{ name: feedback.field, errors: [feedback.message] }])
        setCreateTab('info')
      }
      setCreateFeedback(feedback)
    } finally {
      setCreateLoading(false)
    }
  }

  const inheritedPermSet = useMemo(() => {
    return new Set(permissionState?.inheritedPermissionCodes ?? [])
  }, [permissionState])

  const allPermissionCodes = useMemo(() => {
    return new Set(permCategories.flatMap((cat) => cat.permissions.map((permission) => permission.name)))
  }, [permCategories])

  const platformPermissionCategories = useMemo(
    () => splitPermissionCategoriesByPlatform(permCategories),
    [permCategories],
  )

  const visiblePermissionCategories = platformPermissionCategories[permissionPlatform]
  const visiblePermissionCodes = useMemo(
    () => visiblePermissionCategories.flatMap((category) =>
      category.permissions.map((permission) => permission.name),
    ),
    [visiblePermissionCategories],
  )

  const directPermSet = useMemo(() => {
    return new Set(directPermKeys)
  }, [directPermKeys])

  const effectivePermSet = useMemo(() => {
    return new Set(buildEffectivePermissionCodes(
      permissionState?.inheritedPermissionCodes ?? [],
      directPermKeys,
    ))
  }, [directPermKeys, permissionState])

  const isPermissionStateReadOnly = Boolean(
    permissionState?.isSuperAdmin || permissionState?.implicitAllPermissions,
  )
  const canEditCurrentPermissionState = Boolean(
    permissionState && canEditUserPermissions && !isPermissionStateReadOnly,
  )

  const permissionSourceMap = useMemo(() => {
    return buildPermissionSourceMap(permissionState?.inheritedSources ?? [])
  }, [permissionState])

  const hasDirectPermChanges = useMemo(() => {
    return !arePermissionSetsEqual(directPermKeys, originalDirectPermKeys)
  }, [directPermKeys, originalDirectPermKeys])

  const permTreeData = useMemo<DataNode[]>(() => {
    return visiblePermissionCategories.map((cat) => ({
      key: `category:${cat.category}`,
      title: <strong>{cat.displayName}</strong>,
      children: cat.permissions.map((p) => ({
        key: p.name,
        disableCheckbox: inheritedPermSet.has(p.name) && !directPermSet.has(p.name),
        title: (
          <Space size={4} wrap>
            <span>{p.displayName}</span>
            {(permissionSourceMap[p.name] ?? []).map((roleName) => (
              <Tag key={roleName} color={getRoleColor(roleName)} style={{ fontSize: 11, lineHeight: '18px', padding: '0 4px' }}>
                {roleName}
              </Tag>
            ))}
            {directPermSet.has(p.name) ? (
              <Tag color="blue" style={{ fontSize: 11, lineHeight: '18px', padding: '0 4px' }}>
                {t('system.users.directPermissionTag', '直接授权')}
              </Tag>
            ) : null}
          </Space>
        ),
      })),
    }))
  }, [directPermSet, inheritedPermSet, permissionSourceMap, t, visiblePermissionCategories])

  const checkedPermKeys = useMemo(() => {
    return visiblePermissionCodes.filter((permissionCode) => effectivePermSet.has(permissionCode))
  }, [effectivePermSet, visiblePermissionCodes])

  const posPermissionSections = useMemo(
    () => buildPosPermissionSections(posPermissionState?.assignablePermissions ?? []),
    [posPermissionState],
  )
  const inheritedPosPermissionSet = useMemo(
    () => new Set(posPermissionState?.inheritedPermissionCodes ?? []),
    [posPermissionState],
  )
  const overriddenPosPermissionSet = useMemo(
    () => new Set(posPermissionState?.overriddenPermissionCodes ?? []),
    [posPermissionState],
  )
  const effectivePosPermissionSet = useMemo(
    () => new Set(posPermissionState?.effectivePermissionCodes ?? []),
    [posPermissionState],
  )
  const selectedPosPermissionSet = useMemo(
    () => new Set(selectedPosPermissionCodes),
    [selectedPosPermissionCodes],
  )
  const hasPosPermissionChanges = useMemo(
    () => !arePermissionSetsEqual(selectedPosPermissionCodes, originalPosPermissionCodes),
    [originalPosPermissionCodes, selectedPosPermissionCodes],
  )

  const handlePermissionCheck = (
    nextCheckedKeys: Key[] | { checked: Key[]; halfChecked: Key[] },
    info: { checked: boolean; node: { key: Key } },
  ) => {
    if (!canEditCurrentPermissionState || permSaving) return

    const changedKey = String(info.node.key)
    const checkedKeys = Array.isArray(nextCheckedKeys)
      ? nextCheckedKeys
      : nextCheckedKeys.checked
    const checkedPermissionKeys = checkedKeys.map(String)

    if (!info.checked && inheritedPermSet.has(changedKey)) {
      if (directPermSet.has(changedKey)) {
        message.info(t('system.users.directPermissionRemovedInheritedKept', '已移除直接授权，该权限仍由角色继承保留'))
      } else {
        message.info(t('system.users.inheritedPermissionReadonly', '该权限来自角色，请到角色权限管理调整'))
      }
    }

    setDirectPermKeys(
      mergeVisibleDirectPermissionSelection({
        checkedPermissionKeys,
        visiblePermissionCodes,
        inheritedPermissionCodes: Array.from(inheritedPermSet),
        currentDirectPermissionCodes: directPermKeys,
      }),
    )
  }

  const renderLoginRecordStatus = (record: UserLoginRecordDto) => {
    if (record.status === 'revoked' || record.isRevoked) {
      return <Tag color="warning">{t('system.users.loginRecordRevoked', '已撤销')}</Tag>
    }
    if (record.status === 'expired' || record.isExpired) {
      return <Tag>{t('system.users.loginRecordExpired', '已过期')}</Tag>
    }
    return <Tag color="success">{t('system.users.loginRecordActive', '有效')}</Tag>
  }

  const loginRecordColumns: ColumnsType<UserLoginRecordDto> = [
    {
      title: t('system.users.loginAt', '登录时间'),
      dataIndex: 'loginAt',
      width: 180,
      render: (value: string) => formatUserLocalDateTime(value, t('common.emptyValue')),
    },
    {
      title: t('system.users.loginIp', '登录 IP'),
      dataIndex: 'ipAddress',
      width: 150,
      render: (value: string | undefined) => value || t('common.emptyValue'),
    },
    {
      title: t('common.status', '状态'),
      dataIndex: 'status',
      width: 110,
      render: (_value: string, record) => renderLoginRecordStatus(record),
    },
    {
      title: t('system.users.expiresAt', '过期时间'),
      dataIndex: 'expiresAt',
      width: 180,
      render: (value: string) => formatUserLocalDateTime(value, t('common.emptyValue')),
    },
    {
      title: 'UserAgent',
      dataIndex: 'userAgent',
      ellipsis: true,
      render: (value: string | undefined) => value || t('common.emptyValue'),
    },
  ]

  const formatPlainDateTime = (value?: string | null) => {
    if (!value) return t('common.emptyValue')
    const parsed = dayjs(value)
    return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm') : value
  }

  const formatLastLoginLabel = (record: Pick<UserDto, 'lastLoginAt'>) => {
    const description = describeLastLogin(record.lastLoginAt, new Date())
    if (description.kind === 'never') return { label: t('system.usersWorkspace.lastLoginNever', '从未登录'), stale: true }
    if (description.kind === 'justNow') return { label: t('system.usersWorkspace.lastLoginJustNow', '刚刚'), stale: false }
    if (description.kind === 'minutes') {
      return { label: t('system.usersWorkspace.lastLoginMinutes', '{{count}} 分钟前', { count: description.count }), stale: false }
    }
    if (description.kind === 'hours') {
      return { label: t('system.usersWorkspace.lastLoginHours', '{{count}} 小时前', { count: description.count }), stale: false }
    }
    if (description.kind === 'yesterday') return { label: t('system.usersWorkspace.lastLoginYesterday', '昨天'), stale: false }
    return {
      label: t('system.usersWorkspace.lastLoginDays', '{{count}} 天前', { count: description.count }),
      stale: description.stale,
    }
  }

  const renderLastLogin = (record: UserDto) => {
    const { label, stale } = formatLastLoginLabel(record)
    const parsed = parseUserUtcTimestamp(record.lastLoginAt)
    if (!parsed) {
      return <Typography.Text type="secondary">{label}</Typography.Text>
    }

    // 相对时间便于扫读，绝对时间与登录 IP 放在第二行和悬浮提示里。
    const tooltip = [
      formatUserLocalDateTime(record.lastLoginAt, ''),
      record.lastLoginIp ? t('system.usersWorkspace.loginIp', '登录 IP：{{ip}}', { ip: record.lastLoginIp }) : '',
      stale ? t('system.usersWorkspace.lastLoginStaleHint', '超过 {{days}} 天未登录', { days: STALE_LOGIN_DAYS }) : '',
    ].filter(Boolean).join(' · ')

    return (
      <Tooltip title={tooltip}>
        <span>
          <span className={stale ? 'users-ws-login-rel users-ws-login-stale' : 'users-ws-login-rel'}>{label}</span>
          <span className="users-ws-login-abs">{parsed.format('YYYY-MM-DD HH:mm')}</span>
        </span>
      </Tooltip>
    )
  }

  const renderUserIdentity = (record: UserDto) => {
    const displayName = getUserDisplayName(record)
    return (
      <div className="users-ws-identity">
        <span className={record.isActive ? 'users-ws-avatar' : 'users-ws-avatar users-ws-avatar-inactive'} aria-hidden="true">
          {getUserInitial(displayName)}
        </span>
        <span style={{ minWidth: 0 }}>
          <span className={record.isActive ? 'users-ws-name' : 'users-ws-name users-ws-name-inactive'}>{displayName}</span>
          <span className="users-ws-subline">{getUserSecondaryLine(record) || t('common.emptyValue')}</span>
        </span>
      </div>
    )
  }

  const buildRowMenuItems = (record: UserDto): MenuProps['items'] => {
    const items: NonNullable<MenuProps['items']> = [
      { key: 'login-records', icon: <HistoryOutlined />, label: t('system.users.loginRecords', '登录记录') },
    ]
    if (canManagePosTerminalPermissions) {
      items.push({ key: 'pos', icon: <DollarOutlined />, label: t('system.users.posPermissions', '收银权限') })
    }
    if (access.isAdmin && canManagePosTerminalPermissions) {
      items.push({ key: 'cashier-qr', icon: <QrcodeOutlined />, label: t('system.users.cashierQr.title') })
    }

    const accountItems: NonNullable<MenuProps['items']> = []
    if (canResetUserPassword) {
      accountItems.push({ key: 'reset-password', icon: <LockOutlined />, label: t('system.users.resetPassword', '重置密码') })
    }
    // 不提供停用当前登录账号的入口，避免把自己锁在系统外。
    if (canEditUsers && record.userGUID !== currentUser?.userGUID) {
      accountItems.push(record.isActive
        ? { key: 'toggle-active', icon: <StopOutlined />, danger: true, label: t('system.usersWorkspace.disableUser', '停用账号') }
        : { key: 'toggle-active', icon: <CheckCircleOutlined />, label: t('system.usersWorkspace.enableUser', '启用账号') })
    }
    if (accountItems.length) {
      items.push({ type: 'divider' }, ...accountItems)
    }
    return items
  }

  const columns: ColumnsType<UserDto> = [
    {
      title: t('system.usersWorkspace.user', '用户'),
      dataIndex: 'username',
      width: 240,
      sorter: true,
      sortOrder: sortBy === 'username' ? sortOrder : null,
      render: (_value: string, record) => renderUserIdentity(record),
    },
    {
      title: t('system.users.roles', '角色'),
      dataIndex: 'roleNames',
      width: 190,
      sorter: true,
      sortOrder: sortBy === 'roleNames' ? sortOrder : null,
      render: (value: string[]) => value?.length ? (
        <div className="users-ws-chips">
          {value.map((item) => <RoleChip key={item} roleName={item} />)}
        </div>
      ) : <Typography.Text type="secondary">{t('common.emptyValue')}</Typography.Text>,
    },
    {
      title: t('system.users.linkedStores', '关联分店'),
      dataIndex: 'storeNames',
      width: 260,
      sorter: true,
      sortOrder: sortBy === 'storeNames' ? sortOrder : null,
      render: (value: string[]) => {
        // 分店不承载语义，统一中性标签；超出两家折叠为 +N，悬浮查看全部。
        const { visible, hidden } = splitVisibleStores(value)
        if (!visible.length) return <Typography.Text type="secondary">{t('common.emptyValue')}</Typography.Text>
        return (
          <div className="users-ws-chips users-ws-chips-nowrap">
            {visible.map((store) => <NeutralChip key={store} title={store}>{store}</NeutralChip>)}
            {hidden.length ? (
              <Tooltip title={hidden.join('、')}>
                <span>
                  <NeutralChip outlined>{`+${hidden.length}`}</NeutralChip>
                </span>
              </Tooltip>
            ) : null}
          </div>
        )
      },
    },
    {
      title: t('common.status', '状态'),
      dataIndex: 'isActive',
      width: 88,
      render: (value: boolean) => (
        <StatusDot active={value} label={value ? t('common.active', '启用') : t('common.inactive', '停用')} />
      ),
    },
    {
      title: t('system.users.lastLogin', '最近登录'),
      dataIndex: 'lastLoginAt',
      width: 160,
      sorter: true,
      sortOrder: sortBy === 'lastLoginAt' ? sortOrder : null,
      render: (_value: string | undefined, record) => renderLastLogin(record),
    },
    {
      title: t('common.action', '操作'),
      key: 'action',
      width: 132,
      fixed: 'right',
      align: 'right',
      render: (_, record) => (
        <div className="users-ws-row-actions">
          <Button
            size="small"
            icon={canEditUsers ? <EditOutlined /> : <EyeOutlined />}
            onClick={() => openUserDrawer(record)}
          >
            {canEditUsers ? t('common.edit', '编辑') : t('common.view', '详情')}
          </Button>
          <Dropdown
            trigger={['click']}
            placement="bottomRight"
            menu={{
              items: buildRowMenuItems(record),
              onClick: ({ key, domEvent }) => {
                // 下拉菜单渲染在 Portal 中，事件仍沿 React 树冒泡到表格行，这里拦截以免同时打开抽屉。
                domEvent.stopPropagation()
                handleRowAction(record, key as UserRowAction)
              },
            }}
          >
            <Button size="small" icon={<MoreOutlined />} aria-label={t('system.usersWorkspace.moreActions', '更多操作')} />
          </Dropdown>
        </div>
      ),
    },
  ]

  const toggleRoleAssignment = (roleGuid: string, checked: boolean) => {
    if (!canMutateLoadedAssignment(roleLoadStatus, roleSaving)) return
    setRoleTargetKeys((current) => {
      if (checked) return current.includes(roleGuid) ? current : [...current, roleGuid]
      return current.filter((key) => key !== roleGuid)
    })
  }

  const addStoreAssignment = (storeGuid: string) => {
    // 选中即加入列表：收起下拉并清空搜索词，便于连续添加下一家。
    setAddStoreSelectOpen(false)
    setAddStoreSearch('')
    if (!canMutateLoadedAssignment(storeLoadStatus, storeSaving) || storeTargetKeys.includes(storeGuid)) return
    handleStoreTargetChange([...storeTargetKeys, storeGuid])
  }

  const selectedAssignmentStores = storeTargetKeys
    .map((storeGUID) => sortedStores.find((item) => item.storeGUID === storeGUID))
    .filter((item): item is StoreDto => Boolean(item))
  const addableStoreOptions = sortedStores
    .filter((store) => !storeTargetKeys.includes(store.storeGUID))
    .map((store) => ({ label: `${store.storeName} (${store.storeCode})`, value: store.storeGUID }))
  const storeControlsDisabled = !canMutateLoadedAssignment(storeLoadStatus, storeSaving)

  const assignmentChangeDetail = [
    hasRoleAssignmentChanges
      ? t('system.usersWorkspace.pendingRoles', '角色 +{{added}} / −{{removed}}', {
          added: roleAssignmentDiff.added.length,
          removed: roleAssignmentDiff.removed.length,
        })
      : '',
    storeAssignmentDiff.added.length + storeAssignmentDiff.removed.length > 0
      ? t('system.usersWorkspace.pendingStores', '分店 +{{added}} / −{{removed}}', {
          added: storeAssignmentDiff.added.length,
          removed: storeAssignmentDiff.removed.length,
        })
      : '',
    storeAssignmentDiff.manageableChanged.length > 0
      ? t('system.usersWorkspace.pendingManageable', '可管理 {{count}} 项变更', {
          count: storeAssignmentDiff.manageableChanged.length,
        })
      : '',
  ].filter(Boolean).join(' · ')

  const editTabItems = [
    {
      key: 'info',
      label: t('system.usersWorkspace.tabProfile', '资料'),
      children: (
        <Spin spinning={editLoading}>
          <div className="users-ws-tab-body">
            <section>
              <div className="users-ws-section-head">
                <h3>{t('system.users.basicInfo', '基本信息')}</h3>
              </div>
              <Form form={form} layout="vertical" className="users-ws-info-form">
                <Form.Item label={t('system.users.username', '用户名')} name="username" rules={[{ required: true, message: t('system.users.usernameRequired', '请输入用户名') }]}>
                  <Input />
                </Form.Item>
                <Form.Item
                  label={t('system.users.email', '邮箱')}
                  name="email"
                  rules={[
                    { required: true, message: t('system.users.emailRequired', '请输入邮箱') },
                    { type: 'email', message: t('system.users.emailInvalid', '邮箱格式不正确') },
                  ]}
                >
                  <Input />
                </Form.Item>
                <Form.Item label={t('system.users.fullName', '姓名')} name="fullName">
                  <Input />
                </Form.Item>
                <Form.Item label={t('common.status', '状态')} name="isActive" valuePropName="checked">
                  <Switch checkedChildren={t('common.active', '启用')} unCheckedChildren={t('common.inactive', '停用')} />
                </Form.Item>
              </Form>
              <Button type="primary" icon={<SaveOutlined />} loading={editLoading} onClick={() => void handleEditSubmit()}>
                {t('system.users.saveBasicInfo', '保存基本信息')}
              </Button>
            </section>

            {editingUser ? (
              <Descriptions size="small" column={{ xs: 1, sm: 2 }}>
                <Descriptions.Item label={t('system.users.lastLogin', '最近登录')}>
                  {formatUserLocalDateTime(editingUser.lastLoginAt, t('common.emptyValue'))}
                </Descriptions.Item>
                <Descriptions.Item label={t('system.users.lastLoginIp', '最近登录 IP')}>
                  {editingUser.lastLoginIp || t('common.emptyValue')}
                </Descriptions.Item>
                <Descriptions.Item label={t('system.users.createdAt', '创建时间')}>{formatPlainDateTime(editingUser.createdAt)}</Descriptions.Item>
                <Descriptions.Item label={t('system.users.updatedAt', '更新时间')}>{formatPlainDateTime(editingUser.updatedAt)}</Descriptions.Item>
              </Descriptions>
            ) : null}

            <HasPermission code={P.Users.ResetPassword}>
              <section className="users-ws-danger">
                <div>
                  <Typography.Text strong>{t('system.usersWorkspace.accountSecurity', '账号安全')}</Typography.Text>
                  <div>
                    <Typography.Text type="secondary">
                      {t('system.usersWorkspace.resetPasswordHint', '为该用户设置新密码，原密码立即失效。')}
                    </Typography.Text>
                  </div>
                </div>
                <Button danger icon={<LockOutlined />} disabled={!editingUser} onClick={() => editingUser && openResetPassword(editingUser)}>
                  {t('system.users.resetPassword', '重置密码')}
                </Button>
              </section>
            </HasPermission>
          </div>
        </Spin>
      ),
    },
    {
      key: 'assignments',
      label: t('system.usersWorkspace.tabAssignments', '角色与分店'),
      children: (
        <>
          <div className="users-ws-tab-body">
            <section aria-label={t('system.users.roles', '角色')}>
              <div className="users-ws-section-head">
                <h3>{t('system.users.roles', '角色')}</h3>
                <span className="users-ws-section-hint">{t('system.usersWorkspace.rolesHint', '决定账号的基础权限，可多选')}</span>
              </div>
              <HasPermission code={P.Users.ManageRoles} fallback={<Typography.Text type="secondary">{t('system.users.noRolePermission', '无权限管理角色')}</Typography.Text>}>
                <Spin spinning={roleLoading}>
                  {roleLoadStatus === 'error' ? (
                    <Alert
                      type="error"
                      showIcon
                      style={{ marginBottom: 12 }}
                      message={t('system.users.loadRolesFailed', '加载角色数据失败')}
                      action={(
                        <Button size="small" onClick={retryRoleData}>
                          {t('common.retry', '重试')}
                        </Button>
                      )}
                    />
                  ) : null}
                  {allRoles.length ? (
                    <div className="users-ws-role-grid">
                      {allRoles.map((role) => {
                        const checked = roleTargetKeys.includes(role.roleGUID)
                        const optionClassName = [
                          'users-ws-role-option',
                          checked ? 'users-ws-role-option-checked' : '',
                          canMutateLoadedAssignment(roleLoadStatus, roleSaving) ? '' : 'users-ws-role-option-disabled',
                        ].filter(Boolean).join(' ')
                        return (
                          <Checkbox
                            key={role.roleGUID}
                            className={optionClassName}
                            checked={checked}
                            disabled={!canMutateLoadedAssignment(roleLoadStatus, roleSaving)}
                            onChange={(event) => toggleRoleAssignment(role.roleGUID, event.target.checked)}
                          >
                            <span style={{ display: 'block', minWidth: 0 }}>
                              <RoleChip roleName={role.roleName} />
                              <span className="users-ws-subline" style={{ marginTop: 4 }}>
                                {role.description || t('common.emptyValue')}
                              </span>
                            </span>
                          </Checkbox>
                        )
                      })}
                    </div>
                  ) : roleLoadStatus === 'ready' ? (
                    <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('system.usersWorkspace.noRoles', '暂无可分配的角色')} />
                  ) : null}
                </Spin>
              </HasPermission>
            </section>

            <section aria-label={t('system.users.linkedStores', '关联分店')}>
              <div className="users-ws-section-head">
                <h3>{t('system.users.linkedStores', '关联分店')}</h3>
                <span className="users-ws-section-hint">
                  {t('system.usersWorkspace.storeCount', '共 {{count}} 家', { count: storeTargetKeys.length })}
                  {' · '}
                  {t('system.usersWorkspace.storesHint', '「可管理」的分店允许该用户管理本店数据')}
                </span>
              </div>
              <HasPermission code={P.Users.ManageStores} fallback={<Typography.Text type="secondary">{t('system.users.noStorePermission', '无权限管理分店')}</Typography.Text>}>
                <Spin spinning={storeLoading}>
                  {storeLoadStatus === 'error' ? (
                    <Alert
                      type="error"
                      showIcon
                      style={{ marginBottom: 12 }}
                      message={t('system.users.loadStoresFailed', '加载分店数据失败')}
                      action={(
                        <Button size="small" onClick={retryStoreData}>
                          {t('common.retry', '重试')}
                        </Button>
                      )}
                    />
                  ) : null}
                  <Select<string>
                    showSearch
                    value={null}
                    open={addStoreSelectOpen}
                    onOpenChange={setAddStoreSelectOpen}
                    searchValue={addStoreSearch}
                    onSearch={setAddStoreSearch}
                    optionFilterProp="label"
                    placeholder={t('system.usersWorkspace.addStorePlaceholder', '搜索并添加分店')}
                    aria-label={t('system.usersWorkspace.addStorePlaceholder', '搜索并添加分店')}
                    style={{ width: '100%', marginBottom: 10 }}
                    disabled={!canMutateLoadedAssignment(storeLoadStatus, storeSaving)}
                    options={addableStoreOptions}
                    onChange={(storeGuid) => addStoreAssignment(storeGuid)}
                  />
                  {selectedAssignmentStores.length ? (
                    <div className="users-ws-store-list">
                      {selectedAssignmentStores.map((store) => (
                        <div key={store.storeGUID} className="users-ws-store-row">
                          <ShopOutlined style={{ color: '#667085' }} />
                          <span className="users-ws-store-name">
                            {store.storeName}
                            <span className="users-ws-store-code">{store.storeCode}</span>
                          </span>
                          {storeBaselineKeys.includes(store.storeGUID) ? null : (
                            <Tag color="blue" bordered={false}>{t('system.usersWorkspace.newBadge', '新增')}</Tag>
                          )}
                          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8, color: '#4b5565', fontSize: 13 }}>
                            {t('system.users.manageableStore', '可管理')}
                            <Switch
                              size="small"
                              checked={storeManageableKeys.includes(store.storeGUID)}
                              disabled={storeControlsDisabled}
                              aria-label={`${t('system.users.manageableStore', '可管理')} ${store.storeName}`}
                              onChange={(checked) => toggleStoreManageable(store.storeGUID, checked, setStoreManageableKeys)}
                            />
                          </span>
                          <Button
                            type="text"
                            size="small"
                            icon={<CloseOutlined />}
                            disabled={storeControlsDisabled}
                            aria-label={t('system.usersWorkspace.removeStore', '移除分店 {{name}}', { name: store.storeName })}
                            onClick={() => handleStoreTargetChange(storeTargetKeys.filter((key) => key !== store.storeGUID))}
                          />
                        </div>
                      ))}
                    </div>
                  ) : storeLoadStatus === 'ready' ? (
                    <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('system.usersWorkspace.noStores', '尚未关联分店')} />
                  ) : null}
                </Spin>
              </HasPermission>
            </section>

            {permissionState ? (
              <div className="users-ws-summary">
                <SafetyCertificateOutlined />
                <span>
                  {t(
                    'system.usersWorkspace.effectivePermissionSummary',
                    '当前有效权限 {{effective}} 项（角色继承 {{inherited}} · 直接授权 {{direct}}）',
                    {
                      effective: effectivePermSet.size,
                      inherited: inheritedPermSet.size,
                      direct: directPermKeys.length,
                    },
                  )}
                </span>
                <Button type="link" size="small" style={{ marginLeft: 'auto' }} onClick={() => setEditTab('permissions')}>
                  {t('system.usersWorkspace.viewPermissionDetail', '查看明细')}
                </Button>
              </div>
            ) : null}
          </div>
          <PendingChangesBar
            visible={assignmentChangeCount > 0}
            summary={t('system.usersWorkspace.pendingChanges', '{{count}} 项未保存的更改', { count: assignmentChangeCount })}
            detail={assignmentChangeDetail}
            discardLabel={t('system.usersWorkspace.discardChanges', '放弃更改')}
            saveLabel={t('system.usersWorkspace.saveChanges', '保存更改')}
            saving={roleSaving || storeSaving}
            saveDisabled={
              (hasRoleAssignmentChanges && !canMutateLoadedAssignment(roleLoadStatus, roleSaving)) ||
              (hasStoreAssignmentChanges && !canMutateLoadedAssignment(storeLoadStatus, storeSaving))
            }
            onDiscard={discardAssignmentChanges}
            onSave={() => void handleSaveAssignments()}
          />
        </>
      ),
    },
    {
      key: 'permissions',
      label: t('system.users.functionPermissions', '功能权限'),
      children: (
        <Spin spinning={permLoading}>
          {!canEditUserPermissions ? (
            <Alert
              type="info"
              showIcon
              message={t(
                'system.users.permissionAccessUnavailable',
                '当前账号没有权限委派资格，无法读取或修改该用户的权限范围。',
              )}
            />
          ) : permLoadError ? (
            <Alert type="error" showIcon message={permLoadError} />
          ) : !permissionState ? (
            <Empty description={t('system.users.noPermData', '暂无权限数据')} />
          ) : (
            <Space direction="vertical" size={12} style={{ width: '100%' }}>
              <Typography.Text type="secondary">
                {t(
                  'system.users.permEditDesc',
                  '当前有效权限共 {{effectiveCount}} 项，其中角色继承 {{inheritedCount}} 项，直接授权 {{directCount}} 项。角色标签表示继承来源。',
                  {
                    effectiveCount: effectivePermSet.size,
                    inheritedCount: inheritedPermSet.size,
                    directCount: directPermKeys.length,
                  },
                )}
              </Typography.Text>

              {isPermissionStateReadOnly ? (
                <Alert
                  type="info"
                  showIcon
                  message={t(
                    'system.users.superAdminPermissionsReadOnly',
                    '管理员默认拥有全部权限和移动端菜单，此处仅供查看。',
                  )}
                />
              ) : null}

              <Segmented
                value={permissionPlatform}
                options={[
                  {
                    label: `${t('system.users.webPermissions', 'Web 端')} (${platformPermissionCategories.web.reduce((count, category) => count + category.permissions.length, 0)})`,
                    value: 'web',
                  },
                  {
                    label: `${t('system.users.posAccountPermissions', 'POS 端')} (${platformPermissionCategories.pos.reduce((count, category) => count + category.permissions.length, 0)})`,
                    value: 'pos',
                  },
                ]}
                onChange={(value) => setPermissionPlatform(value as PermissionPlatform)}
              />

              <Alert
                type="info"
                showIcon
                message={permissionPlatform === 'pos'
                  ? t(
                      'system.users.posAccountPermissionTip',
                      '此处为账号级 POS 权限；分店覆盖请使用用户列表中的收银权限。',
                    )
                  : t(
                      'system.users.webPermissionTip',
                      'Web 端包含网页与 HbwebExpo 共用的非 POS 账号权限。',
                    )}
              />

              {visiblePermissionCategories.length === 0 ? (
                <Empty description={t('system.users.noPermData', '暂无权限数据')} />
              ) : (
                <Tree
                  key={permissionPlatform}
                  treeData={permTreeData}
                  checkedKeys={checkedPermKeys}
                  checkable
                  disabled={!canEditCurrentPermissionState || permSaving}
                  selectable={false}
                  defaultExpandAll
                  onCheck={handlePermissionCheck}
                  style={{ background: '#fafafa', padding: 12, borderRadius: 8 }}
                />
              )}

              {canEditCurrentPermissionState ? (
                <div style={{ textAlign: 'right' }}>
                  <Button
                    type="primary"
                    icon={<SaveOutlined />}
                    loading={permSaving}
                    disabled={!hasDirectPermChanges}
                    onClick={() => void handleSavePermissions()}
                  >
                    {t('system.users.savePermissionAssign', '保存权限分配')}
                  </Button>
                </div>
              ) : null}
            </Space>
          )}
        </Spin>
      ),
    },
    {
      key: 'mobile-menu',
      label: t('system.users.mobileMenu', '移动端菜单'),
      children: (
        <Spin spinning={permLoading}>
          {!canEditUserPermissions ? (
            <Alert
              type="info"
              showIcon
              message={t(
                'system.users.permissionAccessUnavailable',
                '当前账号没有权限委派资格，无法读取或修改该用户的权限范围。',
              )}
            />
          ) : permLoadError ? (
            <Alert type="error" showIcon message={permLoadError} />
          ) : (
            <UserMobileMenuPermissionManager
              permissionState={permissionState}
              directPermissionCodes={directPermKeys}
              assignablePermissionCodes={Array.from(allPermissionCodes)}
              scoped={isCurrentUserScoped}
              canEdit={canEditCurrentPermissionState}
              saving={permSaving}
              hasChanges={hasDirectPermChanges}
              onChange={setDirectPermKeys}
              onSave={() => void handleSavePermissions()}
            />
          )}
        </Spin>
      ),
    },
  ]

  const loginRecordsTab = {
    key: 'login-records',
    label: t('system.users.loginRecords', '登录记录'),
    children: (
      <div className="users-ws-tab-body">
        <div className="users-ws-tab-toolbar">
          <Typography.Text type="secondary">
            {t('system.usersWorkspace.loginRecordsTotal', '共 {{count}} 条登录记录', { count: loginRecordsTotal })}
          </Typography.Text>
          <Button
            icon={<ReloadOutlined />}
            loading={loginRecordsLoading}
            onClick={() => {
              if (loginRecordsUser) {
                void loadLoginRecords(loginRecordsUser, loginRecordsPage, loginRecordsPageSize)
              }
            }}
          >
            {t('common.refresh', '刷新')}
          </Button>
        </div>
        <MeasuredTable<UserLoginRecordDto> metricId="system.users.table-2"
          rowKey="sessionId"
          size="small"
          loading={loginRecordsLoading}
          columns={loginRecordColumns}
          dataSource={loginRecords}
          scroll={{ x: 860 }}
          pagination={{
            current: loginRecordsPage,
            pageSize: loginRecordsPageSize,
            total: loginRecordsTotal,
            showSizeChanger: true,
            onChange: (nextPage, nextPageSize) => {
              if (loginRecordsUser) {
                void loadLoginRecords(loginRecordsUser, nextPage, nextPageSize)
              }
            },
          }}
        />
      </div>
    ),
  }

  const posPermissionPanel = (
    <div className="users-ws-tab-body">
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        <div>
          <Typography.Text strong>{t('system.users.targetStore', '目标分店')}</Typography.Text>
          <Select
            aria-label={t('system.users.targetStore', '目标分店')}
            value={selectedPosPermissionStoreGuid}
            loading={posPermissionStoresLoading}
            disabled={posPermissionStoresLoading || posPermissionSaving || posPermissionRestoring || !posPermissionStores.length}
            style={{ width: '100%', marginTop: 8 }}
            placeholder={t('system.users.selectPosPermissionStore', '请选择目标用户关联的分店')}
            options={posPermissionStores.map((store) => ({
              label: `${store.storeName} (${store.storeCode})`,
              value: store.storeGUID,
            }))}
            onChange={(storeGuid) => void handlePosPermissionStoreChange(storeGuid)}
          />
        </div>

        {posPermissionStoresLoading || posPermissionLoading ? (
          <Skeleton active title paragraph={{ rows: 8 }} />
        ) : posPermissionError ? (
          <Alert
            type="error"
            showIcon
            message={posPermissionError}
            action={
              posPermissionUser && selectedPosPermissionStoreGuid ? (
                <Button
                  size="small"
                  onClick={() => void loadPosPermissionState(posPermissionUser.userGUID, selectedPosPermissionStoreGuid)}
                >
                  {t('common.retry', '重试')}
                </Button>
              ) : (
                <Button size="small" onClick={() => posPermissionUser && void handleOpenPosPermissions(posPermissionUser)}>
                  {t('common.retry', '重试')}
                </Button>
              )
            }
          />
        ) : !posPermissionStores.length ? (
          <Empty description={t('system.users.noPosPermissionStores', '目标用户没有可管理的关联分店')} />
        ) : !posPermissionState ? (
          <Empty description={t('system.users.noPosPermissionData', '暂无收银权限数据')} />
        ) : (
          <>
            <Alert
              type="info"
              showIcon
              message={
                isInheritedPosPermissionMode(posPermissionState.mode)
                  ? t('system.users.posPermissionInheritedMode', '当前使用账号权限继承')
                  : t('system.users.posPermissionOverrideMode', '当前使用分店权限覆盖')
              }
              description={t(
                'system.users.posPermissionSummary',
                '可分配 {{assignable}} 项，账号继承 {{inherited}} 项，当前有效 {{effective}} 项。勾选项表示保存后的分店有效权限。',
                {
                  assignable: posPermissionState.assignablePermissions.length,
                  inherited: inheritedPosPermissionSet.size,
                  effective: effectivePosPermissionSet.size,
                },
              )}
            />

            {posPermissionSections.length ? posPermissionSections.map((section) => (
              <Card key={section.module} title={section.displayName} size="small">
                <Space direction="vertical" size={16} style={{ width: '100%' }}>
                  {section.groups.map((group) => {
                    const groupPermissionCodes = group.permissions.map((permission) => permission.code)
                    const groupSelectionState = getPosPermissionGroupSelectionState(
                      selectedPosPermissionCodes,
                      groupPermissionCodes,
                    )

                    return (
                      <div key={group.key}>
                        <Checkbox
                          checked={groupSelectionState.checked}
                          indeterminate={groupSelectionState.indeterminate}
                          disabled={posPermissionSaving || posPermissionRestoring}
                          onChange={(event) => {
                            setSelectedPosPermissionCodes((current) =>
                              setPosPermissionGroupSelection(
                                current,
                                groupPermissionCodes,
                                event.target.checked,
                              ),
                            )
                          }}
                        >
                          <Typography.Text strong>{group.displayName}</Typography.Text>
                        </Checkbox>
                        <List
                          size="small"
                          dataSource={group.permissions}
                          locale={{ emptyText: t('system.users.noPosPermissionData', '暂无收银权限数据') }}
                          renderItem={(permission) => {
                            const isSelected = selectedPosPermissionSet.has(permission.code)
                            const isEffective = effectivePosPermissionSet.has(permission.code)
                            const hasDraftChange = isSelected !== isEffective
                            return (
                              <List.Item>
                                <Space direction="vertical" size={4} style={{ width: '100%' }}>
                                  <Checkbox
                                    checked={isSelected}
                                    disabled={posPermissionSaving || posPermissionRestoring}
                                    onChange={(event) => {
                                      setSelectedPosPermissionCodes((current) => {
                                        const next = new Set(current)
                                        if (event.target.checked) next.add(permission.code)
                                        else next.delete(permission.code)
                                        return Array.from(next)
                                      })
                                    }}
                                  >
                                    {permission.name}
                                  </Checkbox>
                                  <Space wrap size={[4, 4]} style={{ paddingLeft: 24 }}>
                                    {inheritedPosPermissionSet.has(permission.code) ? (
                                      <Tag>{t('system.users.accountInherited', '账号继承')}</Tag>
                                    ) : null}
                                    {overriddenPosPermissionSet.has(permission.code) ? (
                                      <Tag color="processing">{t('system.users.storeOverridden', '分店已覆盖')}</Tag>
                                    ) : null}
                                    <Tag color={isEffective ? 'success' : 'default'}>
                                      {isEffective
                                        ? t('system.users.permissionEffective', '当前有效')
                                        : t('system.users.permissionIneffective', '当前无效')}
                                    </Tag>
                                    {hasDraftChange ? (
                                      <Tag color="warning">
                                        {isSelected
                                          ? t('system.users.pendingEnable', '待保存启用')
                                          : t('system.users.pendingDisable', '待保存停用')}
                                      </Tag>
                                    ) : null}
                                  </Space>
                                  {permission.description ? (
                                    <Typography.Text type="secondary" style={{ paddingLeft: 24, fontSize: 12 }}>
                                      {permission.description}
                                    </Typography.Text>
                                  ) : null}
                                </Space>
                              </List.Item>
                            )
                          }}
                        />
                      </div>
                    )
                  })}
                </Space>
              </Card>
            )) : (
              <Empty description={t('system.users.noAssignablePosPermissions', '当前分店没有可分配的收银权限')} />
            )}

            <Space style={{ width: '100%', justifyContent: 'flex-end' }}>
              <Button
                disabled={isInheritedPosPermissionMode(posPermissionState.mode) || posPermissionSaving || posPermissionRestoring}
                loading={posPermissionRestoring}
                onClick={handleRestorePosPermissionInheritance}
              >
                {t('system.users.restoreInheritance', '恢复继承')}
              </Button>
              <Button
                type="primary"
                icon={<SaveOutlined />}
                disabled={
                  !shouldEnablePosPermissionSave(posPermissionState.mode, hasPosPermissionChanges) ||
                  posPermissionLoading ||
                  posPermissionRestoring
                }
                loading={posPermissionSaving}
                onClick={() => void handleSavePosPermissions()}
              >
                {t('system.users.saveStorePosPermissions', '保存分店权限')}
              </Button>
            </Space>
          </>
        )}
      </Space>
    </div>
  )

  const viewInfoTab = {
    key: 'info',
    label: t('system.usersWorkspace.tabProfile', '资料'),
    children: (
      <Spin spinning={detailLoading}>
        <div className="users-ws-tab-body">
          {detailUser ? (
            <>
              <Descriptions bordered size="small" column={{ xs: 1, sm: 2 }}>
                <Descriptions.Item label={t('system.users.username', '用户名')}>{detailUser.username}</Descriptions.Item>
                <Descriptions.Item label={t('system.users.fullName', '姓名')}>{detailUser.fullName || t('common.emptyValue')}</Descriptions.Item>
                <Descriptions.Item label={t('system.users.email', '邮箱')}>{detailUser.email}</Descriptions.Item>
                <Descriptions.Item label={t('common.status', '状态')}>
                  <StatusDot
                    active={detailUser.isActive}
                    label={detailUser.isActive ? t('common.active', '启用') : t('common.inactive', '停用')}
                  />
                </Descriptions.Item>
                <Descriptions.Item label={t('system.users.lastLogin', '最近登录')}>
                  {formatUserLocalDateTime(detailUser.lastLoginAt, t('common.emptyValue'))}
                </Descriptions.Item>
                <Descriptions.Item label={t('system.users.lastLoginIp', '最近登录 IP')}>{detailUser.lastLoginIp || t('common.emptyValue')}</Descriptions.Item>
                <Descriptions.Item label={t('system.users.createdAt', '创建时间')}>{formatPlainDateTime(detailUser.createdAt)}</Descriptions.Item>
                <Descriptions.Item label={t('system.users.updatedAt', '更新时间')}>{formatPlainDateTime(detailUser.updatedAt)}</Descriptions.Item>
                <Descriptions.Item label={t('system.users.roles', '角色')} span="filled">
                  {detailUser.roleNames?.length ? (
                    <div className="users-ws-chips">
                      {detailUser.roleNames.map((item) => <RoleChip key={item} roleName={item} />)}
                    </div>
                  ) : t('common.emptyValue')}
                </Descriptions.Item>
              </Descriptions>

              <section>
                <div className="users-ws-section-head">
                  <h3>{t('system.users.linkedStores', '关联分店')}</h3>
                  <span className="users-ws-section-hint">
                    {t('system.usersWorkspace.storeCount', '共 {{count}} 家', { count: detailStores.length })}
                  </span>
                </div>
                {detailStores.length ? (
                  <div className="users-ws-store-list">
                    {detailStores.map((store) => (
                      <div key={store.storeGUID} className="users-ws-store-row">
                        <ShopOutlined style={{ color: '#667085' }} />
                        <span className="users-ws-store-name">
                          {store.storeName}
                          <span className="users-ws-store-code">{store.storeCode}</span>
                        </span>
                        {store.isManageable ? (
                          <Tag color="processing">{t('system.users.manageableStore', '可管理')}</Tag>
                        ) : (
                          <Tag>{t('system.users.linkedOnlyStore', '普通关联')}</Tag>
                        )}
                      </div>
                    ))}
                  </div>
                ) : (
                  <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('system.users.noLinkedStores', '暂无关联分店')} />
                )}
              </section>

              <section>
                <div className="users-ws-section-head">
                  <h3>{t('system.usersWorkspace.effectivePermissions', '有效权限')}</h3>
                  <span className="users-ws-section-hint">{detailUser.permissions?.length ?? 0}</span>
                </div>
                {detailUser.permissions?.length ? (
                  <div className="users-ws-chips">
                    {detailUser.permissions.map((permission) => <NeutralChip key={permission}>{permission}</NeutralChip>)}
                  </div>
                ) : (
                  <Typography.Text type="secondary">{t('common.emptyValue')}</Typography.Text>
                )}
              </section>
            </>
          ) : detailLoading ? null : (
            <Empty description={t('system.users.userNotFound', '未找到用户信息')} />
          )}
        </div>
      </Spin>
    ),
  }

  const posPermissionTabs = canManagePosTerminalPermissions
    ? [{ key: 'pos', label: t('system.users.posPermissions', '收银权限'), children: posPermissionPanel }]
    : []
  const drawerTabItems: TabsProps['items'] = editOpen
    ? [...editTabItems, loginRecordsTab, ...posPermissionTabs]
    : [viewInfoTab, loginRecordsTab, ...posPermissionTabs]
  const userDrawerOpen = editOpen || detailOpen

  // 登录记录与收银权限标签页首次切到时才加载，并始终跟随抽屉当前用户。
  useEffect(() => {
    if (!userDrawerOpen || !drawerUser || editTab !== 'login-records') return
    if (loginRecordsUser?.userGUID === drawerUser.userGUID) return
    void handleOpenLoginRecords(drawerUser)
  }, [userDrawerOpen, drawerUser, editTab])

  useEffect(() => {
    if (!userDrawerOpen || !drawerUser || editTab !== 'pos' || !canManagePosTerminalPermissions) return
    if (posPermissionUser?.userGUID === drawerUser.userGUID) return
    void handleOpenPosPermissions(drawerUser)
  }, [userDrawerOpen, drawerUser, editTab, canManagePosTerminalPermissions])

  /** 关闭抽屉前若「角色与分店」或功能权限还有未保存的草稿，先确认再丢弃。 */
  const confirmCloseUserDrawer = (close: () => void) => {
    if (assignmentChangeCount === 0 && !hasDirectPermChanges) {
      close()
      return
    }
    Modal.confirm({
      title: t('system.usersWorkspace.discardConfirmTitle', '放弃未保存的更改？'),
      content: t('system.usersWorkspace.discardConfirmContent', '关闭后 {{count}} 项未保存的更改将被丢弃。', {
        count: assignmentChangeCount + (hasDirectPermChanges ? 1 : 0),
      }),
      okText: t('system.usersWorkspace.discardChanges', '放弃更改'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel', '取消'),
      onOk: close,
    })
  }

  const renderUserDrawerTitle = (user: UserDto | null): ReactNode => {
    if (!user) return t('system.users.userDetail', '用户详情')
    const displayName = getUserDisplayName(user)
    return (
      <div className="users-ws-drawer-header">
        <span className={user.isActive ? 'users-ws-avatar users-ws-avatar-lg' : 'users-ws-avatar users-ws-avatar-lg users-ws-avatar-inactive'} aria-hidden="true">
          {getUserInitial(displayName)}
        </span>
        <div style={{ minWidth: 0 }}>
          <div className="users-ws-drawer-title">
            <span>{displayName}</span>
            <span style={{ fontSize: 13, fontWeight: 400 }}>
              <StatusDot active={user.isActive} label={user.isActive ? t('common.active', '启用') : t('common.inactive', '停用')} />
            </span>
          </div>
          <span className="users-ws-subline">{getUserSecondaryLine(user)}</span>
          <div className="users-ws-drawer-facts">
            <span>
              {t('system.usersWorkspace.lastLogin', '最近登录')}{' '}
              <strong>{formatUserLocalDateTime(user.lastLoginAt, t('system.usersWorkspace.lastLoginNever', '从未登录'))}</strong>
            </span>
            {user.lastLoginIp ? (
              <span>IP <strong>{user.lastLoginIp}</strong></span>
            ) : null}
            {user.createdAt ? (
              <span>
                {t('system.usersWorkspace.createdAt', '创建于')} <strong>{formatPlainDateTime(user.createdAt)}</strong>
              </span>
            ) : null}
          </div>
        </div>
      </div>
    )
  }

  return (
    <PageContainer
      title={t('menu.systemUsers', '用户管理')}
      subtitle={t('system.users.pageSubtitle', '管理用户的基本信息、角色、分店和权限。')}
      extra={(
        <HasPermission code={P.Users.Create}>
          <Button type="primary" icon={<PlusOutlined />} onClick={() => void handleOpenCreate()}>
            {t('system.users.createUser', '创建用户')}
          </Button>
        </HasPermission>
      )}
    >
      <Card>
        <div className="users-ws-toolbar">
          <Input
            className="users-ws-search"
            placeholder={t('system.users.searchPlaceholder', '搜索用户名 / 姓名 / 邮箱')}
            aria-label={t('system.users.searchPlaceholder', '搜索用户名 / 姓名 / 邮箱')}
            value={keyword}
            onChange={(event) => setKeyword(event.target.value)}
            onPressEnter={() => {
              lastKeywordRef.current = keyword
              void loadData(1, pageSize, sortBy, sortOrder)
            }}
            prefix={<SearchOutlined />}
            allowClear
          />
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            placeholder={t('system.users.filterByStore', '按分店过滤')}
            aria-label={t('system.users.filterByStore', '按分店过滤')}
            style={{ width: 220 }}
            value={selectedStoreGuid}
            onChange={(value) => setSelectedStoreGuid(value)}
            options={visibleStoreOptions}
          />
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            placeholder={t('system.users.filterByRole', '按角色过滤')}
            aria-label={t('system.users.filterByRole', '按角色过滤')}
            style={{ width: 180 }}
            value={selectedRoleGuid}
            onChange={(value) => setSelectedRoleGuid(value)}
            options={roleOptions}
          />
          <Segmented<UserStatusFilter>
            aria-label={t('system.usersWorkspace.statusFilter', '账号状态')}
            value={statusFilter}
            onChange={(value) => setStatusFilter(value)}
            options={[
              { label: t('common.all', '全部'), value: 'all' },
              { label: t('common.active', '启用'), value: 'active' },
              { label: t('common.inactive', '停用'), value: 'inactive' },
            ]}
          />
          <Tooltip title={t('common.refresh', '刷新')}>
            <Button
              className="users-ws-refresh"
              icon={<ReloadOutlined />}
              aria-label={t('common.refresh', '刷新')}
              onClick={() => void loadData(page, pageSize, sortBy, sortOrder)}
            />
          </Tooltip>
        </div>

        {canBatchSelectUsers && selectedUsers.length > 0 ? (
          <div className="users-ws-batch-bar">
            <UserBatchActions
              selectedUsers={selectedUsers}
              currentUserGuid={currentUser?.userGUID}
              canEditStatus={canEditUsers}
              canManageRoles={canBatchManageRoles}
              roleOptions={roleOptions}
              isOutOfScope={(user) => isCurrentUserScoped && hasForbiddenRoleForScopedManager(user)}
              onClearSelection={() => setSelectedUsers([])}
              onCompleted={() => {
                setSelectedUsers([])
                void loadData(page, pageSize, sortBy, sortOrder)
              }}
            />
          </div>
        ) : null}

        <MeasuredTable metricId="system.users.table-1"
          rowKey="userGUID"
          className="users-ws-table"
          loading={loading}
          columns={columns}
          dataSource={data}
          scroll={{ x: canBatchSelectUsers ? 1114 : 1070 }}
          rowSelection={canBatchSelectUsers ? {
            selectedRowKeys: selectedUsers.map((user) => user.userGUID),
            preserveSelectedRowKeys: true,
            fixed: true,
            columnWidth: 44,
            onChange: (selectedRowKeys, selectedRows) => {
              if (selectedRowKeys.length > USER_BATCH_SELECTION_LIMIT) {
                message.warning(t('system.usersBatch.selectionLimit', '一次最多勾选 {{count}} 位用户', { count: USER_BATCH_SELECTION_LIMIT }))
                return
              }
              setSelectedUsers((previous) => mergeSelectedUsers(previous, selectedRowKeys, selectedRows))
            },
          } : undefined}
          onRow={(record) => ({
            onClick: (event) => {
              // 整行点击打开抽屉；操作区按钮、下拉菜单与勾选框自己处理点击。
              if ((event.target as HTMLElement).closest('.users-ws-row-actions, .ant-dropdown, .ant-table-selection-column, .ant-checkbox-wrapper')) return
              openUserDrawer(record)
            },
          })}
          onChange={(pagination, _filters, sorter, extra) => {
            const nextPagination = resolveSystemListPagination(extra.action, pagination, pageSize)
            if (extra.action === 'paginate') {
              void loadData(nextPagination.page, nextPagination.pageSize, sortBy, sortOrder)
              return
            }

            const currentSorter = Array.isArray(sorter) ? sorter[0] : sorter
            const field = currentSorter?.field || currentSorter?.column?.dataIndex
            const order = currentSorter?.order as 'ascend' | 'descend' | undefined

            if (field && order) {
              setSortBy(String(field))
              setSortOrder(order)
              void loadData(nextPagination.page, nextPagination.pageSize, String(field), order)
            } else {
              setSortBy(undefined)
              setSortOrder(null)
              void loadData(nextPagination.page, nextPagination.pageSize, undefined, undefined)
            }
          }}
          pagination={{
            current: page,
            pageSize,
            total,
            showSizeChanger: true,
            showTotal: (count) => t('system.usersWorkspace.totalUsers', '共 {{count}} 位用户', { count }),
          }}
        />
      </Card>

      {cashierBarcodeUser && access.isAdmin && canManagePosTerminalPermissions && (
        <UserCashierBarcodeModal
          key={cashierBarcodeUser.userGUID}
          user={cashierBarcodeUser}
          onClose={() => setCashierBarcodeUser(null)}
        />
      )}

      <Drawer
        title={editingUser ? renderUserDrawerTitle(editingUser) : renderUserDrawerTitle(detailUser ?? drawerUser)}
        width="min(860px, 100vw)"
        rootClassName="users-ws-drawer"
        open={userDrawerOpen}
        onClose={() => confirmCloseUserDrawer(() => {
          editingUserGuidRef.current = null
          currentEditSessionIdRef.current = null
          editUserRequestGuardRef.current.invalidate()
          editSessionGuardRef.current.invalidate()
          roleRequestGuardRef.current.invalidate()
          storeRequestGuardRef.current.invalidate()
          permissionRequestGuardRef.current.invalidate()
          permissionSaveGuardRef.current.invalidate()
          setEditOpen(false)
          setEditLoading(false)
          setEditingUser(null)
          setAllRoles([])
          setRoleTargetKeys([])
          setRoleBaselineKeys([])
          setRoleLoadStatus('idle')
          setAllStores([])
          setStoreTargetKeys([])
          setStoreManageableKeys([])
          setStoreBaselineKeys([])
          setStoreManageableBaselineKeys([])
          setStoreLoadStatus('idle')
          setPermCategories([])
          setPermissionState(null)
          setDirectPermKeys([])
          setOriginalDirectPermKeys([])
          setPermLoading(false)
          setPermSaving(false)
          setPermLoadError(null)
          setRoleSaving(false)
          setStoreSaving(false)
          setPermissionPlatform('web')
          form.resetFields()
          // 只读查看、登录记录与收银权限同在这个抽屉，关闭时一并清理。
          setDetailOpen(false)
          setDetailUser(null)
          setDetailStores([])
          closeLoginRecords()
          resetPosPermissionState()
          setDrawerUser(null)
          setEditTab('info')
        })}
        destroyOnHidden
      >
        <Tabs
          className="users-ws-drawer-tabs"
          activeKey={editTab}
          onChange={(key) => setEditTab(key as UserDrawerTab)}
          items={drawerTabItems}
        />
      </Drawer>

      <Modal
        title={resetPwdTarget
          ? t('system.usersWorkspace.resetPasswordTitle', '重置密码 - {{name}}', { name: resetPwdTarget.username })
          : t('system.users.resetPassword', '重置密码')}
        open={resetPwdOpen}
        onCancel={() => {
          setResetPwdOpen(false)
          setResetPwdTarget(null)
          resetPwdForm.resetFields()
        }}
        onOk={() => void handleResetPassword()}
        confirmLoading={resetPwdLoading}
        destroyOnHidden
      >
        <Form form={resetPwdForm} layout="vertical">
          <Form.Item
            label={t('system.users.newPasswordLabel', '请输入新密码')}
            name="newPassword"
            rules={[
              { required: true, message: t('system.users.newPasswordRequired', '请输入新密码') },
              { min: 6, message: t('system.users.passwordMinLength', '密码至少6位') },
            ]}
          >
            <Input.Password placeholder={t('system.users.newPasswordPlaceholder', '输入新密码')} />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title={t('system.users.createUser', '创建用户')}
        width={860}
        open={createOpen}
        onCancel={() => {
          if (createLoading) return
          setCreateOpen(false)
          clearCreateFormState()
        }}
        footer={createTab === 'info' ? [
          <Button key="cancel" disabled={createLoading} onClick={() => {
            if (createLoading) return
            setCreateOpen(false)
            clearCreateFormState()
          }}>
            {t('common.cancel', '取消')}
          </Button>,
          <Button key="submit" type="primary" loading={createLoading} onClick={() => void handleCreateSubmit()}>
            {t('common.create', '新建')}
          </Button>,
        ] : null}
        destroyOnHidden
      >
        {createFeedback && !createFeedback.field ? (
          <Alert
            type={createFeedback.resultUnconfirmed ? 'warning' : 'error'}
            showIcon
            message={createFeedback.resultUnconfirmed
              ? t('system.users.createUserResultUnknownTitle', '创建结果待确认')
              : t('system.users.createUserFeedbackTitle', '无法创建用户')}
            description={createFeedback.message}
            style={{ marginBottom: 16 }}
          />
        ) : null}
        <Tabs
          activeKey={createTab}
          onChange={setCreateTab}
          items={[
            {
              key: 'info',
              label: t('system.users.basicInfo', '基本信息'),
              children: (
                <Form form={createForm} layout="vertical" style={{ maxWidth: 480 }}>
                  <Form.Item
                    label={t('system.users.username', '用户名')}
                    name="username"
                    rules={[
                      { required: true, message: t('system.users.usernameRequired', '请输入用户名') },
                      { min: 3, max: 50, message: t('system.users.usernameLengthInvalid', '用户名长度3-50个字符') },
                    ]}
                  >
                    <Input />
                  </Form.Item>
                  <Form.Item
                    label={t('system.users.email', '邮箱')}
                    name="email"
                    rules={[
                      { required: true, message: t('system.users.emailRequired', '请输入邮箱') },
                      { type: 'email', message: t('system.users.emailInvalid', '邮箱格式不正确') },
                    ]}
                  >
                    <Input />
                  </Form.Item>
                  <Form.Item
                    label={t('system.users.password', '密码')}
                    name="password"
                    rules={[
                      { required: true, message: t('system.users.passwordRequired', '请输入密码') },
                      { min: 6, message: t('system.users.passwordMinLength', '密码至少6位') },
                    ]}
                  >
                    <Input.Password />
                  </Form.Item>
                  <Form.Item
                    label={t('system.users.confirmPassword', '确认密码')}
                    name="confirmPassword"
                    dependencies={['password']}
                    rules={[
                      { required: true, message: t('system.users.confirmPasswordRequired', '请确认密码') },
                      ({ getFieldValue }) => ({
                        validator(_, value) {
                          if (!value || getFieldValue('password') === value) return Promise.resolve()
                          return Promise.reject(new Error(t('system.users.confirmPasswordMismatch', '两次输入的密码不一致')))
                        },
                      }),
                    ]}
                  >
                    <Input.Password />
                  </Form.Item>
                  <Form.Item label={t('system.users.fullName', '姓名')} name="fullName">
                    <Input />
                  </Form.Item>
                  <Form.Item label={t('common.status', '状态')} name="isActive" valuePropName="checked" initialValue={true}>
                    <Switch checkedChildren={t('common.active', '启用')} unCheckedChildren={t('common.inactive', '停用')} />
                  </Form.Item>
                </Form>
              ),
            },
            {
              key: 'roles',
              label: t('system.users.roles', '角色'),
              children: (
                <Spin spinning={createRoleLoading}>
                  <div style={{ marginBottom: 12 }}>
                    <Typography.Text type="secondary">
                      {t('system.users.createSelectedRoles', '已选择 {{count}} 个角色', { count: createRoleTargetKeys.length })}
                    </Typography.Text>
                  </div>
                  <Transfer
                    dataSource={allRoles.map((role) => ({
                      key: role.roleGUID,
                      title: role.roleName,
                      description: role.description || '',
                    }))}
                    targetKeys={createRoleTargetKeys}
                    onChange={(nextTargetKeys: Key[]) => {
                      setCreateRoleTargetKeys(nextTargetKeys.map(String))
                    }}
                    render={(item) => item.title}
                    titles={[t('system.users.availableRoles', '可选角色'), t('system.users.assignedRolesLabel', '已分配角色')]}
                    listStyle={{ width: 320, height: 400 }}
                    showSearch
                  />
                </Spin>
              ),
            },
            {
              key: 'stores',
              label: t('system.users.stores', '分店'),
              children: (
                <Spin spinning={createStoreLoading}>
                  <div style={{ marginBottom: 12 }}>
                    <Typography.Text type="secondary">
                      {t('system.users.createSelectedStores', '已选择 {{count}} 个分店', { count: createStoreTargetKeys.length })}
                    </Typography.Text>
                  </div>
                  <Transfer
                    dataSource={sortedStores.map((store) => ({
                      key: store.storeGUID,
                      title: `${store.storeName} (${store.storeCode})`,
                      description: store.address || '',
                    }))}
                    targetKeys={createStoreTargetKeys}
                    onChange={(nextTargetKeys: Key[]) => {
                      handleCreateStoreTargetChange(nextTargetKeys)
                    }}
                    render={(item) => item.title}
                    titles={[t('system.users.availableStores', '可选分店'), t('system.users.assignedStoresLabel', '已分配分店')]}
                    listStyle={{ width: 320, height: 400 }}
                    showSearch
                  />
                  {renderManageableStoreControls(createStoreTargetKeys, createStoreManageableKeys, setCreateStoreManageableKeys)}
                </Spin>
              ),
            },
          ]}
        />
      </Modal>
    </PageContainer>
  )
}
