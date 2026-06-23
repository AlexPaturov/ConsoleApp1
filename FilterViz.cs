// ════════════════════════════════════════════════════════════════════
//  FilterViz — пошаговая диагностика фильтрации потока int32
//  Читает .bin файл (сырой поток с контроллера весов),
//  прогоняет через фильтры и показывает ASCII график + метрики.
//
//  ЗАПУСК:
//    dotnet run -- raw_COM3_20240101.bin
//
//  ── КАК ПРАВИЛЬНО ИСПОЛЬЗОВАТЬ ──────────────────────────────────
//
//  ШАГ 1 — Первый запуск: НЕ МЕНЯЙ Config
//  ───────────────────────────────────────
//  Запусти с дефолтными настройками. Смотришь только на ШАГ 0:
//    - Какой реальный диапазон значений? (Min/Max)
//    - Есть ли безумные выбросы в млрд? (значит есть рассинхрон потока)
//    - Какой Std у сырого сигнала? (это твой baseline)
//    - Как выглядит форма сигнала на графике?
//
//  ШАГ 2 — Ставишь реальные числа в Config
//  ─────────────────────────────────────────
//  На основе того что увидел в шаге 0:
//    Min/Max      → реальные границы твоего датчика
//                   Пример: датчик 0–150кг с точностью 1г → 0..150000
//    MaxDelta     → оцени типичный выброс на графике шага 0
//                   Начни с 10% от рабочего диапазона, потом уточняй
//    EmaAlpha     → начни с 0.2, уменьшай если сигнал ещё дрожит
//
//  ШАГ 3 — Второй запуск: смотришь метрики
//  ─────────────────────────────────────────
//  После каждого шага фильтрации смотришь:
//    "Std улучшение" → чем больше число, тем лучше шаг сработал
//    "Дропнуто"      → если дропнуто > 20%, параметр слишком жёсткий
//    График          → сигнал стал чище? форма не исказилась?
//
//  Признаки хорошего результата:
//    - Std после EMA в 3–10 раз меньше чем у сырого сигнала
//    - Дропнуто на clamp < 5% (остальное — реальный шум или рассинхрон)
//    - Дропнуто на delta < 15% (всплески и дребезг)
//    - На графике "Сырые vs EMA" виден тот же сигнал, только гладкий
//
//  ШАГ 4 — Переносишь параметры в производственный код
//  ─────────────────────────────────────────────────────
//  Когда доволен результатом — эти же числа из Config идут в код весов:
//    Config.Min      → константа MIN_WEIGHT в продакшн коде
//    Config.Max      → константа MAX_WEIGHT
//    Config.MaxDelta → константа MAX_DELTA
//    Config.EmaAlpha → константа EMA_ALPHA
//  Алгоритмы те же самые, только работают в реальном времени
//  на потоке байт вместо файла.
//
//  ── КРАСНЫЕ ФЛАГИ ───────────────────────────────────────────────
//  Если на ШАГ 0 видишь значения в диапазоне ±2 млрд →
//    это рассинхрон потока (битые пакеты сдвинули границы),
//    нужен ресинк перед фильтрацией (отдельная задача).
//
//  Если после всех фильтров Std почти не изменился →
//    либо MaxDelta слишком мягкий (не режет выбросы),
//    либо EmaAlpha слишком большой (слабое сглаживание).
//
//  Если Mean сильно уплыл после фильтров →
//    delta filter режет не выбросы а реальный сигнал,
//    увеличь MaxDelta.
// ════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

class FilterViz
{
    // ══════════════════════════════════════════════════════════════
    //  НАСТРОЙКИ
    //
    //  Первый запуск: оставь всё как есть, смотри ШАГ 0.
    //  Второй запуск: замени Min/Max/MaxDelta на реальные значения.
    // ══════════════════════════════════════════════════════════════
    static class Config
    {
        // Диапазон допустимых значений датчика.
        // Первый запуск: оставь широкими чтобы увидеть всё что приходит.
        // Второй запуск: поставь реальные границы датчика.
        public const int Min = -2_000_000_000;
        public const int Max =  2_000_000_000;

        // Максимальный прыжок между соседними значениями.
        // Режет всплески и дребезг.
        // Начни с 10% рабочего диапазона, потом уточняй по графику.
        public const int MaxDelta = 50_000;

