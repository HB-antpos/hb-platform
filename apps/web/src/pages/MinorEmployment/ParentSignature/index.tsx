import { CheckCircleOutlined, FileTextOutlined, SafetyCertificateOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Checkbox, Descriptions, Form, Input, Layout, Result, Spin, Tag, Typography, message, Space } from 'antd'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'

import { getGuardianSession, getParentSignatureDocument, guardianErrorCode, submitParentSignature } from '../../../services/minorEmploymentService'
import type { GuardianSession, ParentSignatureDocument } from '../../../types/minorEmployment'
import RecordDetails from '../RecordDetails'

import GuardianEmailVerification from './GuardianEmailVerification'

// 会话密钥只存本标签页的 sessionStorage，关闭页面即失效；读写失败（隐私模式等）时退回为每次重新验证。
const SESSION_STORAGE_KEY = 'hb-minor-guardian-session'
function readStoredSessionKey(token: string) {
  try {
    const stored = JSON.parse(window.sessionStorage.getItem(SESSION_STORAGE_KEY) || 'null') as { token?: string; sessionKey?: string } | null
    return stored?.token === token ? stored.sessionKey : undefined
  } catch { return undefined }
}
function storeSessionKey(token: string, sessionKey: string) {
  try { window.sessionStorage.setItem(SESSION_STORAGE_KEY, JSON.stringify({ token, sessionKey })) } catch { /* 忽略：只影响刷新后是否需要重新验证 */ }
}
function clearStoredSessionKey() {
  try { window.sessionStorage.removeItem(SESSION_STORAGE_KEY) } catch { /* 同上 */ }
}

