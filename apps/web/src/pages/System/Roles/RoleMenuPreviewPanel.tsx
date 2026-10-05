import { DesktopOutlined, DownOutlined, LockOutlined, MinusCircleOutlined, MobileOutlined, PlusCircleOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Segmented, Tag, Typography } from 'antd'
import { useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import type { PermissionAliasDto, RolePermissionStateDto } from '../../../types/role'
import {
  buildExpoRoleMenuPreview,
  filterExpoRoutesByVisibility,
  type ExpoAppVisibleRoute,
  type ExpoMenuVisibilityFilter,
} from '../../../utils/expoRoleMenuPreview'
import { buildRolePreviewAccess, isImplicitAllRole } from '../../../utils/roleMenuPreview'
import {
  buildWebRoleMenuPreview,
  filterWebMenuNodesByVisibility,
  type WebMenuPreviewNode,
  type WebMenuVisibilityFilter,
} from '../../../utils/webMenuPreview'
import { AccentDot, NeutralChip } from '../accessAdminUi'
import { collectWebMenuVisibility, expandRolePermissionCodes } from './rolesWorkspaceLogic'

type MenuPlatform = 'desktop' | 'mobile'

interface MenuEditTarget {
  canAdd: boolean
  canRemove: boolean
  isReadOnly: boolean
  isFixed: boolean
  addPermissionCodes: string[]
  removePermissionCodes: string[]
}

interface RoleMenuPreviewPanelProps {
  /** 已保存的角色权限状态 */
  permissionState: RolePermissionStateDto
  /** 当前草稿中的显式权限 */
  draftCodes: string[]
  aliases: PermissionAliasDto[]
  hasDraftChanges: boolean
  readOnly: boolean
  onMutate: (change: { addPermissionCodes?: string[]; removePermissionCodes?: string[] }) => void
}

interface FlatWebRow {
  node: WebMenuPreviewNode
  depth: number
}

function flattenWebNodes(nodes: WebMenuPreviewNode[], depth = 0, rows: FlatWebRow[] = []) {
  for (const node of nodes) {
    rows.push({ node, depth })
    if (node.children?.length) flattenWebNodes(node.children, depth + 1, rows)
  }
  return rows
}

function countWebNodes(nodes: WebMenuPreviewNode[]): number {
  return nodes.reduce((total, node) => total + 1 + countWebNodes(node.children ?? []), 0)
}

/**
 * 菜单预览：按「草稿」权限推导桌面端与 HbwebExpo 移动端菜单可见性。
 * 「添加 / 移除权限」只改草稿，并与「权限」标签共用同一个待保存栏，保存前标出可见性变化。
 */
export default function RoleMenuPreviewPanel({
  permissionState,
  draftCodes,
  aliases,
  hasDraftChanges,
  readOnly,
  onMutate,
}: RoleMenuPreviewPanelProps) {
  const { t } = useTranslation()
  const [platform, setPlatform] = useState<MenuPlatform>('desktop')
  const [webFilter, setWebFilter] = useState<WebMenuVisibilityFilter>('all')
  const [expoFilter, setExpoFilter] = useState<ExpoMenuVisibilityFilter>('all')
  const implicitAll = isImplicitAllRole(permissionState)
  const translate = (key: string, fallback?: string) => (fallback ? t(key, fallback) : t(key))

  // 草稿未改动时直接用服务端的有效权限；改动后按别名规则在前端展开，保证预览与保存后的结果一致。
  const draftState = useMemo<RolePermissionStateDto>(() => {
    if (!hasDraftChanges || implicitAll) return permissionState
    return {
      ...permissionState,
      explicitPermissionCodes: draftCodes,
      effectivePermissionCodes: expandRolePermissionCodes(draftCodes, aliases),
    }
  }, [aliases, draftCodes, hasDraftChanges, implicitAll, permissionState])

  const draftAccess = useMemo(() => buildRolePreviewAccess(draftState), [draftState])
  const baselineAccess = useMemo(() => buildRolePreviewAccess(permissionState), [permissionState])
  const editableReadOnly = readOnly || implicitAll

  const webNodes = buildWebRoleMenuPreview(draftAccess, translate, {
    includeHidden: true,
    explicitPermissionCodes: draftState.explicitPermissionCodes,
    readOnly: editableReadOnly,
  })
  // 已保存状态下的菜单可见性，用来标出草稿带来的「保存后可见 / 隐藏」。
  const baselineWebVisibility = collectWebMenuVisibility(buildWebRoleMenuPreview(baselineAccess, translate, {
    includeHidden: true,
    explicitPermissionCodes: permissionState.explicitPermissionCodes,
    readOnly: true,
  }))
  const webRows = flattenWebNodes(filterWebMenuNodesByVisibility(webNodes, webFilter))
  const webCounts = {
    all: countWebNodes(webNodes),
    visible: countWebNodes(filterWebMenuNodesByVisibility(webNodes, 'visible')),
    hidden: countWebNodes(filterWebMenuNodesByVisibility(webNodes, 'hidden')),
  }

  const expoMenu = buildExpoRoleMenuPreview(draftAccess, undefined, {
    explicitPermissionCodes: draftState.explicitPermissionCodes,
    readOnly: editableReadOnly,
  })
  const baselineExpoVisibility = useMemo(() => {
    const preview = buildExpoRoleMenuPreview(baselineAccess, undefined, {
      explicitPermissionCodes: permissionState.explicitPermissionCodes,
      readOnly: true,
    })
    return new Map(preview.allRoutes.map((route) => [route.routeName, route.visible]))
  }, [baselineAccess, permissionState.explicitPermissionCodes])
  const expoRoutes = filterExpoRoutesByVisibility(expoMenu.allRoutes, expoFilter)
  const expoVisibleCount = expoMenu.allRoutes.filter((route) => route.visible).length

  const renderStatus = (visible: boolean, baselineVisible: boolean | undefined) => {
    // 与已保存状态不同的可见性，用空心蓝点标出「保存后可见 / 隐藏」。
    if (baselineVisible !== undefined && baselineVisible !== visible) {
      return (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, color: '#0b4fc2' }}>
          <AccentDot color="#1677ff" hollow />
          {visible
            ? t('system.rolesWorkspace.menuStagedVisible', '保存后可见')
            : t('system.rolesWorkspace.menuStagedHidden', '保存后隐藏')}
        </span>
      )
    }
    return (
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, color: visible ? undefined : '#667085' }}>
        <AccentDot color={visible ? '#52c41a' : '#98a2b3'} hollow={!visible} />
        {visible
          ? t('system.roles.menuPermissionVisible', '可见')
          : t('system.roles.menuPermissionHidden', '未显示')}
      </span>
    )
  }

  const renderPermissionCodes = (codes: string[], hasChildren = false): ReactNode => {
    if (codes.length) {
      return (
        <div className="roles-ws-menu-perms">
          {codes.map((code) => (
            <NeutralChip key={code} title={code}>
              <span className="roles-ws-code roles-ws-ellipsis" style={{ color: '#3c4350' }}>{code}</span>
            </NeutralChip>
          ))}
        </div>
      )
    }
    return (
      <Typography.Text type="secondary" style={{ fontSize: 13 }}>
        {hasChildren
          ? t('system.roles.webMenuVisibleByChildren', '由可见子菜单决定')
          : t('system.roles.webMenuNoDirectPermission', '无直接权限')}
      </Typography.Text>
    )
  }

  const renderAction = (edit: MenuEditTarget) => {
    if (edit.isFixed) {
      return (
        <Tag icon={<LockOutlined />} bordered={false}>
          {t('system.roles.fixedMenuPermission', '固定入口')}
        </Tag>
      )
    }
    if (edit.isReadOnly) {
      return editableReadOnly ? null : <Tag bordered={false}>{t('system.roles.readOnlyMenuPermission', '只读')}</Tag>
    }
    if (edit.canRemove) {
      return (
        <Button
          size="small"
          type="text"
          danger
          icon={<MinusCircleOutlined />}
          onClick={() => onMutate({ removePermissionCodes: edit.removePermissionCodes })}
        >
          {t('system.roles.removeMenuPermission', '移除权限')}
        </Button>
      )
    }
    if (edit.canAdd) {
      return (
        <Button
          size="small"
          type="text"
          icon={<PlusCircleOutlined />}
          style={{ color: '#0958d9' }}
          onClick={() => onMutate({ addPermissionCodes: edit.addPermissionCodes })}
        >
          {t('system.roles.addMenuPermission', '添加权限')}
        </Button>
      )
    }
    return null
  }

  const toExpoEditTarget = (route: ExpoAppVisibleRoute): MenuEditTarget => ({
    canAdd: !route.visible && !route.readOnly && !route.locked && route.addPermissionCodes.length > 0,
    canRemove: route.visible && !route.readOnly && !route.locked && route.removePermissionCodes.length > 0,
    isReadOnly: route.readOnly,
    isFixed: route.locked,
    addPermissionCodes: route.addPermissionCodes,
    removePermissionCodes: route.removePermissionCodes,
  })

  const filterOptions = (counts: { all: number; visible: number; hidden: number }) => [
    { label: `${t('system.roles.menuFilterAll', '全部')} ${counts.all}`, value: 'all' },
    { label: `${t('system.roles.menuFilterVisible', '可见')} ${counts.visible}`, value: 'visible' },
    { label: `${t('system.roles.menuFilterHidden', '未显示')} ${counts.hidden}`, value: 'hidden' },
  ]

  const tableHead = (
    <div className="roles-ws-menu-row roles-ws-menu-head" role="row">
      <span role="columnheader">{t('system.rolesWorkspace.columnMenu', '菜单')}</span>
      <span role="columnheader">{t('system.rolesWorkspace.columnPath', '路径')}</span>
      <span role="columnheader">{t('system.rolesWorkspace.columnStatus', '状态')}</span>
      <span role="columnheader">{t('system.rolesWorkspace.columnPermissions', '所需权限（任一满足即可）')}</span>
      <span role="columnheader" style={{ textAlign: 'right' }}>{t('system.rolesWorkspace.columnAction', '操作')}</span>
    </div>
  )

  return (
    <div className="roles-ws-pane">
      {implicitAll ? (
        <Alert type="info" showIcon message={t('system.roles.superAdminMenuPreviewTip', '管理员默认预览全部可访问菜单。')} />
      ) : null}

      <div className="roles-ws-toolbar">
        <Segmented<MenuPlatform>
          value={platform}
          onChange={(value) => setPlatform(value)}
          options={[
            { label: t('system.rolesWorkspace.platformDesktop', '桌面端'), value: 'desktop', icon: <DesktopOutlined /> },
            { label: t('system.rolesWorkspace.platformMobile', '移动端'), value: 'mobile', icon: <MobileOutlined /> },
          ]}
        />
        {platform === 'desktop' ? (
          <Segmented<WebMenuVisibilityFilter>
            value={webFilter}
            onChange={(value) => setWebFilter(value)}
            options={filterOptions(webCounts) as { label: string; value: WebMenuVisibilityFilter }[]}
          />
        ) : (
          <Segmented<ExpoMenuVisibilityFilter>
            value={expoFilter}
            onChange={(value) => setExpoFilter(value)}
            options={filterOptions({
              all: expoMenu.allRoutes.length,
              visible: expoVisibleCount,
              hidden: expoMenu.allRoutes.length - expoVisibleCount,
            }) as { label: string; value: ExpoMenuVisibilityFilter }[]}
          />
        )}
        {editableReadOnly ? null : (
          <span className="roles-ws-hint" style={{ marginLeft: 'auto' }}>
            {t('system.rolesWorkspace.menuDraftHint', '菜单由权限推导；「添加 / 移除权限」会写入待保存的更改')}
          </span>
        )}
      </div>

      {platform === 'desktop' ? (
        webRows.length ? (
          <div className="roles-ws-menu-table" role="table" aria-label={t('system.roles.desktopMenuPreview', '桌面菜单')}>
            {tableHead}
            {webRows.map(({ node, depth }) => {
              const isGroup = Boolean(node.children?.length)
              return (
                <div key={node.key} className={isGroup ? 'roles-ws-menu-row roles-ws-menu-group' : 'roles-ws-menu-row'} role="row">
                  <span role="cell" className="roles-ws-menu-name" style={{ paddingLeft: depth * 20, fontWeight: isGroup ? 600 : 400 }}>
                    {isGroup ? <DownOutlined style={{ fontSize: 11, color: '#667085' }} /> : null}
                    <span className="roles-ws-ellipsis" title={node.title}>{node.title}</span>
                  </span>
                  <span role="cell" className="roles-ws-code roles-ws-ellipsis" title={node.path}>{node.path}</span>
                  <span role="cell" style={{ fontSize: 13 }}>{renderStatus(node.visible, baselineWebVisibility.get(node.key))}</span>
                  <span role="cell">{renderPermissionCodes(node.permissionCodes, isGroup)}</span>
                  <span role="cell" className="roles-ws-menu-action">{renderAction(node.edit)}</span>
                </div>
              )
            })}
          </div>
        ) : (
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('system.roles.noVisibleMenus', '暂无可见菜单')} />
        )
      ) : (
        <>
          <div className="roles-ws-toolbar" style={{ gap: 8 }}>
            <span className="roles-ws-hint">{t('system.roles.expoDirectTabs', 'HbwebExpo 底部直接入口')}</span>
            {expoMenu.displayTabs.length ? expoMenu.displayTabs.map((tab) => (
              <NeutralChip key={tab.key}>
                {tab.type === 'store'
                  ? `${tab.zhTitle} · ${t('system.roles.expoStoreChildrenCount', '{{count}} 个门店子入口', { count: tab.children.length })}`
                  : tab.route.zhTitle}
              </NeutralChip>
            )) : (
              <Typography.Text type="secondary">{t('system.roles.noVisibleExpoTabs', '暂无可见 HbwebExpo 底部入口')}</Typography.Text>
            )}
          </div>
          {expoRoutes.length ? (
            <div className="roles-ws-menu-table" role="table" aria-label={t('system.roles.expoMobileMenuPreview', 'HbwebExpo 移动端菜单')}>
              {tableHead}
              {expoRoutes.map((route) => (
                <div key={route.routeName} className="roles-ws-menu-row" role="row">
                  <span role="cell" className="roles-ws-menu-name">
                    <span>
                      {route.zhTitle}
                      <span className="roles-ws-menu-sub" style={{ marginLeft: 6 }}>{route.enTitle}</span>
                    </span>
                  </span>
                  <span role="cell" className="roles-ws-code roles-ws-ellipsis" title={route.routeName}>{route.routeName}</span>
                  <span role="cell" style={{ fontSize: 13 }}>{renderStatus(route.visible, baselineExpoVisibility.get(route.routeName))}</span>
                  <span role="cell">{renderPermissionCodes(route.permissionCodes)}</span>
                  <span role="cell" className="roles-ws-menu-action">{renderAction(toExpoEditTarget(route))}</span>
                </div>
              ))}
            </div>
          ) : (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('system.roles.noVisibleExpoTabs', '暂无可见 HbwebExpo 底部入口')} />
          )}
        </>
      )}
    </div>
  )
}
