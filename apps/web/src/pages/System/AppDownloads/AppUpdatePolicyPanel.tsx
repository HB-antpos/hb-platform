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
  Radio,
  Row,
  Select,
  Space,
  Switch,
  Tabs,
  Tag,
  Typography,
  message,
} from 'antd'
import type { FormInstance } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import {
  AppstoreAddOutlined,
  LinkOutlined,
  QrcodeOutlined,
  ReloadOutlined,
  SaveOutlined,
} from '@ant-design/icons'
import { QRCodeSVG } from '@rc-component/qrcode'
import { appUpdatePolicyService } from '../../../services/appUpdatePolicyService'
import type {
  AppUpdateApp,
  AppUpdateTargetStoreOption,
  IosAppStoreRelease,
  NativeUpdatePolicy,
  PosIpadOtaRelease,
  PosIpadOtaRollout,
} from '../../../types/appUpdatePolicy'
import {
  buildAppStoreReleaseRegistrationSummary,
  buildNativePolicyConfirmationSummary,
  buildNativeUpdatePolicyRequest,
  buildOtaPolicyConfirmationSummary,
  buildOtaRolloutRequest,
  isAppUpdatePolicyVersionConflict,
  isValidMobileIosBuildNumber,
  isValidPosHandheldBuildNumber,
  isValidPosIpadBuildNumber,
  resolveNativeReleaseStatus,
  resolveOtaReleaseStatus,
  validateMinimumSupportedBuildNumber,
  type AppStoreReleaseRegistrationFormValue,
  type AppStoreReleaseRegistrationSummary,
  type AppUpdatePolicyConfirmationSummary,
  type NativeUpdatePolicyFormValue,
  type OtaRolloutFormValue,
} from './appUpdatePolicyLogic'
import {
  executeLatestRequestLane,
  LatestRequestLane,
  savePolicyWithConflictReload,
} from './appUpdatePolicyRequestLogic'
import { formatAppDownloadLocalDateTime } from './time'
import PosHandheldUpdatePolicyTab from './PosHandheldUpdatePolicyTab'
import MobileOtaPolicyTab from './MobileOtaPolicyTab'
import MobileAndroidNativePolicyTab from './MobileAndroidNativePolicyTab'
import { MeasuredTable } from '../../../components/MeasuredTable'

interface AppUpdatePolicyPanelProps {
  canManage: boolean
}

interface PolicySaveConfirmation {
  kind: 'native' | 'ota'
  nativeApp?: AppUpdateApp
  enabled: boolean
  summary: AppUpdatePolicyConfirmationSummary
  releaseLabel: string
  onOk: () => Promise<void>
}

type LoadLaneKey = 'mobileNative' | 'ipadNative' | 'ipadOta' | 'storeOptions'

interface LoadLaneStatus {
  loading: boolean
  loaded: boolean
  failed: boolean
}

const EMPTY_LOAD_LANE_STATUS: LoadLaneStatus = {
  loading: false,
  loaded: false,
  failed: false,
}

const EMPTY_NATIVE_POLICY: NativeUpdatePolicy = {
  id: null,
  enabled: false,
  policyVersion: 0,
  releaseId: null,
  latestVersion: null,
  minimumSupportedVersion: null,
  minimumSupportedBuildNumber: null,
  appStoreUrl: null,
  releaseMessage: null,
  targetScope: 'all',
  targetStoreGuids: [],
  updatedAt: null,
  updatedBy: null,
}

const EMPTY_OTA_ROLLOUT: PosIpadOtaRollout = {
  id: null,
  enabled: false,
  policyVersion: 0,
  releaseId: null,
  forceUpdate: false,
  targetScope: 'all',
  targetStoreGuids: [],
  releaseMessage: null,
  release: null,
  updatedAt: null,
  updatedBy: null,
}

function toNativeFormValue(policy: NativeUpdatePolicy): NativeUpdatePolicyFormValue {
  return {
    enabled: policy.enabled,
    releaseId: policy.releaseId,
    minimumSupportedVersion: policy.minimumSupportedVersion,
    minimumSupportedBuildNumber: policy.minimumSupportedBuildNumber,
    releaseMessage: policy.releaseMessage,
    targetScope: policy.targetScope,
    targetStoreGuids: policy.targetStoreGuids,
  }
}

function toOtaFormValue(rollout: PosIpadOtaRollout): OtaRolloutFormValue {
  return {
    enabled: rollout.enabled,
    releaseId: rollout.releaseId,
    forceUpdate: rollout.forceUpdate,
    targetScope: rollout.targetScope,
    targetStoreGuids: rollout.targetStoreGuids,
    releaseMessage: rollout.releaseMessage,
  }
}

function formatNativeReleaseLabel(release: IosAppStoreRelease) {
  return `${release.version} (${release.buildNumber || '--'})`
}

function formatOtaReleaseLabel(release: PosIpadOtaRelease) {
  return `${release.channel} · ${release.runtimeVersion} · ${release.iosUpdateId.slice(0, 8)}`
}

function safeExternalUrl(value: string | null | undefined, allowedHosts: readonly string[]) {
  if (!value) {
    return null
  }

  try {
    const url = new URL(value)
    return url.protocol === 'https:'
      && !url.username
      && !url.password
      && allowedHosts.includes(url.hostname)
      ? url.toString()
      : null
  } catch {
    return null
  }
}

