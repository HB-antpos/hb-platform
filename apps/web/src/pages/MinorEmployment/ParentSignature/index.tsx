import { CheckCircleOutlined, EditOutlined, FileTextOutlined, SafetyCertificateOutlined, UndoOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Checkbox, Form, Layout, Progress, Result, Spin, Typography, message, Space } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'

import { getGuardianSession, getParentSignatureDocument, guardianErrorCode, submitParentSignature } from '../../../services/minorEmploymentService'
import type { GuardianSession, ParentSignatureDocument } from '../../../types/minorEmployment'

import { changedSections, toAmendments, toReviewValues } from './guardianAmendments'
import type { GuardianReviewValues } from './guardianAmendments'
import GuardianEmailVerification from './GuardianEmailVerification'
import { guardianErrorText } from './guardianErrors'
import GuardianReviewForm from './GuardianReviewForm'
import { AMENDABLE_SECTION_LABELS_EN, GUARDIAN_SECTION_LABELS, completenessContext, evaluateGuardianSections, guardianProgress, prefillReviewValues, sectionsMissingRequired } from './guardianSections'

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

const INVALID_LINK = 'This signing link is invalid or has expired. Please ask the store to send you a new link.'
const FORM_TITLES: Record<ParentSignatureDocument['formType'], string> = {
  NSW_COMPANY_CONSENT: 'NSW Minor Employee Consent',
  QLD_CE1: 'QLD CE1 Parent Consent',
}
const loadErrorText = (error: unknown) => guardianErrorText(error, guardianErrorCode(error), INVALID_LINK)
const fullName = (values?: GuardianReviewValues) => [values?.child?.givenName, values?.child?.familyName].map((x) => (x ?? '').trim()).filter(Boolean).join(' ')

