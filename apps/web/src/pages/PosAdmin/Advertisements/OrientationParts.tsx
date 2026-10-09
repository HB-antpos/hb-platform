import { WarningOutlined } from '@ant-design/icons'
import { Alert, Segmented, Space, Tag, Typography, theme } from 'antd'
import type { CSSProperties, ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import type { AdvertisementMediaType, AdvertisementOrientation } from '../../../types/advertisement'
import {
  ADVERTISEMENT_SLOTS,
  evaluateSlotFit,
  formatAspectRatio,
  getOrientationMismatch,
  isSlotPlayed,
  toMediaSize,
  type AdvertisementSlot,
  type OrientationMismatch,
} from './orientation'

type TranslateFn = ReturnType<typeof useTranslation>['t']

export const ADVERTISEMENT_ORIENTATION_VALUES: readonly AdvertisementOrientation[] = ['Landscape', 'Portrait', 'Any']

// 以下文案键都写成字面量，便于 pageMessagesContract 测试静态核对键名是否存在。
export function getOrientationLabel(t: TranslateFn, orientation: AdvertisementOrientation) {
  if (orientation === 'Landscape') return t('posAdmin.advertisements.orientations.landscape')
  if (orientation === 'Portrait') return t('posAdmin.advertisements.orientations.portrait')
  return t('posAdmin.advertisements.orientations.any')
}

function getOrientationPlacement(t: TranslateFn, orientation: AdvertisementOrientation) {
  if (orientation === 'Landscape') return t('posAdmin.advertisements.orientationPlacements.landscape')
  if (orientation === 'Portrait') return t('posAdmin.advertisements.orientationPlacements.portrait')
  return t('posAdmin.advertisements.orientationPlacements.any')
}

function getSlotLabel(t: TranslateFn, slot: AdvertisementSlot) {
  return slot === 'idle'
    ? t('posAdmin.advertisements.slots.idle')
    : t('posAdmin.advertisements.slots.checkout')
}

function getLetterboxLabel(t: TranslateFn, letterbox: 'sides' | 'topBottom') {
  return letterbox === 'sides'
    ? t('posAdmin.advertisements.slotLetterbox.sides')
    : t('posAdmin.advertisements.slotLetterbox.topBottom')
}

/** 列表里的简短不适配提示：「两处都会留边」「空闲全屏会两侧留边」等。 */
function getMismatchShortText(t: TranslateFn, mismatch: OrientationMismatch) {
  if (mismatch.kind === 'both') return t('posAdmin.advertisements.mismatchShort.both')
  if (mismatch.slot === 'idle') {
    return mismatch.letterbox === 'sides'
      ? t('posAdmin.advertisements.mismatchShort.idleSides')
      : t('posAdmin.advertisements.mismatchShort.idleTopBottom')
  }
  return mismatch.letterbox === 'sides'
    ? t('posAdmin.advertisements.mismatchShort.checkoutSides')
    : t('posAdmin.advertisements.mismatchShort.checkoutTopBottom')
}

/** 版式对应的比例小矩形尺寸（px）：横版扁、竖版高、通用接近方形。 */
const SHAPE_SIZE: Readonly<Record<AdvertisementOrientation, [number, number]>> = {
  Landscape: [14, 8],
  Portrait: [9, 12],
  Any: [11, 9],
}

export function OrientationShapeIcon({ orientation }: { orientation: AdvertisementOrientation }) {
  const [width, height] = SHAPE_SIZE[orientation]
  return (
    <span
      aria-hidden
      style={{
        display: 'inline-block',
        width,
        height,
        border: '1.5px solid currentColor',
        borderRadius: 2,
        boxSizing: 'border-box',
        verticalAlign: 'middle',
      }}
    />
  )
}

const ORIENTATION_TAG_COLOR: Readonly<Record<AdvertisementOrientation, string>> = {
  Landscape: 'blue',
  Portrait: 'purple',
  Any: 'default',
}

/** 列表「版式」列：彩色标签 + 「宽×高 · 播放位置」+ 必要时的黄色留边提示。 */
export function OrientationCell({
  orientation,
  mediaWidth,
  mediaHeight,
}: {
  orientation: AdvertisementOrientation
  mediaWidth?: number | null
  mediaHeight?: number | null
}) {
  const { t } = useTranslation()
  const size = toMediaSize(mediaWidth, mediaHeight)
  const mismatch = getOrientationMismatch(orientation, mediaWidth, mediaHeight)
  const sizeText = size ? `${size.width}×${size.height}` : t('posAdmin.advertisements.sizeUnknown')

  return (
    <Space direction="vertical" size={2}>
      <Tag
        color={ORIENTATION_TAG_COLOR[orientation]}
        icon={<OrientationShapeIcon orientation={orientation} />}
        style={{ marginInlineEnd: 0, display: 'inline-flex', alignItems: 'center', gap: 6 }}
      >
        {getOrientationLabel(t, orientation)}
      </Tag>
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {sizeText} · {getOrientationPlacement(t, orientation)}
      </Typography.Text>
      {mismatch ? (
        <Typography.Text type="warning" style={{ fontSize: 12 }}>
          <WarningOutlined />{' '}
          {t('posAdmin.advertisements.mismatchWithRatio', {
            ratio: formatAspectRatio(mediaWidth, mediaHeight),
            text: getMismatchShortText(t, mismatch),
          })}
        </Typography.Text>
      ) : null}
    </Space>
  )
}

/**
 * 编辑弹窗里的版式选择：Form.Item 注入 value / onChange。
 * 未选择时必须给 Segmented 传 null（受控空值）；传 undefined 会被当成非受控而默认高亮第一项，
 * 看起来已选「横版」但表单值仍为空。
 */
export function OrientationSegmented({
  value,
  onChange,
  onUserChange,
  disabled,
}: {
  value?: AdvertisementOrientation
  onChange?: (value: AdvertisementOrientation) => void
  /** 只在管理员手动点选时触发，用于去掉「已按尺寸自动选择」标签。 */
  onUserChange?: () => void
  disabled?: boolean
}) {
  const { t } = useTranslation()
  return (
    <Segmented
      disabled={disabled}
      aria-label={t('posAdmin.advertisements.orientation')}
      value={(value ?? null) as unknown as AdvertisementOrientation}
      onChange={(nextValue) => {
        onChange?.(nextValue as AdvertisementOrientation)
        onUserChange?.()
      }}
      options={ADVERTISEMENT_ORIENTATION_VALUES.map((orientation) => ({
        value: orientation,
        label: (
          <Space size={6}>
            <OrientationShapeIcon orientation={orientation} />
            {getOrientationLabel(t, orientation)}
          </Space>
        ),
      }))}
    />
  )
}

/** 三种版式的播放位置与建议尺寸说明。 */
export function OrientationHelp() {
  const { t } = useTranslation()
  const rows: [AdvertisementOrientation, string][] = [
    ['Landscape', t('posAdmin.advertisements.orientationHelp.landscape')],
    ['Portrait', t('posAdmin.advertisements.orientationHelp.portrait')],
    ['Any', t('posAdmin.advertisements.orientationHelp.any')],
  ]
  return (
    <div style={{ fontSize: 12, lineHeight: 1.8 }}>
      {rows.map(([orientation, text]) => (
        <div key={orientation}>
          <Typography.Text strong style={{ fontSize: 12 }}>
            {getOrientationLabel(t, orientation)}
          </Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {t('posAdmin.advertisements.labelSeparator')}
            {text}
          </Typography.Text>
        </div>
      ))}
    </div>
  )
}

/** 版式与素材比例不符时的黄色提示：只提示、不拦截保存。 */
export function OrientationMismatchAlert({
  orientation,
  mediaWidth,
  mediaHeight,
}: {
  orientation?: AdvertisementOrientation
  mediaWidth?: number | null
  mediaHeight?: number | null
}) {
  const { t } = useTranslation()
  const size = toMediaSize(mediaWidth, mediaHeight)
  const mismatch = orientation ? getOrientationMismatch(orientation, mediaWidth, mediaHeight) : null
  if (!orientation || !size || !mismatch) return null

  const shape =
    size.width > size.height
      ? t('posAdmin.advertisements.mediaShapes.landscape')
      : size.width < size.height
        ? t('posAdmin.advertisements.mediaShapes.portrait')
        : t('posAdmin.advertisements.mediaShapes.square')
  const slot =
    mismatch.kind === 'both' ? t('posAdmin.advertisements.slots.both') : getSlotLabel(t, mismatch.slot)
  const effect =
    mismatch.kind === 'both'
      ? t('posAdmin.advertisements.mismatchEffects.both')
      : mismatch.letterbox === 'sides'
        ? t('posAdmin.advertisements.mismatchEffects.sides')
        : t('posAdmin.advertisements.mismatchEffects.topBottom')

  return (
    <Alert
      type="warning"
      showIcon
      message={t('posAdmin.advertisements.editorMismatch', {
        shape,
        size: `${size.width}×${size.height}`,
        orientation: getOrientationLabel(t, orientation),
        slot,
        effect,
      })}
    />
  )
}

/** 客显深色底色（与 WPF 客显广告位背景一致）。 */
const DISPLAY_AREA_BACKGROUND = '#101B2D'
const DISPLAY_BEZEL_BACKGROUND = '#09111F'
/** 两个预览框共用的广告区高度（px），宽度按真实广告位比例推算。 */
const PREVIEW_AREA_HEIGHT = 132

interface PreviewMedia {
  mediaType?: AdvertisementMediaType
  mediaUrl?: string
  posterUrl?: string
  mediaWidth?: number | null
  mediaHeight?: number | null
}

function PreviewMediaElement({ mediaType, mediaUrl, posterUrl }: PreviewMedia) {
  const style: CSSProperties = { width: '100%', height: '100%', objectFit: 'contain', display: 'block' }
  if (!mediaUrl) return null
  if (mediaType === 'Video') {
    // 静音循环播放，模拟客显实际效果；不显示控件，避免占据小预览框。
    return <video src={mediaUrl} poster={posterUrl || undefined} muted loop autoPlay playsInline preload="metadata" style={style} />
  }
  return <img src={mediaUrl} alt="" style={style} />
}

function SlotFrame({
  slot,
  orientation,
  media,
}: {
  slot: AdvertisementSlot
  orientation?: AdvertisementOrientation
  media: PreviewMedia
}) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const slotSize = ADVERTISEMENT_SLOTS[slot]
  const areaWidth = Math.round((PREVIEW_AREA_HEIGHT * slotSize.width) / slotSize.height)
  // 未选版式时不判断是否播放，只展示素材放进广告位的样子。
  const played = orientation ? isSlotPlayed(orientation, slot) : null
  const fit = evaluateSlotFit(media.mediaWidth, media.mediaHeight, slot)

  let status: ReactNode = null
  if (played === false) {
    status = <span style={{ color: token.colorTextQuaternary, fontWeight: 600 }}>{t('posAdmin.advertisements.slotNotPlayed')}</span>
  } else if (played === true && fit && !fit.fits && fit.letterbox) {
    status = (
      <span style={{ color: token.colorWarning, fontWeight: 600 }}>
        <WarningOutlined /> {getLetterboxLabel(t, fit.letterbox)}
      </span>
    )
  } else if (played === true) {
    status = <span style={{ color: token.colorSuccess, fontWeight: 600 }}>{t('posAdmin.advertisements.slotPlays')}</span>
  }

  return (
    <div style={{ width: areaWidth + 12 }}>
      <div style={{ background: DISPLAY_BEZEL_BACKGROUND, borderRadius: 8, padding: 6 }}>
        <div
          style={{
            position: 'relative',
            width: areaWidth,
            height: PREVIEW_AREA_HEIGHT,
            background: DISPLAY_AREA_BACKGROUND,
            borderRadius: 5,
            overflow: 'hidden',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
          }}
        >
          {media.mediaUrl ? (
            <PreviewMediaElement {...media} />
          ) : (
            <span style={{ color: 'rgba(255,255,255,0.45)', fontSize: 12, padding: 8, textAlign: 'center' }}>
              {t('posAdmin.advertisements.previewEmpty')}
            </span>
          )}
          {played === false && orientation ? (
            <div
              style={{
                position: 'absolute',
                inset: 0,
                background: 'rgba(9,17,31,0.72)',
                color: 'rgba(255,255,255,0.78)',
                fontSize: 12,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                textAlign: 'center',
                padding: 8,
              }}
            >
              {t('posAdmin.advertisements.slotNotPlayedOverlay', {
                orientation: getOrientationLabel(t, orientation),
                slot: getSlotLabel(t, slot),
              })}
            </div>
          ) : null}
        </div>
      </div>
      <div
        style={{
          marginTop: 6,
          fontSize: 12,
          display: 'flex',
          flexWrap: 'wrap',
          justifyContent: 'space-between',
          gap: 4,
        }}
      >
        <span>
          {getSlotLabel(t, slot)} · {slotSize.width}×{slotSize.height}
        </span>
        {status}
      </div>
    </div>
  )
}

/** 客显效果预览：两个按真实广告位比例画的小屏框，素材等比缩放不裁切，随版式实时变化。 */
export function DisplayPreview({ orientation, ...media }: PreviewMedia & { orientation?: AdvertisementOrientation }) {
  const { t } = useTranslation()
  return (
    <div>
      <Space size={8} wrap style={{ marginBottom: 8, alignItems: 'baseline' }}>
        <Typography.Text strong>{t('posAdmin.advertisements.displayPreview')}</Typography.Text>
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {t('posAdmin.advertisements.displayPreviewHint')}
        </Typography.Text>
      </Space>
      <div style={{ display: 'flex', gap: 16, alignItems: 'flex-end', flexWrap: 'wrap' }}>
        <SlotFrame slot="idle" orientation={orientation} media={media} />
        <SlotFrame slot="checkout" orientation={orientation} media={media} />
      </div>
    </div>
  )
}
