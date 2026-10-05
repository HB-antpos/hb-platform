import { EditOutlined, PlusOutlined, ReloadOutlined, SearchOutlined, TeamOutlined } from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Descriptions,
  Empty,
  Form,
  Input,
  Modal,
  Pagination,
  Skeleton,
  Spin,
  Switch,
  Tabs,
  Tag,
  Tooltip,
  message,
} from 'antd'
import type { TabsProps } from 'antd'
import dayjs from 'dayjs'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { HasPermission, usePermission } from '../../../components/Access'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { useAuthStore } from '../../../store/auth'
import { P } from '../../../types/permissions'
import { applyRolePermissionMutation, isImplicitAllRole } from '../../../utils/roleMenuPreview'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'
import { getRoleAccentColor } from '../../../utils/userTableColors'
import {
  assignPermissionsToRole,
  createRole,
  getPermissionCatalog,
  getRolePermissionState,
  getRoles,
  updateRole,
} from '../../../services/roleService'
import type {
  CreateRoleDto,
  PermissionCatalogDto,
  RoleDto,
  RolePermissionStateDto,
  RoleQueryDto,
  UpdateRoleDto,
} from '../../../types/role'
import { AccentDot, PendingChangesBar, StatusDot } from '../accessAdminUi'
import RoleMembersPanel from './RoleMembersPanel'
import RoleMenuPreviewPanel from './RoleMenuPreviewPanel'
import RolePermissionPanel from './RolePermissionPanel'
import { diffPermissionCodes, filterRoles } from './rolesWorkspaceLogic'
import rolesPageMessagesEn from './rolesPageMessages.en.json'
import rolesPageMessagesZh from './rolesPageMessages.zh.json'
import './rolesPage.css'

registerPageMessages({ zh: rolesPageMessagesZh, en: rolesPageMessagesEn })

type DesiredRoleListQuery = RoleQueryDto & {
  page: number
  pageSize: number
}

type RoleWorkspaceTab = 'permissions' | 'menu' | 'members' | 'info'

// 角色数量很少：一次取足并在本地检索，切换角色和搜索都不必等待请求。
const ROLE_LIST_PAGE_SIZE = 200

/** Admin 等隐式全权限角色显示有效权限；其余角色维护的是显式授予的权限。 */
function getEditablePermissionCodes(state: RolePermissionStateDto) {
  return state.isSuperAdmin ? state.effectivePermissionCodes : state.explicitPermissionCodes
}

