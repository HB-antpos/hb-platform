using SqlSugar;

namespace BlazorApp.Shared.Models.POSM
{
    /// <summary>
    /// POSM 设备注册信息表
    /// 用于管理 PDA、移动端、POS 等设备的注册和授权信息
    /// </summary>
    [SugarTable("POSM_设备注册信息表")]
    public class POSM_设备注册信息表
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public int ID { get; set; }

        /// <summary>
        /// 设备硬件识别码
        /// 用于唯一标识设备硬件
        /// </summary>
        [SugarColumn(Length = 100, IsNullable = false)]
        public string 设备硬件识别码 { get; set; } = string.Empty;

        /// <summary>
        /// 系统设备编号
        /// 系统内部分配的设备编号
        /// </summary>
        [SugarColumn(Length = 50, IsNullable = false)]
        public string 系统设备编号 { get; set; } = string.Empty;

        /// <summary>
        /// 分店代码
        /// 设备所属分店的代码
        /// </summary>
        [SugarColumn(Length = 50, IsNullable = true)]
        public string? 分店代码 { get; set; }

        /// <summary>
        /// 设备类型：PDA/Mobile/POS/Admin
        /// 标识设备的用途类型
        /// </summary>
        [SugarColumn(Length = 20, IsNullable = false)]
        public string 设备类型 { get; set; } = string.Empty;

        /// <summary>
        /// 设备系统：Android/iOS/iPadOS/Mac/Windows
        /// 设备运行的操作系统
        /// </summary>
        [SugarColumn(Length = 20, IsNullable = false)]
        public string 设备系统 { get; set; } = string.Empty;

        /// <summary>
        /// 设备状态：-1-待确认 0-禁用 1-启用 2-锁定 3-未注册
        /// 设备的当前状态
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public int 设备状态 { get; set; } = -1;

        /// <summary>
        /// 是否允许该设备开始新交易；恢复和可靠补传不受此字段影响
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public bool 是否允许交易 { get; set; } = true;

        /// <summary>
        /// 设备授权码
        /// 用于设备认证的授权码
        /// </summary>
        [SugarColumn(Length = 200, IsNullable = false)]
        public string 设备授权码 { get; set; } = string.Empty;

        /// <summary>
        /// 备注
        /// 设备的备注信息
        /// </summary>
        [SugarColumn(Length = 500, IsNullable = true)]
        public string? 备注 { get; set; }

        #region 运行状态字段
        /// <summary>
        /// 是否在线
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public bool 是否在线 { get; set; }

        /// <summary>
        /// 最后心跳时间
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public DateTime? 最后心跳时间 { get; set; }

        /// <summary>
        /// 当前收银员ID
        /// </summary>
        [SugarColumn(Length = 100, IsNullable = true)]
        public string? 当前收银员ID { get; set; }

        /// <summary>
        /// 当前收银员姓名
        /// </summary>
        [SugarColumn(Length = 100, IsNullable = true)]
        public string? 当前收银员姓名 { get; set; }

        /// <summary>
        /// 收银员登录时间
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public DateTime? 收银员登录时间 { get; set; }

        /// <summary>
        /// 客户端程序版本（WPF 随心跳上报，如 1.0.51）；老版本客户端不上报时为空
        /// </summary>
        [SugarColumn(Length = 50, IsNullable = true)]
        public string? 应用版本 { get; set; }
        #endregion

        #region 审计字段
        /// <summary>
        /// 创建时间
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public DateTime 创建时间 { get; set; } = DateTime.Now;

        /// <summary>
        /// 最后修改时间
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public DateTime? 最后修改时间 { get; set; }

        /// <summary>
        /// 创建人
        /// </summary>
        [SugarColumn(Length = 50, IsNullable = true)]
        public string? 创建人 { get; set; }

        /// <summary>
        /// 最后修改人
        /// </summary>
        [SugarColumn(Length = 50, IsNullable = true)]
        public string? 最后修改人 { get; set; }
        #endregion

        #region 扩展属性

        /// <summary>
        /// 设备状态描述
        /// </summary>
        [SugarColumn(IsIgnore = true)]
        public string 设备状态描述
        {
            get
            {
                return 设备状态 switch
                {
                    -1 => "待确认",
                    0 => "禁用",
                    1 => "启用",
                    2 => "锁定",
                    3 => "未注册",
                    _ => "未知状态"
                };
            }
        }

        /// <summary>
        /// 是否已启用
        /// </summary>
        [SugarColumn(IsIgnore = true)]
        public bool 是否已启用 => 设备状态 == 1;

        /// <summary>
        /// 是否已禁用
        /// </summary>
        [SugarColumn(IsIgnore = true)]
        public bool 是否已禁用 => 设备状态 == 0;

        /// <summary>
        /// 是否已锁定
        /// </summary>
        [SugarColumn(IsIgnore = true)]
        public bool 是否已锁定 => 设备状态 == 2;

        #endregion
    }
}

/// <summary>
/// 设备状态枚举
/// </summary>
public enum DeviceStatus
{
    /// <summary>
    /// 待确认
    /// </summary>
    待确认 = -1,
    
    /// <summary>
    /// 禁用
    /// </summary>
    禁用 = 0,
    
    /// <summary>
    /// 启用
    /// </summary>
    启用 = 1,
    
    /// <summary>
    /// 锁定
    /// </summary>
    锁定 = 2,
    
    /// <summary>
    /// 未注册
    /// </summary>
    未注册 = 3
}

/// <summary>
/// 设备类型枚举
/// </summary>
public enum DeviceType
{
    /// <summary>
    /// PDA设备
    /// </summary>
    PDA,
    
    /// <summary>
    /// 移动端
    /// </summary>
    Mobile,
    
    /// <summary>
    /// POS机
    /// </summary>
    POS,
    
    /// <summary>
    /// 管理端
    /// </summary>
    Admin
}

/// <summary>
/// 设备系统枚举
/// </summary>
public enum DeviceSystem
{
    /// <summary>
    /// Android系统
    /// </summary>
    Android,
    
    /// <summary>
    /// iOS系统
    /// </summary>
    iOS,

    /// <summary>
    /// iPadOS系统
    /// </summary>
    iPadOS,
    
    /// <summary>
    /// Mac系统
    /// </summary>
    Mac,
    
    /// <summary>
    /// Windows系统
    /// </summary>
    Windows
}
