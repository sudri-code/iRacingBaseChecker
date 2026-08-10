using System.Diagnostics;
using FfbLatency.Spike;
using HidSharp;
using Vortice.DirectInput;

// Spike для этапа 0: снять главный технический риск проекта до того, как строить
// архитектуру и UI. Проверяем на живом железе:
//   1. DirectInput даёт Exclusive|Background и позволяет Acquire.
//   2. Какая ось на самом деле рулевая и с какой частотой она обновляется.
//   3. Позиция оси читается ПАРАЛЛЕЛЬНО с удержанием exclusive-режима.
//   4. Сколько стоит вызов SetParameters (он войдёт в измеряемую задержку).
//   5. Виден ли step response — то есть жизнеспособна ли методика.

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
HiddenWindow? window = null;
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
        Console.WriteLine($"  [{i}] {instances[i].ProductName}  (instance: {instances[i].InstanceName})");

    var chosen = instances[AskIndex(instances.Count)];
    Console.WriteLine($"\nВыбрано: {chosen.ProductName}");

    device = directInput.CreateDevice(chosen.InstanceGuid);
    device.SetDataFormat<RawJoystickState>();

    // Окно обязано принадлежать нашему процессу — см. комментарий в HiddenWindow.
    window = new HiddenWindow();
    Console.WriteLine($"Создано собственное скрытое окно: 0x{window.Handle:X}");

    var coop = device.SetCooperativeLevel(window.Handle, CooperativeLevel.Exclusive | CooperativeLevel.Background);
    Console.WriteLine($"SetCooperativeLevel(Exclusive|Background): {Describe(coop)}");
    if (coop.Failure) return 1;

    // ── Шаг 2. Свойства устройства ────────────────────────────────────────────
    var caps = device.Capabilities;
    Console.WriteLine("\n--- Возможности устройства ---");
    Console.WriteLine($"  Оси: {caps.AxeCount}, кнопки: {caps.ButtonCount}");
    Console.WriteLine($"  ForceFeedback: {caps.Flags.HasFlag(DeviceFlags.ForceFeedback)}");
    Console.WriteLine($"  FFB sample period: {caps.ForceFeedbackSamplePeriod} мкс, " +
                      $"min time resolution: {caps.ForceFeedbackMinimumTimeResolution} мкс");
    if (caps.ForceFeedbackSamplePeriod >= 1_000_000)
        Console.WriteLine("    (1 с — это значение-заглушка драйвера, а не характеристика базы)");

    if (!caps.Flags.HasFlag(DeviceFlags.ForceFeedback))
    {
        Console.WriteLine("\nУ устройства нет force feedback. Выбрана не та железка?");
        return 1;
    }

    var props = device.Properties;
    int vid = 0, pid = 0;
    string? interfacePath = null;

    TryRead("VID/PID", () => { vid = props.VendorId; pid = props.ProductId; return $"{vid:X4}:{pid:X4}"; });
    TryRead("Interface path", () => { interfacePath = props.InterfacePath; return interfacePath ?? "?"; });
    TryRead("Диапазон оси", () => { var r = props.Range; return $"{r.Minimum}..{r.Maximum}"; });

    // Автоцентр — это пружина, она исказит step response. Обязательно выключить.
    try
    {
        props.AutoCenter = false;
        autoCenterChanged = true;
        Console.WriteLine("  AutoCenter: выключен");
    }
    catch (Exception ex) { Console.WriteLine($"  AutoCenter выключить не удалось: {Brief(ex)}"); }

    try { props.ForceFeedbackGain = 10000; } catch { /* не критично */ }

    var acq = device.Acquire();
    Console.WriteLine($"Acquire: {Describe(acq)}");
    if (acq.Failure)
    {
        Console.WriteLine("  → Устройство не захвачено. Закройте Pit House / SimPro Manager / SimHub / iRacing.");
        return 1;
    }

    // ── Шаг 3. Какая ось рулевая и как часто обновляется ──────────────────────
    var axis = DetectSteeringAxis(device);
    if (axis is null)
    {
        Console.WriteLine("Рулевую ось определить не удалось — руль почти не двигался.");
        return 1;
    }

    // ── Шаг 4. Создание эффекта ───────────────────────────────────────────────
    var effectParams = new EffectParameters
    {
        Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
        Duration = unchecked((int)0xFFFFFFFF), // INFINITE
        SamplePeriod = 0,
        Gain = 10000,
        TriggerButton = -1,
        TriggerRepeatInterval = unchecked((int)0xFFFFFFFF),
        StartDelay = 0,
        Parameters = new ConstantForce { Magnitude = 0 },
    };
    effectParams.SetAxes(new[] { (int)axis.Offset }, new[] { 0 });

    effect = device.CreateEffect(EffectGuid.ConstantForce, effectParams);
    Console.WriteLine($"\nConstantForce эффект создан на оси {axis.Name}.");
    Console.WriteLine($"Effect.Start: {Describe(effect.Start(1, EffectPlayFlags.None))}");

    // ── Шаг 5. Полярность оси ─────────────────────────────────────────────────
    Console.WriteLine("\nСейчас будет короткий импульс малым усилием — руль слегка качнётся.");
    Console.WriteLine("Освободите руль. Enter — продолжить.");
    Console.ReadLine();

    int polarity = DetectPolarity(device, effect, effectParams, axis);
    if (polarity == 0)
    {
        Console.WriteLine("Полярность определить не удалось — дальше идти нельзя, замеры будут неверными.");
        return 1;
    }

    // ── Шаг 6. Стоимость SetParameters ────────────────────────────────────────
    MeasureSetParametersCost(effect, effectParams);

    // ── Шаг 7. Step response ──────────────────────────────────────────────────
    Console.WriteLine("\nСейчас будет подаваться усилие ~25% — руль дёрнется.");
    Console.WriteLine("Уберите руки, освободите руль. Enter — продолжить, любая другая клавиша — пропустить.");
    if (Console.ReadKey(true).Key == ConsoleKey.Enter)
        MeasureStepResponse(device, effect, effectParams, axis, polarity);
    else
        Console.WriteLine("Step response пропущен.");

    // ── Шаг 8. Параллельное чтение raw HID ────────────────────────────────────
    var hidDevice = ProbeParallelHidRead(vid, pid, interfacePath);

    // ── Шаг 9. Формат HID-репорта ─────────────────────────────────────────────
    // Если HID читается, он предпочтительнее опроса DirectInput: репорт приходит сам,
    // и время его получения — честная метка, без джиттера нашего цикла опроса.
    if (hidDevice is not null)
        HidAxisFinder.Probe(hidDevice, device, axis);

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
    window?.Dispose();
    Native.TimeEndPeriod(1);
}

