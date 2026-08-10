using System.Diagnostics;
using FfbLatency.Spike;
using HidSharp;
using Vortice.DirectInput;

// Spike для этапа 0: снять главный технический риск проекта до того, как строить
// архитектуру и UI. Проверяем четыре вещи на живом железе:
//   1. DirectInput даёт Exclusive|Background и создаёт ConstantForce.
//   2. Позиция оси читается ПАРАЛЛЕЛЬНО с удержанием exclusive-режима.
//   3. Сколько стоит сам вызов SetParameters (он войдёт в измеряемую задержку).
//   4. Виден ли вообще step response — то есть жизнеспособна ли методика.

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== FFB Latency Spike — проверка осуществимости замера ===\n");

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("Этот spike работает только под Windows: нужны DirectInput и подключённая база.");
    return 1;
}

IDirectInput8? directInput = null;
IDirectInputDevice8? device = null;
IDirectInputEffect? effect = null;
bool autoCenterChanged = false;

try
{
    Native.TimeBeginPeriod(1);

    directInput = DInput.DirectInput8Create();

    // ── Шаг 1. Выбор устройства ───────────────────────────────────────────────
    var instances = directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
    if (instances.Count == 0)
    {
        Console.WriteLine("Не найдено ни одного игрового контроллера. База подключена и включена?");
        return 1;
    }

    Console.WriteLine("Найденные устройства:");
    for (int i = 0; i < instances.Count; i++)
    {
        var inst = instances[i];
        Console.WriteLine($"  [{i}] {inst.ProductName}  (instance: {inst.InstanceName})");
    }

    int index = AskIndex(instances.Count);
    var chosen = instances[index];
    Console.WriteLine($"\nВыбрано: {chosen.ProductName}");

    device = directInput.CreateDevice(chosen.InstanceGuid);
    device.SetDataFormat<RawJoystickState>();

    var hwnd = Native.GetOwnerWindow();
    Console.WriteLine($"HWND консоли: 0x{hwnd:X}");

    // Exclusive обязателен для FFB, Background — чтобы замер не ломался при потере фокуса.
    var coop = device.SetCooperativeLevel(hwnd, CooperativeLevel.Exclusive | CooperativeLevel.Background);
    Console.WriteLine($"SetCooperativeLevel(Exclusive|Background): {Describe(coop)}");
    if (coop.Failure)
    {
        Console.WriteLine("  → Не удалось получить exclusive-режим. Закройте SimHub / iRacing / софт базы и повторите.");
        return 1;
    }

    // ── Шаг 2. Свойства устройства ────────────────────────────────────────────
    var caps = device.Capabilities;
    Console.WriteLine("\n--- Возможности устройства ---");
    Console.WriteLine($"  Оси: {caps.AxeCount}, кнопки: {caps.ButtonCount}");
    Console.WriteLine($"  ForceFeedback: {caps.Flags.HasFlag(DeviceFlags.ForceFeedback)}");
    Console.WriteLine($"  FFB sample period:          {caps.ForceFeedbackSamplePeriod} мкс");
    Console.WriteLine($"  FFB min time resolution:    {caps.ForceFeedbackMinimumTimeResolution} мкс");
    Console.WriteLine($"  Firmware rev: {caps.FirmwareRevision}, driver: {caps.DriverVersion}");

    if (!caps.Flags.HasFlag(DeviceFlags.ForceFeedback))
    {
        Console.WriteLine("\nУ устройства нет force feedback. Выбрана не та железка?");
        return 1;
    }

    var props = device.Properties;
    int vid = 0, pid = 0;
    string? interfacePath = null;
    try
    {
        vid = props.VendorId;
        pid = props.ProductId;
        interfacePath = props.InterfacePath;
        Console.WriteLine($"  VID/PID: {vid:X4}:{pid:X4}");
    }
    catch (Exception ex) { Console.WriteLine($"  (VID/PID недоступны: {ex.Message})"); }

    // Автоцентр — это пружина, она исказит step response. Обязательно выключить.
    try
    {
        props.AutoCenter = false;
        autoCenterChanged = true;
        Console.WriteLine("  AutoCenter выключен.");
    }
    catch (Exception ex) { Console.WriteLine($"  AutoCenter выключить не удалось: {ex.Message}"); }

    // Расширяем логический диапазон оси — больше отсчётов, тоньше детект движения.
    try
    {
        props.Range = new InputRange(-32768, 32767);
        var r = props.Range;
        Console.WriteLine($"  Диапазон оси: {r.Minimum}..{r.Maximum}");
    }
    catch (Exception ex) { Console.WriteLine($"  Диапазон задать не удалось: {ex.Message}"); }

    try { props.ForceFeedbackGain = 10000; } catch { /* не критично */ }

    var acq = device.Acquire();
    Console.WriteLine($"Acquire: {Describe(acq)}");
    if (acq.Failure) return 1;

    // ── Шаг 3. Фактическая частота обновления оси ─────────────────────────────
    MeasureAxisUpdateRate(device);

    // ── Шаг 4. Создание эффекта ───────────────────────────────────────────────
    var effectParams = new EffectParameters
    {
        Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
        Duration = unchecked((int)0xFFFFFFFF), // INFINITE
        SamplePeriod = 0,
        Gain = 10000,
        TriggerButton = -1,                    // без триггера
        TriggerRepeatInterval = unchecked((int)0xFFFFFFFF),
        StartDelay = 0,
        Parameters = new ConstantForce { Magnitude = 0 },
    };
    effectParams.SetAxes(new[] { (int)JoystickOffset.X }, new[] { 0 });

    effect = device.CreateEffect(EffectGuid.ConstantForce, effectParams);
    Console.WriteLine("\nConstantForce эффект создан.");

    var startResult = effect.Start(1, EffectPlayFlags.None);
    Console.WriteLine($"Effect.Start: {Describe(startResult)}");

    // ── Шаг 5. Стоимость SetParameters ────────────────────────────────────────
    MeasureSetParametersCost(effect, effectParams);

    // ── Шаг 6. Step response ──────────────────────────────────────────────────
    Console.WriteLine("\nСейчас будет подаваться усилие ~25% — руль дёрнется.");
    Console.WriteLine("Уберите руки, освободите руль. Enter — продолжить, любая другая клавиша — пропустить.");
    if (Console.ReadKey(true).Key == ConsoleKey.Enter)
        MeasureStepResponse(device, effect, effectParams);
    else
        Console.WriteLine("Step response пропущен.");

    // ── Шаг 7. Параллельное чтение raw HID ────────────────────────────────────
    ProbeParallelHidRead(vid, pid, interfacePath);

    Console.WriteLine("\n=== Spike завершён ===");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"\nОшибка: {ex}");
    return 1;
}
finally
{
    try { effect?.Stop(); } catch { }
    try { effect?.Dispose(); } catch { }
    if (device is not null)
    {
        if (autoCenterChanged) { try { device.Properties.AutoCenter = true; } catch { } }
        try { device.Unacquire(); } catch { }
        try { device.Dispose(); } catch { }
    }
    try { directInput?.Dispose(); } catch { }
    Native.TimeEndPeriod(1);
}

