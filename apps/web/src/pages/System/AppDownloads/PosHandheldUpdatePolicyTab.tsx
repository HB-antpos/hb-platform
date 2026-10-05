import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type Dispatch,
  type SetStateAction,
} from 'react'
import { useTranslation } from 'react-i18next'
import {
  Alert,
  Button,
  Card,
  Col,
  Descriptions,
  Empty,
  Form,
  Input,
  InputNumber,
  Modal,
  Row,
  Select,
  Space,
  Switch,
  Tabs,
  Tag,
  Timeline,
  Typography,
  message,
} from 'antd'
import type { FormInstance } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import {
  AppstoreAddOutlined,
  LinkOutlined,
  ReloadOutlined,
  SaveOutlined,
} from '@ant-design/icons'
import { posHandheldUpdatePolicyService } from '../../../services/posHandheldUpdatePolicyService'
import type {
  PosHandheldPlatform,
  PosHandheldPolicyLane,
  PosHandheldReleaseCandidate,
  PosHandheldUpdatePolicy,
  PosHandheldUpdatePolicyRevision,
} from '../../../types/posHandheldUpdatePolicy'
import {
  buildPosHandheldPolicyConfirmationSummary,
  buildPosHandheldPolicyRequest,
  filterPosHandheldCandidates,
  getPosHandheldCandidateEffectiveStatus,
  getPosHandheldCandidateKey,
  getPosHandheldCandidateLabel,
  getPosHandheldPolicySelectionState,
  isPosHandheldPolicyCandidateActive,
  mergePosHandheldPolicyCandidates,
  type PosHandheldCandidateFilters,
  type PosHandheldPolicyFormValue,
} from './posHandheldUpdatePolicyLogic'
import { isAppUpdatePolicyVersionConflict } from './appUpdatePolicyLogic'
import {
  executeLatestRequestLane,
  LatestRequestLane,
  savePolicyWithConflictReload,
} from './appUpdatePolicyRequestLogic'
import { formatAppDownloadLocalDateTime } from './time'
import { MeasuredTable } from '../../../components/MeasuredTable'

interface PosHandheldUpdatePolicyTabProps {
  canManage: boolean
  refreshVersion?: number
  onRegisterIosRelease: () => void
  /** 固定为单条轨道：版本目录只列该轨道候选，策略区只显示该轨道（版本发布中心按轨道拆页签）。 */
  lane?: PosHandheldPolicyLane
  /** 策略保存成功后通知外层刷新摘要。 */
  onChanged?: () => void
}

interface LoadStatus {
  loading: boolean
  loaded: boolean
  failed: boolean
}

type RequestLaneKey = 'policies' | PosHandheldPolicyLane

const POLICY_LANES: readonly PosHandheldPolicyLane[] = [
  'android-native',
  'ios-native',
  'android-ota',
  'ios-ota',
]

const EMPTY_LOAD_STATUS: LoadStatus = {
  loading: false,
  loaded: false,
  failed: false,
}

function emptyPolicy(lane: PosHandheldPolicyLane): PosHandheldUpdatePolicy {
  return {
    id: null,
    lane,
    managed: false,
    enabled: false,
    required: false,
    policyVersion: 0,
    candidateId: null,
    candidateValid: false,
    blockedReason: null,
    candidate: null,
    minimumSupportedVersion: null,
    minimumSupportedBuildNumber: null,
    releaseMessage: null,
    updatedAt: null,
    updatedBy: null,
  }
}

function createPolicyRecord(): Record<PosHandheldPolicyLane, PosHandheldUpdatePolicy> {
  return {
    'android-native': emptyPolicy('android-native'),
    'ios-native': emptyPolicy('ios-native'),
    'android-ota': emptyPolicy('android-ota'),
    'ios-ota': emptyPolicy('ios-ota'),
  }
}

function createCandidateRecord(): Record<PosHandheldPolicyLane, PosHandheldReleaseCandidate[]> {
  return {
    'android-native': [],
    'ios-native': [],
    'android-ota': [],
    'ios-ota': [],
  }
}

function createLoadStatusRecord(): Record<PosHandheldPolicyLane, LoadStatus> {
  return {
    'android-native': { ...EMPTY_LOAD_STATUS },
    'ios-native': { ...EMPTY_LOAD_STATUS },
    'android-ota': { ...EMPTY_LOAD_STATUS },
    'ios-ota': { ...EMPTY_LOAD_STATUS },
  }
}

function createSavingRecord(): Record<PosHandheldPolicyLane, boolean> {
  return {
    'android-native': false,
    'ios-native': false,
    'android-ota': false,
    'ios-ota': false,
  }
}

function createRevisionRecord(): Record<PosHandheldPolicyLane, PosHandheldUpdatePolicyRevision[]> {
  return {
    'android-native': [],
    'ios-native': [],
    'android-ota': [],
    'ios-ota': [],
  }
}

function safeExternalUrl(value?: string | null) {
  if (!value) {
    return null
  }
  try {
    const url = new URL(value)
    return url.protocol === 'https:' ? url.toString() : null
  } catch {
    return null
  }
}

