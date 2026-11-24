namespace KSnakeBatteryTray.Services;

/// <summary>
/// Filters momentary voltage-sag / voltage-recovery jitter (typical +/-2-6% on ADC-based wireless mice)
/// so the displayed battery percentage behaves monotonically like smartphones and laptops:
/// - While discharging: ignores small upward voltage rebounds (<8%) and smooths isolated sudden dips.
/// - While charging: ignores small downward jitter (<5%) and updates upward immediately.
/// - Automatically resets on charging-state transitions, long gaps (>15 min), or sustained real jumps (>=8%).
/// </summary>
public sealed class BatteryStabilizer
{
    private sealed class DeviceFilterState
    {
        public int DisplayedPercent;
        public bool IsCharging;
        public DateTime LastUpdateUtc;
        public int PendingJumpCount;
        public int PendingDipCount;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceFilterState> _states = new(StringComparer.OrdinalIgnoreCase);

    public int Filter(string deviceId, int rawPercent, bool isCharging, bool enabled)
    {
        int clamped = Math.Clamp(rawPercent, 0, 100);
        if (!enabled || string.IsNullOrWhiteSpace(deviceId) || deviceId == "none")
        {
            Reset(deviceId);
            return clamped;
        }

        DateTime now = DateTime.UtcNow;

        lock (_lock)
        {
            if (!_states.TryGetValue(deviceId, out var state))
            {
                _states[deviceId] = new DeviceFilterState
                {
                    DisplayedPercent = clamped,
                    IsCharging = isCharging,
                    LastUpdateUtc = now
                };
                return clamped;
            }

            // Reset baseline if charging state changed or after a long absence (>15 min, e.g. charged externally/sleep)
            bool longGap = (now - state.LastUpdateUtc) > TimeSpan.FromMinutes(15);
            if (state.IsCharging != isCharging || longGap)
            {
                state.DisplayedPercent = clamped;
                state.IsCharging = isCharging;
                state.LastUpdateUtc = now;
                state.PendingJumpCount = 0;
                state.PendingDipCount = 0;
                return clamped;
            }

            state.LastUpdateUtc = now;

            if (isCharging)
            {
                state.PendingDipCount = 0;

                if (clamped >= state.DisplayedPercent)
                {
                    state.DisplayedPercent = clamped;
                    state.PendingJumpCount = 0;
                }
                else
                {
                    // Ignore minor downward noise while charging; only accept sustained >=5% drop
                    int drop = state.DisplayedPercent - clamped;
                    if (drop >= 5)
                    {
                        state.PendingJumpCount++;
                        if (state.PendingJumpCount >= 2)
                        {
                            state.DisplayedPercent = clamped;
                            state.PendingJumpCount = 0;
                        }
                    }
                    else
                    {
                        state.PendingJumpCount = 0;
                    }
                }
            }
            else
            {
                // Discharging: prevent small upward voltage rebounds (e.g. 62% -> 64% -> 62%)
                if (clamped > state.DisplayedPercent)
                {
                    state.PendingDipCount = 0;
                    int rise = clamped - state.DisplayedPercent;

                    // Accept a real recharge jump (>=8%) once confirmed by 2 consecutive readings
                    if (rise >= 8)
                    {
                        state.PendingJumpCount++;
                        if (state.PendingJumpCount >= 2)
                        {
                            state.DisplayedPercent = clamped;
                            state.PendingJumpCount = 0;
                        }
                    }
                    else
                    {
                        state.PendingJumpCount = 0;
                    }
                }
                else if (clamped < state.DisplayedPercent)
                {
                    state.PendingJumpCount = 0;
                    int drop = state.DisplayedPercent - clamped;

                    if (drop <= 2)
                    {
                        // Normal gradual drain: update immediately
                        state.DisplayedPercent = clamped;
                        state.PendingDipCount = 0;
                    }
                    else
                    {
                        // Sudden large dip (e.g. transient load spike): step down smoothly or confirm on 2nd reading
                        state.PendingDipCount++;
                        if (state.PendingDipCount >= 2)
                        {
                            state.DisplayedPercent = clamped;
                            state.PendingDipCount = 0;
                        }
                        else
                        {
                            state.DisplayedPercent = Math.Max(clamped, state.DisplayedPercent - 1);
                        }
                    }
                }
                else
                {
                    state.PendingJumpCount = 0;
                    state.PendingDipCount = 0;
                }
            }

            return state.DisplayedPercent;
        }
    }

    public void Reset(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        lock (_lock)
        {
            _states.Remove(deviceId);
        }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _states.Clear();
        }
    }
}