// ─────────────────────────────────────────────────────────────────────────────

static int AskIndex(int count)
{
    while (true)
    {
        Console.Write($"\nНомер устройства [0..{count - 1}]: ");
        var line = Console.ReadLine();
        if (int.TryParse(line, out int i) && i >= 0 && i < count) return i;
        Console.WriteLine("Не понял, повторите.");
    }
}

static string Describe(SharpGen.Runtime.Result r) =>
    r.Success ? "OK" : $"FAILED (0x{r.Code:X8})";

/// <summary>
/// Меряет, с какой частотой база реально отдаёт новые значения оси. Значение меняется
/// только при движении руля, поэтому крутить его должен человек.
/// </summary>
static void MeasureAxisUpdateRate(IDirectInputDevice8 device)
{
    Console.WriteLine("\n--- Частота обновления оси ---");
    Console.WriteLine("Плавно покрутите руль туда-сюда 5 секунд. Enter — старт.");
    Console.ReadLine();

    var state = new JoystickState();
    var intervals = new List<double>(20000);
    long freq = Stopwatch.Frequency;
    long deadline = Stopwatch.GetTimestamp() + freq * 5;

    device.Poll();
    device.GetCurrentJoystickState(ref state);
    int lastValue = state.X;
    long lastChange = Stopwatch.GetTimestamp();
    int changes = 0;

    while (Stopwatch.GetTimestamp() < deadline)
    {
        device.Poll();
        device.GetCurrentJoystickState(ref state);
        if (state.X != lastValue)
        {
            long now = Stopwatch.GetTimestamp();
            intervals.Add((now - lastChange) * 1000.0 / freq);
            lastChange = now;
            lastValue = state.X;
            changes++;
        }
    }

    if (changes < 20)
    {
        Console.WriteLine($"  Зафиксировано всего {changes} изменений — руль почти не двигался, замер недостоверен.");
        return;
    }

    intervals.Sort();
    double median = intervals[intervals.Count / 2];
    Console.WriteLine($"  Изменений: {changes}");
    Console.WriteLine($"  Интервал между отсчётами: медиана {median:F3} мс, p5 {Percentile(intervals, 5):F3}, p95 {Percentile(intervals, 95):F3}");
    Console.WriteLine($"  → эффективная частота ≈ {1000.0 / median:F0} Гц");
    Console.WriteLine("  (ожидается ~1000 Гц; заметно меньше — USB polling базы ниже, это войдёт в задержку)");
}