// ─────────────────────────────────────────────────────────────────────────────

static int AskIndex(int count)
{
    while (true)
    {
        Console.Write($"\nНомер устройства [0..{count - 1}]: ");
        if (int.TryParse(Console.ReadLine(), out int i) && i >= 0 && i < count) return i;
        Console.WriteLine("Не понял, повторите.");
    }
}

static void TryRead(string label, Func<string> read)
{
    try { Console.WriteLine($"  {label}: {read()}"); }
    catch (Exception ex) { Console.WriteLine($"  {label}: недоступно ({Brief(ex)})"); }
}

static string Brief(Exception ex)
{
    var line = ex.Message.Split('\n')[0].Trim();
    return line.Length > 90 ? line[..90] + "…" : line;
}

static string Describe(SharpGen.Runtime.Result r)
{
    if (r.Success) return "OK";
    string hint = unchecked((uint)r.Code) switch
    {
        0x80070578 => " — ERROR_INVALID_WINDOW_HANDLE: окно не принадлежит процессу",
        0x80070005 => " — доступ запрещён: устройство занято другим приложением",
        0x8007001E => " — DIERR_INPUTLOST: устройство потеряно",
        0x80070015 => " — DIERR_NOTINITIALIZED",
        0x80070057 => " — DIERR_INVALIDPARAM: неверный параметр",
        _ => "",
    };
    return $"FAILED (0x{r.Code:X8}){hint}";
}

