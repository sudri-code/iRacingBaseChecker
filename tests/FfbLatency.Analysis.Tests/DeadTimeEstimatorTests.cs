using FfbLatency.Analysis.DeadTime;
using FfbLatency.Analysis.Signals;
using Xunit;
using Xunit.Abstractions;

namespace FfbLatency.Analysis.Tests;

public class DeadTimeEstimatorTests(ITestOutputHelper output)
{
    [Fact]
    public void RecoversKnownDeadTime_OnCleanSignal()
    {
        var trace = SyntheticStep.Build(deadTimeMs: 6.0, noiseSigma: 0, quantum: 1);

        var result = DeadTimeEstimator.Estimate(trace);

        Assert.True(result.Ok, result.Reason);
        Assert.InRange(result.DeadTimeMs, 5.7, 6.3);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(6.0)]
    [InlineData(11.5)]
    public void RecoversKnownDeadTime_AcrossRange(double trueDeadTimeMs)
    {
        var trace = SyntheticStep.Build(deadTimeMs: trueDeadTimeMs, noiseSigma: 0.3, quantum: 1);

        var result = DeadTimeEstimator.Estimate(trace);

        Assert.True(result.Ok, result.Reason);
        Assert.InRange(result.DeadTimeMs, trueDeadTimeMs - 0.6, trueDeadTimeMs + 0.6);
    }

    [Fact]
    public void WorksForNegativeForce()
    {
        var trace = SyntheticStep.Build(deadTimeMs: 5.0, magnitude: -2500, noiseSigma: 0.3);

        var result = DeadTimeEstimator.Estimate(trace);

        Assert.True(result.Ok, result.Reason);
        Assert.InRange(result.DeadTimeMs, 4.4, 5.6);
        Assert.True(result.Acceleration < 0, "ускорение должно быть отрицательным при отрицательной силе");
    }

    /// <summary>
    /// Смысл всей затеи с экстраполяцией. Порог не может сработать раньше, чем руль
    /// пройдёт хотя бы один квант энкодера, поэтому он систематически завышает задержку —
    /// и тем сильнее, чем грубее энкодер. Экстраполяция от разрешения почти не зависит,
    /// а значит числа двух разных баз становятся сравнимыми.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void ExtrapolationBeatsThreshold_AtCoarseEncoderResolution(int quantum)
    {
        const double trueDeadTime = 6.0;
        var trace = SyntheticStep.Build(deadTimeMs: trueDeadTime, quantum: quantum, noiseSigma: 0);

        var result = DeadTimeEstimator.Estimate(trace);

        Assert.True(result.Ok, result.Reason);

        double extrapolationError = Math.Abs(result.DeadTimeMs - trueDeadTime);
        double thresholdError = Math.Abs(result.ThresholdDelayUs / 1000.0 - trueDeadTime);

        output.WriteLine($"квант {quantum,2}: экстраполяция {result.DeadTimeMs:F2} мс (ошибка {extrapolationError:F2}), " +
                         $"порог {result.ThresholdDelayUs / 1000.0:F2} мс (ошибка {thresholdError:F2})");

        Assert.True(extrapolationError < thresholdError,
            $"экстраполяция ({extrapolationError:F2} мс) должна быть точнее порога ({thresholdError:F2} мс)");
        Assert.True(extrapolationError < 1.0, $"ошибка экстраполяции {extrapolationError:F2} мс слишком велика");
    }

    [Fact]
    public void ThresholdAlwaysLagsBehindExtrapolation()
    {
        var trace = SyntheticStep.Build(deadTimeMs: 6.0, noiseSigma: 0.3);

        var result = DeadTimeEstimator.Estimate(trace);

        Assert.True(result.Ok, result.Reason);
        Assert.True(result.ThresholdDelayUs > result.DeadTimeUs,
            "порог обязан срабатывать позже истинного начала движения");
    }

