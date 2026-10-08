import { CheckCircleOutlined, MinusCircleOutlined, StopOutlined, UsergroupAddOutlined } from '@ant-design/icons'
import { Alert, Button, Modal, Select, Space, Typography, message } from 'antd'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import SelectionActionBar from '../../../components/listToolbar/SelectionActionBar'
import { addUsersToRole, removeUserFromRole } from '../../../services/roleService'
import { batchSetUsersActive } from '../../../services/userService'
import type { UserDto } from '../../../types/user'

import {
  getUserDisplayName,
  isDerivedStoreManagerRoleName,
  planBatchRoleChange,
  planBatchStatusChange,
} from './usersPageLogic'
import type { BatchRoleMode } from './usersPageLogic'

interface RoleOption {
  label: string
  value: string
  roleName: string
}

interface UserBatchActionsProps {
  selectedUsers: UserDto[]
  currentUserGuid?: string
  /** Users.Edit：批量启用 / 停用 */
  canEditStatus: boolean
  /** 管理员且持有 Roles.ManageUsers：批量添加 / 移除角色（后端只允许管理员维护用户角色） */
  canManageRoles: boolean
  roleOptions: RoleOption[]
  /** 范围受限店长不能编辑的账号，提前跳过，避免整批被后端回滚。 */
  isOutOfScope: (user: UserDto) => boolean
  onClearSelection: () => void
  /** 批量操作有写入后刷新列表并清空勾选。 */
  onCompleted: () => void
}

/** 取服务端业务消息（RequestError.payload.message），没有时退回通用文案。 */
function getServerMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object' && 'payload' in error) {
    const payload = (error as { payload?: unknown }).payload
    if (payload && typeof payload === 'object' && 'message' in payload) {
      const text = (payload as { message?: unknown }).message
      if (typeof text === 'string' && text.trim()) return text
    }
  }
  return fallback
}

