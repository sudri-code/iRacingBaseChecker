using System.Runtime.InteropServices;

namespace FfbLatency.Spike;

/// <summary>
/// P/Invoke, нужные для spike. Резолвятся только под Windows — проект намеренно
/// таргетит net8.0 (а не net8.0-windows), чтобы компилироваться и на macOS.
/// </summary>
internal static partial class Native
{
    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetConsoleWindow();

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    internal static partial uint TimeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    internal static partial uint TimeEndPeriod(uint uPeriod);

    /// <summary>
    /// Окно для SetCooperativeLevel. В консоли берём окно консоли; если его нет
    /// (запуск без консоли), DirectInput примет и рабочий стол.
    /// </summary>
    internal static IntPtr GetOwnerWindow()
    {
        var hwnd = GetConsoleWindow();
        return hwnd;
    }
}