    [Fact]
    public void ToleratesSchedulerJitter()
    {
        // Windows не даёт снимать отсчёты строго по таймеру; проверяем, что это не ломает оценку.
        // Допуск здесь широкий намеренно: это одиночный повтор, а методика опирается на медиану.
        var trace = SyntheticStep.Build(deadTimeMs: 6.0, noiseSigma: 0.3, timeJitterUs: 300);

        var result = DeadTimeEstimator.Estimate(trace);

        Assert.True(result.Ok, result.Reason);
        Assert.InRange(result.DeadTimeMs, 5.0, 7.0);
    }

    /// <summary>
    /// Проверка того, как оценщик используется на самом деле. Ошибка одиночного повтора
    /// при реалистичном шуме и джиттере доходит до ~1 мс, но она случайна и потому
    /// подавляется усреднением. Именно медиана по серии — та величина, по которой
    /// сравниваются базы, и именно её точность имеет значение.
    /// </summary>
    [Fact]
    public void MedianOverManyRepeats_IsAccurate()
    {
        const double trueDeadTime = 6.0;
        const int repeats = 200;

        var estimates = new List<double>(repeats);
        for (int i = 0; i < repeats; i++)
        {
            // Знак чередуется, как в реальном тесте, чтобы руль не уезжал в упор.
            int magnitude = i % 2 == 0 ? 2500 : -2500;
            var trace = SyntheticStep.Build(
                deadTimeMs: trueDeadTime,
                noiseSigma: 0.3,
                timeJitterUs: 300,
                magnitude: magnitude,
                seed: 1000 + i);

            var r = DeadTimeEstimator.Estimate(trace);
            if (r.Ok) estimates.Add(r.DeadTimeMs);
        }

        Assert.True(estimates.Count > repeats * 0.9,
            $"слишком много повторов забраковано: прошло {estimates.Count} из {repeats}");

        estimates.Sort();
        double median = estimates[estimates.Count / 2];
        double spread = estimates[(int)(estimates.Count * 0.95)] - estimates[(int)(estimates.Count * 0.05)];

        output.WriteLine($"медиана {median:F3} мс (истина {trueDeadTime}), разброс p5–p95 {spread:F3} мс, повторов {estimates.Count}");

        Assert.InRange(median, trueDeadTime - 0.25, trueDeadTime + 0.25);
    }

    [Fact]
    public void ReportsFailure_WhenWheelNeverMoves()
    {
        var trace = SyntheticStep.Build(accelPerMs2: 0, noiseSigma: 0, quantum: 1);

        var result = DeadTimeEstimator.Estimate(trace);

        Assert.False(result.Ok);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void ReportsFailure_WhenNoRestSamplesPrecedeCommand()
    {
        var trace = new StepTrace
        {
            CommandTimeUs = 0,
            Magnitude = 2500,
            Samples = Enumerable.Range(0, 50).Select(i => new Sample(i * 1000, i * i)).ToList(),
        };

        var result = DeadTimeEstimator.Estimate(trace);

        Assert.False(result.Ok);
        Assert.Contains("покоя", result.Reason);
    }

    /// <summary>
    /// Дрейф руля в сторону, противоположную приложенной силе, не должен быть принят
    /// за отклик: это отдача или чужое воздействие, и повтор надо выбраковывать.
    /// </summary>
    [Fact]
    public void RejectsMotionOpposingTheAppliedForce()
    {
        var trace = SyntheticStep.Build(deadTimeMs: 6.0, magnitude: 2500, noiseSigma: 0);
        var flipped = new StepTrace
        {
            CommandTimeUs = trace.CommandTimeUs,
            Magnitude = trace.Magnitude,
            Samples = trace.Samples.Select(s => new Sample(s.TimeUs, -s.Position)).ToList(),
        };

        var result = DeadTimeEstimator.Estimate(flipped);

        Assert.False(result.Ok);
        Assert.Contains("знак", result.Reason);
    }
}
