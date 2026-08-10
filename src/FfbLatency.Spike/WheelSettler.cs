using System.Diagnostics;
using Vortice.DirectInput;

namespace FfbLatency.Spike;

/// <summary>Чем закончилась попытка успокоить руль.</summary>
internal enum SettleOutcome
{
    /// <summary>Руль остановился рядом с целевой точкой — можно мерить.</summary>
    Settled,

    /// <summary>Руль неподвижен, но далеко от цели: почти наверняка упёрся в ограничитель.</summary>
    Stuck,

    /// <summary>Руль так и не остановился за отведённое время.</summary>
    Timeout,
}

/// <summary>
/// Приводит руль в покой между повторами step-теста.
///
/// Автоцентр выключен (иначе его пружина исказит отклик), поэтому после толчка руль
/// ничем не удерживается и продолжает двигаться.
///
/// Работа идёт в две фазы. Сначала активная: демпфер гасит скорость, пружина подтягивает
/// руль к исходной точке, чтобы за сотни повторов он не уполз в упор. Затем усилие
/// полностью снимается и руль должен успокоиться сам.
///
/// Вторая фаза принципиальна. Пока регулятор удерживает руль, тот слегка автоколеблется:
/// в контуре сидит задержка самой базы около десяти миллисекунд, а скорость считается
/// по разности квантованных отсчётов. Замерять в этот момент нельзя — «покой» окажется
/// движением в тысячу отсчётов, и порог детектирования уедет в небо. Ступеньку нужно
/// подавать на свободный руль.
/// </summary>
internal static class WheelSettler
{
    /// <summary>Сила пружины на отсчёт отклонения от цели.</summary>
    private const double SpringGain = 0.4;

    /// <summary>
    /// Сила демпфера на единицу скорости (отсчёты/мс).
    /// </summary>
    /// <remarks>
    /// Полный ход руля — 65536 отсчётов на 900°, так что один оборот в секунду это
    /// ~26 отсчётов/мс. Слишком малое значение не тормозит вовсе, слишком большое
    /// раскачивает контур из-за задержки базы. 120 — компромисс.
    /// </remarks>
    private const double DamperGain = 120.0;

    /// <summary>Потолок удерживающего усилия.</summary>
    private const int MaxHoldForce = 4000;

    /// <summary>Сглаживание оценки скорости: позиция квантована, а шаг цикла неровный.</summary>
    private const double VelocitySmoothing = 0.25;

    /// <summary>Скорость, ниже которой активную фазу можно заканчивать, отсчёты/мс.</summary>
    private const double SlowEnough = 1.0;

    /// <summary>
    /// Разброс позиции, при котором руль считается стоящим, отсчётов.
    /// </summary>
    /// <remarks>
    /// 20 отсчётов при полном ходе 65536 на 900° — это примерно четверть градуса.
    /// Более жёсткий критерий недостижим: база отдаёт позицию с дрожанием младшего разряда.
    /// </remarks>
    private const int StillnessThreshold = 20;

    /// <summary>Сколько подряд миллисекунд руль должен оставаться неподвижным.</summary>
    private const int StillnessWindowMs = 100;

    /// <summary>
    /// Гасит движение, возвращает руль к <paramref name="targetPosition"/> и отпускает его.
    /// </summary>
    /// <param name="polarity">
    /// Знак связи между усилием и направлением движения оси. Величину обязательно измерять:
    /// у Moza ось инвертирована, и при неверном знаке регулятор превращается из отрицательной
    /// обратной связи в положительную — вместо торможения разгоняет руль до края диапазона.
    /// </param>
    /// <param name="returnTolerance">
    /// Насколько точно активная фаза возвращает руль к цели. Должен быть заметно меньше
    /// <paramref name="stuckTolerance"/>: если прекращать возврат уже на границе допуска,
    /// каждый повтор оставляет руль сдвинутым, и за серию ошибка накапливается в дрейф.
    /// </param>
    /// <param name="stuckTolerance">
    /// Расстояние, дальше которого остановка считается упором, а не успокоением:
    /// неподвижность сама по себе ничего не доказывает — руль, прижатый к ограничителю,
    /// тоже неподвижен.
    /// </param>
    public static SettleOutcome Settle(
        IDirectInputDevice8 device,
        IDirectInputEffect effect,
        EffectParameters p,
        AxisDef axis,
        int targetPosition,
        int polarity,
        EffectParameterFlags flags,
        int returnTolerance = 800,
        int stuckTolerance = 6000,
        int activeMs = 3000,
        int relaxMs = 1500)
    {
        var state = new JoystickState();
        var force = new ConstantForce();
        long freq = Stopwatch.Frequency;

        // ── Фаза 1: активно гасим и подтягиваем к цели ────────────────────────
        long activeDeadline = Stopwatch.GetTimestamp() + freq * activeMs / 1000;

        device.Poll();
        device.GetCurrentJoystickState(ref state);
        int previous = Axes.Read(state, axis.Index);
        long previousTime = Stopwatch.GetTimestamp();
        double velocity = 0;

        while (Stopwatch.GetTimestamp() < activeDeadline)
        {
            device.Poll();
            device.GetCurrentJoystickState(ref state);
            int position = Axes.Read(state, axis.Index);
            long now = Stopwatch.GetTimestamp();

            double dtMs = (now - previousTime) * 1000.0 / freq;
            if (dtMs > 0)
            {
                double instant = (position - previous) / dtMs;
                velocity += VelocitySmoothing * (instant - velocity);
            }

            previous = position;
            previousTime = now;

            if (Math.Abs(position - targetPosition) <= returnTolerance && Math.Abs(velocity) < SlowEnough)
                break;

            double command = polarity * (-SpringGain * (position - targetPosition) - DamperGain * velocity);
            force.Magnitude = (int)Math.Clamp(command, -MaxHoldForce, MaxHoldForce);
            p.Parameters = force;
            effect.SetParameters(p, flags);
        }

        // ── Фаза 2: отпускаем и ждём, пока руль встанет сам ───────────────────
        force.Magnitude = 0;
        p.Parameters = force;
        effect.SetParameters(p, flags);

        long relaxDeadline = Stopwatch.GetTimestamp() + freq * relaxMs / 1000;

        device.Poll();
        device.GetCurrentJoystickState(ref state);
        int stillMin = Axes.Read(state, axis.Index);
        int stillMax = stillMin;
        long stillSince = Stopwatch.GetTimestamp();

        while (Stopwatch.GetTimestamp() < relaxDeadline)
        {
            device.Poll();
            device.GetCurrentJoystickState(ref state);
            int position = Axes.Read(state, axis.Index);
            long now = Stopwatch.GetTimestamp();

            if (position < stillMin) stillMin = position;
            if (position > stillMax) stillMax = position;

            if (stillMax - stillMin > StillnessThreshold)
            {
                stillMin = stillMax = position;
                stillSince = now;
                continue;
            }

            if ((now - stillSince) * 1000.0 / freq >= StillnessWindowMs)
            {
                return Math.Abs(position - targetPosition) <= stuckTolerance
                    ? SettleOutcome.Settled
                    : SettleOutcome.Stuck;
            }
        }

        return SettleOutcome.Timeout;
    }
}
