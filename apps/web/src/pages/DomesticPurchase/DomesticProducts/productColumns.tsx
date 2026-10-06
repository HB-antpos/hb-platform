import { EllipsisOutlined } from '@ant-design/icons'
import type { MenuProps } from 'antd'
import { Button, Dropdown } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { SortOrder } from 'antd/es/table/interface'
import type { TFunction } from 'i18next'
import type { DomesticProductItem, ProductType } from '../../../types/domesticProduct'
import { CopyableText, ProductStatusText, ProductTypeTag } from './ProductCells'
import ProductThumb from './ProductThumb'
import {
  LIST_COLUMN_WIDTHS,
  SORT_FIELD_BY_COLUMN,
  formatFullTimestamp,
  formatMoney,
  formatUpdatedParts,
  sortOrderForColumn,
  type SortOrderValue,
} from './domesticProductsLogic'
import { rowSerialNumber } from '../tableWidthLogic'
import './domesticProducts.css'

/** 列头只开放升 / 降序两态：服务端查询必须有明确排序，不提供「取消排序」。 */
const SORT_DIRECTIONS: SortOrder[] = ['ascend', 'descend']

export interface ProductColumnsOptions {
  t: TFunction
  sortField?: string
  sortOrder?: SortOrderValue
  canWrite: boolean
  /** 用于「更新」列：当年的日期省略年份。 */
  currentYear: number
  /** 当前页码与每页条数：序号列跨页连续编号。 */
  page: number
  pageSize: number
  /** 商品列、供应商列的宽度（由表格可用宽度算出，见 resolveListTableLayout）。 */
  productWidth: number
  supplierWidth: number
  onEdit: (record: DomesticProductItem) => void
  /** 行末「更多」菜单，由页面提供（菜单项要调用页面里的打开详情 / 套装子项 / 复制等动作）。 */
  getRowMenu: (record: DomesticProductItem) => MenuProps
}

/**
 * 国内商品列表的列：勾选列由 rowSelection 提供，这里是其余 10 列，合计 11 列
 * （旧版 19 列 2506px）：序号 / 商品 / HB 货号 / 条码 / 供应商 / 类型 / 价格 / 状态 / 更新 / 操作。
 * 进口价、规格、包装等低频字段不上列表，收进详情抽屉。
 */
