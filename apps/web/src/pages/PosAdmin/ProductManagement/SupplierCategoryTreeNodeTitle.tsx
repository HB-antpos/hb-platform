import { Button, Space, Tag, Tooltip, Typography } from 'antd'
import { useTranslation } from 'react-i18next'
import type { LocalSupplierCategoryNode } from '../../../types/localSupplierCategory'

const I18N = 'posAdmin.products.supplierCategory'

export interface SupplierCategoryTreeNodeTitleProps {
  node: LocalSupplierCategoryNode
  canManage: boolean
  /** 该节点的促销标记正在提交。 */
  pending?: boolean
  onTogglePromotional?: (node: LocalSupplierCategoryNode) => void
}

/**
 * 管理弹窗分类树的节点标题：名称 + 商品数 + 促销/停用标记；
 * 有管理权限时提供文字链接切换促销（促销分类不参与自动归类）。
 */
export default function SupplierCategoryTreeNodeTitle({ node, canManage, pending, onTogglePromotional }: SupplierCategoryTreeNodeTitleProps) {
  const { t } = useTranslation()
  const promotionalTag = node.isPromotional ? (
    <Tooltip title={t(`${I18N}.promotionalHint`, '促销分类不参与自动归类')}>
      <Tag color="orange">
        {t(`${I18N}.promotional`, '促销')}
        {node.promotionalSource === 'manual' ? ` · ${t(`${I18N}.promotionalManual`, '人工')}` : ''}
      </Tag>
    </Tooltip>
  ) : null

  return (
    <Space size={6} wrap={false}>
      <span>{node.name}</span>
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {node.productCount} {t(`${I18N}.productsUnit`, '个商品')}
      </Typography.Text>
      {promotionalTag}
      {node.isActive === false ? <Tag>{t(`${I18N}.inactive`, '已停用')}</Tag> : null}
      {canManage && onTogglePromotional ? (
        <Button
          type="link"
          size="small"
          disabled={pending || undefined}
          onClick={(event) => {
            // 点击切换不应同时选中/展开树节点。
            event.stopPropagation()
            onTogglePromotional(node)
          }}
        >
          {node.isPromotional
            ? t(`${I18N}.unmarkPromotional`, '取消促销')
            : t(`${I18N}.markPromotional`, '设为促销')}
        </Button>
      ) : null}
    </Space>
  )
}
