using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    public class PaymentTerminalSettingsDto
    {
        public List<PaymentTerminalEnvironmentStatusDto> Square { get; set; } = new();
        public List<PaymentTerminalStoreOptionDto> Stores { get; set; } = new();
        public string? SelectedStoreCode { get; set; }
        public List<LinklyCloudCredentialAdminDto> Linkly { get; set; } = new();
    }

    public class PaymentTerminalEnvironmentStatusDto
    {
        public string Environment { get; set; } = string.Empty;
        public bool Configured { get; set; }
        public bool Enabled { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
    }

    public class PaymentTerminalStoreOptionDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
    }

    public class LinklyCloudCredentialAdminDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string Environment { get; set; } = string.Empty;
        public string? Username { get; set; }
        public bool HasPassword { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
    }

    public class UpdateSquareTokenDto
    {
        [Required(ErrorMessage = "支付环境不能为空")]
        [StringLength(32, ErrorMessage = "支付环境长度不能超过32个字符")]
        public string Environment { get; set; } = string.Empty;

        [StringLength(2048, ErrorMessage = "Square Token 长度不能超过2048个字符")]
        public string? AccessToken { get; set; }

        public bool ClearToken { get; set; }
    }

    public class LinklyTerminalManagementDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string Environment { get; set; } = string.Empty;
        public string Mode { get; set; } = "Legacy";
        public List<LinklyTerminalAdminDto> Terminals { get; set; } = new();
        public List<LinklyTerminalDeviceAdminDto> Devices { get; set; } = new();
    }

    public class LinklyTerminalAdminDto
    {
        public Guid TerminalId { get; set; }
        public string StoreCode { get; set; } = string.Empty;
        public string Environment { get; set; } = string.Empty;
        public int LaneNo { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string UsernameMasked { get; set; } = string.Empty;
        public bool HasPassword { get; set; }
        public string PairingState { get; set; } = "Unpaired";
        public string? LastHealthStatus { get; set; }
        public DateTime? LastHealthAtUtc { get; set; }
        public int SelectedDeviceCount { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
    }

    public class LinklyTerminalDeviceAdminDto
    {
        public string DeviceCode { get; set; } = string.Empty;
        public string DeviceSystem { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        // 已删除的设备仍可能保留终端选择；显式标记，避免管理端把该约束静默隐藏。
        public bool DeviceMissing { get; set; }
        public Guid? TerminalId { get; set; }
        public long Revision { get; set; }
    }

    public class CreateLinklyTerminalDto
    {
        [Required(ErrorMessage = "门店编码不能为空")]
        [StringLength(32, ErrorMessage = "门店编码长度不能超过32个字符")]
        public string StoreCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "支付环境不能为空")]
        [StringLength(32, ErrorMessage = "支付环境长度不能超过32个字符")]
        public string Environment { get; set; } = string.Empty;

        [Range(1, 9999, ErrorMessage = "Lane 编号必须在1到9999之间")]
        public int LaneNo { get; set; }

        [Required(ErrorMessage = "终端名称不能为空")]
        [StringLength(128, ErrorMessage = "终端名称长度不能超过128个字符")]
        public string DisplayName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Linkly 用户名不能为空")]
        [StringLength(128, ErrorMessage = "Linkly 用户名长度不能超过128个字符")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Linkly 密码不能为空")]
        [StringLength(512, ErrorMessage = "Linkly 密码长度不能超过512个字符")]
        public string Password { get; set; } = string.Empty;
    }

    public class UpdateLinklyTerminalDto
    {
        [Required(ErrorMessage = "门店编码不能为空")]
        [StringLength(32, ErrorMessage = "门店编码长度不能超过32个字符")]
        public string StoreCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "支付环境不能为空")]
        [StringLength(32, ErrorMessage = "支付环境长度不能超过32个字符")]
        public string Environment { get; set; } = string.Empty;

        [Range(1, 9999, ErrorMessage = "Lane 编号必须在1到9999之间")]
        public int LaneNo { get; set; }

        [Required(ErrorMessage = "终端名称不能为空")]
        [StringLength(128, ErrorMessage = "终端名称长度不能超过128个字符")]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(128, ErrorMessage = "Linkly 用户名长度不能超过128个字符")]
        public string? Username { get; set; }

        [StringLength(512, ErrorMessage = "Linkly 密码长度不能超过512个字符")]
        public string? Password { get; set; }
    }

    public class UpdateLinklyDeviceSelectionDto
    {
        [Required(ErrorMessage = "门店编码不能为空")]
        [StringLength(32, ErrorMessage = "门店编码长度不能超过32个字符")]
        public string StoreCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "支付环境不能为空")]
        [StringLength(32, ErrorMessage = "支付环境长度不能超过32个字符")]
        public string Environment { get; set; } = string.Empty;

        public Guid TerminalId { get; set; }

        [Range(0, long.MaxValue, ErrorMessage = "终端选择版本不能小于0")]
        public long? ExpectedRevision { get; set; }
    }

    public class DeleteLinklyDeviceSelectionDto
    {
        [Required(ErrorMessage = "门店编码不能为空")]
        [StringLength(32, ErrorMessage = "门店编码长度不能超过32个字符")]
        public string StoreCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "支付环境不能为空")]
        [StringLength(32, ErrorMessage = "支付环境长度不能超过32个字符")]
        public string Environment { get; set; } = string.Empty;

        // 解除分配是破坏一对一约束的操作，必须由当前读取到的版本显式确认。
        [Range(1, long.MaxValue, ErrorMessage = "终端选择版本必须大于0")]
        public long ExpectedRevision { get; set; }
    }

    public class ActivateLinklyConfigurationDto
    {
        [Required(ErrorMessage = "门店编码不能为空")]
        [StringLength(32, ErrorMessage = "门店编码长度不能超过32个字符")]
        public string StoreCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "支付环境不能为空")]
        [StringLength(32, ErrorMessage = "支付环境长度不能超过32个字符")]
        public string Environment { get; set; } = string.Empty;
    }
}
