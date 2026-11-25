using System.Globalization;

namespace NetRun.Tests;

/// <summary>Minimal assertion helper in the style of the existing <c>NetDM.SmokeTests</c> harness.</summary>
internal static class Harness
{
    private static int _checks;

    public static void Check(bool condition, string message)
    {
        _checks++;
        if (condition)
            return;
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"FAILED: {message}"));
        Environment.ExitCode = 1;
        throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual),
            string.Create(CultureInfo.InvariantCulture, $"{message}: expected {expected}, got {actual}"));

    public static void Section(string title) => Console.WriteLine($"-- {title}");

    public static int Count => _checks;
}
