using Vortice.DirectInput;

namespace FfbLatency.Spike;

/// <summary>
/// Описание одной оси устройства: как её читать из состояния и как адресовать в FFB-эффекте.
/// </summary>
/// <param name="Name">Человекочитаемое имя оси.</param>
/// <param name="Offset">Смещение оси в формате данных DirectInput — им эффект привязывается к оси.</param>
/// <param name="Index">Порядковый номер для быстрого чтения значения без делегатов.</param>
internal sealed record AxisDef(string Name, JoystickOffset Offset, int Index);
