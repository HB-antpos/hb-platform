import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  Alert,
  Button,
  Card,
  Empty,
  Input,
  Modal,
  Popconfirm,
  Select,
  Space,
  Tag,
  Tooltip,
  Typography,
  message,
} from 'antd'
import type { TableProps } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { FilterDropdownProps, FilterValue } from 'antd/es/table/interface'
import {
  CopyOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
  StopOutlined,
} from '@ant-design/icons'
import {
  createServiceApiToken,
  getServiceApiTokens,
  revokeServiceApiToken,
} from '../../../services/serviceApiTokenService'
import type {
  ServiceApiToken,
  ServiceApiTokenCreateResponse,
  ServiceApiTokenPurpose,
} from '../../../types/serviceApiToken'
import { formatAppDownloadLocalDateTime } from './time'
import {
  buildServiceApiTokenEnvSnippet,
  canRevokeServiceApiToken,
  matchesServiceApiTokenScopeFilter,
  matchesServiceApiTokenStatusFilter,
  matchesServiceApiTokenTextFilter,
  resolveServiceApiTokenApiBaseUrl,
  resolveServiceApiTokenStatusColor,
} from './serviceApiTokenPanelLogic'
import { MeasuredTable } from '../../../components/MeasuredTable'

const DEFAULT_TOKEN_PURPOSE: ServiceApiTokenPurpose = 'mobile-ota-publisher'
const KNOWN_TOKEN_STATUSES = ['active', 'revoked', 'expired'] as const

interface ServiceApiTokenColumnFilters {
  name: string
  tokenPrefix: string
  scopes: string[]
  status: string[]
}

function toStringFilterValues(value: FilterValue | null | undefined) {
  return (value ?? [])
    .map((item) => String(item).trim())
    .filter(Boolean)
}

function resolveBrowserApiBaseUrl() {
  const envBaseUrl = (((import.meta as ImportMeta & { env?: ImportMetaEnv }).env?.VITE_API_BASE_URL) || '').trim()
  const origin = typeof window === 'undefined' ? '' : window.location.origin
  return resolveServiceApiTokenApiBaseUrl(envBaseUrl, origin)
}

