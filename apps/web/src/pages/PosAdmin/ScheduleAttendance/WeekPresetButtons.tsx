import { Button, Space } from 'antd'
import dayjs, { type Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'

import './registerScheduleAttendanceMessages'
import { WEEK_PRESETS, WEEK_PRESET_LABEL_KEYS, activeWeekPreset, weekPresetRange } from './weekPresetLogic'

interface WeekPresetButtonsProps {
  value: [Dayjs, Dayjs] | null
  onChange: (range: [Dayjs, Dayjs]) => void
}

/** 「上周 / 本周 / 下周」：选中时把日期范围设为那一整周（周一到周日）；当前范围恰好是其中一周时高亮。 */
export default function WeekPresetButtons({ value, onChange }: WeekPresetButtonsProps) {
  const { t } = useTranslation()
  const today = dayjs().format('YYYY-MM-DD')
  const active = activeWeekPreset(value?.[0]?.format('YYYY-MM-DD'), value?.[1]?.format('YYYY-MM-DD'), today)
  return (
    <Space.Compact role="group" aria-label={t('posAdmin.scheduleAttendance.weekNav.presetsAria')}>
      {WEEK_PRESETS.map((preset) => (
        <Button
          key={preset}
          type={active === preset ? 'primary' : 'default'}
          aria-pressed={active === preset}
          onClick={() => {
            const range = weekPresetRange(preset, today)
            if (range) onChange([dayjs(range[0]), dayjs(range[1])])
          }}
        >
          {t(WEEK_PRESET_LABEL_KEYS[preset])}
        </Button>
      ))}
    </Space.Compact>
  )
}
