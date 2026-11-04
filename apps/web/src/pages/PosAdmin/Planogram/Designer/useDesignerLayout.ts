import { useMemo } from 'react'
import type { ConflictDto, PlanogramStatsDto, ShelfModelDto } from '../../../../types/planogram'
import { buildPlaced, validateLayout, type ConflictDetail, type LayoutContext, type LayoutResult, type PlacedItem } from '../engine/geometry'
import { buildLayoutContext, conflictLocateList, conflictsByItem, type DesignerDoc } from './designerLogic'
import { useDesignerStore } from './useDesignerStore'

/** 界面用的冲突：本地复算的带 detail（具体数字），服务端返回的没有。 */
export type DisplayConflict = ConflictDto & { detail?: ConflictDetail }

export interface DesignerLayout {
  doc: DesignerDoc
  shelf: ShelfModelDto | null
  ctx: LayoutContext | null
  placed: PlacedItem[]
  /** 本地实时复算（没法复算时为 null）。 */
  local: LayoutResult | null
  conflicts: DisplayConflict[]
  stats: PlanogramStatsDto
  /** 当前显示的是不是服务端刚返回的结果（保存后、下一次编辑前）。 */
  showingServer: boolean
  conflictsByGuid: Map<string, DisplayConflict[]>
  /** 有 error 级冲突的条目 guid（「冲突 N · 定位」的跳转顺序）。 */
  locateList: string[]
}

const EMPTY_STATS: PlanogramStatsDto = { itemCount: 0, skuCount: 0, unitCount: 0, retailValue: 0, fillPercent: 0, conflictCount: 0 }

/**
 * 设计器的派生布局：本地实时复算为准（每次编辑都按同一套引擎重算，冲突立刻标红）；
 * 保存成功后、下一次编辑之前以服务端返回为准（保存那一刻本地与服务端不一致会在保存时单独提示）；
 * 本地没法复算（货架型号还没取到）时用服务端载入的结果兜底。
 */
export function useDesignerLayout(): DesignerLayout {
  const doc = useDesignerStore((state) => state.doc)
  const specs = useDesignerStore((state) => state.specs)
  const hooks = useDesignerStore((state) => state.hooks)
  const shelfModels = useDesignerStore((state) => state.shelfModels)
  const serverResult = useDesignerStore((state) => state.serverResult)

  const shelf = useMemo(() => shelfModels.find((model) => model.guid === doc.shelfModelGuid) ?? null, [doc.shelfModelGuid, shelfModels])
  const ctx = useMemo(
    () => buildLayoutContext({ shelf, hooks, specs, bayCount: doc.bayCount, gapXMm: doc.gapXMm, gapYMm: doc.gapYMm }),
    [doc.bayCount, doc.gapXMm, doc.gapYMm, hooks, shelf, specs],
  )
  const placed = useMemo(() => (ctx ? buildPlaced(ctx, doc.items) : []), [ctx, doc.items])
  const local = useMemo(() => (ctx ? validateLayout(ctx, doc.items) : null), [ctx, doc.items])

  const serverFresh = serverResult !== null && serverResult.doc === doc
  const showingServer = serverFresh && (serverResult.fromSave || local === null)
  const conflicts: DisplayConflict[] = showingServer ? serverResult.conflicts : local?.conflicts ?? []
  const stats = showingServer ? serverResult.stats : local?.stats ?? EMPTY_STATS

  const conflictsByGuid = useMemo(() => conflictsByItem(conflicts), [conflicts])
  const locateList = useMemo(() => conflictLocateList(conflicts, doc.items), [conflicts, doc.items])

  return { doc, shelf, ctx, placed, local, conflicts, stats, showingServer, conflictsByGuid, locateList }
}
