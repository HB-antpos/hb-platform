import { Segmented } from 'antd'
import { useTranslation } from 'react-i18next'
import { MANUAL_ENTRY_MODES, type ManualEntryMode } from './manualEntryLogic'

interface ManualEntryModeSwitchProps {
  value: ManualEntryMode
  onChange: (mode: ManualEntryMode) => void
  disabled?: boolean
}

/** 补录抽屉顶部的模式切换：补工时 / 带薪病假 / 带薪年假 / 年假发放（两个抽屉共用，切换时由外层换抽屉）。 */
export default function ManualEntryModeSwitch({ value, onChange, disabled }: ManualEntryModeSwitchProps) {
  const { t } = useTranslation()
  return (
    <Segmented
      block
      className="atp-me-mode"
      value={value}
      disabled={disabled}
      onChange={(next) => onChange(next as ManualEntryMode)}
      options={MANUAL_ENTRY_MODES.map((mode) => ({ value: mode, label: t(`attendancePayroll.manualLeave.mode.${mode}`) }))}
    />
  )
}
