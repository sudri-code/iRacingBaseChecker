using FfbLatency.Analysis.Signals;

namespace FfbLatency.Analysis.Tests;

/// <summary>
/// Генератор синтетических step-трейсов с заранее известной задержкой.
/// Позволяет проверять оценщик без железа: истинный ответ известен точно.
/// </summary>
public static class SyntheticStep
{
    /// <summary>
    /// Строит трейс: участок покоя, затем — после ровно <paramref name="deadTimeMs"/> —
    /// равноускоренное движение.
    /// </summary>
    /// <param name="accelPerMs2">Ускорение в отсчётах энкодера на мс². ~0.4 соответствует DD-базе на 25% усилия.</param>
    /// <param name="quantum">Шаг квантования энкодера в отсчётах. Именно он мешает поймать самое начало движения.</param>
    /// <param name="timeJitterUs">Разброс момента снятия отсчёта — имитирует планировщик Windows.</param>
    public static StepTrace Build(
        double deadTimeMs = 6.0,
        double accelPerMs2 = 0.4,
        double sampleRateHz = 1000,
        double noiseSigma = 0.0,
        int quantum = 1,
        double restMs = 200,
        double moveMs = 60,
        int magnitude = 2500,
        double timeJitterUs = 0,
        int seed = 12345)
    {
        var rng = new Random(seed);
        var samples = new List<Sample>();

        const long commandTimeUs = 1_000_000; // произвольная точка отсчёта
        double stepUs = 1_000_000.0 / sampleRateHz;
        double sign = Math.Sign(magnitude);

        double startUs = commandTimeUs - restMs * 1000.0;
        double endUs = commandTimeUs + moveMs * 1000.0;

        for (double t = startUs; t <= endUs; t += stepUs)
        {
            double jitter = timeJitterUs > 0 ? (rng.NextDouble() - 0.5) * 2 * timeJitterUs : 0;
            long timeUs = (long)Math.Round(t + jitter);

            double sinceCommandMs = (timeUs - commandTimeUs) / 1000.0;
            double movedMs = sinceCommandMs - deadTimeMs;

            double trueValue = movedMs <= 0 ? 0 : sign * 0.5 * accelPerMs2 * movedMs * movedMs;

            if (noiseSigma > 0) trueValue += Gaussian(rng) * noiseSigma;

            int quantized = quantum <= 1
                ? (int)Math.Round(trueValue)
                : (int)Math.Round(trueValue / quantum) * quantum;

            samples.Add(new Sample(timeUs, quantized));
        }

        return new StepTrace
        {
            CommandTimeUs = commandTimeUs,
            Magnitude = magnitude,
            Samples = samples,
        };
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
