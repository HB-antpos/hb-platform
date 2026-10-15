import { LeftOutlined, PictureOutlined, RightOutlined } from '@ant-design/icons'
import { Alert, Button, Descriptions, Drawer, Space, Spin, Tag, Timeline, Tooltip, Typography, theme } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import { fetchFaceCaptureResultImage, fetchFaceHitDetail, fetchUnwelcomeReferenceImage } from '../../../services/faceMatchService'
import type { FaceCaptureResult, FaceCaptureResultHit, FaceHitDetail } from '../../../types/faceMatch'
import { formatDateTime } from '../FaceCameras/logic'
import {
  RESULT_IMAGE_ERROR_KEYS,
  REVIEW_IMAGE_ERROR_KEYS,
  createReferenceImageSession,
  createResultImageSession,
  referenceSlot,
  type ResultImageSlot,
  type ResultImageStates,
  type ReviewImageSession,
  type ReviewImageStates,
} from './imageLogic'
import {
  BAND_COLORS,
  BAND_LABEL_KEYS,
  HIT_NOTIFICATION_TITLE,
  REVIEW_STATUS_COLORS,
  REVIEW_STATUS_LABEL_KEYS,
  TARGET_NOTE_KEYS,
  buildHitNotificationBody,
  buildNotifyTimeline,
  canGoReview,
  formatResultPercent,
  resolveResultDecision,
  resolveTargetAreaView,
  stepTargetIndex,
  summarizeRecipients,
} from './logic'
import { NUMERIC_STYLE } from './shared'
import { useRemote } from './useRemote'

interface ResultDetailDrawerProps {
  /** 要查看的抓拍快照。父组件只在点击「查看」后才挂载本组件，并以 captureId 作 key：切换抓拍即重新挂载。 */
  result: FaceCaptureResult
  /** 能否查看名单目标图：等于有「复核任务」页签权限（目标图走复核任务的参考照片接口，只认复核权限）。 */
  canViewTarget: boolean
  /** 切到复核任务页签；没有复核任务页签权限时为 undefined（按钮禁用）。 */
  onGoReview?: () => void
  onClose: () => void
}

/**
 * 图片槽位的显示状态：抓拍图会话（RESULT_IMAGE_*）与名单目标图会话（REVIEW_IMAGE_*）的状态都符合这个形状，
 * 失败分类到文案键的映射由调用方传入（两套分类各有各的文案）。
 */
type ImageViewState =
  | { status: 'loading' }
  | { status: 'loaded'; url: string }
  | { status: 'error'; kind: string }

interface ResultImageViewProps {
  /** 没有图片编号（后端没给）时为 false：直接显示「没有这张图片」，不发请求。 */
  hasImage: boolean
  state: ImageViewState | undefined
  alt: string
  broken: boolean
  onBroken: () => void
  /** 失败分类 → 文案键；必须含 failed（字节拿到但浏览器解码失败时用）。 */
  errorKeys: Readonly<Record<string, string>>
}

/** 单张图片：<img src> 永远是 state 里的 objectURL，不是后端地址。 */
function ResultImageView({ hasImage, state, alt, broken, onBroken, errorKeys }: ResultImageViewProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const frame = { minHeight: 160, display: 'flex', alignItems: 'center', justifyContent: 'center', borderRadius: token.borderRadius, background: token.colorFillQuaternary } as const
  if (!hasImage) {
    return (
      <div style={frame}>
        <Space direction="vertical" size={4} align="center" style={{ color: token.colorTextTertiary }}>
          <PictureOutlined style={{ fontSize: 28 }} />
          <Typography.Text type="secondary">{t('faceMatch.results.drawer.noImage')}</Typography.Text>
        </Space>
      </div>
    )
  }
  if (!state || state.status === 'loading') {
    return (
      <div style={frame}>
        <Spin tip={t('faceMatch.results.drawer.loadingImage')}>
          <div style={{ padding: 24 }} />
        </Spin>
      </div>
    )
  }
  if (state.status === 'error' || broken) {
    const key = state.status === 'error' ? (errorKeys[state.kind] ?? errorKeys.failed) : errorKeys.failed
    return <Alert type="error" showIcon message={t(key)} />
  }
  return (
    <div style={{ ...frame, lineHeight: 0 }}>
      <img
        src={state.url}
        alt={alt}
        draggable={false}
        style={{ display: 'block', width: '100%', maxHeight: 260, objectFit: 'contain' }}
        onError={onBroken}
      />
    </div>
  )
}

