import {
  CheckCircleFilled,
  CheckOutlined,
  CreditCardOutlined,
  DeleteOutlined,
  EditOutlined,
  ExclamationCircleOutlined,
  ExclamationOutlined,
  ExperimentOutlined,
  PlusOutlined,
  ReloadOutlined,
  WalletOutlined,
  WarningOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Col,
  Form,
  Input,
  InputNumber,
  Modal,
  Popconfirm,
  Row,
  Segmented,
  Select,
  Space,
  Tag,
  Tooltip,
  Typography,
  message,
  type TableProps,
} from 'antd'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  activateLinklyConfiguration,
  createLinklyTerminal,
  deleteLinklyDeviceSelection,
  getLinklyTerminals,
  getPaymentTerminalSettings,
  saveSquareToken,
  updateLinklyDeviceSelection,
  updateLinklyTerminal,
} from '../../../services/paymentTerminalSettingsService'
import { useAuthStore } from '../../../store/auth'
import type {
  LinklyPairingState,
  LinklyTerminalAdminDto,
  LinklyTerminalDeviceAdminDto,
  LinklyTerminalManagementDto,
  PaymentTerminalEnvironment,
  PaymentTerminalEnvironmentStatusDto,
  PaymentTerminalSettingsDto,
} from '../../../types/paymentTerminalSettings'
import { formatUserLocalDateTime } from '../Users/time'
import {
  buildCreateLinklyTerminalPayload,
  buildLinklyActivationChecklist,
  buildSquareTokenPayload,
  buildUpdateLinklyTerminalPayload,
  createLinklyTerminalFormValues,
  describeElapsedSince,
  getEnvironmentStatus,
  getLinklyTerminalAssignmentOwner,
  getTerminalDeviceCodes,
  getTerminalHealthTone,
  isConfiguredStatus,
  suggestNextLaneNo,
  resolvePaymentTerminalSettingsErrorMessage,
  type LinklyActivationChecklist,
  type LinklyTerminalFormValues,
} from './pageLogic'
import paymentTerminalSettingsMessagesEn from './paymentTerminalSettingsMessages.en.json'
import paymentTerminalSettingsMessagesZh from './paymentTerminalSettingsMessages.zh.json'
import './paymentTerminalSettings.css'

// 重设计新增的文案随页面懒加载注册，不进首屏 i18n 包。
registerPageMessages({ zh: paymentTerminalSettingsMessagesZh, en: paymentTerminalSettingsMessagesEn })

const ENVIRONMENTS: PaymentTerminalEnvironment[] = ['Production', 'Sandbox']

function StatusTag({ status }: { status?: PaymentTerminalEnvironmentStatusDto }) {
  const { t } = useTranslation()
  return isConfiguredStatus(status)
    ? <Tag color="green">{t('paymentTerminalSettings.configured')}</Tag>
    : <Tag>{t('paymentTerminalSettings.notConfigured')}</Tag>
}

function PairingStateTag({ state }: { state: LinklyPairingState }) {
  const { t } = useTranslation()
  const colors: Record<LinklyPairingState, string> = {
    Ready: 'green',
    Unpaired: 'default',
    Unknown: 'orange',
    NeedsRepair: 'red',
  }
  return <Tag color={colors[state]}>{t(`paymentTerminalSettings.pairingStates.${state}`)}</Tag>
}

function ElapsedText({ value, now }: { value?: string | null; now: Date }) {
  const { t } = useTranslation()
  const description = describeElapsedSince(value, now)
  switch (description.kind) {
    case 'never':
      return null
    case 'justNow':
      return <>{t('paymentTerminalSettings.elapsedJustNow')}</>
    case 'minutes':
      return <>{t('paymentTerminalSettings.elapsedMinutes', { count: description.count })}</>
    case 'hours':
      return <>{t('paymentTerminalSettings.elapsedHours', { count: description.count })}</>
    case 'days':
      return <>{t('paymentTerminalSettings.elapsedDays', { count: description.count })}</>
  }
}

function TerminalHealth({ terminal, now }: { terminal: LinklyTerminalAdminDto; now: Date }) {
  const { t } = useTranslation()
  const tone = getTerminalHealthTone(terminal.lastHealthStatus)
  const label = {
    healthy: t('paymentTerminalSettings.healthHealthy'),
    unhealthy: t('paymentTerminalSettings.healthUnhealthy'),
    other: terminal.lastHealthStatus,
    unchecked: t('paymentTerminalSettings.healthUnchecked'),
  }[tone]
  const content = (
    <span className={`pts-health pts-health-${tone}`}>
      <span className="pts-health-dot" aria-hidden="true" />
      <span>{label}</span>
      {terminal.lastHealthAtUtc ? (
        <span className="pts-health-time">
          · <ElapsedText value={terminal.lastHealthAtUtc} now={now} />
        </span>
      ) : null}
    </span>
  )

  // 列表只显示「多久前」，精确的本地检测时间放在悬停提示里。
  return terminal.lastHealthAtUtc ? (
    <Tooltip title={t('paymentTerminalSettings.healthCheckedAt', { time: formatUserLocalDateTime(terminal.lastHealthAtUtc) })}>
      {content}
    </Tooltip>
  ) : content
}

