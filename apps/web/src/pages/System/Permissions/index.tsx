import { DeleteOutlined, PlusOutlined, ReloadOutlined, SearchOutlined, TeamOutlined, UserOutlined } from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Checkbox,
  Empty,
  Form,
  Input,
  Modal,
  Popconfirm,
  Space,
  Tag,
  Typography,
  Transfer,
  message,
} from 'antd'
import type { TransferDirection } from 'antd/es/transfer'
import type { ColumnsType } from 'antd/es/table'
import type { Key } from 'react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import PageContainer from '../../../components/PageContainer'
import {
  assignRolesToPermission,
  assignUsersToPermission,
  createPermission,
  deletePermission,
  getActiveRoles,
  getPermissionCatalog,
  getPermissionRoles,
  getPermissionUsers,
  getSysPermissions,
} from '../../../services/roleService'
import { getUsers } from '../../../services/userService'
import type { CreateSysPermissionDto, PermissionCategoryDto, RoleOptionDto, SysPermissionDto } from '../../../types/role'
import { useAuthStore } from '../../../store/auth'
import { RequestError } from '../../../utils/request'
import {
  buildTaxonomyNav,
  categoriesOfGroup,
  matchesPermissionSurface,
  resolveActiveGroupKey,
  resolvePermissionSurfaces,
} from '../permissionTaxonomy'
import type { PermissionSurface, PermissionSurfaceFilter } from '../permissionTaxonomy'
import { BreakableCode, PermissionTaxonomyNav, SurfaceBadges, SurfaceFilterBar } from '../PermissionTaxonomyUi'
import { canManageSystemPermissions } from './permissionsAccess'
import {
  buildPermissionUserDelta,
  buildPermissionUserOptions,
  isPermissionUserDeltaEmpty,
  loadAllUserPages,
  matchesPermissionUserKeyword,
  toAssignedUserGuids,
  type PermissionUserOption,
} from './permissionUserAssignment'
import { MeasuredTable } from '../../../components/MeasuredTable'

const CATEGORY_COLORS: Record<string, string> = {
  Users: 'blue',
  Roles: 'purple',
  Stores: 'green',
  Warehouse: 'orange',
  Products: 'cyan',
  Orders: 'magenta',
  DomesticPurchase: 'gold',
  PosAdmin: 'geekblue',
  Shop: 'volcano',
}

interface PermissionTableItem {
  id: string
  code: string
  name: string
  category: string
  description?: string
  /** 使用端：收银端 / 后台 / 移动端，可同时属于多个。 */
  surfaces: PermissionSurface[]
  deletable: boolean
}

function buildPermissionTableItems(
  permissionCategories: PermissionCategoryDto[],
  sysPermissions: SysPermissionDto[],
): PermissionTableItem[] {
  const sysPermissionMap = new Map(sysPermissions.map((item) => [item.code, item]))
  const items = new Map<string, PermissionTableItem>()

  permissionCategories.forEach((category) => {
    category.permissions.forEach((permission) => {
      const sysPermission = sysPermissionMap.get(permission.name)
      items.set(permission.name, {
        id: sysPermission?.id ?? permission.name,
        code: permission.name,
        name: permission.displayName || sysPermission?.name || permission.name,
        category: category.displayName || permission.category || category.category,
        description: permission.description || sysPermission?.description,
        surfaces: resolvePermissionSurfaces(permission.name, permission.surfaces),
        deletable: !permission.isSystemPermission && Boolean(sysPermission),
      })
    })
  })

  sysPermissions.forEach((permission) => {
    if (items.has(permission.code)) return
    items.set(permission.code, {
      id: permission.id,
      code: permission.code,
      name: permission.name,
      category: permission.category,
      description: permission.description,
      // 只在权限表里、不在权限目录里的行没有后端解析结果，按编码前缀兜底
      surfaces: resolvePermissionSurfaces(permission.code),
      deletable: true,
    })
  })

  return Array.from(items.values())
}

