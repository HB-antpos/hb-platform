import { CheckCircleOutlined, EditOutlined, FileTextOutlined, SafetyCertificateOutlined, UndoOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Checkbox, Descriptions, Form, Layout, Result, Spin, Tag, Typography, message, Space } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'

import { getGuardianSession, getParentSignatureDocument, guardianErrorCode, submitParentSignature } from '../../../services/minorEmploymentService'
import type { GuardianSession, ParentSignatureDocument } from '../../../types/minorEmployment'

import { AMENDABLE_SECTIONS, changedSections, toAmendments, toReviewValues } from './guardianAmendments'
import type { AmendableSection, GuardianReviewValues } from './guardianAmendments'
import GuardianEmailVerification from './GuardianEmailVerification'
import GuardianReviewForm from './GuardianReviewForm'

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
  const [reviewForm] = Form.useForm<GuardianReviewValues>()
  // 现场修改：editing 控制核对/修改切换，changed 记录与原始资料不同的分区（用于"已修改"标记和签名前汇总）。
  const [editing, setEditing] = useState(false)
  const [changed, setChanged] = useState<AmendableSection[]>([])
  const signerName = Form.useWatch(['guardian', 'name'], reviewForm)
  const loadDocument = useCallback(async (key: string) => {
    const value = await getParentSignatureDocument(token, key)
    setDocument(value)
  }, [token])
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
  const initialReview = useMemo(() => (document ? toReviewValues(document.snapshot) : null), [document])
  if (loading) return <Spin fullscreen />
  if (!token || loadError) return <Result status="error" title="签署链接无效或已过期" subTitle="请联系员工重新发送签署邮件。" />
  if (submitted) return <Result status="success" icon={<CheckCircleOutlined />} title="已完成家长签署" subTitle={changed.length ? '你修改的资料已一并保存，孩子不需要重新填写。员工还需要将已签文件提交 HR 审核。' : '员工还需要将已签文件提交 HR 审核。'} />
  if (!document && session) return <GuardianEmailVerification token={token} session={session} onVerified={(key) => { storeSessionKey(token, key); setSessionKey(key); setLoading(true); void loadDocument(key).then(() => setSession(null)).catch(() => setLoadError('签署链接无效或已过期')).finally(() => setLoading(false)) }} />
  if (!document || !initialReview) return <Result status="error" title="签署链接无效或已过期" subTitle="请联系员工重新发送签署邮件。" />

  const dateOfBirth = String(document.snapshot.dateOfBirth ?? document.snapshot.DateOfBirth ?? '').slice(0, 10)
  const readonlyFacts = [
    { label: '监护人邮箱', value: document.parentEmail, hint: '验证码发到这个邮箱，不能在此修改。填错了请让孩子在 App 里改正后重新发送。' },
    { label: '出生日期', value: dateOfBirth, hint: '以员工正式资料为准，有误请联系店长或 HR。' },
    { label: '雇主', value: document.employerName, hint: '由公司填写。' },
  ]
  const refreshChanged = () => setChanged(changedSections(initialReview, reviewForm.getFieldsValue(true)))
  const undoAll = () => { reviewForm.resetFields(); setChanged([]); setEditing(false) }

  const submit = async (values: Record<string, unknown>) => {
    if (!signature) { message.error('请完成手写签名'); return }
    if (saving || !document.consentScope) return
    if (editing) {
      try {
        await reviewForm.validateFields()
      } catch {
        message.error('修改的资料还有问题，请检查标红的地方')
        return
      }
    }
    const current = reviewForm.getFieldsValue(true)
    const amendments = toAmendments(initialReview, current, document.snapshot)
    const signedName = (current.guardian?.name ?? document.parentName ?? '').trim()
    setSaving(true)
    try {
      await submitParentSignature(token, {
        parentName: signedName, relationship: current.guardian?.relationship ?? '', parentPhone: current.guardian?.phone ?? '', parentEmail: document.parentEmail ?? '',
        signature, revision: document.revision, version: document.version, consentScope: document.consentScope,
        confirmRelationship: Boolean(values.confirmRelationship), confirmBackupContact: Boolean(values.confirmBackupContact), confirmConsent: Boolean(values.confirmConsent),
      }, sessionKey, amendments)
      clearStoredSessionKey()
      setSubmitted(true)
    } catch (error) {
      if (guardianErrorCode(error) === 'GUARDIAN_EMAIL_NOT_VERIFIED') {
        message.warning('验证已过期，请重新输入邮箱验证码')
        clearStoredSessionKey()
        setLoading(true)
        void startSession().catch(() => setLoadError('签署链接无效或已过期')).finally(() => setLoading(false))
      } else {
        // 业务校验（如备用联系人与监护人同号）直接提示后端原因，便于监护人当场改正。
        message.error(error instanceof Error && error.message ? error.message.replace(/^[A-Z_]+:\s*/, '') : '签署未完成，请刷新后重试')
      }
    } finally {
      setSaving(false)
    }
  }

  return (
    <Layout style={{ minHeight: '100vh', background: '#f5f7fa', padding: 16 }}>
      <meta name="referrer" content="no-referrer" />
      <main style={{ maxWidth: 900, width: '100%', margin: '0 auto', display: 'grid', gap: 16 }}>
        <Card>
          <Typography.Text type="secondary">Hot Bargain · 未成年员工家长签署</Typography.Text>
          <Typography.Title level={3} style={{ marginTop: 4 }}>{document.formType === 'QLD_CE1' ? 'QLD CE1 家长同意表' : 'NSW 公司未成年员工同意书'}</Typography.Title>
          <Alert type="info" showIcon icon={<SafetyCertificateOutlined />} message={`请确认您是 ${document.employeeName} 的家长 / 监护人`} description="此页面记录家长/监护人的签署声明，HR 将审核材料。签署不会授权超出适用法律、Award 或公司政策的工时。" />
          <Descriptions bordered size="small" column={{ xs: 1, sm: 2 }} style={{ marginTop: 16 }} items={[{ label: '员工', children: `${document.employeeName}（${document.employeeAge ?? '--'}岁）` }, { label: '州 / 版本', children: <><Tag>{document.state}</Tag> v{document.version}</> }]} />
        </Card>

        <Card size="small" title={<><FileTextOutlined /> 同意声明</>}>
          <Typography.Paragraph>{document.consentScope}</Typography.Paragraph>
          {document.consentText.map((line) => <Typography.Paragraph key={line}>{line}</Typography.Paragraph>)}
        </Card>

        {/* 核对 / 修改切换：默认只读核对；修改后与签名一起提交，孩子不用回 App 重填。 */}
        {editing ? (
          <Alert type="warning" showIcon message="正在修改资料" description={<><div>直接在下面改正，改完到页面底部签字提交，修改会和签名一起保存。</div><Button block icon={<UndoOutlined />} style={{ marginTop: 12 }} onClick={undoAll}>撤销全部修改</Button></>} />
        ) : (
          <Alert type="info" showIcon message="请逐项核对孩子填写的资料" description={<><div>如有错误，点“修改资料”直接更正，改完签字即可，孩子不需要重新填写。</div><Button block type="primary" icon={<EditOutlined />} style={{ marginTop: 12 }} onClick={() => setEditing(true)}>修改资料</Button></>} />
        )}

        <GuardianReviewForm form={reviewForm} initialValues={initialReview} editing={editing} changed={changed} readonlyFacts={readonlyFacts} onValuesChange={refreshChanged} />

        <Card title="签名">
          <Form form={form} layout="vertical" onFinish={submit}>
            {changed.length ? (
              <Alert type="warning" showIcon style={{ marginBottom: 16 }} message={`你修改了：${changed.map((key) => AMENDABLE_SECTIONS[key]).join('、')}`} description="提交签名时会一并保存，并记录修改前后的内容供 HR 核对。" />
            ) : null}
            <Typography.Paragraph>签名人：<Typography.Text strong>{(signerName ?? initialReview.guardian.name) || '--'}</Typography.Text>（须与上方监护人姓名一致）</Typography.Paragraph>
            <Form.Item label="手写签名（请用手指或触控笔）" required>
              <canvas ref={canvasRef} width={700} height={180} style={{ width: '100%', maxWidth: 700, height: 180, border: '1px dashed #91caff', borderRadius: 8, background: '#fff', touchAction: 'none' }} onPointerDown={(event) => { event.currentTarget.setPointerCapture(event.pointerId); drawingRef.current = true; const rect = event.currentTarget.getBoundingClientRect(); const ctx = event.currentTarget.getContext('2d'); ctx?.beginPath(); ctx?.moveTo((event.clientX - rect.left) * (700 / rect.width), (event.clientY - rect.top) * (180 / rect.height)) }} onPointerMove={(event) => { if (!drawingRef.current) return; const rect = event.currentTarget.getBoundingClientRect(); const ctx = event.currentTarget.getContext('2d'); if (!ctx) return; ctx.lineWidth = 2; ctx.lineCap = 'round'; ctx.lineTo((event.clientX - rect.left) * (700 / rect.width), (event.clientY - rect.top) * (180 / rect.height)); ctx.stroke() }} onPointerUp={(event) => { drawingRef.current = false; setSignature(event.currentTarget.toDataURL('image/png')) }} onPointerLeave={() => { drawingRef.current = false }} />
              <Button type="link" onClick={() => { const canvas = canvasRef.current; canvas?.getContext('2d')?.clearRect(0, 0, 700, 180); setSignature('') }}>清除签名</Button>
            </Form.Item>
            <Space direction="vertical" style={{ width: '100%' }}>
              <Form.Item name="confirmRelationship" valuePropName="checked" rules={[{ validator: (_, value) => value ? Promise.resolve() : Promise.reject(new Error('请确认家长 / 监护人身份')) }]}><Checkbox>我确认自己是该员工的家长 / 监护人，并确认上方关系准确。</Checkbox></Form.Item>
              <Form.Item name="confirmBackupContact" valuePropName="checked" rules={[{ validator: (_, value) => value ? Promise.resolve() : Promise.reject(new Error('请确认备用联系人')) }]}><Checkbox>我确认已核对指定备用联系人，且其资料可用于必要的安全联系。</Checkbox></Form.Item>
              <Form.Item name="confirmConsent" valuePropName="checked" rules={[{ validator: (_, value) => value ? Promise.resolve() : Promise.reject(new Error('请确认同意范围')) }]}><Checkbox>我确认以上资料真实，并同意员工仅在适用法律、Award、学校安排和公司政策允许范围内工作。</Checkbox></Form.Item>
            </Space>
            <Button type="primary" htmlType="submit" size="large" block loading={saving} disabled={saving || !document.consentScope}>{changed.length ? '保存修改并签署提交' : '签署并提交'}</Button>
          </Form>
        </Card>
      </main>
    </Layout>
  )
}
