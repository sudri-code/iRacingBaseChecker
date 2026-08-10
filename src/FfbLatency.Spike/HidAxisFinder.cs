using System.Diagnostics;
using HidSharp;
using Vortice.DirectInput;

namespace FfbLatency.Spike;

/// <summary>
/// Находит, в каком месте HID-репорта лежит положение руля.
///
/// Зачем это нужно: HID отдаёт репорты сам, примерно тысячу раз в секунду, и момент
/// прихода репорта — это честная метка времени. Опрос DirectInput даёт лишь момент,
/// когда мы удосужились спросить, то есть добавляет к задержке собственный джиттер.
/// Чтобы перейти на HID как основной источник, надо знать формат репорта, а он
/// не документирован — поэтому определяем его эмпирически, по корреляции с DirectInput.
/// </summary>
internal static class HidAxisFinder
{
    private readonly record struct HidSample(long TimeUs, int Value);

    /// <summary>Один разобранный вариант укладки поля в репорте.</summary>
    internal sealed record Candidate(int Offset, string Format, double Correlation, int Min, int Max)
    {
        public override string ToString() =>
            $"смещение {Offset,2}, {Format,-5}: корреляция {Correlation,6:F3}, диапазон {Min}..{Max}";
    }

    public static void Probe(HidDevice hid, IDirectInputDevice8 device, AxisDef axis, int durationSeconds = 5)
    {
        Console.WriteLine("\n--- Поиск положения руля в HID-репорте ---");
        Console.WriteLine($"Снова покрутите руль влево-вправо. Enter — старт ({durationSeconds} секунд).");
        Console.ReadLine();

        if (!hid.TryOpen(out var stream))
        {
            Console.WriteLine("  Не удалось открыть HID-поток — пропускаем.");
            return;
        }

        int reportLength = hid.GetMaxInputReportLength();
        var reports = new List<(long TimeUs, byte[] Data)>(8000);
        var direct = new List<HidSample>(20000);

        long freq = Stopwatch.Frequency;
        long start = Stopwatch.GetTimestamp();
        long deadline = start + freq * durationSeconds;
        bool running = true;

        var reader = new Thread(() =>
        {
            var buffer = new byte[reportLength];
            try
            {
                stream.ReadTimeout = 500;
                while (Volatile.Read(ref running))
                {
                    int read;
                    try { read = stream.Read(buffer, 0, buffer.Length); }
                    catch (TimeoutException) { continue; }
                    if (read <= 0) continue;

                    long t = (Stopwatch.GetTimestamp() - start) * 1_000_000 / freq;
                    reports.Add((t, buffer[..read]));
                }
            }
            catch { /* поток завершается вместе с замером */ }
        })
        { IsBackground = true, Priority = ThreadPriority.AboveNormal };

        reader.Start();

        var state = new JoystickState();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            device.Poll();
            device.GetCurrentJoystickState(ref state);
            long t = (Stopwatch.GetTimestamp() - start) * 1_000_000 / freq;
            direct.Add(new HidSample(t, Axes.Read(state, axis.Index)));
        }

        Volatile.Write(ref running, false);
        reader.Join(TimeSpan.FromSeconds(2));
        stream.Dispose();

        Console.WriteLine($"  Собрано: {reports.Count} HID-репортов, {direct.Count} отсчётов DirectInput");

        if (reports.Count < 100 || direct.Count < 100)
        {
            Console.WriteLine("  Данных мало — определить формат нельзя.");
            return;
        }

        int actualLength = reports.Min(r => r.Data.Length);
        var candidates = new List<Candidate>();

        foreach (int offset in Enumerable.Range(0, Math.Max(0, actualLength - 1)))
        {
            foreach (var (name, decode) in Decoders)
            {
                var values = new double[reports.Count];
                var reference = new double[reports.Count];
                int min = int.MaxValue, max = int.MinValue;

                for (int i = 0; i < reports.Count; i++)
                {
                    int v = decode(reports[i].Data, offset);
                    values[i] = v;
                    if (v < min) min = v;
                    if (v > max) max = v;
                    reference[i] = NearestValue(direct, reports[i].TimeUs);
                }

                // Постоянное поле ничего не скажет о положении руля.
                if (max - min < 1000) continue;

                double r = Correlation(values, reference);
                if (double.IsFinite(r)) candidates.Add(new Candidate(offset, name, r, min, max));
            }
        }

        if (candidates.Count == 0)
        {
            Console.WriteLine("  Ни одно поле не менялось достаточно — руль крутили слишком мало?");
            return;
        }

        Console.WriteLine("  Наиболее похожие поля (по модулю корреляции):");
        foreach (var c in candidates.OrderByDescending(c => Math.Abs(c.Correlation)).Take(5))
            Console.WriteLine($"    {c}");

        var best = candidates.OrderByDescending(c => Math.Abs(c.Correlation)).First();
        if (Math.Abs(best.Correlation) > 0.99)
            Console.WriteLine($"  → положение руля лежит по смещению {best.Offset} как {best.Format}");
        else
            Console.WriteLine("  → уверенного совпадения нет; возможно, репорт упакован по битам, а не по байтам");

        ReportRate(reports);
    }

    private static void ReportRate(List<(long TimeUs, byte[] Data)> reports)
    {
        var gaps = new List<double>(reports.Count);
        for (int i = 1; i < reports.Count; i++)
            gaps.Add((reports[i].TimeUs - reports[i - 1].TimeUs) / 1000.0);

        if (gaps.Count == 0) return;
        gaps.Sort();
        Console.WriteLine($"  Интервал между HID-репортами: медиана {gaps[gaps.Count / 2]:F3} мс, " +
                          $"p95 {gaps[(int)(gaps.Count * 0.95)]:F3} мс");
    }

    private static readonly (string Name, Func<byte[], int, int> Decode)[] Decoders =
    [
        ("u16le", (d, o) => o + 1 < d.Length ? d[o] | (d[o + 1] << 8) : 0),
        ("u16be", (d, o) => o + 1 < d.Length ? (d[o] << 8) | d[o + 1] : 0),
        ("s16le", (d, o) => o + 1 < d.Length ? (short)(d[o] | (d[o + 1] << 8)) : 0),
        ("s16be", (d, o) => o + 1 < d.Length ? (short)((d[o] << 8) | d[o + 1]) : 0),
    ];

    /// <summary>Значение DirectInput, ближайшее по времени к моменту прихода репорта.</summary>
    private static double NearestValue(List<HidSample> sorted, long timeUs)
    {
        int lo = 0, hi = sorted.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid].TimeUs < timeUs) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && Math.Abs(sorted[lo - 1].TimeUs - timeUs) < Math.Abs(sorted[lo].TimeUs - timeUs)) lo--;
        return sorted[lo].Value;
    }

    private static double Correlation(double[] a, double[] b)
    {
        int n = a.Length;
        double meanA = a.Average(), meanB = b.Average();
        double cov = 0, varA = 0, varB = 0;

        for (int i = 0; i < n; i++)
        {
            double da = a[i] - meanA, db = b[i] - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }

        double denom = Math.Sqrt(varA * varB);
        return denom > 0 ? cov / denom : double.NaN;
    }
}