export default function SystemPermissionsPage() {
  const { t } = useTranslation()
  const access = useAuthStore((state) => state.access)
  const canWritePermissions = canManageSystemPermissions(access)
  const [loading, setLoading] = useState(false)
  const [data, setData] = useState<PermissionTableItem[]>([])
  // null = 还没手动选过，落到第一个有内容的分组；'all' = 用户选了「全部」
  const [requestedGroup, setRequestedGroup] = useState<string | null>(null)
  const [surfaceFilter, setSurfaceFilter] = useState<PermissionSurfaceFilter>('all')
  const [keyword, setKeyword] = useState('')

  const [createOpen, setCreateOpen] = useState(false)
  const [createLoading, setCreateLoading] = useState(false)
  const [createForm] = Form.useForm<CreateSysPermissionDto>()

  const [assignOpen, setAssignOpen] = useState(false)
  const [assignLoading, setAssignLoading] = useState(false)
  const [assignSaving, setAssignSaving] = useState(false)
  const [currentPermission, setCurrentPermission] = useState<PermissionTableItem | null>(null)
  const [allRoles, setAllRoles] = useState<RoleOptionDto[]>([])
  const [roleTargetKeys, setRoleTargetKeys] = useState<string[]>([])

  const [userAssignOpen, setUserAssignOpen] = useState(false)
  const [userAssignLoading, setUserAssignLoading] = useState(false)
  const [userAssignSaving, setUserAssignSaving] = useState(false)
  const [userAssignPermission, setUserAssignPermission] = useState<PermissionTableItem | null>(null)
  const [userOptions, setUserOptions] = useState<PermissionUserOption[]>([])
  // baseline 为打开弹窗时服务端的直接授权用户，保存时据此计算增量。
  const [userBaselineKeys, setUserBaselineKeys] = useState<string[]>([])
  const [userTargetKeys, setUserTargetKeys] = useState<string[]>([])
  // 每次打开/关闭都递增序号，迟到的旧请求结果不得覆盖当前弹窗。
  const userAssignRequestRef = useRef(0)

  const loadData = async () => {
    setLoading(true)
    try {
      const [permissionCatalog, sysPermissions] = await Promise.all([
        getPermissionCatalog(),
        getSysPermissions(),
      ])
      setData(buildPermissionTableItems(permissionCatalog.categories, sysPermissions))
    } catch (error) {
      console.error(error)
      message.error(t('system.permissions.loadListFailed'))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void loadData()
  }, [])

  const normalizedKeyword = keyword.trim().toLowerCase()

  // 搜索 + 使用端先筛一遍，分类导航的数量与右侧表格保持一致
  const visibleData = useMemo(
    () =>
      data.filter((item) => {
        if (!matchesPermissionSurface(item.surfaces, surfaceFilter)) return false
        if (!normalizedKeyword) return true
        return (
          item.code.toLowerCase().includes(normalizedKeyword) ||
          item.name.toLowerCase().includes(normalizedKeyword)
        )
      }),
    [data, normalizedKeyword, surfaceFilter],
  )
  const navSections = useMemo(
    () => buildTaxonomyNav(visibleData.map((item) => ({ category: item.category }))),
    [visibleData],
  )
  const activeGroupKey = resolveActiveGroupKey(requestedGroup, navSections)

  const filteredData = useMemo(() => {
    const activeCategories = categoriesOfGroup(activeGroupKey, navSections)
    const byGroup = activeCategories
      ? visibleData.filter((item) => activeCategories.has(item.category))
      : visibleData
    return [...byGroup].sort((a, b) => a.name.localeCompare(b.name))
  }, [activeGroupKey, navSections, visibleData])

  const handleCreate = async () => {
    if (!canWritePermissions) {
      message.warning(t('system.permissions.noManagePermission', '无权限管理权限'))
      return
    }

    try {
      const values = await createForm.validateFields()
      setCreateLoading(true)
      const payload: CreateSysPermissionDto = {
        code: values.code,
        name: values.name,
        category: values.category,
        description: values.description,
        actions: values.actions?.length ? values.actions : undefined,
      }
      await createPermission(payload)
      message.success(t('system.permissions.createSuccess'))
      setCreateOpen(false)
      createForm.resetFields()
      void loadData()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      console.error(error)
      message.error(t('system.permissions.createFailed'))
    } finally {
      setCreateLoading(false)
    }
  }

  const handleAssignRoles = async (record: PermissionTableItem) => {
    if (!canWritePermissions) {
      message.warning(t('system.permissions.noManagePermission', '无权限管理权限'))
      return
    }

    setCurrentPermission(record)
    setAssignOpen(true)
    setAssignLoading(true)
    try {
      const [roles, permRoles] = await Promise.all([getActiveRoles(), getPermissionRoles(record.code)])
      setAllRoles(roles)
      setRoleTargetKeys(permRoles.map((item) => item.roleGUID))
    } catch (error) {
      console.error(error)
      message.error(t('system.permissions.loadRolesFailed'))
    } finally {
      setAssignLoading(false)
    }
  }

  const handleSaveRoles = async () => {
    if (!currentPermission) return
    if (!canWritePermissions) {
      message.warning(t('system.permissions.noManagePermission', '无权限管理权限'))
      return
    }

    setAssignSaving(true)
    try {
      await assignRolesToPermission(currentPermission.code, roleTargetKeys)
      message.success(t('system.permissions.roleAssignSuccess', { name: currentPermission.name }))
      setAssignOpen(false)
    } catch (error) {
      console.error(error)
      message.error(t('system.permissions.roleAssignFailed'))
    } finally {
      setAssignSaving(false)
    }
  }

  const handleAssignUsers = async (record: PermissionTableItem) => {
    if (!canWritePermissions) {
      message.warning(t('system.permissions.noManagePermission', '无权限管理权限'))
      return
    }

    const requestId = ++userAssignRequestRef.current
    setUserAssignPermission(record)
    setUserAssignOpen(true)
    setUserAssignLoading(true)
    setUserOptions([])
    setUserBaselineKeys([])
    setUserTargetKeys([])
    try {
      const [allUsers, assignedUsers] = await Promise.all([
        loadAllUserPages((page) => getUsers({ page, pageSize: 100 })),
        getPermissionUsers(record.code),
      ])
      if (requestId !== userAssignRequestRef.current) return
      const assignedKeys = toAssignedUserGuids(assignedUsers)
      setUserOptions(buildPermissionUserOptions(allUsers, assignedUsers))
      setUserBaselineKeys(assignedKeys)
      setUserTargetKeys(assignedKeys)
    } catch (error) {
      if (requestId !== userAssignRequestRef.current) return
      console.error(error)
      message.error(t('system.permissions.loadUsersFailed', '加载用户数据失败'))
      // 加载失败时关闭弹窗，避免在不完整的基线上计算增量。
      closeUserAssign()
    } finally {
      if (requestId === userAssignRequestRef.current) setUserAssignLoading(false)
    }
  }

  const closeUserAssign = () => {
    userAssignRequestRef.current += 1
    setUserAssignOpen(false)
    setUserAssignPermission(null)
    setUserAssignLoading(false)
  }

  const handleSaveUsers = async () => {
    if (!userAssignPermission) return
    if (!canWritePermissions) {
      message.warning(t('system.permissions.noManagePermission', '无权限管理权限'))
      return
    }

    const delta = buildPermissionUserDelta(userBaselineKeys, userTargetKeys)
    if (isPermissionUserDeltaEmpty(delta)) {
      closeUserAssign()
      return
    }

    setUserAssignSaving(true)
    try {
      await assignUsersToPermission(userAssignPermission.code, delta)
      message.success(t('system.permissions.userAssignSuccess', {
        name: userAssignPermission.name,
        defaultValue: '已更新「{{name}}」的用户授权',
      }))
      closeUserAssign()
    } catch (error) {
      console.error(error)
      // 后端会给出「权限尚未入库」「用户不存在」等具体原因，直接附在提示后便于管理员处理。
      const detail = error instanceof RequestError ? error.message : ''
      const failed = t('system.permissions.userAssignFailed', '分配用户失败')
      message.error(detail ? `${failed}：${detail}` : failed)
    } finally {
      setUserAssignSaving(false)
    }
  }

  const handleDelete = async (record: PermissionTableItem) => {
    if (!canWritePermissions) {
      message.warning(t('system.permissions.noManagePermission', '无权限管理权限'))
      return
    }

    try {
      await deletePermission(record.code)
      message.success(t('common.deleteSuccess'))
      void loadData()
    } catch (error) {
      console.error(error)
      message.error(t('common.deleteFailed'))
    }
  }

  const columns: ColumnsType<PermissionTableItem> = [
    {
      title: '#',
      width: 44,
      render: (_, __, index) => index + 1,
    },
    {
      title: `${t('system.permissions.permissionName')} / ${t('system.permissions.permissionCodeCol')}`,
      dataIndex: 'name',
      width: 300,
      sorter: (a, b) => a.name.localeCompare(b.name),
      defaultSortOrder: 'ascend',
      // 中文名与英文编码上下两行，都完整显示，不用省略号
      render: (_, record) => (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <span style={{ fontWeight: 600, overflowWrap: 'anywhere' }}>{record.name}</span>
          <BreakableCode code={record.code} />
        </div>
      ),
    },
    {
      title: t('system.permissions.category'),
      dataIndex: 'category',
      width: 110,
      render: (value) => <Tag color={CATEGORY_COLORS[value] || 'default'}>{value}</Tag>,
    },
    {
      title: t('system.permissionTaxonomy.surfaceLabel', '使用端'),
      dataIndex: 'surfaces',
      width: 100,
      render: (_, record) => <SurfaceBadges surfaces={record.surfaces} />,
    },
    {
      title: t('column.description'),
      dataIndex: 'description',
      width: 200,
      render: (value) => <span style={{ overflowWrap: 'anywhere' }}>{value || '--'}</span>,
    },
    {
      title: t('column.action'),
      key: 'action',
      width: 200,
      render: (_, record) => (
        // 三个操作按钮在窄列里允许折行，别把描述列挤没
        <Space size={0} wrap>
          {canWritePermissions ? (
            <Button type="link" icon={<TeamOutlined />} onClick={() => void handleAssignRoles(record)}>
              {t('system.permissions.assignRoles')}
            </Button>
          ) : null}
          {canWritePermissions ? (
            <Button type="link" icon={<UserOutlined />} onClick={() => void handleAssignUsers(record)}>
              {t('system.permissions.assignUsers', '分配用户')}
            </Button>
          ) : null}
          {canWritePermissions && record.deletable ? (
            <Popconfirm
              title={t('common.delete')}
              description={t('common.deleteIrreversible', '删除后不可恢复')}
              onConfirm={() => void handleDelete(record)}
              okText={t('common.delete')}
              cancelText={t('common.cancel')}
            >
              <Button type="link" danger icon={<DeleteOutlined />}>
                {t('common.delete')}
              </Button>
            </Popconfirm>
          ) : canWritePermissions ? (
            <Typography.Text type="secondary">--</Typography.Text>
          ) : null}
        </Space>
      ),
    },
  ]

  const actionOptions = ['Create', 'View', 'Edit', 'Delete']

  return (
    <PageContainer title={t('system.permissions.pageTitle')} subtitle={t('system.permissions.pageSubtitle')}>
      <div style={{ display: 'flex', gap: 16, alignItems: 'flex-start' }}>
        <Card
          title={t('system.permissionTaxonomy.title', '权限分类')}
          style={{ width: 268, flexShrink: 0 }}
          styles={{ body: { padding: '8px 10px 12px' } }}
        >
          {navSections.length ? (
            <div
              style={{
                maxHeight: 'calc(100vh - 280px)',
                overflowY: 'auto',
                overflowX: 'hidden',
              }}
            >
              <PermissionTaxonomyNav
                sections={navSections}
                activeKey={activeGroupKey}
                allCount={String(visibleData.length)}
                onSelect={setRequestedGroup}
                showGranted={false}
              />
            </div>
          ) : (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('common.noData', '暂无数据')} />
          )}
        </Card>

        <Card style={{ flex: 1, minWidth: 0 }}>
          <Space wrap style={{ marginBottom: 16 }}>
            <Input
              value={keyword}
              onChange={(event) => setKeyword(event.target.value)}
              placeholder={t('system.permissions.searchPlaceholder')}
              prefix={<SearchOutlined />}
              style={{ width: 280 }}
              allowClear
            />
            <SurfaceFilterBar value={surfaceFilter} onChange={setSurfaceFilter} />
            {canWritePermissions ? (
              <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
                {t('system.permissions.newPermission')}
              </Button>
            ) : null}
            <Button icon={<ReloadOutlined />} onClick={() => void loadData()}>
              {t('common.refresh')}
            </Button>
          </Space>

          <MeasuredTable metricId="system.permissions.table-1"
            rowKey="id"
            loading={loading}
            columns={columns}
            dataSource={filteredData}
            // 列宽合计的下限：容器更窄时横向滚动，而不是把描述列压成一个字宽
            scroll={{ x: 954 }}
            pagination={{ pageSize: 20, showSizeChanger: true, pageSizeOptions: ['20', '50', '100'] }}
          />
        </Card>
      </div>

      <Modal
        title={t('system.permissions.newPermission')}
        open={createOpen}
        onCancel={() => {
          setCreateOpen(false)
          createForm.resetFields()
        }}
        onOk={() => void handleCreate()}
        confirmLoading={createLoading}
        okButtonProps={{ disabled: !canWritePermissions }}
        width={600}
        destroyOnHidden
      >
        <Form form={createForm} layout="vertical">
          <Form.Item label={t('system.permissions.permissionCodeCol')} name="code" rules={[{ required: true, message: t('system.permissions.permissionCodeRequired') }]}>
            <Input placeholder={t('system.permissions.codePlaceholder')} />
          </Form.Item>
          <Form.Item label={t('system.permissions.permissionName')} name="name" rules={[{ required: true, message: t('system.permissions.permissionNameRequired') }]}>
            <Input placeholder={t('system.permissions.namePlaceholder')} />
          </Form.Item>
          <Form.Item label={t('system.permissions.category')} name="category" rules={[{ required: true, message: t('system.permissions.category') + t('system.permissions.permissionCodeRequired').replace(t('system.permissions.permissionCodeCol'), '') }]}>
            <Input placeholder={t('system.permissions.categoryPlaceholder')} />
          </Form.Item>
          <Form.Item label={t('column.description')} name="description">
            <Input.TextArea rows={2} />
          </Form.Item>
          <Form.Item name="actions" label={t('system.permissions.batchGeneration')}>
            <Checkbox.Group
              options={actionOptions.map((action) => ({ label: action, value: action }))}
            />
          </Form.Item>
          <div style={{ color: '#999', fontSize: 12, marginTop: -16, marginBottom: 24 }}>
            {t('system.permissions.batchGenDesc')}
          </div>
        </Form>
      </Modal>

      <Modal
        title={currentPermission ? t('system.permissions.assignRolesTitle', { name: currentPermission.name }) : t('system.permissions.assignRolesTitleShort')}
        open={assignOpen}
        onCancel={() => {
          setAssignOpen(false)
          setCurrentPermission(null)
        }}
        onOk={() => void handleSaveRoles()}
        confirmLoading={assignSaving}
        okButtonProps={{ disabled: !canWritePermissions }}
        width={700}
        destroyOnHidden
      >
        {assignLoading ? (
          <div style={{ textAlign: 'center', padding: 40 }}>{t('system.permissions.loading')}</div>
        ) : (
          <Transfer
            dataSource={allRoles.map((role) => ({
              key: role.roleGUID,
              title: role.roleName,
              description: role.description || '',
            }))}
            targetKeys={roleTargetKeys}
            onChange={(nextTargetKeys: Key[], _direction: TransferDirection, _moveKeys: Key[]) => {
              setRoleTargetKeys(nextTargetKeys.map(String))
            }}
            render={(item) => item.title}
            titles={[t('system.users.availableRoles'), t('system.users.assignedRolesLabel')]}
            listStyle={{ width: 280, height: 400 }}
            showSearch
          />
        )}
      </Modal>

      <Modal
        title={userAssignPermission
          ? t('system.permissions.assignUsersTitle', { name: userAssignPermission.name, defaultValue: '分配用户 - {{name}}' })
          : t('system.permissions.assignUsers', '分配用户')}
        open={userAssignOpen}
        onCancel={closeUserAssign}
        onOk={() => void handleSaveUsers()}
        confirmLoading={userAssignSaving}
        okButtonProps={{ disabled: !canWritePermissions || userAssignLoading }}
        width={760}
        destroyOnHidden
      >
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 16 }}
          message={t(
            'system.permissions.assignUsersHint',
            '此处只维护用户的直接授权；通过角色获得该权限的用户不在右侧列出，请使用「分配角色」调整。',
          )}
        />
        {userAssignLoading ? (
          <div style={{ textAlign: 'center', padding: 40 }}>{t('system.permissions.loading')}</div>
        ) : (
          <Transfer
            dataSource={userOptions}
            targetKeys={userTargetKeys}
            onChange={(nextTargetKeys: Key[], _direction: TransferDirection, _moveKeys: Key[]) => {
              setUserTargetKeys(nextTargetKeys.map(String))
            }}
            render={(item) => (
              <span title={item.description}>
                {item.title}
                {item.isActive ? null : (
                  <Tag style={{ marginLeft: 6 }}>{t('common.inactive')}</Tag>
                )}
              </span>
            )}
            filterOption={(input, item) => matchesPermissionUserKeyword(item, input)}
            titles={[
              t('system.permissions.availableUsers', '可选用户'),
              t('system.permissions.assignedUsers', '已直接授权用户'),
            ]}
            listStyle={{ width: 330, height: 420 }}
            showSearch
          />
        )}
      </Modal>
    </PageContainer>
  )
}