/// <summary>
/// Определяет рулевую ось по фактическому размаху при вращении и заодно меряет,
/// с какой частотой база отдаёт новые значения.
/// </summary>
/// <remarks>
/// У базы восемь осей, и считать рулевой именно X — предположение, которое дёшево
/// проверить и дорого не заметить: эффект, привязанный не к той оси, просто не даст отклика.
/// </remarks>
static AxisDef? DetectSteeringAxis(IDirectInputDevice8 device)
{
    var all = Axes.All;

    Console.WriteLine("\n--- Рулевая ось и частота обновления ---");
    Console.WriteLine("Плавно покрутите руль влево-вправо примерно на пол-оборота. Enter — старт (5 секунд).");
    Console.ReadLine();

    var min = new int[all.Length];
    var max = new int[all.Length];
    var state = new JoystickState();
    long freq = Stopwatch.Frequency;

    device.Poll();
    device.GetCurrentJoystickState(ref state);
    for (int a = 0; a < all.Length; a++) min[a] = max[a] = Axes.Read(state, a);

    var intervals = new List<double>(30000);
    int previous = Axes.Read(state, 0);
    long lastChange = Stopwatch.GetTimestamp();
    long deadline = lastChange + freq * 5;
    int changes = 0;
    int steeringGuess = 0;

    while (Stopwatch.GetTimestamp() < deadline)
    {
        device.Poll();
        device.GetCurrentJoystickState(ref state);

        for (int a = 0; a < all.Length; a++)
        {
            int v = Axes.Read(state, a);
            if (v < min[a]) min[a] = v;
            if (v > max[a]) max[a] = v;
        }

        // Частоту меряем по оси с наибольшим размахом на текущий момент.
        int best = 0;
        for (int a = 1; a < all.Length; a++)
            if (max[a] - min[a] > max[best] - min[best]) best = a;
        steeringGuess = best;

        int current = Axes.Read(state, best);
        if (current != previous)
        {
            long now = Stopwatch.GetTimestamp();
            intervals.Add((now - lastChange) * 1000.0 / freq);
            lastChange = now;
            previous = current;
            changes++;
        }
    }

    Console.WriteLine("  Размах по осям:");
    for (int a = 0; a < all.Length; a++)
    {
        int span = max[a] - min[a];
        if (span > 0)
            Console.WriteLine($"    {all[a].Name,-10} {min[a],7}..{max[a],-7} размах {span}");
    }

    if (max[steeringGuess] - min[steeringGuess] < 100 || changes < 20)
    {
        Console.WriteLine($"  Движения почти не было (изменений {changes}) — определить ось нельзя.");
        return null;
    }

    var axis = all[steeringGuess];
    Console.WriteLine($"  → рулевая ось: {axis.Name}, наблюдаемый диапазон {min[steeringGuess]}..{max[steeringGuess]}");

    intervals.Sort();
    double median = intervals[intervals.Count / 2];
    Console.WriteLine($"  Изменений: {changes}, интервал между отсчётами: медиана {median:F3} мс, " +
                      $"p5 {Percentile(intervals, 5):F3}, p95 {Percentile(intervals, 95):F3}");
    Console.WriteLine($"  → эффективная частота ≈ {1000.0 / median:F0} Гц (ожидается ~1000)");

    return axis;
}

