using System.Globalization;
using System.Text.RegularExpressions;
using BlazorApp.Shared.Constants;

namespace BlazorApp.Api.Services.Attendance
{
    public sealed record PublicHolidaySourceItem(
        string Jurisdiction,
        DateTime HolidayDate,
        string HolidayName
    );

    public sealed record PublicHolidaySyncWindow(DateTime FromDate, DateTime ToDate);

    public static partial class PublicHolidaySyncHelper
    {
        private static readonly Regex PostcodeRegex = AustralianPostcodeRegex();
        private static readonly Regex FairWorkHolidayRegex = FairWorkHolidayLineRegex();

        public static string? ExtractPostcodeFromAddress(string? address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return null;
            }

            return PostcodeRegex.Matches(address)
                .Select(match => match.Value)
                .LastOrDefault();
        }

        public static string? NormalizeJurisdiction(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim().ToUpperInvariant();
            return normalized switch
            {
                "NSW" or "NEW SOUTH WALES" => "NSW",
                "QLD" or "QUEENSLAND" => "QLD",
                _ => null,
            };
        }

        /// <summary>
        /// 门店所在州（仅 NSW/QLD）：先认分店管理里配置的时区（Sydney=NSW、Brisbane=QLD），
        /// 未配置、无效或为其他时区（如 Melbourne）时再按地址里的邮编推导。
        /// 地址只写区名的门店（如 Bankstown）靠时区才能识别。
        /// </summary>
        public static string? ResolveStoreJurisdiction(string? timeZoneId, string? address)
        {
            if (StoreTimeZonePolicy.TryNormalize(timeZoneId, out var normalizedTimeZoneId))
            {
                if (normalizedTimeZoneId == StoreTimeZonePolicy.Sydney) return "NSW";
                if (normalizedTimeZoneId == StoreTimeZonePolicy.Brisbane) return "QLD";
            }

            return ResolveJurisdictionFromPostcode(ExtractPostcodeFromAddress(address));
        }

        public static string? ResolveJurisdictionFromPostcode(string? postcode)
        {
            if (!int.TryParse(postcode?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            return value switch
            {
                2406 => null,
                >= 1000 and <= 2599 => "NSW",
                >= 2619 and <= 2899 => "NSW",
                >= 2921 and <= 2999 => "NSW",
                >= 4000 and <= 4999 => "QLD",
                >= 9000 and <= 9999 => "QLD",
                _ => null,
            };
        }

        public static PublicHolidaySyncWindow BuildSyncWindow(
            DateTime today,
            DateTime? fromDate,
            DateTime? toDate,
            int? daysAhead
        )
        {
            var from = (fromDate ?? today).Date;
            var to = (toDate ?? from.AddDays(Math.Clamp(daysAhead ?? 30, 0, 366))).Date;
            if (to < from)
            {
                to = from;
            }

            return new PublicHolidaySyncWindow(from, to);
        }

        public static bool IsInWindow(DateTime date, PublicHolidaySyncWindow window) =>
            date.Date >= window.FromDate && date.Date <= window.ToDate;

        public static IEnumerable<PublicHolidaySourceItem> FilterStatewideHolidays(
            IEnumerable<PublicHolidaySourceItem> holidays
        )
        {
            foreach (var holiday in holidays)
            {
                var jurisdiction = NormalizeJurisdiction(holiday.Jurisdiction);
                if (jurisdiction == null)
                {
                    continue;
                }

                var name = NormalizeHolidayName(holiday.HolidayName);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var lower = name.ToLowerInvariant();
                if (
                    (jurisdiction == "NSW" && lower.Contains("bank holiday"))
                    || lower.Contains("brisbane area only")
                    || lower.Contains("royal queensland show")
                    || lower.Contains("show holiday")
                )
                {
                    continue;
                }

                yield return new PublicHolidaySourceItem(jurisdiction, holiday.HolidayDate.Date, name);
            }
        }

        public static IEnumerable<PublicHolidaySourceItem> ParseFairWorkSection(
            string htmlOrText,
            string jurisdiction,
            int year
        )
        {
            var sectionName = jurisdiction == "NSW" ? "New South Wales" : "Queensland";
            var text = Regex.Replace(htmlOrText, "<[^>]+>", "\n");
            text = System.Net.WebUtility.HtmlDecode(text).Replace('\u2019', '\'').Replace('\u2013', '-');
            var sectionMatch = Regex.Match(
                text,
                $@"##\s*{Regex.Escape(sectionName)}(?<body>.*?)(?=\n\s*##\s|\z)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline
            );

            if (!sectionMatch.Success)
            {
                yield break;
            }

            foreach (Match match in FairWorkHolidayRegex.Matches(sectionMatch.Groups["body"].Value))
            {
                var dateText = match.Groups["date"].Value.Trim();
                if (
                    DateTime.TryParseExact(
                        $"{dateText} {year}",
                        "dddd d MMMM yyyy",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var date
                    )
                )
                {
                    yield return new PublicHolidaySourceItem(
                        jurisdiction,
                        date,
                        NormalizeHolidayName(match.Groups["name"].Value)
                    );
                }
            }
        }

        private static string NormalizeHolidayName(string value) =>
            Regex.Replace(value.Trim().Replace('\u2019', '\''), @"\s+", " ");

        [GeneratedRegex(@"\b[0-9]{4}\b")]
        private static partial Regex AustralianPostcodeRegex();

        [GeneratedRegex(@"[*]\s*(?<date>[A-Za-z]+ \d{1,2} [A-Za-z]+):\s*(?<name>[^\r\n<]+)")]
        private static partial Regex FairWorkHolidayLineRegex();
    }
}