        // Коэффициент сглаживания EMA (0.0 – 1.0).
        // Ближе к 0.0 → сильнее сглаживает, медленнее реагирует.
        // Ближе к 1.0 → слабее сглаживает, быстрее реагирует.
        // Старт: 0.2. Уменьшай если сигнал ещё дрожит.
        public const double EmaAlpha = 0.2;

        // Ширина и высота ASCII графика в символах.
        public const int ChartWidth  = 80;
        public const int ChartHeight = 20;

        // Сколько пакетов читать из файла (0 = все).
        public const int MaxPackets = 0;
    }
    // ══════════════════════════════════════════════════════════════

    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Использование: FilterViz <file.bin>");
            return;
        }

        string path = args[0];
        if (!File.Exists(path))
        {
            Console.WriteLine($"Файл не найден: {path}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════════");
        Console.WriteLine("  FilterViz — пошаговая фильтрация потока");
        Console.WriteLine("══════════════════════════════════════════════════");
        Console.WriteLine();

        var raw = ReadRaw(path);
        Console.WriteLine($"Прочитано байт:    {raw.rawBytes}");
        Console.WriteLine($"Полных пакетов:    {raw.values.Count}");
        Console.WriteLine($"Остаток байт:      {raw.remainder} " +
                          $"(потери {raw.remainder * 100.0 / raw.rawBytes:F2}%)");
        Console.WriteLine();
        Console.WriteLine("► Первый запуск? Смотри только ШАГ 0 — реальный диапазон");
        Console.WriteLine("  и форму сигнала. Потом настрой Config и запусти снова.");

        ShowStep("ШАГ 0 — Сырые данные (после парсинга int32)", raw.values);

        var clamped = StepClamp(raw.values, Config.Min, Config.Max);
        ShowStep($"ШАГ 1 — Hard clamp [{Config.Min} .. {Config.Max}]",
                 clamped, raw.values);

        var deltaed = StepDelta(clamped, Config.MaxDelta);
        ShowStep($"ШАГ 2 — Delta filter [макс Δ = {Config.MaxDelta}]",
                 deltaed, clamped);

        var ema = StepEma(deltaed, Config.EmaAlpha);
        ShowStep($"ШАГ 3 — EMA [α = {Config.EmaAlpha}]", ema, deltaed);

        // Итоговое сравнение
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════════");
        Console.WriteLine("  Итог");
        Console.WriteLine("══════════════════════════════════════════════════");
        if (raw.values.Count > 0 && ema.Count > 0)
        {
            double stdRaw = Std(raw.values);
            double stdEma = Std(ema);
            double improvement = stdRaw > 0 ? stdRaw / stdEma : 0;
            Console.WriteLine($"  Std сырой:   {stdRaw,10:F1}");
            Console.WriteLine($"  Std EMA:     {stdEma,10:F1}");
            Console.WriteLine($"  Улучшение:   {improvement,10:F1}x");
            Console.WriteLine();
            if (improvement >= 3)
                Console.WriteLine("  ✓ Хороший результат (≥3x улучшение)");
            else if (improvement >= 1.5)
                Console.WriteLine("  ~ Средний результат — попробуй уменьшить EmaAlpha");
            else
                Console.WriteLine("  ✗ Слабый результат — проверь MaxDelta и EmaAlpha");
        }
        Console.WriteLine();
        Console.WriteLine("  Следующий шаг: перенеси Config в производственный код весов.");
        Console.WriteLine("══════════════════════════════════════════════════");
        Console.WriteLine();
    }

    static (List<double> values, long rawBytes, int remainder) ReadRaw(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var values   = new List<double>();
        int limit    = Config.MaxPackets > 0
            ? Config.MaxPackets * 4
            : bytes.Length;

        for (int i = 0; i + 3 < Math.Min(bytes.Length, limit); i += 4)
            values.Add(BitConverter.ToInt32(bytes, i));

        return (values, bytes.Length, bytes.Length % 4);
    }

    static List<double> StepClamp(List<double> src, double min, double max)
    {
        var result = new List<double>(src.Count);
        foreach (var v in src)
            if (v >= min && v <= max)
                result.Add(v);
        return result;
    }

    static List<double> StepDelta(List<double> src, double maxDelta)
    {
        if (src.Count == 0) return src;
        var result = new List<double> { src[0] };
        for (int i = 1; i < src.Count; i++)
            if (Math.Abs(src[i] - src[i - 1]) <= maxDelta)
                result.Add(src[i]);
        return result;
    }

    static List<double> StepEma(List<double> src, double alpha)
    {
        if (src.Count == 0) return src;
        var result = new List<double>(src.Count);
        double ema = src[0];
        result.Add(ema);
        for (int i = 1; i < src.Count; i++)
        {
            ema = alpha * src[i] + (1.0 - alpha) * ema;
            result.Add(ema);
        }
        return result;
    }

    static void ShowStep(string title, List<double> current,
                         List<double> previous = null)
    {
        Console.WriteLine();
        Console.WriteLine($"┌─ {title}");

        if (current.Count == 0)
        {
            Console.WriteLine("│  ⚠ Нет данных после фильтра!");
            Console.WriteLine("│  Возможно параметр слишком жёсткий — расширь диапазон.");
            Console.WriteLine("└─────────────────────────────────────────────");
            return;
        }

        double mean  = current.Average();
        double min   = current.Min();
        double max   = current.Max();
        double std   = Std(current);
        int    count = current.Count;

        Console.WriteLine($"│  Точек:  {count,8}");
        Console.WriteLine($"│  Min:    {min,14:F1}");
        Console.WriteLine($"│  Max:    {max,14:F1}");
        Console.WriteLine($"│  Mean:   {mean,14:F1}");
        Console.WriteLine($"│  Std:    {std,14:F1}");

        if (previous != null && previous.Count > 0)
        {
            int    dropped  = previous.Count - count;
            double dropPct  = dropped * 100.0 / previous.Count;
            double stdPrev  = Std(previous);
            double stdDiff  = stdPrev - std;

            string dropWarn = dropPct > 20 ? "  ← много! смягчи параметр" : "";
            Console.WriteLine($"│  Дропнуто:      {dropped,6} ({dropPct:F1}%){dropWarn}");
            Console.WriteLine($"│  Std улучшение: {stdDiff:+F1;-F1;0}  " +
                              (stdDiff > 0 ? "↓ лучше" : "↑ хуже — проверь параметр"));
        }

        Console.WriteLine("│");
        DrawChart(current, Config.ChartWidth, Config.ChartHeight);
        Console.WriteLine("└─────────────────────────────────────────────");
    }

    static void DrawChart(List<double> data, int width, int height)
    {
        if (data.Count == 0) return;

        double min = data.Min();
        double max = data.Max();
        if (Math.Abs(max - min) < 1e-9) max = min + 1;

        var sampled = Downsample(data, width);

        var grid = new char[height, width];
        for (int r = 0; r < height; r++)
            for (int c = 0; c < width; c++)
                grid[r, c] = ' ';

        for (int c = 0; c < sampled.Count; c++)
        {
            int row = (int)((max - sampled[c]) / (max - min) * (height - 1));
            row = Math.Clamp(row, 0, height - 1);
            grid[row, c] = '█';
        }

        int midRow = height / 2;
        for (int r = 0; r < height; r++)
        {
            string prefix = r == 0        ? $"{max,10:F0} │"
                          : r == midRow   ? $"{(max + min) / 2,10:F0} │"
                          : r == height-1 ? $"{min,10:F0} │"
                          : "           │";

            Console.Write("│  " + prefix);
            for (int c = 0; c < width; c++)
                Console.Write(grid[r, c]);
            Console.WriteLine();
        }

        Console.WriteLine("│  " + new string(' ', 11) + "└" + new string('─', width));
        Console.WriteLine("│  " + new string(' ', 12) +
                          "0" + new string(' ', width / 2 - 2) +
                          $"{data.Count / 2}" +
                          new string(' ', width / 2 - 5) +
                          $"{data.Count}  (пакеты)");
    }

    static List<double> Downsample(List<double> data, int targetWidth)
    {
        if (data.Count <= targetWidth) return data;
        var result = new List<double>(targetWidth);
        double step = (double)data.Count / targetWidth;
        for (int i = 0; i < targetWidth; i++)
        {
            int from = (int)(i * step);
            int to   = Math.Min((int)((i + 1) * step), data.Count);
            double sum = 0;
            for (int j = from; j < to; j++) sum += data[j];
            result.Add(sum / (to - from));
        }
        return result;
    }

    static double Std(List<double> data)
    {
        if (data.Count < 2) return 0;
        double mean = data.Average();
        double sum  = data.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sum / (data.Count - 1));
    }
}