export default function UserBatchActions({
  selectedUsers,
  currentUserGuid,
  canEditStatus,
  canManageRoles,
  roleOptions,
  isOutOfScope,
  onClearSelection,
  onCompleted,
}: UserBatchActionsProps) {
  const { t } = useTranslation()
  const [roleMode, setRoleMode] = useState<BatchRoleMode | null>(null)
  const [roleGuid, setRoleGuid] = useState<string | undefined>(undefined)
  const [roleSubmitting, setRoleSubmitting] = useState(false)

  // 店长角色由可管理分店派生，后端拒绝直接增删，不放进可选列表。
  const assignableRoleOptions = useMemo(
    () => roleOptions.filter((option) => !isDerivedStoreManagerRoleName(option.roleName)),
    [roleOptions],
  )
  // 列出前几位用户名，超出部分折叠为「等 N 位」，用在确认弹窗与跳过说明里。
  const listSeparator = t('system.usersBatch.listSeparator', '、')
  const summarizeNames = (users: UserDto[], limit = 3) => {
    const names = users.slice(0, limit).map(getUserDisplayName).join(listSeparator)
    return users.length > limit
      ? t('system.usersBatch.namesAndMore', '{{names}} 等 {{count}} 位', { names, count: users.length })
      : names
  }
  const clauseSeparator = t('system.usersBatch.clauseSeparator', '；')

  const selectedRole = assignableRoleOptions.find((option) => option.value === roleGuid)
  const rolePlan = selectedRole && roleMode ? planBatchRoleChange(selectedUsers, selectedRole.roleName, roleMode) : null

  const openStatusConfirm = (nextActive: boolean) => {
    const plan = planBatchStatusChange(selectedUsers, nextActive, currentUserGuid, isOutOfScope)
    const skippedLines = [
      plan.skippedUnchanged.length
        ? (nextActive
          ? t('system.usersBatch.skippedAlreadyActive', '{{count}} 位已是启用状态', { count: plan.skippedUnchanged.length })
          : t('system.usersBatch.skippedAlreadyInactive', '{{count}} 位已是停用状态', { count: plan.skippedUnchanged.length }))
        : null,
      plan.skippedSelf.length ? t('system.usersBatch.skippedSelf', '当前登录账号不参与批量启停用') : null,
      plan.skippedOutOfScope.length
        ? t('system.usersBatch.skippedOutOfScope', '{{names}} 不在你的管理范围内', { names: summarizeNames(plan.skippedOutOfScope) })
        : null,
    ].filter(Boolean) as string[]

    if (!plan.targets.length) {
      message.info(skippedLines.length
        ? t('system.usersBatch.nothingToDoWithReason', '没有需要变更的账号：{{reason}}', { reason: skippedLines.join(clauseSeparator) })
        : t('system.usersBatch.nothingToDo', '没有需要变更的账号'))
      return
    }

    const count = plan.targets.length
    Modal.confirm({
      title: nextActive
        ? t('system.usersBatch.enableTitle', '批量启用 {{count}} 个账号？', { count })
        : t('system.usersBatch.disableTitle', '批量停用 {{count}} 个账号？', { count }),
      content: (
        <Space direction="vertical" size={8} style={{ width: '100%' }}>
          <Typography.Text>
            {nextActive
              ? t('system.usersBatch.enableContent', '启用后这些账号可以重新登录。')
              : t('system.usersBatch.disableContent', '停用后这些账号将无法登录，已分配的角色与分店保持不变。')}
          </Typography.Text>
          <Typography.Text type="secondary">{summarizeNames(plan.targets, 5)}</Typography.Text>
          {skippedLines.length ? (
            <Typography.Text type="secondary">
              {t('system.usersBatch.skippedSummary', '已跳过：{{reason}}', { reason: skippedLines.join(clauseSeparator) })}
            </Typography.Text>
          ) : null}
        </Space>
      ),
      okText: nextActive ? t('system.usersBatch.enable', '批量启用') : t('system.usersBatch.disable', '批量停用'),
      okButtonProps: { danger: !nextActive },
      cancelText: t('common.cancel', '取消'),
      onOk: async () => {
        try {
          await batchSetUsersActive(plan.targets.map((user) => user.userGUID), nextActive)
          message.success(nextActive
            ? t('system.usersBatch.enableSuccess', '已启用 {{count}} 个账号', { count })
            : t('system.usersBatch.disableSuccess', '已停用 {{count}} 个账号', { count }))
          onCompleted()
        } catch (error) {
          console.error(error)
          // 后端整批回滚：任何一个目标不合法都不会有账号被修改。
          message.error(getServerMessage(error, t('system.usersBatch.statusFailed', '批量操作失败，所有账号均未修改')))
        }
      },
    })
  }

  const openRoleModal = (mode: BatchRoleMode) => {
    setRoleMode(mode)
    setRoleGuid(undefined)
  }

  const closeRoleModal = () => {
    if (roleSubmitting) return
    setRoleMode(null)
    setRoleGuid(undefined)
  }

  const submitRoleChange = async () => {
    if (!selectedRole || !rolePlan || !roleMode) return
    if (!rolePlan.targets.length) {
      closeRoleModal()
      return
    }

    setRoleSubmitting(true)
    try {
      if (roleMode === 'add') {
        // 添加走角色成员接口一次提交：后端自动忽略已存在的关联。
        await addUsersToRole(selectedRole.value, rolePlan.targets.map((user) => user.userGUID))
        message.success(t('system.usersBatch.addRoleSuccess', '已为 {{count}} 个账号添加角色「{{role}}」', {
          count: rolePlan.targets.length,
          role: selectedRole.roleName,
        }))
      } else {
        // 后端只有单个移除接口：逐个顺序调用，汇总成功与失败，失败不影响已成功的移除。
        const failures: { user: UserDto; reason: string }[] = []
        for (const user of rolePlan.targets) {
          try {
            await removeUserFromRole(selectedRole.value, user.userGUID)
          } catch (error) {
            console.error(error)
            failures.push({ user, reason: getServerMessage(error, t('system.roles.removeUserFailed', '移除用户失败')) })
          }
        }
        const succeeded = rolePlan.targets.length - failures.length
        if (!failures.length) {
          message.success(t('system.usersBatch.removeRoleSuccess', '已从 {{count}} 个账号移除角色「{{role}}」', {
            count: succeeded,
            role: selectedRole.roleName,
          }))
        } else {
          Modal.warning({
            title: t('system.usersBatch.removeRolePartial', '移除完成：成功 {{succeeded}} 个，失败 {{failed}} 个', {
              succeeded,
              failed: failures.length,
            }),
            content: (
              <ul style={{ margin: 0, paddingLeft: 18 }}>
                {failures.map(({ user, reason }) => (
                  <li key={user.userGUID}>
                    {t('system.usersBatch.failureLine', '{{name}}：{{reason}}', { name: getUserDisplayName(user), reason })}
                  </li>
                ))}
              </ul>
            ),
          })
        }
        if (!succeeded) {
          setRoleSubmitting(false)
          return
        }
      }
      setRoleSubmitting(false)
      setRoleMode(null)
      setRoleGuid(undefined)
      onCompleted()
    } catch (error) {
      console.error(error)
      message.error(getServerMessage(error, t('system.usersBatch.addRoleFailed', '批量添加角色失败')))
      setRoleSubmitting(false)
    }
  }

  const roleCount = rolePlan?.targets.length ?? 0

  return (
    <>
      <SelectionActionBar selectedCount={selectedUsers.length} onClearSelection={onClearSelection}>
        {canEditStatus ? (
          <>
            <Button size="small" icon={<CheckCircleOutlined />} onClick={() => openStatusConfirm(true)}>
              {t('system.usersBatch.enable', '批量启用')}
            </Button>
            <Button size="small" danger icon={<StopOutlined />} onClick={() => openStatusConfirm(false)}>
              {t('system.usersBatch.disable', '批量停用')}
            </Button>
          </>
        ) : null}
        {canManageRoles ? (
          <>
            <Button size="small" icon={<UsergroupAddOutlined />} onClick={() => openRoleModal('add')}>
              {t('system.usersBatch.addRole', '添加角色')}
            </Button>
            <Button size="small" icon={<MinusCircleOutlined />} onClick={() => openRoleModal('remove')}>
              {t('system.usersBatch.removeRole', '移除角色')}
            </Button>
          </>
        ) : null}
      </SelectionActionBar>

      <Modal
        title={roleMode === 'remove'
          ? t('system.usersBatch.removeRoleTitle', '为 {{count}} 个已选账号移除角色', { count: selectedUsers.length })
          : t('system.usersBatch.addRoleTitle', '为 {{count}} 个已选账号添加角色', { count: selectedUsers.length })}
        open={roleMode !== null}
        onCancel={closeRoleModal}
        onOk={() => void submitRoleChange()}
        okText={roleMode === 'remove'
          ? t('system.usersBatch.removeRoleOk', '移除（{{count}}）', { count: roleCount })
          : t('system.usersBatch.addRoleOk', '添加（{{count}}）', { count: roleCount })}
        okButtonProps={{ disabled: !roleCount, danger: roleMode === 'remove' }}
        cancelText={t('common.cancel', '取消')}
        confirmLoading={roleSubmitting}
        maskClosable={!roleSubmitting}
        destroyOnHidden
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Select
            showSearch
            optionFilterProp="label"
            style={{ width: '100%' }}
            placeholder={t('system.usersBatch.selectRole', '选择角色')}
            aria-label={t('system.usersBatch.selectRole', '选择角色')}
            value={roleGuid}
            onChange={setRoleGuid}
            options={assignableRoleOptions}
          />
          {rolePlan ? (
            <Typography.Text type="secondary">
              {roleMode === 'remove'
                ? t('system.usersBatch.removeRolePlan', '将从 {{count}} 个账号移除；{{skipped}} 个本来就没有该角色，自动跳过。', {
                  count: rolePlan.targets.length,
                  skipped: rolePlan.skipped.length,
                })
                : t('system.usersBatch.addRolePlan', '将为 {{count}} 个账号添加；{{skipped}} 个已持有该角色，自动跳过。', {
                  count: rolePlan.targets.length,
                  skipped: rolePlan.skipped.length,
                })}
            </Typography.Text>
          ) : null}
          <Alert
            type="info"
            showIcon
            message={t(
              'system.usersBatch.storeManagerHint',
              '店长角色由用户的「可管理分店」自动决定，不在此列表中；请在用户的分店设置里调整。',
            )}
          />
        </Space>
      </Modal>
    </>
  )
}
