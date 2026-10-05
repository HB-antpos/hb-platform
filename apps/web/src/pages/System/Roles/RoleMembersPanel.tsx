import { DeleteOutlined, SearchOutlined, UserAddOutlined } from '@ant-design/icons'
import { Button, Input, Modal, Popconfirm, Select, Space, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { addUsersToRole, getRoleUsers, removeUserFromRole } from '../../../services/roleService'
import { getUsers } from '../../../services/userService'
import type { RoleDto, RoleUserDto } from '../../../types/role'
import type { UserDto } from '../../../types/user'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import { StatusDot } from '../accessAdminUi'

interface RoleMembersPanelProps {
  role: RoleDto
  /** 是否有 Roles.ManageUsers：决定能否添加 / 移除成员 */
  canManage: boolean
  /** 成员变动后通知页面刷新角色列表的人数 */
  onChanged?: () => void
}

/** 角色工作区「成员」标签：成员列表 + 添加 / 移除，原独立抽屉内嵌到工作区。 */
export default function RoleMembersPanel({ role, canManage, onChanged }: RoleMembersPanelProps) {
  const { t } = useTranslation()
  const [loading, setLoading] = useState(false)
  const [users, setUsers] = useState<RoleUserDto[]>([])
  const [keyword, setKeyword] = useState('')
  const [addOpen, setAddOpen] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [allUsers, setAllUsers] = useState<UserDto[]>([])
  const [selectedUserGuids, setSelectedUserGuids] = useState<string[]>([])
  // 快速切换角色时，只允许最后一次请求写入成员列表。
  const usersRequestGuardRef = useRef(createLatestRequestGuard())

  const loadUsers = async (roleGuid = role.roleGUID) => {
    await runLatestGuardedRequest(
      usersRequestGuardRef.current,
      () => getRoleUsers(roleGuid, { page: 1, pageSize: 200 }),
      {
        onStart: () => setLoading(true),
        onSuccess: (result) => setUsers(result.items),
        onError: (error) => {
          console.error(error)
          message.error(t('system.roles.loadUsersFailed'))
        },
        onSettled: () => setLoading(false),
      },
    )
  }

  useEffect(() => {
    setUsers([])
    setKeyword('')
    void loadUsers(role.roleGUID)
  }, [role.roleGUID])

  useEffect(() => () => usersRequestGuardRef.current.invalidate(), [])

  const loadAvailableUsers = async () => {
    try {
      const result = await getUsers({ page: 1, pageSize: 200 })
      setAllUsers(result.items)
    } catch (error) {
      console.error(error)
      message.error(t('system.roles.loadUserOptionsFailed'))
    }
  }

  const availableUsers = useMemo(() => {
    const assigned = new Set(users.map((item) => item.userGUID))
    return allUsers.filter((item) => !assigned.has(item.userGUID))
  }, [allUsers, users])

  const filteredUsers = useMemo(() => {
    const normalized = keyword.trim().toLowerCase()
    if (!normalized) return users
    return users.filter((item) =>
      [item.username, item.fullName ?? '', item.email].some((value) => value.toLowerCase().includes(normalized)),
    )
  }, [keyword, users])

  const handleOpenAdd = async () => {
    setSelectedUserGuids([])
    setAddOpen(true)
    if (!allUsers.length) {
      await loadAvailableUsers()
    }
  }

  const handleAddUsers = async () => {
    if (!selectedUserGuids.length) {
      message.warning(t('system.roles.selectUser'))
      return
    }

    setSubmitting(true)
    try {
      await addUsersToRole(role.roleGUID, selectedUserGuids)
      message.success(t('system.roles.addUserSuccess'))
      setAddOpen(false)
      await loadUsers()
      onChanged?.()
    } catch (error) {
      console.error(error)
      message.error(t('system.roles.addUserFailed'))
    } finally {
      setSubmitting(false)
    }
  }

  const handleRemoveUser = async (userGuid: string) => {
    try {
      await removeUserFromRole(role.roleGUID, userGuid)
      message.success(t('system.roles.removeUserSuccess'))
      await loadUsers()
      onChanged?.()
    } catch (error) {
      console.error(error)
      message.error(t('system.roles.removeUserFailed'))
    }
  }

  const columns: ColumnsType<RoleUserDto> = [
    {
      title: t('system.users.username'),
      dataIndex: 'username',
      render: (_value: string, record) => (
        <span style={{ display: 'block', minWidth: 0 }}>
          <Typography.Text strong>{record.fullName?.trim() || record.username}</Typography.Text>
          <Typography.Text type="secondary" style={{ display: 'block', fontSize: 12.5 }}>
            {record.fullName?.trim() && record.fullName.trim() !== record.username
              ? `@${record.username} · ${record.email}`
              : record.email}
          </Typography.Text>
        </span>
      ),
    },
    {
      title: t('column.status'),
      dataIndex: 'isActive',
      width: 100,
      render: (value: boolean) => <StatusDot active={value} label={value ? t('common.active') : t('common.inactive')} />,
    },
    {
      title: t('system.rolesWorkspace.joinedAt', '加入时间'),
      dataIndex: 'assignedAt',
      width: 170,
      render: (value: string) => {
        const parsed = value ? dayjs(value) : null
        return parsed?.isValid() ? parsed.format('YYYY-MM-DD HH:mm') : (value || '--')
      },
    },
    ...(canManage ? [{
      title: t('column.action'),
      key: 'action',
      width: 100,
      align: 'right' as const,
      render: (_: unknown, record: RoleUserDto) => (
        <Popconfirm
          title={t('system.roles.confirmRemoveUser')}
          okText={t('common.confirm')}
          cancelText={t('common.cancel')}
          onConfirm={() => void handleRemoveUser(record.userGUID)}
        >
          <Button type="text" size="small" danger icon={<DeleteOutlined />}>
            {t('system.roles.remove')}
          </Button>
        </Popconfirm>
      ),
    }] : []),
  ]

  return (
    <div className="roles-ws-pane">
      <div className="roles-ws-toolbar">
        <Input
          className="roles-ws-search"
          allowClear
          prefix={<SearchOutlined />}
          placeholder={t('system.users.searchPlaceholder', '搜索用户名 / 姓名 / 邮箱')}
          aria-label={t('system.users.searchPlaceholder', '搜索用户名 / 姓名 / 邮箱')}
          value={keyword}
          onChange={(event) => setKeyword(event.target.value)}
        />
        <Typography.Text type="secondary">
          {t('system.rolesWorkspace.membersCount', '共 {{count}} 位成员', { count: users.length })}
        </Typography.Text>
        {canManage ? (
          <span className="roles-ws-toolbar-end">
            <Button type="primary" icon={<UserAddOutlined />} onClick={() => void handleOpenAdd()}>
              {t('system.roles.addUser')}
            </Button>
          </span>
        ) : null}
      </div>

      <MeasuredTable metricId="system.roles.role-user-management.table-1"
        rowKey="userGUID"
        size="middle"
        loading={loading}
        dataSource={filteredUsers}
        columns={columns}
        pagination={{ pageSize: 10, showSizeChanger: false, hideOnSinglePage: true }}
        scroll={{ x: 600 }}
      />

      <Modal
        title={t('system.roles.addUserToRole')}
        open={addOpen}
        onCancel={() => setAddOpen(false)}
        onOk={() => void handleAddUsers()}
        confirmLoading={submitting}
        destroyOnHidden
      >
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          <Select
            mode="multiple"
            placeholder={t('system.roles.selectUser')}
            value={selectedUserGuids}
            onChange={setSelectedUserGuids}
            showSearch
            optionFilterProp="label"
            style={{ width: '100%' }}
            options={availableUsers.map((item) => ({
              value: item.userGUID,
              label: `${item.username}${item.fullName ? ` / ${item.fullName}` : ''}`,
            }))}
          />
        </Space>
      </Modal>
    </div>
  )
}
