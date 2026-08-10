using FfbLatency.Analysis.Signals;

namespace FfbLatency.Analysis.DeadTime;

/// <summary>
/// Метод наименьших квадратов для параболы y = c₀ + c₁·t + c₂·t².
/// </summary>
public readonly record struct QuadraticFit(bool Ok, double C0, double C1, double C2, double R2)
{
    /// <summary>
    /// Подгоняет параболу по отсчётам [from..to] включительно.
    /// </summary>
    /// <param name="tRefUs">Время, относительно которого центрируются отсчёты.</param>
    /// <param name="yOffset">Уровень, вычитаемый из позиции (обычно среднее покоя).</param>
    /// <returns>Коэффициенты в единицах «отсчёты» и «микросекунды».</returns>
    public static QuadraticFit Fit(IReadOnlyList<Sample> samples, int from, int to, double tRefUs, double yOffset)
    {
        int n = to - from + 1;
        if (n < 3) return default;

        // Внутри считаем время в миллисекундах. В микросекундах Σt⁴ достигает ~10¹⁶
        // и нормальные уравнения теряют значащие разряды.
        double s0 = n, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
        double t0 = 0, t1 = 0, t2 = 0;

        for (int i = from; i <= to; i++)
        {
            double x = (samples[i].TimeUs - tRefUs) / 1000.0;
            double y = samples[i].Position - yOffset;

            double x2 = x * x;
            s1 += x;
            s2 += x2;
            s3 += x2 * x;
            s4 += x2 * x2;

            t0 += y;
            t1 += x * y;
            t2 += x2 * y;
        }

        Span<double> m = stackalloc double[12]
        {
            s0, s1, s2, t0,
            s1, s2, s3, t1,
            s2, s3, s4, t2,
        };

        if (!SolveGauss3(m, out double a0, out double a1, out double a2))
            return default;

        // Коэффициент детерминации — на тех же центрированных данных.
        double meanY = t0 / n;
        double ssTot = 0, ssRes = 0;
        for (int i = from; i <= to; i++)
        {
            double x = (samples[i].TimeUs - tRefUs) / 1000.0;
            double y = samples[i].Position - yOffset;
            double pred = a0 + a1 * x + a2 * x * x;
            ssRes += (y - pred) * (y - pred);
            ssTot += (y - meanY) * (y - meanY);
        }
        double r2 = ssTot > 0 ? 1 - ssRes / ssTot : 0;

        // Возврат к микросекундам: x_ms = x_us / 1000.
        return new QuadraticFit(true, a0, a1 / 1000.0, a2 / 1_000_000.0, r2);
    }

    /// <summary>Гаусс с частичным выбором ведущего элемента для матрицы 3×4.</summary>
    private static bool SolveGauss3(Span<double> m, out double x0, out double x1, out double x2)
    {
        x0 = x1 = x2 = 0;

        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 3; r++)
                if (Math.Abs(m[r * 4 + col]) > Math.Abs(m[pivot * 4 + col])) pivot = r;

            if (Math.Abs(m[pivot * 4 + col]) < 1e-15) return false;

            if (pivot != col)
                for (int c = 0; c < 4; c++)
                    (m[col * 4 + c], m[pivot * 4 + c]) = (m[pivot * 4 + c], m[col * 4 + c]);

            double diag = m[col * 4 + col];
            for (int r = col + 1; r < 3; r++)
            {
                double factor = m[r * 4 + col] / diag;
                if (factor == 0) continue;
                for (int c = col; c < 4; c++) m[r * 4 + c] -= factor * m[col * 4 + c];
            }
        }

        x2 = m[2 * 4 + 3] / m[2 * 4 + 2];
        x1 = (m[1 * 4 + 3] - m[1 * 4 + 2] * x2) / m[1 * 4 + 1];
        x0 = (m[0 * 4 + 3] - m[0 * 4 + 1] * x1 - m[0 * 4 + 2] * x2) / m[0 * 4 + 0];
        return true;
    }
}
