import {
  CloseCircleOutlined,
  CloseOutlined,
  DoubleLeftOutlined,
  DoubleRightOutlined,
  LockFilled,
  LockOutlined,
  PushpinFilled,
  PushpinOutlined,
  ReloadOutlined,
  UnlockOutlined,
} from '@ant-design/icons'
import {
  DndContext,
  PointerSensor,
  closestCenter,
  type CollisionDetection,
  type DragEndEvent,
  useSensor,
  useSensors,
} from '@dnd-kit/core'
import { SortableContext, horizontalListSortingStrategy, useSortable } from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'
import { Button, Dropdown, Space, Tabs } from 'antd'
import type { MenuProps } from 'antd'
import type { CSSProperties, KeyboardEvent, ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import type { TabItem } from '../types/router'
import { useTabsStore } from '../store/tabs'
import { getAdjacentTabKey, getClosableKeys, isClosableTab, isLockedTab } from '../store/tabsLogic'
import { useTranslation } from 'react-i18next'

interface AppTabsProps {
  onRefreshCurrent: () => void
  /** 刷新指定页签（右键菜单作用于被点击的页签，不一定是当前页签）。 */
  onRefreshTab: (key: string) => void
  onRemoveTab: (key: string) => void
  onRemoveOtherTabs: (key: string) => void
  onRemoveLeftTabs: (key: string) => void
  onRemoveRightTabs: (key: string) => void
}

interface TabNodeShellProps {
  tabKey: string
  menuItems: MenuProps['items']
  children: ReactNode
}

/** 右键菜单：包一层 div 作为触发器，避免给 rc-tabs 的页签节点直接塞 props / ref。 */
function TabContextMenu({ menuItems, children }: Pick<TabNodeShellProps, 'menuItems' | 'children'>) {
  return (
    <Dropdown menu={{ items: menuItems }} trigger={['contextMenu']}>
      <div className="app-tab-context-target">{children}</div>
    </Dropdown>
  )
}

interface SortableTabNodeProps extends TabNodeShellProps {
  locked: boolean
  /** 锁定组的最后一个页签：右侧画分隔线，区分锁定组与普通组。 */
  groupEnd: boolean
  onKeyboardMove: (direction: 'left' | 'right') => void
}

/**
 * 可排序页签：整个页签都是拖动区域（不再需要单独的手柄）。
 * 拖动由 PointerSensor 的 6px 阈值触发，所以单击仍是切换页签；右键不会触发拖动。
 * 键盘：聚焦页签后按 Alt+←/→ 在本组内调整顺序。
 */
function SortableTabNode({ tabKey, locked, groupEnd, menuItems, onKeyboardMove, children }: SortableTabNodeProps) {
  const { listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: tabKey })

  const style: CSSProperties = {
    transform: CSS.Translate.toString(transform),
    transition,
    zIndex: isDragging ? 1 : undefined,
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (!event.altKey || (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight')) {
      return
    }
    // 拦下来，避免 rc-tabs 把 Alt+方向键当成切换页签焦点。
    event.preventDefault()
    event.stopPropagation()
    onKeyboardMove(event.key === 'ArrowLeft' ? 'left' : 'right')
  }

  return (
    <div
      ref={setNodeRef}
      className="app-tab-sortable-node"
      style={style}
      data-dnd-kit-dragging={isDragging}
      data-tab-locked={locked}
      data-tab-group-end={groupEnd}
      aria-keyshortcuts="Alt+ArrowLeft Alt+ArrowRight"
      {...listeners}
      onKeyDown={handleKeyDown}
    >
      <TabContextMenu menuItems={menuItems}>{children}</TabContextMenu>
    </div>
  )
}

export default function AppTabs({
  onRefreshCurrent,
  onRefreshTab,
  onRemoveTab,
  onRemoveOtherTabs,
  onRemoveLeftTabs,
  onRemoveRightTabs,
}: AppTabsProps) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { tabs, activeKey, pinTabsBar, setPinTabsBar, moveTab, lockTab, unlockTab } = useTabsStore()
  const sensors = useSensors(
    useSensor(PointerSensor, {
      activationConstraint: {
        distance: 6,
      },
    }),
  )
  const sortableTabKeys = tabs.filter((tab) => !tab.affix).map((tab) => tab.key)
  const lockedTabs = tabs.filter(isLockedTab)
  const lockedKeys = new Set(lockedTabs.map((tab) => tab.key))
  const lastLockedKey = lockedTabs[lockedTabs.length - 1]?.key

  const currentTab = tabs.find((item) => item.key === activeKey)

  // 锁定组与普通组各自排序：拖动时只在同组页签里找落点，另一组的页签不会被挤动。
  const collisionDetection: CollisionDetection = (args) =>
    closestCenter({
      ...args,
      droppableContainers: args.droppableContainers.filter(
        (container) => lockedKeys.has(String(container.id)) === lockedKeys.has(String(args.active.id)),
      ),
    })

  const handleDragEnd = ({ active, over }: DragEndEvent) => {
    if (!over || active.id === over.id) {
      return
    }

    moveTab(String(active.id), String(over.id))
  }

  /** 页签右键菜单与右上角「更多」共用同一套：对哪个页签操作就传哪个页签。 */
  const buildTabMenuItems = (tab: TabItem): MenuProps['items'] => {
    const locked = isLockedTab(tab)
    const hasClosable = (scope: 'others' | 'left' | 'right') => getClosableKeys(tabs, tab.key, scope).length > 0

    return [
      {
        key: 'refresh',
        label: t('common.refreshTab'),
        icon: <ReloadOutlined />,
        onClick: () => onRefreshTab(tab.key),
      },
      // 固定页签（工作台）不参与锁定。
      ...(tab.affix
        ? []
        : [
            locked
              ? {
                  key: 'unlock',
                  label: t('common.unlockTab'),
                  icon: <UnlockOutlined />,
                  onClick: () => unlockTab(tab.key),
                }
              : {
                  key: 'lock',
                  label: t('common.lockTab'),
                  icon: <LockOutlined />,
                  onClick: () => lockTab(tab.key),
                },
          ]),
      { type: 'divider' as const },
      {
        key: 'close',
        label: t('common.closeTab'),
        icon: <CloseOutlined />,
        disabled: !isClosableTab(tab),
        extra: locked ? t('common.tabLocked') : undefined,
        onClick: () => onRemoveTab(tab.key),
      },
      {
        key: 'closeOthers',
        label: t('common.closeOtherTabs'),
        icon: <CloseCircleOutlined />,
        disabled: !hasClosable('others'),
        onClick: () => onRemoveOtherTabs(tab.key),
      },
      {
        key: 'closeLeft',
        label: t('common.closeLeftTabs'),
        icon: <DoubleLeftOutlined />,
        disabled: !hasClosable('left'),
        onClick: () => onRemoveLeftTabs(tab.key),
      },
      {
        key: 'closeRight',
        label: t('common.closeRightTabs'),
        icon: <DoubleRightOutlined />,
        disabled: !hasClosable('right'),
        onClick: () => onRemoveRightTabs(tab.key),
      },
    ]
  }

  const moreMenuItems: MenuProps['items'] = [
    {
      key: 'togglePinTabsBar',
      label: pinTabsBar ? t('common.pinTabsBar') : t('common.unpinTabsBar'),
      icon: pinTabsBar ? <PushpinFilled /> : <PushpinOutlined />,
      onClick: () => setPinTabsBar(!pinTabsBar),
    },
    ...(currentTab ? [{ type: 'divider' as const }, ...(buildTabMenuItems(currentTab) ?? [])] : []),
  ]

  return (
    <div className="app-tabs">
      <Tabs
        hideAdd
        type="editable-card"
        activeKey={activeKey}
        renderTabBar={(tabBarProps, DefaultTabBar) => (
          <DndContext sensors={sensors} collisionDetection={collisionDetection} onDragEnd={handleDragEnd}>
            <SortableContext items={sortableTabKeys} strategy={horizontalListSortingStrategy}>
              <DefaultTabBar {...tabBarProps}>
                {(node) => {
                  const tabKey = String(node.key)
                  const tab = tabs.find((item) => item.key === tabKey)

                  if (!tab) {
                    return node
                  }

                  const menuItems = buildTabMenuItems(tab)

                  if (tab.affix) {
                    return (
                      <TabContextMenu key={tabKey} menuItems={menuItems}>
                        {node}
                      </TabContextMenu>
                    )
                  }

                  return (
                    <SortableTabNode
                      key={tabKey}
                      tabKey={tabKey}
                      locked={lockedKeys.has(tabKey)}
                      groupEnd={tabKey === lastLockedKey}
                      menuItems={menuItems}
                      onKeyboardMove={(direction) => {
                        const neighborKey = getAdjacentTabKey(tabs, tabKey, direction)
                        if (neighborKey) {
                          moveTab(tabKey, neighborKey)
                        }
                      }}
                    >
                      {node}
                    </SortableTabNode>
                  )
                }}
              </DefaultTabBar>
            </SortableContext>
          </DndContext>
        )}
        items={tabs.map((tab: TabItem) => {
          const locked = isLockedTab(tab)
          return {
            key: tab.key,
            // 锁定页签用锁图标代替「×」，所以不再提供关闭按钮。
            label: locked ? (
              <span className="app-tab-label">
                {tab.title}
                <LockFilled className="app-tab-lock" />
              </span>
            ) : (
              tab.title
            ),
            closable: tab.closable !== false && !locked,
          }
        })}
        onChange={(key) => {
          navigate(key)
        }}
        onEdit={(targetKey, action) => {
          if (action === 'remove') {
            onRemoveTab(targetKey as string)
          }
        }}
      />
      <Space size={8} className="app-tabs-actions">
        <Button icon={<ReloadOutlined />} onClick={() => onRefreshCurrent()}>
          {t('common.refresh')}
        </Button>
        <Dropdown menu={{ items: moreMenuItems }} placement="bottomRight" trigger={['click']}>
          <Button
            type={pinTabsBar ? 'primary' : 'default'}
            icon={pinTabsBar ? <PushpinFilled /> : <PushpinOutlined />}
          >
            {t('common.more')}
          </Button>
        </Dropdown>
      </Space>
    </div>
  )
}
