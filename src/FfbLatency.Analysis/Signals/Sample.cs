namespace FfbLatency.Analysis.Signals;

/// <summary>
/// Один отсчёт положения оси. Структура намеренно маленькая и без ссылок:
/// измерительный поток пишет их в преаллоцированный массив без единой аллокации.
/// </summary>
/// <param name="TimeUs">Момент получения отсчёта, микросекунды по QPC.</param>
/// <param name="Position">Сырое значение оси в отсчётах DirectInput.</param>
public readonly record struct Sample(long TimeUs, int Position);

/// <summary>
/// Одна ступенька step-теста: покой, затем команда, затем движение.
/// </summary>
public sealed class StepTrace
{
    /// <summary>Момент отправки команды (QPC, мкс). Отсчёты до него — покой.</summary>
    public required long CommandTimeUs { get; init; }

    /// <summary>Знак и величина поданного усилия, отсчёты DirectInput (-10000..10000).</summary>
    public required int Magnitude { get; init; }

    /// <summary>Все отсчёты повтора, включая участок покоя до команды. Упорядочены по времени.</summary>
    public required IReadOnlyList<Sample> Samples { get; init; }
}