/// <summary>
/// Определяет, в какую сторону положительное усилие двигает ось.
/// </summary>
/// <remarks>
/// Связь знака силы с направлением координаты зависит от базы: у Moza R21 ось
/// инвертирована. Предполагать её нельзя — на неверной полярности успокоитель
/// работает как положительная обратная связь и загоняет руль в край диапазона,
/// а признак «ускорение направлено по силе» отбраковывает все замеры подряд.
/// </remarks>
/// <returns>+1, −1 или 0, если определить не удалось.</returns>
static int DetectPolarity(IDirectInputDevice8 device, IDirectInputEffect effect, EffectParameters p, AxisDef axis)
{
    Console.WriteLine("\n--- Полярность оси ---");

    const int probeForce = 2200;
    const int probeMs = 120;
    long freq = Stopwatch.Frequency;

    var state = new JoystickState();
    var force = new ConstantForce();

    device.Poll();
    device.GetCurrentJoystickState(ref state);
    int baseline = Axes.Read(state, axis.Index);

    int travel = Pulse(probeForce);

    // Компенсирующий импульс той же длительности возвращает руль примерно на место,
    // чтобы проба не сдвигала точку старта следующих тестов.
    Pulse(-probeForce);

    Console.WriteLine($"  Усилие +{probeForce} сместило ось на {travel} отсчётов (от {baseline})");

    if (Math.Abs(travel) < 50)
    {
        Console.WriteLine("  Смещение слишком мало. Руль зажат, или усилие в софте базы выкручено в ноль.");
        return 0;
    }

    int polarity = Math.Sign(travel);
    Console.WriteLine(polarity > 0
        ? "  → полярность +1: положительное усилие увеличивает координату"
        : "  → полярность −1: положительное усилие уменьшает координату (ось инвертирована)");

    return polarity;

    int Pulse(int magnitude)
    {
        device.Poll();
        device.GetCurrentJoystickState(ref state);
        int from = Axes.Read(state, axis.Index);

        force.Magnitude = magnitude;
        p.Parameters = force;
        effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);

        long until = Stopwatch.GetTimestamp() + freq * probeMs / 1000;
        int furthest = 0;
        while (Stopwatch.GetTimestamp() < until)
        {
            device.Poll();
            device.GetCurrentJoystickState(ref state);
            int delta = Axes.Read(state, axis.Index) - from;
            if (Math.Abs(delta) > Math.Abs(furthest)) furthest = delta;
        }

        force.Magnitude = 0;
        p.Parameters = force;
        effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);

        // Даём инерции угаснуть, прежде чем мерить дальше.
        Thread.Sleep(400);
        return furthest;
    }
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
    double median = costs[costs.Count / 2];
    Console.WriteLine($"  медиана {median:F4} мс, p95 {Percentile(costs, 95):F4} мс, max {costs[^1]:F4} мс");

    if (median > 0.2)
    {
        // Если вызов блокирующий, важно понять, чем он занят. Кратность миллисекунде
        // означала бы ожидание USB-фреймов: команда уходит в следующем фрейме, и тогда
        // момент реальной отправки известен с точностью до кванта, а не «где-то внутри вызова».
        Console.WriteLine("  Распределение (бины по 0.25 мс):");
        var histogram = new SortedDictionary<int, int>();
        foreach (double c in costs)
        {
            int bin = (int)(c / 0.25);
            histogram[bin] = histogram.GetValueOrDefault(bin) + 1;
        }

        foreach (var (bin, count) in histogram)
        {
            if (count * 100 / costs.Count < 1) continue;
            string bar = new('#', Math.Max(1, count * 40 / costs.Count));
            Console.WriteLine($"    {bin * 0.25,5:F2}–{(bin + 1) * 0.25,-5:F2} мс {count,5}  {bar}");
        }

        Console.WriteLine("  Вызов блокирующий: реальный момент отправки лежит внутри этого интервала,");
        Console.WriteLine("  что даёт систематическую неопределённость того же порядка. Учесть при сравнении баз.");
    }
}

