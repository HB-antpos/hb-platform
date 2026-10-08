import { Card, Descriptions, Spin, Tag, Typography, message } from 'antd'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router-dom'
import PageContainer from '../../../components/PageContainer'
import { useDynamicTabTitle } from '../../../hooks/useDynamicTabTitle'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getStoreByGuid } from '../../../services/storeService'
import type { StoreDto } from '../../../types/store'
import storesMessagesEn from './storesMessages.en.json'
import storesMessagesZh from './storesMessages.zh.json'
import { formatStoreTimeZoneId } from './timeZoneOptions'

// 代金券使用说明 / 分期条款的文案在页面级消息文件里；本页独立于分店列表页加载时也要能取到。
registerPageMessages({ zh: storesMessagesZh, en: storesMessagesEn })

export default function StoreDetailPage() {
  const { t } = useTranslation()
  const { id = '' } = useParams()
  const [loading, setLoading] = useState(true)
  const [store, setStore] = useState<StoreDto | null>(null)

  useDynamicTabTitle(store ? t('system.stores.detailTitle', { name: store.storeCode }) : t('system.stores.detailTitle', { name: id }))

  useEffect(() => {
    const run = async () => {
      setLoading(true)
      try {
        const detail = await getStoreByGuid(id)
        setStore(detail)
      } catch (error) {
        console.error(error)
        message.error(t('system.stores.loadDetailFailed'))
      } finally {
        setLoading(false)
      }
    }

    void run()
  }, [id])

  if (loading) {
    return <Spin size="large" className="page-spin" />
  }

  if (!store) {
    return <Typography.Text type="danger">{t('system.stores.notFound')}</Typography.Text>
  }

  return (
    <PageContainer
      title={t('system.stores.detailTitle', { name: store.storeCode })}
      subtitle={t('system.stores.detailTabSubtitle')}
    >
      <Card>
        <Descriptions bordered column={2}>
          <Descriptions.Item label={t('system.stores.storeName')}>{store.storeName}</Descriptions.Item>
          <Descriptions.Item label={t('system.stores.storeCode')}>{store.storeCode}</Descriptions.Item>
          <Descriptions.Item label={t('system.stores.brandName')}>{store.brandName || '--'}</Descriptions.Item>
          <Descriptions.Item label={t('system.stores.abn')}>{store.abn || '--'}</Descriptions.Item>
          <Descriptions.Item label={t('system.stores.timeZone')}>{formatStoreTimeZoneId(store.timeZoneId)}</Descriptions.Item>
          <Descriptions.Item label={t('system.stores.contactPhone')}>{store.contactPhone || '--'}</Descriptions.Item>
          <Descriptions.Item label={t('system.stores.cashRegisterEnabled')}>
            <Tag color={store.isActive ? 'success' : 'default'}>{store.isActive ? t('common.active') : t('common.inactive')}</Tag>
          </Descriptions.Item>
          <Descriptions.Item label={t('system.stores.address')} span={2}>
            {store.address || '--'}
          </Descriptions.Item>
          <Descriptions.Item label={t('system.stores.returnPolicy')} span={2}>
            <div style={{ whiteSpace: 'pre-wrap' }}>{store.returnPolicy || '--'}</div>
          </Descriptions.Item>
          <Descriptions.Item label={t('system.stores.voucherTerms')} span={2}>
            {store.voucherTerms
              ? <div style={{ whiteSpace: 'pre-wrap' }}>{store.voucherTerms}</div>
              : <Typography.Text type="secondary">{t('system.stores.termsUsingDefault')}</Typography.Text>}
          </Descriptions.Item>
          <Descriptions.Item label={t('system.stores.installmentTerms')} span={2}>
            {store.installmentTerms
              ? <div style={{ whiteSpace: 'pre-wrap' }}>{store.installmentTerms}</div>
              : <Typography.Text type="secondary">{t('system.stores.termsUsingDefault')}</Typography.Text>}
          </Descriptions.Item>
          <Descriptions.Item label={t('column.createTime')}>{store.createdAt}</Descriptions.Item>
          <Descriptions.Item label={t('system.users.updatedAt')}>{store.updatedAt}</Descriptions.Item>
        </Descriptions>
      </Card>
    </PageContainer>
  )
}
