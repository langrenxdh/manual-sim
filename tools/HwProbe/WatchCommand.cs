using System.Diagnostics;
using System.Globalization;
using System.Text;
using SDL;
using static SDL.SDL3;

namespace HwProbe;

/// <summary>
/// `HwProbe watch`: polls the joystick at ~1 kHz and shows raw axes, buttons and hats.
/// Per axis it tracks min/max, the number of distinct raw values seen and the smallest step
/// between them, which is what tells us the real pedal resolution.
/// </summary>
internal static unsafe class WatchCommand
{
    private const double PollPeriodMs = 1.0;
    private const double RenderPeriodMs = 50.0;
    private const int BarWidth = 30;
    private const int RecentPressCount = 10;

    private sealed class AxisStats
    {
        public readonly bool[] Seen = new bool[65536];
        public int Distinct;
        public short Min = short.MaxValue, Max = short.MinValue;

        public void Add(short v)
        {
            if (!Seen[v + 32768]) { Seen[v + 32768] = true; Distinct++; }
            if (v < Min) Min = v;
            if (v > Max) Max = v;
        }

        /// <summary>Smallest gap between two distinct seen values; 0 if fewer than two.</summary>
        public int SmallestStep()
        {
            int best = 0, prev = -1;
            for (int i = 0; i < Seen.Length; i++)
            {
                if (!Seen[i]) continue;
                if (prev >= 0 && (best == 0 || i - prev < best)) best = i - prev;
                prev = i;
            }
            return best;
        }
    }

    public static int Run(Options opts, CancellationToken cancel)
    {
        var joy = Sdl.OpenJoystick(opts.DeviceIndex);
        if (joy == null) return 1;

        int nAxes = SDL_GetNumJoystickAxes(joy);
        int nButtons = SDL_GetNumJoystickButtons(joy);
        int nHats = SDL_GetNumJoystickHats(joy);
        string name = SDL_GetJoystickName(joy) ?? "?";

        var axes = new short[nAxes];
        var stats = new AxisStats[nAxes];
        for (int a = 0; a < nAxes; a++) stats[a] = new AxisStats();
        var buttons = new bool[nButtons];
        var hats = new byte[nHats];
        var recentPresses = new Queue<int>();

        using var csv = opts.CsvPath is null ? null : OpenCsv(opts.CsvPath, nAxes, nHats);

        Console.Clear();
        Console.CursorVisible = false;
        var clock = Stopwatch.StartNew();
        double nextPoll = 0, nextRender = 0, rateWindowStart = 0;
        long polls = 0, changes = 0;
        double pollHz = 0, changeHz = 0;

        try
        {
            while (!cancel.IsCancellationRequested)
            {
                WaitUntil(clock, nextPoll);
                nextPoll += PollPeriodMs;
                double nowMs = clock.Elapsed.TotalMilliseconds;
                if (nowMs > nextPoll + 10 * PollPeriodMs) nextPoll = nowMs; // fell behind (e.g. console stall): resync

                SDL_UpdateJoysticks();
                bool changed = false;
                for (int a = 0; a < nAxes; a++)
                {
                    short v = SDL_GetJoystickAxis(joy, a);
                    changed |= v != axes[a];
                    axes[a] = v;
                    stats[a].Add(v);
                }
                for (int b = 0; b < nButtons; b++)
                {
                    bool down = SDL_GetJoystickButton(joy, b);
                    if (down && !buttons[b])
                    {
                        recentPresses.Enqueue(b);
                        if (recentPresses.Count > RecentPressCount) recentPresses.Dequeue();
                    }
                    changed |= down != buttons[b];
                    buttons[b] = down;
                }
                for (int h = 0; h < nHats; h++)
                {
                    byte v = SDL_GetJoystickHat(joy, h);
                    changed |= v != hats[h];
                    hats[h] = v;
                }
                polls++;
                if (changed) changes++;
                csv?.WriteLine(CsvRow(nowMs, axes, buttons, hats));

                if (nowMs - rateWindowStart >= 1000)
                {
                    double s = (nowMs - rateWindowStart) / 1000;
                    pollHz = polls / s;
                    changeHz = changes / s;
                    polls = changes = 0;
                    rateWindowStart = nowMs;
                }

                if (nowMs >= nextRender)
                {
                    nextRender = nowMs + RenderPeriodMs;
                    if (!HandleKeys(stats, recentPresses)) break;
                    Render(name, opts.CsvPath, pollHz, changeHz, axes, stats, buttons, hats, recentPresses);
                }
            }
        }
        finally
        {
            Console.CursorVisible = true;
            SDL_CloseJoystick(joy);
        }

        Console.WriteLine();
        Console.WriteLine("Summary (copy this into your report):");
        for (int a = 0; a < nAxes; a++)
        {
            var st = stats[a];
            Console.WriteLine($"  axis {a}: min {st.Min,6}  max {st.Max,6}  distinct {st.Distinct,5}  " +
                              $"smallest step {st.SmallestStep()}");
        }
        return 0;
    }

