using System;
using System.Linq;
using BlazorApp.Api.Services.Attendance;
using Xunit;

namespace BlazorApp.Api.Tests
{
    public class AttendancePublicHolidaySyncHelperTests
    {
        [Theory]
        [InlineData("Shop 1, George Street, Sydney NSW 2000", "2000")]
        [InlineData("Brisbane City QLD 4000 Australia", "4000")]
        [InlineData("PO Box 9726 Brisbane QLD", "9726")]
        [InlineData("No postcode here", null)]
        public void ExtractPostcodeFromAddress_ReturnsLastAustralianPostcode(
            string address,
            string? expected
        )
        {
            Assert.Equal(expected, PublicHolidaySyncHelper.ExtractPostcodeFromAddress(address));
        }

        [Theory]
        [InlineData("2000", "NSW")]
        [InlineData("2485", "NSW")]
        [InlineData("4000", "QLD")]
        [InlineData("9726", "QLD")]
        [InlineData("2406", null)]
        [InlineData("0800", null)]
        public void ResolveJurisdictionFromPostcode_ReturnsSupportedStateOnly(
            string postcode,
            string? expected
        )
        {
            Assert.Equal(expected, PublicHolidaySyncHelper.ResolveJurisdictionFromPostcode(postcode));
        }

        [Theory]
        // 配置时区优先：地址只写区名（生产 Bankstown）或邮编指向别州时都以时区为准
        [InlineData("Australia/Sydney", "Bankstown", "NSW")]
        [InlineData("Australia/Brisbane", "SHOP 1 65-69 CRONULLA ST, CRONULLA, NSW, 2230", "QLD")]
        [InlineData(" australia/sydney ", null, "NSW")]
        // 未配置、无效或不支持的时区回退到地址邮编
        [InlineData(null, "Shop 11, Southport QLD 4215", "QLD")]
        [InlineData("Invalid/TimeZone", "Kotara NSW 2289", "NSW")]
        [InlineData("Australia/Melbourne", "Kotara NSW 2289", "NSW")]
        [InlineData("Australia/Melbourne", "Cheltenham 3192, VIC", null)]
        [InlineData(null, "Bankstown", null)]
        public void ResolveStoreJurisdiction_PrefersConfiguredTimeZoneThenPostcode(
            string? timeZoneId,
            string? address,
            string? expected
        )
        {
            Assert.Equal(expected, PublicHolidaySyncHelper.ResolveStoreJurisdiction(timeZoneId, address));
        }

        [Fact]
        public void BuildSyncWindow_IncludesTodayAndThirtyDays()
        {
            var window = PublicHolidaySyncHelper.BuildSyncWindow(
                new DateTime(2026, 5, 25),
                null,
                null,
                30
            );

            Assert.Equal(new DateTime(2026, 5, 25), window.FromDate);
            Assert.Equal(new DateTime(2026, 6, 24), window.ToDate);
            Assert.True(PublicHolidaySyncHelper.IsInWindow(new DateTime(2026, 6, 24), window));
            Assert.False(PublicHolidaySyncHelper.IsInWindow(new DateTime(2026, 6, 25), window));
        }

        [Fact]
        public void FilterStatewideHolidays_ExcludesIndustryAndRegionalItems()
        {
            var holidays = new[]
            {
                new PublicHolidaySourceItem("NSW", new DateTime(2026, 6, 8), "King's Birthday"),
                new PublicHolidaySourceItem("NSW", new DateTime(2026, 8, 3), "Bank Holiday"),
                new PublicHolidaySourceItem("QLD", new DateTime(2026, 8, 12), "Royal Queensland Show (Brisbane area only)"),
                new PublicHolidaySourceItem("QLD", new DateTime(2026, 10, 5), "King's Birthday"),
            };

            var filtered = PublicHolidaySyncHelper.FilterStatewideHolidays(holidays).ToList();

            Assert.Equal(2, filtered.Count);
            Assert.Contains(filtered, item => item.Jurisdiction == "NSW" && item.HolidayName == "King's Birthday");
            Assert.Contains(filtered, item => item.Jurisdiction == "QLD" && item.HolidayName == "King's Birthday");
            Assert.DoesNotContain(filtered, item => item.HolidayName.Contains("Bank Holiday"));
            Assert.DoesNotContain(filtered, item => item.HolidayName.Contains("Brisbane area only"));
        }
    }
}
