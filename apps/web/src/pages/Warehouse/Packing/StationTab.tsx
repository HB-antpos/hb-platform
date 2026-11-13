import { BarcodeOutlined, DeleteOutlined, InboxOutlined, PictureOutlined, PrinterOutlined } from '@ant-design/icons'
import { Button, Input, InputNumber, Modal, Select, Switch, Tag, Typography, message } from 'antd'
import type { InputRef } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { useBarcodeScanner } from '../../../hooks/useBarcodeScanner'
import {
  addCartonToPackPallet,
  createPackCarton,
  createPackPallet,
  getPackCarton,
  getPackPallet,
  listPackPallets,
  readPackingError,
  resolvePackCode,
  scanIntoPackCarton,
  sealPackCarton,
  sealPackPallet,
  searchPackOrders,
  setPackCartonItemQuantity,
  updatePackCartonHeader,
} from '../../../services/warehousePackingService'
import type {
  PackCartonDetail,
  PackCartonItem,
  PackCartonSummary,
  PackOrderOption,
  PackPalletDetail,
  PackPalletSummary,
  PackProductCandidate,
  PackResolveResult,
} from '../../../types/warehousePacking'
import {
  PACK_CARTON_STATUS,
  PACK_DESTINATION_TYPE,
  PACK_ERROR_CODE,
  PACK_PALLET_STATUS,
} from '../../../types/warehousePacking'
import { isScanFeedbackUnlocked, playScanFeedback, unlockScanFeedback } from '../../../utils/scanFeedback'
import type { ScanFeedbackTone } from '../../../utils/scanFeedback'

import { DestinationModal } from './DestinationSelect'
import type { PackCountMode, PackDestinationValue } from './packingLogic'
import {
  UNASSIGNED_DESTINATION,
  destinationToRequest,
  destinationToValue,
  findCartonItemByCode,
  looksLikePackNo,
  parseWeightInput,
  resolveOrderProgress,
  resolveScanStep,
  resolveStationHotkey,
} from './packingLogic'
import { CartonStatusTag, DestinationText, showPackingError, usePackingPage } from './packingShared'
import { buildCartonLabelFields, buildPalletLabelFields, printPackLabels } from './packLabel'
import QuantityStepper from './QuantityStepper'

// 打包台的当前箱 / 当前托盘 / 计数方式记在本机：刷新页面或换班后接着装（同一只箱也可以在手机端接着装）。
const STORAGE_CARTON = 'hb.warehousePacking.station.cartonGuid'
const STORAGE_PALLET = 'hb.warehousePacking.station.palletGuid'
const STORAGE_PALLET_ENABLED = 'hb.warehousePacking.station.palletEnabled'
const STORAGE_COUNT_MODE = 'hb.warehousePacking.station.countMode'

function readStorage(key: string): string | null {
  try {
    return window.localStorage.getItem(key)
  } catch {
    return null
  }
}

function writeStorage(key: string, value: string | null) {
  try {
    if (value === null) {
      window.localStorage.removeItem(key)
    } else {
      window.localStorage.setItem(key, value)
    }
  } catch {
    // 本机存储不可用时只是不记忆，不影响装箱。
  }
}

interface NextCartonHeader {
  destination: PackDestinationValue
  orderGuid: string | null
}

interface ScanStatus {
  tone: 'success' | 'warning' | 'error'
  text: string
}

interface CandidatePrompt {
  code: string
  candidates: PackProductCandidate[]
}

type DestinationTarget = 'carton' | 'newPallet'

interface StationTabProps {
  /** 本标签可见且页面处于激活状态：只有这时才监听扫码枪和快捷键。 */
  active: boolean
}

