// 「开启并下单」确认框：开启分店会改变订单表（计入合计与装柜、下单人可见），所以必须先说清影响再执行。
// 数据只来自候选分店（去年销量 / 有记录款数 / 授权下单人），不额外请求。
import { PlusOutlined } from '@ant-design/icons'
import { Checkbox, Modal } from 'antd'
import { useEffect, useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'

import type { SupplierPreorderStoreCandidate } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { formatInteger } from './supplierPreorderAdmin.logic'

interface SummaryEnableStoreModalProps {
  /** 要开启的分店；null = 不显示 */
  candidate: SupplierPreorderStoreCandidate | null
  confirming: boolean
  /** 「开启后切到全部商品」默认值（当前筛选已经是「全部」时没必要再勾） */
  defaultSwitchToAll: boolean
  onConfirm: (switchToAll: boolean) => void
  onCancel: () => void
}

export default function SummaryEnableStoreModal({ candidate, confirming, defaultSwitchToAll, onConfirm, onCancel }: SummaryEnableStoreModalProps) {
  const { t } = useTranslation()
  const [switchToAll, setSwitchToAll] = useState(defaultSwitchToAll)
  // 每次换一家 / 重新打开都重置为默认值（上次取消勾选的选择不带到下一家）
  useEffect(() => {
    if (candidate) setSwitchToAll(defaultSwitchToAll)
  }, [candidate, defaultSwitchToAll])
  const store = candidate?.storeName ?? ''
  const hasOrderers = !!candidate && candidate.orderers.length > 0

  return (
    <Modal
      open={!!candidate}
      width={560}
      centered
      destroyOnHidden
      maskClosable={!confirming}
      keyboard={!confirming}
      closable={false}
      onCancel={onCancel}
      title={
        <div className="sp-admin-sum-em-head">
          <span className="sp-admin-sum-em-icon" aria-hidden="true">
            <PlusOutlined />
          </span>
          <div>
            <h3>{t('spAdmin.summary.enableTitle', { store })}</h3>
            <p>{t('spAdmin.summary.enableDesc', { store })}</p>
          </div>
        </div>
      }
      footer={(_, { OkBtn, CancelBtn }) => (
        <div className="sp-admin-sum-em-foot">
          <Checkbox checked={switchToAll} disabled={confirming} onChange={(event) => setSwitchToAll(event.target.checked)}>
            {t('spAdmin.summary.enableSwitchAll')}
          </Checkbox>
          <span className="sp-admin-toolbar-spacer" />
          <CancelBtn />
          <OkBtn />
        </div>
      )}
      okText={t('spAdmin.summary.enableOk')}
      cancelText={t('spAdmin.summary.enableCancel')}
      confirmLoading={confirming}
      onOk={() => onConfirm(switchToAll)}
    >
      {candidate ? (
        <div className="sp-admin-sum-em-body">
          <div className="sp-admin-sum-em-facts sp-admin-number">
            <div>
              <span>{t('spAdmin.summary.enableFactSales')}</span>
              <b>
                {formatInteger(candidate.lastYearSalesQty)}
                <small>{t('spAdmin.summary.enableUnitPcs')}</small>
              </b>
            </div>
            <div>
              <span>{t('spAdmin.summary.enableFactItems')}</span>
              <b>
                {formatInteger(candidate.lastYearItemCount)}
                <small>{t('spAdmin.summary.enableUnitItems')}</small>
              </b>
            </div>
            <div>
              <span>{t('spAdmin.summary.enableFactOrderers')}</span>
              <b>
                {formatInteger(candidate.orderers.length)}
                <small>{t('spAdmin.summary.enableUnitPeople')}</small>
              </b>
            </div>
          </div>
          <ul className="sp-admin-sum-em-list">
            <li>
              <Trans i18nKey="spAdmin.summary.enableImpactTotals" values={{ store }} components={{ b: <b /> }} />
            </li>
            {hasOrderers ? (
              <li className="is-warn">
                <Trans i18nKey="spAdmin.summary.enableImpactOrderers" values={{ names: candidate.orderers.join(t('spAdmin.summary.listSeparator')) }} components={{ b: <b /> }} />
              </li>
            ) : (
              <li className="is-alert">
                <Trans i18nKey="spAdmin.summary.enableImpactNoOrderer" components={{ b: <b /> }} />
              </li>
            )}
            <li>
              <Trans i18nKey="spAdmin.summary.enableImpactDraft" components={{ b: <b /> }} />
            </li>
            <li>
              <Trans i18nKey="spAdmin.summary.enableImpactRemind" components={{ b: <b /> }} />
            </li>
            <li>
              <Trans i18nKey="spAdmin.summary.enableImpactUndo" components={{ b: <b /> }} />
            </li>
          </ul>
        </div>
      ) : null}
    </Modal>
  )
}
