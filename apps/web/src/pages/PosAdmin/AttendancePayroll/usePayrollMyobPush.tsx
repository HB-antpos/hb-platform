import { useCallback, useEffect, useMemo, useRef, useState, type ReactElement } from 'react'
import { useNavigate } from 'react-router-dom'

import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getMyobCompanies, getMyobTimesheetPreview } from '../../../services/myobService'
import type { MyobCompanyOption, MyobTimesheetPreview } from '../../../types/myob'
import myobMessagesEn from '../Myob/myobMessages.en.json'
import myobMessagesZh from '../Myob/myobMessages.zh.json'
import { getPushableUserGuids } from '../Myob/myobPushLogic'
import PushBatchesDrawer from '../Myob/PushBatchesDrawer'
import PushConfirmModal from '../Myob/PushConfirmModal'

import '../Myob/myob.css'
import {
  buildPayrollPushCells,
  getPayrollPushButton,
  pickCompanyForStore,
  type PayrollPushButton,
  type PayrollPushCell,
  type PayrollPushLoadStatus,
} from './payrollMyobPushLogic'

// 推送弹窗直接复用 MYOB 页的确认 / 进度 / 结果组件和文案；单独打开考勤工资页时 MYOB 页模块没加载过，所以这里自己注册（幂等）。
registerPageMessages({ zh: myobMessagesZh, en: myobMessagesEn })

interface UsePayrollMyobPushInput {
  /** 有 Myob.Timesheet.Push 与工资编辑权限（预览接口要工资编辑），且正在看工资表页签。 */
  allowed: boolean
  storeCode: string | undefined
  periodStart: string | undefined
  /** 所看周期已提交锁定。 */
  locked: boolean
  canGoBind: boolean
  canGoMapping: boolean
}

export interface PayrollMyobPush {
  button: PayrollPushButton
  /** 工资表「MYOB 推送」列；没有预览（按钮不可用）时为 null，不显示这一列。 */
  cells: Record<string, PayrollPushCell> | null
  open: () => void
  /** 弹窗与推送记录抽屉，放在页面末尾。 */
  modals: ReactElement | null
}

/**
 * 工资表页的「推送到 MYOB」：
 * 本期已锁定且有权限时，按当前分店找到所属 MYOB 公司，读一次该公司这一期的推送预览（只读，不检查 MYOB）；
 * 预览同时给出按钮是否可用、每个人的推送状态和弹窗里要列的员工。推送 / 撤销后重读。
 */
export function usePayrollMyobPush({ allowed, storeCode, periodStart, locked, canGoBind, canGoMapping }: UsePayrollMyobPushInput): PayrollMyobPush {
  const navigate = useNavigate()
  const companiesRef = useRef<MyobCompanyOption[] | null>(null)
  const requestSeq = useRef(0)
  const [status, setStatus] = useState<PayrollPushLoadStatus>('idle')
  const [preview, setPreview] = useState<MyobTimesheetPreview | null>(null)
  const [version, setVersion] = useState(0)
  // 点过「检查 MYOB 状态」后，之后的重读都带 checkMyob（慢，会逐人读 MYOB）；换分店 / 周期时重置
  const [checkMyob, setCheckMyob] = useState(false)
  const [modalOpen, setModalOpen] = useState(false)
  const [batchesOpen, setBatchesOpen] = useState(false)

  const shouldLoad = allowed && locked && Boolean(storeCode) && Boolean(periodStart)

  useEffect(() => {
    const seq = ++requestSeq.current
    if (!shouldLoad || !storeCode || !periodStart) {
      setStatus('idle')
      setPreview(null)
      return undefined
    }
    setStatus('loading')
    void (async () => {
      try {
        // 公司列表只读一次；分店映射改了要刷新页面才生效（映射很少改）
        companiesRef.current ??= await getMyobCompanies()
        const company = pickCompanyForStore(companiesRef.current, storeCode)
        if (!company) {
          if (seq === requestSeq.current) {
            setPreview(null)
            setStatus('unmapped')
          }
          return
        }
        const data = await getMyobTimesheetPreview(company.connectionGuid, { periodStart, checkMyob })
        if (seq === requestSeq.current) {
          setPreview(data)
          setStatus('ready')
        }
      } catch {
        // 只是附加功能：读取失败不弹错误打扰主流程，按钮置灰并在悬停里说明
        if (seq === requestSeq.current) {
          setPreview(null)
          setStatus('error')
        }
      }
    })()
    return () => {
      // 换分店 / 周期时作废还在路上的请求
      requestSeq.current += 1
    }
  }, [checkMyob, periodStart, shouldLoad, storeCode, version])

  useEffect(() => setCheckMyob(false), [periodStart, storeCode])

  const button = getPayrollPushButton({ allowed, locked, status, pushEnabled: preview?.pushEnabled })
  const cells = useMemo(() => (preview && preview.pushEnabled ? buildPayrollPushCells(preview) : null), [preview])
  const reload = useCallback(() => setVersion((value) => value + 1), [])

  // 没有可推送的人时也照常打开：弹窗里每个人被拦截 / 不推送的原因都写着，比什么都不发生更有用
  const open = useCallback(() => {
    if (preview) setModalOpen(true)
  }, [preview])

  const modals = preview ? (
    <>
      <PushConfirmModal
        open={modalOpen}
        preview={preview}
        selectable
        // 默认勾选所有可推送的人；弹窗里可以取消
        selectedUserGuids={getPushableUserGuids(preview)}
        hintsAcknowledged={false}
        onGoBind={canGoBind ? () => navigate('/pos-admin/myob') : undefined}
        onGoMapping={canGoMapping ? () => navigate('/pos-admin/myob') : undefined}
        onCheckMyob={() => (checkMyob ? reload() : setCheckMyob(true))}
        checkingMyob={status === 'loading'}
        onClose={(changed) => {
          setModalOpen(false)
          if (changed) reload()
        }}
        onOpenBatches={() => {
          setModalOpen(false)
          setBatchesOpen(true)
          reload()
        }}
      />
      <PushBatchesDrawer
        open={batchesOpen}
        connectionGuid={preview.connectionGuid}
        connectionName={preview.connectionName}
        periodStart={preview.periodStart}
        periodEnd={preview.periodEnd}
        onClose={() => setBatchesOpen(false)}
        onUndone={reload}
      />
    </>
  ) : null

  return { button, cells, open, modals }
}
