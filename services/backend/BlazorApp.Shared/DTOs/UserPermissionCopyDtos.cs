using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 复制用户直接权限请求：把源员工的「直接权限」复制给一个或多个目标员工。
    /// 角色带来的权限不复制；方向由调用方决定（复制给他人＝多个目标，从同事复制＝单个目标）。
    /// </summary>
    public class CopyUserPermissionsRequestDto
    {
        public const string ModeMerge = "merge";
        public const string ModeReplace = "replace";

        [Required(ErrorMessage = "目标员工不能为空")]
        public List<string> TargetUserGuids { get; set; } = new();

        /// <summary>merge＝保留目标现有权限再加上；replace＝目标可委派范围内的直接权限换成源的。</summary>
        public string Mode { get; set; } = ModeMerge;

        /// <summary>true 只计算每人的变化，不写库。</summary>
        public bool Preview { get; set; }
    }

    public class CopyUserPermissionsTargetResultDto
    {
        public string UserGuid { get; set; } = string.Empty;
        public List<string> Added { get; set; } = new();
        public List<string> Removed { get; set; } = new();
        /// <summary>可复制的权限里，目标本来就有的数量。</summary>
        public int UnchangedCount { get; set; }
    }

    public class CopyUserPermissionsResultDto
    {
        public string SourceUserGuid { get; set; } = string.Empty;
        public string Mode { get; set; } = CopyUserPermissionsRequestDto.ModeMerge;
        public bool Preview { get; set; }
        public List<string> SourceDirectPermissionCodes { get; set; } = new();
        /// <summary>当前操作者有权分配、因此会被复制的权限。</summary>
        public List<string> TransferablePermissionCodes { get; set; } = new();
        /// <summary>源的直接权限里当前操作者不能分配（或已不存在）而被跳过的权限。</summary>
        public List<string> SkippedPermissionCodes { get; set; } = new();
        public List<CopyUserPermissionsTargetResultDto> Targets { get; set; } = new();
    }
}
