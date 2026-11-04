import { createContext, createElement, useContext, type ReactNode } from 'react'
import { useStore } from 'zustand'
import { createStore, type StoreApi } from 'zustand/vanilla'
import { savePlan } from '../../../../services/planogramService'
import type {
  ConflictDto,
  HookModelDto,
  PlanogramDetailDto,
  PlanogramListItemDto,
  PlanogramStatsDto,
  ProductSpecListItemDto,
  ShelfModelDto,
} from '../../../../types/planogram'
import { resolvePlanogramError, type ResolvedPlanogramError } from '../planogramLogic'
import {
  buildSaveInput,
  clampInt,
  docFromDetail,
  MAX_BAYS,
  MAX_GAP_MM,
  MAX_PLAN_ITEMS,
  MIN_BAYS,
  patchItem,
  pushHistory,
  removeItemByGuid,
  sameConflictSet,
  type DesignerDoc,
  type DesignerItem,
  type Slot,
} from './designerLogic'

/**
 * 设计器状态（zustand，一个设计器页面实例一个 store，所以保活的多个方案互不干扰）：
 * - doc：正在编辑的整份内容（头字段 + 明细），不可变更新；past / future 是撤销 / 重做快照栈（上限 50，快照只是引用，很便宜）；
 * - savedDoc：最近一次载入 / 保存成功时的 doc 引用，dirty = (doc !== savedDoc)，撤销回到保存点就不再算有改动；
 * - 保存失败（网络、校验、并发冲突）一律不动 doc，本地改动不丢；
 * - 冲突以本地实时复算为准（见 useDesignerLayout）；保存成功后以服务端返回为准，直到下一次编辑。
 */

export type SaveStatus = 'idle' | 'saving'

/** 服务端对「保存时那一版 doc」的复算结果：doc 引用不变期间界面显示它，一旦继续编辑就回到本地复算。 */
export interface ServerResult {
  doc: DesignerDoc
  conflicts: ConflictDto[]
  stats: PlanogramStatsDto
  /** true = 来自刚才的保存响应（界面以它为准直到下一次编辑）；false = 载入时的结果（只在本地没法复算时兜底）。 */
  fromSave: boolean
}

export type SaveOutcome =
  | { kind: 'ok'; serverConflictCount: number; localConflictCount: number | null; differs: boolean }
  | { kind: 'skipped'; reason: 'readOnly' | 'saving' | 'noName' | 'tooManyItems' | 'noShelf' }
  | { kind: 'stale'; resolved: Extract<ResolvedPlanogramError, { kind: 'error' }> }
  | { kind: 'error'; resolved: ResolvedPlanogramError }

export interface DesignerState {
  planGuid: string
  /** 页面级只读（已归档 / 没有编辑权限 / 窄屏）；由外部通过 setReadOnly 同步。 */
  readOnly: boolean
  header: PlanogramListItemDto
  rowVersion: string
  doc: DesignerDoc
  savedDoc: DesignerDoc
  past: DesignerDoc[]
  future: DesignerDoc[]
  selectedGuid: string | null
  /** productCode → 商品参数行：方案内商品 + 从左栏拖入过的商品（含零售价、图片、名称）。 */
  specs: Record<string, ProductSpecListItemDto>
  hooks: HookModelDto[]
  /** 工具栏可选的货架型号（启用的 + 当前方案正在用的）。 */
  shelfModels: ShelfModelDto[]
  serverResult: ServerResult | null
  saveStatus: SaveStatus
  /** 保存时遇到 PLANOGRAM_STALE：别人（或另一个窗口）已更新了方案，界面提示并提供重新载入。 */
  stale: boolean
}