export default function ParentSignaturePage() {
  const [params] = useSearchParams()
  const { token: pathToken } = useParams<{ token: string }>()
  const token = pathToken || params.get('token') || (window.location.hash.startsWith('#token=') ? decodeURIComponent(window.location.hash.slice('#token='.length)) : '')
  const [document, setDocument] = useState<ParentSignatureDocument | null>(null)
  const [session, setSession] = useState<GuardianSession | null>(null)
  const [sessionKey, setSessionKey] = useState('')
  const [loadError, setLoadError] = useState('')
  const [loading, setLoading] = useState(true)
  const [submitted, setSubmitted] = useState(false)
  const [signature, setSignature] = useState('')
  const [saving, setSaving] = useState(false)
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const drawingRef = useRef(false)
  const [form] = Form.useForm()
  const loadDocument = useCallback(async (key: string) => {
    const value = await getParentSignatureDocument(token, key)
    setDocument(value)
    form.setFieldsValue({ parentName: value.parentName, parentPhone: value.parentPhone, parentEmail: value.parentEmail, relationship: value.relationship })
  }, [token, form])
  // 先查会话：本标签页已验证过就直接取表单，否则停在邮箱验证码页，验证前不请求任何孩子资料。
  const startSession = useCallback(async () => {
    const stored = readStoredSessionKey(token)
    const current = await getGuardianSession(token, stored)
    if (current.emailVerified && stored) {
      setSessionKey(stored)
      await loadDocument(stored)
      setSession(null)
    } else {
      clearStoredSessionKey()
      setDocument(null)
      setSession(current)
    }
  }, [token, loadDocument])
  useEffect(() => {
    if (!token) { setLoading(false); return }
    let cancelled = false
    void startSession().catch(() => { if (!cancelled) setLoadError('签署链接无效或已过期') }).finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [token, startSession])
  if (loading) return <Spin fullscreen />
  if (!token || loadError) return <Result status="error" title="签署链接无效或已过期" subTitle="请联系员工重新发送签署邮件。" />
  if (submitted) return <Result status="success" icon={<CheckCircleOutlined />} title="已完成家长签署" subTitle="员工还需要将已签文件提交 HR 审核。" />
  if (!document && session) return <GuardianEmailVerification token={token} session={session} onVerified={(key) => { storeSessionKey(token, key); setSessionKey(key); setLoading(true); void loadDocument(key).then(() => setSession(null)).catch(() => setLoadError('签署链接无效或已过期')).finally(() => setLoading(false)) }} />
  if (!document) return <Result status="error" title="签署链接无效或已过期" subTitle="请联系员工重新发送签署邮件。" />
  return <Layout style={{ minHeight: '100vh', background: '#f5f7fa', padding: 24 }}><meta name="referrer" content="no-referrer" /><main style={{ maxWidth: 900, width: '100%', margin: '0 auto' }}><Card><Typography.Text type="secondary">Hot Bargain · 未成年员工家长签署</Typography.Text><Typography.Title level={2}>{document.formType === 'QLD_CE1' ? 'QLD CE1 家长同意表' : 'NSW 公司未成年员工同意书'}</Typography.Title><Alert type="info" showIcon icon={<SafetyCertificateOutlined />} message={`请确认您是 ${document.employeeName} 的家长 / 监护人`} description="此页面记录家长/监护人的签署声明，HR 将审核材料。电子签署是公司流程设计，不声称政府批准电子替代；签署不会授权超出适用法律、Award 或公司政策的工时。" />
    <Descriptions bordered size="small" column={2} style={{ marginTop: 16 }} items={[{ label: '员工', children: `${document.employeeName}（${document.employeeAge ?? '--'}岁）` }, { label: '州 / 版本', children: <><Tag>{document.state}</Tag> v{document.version}</> }, { label: '雇主 / 岗位', children: `${document.employerName || '--'} / ${document.roleName || '--'}` }, { label: '学校安排', children: document.education?.schoolName || '--' }]} />
    <Card size="small" title={<><FileTextOutlined /> 同意声明</>} style={{ marginTop: 16 }}><Typography.Paragraph>{document.consentScope}</Typography.Paragraph>{document.consentText.map((line) => <Typography.Paragraph key={line}>{line}</Typography.Paragraph>)}</Card>
    <RecordDetails snapshot={document.snapshot} />
    <Form form={form} layout="vertical" style={{ marginTop: 16 }} onFinish={async (values) => { if (!signature) { message.error('请完成手写签名'); return } if (saving || !document.consentScope) return; setSaving(true); try { await submitParentSignature(token, { ...values, parentName: values.parentName, parentPhone: values.parentPhone, parentEmail: values.parentEmail, signature, revision: document.revision, version: document.version, consentScope: document.consentScope, confirmRelationship: Boolean(values.confirmRelationship), confirmBackupContact: Boolean(values.confirmBackupContact), confirmConsent: Boolean(values.confirmConsent) }, sessionKey); clearStoredSessionKey(); setSubmitted(true) } catch (error) { if (guardianErrorCode(error) === 'GUARDIAN_EMAIL_NOT_VERIFIED') { message.warning('验证已过期，请重新输入邮箱验证码'); clearStoredSessionKey(); setLoading(true); void startSession().catch(() => setLoadError('签署链接无效或已过期')).finally(() => setLoading(false)) } else { message.error('签署未完成，请刷新后重试') } } finally { setSaving(false) } }}><Form.Item name="parentName" label="家长 / 监护人姓名" rules={[{ required: true }]}><Input readOnly /></Form.Item><Form.Item name="relationship" label="与员工关系" rules={[{ required: true }]}><Input readOnly /></Form.Item><Form.Item name="parentPhone" label="家长电话（必填）" rules={[{ required: true, pattern: /^[0-9+() .-]{8,30}$/ }]}><Input readOnly /></Form.Item><Form.Item name="parentEmail" label="家长邮箱（必填）" rules={[{ required: true, type: 'email' }]}><Input readOnly /></Form.Item><Alert type="warning" message="家长电话和邮箱属于当前签署版本快照" description="如需更正，请联系员工或 HR 退回资料；员工修改后会生成新版本并重新发送签署链接。" showIcon /><Form.Item label="手写签名（请使用触控板或触摸屏）" required><canvas ref={canvasRef} width={700} height={180} style={{ width: '100%', maxWidth: 700, height: 180, border: '1px dashed #91caff', borderRadius: 8, background: '#fff', touchAction: 'none' }} onPointerDown={(event) => { event.currentTarget.setPointerCapture(event.pointerId); drawingRef.current = true; const rect = event.currentTarget.getBoundingClientRect(); const ctx = event.currentTarget.getContext('2d'); ctx?.beginPath(); ctx?.moveTo((event.clientX - rect.left) * (700 / rect.width), (event.clientY - rect.top) * (180 / rect.height)) }} onPointerMove={(event) => { if (!drawingRef.current) return; const rect = event.currentTarget.getBoundingClientRect(); const ctx = event.currentTarget.getContext('2d'); if (!ctx) return; ctx.lineWidth = 2; ctx.lineCap = 'round'; ctx.lineTo((event.clientX - rect.left) * (700 / rect.width), (event.clientY - rect.top) * (180 / rect.height)); ctx.stroke() }} onPointerUp={(event) => { drawingRef.current = false; setSignature(event.currentTarget.toDataURL('image/png')) }} onPointerLeave={() => { drawingRef.current = false }} /><Button type="link" onClick={() => { const canvas = canvasRef.current; canvas?.getContext('2d')?.clearRect(0, 0, 700, 180); setSignature('') }}>清除签名</Button></Form.Item><Space direction="vertical" style={{ width: '100%' }}><Form.Item name="confirmRelationship" valuePropName="checked" rules={[{ validator: (_, value) => value ? Promise.resolve() : Promise.reject(new Error('请确认家长 / 监护人身份')) }]}><Checkbox>我确认自己是该员工的家长 / 监护人，并确认上方关系准确。</Checkbox></Form.Item><Form.Item name="confirmBackupContact" valuePropName="checked" rules={[{ validator: (_, value) => value ? Promise.resolve() : Promise.reject(new Error('请确认备用联系人')) }]}><Checkbox>我确认已核对指定备用联系人，且其资料可用于必要的安全联系。</Checkbox></Form.Item><Form.Item name="confirmConsent" valuePropName="checked" rules={[{ validator: (_, value) => value ? Promise.resolve() : Promise.reject(new Error('请确认同意范围')) }]}><Checkbox>我确认以上资料真实，并同意员工仅在适用法律、Award、学校安排和公司政策允许范围内工作。</Checkbox></Form.Item></Space><Button type="primary" htmlType="submit" block loading={saving} disabled={saving || !document.consentScope}>签署并提交</Button></Form></Card></main></Layout>
}
