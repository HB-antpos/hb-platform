import { Alert, message } from 'antd'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router-dom'

import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  createMyobAuthorizeUrl,
  getMyobConnections,
  getMyobStoreMappings,
} from '../../../services/myobService'
import type { MyobConnection, MyobStoreMappings } from '../../../types/myob'
import ConnectionsPanel from './ConnectionsPanel'
import StoreMappingsPanel from './StoreMappingsPanel'
import {
  extractMyobError,
  getMyobErrorMessageKey,
  parseMyobCallback,
  stripMyobCallbackParams,
  upsertConnection,
} from './myobLogic'
import messagesEn from './myobMessages.en.json'
import messagesZh from './myobMessages.zh.json'
import './myob.css'

registerPageMessages({ zh: messagesZh, en: messagesEn })

const EMPTY_MAPPINGS: MyobStoreMappings = { stores: [], connections: [] }

export default function MyobPage() {
  const { t } = useTranslation()
  const location = useLocation()
  const navigate = useNavigate()

  const [connections, setConnections] = useState<MyobConnection[]>([])
  const [connectionsLoading, setConnectionsLoading] = useState(true)
  const [connectionsError, setConnectionsError] = useState<string | null>(null)
  const [mappings, setMappings] = useState<MyobStoreMappings>(EMPTY_MAPPINGS)
  const [mappingsLoading, setMappingsLoading] = useState(true)
  const [mappingsError, setMappingsError] = useState<string | null>(null)
  const [authorizing, setAuthorizing] = useState(false)

  // 只认最后一次发起的请求，避免快速连点刷新时旧响应覆盖新数据
  const connectionsSeq = useRef(0)
  const mappingsSeq = useRef(0)

  const loadConnections = useCallback(async () => {
    const seq = ++connectionsSeq.current
    setConnectionsLoading(true)
    try {
      const data = await getMyobConnections()
      if (seq === connectionsSeq.current) {
        setConnections(data)
        setConnectionsError(null)
      }
    } catch (err) {
      if (seq === connectionsSeq.current) {
        setConnectionsError(extractMyobError(err, t('myob.connections.loadFailed')).message)
      }
    } finally {
      if (seq === connectionsSeq.current) {
        setConnectionsLoading(false)
      }
    }
  }, [t])

  const loadMappings = useCallback(async () => {
    const seq = ++mappingsSeq.current
    setMappingsLoading(true)
    try {
      const data = await getMyobStoreMappings()
      if (seq === mappingsSeq.current) {
        setMappings(data)
        setMappingsError(null)
      }
    } catch (err) {
      if (seq === mappingsSeq.current) {
        setMappingsError(extractMyobError(err, t('myob.mappings.loadFailed')).message)
      }
    } finally {
      if (seq === mappingsSeq.current) {
        setMappingsLoading(false)
      }
    }
  }, [t])

  useEffect(() => {
    void loadConnections()
    void loadMappings()
  }, [loadConnections, loadMappings])

  // 授权完成后后端会把浏览器重定向回本页并带上 ?myob=connected / error；
  // 读取一次、提示结果，然后用 replace 清掉参数，刷新或返回时不会重复提示。
  const handledCallback = useRef(false)
  useEffect(() => {
    if (handledCallback.current) {
      return
    }
    const result = parseMyobCallback(location.search)
    if (!result) {
      return
    }
    handledCallback.current = true
    if (result.kind === 'connected') {
      message.success(t('myob.callback.connected'))
    } else {
      message.error(t('myob.callback.error', { reason: t(getMyobErrorMessageKey(result.code)) }))
    }
    navigate({ pathname: location.pathname, search: stripMyobCallbackParams(location.search), hash: location.hash }, { replace: true })
  }, [location.hash, location.pathname, location.search, navigate, t])

  // 取授权地址后整页跳转到 MYOB；跳转前保持按钮禁用，失败才恢复
  const handleAuthorize = useCallback(async () => {
    if (authorizing) {
      return
    }
    setAuthorizing(true)
    try {
      const url = await createMyobAuthorizeUrl()
      // 只允许跳转 https 地址，防止后端响应被篡改成 javascript: 等
      if (!/^https:\/\//i.test(url)) {
        throw new Error('invalid authorize url')
      }
      window.location.href = url
    } catch (err) {
      message.error(extractMyobError(err, t('myob.connections.authorizeFailed')).message)
      setAuthorizing(false)
    }
  }, [authorizing, t])

  return (
    <PageContainer title={t('myob.title')} subtitle={t('myob.subtitle')}>
      <div className="myob-page">
        <Alert
          type="info"
          showIcon
          message={t('myob.notice.title')}
          description={
            <ul className="myob-notice-list">
              <li>{t('myob.notice.admin')}</li>
              <li>{t('myob.notice.perAbn')}</li>
            </ul>
          }
        />

        <ConnectionsPanel
          connections={connections}
          loading={connectionsLoading}
          error={connectionsError}
          stores={mappings.stores}
          authorizing={authorizing}
          onAuthorize={() => void handleAuthorize()}
          onReload={() => void loadConnections()}
          onConnectionUpdated={(updated) => setConnections((current) => upsertConnection(current, updated))}
          // 断开会移除该连接下的全部映射：重新拉映射表（可选连接列表也随之变化）
          onDisconnected={() => void loadMappings()}
        />

        <StoreMappingsPanel
          mappings={mappings}
          allConnections={connections}
          loading={mappingsLoading}
          error={mappingsError}
          onReload={() => void loadMappings()}
          onSaved={(saved) => {
            setMappings(saved)
            // 映射数量显示在连接表里，保存后同步刷新
            void loadConnections()
          }}
        />
      </div>
    </PageContainer>
  )
}
