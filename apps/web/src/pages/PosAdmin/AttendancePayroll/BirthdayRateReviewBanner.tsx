import { Button } from 'antd'
import dayjs from 'dayjs'
import { useTranslation } from 'react-i18next'
import type { BirthdayRateReview } from './payrollLogic'

interface BirthdayRateReviewBannerProps {
  reviews: readonly BirthdayRateReview[]
  /** 点「去核对」跳到个人时薪表；没有查看时薪权限时不传，只提示不给按钮。 */
  onOpenRates?: () => void
}

/**
 * 「生日后时薪还没按新年龄更新」待办条：工时统计、工资表顶部共用，每人一条。
 * 待办来自个人时薪表接口的 birthdayReviewHint，时薪一改好后端就不再返回，条自然消失；这里不提供「知道了」，避免待办被点掉后没人再看。
 */
export default function BirthdayRateReviewBanner({ reviews, onOpenRates }: BirthdayRateReviewBannerProps) {
  const { t } = useTranslation()
  if (!reviews.length) return null
  return (
    <>
      {reviews.map((review) => (
        <div key={`${review.userGuid}|${review.date}`} className="atp-alert atp-alert-birthday">
          <div className="atp-alert-text">
            {t('attendancePayroll.birthdayTodo.text', {
              name: review.userName,
              date: dayjs(review.date).format('MM-DD'),
              age: review.age,
            })}
          </div>
          {onOpenRates ? (
            <Button size="small" type="primary" onClick={onOpenRates}>{t('attendancePayroll.birthdayTodo.open')}</Button>
          ) : null}
        </div>
      ))}
    </>
  )
}
