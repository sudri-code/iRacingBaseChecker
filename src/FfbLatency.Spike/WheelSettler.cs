using System.Diagnostics;
using Vortice.DirectInput;

namespace FfbLatency.Spike;

/// <summary>
/// Приводит руль в покой между повторами step-теста.
///
/// Автоцентр выключен (иначе его пружина исказит отклик), поэтому после толчка руль
/// ничем не удерживается и продолжает двигаться. Пассивное ожидание не работает:
/// в прогоне на R21 к пятому повтору шум позиции дорос до 8659 отсчётов и один замер
/// был потерян полностью. Поэтому скорость гасится активно — программным демпфером,
/// плюс слабая пружина, чтобы за сотни повторов руль не уполз в упор.
///
/// Регулятор работает только между замерами и обязательно выключается перед подачей
/// ступеньки: во время самого замера никакой посторонней силы быть не должно.
/// </summary>
internal static class WheelSettler
{
    /// <summary>Сила пружины на отсчёт отклонения от цели.</summary>
    private const double SpringGain = 0.15;

    /// <summary>Сила демпфера на отсчёт скорости (отсчёты/мс).</summary>
    private const double DamperGain = 12.0;

    /// <summary>Потолок удерживающего усилия — четверть максимума, чтобы руль не рвало.</summary>
    private const int MaxHoldForce = 2500;

    /// <summary>Насколько неподвижным должен стать руль, отсчётов за окно проверки.</summary>
    private const int StillnessThreshold = 3;

    /// <summary>Сколько подряд миллисекунд руль должен оставаться неподвижным.</summary>
    private const int StillnessWindowMs = 120;

    /// <summary>
    /// Гасит движение и удерживает руль около <paramref name="targetPosition"/>,
    /// пока он не успокоится или не выйдет время.
    /// </summary>
    /// <returns>true, если руль действительно остановился.</returns>
    public static bool Settle(
        IDirectInputDevice8 device,
        IDirectInputEffect effect,
        EffectParameters p,
        AxisDef axis,
        int targetPosition,
        int timeoutMs = 3000)
    {
        var state = new JoystickState();
        var force = new ConstantForce();
        long freq = Stopwatch.Frequency;
        long deadline = Stopwatch.GetTimestamp() + freq * timeoutMs / 1000;

        device.Poll();
        device.GetCurrentJoystickState(ref state);
        int previous = Axes.Read(state, axis.Index);
        long previousTime = Stopwatch.GetTimestamp();

        int stillMin = previous, stillMax = previous;
        long stillSince = previousTime;

        while (Stopwatch.GetTimestamp() < deadline)
        {
            device.Poll();
            device.GetCurrentJoystickState(ref state);
            int position = Axes.Read(state, axis.Index);
            long now = Stopwatch.GetTimestamp();

            double dtMs = (now - previousTime) * 1000.0 / freq;
            double velocity = dtMs > 0 ? (position - previous) / dtMs : 0;

            // Разжимаем окно неподвижности, если руль ушёл за его пределы.
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
                return true;
            }

            double command = -SpringGain * (position - targetPosition) - DamperGain * velocity;
            int magnitude = (int)Math.Clamp(command, -MaxHoldForce, MaxHoldForce);

            force.Magnitude = magnitude;
            p.Parameters = force;
            effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);

            previous = position;
            previousTime = now;
        }

        Release(effect, p, force);
        return false;
    }

    private static void Release(IDirectInputEffect effect, EffectParameters p, ConstantForce force)
    {
        force.Magnitude = 0;
        p.Parameters = force;
        effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);
    }
}
