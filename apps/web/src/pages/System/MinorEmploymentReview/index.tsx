import { FileSearchOutlined, ReloadOutlined, SafetyCertificateOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Descriptions, Drawer, Empty, Form, Input, List, Modal, Pagination, Select, Space, Spin, Tag, Typography, message } from 'antd'
import { useEffect, useState } from 'react'

import PageContainer from '../../../components/PageContainer'
import { decideMinorEmploymentReview, fetchMinorEmploymentDocument, getMinorEmploymentHistory, getMinorEmploymentReview, getMinorEmploymentReviews } from '../../../services/minorEmploymentService'
import type { MinorEmploymentHistoryEntry, MinorEmploymentReviewDetail, MinorEmploymentReviewSummary, MinorEmploymentState, MinorEmploymentStateCode } from '../../../types/minorEmployment'
import type { AmendableSection } from '../../MinorEmployment/ParentSignature/guardianAmendments'
import { AMENDABLE_SECTIONS } from '../../MinorEmployment/ParentSignature/guardianAmendments'
import RecordDetails from '../../MinorEmployment/RecordDetails'

const statusLabel: Record<MinorEmploymentState, string> = {
  Draft: '草稿', AwaitingParentSignature: '待家长签字', SignedAwaitingSubmission: '已签待提交', PendingReview: '待审核',
  Approved: '审核通过', Returned: '退回修改', Withdrawn: '已撤回', Expired: '已失效',
}
const stateLabel: Record<MinorEmploymentStateCode, string> = { NSW: 'NSW', QLD: 'QLD' }

function statusColor(status: MinorEmploymentState) {
  return status === 'Approved' ? 'green' : status === 'Returned' ? 'red' : status === 'PendingReview' ? 'gold' : 'default'
}

