import {
  CheckCircleOutlined,
  DeleteOutlined,
  LockOutlined,
  PlusOutlined,
  ReloadOutlined,
  SaveOutlined,
  SendOutlined,
  WarningFilled,
} from '@ant-design/icons'
import {
  Alert,
  Button,
  Col,
  Empty,
  Form,
  Grid,
  Input,
  InputNumber,
  Popconfirm,
  Row,
  Space,
  Spin,
  Switch,
  Tag,
  Typography,
  message,
  theme,
} from 'antd'
import type { CSSProperties, ReactNode } from 'react'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  getInvoiceEmailSettings,
  saveInvoiceEmailSettings,
  sendInvoiceEmailSettingsTestEmail,
} from '../../../services/invoiceEmailSettingsService'
import { useAuthStore } from '../../../store/auth'

import invoiceEmailSettingsMessagesEn from './invoiceEmailSettingsMessages.en.json'
import invoiceEmailSettingsMessagesZh from './invoiceEmailSettingsMessages.zh.json'
import {
  buildInvoiceEmailSettingsSavePayload,
  buildInvoiceEmailSettingsTestPayload,
  collectInvoiceEmailAccountErrorIndexes,
  createInvoiceEmailSettingsFormValues,
  createNewInvoiceEmailAccountFormValue,
  ensureInvoiceEmailDefaultAccount,
  resolveInvoiceEmailSettingsErrorMessage,
  setInvoiceEmailDefaultAccount,
  type InvoiceEmailAccountFormValues,
  type InvoiceEmailSettingsFormValues,
} from './pageLogic'

// 配置页文案随页面代码块懒注册，不进首屏 i18n 包（首屏 gzip 预算很紧）。
registerPageMessages({ zh: invoiceEmailSettingsMessagesZh, en: invoiceEmailSettingsMessagesEn })

const EMAIL_REGEXP = /^[^\s@]+@[^\s@]+\.[^\s@]+$/
const ACCOUNT_LIST_WIDTH = 300

interface FormSectionProps {
  title: string
  description?: ReactNode
  children: ReactNode
  first?: boolean
}

/** 编辑区里的一个分组：标题 + 说明 + 字段，组与组之间用细分隔线隔开。 */
function FormSection({ title, description, children, first }: FormSectionProps) {
  const { token } = theme.useToken()

  return (
    <section
      style={{
        paddingTop: first ? 0 : token.paddingLG,
        marginTop: first ? 0 : token.paddingLG,
        borderTop: first ? undefined : `1px solid ${token.colorBorderSecondary}`,
      }}
    >
      <Typography.Text strong style={{ fontSize: token.fontSizeLG }}>
        {title}
      </Typography.Text>
      {description ? (
        <Typography.Paragraph type="secondary" style={{ margin: `${token.marginXXS}px 0 ${token.margin}px` }}>
          {description}
        </Typography.Paragraph>
      ) : (
        <div style={{ height: token.margin }} />
      )}
      {children}
    </section>
  )
}

