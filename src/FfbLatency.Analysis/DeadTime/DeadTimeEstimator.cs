using FfbLatency.Analysis.Signals;

namespace FfbLatency.Analysis.DeadTime;

/// <summary>
/// Оценка «мёртвого времени» — интервала от отправки FFB-команды до момента,
/// когда ось действительно тронулась.
///
/// Наивный способ (первый отсчёт выше порога шума) систематически завышает результат
/// и, что хуже, завышает его по-разному на разных базах: порог срабатывает тем позже,
/// чем грубее энкодер и чем тяжелее ротор. Сравнивать такие числа между базами нельзя.
///
/// Поэтому порог используется только чтобы найти участок движения, а сам момент старта
/// экстраполируется назад. Из покоя под постоянной силой движение равноускоренное:
///     θ(t) = θ₀ + ½a(t − t_start)²
/// Раскрыв скобки, получаем обычную параболу θ = c₀ + c₁t + c₂t², где скорость обращается
/// в ноль при t = −c₁/(2c₂). Это даёт t_start через линейный МНК, без итераций
/// и без зависимости от величины порога.
/// </summary>
public static class DeadTimeEstimator
{
    /// <summary>Во сколько сигм шума покоя должно уложиться движение, чтобы считаться начавшимся.</summary>
    public const double DefaultSigmaThreshold = 6.0;

    /// <summary>Сколько отсчётов подряд должны превысить порог — защита от одиночного выброса.</summary>
    public const int DefaultConfirmSamples = 3;

    /// <summary>Предельная длительность окна подгонки после точки детекта, мкс.</summary>
    /// <remarks>
    /// Верхняя граница, а не рабочая длина. Равноускоренная модель верна лишь в начале
    /// движения: дальше вмешиваются трение и демпфирование, и парабола перестаёт описывать
    /// реальность. Растягивать окно ради статистики нельзя.
    /// </remarks>
    public const long DefaultFitWindowUs = 40_000;

    /// <summary>
    /// Сколько различимых уровней энкодера должно попасть в окно подгонки.
    /// </summary>
    /// <remarks>
    /// Окно задаётся не временем, а числом уровней. При грубом кванте руль за фиксированные
    /// 15 мс проходит всего несколько ступенек, и парабола ложится по ним со смещением —
    /// ровно та ошибка, ради устранения которой экстраполяция и затевалась.
    /// </remarks>
    public const int DefaultMinFitLevels = 12;

    public static DeadTimeResult Estimate(
        StepTrace trace,
        double sigmaThreshold = DefaultSigmaThreshold,
        int confirmSamples = DefaultConfirmSamples,
        long fitWindowUs = DefaultFitWindowUs,
        int minFitLevels = DefaultMinFitLevels)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var samples = trace.Samples;
        if (samples.Count < 8)
            return DeadTimeResult.Failed("слишком мало отсчётов");

        // ── Шаг 1. Статистика покоя по отсчётам до команды ────────────────────
        int restCount = 0;
        double sum = 0, sumSq = 0;
        for (int i = 0; i < samples.Count && samples[i].TimeUs < trace.CommandTimeUs; i++)
        {
            sum += samples[i].Position;
            sumSq += (double)samples[i].Position * samples[i].Position;
            restCount++;
        }

        if (restCount < 4)
            return DeadTimeResult.Failed("недостаточно отсчётов покоя до команды");

        double mean = sum / restCount;
        double variance = Math.Max(0, sumSq / restCount - mean * mean);
        double sigma = Math.Sqrt(variance);

        // Энкодер квантован: при полностью неподвижном руле σ выходит нулевой.
        // Тогда порогом становится единица дискретности, иначе движение «обнаружится»
        // на первом же отсчёте и результат окажется бессмысленно малым.
        double threshold = Math.Max(sigmaThreshold * sigma, 1.0);

        // ── Шаг 2. Найти начало движения по порогу ────────────────────────────
        int detectIndex = -1;
        for (int i = restCount; i + confirmSamples - 1 < samples.Count; i++)
        {
            bool confirmed = true;
            for (int k = 0; k < confirmSamples; k++)
            {
                if (Math.Abs(samples[i + k].Position - mean) <= threshold) { confirmed = false; break; }
            }
            if (confirmed) { detectIndex = i; break; }
        }

        if (detectIndex < 0)
            return DeadTimeResult.Failed("движение не обнаружено");

        long thresholdDelayUs = samples[detectIndex].TimeUs - trace.CommandTimeUs;

