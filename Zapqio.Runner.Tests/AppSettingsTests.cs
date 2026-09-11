namespace Zapqio.Runner.Tests;

/// <summary>Domyślne wartości nowych ustawień to dotychczasowe zachowanie; wartości bez sensu są sprowadzane do dopuszczalnych.</summary>
public class AppSettingsTests
{
    [Fact]
    public void Defaults_KeepTheRunnerSequential()
    {
        var settings = new AppSettings();

        Assert.Equal(1, settings.MaxConcurrency);
        Assert.Equal(30, settings.StopTimeoutSeconds);
        Assert.Equal(10_000, settings.MaxQueuedLogLines);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    public void Normalize_KeepsMaxConcurrencyAtLeastOne(int configured, int expected)
    {
        var settings = new AppSettings { MaxConcurrency = configured };

        settings.Normalize();

        Assert.Equal(expected, settings.MaxConcurrency);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(45, 45)]
    public void Normalize_KeepsStopTimeoutNonNegative(int configured, int expected)
    {
        var settings = new AppSettings { StopTimeoutSeconds = configured };

        settings.Normalize();

        Assert.Equal(expected, settings.StopTimeoutSeconds);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(50, 100)]
    [InlineData(100, 100)]
    [InlineData(500, 500)]
    public void Normalize_KeepsLogQueueAtLeastAHundredLines(int configured, int expected)
    {
        var settings = new AppSettings { MaxQueuedLogLines = configured };

        settings.Normalize();

        Assert.Equal(expected, settings.MaxQueuedLogLines);
    }
}