type StepState = 'done' | 'blocked' | 'todo'

function ActivationStep({ index, state, title, description }: {
  index: number
  state: StepState
  title: string
  description: string
}) {
  return (
    <li className={`pts-step pts-step-${state}`}>
      <span className="pts-step-icon" aria-hidden="true">
        {state === 'done' ? <CheckOutlined /> : state === 'blocked' ? <ExclamationOutlined /> : index}
      </span>
      <span>
        <span className="pts-step-title">{title}</span>
        <span className="pts-step-desc">{description}</span>
      </span>
    </li>
  )
}

function LinklyActivationPanel({ checklist, canManage, activating, onActivate }: {
  checklist: LinklyActivationChecklist
  canManage: boolean
  activating: boolean
  onActivate: () => void
}) {
  const { t } = useTranslation()
  const blockingIssueCount = checklist.conflicts.length + checklist.notReadySelections.length
  const missingCredentialCount = checklist.missingCredentialTerminals.length
  // 有终端缺少受保护密码时多出一步，后面的序号顺延。
  const stepOffset = missingCredentialCount > 0 ? 1 : 0
  const canActivate = canManage && checklist.canActivate

  return (
    <div className="pts-activation">
      <span className="pts-activation-title">{t('paymentTerminalSettings.checklistTitle')}</span>
      <ol className="pts-steps">
        <ActivationStep
          index={1}
          state={checklist.terminalCount > 0 ? 'done' : 'todo'}
          title={t('paymentTerminalSettings.stepAddTerminals')}
          description={checklist.terminalCount > 0
            ? t('paymentTerminalSettings.stepAddTerminalsDone', { count: checklist.terminalCount })
            : t('paymentTerminalSettings.stepAddTerminalsTodo')}
        />
        {missingCredentialCount > 0 ? (
          <ActivationStep
            index={2}
            state="blocked"
            title={t('paymentTerminalSettings.stepCredentials')}
            description={t('paymentTerminalSettings.stepCredentialsBlocked', {
              names: checklist.missingCredentialTerminals.map((terminal) => terminal.displayName).join(', '),
            })}
          />
        ) : null}
        <ActivationStep
          index={2 + stepOffset}
          state={checklist.readyTerminalCount > 0 ? 'done' : 'todo'}
          title={t('paymentTerminalSettings.stepPair')}
          description={checklist.readyTerminalCount > 0
            ? t('paymentTerminalSettings.stepPairDone', {
              ready: checklist.readyTerminalCount,
              total: checklist.terminalCount,
            })
            : t('paymentTerminalSettings.stepPairTodo')}
        />
        <ActivationStep
          index={3 + stepOffset}
          // 还没有终端时「无冲突」只是空成立，显示为待办而不是打勾。
          state={blockingIssueCount > 0 ? 'blocked' : checklist.terminalCount > 0 ? 'done' : 'todo'}
          title={t('paymentTerminalSettings.stepAssign')}
          description={blockingIssueCount > 0
            ? t('paymentTerminalSettings.stepAssignBlocked', { count: blockingIssueCount })
            : checklist.assignedDeviceCount > 0
              ? t('paymentTerminalSettings.stepAssignDone', { count: checklist.assignedDeviceCount })
              : t('paymentTerminalSettings.stepAssignNone')}
        />
      </ol>
      <div className="pts-activation-action">
        <Popconfirm
          title={t('paymentTerminalSettings.activateConfirmTitle')}
          description={t('paymentTerminalSettings.activateConfirmDescription')}
          onConfirm={onActivate}
          okText={t('paymentTerminalSettings.activate')}
          cancelText={t('common.cancel')}
          disabled={!canActivate}
        >
          <Button type="primary" loading={activating} disabled={!canActivate}>
            {t('paymentTerminalSettings.activate')}
          </Button>
        </Popconfirm>
        <span className="pts-activation-hint">
          {checklist.canActivate
            ? t('paymentTerminalSettings.activateReadyHint')
            : t('paymentTerminalSettings.activateBlockedHint')}
        </span>
      </div>
    </div>
  )
}