/// <summary>
/// Стоимость самого вызова SetParameters. Если он дорогой или блокирующий,
/// это систематически войдёт в измеряемую задержку и потребует поправки.
/// </summary>
static void MeasureSetParametersCost(IDirectInputEffect effect, EffectParameters p)
{
    Console.WriteLine("\n--- Стоимость SetParameters ---");
    const int n = 1000;
    var costs = new List<double>(n);
    var force = new ConstantForce();
    long freq = Stopwatch.Frequency;

    for (int i = 0; i < n; i++)
    {
        // Малая амплитуда со сменой знака: руль почти не двигается, но команда настоящая.
        force.Magnitude = (i % 2 == 0) ? 400 : -400;
        p.Parameters = force;

        long t0 = Stopwatch.GetTimestamp();
        effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);
        long t1 = Stopwatch.GetTimestamp();

        costs.Add((t1 - t0) * 1000.0 / freq);
    }

    force.Magnitude = 0;
    p.Parameters = force;
    effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);

    costs.Sort();
    Console.WriteLine($"  медиана {costs[costs.Count / 2]:F4} мс, p95 {Percentile(costs, 95):F4} мс, max {costs[^1]:F4} мс");
    Console.WriteLine("  (если медиана заметно больше ~0.05 мс — вызов блокирующий, поправка обязательна)");
}

/// <summary>
/// Грубый step response: подать ступеньку и засечь, через сколько ось стронется.
/// Это ещё не измерительная методика из плана (без параболической экстраполяции),
/// а проверка того, что сигнал вообще виден и порядок величины разумный.
/// </summary>
static void MeasureStepResponse(IDirectInputDevice8 device, IDirectInputEffect effect, EffectParameters p)
{
    Console.WriteLine("\n--- Step response (грубо, 20 повторов) ---");
    const int repeats = 20;
    const int magnitude = 2500; // 25%
    long freq = Stopwatch.Frequency;

    var state = new JoystickState();
    var force = new ConstantForce();
    var results = new List<double>(repeats);

    for (int rep = 0; rep < repeats; rep++)
    {
        // Знак чередуем, иначе руль за несколько повторов уедет в упор.
        int mag = (rep % 2 == 0) ? magnitude : -magnitude;

        // Покой: оцениваем шум позиции.
        force.Magnitude = 0;
        p.Parameters = force;
        effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);
        Thread.Sleep(250);

        device.Poll();
        device.GetCurrentJoystickState(ref state);
        int baseline = state.X;

        int noise = 0;
        long noiseDeadline = Stopwatch.GetTimestamp() + freq / 10; // 100 мс
        while (Stopwatch.GetTimestamp() < noiseDeadline)
        {
            device.Poll();
            device.GetCurrentJoystickState(ref state);
            noise = Math.Max(noise, Math.Abs(state.X - baseline));
        }
        int threshold = Math.Max(noise * 3, 2);

        // Ступенька.
        force.Magnitude = mag;
        p.Parameters = force;

        long t0 = Stopwatch.GetTimestamp();
        effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);

        long timeout = t0 + freq / 5; // 200 мс
        long detected = 0;
        while (Stopwatch.GetTimestamp() < timeout)
        {
            device.Poll();
            device.GetCurrentJoystickState(ref state);
            if (Math.Abs(state.X - baseline) > threshold) { detected = Stopwatch.GetTimestamp(); break; }
        }

        force.Magnitude = 0;
        p.Parameters = force;
        effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);

        if (detected == 0)
        {
            Console.WriteLine($"  #{rep,2}: движение не обнаружено (порог {threshold}, шум {noise})");
            continue;
        }

        double ms = (detected - t0) * 1000.0 / freq;
        results.Add(ms);
        Console.WriteLine($"  #{rep,2}: {ms,6:F2} мс   (порог {threshold}, шум {noise})");
    }

    if (results.Count == 0)
    {
        Console.WriteLine("  Ни одного успешного замера. Проверьте, что руль свободен, а усилие в софте базы не в нуле.");
        return;
    }

    results.Sort();
    Console.WriteLine($"\n  Медиана: {results[results.Count / 2]:F2} мс, мин {results[0]:F2}, макс {results[^1]:F2}");
    Console.WriteLine($"  Разброс (p95-p5): {Percentile(results, 95) - Percentile(results, 5):F2} мс");
    Console.WriteLine("  Внимание: это сквозная задержка вместе с механикой, а не задержка электроники.");
}

