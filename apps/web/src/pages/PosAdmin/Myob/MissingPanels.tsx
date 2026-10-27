import { Alert, Button, Empty, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import type { MyobOnlyInMyob, MyobOnlyInOurs, MyobTypeIssue } from '../../../types/myob'
import { Avatar, EmployeeTypeTag } from './MyobBadges'
import {
  describeEntitlementKey,
  formatBirthday,
  formatShortDate,
  getEmployeeTypeKey,
  getTypeIssueActionKey,
  getTypeIssueKey,
  getTypeIssueNoteKey,
  joinSubtitle,
} from './myobEmployeeLogic'

// ───────────────────────── 对方没有 ─────────────────────────

interface MissingPanelProps {
  onlyInOurs: MyobOnlyInOurs[]
  onlyInMyob: MyobOnlyInMyob[]
  /** 有「用户管理」访问权限才显示「我方建档」。 */
  canCreateOurSide: boolean
  /** 正在忽略的 MYOB 员工 uid。 */
  ignoringUids: ReadonlySet<string>
  onCopy: (item: MyobOnlyInOurs) => void
  onCreateOurSide: () => void
  onIgnore: (item: MyobOnlyInMyob) => void
}

export function MissingPanel({ onlyInOurs, onlyInMyob, canCreateOurSide, ignoringUids, onCopy, onCreateOurSide, onIgnore }: MissingPanelProps) {
  const { t } = useTranslation()
  return (
    <div className="myob-grid2">
      <section className="myob-subcard" aria-labelledby="myob-missing-ours">
        <div>
          <b id="myob-missing-ours">{t('myob.employees.missing.ours.title')}</b>{' '}
          <Tag color="warning">{onlyInOurs.length}</Tag>
          <div className="myob-card-hint">{t('myob.employees.missing.ours.hint')}</div>
        </div>
        {onlyInOurs.length === 0 ? (
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.employees.missing.ours.empty')} />
        ) : (
          <div>
            {onlyInOurs.map((item) => (
              <div key={item.userGuid} className="myob-list-item">
                <Avatar name={item.userName} />
                <div className="myob-list-body">
                  <span className="myob-person-name">{item.userName}</span>
                  <span className="myob-person-tags">
                    <EmployeeTypeTag type={item.employeeType} />
                  </span>
                  <span className="myob-sub">
                    {joinSubtitle([
                      item.email,
                      item.storeLabels.join(' / '),
                      item.joinedAt ? t('myob.employees.missing.ours.joined', { date: formatShortDate(item.joinedAt) }) : null,
                    ])}
                  </span>
                </div>
                <div className="myob-list-actions">
                  <Button size="small" onClick={() => onCopy(item)}>
                    {t('myob.employees.missing.ours.copy')}
                  </Button>
                </div>
              </div>
            ))}
          </div>
        )}
        <Alert className="myob-note" type="info" showIcon message={t('myob.employees.missing.ours.note')} />
      </section>

      <section className="myob-subcard" aria-labelledby="myob-missing-theirs">
        <div>
          <b id="myob-missing-theirs">{t('myob.employees.missing.theirs.title')}</b>{' '}
          <Tag color="warning">{onlyInMyob.length}</Tag>
          <div className="myob-card-hint">{t('myob.employees.missing.theirs.hint')}</div>
        </div>
        {onlyInMyob.length === 0 ? (
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.employees.missing.theirs.empty')} />
        ) : (
          <div>
            {onlyInMyob.map((item) => {
              const birthday = formatBirthday(item.card.dateOfBirth)
              return (
                <div key={item.card.uid} className="myob-list-item">
                  <Avatar name={item.card.name} />
                  <div className="myob-list-body">
                    <span className="myob-person-name">{item.card.name}</span>
                    <span className="myob-person-tags">
                      {item.card.isActive ? (
                        <Tag>{t('myob.employees.missing.theirs.active')}</Tag>
                      ) : (
                        <Tag color="error">{t('myob.employees.missing.theirs.inactive')}</Tag>
                      )}
                    </span>
                    <span className="myob-sub">
                      {joinSubtitle([item.card.email, birthday ? t('myob.drawer.birthday', { value: birthday }) : null])}
                    </span>
                  </div>
                  <div className="myob-list-actions">
                    {canCreateOurSide ? (
                      <Button size="small" onClick={onCreateOurSide}>
                        {t('myob.employees.missing.theirs.create')}
                      </Button>
                    ) : null}
                    <Button type="link" size="small" loading={ignoringUids.has(item.card.uid)} onClick={() => onIgnore(item)}>
                      {t('myob.employees.missing.theirs.ignore')}
                    </Button>
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </section>
    </div>
  )
}

// ───────────────────────── 用工类型检查 ─────────────────────────

interface TypeIssuesPanelProps {
  issues: MyobTypeIssue[]
  /** 「告知财务」= 复制一段说明文字。 */
  onCopyNote: (issue: MyobTypeIssue, note: string) => void
}

export function TypeIssuesPanel({ issues, onCopyNote }: TypeIssuesPanelProps) {
  const { t } = useTranslation()

  const typeLabel = (type: string) => {
    const key = getEmployeeTypeKey(type)
    return key ? t(key) : type || '—'
  }

  const columns: ColumnsType<MyobTypeIssue> = [
    {
      title: t('myob.employees.typeIssues.columns.employee'),
      key: 'employee',
      width: 200,
      render: (_, issue) => (
        <>
          <span className="myob-person-name">{issue.userName}</span>
          <span className="myob-sub">{issue.storeLabels.join(' / ') || '—'}</span>
        </>
      ),
    },
    {
      title: t('myob.employees.typeIssues.columns.ourType'),
      key: 'ourType',
      width: 130,
      render: (_, issue) => <Tag>{typeLabel(issue.ourType)}</Tag>,
    },
    {
      title: t('myob.employees.typeIssues.columns.myobStatus'),
      key: 'myobStatus',
      width: 260,
      render: (_, issue) => (
        <>
          {t(describeEntitlementKey(issue.hasAnnualEntitlement, issue.hasSickEntitlement))}
          {issue.myobStatus ? <span className="myob-sub">{t('myob.employees.typeIssues.mystatus', { status: issue.myobStatus })}</span> : null}
        </>
      ),
    },
    {
      title: t('myob.employees.typeIssues.columns.issue'),
      key: 'issue',
      width: 240,
      render: (_, issue) => <Tag color="warning">{t(getTypeIssueKey(issue.issueCode))}</Tag>,
    },
    {
      title: t('myob.employees.typeIssues.columns.advice'),
      key: 'advice',
      width: 230,
      render: (_, issue) => (
        <Button
          size="small"
          onClick={() =>
            onCopyNote(
              issue,
              t(getTypeIssueNoteKey(issue.issueCode), {
                name: issue.userName,
                type: typeLabel(issue.ourType),
                stores: issue.storeLabels.join(' / ') || '—',
              }),
            )
          }
        >
          {t(getTypeIssueActionKey(issue.issueCode))}
        </Button>
      ),
    },
  ]

  return (
    <div>
      <div className="myob-card-head" style={{ marginBottom: 8 }}>
        <div>
          <h3 className="myob-card-title">
            {t('myob.employees.typeIssues.title')} <Tag color="warning">{issues.length}</Tag>
          </h3>
          <div className="myob-card-hint">{t('myob.employees.typeIssues.hint')}</div>
        </div>
      </div>
      <MeasuredTable<MyobTypeIssue>
        metricId="pos-admin.myob.type-issues"
        rowKey="userGuid"
        size="middle"
        columns={columns}
        dataSource={issues}
        pagination={false}
        scroll={{ x: 'max-content' }}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.employees.typeIssues.empty')} /> }}
      />
    </div>
  )
}