    /// <summary>Sleeps while far from the deadline, then spins: Thread.Sleep alone is too coarse for 1 ms.</summary>
    private static void WaitUntil(Stopwatch clock, double deadlineMs)
    {
        while (true)
        {
            double remaining = deadlineMs - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return;
            if (remaining > 2) Thread.Sleep(1);
            else Thread.SpinWait(50);
        }
    }

    /// <summary>Returns false when the user asked to quit.</summary>
    private static bool HandleKeys(AxisStats[] stats, Queue<int> recentPresses)
    {
        while (Console.KeyAvailable)
        {
            switch (Console.ReadKey(intercept: true).Key)
            {
                case ConsoleKey.Q or ConsoleKey.Escape:
                    return false;
                case ConsoleKey.R:
                    for (int a = 0; a < stats.Length; a++) stats[a] = new AxisStats();
                    recentPresses.Clear();
                    break;
            }
        }
        return true;
    }

    private static void Render(string name, string? csvPath, double pollHz, double changeHz, short[] axes,
        AxisStats[] stats, bool[] buttons, byte[] hats, Queue<int> recentPresses)
    {
        var sb = new StringBuilder();
        int width = Math.Max(Console.WindowWidth - 1, 40);
        void Line(string s) => sb.Append(s.Length > width ? s[..width] : s.PadRight(width)).Append('\n');

        Line($"{name}   poll {pollHz,6:F0} Hz   input changes {changeHz,5:F0} Hz" +
             (csvPath is null ? "" : $"   logging to {csvPath}"));
        Line("keys: r = reset stats, q = quit");
        Line("");
        Line("axis    raw   0..1   " + "".PadRight(BarWidth) + "     min     max  distinct  step");
        for (int a = 0; a < axes.Length; a++)
        {
            double norm = (axes[a] + 32768) / 65535.0;
            int filled = (int)Math.Round(norm * BarWidth);
            var st = stats[a];
            Line($"{a,4} {axes[a],6} {norm,6:F3}  [{new string('#', filled)}{new string('.', BarWidth - filled)}] " +
                 $"{st.Min,7} {st.Max,7} {st.Distinct,9} {st.SmallestStep(),5}");
        }
        Line("");
        var down = Enumerable.Range(0, buttons.Length).Where(b => buttons[b]).ToList();
        Line($"buttons held: {(down.Count == 0 ? "-" : string.Join(" ", down))}");
        Line($"recent presses (oldest first): {(recentPresses.Count == 0 ? "-" : string.Join(" ", recentPresses))}");
        for (int h = 0; h < hats.Length; h++)
            Line($"hat {h}: {HatName(hats[h])}");

        Console.SetCursorPosition(0, 0);
        Console.Write(sb.ToString());
    }

    private static string HatName(byte v) => v switch
    {
        (byte)SDL_HAT_CENTERED => "centered",
        (byte)SDL_HAT_UP => "up",
        (byte)SDL_HAT_RIGHT => "right",
        (byte)SDL_HAT_DOWN => "down",
        (byte)SDL_HAT_LEFT => "left",
        (byte)SDL_HAT_RIGHTUP => "right-up",
        (byte)SDL_HAT_RIGHTDOWN => "right-down",
        (byte)SDL_HAT_LEFTUP => "left-up",
        (byte)SDL_HAT_LEFTDOWN => "left-down",
        _ => $"0x{v:X2}",
    };

    private static StreamWriter OpenCsv(string path, int nAxes, int nHats)
    {
        var w = new StreamWriter(path, append: false, Encoding.UTF8);
        var cols = new List<string> { "t_ms" };
        cols.AddRange(Enumerable.Range(0, nAxes).Select(a => $"axis{a}"));
        cols.Add("buttons_held");
        cols.AddRange(Enumerable.Range(0, nHats).Select(h => $"hat{h}"));
        w.WriteLine(string.Join(',', cols));
        return w;
    }

    private static string CsvRow(double tMs, short[] axes, bool[] buttons, byte[] hats)
    {
        var sb = new StringBuilder();
        sb.Append(tMs.ToString("F3", CultureInfo.InvariantCulture));
        foreach (var v in axes) sb.Append(',').Append(v);
        // Space-separated indices of held buttons; empty when none.
        sb.Append(',').AppendJoin(' ', Enumerable.Range(0, buttons.Length).Where(b => buttons[b]));
        foreach (var h in hats) sb.Append(',').Append(h);
        return sb.ToString();
    }
}