function SectionTitle({ text }: { text: string }) {
  const { token } = theme.useToken()
  return (
    <Typography.Title level={5} style={{ margin: '4px 0 0', paddingInlineStart: 8, borderInlineStart: `3px solid ${token.colorPrimary}`, fontSize: 14 }}>
      {text}
    </Typography.Title>
  )
}

interface TargetPhotoBlockProps {
  taskId: string
  /** 名单条目参考照片张数（来自命中详情，大于 0 才会挂载本组件）。 */
  count: number
  /** 抓拍人脸小图（与抽屉顶部同一份 objectURL 的另一处视图，不另发请求）：放在目标图旁并排对照。 */
  faceCrop: ReactNode
}

/**
 * 名单目标图（敏感生物识别信息）：与抓拍人脸小图并排对照，多张时可切换。只在「有复核权限且详情给出张数大于 0」时才挂载。
 * 图片读取复用复核任务的参考照片接口（每次查看服务端写审计），会话复用复核弹框的生命周期：
 * 挂载后只取第 1 张，切换到哪张才取哪张（不预取）；卸载 / 切换抓拍时 effect 清理函数 dispose 会话：中止在途请求并撤销全部 objectURL。
 */
function TargetPhotoBlock({ taskId, count, faceCrop }: TargetPhotoBlockProps) {
  const { t } = useTranslation()
  const [states, setStates] = useState<ReviewImageStates>(() => new Map())
  // 字节已拿到但浏览器解码失败（内容损坏）的图片槽位。
  const [broken, setBroken] = useState<ReadonlySet<string>>(() => new Set())
  const [index, setIndex] = useState(0)
  const sessionRef = useRef<ReviewImageSession | null>(null)

  useEffect(() => {
    const session = createReferenceImageSession({
      referenceCount: count,
      fetchReference: (referenceIndex, signal) => fetchUnwelcomeReferenceImage(taskId, referenceIndex, signal),
      onChange: setStates,
    })
    sessionRef.current = session
    void session.start()
    // 关闭 / 切换抓拍 / 卸载（含 React 严格模式的重复挂载）：中止在途请求并 revoke 全部 objectURL。
    return () => {
      session.dispose()
      sessionRef.current = null
    }
  }, [taskId, count])

  const current = Math.min(index, count - 1)
  const slot = referenceSlot(current)

  const go = (delta: -1 | 1) => {
    const next = stepTargetIndex(current, delta, count)
    setIndex(next)
    void sessionRef.current?.loadReference(next)
  }

  const caption = (text: string) => (
    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
      {text}
    </Typography.Text>
  )

  return (
    <div>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: 12 }}>
        <div style={{ minWidth: 0 }}>
          {faceCrop}
          <div style={{ marginTop: 4 }}>{caption(t('faceMatch.results.drawer.faceImage'))}</div>
        </div>
        <div style={{ minWidth: 0 }}>
          <ResultImageView
            // 切换目标图时换 key：旧图卸载，不残留上一张的解码失败状态。
            key={slot}
            hasImage
            state={states.get(slot)}
            alt={t('faceMatch.results.drawer.targetImage')}
            broken={broken.has(slot)}
            onBroken={() => setBroken((currentBroken) => new Set(currentBroken).add(slot))}
            errorKeys={REVIEW_IMAGE_ERROR_KEYS}
          />
          <div style={{ marginTop: 4, display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: '4px 8px' }}>
            <div style={{ flex: '1 1 auto', minWidth: 0 }}>
              {caption(t('faceMatch.results.drawer.targetImage'))}
              {count > 1 ? (
                <Typography.Text type="secondary" style={{ fontSize: 12, ...NUMERIC_STYLE }}>
                  {' · '}
                  {t('faceMatch.results.drawer.targetPosition', { index: current + 1, total: count })}
                </Typography.Text>
              ) : null}
            </div>
            {count > 1 ? (
              <Space size={4}>
                <Button size="small" icon={<LeftOutlined />} aria-label={t('faceMatch.results.drawer.targetPrev')} disabled={current <= 0} onClick={() => go(-1)} />
                <Button size="small" icon={<RightOutlined />} aria-label={t('faceMatch.results.drawer.targetNext')} disabled={current >= count - 1} onClick={() => go(1)} />
              </Space>
            ) : null}
          </div>
        </div>
      </div>
    </div>
  )
}

