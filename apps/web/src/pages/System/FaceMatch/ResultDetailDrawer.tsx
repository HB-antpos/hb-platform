import { PictureOutlined } from '@ant-design/icons'
import { Alert, Button, Descriptions, Drawer, Space, Spin, Tag, Timeline, Tooltip, Typography, theme } from 'antd'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { fetchFaceCaptureResultImage } from '../../../services/faceMatchService'
import type { FaceCaptureResult } from '../../../types/faceMatch'
import { formatDateTime } from '../FaceCameras/logic'
import {
  RESULT_IMAGE_ERROR_KEYS,
  createResultImageSession,
  type ResultImageSlot,
  type ResultImageState,
  type ResultImageStates,
} from './imageLogic'
import {
  BAND_COLORS,
  BAND_LABEL_KEYS,
  HIT_NOTIFICATION_TITLE,
  REVIEW_STATUS_COLORS,
  REVIEW_STATUS_LABEL_KEYS,
  buildHitNotificationBody,
  buildNotifyTimeline,
  canGoReview,
  formatResultPercent,
  resolveResultDecision,
} from './logic'
import { NUMERIC_STYLE } from './shared'

interface ResultDetailDrawerProps {
  /** 要查看的抓拍快照。父组件只在点击「查看」后才挂载本组件，并以 captureId 作 key：切换抓拍即重新挂载。 */
  result: FaceCaptureResult
  /** 切到复核任务页签；没有复核任务页签权限时为 undefined（按钮禁用）。 */
  onGoReview?: () => void
  onClose: () => void
}

interface ResultImageViewProps {
  /** 没有图片编号（后端没给）时为 false：直接显示「没有这张图片」，不发请求。 */
  hasImage: boolean
  state: ResultImageState | undefined
  alt: string
  broken: boolean
  onBroken: () => void
}

/** 单张图片：<img src> 永远是 state 里的 objectURL，不是后端地址。 */
function ResultImageView({ hasImage, state, alt, broken, onBroken }: ResultImageViewProps) {
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
    const key = state.status === 'error' ? RESULT_IMAGE_ERROR_KEYS[state.kind] : RESULT_IMAGE_ERROR_KEYS.failed
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

/**
 * 抓拍详情抽屉（敏感生物识别信息）：人脸小图 + 背景图并排，抓拍信息、比对结果、名单命中与通知发送情况。
 * 约定（由 sourceContract / imageLogic 测试守住）：
 * 1. 挂载（用户点击「查看」）后才请求图片，先人脸小图再背景图；列表里没有任何真实照片，也没有预取；
 * 2. 图片只以 Blob → objectURL 的形式存在于 state 里；关闭 / 切换 / 卸载时 effect 清理函数调用 session.dispose()；
 * 3. 通知只显示人数与已读数，不显示收件人姓名；通知文案是固定英文原文（中英文界面一致）；
 * 4. 相似度只写「不等于是同一个人的概率」；「去复核」只是切到复核任务页签，是否能判断仍由复核页的后端 canReview 决定。
 */
export default function ResultDetailDrawer({ result, onGoReview, onClose }: ResultDetailDrawerProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
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
  const timeline = useMemo(() => (hit ? buildNotifyTimeline(hit) : []), [hit])
  const storeText = `${result.storeCode}${result.storeName ? ` ${result.storeName}` : ''}`
  const reviewable = canGoReview(result)

  const sectionTitle = (text: string) => (
    <Typography.Title level={5} style={{ margin: '4px 0 0', paddingInlineStart: 8, borderInlineStart: `3px solid ${token.colorPrimary}`, fontSize: 14 }}>
      {text}
    </Typography.Title>
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
            <ResultImageView
              hasImage={faceImageId !== null}
              state={states.get('face')}
              alt={t('faceMatch.results.drawer.faceImage')}
              broken={broken.has('face')}
              onBroken={() => markBroken('face')}
            />
            <ResultImageView
              hasImage={sceneImageId !== null}
              state={states.get('scene')}
              alt={t('faceMatch.results.drawer.sceneImage')}
              broken={broken.has('scene')}
              onBroken={() => markBroken('scene')}
            />
          </div>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {t('faceMatch.results.drawer.imageCaption')}
          </Typography.Text>
        </div>

        {sectionTitle(t('faceMatch.results.drawer.sections.capture'))}
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

        {sectionTitle(t('faceMatch.results.drawer.sections.compare'))}
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

        {sectionTitle(t('faceMatch.results.drawer.sections.hit'))}
        {hit ? (
          <>
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

            {sectionTitle(t('faceMatch.results.drawer.sections.notify'))}
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
          </>
        ) : (
          <Typography.Text type="secondary">{t('faceMatch.results.drawer.noHit')}</Typography.Text>
        )}
      </Space>
    </Drawer>
  )
}