/// <summary>
/// Грубый step response: подать ступеньку и засечь, через сколько ось стронется.
/// Это ещё не измерительная методика (без параболической экстраполяции),
/// а проверка того, что сигнал виден и порядок величины разумный.
/// </summary>
static void MeasureStepResponse(IDirectInputDevice8 device, IDirectInputEffect effect, EffectParameters p, AxisDef axis, int polarity)
{
    Console.WriteLine("\n--- Step response (грубо, 20 повторов) ---");
    Console.WriteLine("Выставьте руль примерно в центр и отпустите — нужен запас хода в обе стороны.");
    Console.WriteLine("Enter — начать.");
    Console.ReadLine();

    const int repeats = 20;
    const int magnitude = 2500;   // 25%
    const int holdMs = 60;        // ступенька снимается сразу после детекта, это лишь потолок
    long freq = Stopwatch.Frequency;

    var state = new JoystickState();
    var force = new ConstantForce();
    var results = new List<double>(repeats);

    device.Poll();
    device.GetCurrentJoystickState(ref state);
    int home = Axes.Read(state, axis.Index);
    Console.WriteLine($"  Исходная позиция: {home}");

    int stuckInARow = 0;

    for (int rep = 0; rep < repeats; rep++)
    {
        // Знак чередуем, иначе руль за несколько повторов уедет в упор.
        int mag = (rep % 2 == 0) ? magnitude : -magnitude;

        // Активно гасим движение от прошлого повтора: пассивной паузы недостаточно,
        // автоцентр выключен и руль продолжает вращаться по инерции.
        var settle = WheelSettler.Settle(device, effect, p, axis, home, polarity);

        device.Poll();
        device.GetCurrentJoystickState(ref state);
        int where = Axes.Read(state, axis.Index);

        if (settle == SettleOutcome.Stuck)
        {
            Console.WriteLine($"  #{rep,2}: руль застрял на {where} (цель {home}, отклонение {where - home}) — похоже на упор.");

            if (++stuckInARow >= 3)
            {
                Console.WriteLine("  Три раза подряд — прерываю серию. Верните руль в центр вручную и запустите снова.");
                break;
            }
            continue;
        }

        if (settle == SettleOutcome.Timeout)
        {
            // Мерить на движущемся руле бессмысленно: «покой» окажется движением,
            // порог детектирования уедет вверх и отклик просто не будет виден.
            Console.WriteLine($"  #{rep,2}: руль не успокоился за отведённое время (позиция {where}) — повтор пропущен.");
            continue;
        }
        stuckInARow = 0;

        device.Poll();
        device.GetCurrentJoystickState(ref state);
        int baseline = Axes.Read(state, axis.Index);

        int noise = 0;
        long noiseDeadline = Stopwatch.GetTimestamp() + freq / 20; // 50 мс
        while (Stopwatch.GetTimestamp() < noiseDeadline)
        {
            device.Poll();
            device.GetCurrentJoystickState(ref state);
            noise = Math.Max(noise, Math.Abs(Axes.Read(state, axis.Index) - baseline));
        }
        int threshold = Math.Max(noise * 3, 2);

        force.Magnitude = mag;
        p.Parameters = force;

        long t0 = Stopwatch.GetTimestamp();
        effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);

        long timeout = t0 + freq * holdMs / 1000;
        long detected = 0;
        int travel = 0;

        while (Stopwatch.GetTimestamp() < timeout)
        {
            device.Poll();
            device.GetCurrentJoystickState(ref state);
            int delta = Axes.Read(state, axis.Index) - baseline;
            if (Math.Abs(delta) > Math.Abs(travel)) travel = delta;
            if (detected == 0 && Math.Abs(delta) > threshold) { detected = Stopwatch.GetTimestamp(); break; }
        }

        // Ступеньку снимаем немедленно: чем дольше она держится, тем сильнее руль
        // разгоняется и тем дальше уезжает от центра к следующему повтору.
        force.Magnitude = 0;
        p.Parameters = force;
        effect.SetParameters(p, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);

        if (detected == 0)
        {
            // Смещение за время удержания отличает упор от отсутствия отклика:
            // при упоре руль не сдвинется вовсе, при слабом усилии — сдвинется, но мало.
            Console.WriteLine($"  #{rep,2}: движения нет. Усилие {mag,6}, смещение за {holdMs} мс: {travel,6}, " +
                              $"позиция {baseline}, порог {threshold}");
            continue;
        }

        double ms = (detected - t0) * 1000.0 / freq;

        // Ось обязана двинуться туда, куда толкает сила с учётом полярности.
        // Обратное направление означает, что мы поймали не отклик, а остаточное движение.
        int expected = Math.Sign(mag) * polarity;
        if (Math.Sign(travel) != expected)
        {
            Console.WriteLine($"  #{rep,2}: движение против ожидаемого направления (смещение {travel}) — замер отброшен.");
            continue;
        }

        results.Add(ms);
        Console.WriteLine($"  #{rep,2}: {ms,6:F2} мс   усилие {mag,6}, позиция {baseline,6}, порог {threshold}, шум {noise}");
    }

    if (results.Count == 0)
    {
        Console.WriteLine("  Ни одного успешного замера. Проверьте, что руль свободен и усилие в софте базы не в нуле.");
        return;
    }

    results.Sort();
    Console.WriteLine($"\n  Успешных замеров: {results.Count} из {repeats}");
    Console.WriteLine($"  Медиана: {results[results.Count / 2]:F2} мс, мин {results[0]:F2}, макс {results[^1]:F2}");
    Console.WriteLine($"  Разброс (p95-p5): {Percentile(results, 95) - Percentile(results, 5):F2} мс");
    Console.WriteLine($"  Шум покоя должен быть близок к нулю: большой шум означает, что руль ещё двигался.");
    Console.WriteLine("  Внимание: это сквозная задержка вместе с механикой, а не задержка электроники.");
    Console.WriteLine("  Порог завышает результат на несколько мс; итоговый инструмент считает экстраполяцией.");
}