export function buildProductColumns({
  t,
  sortField,
  sortOrder,
  canWrite,
  currentYear,
  page,
  pageSize,
  productWidth,
  supplierWidth,
  onEdit,
  getRowMenu,
}: ProductColumnsOptions): ColumnsType<DomesticProductItem> {
  return [
    {
      // 序号：跨页连续编号（第 2 页每页 50 条时第一行是 51）。
      key: 'serial',
      title: t('common.index'),
      width: LIST_COLUMN_WIDTHS.serial,
      render: (_value: unknown, _record, index) => (
        <span className="dp-serial">{rowSerialNumber(page, pageSize, index)}</span>
      ),
    },
    {
      key: 'product',
      title: t('domesticProducts.product', '商品'),
      dataIndex: SORT_FIELD_BY_COLUMN.product,
      sorter: true,
      sortDirections: SORT_DIRECTIONS,
      sortOrder: sortOrderForColumn('product', sortField, sortOrder),
      // 宽度范围 148~340，随表格可用宽度算出：笔记本宽度下与原先接近，大屏上封顶，不再被拉到上千像素。
      width: productWidth,
      render: (_, record) => (
        <div className="dp-who">
          <ProductThumb src={record.productImage} name={record.name} seed={record.itemNumber} />
          <div className="dp-c2">
            <span className="dp-c2-t" title={record.name}>
              {record.name || '--'}
            </span>
            {record.nameEn ? (
              <span className="dp-c2-s" title={record.nameEn}>
                {record.nameEn}
              </span>
            ) : null}
          </div>
        </div>
      ),
    },
    {
      key: 'itemNumber',
      title: t('domesticProducts.hbProductNo', 'HB货号'),
      dataIndex: SORT_FIELD_BY_COLUMN.itemNumber,
      width: LIST_COLUMN_WIDTHS.itemNumber,
      sorter: true,
      sortDirections: SORT_DIRECTIONS,
      sortOrder: sortOrderForColumn('itemNumber', sortField, sortOrder),
      render: (value: string) => <CopyableText value={value} label={t('domesticProducts.hbProductNo', 'HB货号')} />,
    },
    {
      // 条码列只留可复制文本：条码画布每行一个 canvas，又慢又占地方，已移到详情抽屉。
      key: 'barcode',
      title: t('domesticProducts.barcode', '条码'),
      dataIndex: 'barcode',
      width: LIST_COLUMN_WIDTHS.barcode,
      render: (value?: string) => <CopyableText value={value} label={t('domesticProducts.barcode', '条码')} />,
    },
    {
      key: 'supplier',
      title: t('domesticProducts.supplier', '供应商'),
      dataIndex: SORT_FIELD_BY_COLUMN.supplier,
      width: supplierWidth,
      sorter: true,
      sortDirections: SORT_DIRECTIONS,
      sortOrder: sortOrderForColumn('supplier', sortField, sortOrder),
      render: (_, record) => (
        <div className="dp-c2">
          <span className="dp-c2-t" title={record.supplierName}>
            {record.supplierName || '--'}
          </span>
          <span className="dp-c2-s dp-mono">{record.supplierCode}</span>
        </div>
      ),
    },
    {
      key: 'productType',
      title: t('domesticProducts.colType'),
      dataIndex: 'productType',
      width: LIST_COLUMN_WIDTHS.type,
      render: (value: ProductType) => <ProductTypeTag type={value} />,
    },
    {
      key: 'price',
      title: (
        <div className="dp-c2">
          <span>{t('domesticProducts.colPrice')}</span>
          <span className="dp-c2-s">{t('domesticProducts.colPriceHint')}</span>
        </div>
      ),
      dataIndex: 'domesticPrice',
      width: LIST_COLUMN_WIDTHS.price,
      render: (_, record) => (
        <div className="dp-c2">
          <span className="dp-price-main">{formatMoney('¥', record.domesticPrice)}</span>
          <span className="dp-c2-s">{formatMoney('$', record.labelPrice)}</span>
        </div>
      ),
    },
    {
      key: 'status',
      title: t('domesticProducts.status', '状态'),
      dataIndex: 'isActive',
      width: LIST_COLUMN_WIDTHS.status,
      render: (value: boolean) => <ProductStatusText active={value} />,
    },
    {
      // 后端 grid 接口目前不返回 updatedBy：没有更新人时第二行退回显示时间。
      key: 'updated',
      title: t('domesticProducts.colUpdated'),
      dataIndex: SORT_FIELD_BY_COLUMN.updated,
      width: LIST_COLUMN_WIDTHS.updated,
      sorter: true,
      sortDirections: SORT_DIRECTIONS,
      sortOrder: sortOrderForColumn('updated', sortField, sortOrder),
      render: (_, record) => {
        const { date, time } = formatUpdatedParts(record.updatedAt, currentYear)
        return (
          <div className="dp-c2" title={formatFullTimestamp(record.updatedAt)}>
            <span className="dp-c2-t dp-updated-date">{date}</span>
            <span className="dp-c2-s">{record.updatedBy || time}</span>
          </div>
        )
      },
    },
    {
      key: 'action',
      title: t('common.action', '操作'),
      // 不设宽度：吸收商品 / 供应商列封顶之后多出来的宽度，按钮靠右；最窄时是 LIST_COLUMN_WIDTHS.action（已计入 scroll.x）。
      align: 'right',
      render: (_, record) => (
        <div className="dp-actions">
          {canWrite ? (
            <Button size="small" type="link" onClick={() => onEdit(record)}>
              {t('common.edit', '编辑')}
            </Button>
          ) : null}
          <Dropdown trigger={['click']} menu={getRowMenu(record)}>
            <Button size="small" type="link" icon={<EllipsisOutlined />} aria-label={t('domesticProducts.rowMoreActions')} />
          </Dropdown>
        </div>
      ),
    },
  ]
}
