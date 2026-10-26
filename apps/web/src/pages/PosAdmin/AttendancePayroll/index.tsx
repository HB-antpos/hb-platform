import {
  DownloadOutlined,
  LeftOutlined,
  LockOutlined,
  RightOutlined,
  SearchOutlined,
  UnlockOutlined,
} from '@ant-design/icons'
import { Button, Input, Modal, Segmented, Select, Space, Tabs, Tag, Tooltip, Typography, message } from 'antd'
import dayjs from 'dayjs'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  getAttendancePayrollHours,
  getAttendancePayrollPay,
  getAttendancePayRates,
  submitAttendancePayPeriod,
  unlockAttendancePayPeriod,
} from '../../../services/attendancePayrollService'
import { useAuthStore } from '../../../store/auth'
import type {
  AttendanceHoursResultDto,
  AttendancePayRateDto,
  AttendancePayResultDto,
} from '../../../types/attendancePayroll'
import messagesEn from './attendancePayrollMessages.en.json'
import messagesZh from './attendancePayrollMessages.zh.json'
import HoursTab from './HoursTab'
import PayTab from './PayTab'
import RatesTab from './RatesTab'
import { exportPayrollWorkbook } from './payrollExport'
import { isoWeekNumber, periodEnd, shiftPeriod } from './payrollLogic'
import { useAttendanceStores } from './useAttendanceStores'
import './attendancePayroll.css'

registerPageMessages({ zh: messagesZh, en: messagesEn })

type PayrollTabKey = 'hours' | 'pay' | 'rates'

