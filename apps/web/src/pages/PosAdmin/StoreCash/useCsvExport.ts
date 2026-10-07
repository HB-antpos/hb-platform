import { message } from 'antd'
import { useCallback, useEffect, useRef, useState } from 'react'
import { downloadCsvFile } from './csv'
import type { ExportProgressState, Tr } from './parts'

export type ExportOutcome =
  | { status: 'ok'; content: string; fileName: string; rowCount: number }
  | { status: 'tooMany'; total: number; maxRows: number }
  | { status: 'empty' }

export type ExportTask = (
  signal: AbortSignal,
  onProgress: (progress: ExportProgressState) => void,
) => Promise<ExportOutcome>

/**
 * 每个页签一个「导出 CSV」：同一时刻只跑一次；分页取数时回报进度；
 * 超过行数上限提示缩小范围；离开页面（组件卸载）时中止仍在进行的取数。
 */
export function useCsvExport(tr: Tr, errorText: (error: unknown) => string | null) {
  const [running, setRunning] = useState(false)
  const [progress, setProgress] = useState<ExportProgressState | null>(null)
  const controllerRef = useRef<AbortController | null>(null)

  useEffect(() => () => controllerRef.current?.abort(), [])

  const run = useCallback(async (task: ExportTask) => {
    if (controllerRef.current) return
    const controller = new AbortController()
    controllerRef.current = controller
    setRunning(true)
    setProgress(null)
    try {
      const outcome = await task(controller.signal, setProgress)
      if (outcome.status === 'ok') {
        downloadCsvFile(outcome.content, outcome.fileName)
        message.success(tr('export.success', { fileName: outcome.fileName, rows: outcome.rowCount }))
      } else if (outcome.status === 'tooMany') {
        message.warning(tr('export.tooMany', {
          max: outcome.maxRows.toLocaleString('en-US'),
          total: outcome.total.toLocaleString('en-US'),
        }))
      } else {
        message.info(tr('export.empty'))
      }
    } catch (error) {
      const text = errorText(error)
      if (text) message.error(tr('export.failed', { reason: text }))
    } finally {
      if (controllerRef.current === controller) controllerRef.current = null
      setRunning(false)
      setProgress(null)
    }
  }, [errorText, tr])

  return { running, progress, run }
}
