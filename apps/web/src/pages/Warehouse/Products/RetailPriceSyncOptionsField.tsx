import { Checkbox, Space, Typography } from 'antd';
import { useTranslation } from 'react-i18next';
import type { RetailPriceSyncChoice } from './retailPriceSyncOptions';

// 仓库改零售价时的两个勾选项（编辑弹窗 / 批量修改弹窗 / 行内改价确认共用）。
// 文案是仓库商品页的页面级消息（warehouseUi.products.retailPriceSync），由 index.tsx 懒注册，不进首屏语言包。
export default function RetailPriceSyncOptionsField({ value, onChange, disabled, }: {
    value: RetailPriceSyncChoice;
    onChange: (next: RetailPriceSyncChoice) => void;
    disabled?: boolean;
}) {
    const { t } = useTranslation();
    // 说明文字缩进到勾选框文字下方（16px 勾选框 + 8px 间距）。
    const hintStyle = { display: 'block', marginInlineStart: 24, fontSize: 12 } as const;
    return (<Space direction="vertical" size={8} style={{ width: '100%' }}>
      <div>
        <Checkbox checked={value.syncStoreRetailPrices} disabled={disabled} onChange={(event) => onChange({ ...value, syncStoreRetailPrices: event.target.checked })}>
          {t('warehouseUi.products.retailPriceSync.syncStoreRetailPrices')}
        </Checkbox>
        <Typography.Text type="secondary" style={hintStyle}>
          {t('warehouseUi.products.retailPriceSync.syncStoreRetailPricesHint')}
        </Typography.Text>
      </div>
      <div>
        <Checkbox checked={value.generateTasks} disabled={disabled} onChange={(event) => onChange({ ...value, generateTasks: event.target.checked })}>
          {t('warehouseUi.products.retailPriceSync.generateTasks')}
        </Checkbox>
        <Typography.Text type="secondary" style={hintStyle}>
          {t('warehouseUi.products.retailPriceSync.generateTasksHint')}
        </Typography.Text>
      </div>
    </Space>);
}
