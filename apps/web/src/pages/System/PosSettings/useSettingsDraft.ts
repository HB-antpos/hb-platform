import { useCallback, useEffect, useMemo, useState } from 'react'
import type { PosSettingDefinition } from '../../../types/posSettings'
import { buildDraft, diffDraft, type SettingChange, type SettingDraft, type TriState } from './logic'

export interface SettingsDraftState {
  /** 服务端当前值对应的三态（用来判断哪些项被改了）。 */
  original: SettingDraft
  draft: SettingDraft
  changes: SettingChange[]
  setTri: (key: string, tri: TriState) => void
  reset: () => void
}

/**
 * 某一层的编辑草稿：values 是服务端返回的「已设置的项」。
 * values 换了对象（重新取数、保存成功后返回新值）就以新值重置草稿，避免残留旧的未保存修改。
 */
export function useSettingsDraft(
  definitions: readonly PosSettingDefinition[],
  values: Readonly<Record<string, boolean>> | null | undefined,
): SettingsDraftState {
  const original = useMemo(() => buildDraft(definitions, values), [definitions, values])
  const [draft, setDraft] = useState<SettingDraft>(original)

  useEffect(() => {
    setDraft(original)
  }, [original])

  const changes = useMemo(() => diffDraft(definitions, original, draft), [definitions, draft, original])
  const setTri = useCallback((key: string, tri: TriState) => setDraft((current) => ({ ...current, [key]: tri })), [])
  const reset = useCallback(() => setDraft(original), [original])
  return { original, draft, changes, setTri, reset }
}
