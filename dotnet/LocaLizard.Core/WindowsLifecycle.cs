namespace LocaLizard.Core;

public sealed record WindowsSupportInfo(
    int BuildFamily,
    string Product,
    DateOnly EndOfSupport,
    bool IsSupported,
    int DaysRemaining,
    string Message);

public static class WindowsLifecycle
{
    private static readonly IReadOnlyDictionary<int, (string Product, DateOnly End)> Releases =
        new Dictionary<int, (string, DateOnly)>
        {
            [10240] = ("Windows 10 IoT Enterprise LTSB 2015", new DateOnly(2025, 10, 14)),
            [14393] = ("Windows 10 IoT Enterprise LTSB 2016", new DateOnly(2026, 10, 13)),
            [17763] = ("Windows 10 IoT Enterprise LTSC 2019", new DateOnly(2029, 1, 9)),
            [19044] = ("Windows 10 IoT Enterprise LTSC 2021", new DateOnly(2032, 1, 13)),
            [26100] = ("Windows 11 IoT Enterprise LTSC 2024", new DateOnly(2034, 10, 10))
        };

    public static WindowsSupportInfo? GetInfo(int buildFamily, DateOnly? today = null)
    {
        if (!Releases.TryGetValue(buildFamily, out var release)) return null;
        var date = today ?? DateOnly.FromDateTime(DateTime.Today);
        var remaining = release.End.DayNumber - date.DayNumber;
        var supported = remaining >= 0;
        var message = supported
            ? $"Поддержка {release.Product} по опубликованному жизненному циклу IoT Enterprise заканчивается {release.End:dd.MM.yyyy}."
            : $"Поддержка {release.Product} завершилась {release.End:dd.MM.yyyy}. Программа продолжит подготовку ISO, но актуальные обновления безопасности могут быть недоступны.";
        return new WindowsSupportInfo(buildFamily, release.Product, release.End, supported, remaining, message);
    }
}