export default function InvoiceEmailSettingsPage() {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const screens = Grid.useBreakpoint()
  const access = useAuthStore((state) => state.access)
  const canManageSettings = access.hasPermission('System.ManageSettings')
  const [form] = Form.useForm<InvoiceEmailSettingsFormValues>()
  const watchedAccounts = Form.useWatch('accounts', form) as InvoiceEmailAccountFormValues[] | undefined
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [testingAccountIndex, setTestingAccountIndex] = useState<number | null>(null)
  const [selectedIndex, setSelectedIndex] = useState(0)
  const [errorAccountIndexes, setErrorAccountIndexes] = useState<number[]>([])
  const editorRef = useRef<HTMLDivElement | null>(null)

  const accounts = watchedAccounts ?? []
  const isWideLayout = Boolean(screens.lg)

  const getAccountValues = () => (form.getFieldValue('accounts') ?? []) as InvoiceEmailAccountFormValues[]

  const accountLabels = {
    defaultName: t('invoiceEmailSettings.defaultAccountName'),
    accountNamePrefix: t('invoiceEmailSettings.accountNamePrefix'),
  }

  const loadSettings = async () => {
    setLoading(true)
    try {
      const result = await getInvoiceEmailSettings()
      form.setFieldsValue(createInvoiceEmailSettingsFormValues(result))
      // 后端返回默认账号在前，重新加载后回到第一个账号并清掉旧的出错标记。
      setSelectedIndex(0)
      setErrorAccountIndexes([])
    } catch (error) {
      console.error(error)
      message.error(resolveInvoiceEmailSettingsErrorMessage(error, t('invoiceEmailSettings.loadFailed')))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void loadSettings()
  }, [])

  const refreshErrorIndexes = () => {
    setErrorAccountIndexes(collectInvoiceEmailAccountErrorIndexes(form.getFieldsError()))
  }

  const handleSave = async () => {
    try {
      await form.validateFields()
    } catch (error) {
      // 未选中的账号只是隐藏不卸载，校验会覆盖全部账号；失败时跳到第一个出错账号，避免用户找不到错误。
      const errorFields = (error as { errorFields?: { name: (string | number)[]; errors: string[] }[] }).errorFields ?? []
      const indexes = collectInvoiceEmailAccountErrorIndexes(errorFields)
      setErrorAccountIndexes(indexes)
      if (indexes.length > 0) {
        setSelectedIndex(indexes[0])
      }
      message.warning(t('invoiceEmailSettings.validationFailed'))
      return
    }

    setSaving(true)
    try {
      const result = await saveInvoiceEmailSettings(buildInvoiceEmailSettingsSavePayload({ accounts: getAccountValues() }))
      const selectedId = getAccountValues()[selectedIndex]?.id
      form.setFieldsValue(createInvoiceEmailSettingsFormValues(result))
      // 保存后服务端会重新排序（默认账号在前），按 ID 找回刚才正在编辑的账号；新账号没有 ID 时回到第一个。
      const nextIndex = selectedId ? result.accounts.findIndex((account) => account.id === selectedId) : -1
      setSelectedIndex(nextIndex >= 0 ? nextIndex : 0)
      setErrorAccountIndexes([])
      message.success(t('invoiceEmailSettings.saveSuccess'))
    } catch (error) {
      console.error(error)
      message.error(resolveInvoiceEmailSettingsErrorMessage(error, t('invoiceEmailSettings.saveFailed')))
    } finally {
      setSaving(false)
    }
  }

  const handleAddAccount = () => {
    const current = getAccountValues()
    form.setFieldsValue({
      accounts: [...current, createNewInvoiceEmailAccountFormValue(current.length, accountLabels)],
    })
    setSelectedIndex(current.length)
  }

  const handleRemoveAccount = (index: number) => {
    const current = getAccountValues()
    if (current.length <= 1) {
      message.warning(t('invoiceEmailSettings.keepOneAccount'))
      return
    }

    form.setFieldsValue({
      accounts: ensureInvoiceEmailDefaultAccount(
        current.filter((_, accountIndex) => accountIndex !== index),
        0,
      ),
    })
    setSelectedIndex(Math.max(0, index - 1))
    // 删除后后面账号的序号整体前移，出错标记按新序号重新计算。
    setTimeout(refreshErrorIndexes)
  }

  const handleSetDefaultAccount = (index: number) => {
    form.setFieldsValue({
      accounts: setInvoiceEmailDefaultAccount(getAccountValues(), index),
    })
  }

  const validateAccountForTest = async (index: number) => {
    const field = (name: string) => ['accounts', index, name] as const
    await form.validateFields([
      field('name'),
      field('host'),
      field('port'),
      field('fromEmail'),
      field('maxAttachmentMegabytes'),
    ])

    const account = getAccountValues()[index]
    const testToEmail = account?.testToEmail?.trim()
    if (!testToEmail) {
      throw new Error(t('invoiceEmailSettings.validation.testToEmail'))
    }
    if (!EMAIL_REGEXP.test(testToEmail)) {
      throw new Error(t('invoiceEmailSettings.validation.email'))
    }

    return account
  }

  const handleSendTest = async (index: number) => {
    setTestingAccountIndex(index)
    try {
      const account = await validateAccountForTest(index)
      const result = await sendInvoiceEmailSettingsTestEmail(buildInvoiceEmailSettingsTestPayload(account))
      message.success(result.message || t('invoiceEmailSettings.testSuccess'))
    } catch (error) {
      console.error(error)
      message.error(resolveInvoiceEmailSettingsErrorMessage(error, t('invoiceEmailSettings.testFailed')))
    } finally {
      setTestingAccountIndex(null)
    }
  }

  const handleSelectAccount = (index: number) => {
    setSelectedIndex(index)
    // 窄屏时编辑区堆在列表下方、通常在屏幕外，点选后把它滚进视野，否则用户会以为点击没反应。
    if (!isWideLayout) {
      const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
      requestAnimationFrame(() => editorRef.current?.scrollIntoView({ behavior: reduceMotion ? 'auto' : 'smooth', block: 'start' }))
    }
  }

  const renderAccountListItem = (account: InvoiceEmailAccountFormValues, index: number) => {
    const selected = index === selectedIndex
    const hasError = errorAccountIndexes.includes(index)
    const fromEmail = account.fromEmail?.trim()
    const host = account.host?.trim()
    const itemStyle: CSSProperties = {
      display: 'block',
      width: '100%',
      textAlign: 'left',
      cursor: 'pointer',
      padding: `${token.paddingSM}px ${token.padding}px`,
      borderRadius: token.borderRadiusLG,
      border: `1px solid ${hasError ? token.colorErrorBorder : selected ? token.colorPrimaryBorder : token.colorBorderSecondary}`,
      background: selected ? token.colorPrimaryBg : token.colorBgContainer,
      // 左侧色条标记当前正在编辑的账号，比单纯换底色更容易在多个账号间定位。
      boxShadow: selected ? `inset 3px 0 0 ${token.colorPrimary}` : undefined,
      font: 'inherit',
      color: 'inherit',
    }

    return (
      <button
        key={account.id ?? `new-${index}`}
        type="button"
        aria-current={selected ? 'true' : undefined}
        style={itemStyle}
        onClick={() => handleSelectAccount(index)}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: token.marginXS, minWidth: 0 }}>
          <Typography.Text strong ellipsis style={{ flex: 1, minWidth: 0 }}>
            {account.name?.trim() || t('invoiceEmailSettings.accountTitle', { index: index + 1 })}
          </Typography.Text>
          {hasError ? <WarningFilled style={{ color: token.colorError }} aria-label={t('invoiceEmailSettings.accountHasError')} /> : null}
          {account.isDefault ? <Tag color="blue" style={{ marginInlineEnd: 0 }}>{t('invoiceEmailSettings.defaultAccount')}</Tag> : null}
        </div>
        <Typography.Text type={fromEmail ? undefined : 'secondary'} ellipsis style={{ display: 'block', fontSize: token.fontSizeSM }}>
          {fromEmail || t('invoiceEmailSettings.fromEmailMissing')}
        </Typography.Text>
        <div style={{ display: 'flex', alignItems: 'center', gap: token.marginXS, marginTop: token.marginXXS, minWidth: 0 }}>
          <Typography.Text type="secondary" ellipsis style={{ flex: 1, minWidth: 0, fontSize: token.fontSizeSM }}>
            {host ? `${host}:${account.port ?? ''}` : t('invoiceEmailSettings.hostMissing')}
          </Typography.Text>
          <Typography.Text
            type={account.hasPassword && !account.clearPassword ? 'success' : 'secondary'}
            style={{ fontSize: token.fontSizeSM, whiteSpace: 'nowrap' }}
          >
            <LockOutlined />{' '}
            {account.hasPassword && !account.clearPassword
              ? t('invoiceEmailSettings.passwordConfigured')
              : t('invoiceEmailSettings.passwordNotConfigured')}
          </Typography.Text>
        </div>
      </button>
    )
  }

  const panelStyle: CSSProperties = {
    background: token.colorBgContainer,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: token.borderRadiusLG,
    padding: token.paddingLG,
    minWidth: 0,
  }

  return (
    <PageContainer
      title={t('invoiceEmailSettings.title')}
      subtitle={t('invoiceEmailSettings.subtitle')}
      extra={(
        <Space>
          <Button icon={<ReloadOutlined />} onClick={() => void loadSettings()} loading={loading}>
            {t('common.refresh')}
          </Button>
          <Button
            type="primary"
            icon={<SaveOutlined />}
            onClick={() => void handleSave()}
            loading={saving}
            disabled={!canManageSettings}
          >
            {t('common.save')}
          </Button>
        </Space>
      )}
    >
      {!canManageSettings ? (
        <Alert
          showIcon
          type="warning"
          message={t('invoiceEmailSettings.noPermission')}
          style={{ marginBottom: token.margin }}
        />
      ) : null}

      <Spin spinning={loading}>
        <Form
          form={form}
          layout="vertical"
          disabled={loading || !canManageSettings}
          onFieldsChange={refreshErrorIndexes}
          initialValues={{ accounts: [createNewInvoiceEmailAccountFormValue(0, accountLabels)] }}
        >
          <div
            style={{
              display: 'grid',
              // 宽屏左右分栏（账号列表 + 编辑区），窄屏上下堆叠。
              gridTemplateColumns: isWideLayout ? `${ACCOUNT_LIST_WIDTH}px minmax(0, 1fr)` : 'minmax(0, 1fr)',
              gap: token.marginLG,
              alignItems: 'start',
            }}
          >
            <aside
              aria-label={t('invoiceEmailSettings.accountsTitle')}
              style={{ ...panelStyle, padding: token.padding, position: isWideLayout ? 'sticky' : undefined, top: token.margin }}
            >
              <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', marginBottom: token.marginSM }}>
                <Typography.Text strong>{t('invoiceEmailSettings.accountsTitle')}</Typography.Text>
                <Typography.Text type="secondary" style={{ fontSize: token.fontSizeSM }}>
                  {t('invoiceEmailSettings.accountCountValue', { count: accounts.length })}
                </Typography.Text>
              </div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: token.marginXS }}>
                {accounts.map(renderAccountListItem)}
              </div>
              <Button
                block
                type="dashed"
                icon={<PlusOutlined />}
                onClick={handleAddAccount}
                disabled={!canManageSettings || loading}
                style={{ marginTop: token.marginSM }}
              >
                {t('invoiceEmailSettings.addAccount')}
              </Button>
              <Typography.Paragraph type="secondary" style={{ fontSize: token.fontSizeSM, margin: `${token.marginSM}px 0 0` }}>
                {t('invoiceEmailSettings.defaultAccountHint')}
              </Typography.Paragraph>
            </aside>

            <div ref={editorRef} style={panelStyle}>
              <Form.List name="accounts">
                {(fields) => (
                  <>
                    {fields.length === 0 ? <Empty /> : null}
                    {fields.map((field, index) => {
                      const account = accounts[index]
                      const isDefault = Boolean(account?.isDefault)
                      const clearPassword = Boolean(account?.clearPassword)

                      return (
                        // 所有账号的字段都保持挂载、只隐藏未选中的，保存时 validateFields 才能覆盖全部账号。
                        <div key={field.key} hidden={index !== selectedIndex}>
                          <Form.Item name={[field.name, 'id']} hidden>
                            <Input />
                          </Form.Item>

                          <div
                            style={{
                              display: 'flex',
                              flexWrap: 'wrap',
                              alignItems: 'flex-start',
                              justifyContent: 'space-between',
                              gap: token.marginSM,
                              marginBottom: token.marginLG,
                            }}
                          >
                            <div style={{ minWidth: 0 }}>
                              <Space size={token.marginXS} wrap>
                                <Typography.Title level={4} style={{ margin: 0 }}>
                                  {account?.name?.trim() || t('invoiceEmailSettings.accountTitle', { index: index + 1 })}
                                </Typography.Title>
                                {isDefault ? <Tag color="blue">{t('invoiceEmailSettings.defaultAccount')}</Tag> : null}
                              </Space>
                              <Typography.Text type="secondary" style={{ display: 'block' }}>
                                {account?.fromEmail?.trim() || t('invoiceEmailSettings.fromEmailMissing')}
                              </Typography.Text>
                            </div>
                            <Space wrap>
                              <Button
                                icon={<CheckCircleOutlined />}
                                onClick={() => handleSetDefaultAccount(index)}
                                disabled={!canManageSettings || isDefault}
                              >
                                {isDefault ? t('invoiceEmailSettings.defaultAccount') : t('invoiceEmailSettings.setDefault')}
                              </Button>
                              <Popconfirm
                                title={t('invoiceEmailSettings.deleteConfirm')}
                                okText={t('common.delete')}
                                okButtonProps={{ danger: true }}
                                cancelText={t('common.cancel')}
                                onConfirm={() => handleRemoveAccount(index)}
                                disabled={!canManageSettings || fields.length <= 1}
                              >
                                <Button danger icon={<DeleteOutlined />} disabled={!canManageSettings || fields.length <= 1}>
                                  {t('common.delete')}
                                </Button>
                              </Popconfirm>
                            </Space>
                          </div>

                          <FormSection first title={t('invoiceEmailSettings.sectionSender')} description={t('invoiceEmailSettings.sectionSenderHint')}>
                            <Row gutter={16}>
                              <Col xs={24} md={8}>
                                <Form.Item
                                  label={t('invoiceEmailSettings.accountName')}
                                  name={[field.name, 'name']}
                                  rules={[{ required: true, message: t('invoiceEmailSettings.validation.accountName') }]}
                                >
                                  <Input />
                                </Form.Item>
                              </Col>
                              <Col xs={24} md={8}>
                                <Form.Item
                                  label={t('invoiceEmailSettings.fromEmail')}
                                  name={[field.name, 'fromEmail']}
                                  rules={[
                                    { required: true, message: t('invoiceEmailSettings.validation.fromEmail') },
                                    { type: 'email', message: t('invoiceEmailSettings.validation.email') },
                                  ]}
                                >
                                  <Input />
                                </Form.Item>
                              </Col>
                              <Col xs={24} md={8}>
                                <Form.Item label={t('invoiceEmailSettings.fromName')} name={[field.name, 'fromName']}>
                                  <Input />
                                </Form.Item>
                              </Col>
                            </Row>
                          </FormSection>

                          <FormSection title={t('invoiceEmailSettings.sectionServer')}>
                            <Row gutter={16}>
                              <Col xs={24} md={16}>
                                <Form.Item
                                  label={t('invoiceEmailSettings.host')}
                                  name={[field.name, 'host']}
                                  rules={[{ required: true, message: t('invoiceEmailSettings.validation.host') }]}
                                >
                                  <Input placeholder="smtp.example.com" />
                                </Form.Item>
                              </Col>
                              <Col xs={24} md={8}>
                                <Form.Item
                                  label={t('invoiceEmailSettings.port')}
                                  name={[field.name, 'port']}
                                  rules={[{ required: true, message: t('invoiceEmailSettings.validation.port') }]}
                                >
                                  <InputNumber min={1} max={65535} style={{ width: '100%' }} />
                                </Form.Item>
                              </Col>
                              <Col xs={24} md={8}>
                                <Form.Item
                                  label={t('invoiceEmailSettings.useSsl')}
                                  name={[field.name, 'useSsl']}
                                  valuePropName="checked"
                                  tooltip={t('invoiceEmailSettings.useSslHint')}
                                >
                                  <Switch />
                                </Form.Item>
                              </Col>
                              <Col xs={24} md={8}>
                                <Form.Item
                                  label={t('invoiceEmailSettings.checkCertificateRevocation')}
                                  name={[field.name, 'checkCertificateRevocation']}
                                  valuePropName="checked"
                                >
                                  <Switch />
                                </Form.Item>
                              </Col>
                            </Row>
                          </FormSection>

                          <FormSection title={t('invoiceEmailSettings.sectionAuth')} description={t('invoiceEmailSettings.passwordHint')}>
                            <Row gutter={16}>
                              <Col xs={24} md={8}>
                                <Form.Item label={t('invoiceEmailSettings.username')} name={[field.name, 'username']}>
                                  <Input autoComplete="off" />
                                </Form.Item>
                              </Col>
                              <Col xs={24} md={8}>
                                <Form.Item
                                  label={t('invoiceEmailSettings.password')}
                                  name={[field.name, 'password']}
                                  extra={account?.hasPassword && !clearPassword ? t('invoiceEmailSettings.passwordKeepHint') : undefined}
                                >
                                  {/* 开启"清空已保存密码"时后端会忽略新密码，这里同步禁用输入框避免误以为已改密码。 */}
                                  <Input.Password autoComplete="new-password" disabled={clearPassword} />
                                </Form.Item>
                              </Col>
                              <Col xs={24} md={8}>
                                <Form.Item
                                  label={t('invoiceEmailSettings.clearPassword')}
                                  name={[field.name, 'clearPassword']}
                                  valuePropName="checked"
                                >
                                  <Switch />
                                </Form.Item>
                              </Col>
                            </Row>
                          </FormSection>

                          <FormSection title={t('invoiceEmailSettings.sectionLimits')}>
                            <Row gutter={16}>
                              <Col xs={24} md={8}>
                                <Form.Item
                                  label={t('invoiceEmailSettings.maxAttachmentBytes')}
                                  name={[field.name, 'maxAttachmentMegabytes']}
                                  rules={[{ required: true, message: t('invoiceEmailSettings.validation.maxAttachmentBytes') }]}
                                >
                                  <InputNumber min={0.01} precision={2} style={{ width: '100%' }} />
                                </Form.Item>
                              </Col>
                            </Row>
                          </FormSection>

                          <FormSection title={t('invoiceEmailSettings.sectionTest')} description={t('invoiceEmailSettings.sectionTestHint')}>
                            <Form.Item label={t('invoiceEmailSettings.testToEmail')} style={{ marginBottom: 0 }}>
                              <Space.Compact style={{ width: '100%', maxWidth: 520 }}>
                                <Form.Item name={[field.name, 'testToEmail']} noStyle>
                                  <Input placeholder="name@example.com" />
                                </Form.Item>
                                <Button
                                  icon={<SendOutlined />}
                                  onClick={() => void handleSendTest(index)}
                                  loading={testingAccountIndex === index}
                                  disabled={!canManageSettings}
                                >
                                  {t('invoiceEmailSettings.sendTest')}
                                </Button>
                              </Space.Compact>
                            </Form.Item>
                          </FormSection>
                        </div>
                      )
                    })}
                  </>
                )}
              </Form.List>
            </div>
          </div>
        </Form>
      </Spin>
    </PageContainer>
  )
}