interface RecipientListProps {
  status: 'loading' | 'error' | 'ready'
  detail: FaceHitDetail | null
}

/**
 * 命中通知的接收人：姓名 + 已读 / 未读 + 已读时间。姓名只来自详情接口的 displayName（后端已按「全名 → 用户名 → -」回退），
 * 不显示、不保存任何账号标识。读取中 / 失败时各显示一行次要文字（失败静默，不弹错、不影响抽屉其余内容）。
 */
function RecipientList({ status, detail }: RecipientListProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  if (status === 'loading') {
    return <Typography.Text type="secondary">{t('faceMatch.results.drawer.recipients.loading')}</Typography.Text>
  }
  if (status === 'error' || !detail) {
    return <Typography.Text type="secondary">{t('faceMatch.results.drawer.recipients.failed')}</Typography.Text>
  }
  const summary = summarizeRecipients(detail.recipients)
  return (
    <div>
      <Space size={8} wrap style={{ marginBottom: 4 }}>
        <Typography.Text strong style={{ fontSize: 12 }}>{t('faceMatch.results.drawer.recipients.title')}</Typography.Text>
        {summary.total > 0 ? (
          <Typography.Text type="secondary" style={{ fontSize: 12, ...NUMERIC_STYLE }}>
            {t('faceMatch.results.drawer.recipients.summary', { read: summary.read, total: summary.total })}
          </Typography.Text>
        ) : null}
        {detail.notificationRevoked ? <Tag color="warning" style={{ marginInlineEnd: 0 }}>{t('faceMatch.results.drawer.recipients.revoked')}</Tag> : null}
      </Space>
      {summary.total === 0 ? (
        <Typography.Text type="secondary" style={{ display: 'block' }}>{t('faceMatch.results.drawer.recipients.empty')}</Typography.Text>
      ) : (
        <ul style={{ margin: 0, padding: 0, listStyle: 'none' }}>
          {detail.recipients.map((recipient, position) => (
            <li
              key={position}
              style={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: '4px 8px', padding: '6px 0', borderBottom: `1px solid ${token.colorBorderSecondary}` }}
            >
              {/* 长姓名省略号（完整姓名放在 title）；状态区在窄屏换行而不是撑出抽屉。 */}
              <span
                title={recipient.displayName}
                style={{ flex: '1 1 120px', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
              >
                {recipient.displayName}
              </span>
              <Space size={8} style={{ flex: 'none' }}>
                <Tag color={recipient.read ? 'success' : 'default'} style={{ marginInlineEnd: 0 }}>
                  {recipient.read ? t('faceMatch.results.drawer.recipients.read') : t('faceMatch.results.drawer.recipients.unread')}
                </Tag>
                {recipient.read && recipient.readAtUtc ? (
                  <Typography.Text type="secondary" style={{ fontSize: 12, ...NUMERIC_STYLE }}>
                    {formatDateTime(recipient.readAtUtc)}
                  </Typography.Text>
                ) : null}
              </Space>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

interface HitSectionsProps {
  hit: FaceCaptureResultHit
  canViewTarget: boolean
  faceCrop: ReactNode
}

/**
 * 「不受欢迎人员名单命中」区下半部：名单目标图（与人脸小图并排）、相似度与复核状态、通知时间线、固定英文通知文案、接收人。
 * 只在这条抓拍有命中时挂载；挂载后只发一次命中详情请求（接收人 + 目标图张数），卸载 / 切换抓拍即中止。
 */
function HitSections({ hit, canViewTarget, faceCrop }: HitSectionsProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const taskId = hit.taskId
  const loader = useCallback((signal: AbortSignal) => fetchFaceHitDetail(taskId, signal), [taskId])
  const remote = useRemote<FaceHitDetail>(loader)
  const detail = remote.data
  const detailStatus = detail ? 'ready' : remote.error ? 'error' : 'loading'
  const timeline = useMemo(() => buildNotifyTimeline(hit), [hit])

  // 没有复核权限一律不读目标图；有权限也要详情给出张数大于 0 才读。
  const targetPhotoCount = detail?.targetPhotoCount ?? 0
  const targetView = resolveTargetAreaView({ canViewTarget, status: detailStatus, targetPhotoCount })

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      {targetView === 'photos' ? (
        <TargetPhotoBlock taskId={taskId} count={targetPhotoCount} faceCrop={faceCrop} />
      ) : (
        <Typography.Text type="secondary">{t(TARGET_NOTE_KEYS[targetView])}</Typography.Text>
      )}

      <Descriptions column={1} size="small" colon={false} labelStyle={{ width: 110 }}>
        <Descriptions.Item label={t('faceMatch.results.drawer.fields.similarity')}>
          <Space size={6} wrap>
            <Typography.Text strong style={NUMERIC_STYLE}>{formatResultPercent(hit.scorePercent)}</Typography.Text>
            {hit.band ? <Tag color={BAND_COLORS[hit.band]} style={{ marginInlineEnd: 0 }}>{t(BAND_LABEL_KEYS[hit.band])}</Tag> : null}
            <Typography.Text type="secondary">{t('faceMatch.results.drawer.notProbability')}</Typography.Text>
          </Space>
        </Descriptions.Item>
        <Descriptions.Item label={t('faceMatch.results.drawer.fields.reviewStatus')}>
          <Space size={6} wrap>
            <Tag color={REVIEW_STATUS_COLORS[hit.reviewStatus] ?? 'default'} style={{ marginInlineEnd: 0 }}>
              {REVIEW_STATUS_LABEL_KEYS[hit.reviewStatus] ? t(REVIEW_STATUS_LABEL_KEYS[hit.reviewStatus]) : hit.reviewStatusName}
            </Tag>
            {!hit.imageAvailable ? <Tag style={{ marginInlineEnd: 0 }}>{t('faceMatch.results.hitImageGone')}</Tag> : null}
          </Space>
        </Descriptions.Item>
      </Descriptions>

      <SectionTitle text={t('faceMatch.results.drawer.sections.notify')} />
      <Timeline
        style={{ marginBottom: -8 }}
        items={timeline.map((step) => ({
          color: step.done ? 'blue' : 'gray',
          children: (
            <div>
              <div>{t(step.labelKey, step.params)}</div>
              {step.atUtc || step.detailKey ? (
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {[step.atUtc ? formatDateTime(step.atUtc) : null, step.detailKey ? t(step.detailKey) : null].filter(Boolean).join(' · ')}
                </Typography.Text>
              ) : null}
            </div>
          ),
        }))}
      />
      {/* 通知文案是通知平台发出的固定英文原文（不含姓名与照片），中英文界面都原样展示，不翻译。 */}
      {hit.notifyState !== 'skipped' ? (
        <div style={{ padding: '10px 12px', borderRadius: token.borderRadius, background: token.colorFillQuaternary, border: `1px solid ${token.colorBorderSecondary}` }}>
          <Typography.Text strong style={{ fontSize: 12 }}>{t('faceMatch.results.drawer.notifyTextTitle')}</Typography.Text>
          <div lang="en" style={{ marginTop: 4, color: token.colorTextSecondary }}>
            <div>{HIT_NOTIFICATION_TITLE}</div>
            <div>{buildHitNotificationBody(hit.scorePercent)}</div>
          </div>
        </div>
      ) : null}
      <RecipientList status={detailStatus} detail={detail} />
    </Space>
  )
}

/**
 * 抓拍详情抽屉（敏感生物识别信息）：人脸小图 + 背景图并排，抓拍信息、比对结果、名单命中与通知发送情况。
 * 约定（由 sourceContract / imageLogic 测试守住）：
 * 1. 挂载（用户点击「查看」）后才请求图片，先人脸小图再背景图；列表里没有任何真实照片，也没有预取；
 * 2. 图片只以 Blob → objectURL 的形式存在于 state 里；关闭 / 切换 / 卸载时 effect 清理函数调用 session.dispose()；
 * 3. 通知时间线只显示人数与已读数；收件人姓名只来自命中详情接口的 displayName，且只显示「姓名 + 已读状态（+ 已读时间）」，
 *    不显示、不保存任何账号标识（用户编号、用户名、邮箱）；通知文案是固定英文原文（中英文界面一致）；
 *    名单目标图只在有复核权限（canViewTarget）且详情给出张数大于 0 时才读取，复用复核任务的参考照片接口（每次查看服务端写审计）；
 * 4. 相似度只写「不等于是同一个人的概率」；「去复核」只是切到复核任务页签，是否能判断仍由复核页的后端 canReview 决定。
 */
export default function ResultDetailDrawer({ result, canViewTarget, onGoReview, onClose }: ResultDetailDrawerProps) {
  const { t } = useTranslation()
  const [states, setStates] = useState<ResultImageStates>(() => new Map())
  // 字节已拿到但浏览器解码失败（内容损坏）的图片槽位。
  const [broken, setBroken] = useState<ReadonlySet<ResultImageSlot>>(() => new Set())

  const captureId = result.captureId
  const faceImageId = result.faceImageId
  const sceneImageId = result.sceneImageId

  useEffect(() => {
    const session = createResultImageSession({
      fetchers: {
        ...(faceImageId ? { face: (signal: AbortSignal) => fetchFaceCaptureResultImage(captureId, faceImageId, signal) } : {}),
        ...(sceneImageId ? { scene: (signal: AbortSignal) => fetchFaceCaptureResultImage(captureId, sceneImageId, signal) } : {}),
      },
      onChange: setStates,
    })
    void session.start()
    // 关闭 / 切换抓拍 / 卸载（含 React 严格模式的重复挂载）：中止在途请求并 revoke 全部 objectURL。
    return () => session.dispose()
  }, [captureId, faceImageId, sceneImageId])

  const markBroken = (slot: ResultImageSlot) => setBroken((current) => new Set(current).add(slot))

  const decision = resolveResultDecision(result)
  const hit = result.hit
  const storeText = `${result.storeCode}${result.storeName ? ` ${result.storeName}` : ''}`
  const reviewable = canGoReview(result)

  // 人脸小图视图：抽屉顶部与「名单目标图」旁各放一处，共用同一份 objectURL，不另发请求。
  const faceView = (
    <ResultImageView
      hasImage={faceImageId !== null}
      state={states.get('face')}
      alt={t('faceMatch.results.drawer.faceImage')}
      broken={broken.has('face')}
      onBroken={() => markBroken('face')}
      errorKeys={RESULT_IMAGE_ERROR_KEYS}
    />
  )

  const goReviewButton =
    hit !== null ? (
      <Tooltip title={!reviewable ? t('faceMatch.results.drawer.goReviewImageGone') : !onGoReview ? t('faceMatch.results.drawer.goReviewNoAccess') : undefined}>
        <span>
          <Button type="primary" disabled={!reviewable || !onGoReview} onClick={onGoReview}>
            {t('faceMatch.results.drawer.goReview')}
          </Button>
        </span>
      </Tooltip>
    ) : null

  return (
    <Drawer
      open
      width={560}
      destroyOnHidden
      title={t('faceMatch.results.drawer.title', { store: storeText })}
      onClose={onClose}
      footer={
        <Space style={{ width: '100%', justifyContent: 'flex-end' }}>
          <Button onClick={onClose}>{t('faceMatch.results.drawer.close')}</Button>
          {goReviewButton}
        </Space>
      }
    >
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        <Alert type="warning" showIcon message={t('faceMatch.results.drawer.sensitiveNotice')} />

        <div>
          <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 2.2fr)', gap: 12 }}>
            {faceView}
            <ResultImageView
              hasImage={sceneImageId !== null}
              state={states.get('scene')}
              alt={t('faceMatch.results.drawer.sceneImage')}
              broken={broken.has('scene')}
              onBroken={() => markBroken('scene')}
              errorKeys={RESULT_IMAGE_ERROR_KEYS}
            />
          </div>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {t('faceMatch.results.drawer.imageCaption')}
          </Typography.Text>
        </div>

        <SectionTitle text={t('faceMatch.results.drawer.sections.capture')} />
        <Descriptions column={1} size="small" colon={false} labelStyle={{ width: 110 }}>
          <Descriptions.Item label={t('faceMatch.results.drawer.fields.capturedAt')}>
            <span style={NUMERIC_STYLE}>{formatDateTime(result.capturedAtUtc)}</span>
          </Descriptions.Item>
          <Descriptions.Item label={t('faceMatch.results.drawer.fields.camera')}>{result.cameraId || '-'}</Descriptions.Item>
          <Descriptions.Item label={t('faceMatch.results.drawer.fields.faceCount')}>
            <span style={NUMERIC_STYLE}>{result.faceCount}</span>
          </Descriptions.Item>
          <Descriptions.Item label={t('faceMatch.results.drawer.fields.duplicate')}>
            {result.isDuplicate ? t('faceMatch.results.drawer.yes') : t('faceMatch.results.drawer.no')}
          </Descriptions.Item>
        </Descriptions>

        <SectionTitle text={t('faceMatch.results.drawer.sections.compare')} />
        <Descriptions column={1} size="small" colon={false} labelStyle={{ width: 110 }}>
          <Descriptions.Item label={t('faceMatch.results.drawer.fields.decision')}>
            <Tag color={decision.color} style={{ marginInlineEnd: 0 }}>{t(decision.labelKey)}</Tag>
          </Descriptions.Item>
          <Descriptions.Item label={t('faceMatch.results.drawer.fields.staffScore')}>
            <span style={NUMERIC_STYLE}>{formatResultPercent(result.staffScorePercent)}</span>
          </Descriptions.Item>
          <Descriptions.Item label={t('faceMatch.results.drawer.fields.visitScore')}>
            <span style={NUMERIC_STYLE}>{formatResultPercent(result.visitScorePercent)}</span>
          </Descriptions.Item>
        </Descriptions>
        <Typography.Text type="secondary" style={{ fontSize: 12, marginTop: -8 }}>
          {t('faceMatch.results.drawer.scoreNote')}
        </Typography.Text>

        <SectionTitle text={t('faceMatch.results.drawer.sections.hit')} />
        {hit ? (
          <HitSections hit={hit} canViewTarget={canViewTarget} faceCrop={faceView} />
        ) : (
          <Typography.Text type="secondary">{t('faceMatch.results.drawer.noHit')}</Typography.Text>
        )}
      </Space>
    </Drawer>
  )
}
