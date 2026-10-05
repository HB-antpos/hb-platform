import { DownOutlined, RightOutlined, SearchOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Empty, Input, Segmented } from 'antd'
import { useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import type { PermissionCategoryDto } from '../../../types/role'
import { NeutralChip } from '../accessAdminUi'
import { buildPermissionGroupViews } from './rolesWorkspaceLogic'
import type { PermissionFilter } from './rolesWorkspaceLogic'

interface RolePermissionPanelProps {
  categories: PermissionCategoryDto[]
  /** 当前草稿（尚未保存的勾选结果） */
  draftCodes: string[]
  /** 已保存的权限，用于标出「+ 授予 / − 移除」 */
  baselineCodes: string[]
  readOnly: boolean
  /** 只读原因，显示在面板顶部 */
  readOnlyReason?: ReactNode
  onToggle: (code: string, checked: boolean) => void
  onToggleGroup: (codes: string[], checked: boolean) => void
}

/**
 * 角色权限清单（受控）：按模块分组的复选框，支持搜索与「已授予 / 未授予 / 已修改」筛选。
 * 勾选只改草稿，统一由工作区底部的待保存栏保存。
 */
export default function RolePermissionPanel({
  categories,
  draftCodes,
  baselineCodes,
  readOnly,
  readOnlyReason,
  onToggle,
  onToggleGroup,
}: RolePermissionPanelProps) {
  const { t } = useTranslation()
  const [keyword, setKeyword] = useState('')
  const [filter, setFilter] = useState<PermissionFilter>('all')
  const [collapsedKeys, setCollapsedKeys] = useState<Set<string>>(() => new Set())

  const { groups, counts } = useMemo(
    () => buildPermissionGroupViews({ categories, draftCodes, baselineCodes, keyword, filter }),
    [baselineCodes, categories, draftCodes, filter, keyword],
  )
  // 搜索或筛选时强制展开，避免命中的权限藏在已收起的分组里。
  const isNarrowed = keyword.trim() !== '' || filter !== 'all'

  const toggleCollapsed = (key: string) => {
    setCollapsedKeys((current) => {
      const next = new Set(current)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  const filterLabel = (label: string, count: number) => (
    <span>
      {label}
      <span style={{ marginLeft: 6, fontVariantNumeric: 'tabular-nums' }}>{count}</span>
    </span>
  )

  return (
    <div className="roles-ws-pane">
      {readOnlyReason ? <Alert type="info" showIcon message={readOnlyReason} /> : null}

      <div className="roles-ws-toolbar">
        <Input
          className="roles-ws-search"
          allowClear
          prefix={<SearchOutlined />}
          placeholder={t('system.rolesWorkspace.permissionSearchPlaceholder', '搜索权限名称或编码')}
          aria-label={t('system.rolesWorkspace.permissionSearchPlaceholder', '搜索权限名称或编码')}
          value={keyword}
          onChange={(event) => setKeyword(event.target.value)}
        />
        <Segmented<PermissionFilter>
          aria-label={t('system.rolesWorkspace.permissionFilter', '权限筛选')}
          value={filter}
          onChange={(value) => setFilter(value)}
          options={[
            { label: filterLabel(t('system.rolesWorkspace.filterAll', '全部'), counts.all), value: 'all' },
            { label: filterLabel(t('system.rolesWorkspace.filterGranted', '已授予'), counts.granted), value: 'granted' },
            { label: filterLabel(t('system.rolesWorkspace.filterUngranted', '未授予'), counts.ungranted), value: 'ungranted' },
            { label: filterLabel(t('system.rolesWorkspace.filterChanged', '已修改'), counts.changed), value: 'changed' },
          ]}
        />
        <span className="roles-ws-toolbar-end">
          <Button size="small" type="text" disabled={isNarrowed} onClick={() => setCollapsedKeys(new Set())}>
            {t('system.rolesWorkspace.expandAll', '展开全部')}
          </Button>
          <Button
            size="small"
            type="text"
            disabled={isNarrowed}
            onClick={() => setCollapsedKeys(new Set(groups.map((group) => group.key)))}
          >
            {t('system.rolesWorkspace.collapseAll', '收起全部')}
          </Button>
        </span>
      </div>

      {groups.length ? groups.map((group) => {
        const expanded = isNarrowed || !collapsedKeys.has(group.key)
        return (
          <section key={group.key} aria-label={group.displayName}>
            <div className="roles-ws-group-head">
              <button
                type="button"
                className="roles-ws-group-toggle"
                aria-expanded={expanded}
                onClick={() => toggleCollapsed(group.key)}
              >
                {expanded ? <DownOutlined /> : <RightOutlined />}
                {group.displayName}
              </button>
              {group.key !== group.displayName ? <span className="roles-ws-code">{group.key}</span> : null}
              <NeutralChip>{`${group.granted} / ${group.total}`}</NeutralChip>
              {readOnly ? null : (
                <span className="roles-ws-group-actions">
                  <Button
                    size="small"
                    type="text"
                    disabled={group.granted === group.total}
                    onClick={() => onToggleGroup(group.codes, true)}
                  >
                    {t('system.rolesWorkspace.selectAll', '全选')}
                  </Button>
                  <Button
                    size="small"
                    type="text"
                    disabled={group.granted === 0}
                    onClick={() => onToggleGroup(group.codes, false)}
                  >
                    {t('system.rolesWorkspace.clearAll', '清空')}
                  </Button>
                </span>
              )}
            </div>
            {expanded ? (
              <div className="roles-ws-perm-grid">
                {group.items.map((item) => (
                  <Checkbox
                    key={item.code}
                    className={item.change ? 'roles-ws-perm roles-ws-perm-changed' : 'roles-ws-perm'}
                    checked={item.granted}
                    disabled={readOnly}
                    onChange={(event) => onToggle(item.code, event.target.checked)}
                  >
                    <span className="roles-ws-perm-name" title={item.description || item.displayName}>
                      {item.displayName}
                    </span>
                    {item.change ? (
                      <span className={item.change === 'added' ? 'roles-ws-badge roles-ws-badge-add' : 'roles-ws-badge roles-ws-badge-remove'}>
                        {item.change === 'added'
                          ? t('system.rolesWorkspace.badgeAdded', '+ 授予')
                          : t('system.rolesWorkspace.badgeRemoved', '− 移除')}
                      </span>
                    ) : null}
                    <span className="roles-ws-code">{item.code}</span>
                  </Checkbox>
                ))}
              </div>
            ) : null}
          </section>
        )
      }) : (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('system.rolesWorkspace.noMatchedPermissions', '没有匹配的权限')} />
      )}
    </div>
  )
}