export interface DesignerActions {
  select: (guid: string | null) => void
  addItem: (item: DesignerItem, spec?: ProductSpecListItemDto) => void
  /** coalesce：右栏数字框里连续改孔位时合并成一个撤销快照。 */
  moveItem: (guid: string, slot: Slot, options?: { coalesce?: boolean }) => void
  patchItem: (guid: string, patch: Partial<Omit<DesignerItem, 'guid'>>) => void
  removeItem: (guid: string) => void
  /** 整体替换明细（整行均分等批量操作），一次操作一个撤销快照。 */
  replaceItems: (items: readonly DesignerItem[], selectGuid?: string | null) => void
  setHeader: (patch: Partial<Pick<DesignerDoc, 'name' | 'notes' | 'shelfModelGuid' | 'bayCount' | 'gapXMm' | 'gapYMm'>>) => void
  undo: () => void
  redo: () => void
  registerSpecs: (items: readonly ProductSpecListItemDto[]) => void
  setShelfModels: (models: readonly ShelfModelDto[]) => void
  setReadOnly: (readOnly: boolean) => void
  dismissStale: () => void
  /** 其他接口（发布 PLANOGRAM_STALE）发现方案已被别人更新：标记「已被他人更新」，界面弹出重新载入提示。 */
  markStale: () => void
  /** 发布成功等「服务端返回了新详情、但编辑内容没变」的场景：只刷新头 / rowVersion / 冲突与统计 / 商品与挂钩目录，不动 doc。 */
  applyServerDetail: (detail: PlanogramDetailDto) => void
  /** localConflicts：保存那一刻本地复算的冲突，用来和服务端返回对比并给出「口径不一致」提示。 */
  save: (localConflicts?: readonly ConflictDto[]) => Promise<SaveOutcome>
}

export type DesignerStore = DesignerState & DesignerActions
export type DesignerStoreApi = StoreApi<DesignerStore>

/** 同一个键的连续编辑（敲名称、改间距）在这个时间窗内合并成一个撤销快照。 */
const COALESCE_WINDOW_MS = 1000

function mergeSpecs(current: Record<string, ProductSpecListItemDto>, items: readonly ProductSpecListItemDto[]): Record<string, ProductSpecListItemDto> {
  if (items.length === 0) return current
  const next = { ...current }
  for (const item of items) next[item.productCode] = item
  return next
}

function mergeShelfModels(models: readonly ShelfModelDto[], current: ShelfModelDto | null | undefined): ShelfModelDto[] {
  const byGuid = new Map<string, ShelfModelDto>()
  for (const model of models) byGuid.set(model.guid, model)
  // 当前方案正在用的货架（可能已停用，不在启用列表里）也要能显示在下拉里
  if (current && current.guid && !byGuid.has(current.guid)) byGuid.set(current.guid, current)
  return [...byGuid.values()]
}

