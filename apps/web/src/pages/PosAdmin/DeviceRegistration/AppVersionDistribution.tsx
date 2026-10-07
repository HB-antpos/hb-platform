import { Tag } from 'antd'
import { useTranslation } from 'react-i18next'

import type { AppVersionDistribution as AppVersionDistributionData } from '../../../types/deviceRegistration'

import {
  formatAppPackageVersion,
  getAppVersionShare,
  isAppVersionSelected,
  toAppVersionSelection,
  type AppVersionSelection,
} from './appVersionDistributionLogic'

interface AppVersionDistributionProps {
  distribution: AppVersionDistributionData
  loading: boolean
  selection?: AppVersionSelection
  onSelect: (selection?: AppVersionSelection) => void
}

/** 零值淡化成短横线，让有设备的格子一眼可见。 */
function Count({ value, className }: { value: number; className?: string }) {
  return value > 0 ? (
    <span className={className}>{value}</span>
  ) : (
    <span className="dev-mgmt-faint">–</span>
  )
}

/** App 版本分布：按系统 + 版本号 + 构建号统计设备数；点选一行即筛选下方设备明细，再点一次取消。 */
export default function AppVersionDistribution({
  distribution,
  loading,
  selection,
  onSelect,
}: AppVersionDistributionProps) {
  const { t } = useTranslation()
  const { items, total } = distribution

  return (
    <section className="dev-mgmt-versions" aria-busy={loading} aria-label={t('posAdmin.devices.mgmt.versionDist.title')}>
      <div className="dev-mgmt-versions-head">
        <span className="dev-mgmt-versions-title">{t('posAdmin.devices.mgmt.versionDist.title')}</span>
        <span className="dev-mgmt-sub">{t('posAdmin.devices.mgmt.versionDist.hint', { count: items.length })}</span>
      </div>

      {items.length === 0 ? (
        <div className="dev-mgmt-versions-empty">
          {loading ? t('common.loading') : t('posAdmin.devices.mgmt.versionDist.empty')}
        </div>
      ) : (
        <>
          <div className="dev-mgmt-versions-grid dev-mgmt-versions-columns" aria-hidden="true">
            <span>{t('posAdmin.devices.mgmt.versionDist.columns.system')}</span>
            <span>{t('posAdmin.devices.mgmt.versionDist.columns.version')}</span>
            <span>{t('posAdmin.devices.mgmt.versionDist.columns.devices')}</span>
            <span className="dev-mgmt-versions-num">{t('posAdmin.devices.mgmt.versionDist.columns.online')}</span>
            <span className="dev-mgmt-versions-num dev-mgmt-versions-extra">OTA</span>
            <span className="dev-mgmt-versions-num dev-mgmt-versions-extra">
              {t('posAdmin.devices.mgmt.versionDist.columns.embedded')}
            </span>
          </div>

          <ul className="dev-mgmt-versions-list">
            {items.map((item) => {
              const rowSelection = toAppVersionSelection(item)
              const selected = isAppVersionSelected(item, selection)
              const version = formatAppPackageVersion(item.appVersion, item.appBuildVersion)
              const system = item.deviceSystem || t('posAdmin.devices.mgmt.appSummaryUnknown')
              const share = getAppVersionShare(item.total, total)
              const rowKey = [item.deviceSystem, item.appVersion, item.appBuildVersion].join('|')

              return (
                // 提示放在 li 上：disabled 的原生按钮收不到鼠标事件，挂在按钮上的提示不会出现。
                <li key={rowKey} title={rowSelection ? undefined : t('posAdmin.devices.mgmt.versionDist.unselectable')}>
                  <button
                    type="button"
                    className={`dev-mgmt-versions-grid dev-mgmt-versions-row${selected ? ' dev-mgmt-versions-row-active' : ''}`}
                    aria-pressed={selected}
                    aria-label={t('posAdmin.devices.mgmt.versionDist.rowAria', {
                      system,
                      version: version ?? t('posAdmin.devices.mgmt.versionDist.unreportedVersion'),
                      count: item.total,
                      online: item.online,
                    })}
                    disabled={!rowSelection}
                    onClick={() => onSelect(selected ? undefined : rowSelection)}
                  >
                    <span>
                      <Tag className="dev-mgmt-versions-system">{system}</Tag>
                    </span>
                    <span className={`dev-mgmt-versions-ver ${version ? 'dev-mgmt-mono' : 'dev-mgmt-faint'}`}>
                      {version ?? t('posAdmin.devices.mgmt.versionDist.unreportedVersion')}
                    </span>
                    <span className="dev-mgmt-versions-bar">
                      <span className="dev-mgmt-versions-track" aria-hidden="true">
                        <span className="dev-mgmt-versions-fill" style={{ width: `${share}%` }} />
                      </span>
                      <span className="dev-mgmt-versions-total">{item.total}</span>
                      <span className="dev-mgmt-sub dev-mgmt-versions-share">{share}%</span>
                    </span>
                    <span className="dev-mgmt-versions-num">
                      <Count value={item.online} className="dev-mgmt-versions-online" />
                    </span>
                    <span className="dev-mgmt-versions-num dev-mgmt-versions-extra">
                      <Count value={item.ota} />
                    </span>
                    <span className="dev-mgmt-versions-num dev-mgmt-versions-extra">
                      <Count value={item.embedded} />
                    </span>
                  </button>
                </li>
              )
            })}
          </ul>
          <div className="dev-mgmt-sub dev-mgmt-versions-foot">{t('posAdmin.devices.mgmt.versionDist.sourceNote')}</div>
        </>
      )}
    </section>
  )
}
