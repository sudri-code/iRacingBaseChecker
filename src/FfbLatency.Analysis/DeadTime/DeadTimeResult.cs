namespace FfbLatency.Analysis.DeadTime;

/// <summary>
/// Результат оценки мёртвого времени для одного повтора step-теста.
/// </summary>
public sealed record DeadTimeResult
{
    /// <summary>Удалась ли основная оценка (параболическая экстраполяция).</summary>
    public required bool Ok { get; init; }

    /// <summary>Основная метрика: задержка от команды до начала движения, мкс.</summary>
    public double DeadTimeUs { get; init; }

    /// <summary>
    /// Задержка по срабатыванию порога, мкс. Всегда больше <see cref="DeadTimeUs"/>.
    /// Держим её как sanity-check: если основная оценка «уехала», разница станет заметной.
    /// </summary>
    public double ThresholdDelayUs { get; init; }

    /// <summary>СКО шума позиции на участке покоя, отсчёты энкодера.</summary>
    public double NoiseSigma { get; init; }

    /// <summary>Оценённое угловое ускорение, отсчёты/мкс². Знак должен совпадать с направлением силы.</summary>
    public double Acceleration { get; init; }

    /// <summary>Качество подгонки параболы. Низкое значение — повтор стоит выбросить.</summary>
    public double FitR2 { get; init; }

    /// <summary>Причина, по которой оценка неполная или не удалась.</summary>
    public string? Reason { get; init; }

    public double DeadTimeMs => DeadTimeUs / 1000.0;

    public static DeadTimeResult Failed(string reason) =>
        new() { Ok = false, Reason = reason };

    /// <summary>
    /// Порог сработал, но экстраполяция не удалась. Результат по порогу сохраняем:
    /// он завышен, но всё-таки информативен, и явно помечен как неполный.
    /// </summary>
    public static DeadTimeResult Partial(double thresholdDelayUs, double sigma, string reason) =>
        new()
        {
            Ok = false,
            ThresholdDelayUs = thresholdDelayUs,
            NoiseSigma = sigma,
            Reason = reason,
        };
}