export default function ServiceApiTokensPanel() {
  const { t } = useTranslation()
  const [tokens, setTokens] = useState<ServiceApiToken[]>([])
  const [loading, setLoading] = useState(false)
  const [loadFailed, setLoadFailed] = useState(false)
  const [createOpen, setCreateOpen] = useState(false)
  const [tokenName, setTokenName] = useState('')
  const [tokenPurpose, setTokenPurpose] = useState<ServiceApiTokenPurpose>(
    DEFAULT_TOKEN_PURPOSE,
  )
  const [creating, setCreating] = useState(false)
  const [createdToken, setCreatedToken] = useState<ServiceApiTokenCreateResponse | null>(null)
  const [createdPurpose, setCreatedPurpose] = useState<ServiceApiTokenPurpose | null>(null)
  const [revokingId, setRevokingId] = useState<string | null>(null)
  const [columnFilters, setColumnFilters] = useState<ServiceApiTokenColumnFilters>({
    name: '',
    tokenPrefix: '',
    scopes: [],
    status: [],
  })
  const [currentPage, setCurrentPage] = useState(1)

  async function copyText(value: string, successMessage: string, failedMessage: string) {
    try {
      await navigator.clipboard.writeText(value)
      message.success(successMessage)
    } catch {
      message.error(failedMessage)
    }
  }

  async function loadTokens() {
    setLoading(true)
    setLoadFailed(false)
    try {
      setTokens(await getServiceApiTokens())
      setCurrentPage(1)
    } catch {
      setLoadFailed(true)
      message.error(t('system.appDownloads.serviceTokens.loadFailed'))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void loadTokens()
  }, [])

  async function handleCreate() {
    const name = tokenName.trim()
    if (!name) {
      message.warning(t('system.appDownloads.serviceTokens.nameRequired'))
      return
    }

    setCreating(true)
    try {
      const result = await createServiceApiToken(name, tokenPurpose)
      setCreatedToken(result)
      setCreatedPurpose(tokenPurpose)
      setTokenName('')
      message.success(t('system.appDownloads.serviceTokens.createSuccess'))
      await loadTokens()
    } catch {
      message.error(t('system.appDownloads.serviceTokens.createFailed'))
    } finally {
      setCreating(false)
    }
  }

  async function handleRevoke(record: ServiceApiToken) {
    setRevokingId(record.id)
    try {
      await revokeServiceApiToken(record.id)
      message.success(t('system.appDownloads.serviceTokens.revokeSuccess'))
      await loadTokens()
    } catch {
      message.error(t('system.appDownloads.serviceTokens.revokeFailed'))
    } finally {
      setRevokingId(null)
    }
  }

  function closeCreateModal() {
    setCreateOpen(false)
    setCreatedToken(null)
    setCreatedPurpose(null)
    setTokenName('')
    setTokenPurpose(DEFAULT_TOKEN_PURPOSE)
  }

  const apiBaseUrl = useMemo(() => resolveBrowserApiBaseUrl(), [])
  const envSnippet = createdToken
    ? buildServiceApiTokenEnvSnippet(
        apiBaseUrl,
        createdToken.token,
        createdPurpose ?? DEFAULT_TOKEN_PURPOSE,
      )
    : ''

  const scopeFilterOptions = useMemo(() => {
    const values = new Set(columnFilters.scopes)
    tokens.forEach((token) => {
      token.scopes.forEach((scope) => {
        const normalizedScope = scope.trim()
        if (normalizedScope) {
          values.add(normalizedScope)
        }
      })
    })

    return Array.from(values)
      .sort((left, right) => left.localeCompare(right))
      .map((scope) => ({ text: scope, value: scope }))
  }, [columnFilters.scopes, tokens])

  const statusFilterOptions = useMemo(() => {
    const values = new Set<string>(KNOWN_TOKEN_STATUSES)
    columnFilters.status.forEach((status) => values.add(status.trim().toLowerCase()))
    tokens.forEach((token) => {
      const normalizedStatus = token.status.trim().toLowerCase()
      if (normalizedStatus) {
        values.add(normalizedStatus)
      }
    })

    const extraStatuses = Array.from(values)
      .filter((status) => !KNOWN_TOKEN_STATUSES.includes(status as (typeof KNOWN_TOKEN_STATUSES)[number]))
      .sort((left, right) => left.localeCompare(right))

    return [...KNOWN_TOKEN_STATUSES, ...extraStatuses].map((status) => ({
      text: t(`system.appDownloads.serviceTokens.statuses.${status}`, status),
      value: status,
    }))
  }, [columnFilters.status, t, tokens])

  const filterIcon = (filtered?: boolean) => (
    <SearchOutlined style={{ color: filtered ? '#1677ff' : undefined }} />
  )

  const makeTextFilterDropdown =
    (columnLabel: string) =>
    ({ selectedKeys, setSelectedKeys, confirm, clearFilters }: FilterDropdownProps) => {
      const selectedValue = selectedKeys[0]
      const inputValue = selectedValue == null ? '' : String(selectedValue)
      const applyFilter = () => {
        setCurrentPage(1)
        confirm()
      }
      const resetFilter = () => {
        setSelectedKeys([])
        setCurrentPage(1)
        if (clearFilters) {
          clearFilters({ confirm: true, closeDropdown: true })
        } else {
          confirm()
        }
      }

      return (
        <div
          style={{ padding: 8, width: 240 }}
          onKeyDown={(event) => event.stopPropagation()}
          onMouseDown={(event) => event.stopPropagation()}
        >
          <Space direction="vertical" style={{ width: '100%' }}>
            <Input
              autoFocus
              allowClear
              aria-label={`${t('common.filter')} ${columnLabel}`}
              placeholder={`${t('common.search')} ${columnLabel}`}
              value={inputValue}
              onChange={(event) =>
                setSelectedKeys(event.target.value ? [event.target.value] : [])
              }
              onPressEnter={applyFilter}
            />
            <Space>
              <Button
                size="small"
                type="primary"
                icon={<SearchOutlined />}
                onClick={applyFilter}
              >
                {t('common.search')}
              </Button>
              <Button size="small" onClick={resetFilter}>
                {t('common.reset')}
              </Button>
            </Space>
          </Space>
        </div>
      )
    }

  const handleTableChange: NonNullable<TableProps<ServiceApiToken>['onChange']> = (
    pagination,
    filters,
    _sorter,
    extra,
  ) => {
    const nameValues = toStringFilterValues(filters.name)
    const tokenPrefixValues = toStringFilterValues(filters.tokenPrefix)
    setColumnFilters({
      name: nameValues[0] ?? '',
      tokenPrefix: tokenPrefixValues[0] ?? '',
      scopes: toStringFilterValues(filters.scopes),
      status: toStringFilterValues(filters.status).map((status) => status.toLowerCase()),
    })
    setCurrentPage(extra.action === 'paginate' ? (pagination.current ?? 1) : 1)
  }

  const columns: ColumnsType<ServiceApiToken> = [
    {
      title: t('system.appDownloads.serviceTokens.name'),
      dataIndex: 'name',
      width: 180,
      filteredValue: columnFilters.name ? [columnFilters.name] : null,
      filterDropdown: makeTextFilterDropdown(t('system.appDownloads.serviceTokens.name')),
      filterIcon,
      onFilter: (value, record) => matchesServiceApiTokenTextFilter(record.name, value),
      render: (value: string) => value || '--',
    },
    {
      title: t('system.appDownloads.serviceTokens.tokenPrefix'),
      dataIndex: 'tokenPrefix',
      width: 170,
      filteredValue: columnFilters.tokenPrefix ? [columnFilters.tokenPrefix] : null,
      filterDropdown: makeTextFilterDropdown(
        t('system.appDownloads.serviceTokens.tokenPrefix'),
      ),
      filterIcon,
      onFilter: (value, record) =>
        matchesServiceApiTokenTextFilter(record.tokenPrefix, value),
      render: (value: string) => <Typography.Text code>{value || '--'}</Typography.Text>,
    },
    {
      title: t('system.appDownloads.serviceTokens.scopes'),
      dataIndex: 'scopes',
      width: 240,
      filters: scopeFilterOptions,
      filteredValue: columnFilters.scopes.length ? columnFilters.scopes : null,
      filterSearch: true,
      filterIcon,
      onFilter: (value, record) =>
        matchesServiceApiTokenScopeFilter(record.scopes, value),
      render: (scopes: string[]) => (
        <Space size={[4, 4]} wrap>
          {(scopes || []).map((scope) => (
            <Tag key={scope}>{scope}</Tag>
          ))}
        </Space>
      ),
    },
    {
      title: t('system.appDownloads.serviceTokens.status'),
      dataIndex: 'status',
      width: 120,
      filters: statusFilterOptions,
      filteredValue: columnFilters.status.length ? columnFilters.status : null,
      filterIcon,
      onFilter: (value, record) =>
        matchesServiceApiTokenStatusFilter(record.status, value),
      render: (status: string) => (
        <Tag color={resolveServiceApiTokenStatusColor(status)}>
          {t(`system.appDownloads.serviceTokens.statuses.${status}`, status)}
        </Tag>
      ),
    },
    {
      title: t('system.appDownloads.serviceTokens.createdAt'),
      dataIndex: 'createdAt',
      width: 180,
      render: (value: string | null | undefined) => formatAppDownloadLocalDateTime(value),
    },
    {
      title: t('system.appDownloads.serviceTokens.lastUsedAt'),
      dataIndex: 'lastUsedAt',
      width: 180,
      render: (value: string | null | undefined) => formatAppDownloadLocalDateTime(value),
    },
    {
      title: t('system.appDownloads.serviceTokens.lastUsedIp'),
      dataIndex: 'lastUsedIp',
      width: 150,
      render: (value: string | null | undefined) => value || '--',
    },
    {
      title: t('system.appDownloads.serviceTokens.revokedAt'),
      dataIndex: 'revokedAt',
      width: 180,
      render: (value: string | null | undefined) => formatAppDownloadLocalDateTime(value),
    },
    {
      title: t('common.actions'),
      key: 'actions',
      fixed: 'right',
      width: 100,
      render: (_, record) =>
        canRevokeServiceApiToken(record.status) ? (
          <Popconfirm
            title={t('system.appDownloads.serviceTokens.revokeConfirm')}
            onConfirm={() => void handleRevoke(record)}
          >
            <Tooltip title={t('system.appDownloads.serviceTokens.revoke')}>
              <Button
                danger
                size="small"
                icon={<StopOutlined />}
                loading={revokingId === record.id}
              />
            </Tooltip>
          </Popconfirm>
        ) : (
          '--'
        ),
    },
  ]

  return (
    <>
      <Card
        title={t('system.appDownloads.serviceTokens.title')}
        extra={
          <Space>
            <Button icon={<ReloadOutlined />} loading={loading} onClick={() => void loadTokens()}>
              {t('common.refresh')}
            </Button>
            <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
              {t('system.appDownloads.serviceTokens.create')}
            </Button>
          </Space>
        }
      >
        {loadFailed ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 12 }}
            message={t('system.appDownloads.serviceTokens.loadFailed')}
          />
        ) : null}
        <MeasuredTable<ServiceApiToken> metricId="system.app-downloads.service-api-tokens-panel.table-1"
          rowKey="id"
          loading={loading}
          columns={columns}
          dataSource={tokens}
          scroll={{ x: 1500 }}
          locale={{ emptyText: <Empty description={t('system.appDownloads.serviceTokens.empty')} /> }}
          pagination={{ current: currentPage, pageSize: 5, showSizeChanger: false }}
          onChange={handleTableChange}
        />
      </Card>

      <Modal
        open={createOpen}
        title={t('system.appDownloads.serviceTokens.createTitle')}
        onCancel={closeCreateModal}
        destroyOnHidden
        footer={
          createdToken
            ? [
                <Button
                  key="copy-env"
                  icon={<CopyOutlined />}
                  onClick={() =>
                    void copyText(
                      envSnippet,
                      t('system.appDownloads.serviceTokens.copyEnvSuccess'),
                      t('system.appDownloads.serviceTokens.copyFailed'),
                    )
                  }
                >
                  {t('system.appDownloads.serviceTokens.copyEnv')}
                </Button>,
                <Button key="close" type="primary" onClick={closeCreateModal}>
                  {t('common.close')}
                </Button>,
              ]
            : [
                <Button key="cancel" onClick={closeCreateModal}>
                  {t('common.cancel')}
                </Button>,
                <Button key="create" type="primary" loading={creating} onClick={() => void handleCreate()}>
                  {t('common.create')}
                </Button>,
              ]
        }
      >
        {createdToken ? (
          <Space direction="vertical" size={12} style={{ width: '100%' }}>
            <Alert
              type="success"
              showIcon
              message={t('system.appDownloads.serviceTokens.oneTimeWarning')}
            />
            <Typography.Text strong>
              {t('system.appDownloads.serviceTokens.oneTimeToken')}
            </Typography.Text>
            <Typography.Paragraph copyable={{ text: createdToken.token }} code>
              {createdToken.token}
            </Typography.Paragraph>
            <Typography.Text strong>
              {t('system.appDownloads.serviceTokens.envSnippet')}
            </Typography.Text>
            <Typography.Text type="secondary">
              {t(
                `system.appDownloads.serviceTokens.purposes.${
                  createdPurpose ?? DEFAULT_TOKEN_PURPOSE
                }.description`,
              )}
            </Typography.Text>
            <Typography.Paragraph
              copyable={{ text: envSnippet }}
              style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}
            >
              {envSnippet}
            </Typography.Paragraph>
          </Space>
        ) : (
          <Space direction="vertical" size={12} style={{ width: '100%' }}>
            <Typography.Text>{t('system.appDownloads.serviceTokens.name')}</Typography.Text>
            <Input
              autoFocus
              maxLength={120}
              value={tokenName}
              placeholder={t('system.appDownloads.serviceTokens.namePlaceholder')}
              onChange={(event) => setTokenName(event.target.value)}
              onPressEnter={() => void handleCreate()}
            />
            <Typography.Text>
              {t('system.appDownloads.serviceTokens.purpose')}
            </Typography.Text>
            <Select<ServiceApiTokenPurpose>
              aria-label={t('system.appDownloads.serviceTokens.purpose')}
              value={tokenPurpose}
              onChange={setTokenPurpose}
              options={[
                {
                  value: 'mobile-ota-publisher',
                  label: t(
                    'system.appDownloads.serviceTokens.purposes.mobile-ota-publisher.label',
                  ),
                },
                {
                  value: 'pos-ipad-update-decision-reader',
                  label: t(
                    'system.appDownloads.serviceTokens.purposes.pos-ipad-update-decision-reader.label',
                  ),
                },
                {
                  value: 'quality-ci-reporter',
                  label: t(
                    'system.appDownloads.serviceTokens.purposes.quality-ci-reporter.label',
                  ),
                },
                {
                  value: 'deployment-acceptance-reporter',
                  label: t(
                    'system.appDownloads.serviceTokens.purposes.deployment-acceptance-reporter.label',
                  ),
                },
                {
                  value: 'pos-supplier-assistant-token-issuer',
                  label: t(
                    'system.appDownloads.serviceTokens.purposes.pos-supplier-assistant-token-issuer.label',
                  ),
                },
              ]}
            />
            <Typography.Text type="secondary">
              {t(
                `system.appDownloads.serviceTokens.purposes.${tokenPurpose}.description`,
              )}
            </Typography.Text>
          </Space>
        )}
      </Modal>
    </>
  )
}
