using System.Text.RegularExpressions;
using Shouldly;

namespace RaterBot.Tests.Unit;

public class WorkerTests
{
    private static readonly Regex IgnorePattern = new("(\\/|#)(ignore|skip)", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    [Theory]
    [InlineData("/ignore", true)]
    [InlineData("#ignore", true)]
    [InlineData("/skip", true)]
    [InlineData("#skip", true)]
    [InlineData("/IGNORE", true)]
    [InlineData("#IGNORE", true)]
    [InlineData("/Ignore", true)]
    [InlineData("#Skip", true)]
    [InlineData("some text /ignore", true)]
    [InlineData("some text #skip more text", true)]
    [InlineData("/ignore some text", true)]
    [InlineData("don't ignore this", false)]
    [InlineData("nothing special", false)]
    [InlineData("/ignored", true)]
    [InlineData("#skipped", true)]
    public void ShouldBeIgnored_Tests(string text, bool expected)
    {
        var result = IgnorePattern.IsMatch(text);
        result.ShouldBe(expected);
    }
}

public class DateParsingTests
{
    private (DateTime? startDate, DateTime? endDate, string? error) ParseCustomDateRange(string msgText)
    {
        var parts = msgText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return (null, null, "Использование: /command YYYY-MM-DD YYYY-MM-DD");

        if (!DateTime.TryParseExact(parts[1], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var startDate))
            return (null, null, $"Неправильный формат первой даты: {parts[1]}. Используйте формат YYYY-MM-DD");

        if (!DateTime.TryParseExact(parts[2], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var endDate))
            return (null, null, $"Неправильный формат второй даты: {parts[2]}. Используйте формат YYYY-MM-DD");

        if (startDate > endDate)
            return (null, null, "Первая дата должна быть раньше или равна второй дате");

        endDate = endDate.AddDays(1);

        return (startDate, endDate, null);
    }

    [Fact]
    public void ParseCustomDateRange_ValidDates_ReturnsCorrectRange()
    {
        var (startDate, endDate, error) = ParseCustomDateRange("/top_posts_custom 2026-01-01 2026-01-07");

        error.ShouldBeNull();
        startDate.ShouldBe(new DateTime(2026, 1, 1));
        endDate.ShouldBe(new DateTime(2026, 1, 8));
    }

    [Fact]
    public void ParseCustomDateRange_SameDates_ReturnsOneDayRange()
    {
        var (startDate, endDate, error) = ParseCustomDateRange("/command 2026-01-01 2026-01-01");

        error.ShouldBeNull();
        startDate.ShouldBe(new DateTime(2026, 1, 1));
        endDate.ShouldBe(new DateTime(2026, 1, 2));
    }

    [Fact]
    public void ParseCustomDateRange_ReversedDates_ReturnsError()
    {
        var (startDate, endDate, error) = ParseCustomDateRange("/command 2026-01-07 2026-01-01");

        error!.ShouldContain("раньше");
        startDate.ShouldBeNull();
        endDate.ShouldBeNull();
    }

    [Fact]
    public void ParseCustomDateRange_InvalidFirstDate_ReturnsError()
    {
        var (startDate, endDate, error) = ParseCustomDateRange("/command invalid 2026-01-01");

        error!.ShouldContain("первой даты");
        startDate.ShouldBeNull();
        endDate.ShouldBeNull();
    }

    [Fact]
    public void ParseCustomDateRange_InvalidSecondDate_ReturnsError()
    {
        var (startDate, endDate, error) = ParseCustomDateRange("/command 2026-01-01 invalid");

        error!.ShouldContain("второй даты");
        startDate.ShouldBeNull();
        endDate.ShouldBeNull();
    }

    [Fact]
    public void ParseCustomDateRange_MissingArguments_ReturnsError()
    {
        var (startDate, endDate, error) = ParseCustomDateRange("/command 2026-01-01");

        error!.ShouldContain("Использование");
        startDate.ShouldBeNull();
        endDate.ShouldBeNull();
    }
}