/// <summary>
/// Главный вопрос этапа 0: можно ли читать raw HID, пока DirectInput держит
/// устройство в exclusive-режиме. Если да — появляется независимый второй источник
/// данных для кросс-проверки замеров.
/// </summary>
static HidDevice? ProbeParallelHidRead(int vid, int pid, string? interfacePath)
{
    Console.WriteLine("\n--- Параллельное чтение raw HID ---");

    if (vid == 0)
    {
        Console.WriteLine("  VID неизвестен — пропускаем.");
        return null;
    }

    HidDevice? readable = null;

    // Некоторые базы сообщают PID как 0000, поэтому фильтруем только по VID.
    var candidates = DeviceList.Local.GetHidDevices(vendorID: vid).ToList();
    Console.WriteLine($"  HID-устройств с VID {vid:X4}: {candidates.Count}" + (pid == 0 ? " (PID не сообщён, фильтр только по VID)" : ""));

    foreach (var hid in candidates)
    {
        string path = SafePath(hid);
        bool matchesDirectInput = interfacePath is not null &&
            string.Equals(TryDevicePath(hid), interfacePath, StringComparison.OrdinalIgnoreCase);

        Console.Write($"  → {path}{(matchesDirectInput ? "  [то же устройство, что в DirectInput]" : "")}: ");
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
                long deadline = Stopwatch.GetTimestamp() + freq;
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
                    readable ??= hid;
                }
                else
                {
                    Console.WriteLine($"открылось, но репортов почти нет ({reports}) — устройство молчит без движения руля");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ошибка — {Brief(ex)}");
        }
    }

    if (interfacePath is not null)
        Console.WriteLine($"  DirectInput interface path: {interfacePath}");

    return readable;
}

static string TryDevicePath(HidDevice d)
{
    try { return d.DevicePath; } catch { return ""; }
}

static string SafePath(HidDevice d)
{
    string p = TryDevicePath(d);
    if (p.Length == 0) return "<путь недоступен>";
    return p.Length > 70 ? p[..70] + "…" : p;
}

static double Percentile(List<double> sorted, double p)
{
    if (sorted.Count == 0) return 0;
    double rank = p / 100.0 * (sorted.Count - 1);
    int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
    return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
}
