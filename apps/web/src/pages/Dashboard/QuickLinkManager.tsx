import { AppstoreOutlined, CloseOutlined, HolderOutlined, PlusOutlined, SearchOutlined } from '@ant-design/icons'
import {
  DndContext,
  KeyboardSensor,
  PointerSensor,
  closestCenter,
  type DragEndEvent,
  useSensor,
  useSensors,
} from '@dnd-kit/core'
import {
  SortableContext,
  sortableKeyboardCoordinates,
  useSortable,
  verticalListSortingStrategy,
} from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'
import { Button, Drawer, Empty, Input } from 'antd'
import { useMemo, useState, type CSSProperties } from 'react'
import { useTranslation } from 'react-i18next'
import {
  QUICK_LINK_LIMIT,
  filterLinks,
  groupLinksByModule,
  moveKey,
  pinKey,
  unpinKey,
  type QuickLinkEntry,
} from './quickLinks'

interface QuickLinkManagerProps {
  open: boolean
  entries: QuickLinkEntry[]
  pinnedKeys: string[]
  onChange: (keys: string[]) => void
  onReset: () => void
  onClose: () => void
}

function PinnedRow({ entry, onRemove }: { entry: QuickLinkEntry; onRemove: () => void }) {
  const { t } = useTranslation()
  const { attributes, listeners, setNodeRef, setActivatorNodeRef, transform, transition, isDragging } = useSortable({
    id: entry.key,
  })
  const style: CSSProperties = {
    transform: CSS.Translate.toString(transform),
    transition,
    zIndex: isDragging ? 2 : undefined,
  }
  // 整行都能拖动（左侧抓手只是视觉提示）；键盘只在行本身获得焦点时才能起拖，避免和行内「移除」按钮抢空格键。
  const setRefs = (element: HTMLDivElement | null) => {
    setNodeRef(element)
    setActivatorNodeRef(element)
  }

  return (
    <div
      ref={setRefs}
      className="workbench-pin-row"
      style={style}
      data-dragging={isDragging}
      {...attributes}
      {...listeners}
    >
      <HolderOutlined className="workbench-pin-grip" aria-hidden />
      <span className="workbench-pin-icon">{entry.icon ?? <AppstoreOutlined />}</span>
      <span className="workbench-pin-name">
        {entry.title}
        {entry.module !== entry.title ? <small>{entry.module}</small> : null}
      </span>
      <button
        type="button"
        className="workbench-icon-btn"
        aria-label={t('dashboard.managerRemove', { title: entry.title })}
        onClick={onRemove}
      >
        <CloseOutlined />
      </button>
    </div>
  )
}

export default function QuickLinkManager({ open, entries, pinnedKeys, onChange, onReset, onClose }: QuickLinkManagerProps) {
  const { t } = useTranslation()
  const [query, setQuery] = useState('')
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 6 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  )

  const entryByKey = useMemo(() => new Map(entries.map((entry) => [entry.key, entry])), [entries])
  const pinnedEntries = pinnedKeys.map((key) => entryByKey.get(key)).filter((entry): entry is QuickLinkEntry => Boolean(entry))
  const pinnedSet = new Set(pinnedKeys)
  const availableModules = useMemo(
    () => groupLinksByModule(filterLinks(entries, query).filter((entry) => !pinnedSet.has(entry.key))),
    // pinnedSet 每次渲染重建，依赖 pinnedKeys 即可
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [entries, query, pinnedKeys],
  )
  const atLimit = pinnedKeys.length >= QUICK_LINK_LIMIT

  const handleDragEnd = ({ active, over }: DragEndEvent) => {
    if (over && active.id !== over.id) {
      onChange(moveKey(pinnedKeys, String(active.id), String(over.id)))
    }
  }

  return (
    <Drawer
      open={open}
      onClose={onClose}
      width={460}
      title={t('dashboard.managerTitle')}
      footer={
        <div className="workbench-drawer-footer">
          <Button type="text" onClick={onReset}>
            {t('dashboard.managerReset')}
          </Button>
          <Button type="primary" onClick={onClose}>
            {t('dashboard.managerDone')}
          </Button>
        </div>
      }
    >
      <Input
        allowClear
        prefix={<SearchOutlined />}
        placeholder={t('dashboard.managerSearch')}
        value={query}
        onChange={(event) => setQuery(event.target.value)}
      />

      <div className="workbench-drawer-sub">
        <strong>{t('dashboard.managerPinned')}</strong>
        <span>{t('dashboard.managerPinnedHint', { count: pinnedEntries.length, max: QUICK_LINK_LIMIT })}</span>
      </div>
      {pinnedEntries.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('dashboard.managerPinnedEmpty')} />
      ) : (
        <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
          <SortableContext items={pinnedEntries.map((entry) => entry.key)} strategy={verticalListSortingStrategy}>
            {pinnedEntries.map((entry) => (
              <PinnedRow key={entry.key} entry={entry} onRemove={() => onChange(unpinKey(pinnedKeys, entry.key))} />
            ))}
          </SortableContext>
        </DndContext>
      )}

      <div className="workbench-drawer-sub">
        <strong>{t('dashboard.managerAvailable')}</strong>
        <span>{atLimit ? t('dashboard.limitReached', { max: QUICK_LINK_LIMIT }) : t('dashboard.managerAvailableHint')}</span>
      </div>
      {availableModules.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('dashboard.managerNoResult')} />
      ) : (
        availableModules.map((group) => (
          <div key={group.key} className="workbench-avail-group">
            <div className="workbench-avail-title">{group.title}</div>
            {group.links.map((entry) => (
              <div key={entry.key} className="workbench-avail-row">
                <span>{entry.title}</span>
                <button
                  type="button"
                  className="workbench-icon-btn workbench-icon-btn-primary"
                  disabled={atLimit}
                  aria-label={t('dashboard.managerAdd', { title: entry.title })}
                  onClick={() => onChange(pinKey(pinnedKeys, entry.key))}
                >
                  <PlusOutlined />
                </button>
              </div>
            ))}
          </div>
        ))
      )}
    </Drawer>
  )
}