        // ── Шаг 3. Подгонка параболы на окне движения ─────────────────────────
        // Окно начинается от точки детекта, но включает и несколько отсчётов до неё:
        // там уже есть реальное движение, ещё не пробившее порог, и оно уточняет параболу.
        // Отсчёты, ещё лежащие на исходном уровне энкодера, в подгонку не берём.
        // Они несут не значение, а неравенство «смещение меньше половины кванта»:
        // формально это цензурированные данные. Подставленные как нули, они тянут
        // параболу вниз и уводят вершину назад по времени — при грубом энкодере
        // ошибка доходила до 2 мс, что сопоставимо с самой измеряемой величиной.
        double quantum = EstimateQuantum(samples, restCount);
        int fitStart = restCount;
        while (fitStart < detectIndex && Math.Abs(samples[fitStart].Position - mean) < quantum) fitStart++;

        long fitEndTime = samples[detectIndex].TimeUs + fitWindowUs;

        // Окно растёт, пока не наберётся нужное число различимых уровней энкодера,
        // но не дольше предельного времени: за ним равноускоренная модель уже неверна.
        int fitEnd = fitStart;
        var levels = new HashSet<int>();
        while (fitEnd + 1 < samples.Count && samples[fitEnd + 1].TimeUs <= fitEndTime)
        {
            fitEnd++;
            levels.Add(samples[fitEnd].Position);
            if (levels.Count >= minFitLevels && fitEnd - fitStart + 1 >= 8) break;
        }

        int n = fitEnd - fitStart + 1;
        if (n < 5)
            return DeadTimeResult.Partial(thresholdDelayUs, sigma, "окно подгонки слишком короткое");

        // Время центрируем относительно точки детекта: без этого t² даёт огромные
        // числа (QPC в микросекундах), и нормальные уравнения теряют точность.
        double tRef = samples[detectIndex].TimeUs;
        var fit = QuadraticFit.Fit(samples, fitStart, fitEnd, tRef, mean);
        if (!fit.Ok)
            return DeadTimeResult.Partial(thresholdDelayUs, sigma, "парабола не подогналась");

        // ── Шаг 4. Валидация ──────────────────────────────────────────────────
        // Ускорение обязано быть направлено в сторону приложенной силы. Если знак
        // не тот, мы поймали отдачу или дрейф, а не отклик на команду.
        double expectedSign = Math.Sign(trace.Magnitude);
        if (expectedSign != 0 && Math.Sign(fit.C2) != expectedSign)
            return DeadTimeResult.Partial(thresholdDelayUs, sigma, "знак ускорения не совпал с направлением силы");

        if (Math.Abs(fit.C2) < 1e-12)
            return DeadTimeResult.Partial(thresholdDelayUs, sigma, "ускорение неотличимо от нуля");

        double tStartRelative = -fit.C1 / (2 * fit.C2);      // мкс относительно tRef
        double tStartAbsolute = tRef + tStartRelative;
        double extrapolatedUs = tStartAbsolute - trace.CommandTimeUs;

        // Экстраполяция назад дальше начала окна физически бессмысленна, а отрицательная
        // задержка означала бы движение до команды. И то, и другое — признак плохой подгонки.
        if (extrapolatedUs < 0 || extrapolatedUs > thresholdDelayUs)
            return DeadTimeResult.Partial(thresholdDelayUs, sigma,
                $"экстраполяция вне допустимого диапазона ({extrapolatedUs / 1000.0:F2} мс)");

        return new DeadTimeResult
        {
            Ok = true,
            DeadTimeUs = extrapolatedUs,
            ThresholdDelayUs = thresholdDelayUs,
            NoiseSigma = sigma,
            Acceleration = 2 * fit.C2,
            FitR2 = fit.R2,
        };
    }

    /// <summary>
    /// Оценивает шаг квантования энкодера как наименьшую ненулевую разницу между
    /// соседними отсчётами. Разрешение оси зависит от базы и от заданного диапазона,
    /// поэтому берём его из самих данных, а не из константы.
    /// </summary>
    private static double EstimateQuantum(IReadOnlyList<Sample> samples, int from)
    {
        int smallest = int.MaxValue;
        for (int i = Math.Max(1, from); i < samples.Count; i++)
        {
            int delta = Math.Abs(samples[i].Position - samples[i - 1].Position);
            if (delta > 0 && delta < smallest) smallest = delta;
        }
        return smallest == int.MaxValue ? 1.0 : smallest;
    }
}
