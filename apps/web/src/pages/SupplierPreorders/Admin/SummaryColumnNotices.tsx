// 工具条下方的两条提示：① 有已开启分店没显示时的「分店列」说明（合计不受影响）；② 刚开启分店后的蓝色提示条。
import { CloseOutlined } from '@ant-design/icons'
import { Button } from 'antd'
import { Trans, useTranslation } from 'react-i18next'

import './registerSpAdminMessages'

/** 提示条里最多列出几家未显示分店的名字，其余用省略号 */
const MAX_NAMES = 4

interface HiddenColumnsHintProps {
  shownCount: number
  hiddenCount: number
  hiddenNames: string[]
  /** 未显示的分店是否都还没下单（决定措辞：「还没下单」还是「未显示」） */
  allWithoutOrder: boolean
  /** 未开启的分店数；候选还没请求过时为 null，不显示这一段 */
  offCount: number | null
  onManage: () => void
}

/** 没有隐藏列时不渲染 */
export function HiddenColumnsHint({ shownCount, hiddenCount, hiddenNames, allWithoutOrder, offCount, onManage }: HiddenColumnsHintProps) {
  const { t } = useTranslation()
  if (hiddenCount <= 0) return null
  const names = hiddenNames.slice(0, MAX_NAMES).join(t('spAdmin.summary.listSeparator')) + (hiddenNames.length > MAX_NAMES ? '…' : '')
  return (
    <div className="sp-admin-sum-hint sp-admin-number">
      <span>
        <Trans
          i18nKey={allWithoutOrder ? 'spAdmin.summary.hintNoOrder' : 'spAdmin.summary.hintHidden'}
          values={{ shown: shownCount, hidden: hiddenCount, names }}
          components={{ b: <b /> }}
        />
        {offCount !== null && offCount > 0 ? (
          <>
            {' · '}
            <Trans i18nKey="spAdmin.summary.hintOff" values={{ count: offCount }} components={{ b: <b /> }} />
          </>
        ) : null}
      </span>
      <Button type="link" size="small" onClick={onManage}>
        {t('spAdmin.summary.hintManage')}
      </Button>
      <span className="sp-admin-toolbar-spacer" />
      <span className="sp-admin-sum-hint-note">{t('spAdmin.summary.hintTotalsNote')}</span>
    </div>
  )
}

interface FreshStoreBannerProps {
  storeName: string
  /** 当前筛选不是「全部」时才提示「只看已下单的 N 款」并给出切换按钮 */
  orderedCount: number | null
  onShowAll: () => void
  onClose: () => void
}

export function FreshStoreBanner({ storeName, orderedCount, onShowAll, onClose }: FreshStoreBannerProps) {
  const { t } = useTranslation()
  return (
    <div className="sp-admin-sum-banner" role="status">
      <span>
        <Trans i18nKey="spAdmin.summary.bannerOpened" values={{ store: storeName }} components={{ b: <b /> }} />
        {orderedCount !== null ? (
          <>
            {' '}
            <Trans i18nKey="spAdmin.summary.bannerOnlyOrdered" values={{ count: orderedCount }} components={{ b: <b /> }} />
          </>
        ) : null}
      </span>
      {orderedCount !== null ? (
        <Button size="small" onClick={onShowAll}>
          {t('spAdmin.summary.bannerShowAll')}
        </Button>
      ) : null}
      <button type="button" className="sp-admin-sum-banner-x" onClick={onClose} aria-label={t('spAdmin.summary.bannerClose')}>
        <CloseOutlined />
      </button>
    </div>
  )
}