export default function MinorEmploymentReviewPage() {
  const [rows, setRows] = useState<MinorEmploymentReviewSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [state, setState] = useState<MinorEmploymentStateCode | undefined>()
  const [status, setStatus] = useState<MinorEmploymentState | undefined>('PendingReview')
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [total, setTotal] = useState(0)
  const [selected, setSelected] = useState<MinorEmploymentReviewDetail | null>(null)
  const [drawerLoading, setDrawerLoading] = useState(false)
  const [decisionLoading, setDecisionLoading] = useState(false)
  const [returnOpen, setReturnOpen] = useState(false)
  const [documentLoading, setDocumentLoading] = useState(false)
  const [history, setHistory] = useState<MinorEmploymentHistoryEntry[]>([])
  const [historyOpen, setHistoryOpen] = useState(false)
  const [returnForm] = Form.useForm<{ reason: string; fields: string[] }>()

  const load = async () => {
    setLoading(true)
    try {
      const result = await getMinorEmploymentReviews({ page, pageSize, keyword: keyword || undefined, state, status })
      setRows(result.items)
      setTotal(result.total)
    } catch { message.error('未成年用工审核列表加载失败') } finally { setLoading(false) }
  }
  useEffect(() => { void load() }, [state, status, page, pageSize])

  const open = async (row: MinorEmploymentReviewSummary) => {
    setDrawerLoading(true)
    try { setSelected(await getMinorEmploymentReview(row.id)) } catch { message.error('审核详情加载失败') } finally { setDrawerLoading(false) }
  }

  const approve = async () => {
    if (!selected) return
    setDecisionLoading(true)
    try { setSelected(await decideMinorEmploymentReview(selected.id, { action: 'approve', expectedRevision: selected.revision, version: selected.version })); await load(); message.success('已通过审核，排班提醒仍独立保留') }
    catch (error) { message.error(error instanceof Error ? error.message : '审核状态已变化，请刷新后重试') }
    finally { setDecisionLoading(false) }
  }
  const returnReview = async (values: { reason: string; fields: string[] }) => {
    if (!selected) return
    setDecisionLoading(true)
    try { setSelected(await decideMinorEmploymentReview(selected.id, { action: 'return', expectedRevision: selected.revision, version: selected.version, reason: values.reason.trim(), fields: values.fields })); setReturnOpen(false); await load(); message.success('已退回补充，排班发布仍可继续') }
    catch (error) { message.error(error instanceof Error ? error.message : '审核状态已变化，请刷新后重试') }
    finally { setDecisionLoading(false) }
  }
  const openDocument = async () => {
    if (!selected) return
    setDocumentLoading(true)
    try { const result = await fetchMinorEmploymentDocument(selected.id); const url = URL.createObjectURL(result.blob); window.open(url, '_blank', 'noopener,noreferrer'); window.setTimeout(() => URL.revokeObjectURL(url), 60_000) }
    catch (error) { message.error(error instanceof Error ? error.message : '签署原件暂不可用') }
    finally { setDocumentLoading(false) }
  }
  const openHistory = async () => {
    if (!selected) return
    try { setHistory(await getMinorEmploymentHistory(selected.id)); setHistoryOpen(true) } catch { message.error('版本与审核历史加载失败') }
  }

  return <PageContainer title="未成年用工合规审核" subtitle="系统管理 / 员工个人信息维护 / 待审核">
    <Alert type="info" showIcon icon={<SafetyCertificateOutlined />} message="审核结果只更新合规资料状态，不阻断排班发布。工时、学校假期、周末和通勤风险继续显示为提醒。" style={{ marginBottom: 16 }} />
    <Card>
      <Space wrap style={{ marginBottom: 16 }}>
        <Input.Search allowClear placeholder="员工姓名 / 员工编号" value={keyword} onChange={(e) => setKeyword(e.target.value)} onSearch={() => void load()} style={{ width: 260 }} />
        <Select allowClear placeholder="工作州" value={state} onChange={(value) => { setPage(1); setState(value) }} options={[{ value: 'QLD', label: 'QLD' }, { value: 'NSW', label: 'NSW' }]} style={{ width: 120 }} />
        <Select value={status} onChange={(value) => { setPage(1); setStatus(value) }} options={Object.entries(statusLabel).map(([value, label]) => ({ value, label }))} style={{ width: 150 }} />
        <Button icon={<ReloadOutlined />} onClick={() => void load()}>刷新</Button>
      </Space>
      <List loading={loading} locale={{ emptyText: <Empty description="暂无未成年用工审核记录" /> }} dataSource={rows} renderItem={(row) => (<List.Item actions={[<Button key="view" type="link" icon={<FileSearchOutlined />} onClick={() => void open(row)}>查看审核</Button>]}> 
        <List.Item.Meta title={<Space><Typography.Text strong>{row.employeeName}</Typography.Text><Tag>{stateLabel[row.state]}</Tag><Tag color={statusColor(row.status)}>{statusLabel[row.status]}</Tag></Space>} description={<Space wrap><span>{row.formType === 'QLD_CE1' ? 'QLD CE1 家长同意表' : 'NSW 公司未成年员工同意书'}</span><span>版本 v{row.version}</span><span>{row.riskSummary || '提醒见排班评估'}</span></Space>} />
      </List.Item>)} />
      <Pagination current={page} pageSize={pageSize} total={total} showSizeChanger showTotal={(value) => `共 ${value} 条`} onChange={(nextPage, nextSize) => { setPage(nextSize !== pageSize ? 1 : nextPage); setPageSize(nextSize) }} style={{ marginTop: 16, textAlign: 'right' }} />
    </Card>
    <Drawer title={selected ? `${selected.employeeName} · ${selected.state} 审核详情` : '审核详情'} width={960} open={Boolean(selected)} onClose={() => setSelected(null)} destroyOnClose>
      {drawerLoading ? <Spin /> : selected ? <>
        <Alert type="warning" showIcon message="风险提醒不等于排班门禁" description="即使资料退回、工时超限或通勤安排待确认，店长仍可继续发布排班；本次审核不会清除这些提醒。" style={{ marginBottom: 16 }} />
        <Descriptions bordered size="small" column={2} items={[{ label: '员工', children: `${selected.employeeName}（${selected.age ?? '--'}岁）` }, { label: '门店 / 岗位', children: `${selected.storeName || '--'} / ${selected.roleName || '--'}` }, { label: '表单', children: selected.formType === 'QLD_CE1' ? 'QLD 官方 CE1（产品在线填写映射）' : 'NSW 公司未成年员工同意书' }, { label: '版本 / 修订', children: `v${selected.version} / ${selected.revision}` }, { label: '家长电话', children: selected.parent.phone || <Typography.Text type="danger">必填缺失</Typography.Text> }, { label: '家长邮箱', children: selected.parent.email || <Typography.Text type="danger">必填缺失</Typography.Text> }, { label: '备用联系人', children: `${selected.backupContact.name || '--'} · ${selected.backupContact.phone || '--'}` }, { label: '其他工作', children: selected.otherWork ? (selected.otherWork.hasOtherWork === true ? `已声明${selected.otherWork.hoursUnknown ? '（时数未知）' : ''}，雇主 ${selected.otherWork.employers?.length ?? 0} 个` : selected.otherWork.hasOtherWork === false ? '未声明其他工作' : '资料未返回') : '资料未返回' }]} />
        {/* 监护人在签署页现场改过资料：提示 HR 到「历史 / 审计」核对修改前后内容。 */}
        {selected.guardianAmendedFields.length ? <Alert type="warning" showIcon message={`监护人签署时修改了：${selected.guardianAmendedFields.map((key) => AMENDABLE_SECTIONS[key as AmendableSection] ?? key).join('、')}`} description="下方为修改后的内容；修改前后的完整值记录在历史中的「guardian_amended」审计里，请核对是否合理。" style={{ marginBottom: 16 }} /> : null}
        <RecordDetails snapshot={selected.snapshot} />
        <Card size="small" title="学校与排班事实" style={{ marginTop: 16 }}><Descriptions size="small" column={2} items={[{ label: '学校', children: selected.education?.schoolProvider || selected.education?.schoolName || '--' }, { label: '学校联系人', children: selected.education?.schoolContactName || '--' }, { label: '学期日期', children: selected.education?.termRanges?.length ? `${selected.education.termRanges.length} 段` : '--' }, { label: '学校假期', children: selected.education?.holidays?.length ? `${selected.education.holidays.length} 段` : '--' }, { label: '通勤', children: selected.rosterEvidence ? `${selected.rosterEvidence.afterSchoolToStoreMinutes ?? '--'} 分钟到店 · ${selected.rosterEvidence.homewardMinutes ?? '--'} 分钟回家` : '--' }, { label: '交通 / 接送', children: selected.rosterEvidence ? `${selected.rosterEvidence.transportMode || '--'} · ${selected.rosterEvidence.pickupPerson || '--'}` : '--' }]} /></Card>
        <Card size="small" title="风险提醒" style={{ marginTop: 16 }}>{selected.risks ? <List size="small" dataSource={selected.risks} locale={{ emptyText: '当前版本未计算出提醒' }} renderItem={(risk) => <List.Item><Space><Tag color={risk.level === 'high' ? 'red' : 'gold'}>{risk.level === 'high' ? '高风险提醒' : '提醒'}</Tag><span>{risk.title}：{risk.detail}</span><Typography.Text type="secondary">可继续发布</Typography.Text></Space></List.Item>} /> : <Alert type="info" showIcon message="风险计算结果未返回" description="当前页面不会推断为无风险；排班限制和提醒仍由后端规则持续计算。" />}</Card>
        <Space style={{ marginTop: 16 }}>
          <Button loading={documentLoading} onClick={() => void openDocument()}>只读查看签署原件</Button><Button onClick={() => void openHistory()}>版本与审核历史</Button>
          {selected.status === 'PendingReview' ? <><Button type="primary" loading={decisionLoading} onClick={() => void approve()}>审核通过</Button><Button danger loading={decisionLoading} onClick={() => { returnForm.resetFields(); setReturnOpen(true) }}>退回补充</Button></> : null}
        </Space>
        {selected.reviewReason ? <Alert type="error" message={`上次退回意见：${selected.reviewReason}`} style={{ marginTop: 16 }} /> : null}
      </> : null}
    </Drawer>
    <Modal title="版本与审核历史" open={historyOpen} footer={null} onCancel={() => setHistoryOpen(false)}><List dataSource={history} locale={{ emptyText: '暂无历史记录' }} renderItem={(item) => <List.Item><List.Item.Meta title={`${item.action}${item.version ? ` · v${item.version}` : ''}`} description={`${item.actor || '--'} · ${item.createdAt || '--'}${item.comment ? ` · ${item.comment}` : ''}`} /></List.Item>} /></Modal>
    <Modal title="退回补充" open={returnOpen} confirmLoading={decisionLoading} okText="确认退回" cancelText="取消" onCancel={() => setReturnOpen(false)} onOk={() => void returnForm.submit()}><Form form={returnForm} layout="vertical" onFinish={(values) => void returnReview(values)}><Form.Item name="fields" label="要求员工补充字段" rules={[{ required: true, type: 'array', min: 1, message: '至少选择一个字段' }]}><Select mode="multiple" options={[{ value: 'parent', label: '家长电话 / 邮箱' }, { value: 'education', label: '学校时间与假期' }, { value: 'backupContact', label: '备用联系人' }, { value: 'otherWork', label: '其他工作' }, { value: 'rosterEvidence', label: '通勤与排班事实' }]} /></Form.Item><Form.Item name="reason" label="退回意见" rules={[{ required: true, whitespace: true, message: '必须填写退回意见' }]}><Input.TextArea rows={4} placeholder="请说明员工需要补充或更正的内容" /></Form.Item></Form></Modal>
  </PageContainer>
}