export default function AppUpdatePolicyPanel({ canManage }: AppUpdatePolicyPanelProps) {
  const { t } = useTranslation()
  const [mobileForm] = Form.useForm<NativeUpdatePolicyFormValue>()
  const [ipadForm] = Form.useForm<NativeUpdatePolicyFormValue>()
  const [otaForm] = Form.useForm<OtaRolloutFormValue>()
  const [registerForm] = Form.useForm<AppStoreReleaseRegistrationFormValue>()
  const [mobileReleases, setMobileReleases] = useState<IosAppStoreRelease[]>([])
  const [ipadReleases, setIpadReleases] = useState<IosAppStoreRelease[]>([])
  const [otaReleases, setOtaReleases] = useState<PosIpadOtaRelease[]>([])
  const [mobilePolicy, setMobilePolicy] = useState<NativeUpdatePolicy | null>(null)
  const [ipadPolicy, setIpadPolicy] = useState<NativeUpdatePolicy | null>(null)
  const [otaRollout, setOtaRollout] = useState<PosIpadOtaRollout | null>(null)
  const [storeOptions, setStoreOptions] = useState<AppUpdateTargetStoreOption[]>([])
  const [mobileLoadState, setMobileLoadState] = useState<LoadLaneStatus>(
    () => ({ ...EMPTY_LOAD_LANE_STATUS }),
  )
  const [ipadLoadState, setIpadLoadState] = useState<LoadLaneStatus>(
    () => ({ ...EMPTY_LOAD_LANE_STATUS }),
  )
  const [otaLoadState, setOtaLoadState] = useState<LoadLaneStatus>(
    () => ({ ...EMPTY_LOAD_LANE_STATUS }),
  )
  const [storeLoadState, setStoreLoadState] = useState<LoadLaneStatus>(
    () => ({ ...EMPTY_LOAD_LANE_STATUS }),
  )
  const [mobileSaving, setMobileSaving] = useState(false)
  const [ipadSaving, setIpadSaving] = useState(false)
  const [otaSaving, setOtaSaving] = useState(false)
  const [registerApp, setRegisterApp] = useState<AppUpdateApp | null>(null)
  const [registerSaving, setRegisterSaving] = useState(false)
  const [qrRelease, setQrRelease] = useState<IosAppStoreRelease | null>(null)
  const [handheldRefreshVersion, setHandheldRefreshVersion] = useState(0)
  const [mobileOtaRefreshVersion, setMobileOtaRefreshVersion] = useState(0)
  const [mobileAndroidRefreshVersion, setMobileAndroidRefreshVersion] = useState(0)
  const laneRequestsRef = useRef<Record<LoadLaneKey, LatestRequestLane>>({
    mobileNative: new LatestRequestLane(),
    ipadNative: new LatestRequestLane(),
    ipadOta: new LatestRequestLane(),
    storeOptions: new LatestRequestLane(),
  })

  const mobileEnabled = Form.useWatch('enabled', mobileForm) ?? false
  const ipadEnabled = Form.useWatch('enabled', ipadForm) ?? false
  const ipadTargetScope = Form.useWatch('targetScope', ipadForm) ?? 'all'
  const otaEnabled = Form.useWatch('enabled', otaForm) ?? false
  const otaTargetScope = Form.useWatch('targetScope', otaForm) ?? 'all'
  const qrReleaseUrl = qrRelease
    ? safeExternalUrl(qrRelease.appStoreUrl, ['apps.apple.com', 'itunes.apple.com'])
    : null

  const runLoadLane = useCallback(async <T,>(
    key: LoadLaneKey,
    setStatus: Dispatch<SetStateAction<LoadLaneStatus>>,
    load: (signal: AbortSignal) => Promise<T>,
    commit: (value: T) => void,
    failedMessage: string,
    onFailure?: () => void,
  ) => {
    setStatus((current) => ({ ...current, loading: true, failed: false }))
    const result = await executeLatestRequestLane(
      laneRequestsRef.current[key],
      load,
      commit,
    )
    if (result.status === 'stale') {
      return 'stale' as const
    }
    if (result.status === 'failed') {
      console.error(`Failed to load app update policy lane: ${key}`, result.error)
      onFailure?.()
      setStatus((status) => ({ ...status, loading: false, failed: true }))
      message.error(failedMessage)
      return 'failed' as const
    }

    setStatus({ loading: false, loaded: true, failed: false })
    return 'applied' as const
  }, [])

  const invalidateLoadLane = useCallback((
    key: LoadLaneKey,
    setStatus: Dispatch<SetStateAction<LoadLaneStatus>>,
  ) => {
    laneRequestsRef.current[key].invalidate()
    setStatus((status) => ({ ...status, loading: false }))
  }, [])

  const loadMobileNativeLane = useCallback(
    () => runLoadLane(
      'mobileNative',
      setMobileLoadState,
      async (signal) => Promise.all([
        appUpdatePolicyService.getIosAppStoreReleases('mobile-ios', signal),
        appUpdatePolicyService.getMobileIosNativePolicy(signal),
      ]),
      ([releases, policy]) => {
        setMobileReleases(releases)
        setMobilePolicy(policy)
        mobileForm.setFieldsValue(toNativeFormValue(policy))
      },
      t('system.appDownloads.updatePolicy.mobileLoadFailed'),
    ),
    [mobileForm, runLoadLane, t],
  )

  const loadIpadNativeLane = useCallback(
    () => runLoadLane(
      'ipadNative',
      setIpadLoadState,
      async (signal) => Promise.all([
        appUpdatePolicyService.getIosAppStoreReleases('pos-ipad', signal),
        appUpdatePolicyService.getPosIpadNativePolicy(signal),
      ]),
      ([releases, policy]) => {
        setIpadReleases(releases)
        setIpadPolicy(policy)
        ipadForm.setFieldsValue(toNativeFormValue(policy))
      },
      t('system.appDownloads.updatePolicy.ipadNativeLoadFailed'),
    ),
    [ipadForm, runLoadLane, t],
  )

  const loadIpadOtaLane = useCallback(
    () => runLoadLane(
      'ipadOta',
      setOtaLoadState,
      async (signal) => Promise.all([
        appUpdatePolicyService.getPosIpadOtaReleases(signal),
        appUpdatePolicyService.getPosIpadOtaRollout(signal),
      ]),
      ([releases, rollout]) => {
        setOtaReleases(releases)
        setOtaRollout(rollout)
        otaForm.setFieldsValue(toOtaFormValue(rollout))
      },
      t('system.appDownloads.updatePolicy.ipadOtaLoadFailed'),
    ),
    [otaForm, runLoadLane, t],
  )

  const loadStoreOptionsLane = useCallback(
    () => runLoadLane(
      'storeOptions',
      setStoreLoadState,
      (signal) => appUpdatePolicyService.getPosIpadStoreOptions(signal),
      setStoreOptions,
      t('system.appDownloads.updatePolicy.storeOptionsLoadFailed'),
      () => setStoreOptions([]),
    ),
    [runLoadLane, t],
  )

  const refreshAll = useCallback(async () => {
    setHandheldRefreshVersion((version) => version + 1)
    setMobileOtaRefreshVersion((version) => version + 1)
    setMobileAndroidRefreshVersion((version) => version + 1)
    await Promise.allSettled([
      loadMobileNativeLane(),
      loadIpadNativeLane(),
      loadIpadOtaLane(),
      loadStoreOptionsLane(),
    ])
  }, [
    loadIpadNativeLane,
    loadIpadOtaLane,
    loadMobileNativeLane,
    loadStoreOptionsLane,
  ])

  useEffect(() => {
    void refreshAll()
    return () => {
      for (const key of Object.keys(laneRequestsRef.current) as LoadLaneKey[]) {
        laneRequestsRef.current[key].invalidate()
      }
    }
  }, [refreshAll])

  const storeOptionsUsable = storeLoadState.loaded
    && !storeLoadState.loading
    && !storeLoadState.failed
  const anyLaneLoading = mobileLoadState.loading
    || ipadLoadState.loading
    || otaLoadState.loading
    || storeLoadState.loading

  const mergedStoreOptions = useMemo(() => {
    const options = new Map(
      storeOptions.map((store) => [
        store.storeGuid,
        {
          label: `${store.storeCode} · ${store.storeName}`,
          value: store.storeGuid,
        },
      ]),
    )

    for (const storeGuid of [
      ...(ipadPolicy?.targetStoreGuids ?? []),
      ...(otaRollout?.targetStoreGuids ?? []),
    ]) {
      if (!options.has(storeGuid)) {
        options.set(storeGuid, { label: storeGuid, value: storeGuid })
      }
    }

    return [...options.values()]
  }, [ipadPolicy?.targetStoreGuids, otaRollout?.targetStoreGuids, storeOptions])

  function openRegisterModal(app: AppUpdateApp) {
    setRegisterApp(app)
    registerForm.setFieldsValue({
      appStoreId: '',
      buildNumber: '',
      storefront: 'au',
    })
  }

  function closeRegisterModal() {
    if (registerSaving) {
      return
    }
    setRegisterApp(null)
    registerForm.resetFields()
  }

  function handleRegisterRelease(values: AppStoreReleaseRegistrationFormValue) {
    if (!registerApp || registerSaving) {
      return
    }

    const app = registerApp
    const summary = buildAppStoreReleaseRegistrationSummary(values)
    Modal.confirm({
      title: t('system.appDownloads.updatePolicy.registerFinalConfirmTitle'),
      content: (
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Typography.Text>
            {t('system.appDownloads.updatePolicy.registerFinalConfirmDescription')}
          </Typography.Text>
          {app === 'pos-ipad' ? (
            <Alert
              type="warning"
              showIcon
              message={t('system.appDownloads.updatePolicy.ipadBuildNotVerifiedWarning')}
            />
          ) : null}
          {app === 'mobile-ios' ? (
            <Alert
              type="warning"
              showIcon
              message={t('system.appDownloads.updatePolicy.mobileBuildNotVerifiedWarning')}
            />
          ) : null}
          {app === 'pos-handheld' ? (
            <Alert
              type="warning"
              showIcon
              message={t(
                'system.appDownloads.updatePolicy.posHandheld.buildNotVerifiedWarning',
              )}
            />
          ) : null}
          <Descriptions size="small" bordered column={1}>
            <Descriptions.Item
              label={t('system.appDownloads.updatePolicy.registerTargetApp')}
            >
              {app === 'pos-ipad'
                ? t('system.appDownloads.updatePolicy.tabs.ipadNative')
                : app === 'pos-handheld'
                  ? t('system.appDownloads.updatePolicy.tabs.posHandheld')
                  : t('system.appDownloads.updatePolicy.tabs.mobile')}
            </Descriptions.Item>
            <Descriptions.Item
              label={t('system.appDownloads.updatePolicy.appStoreId')}
            >
              {summary.appStoreId}
            </Descriptions.Item>
            <Descriptions.Item
              label={t('system.appDownloads.updatePolicy.buildNumber')}
            >
              {summary.buildNumber}
            </Descriptions.Item>
            <Descriptions.Item
              label={t('system.appDownloads.updatePolicy.storefront')}
            >
              {summary.storefront.toUpperCase()}
            </Descriptions.Item>
          </Descriptions>
        </Space>
      ),
      okText: t('system.appDownloads.updatePolicy.verifyAndRegister'),
      cancelText: t('common.cancel'),
      width: 560,
      onOk: () => registerRelease(app, summary),
    })
  }

  async function registerRelease(
    app: AppUpdateApp,
    summary: AppStoreReleaseRegistrationSummary,
  ) {
    const isIpad = app === 'pos-ipad'
    const isHandheld = app === 'pos-handheld'
    if (!isHandheld) {
      invalidateLoadLane(
        isIpad ? 'ipadNative' : 'mobileNative',
        isIpad ? setIpadLoadState : setMobileLoadState,
      )
    }
    setRegisterSaving(true)
    try {
      await appUpdatePolicyService.createIosAppStoreRelease({
        app,
        ...summary,
      })
      message.success(t('system.appDownloads.updatePolicy.registerSuccess'))
      setRegisterApp(null)
      registerForm.resetFields()
      if (isHandheld) {
        setHandheldRefreshVersion((version) => version + 1)
      } else {
        await (isIpad ? loadIpadNativeLane() : loadMobileNativeLane())
      }
    } catch (error) {
      console.error('Failed to register App Store release', error)
      message.error(t('system.appDownloads.updatePolicy.registerFailed'))
    } finally {
      setRegisterSaving(false)
    }
  }

  function confirmPolicySave({
    kind,
    nativeApp,
    enabled,
    summary,
    releaseLabel,
    onOk,
  }: PolicySaveConfirmation) {
    const storeLabels = new Map(
      mergedStoreOptions.map((option) => [String(option.value), String(option.label)]),
    )
    const selectedStoreLabels = summary.targetStoreGuids.map(
      (storeGuid) => storeLabels.get(storeGuid) ?? storeGuid,
    )
    const scopeSummary = summary.targetScope === 'stores'
      ? (
          <Space direction="vertical" size={2}>
            <Typography.Text>
              {t('system.appDownloads.updatePolicy.confirmSelectedStores', {
                count: selectedStoreLabels.length,
              })}
            </Typography.Text>
            <Typography.Text
              type="secondary"
              style={{
                display: 'block',
                maxHeight: 96,
                overflowY: 'auto',
                overflowWrap: 'anywhere',
              }}
            >
              {selectedStoreLabels.join(', ')}
            </Typography.Text>
          </Space>
        )
      : t('system.appDownloads.updatePolicy.targetAll')
    const updateMode = kind === 'native'
      ? (
          summary.updateMode === 'required'
            ? t('system.appDownloads.updatePolicy.confirmNativeRequiredMode', {
                version: summary.minimumSupportedVersion,
              })
            : t('system.appDownloads.updatePolicy.confirmNativeOptionalMode')
        )
      : (
          summary.updateMode === 'required'
            ? t('system.appDownloads.updatePolicy.confirmOtaRequiredMode')
            : t('system.appDownloads.updatePolicy.confirmOtaOptionalMode')
        )

    Modal.confirm({
      title: enabled
        ? t(
            kind === 'native'
              ? 'system.appDownloads.updatePolicy.activateNativeConfirmTitle'
              : 'system.appDownloads.updatePolicy.activateOtaConfirmTitle',
          )
        : t('system.appDownloads.updatePolicy.disableConfirmTitle'),
      content: enabled
        ? (
            <Space direction="vertical" size={12} style={{ width: '100%' }}>
              <Typography.Text>
                {t(
                  kind === 'native'
                    ? 'system.appDownloads.updatePolicy.activateNativeConfirmDescription'
                    : 'system.appDownloads.updatePolicy.activateOtaConfirmDescription',
                )}
              </Typography.Text>
              {kind === 'native' && nativeApp === 'pos-ipad' ? (
                <Alert
                  type="warning"
                  showIcon
                  message={t(
                    'system.appDownloads.updatePolicy.ipadBuildNotVerifiedWarning',
                  )}
                  description={t(
                    'system.appDownloads.updatePolicy.ipadPolicyBuildConfirmDescription',
                  )}
                />
              ) : null}
              {kind === 'native' && nativeApp === 'mobile-ios' ? (
                <Alert
                  type="warning"
                  showIcon
                  message={t(
                    'system.appDownloads.updatePolicy.mobileBuildNotVerifiedWarning',
                  )}
                  description={t(
                    'system.appDownloads.updatePolicy.mobilePolicyBuildConfirmDescription',
                  )}
                />
              ) : null}
              <Descriptions size="small" bordered column={1}>
                <Descriptions.Item
                  label={t('system.appDownloads.updatePolicy.confirmRelease')}
                >
                  <Typography.Text style={{ overflowWrap: 'anywhere' }}>
                    {releaseLabel}
                  </Typography.Text>
                </Descriptions.Item>
                <Descriptions.Item
                  label={t('system.appDownloads.updatePolicy.confirmScope')}
                >
                  {scopeSummary}
                </Descriptions.Item>
                <Descriptions.Item
                  label={t('system.appDownloads.updatePolicy.confirmUpdateMode')}
                >
                  {updateMode}
                </Descriptions.Item>
                {kind === 'native' ? (
                  <>
                    <Descriptions.Item
                      label={t('system.appDownloads.updatePolicy.confirmMinimumVersion')}
                    >
                      {summary.minimumSupportedVersion ?? '--'}
                    </Descriptions.Item>
                    <Descriptions.Item
                      label={t('system.appDownloads.updatePolicy.confirmMinimumBuild')}
                    >
                      {summary.minimumSupportedBuildNumber ?? '--'}
                    </Descriptions.Item>
                  </>
                ) : null}
              </Descriptions>
            </Space>
          )
        : t('system.appDownloads.updatePolicy.disableConfirmDescription'),
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      okButtonProps: enabled ? undefined : { danger: true },
      width: 560,
      onOk,
    })
  }

  async function saveNativePolicy(
    app: AppUpdateApp,
    values: NativeUpdatePolicyFormValue,
    expectedPolicyVersion: number,
  ) {
    const isIpad = app === 'pos-ipad'
    const setSaving = isIpad ? setIpadSaving : setMobileSaving
    const setLoadState = isIpad ? setIpadLoadState : setMobileLoadState
    const loadLane = isIpad ? loadIpadNativeLane : loadMobileNativeLane
    if (
      isIpad
      && values.enabled
      && values.targetScope === 'stores'
      && !storeOptionsUsable
    ) {
      message.error(t('system.appDownloads.updatePolicy.storeOptionsSaveBlocked'))
      return
    }

    invalidateLoadLane(
      isIpad ? 'ipadNative' : 'mobileNative',
      setLoadState,
    )
    setSaving(true)
    try {
      const result = await savePolicyWithConflictReload(
        () => isIpad
          ? appUpdatePolicyService.savePosIpadNativePolicy(
              buildNativeUpdatePolicyRequest(values, true, expectedPolicyVersion),
            )
          : appUpdatePolicyService.saveMobileIosNativePolicy(
              buildNativeUpdatePolicyRequest(values, false, expectedPolicyVersion),
            ),
        loadLane,
        isAppUpdatePolicyVersionConflict,
      )
      if (result !== 'saved') {
        const messageKey = result === 'conflict-reloaded'
          ? 'system.appDownloads.updatePolicy.versionConflict'
          : result === 'conflict-reload-superseded'
            ? 'system.appDownloads.updatePolicy.versionConflictReloadSuperseded'
            : 'system.appDownloads.updatePolicy.versionConflictReloadFailed'
        message.warning(t(messageKey))
        return
      }

      message.success(t('system.appDownloads.updatePolicy.saveSuccess'))
      await loadLane()
    } catch (error) {
      console.error('Failed to save native update policy', error)
      message.error(t('system.appDownloads.updatePolicy.saveFailed'))
      throw error
    } finally {
      setSaving(false)
    }
  }

  function handleNativeFinish(
    app: AppUpdateApp,
    values: NativeUpdatePolicyFormValue,
  ) {
    const isIpad = app === 'pos-ipad'
    const currentPolicy = isIpad ? ipadPolicy : mobilePolicy
    if (!currentPolicy) {
      message.error(t('system.appDownloads.updatePolicy.loadFailed'))
      return
    }
    if (
      isIpad
      && values.enabled
      && values.targetScope === 'stores'
      && !storeOptionsUsable
    ) {
      message.error(t('system.appDownloads.updatePolicy.storeOptionsSaveBlocked'))
      return
    }

    const summary = buildNativePolicyConfirmationSummary(values, isIpad)
    const releases = isIpad ? ipadReleases : mobileReleases
    const release = releases.find((item) => item.id === summary.releaseId)
    confirmPolicySave({
      kind: 'native',
      nativeApp: app,
      enabled: values.enabled,
      summary,
      releaseLabel: release
        ? formatNativeReleaseLabel(release)
        : summary.releaseId ?? '--',
      onOk: () => saveNativePolicy(app, values, currentPolicy.policyVersion),
    })
  }

  async function saveOtaRollout(
    values: OtaRolloutFormValue,
    expectedPolicyVersion: number,
  ) {
    if (
      values.enabled
      && values.targetScope === 'stores'
      && !storeOptionsUsable
    ) {
      message.error(t('system.appDownloads.updatePolicy.storeOptionsSaveBlocked'))
      return
    }

    invalidateLoadLane('ipadOta', setOtaLoadState)
    setOtaSaving(true)
    try {
      const result = await savePolicyWithConflictReload(
        () => appUpdatePolicyService.savePosIpadOtaRollout(
          buildOtaRolloutRequest(values, expectedPolicyVersion),
        ),
        loadIpadOtaLane,
        isAppUpdatePolicyVersionConflict,
      )
      if (result !== 'saved') {
        const messageKey = result === 'conflict-reloaded'
          ? 'system.appDownloads.updatePolicy.versionConflict'
          : result === 'conflict-reload-superseded'
            ? 'system.appDownloads.updatePolicy.versionConflictReloadSuperseded'
            : 'system.appDownloads.updatePolicy.versionConflictReloadFailed'
        message.warning(t(messageKey))
        return
      }

      message.success(t('system.appDownloads.updatePolicy.saveSuccess'))
      await loadIpadOtaLane()
    } catch (error) {
      console.error('Failed to save iPad OTA rollout', error)
      message.error(t('system.appDownloads.updatePolicy.saveFailed'))
      throw error
    } finally {
      setOtaSaving(false)
    }
  }

  function handleOtaFinish(values: OtaRolloutFormValue) {
    if (!otaRollout) {
      message.error(t('system.appDownloads.updatePolicy.loadFailed'))
      return
    }
    if (
      values.enabled
      && values.targetScope === 'stores'
      && !storeOptionsUsable
    ) {
      message.error(t('system.appDownloads.updatePolicy.storeOptionsSaveBlocked'))
      return
    }

    const summary = buildOtaPolicyConfirmationSummary(values)
    const release = otaReleases.find((item) => item.id === summary.releaseId)
    confirmPolicySave({
      kind: 'ota',
      enabled: values.enabled,
      summary,
      releaseLabel: release
        ? formatOtaReleaseLabel(release)
        : summary.releaseId ?? '--',
      onOk: () => saveOtaRollout(values, otaRollout.policyVersion),
    })
  }

  function nativeReleaseColumns(
    policy: NativeUpdatePolicy,
    app: AppUpdateApp,
  ): ColumnsType<IosAppStoreRelease> {
    return [
      {
        title: t('system.appDownloads.updatePolicy.status'),
        key: 'status',
        width: 140,
        render: (_value, release) => {
          const status = resolveNativeReleaseStatus(
            release.id,
            policy.releaseId,
            policy.enabled,
          )
          return (
            <Tag color={status === 'active' ? 'green' : 'blue'}>
              {t(`system.appDownloads.updatePolicy.${status}`)}
            </Tag>
          )
        },
      },
      {
        title: t('system.appDownloads.updatePolicy.versionBuild'),
        key: 'version',
        width: 160,
        render: (_value, release) => formatNativeReleaseLabel(release),
      },
      {
        title: t('system.appDownloads.updatePolicy.bundle'),
        dataIndex: 'bundleIdentifier',
        width: 220,
        render: (value: string) => value || '--',
      },
      {
        title: t('system.appDownloads.updatePolicy.verifiedAt'),
        dataIndex: 'appleVerifiedAtUtc',
        width: 190,
        render: (value: string) => formatAppDownloadLocalDateTime(value),
      },
      {
        title: t('column.action'),
        key: 'action',
        width: app === 'mobile-ios' ? 300 : 150,
        render: (_value, release) => {
          const url = safeExternalUrl(
            release.appStoreUrl,
            ['apps.apple.com', 'itunes.apple.com'],
          )
          return (
            <Space size={4} wrap>
              <Button
                size="small"
                icon={<LinkOutlined />}
                disabled={!url}
                href={url ?? undefined}
                target="_blank"
                rel="noopener noreferrer"
              >
                {t('system.appDownloads.updatePolicy.openStore')}
              </Button>
              {app === 'mobile-ios' ? (
                <Button
                  size="small"
                  icon={<QrcodeOutlined />}
                  disabled={!url}
                  onClick={() => setQrRelease(release)}
                >
                  {t('system.appDownloads.updatePolicy.viewInstallQrCode')}
                </Button>
              ) : null}
            </Space>
          )
        },
      },
    ]
  }

  const otaReleaseColumns = useMemo<ColumnsType<PosIpadOtaRelease>>(
    () => [
      {
        title: t('system.appDownloads.updatePolicy.status'),
        key: 'status',
        width: 180,
        render: (_value, release) => {
          const status = resolveOtaReleaseStatus(
            release.id,
            otaRollout?.releaseId,
            otaRollout?.enabled ?? false,
          )
          return (
            <Space size={4} wrap>
              <Tag color={status === 'active' ? 'green' : 'blue'}>
                {t(`system.appDownloads.updatePolicy.${status}`)}
              </Tag>
              {release.isRollback ? (
                <Tag color="orange">
                  {t('system.appDownloads.updatePolicy.rollback')}
                </Tag>
              ) : null}
            </Space>
          )
        },
      },
      {
        title: t('system.appDownloads.updatePolicy.channel'),
        dataIndex: 'channel',
        width: 180,
      },
      {
        title: t('system.appDownloads.updatePolicy.environment'),
        dataIndex: 'environment',
        width: 130,
      },
      {
        title: t('system.appDownloads.updatePolicy.runtime'),
        dataIndex: 'runtimeVersion',
        width: 150,
      },
      {
        title: t('system.appDownloads.updatePolicy.iosUpdateId'),
        dataIndex: 'iosUpdateId',
        width: 260,
        render: (value: string) => (
          <Typography.Text copyable ellipsis={{ tooltip: value }} style={{ maxWidth: 235 }}>
            {value || '--'}
          </Typography.Text>
        ),
      },
      {
        title: t('system.appDownloads.updatePolicy.updateGroupId'),
        dataIndex: 'updateGroupId',
        width: 260,
        render: (value: string) => (
          <Typography.Text copyable ellipsis={{ tooltip: value }} style={{ maxWidth: 235 }}>
            {value || '--'}
          </Typography.Text>
        ),
      },
      {
        title: t('system.appDownloads.updatePolicy.publishedAt'),
        dataIndex: 'publishedAtUtc',
        width: 190,
        render: (value: string) => formatAppDownloadLocalDateTime(value),
      },
      {
        title: t('column.action'),
        key: 'action',
        width: 170,
        render: (_value, release) => {
          const url = safeExternalUrl(release.dashboardUrl, ['expo.dev'])
          return (
            <Button
              size="small"
              icon={<LinkOutlined />}
              disabled={!url}
              href={url ?? undefined}
              target="_blank"
              rel="noopener noreferrer"
            >
              {t('system.appDownloads.updatePolicy.openDashboard')}
            </Button>
          )
        },
      },
    ],
    [otaRollout?.enabled, otaRollout?.releaseId, t],
  )

  function renderLoadFailure(
    state: LoadLaneStatus,
    retry: () => Promise<'applied' | 'stale' | 'failed'>,
    messageKey: string,
  ) {
    return state.failed ? (
      <Alert
        type="error"
        showIcon
        message={t(messageKey)}
        action={
          <Button
            size="small"
            loading={state.loading}
            onClick={() => void retry()}
          >
            {t('system.appDownloads.updatePolicy.retry')}
          </Button>
        }
      />
    ) : null
  }

  function renderStoreOptionsFailure() {
    return storeLoadState.failed ? (
      <Alert
        type="warning"
        showIcon
        message={t('system.appDownloads.updatePolicy.storeOptionsLoadFailed')}
        description={t('system.appDownloads.updatePolicy.storeOptionsFallback')}
        action={
          <Button
            size="small"
            loading={storeLoadState.loading}
            onClick={() => void loadStoreOptionsLane()}
          >
            {t('system.appDownloads.updatePolicy.retry')}
          </Button>
        }
      />
    ) : null
  }

  function renderPolicyMetadata(
    policy: NativeUpdatePolicy | PosIpadOtaRollout,
    forceUpdate?: boolean,
  ) {
    return (
      <Space size={4} wrap>
        <Tag color={policy.enabled ? 'green' : 'default'}>
          {t(`system.appDownloads.updatePolicy.${policy.enabled ? 'enabled' : 'disabled'}`)}
        </Tag>
        <Tag>
          {t('system.appDownloads.updatePolicy.policyVersion')}: {policy.policyVersion}
        </Tag>
        {forceUpdate ? (
          <Tag color="red">{t('system.appDownloads.updatePolicy.forceUpdate')}</Tag>
        ) : null}
      </Space>
    )
  }

  function renderTargetFields(
    enabled: boolean,
    targetScope: string,
  ) {
    return (
      <>
        <Col xs={24} md={12}>
          <Form.Item
            name="targetScope"
            label={t('system.appDownloads.updatePolicy.targetScope')}
            rules={[{ required: enabled }]}
          >
            <Radio.Group
              disabled={!canManage || !enabled}
              options={[
                {
                  label: t('system.appDownloads.updatePolicy.targetAll'),
                  value: 'all',
                },
                {
                  label: t('system.appDownloads.updatePolicy.targetStores'),
                  value: 'stores',
                },
              ]}
            />
          </Form.Item>
        </Col>
        <Col xs={24} md={12}>
          <Form.Item
            name="targetStoreGuids"
            label={t('system.appDownloads.updatePolicy.selectStores')}
            dependencies={['enabled', 'targetScope']}
            rules={[
              ({ getFieldValue }) => ({
                validator: async (_rule, value?: string[]) => {
                  if (
                    getFieldValue('enabled')
                    && getFieldValue('targetScope') === 'stores'
                    && (!value || value.length === 0)
                  ) {
                    throw new Error(t('system.appDownloads.updatePolicy.storesRequired'))
                  }
                },
              }),
            ]}
          >
            <Select
              mode="multiple"
              allowClear
              showSearch
              loading={storeLoadState.loading}
              optionFilterProp="label"
              options={mergedStoreOptions}
              disabled={
                !canManage
                || !enabled
                || targetScope !== 'stores'
                || !storeOptionsUsable
              }
              placeholder={t('system.appDownloads.updatePolicy.selectStores')}
            />
          </Form.Item>
        </Col>
      </>
    )
  }

  function renderNativePolicyEditor(
    app: AppUpdateApp,
    policy: NativeUpdatePolicy,
    releases: IosAppStoreRelease[],
    form: FormInstance<NativeUpdatePolicyFormValue>,
    enabled: boolean,
    targetScope: string,
    saving: boolean,
    loadState: LoadLaneStatus,
  ) {
    const isIpad = app === 'pos-ipad'
    const supportsMinimumBuild = app === 'mobile-ios' || app === 'pos-ipad'
    const fieldColumn = 6
    const domainReady = loadState.loaded && !loadState.loading && !loadState.failed
    const storeScopeBlocked = isIpad
      && enabled
      && targetScope === 'stores'
      && !storeOptionsUsable
    return (
      <Card
        size="small"
        title={t('system.appDownloads.updatePolicy.policy')}
        extra={renderPolicyMetadata(policy)}
        loading={loadState.loading && !loadState.loaded}
      >
        <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 3 }} style={{ marginBottom: 16 }}>
          <Descriptions.Item label={t('system.appDownloads.updatePolicy.latestVersion')}>
            {policy.latestVersion || '--'}
          </Descriptions.Item>
          <Descriptions.Item label={t('system.appDownloads.updatePolicy.updatedAt')}>
            {formatAppDownloadLocalDateTime(policy.updatedAt)}
          </Descriptions.Item>
          <Descriptions.Item label={t('system.appDownloads.updatePolicy.updatedBy')}>
            {policy.updatedBy || '--'}
          </Descriptions.Item>
          {supportsMinimumBuild ? (
            <Descriptions.Item
              label={t('system.appDownloads.updatePolicy.minimumBuild')}
            >
              {policy.minimumSupportedBuildNumber ?? '--'}
            </Descriptions.Item>
          ) : null}
        </Descriptions>

        <Form<NativeUpdatePolicyFormValue>
          form={form}
          layout="vertical"
          disabled={!canManage}
          onFinish={(values) => handleNativeFinish(app, values)}
        >
          <Row gutter={[16, 0]}>
            <Col xs={24} md={fieldColumn}>
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
            <Col xs={24} md={fieldColumn}>
              <Form.Item
                name="releaseId"
                label={t('system.appDownloads.updatePolicy.release')}
                dependencies={['enabled']}
                rules={[
                  ({ getFieldValue }) => ({
                    validator: async (_rule, value?: string) => {
                      if (getFieldValue('enabled') && !value) {
                        throw new Error(t('system.appDownloads.updatePolicy.releaseRequired'))
                      }
                    },
                  }),
                ]}
              >
                <Select
                  showSearch
                  optionFilterProp="label"
                  disabled={!canManage || !enabled}
                  placeholder={t('system.appDownloads.updatePolicy.selectRelease')}
                  options={releases.map((release) => ({
                    label: formatNativeReleaseLabel(release),
                    value: release.id,
                  }))}
                />
              </Form.Item>
            </Col>
            <Col xs={24} md={fieldColumn}>
              <Form.Item
                name="minimumSupportedVersion"
                label={t('system.appDownloads.updatePolicy.minimumVersion')}
                extra={t('system.appDownloads.updatePolicy.minimumVersionHelp')}
              >
                <Input
                  disabled={!canManage || !enabled}
                  placeholder={t('system.appDownloads.updatePolicy.minimumVersionPlaceholder')}
                />
              </Form.Item>
            </Col>
            {supportsMinimumBuild ? (
              <Col xs={24} md={fieldColumn}>
                <Form.Item
                  name="minimumSupportedBuildNumber"
                  label={t('system.appDownloads.updatePolicy.minimumBuild')}
                  extra={t('system.appDownloads.updatePolicy.minimumBuildHelp')}
                  dependencies={['enabled', 'minimumSupportedVersion']}
                  rules={[
                    ({ getFieldValue }) => ({
                      validator: async (_rule, value?: number | null) => {
                        if (
                          getFieldValue('enabled')
                          && !validateMinimumSupportedBuildNumber(
                            getFieldValue('minimumSupportedVersion'),
                            value,
                          )
                        ) {
                          throw new Error(
                            t('system.appDownloads.updatePolicy.minimumBuildRequiresVersion'),
                          )
                        }
                      },
                    }),
                  ]}
                >
                  <InputNumber
                    min={0}
                    max={2_147_483_647}
                    precision={0}
                    style={{ width: '100%' }}
                    disabled={!canManage || !enabled}
                    placeholder={t(
                      'system.appDownloads.updatePolicy.minimumBuildPlaceholder',
                    )}
                  />
                </Form.Item>
              </Col>
            ) : null}
            {isIpad ? renderTargetFields(enabled, targetScope) : null}
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
              loading={saving}
              disabled={!domainReady || storeScopeBlocked}
            >
              {t('common.save')}
            </Button>
          ) : null}
        </Form>
      </Card>
    )
  }

  function renderNativeTab(
    app: AppUpdateApp,
    releases: IosAppStoreRelease[],
    policy: NativeUpdatePolicy,
    form: FormInstance<NativeUpdatePolicyFormValue>,
    enabled: boolean,
    targetScope: string,
    saving: boolean,
    loadState: LoadLaneStatus,
    retry: () => Promise<'applied' | 'stale' | 'failed'>,
  ) {
    const isIpad = app === 'pos-ipad'
    const domainReady = loadState.loaded && !loadState.loading && !loadState.failed
    return (
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        {renderLoadFailure(
          loadState,
          retry,
          isIpad
            ? 'system.appDownloads.updatePolicy.ipadNativeLoadFailed'
            : 'system.appDownloads.updatePolicy.mobileLoadFailed',
        )}
        {isIpad ? renderStoreOptionsFailure() : null}
        <Card
          size="small"
          title={t('system.appDownloads.updatePolicy.releaseFacts')}
          extra={
            canManage ? (
              <Button
                icon={<AppstoreAddOutlined />}
                disabled={!domainReady}
                onClick={() => openRegisterModal(app)}
              >
                {t('system.appDownloads.updatePolicy.registerRelease')}
              </Button>
            ) : null
          }
        >
          <Alert
            type="info"
            showIcon
            style={{ marginBottom: 12 }}
            message={t('system.appDownloads.updatePolicy.appleVerificationHint')}
            description={t('system.appDownloads.updatePolicy.registrationNotActivation')}
          />
          <MeasuredTable<IosAppStoreRelease>
            metricId="system.app-downloads.app-update-policy-panel.table-1"
            rowKey="id"
            size="small"
            columns={nativeReleaseColumns(policy, app)}
            dataSource={releases}
            loading={loadState.loading}
            scroll={{ x: app === 'mobile-ios' ? 1010 : 860 }}
            locale={{
              emptyText: <Empty description={t('system.appDownloads.updatePolicy.noReleases')} />,
            }}
            pagination={{ pageSize: 5, hideOnSinglePage: true }}
          />
        </Card>
        {renderNativePolicyEditor(
          app,
          policy,
          releases,
          form,
          enabled,
          targetScope,
          saving,
          loadState,
        )}
      </Space>
    )
  }

  const otaDomainReady = otaLoadState.loaded
    && !otaLoadState.loading
    && !otaLoadState.failed
  const otaStoreScopeBlocked = otaEnabled
    && otaTargetScope === 'stores'
    && !storeOptionsUsable

  const tabs = [
    {
      key: 'mobile-native',
      label: t('system.appDownloads.updatePolicy.tabs.mobile'),
      children: renderNativeTab(
        'mobile-ios',
        mobileReleases,
        mobilePolicy ?? EMPTY_NATIVE_POLICY,
        mobileForm,
        mobileEnabled,
        'all',
        mobileSaving,
        mobileLoadState,
        loadMobileNativeLane,
      ),
    },
    {
      // 安卓原生只按 versionCode 设最低支持构建号，独立组件自管加载与保存。
      key: 'mobile-android-native',
      label: t('system.appDownloads.updatePolicy.tabs.mobileAndroidNative'),
      children: (
        <MobileAndroidNativePolicyTab
          canManage={canManage}
          refreshVersion={mobileAndroidRefreshVersion}
        />
      ),
    },
    {
      key: 'mobile-ota',
      label: t('system.appDownloads.updatePolicy.tabs.mobileOta'),
      children: (
        <MobileOtaPolicyTab
          canManage={canManage}
          refreshVersion={mobileOtaRefreshVersion}
        />
      ),
    },
    {
      key: 'ipad-native',
      label: t('system.appDownloads.updatePolicy.tabs.ipadNative'),
      children: renderNativeTab(
        'pos-ipad',
        ipadReleases,
        ipadPolicy ?? EMPTY_NATIVE_POLICY,
        ipadForm,
        ipadEnabled,
        ipadTargetScope,
        ipadSaving,
        ipadLoadState,
        loadIpadNativeLane,
      ),
    },
    {
      key: 'ipad-ota',
      label: t('system.appDownloads.updatePolicy.tabs.ipadOta'),
      children: (
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          {renderLoadFailure(
            otaLoadState,
            loadIpadOtaLane,
            'system.appDownloads.updatePolicy.ipadOtaLoadFailed',
          )}
          {renderStoreOptionsFailure()}
          <Card size="small" title={t('system.appDownloads.updatePolicy.releaseFacts')}>
            <Alert
              type="info"
              showIcon
              style={{ marginBottom: 12 }}
              message={t('system.appDownloads.updatePolicy.otaScriptHint')}
            />
            <MeasuredTable<PosIpadOtaRelease>
              metricId="system.app-downloads.app-update-policy-panel.table-2"
              rowKey="id"
              size="small"
              columns={otaReleaseColumns}
              dataSource={otaReleases}
              loading={otaLoadState.loading}
              scroll={{ x: 1420 }}
              locale={{
                emptyText: <Empty description={t('system.appDownloads.updatePolicy.noReleases')} />,
              }}
              pagination={{ pageSize: 5, hideOnSinglePage: true }}
            />
          </Card>

          <Card
            size="small"
            title={t('system.appDownloads.updatePolicy.rollout')}
            extra={renderPolicyMetadata(
              otaRollout ?? EMPTY_OTA_ROLLOUT,
              otaRollout?.enabled && otaRollout.forceUpdate,
            )}
            loading={otaLoadState.loading && !otaLoadState.loaded}
          >
            <Descriptions
              size="small"
              column={{ xs: 1, sm: 2 }}
              style={{ marginBottom: 16 }}
            >
              <Descriptions.Item label={t('system.appDownloads.updatePolicy.updatedAt')}>
                {formatAppDownloadLocalDateTime(otaRollout?.updatedAt)}
              </Descriptions.Item>
              <Descriptions.Item label={t('system.appDownloads.updatePolicy.updatedBy')}>
                {otaRollout?.updatedBy || '--'}
              </Descriptions.Item>
            </Descriptions>
            <Form<OtaRolloutFormValue>
              form={otaForm}
              layout="vertical"
              disabled={!canManage}
              onFinish={handleOtaFinish}
            >
              <Row gutter={[16, 0]}>
                <Col xs={24} md={8}>
                  <Form.Item
                    name="enabled"
                    label={t('system.appDownloads.updatePolicy.rolloutStatus')}
                    valuePropName="checked"
                  >
                    <Switch
                      checkedChildren={t('system.appDownloads.updatePolicy.enabled')}
                      unCheckedChildren={t('system.appDownloads.updatePolicy.disabled')}
                    />
                  </Form.Item>
                </Col>
                <Col xs={24} md={8}>
                  <Form.Item
                    name="releaseId"
                    label={t('system.appDownloads.updatePolicy.release')}
                    dependencies={['enabled']}
                    rules={[
                      ({ getFieldValue }) => ({
                        validator: async (_rule, value?: string) => {
                          if (getFieldValue('enabled') && !value) {
                            throw new Error(t('system.appDownloads.updatePolicy.releaseRequired'))
                          }
                        },
                      }),
                    ]}
                  >
                    <Select
                      showSearch
                      optionFilterProp="label"
                      disabled={!canManage || !otaEnabled}
                      placeholder={t('system.appDownloads.updatePolicy.selectRelease')}
                      options={otaReleases.map((release) => ({
                        label: formatOtaReleaseLabel(release),
                        value: release.id,
                      }))}
                    />
                  </Form.Item>
                </Col>
                <Col xs={24} md={8}>
                  <Form.Item
                    name="forceUpdate"
                    label={t('system.appDownloads.updatePolicy.forceUpdate')}
                    valuePropName="checked"
                  >
                    <Switch disabled={!canManage || !otaEnabled} />
                  </Form.Item>
                </Col>
                {renderTargetFields(otaEnabled, otaTargetScope)}
                <Col span={24}>
                  <Form.Item
                    name="releaseMessage"
                    label={t('system.appDownloads.updatePolicy.releaseMessage')}
                  >
                    <Input.TextArea
                      rows={3}
                      maxLength={1000}
                      showCount
                      disabled={!canManage || !otaEnabled}
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
                  loading={otaSaving}
                  disabled={!otaDomainReady || otaStoreScopeBlocked}
                >
                  {t('common.save')}
                </Button>
              ) : null}
            </Form>
          </Card>
        </Space>
      ),
    },
    {
      key: 'pos-handheld',
      label: t('system.appDownloads.updatePolicy.tabs.posHandheld'),
      children: (
        <PosHandheldUpdatePolicyTab
          canManage={canManage}
          refreshVersion={handheldRefreshVersion}
          onRegisterIosRelease={() => openRegisterModal('pos-handheld')}
        />
      ),
    },
  ]

  return (
    <>
      <Card
        title={t('system.appDownloads.updatePolicy.title')}
        extra={
          <Button
            icon={<ReloadOutlined />}
            loading={anyLaneLoading}
            onClick={() => void refreshAll()}
          >
            {t('common.refresh')}
          </Button>
        }
      >
        <Typography.Paragraph type="secondary">
          {t('system.appDownloads.updatePolicy.subtitle')}
        </Typography.Paragraph>
        {!canManage ? (
          <Alert
            type="info"
            showIcon
            style={{ marginBottom: 12 }}
            message={t('system.appDownloads.updatePolicy.readOnly')}
          />
        ) : null}
        <Tabs items={tabs} destroyInactiveTabPane={false} />
      </Card>

      <Modal
        open={Boolean(qrRelease)}
        title={t('system.appDownloads.updatePolicy.appStoreQrCodeTitle')}
        footer={null}
        onCancel={() => setQrRelease(null)}
        destroyOnHidden
        width={420}
      >
        {qrRelease && qrReleaseUrl ? (
          <Space
            direction="vertical"
            size={16}
            style={{ width: '100%', alignItems: 'center' }}
          >
            {/* 四模块静区直接写入 SVG，确保手机相机在不同背景下稳定识别。 */}
            <QRCodeSVG
              value={qrReleaseUrl}
              size={280}
              marginSize={4}
              level="M"
              title={t('system.appDownloads.updatePolicy.appStoreQrCodeAccessibleTitle', {
                version: formatNativeReleaseLabel(qrRelease),
              })}
              style={{ display: 'block', width: '100%', maxWidth: 280, height: 'auto' }}
            />
            <Typography.Text strong>{formatNativeReleaseLabel(qrRelease)}</Typography.Text>
            <Typography.Text type="secondary" style={{ textAlign: 'center' }}>
              {t('system.appDownloads.updatePolicy.appStoreQrCodeHint')}
            </Typography.Text>
            <Typography.Text
              copyable={{ text: qrReleaseUrl }}
              style={{ maxWidth: '100%', textAlign: 'center', wordBreak: 'break-all' }}
            >
              {qrReleaseUrl}
            </Typography.Text>
          </Space>
        ) : null}
      </Modal>

      <Modal
        open={registerApp !== null}
        title={t('system.appDownloads.updatePolicy.registerTitle', {
          app: registerApp === 'pos-ipad'
            ? t('system.appDownloads.updatePolicy.tabs.ipadNative')
            : registerApp === 'pos-handheld'
              ? t('system.appDownloads.updatePolicy.tabs.posHandheld')
              : t('system.appDownloads.updatePolicy.tabs.mobile'),
        })}
        onCancel={closeRegisterModal}
        destroyOnHidden
        footer={[
          <Button key="cancel" disabled={registerSaving} onClick={closeRegisterModal}>
            {t('common.cancel')}
          </Button>,
          <Button
            key="register"
            type="primary"
            loading={registerSaving}
            onClick={() => registerForm.submit()}
          >
            {t('system.appDownloads.updatePolicy.verifyAndRegister')}
          </Button>,
        ]}
      >
        <Alert
          type="warning"
          showIcon
          style={{ marginBottom: 16 }}
          message={t('system.appDownloads.updatePolicy.registerConfirmDescription')}
        />
        {registerApp === 'pos-ipad' ? (
          <Alert
            type="warning"
            showIcon
            style={{ marginBottom: 16 }}
            message={t('system.appDownloads.updatePolicy.ipadBuildNotVerifiedWarning')}
            description={t(
              'system.appDownloads.updatePolicy.ipadBuildDoubleConfirmDescription',
            )}
          />
        ) : null}
        {registerApp === 'mobile-ios' ? (
          <Alert
            type="warning"
            showIcon
            style={{ marginBottom: 16 }}
            message={t('system.appDownloads.updatePolicy.mobileBuildNotVerifiedWarning')}
            description={t(
              'system.appDownloads.updatePolicy.mobileBuildDoubleConfirmDescription',
            )}
          />
        ) : null}
        {registerApp === 'pos-handheld' ? (
          <Alert
            type="warning"
            showIcon
            style={{ marginBottom: 16 }}
            message={t(
              'system.appDownloads.updatePolicy.posHandheld.buildNotVerifiedWarning',
            )}
            description={t(
              'system.appDownloads.updatePolicy.posHandheld.buildDoubleConfirmDescription',
            )}
          />
        ) : null}
        <Form<AppStoreReleaseRegistrationFormValue>
          form={registerForm}
          layout="vertical"
          initialValues={{ storefront: 'au' }}
          onFinish={handleRegisterRelease}
        >
          <Form.Item
            name="appStoreId"
            label={t('system.appDownloads.updatePolicy.appStoreId')}
            rules={[
              {
                validator: async (_rule, value?: string) => {
                  if (!value?.trim()) {
                    throw new Error(t('system.appDownloads.updatePolicy.appStoreIdRequired'))
                  }
                },
              },
              {
                pattern: /^\d{6,20}$/,
                message: t('system.appDownloads.updatePolicy.appStoreIdInvalid'),
              },
            ]}
          >
            <Input autoComplete="off" placeholder="1234567890" />
          </Form.Item>
          <Form.Item
            name="buildNumber"
            label={t('system.appDownloads.updatePolicy.buildNumber')}
            rules={[
              {
                validator: async (_rule, value?: string) => {
                  const normalized = value?.trim() ?? ''
                  if (!normalized) {
                    throw new Error(t('system.appDownloads.updatePolicy.buildNumberRequired'))
                  }
                  if (registerApp === 'pos-ipad' && !isValidPosIpadBuildNumber(normalized)) {
                    throw new Error(
                      t('system.appDownloads.updatePolicy.ipadBuildNumberInvalid'),
                    )
                  }
                  if (
                    registerApp === 'mobile-ios'
                    && !isValidMobileIosBuildNumber(normalized)
                  ) {
                    throw new Error(
                      t('system.appDownloads.updatePolicy.mobileBuildNumberInvalid'),
                    )
                  }
                  if (
                    registerApp === 'pos-handheld'
                    && !isValidPosHandheldBuildNumber(normalized)
                  ) {
                    throw new Error(
                      t('system.appDownloads.updatePolicy.posHandheld.buildNumberInvalid'),
                    )
                  }
                  if (
                    registerApp !== 'pos-ipad'
                    && registerApp !== 'pos-handheld'
                    && registerApp !== 'mobile-ios'
                    && !/^[0-9A-Za-z._-]{1,64}$/.test(normalized)
                  ) {
                    throw new Error(
                      t('system.appDownloads.updatePolicy.buildNumberInvalid'),
                    )
                  }
                },
              },
            ]}
          >
            <Input
              autoComplete="off"
              inputMode={
                registerApp === 'mobile-ios'
                || registerApp === 'pos-ipad'
                || registerApp === 'pos-handheld'
                  ? 'numeric'
                  : undefined
              }
            />
          </Form.Item>
          <Form.Item
            name="storefront"
            label={t('system.appDownloads.updatePolicy.storefront')}
            rules={[
              { required: true, message: t('system.appDownloads.updatePolicy.storefrontRequired') },
              {
                pattern: /^[A-Za-z]{2}$/,
                message: t('system.appDownloads.updatePolicy.storefrontInvalid'),
              },
            ]}
          >
            <Select options={[{ label: 'Australia (au)', value: 'au' }]} />
          </Form.Item>
        </Form>
      </Modal>
    </>
  )
}