function toFormValue(policy: PosHandheldUpdatePolicy): PosHandheldPolicyFormValue {
  return {
    enabled: policy.enabled,
    required: policy.required,
    candidateId: policy.candidateId,
    minimumSupportedVersion: policy.minimumSupportedVersion,
    minimumSupportedBuildNumber: policy.minimumSupportedBuildNumber,
    releaseMessage: policy.releaseMessage,
  }
}

function lanePlatform(lane: PosHandheldPolicyLane): PosHandheldPlatform {
  return lane.startsWith('ios-') ? 'ios' : 'android'
}

function isNativeLane(lane: PosHandheldPolicyLane) {
  return lane.endsWith('-native')
}

export default function PosHandheldUpdatePolicyTab({
  canManage,
  refreshVersion = 0,
  onRegisterIosRelease,
  lane: fixedLane,
  onChanged,
}: PosHandheldUpdatePolicyTabProps) {
  const { t } = useTranslation()
  const [androidNativeForm] = Form.useForm<PosHandheldPolicyFormValue>()
  const [iosNativeForm] = Form.useForm<PosHandheldPolicyFormValue>()
  const [androidOtaForm] = Form.useForm<PosHandheldPolicyFormValue>()
  const [iosOtaForm] = Form.useForm<PosHandheldPolicyFormValue>()
  const [policies, setPolicies] = useState(createPolicyRecord)
  const [candidates, setCandidates] = useState(createCandidateRecord)
  const [policyLoadStatus, setPolicyLoadStatus] = useState<LoadStatus>(
    () => ({ ...EMPTY_LOAD_STATUS }),
  )
  const [candidateLoadStatus, setCandidateLoadStatus] = useState(createLoadStatusRecord)
  const [saving, setSaving] = useState(createSavingRecord)
  const [revisions, setRevisions] = useState(createRevisionRecord)
  const [activePlatform, setActivePlatform] = useState<PosHandheldPlatform>('android')
  const [filters, setFilters] = useState<PosHandheldCandidateFilters>({
    platform: 'all',
    kind: 'all',
    status: 'all',
    keyword: '',
  })
  const requestLanesRef = useRef<Record<RequestLaneKey, LatestRequestLane>>({
    policies: new LatestRequestLane(),
    'android-native': new LatestRequestLane(),
    'ios-native': new LatestRequestLane(),
    'android-ota': new LatestRequestLane(),
    'ios-ota': new LatestRequestLane(),
  })

  const androidNativeEnabled = Form.useWatch('enabled', androidNativeForm) ?? false
  const iosNativeEnabled = Form.useWatch('enabled', iosNativeForm) ?? false
  const androidOtaEnabled = Form.useWatch('enabled', androidOtaForm) ?? false
  const iosOtaEnabled = Form.useWatch('enabled', iosOtaForm) ?? false
  const androidNativeCandidateId = Form.useWatch('candidateId', androidNativeForm)
  const iosNativeCandidateId = Form.useWatch('candidateId', iosNativeForm)
  const androidOtaCandidateId = Form.useWatch('candidateId', androidOtaForm)
  const iosOtaCandidateId = Form.useWatch('candidateId', iosOtaForm)
  const enabledByLane: Record<PosHandheldPolicyLane, boolean> = {
    'android-native': androidNativeEnabled,
    'ios-native': iosNativeEnabled,
    'android-ota': androidOtaEnabled,
    'ios-ota': iosOtaEnabled,
  }
  const candidateIdByLane: Record<PosHandheldPolicyLane, string | null | undefined> = {
    'android-native': androidNativeCandidateId,
    'ios-native': iosNativeCandidateId,
    'android-ota': androidOtaCandidateId,
    'ios-ota': iosOtaCandidateId,
  }

  const getForm = useCallback((lane: PosHandheldPolicyLane): FormInstance<PosHandheldPolicyFormValue> => {
    switch (lane) {
      case 'android-native':
        return androidNativeForm
      case 'ios-native':
        return iosNativeForm
      case 'android-ota':
        return androidOtaForm
      case 'ios-ota':
        return iosOtaForm
    }
  }, [androidNativeForm, androidOtaForm, iosNativeForm, iosOtaForm])

  const runLoad = useCallback(async <T,>(
    key: RequestLaneKey,
    setStatus: Dispatch<SetStateAction<LoadStatus>>,
    load: (signal: AbortSignal) => Promise<T>,
    commit: (value: T) => void,
  ) => {
    setStatus((current) => ({ ...current, loading: true, failed: false }))
    const result = await executeLatestRequestLane(
      requestLanesRef.current[key],
      load,
      commit,
    )
    if (result.status === 'applied') {
      setStatus({ loading: false, loaded: true, failed: false })
    } else if (result.status === 'failed') {
      setStatus((current) => ({ ...current, loading: false, failed: true }))
    }
    return result.status
  }, [])

  const loadPolicies = useCallback(
    () => runLoad(
      'policies',
      setPolicyLoadStatus,
      async (signal) => {
        const [items, revisionLists] = await Promise.all([
          posHandheldUpdatePolicyService.getPolicies(signal),
          Promise.all(POLICY_LANES.map(
            (lane) => posHandheldUpdatePolicyService.getRevisions(lane, signal),
          )),
        ])
        return { items, revisionLists }
      },
      ({ items, revisionLists }) => {
        const next = createPolicyRecord()
        const nextRevisions = createRevisionRecord()
        for (const policy of items) {
          if (POLICY_LANES.includes(policy.lane)) {
            next[policy.lane] = policy
          }
        }
        POLICY_LANES.forEach((lane, index) => {
          nextRevisions[lane] = revisionLists[index] ?? []
        })
        setPolicies(next)
        setRevisions(nextRevisions)
        for (const lane of POLICY_LANES) {
          getForm(lane).setFieldsValue(toFormValue(next[lane]))
        }
      },
    ),
    [getForm, runLoad],
  )

  const loadCandidates = useCallback((lane: PosHandheldPolicyLane) => {
    const setStatus: Dispatch<SetStateAction<LoadStatus>> = (update) => {
      setCandidateLoadStatus((current) => ({
        ...current,
        [lane]: typeof update === 'function' ? update(current[lane]) : update,
      }))
    }
    const platform = lanePlatform(lane)
    const load = isNativeLane(lane)
      ? (signal: AbortSignal) => posHandheldUpdatePolicyService
        .getNativeCandidates(platform, signal)
      : (signal: AbortSignal) => posHandheldUpdatePolicyService
        .getOtaCandidates(platform, signal)
    return runLoad(
      lane,
      setStatus,
      load,
      (items) => setCandidates((current) => ({ ...current, [lane]: items })),
    )
  }, [runLoad])

  const refreshAll = useCallback(async () => {
    await Promise.allSettled([
      loadPolicies(),
      ...POLICY_LANES.map((lane) => loadCandidates(lane)),
    ])
  }, [loadCandidates, loadPolicies])

  useEffect(() => {
    void refreshAll()
    return () => {
      for (const key of Object.keys(requestLanesRef.current) as RequestLaneKey[]) {
        requestLanesRef.current[key].invalidate()
      }
    }
  }, [refreshAll, refreshVersion])

  const policyList = useMemo(
    () => POLICY_LANES.map((lane) => policies[lane]),
    [policies],
  )
  const allCandidates = useMemo(
    () => mergePosHandheldPolicyCandidates(
      POLICY_LANES.flatMap((lane) => candidates[lane]),
      policyList,
    ),
    [candidates, policyList],
  )
  const mergedCandidatesByLane = useMemo(() => {
    const next = createCandidateRecord()
    for (const candidate of allCandidates) {
      next[candidate.lane].push(candidate)
    }
    return next
  }, [allCandidates])
  const activeCandidateIds = useMemo(
    () => new Set(
      POLICY_LANES
        .map((lane) => policies[lane])
        .filter(isPosHandheldPolicyCandidateActive)
        .map((policy) => `${policy.lane}:${policy.candidateId}`),
    ),
    [policies],
  )
  const boundPoliciesByCandidate = useMemo(
    () => new Map(
      POLICY_LANES
        .map((lane) => policies[lane])
        .filter((policy) => policy.enabled && policy.candidateId)
        .map((policy) => [`${policy.lane}:${policy.candidateId}`, policy]),
    ),
    [policies],
  )
  const blockedCandidateIds = useMemo(
    () => new Set(
      policyList
        .filter((policy) =>
          policy.enabled && policy.candidateId && !policy.candidateValid
        )
        .map((policy) => `${policy.lane}:${policy.candidateId}`),
    ),
    [policyList],
  )
  // 固定轨道时平台与类型由轨道决定，用户只能再按状态与关键字筛选。
  const effectiveFilters = useMemo<PosHandheldCandidateFilters>(
    () => (fixedLane
      ? {
        ...filters,
        platform: lanePlatform(fixedLane),
        kind: isNativeLane(fixedLane) ? 'native' : 'ota',
      }
      : filters),
    [filters, fixedLane],
  )
  const filteredCandidates = useMemo(
    () => filterPosHandheldCandidates(
      allCandidates,
      effectiveFilters,
      activeCandidateIds,
      blockedCandidateIds,
    ),
    [activeCandidateIds, allCandidates, blockedCandidateIds, effectiveFilters],
  )
  const anyLoading = policyLoadStatus.loading
    || POLICY_LANES.some((lane) => candidateLoadStatus[lane].loading)

  const blockedReasonText = useCallback((reason: string | null) => {
    if (
      reason === 'OTA_CANDIDATE_NOT_CHANNEL_HEAD'
      || reason === 'POS_HANDHELD_OTA_CANDIDATE_NOT_CHANNEL_HEAD'
    ) {
      return t('system.appDownloads.updatePolicy.posHandheld.otaNotHead')
    }
    if (reason === 'POS_HANDHELD_UPDATE_CANDIDATE_FINGERPRINT_MISMATCH') {
      return t('system.appDownloads.updatePolicy.posHandheld.fingerprintMismatch')
    }
    return reason || t('system.appDownloads.updatePolicy.posHandheld.notActivatable')
  }, [t])

  const selectCandidate = useCallback((candidate: PosHandheldReleaseCandidate) => {
    if (!candidate.activatable || !canManage) {
      return
    }
    setActivePlatform(candidate.platform)
    getForm(candidate.lane).setFieldsValue({
      enabled: true,
      candidateId: candidate.id,
    })
    message.info(t('system.appDownloads.updatePolicy.posHandheld.candidateSelected'))
  }, [canManage, getForm, t])

  const catalogColumns: ColumnsType<PosHandheldReleaseCandidate> = useMemo(() => [
    {
      title: t('system.appDownloads.updatePolicy.posHandheld.platform'),
      dataIndex: 'platform',
      width: 100,
      render: (value: PosHandheldPlatform) => (
        <Tag color={value === 'android' ? 'green' : 'blue'}>
          {value === 'android' ? 'Android' : 'iOS'}
        </Tag>
      ),
    },
    {
      title: t('system.appDownloads.updatePolicy.posHandheld.releaseType'),
      dataIndex: 'kind',
      width: 100,
      render: (value: PosHandheldReleaseCandidate['kind'], candidate) => (
        <Space size={4} wrap>
          <Tag>{value === 'native' ? t('system.appDownloads.updatePolicy.posHandheld.native') : 'OTA'}</Tag>
          {value === 'ota' ? (
            <Tag color={candidate.legacy ? 'default' : 'cyan'}>
              {candidate.legacy
                ? t('system.appDownloads.updatePolicy.posHandheld.legacyFixedChannel')
                : t('system.appDownloads.updatePolicy.posHandheld.releaseChannel')}
            </Tag>
          ) : null}
        </Space>
      ),
    },
    {
      title: t('system.appDownloads.updatePolicy.versionBuild'),
      width: 150,
      render: (_, candidate) => candidate.kind === 'native'
        ? getPosHandheldCandidateLabel(candidate)
        : '--',
    },
    {
      title: t('system.appDownloads.updatePolicy.posHandheld.runtimeChannel'),
      width: 330,
      render: (_, candidate) => candidate.kind === 'ota' ? (
        <Space direction="vertical" size={0}>
          <Typography.Text>{candidate.runtimeVersion || '--'}</Typography.Text>
          <Typography.Text copyable type="secondary">
            {candidate.releaseChannel || candidate.channel || '--'}
          </Typography.Text>
        </Space>
      ) : '--',
    },
    {
      title: t('system.appDownloads.updatePolicy.posHandheld.identity'),
      width: 300,
      render: (_, candidate) => (
        <Space direction="vertical" size={0}>
          <Typography.Text copyable={{ text: candidate.updateId || candidate.id }}>
            {candidate.updateId || candidate.id}
          </Typography.Text>
          {candidate.updateGroupId ? (
            <Typography.Text type="secondary" copyable={{ text: candidate.updateGroupId }}>
              Group: {candidate.updateGroupId}
            </Typography.Text>
          ) : null}
        </Space>
      ),
    },
    {
      title: t('system.appDownloads.updatePolicy.posHandheld.releaseAudit'),
      width: 330,
      render: (_, candidate) => candidate.kind === 'ota' ? (
        <Space direction="vertical" size={0} style={{ width: '100%' }}>
          <Typography.Text ellipsis={{ tooltip: candidate.message || undefined }}>
            {candidate.message || '--'}
          </Typography.Text>
          <Typography.Text type="secondary">
            Commit: {candidate.gitCommitHash?.slice(0, 8) || '--'} · {candidate.createdBy || '--'}
          </Typography.Text>
          {candidate.isRollback ? (
            <Typography.Text copyable={{ text: candidate.rollbackOfReleaseId || '' }} type="warning">
              {t('system.appDownloads.updatePolicy.posHandheld.rollbackSource')}: {candidate.rollbackOfReleaseId?.slice(0, 8) || '--'}
            </Typography.Text>
          ) : null}
        </Space>
      ) : '--',
    },
    {
      title: t('system.appDownloads.updatePolicy.status'),
      width: 150,
      render: (_, candidate) => {
        const candidateKey = getPosHandheldCandidateKey(candidate)
        const boundPolicy = boundPoliciesByCandidate.get(candidateKey)
        const effectiveStatus = getPosHandheldCandidateEffectiveStatus(
          candidate,
          activeCandidateIds,
          blockedCandidateIds,
        )
        if (effectiveStatus === 'active') {
          return <Tag color="processing">{t('system.appDownloads.updatePolicy.active')}</Tag>
        }
        if (boundPolicy) {
          return (
            <Space size={4} wrap>
              <Tag color="processing">
                {t('system.appDownloads.updatePolicy.posHandheld.policyBound')}
              </Tag>
              <Tag
                color="warning"
                title={blockedReasonText(
                  boundPolicy.blockedReason ?? candidate.blockedReason,
                )}
              >
                {t('system.appDownloads.updatePolicy.posHandheld.blocked')}
              </Tag>
            </Space>
          )
        }
        return effectiveStatus === 'activatable'
          ? <Tag color="success">{t('system.appDownloads.updatePolicy.posHandheld.activatable')}</Tag>
          : (
              <Tag color="warning" title={blockedReasonText(candidate.blockedReason)}>
                {t('system.appDownloads.updatePolicy.posHandheld.blocked')}
              </Tag>
            )
      },
    },
    {
      title: t('system.appDownloads.updatePolicy.publishedAt'),
      dataIndex: 'createdAt',
      width: 180,
      render: (value: string) => formatAppDownloadLocalDateTime(value),
    },
    {
      title: t('common.action'),
      fixed: 'right',
      width: 210,
      render: (_, candidate) => {
        const dashboardUrl = safeExternalUrl(candidate.dashboardUrl)
        return (
          <Space size={4} wrap>
            {dashboardUrl ? (
              <Button
                type="link"
                size="small"
                icon={<LinkOutlined />}
                onClick={() => window.open(dashboardUrl, '_blank', 'noopener,noreferrer')}
              >
                Dashboard
              </Button>
            ) : null}
            <Button
              type="link"
              size="small"
              disabled={!canManage || !candidate.activatable}
              onClick={() => selectCandidate(candidate)}
            >
              {t('system.appDownloads.updatePolicy.posHandheld.selectForRelease')}
            </Button>
          </Space>
        )
      },
    },
  ], [
    activeCandidateIds,
    blockedReasonText,
    blockedCandidateIds,
    boundPoliciesByCandidate,
    canManage,
    selectCandidate,
    t,
  ])

  const handleSaveResult = useCallback((result: Awaited<ReturnType<typeof savePolicyWithConflictReload>>) => {
    if (result === 'saved') {
      message.success(t('system.appDownloads.updatePolicy.posHandheld.saveSuccess'))
      onChanged?.()
      return
    }
    const key = result === 'conflict-reloaded'
      ? 'versionConflict'
      : result === 'conflict-reload-superseded'
        ? 'versionConflictReloadSuperseded'
        : 'versionConflictReloadFailed'
    message.warning(t(`system.appDownloads.updatePolicy.${key}`))
  }, [onChanged, t])

  const saveLane = useCallback(async (
    lane: PosHandheldPolicyLane,
    value: PosHandheldPolicyFormValue,
  ) => {
    setSaving((current) => ({ ...current, [lane]: true }))
    try {
      const request = buildPosHandheldPolicyRequest(
        value,
        lane,
        policies[lane].policyVersion,
      )
      const result = await savePolicyWithConflictReload(
        async () => {
          const saved = await posHandheldUpdatePolicyService.savePolicy(lane, request)
          setPolicies((current) => ({ ...current, [lane]: saved }))
          getForm(lane).setFieldsValue(toFormValue(saved))
        },
        loadPolicies,
        isAppUpdatePolicyVersionConflict,
      )
      if (result === 'saved') {
        // 保存成功后读取权威策略与 append-only revisions，避免时间线停留在旧快照。
        await loadPolicies()
      }
      handleSaveResult(result)
    } catch {
      message.error(t('system.appDownloads.updatePolicy.posHandheld.saveFailed'))
    } finally {
      setSaving((current) => ({ ...current, [lane]: false }))
    }
  }, [getForm, handleSaveResult, loadPolicies, policies, t])

  const confirmSave = useCallback((
    lane: PosHandheldPolicyLane,
    value: PosHandheldPolicyFormValue,
  ) => {
    const candidate = mergedCandidatesByLane[lane]
      .find((item) => item.id === value.candidateId)
    const summary = buildPosHandheldPolicyConfirmationSummary(value, lane, candidate)
    Modal.confirm({
      title: summary.enabled
        ? t('system.appDownloads.updatePolicy.posHandheld.confirmTitle')
        : t('system.appDownloads.updatePolicy.disableConfirmTitle'),
      content: summary.enabled ? (
        <Space direction="vertical" size={8} style={{ width: '100%' }}>
          <Alert
            type="warning"
            showIcon
            message={t('system.appDownloads.updatePolicy.posHandheld.activationBoundary')}
          />
          <Descriptions size="small" column={1} bordered>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.posHandheld.lane')}>
              {t(`system.appDownloads.updatePolicy.posHandheld.lanes.${lane}`)}
            </Descriptions.Item>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.confirmRelease')}>
              {summary.candidateLabel}
            </Descriptions.Item>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.confirmUpdateMode')}>
              {summary.updateMode === 'required'
                ? t('system.appDownloads.updatePolicy.posHandheld.required')
                : t('system.appDownloads.updatePolicy.posHandheld.optional')}
            </Descriptions.Item>
            {summary.minimumSupportedVersion ? (
              <Descriptions.Item label={t('system.appDownloads.updatePolicy.minimumVersion')}>
                {summary.minimumSupportedVersion}
              </Descriptions.Item>
            ) : null}
            {summary.minimumSupportedBuildNumber !== null ? (
              <Descriptions.Item label={t('system.appDownloads.updatePolicy.confirmMinimumBuild')}>
                {summary.minimumSupportedBuildNumber}
              </Descriptions.Item>
            ) : null}
          </Descriptions>
        </Space>
      ) : t('system.appDownloads.updatePolicy.disableConfirmDescription'),
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      okButtonProps: summary.enabled ? undefined : { danger: true },
      onOk: () => saveLane(lane, value),
    })
  }, [mergedCandidatesByLane, saveLane, t])

  function renderLoadFailure(lane: PosHandheldPolicyLane) {
    const status = candidateLoadStatus[lane]
    return status.failed ? (
      <Alert
        type="error"
        showIcon
        message={t('system.appDownloads.updatePolicy.posHandheld.candidateLoadFailed', {
          lane: t(`system.appDownloads.updatePolicy.posHandheld.lanes.${lane}`),
        })}
        action={(
          <Button size="small" onClick={() => void loadCandidates(lane)}>
            {t('system.appDownloads.updatePolicy.retry')}
          </Button>
        )}
      />
    ) : null
  }

  function renderPolicyEditor(lane: PosHandheldPolicyLane) {
    const policy = policies[lane]
    const form = getForm(lane)
    const laneCandidates = mergedCandidatesByLane[lane]
    const enabled = enabledByLane[lane]
    const selectedCandidateId = candidateIdByLane[lane]
    const selectedCandidate = laneCandidates.find((item) => item.id === selectedCandidateId)
      ?? (policy.candidate?.id === selectedCandidateId ? policy.candidate : undefined)
    const selectedIsBoundCandidate = selectedCandidateId === policy.candidateId
    const selectionState = getPosHandheldPolicySelectionState(
      enabled,
      selectedCandidateId,
      policy,
      selectedCandidate,
    )
    const staleSelection = selectionState === 'blocked'
    const refreshableSelection = selectionState === 'refreshable'
    const domainReady = policyLoadStatus.loaded
      && !policyLoadStatus.loading
      && !policyLoadStatus.failed
      && candidateLoadStatus[lane].loaded
      && !candidateLoadStatus[lane].loading
      && !candidateLoadStatus[lane].failed
    const native = isNativeLane(lane)
    const laneRevisions = revisions[lane]
    const selectableCandidates = laneCandidates.map((candidate) => ({
      value: candidate.id,
      label: getPosHandheldCandidateLabel(candidate),
      disabled: !candidate.activatable && candidate.id !== policy.candidateId,
    }))
    if (
      policy.candidateId
      && !laneCandidates.some((candidate) => candidate.id === policy.candidateId)
    ) {
      selectableCandidates.unshift({
        value: policy.candidateId,
        label: policy.candidate
          ? getPosHandheldCandidateLabel(policy.candidate)
          : `${policy.candidateId} · ${t(
              'system.appDownloads.updatePolicy.posHandheld.candidateUnavailable',
            )}`,
        disabled: !policy.candidateValid,
      })
    }

    return (
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        {renderLoadFailure(lane)}
        {staleSelection || refreshableSelection ? (
          <Alert
            type="warning"
            showIcon
            message={t(
              refreshableSelection
                ? 'system.appDownloads.updatePolicy.posHandheld.refreshableSelection'
                : 'system.appDownloads.updatePolicy.posHandheld.staleSelection',
            )}
            description={blockedReasonText(
              selectedIsBoundCandidate
                ? policy.blockedReason ?? selectedCandidate?.blockedReason ?? null
                : selectedCandidate?.blockedReason ?? null,
            )}
          />
        ) : null}
        <Card
          size="small"
          title={t(`system.appDownloads.updatePolicy.posHandheld.lanes.${lane}`)}
          loading={policyLoadStatus.loading && !policyLoadStatus.loaded}
          extra={(
            <Space size={6} wrap>
              <Tag color={policy.managed ? 'processing' : 'default'}>
                {policy.managed
                  ? t('system.appDownloads.updatePolicy.posHandheld.databaseManaged')
                  : t('system.appDownloads.updatePolicy.posHandheld.legacyManaged')}
              </Tag>
              <Tag>{t('system.appDownloads.updatePolicy.policyVersion')}: {policy.policyVersion}</Tag>
            </Space>
          )}
        >
          <Descriptions size="small" column={{ xs: 1, sm: 2 }} style={{ marginBottom: 16 }}>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.updatedAt')}>
              {formatAppDownloadLocalDateTime(policy.updatedAt)}
            </Descriptions.Item>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.updatedBy')}>
              {policy.updatedBy || '--'}
            </Descriptions.Item>
          </Descriptions>
          <Alert
            type="info"
            showIcon
            style={{ marginBottom: 16 }}
            message={native
              ? t('system.appDownloads.updatePolicy.posHandheld.nativeAuditChain')
              : t('system.appDownloads.updatePolicy.posHandheld.otaAuditChain')}
            description={!native
              ? t('system.appDownloads.updatePolicy.posHandheld.productionOnlyDescription')
              : undefined}
          />
          <Form<PosHandheldPolicyFormValue>
            form={form}
            layout="vertical"
            disabled={!canManage}
            initialValues={toFormValue(policy)}
            onFinish={(value) => confirmSave(lane, value)}
          >
            <Row gutter={[16, 0]}>
              <Col xs={24} sm={12} lg={6}>
                <Form.Item
                  name="enabled"
                  label={t('system.appDownloads.updatePolicy.policyStatus')}
                  valuePropName="checked"
                >
                  <Switch
                    checkedChildren={t('system.appDownloads.updatePolicy.enabled')}
                    unCheckedChildren={t('system.appDownloads.updatePolicy.disabled')}
                  />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={6}>
                <Form.Item
                  name="required"
                  label={t('system.appDownloads.updatePolicy.posHandheld.updateMode')}
                  valuePropName="checked"
                >
                  <Switch
                    disabled={!canManage || !enabled}
                    checkedChildren={t('system.appDownloads.updatePolicy.posHandheld.required')}
                    unCheckedChildren={t('system.appDownloads.updatePolicy.posHandheld.optional')}
                  />
                </Form.Item>
              </Col>
              <Col xs={24} lg={12}>
                <Form.Item
                  name="candidateId"
                  label={t('system.appDownloads.updatePolicy.posHandheld.candidate')}
                  dependencies={['enabled']}
                  rules={[
                    ({ getFieldValue }) => ({
                      validator: async (_rule, value?: string) => {
                        if (getFieldValue('enabled') && !value) {
                          throw new Error(
                            t('system.appDownloads.updatePolicy.posHandheld.candidateRequired'),
                          )
                        }
                      },
                    }),
                  ]}
                >
                  <Select
                    showSearch
                    optionFilterProp="label"
                    disabled={!canManage || !enabled}
                    placeholder={t('system.appDownloads.updatePolicy.posHandheld.selectCandidate')}
                    options={selectableCandidates}
                    notFoundContent={(
                      <Empty
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                        description={t('system.appDownloads.updatePolicy.posHandheld.noCandidates')}
                      />
                    )}
                  />
                </Form.Item>
              </Col>
              {native ? (
                <>
                  <Col xs={24} lg={12}>
                    <Form.Item
                      name="minimumSupportedVersion"
                      label={t('system.appDownloads.updatePolicy.minimumVersion')}
                      extra={t('system.appDownloads.updatePolicy.posHandheld.minimumVersionHelp')}
                    >
                      <Input
                        disabled={!canManage || !enabled}
                        placeholder={t('system.appDownloads.updatePolicy.minimumVersionPlaceholder')}
                      />
                    </Form.Item>
                  </Col>
                  <Col xs={24} lg={12}>
                    <Form.Item
                      name="minimumSupportedBuildNumber"
                      label={t('system.appDownloads.updatePolicy.minimumBuild')}
                    >
                      <InputNumber
                        min={1}
                        max={2_147_483_647}
                        precision={0}
                        style={{ width: '100%' }}
                        disabled={!canManage || !enabled}
                        placeholder={t('system.appDownloads.updatePolicy.minimumBuildPlaceholder')}
                      />
                    </Form.Item>
                  </Col>
                </>
              ) : null}
              <Col span={24}>
                <Form.Item
                  name="releaseMessage"
                  label={t('system.appDownloads.updatePolicy.releaseMessage')}
                >
                  <Input.TextArea
                    rows={3}
                    maxLength={1000}
                    showCount
                    disabled={!canManage || !enabled}
                    placeholder={t('system.appDownloads.updatePolicy.releaseMessagePlaceholder')}
                  />
                </Form.Item>
              </Col>
            </Row>
            {canManage ? (
              <Button
                type="primary"
                htmlType="submit"
                icon={<SaveOutlined />}
                loading={saving[lane]}
                disabled={!domainReady || staleSelection}
              >
                {t('system.appDownloads.updatePolicy.posHandheld.publishPolicy')}
              </Button>
            ) : null}
          </Form>
          <Card
            size="small"
            type="inner"
            title={t('system.appDownloads.updatePolicy.posHandheld.revisionTitle')}
            style={{ marginTop: 16 }}
          >
            {laneRevisions.length > 0 ? (
              <Timeline
                items={laneRevisions.map((revision) => ({
                  color: revision.policyVersion === policy.policyVersion ? 'blue' : 'gray',
                  children: (
                    <Space direction="vertical" size={2} style={{ width: '100%' }}>
                      <Space size={6} wrap>
                        <Tag>{t('system.appDownloads.updatePolicy.policyVersion')}: {revision.policyVersion}</Tag>
                        <Tag>{revision.operation || '--'}</Tag>
                      </Space>
                      <Typography.Text type="secondary">
                        {formatAppDownloadLocalDateTime(revision.createdAt)} · {revision.createdBy || '--'}
                      </Typography.Text>
                      <Typography.Paragraph
                        copyable={{ text: revision.snapshotJson }}
                        ellipsis={{ rows: 2, expandable: true, symbol: t('common.more') }}
                        style={{ marginBottom: 0, overflowWrap: 'anywhere' }}
                      >
                        {revision.snapshotJson}
                      </Typography.Paragraph>
                    </Space>
                  ),
                }))}
              />
            ) : (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={t('system.appDownloads.updatePolicy.posHandheld.noRevisions')}
              />
            )}
          </Card>
        </Card>
      </Space>
    )
  }

  const platformTabs = (['android', 'ios'] as const).map((platform) => ({
    key: platform,
    label: platform === 'android' ? 'Android' : 'iOS',
    children: (
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        {renderPolicyEditor(`${platform}-native`)}
        {renderPolicyEditor(`${platform}-ota`)}
      </Space>
    ),
  }))

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Alert
        type="info"
        showIcon
        message={t('system.appDownloads.updatePolicy.posHandheld.boundaryTitle')}
        description={t('system.appDownloads.updatePolicy.posHandheld.boundaryDescription')}
      />
      <Alert
        type="warning"
        showIcon
        message={t('system.appDownloads.updatePolicy.posHandheld.productionOnly')}
        description={t('system.appDownloads.updatePolicy.posHandheld.productionOnlyDescription')}
      />
      {policyLoadStatus.failed ? (
        <Alert
          type="error"
          showIcon
          message={t('system.appDownloads.updatePolicy.posHandheld.policyLoadFailed')}
          action={(
            <Button size="small" onClick={() => void loadPolicies()}>
              {t('system.appDownloads.updatePolicy.retry')}
            </Button>
          )}
        />
      ) : null}
      <Card
        size="small"
        title={t('system.appDownloads.updatePolicy.posHandheld.catalogTitle')}
        extra={(
          <Space wrap>
            {canManage && (!fixedLane || fixedLane === 'ios-native') ? (
              <Button icon={<AppstoreAddOutlined />} onClick={onRegisterIosRelease}>
                {t('system.appDownloads.updatePolicy.posHandheld.registerIosRelease')}
              </Button>
            ) : null}
            <Button icon={<ReloadOutlined />} loading={anyLoading} onClick={() => void refreshAll()}>
              {t('common.refresh')}
            </Button>
          </Space>
        )}
      >
        <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
          {fixedLane ? null : (
            <Col xs={24} sm={12} lg={5}>
              <Select
                value={filters.platform}
                style={{ width: '100%' }}
                onChange={(platform) => setFilters((current) => ({ ...current, platform }))}
                options={[
                  { value: 'all', label: t('system.appDownloads.updatePolicy.posHandheld.allPlatforms') },
                  { value: 'android', label: 'Android' },
                  { value: 'ios', label: 'iOS' },
                ]}
              />
            </Col>
          )}
          {fixedLane ? null : (
            <Col xs={24} sm={12} lg={5}>
              <Select
                value={filters.kind}
                style={{ width: '100%' }}
                onChange={(kind) => setFilters((current) => ({ ...current, kind }))}
                options={[
                  { value: 'all', label: t('system.appDownloads.updatePolicy.posHandheld.allTypes') },
                  { value: 'native', label: t('system.appDownloads.updatePolicy.posHandheld.native') },
                  { value: 'ota', label: 'OTA' },
                ]}
              />
            </Col>
          )}
          <Col xs={24} sm={12} lg={5}>
            <Select
              value={filters.status}
              style={{ width: '100%' }}
              onChange={(status) => setFilters((current) => ({ ...current, status }))}
              options={[
                { value: 'all', label: t('system.appDownloads.updatePolicy.posHandheld.allStatuses') },
                { value: 'activatable', label: t('system.appDownloads.updatePolicy.posHandheld.activatable') },
                { value: 'active', label: t('system.appDownloads.updatePolicy.active') },
                { value: 'blocked', label: t('system.appDownloads.updatePolicy.posHandheld.blocked') },
              ]}
            />
          </Col>
          <Col xs={24} sm={12} lg={9}>
            <Input.Search
              allowClear
              value={filters.keyword}
              placeholder={t('system.appDownloads.updatePolicy.posHandheld.searchPlaceholder')}
              onChange={(event) => setFilters((current) => ({
                ...current,
                keyword: event.target.value,
              }))}
            />
          </Col>
        </Row>
        <MeasuredTable<PosHandheldReleaseCandidate> metricId="system.app-downloads.pos-handheld-update-policy-tab.table-1"
          rowKey={getPosHandheldCandidateKey}
          size="small"
          columns={catalogColumns}
          dataSource={filteredCandidates}
          loading={POLICY_LANES.some((lane) => candidateLoadStatus[lane].loading)}
          scroll={{ x: 2020 }}
          locale={{
            emptyText: <Empty description={t('system.appDownloads.updatePolicy.posHandheld.noCandidates')} />,
          }}
          pagination={{ pageSize: 10, hideOnSinglePage: true }}
        />
      </Card>
      {fixedLane ? renderPolicyEditor(fixedLane) : (
        <Card size="small" title={t('system.appDownloads.updatePolicy.posHandheld.strategyTitle')}>
          <Tabs
            activeKey={activePlatform}
            onChange={(key) => setActivePlatform(key as PosHandheldPlatform)}
            items={platformTabs}
            destroyInactiveTabPane={false}
          />
        </Card>
      )}
    </Space>
  )
}