export default function PaymentTerminalSettingsPage() {
  const { t } = useTranslation()
  const access = useAuthStore((state) => state.access)
  const canManageSettings = access.hasPermission('System.ManageSettings')
  const [squareForm] = Form.useForm<{ accessToken: string }>()
  const [terminalForm] = Form.useForm<LinklyTerminalFormValues>()
  const [environment, setEnvironment] = useState<PaymentTerminalEnvironment>('Production')
  const [settings, setSettings] = useState<PaymentTerminalSettingsDto | null>(null)
  const [linklyManagement, setLinklyManagement] = useState<LinklyTerminalManagementDto | null>(null)
  const [selectedStoreCode, setSelectedStoreCode] = useState<string>()
  const [editingTerminal, setEditingTerminal] = useState<LinklyTerminalAdminDto | null>(null)
  const [terminalModalOpen, setTerminalModalOpen] = useState(false)
  const [squareModalOpen, setSquareModalOpen] = useState(false)
  const [settingsLoading, setSettingsLoading] = useState(false)
  const [linklyLoading, setLinklyLoading] = useState(false)
  const [savingSquare, setSavingSquare] = useState(false)
  const [savingTerminal, setSavingTerminal] = useState(false)
  const [activating, setActivating] = useState(false)
  const [savingSelectionDevice, setSavingSelectionDevice] = useState<string>()
  const settingsRequestSequence = useRef(0)
  const linklyRequestSequence = useRef(0)

  const squareStatus = useMemo(
    () => getEnvironmentStatus(settings?.square ?? [], environment),
    [settings, environment],
  )
  const checklist = useMemo(() => buildLinklyActivationChecklist(linklyManagement), [linklyManagement])

  const loadSettings = async (storeCode = selectedStoreCode) => {
    const requestSequence = ++settingsRequestSequence.current
    setSettingsLoading(true)
    try {
      const result = await getPaymentTerminalSettings(storeCode)
      if (requestSequence === settingsRequestSequence.current) {
        setSettings(result)
        setSelectedStoreCode(result.selectedStoreCode ?? result.stores[0]?.storeCode)
      }
    } catch (error) {
      if (requestSequence === settingsRequestSequence.current) {
        console.error(error)
        message.error(resolvePaymentTerminalSettingsErrorMessage(error, t('paymentTerminalSettings.loadFailed')))
      }
    } finally {
      if (requestSequence === settingsRequestSequence.current) {
        setSettingsLoading(false)
      }
    }
  }

  const loadLinklyManagement = async (storeCode: string, targetEnvironment: PaymentTerminalEnvironment) => {
    const requestSequence = ++linklyRequestSequence.current
    setLinklyLoading(true)
    try {
      const result = await getLinklyTerminals(storeCode, targetEnvironment)
      if (requestSequence === linklyRequestSequence.current) {
        setLinklyManagement(result)
      }
    } catch (error) {
      if (requestSequence === linklyRequestSequence.current) {
        console.error(error)
        setLinklyManagement(null)
        message.error(resolvePaymentTerminalSettingsErrorMessage(error, t('paymentTerminalSettings.loadFailed')))
      }
    } finally {
      if (requestSequence === linklyRequestSequence.current) {
        setLinklyLoading(false)
      }
    }
  }

  useEffect(() => {
    void loadSettings()
  }, [])

  useEffect(() => {
    if (!selectedStoreCode) {
      linklyRequestSequence.current += 1
      setLinklyManagement(null)
      setLinklyLoading(false)
      return
    }
    void loadLinklyManagement(selectedStoreCode, environment)
  }, [selectedStoreCode, environment])

  const handleStoreChange = (storeCode: string) => {
    linklyRequestSequence.current += 1
    setSelectedStoreCode(storeCode)
    void loadSettings(storeCode)
  }

  const handleEnvironmentChange = (value: string | number) => {
    linklyRequestSequence.current += 1
    setEnvironment(value as PaymentTerminalEnvironment)
  }

  const handleRefresh = () => {
    void loadSettings(selectedStoreCode)
    if (selectedStoreCode) {
      void loadLinklyManagement(selectedStoreCode, environment)
    }
  }

  // 更换与清除共用同一个保存接口：清除不需要输入，更换只校验 token 输入框。
  const submitSquareToken = async (clearToken: boolean) => {
    let accessToken = ''
    if (!clearToken) {
      try {
        accessToken = (await squareForm.validateFields()).accessToken
      } catch {
        return
      }
    }

    const requestSequence = ++settingsRequestSequence.current
    setSettingsLoading(false)
    setSavingSquare(true)
    try {
      const result = await saveSquareToken(
        buildSquareTokenPayload(environment, { accessToken, clearToken }),
        selectedStoreCode,
      )
      if (requestSequence === settingsRequestSequence.current) {
        setSettings(result)
        setSelectedStoreCode(result.selectedStoreCode ?? selectedStoreCode)
      }
      setSquareModalOpen(false)
      message.success(clearToken ? t('paymentTerminalSettings.squareCleared') : t('paymentTerminalSettings.saveSuccess'))
    } catch (error) {
      console.error(error)
      message.error(resolvePaymentTerminalSettingsErrorMessage(error, t('paymentTerminalSettings.saveFailed')))
    } finally {
      setSavingSquare(false)
    }
  }

  const createNewTerminalFormValues = (): LinklyTerminalFormValues => ({
    ...createLinklyTerminalFormValues(),
    laneNo: suggestNextLaneNo(linklyManagement?.terminals ?? []),
  })

  const openCreateTerminal = () => {
    setEditingTerminal(null)
    terminalForm.setFieldsValue(createNewTerminalFormValues())
    setTerminalModalOpen(true)
  }

  const openEditTerminal = (terminal: LinklyTerminalAdminDto) => {
    setEditingTerminal(terminal)
    terminalForm.setFieldsValue(createLinklyTerminalFormValues(terminal))
    setTerminalModalOpen(true)
  }

  const handleSaveTerminal = async () => {
    if (!selectedStoreCode) {
      message.warning(t('paymentTerminalSettings.selectStoreRequired'))
      return
    }

    const values = await terminalForm.validateFields()
    const requestSequence = ++linklyRequestSequence.current
    setLinklyLoading(false)
    setSavingTerminal(true)
    try {
      const result = editingTerminal
        ? await updateLinklyTerminal(
          editingTerminal.terminalId,
          buildUpdateLinklyTerminalPayload(selectedStoreCode, environment, values),
        )
        : await createLinklyTerminal(
          buildCreateLinklyTerminalPayload(selectedStoreCode, environment, values),
        )
      if (requestSequence === linklyRequestSequence.current) {
        setLinklyManagement(result)
        setLinklyLoading(false)
      }
      setTerminalModalOpen(false)
      setEditingTerminal(null)
      terminalForm.resetFields()
      message.success(t('paymentTerminalSettings.saveSuccess'))
    } catch (error) {
      console.error(error)
      message.error(resolvePaymentTerminalSettingsErrorMessage(error, t('paymentTerminalSettings.saveFailed')))
    } finally {
      setSavingTerminal(false)
    }
  }

  const handleDeviceSelection = async (device: LinklyTerminalDeviceAdminDto, terminalId: string) => {
    if (!selectedStoreCode) return

    const requestSequence = ++linklyRequestSequence.current
    setLinklyLoading(false)
    setSavingSelectionDevice(device.deviceCode)
    try {
      const result = await updateLinklyDeviceSelection(device.deviceCode, {
        storeCode: selectedStoreCode,
        environment,
        terminalId,
        ...(device.revision > 0 ? { expectedRevision: device.revision } : {}),
      })
      if (requestSequence === linklyRequestSequence.current) {
        setLinklyManagement(result)
        setLinklyLoading(false)
      }
      message.success(t('paymentTerminalSettings.selectionSaved'))
    } catch (error) {
      console.error(error)
      message.error(resolvePaymentTerminalSettingsErrorMessage(error, t('paymentTerminalSettings.saveFailed')))
      if (requestSequence === linklyRequestSequence.current) {
        await loadLinklyManagement(selectedStoreCode, environment)
      }
    } finally {
      setSavingSelectionDevice(undefined)
    }
  }

  const handleDeleteDeviceSelection = async (device: LinklyTerminalDeviceAdminDto) => {
    if (!selectedStoreCode || !device.terminalId || device.revision <= 0) return

    const requestSequence = ++linklyRequestSequence.current
    setLinklyLoading(false)
    setSavingSelectionDevice(device.deviceCode)
    try {
      const result = await deleteLinklyDeviceSelection(device.deviceCode, {
        storeCode: selectedStoreCode,
        environment,
        expectedRevision: device.revision,
      })
      if (requestSequence === linklyRequestSequence.current) {
        setLinklyManagement(result)
        setLinklyLoading(false)
      }
      message.success(t('paymentTerminalSettings.selectionReleased'))
    } catch (error) {
      console.error(error)
      message.error(resolvePaymentTerminalSettingsErrorMessage(error, t('paymentTerminalSettings.saveFailed')))
      if (requestSequence === linklyRequestSequence.current) {
        await loadLinklyManagement(selectedStoreCode, environment)
      }
    } finally {
      setSavingSelectionDevice(undefined)
    }
  }

  const handleActivate = async () => {
    if (!selectedStoreCode) return

    const requestSequence = ++linklyRequestSequence.current
    setLinklyLoading(false)
    setActivating(true)
    try {
      const result = await activateLinklyConfiguration({ storeCode: selectedStoreCode, environment })
      if (requestSequence === linklyRequestSequence.current) {
        setLinklyManagement(result)
        setLinklyLoading(false)
      }
      message.success(t('paymentTerminalSettings.activationSuccess'))
    } catch (error) {
      console.error(error)
      message.error(resolvePaymentTerminalSettingsErrorMessage(error, t('paymentTerminalSettings.activationFailed')))
    } finally {
      setActivating(false)
    }
  }

  const now = new Date()
  const environmentLabel = t(`paymentTerminalSettings.environments.${environment}`)
  const terminals = linklyManagement?.terminals ?? []
  const devices = linklyManagement?.devices ?? []
  const mode = linklyManagement?.mode ?? 'Legacy'
  const squareConfigured = isConfiguredStatus(squareStatus)

  const terminalLabel = (terminalId: string) => {
    const terminal = terminals.find((item) => item.terminalId === terminalId)
    return terminal
      ? t('paymentTerminalSettings.terminalLabel', { lane: terminal.laneNo, name: terminal.displayName })
      : terminalId
  }

  const missingDeviceCodes = new Set(devices.filter((device) => device.deviceMissing).map((device) => device.deviceCode))
  const conflictDeviceCodes = new Set(checklist.conflicts.flatMap((conflict) => conflict.deviceCodes))
  const hasDuplicateAssignments = terminals.some((terminal) => terminal.selectedDeviceCount > 1)

  // 冲突与「选中未配对终端」会挡住启用（红色）；设备记录缺失只需清除（橙色）。
  const issues = [
    ...checklist.conflicts.map((conflict) => ({
      key: `conflict-${conflict.terminalId}`,
      tone: 'error' as const,
      text: t('paymentTerminalSettings.issueConflict', {
        devices: conflict.deviceCodes.join(' / '),
        terminal: terminalLabel(conflict.terminalId),
      }),
    })),
    ...checklist.notReadySelections.map((issue) => ({
      key: `not-ready-${issue.deviceCode}`,
      tone: 'error' as const,
      text: t('paymentTerminalSettings.issueNotReady', {
        device: issue.deviceCode,
        terminal: terminalLabel(issue.terminalId),
      }),
    })),
    ...checklist.orphanSelections.map((issue) => ({
      key: `orphan-${issue.deviceCode}`,
      tone: 'warning' as const,
      text: t('paymentTerminalSettings.issueOrphan', {
        device: issue.deviceCode,
        terminal: terminalLabel(issue.terminalId),
      }),
    })),
  ]

  const terminalColumns: TableProps<LinklyTerminalAdminDto>['columns'] = [
    {
      title: t('paymentTerminalSettings.laneNo'),
      dataIndex: 'laneNo',
      width: 96,
      render: (value: number) => <span className="pts-lane">{value}</span>,
    },
    {
      title: t('paymentTerminalSettings.terminalName'),
      dataIndex: 'displayName',
      width: 260,
      render: (value: string, terminal) => (
        <>
          <span className="pts-cell-primary">{value}</span>
          <span className="pts-cell-secondary">
            <code>{terminal.usernameMasked || '--'}</code>
            {terminal.hasPassword
              ? <span>{t('paymentTerminalSettings.passwordSaved')}</span>
              : <span className="pts-cell-warning">{t('paymentTerminalSettings.passwordMissing')}</span>}
          </span>
        </>
      ),
    },
    {
      title: t('paymentTerminalSettings.pairingState'),
      dataIndex: 'pairingState',
      width: 180,
      render: (value: LinklyPairingState) => {
        // 配对只能在 POS 端输入 Pair Code 完成，这里只提示下一步去哪里做。
        const hint = value === 'Unpaired'
          ? t('paymentTerminalSettings.pairingHintUnpaired')
          : value === 'NeedsRepair'
            ? t('paymentTerminalSettings.pairingHintNeedsRepair')
            : null
        return (
          <>
            <PairingStateTag state={value} />
            {hint ? <span className="pts-cell-secondary">{hint}</span> : null}
          </>
        )
      },
    },
    {
      title: t('paymentTerminalSettings.health'),
      key: 'health',
      width: 190,
      render: (_, terminal) => <TerminalHealth terminal={terminal} now={now} />,
    },
    {
      title: t('paymentTerminalSettings.selectedDevices'),
      key: 'devices',
      width: 210,
      render: (_, terminal) => {
        const deviceCodes = getTerminalDeviceCodes(linklyManagement, terminal.terminalId)
        if (deviceCodes.length === 0) {
          return <span className="pts-muted">{t('paymentTerminalSettings.notAssigned')}</span>
        }
        const duplicated = deviceCodes.length > 1 || terminal.selectedDeviceCount > 1
        return (
          <Space size={4} wrap>
            {deviceCodes.map((deviceCode) => (
              <Tag key={deviceCode} color={duplicated || missingDeviceCodes.has(deviceCode) ? 'error' : undefined}>
                {deviceCode}
              </Tag>
            ))}
          </Space>
        )
      },
    },
    {
      title: t('common.action'),
      key: 'actions',
      fixed: 'right',
      width: 90,
      render: (_, terminal) => (
        <Button
          type="link"
          icon={<EditOutlined />}
          onClick={() => openEditTerminal(terminal)}
          disabled={!canManageSettings}
        >
          {t('common.edit')}
        </Button>
      ),
    },
  ]

  const deviceColumns: TableProps<LinklyTerminalDeviceAdminDto>['columns'] = [
    {
      title: t('paymentTerminalSettings.deviceCode'),
      dataIndex: 'deviceCode',
      width: 220,
      render: (value: string, device) => (
        <>
          <span className="pts-cell-primary">{value}</span>
          <span className="pts-cell-secondary">{device.deviceSystem || '--'}</span>
        </>
      ),
    },
    {
      title: t('paymentTerminalSettings.deviceStatus'),
      dataIndex: 'enabled',
      width: 130,
      render: (enabled: boolean, device) => device.deviceMissing
        ? <Tag color="red">{t('paymentTerminalSettings.deviceMissing')}</Tag>
        : enabled
          ? <Tag color="green">{t('paymentTerminalSettings.deviceEnabled')}</Tag>
          : <Tag>{t('paymentTerminalSettings.deviceDisabled')}</Tag>,
    },
    {
      title: t('paymentTerminalSettings.initialTerminal'),
      dataIndex: 'terminalId',
      render: (terminalId: string | null | undefined, device) => (
        <Select
          style={{ width: '100%', minWidth: 260 }}
          value={terminalId ?? undefined}
          placeholder={t('paymentTerminalSettings.selectTerminal')}
          options={terminals.map((terminal) => {
            const owner = getLinklyTerminalAssignmentOwner(linklyManagement, terminal.terminalId, device.deviceCode)
            // 选项里直接带上配对状态与占用者，避免把 POS 分到一台还不能用的刷卡机上。
            const parts = [t('paymentTerminalSettings.terminalLabel', { lane: terminal.laneNo, name: terminal.displayName })]
            if (terminal.pairingState !== 'Ready') {
              parts.push(t(`paymentTerminalSettings.pairingStates.${terminal.pairingState}`))
            }
            if (owner) {
              parts.push(t('paymentTerminalSettings.assignedToDevice', { deviceCode: owner }))
            }
            return { value: terminal.terminalId, label: parts.join(' · '), disabled: Boolean(owner) }
          })}
          onChange={(value) => void handleDeviceSelection(device, value)}
          loading={savingSelectionDevice === device.deviceCode}
          disabled={!canManageSettings || !device.enabled || terminals.length === 0}
        />
      ),
    },
    {
      title: t('common.action'),
      key: 'actions',
      width: 110,
      render: (_, device) => device.terminalId && device.revision > 0 ? (
        <Popconfirm
          title={t('paymentTerminalSettings.releaseSelectionConfirmTitle')}
          description={t('paymentTerminalSettings.releaseSelectionConfirmDescription', { deviceCode: device.deviceCode })}
          onConfirm={() => void handleDeleteDeviceSelection(device)}
          okText={t('paymentTerminalSettings.releaseSelection')}
          cancelText={t('common.cancel')}
          disabled={!canManageSettings}
        >
          <Button
            type="link"
            danger
            icon={<DeleteOutlined />}
            loading={savingSelectionDevice === device.deviceCode}
            disabled={!canManageSettings}
          >
            {t('paymentTerminalSettings.releaseSelection')}
          </Button>
        </Popconfirm>
      ) : <span className="pts-muted">--</span>,
    },
  ]

  const squareMeta = squareConfigured
    ? (squareStatus?.updatedBy
      ? t('paymentTerminalSettings.squareUpdatedBy', {
        time: formatUserLocalDateTime(squareStatus.updatedAtUtc),
        user: squareStatus.updatedBy,
      })
      : t('paymentTerminalSettings.squareUpdatedAtOnly', { time: formatUserLocalDateTime(squareStatus?.updatedAtUtc) }))
    : t('paymentTerminalSettings.squareNotConfiguredHint', { environment: environmentLabel })

  return (
    <PageContainer
      title={t('paymentTerminalSettings.title')}
      subtitle={t('paymentTerminalSettings.subtitle')}
      extra={(
        <Space wrap>
          <Segmented
            value={environment}
            options={ENVIRONMENTS.map((item) => ({
              label: t(`paymentTerminalSettings.environments.${item}`),
              value: item,
            }))}
            onChange={handleEnvironmentChange}
          />
          <Button icon={<ReloadOutlined />} onClick={handleRefresh} loading={settingsLoading || linklyLoading}>
            {t('common.refresh')}
          </Button>
        </Space>
      )}
    >
      <div className="pts-page">
        {environment === 'Sandbox' ? (
          <div className="pts-env-banner" role="status">
            <ExperimentOutlined />
            <span>{t('paymentTerminalSettings.sandboxBanner')}</span>
          </div>
        ) : null}

        {!canManageSettings ? (
          <Alert showIcon type="warning" message={t('paymentTerminalSettings.noPermission')} />
        ) : null}

        {/* Square 按环境全局生效，与门店无关，所以放在门店选择之外单独一行。 */}
        <Card className="pts-square" loading={settingsLoading && !settings}>
          <div className="pts-square-row">
            <div className="pts-square-main">
              <span className="pts-square-icon" aria-hidden="true"><WalletOutlined /></span>
              <div>
                <div className="pts-square-title">
                  <span>{t('paymentTerminalSettings.squareTitle')}</span>
                  <StatusTag status={squareStatus} />
                  <span className="pts-scope">{t('paymentTerminalSettings.squareScope')}</span>
                </div>
                <div className={squareConfigured ? 'pts-square-meta' : 'pts-square-meta pts-square-meta-warning'}>
                  {squareMeta}
                </div>
              </div>
            </div>
            <Space wrap>
              <Button
                type={squareConfigured ? 'default' : 'primary'}
                onClick={() => setSquareModalOpen(true)}
                disabled={!canManageSettings}
              >
                {squareConfigured
                  ? t('paymentTerminalSettings.squareReplaceToken')
                  : t('paymentTerminalSettings.squareSetToken')}
              </Button>
              {squareConfigured ? (
                <Popconfirm
                  title={t('paymentTerminalSettings.squareClearConfirmTitle', { environment: environmentLabel })}
                  description={t('paymentTerminalSettings.squareClearConfirmDescription')}
                  onConfirm={() => void submitSquareToken(true)}
                  okText={t('paymentTerminalSettings.squareClear')}
                  okButtonProps={{ danger: true }}
                  cancelText={t('common.cancel')}
                  disabled={!canManageSettings}
                >
                  <Button danger loading={savingSquare && !squareModalOpen} disabled={!canManageSettings}>
                    {t('paymentTerminalSettings.squareClear')}
                  </Button>
                </Popconfirm>
              ) : null}
            </Space>
          </div>
        </Card>

        <Card
          loading={linklyLoading}
          title={(
            <span className="pts-linkly-title">
              <CreditCardOutlined />
              <span>{t('paymentTerminalSettings.linklyTerminalsTitle')}</span>
              {linklyManagement ? (
                <Tag color={mode === 'Active' ? 'green' : mode === 'Draft' ? 'gold' : 'default'}>
                  {t(`paymentTerminalSettings.configurationModes.${mode}`)}
                </Tag>
              ) : null}
              <span className="pts-scope">{t('paymentTerminalSettings.linklyStoreScope')}</span>
            </span>
          )}
          extra={(
            <Select
              showSearch
              optionFilterProp="label"
              style={{ width: 260 }}
              placeholder={t('paymentTerminalSettings.store')}
              aria-label={t('paymentTerminalSettings.store')}
              value={selectedStoreCode}
              options={(settings?.stores ?? []).map((store) => ({
                value: store.storeCode,
                label: `${store.storeCode} - ${store.storeName}`,
              }))}
              onChange={handleStoreChange}
              loading={settingsLoading}
            />
          )}
        >
          {mode === 'Active' ? (
            <div className="pts-active-summary">
              <CheckCircleFilled />
              <strong>{t('paymentTerminalSettings.modeDescriptions.Active')}</strong>
              <span>
                {t('paymentTerminalSettings.activeSummary', {
                  ready: checklist.readyTerminalCount,
                  assigned: checklist.assignedDeviceCount,
                })}
              </span>
            </div>
          ) : (
            <>
              <p className="pts-mode-description">{t(`paymentTerminalSettings.modeDescriptions.${mode}`)}</p>
              <LinklyActivationPanel
                checklist={checklist}
                canManage={canManageSettings}
                activating={activating}
                onActivate={() => void handleActivate()}
              />
            </>
          )}

          {issues.length > 0 ? (
            <ul className="pts-issues">
              {issues.map((issue) => (
                <li key={issue.key} className={issue.tone === 'warning' ? 'pts-issue pts-issue-warning' : 'pts-issue'}>
                  {issue.tone === 'warning' ? <WarningOutlined /> : <ExclamationCircleOutlined />}
                  <span>{issue.text}</span>
                </li>
              ))}
            </ul>
          ) : null}

          {/* 重复分配若只涉及已禁用/已删除的设备，上面的检查不会列出，保留原有的整体提醒。 */}
          {hasDuplicateAssignments && checklist.conflicts.length === 0 ? (
            <Alert
              showIcon
              type="error"
              message={t('paymentTerminalSettings.duplicateAssignmentWarning')}
              style={{ marginTop: 12 }}
            />
          ) : null}

          <section className="pts-section">
            <div className="pts-section-head">
              <div>
                <h3 className="pts-section-title">
                  {t('paymentTerminalSettings.terminalsHeading')}
                  <span className="pts-count">{terminals.length}</span>
                </h3>
                <p className="pts-section-hint">
                  {t('paymentTerminalSettings.oneCredentialPerTerminal')} {t('paymentTerminalSettings.pairHint')}
                </p>
              </div>
              <Button
                icon={<PlusOutlined />}
                onClick={openCreateTerminal}
                disabled={!canManageSettings || !selectedStoreCode}
              >
                {t('paymentTerminalSettings.addTerminal')}
              </Button>
            </div>
            <MeasuredTable
              metricId="system.payment-terminal-settings.terminals"
              rowKey="terminalId"
              columns={terminalColumns}
              dataSource={terminals}
              pagination={false}
              size="small"
              scroll={{ x: 1030 }}
              rowClassName={(terminal) => (
                getTerminalDeviceCodes(linklyManagement, terminal.terminalId).length > 1 || terminal.selectedDeviceCount > 1
                  ? 'pts-row-danger'
                  : ''
              )}
              locale={{ emptyText: t('paymentTerminalSettings.noTerminals') }}
            />
          </section>

          <section className="pts-section">
            <div className="pts-section-head">
              <div>
                <h3 className="pts-section-title">
                  {t('paymentTerminalSettings.deviceSelectionsTitle')}
                  <span className="pts-count">{devices.length}</span>
                </h3>
                <p className="pts-section-hint">{t('paymentTerminalSettings.deviceSelectionsDescription')}</p>
              </div>
            </div>
            <MeasuredTable
              metricId="system.payment-terminal-settings.device-selections"
              rowKey="deviceCode"
              columns={deviceColumns}
              dataSource={devices}
              pagination={false}
              size="small"
              scroll={{ x: 860 }}
              rowClassName={(device) => (
                device.deviceMissing || conflictDeviceCodes.has(device.deviceCode)
                  ? 'pts-row-danger'
                  : device.enabled ? '' : 'pts-row-disabled'
              )}
              locale={{ emptyText: t('paymentTerminalSettings.noDevices') }}
            />
          </section>
        </Card>
      </div>

      <Modal
        open={squareModalOpen}
        title={t('paymentTerminalSettings.squareTokenModalTitle', { environment: environmentLabel })}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        onOk={() => void submitSquareToken(false)}
        onCancel={() => setSquareModalOpen(false)}
        confirmLoading={savingSquare}
        destroyOnClose
      >
        <Alert
          showIcon
          type={environment === 'Sandbox' ? 'warning' : 'info'}
          message={t('paymentTerminalSettings.squareTokenModalHint', { environment: environmentLabel })}
          style={{ marginBottom: 16 }}
        />
        {/* preserve=false：关闭弹窗即丢弃输入，token 明文不在表单里留存。 */}
        <Form form={squareForm} layout="vertical" preserve={false} initialValues={{ accessToken: '' }}>
          <Form.Item
            label={t('paymentTerminalSettings.accessToken')}
            name="accessToken"
            rules={[{ required: true, whitespace: true, message: t('paymentTerminalSettings.validationAccessToken') }]}
          >
            <Input.Password autoComplete="new-password" />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        open={terminalModalOpen}
        title={editingTerminal
          ? t('paymentTerminalSettings.editTerminalTitle', { name: editingTerminal.displayName })
          : t('paymentTerminalSettings.addTerminalTitle')}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        onOk={() => void handleSaveTerminal()}
        onCancel={() => {
          setTerminalModalOpen(false)
          setEditingTerminal(null)
          terminalForm.resetFields()
        }}
        confirmLoading={savingTerminal}
        width={560}
        destroyOnClose
      >
        <Alert
          showIcon
          type={editingTerminal ? 'warning' : 'info'}
          message={editingTerminal
            ? t('paymentTerminalSettings.editCredentialHint', { username: editingTerminal.usernameMasked })
            : t('paymentTerminalSettings.pairHint')}
          style={{ marginBottom: 16 }}
        />
        <Form
          form={terminalForm}
          layout="vertical"
          initialValues={editingTerminal ? createLinklyTerminalFormValues(editingTerminal) : createNewTerminalFormValues()}
        >
          <Row gutter={12}>
            <Col xs={24} sm={8}>
              <Form.Item
                label={t('paymentTerminalSettings.laneNo')}
                name="laneNo"
                rules={[{ required: true, message: t('paymentTerminalSettings.validation.laneNo') }]}
              >
                <InputNumber min={1} max={9999} precision={0} style={{ width: '100%' }} />
              </Form.Item>
            </Col>
            <Col xs={24} sm={16}>
              <Form.Item
                label={t('paymentTerminalSettings.terminalName')}
                name="displayName"
                rules={[{ required: true, whitespace: true, message: t('paymentTerminalSettings.validation.terminalName') }]}
              >
                <Input maxLength={128} />
              </Form.Item>
            </Col>
          </Row>
          <Typography.Text strong style={{ display: 'block', marginBottom: 8 }}>
            {t('paymentTerminalSettings.credential')}
          </Typography.Text>
          <Row gutter={12}>
            <Col xs={24} sm={12}>
              <Form.Item
                label={t('paymentTerminalSettings.username')}
                name="username"
                extra={editingTerminal ? t('paymentTerminalSettings.blankKeepsCredential') : undefined}
                rules={editingTerminal
                  ? []
                  : [{ required: true, whitespace: true, message: t('paymentTerminalSettings.validation.username') }]}
              >
                <Input autoComplete="off" maxLength={128} />
              </Form.Item>
            </Col>
            <Col xs={24} sm={12}>
              <Form.Item
                label={t('paymentTerminalSettings.password')}
                name="password"
                extra={editingTerminal ? t('paymentTerminalSettings.blankKeepsCredential') : undefined}
                rules={editingTerminal
                  ? []
                  : [{ required: true, whitespace: true, message: t('paymentTerminalSettings.validation.password') }]}
              >
                <Input.Password autoComplete="new-password" maxLength={512} />
              </Form.Item>
            </Col>
          </Row>
        </Form>
      </Modal>
    </PageContainer>
  )
}