export default function ParentSignaturePage() {
  const [params] = useSearchParams()
  const { token: pathToken } = useParams<{ token: string }>()
  const token = pathToken || params.get('token') || (window.location.hash.startsWith('#token=') ? decodeURIComponent(window.location.hash.slice('#token='.length)) : '')
  const [document, setDocument] = useState<ParentSignatureDocument | null>(null)
  const [session, setSession] = useState<GuardianSession | null>(null)
  const [sessionKey, setSessionKey] = useState('')
  const [loadError, setLoadError] = useState('')
  const [loading, setLoading] = useState(true)
  // 签完后的结果页需要的信息（表单卸载后取不到监护人填写的值）。
  const [submitted, setSubmitted] = useState<{ childFirstName: string; amended: boolean } | null>(null)
  const [signature, setSignature] = useState('')
  const [saving, setSaving] = useState(false)
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const drawingRef = useRef(false)
  const [form] = Form.useForm()
  const [reviewForm] = Form.useForm<GuardianReviewValues>()
  // editing 控制核对 / 修改切换；缺必填时默认进入可编辑（见下方 effect）。
  const [editing, setEditing] = useState(false)
  const signerName = Form.useWatch(['guardian', 'name'], reviewForm)
  // 监听整份资料：分区完整性、进度与"已修改"标记都随家长输入实时更新。
  const watchedValues = Form.useWatch((values: GuardianReviewValues) => values, { form: reviewForm, preserve: true })
  const loadDocument = useCallback(async (key: string) => {
    const value = await getParentSignatureDocument(token, key)
    setDocument(value)
  }, [token])
  // 先查会话：本标签页已验证过就直接取表单，否则停在验证码页，验证前不请求任何孩子资料。
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
    void startSession().catch((error) => { if (!cancelled) setLoadError(loadErrorText(error)) }).finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [token, startSession])

  // baseline = 后端当前资料（用于生成 amendments）；formInitial = 表单初始值（可能带学校名预填）。
  const baseline = useMemo(() => (document ? toReviewValues(document.snapshot) : null), [document])
  const formInitial = useMemo(() => (baseline ? prefillReviewValues(baseline) : null), [baseline])
  const context = useMemo(() => (document ? completenessContext(document.snapshot) : {}), [document])
  // 表单挂载前 useWatch 拿到的是空对象，先用初始值兜底。
  const liveValues = watchedValues && watchedValues.child ? watchedValues : formInitial
  const sectionStates = useMemo(() => (liveValues ? evaluateGuardianSections(liveValues, context) : []), [liveValues, context])
  const changed = useMemo(() => (formInitial && liveValues ? changedSections(formInitial, liveValues) : []), [formInitial, liveValues])
  const initiallyIncomplete = useMemo(() => (formInitial ? sectionsMissingRequired(evaluateGuardianSections(formInitial, context)).length > 0 : false), [formInitial, context])
  // 每份文件只决定一次默认模式：有分区缺必填就直接可编辑，全部齐全则保持"先核对、再修改"。
  const modeDecidedFor = useRef('')
  useEffect(() => {
    if (!document) return
    const key = `${document.id}:${document.version}:${document.revision}`
    if (modeDecidedFor.current === key) return
    modeDecidedFor.current = key
    setEditing(initiallyIncomplete)
  }, [document, initiallyIncomplete])

  if (loading) return <Spin fullscreen />
  if (!token || loadError) return <Result status="error" title="We can't open this signing link" subTitle={loadError || INVALID_LINK} />
  if (submitted) {
    return (
      <Result
        status="success"
        icon={<CheckCircleOutlined />}
        title="Thank you — your signature has been received"
        subTitle={`HR will now review the form. ${submitted.childFirstName || 'Your child'} doesn't need to do anything else.${submitted.amended ? ' The details you added or updated have been saved with your signature.' : ''} You can close this page.`}
      />
    )
  }
  if (!document && session) return <GuardianEmailVerification token={token} session={session} onVerified={(key) => { storeSessionKey(token, key); setSessionKey(key); setLoading(true); void loadDocument(key).then(() => setSession(null)).catch((error) => setLoadError(loadErrorText(error))).finally(() => setLoading(false)) }} />
  if (!document || !baseline || !formInitial) return <Result status="error" title="We can't open this signing link" subTitle={INVALID_LINK} />

  const missingSections = sectionsMissingRequired(sectionStates)
  const progress = guardianProgress(sectionStates)
  const childName = fullName(liveValues ?? undefined) || document.employeeName
  const childFirstName = (liveValues?.child?.givenName ?? '').trim() || childName
  const employer = document.employerName || 'Hot Bargain'
  const dateOfBirth = String(document.snapshot.dateOfBirth ?? document.snapshot.DateOfBirth ?? '').slice(0, 10)
  const readonlyFacts = [
    { label: 'Date of birth', value: dateOfBirth },
    { label: 'Employer', value: document.employerName },
    { label: 'State', value: document.state },
    // 只发短信时门店可能没登记监护人邮箱：为空就不显示这一行。
    ...(document.parentEmail ? [{ label: 'Parent / guardian email', value: document.parentEmail, hint: "Entered by the store. Contact the store if it's wrong." }] : []),
  ]
  const undoAll = () => { reviewForm.resetFields(); setEditing(initiallyIncomplete) }
  const consentLines = [
    `I consent to ${childName} working at ${employer} only within the hours allowed by law, the applicable Award, the school timetable and company policy.`,
    document.formType === 'QLD_CE1'
      ? 'This form follows the Queensland CE1 parent consent form. The school hours and holidays given here are true and current.'
      : `This is ${employer}'s NSW minor employee consent form.`,
    'Parent consent does not remove any legal limits on working hours, breaks or safety.',
    'The backup contact is a different person from me, and may be contacted only when needed for safety.',
  ]

  const submit = async (values: Record<string, unknown>) => {
    if (missingSections.length) { message.error(`Please complete: ${missingSections.map((x) => GUARDIAN_SECTION_LABELS[x.key]).join(', ')}`); return }
    if (!signature) { message.error('Please sign in the box above'); return }
    if (saving || !document.consentScope) return
    if (editing) {
      try {
        await reviewForm.validateFields()
      } catch {
        message.error('Some details need fixing. Please check the fields marked in red.')
        return
      }
    }
    const current = reviewForm.getFieldsValue(true)
    // 家长填了或改了任何分区都会带上（与后端当前资料比对），后端据此补全资料后再做完整性校验。
    const amendments = toAmendments(baseline, current, document.snapshot)
    const signedName = (current.guardian?.name ?? document.parentName ?? '').trim()
    setSaving(true)
    try {
      await submitParentSignature(token, {
        parentName: signedName, relationship: current.guardian?.relationship ?? '', parentPhone: current.guardian?.phone ?? '', parentEmail: document.parentEmail ?? '',
        signature, revision: document.revision, version: document.version, consentScope: document.consentScope,
        confirmRelationship: Boolean(values.confirmRelationship), confirmBackupContact: Boolean(values.confirmBackupContact), confirmConsent: Boolean(values.confirmConsent),
      }, sessionKey, amendments)
      clearStoredSessionKey()
      setSubmitted({ childFirstName: (current.child?.givenName ?? '').trim() || childFirstName, amended: Boolean(amendments) })
    } catch (error) {
      const errorCode = guardianErrorCode(error)
      if (errorCode === 'GUARDIAN_EMAIL_NOT_VERIFIED') {
        message.warning('Your verification has expired. Please enter a new code.')
        clearStoredSessionKey()
        setLoading(true)
        void startSession().catch((reason) => setLoadError(loadErrorText(reason))).finally(() => setLoading(false))
      } else {
        // 后端中文提示不直接展示：按错误码换成英文，便于家长当场改正。
        message.error(guardianErrorText(error, errorCode, "Your signature wasn't submitted. Please refresh the page and try again."), 6)
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
          <Typography.Text type="secondary">Hot Bargain · Parent / guardian consent</Typography.Text>
          <Typography.Title level={3} style={{ margin: '4px 0 8px' }}>{FORM_TITLES[document.formType] ?? 'Minor Employee Consent'}</Typography.Title>
          <div style={{ display: 'flex', flexWrap: 'wrap', justifyContent: 'space-between', gap: 8 }}>
            <Typography.Text>
              {childName}{document.employeeAge != null ? ` (${document.employeeAge})` : ''} · <Typography.Text strong>{document.state}</Typography.Text> · v{document.version}
            </Typography.Text>
            <Typography.Text strong style={{ color: '#1677ff' }}>{progress.complete} of {progress.total} complete</Typography.Text>
          </div>
          <Progress percent={progress.total ? Math.round((progress.complete / progress.total) * 100) : 0} showInfo={false} size="small" style={{ margin: '4px 0 12px' }} aria-label={`${progress.complete} of ${progress.total} sections complete`} />
          <Alert
            type="info"
            showIcon
            icon={<SafetyCertificateOutlined />}
            message={`Please confirm you are ${childFirstName}'s parent or guardian`}
            description={`This page records your consent as the parent or guardian. HR will review it. Signing does not allow ${childFirstName} to work more hours than the law, the applicable Award or company policy allow.`}
          />
        </Card>

        <Card size="small" title={<><FileTextOutlined /> Consent statement</>}>
          {consentLines.map((line) => <Typography.Paragraph key={line}>{line}</Typography.Paragraph>)}
          <Typography.Paragraph style={{ marginBottom: 0 }}>{document.consentScope}</Typography.Paragraph>
        </Card>

        {/* 补全 / 核对 / 修改：缺必填时默认可编辑；全部齐全时默认只读核对，点 Edit details 才可修改。 */}
        {editing ? (
          missingSections.length || initiallyIncomplete ? (
            <Alert
              type="warning"
              showIcon
              message="Please complete your child's details"
              description={<>
                <div>Fill in the sections marked &ldquo;Needs your input&rdquo;, check the rest, then sign at the bottom. Everything you enter is saved with your signature.</div>
                {changed.length ? <Button block icon={<UndoOutlined />} style={{ marginTop: 12 }} onClick={undoAll}>Undo all changes</Button> : null}
              </>}
            />
          ) : (
            <Alert
              type="warning"
              showIcon
              message="You're editing the details"
              description={<>
                <div>Make your changes below, then sign at the bottom. Your changes are saved with your signature.</div>
                <Button block icon={<UndoOutlined />} style={{ marginTop: 12 }} onClick={undoAll}>Undo all changes</Button>
              </>}
            />
          )
        ) : (
          <Alert
            type="info"
            showIcon
            message="Please check each section"
            description={<>
              <div>If anything is wrong, tap &ldquo;Edit details&rdquo; to correct it, then sign. {childFirstName} doesn&apos;t need to fill anything in.</div>
              <Button block type="primary" icon={<EditOutlined />} style={{ marginTop: 12 }} onClick={() => setEditing(true)}>Edit details</Button>
            </>}
          />
        )}

        <GuardianReviewForm
          form={reviewForm}
          initialValues={formInitial}
          editing={editing}
          changed={changed}
          sectionStates={sectionStates}
          schoolRequired={context.requiredToBeEnrolled !== false}
          readonlyFacts={readonlyFacts}
        />

        <Card title="Signature">
          <Form form={form} layout="vertical" onFinish={submit}>
            {changed.length ? (
              <Alert type="warning" showIcon style={{ marginBottom: 16 }} message={`You changed: ${changed.map((key) => AMENDABLE_SECTION_LABELS_EN[key]).join(', ')}`} description="These changes are saved with your signature, and HR can see what was changed." />
            ) : null}
            <Typography.Paragraph>Signing as <Typography.Text strong>{(signerName ?? formInitial.guardian.name) || '--'}</Typography.Text> (must match the guardian name above)</Typography.Paragraph>
            <Form.Item label="Sign here with your finger or a stylus" required>
              <canvas ref={canvasRef} width={700} height={180} aria-label="Signature pad" style={{ width: '100%', maxWidth: 700, height: 180, border: '1px dashed #91caff', borderRadius: 8, background: '#fff', touchAction: 'none' }} onPointerDown={(event) => { event.currentTarget.setPointerCapture(event.pointerId); drawingRef.current = true; const rect = event.currentTarget.getBoundingClientRect(); const ctx = event.currentTarget.getContext('2d'); ctx?.beginPath(); ctx?.moveTo((event.clientX - rect.left) * (700 / rect.width), (event.clientY - rect.top) * (180 / rect.height)) }} onPointerMove={(event) => { if (!drawingRef.current) return; const rect = event.currentTarget.getBoundingClientRect(); const ctx = event.currentTarget.getContext('2d'); if (!ctx) return; ctx.lineWidth = 2; ctx.lineCap = 'round'; ctx.lineTo((event.clientX - rect.left) * (700 / rect.width), (event.clientY - rect.top) * (180 / rect.height)); ctx.stroke() }} onPointerUp={(event) => { drawingRef.current = false; setSignature(event.currentTarget.toDataURL('image/png')) }} onPointerLeave={() => { drawingRef.current = false }} />
              <Button type="link" onClick={() => { const canvas = canvasRef.current; canvas?.getContext('2d')?.clearRect(0, 0, 700, 180); setSignature('') }}>Clear signature</Button>
            </Form.Item>
            <Space direction="vertical" style={{ width: '100%' }}>
              <Form.Item name="confirmRelationship" valuePropName="checked" rules={[{ validator: (_, value) => value ? Promise.resolve() : Promise.reject(new Error('Please confirm you are the parent or guardian')) }]}><Checkbox>I confirm I am {childFirstName}&apos;s parent or guardian and the relationship above is correct.</Checkbox></Form.Item>
              <Form.Item name="confirmBackupContact" valuePropName="checked" rules={[{ validator: (_, value) => value ? Promise.resolve() : Promise.reject(new Error('Please confirm the backup contact')) }]}><Checkbox>I have checked the backup contact and agree they may be contacted for safety reasons.</Checkbox></Form.Item>
              <Form.Item name="confirmConsent" valuePropName="checked" rules={[{ validator: (_, value) => value ? Promise.resolve() : Promise.reject(new Error('Please confirm the information and your consent')) }]}><Checkbox>The information is true, and I agree {childFirstName} will only work within the law, the Award, school arrangements and company policy.</Checkbox></Form.Item>
            </Space>
            {missingSections.length ? (
              <Alert
                type="warning"
                showIcon
                style={{ marginBottom: 16 }}
                message="Some required details are still missing"
                description={`Please complete: ${missingSections.map((x) => GUARDIAN_SECTION_LABELS[x.key]).join(', ')}. You can sign once these are filled in.`}
              />
            ) : null}
            <Button type="primary" htmlType="submit" size="large" block loading={saving} disabled={saving || !document.consentScope || missingSections.length > 0}>{changed.length ? 'Save changes, sign and submit' : 'Sign and submit'}</Button>
          </Form>
        </Card>
      </main>
    </Layout>
  )
}