export default function AttendancePayrollPage() {
  const { t } = useTranslation()
  const access = useAuthStore((state) => state.access)
  const canViewHours = access.canViewAttendancePunches
  const canViewPay = access.canViewAttendancePayroll
  const canEdit = access.canEditAttendancePayroll
  const canUnlock = access.canUnlockAttendancePayroll
  const { storeOptions, storeCode, setStoreCode } = useAttendanceStores(true)
  const [activeTab, setActiveTab] = useState<PayrollTabKey>(canViewHours ? 'hours' : 'pay')
  // 期以后端为准：首次不传期起点 / 周数，后端返回今天所在期（周数取设置默认、两周按双周起始日对齐）；
  // 翻期在返回的起点上 ± 周数 × 7 天，切周数时把当前起点连同新周数发过去由后端重新对齐。
  // 写接口（提交、解锁、备注、核定）都用后端返回的 periodStart，避免 INVALID_PERIOD。
  const [requested, setRequested] = useState<{ periodStart?: string; weeks?: 1 | 2 }>({})
  const [resolvedPeriod, setResolvedPeriod] = useState<{ periodStart: string; weeks: 1 | 2 } | null>(null)
  const [includePending, setIncludePending] = useState(false)
  const [mergeStores, setMergeStores] = useState(false)
  const [hours, setHours] = useState<AttendanceHoursResultDto | null>(null)
  const [pay, setPay] = useState<AttendancePayResultDto | null>(null)
  const [rates, setRates] = useState<AttendancePayRateDto[]>([])
  const [rateKeyword, setRateKeyword] = useState('')
  const [loading, setLoading] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [unlockOpen, setUnlockOpen] = useState(false)
  const [unlockReason, setUnlockReason] = useState('')

  const periodQuery = useMemo(
    () => (storeCode ? { storeCode, ...requested, includePending, mergeStores } : null),
    [includePending, mergeStores, requested, storeCode],
  )

  const loadHours = useCallback(async () => {
    if (!periodQuery || !canViewHours) return
    setLoading(true)
    try {
      const result = await getAttendancePayrollHours(periodQuery)
      setHours(result)
      setResolvedPeriod({ periodStart: result.periodStart, weeks: result.weeks })
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.messages.loadFailed'))
    } finally {
      setLoading(false)
    }
  }, [canViewHours, periodQuery, t])

  const loadPay = useCallback(async () => {
    if (!periodQuery || !canViewPay) return
    setLoading(true)
    try {
      const result = await getAttendancePayrollPay(periodQuery)
      setPay(result)
      setResolvedPeriod({ periodStart: result.periodStart, weeks: result.weeks })
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.messages.loadFailed'))
    } finally {
      setLoading(false)
    }
  }, [canViewPay, periodQuery, t])

  const loadRates = useCallback(async () => {
    if (!storeCode || !canViewPay) return
    setLoading(true)
    try {
      setRates(await getAttendancePayRates({ storeCode, keyword: rateKeyword || undefined }))
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.messages.loadFailed'))
    } finally {
      setLoading(false)
    }
  }, [canViewPay, rateKeyword, storeCode, t])

  useEffect(() => {
    if (activeTab === 'hours') void loadHours()
    if (activeTab === 'pay') void loadPay()
    if (activeTab === 'rates') {
      void loadRates()
      // 时薪抽屉要用本期应付工时做实时试算
      void loadPay()
    }
  }, [activeTab, loadHours, loadPay, loadRates])

  const reloadAll = useCallback(() => {
    if (activeTab === 'hours') void loadHours()
    else void loadPay()
    if (activeTab === 'rates') void loadRates()
  }, [activeTab, loadHours, loadPay, loadRates])

  const weeks = resolvedPeriod?.weeks ?? requested.weeks ?? 1
  const periodStart = resolvedPeriod?.periodStart ?? requested.periodStart
  const changeWeeks = (value: 1 | 2) => setRequested({ periodStart, weeks: value })
  const shiftRequested = (direction: -1 | 1) => {
    if (periodStart) setRequested({ periodStart: shiftPeriod(periodStart, weeks, direction), weeks })
  }

  const status = (activeTab === 'hours' ? hours : pay) ?? hours ?? pay
  const pendingCount = status?.pendingCount ?? 0
  const isSubmitted = status?.periodStatus === 'Submitted'
  const periodLabel = useMemo(() => {
    if (!periodStart) return '…'
    const firstWeek = isoWeekNumber(periodStart)
    const weekText = weeks === 2
      ? t('attendancePayroll.period.weeksRange', { from: firstWeek, to: isoWeekNumber(shiftPeriod(periodStart, 1, 1)) })
      : t('attendancePayroll.period.week', { week: firstWeek })
    return `${weekText} · ${dayjs(periodStart).format('MM-DD')} ~ ${dayjs(periodEnd(periodStart, weeks)).format('MM-DD')}`
  }, [periodStart, t, weeks])

  const handleSubmit = () => {
    if (!storeCode || !periodStart) return
    Modal.confirm({
      title: t('attendancePayroll.submit.confirmTitle'),
      content: t('attendancePayroll.submit.confirmContent', { period: periodLabel }),
      okText: t('attendancePayroll.submit.button'),
      onOk: async () => {
        setSubmitting(true)
        try {
          await submitAttendancePayPeriod({ storeCode, periodStart, weeks })
          message.success(t('attendancePayroll.submit.done'))
          reloadAll()
        } catch (error) {
          message.error(error instanceof Error ? error.message : t('attendancePayroll.submit.failed'))
        } finally {
          setSubmitting(false)
        }
      },
    })
  }

  const handleUnlock = async () => {
    if (!storeCode || !periodStart || !unlockReason.trim()) return
    setSubmitting(true)
    try {
      await unlockAttendancePayPeriod({ storeCode, periodStart, weeks, reason: unlockReason.trim() })
      message.success(t('attendancePayroll.unlock.done'))
      setUnlockOpen(false)
      setUnlockReason('')
      reloadAll()
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.unlock.failed'))
    } finally {
      setSubmitting(false)
    }
  }

  const handleExport = async () => {
    const storeName = storeOptions.find((option) => option.value === storeCode)?.label ?? storeCode ?? ''
    if (!periodStart && activeTab !== 'rates') return
    try {
      await exportPayrollWorkbook({
        tab: activeTab,
        storeName,
        periodStart: periodStart ?? '',
        weeks,
        hours,
        pay,
        rates,
        text: (key, values) => t(key, values),
      })
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.messages.exportFailed'))
    }
  }

  const periodControls = activeTab !== 'rates' ? (
    <>
      <Segmented
        value={weeks}
        options={[
          { label: t('attendancePayroll.period.oneWeek'), value: 1 },
          { label: t('attendancePayroll.period.twoWeeks'), value: 2 },
        ]}
        onChange={(value) => changeWeeks(value as 1 | 2)}
      />
      <Space.Compact>
        <Button icon={<LeftOutlined />} aria-label={t('attendancePayroll.period.previous')} disabled={!periodStart} onClick={() => shiftRequested(-1)} />
        <Button className="atp-period-label">{periodLabel}</Button>
        <Button icon={<RightOutlined />} aria-label={t('attendancePayroll.period.next')} disabled={!periodStart} onClick={() => shiftRequested(1)} />
      </Space.Compact>
    </>
  ) : null

  const lockControl = activeTab !== 'rates' && canViewPay ? (
    isSubmitted ? (
      <Space>
        <Tag color="green" icon={<LockOutlined />}>{t('attendancePayroll.submit.locked')}</Tag>
        {canUnlock ? <Button icon={<UnlockOutlined />} onClick={() => setUnlockOpen(true)}>{t('attendancePayroll.unlock.button')}</Button> : null}
      </Space>
    ) : canEdit ? (
      <Tooltip title={pendingCount > 0 ? t('attendancePayroll.submit.pendingTip') : undefined}>
        <Button type="primary" icon={<LockOutlined />} disabled={pendingCount > 0 || !status} loading={submitting} onClick={handleSubmit}>
          {pendingCount > 0
            ? t('attendancePayroll.submit.buttonWithPending', { count: pendingCount })
            : t('attendancePayroll.submit.button')}
        </Button>
      </Tooltip>
    ) : null
  ) : null

  const tabItems = [
    ...(canViewHours ? [{
      key: 'hours',
      label: t('attendancePayroll.tabs.hours'),
      children: (
        <HoursTab
          data={hours}
          loading={loading}
          weeks={weeks}
          includePending={includePending}
          mergeStores={mergeStores}
          canReview={access.canReviewAttendance}
          onMergeStoresChange={setMergeStores}
          onReload={() => void loadHours()}
          onOpenRates={canViewPay ? () => setActiveTab('rates') : undefined}
        />
      ),
    }] : []),
    ...(canViewPay ? [
      {
        key: 'pay',
        label: t('attendancePayroll.tabs.pay'),
        children: (
          <PayTab
            data={pay}
            loading={loading}
            weeks={weeks}
            canEdit={canEdit && !isSubmitted}
            onReload={() => void loadPay()}
          />
        ),
      },
      {
        key: 'rates',
        label: t('attendancePayroll.tabs.rates'),
        children: (
          <RatesTab
            rates={rates}
            pay={pay}
            loading={loading}
            storeCode={storeCode}
            canEdit={canEdit}
            onSaved={() => {
              void loadRates()
              void loadPay()
            }}
          />
        ),
      },
    ] : []),
  ]

  return (
    <div className="atp-page">
      <div className="atp-header">
        <Space wrap size={12}>
          <Typography.Title level={4} className="atp-title">{t('attendancePayroll.title')}</Typography.Title>
          {periodControls}
          <Select
            showSearch
            optionFilterProp="label"
            style={{ minWidth: 160 }}
            options={storeOptions}
            value={storeCode}
            onChange={setStoreCode}
            placeholder={t('attendancePayroll.selectStore')}
          />
          {activeTab === 'hours' ? (
            <Segmented
              value={includePending ? 'all' : 'reviewed'}
              options={[
                { label: t('attendancePayroll.reviewedOnly'), value: 'reviewed' },
                { label: t('attendancePayroll.includePending'), value: 'all' },
              ]}
              onChange={(value) => setIncludePending(value === 'all')}
            />
          ) : null}
          {activeTab === 'rates' ? (
            <Input
              allowClear
              prefix={<SearchOutlined />}
              placeholder={t('attendancePayroll.searchPlaceholder')}
              style={{ width: 180 }}
              onPressEnter={(event) => setRateKeyword((event.target as HTMLInputElement).value.trim())}
              onChange={(event) => {
                if (!event.target.value) setRateKeyword('')
              }}
            />
          ) : null}
        </Space>
        <Space wrap>
          <Button icon={<DownloadOutlined />} onClick={() => void handleExport()}>{t('attendancePayroll.exportExcel')}</Button>
          {lockControl}
        </Space>
      </div>
      <Tabs activeKey={activeTab} items={tabItems} onChange={(key) => setActiveTab(key as PayrollTabKey)} />

      <Modal
        open={unlockOpen}
        title={t('attendancePayroll.unlock.title', { period: periodLabel })}
        okText={t('attendancePayroll.unlock.button')}
        okButtonProps={{ disabled: !unlockReason.trim(), loading: submitting }}
        onCancel={() => setUnlockOpen(false)}
        onOk={() => void handleUnlock()}
      >
        <Typography.Paragraph type="secondary">{t('attendancePayroll.unlock.hint')}</Typography.Paragraph>
        <Input.TextArea rows={3} value={unlockReason} placeholder={t('attendancePayroll.unlock.reasonPlaceholder')} onChange={(event) => setUnlockReason(event.target.value)} />
      </Modal>
    </div>
  )
}
