import {
  DeleteOutlined,
  EyeOutlined,
  FileImageOutlined,
  PlusOutlined,
  UploadOutlined,
  VideoCameraOutlined,
} from '@ant-design/icons'
import {
  Alert,
  App as AntdApp,
  Button,
  Card,
  Checkbox,
  DatePicker,
  Form,
  Image,
  Input,
  InputNumber,
  Modal,
  Popconfirm,
  Segmented,
  Select,
  Space,
  Switch,
  Tag,
  Tooltip,
  Typography,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import type { Dayjs } from 'dayjs'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import PageContainer from '../../../components/PageContainer'
import {
  buildAdvertisementUpsertPayload,
  createAdvertisement,
  deleteAdvertisement,
  enableAdvertisement,
  getAdvertisementById,
  getAdvertisementGrid,
  getAdvertisementStoreOptions,
  requestAdvertisementUploadSignature,
  resolveAdvertisementMediaType,
  stripAdvertisementMediaUrlQuery,
  updateAdvertisement,
  uploadAdvertisementFile,
} from '../../../services/advertisementService'
import {
  type BrandedStoreOption,
  type StoreOption,
} from '../../../services/storeService'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { useAuthStore } from '../../../store/auth'
import type {
  AdvertisementDetailDto,
  AdvertisementListDto,
  AdvertisementMediaType,
  AdvertisementOrientation,
} from '../../../types/advertisement'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { getAdvertisementStoreTagLabels } from './storeTagDisplay'
import {
  UNBRANDED_STORE_KEY,
  applyScopeSelection,
  buildStoreBrandGroups,
  filterStoresByBrand,
  getScopeSelectionState,
} from './storeBrandFilter'
import { readMediaDimensions } from './mediaDimensions'
import { resolveOrientationAfterUpload, toMediaSize } from './orientation'
import {
  ADVERTISEMENT_ORIENTATION_VALUES,
  DisplayPreview,
  OrientationCell,
  OrientationHelp,
  OrientationMismatchAlert,
  OrientationSegmented,
  getOrientationLabel,
} from './OrientationParts'
import advertisementsMessagesEn from './advertisementsMessages.en.json'
import advertisementsMessagesZh from './advertisementsMessages.zh.json'

// 页面级文案随页面代码块懒加载，不进首屏 i18n 包（首屏 gzip 预算很紧，见仓库约定）。
registerPageMessages({ zh: advertisementsMessagesZh, en: advertisementsMessagesEn })

/** 品牌筛选里「全部品牌」的取值；品牌键都是小写品牌名或 UNBRANDED_STORE_KEY，不会与之冲突。 */
const ALL_STORE_BRANDS = '__all__'
// 序号列固定宽度；横向滚动总宽要把它算进去，否则右侧列会被挤压。
const ROW_INDEX_COLUMN_WIDTH = 64

type AdvertisementRow = AdvertisementListDto & { key: string }

interface QueryFormValues {
  keyword?: string
  storeCode?: string
  mediaType?: AdvertisementMediaType
  /** 空字符串 = 全部（不过滤）。 */
  orientation?: AdvertisementOrientation | ''
  isEnabled?: boolean
  effectiveRange?: [Dayjs, Dayjs]
}

interface AdvertisementFormValues {
  title: string
  description?: string
  mediaType: AdvertisementMediaType
  mediaUrl: string
  thumbnailUrl?: string
  objectKey?: string
  originalFileName?: string
  contentType?: string
  fileSize?: number
  /** 版式必填；新建且读不到尺寸时为空，须管理员手动选择。 */
  orientation?: AdvertisementOrientation
  /** 素材像素宽高（上传时在浏览器端读取），读不到为 null。 */
  mediaWidth?: number | null
  mediaHeight?: number | null
  effectiveStart: Dayjs
  effectiveEnd: Dayjs
  isEnabled: boolean
  sortOrder: number
  stores: string[]
}

function formatDateTime(value?: string | null) {
  if (!value) return '--'
  const timestamp = Date.parse(value)
  if (Number.isNaN(timestamp)) return value
  return new Date(timestamp).toLocaleString()
}

function formatFileSize(fileSize?: number) {
  if (!fileSize) return '--'
  if (fileSize < 1024) return `${fileSize} B`
  if (fileSize < 1024 * 1024) return `${(fileSize / 1024).toFixed(1)} KB`
  return `${(fileSize / 1024 / 1024).toFixed(1)} MB`
}

function renderStoreTags(
  stores: AdvertisementListDto['stores'],
  storeOptions: readonly StoreOption[],
) {
  const labels = getAdvertisementStoreTagLabels(stores, storeOptions)
  return (
    <Space size={[4, 4]} wrap>
      {labels.map((label, index) => (
        <Tag key={`${label}-${index}`}>{label}</Tag>
      ))}
    </Space>
  )
}

function renderMediaThumb(record: AdvertisementListDto) {
  const previewUrl = record.thumbnailUrl || record.mediaUrl

  if (record.mediaType === 'Image' && previewUrl) {
    return (
      <Image
        src={previewUrl}
        alt={record.title}
        width={72}
        height={48}
        style={{ objectFit: 'cover', borderRadius: 6 }}
        preview={false}
      />
    )
  }

  if (record.thumbnailUrl) {
    return (
      <Image
        src={record.thumbnailUrl}
        alt={record.title}
        width={72}
        height={48}
        style={{ objectFit: 'cover', borderRadius: 6 }}
        preview={false}
      />
    )
  }

  return (
    <Space>
      {record.mediaType === 'Video' ? <VideoCameraOutlined /> : <FileImageOutlined />}
      <Typography.Text type="secondary">{record.mediaType}</Typography.Text>
    </Space>
  )
}

export default function AdvertisementsPage() {
  const { t } = useTranslation()
  const { message: messageApi } = AntdApp.useApp()
  const access = useAuthStore((state) => state.access)
  const [queryForm] = Form.useForm<QueryFormValues>()
  const [editorForm] = Form.useForm<AdvertisementFormValues>()
  const [loading, setLoading] = useState(false)
  const [data, setData] = useState<AdvertisementRow[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [sortField, setSortField] = useState<string | undefined>('sortOrder')
  const [sortOrder, setSortOrder] = useState<'ascend' | 'descend' | undefined>('ascend')
  const [storeOptions, setStoreOptions] = useState<BrandedStoreOption[]>([])
  // 编辑弹窗里分店范围的品牌筛选；null 表示全部品牌。只影响下拉可选项与全选范围，不改已选分店。
  const [storeBrandKey, setStoreBrandKey] = useState<string | null>(null)
  const [editorOpen, setEditorOpen] = useState(false)
  const [editorSaving, setEditorSaving] = useState(false)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [uploading, setUploading] = useState(false)
  const [previewOpen, setPreviewOpen] = useState(false)
  const [previewRecord, setPreviewRecord] = useState<AdvertisementListDto | null>(null)
  const [togglingId, setTogglingId] = useState<string | null>(null)
  // 「已按尺寸自动选择」标签：上传后按宽高自动预选时为 true，管理员手动改动后消失。
  const [orientationAutoSelected, setOrientationAutoSelected] = useState(false)
  const mainListRequestGuardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)
  const fileInputRef = useRef<HTMLInputElement | null>(null)

  const selectedStores = Form.useWatch('stores', editorForm) ?? []
  const currentMediaType = Form.useWatch('mediaType', editorForm)
  const currentMediaUrl = Form.useWatch('mediaUrl', editorForm)
  const currentThumbnailUrl = Form.useWatch('thumbnailUrl', editorForm)
  // 以下元数据没有挂 Form.Item（只经 setFieldsValue 写入表单 store），必须用 preserve 才能被 useWatch 读到。
  const currentOriginalFileName = Form.useWatch('originalFileName', { form: editorForm, preserve: true })
  const currentContentType = Form.useWatch('contentType', { form: editorForm, preserve: true })
  const currentFileSize = Form.useWatch('fileSize', { form: editorForm, preserve: true })
  const currentMediaWidth = Form.useWatch('mediaWidth', { form: editorForm, preserve: true })
  const currentMediaHeight = Form.useWatch('mediaHeight', { form: editorForm, preserve: true })
  const currentOrientation = Form.useWatch('orientation', editorForm)
  const currentMediaSize = toMediaSize(currentMediaWidth, currentMediaHeight)
  const storeBrandGroups = useMemo(() => buildStoreBrandGroups(storeOptions), [storeOptions])
  const brandFilteredStoreOptions = useMemo(
    () => filterStoresByBrand(storeOptions, storeBrandKey),
    [storeOptions, storeBrandKey],
  )
  const activeStoreBrandGroup = storeBrandGroups.find((group) => group.key === storeBrandKey)
  const storeScopeSelection = getScopeSelectionState(
    selectedStores,
    brandFilteredStoreOptions.map((item) => item.value),
  )
  // 下拉只列出当前品牌的分店，范围外已选的分店仍要显示店名而不是分店代码。
  const storeLabelByCode = useMemo(
    () => new Map(storeOptions.map((item) => [item.value, item.label])),
    [storeOptions],
  )

  const mediaTypeOptions = useMemo(
    () => [
      { label: t('posAdmin.advertisements.mediaTypes.image'), value: 'Image' },
      { label: t('posAdmin.advertisements.mediaTypes.video'), value: 'Video' },
    ],
    [t],
  )

  const orientationFilterOptions = useMemo(
    () => [
      { label: t('posAdmin.advertisements.orientationAll'), value: '' },
      ...ADVERTISEMENT_ORIENTATION_VALUES.map((orientation) => ({
        label: getOrientationLabel(t, orientation),
        value: orientation,
      })),
    ],
    [t],
  )

  const loadStoreOptions = async () => {
    try {
      const stores = await getAdvertisementStoreOptions()
      setStoreOptions(stores)
    } catch (error) {
      console.error(t('posAdmin.advertisements.loadStoresFailed'), error)
      messageApi.error(t('posAdmin.advertisements.loadStoresFailed'))
    }
  }

  const loadData = async () => {
    if (!mountedRef.current) {
      return
    }

    const values = queryForm.getFieldsValue()
    const sortModel: Record<string, string>[] = []

    if (sortField && sortOrder) {
      sortModel.push({
        ColId: sortField,
        Sort: sortOrder === 'ascend' ? 'asc' : 'desc',
      })
    }

    await runLatestGuardedRequest(mainListRequestGuardRef.current, () => getAdvertisementGrid({
        StartRow: (page - 1) * pageSize,
        EndRow: page * pageSize - 1,
        PageSize: pageSize,
        GlobalSearch: values.keyword || undefined,
        keyword: values.keyword || undefined,
        storeCode: values.storeCode || undefined,
        mediaType: values.mediaType || undefined,
        orientation: values.orientation || undefined,
        isEnabled: typeof values.isEnabled === 'boolean' ? values.isEnabled : undefined,
        effectiveStart: values.effectiveRange?.[0]?.startOf('day').toISOString(),
        effectiveEnd: values.effectiveRange?.[1]?.endOf('day').toISOString(),
        SortModel: sortModel.length ? sortModel : undefined,
      }), {
      onStart: () => setLoading(true),
      onSuccess: (result) => {
        const items = result?.items ?? []
        setData(items.map((item) => ({ ...item, key: String(item.id) })))
        setTotal(result?.total ?? 0)
      },
      onError: (error) => {
        console.error(t('posAdmin.advertisements.loadFailed'), error)
        messageApi.error(t('posAdmin.advertisements.loadFailed'))
      },
      // 旧请求完成时不能关闭后续请求的 loading。
      onSettled: () => setLoading(false),
    })
  }

  // mutation 等待期间可能发生翻页或排序，完成后始终使用当前 render 的查询条件刷新。
  const latestLoadDataRef = useRef(loadData)
  useLayoutEffect(() => {
    latestLoadDataRef.current = loadData
  })

  useLayoutEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      mainListRequestGuardRef.current.invalidate()
    }
  }, [])

  useEffect(() => {
    void loadStoreOptions()
  }, [])

  useEffect(() => {
    void loadData()
  }, [page, pageSize, sortField, sortOrder])

  const triggerSearch = () => {
    if (page !== 1) {
      setPage(1)
      return
    }
    void loadData()
  }

  const resetEditor = () => {
    setEditingId(null)
    setStoreBrandKey(null)
    setOrientationAutoSelected(false)
    editorForm.resetFields()
    editorForm.setFieldsValue({
      mediaType: 'Image',
      isEnabled: true,
      sortOrder: 0,
      stores: [],
      mediaUrl: '',
      thumbnailUrl: '',
      objectKey: '',
      originalFileName: '',
      contentType: '',
      fileSize: undefined,
      orientation: undefined,
      mediaWidth: null,
      mediaHeight: null,
      effectiveStart: dayjs(),
      effectiveEnd: dayjs().add(7, 'day'),
    })
  }

  const openCreate = () => {
    resetEditor()
    setEditorOpen(true)
  }

  const openEdit = async (id: string) => {
    try {
      const detail: AdvertisementDetailDto = await getAdvertisementById(id)
      setEditingId(id)
      setStoreBrandKey(null)
      // 打开编辑弹窗：沿用记录里的版式（service 已把缺省兜底为 Any）；只有重新上传素材才会按新尺寸预选。
      setOrientationAutoSelected(false)
      editorForm.setFieldsValue({
        title: detail.title,
        description: detail.description,
        mediaType: detail.mediaType,
        mediaUrl: stripAdvertisementMediaUrlQuery(detail.mediaUrl),
        thumbnailUrl: detail.thumbnailUrl,
        objectKey: detail.objectKey,
        originalFileName: detail.originalFileName,
        contentType: detail.contentType,
        fileSize: detail.fileSize,
        orientation: detail.orientation,
        mediaWidth: detail.mediaWidth ?? null,
        mediaHeight: detail.mediaHeight ?? null,
        effectiveStart: dayjs(detail.effectiveStart),
        effectiveEnd: dayjs(detail.effectiveEnd),
        isEnabled: detail.isEnabled,
        sortOrder: detail.sortOrder,
        stores: detail.stores?.map((store) => store.storeCode) ?? [],
      })
      setEditorOpen(true)
    } catch (error) {
      console.error(t('posAdmin.advertisements.loadDetailFailed'), error)
      messageApi.error(t('posAdmin.advertisements.loadDetailFailed'))
    }
  }

  const closeEditor = () => {
    setEditorOpen(false)
    setEditingId(null)
    setUploading(false)
    setOrientationAutoSelected(false)
    editorForm.resetFields()
  }

  const handleUploadClick = () => {
    if (!access.canEditAdvertisements) return
    fileInputRef.current?.click()
  }

  const handleFileSelected = async (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    event.target.value = ''

    if (!file) {
      return
    }

    try {
      setUploading(true)
      const mediaType = resolveAdvertisementMediaType(file)
      // 与上传并行读取本地素材宽高；该 Promise 永不 reject，读不到（超时/失败）时为 null，不阻断上传。
      const dimensionsPromise = readMediaDimensions(file, mediaType)
      const signature = await requestAdvertisementUploadSignature({
        fileName: file.name,
        contentType: file.type || (mediaType === 'Video' ? 'video/mp4' : 'image/jpeg'),
        fileSize: file.size,
        mediaType,
      })
      const mediaUrl = await uploadAdvertisementFile(signature, file)
      const dimensions = await dimensionsPromise
      const nextValues: Partial<AdvertisementFormValues> = {
        mediaType,
        mediaUrl,
        objectKey: signature.objectKey,
        originalFileName: file.name,
        contentType: file.type || undefined,
        fileSize: file.size,
        // 换了素材就必须同步覆盖宽高（读不到也要清成 null），不能残留旧素材的尺寸。
        mediaWidth: dimensions?.width ?? null,
        mediaHeight: dimensions?.height ?? null,
      }

      // 每次上传成功都按新素材预选版式（新建 / 编辑一致，即使之前手动选过）；读不到宽高时保持当前版式。
      // 只打开编辑弹窗不会走到这里，记录里的版式保持不变。
      const nextOrientation = resolveOrientationAfterUpload(
        editorForm.getFieldValue('orientation'),
        dimensions?.width,
        dimensions?.height,
      )
      nextValues.orientation = nextOrientation.orientation
      setOrientationAutoSelected(nextOrientation.autoSelected)

      if (mediaType === 'Image') {
        nextValues.thumbnailUrl = mediaUrl
      }

      editorForm.setFieldsValue(nextValues)
      messageApi.success(t('posAdmin.advertisements.uploadSuccess'))
    } catch (error) {
      console.error(t('posAdmin.advertisements.uploadFailed'), error)
      messageApi.error(t('posAdmin.advertisements.uploadFailed'))
    } finally {
      setUploading(false)
    }
  }

  const handleSave = async () => {
    try {
      await editorForm.validateFields()
      // 上传元数据由 setFieldsValue 写入隐藏表单 store，保存时必须读取完整值集。
      const values = editorForm.getFieldsValue(true) as AdvertisementFormValues

      if (!values.mediaUrl) {
        messageApi.error(t('posAdmin.advertisements.mediaRequired'))
        return
      }

      if (values.effectiveEnd.isBefore(values.effectiveStart)) {
        messageApi.error(t('posAdmin.advertisements.invalidEffectiveRange'))
        return
      }

      setEditorSaving(true)
      const payload = buildAdvertisementUpsertPayload(values)

      if (editingId) {
        await updateAdvertisement(editingId, payload)
      } else {
        await createAdvertisement(payload)
      }

      messageApi.success(t('message.saveSuccess'))
      closeEditor()
      if (mountedRef.current) {
        await latestLoadDataRef.current()
      }
    } catch (error) {
      if (
        typeof error === 'object' &&
        error !== null &&
        'errorFields' in error &&
        Array.isArray((error as { errorFields?: unknown[] }).errorFields)
      ) {
        return
      }
      console.error(t('posAdmin.advertisements.saveFailed'), error)
      messageApi.error(error instanceof Error && error.message ? error.message : t('posAdmin.advertisements.saveFailed'))
    } finally {
      setEditorSaving(false)
    }
  }

  const handleDelete = async (id: string) => {
    try {
      await deleteAdvertisement(id)
      messageApi.success(t('message.deleteSuccess'))
      if (mountedRef.current) {
        await latestLoadDataRef.current()
      }
    } catch (error) {
      console.error(t('posAdmin.advertisements.deleteFailed'), error)
      messageApi.error(t('posAdmin.advertisements.deleteFailed'))
    }
  }

  const handleToggleEnable = async (record: AdvertisementRow, enable: boolean) => {
    try {
      setTogglingId(record.id)
      await enableAdvertisement(record.id, enable)
      messageApi.success(t('posAdmin.advertisements.toggleSuccess'))
      if (mountedRef.current) {
        await latestLoadDataRef.current()
      }
    } catch (error) {
      console.error(t('posAdmin.advertisements.toggleFailed'), error)
      messageApi.error(t('posAdmin.advertisements.toggleFailed'))
    } finally {
      setTogglingId(null)
    }
  }

  const columns: ColumnsType<AdvertisementRow> = [
    {
      // 序号接着服务端分页往下数：(页码-1)×每页条数 + 行下标 + 1。
      title: t('column.index'),
      key: 'rowIndex',
      width: ROW_INDEX_COLUMN_WIDTH,
      align: 'center',
      render: (_: unknown, __: AdvertisementRow, index: number) => (page - 1) * pageSize + index + 1,
    },
    {
      title: t('posAdmin.advertisements.preview'),
      dataIndex: 'mediaUrl',
      key: 'preview',
      width: 120,
      render: (_, record) => renderMediaThumb(record),
    },
    {
      title: t('posAdmin.advertisements.adTitle'),
      dataIndex: 'title',
      key: 'title',
      sorter: true,
      width: 220,
      render: (value: string, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{value}</Typography.Text>
          {record.description ? (
            <Typography.Text type="secondary" ellipsis={{ tooltip: record.description }}>
              {record.description}
            </Typography.Text>
          ) : null}
        </Space>
      ),
    },
    {
      title: t('posAdmin.advertisements.mediaType'),
      dataIndex: 'mediaType',
      key: 'mediaType',
      width: 120,
      render: (value: AdvertisementMediaType) => (
        <Tag color={value === 'Video' ? 'purple' : 'blue'}>
          {value === 'Video'
            ? t('posAdmin.advertisements.mediaTypes.video')
            : t('posAdmin.advertisements.mediaTypes.image')}
        </Tag>
      ),
    },
    {
      title: t('posAdmin.advertisements.orientation'),
      dataIndex: 'orientation',
      key: 'orientation',
      width: 210,
      render: (value: AdvertisementOrientation, record) => (
        <OrientationCell
          orientation={value}
          mediaWidth={record.mediaWidth}
          mediaHeight={record.mediaHeight}
        />
      ),
    },
    {
      title: t('posAdmin.advertisements.sortOrder'),
      dataIndex: 'sortOrder',
      key: 'sortOrder',
      sorter: true,
      width: 110,
    },
    {
      title: t('posAdmin.advertisements.stores'),
      dataIndex: 'stores',
      key: 'stores',
      width: 240,
      render: (stores: AdvertisementListDto['stores']) => renderStoreTags(stores, storeOptions),
    },
    {
      title: t('posAdmin.advertisements.effectiveRange'),
      key: 'effectiveRange',
      width: 220,
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>{formatDateTime(record.effectiveStart)}</Typography.Text>
          <Typography.Text type="secondary">{formatDateTime(record.effectiveEnd)}</Typography.Text>
        </Space>
      ),
    },
    {
      title: t('posAdmin.advertisements.enabled'),
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 110,
      render: (value: boolean, record) => (
        <Switch
          checked={value}
          disabled={!access.canEditAdvertisements}
          loading={togglingId === record.id}
          onChange={(checked) => void handleToggleEnable(record, checked)}
        />
      ),
    },
    {
      title: t('column.action'),
      key: 'action',
      width: 220,
      fixed: 'right',
      render: (_, record) => (
        <Space size="small" wrap>
          <Tooltip title={t('posAdmin.advertisements.preview')}>
            <Button
              icon={<EyeOutlined />}
              onClick={() => {
                setPreviewRecord(record)
                setPreviewOpen(true)
              }}
            />
          </Tooltip>
          <Button onClick={() => void openEdit(record.id)} disabled={!access.canEditAdvertisements}>
            {t('common.edit')}
          </Button>
          <Popconfirm
            title={t('posAdmin.advertisements.confirmDelete')}
            okText={t('common.delete')}
            cancelText={t('common.cancel')}
            okButtonProps={{ danger: true }}
            onConfirm={() => void handleDelete(record.id)}
            disabled={!access.canEditAdvertisements}
          >
            <Button danger icon={<DeleteOutlined />} disabled={!access.canEditAdvertisements}>
              {t('common.delete')}
            </Button>
          </Popconfirm>
        </Space>
      ),
    },
  ]

  return (
    <PageContainer
      title={t('posAdmin.advertisements.title')}
      subtitle={t('posAdmin.advertisements.subtitle')}
      extra={
        <Button
          type="primary"
          icon={<PlusOutlined />}
          disabled={!access.canEditAdvertisements}
          onClick={openCreate}
        >
          {t('posAdmin.advertisements.create')}
        </Button>
      }
    >
      <Card>
        <Form<QueryFormValues> form={queryForm} layout="inline" onFinish={triggerSearch}>
          <Form.Item name="keyword" label={t('posAdmin.advertisements.keyword')}>
            <Input
              allowClear
              placeholder={t('posAdmin.advertisements.keywordPlaceholder')}
              style={{ width: 220 }}
            />
          </Form.Item>
          <Form.Item name="storeCode" label={t('common.store')}>
            <Select
              allowClear
              showSearch
              optionFilterProp="label"
              options={storeOptions}
              style={{ width: 180 }}
            />
          </Form.Item>
          <Form.Item name="mediaType" label={t('posAdmin.advertisements.mediaType')}>
            <Select allowClear options={mediaTypeOptions} style={{ width: 140 }} />
          </Form.Item>
          <Form.Item name="orientation" label={t('posAdmin.advertisements.orientation')} initialValue="">
            <Select options={orientationFilterOptions} style={{ width: 120 }} />
          </Form.Item>
          <Form.Item name="isEnabled" label={t('posAdmin.advertisements.enabled')}>
            <Select
              allowClear
              style={{ width: 140 }}
              options={[
                { label: t('posAdmin.advertisements.enabledOptions.enabled'), value: true },
                { label: t('posAdmin.advertisements.enabledOptions.disabled'), value: false },
              ]}
            />
          </Form.Item>
          <Form.Item name="effectiveRange" label={t('posAdmin.advertisements.effectiveRange')}>
            <DatePicker.RangePicker showTime />
          </Form.Item>
          <Form.Item>
            <Space>
              <Button type="primary" htmlType="submit">
                {t('common.query')}
              </Button>
              <Button
                onClick={() => {
                  queryForm.resetFields()
                  triggerSearch()
                }}
              >
                {t('common.reset')}
              </Button>
            </Space>
          </Form.Item>
        </Form>
      </Card>

      <Card style={{ marginTop: 16 }}>
        {/* 常驻说明：客显在哪个广告位播哪种版式，以及没有匹配广告时的退回规则。 */}
        <Alert
          type="info"
          showIcon
          message={t('posAdmin.advertisements.orientationNotice')}
          style={{ marginBottom: 16 }}
        />
        <MeasuredTable<AdvertisementRow> metricId="pos-admin.advertisements.table-1"
          rowKey="key"
          loading={loading}
          dataSource={data}
          columns={columns}
          scroll={{ x: 1590 + ROW_INDEX_COLUMN_WIDTH }}
          pagination={{
            total,
            current: page,
            pageSize,
            showSizeChanger: true,
            pageSizeOptions: ['10', '20', '50', '100'],
          }}
          onChange={(pagination, _filters, sorter) => {
            const singleSorter = Array.isArray(sorter) ? sorter[0] : sorter
            setPage(pagination.current ?? 1)
            setPageSize(pagination.pageSize ?? 20)
            setSortField(
              singleSorter?.field ? String(singleSorter.field) : undefined,
            )
            setSortOrder(singleSorter?.order ?? undefined)
          }}
        />
      </Card>

      <Modal
        open={editorOpen}
        title={editingId ? t('posAdmin.advertisements.edit') : t('posAdmin.advertisements.create')}
        onCancel={closeEditor}
        onOk={() => void handleSave()}
        width={860}
        okButtonProps={{ disabled: !access.canEditAdvertisements, loading: editorSaving }}
        destroyOnHidden
      >
        <Form<AdvertisementFormValues> form={editorForm} layout="vertical" disabled={!access.canEditAdvertisements}>
          <Space style={{ width: '100%' }} wrap>
            <Form.Item
              name="title"
              label={t('posAdmin.advertisements.adTitle')}
              rules={[{ required: true, message: t('posAdmin.advertisements.titleRequired') }]}
              style={{ minWidth: 260, flex: 1 }}
            >
              <Input maxLength={120} />
            </Form.Item>
            <Form.Item name="mediaType" label={t('posAdmin.advertisements.mediaType')} rules={[{ required: true }]}>
              <Select options={mediaTypeOptions} style={{ width: 160 }} />
            </Form.Item>
            <Form.Item name="sortOrder" label={t('posAdmin.advertisements.sortOrder')}>
              <InputNumber style={{ width: 140 }} />
            </Form.Item>
            <Form.Item name="isEnabled" label={t('posAdmin.advertisements.enabled')} valuePropName="checked">
              <Switch />
            </Form.Item>
          </Space>

          <Form.Item name="description" label={t('posAdmin.advertisements.description')}>
            <Input.TextArea rows={3} maxLength={500} showCount />
          </Form.Item>

          <Space style={{ width: '100%' }} wrap align="start">
            <Form.Item
              name="effectiveStart"
              label={t('posAdmin.advertisements.effectiveStart')}
              rules={[{ required: true, message: t('posAdmin.advertisements.effectiveStartRequired') }]}
            >
              <DatePicker showTime style={{ width: 240 }} />
            </Form.Item>
            <Form.Item
              name="effectiveEnd"
              label={t('posAdmin.advertisements.effectiveEnd')}
              rules={[{ required: true, message: t('posAdmin.advertisements.effectiveEndRequired') }]}
            >
              <DatePicker showTime style={{ width: 240 }} />
            </Form.Item>
          </Space>

          <Form.Item
            label={t('posAdmin.advertisements.stores')}
            required
          >
            <Space direction="vertical" style={{ width: '100%' }} size={8}>
              {storeBrandGroups.length > 1 && (
                <Space size={8} wrap>
                  <Typography.Text type="secondary">{t('posAdmin.advertisements.brandFilter')}</Typography.Text>
                  <Segmented
                    size="small"
                    aria-label={t('posAdmin.advertisements.brandFilter')}
                    value={storeBrandKey ?? ALL_STORE_BRANDS}
                    onChange={(value) => setStoreBrandKey(value === ALL_STORE_BRANDS ? null : String(value))}
                    options={[
                      {
                        value: ALL_STORE_BRANDS,
                        label: `${t('posAdmin.advertisements.allBrands')} · ${storeOptions.length}`,
                      },
                      ...storeBrandGroups.map((group) => ({
                        value: group.key,
                        label: `${group.brandName ?? t('posAdmin.advertisements.unbrandedStores')} · ${group.count}`,
                      })),
                    ]}
                  />
                </Space>
              )}
              <Checkbox
                checked={storeScopeSelection.checked}
                indeterminate={storeScopeSelection.indeterminate}
                disabled={brandFilteredStoreOptions.length === 0}
                onChange={(event) =>
                  editorForm.setFieldsValue({
                    stores: applyScopeSelection(
                      editorForm.getFieldValue('stores') ?? [],
                      brandFilteredStoreOptions.map((item) => item.value),
                      event.target.checked,
                    ),
                  })
                }
              >
                {activeStoreBrandGroup
                  ? t('posAdmin.advertisements.selectAllBrandStores', {
                      brand:
                        activeStoreBrandGroup.key === UNBRANDED_STORE_KEY
                          ? t('posAdmin.advertisements.unbrandedStores')
                          : activeStoreBrandGroup.brandName,
                      count: activeStoreBrandGroup.count,
                    })
                  : t('posAdmin.advertisements.selectAllStoresWithCount', { count: storeOptions.length })}
              </Checkbox>
              <Form.Item
                name="stores"
                noStyle
                rules={[{ required: true, message: t('posAdmin.advertisements.storesRequired') }]}
              >
                <Select
                  mode="multiple"
                  allowClear
                  showSearch
                  optionFilterProp="label"
                  options={brandFilteredStoreOptions}
                  labelRender={(option) => storeLabelByCode.get(String(option.value)) ?? option.label}
                  placeholder={t('posAdmin.advertisements.storesPlaceholder')}
                />
              </Form.Item>
            </Space>
          </Form.Item>

          <Card
            size="small"
            title={t('posAdmin.advertisements.mediaSection')}
            extra={
              <Space>
                <Button
                  icon={<UploadOutlined />}
                  loading={uploading}
                  onClick={handleUploadClick}
                  disabled={!access.canEditAdvertisements}
                >
                  {t('common.upload')}
                </Button>
                <Button
                  icon={<EyeOutlined />}
                  disabled={!currentMediaUrl}
                  onClick={() => {
                    setPreviewRecord({
                      id: editingId || 'preview',
                      title: editorForm.getFieldValue('title') || t('posAdmin.advertisements.preview'),
                      description: editorForm.getFieldValue('description'),
                      mediaType: currentMediaType || 'Image',
                      mediaUrl: currentMediaUrl,
                      thumbnailUrl: currentThumbnailUrl,
                      objectKey: editorForm.getFieldValue('objectKey'),
                      originalFileName: editorForm.getFieldValue('originalFileName'),
                      contentType: editorForm.getFieldValue('contentType'),
                      fileSize: editorForm.getFieldValue('fileSize'),
                      orientation: currentOrientation ?? 'Any',
                      mediaWidth: currentMediaSize?.width ?? null,
                      mediaHeight: currentMediaSize?.height ?? null,
                      effectiveStart: editorForm.getFieldValue('effectiveStart')?.toISOString?.() || '',
                      effectiveEnd: editorForm.getFieldValue('effectiveEnd')?.toISOString?.() || '',
                      isEnabled: editorForm.getFieldValue('isEnabled') ?? true,
                      sortOrder: editorForm.getFieldValue('sortOrder') ?? 0,
                      stores: (editorForm.getFieldValue('stores') || []).map((storeCode: string) => ({
                        storeCode,
                      })),
                    })
                    setPreviewOpen(true)
                  }}
                >
                  {t('posAdmin.advertisements.preview')}
                </Button>
              </Space>
            }
          >
            <input
              ref={fileInputRef}
              type="file"
              accept="image/*,video/*"
              style={{ display: 'none' }}
              onChange={(event) => void handleFileSelected(event)}
            />

            <Space direction="vertical" style={{ width: '100%' }} size={12}>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 20, alignItems: 'flex-start' }}>
                <div style={{ flex: '1 1 280px', minWidth: 260 }}>
                  {/* 素材信息：文件名、类型/大小、像素尺寸（宽高比） */}
                  <div
                    style={{
                      padding: '8px 12px',
                      border: '1px solid rgba(5, 5, 5, 0.06)',
                      borderRadius: 8,
                      marginBottom: 12,
                    }}
                  >
                    <Typography.Text strong style={{ display: 'block', wordBreak: 'break-all' }}>
                      {currentOriginalFileName || '--'}
                    </Typography.Text>
                    <Typography.Text type="secondary" style={{ display: 'block', fontSize: 12 }}>
                      {currentContentType || '--'} · {formatFileSize(currentFileSize)}
                    </Typography.Text>
                    <Typography.Text type="secondary" style={{ display: 'block', fontSize: 12 }}>
                      {currentMediaSize
                        ? t('posAdmin.advertisements.mediaDimensions', {
                            width: currentMediaSize.width,
                            height: currentMediaSize.height,
                            ratio: (currentMediaSize.width / currentMediaSize.height).toFixed(2),
                          })
                        : t('posAdmin.advertisements.sizeUnknown')}
                    </Typography.Text>
                  </div>

                  <Form.Item
                    name="orientation"
                    label={
                      <Space size={8}>
                        {t('posAdmin.advertisements.orientation')}
                        {orientationAutoSelected ? (
                          <Tag color="success" style={{ marginInlineEnd: 0 }}>
                            {t('posAdmin.advertisements.orientationAutoSelected')}
                          </Tag>
                        ) : null}
                      </Space>
                    }
                    rules={[{ required: true, message: t('posAdmin.advertisements.orientationRequired') }]}
                    style={{ marginBottom: 8 }}
                  >
                    <OrientationSegmented
                      disabled={!access.canEditAdvertisements}
                      onUserChange={() => setOrientationAutoSelected(false)}
                    />
                  </Form.Item>
                  <OrientationHelp />
                </div>

                <DisplayPreview
                  orientation={currentOrientation}
                  mediaType={currentMediaType}
                  mediaUrl={currentMediaUrl}
                  posterUrl={currentThumbnailUrl}
                  mediaWidth={currentMediaSize?.width ?? null}
                  mediaHeight={currentMediaSize?.height ?? null}
                />
              </div>

              <OrientationMismatchAlert
                orientation={currentOrientation}
                mediaWidth={currentMediaSize?.width ?? null}
                mediaHeight={currentMediaSize?.height ?? null}
              />

              <Form.Item name="mediaUrl" label={t('posAdmin.advertisements.mediaUrl')} rules={[{ required: true, message: t('posAdmin.advertisements.mediaRequired') }]}>
                <Input disabled placeholder={t('posAdmin.advertisements.mediaUrlPlaceholder')} />
              </Form.Item>
              <Form.Item name="thumbnailUrl" label={t('posAdmin.advertisements.thumbnailUrl')}>
                <Input placeholder={t('posAdmin.advertisements.thumbnailUrlPlaceholder')} />
              </Form.Item>

              <Form.Item name="objectKey" label={t('posAdmin.advertisements.objectKey')}>
                <Input disabled placeholder={t('posAdmin.advertisements.objectKeyPlaceholder')} />
              </Form.Item>
            </Space>
          </Card>
        </Form>
      </Modal>

      <Modal
        open={previewOpen}
        title={previewRecord?.title || t('posAdmin.advertisements.preview')}
        footer={null}
        onCancel={() => {
          setPreviewOpen(false)
          setPreviewRecord(null)
        }}
        width={760}
      >
        {previewRecord?.mediaType === 'Video' ? (
          <video
            src={previewRecord.mediaUrl}
            poster={previewRecord.thumbnailUrl}
            controls
            style={{ width: '100%', maxHeight: 460, borderRadius: 8, background: '#000' }}
          />
        ) : (
          <Image
            src={previewRecord?.mediaUrl}
            alt={previewRecord?.title}
            width="100%"
            style={{ borderRadius: 8 }}
          />
        )}
        <Space direction="vertical" style={{ width: '100%', marginTop: 16 }}>
          <Typography.Text>{previewRecord?.description || '--'}</Typography.Text>
          <Typography.Text type="secondary">
            {t('posAdmin.advertisements.mediaUrl')}: {previewRecord?.mediaUrl || '--'}
          </Typography.Text>
          <Typography.Text type="secondary">
            {t('posAdmin.advertisements.thumbnailUrl')}: {previewRecord?.thumbnailUrl || '--'}
          </Typography.Text>
        </Space>
      </Modal>
    </PageContainer>
  )
}
