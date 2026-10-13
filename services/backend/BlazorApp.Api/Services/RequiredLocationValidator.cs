namespace BlazorApp.Api.Services
{
    public sealed record RequiredLocationValidationResult(
        bool Success,
        string Message,
        string ErrorCode
    );

    public static class RequiredLocationValidator
    {
        public const string ErrorCode = "LOCATION_REQUIRED";

        public static RequiredLocationValidationResult Validate(
            double? latitude,
            double? longitude,
            string? permissionStatus,
            DateTime? capturedAtUtc,
            string requiredMessage,
            TimeSpan maxAge
        )
        {
            var coordinateResult = ValidateCoordinates(
                latitude,
                longitude,
                permissionStatus,
                capturedAtUtc,
                requiredMessage
            );
            if (!coordinateResult.Success)
            {
                return coordinateResult;
            }

            var now = DateTime.UtcNow;
            var capturedAt = DateTime.SpecifyKind(capturedAtUtc!.Value, DateTimeKind.Utc);
            if (capturedAt > now.AddMinutes(5) || capturedAt < now.Subtract(maxAge))
            {
                return Error("定位采集时间无效");
            }

            return new RequiredLocationValidationResult(true, string.Empty, string.Empty);
        }

        /// <summary>
        /// 只校验「定位是否齐全且合法」：坐标与采集时间必须存在、权限状态必须为 granted、坐标在有效范围内。
        /// 不判断采集时间是否过旧/过新——批量补传要自己按服务端时钟区分 SAMPLE_TOO_OLD / SAMPLE_IN_FUTURE，
        /// 因此把这三项抽成可单独调用的方法；Validate 在其后再叠加固定的时间窗口检查，行为保持不变。
        /// </summary>
        public static RequiredLocationValidationResult ValidateCoordinates(
            double? latitude,
            double? longitude,
            string? permissionStatus,
            DateTime? capturedAtUtc,
            string requiredMessage
        )
        {
            if (!latitude.HasValue || !longitude.HasValue || !capturedAtUtc.HasValue)
            {
                return Error(requiredMessage);
            }

            if (!string.Equals(permissionStatus?.Trim(), "granted", StringComparison.OrdinalIgnoreCase))
            {
                return Error(requiredMessage);
            }

            // 服务端兜底验证坐标范围，避免绕过 App 直接写入无效审计位置。
            if (latitude.Value < -90 || latitude.Value > 90 || longitude.Value < -180 || longitude.Value > 180)
            {
                return Error("定位坐标无效");
            }

            return new RequiredLocationValidationResult(true, string.Empty, string.Empty);
        }

        private static RequiredLocationValidationResult Error(string message)
        {
            return new RequiredLocationValidationResult(false, message, ErrorCode);
        }
    }
}
