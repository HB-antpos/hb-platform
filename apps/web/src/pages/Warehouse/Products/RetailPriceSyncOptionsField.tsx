import { useState } from 'react';
import { Checkbox, Space, Typography } from 'antd';
import { useTranslation } from 'react-i18next';
import type { RetailPriceSyncChoice } from './retailPriceSyncOptions';

// 仓库改零售价时的两个勾选项（编辑弹窗 / 批量修改弹窗 / 行内改价确认 / 货柜明细改价共用）。
// 文案是页面级消息，由各页面懒注册，不进首屏语言包：仓库商品页用 warehouseUi.products.retailPriceSync（默认），
// 货柜明细页通过 messageKeys 指向自己页面文件里的同名四条文案，避免为了四句话把整份商品页消息拉进货柜页。
// 键写成完整的叶子键字面量：页面文案契约测试按字面量核对「源码里用到的键在中英文文件里都存在」。
export interface RetailPriceSyncMessageKeys {
    syncStoreRetailPrices: string;
    syncStoreRetailPricesHint: string;
    generateTasks: string;
    generateTasksHint: string;
}
export const PRODUCTS_RETAIL_PRICE_SYNC_MESSAGE_KEYS: RetailPriceSyncMessageKeys = {
    syncStoreRetailPrices: 'warehouseUi.products.retailPriceSync.syncStoreRetailPrices',
    syncStoreRetailPricesHint: 'warehouseUi.products.retailPriceSync.syncStoreRetailPricesHint',
    generateTasks: 'warehouseUi.products.retailPriceSync.generateTasks',
    generateTasksHint: 'warehouseUi.products.retailPriceSync.generateTasksHint',
};
export default function RetailPriceSyncOptionsField({ value, onChange, disabled, messageKeys = PRODUCTS_RETAIL_PRICE_SYNC_MESSAGE_KEYS, }: {
    value: RetailPriceSyncChoice;
    onChange: (next: RetailPriceSyncChoice) => void;
    disabled?: boolean;
    messageKeys?: RetailPriceSyncMessageKeys;
}) {
    const { t } = useTranslation();
    // 说明文字缩进到勾选框文字下方（16px 勾选框 + 8px 间距）。
    const hintStyle = { display: 'block', marginInlineStart: 24, fontSize: 12 } as const;
    return (<Space direction="vertical" size={8} style={{ width: '100%' }}>
      <div>
        <Checkbox checked={value.syncStoreRetailPrices} disabled={disabled} onChange={(event) => onChange({ ...value, syncStoreRetailPrices: event.target.checked })}>
          {t(messageKeys.syncStoreRetailPrices)}
        </Checkbox>
        <Typography.Text type="secondary" style={hintStyle}>
          {t(messageKeys.syncStoreRetailPricesHint)}
        </Typography.Text>
      </div>
      <div>
        <Checkbox checked={value.generateTasks} disabled={disabled} onChange={(event) => onChange({ ...value, generateTasks: event.target.checked })}>
          {t(messageKeys.generateTasks)}
        </Checkbox>
        <Typography.Text type="secondary" style={hintStyle}>
          {t(messageKeys.generateTasksHint)}
        </Typography.Text>
      </div>
    </Space>);
}

// 确认框（Modal.confirm / confirm 对话框）的内容是一次性渲染的静态节点，没法随父级 state 重绘：
// 这里自带一份勾选状态，并把每次变化回调给外层闭包变量；外层用 initialValue 保证内容被卸载重挂后仍显示上一次的选择。
export function UncontrolledRetailPriceSyncOptions({ initialValue, onChange, messageKeys, }: {
    initialValue: RetailPriceSyncChoice;
    onChange: (next: RetailPriceSyncChoice) => void;
    messageKeys?: RetailPriceSyncMessageKeys;
}) {
    const [value, setValue] = useState<RetailPriceSyncChoice>(initialValue);
    return (<RetailPriceSyncOptionsField value={value} messageKeys={messageKeys} onChange={(next) => {
            setValue(next);
            onChange(next);
        }}/>);
}