export default function StationTab({ active }: StationTabProps) {
  const { t } = useTranslation()
  const { notifyChanged, openCarton, openPallet, dataVersion } = usePackingPage()

  const [carton, setCarton] = useState<PackCartonDetail | null>(null)
  const [highlightCode, setHighlightCode] = useState<string | null>(null)
  const [countMode, setCountMode] = useState<PackCountMode>(() => {
    const saved = readStorage(STORAGE_COUNT_MODE)
    return saved === 'middle' || saved === 'packing' ? saved : 'one'
  })
  const [weight, setWeight] = useState<number | null>(null)
  const [palletEnabled, setPalletEnabled] = useState(() => readStorage(STORAGE_PALLET_ENABLED) === '1')
  const [pallet, setPallet] = useState<PackPalletDetail | null>(null)
  const [palletOptions, setPalletOptions] = useState<PackPalletSummary[]>([])
  const [lastSealed, setLastSealed] = useState<PackCartonSummary | null>(null)
  const [scanText, setScanText] = useState('')
  const [scanFocused, setScanFocused] = useState(false)
  const [scanStatus, setScanStatus] = useState<ScanStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [candidatePrompt, setCandidatePrompt] = useState<CandidatePrompt | null>(null)
  const [destinationTarget, setDestinationTarget] = useState<DestinationTarget | null>(null)
  const [orderOptions, setOrderOptions] = useState<PackOrderOption[]>([])

  const scanInputRef = useRef<InputRef>(null)
  const cartonRef = useRef<PackCartonDetail | null>(null)
  const palletRef = useRef<PackPalletDetail | null>(null)
  const palletEnabledRef = useRef(palletEnabled)
  const countModeRef = useRef(countMode)
  const nextHeaderRef = useRef<NextCartonHeader>({ destination: UNASSIGNED_DESTINATION, orderGuid: null })
  const queueRef = useRef<Promise<void>>(Promise.resolve())
  const pendingRef = useRef(0)
  const orderSearchSeqRef = useRef(0)
  const modalOpen = candidatePrompt !== null || destinationTarget !== null
  const modalOpenRef = useRef(modalOpen)
  modalOpenRef.current = modalOpen
  palletEnabledRef.current = palletEnabled
  countModeRef.current = countMode

  const applyCarton = useCallback((next: PackCartonDetail | null) => {
    cartonRef.current = next
    setCarton(next)
    writeStorage(STORAGE_CARTON, next?.cartonGuid ?? null)
  }, [])

  const applyPallet = useCallback((next: PackPalletDetail | null) => {
    palletRef.current = next
    setPallet(next)
    writeStorage(STORAGE_PALLET, next?.palletGuid ?? null)
  }, [])

  /** 扫码框常驻焦点：操作完成后把焦点还给扫码框；用户正在别的输入框里打字或有弹窗时不抢。 */
  const refocusScan = useCallback(() => {
    window.setTimeout(() => {
      if (modalOpenRef.current) {
        return
      }
      const element = document.activeElement
      const idle = !element || element === document.body || element.closest('button') !== null
      if (idle) {
        scanInputRef.current?.focus()
      }
    }, 0)
  }, [])

  /** 所有写操作排队串行：扫码枪连扫时保证顺序，前一枪的结果是后一枪的输入。 */
  const enqueue = useCallback(
    (task: () => Promise<void>) => {
      pendingRef.current += 1
      setBusy(true)
      queueRef.current = queueRef.current
        .then(task)
        .catch((error) => {
          showPackingError(error)
        })
        .finally(() => {
          pendingRef.current -= 1
          if (pendingRef.current === 0) {
            setBusy(false)
          }
          refocusScan()
        })
    },
    [refocusScan],
  )

  const feedback = useCallback((tone: ScanFeedbackTone, status: ScanStatus | null) => {
    playScanFeedback(tone)
    setScanStatus(status)
  }, [])

  // ---- 恢复上次的当前箱 / 当前托盘 ----
  useEffect(() => {
    let cancelled = false
    const cartonGuid = readStorage(STORAGE_CARTON)
    const palletGuid = readStorage(STORAGE_PALLET)
    if (cartonGuid) {
      getPackCarton(cartonGuid)
        .then((detail) => {
          if (cancelled || cartonRef.current) {
            return
          }
          // 已在别处封箱 / 作废的箱不再作为当前箱。
          applyCarton(detail.status === PACK_CARTON_STATUS.Packing ? detail : null)
          setWeight(detail.weightKg ?? null)
        })
        .catch(() => writeStorage(STORAGE_CARTON, null))
    }
    if (palletGuid) {
      getPackPallet(palletGuid)
        .then((detail) => {
          if (cancelled || palletRef.current) {
            return
          }
          applyPallet(detail.status === PACK_PALLET_STATUS.Building ? detail : null)
        })
        .catch(() => writeStorage(STORAGE_PALLET, null))
    }
    return () => {
      cancelled = true
    }
  }, [applyCarton, applyPallet])

  // 抽屉或其他标签里改过数据（重新开箱、移出托盘等）后，刷新打包台手上的箱和托盘。
  const seenVersionRef = useRef(dataVersion)
  useEffect(() => {
    if (seenVersionRef.current === dataVersion) {
      return
    }
    seenVersionRef.current = dataVersion
    enqueue(async () => {
      const currentCarton = cartonRef.current
      if (currentCarton) {
        const detail = await getPackCarton(currentCarton.cartonGuid)
        applyCarton(detail.status === PACK_CARTON_STATUS.Packing ? detail : null)
      }
      const currentPallet = palletRef.current
      if (currentPallet) {
        const detail = await getPackPallet(currentPallet.palletGuid)
        applyPallet(detail.status === PACK_PALLET_STATUS.Building ? detail : null)
      }
    })
  }, [applyCarton, applyPallet, dataVersion, enqueue])

  useEffect(() => {
    if (active && !modalOpen) {
      scanInputRef.current?.focus()
    }
  }, [active, modalOpen])

  // ---- 箱 ----

  const createCarton = useCallback(
    async (header: NextCartonHeader) => {
      const created = await createPackCarton({ ...destinationToRequest(header.destination), orderGuid: header.orderGuid })
      applyCarton(created)
      setWeight(null)
      setHighlightCode(null)
      return created
    },
    [applyCarton],
  )

  /** 把箱放上托盘；去向不一致时询问是否放行（只有仓库经理能放行，后端会再核一次）。 */
  const addToPallet = useCallback(
    async (palletGuid: string, cartonNo: string, allowMismatch = false): Promise<void> => {
      try {
        applyPallet(await addCartonToPackPallet(palletGuid, cartonNo, allowMismatch))
        notifyChanged()
      } catch (error) {
        const info = readPackingError(error)
        if (info.errorCode === PACK_ERROR_CODE.DestinationMismatch && !allowMismatch) {
          playScanFeedback('blocked')
          Modal.confirm({
            title: info.message,
            content: t('warehouseUi.packing.station.mismatchConfirm', { cartonNo }),
            okText: t('warehouseUi.packing.station.mismatchAllow'),
            cancelText: t('common.cancel'),
            onOk: () => enqueue(() => addToPallet(palletGuid, cartonNo, true)),
          })
          return
        }
        playScanFeedback('blocked')
        void message.warning(t('warehouseUi.packing.station.notOnPallet', { cartonNo, reason: info.message }))
      }
    },
    [applyPallet, enqueue, notifyChanged, t],
  )

  const scanProduct = useCallback(
    async (input: { code?: string; productCode?: string }) => {
      let current = cartonRef.current
      if (!current || current.status !== PACK_CARTON_STATUS.Packing) {
        // 没有当前箱时扫商品即自动开新箱，沿用上一箱的去向和订货单。
        current = await createCarton(nextHeaderRef.current)
      }
      const mode = countModeRef.current
      const known = input.productCode
        ? current.items.find((item) => item.productCode === input.productCode)
        : findCartonItemByCode(current.items, input.code ?? '')
      // 箱里已有的商品知道中包数 / 箱规，一次加够；新商品先加 1 件拿到规格，再补足差额。
      let step = known ? resolveScanStep(mode, known) : { quantity: 1, fellBack: false }
      try {
        let detail = await scanIntoPackCarton(current.cartonGuid, { ...input, quantity: step.quantity })
        const changed = detail.changedProductCode ?? null
        if (!known && mode !== 'one' && changed) {
          step = resolveScanStep(mode, detail.items.find((item) => item.productCode === changed))
          if (step.quantity > 1) {
            detail = await scanIntoPackCarton(current.cartonGuid, { productCode: changed, quantity: step.quantity - 1 })
          }
        }
        applyCarton(detail)
        setHighlightCode(changed)
        feedback(
          'success',
          step.fellBack
            ? { tone: 'warning', text: t('warehouseUi.packing.station.countFallback') }
            : { tone: 'success', text: t('warehouseUi.packing.station.scanAdded', { count: step.quantity }) },
        )
      } catch (error) {
        const info = readPackingError<PackCartonDetail>(error)
        const candidates = info.data?.candidates ?? []
        if (info.errorCode === PACK_ERROR_CODE.CodeAmbiguous && candidates.length > 0) {
          // 一码多品：弹出候选，选定后带 productCode 重新提交。
          feedback('multiple', { tone: 'warning', text: info.message })
          setCandidatePrompt({ code: input.code ?? '', candidates })
          return
        }
        feedback(info.errorCode === PACK_ERROR_CODE.CodeNotFound ? 'not-found' : 'error', {
          tone: 'error',
          text: input.code ? `${input.code}：${info.message}` : info.message,
        })
        if (info.status === 409 || (info.status === 404 && info.errorCode !== PACK_ERROR_CODE.CodeNotFound)) {
          // 箱可能已在别的设备上封箱 / 作废：重新读一次，不是装箱中就放下它。
          const latest = await getPackCarton(current.cartonGuid).catch(() => null)
          applyCarton(latest && latest.status === PACK_CARTON_STATUS.Packing ? latest : null)
        }
      }
    },
    [applyCarton, createCarton, feedback, t],
  )

  /** 扫到箱号 / 托盘号：装箱中的箱切为当前箱；已封的箱在开着托盘时直接上托盘；组托中的托盘切为当前托盘。 */
  const handleResolved = useCallback(
    async (resolved: PackResolveResult) => {
      if (resolved.kind === 'carton') {
        const detail = await getPackCarton(resolved.guid)
        if (detail.status === PACK_CARTON_STATUS.Packing) {
          applyCarton(detail)
          setWeight(detail.weightKg ?? null)
          setHighlightCode(null)
          feedback('success', { tone: 'success', text: t('warehouseUi.packing.station.switchedCarton', { no: detail.cartonNo }) })
          return
        }
        const currentPallet = palletRef.current
        if (detail.status === PACK_CARTON_STATUS.Sealed && palletEnabledRef.current && currentPallet) {
          await addToPallet(currentPallet.palletGuid, detail.cartonNo)
          return
        }
        openCarton(detail.cartonGuid)
        return
      }
      const detail = await getPackPallet(resolved.guid)
      if (detail.status === PACK_PALLET_STATUS.Building) {
        applyPallet(detail)
        setPalletEnabled(true)
        writeStorage(STORAGE_PALLET_ENABLED, '1')
        feedback('success', { tone: 'success', text: t('warehouseUi.packing.station.switchedPallet', { no: detail.palletNo }) })
        return
      }
      openPallet(detail.palletGuid)
    },
    [addToPallet, applyCarton, applyPallet, feedback, openCarton, openPallet, t],
  )

  const handleScan = useCallback(
    (raw: string) => {
      const code = raw.trim()
      if (!code) {
        return
      }
      // 提示音要在一次用户操作里解锁：扫码枪的按键也算，页面自动聚焦时没解锁成的在这里补上。
      if (!isScanFeedbackUnlocked()) {
        void unlockScanFeedback().catch(() => undefined)
      }
      enqueue(async () => {
        if (looksLikePackNo(code)) {
          const resolved = await resolvePackCode(code)
          if (resolved) {
            await handleResolved(resolved)
            return
          }
        }
        await scanProduct({ code })
      })
    },
    [enqueue, handleResolved, scanProduct],
  )

  const handleNewCarton = useCallback(() => {
    enqueue(async () => {
      const current = cartonRef.current
      if (current && current.items.length === 0) {
        void message.info(t('warehouseUi.packing.station.cartonStillEmpty'))
        return
      }
      // 新箱沿用当前箱的去向和订货单；原来那只箱保持「装箱中」，可在「箱」标签里继续或作废。
      const header: NextCartonHeader = current
        ? { destination: destinationToValue(current.destination), orderGuid: current.orderGuid ?? null }
        : nextHeaderRef.current
      const created = await createCarton(header)
      setScanStatus({ tone: 'success', text: t('warehouseUi.packing.station.cartonCreated', { no: created.cartonNo }) })
      notifyChanged()
    })
  }, [createCarton, enqueue, notifyChanged, t])

  const handleSealCarton = useCallback(() => {
    enqueue(async () => {
      const current = cartonRef.current
      if (!current) {
        void message.warning(t('warehouseUi.packing.station.noCarton'))
        return
      }
      if (current.items.length === 0) {
        void message.warning(t('warehouseUi.packing.station.cartonEmpty'))
        return
      }
      const weightKg = parseWeightInput(weight)
      if (weightKg === undefined) {
        void message.warning(t('warehouseUi.packing.station.weightInvalid'))
        return
      }
      const sealed = await sealPackCarton(current.cartonGuid, weightKg)
      printPackLabels([buildCartonLabelFields(sealed)])
      nextHeaderRef.current = { destination: destinationToValue(sealed.destination), orderGuid: sealed.orderGuid ?? null }
      setLastSealed(sealed)
      applyCarton(null)
      setWeight(null)
      setHighlightCode(null)
      setScanStatus({ tone: 'success', text: t('warehouseUi.packing.station.cartonSealed', { no: sealed.cartonNo }) })
      const currentPallet = palletRef.current
      if (palletEnabledRef.current && currentPallet) {
        await addToPallet(currentPallet.palletGuid, sealed.cartonNo)
      }
      notifyChanged()
    })
  }, [addToPallet, applyCarton, enqueue, notifyChanged, t, weight])

  const handleSealPallet = useCallback(() => {
    enqueue(async () => {
      const current = palletRef.current
      if (!palletEnabledRef.current || !current) {
        void message.warning(t('warehouseUi.packing.station.noPallet'))
        return
      }
      if (current.cartonCount === 0) {
        void message.warning(t('warehouseUi.packing.station.palletEmpty'))
        return
      }
      const sealed = await sealPackPallet(current.palletGuid)
      printPackLabels(buildPalletLabelFields(sealed))
      applyPallet(null)
      setScanStatus({ tone: 'success', text: t('warehouseUi.packing.station.palletSealed', { no: sealed.palletNo }) })
      notifyChanged()
    })
  }, [applyPallet, enqueue, notifyChanged, t])

  const handleSetQuantity = useCallback(
    (item: PackCartonItem, quantity: number) => {
      enqueue(async () => {
        const current = cartonRef.current
        if (!current) {
          return
        }
        const detail = await setPackCartonItemQuantity(current.cartonGuid, item.productCode, quantity)
        applyCarton(detail)
        setHighlightCode(quantity > 0 ? item.productCode : null)
      })
    },
    [applyCarton, enqueue],
  )

  /** 保存当前箱的箱头（去向 / 订货单）；重量和备注原样带回，避免被清空。 */
  const saveCartonHeader = useCallback(
    async (destination: PackDestinationValue, orderGuid: string | null) => {
      const current = cartonRef.current
      if (!current) {
        nextHeaderRef.current = { destination, orderGuid }
        return
      }
      const detail = await updatePackCartonHeader(current.cartonGuid, {
        ...destinationToRequest(destination),
        orderGuid,
        weightKg: current.weightKg ?? null,
        remark: current.remark ?? null,
      })
      applyCarton(detail)
      notifyChanged()
    },
    [applyCarton, notifyChanged],
  )

  const handleOrderChange = useCallback(
    (orderGuid: string | undefined) => {
      enqueue(async () => {
        const current = cartonRef.current
        if (!current) {
          return
        }
        let destination = destinationToValue(current.destination)
        const order = orderOptions.find((option) => option.orderGuid === orderGuid)
        // 还没指定去向时，关联订货单即默认发往该订货单的分店。
        if (destination.destinationType === PACK_DESTINATION_TYPE.Unassigned && order?.storeCode) {
          destination = { destinationType: PACK_DESTINATION_TYPE.Store, storeCode: order.storeCode }
        }
        await saveCartonHeader(destination, orderGuid ?? null)
      })
    },
    [enqueue, orderOptions, saveCartonHeader],
  )

  const searchOrders = useCallback((keyword?: string) => {
    const sequence = ++orderSearchSeqRef.current
    searchPackOrders(keyword)
      .then((options) => {
        if (sequence === orderSearchSeqRef.current) {
          setOrderOptions(options)
        }
      })
      .catch(showPackingError)
  }, [])

  // ---- 托盘 ----

  const togglePallet = (enabled: boolean) => {
    setPalletEnabled(enabled)
    writeStorage(STORAGE_PALLET_ENABLED, enabled ? '1' : '0')
    refocusScan()
  }

  const loadPalletOptions = useCallback(() => {
    listPackPallets({ filter: 'building' })
      .then((list) => setPalletOptions(list.items))
      .catch(showPackingError)
  }, [])

  const selectPallet = (palletGuid: string) => {
    enqueue(async () => {
      applyPallet(await getPackPallet(palletGuid))
    })
  }

  const submitDestination = async (value: PackDestinationValue) => {
    try {
      if (destinationTarget === 'newPallet') {
        applyPallet(await createPackPallet(destinationToRequest(value)))
        notifyChanged()
      } else {
        await saveCartonHeader(value, cartonRef.current?.orderGuid ?? null)
      }
      setDestinationTarget(null)
    } catch (error) {
      showPackingError(error)
      throw error
    }
  }

  // ---- 扫码枪与快捷键 ----

  useBarcodeScanner({
    enabled: active && !modalOpen,
    onScan: handleScan,
  })

  useEffect(() => {
    if (!active) {
      return undefined
    }
    const handleKeyDown = (event: KeyboardEvent) => {
      const hotkey = resolveStationHotkey(event)
      if (!hotkey) {
        return
      }
      // F10 等在浏览器里有默认行为（打开菜单），工位上一律拦下。
      event.preventDefault()
      if (modalOpenRef.current || event.repeat) {
        return
      }
      if (hotkey === 'newCarton') {
        handleNewCarton()
      } else if (hotkey === 'sealCarton') {
        handleSealCarton()
      } else {
        handleSealPallet()
      }
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [active, handleNewCarton, handleSealCarton, handleSealPallet])

  const submitScanInput = () => {
    const value = scanText
    setScanText('')
    handleScan(value)
  }

  const handleScanFocus = () => {
    setScanFocused(true)
    // 提示音需要一次用户操作后才能播放。
    if (!isScanFeedbackUnlocked()) {
      void unlockScanFeedback().catch(() => undefined)
    }
  }

  // ---- 渲染 ----

  const items = useMemo(() => carton?.items ?? [], [carton])
  const hasOrder = Boolean(carton?.orderGuid)
  const lastItem = highlightCode ? items.find((item) => item.productCode === highlightCode) : undefined
  const lastProgress = lastItem ? resolveOrderProgress(lastItem) : null

  const columns = useMemo<ColumnsType<PackCartonItem>>(
    () => [
      {
        title: '#',
        key: 'index',
        width: 48,
        render: (_value, _record, index) => items.length - index,
      },
      {
        title: t('warehouseUi.packing.columns.product'),
        dataIndex: 'productName',
        ellipsis: true,
        render: (_value, record) => (
          <span>
            {record.productName || record.productCode}
            {hasOrder && !resolveOrderProgress(record).onOrder ? (
              <Tag color="orange" className="wh-packing-inline-tag">
                {t('warehouseUi.packing.station.offOrder')}
              </Tag>
            ) : null}
          </span>
        ),
      },
      {
        title: t('warehouseUi.packing.columns.itemNumber'),
        dataIndex: 'itemNumber',
        width: 130,
        render: (value: string | null) => <span className="wh-packing-mono">{value || '—'}</span>,
      },
      {
        title: t('warehouseUi.packing.columns.orderQuantity'),
        dataIndex: 'orderQuantity',
        width: 80,
        align: 'right',
        render: (value: number | null | undefined) =>
          value === null || value === undefined ? <Typography.Text type="secondary">—</Typography.Text> : value,
      },
      {
        title: t('warehouseUi.packing.columns.packedElsewhere'),
        dataIndex: 'packedInOtherCartons',
        width: 110,
        align: 'right',
      },
      {
        title: t('warehouseUi.packing.columns.cartonQuantity'),
        key: 'quantity',
        width: 150,
        render: (_value, record) => (
          <QuantityStepper
            value={record.quantity}
            warning={resolveOrderProgress(record).over}
            onCommit={(quantity) => handleSetQuantity(record, quantity)}
          />
        ),
      },
      {
        key: 'remove',
        width: 48,
        render: (_value, record) => (
          <Button
            type="text"
            size="small"
            icon={<DeleteOutlined />}
            aria-label={t('common.delete')}
            onClick={() => handleSetQuantity(record, 0)}
          />
        ),
      },
    ],
    [handleSetQuantity, hasOrder, items.length, t],
  )

  // 没有当前箱时显示下一箱将沿用的订货单（上一箱的）。
  const headerOrder = carton ?? lastSealed
  // 当前订货单不一定在搜索结果里：补一个选项保证下拉能显示单号。
  const orderSelectOptions = useMemo(() => {
    const options = orderOptions.map((option) => ({
      value: option.orderGuid,
      label: [option.orderNo || option.orderGuid, option.storeName].filter(Boolean).join(' · '),
    }))
    const orderGuid = headerOrder?.orderGuid
    if (orderGuid && !options.some((option) => option.value === orderGuid)) {
      options.unshift({ value: orderGuid, label: headerOrder?.orderNo || orderGuid })
    }
    return options
  }, [headerOrder, orderOptions])

  const palletSelectOptions = palletOptions.map((option) => ({
    value: option.palletGuid,
    label: `${option.palletNo} · ${t('warehouseUi.packing.summary.cartons', { count: option.cartonCount })}`,
  }))

  // 改当前箱去向、新建托盘都以当前箱的去向为初值（没有当前箱时取上一箱的）。
  const destinationInitial: PackDestinationValue = carton
    ? destinationToValue(carton.destination)
    : nextHeaderRef.current.destination

  return (
    <div className="wh-packing-station">
      {/* 左：扫码与箱内明细 */}
      <div className="wh-packing-station-main">
        <div className="wh-packing-scanrow">
          <Input
            ref={scanInputRef}
            className="wh-packing-scanbox"
            size="large"
            allowClear
            autoComplete="off"
            prefix={<BarcodeOutlined />}
            placeholder={t('warehouseUi.packing.station.scanPlaceholder')}
            value={scanText}
            onChange={(event) => setScanText(event.target.value)}
            onPressEnter={submitScanInput}
            onFocus={handleScanFocus}
            onBlur={() => setScanFocused(false)}
            suffix={
              scanFocused ? (
                <Tag color="green" className="wh-packing-scan-ready">
                  {t('warehouseUi.packing.station.scannerReady')}
                </Tag>
              ) : (
                <Tag className="wh-packing-scan-ready">{t('warehouseUi.packing.station.scannerPaused')}</Tag>
              )
            }
          />
          <Select<PackCountMode>
            className="wh-packing-countmode"
            size="large"
            value={countMode}
            onChange={(mode) => {
              setCountMode(mode)
              writeStorage(STORAGE_COUNT_MODE, mode)
              refocusScan()
            }}
            options={[
              { value: 'one', label: t('warehouseUi.packing.station.countOne') },
              { value: 'middle', label: t('warehouseUi.packing.station.countMiddle') },
              { value: 'packing', label: t('warehouseUi.packing.station.countPacking') },
            ]}
          />
        </div>

        {scanStatus ? (
          <div className={`wh-packing-scanstatus wh-packing-scanstatus-${scanStatus.tone}`} role="status">
            {scanStatus.text}
          </div>
        ) : null}

        {lastItem ? (
          <div className="wh-packing-last">
            <div className="wh-packing-thumb">
              {lastItem.productImage ? <img src={lastItem.productImage} alt="" /> : <PictureOutlined />}
            </div>
            <div className="wh-packing-last-body">
              <div className="wh-packing-last-name">
                {lastItem.productName || lastItem.productCode}
                <Tag color="green" className="wh-packing-inline-tag">
                  {t('warehouseUi.packing.station.justScanned')}
                </Tag>
              </div>
              <div className="wh-packing-mono wh-packing-muted">
                {[lastItem.itemNumber, lastItem.barcode].filter(Boolean).join(' · ')}
                {lastItem.middlePackageQuantity
                  ? `　${t('warehouseUi.packing.station.middlePack', { count: lastItem.middlePackageQuantity })}`
                  : ''}
                {lastItem.packingQuantity
                  ? ` · ${t('warehouseUi.packing.station.packingSize', { count: lastItem.packingQuantity })}`
                  : ''}
              </div>
            </div>
            <div className="wh-packing-last-qty">
              {lastProgress?.onOrder ? (
                <div className={lastProgress.over ? 'wh-packing-over' : 'wh-packing-muted'}>
                  {lastProgress.over
                    ? t('warehouseUi.packing.station.orderOver', {
                        order: lastItem.orderQuantity,
                        over: -lastProgress.remaining,
                      })
                    : t('warehouseUi.packing.station.orderRemaining', {
                        order: lastItem.orderQuantity,
                        remaining: lastProgress.remaining,
                      })}
                </div>
              ) : null}
              <div className="wh-packing-last-count">
                {lastItem.quantity}
                <small>{t('warehouseUi.packing.unit.piece')}</small>
              </div>
            </div>
          </div>
        ) : null}

        <div className="wh-packing-panel wh-packing-station-table">
          <MeasuredTable<PackCartonItem>
            metricId="warehouse.packing.station-items"
            size="small"
            rowKey="productCode"
            columns={columns}
            dataSource={items}
            pagination={false}
            scroll={{ y: 'calc(100vh - 470px)' }}
            rowClassName={(record) => (record.productCode === highlightCode ? 'wh-packing-row-highlight' : '')}
            locale={{
              emptyText: carton
                ? t('warehouseUi.packing.station.emptyCarton')
                : t('warehouseUi.packing.station.emptyNoCarton'),
            }}
          />
        </div>
      </div>

      {/* 右：当前箱、当前托盘 */}
      <div className="wh-packing-station-side">
        <div className="wh-packing-panel wh-packing-side-card">
          <div className="wh-packing-side-head">
            <span className="wh-packing-side-title">
              {t('warehouseUi.packing.station.currentCarton')}
              {carton ? <span className="wh-packing-mono">{carton.cartonNo}</span> : null}
            </span>
            {carton ? (
              <CartonStatusTag status={carton.status} t={t} />
            ) : (
              <Tag>{t('warehouseUi.packing.station.noCartonTag')}</Tag>
            )}
          </div>
          <div className="wh-packing-kv">
            <span>{t('warehouseUi.packing.columns.destination')}</span>
            <span className="wh-packing-kv-value">
              {/* 没有当前箱时显示下一箱将沿用的去向（上一箱的）。 */}
              <DestinationText destination={carton?.destination ?? lastSealed?.destination} t={t} />
              <Button type="link" size="small" disabled={!carton} onClick={() => setDestinationTarget('carton')}>
                {t('warehouseUi.packing.action.change')}
              </Button>
            </span>
          </div>
          <div className="wh-packing-kv">
            <span>{t('warehouseUi.packing.columns.order')}</span>
            <Select
              className="wh-packing-order-select"
              showSearch
              allowClear
              filterOption={false}
              disabled={!carton}
              placeholder={t('warehouseUi.packing.station.orderPlaceholder')}
              value={headerOrder?.orderGuid ?? undefined}
              options={orderSelectOptions}
              onDropdownVisibleChange={(open) => {
                if (open) {
                  searchOrders()
                }
              }}
              onSearch={searchOrders}
              onChange={handleOrderChange}
            />
          </div>
          <div className="wh-packing-kv">
            <span>{t('warehouseUi.packing.columns.total')}</span>
            <b className="wh-packing-total">
              {t('warehouseUi.packing.summary.skuPieces', {
                sku: carton?.skuCount ?? 0,
                pieces: carton?.pieceCount ?? 0,
              })}
            </b>
          </div>
          <div className="wh-packing-kv">
            <span>{t('warehouseUi.packing.station.weightOptional')}</span>
            <InputNumber
              className="wh-packing-weight"
              min={0}
              max={9999.99}
              precision={2}
              controls={false}
              disabled={!carton}
              value={weight}
              onChange={(value) => setWeight(typeof value === 'number' ? value : null)}
              onPressEnter={(event) => (event.target as HTMLInputElement).blur()}
              onBlur={refocusScan}
              addonAfter="kg"
            />
          </div>
          <div className="wh-packing-side-actions">
            <Button size="large" disabled={busy} onClick={handleNewCarton}>
              {t('warehouseUi.packing.station.newCarton')}
              <kbd>F2</kbd>
            </Button>
            <Button
              type="primary"
              size="large"
              icon={<PrinterOutlined />}
              disabled={busy || !carton || items.length === 0}
              onClick={handleSealCarton}
            >
              {t('warehouseUi.packing.station.sealAndPrint')}
              <kbd>F9</kbd>
            </Button>
          </div>
          {lastSealed ? (
            <div className="wh-packing-last-sealed">
              <span>{t('warehouseUi.packing.station.lastSealed', { no: lastSealed.cartonNo })}</span>
              <Button type="link" size="small" onClick={() => printPackLabels([buildCartonLabelFields(lastSealed)])}>
                {t('warehouseUi.packing.action.reprint')}
              </Button>
              <Button type="link" size="small" onClick={() => openCarton(lastSealed.cartonGuid)}>
                {t('common.detail')}
              </Button>
            </div>
          ) : null}
        </div>

        <div className="wh-packing-panel wh-packing-side-card wh-packing-side-pallet">
          <div className="wh-packing-side-head">
            <span className="wh-packing-side-title">
              {t('warehouseUi.packing.station.currentPallet')}
              {palletEnabled && pallet ? <span className="wh-packing-mono">{pallet.palletNo}</span> : null}
            </span>
            <Switch
              size="small"
              checked={palletEnabled}
              onChange={togglePallet}
              aria-label={t('warehouseUi.packing.station.currentPallet')}
            />
          </div>
          {!palletEnabled ? (
            <Typography.Paragraph type="secondary" className="wh-packing-side-hint">
              {t('warehouseUi.packing.station.palletOff')}
            </Typography.Paragraph>
          ) : (
            <>
              <div className="wh-packing-pallet-pick">
                <Select
                  className="wh-packing-pallet-select"
                  showSearch
                  optionFilterProp="label"
                  placeholder={t('warehouseUi.packing.station.choosePallet')}
                  value={pallet?.palletGuid}
                  options={
                    pallet && !palletSelectOptions.some((option) => option.value === pallet.palletGuid)
                      ? [{ value: pallet.palletGuid, label: pallet.palletNo }, ...palletSelectOptions]
                      : palletSelectOptions
                  }
                  onDropdownVisibleChange={(open) => {
                    if (open) {
                      loadPalletOptions()
                    }
                  }}
                  onChange={selectPallet}
                />
                <Button onClick={() => setDestinationTarget('newPallet')}>{t('warehouseUi.packing.action.newPallet')}</Button>
              </div>
              {pallet ? (
                <>
                  <div className="wh-packing-side-hint">
                    {t('warehouseUi.packing.columns.destination')} <DestinationText destination={pallet.destination} t={t} />
                    {' · '}
                    {t('warehouseUi.packing.station.palletAuto')}
                    <Button type="link" size="small" onClick={() => openPallet(pallet.palletGuid)}>
                      {t('common.detail')}
                    </Button>
                  </div>
                  <div className="wh-packing-mini-list">
                    {pallet.cartons.map((item) => (
                      <div className="wh-packing-mini" key={item.cartonGuid}>
                        <InboxOutlined />
                        <span className="wh-packing-mono">{item.cartonNo}</span>
                        <span className="wh-packing-mini-spacer" />
                        <span className="wh-packing-muted">
                          {t('warehouseUi.packing.summary.skuPieces', { sku: item.skuCount, pieces: item.pieceCount })}
                        </span>
                      </div>
                    ))}
                    {carton ? (
                      <div className="wh-packing-mini wh-packing-muted">
                        <InboxOutlined />
                        <span className="wh-packing-mono">{carton.cartonNo}</span>
                        <span className="wh-packing-mini-spacer" />
                        {t('warehouseUi.packing.station.joinsAfterSeal')}
                      </div>
                    ) : null}
                  </div>
                  <div className="wh-packing-kv wh-packing-kv-top">
                    <span>{t('warehouseUi.packing.station.palletTotal')}</span>
                    <b>
                      {t('warehouseUi.packing.summary.palletTotal', {
                        cartons: pallet.cartonCount,
                        sku: pallet.skuCount,
                        pieces: pallet.pieceCount,
                      })}
                    </b>
                  </div>
                </>
              ) : (
                <Typography.Paragraph type="secondary" className="wh-packing-side-hint">
                  {t('warehouseUi.packing.station.palletNone')}
                </Typography.Paragraph>
              )}
              <Button
                size="large"
                block
                icon={<PrinterOutlined />}
                disabled={busy || !pallet || pallet.cartonCount === 0}
                onClick={handleSealPallet}
              >
                {t('warehouseUi.packing.station.sealPalletAndPrint')}
                <kbd>F10</kbd>
              </Button>
            </>
          )}
        </div>
      </div>

      <Modal
        open={candidatePrompt !== null}
        title={t('warehouseUi.packing.station.candidateTitle')}
        footer={null}
        width={560}
        onCancel={() => {
          setCandidatePrompt(null)
          refocusScan()
        }}
        destroyOnClose
      >
        <Typography.Paragraph type="secondary">
          {t('warehouseUi.packing.station.candidateHint', { code: candidatePrompt?.code ?? '' })}
        </Typography.Paragraph>
        <div className="wh-packing-candidates">
          {(candidatePrompt?.candidates ?? []).map((candidate) => (
            <button
              type="button"
              key={candidate.productCode}
              className="wh-packing-candidate"
              onClick={() => {
                setCandidatePrompt(null)
                enqueue(() => scanProduct({ productCode: candidate.productCode }))
              }}
            >
              <span className="wh-packing-thumb">
                {candidate.productImage ? <img src={candidate.productImage} alt="" /> : <PictureOutlined />}
              </span>
              <span className="wh-packing-candidate-body">
                <b>{candidate.productName || candidate.productCode}</b>
                <span className="wh-packing-mono wh-packing-muted">
                  {[candidate.itemNumber, candidate.barcode].filter(Boolean).join(' · ')}
                </span>
              </span>
            </button>
          ))}
        </div>
      </Modal>

      <DestinationModal
        open={destinationTarget !== null}
        title={
          destinationTarget === 'newPallet'
            ? t('warehouseUi.packing.action.newPallet')
            : t('warehouseUi.packing.action.changeDestination')
        }
        initialValue={destinationInitial}
        onCancel={() => setDestinationTarget(null)}
        onSubmit={submitDestination}
      />
    </div>
  )
}