export default function SystemRolesPage() {
  const { t } = useTranslation()
  const [loading, setLoading] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [data, setData] = useState<RoleDto[]>([])
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(ROLE_LIST_PAGE_SIZE)
  const [total, setTotal] = useState(0)
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)
  const desiredListQueryRef = useRef<DesiredRoleListQuery>({
    page,
    pageSize,
  })

  const canManageRolePermissions = usePermission(P.Roles.ManagePermissions)
  const canManageRoleUsers = usePermission(P.Roles.ManageUsers)
  const refreshCurrentUserSilently = useAuthStore((state) => state.refreshCurrentUserSilently)

  const [selectedRoleGuid, setSelectedRoleGuid] = useState<string | null>(null)
  // 异步回读时判断用户是否已切到别的角色，避免旧结果覆盖当前工作区。
  const selectedRoleGuidRef = useRef<string | null>(null)
  const [activeTab, setActiveTab] = useState<RoleWorkspaceTab>('permissions')

  const workspaceRequestGuardRef = useRef(createLatestRequestGuard())
  const catalogRef = useRef<PermissionCatalogDto | null>(null)
  const [workspaceLoading, setWorkspaceLoading] = useState(false)
  const [workspaceError, setWorkspaceError] = useState(false)
  const [catalog, setCatalog] = useState<PermissionCatalogDto | null>(null)
  const [permissionState, setPermissionState] = useState<RolePermissionStateDto | null>(null)
  // 权限草稿：「权限」与「菜单预览」两个标签共用，统一由底部待保存栏保存。
  const [baselineCodes, setBaselineCodes] = useState<string[]>([])
  const [draftCodes, setDraftCodes] = useState<string[]>([])
  const [savingPermissions, setSavingPermissions] = useState(false)

  const [createOpen, setCreateOpen] = useState(false)
  const [createLoading, setCreateLoading] = useState(false)
  const [createForm] = Form.useForm<CreateRoleDto>()

  const [editOpen, setEditOpen] = useState(false)
  const [editLoading, setEditLoading] = useState(false)
  const [form] = Form.useForm<UpdateRoleDto>()

  const loadData = async (overrides: Partial<DesiredRoleListQuery> = {}) => {
    if (!mountedRef.current) {
      return
    }

    const query: DesiredRoleListQuery = {
      page,
      pageSize,
      ...overrides,
    }
    // 角色 mutation 晚完成时刷新已开始的目标页，而不是最后成功页。
    desiredListQueryRef.current = query

    await runLatestGuardedRequest(listRequestGuardRef.current, () => getRoles(query), {
      onStart: () => setLoading(true),
      onSuccess: (result) => {
        setData(result.items)
        setTotal(result.total)
        setPage(result.page)
        setPageSize(result.pageSize)
      },
      onError: (error) => {
        console.error(error)
        message.error(t('system.roles.loadListFailed'))
      },
      onSettled: () => setLoading(false),
    })
  }

  const latestLoadDataRef = useRef(loadData)

  useLayoutEffect(() => {
    latestLoadDataRef.current = loadData
  })

  const refreshDesiredList = (overrides: Partial<DesiredRoleListQuery> = {}) =>
    latestLoadDataRef.current({ ...desiredListQueryRef.current, ...overrides })

  useLayoutEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      listRequestGuardRef.current.invalidate()
      workspaceRequestGuardRef.current.invalidate()
    }
  }, [])

  useEffect(() => {
    void loadData({ page: 1, pageSize })
  }, [])

  const selectedRole = useMemo(
    () => data.find((role) => role.roleGUID === selectedRoleGuid) ?? null,
    [data, selectedRoleGuid],
  )
  const filteredRoles = useMemo(() => filterRoles(data, keyword), [data, keyword])
  const permissionDiff = useMemo(() => diffPermissionCodes(baselineCodes, draftCodes), [baselineCodes, draftCodes])
  const permissionChangeCount = permissionDiff.added.length + permissionDiff.removed.length
  const isImplicitAll = permissionState ? isImplicitAllRole(permissionState) : false
  const permissionsReadOnly = !canManageRolePermissions || !permissionState || isImplicitAll || savingPermissions

  const loadWorkspace = async (roleGuid: string) => {
    await runLatestGuardedRequest(
      workspaceRequestGuardRef.current,
      () => Promise.all([
        getRolePermissionState(roleGuid),
        // 权限目录与角色无关，整页只取一次。
        catalogRef.current ? Promise.resolve(catalogRef.current) : getPermissionCatalog(),
      ]),
      {
        onStart: () => {
          setWorkspaceLoading(true)
          setWorkspaceError(false)
        },
        onSuccess: ([state, permissionCatalog]) => {
          catalogRef.current = permissionCatalog
          setCatalog(permissionCatalog)
          setPermissionState(state)
          const codes = getEditablePermissionCodes(state)
          setBaselineCodes(codes)
          setDraftCodes(codes)
        },
        onError: (error) => {
          console.error(error)
          setWorkspaceError(true)
          message.error(t('system.rolesWorkspace.loadWorkspaceFailed', '加载角色数据失败'))
        },
        onSettled: () => setWorkspaceLoading(false),
      },
    )
  }

  const selectRole = (roleGuid: string) => {
    selectedRoleGuidRef.current = roleGuid
    setSelectedRoleGuid(roleGuid)
    setPermissionState(null)
    setBaselineCodes([])
    setDraftCodes([])
    void loadWorkspace(roleGuid)
  }

  /** 切换角色前若有未保存的权限草稿，先确认是否丢弃。 */
  const requestSelectRole = (roleGuid: string) => {
    if (roleGuid === selectedRoleGuid) return
    if (permissionChangeCount === 0) {
      selectRole(roleGuid)
      return
    }
    Modal.confirm({
      title: t('system.rolesWorkspace.discardConfirmTitle', '放弃未保存的更改？'),
      content: t('system.rolesWorkspace.discardConfirmContent', '「{{name}}」还有尚未保存的权限更改，切换后将丢弃。', {
        name: selectedRole?.roleName ?? '',
      }),
      okText: t('system.rolesWorkspace.discardAndSwitch', '放弃并切换'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: () => selectRole(roleGuid),
    })
  }

  // 列表加载后默认选中第一个角色；当前角色仍在列表中时保持不变。
  useEffect(() => {
    if (!data.length) return
    if (selectedRoleGuidRef.current && data.some((role) => role.roleGUID === selectedRoleGuidRef.current)) return
    selectRole(data[0].roleGUID)
  }, [data])

  const togglePermission = (code: string, checked: boolean) => {
    if (permissionsReadOnly) return
    setDraftCodes((current) => {
      if (checked) return current.includes(code) ? current : [...current, code]
      return current.filter((item) => item !== code)
    })
  }

  const togglePermissionGroup = (codes: string[], checked: boolean) => {
    if (permissionsReadOnly) return
    setDraftCodes((current) => applyRolePermissionMutation({
      currentPermissionCodes: current,
      addPermissionCodes: checked ? codes : [],
      removePermissionCodes: checked ? [] : codes,
    }))
  }

  const handleMenuPermissionChange = ({
    addPermissionCodes = [],
    removePermissionCodes = [],
  }: {
    addPermissionCodes?: string[]
    removePermissionCodes?: string[]
  }) => {
    if (!canManageRolePermissions) {
      message.warning(t('system.roles.menuPermissionReadonlyTip', '当前账号没有维护角色权限的权限。'))
      return
    }
    if (permissionState && isImplicitAllRole(permissionState)) {
      message.info(t('system.roles.superAdminPermissionsReadOnly', '管理员默认拥有所有权限和菜单，无需在此处维护。'))
      return
    }
    if (permissionsReadOnly) return
    // 菜单预览里的增删只写入草稿，与「权限」标签共用同一个保存入口。
    setDraftCodes((current) => applyRolePermissionMutation({
      currentPermissionCodes: current,
      addPermissionCodes,
      removePermissionCodes,
    }))
  }

  const handleSavePermissions = async () => {
    if (!selectedRoleGuid || !permissionState || permissionsReadOnly) return
    const roleGuid = selectedRoleGuid
    const roleName = permissionState.roleName
    const permissions = [...draftCodes]
    setSavingPermissions(true)
    try {
      try {
        await assignPermissionsToRole(roleGuid, { permissions })
      } catch (error) {
        console.error(error)
        message.error(t('system.roles.permSaveFailed'))
        return
      }

      message.success(t('system.roles.permUpdateSuccess', { name: roleName }))
      void refreshDesiredList()
      void refreshCurrentUserSilently()

      // 写入成功后回读服务端状态（含别名展开后的有效权限）；期间切到别的角色则不覆盖。
      try {
        const nextState = await getRolePermissionState(roleGuid)
        if (selectedRoleGuidRef.current !== roleGuid) return
        const codes = getEditablePermissionCodes(nextState)
        setPermissionState(nextState)
        setBaselineCodes(codes)
        setDraftCodes(codes)
      } catch (error) {
        console.error(error)
        if (selectedRoleGuidRef.current !== roleGuid) return
        // 写入已成功：以本次保存的草稿作为新基线，避免继续显示「未保存」。
        setBaselineCodes(permissions)
        message.warning(t('system.rolesWorkspace.permissionReadbackFailed', '权限已保存，但最新权限状态刷新失败，请刷新页面后核对。'))
      }
    } finally {
      setSavingPermissions(false)
    }
  }

  const handleCreateOpen = () => {
    createForm.setFieldsValue({
      isActive: true,
    })
    setCreateOpen(true)
  }

  const handleCreateSubmit = async () => {
    try {
      const values = await createForm.validateFields()
      setCreateLoading(true)
      const created = await createRole({
        ...values,
        description: values.description?.trim() || undefined,
        roleName: values.roleName.trim(),
        isActive: values.isActive ?? true,
      })
      message.success(t('system.roles.createSuccess'))
      setCreateOpen(false)
      createForm.resetFields()
      await refreshDesiredList({ page: 1 })
      if (mountedRef.current) {
        // 新角色还没有权限：直接选中并停在「权限」标签，方便立即分配。
        setActiveTab('permissions')
        requestSelectRole(created.roleGUID)
      }
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      console.error(error)
      message.error(t('system.roles.createFailed'))
    } finally {
      setCreateLoading(false)
    }
  }

  const handleEditOpen = () => {
    if (!selectedRole) return
    form.setFieldsValue({
      roleName: selectedRole.roleName,
      description: selectedRole.description,
      isActive: selectedRole.isActive,
    })
    setEditOpen(true)
  }

  const handleEditSubmit = async () => {
    if (!selectedRoleGuid) return
    const roleGuid = selectedRoleGuid
    try {
      const values = await form.validateFields()
      setEditLoading(true)
      const updated = await updateRole(roleGuid, values)
      message.success(t('system.roles.updateSuccess'))
      setEditOpen(false)
      form.resetFields()
      // 角色名参与管理员判定，同步到权限状态，菜单预览随之更新。
      if (selectedRoleGuidRef.current === roleGuid) {
        setPermissionState((current) => (current ? { ...current, roleName: updated.roleName } : current))
      }
      void refreshDesiredList()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      console.error(error)
      message.error(t('system.roles.updateFailed'))
    } finally {
      setEditLoading(false)
    }
  }

  const handleRefresh = () => {
    void refreshDesiredList()
    if (selectedRoleGuid && permissionChangeCount === 0) {
      void loadWorkspace(selectedRoleGuid)
    }
  }

  const formatDateTime = (value?: string) => {
    if (!value) return '--'
    const parsed = dayjs(value)
    return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm') : value
  }

  const renderTabLabel = (label: string, count?: number) => (
    <span>
      {label}
      {count === undefined ? null : <span className="roles-ws-tab-count">{count}</span>}
    </span>
  )

  const renderWorkspaceBody = (content: () => ReactNode) => {
    if (workspaceError && !permissionState) {
      return (
        <div className="roles-ws-pane">
          <Alert
            type="error"
            showIcon
            message={t('system.rolesWorkspace.loadWorkspaceFailed', '加载角色数据失败')}
            action={selectedRoleGuid ? (
              <Button size="small" onClick={() => void loadWorkspace(selectedRoleGuid)}>
                {t('system.rolesWorkspace.retry', '重试')}
              </Button>
            ) : null}
          />
        </div>
      )
    }
    if (!permissionState || !catalog) {
      return (
        <div className="roles-ws-pane">
          <Skeleton active paragraph={{ rows: 8 }} />
        </div>
      )
    }
    // 同一角色刷新时保留现有内容，仅叠加加载遮罩。
    return <Spin spinning={workspaceLoading}>{content()}</Spin>
  }

  const workspaceTabs: TabsProps['items'] = selectedRole ? [
    {
      key: 'permissions',
      label: renderTabLabel(t('system.rolesWorkspace.tabPermissions', '权限'), permissionState ? draftCodes.length : undefined),
      children: renderWorkspaceBody(() => (
        <RolePermissionPanel
          categories={catalog?.categories ?? []}
          draftCodes={draftCodes}
          baselineCodes={baselineCodes}
          readOnly={permissionsReadOnly}
          readOnlyReason={isImplicitAll
            ? t('system.roles.superAdminPermissionsHint', 'Admin 默认拥有所有权限，无需分配')
            : !canManageRolePermissions
              ? t('system.rolesWorkspace.readOnlyPermissions', '当前账号没有维护角色权限的权限，仅可查看。')
              : undefined}
          onToggle={togglePermission}
          onToggleGroup={togglePermissionGroup}
        />
      )),
    },
    {
      key: 'menu',
      label: renderTabLabel(t('system.rolesWorkspace.tabMenu', '菜单预览')),
      children: renderWorkspaceBody(() => (
        <RoleMenuPreviewPanel
          permissionState={permissionState as RolePermissionStateDto}
          draftCodes={draftCodes}
          aliases={catalog?.permissionAliases ?? []}
          hasDraftChanges={permissionChangeCount > 0}
          readOnly={permissionsReadOnly}
          onMutate={handleMenuPermissionChange}
        />
      )),
    },
    {
      key: 'members',
      label: renderTabLabel(t('system.rolesWorkspace.tabMembers', '成员'), selectedRole.userCount),
      children: (
        <RoleMembersPanel
          role={selectedRole}
          canManage={canManageRoleUsers}
          onChanged={() => void refreshDesiredList()}
        />
      ),
    },
    {
      key: 'info',
      label: renderTabLabel(t('system.rolesWorkspace.tabInfo', '基本信息')),
      children: (
        <div className="roles-ws-pane">
          <Descriptions bordered size="small" column={{ xs: 1, sm: 2 }}>
            <Descriptions.Item label={t('system.roles.roleName')}>{selectedRole.roleName}</Descriptions.Item>
            <Descriptions.Item label={t('column.status')}>
              <StatusDot
                active={selectedRole.isActive}
                label={selectedRole.isActive ? t('common.active') : t('common.inactive')}
              />
            </Descriptions.Item>
            <Descriptions.Item label={t('column.description')} span="filled">{selectedRole.description || '--'}</Descriptions.Item>
            <Descriptions.Item label={t('system.roles.linkedUserCount')}>{selectedRole.userCount}</Descriptions.Item>
            <Descriptions.Item label={t('system.rolesWorkspace.effectivePermissions', '有效权限')}>
              {permissionState ? permissionState.effectivePermissionCodes.length : '--'}
            </Descriptions.Item>
            <Descriptions.Item label={t('system.rolesWorkspace.createdAt', '创建时间')}>{formatDateTime(selectedRole.createdAt)}</Descriptions.Item>
            <Descriptions.Item label={t('system.rolesWorkspace.updatedAtLabel', '更新时间')}>{formatDateTime(selectedRole.updatedAt)}</Descriptions.Item>
          </Descriptions>
        </div>
      ),
    },
  ] : []

  return (
    <PageContainer
      title={t('system.roles.pageTitle')}
      subtitle={t('system.rolesWorkspace.subtitle', '管理角色的权限、菜单可见范围与成员。')}
      extra={(
        <span style={{ display: 'flex', gap: 8 }}>
          <Tooltip title={t('common.refresh')}>
            <Button icon={<ReloadOutlined />} aria-label={t('common.refresh')} onClick={handleRefresh} />
          </Tooltip>
          <HasPermission code={P.Roles.Create}>
            <Button type="primary" icon={<PlusOutlined />} onClick={handleCreateOpen}>
              {t('system.roles.createRole')}
            </Button>
          </HasPermission>
        </span>
      )}
    >
      <div className="roles-ws-layout">
        <Card className="roles-ws-list">
          <div className="roles-ws-list-search">
            <Input
              allowClear
              prefix={<SearchOutlined />}
              placeholder={t('system.roles.searchPlaceholder')}
              aria-label={t('system.roles.searchPlaceholder')}
              value={keyword}
              onChange={(event) => setKeyword(event.target.value)}
            />
          </div>
          <div className="roles-ws-list-meta">
            <span>{t('system.rolesWorkspace.roleCount', '共 {{count}} 个角色', { count: total })}</span>
            <span>{t('system.rolesWorkspace.linkedUsers', '关联用户')}</span>
          </div>
          <Spin spinning={loading}>
            {filteredRoles.length ? (
              <ul className="roles-ws-list-items" aria-label={t('system.roles.pageTitle')}>
                {filteredRoles.map((role) => {
                  const active = role.roleGUID === selectedRoleGuid
                  return (
                    <li key={role.roleGUID}>
                      <button
                        type="button"
                        className={active ? 'roles-ws-role roles-ws-role-active' : 'roles-ws-role'}
                        aria-current={active ? 'true' : undefined}
                        onClick={() => requestSelectRole(role.roleGUID)}
                      >
                        <AccentDot color={getRoleAccentColor(role.roleName)} />
                        <span className="roles-ws-role-text">
                          <span className="roles-ws-role-name">{role.roleName}</span>
                          <span className="roles-ws-role-desc">{role.description || '--'}</span>
                        </span>
                        {role.isActive ? null : <Tag bordered={false}>{t('common.inactive')}</Tag>}
                        <span className="roles-ws-role-count">
                          <TeamOutlined />
                          {role.userCount}
                        </span>
                      </button>
                    </li>
                  )
                })}
              </ul>
            ) : (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={keyword.trim() ? t('system.rolesWorkspace.noMatchedRoles', '没有匹配的角色') : undefined}
              />
            )}
          </Spin>
          {total > pageSize ? (
            <div style={{ display: 'flex', justifyContent: 'center', paddingTop: 8 }}>
              <Pagination simple size="small" current={page} pageSize={pageSize} total={total} onChange={(nextPage) => void loadData({ page: nextPage })} />
            </div>
          ) : null}
        </Card>

        <Card className="roles-ws-workspace">
          {selectedRole ? (
            <>
              <div className="roles-ws-head">
                <div className="roles-ws-head-row">
                  <AccentDot color={getRoleAccentColor(selectedRole.roleName)} size={12} />
                  <h2 className="roles-ws-head-title">{selectedRole.roleName}</h2>
                  <StatusDot
                    active={selectedRole.isActive}
                    label={selectedRole.isActive ? t('common.active') : t('common.inactive')}
                  />
                  <span className="roles-ws-head-actions">
                    <HasPermission code={P.Roles.Edit}>
                      <Button icon={<EditOutlined />} onClick={handleEditOpen}>
                        {t('system.rolesWorkspace.editInfo', '编辑信息')}
                      </Button>
                    </HasPermission>
                  </span>
                </div>
                <p className="roles-ws-head-meta">
                  {[
                    selectedRole.description,
                    t('system.rolesWorkspace.updatedAt', '更新于 {{time}}', { time: formatDateTime(selectedRole.updatedAt) }),
                  ].filter(Boolean).join(' · ')}
                </p>
              </div>
              <Tabs
                className="roles-ws-tabs"
                activeKey={activeTab}
                onChange={(key) => setActiveTab(key as RoleWorkspaceTab)}
                items={workspaceTabs}
              />
              <PendingChangesBar
                visible={permissionChangeCount > 0}
                summary={t('system.rolesWorkspace.pendingChanges', '{{count}} 项未保存的更改', { count: permissionChangeCount })}
                detail={t('system.rolesWorkspace.pendingDetail', '+{{added}} 授予 · −{{removed}} 移除 · 保存后立即对 {{members}} 位成员生效', {
                  added: permissionDiff.added.length,
                  removed: permissionDiff.removed.length,
                  members: selectedRole.userCount,
                })}
                discardLabel={t('system.rolesWorkspace.discardChanges', '放弃更改')}
                saveLabel={t('system.rolesWorkspace.saveChanges', '保存更改')}
                saving={savingPermissions}
                onDiscard={() => setDraftCodes(baselineCodes)}
                onSave={() => void handleSavePermissions()}
              />
            </>
          ) : (
            <div className="roles-ws-pane">
              <Empty description={loading ? null : t('system.rolesWorkspace.noRoleSelected', '请选择左侧的角色')} />
            </div>
          )}
        </Card>
      </div>

      <Modal
        title={t('system.roles.createTitle')}
        open={createOpen}
        onCancel={() => {
          setCreateOpen(false)
          createForm.resetFields()
        }}
        onOk={() => void handleCreateSubmit()}
        confirmLoading={createLoading}
        width={620}
        destroyOnHidden
      >
        <Form form={createForm} layout="vertical" initialValues={{ isActive: true }}>
          <Form.Item
            label={t('system.roles.roleName')}
            name="roleName"
            rules={[
              { required: true, message: t('system.roles.roleNameRequired') },
              { whitespace: true, message: t('system.roles.roleNameRequired') },
              { min: 2, max: 50, message: t('system.roles.roleNameLength') },
            ]}
          >
            <Input maxLength={50} />
          </Form.Item>
          <Form.Item
            label={t('column.description')}
            name="description"
            rules={[{ max: 200, message: t('system.roles.descriptionLength') }]}
          >
            <Input.TextArea rows={4} maxLength={200} showCount />
          </Form.Item>
          <Form.Item label={t('column.status')} name="isActive" valuePropName="checked">
            <Switch checkedChildren={t('common.active')} unCheckedChildren={t('common.inactive')} />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title={selectedRole ? t('system.roles.editTitle', { name: selectedRole.roleName }) : t('system.roles.editTitleShort')}
        open={editOpen}
        onCancel={() => {
          setEditOpen(false)
          form.resetFields()
        }}
        onOk={() => void handleEditSubmit()}
        confirmLoading={editLoading}
        width={620}
        destroyOnHidden
      >
        <Form form={form} layout="vertical">
          <Form.Item label={t('system.roles.roleName')} name="roleName" rules={[{ required: true, message: t('system.roles.roleNameRequired') }]}>
            <Input />
          </Form.Item>
          <Form.Item label={t('column.description')} name="description">
            <Input.TextArea rows={4} />
          </Form.Item>
          <Form.Item label={t('column.status')} name="isActive" valuePropName="checked">
            <Switch checkedChildren={t('common.active')} unCheckedChildren={t('common.inactive')} />
          </Form.Item>
        </Form>
      </Modal>
    </PageContainer>
  )
}
