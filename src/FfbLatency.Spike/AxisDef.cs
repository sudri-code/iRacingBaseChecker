using Vortice.DirectInput;

namespace FfbLatency.Spike;

/// <summary>
/// Описание одной оси устройства: как её читать из состояния и как адресовать в FFB-эффекте.
/// </summary>
/// <param name="Name">Человекочитаемое имя оси.</param>
/// <param name="Offset">Смещение оси в формате данных DirectInput — им эффект привязывается к оси.</param>
/// <param name="Index">Порядковый номер для быстрого чтения значения без делегатов.</param>
internal sealed record AxisDef(string Name, JoystickOffset Offset, int Index);

internal static class Axes
{
    /// <summary>Все оси, среди которых ищется рулевая.</summary>
    public static readonly AxisDef[] All =
    [
        new("X",         JoystickOffset.X,         0),
        new("Y",         JoystickOffset.Y,         1),
        new("Z",         JoystickOffset.Z,         2),
        new("RotationX", JoystickOffset.RotationX, 3),
        new("RotationY", JoystickOffset.RotationY, 4),
        new("RotationZ", JoystickOffset.RotationZ, 5),
        new("Slider0",   JoystickOffset.Sliders0,  6),
        new("Slider1",   JoystickOffset.Sliders1,  7),
    ];

    /// <summary>
    /// Читает значение оси по индексу. Switch вместо делегата намеренно: вызов происходит
    /// в горячем цикле опроса, где лишние косвенные переходы нежелательны.
    /// </summary>
    public static int Read(JoystickState s, int index) => index switch
    {
        0 => s.X,
        1 => s.Y,
        2 => s.Z,
        3 => s.RotationX,
        4 => s.RotationY,
        5 => s.RotationZ,
        6 => s.Sliders is { Length: > 0 } sl ? sl[0] : 0,
        7 => s.Sliders is { Length: > 1 } sl2 ? sl2[1] : 0,
        _ => 0,
    };
}
