using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FfbLatency.Spike;

/// <summary>
/// Невидимое окно, принадлежащее текущему процессу.
///
/// DirectInput требует, чтобы окно, переданное в SetCooperativeLevel, принадлежало
/// вызывающему процессу. Окно от GetConsoleWindow() этому условию не удовлетворяет:
/// под Windows Terminal и современным conhost оно принадлежит хосту консоли, а не
/// нашему приложению. Причём SetCooperativeLevel такой HWND принимает молча,
/// и ошибка вылезает только на Acquire как 0x80070578 (ERROR_INVALID_WINDOW_HANDLE).
///
/// В WPF-приложении этого класса не понадобится — там окно и так своё.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HiddenWindow : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Делегат обязан пережить окно: GC не знает, что на него ссылается неуправляемый код.
    private readonly WndProcDelegate _wndProc;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    private IntPtr _hwnd;
    private Exception? _startupError;
    private volatile bool _disposed;

    public IntPtr Handle => _hwnd;

    public HiddenWindow()
    {
        _wndProc = StaticWndProc;

        // Окно живёт в собственном потоке с циклом сообщений: иначе система сочтёт
        // его зависшим, а нам нужно, чтобы оно оставалось валидным всё время замера.
        _thread = new Thread(ThreadBody)
        {
            IsBackground = true,
            Name = "FfbLatency hidden window",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Не удалось создать скрытое окно за 5 секунд.");

        if (_startupError is not null)
            throw _startupError;
    }

    private void ThreadBody()
    {
        try
        {
            IntPtr hInstance = GetModuleHandleW(null);
            string className = "FfbLatencySpikeWindow_" + Environment.ProcessId;
            IntPtr classNamePtr = Marshal.StringToHGlobalUni(className);
            IntPtr windowNamePtr = Marshal.StringToHGlobalUni("FfbLatency");

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = classNamePtr,
            };

            ushort atom = RegisterClassExW(ref wc);
            if (atom == 0)
                throw new InvalidOperationException($"RegisterClassEx не удался, ошибка {Marshal.GetLastWin32Error()}.");

            // Обычное, но не показанное окно. Message-only окно (HWND_MESSAGE) здесь
            // не подходит: оно не участвует в оконной иерархии так, как ожидает DirectInput.
            _hwnd = CreateWindowExW(
                0, classNamePtr, windowNamePtr, WS_POPUP,
                0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowEx не удался, ошибка {Marshal.GetLastWin32Error()}.");

            _ready.Set();

            while (!_disposed && GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
        }
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DESTROY) PostQuitMessage(0);
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _hwnd = IntPtr.Zero;
        }

        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, IntPtr lpClassName, IntPtr lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
