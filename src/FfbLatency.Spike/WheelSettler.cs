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
/// ничем не удерживается и продолжает двигаться. Скорость гасится активно — программным
/// демпфером, плюс пружина возвращает руль к исходной точке, чтобы за сотни повторов
/// он не уполз в упор.
///
/// Регулятор работает только между замерами и обязательно выключается перед подачей
/// ступеньки: во время самого замера никакой посторонней силы быть не должно.
/// </summary>
internal static class WheelSettler
{
    /// <summary>Сила пружины на отсчёт отклонения от цели.</summary>
    /// <remarks>При отклонении ~10000 отсчётов упирается в потолок усилия.</remarks>
    private const double SpringGain = 0.4;

    /// <summary>
    /// Сила демпфера на единицу скорости (отсчёты/мс).
    /// </summary>
    /// <remarks>
    /// Порядок величины здесь принципиален. Полный ход руля — 65536 отсчётов на 900°,
    /// так что один оборот в секунду это уже ~26 отсчётов/мс. Прежнее значение 12 давало
    /// при такой скорости силу ~310 из 10000 и практически не тормозило — руль уезжал
    /// в упор, а успокоитель считал его остановившимся.
    /// </remarks>
    private const double DamperGain = 250.0;

    /// <summary>Потолок удерживающего усилия.</summary>
    private const int MaxHoldForce = 4000;

    /// <summary>Сглаживание оценки скорости: позиция квантована, а шаг цикла неровный.</summary>
    private const double VelocitySmoothing = 0.35;

    /// <summary>Насколько неподвижным должен стать руль, отсчётов за окно проверки.</summary>
    private const int StillnessThreshold = 3;

    /// <summary>Сколько подряд миллисекунд руль должен оставаться неподвижным.</summary>
    private const int StillnessWindowMs = 120;

    /// <summary>
    /// Гасит движение и удерживает руль около <paramref name="targetPosition"/>.
    /// </summary>
    /// <param name="tolerance">
    /// Допустимое расстояние до цели. Остановка дальше этого расстояния считается упором,
    /// а не успокоением: неподвижность сама по себе ничего не доказывает — руль,
    /// прижатый к ограничителю, тоже неподвижен.
    /// </param>
    public static SettleOutcome Settle(
        IDirectInputDevice8 device,
        IDirectInputEffect effect,
        EffectParameters p,
        AxisDef axis,
        int targetPosition,
        int tolerance = 4000,
        int timeoutMs = 4000)
    {
        var state = new JoystickState();
        var force = new ConstantForce();
        long freq = Stopwatch.Frequency;
        long deadline = Stopwatch.GetTimestamp() + freq * timeoutMs / 1000;

        device.Poll();
        device.GetCurrentJoystickState(ref state);
        int previous = Axes.Read(state, axis.Index);
        long previousTime = Stopwatch.GetTimestamp();

        double velocity = 0;
        int stillMin = previous, stillMax = previous;
        long stillSince = previousTime;

        while (Stopwatch.GetTimestamp() < deadline)
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

            if (position < stillMin) stillMin = position;
            if (position > stillMax) stillMax = position;

            if (stillMax - stillMin > StillnessThreshold)
            {
                stillMin = stillMax = position;
                stillSince = now;
            }
            else if ((now - stillSince) * 1000.0 / freq >= StillnessWindowMs)
            {
                Release(effect, p, force);
                return Math.Abs(position - targetPosition) <= tolerance
                    ? SettleOutcome.Settled
                    : SettleOutcome.Stuck;
            }

            double command = -SpringGain * (position - targetPosition) - DamperGain * velocity;
            force.Magnitude = (int)Math.Clamp(command, -MaxHoldForce, MaxHoldForce);
            p.Parameters = force;
            effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);

            previous = position;
            previousTime = now;
        }

        Release(effect, p, force);
        return SettleOutcome.Timeout;
    }

    private static void Release(IDirectInputEffect effect, EffectParameters p, ConstantForce force)
    {
        force.Magnitude = 0;
        p.Parameters = force;
        effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);
    }
}