/// <summary>
/// Главный вопрос этапа 0: можно ли читать raw HID, пока DirectInput держит
/// устройство в exclusive-режиме. Если да — появляется независимый второй источник
/// данных для кросс-проверки замеров.
/// </summary>
static void ProbeParallelHidRead(int vid, int pid, string? interfacePath)
{
    Console.WriteLine("\n--- Параллельное чтение raw HID ---");

    if (vid == 0 && pid == 0)
    {
        Console.WriteLine("  VID/PID неизвестны — пропускаем.");
        return;
    }

    var candidates = DeviceList.Local.GetHidDevices(vid, pid).ToList();
    Console.WriteLine($"  HID-устройств с VID/PID {vid:X4}:{pid:X4}: {candidates.Count}");

    foreach (var hid in candidates)
    {
        string path = SafePath(hid);
        Console.Write($"  → {path}: ");
        try
        {
            if (!hid.TryOpen(out var stream))
            {
                Console.WriteLine("открыть не удалось (занято exclusive-режимом DirectInput?)");
                continue;
            }

            using (stream)
            {
                stream.ReadTimeout = 1000;
                var buffer = new byte[hid.GetMaxInputReportLength()];

                int reports = 0;
                long freq = Stopwatch.Frequency;
                long deadline = Stopwatch.GetTimestamp() + freq; // 1 секунда
                long first = 0, last = 0;

                while (Stopwatch.GetTimestamp() < deadline)
                {
                    try
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0) continue;
                        last = Stopwatch.GetTimestamp();
                        if (first == 0) first = last;
                        reports++;
                    }
                    catch (TimeoutException) { break; }
                }

                if (reports >= 2)
                {
                    double seconds = (last - first) / (double)freq;
                    double rate = seconds > 0 ? (reports - 1) / seconds : 0;
                    Console.WriteLine($"ЧИТАЕТСЯ. Репортов {reports}, длина {buffer.Length} Б, ≈{rate:F0} Гц");
                }
                else
                {
                    Console.WriteLine($"открылось, но репортов почти нет ({reports}) — возможно, устройство молчит без движения руля");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ошибка — {ex.Message}");
        }
    }

    if (interfacePath is not null)
        Console.WriteLine($"  DirectInput interface path: {interfacePath}");
}

static string SafePath(HidDevice d)
{
    try { return d.DevicePath.Length > 60 ? d.DevicePath[..60] + "…" : d.DevicePath; }
    catch { return "<путь недоступен>"; }
}

static double Percentile(List<double> sorted, double p)
{
    if (sorted.Count == 0) return 0;
    double rank = p / 100.0 * (sorted.Count - 1);
    int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
    return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
}
