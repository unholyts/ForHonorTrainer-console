using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;


internal sealed class Settings
{
    public int FirstToSecondClickMs { get; set; } = 800;
    public int SecondClickToEMs { get; set; } = 300;
    public int ClickHoldMs { get; set; } = 40;
    public int KeyHoldMs { get; set; } = 40;
    public int PauseBetweenCyclesMs { get; set; } = 1500;
    public int PauseAfterCommittedMs { get; set; } = 3000;
    public int EProbabilityPercent { get; set; } = 50;
    public string GameProcessName { get; set; } = "forhonor";


    public static Settings Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("settings.json was not found", path);

        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("settings.json is empty");
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        CheckRange(FirstToSecondClickMs, 50, 60_000, nameof(FirstToSecondClickMs));
        CheckRange(SecondClickToEMs, 0, 60_000, nameof(SecondClickToEMs));
        CheckRange(ClickHoldMs, 1, 1000, nameof(ClickHoldMs));
        CheckRange(KeyHoldMs, 1, 1000, nameof(KeyHoldMs));
        CheckRange(PauseBetweenCyclesMs, 0, 60_000, nameof(PauseBetweenCyclesMs));
        CheckRange(PauseAfterCommittedMs, 0, 60_000, nameof(PauseAfterCommittedMs));
        CheckRange(EProbabilityPercent, 0, 100, nameof(EProbabilityPercent));

        if (ClickHoldMs >= FirstToSecondClickMs)
            throw new InvalidDataException("Удержание ПКМ должно быть короче интервала между нажатиями.");
        if (string.IsNullOrWhiteSpace(GameProcessName))
            throw new InvalidDataException("Укажите имя процесса игры.");

        GameProcessName = Path.GetFileNameWithoutExtension(GameProcessName.Trim());
        if (string.IsNullOrWhiteSpace(GameProcessName))
            throw new InvalidDataException("Укажите имя процесса игры, например forhonor.");
    }

    public void Save(string path)
    {
        Validate();
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + Environment.NewLine);
    }

    private static void CheckRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw new InvalidDataException($"{name} must be between {min} and {max}");
    }
}

internal static class NativeInput
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint KeyScanCode = 0x0008;
    private const uint KeyUp = 0x0002;
    private const ushort EScanCode = 0x12;
    private const int InputSize = 40; // Windows x64 INPUT structure

    [StructLayout(LayoutKind.Explicit, Size = InputSize)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public static bool IsPressed(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public static bool IsGameInForeground(string processName)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static void RightButton(bool down) => Send(new Input
    {
        Type = InputMouse,
        Mouse = new MouseInput { Flags = down ? MouseRightDown : MouseRightUp }
    });

    public static void EKey(bool down) => Send(new Input
    {
        Type = InputKeyboard,
        Keyboard = new KeyboardInput
        {
            ScanCode = EScanCode,
            Flags = KeyScanCode | (down ? 0u : KeyUp)
        }
    });

    private static void Send(Input input)
    {
        if (IntPtr.Size != 8)
            throw new PlatformNotSupportedException("This program requires 64-bit Windows");

        if (SendInput(1, new[] { input }, InputSize) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed");
    }
}

internal static class TrainerEngine
{
    public static async Task RunLoop(Settings settings, CancellationToken stop,
        Action<int, bool, int> onCycle, Action<string> onStatus)
    {
        var cycle = 0;
        var waiting = true;
        try
        {
            onStatus("Ожидание окна For Honor");
            while (!stop.IsCancellationRequested)
            {
                if (!NativeInput.IsGameInForeground(settings.GameProcessName))
                {
                    if (!waiting)
                    {
                        onStatus("Ожидание окна For Honor");
                        waiting = true;
                    }
                    await Task.Delay(100, stop);
                    continue;
                }

                if (waiting)
                {
                    onStatus("Работает");
                    waiting = false;
                }

                var pressE = ShouldPressE(settings.EProbabilityPercent);
                var pauseMs = pressE ? settings.PauseBetweenCyclesMs : settings.PauseAfterCommittedMs;
                try
                {
                    await RunCycle(settings, pressE, stop);
                    onCycle(++cycle, pressE, pauseMs);
                }
                catch (FocusLostException)
                {
                    onStatus("Ожидание окна For Honor");
                    waiting = true;
                    continue;
                }

                await Task.Delay(pauseMs, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public static bool ShouldPressE(int probabilityPercent) =>
        RandomNumberGenerator.GetInt32(100) < probabilityPercent;

    private static async Task RunCycle(Settings settings, bool pressE, CancellationToken stop)
    {
        var timer = Stopwatch.StartNew();
        EnsureFocus(settings);
        await TapRightButton(settings.ClickHoldMs, stop);
        await WaitUntil(timer, settings.FirstToSecondClickMs, stop);

        EnsureFocus(settings);
        timer.Restart();
        await TapRightButton(settings.ClickHoldMs, stop);

        if (pressE)
        {
            await WaitUntil(timer, settings.SecondClickToEMs, stop);
            EnsureFocus(settings);
            await TapE(settings.KeyHoldMs, stop);
        }
    }

    private static void EnsureFocus(Settings settings)
    {
        if (!NativeInput.IsGameInForeground(settings.GameProcessName))
            throw new FocusLostException();
    }

    private static async Task TapRightButton(int holdMs, CancellationToken stop)
    {
        NativeInput.RightButton(true);
        try { await Task.Delay(holdMs, stop); }
        finally { NativeInput.RightButton(false); }
    }

    private static async Task TapE(int holdMs, CancellationToken stop)
    {
        NativeInput.EKey(true);
        try { await Task.Delay(holdMs, stop); }
        finally { NativeInput.EKey(false); }
    }

    private static async Task WaitUntil(Stopwatch timer, int targetMs, CancellationToken stop)
    {
        var remaining = targetMs - timer.ElapsedMilliseconds;
        if (remaining > 0)
            await Task.Delay(checked((int)remaining), stop);
    }

    private sealed class FocusLostException : Exception { }
}
