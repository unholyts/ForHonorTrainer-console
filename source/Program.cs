using System.Text;
using System.Text.Json;

internal static class Program
{
    private static readonly object OutputLock = new();

    private static void Log(string message)
    {
        lock (OutputLock)
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private static bool IsSettingsError(Exception ex) =>
        ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            Console.Error.WriteLine("Требуется 64-разрядная Windows.");
            return 1;
        }

        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("ForHonorTrainer.Console.exe [--dry-run N]");
            Console.WriteLine("F8 — старт / стоп; F10 или Ctrl+C — выход.");
            Console.WriteLine("--dry-run N — показать N циклов без отправки ввода (1–1000).");
            return 0;
        }

        var dryRunCount = 0;
        if (args.Length != 0 && !(args.Length == 2 && args[0] == "--dry-run" &&
            int.TryParse(args[1], out dryRunCount) && dryRunCount is > 0 and <= 1000))
        {
            Console.Error.WriteLine("Использование: ForHonorTrainer.Console.exe [--dry-run N]");
            return 1;
        }

        var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        Settings settings;
        try { settings = Settings.Load(settingsPath); }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            Console.Error.WriteLine($"Ошибка настроек: {ex.Message}");
            return 1;
        }

        if (dryRunCount > 0)
        {
            Console.WriteLine("ПРОВЕРКА: ввод в игру не отправляется.");
            for (var i = 1; i <= dryRunCount; i++)
            {
                var pressE = TrainerEngine.ShouldPressE(settings.EProbabilityPercent);
                var pause = pressE ? settings.PauseBetweenCyclesMs : settings.PauseAfterCommittedMs;
                Console.WriteLine($"Цикл {i}: ПКМ → {settings.FirstToSecondClickMs} мс → ПКМ; " +
                    $"E: {(pressE ? $"через {settings.SecondClickToEMs} мс" : "пропустить")}; пауза {pause} мс.");
            }
            return 0;
        }

        Console.Title = "For Honor Trainer — Console";
        Console.WriteLine("FOR HONOR TRAINER — КОНСОЛЬНАЯ ВЕРСИЯ");
        Console.WriteLine("F8 — старт / стоп; F10 или Ctrl+C — выход.");
        Console.WriteLine($"Настройки: {settingsPath}");
        Console.WriteLine($"Процесс игры: {settings.GameProcessName}.exe");
        Console.WriteLine($"ПКМ → {settings.FirstToSecondClickMs} мс → ПКМ → {settings.SecondClickToEMs} мс → E ({settings.EProbabilityPercent}%).");
        Console.WriteLine($"Пауза с E: {settings.PauseBetweenCyclesMs} мс; без E: {settings.PauseAfterCommittedMs} мс.");
        Log("Остановлено. Нажмите F8 для старта.");

        using var exit = new CancellationTokenSource();
        CancellationTokenSource? runStop = null;
        Task? worker = null;
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; exit.Cancel(); };
        Console.CancelKeyPress += onCancel;

        async Task StopAsync()
        {
            var stopping = runStop;
            var finishing = worker;
            if (stopping is null || finishing is null) return;
            stopping.Cancel();
            try { await finishing; }
            catch (Exception ex) { Log($"Ошибка ввода: {ex.Message}"); }
            finally
            {
                stopping.Dispose();
                runStop = null;
                worker = null;
            }
            Log("Остановлено.");
        }

        // Не обрабатывать клавишу, удерживаемую ещё до запуска программы.
        var lastF8 = NativeInput.IsPressed(0x77);
        var lastF10 = NativeInput.IsPressed(0x79);
        try
        {
            while (!exit.IsCancellationRequested)
            {
                if (worker?.IsCompleted == true) await StopAsync();
                var f8 = NativeInput.IsPressed(0x77);
                var f10 = NativeInput.IsPressed(0x79);
                var toggle = f8 && !lastF8;
                var close = f10 && !lastF10;
                lastF8 = f8;
                lastF10 = f10;
                if (close) break;
                if (toggle)
                {
                    if (worker is not null) await StopAsync();
                    else
                    {
                        try
                        {
                            // Файл можно менять между остановкой и следующим стартом.
                            var current = Settings.Load(settingsPath);
                            runStop = CancellationTokenSource.CreateLinkedTokenSource(exit.Token);
                            var token = runStop.Token;
                            worker = Task.Run(() => TrainerEngine.RunLoop(current, token,
                                (count, pressE, pause) => Log($"Цикл {count}: E {(pressE ? "нажата" : "пропущена")}; пауза {pause} мс."),
                                Log));
                            Log("Запуск. Переключитесь в окно игры.");
                        }
                        catch (Exception ex) when (IsSettingsError(ex))
                        {
                            Log($"Ошибка настроек: {ex.Message}");
                        }
                    }
                }
                await Task.Delay(25, exit.Token);
            }
        }
        catch (OperationCanceledException) when (exit.IsCancellationRequested) { }
        finally
        {
            await StopAsync();
            Console.CancelKeyPress -= onCancel;
        }
        Log("Выход.");
        return 0;
    }
}