export function createDesignerStore(detail: PlanogramDetailDto, options: { readOnly?: boolean } = {}): DesignerStoreApi {
  const doc = docFromDetail(detail)
  const specs = mergeSpecs({}, detail.productSpecs)
  let lastCoalesce: { key: string; at: number } | null = null

  return createStore<DesignerStore>()((set, get) => {
    /** 提交一份新文档：压撤销栈、清重做栈。coalesceKey 相同且在时间窗内的连续提交合并成一个快照。 */
    const commit = (next: DesignerDoc, coalesceKey?: string) => {
      const state = get()
      if (next === state.doc) return
      const now = Date.now()
      const coalesce = coalesceKey !== undefined && lastCoalesce?.key === coalesceKey && now - lastCoalesce.at < COALESCE_WINDOW_MS
      lastCoalesce = coalesceKey === undefined ? null : { key: coalesceKey, at: now }
      set({
        doc: next,
        past: coalesce ? state.past : pushHistory(state.past, state.doc),
        future: [],
      })
    }

    const guard = () => get().readOnly

    return {
      planGuid: detail.header.guid,
      readOnly: options.readOnly === true,
      header: detail.header,
      rowVersion: detail.rowVersion,
      doc,
      savedDoc: doc,
      past: [],
      future: [],
      selectedGuid: null,
      specs,
      hooks: detail.hookModels,
      shelfModels: mergeShelfModels([], detail.shelfModel),
      serverResult: { doc, conflicts: detail.conflicts, stats: detail.stats, fromSave: false },
      saveStatus: 'idle',
      stale: false,

      select: (guid) => {
        if (get().selectedGuid !== guid) set({ selectedGuid: guid })
      },

      addItem: (item, spec) => {
        if (guard()) return
        const state = get()
        if (state.doc.items.length >= MAX_PLAN_ITEMS) return
        if (spec) set({ specs: mergeSpecs(state.specs, [spec]) })
        commit({ ...state.doc, items: [...state.doc.items, item] })
        set({ selectedGuid: item.guid })
      },

      moveItem: (guid, slot, options) => {
        if (guard()) return
        const state = get()
        const items = patchItem(state.doc.items, guid, { bayIndex: slot.bayIndex, holeCol: slot.holeCol, holeRow: slot.holeRow })
        if (items !== state.doc.items) commit({ ...state.doc, items }, options?.coalesce ? `move:${guid}` : undefined)
      },

      patchItem: (guid, patch) => {
        if (guard()) return
        const state = get()
        const items = patchItem(state.doc.items, guid, patch)
        // 手填每钩挂数在输入框里连续调整，合并成一个快照
        if (items !== state.doc.items) commit({ ...state.doc, items }, 'patch-qty:' + guid + ':' + Object.keys(patch).join(','))
      },

      removeItem: (guid) => {
        if (guard()) return
        const state = get()
        const items = removeItemByGuid(state.doc.items, guid)
        if (items === state.doc.items) return
        commit({ ...state.doc, items })
        if (state.selectedGuid === guid) set({ selectedGuid: null })
      },

      replaceItems: (items, selectGuid) => {
        if (guard()) return
        const state = get()
        if (items !== state.doc.items) commit({ ...state.doc, items })
        if (selectGuid !== undefined) set({ selectedGuid: selectGuid })
      },

      setHeader: (patch) => {
        if (guard()) return
        const state = get()
        const next: DesignerDoc = { ...state.doc }
        let changed = false
        if (patch.name !== undefined && patch.name !== next.name) {
          next.name = patch.name
          changed = true
        }
        if (patch.notes !== undefined && patch.notes !== next.notes) {
          next.notes = patch.notes
          changed = true
        }
        if (patch.shelfModelGuid !== undefined && patch.shelfModelGuid !== next.shelfModelGuid) {
          next.shelfModelGuid = patch.shelfModelGuid
          changed = true
        }
        if (patch.bayCount !== undefined) {
          const bayCount = clampInt(patch.bayCount, MIN_BAYS, MAX_BAYS)
          if (bayCount !== next.bayCount) {
            next.bayCount = bayCount
            changed = true
          }
        }
        if (patch.gapXMm !== undefined) {
          const gap = clampInt(patch.gapXMm, 0, MAX_GAP_MM)
          if (gap !== next.gapXMm) {
            next.gapXMm = gap
            changed = true
          }
        }
        if (patch.gapYMm !== undefined) {
          const gap = clampInt(patch.gapYMm, 0, MAX_GAP_MM)
          if (gap !== next.gapYMm) {
            next.gapYMm = gap
            changed = true
          }
        }
        if (!changed) return
        // 名称 / 备注 / 间距：连续输入合并快照；货架与节数是一次性选择，每次一个快照
        const key = patch.name !== undefined ? 'name' : patch.notes !== undefined ? 'notes' : patch.gapXMm !== undefined ? 'gapX' : patch.gapYMm !== undefined ? 'gapY' : undefined
        commit(next, key)
      },

      undo: () => {
        if (guard()) return
        const state = get()
        const previous = state.past[state.past.length - 1]
        if (!previous) return
        lastCoalesce = null
        set({
          doc: previous,
          past: state.past.slice(0, -1),
          future: [...state.future, state.doc],
          selectedGuid: state.selectedGuid && previous.items.some((item) => item.guid === state.selectedGuid) ? state.selectedGuid : null,
        })
      },

      redo: () => {
        if (guard()) return
        const state = get()
        const next = state.future[state.future.length - 1]
        if (!next) return
        lastCoalesce = null
        set({
          doc: next,
          past: pushHistory(state.past, state.doc),
          future: state.future.slice(0, -1),
          selectedGuid: state.selectedGuid && next.items.some((item) => item.guid === state.selectedGuid) ? state.selectedGuid : null,
        })
      },

      registerSpecs: (items) => {
        const next = mergeSpecs(get().specs, items)
        if (next !== get().specs) set({ specs: next })
      },

      setShelfModels: (models) => {
        const state = get()
        const current = state.shelfModels.find((model) => model.guid === state.doc.shelfModelGuid)
        set({ shelfModels: mergeShelfModels(models, current) })
      },

      setReadOnly: (readOnly) => {
        if (get().readOnly !== readOnly) set({ readOnly })
      },

      dismissStale: () => set({ stale: false }),

      markStale: () => set({ stale: true }),

      applyServerDetail: (detail) => {
        const current = get()
        set({
          header: detail.header,
          rowVersion: detail.rowVersion,
          // 服务端结果对应的是「已保存的那一版」（发布前必须已保存），界面在 doc 没变期间以它为准
          serverResult: { doc: current.savedDoc, conflicts: detail.conflicts, stats: detail.stats, fromSave: true },
          specs: mergeSpecs(current.specs, detail.productSpecs),
          hooks: detail.hookModels.length > 0 ? detail.hookModels : current.hooks,
        })
      },

      save: async (localConflicts) => {
        const state = get()
        if (state.readOnly) return { kind: 'skipped', reason: 'readOnly' }
        if (state.saveStatus === 'saving') return { kind: 'skipped', reason: 'saving' }
        const sentDoc = state.doc
        if (!sentDoc.name.trim()) return { kind: 'skipped', reason: 'noName' }
        if (sentDoc.items.length > MAX_PLAN_ITEMS) return { kind: 'skipped', reason: 'tooManyItems' }
        if (!state.shelfModels.some((model) => model.guid === sentDoc.shelfModelGuid)) return { kind: 'skipped', reason: 'noShelf' }

        set({ saveStatus: 'saving' })
        try {
          const saved = await savePlan(state.planGuid, buildSaveInput(sentDoc, state.rowVersion))
          const flags = new Map(saved.items.map((item) => [item.guid, item.specChanged === true]))
          // 服务端保存后重新比对了商品尺寸快照：回填「参数已变」标记（保存即把快照更新到当前参数，所以通常全部清零）
          const refreshed: DesignerDoc = sentDoc.items.some((item) => item.specChanged !== (flags.get(item.guid) ?? false))
            ? { ...sentDoc, items: sentDoc.items.map((item) => (item.specChanged === (flags.get(item.guid) ?? false) ? item : { ...item, specChanged: flags.get(item.guid) ?? false })) }
            : sentDoc
          const current = get()
          // 保存请求在路上的时候用户又改了：当前 doc 不动（仍算有改动），只把「保存点」推进到刚保存的那一版
          const editedMeanwhile = current.doc !== sentDoc
          set({
            header: saved.header,
            rowVersion: saved.rowVersion,
            savedDoc: refreshed,
            doc: editedMeanwhile ? current.doc : refreshed,
            serverResult: { doc: refreshed, conflicts: saved.conflicts, stats: saved.stats, fromSave: true },
            specs: mergeSpecs(current.specs, saved.productSpecs),
            hooks: saved.hookModels.length > 0 ? saved.hookModels : current.hooks,
            saveStatus: 'idle',
            stale: false,
          })
          return {
            kind: 'ok',
            // 两边都按「冲突记录条数」报（成对冲突各算两条）：条目去重后的数字可能一样，而冲突集合其实不同
            serverConflictCount: saved.conflicts.length,
            localConflictCount: localConflicts ? localConflicts.length : null,
            differs: localConflicts ? !sameConflictSet(localConflicts, saved.conflicts) : false,
          }
        } catch (error) {
          set({ saveStatus: 'idle' })
          const resolved = resolvePlanogramError(error)
          if (resolved.kind === 'error' && resolved.code === 'PLANOGRAM_STALE') {
            set({ stale: true })
            return { kind: 'stale', resolved }
          }
          return { kind: 'error', resolved }
        }
      },
    }
  })
}

// ---------------------------------------------------------------------------
// React 接线：store 通过 context 提供，组件用 useDesignerStore(selector) 取切片
// ---------------------------------------------------------------------------

const DesignerStoreContext = createContext<DesignerStoreApi | null>(null)

export function DesignerStoreProvider({ store, children }: { store: DesignerStoreApi; children: ReactNode }) {
  return createElement(DesignerStoreContext.Provider, { value: store }, children)
}

export function useDesignerStoreApi(): DesignerStoreApi {
  const store = useContext(DesignerStoreContext)
  if (!store) throw new Error('useDesignerStore must be used inside DesignerStoreProvider')
  return store
}

export function useDesignerStore<T>(selector: (state: DesignerStore) => T): T {
  return useStore(useDesignerStoreApi(), selector)
}

/** 有未保存改动：当前 doc 不是保存点那一份。 */
export const selectDirty = (state: DesignerStore) => state.doc !== state.savedDoc
