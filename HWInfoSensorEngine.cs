// HWInfoSensorEngine.cs
// ─────────────────────────────────────────────────────────────────────────────
// Hardware telemetry service using LibreHardwareMonitorLib.
//
// Power resolution — 4-tier cascade (executed in strict priority order):
//
//  ┌──────┬──────────────────────────────────┬────────────────┬───────────────┐
//  │ Tier │ Source                           │ IsEstimated    │ Tag           │
//  ├──────┼──────────────────────────────────┼────────────────┼───────────────┤
//  │  1   │ Intel/AMD Platform Telemetry     │ false          │ [EXACT..]     │
//  │  2   │ Battery EC discharge-rate shunt  │ false          │ [PHYSICAL..] │
//  │  3   │ PMBus digital PSU (AC input)     │ false          │ [PHYSICAL..] │
//  │  4   │ Motherboard / EC VRM input shunt │ false          │ [PHYSICAL..] │
//  │  5   │ CPU + GPU + base offset (sum)    │ true           │ (EST)         │
//  └──────┴──────────────────────────────────┴────────────────┴───────────────┘
//
// Individual sensor tags:
//   CpuPowerTag  = "EXACT" when read from RAPL MSR / AMD SMU; "(EST)" otherwise.
//   GpuPowerTag  = "EXACT" when read from NVML / ADL; "(EST)" otherwise.
//
// ELEVATION REQUIREMENT:
//   WinRing0x64.sys is installed by LHM on first run to read Intel MSR 0x611
//   and AMD SMU registers.  Process must run as Administrator (enforce via
//   app.manifest requireAdministrator).
// ─────────────────────────────────────────────────────────────────────────────

using LibreHardwareMonitor.Hardware;

namespace HWMonitor;

/// <summary>
/// Thread-safe hardware telemetry service.  Call <see cref="GetSnapshot"/> at
/// any interval; dispose to unload the Ring-0 driver.
/// </summary>
public sealed class HWInfoSensorEngine : IDisposable
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const float   MinValidPowerWatts = 0.5f;  // below this = sensor noise / off
    private const string  TagExact           = "EXACT";
    private const string  TagEst             = "(EST)";
    private const string  TagNa              = "N/A";
    private const string  TagPhysical        = "[PHYSICAL SHUNT]";
    private const string  TagPlatform        = "[EXACT - Intel/Platform Telemetry]";

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly Computer        _computer;
    private readonly HardwareVisitor _visitor;
    private readonly object          _lock    = new();
    private          bool            _disposed;

    // Cache the Nvidia SMI limit (rarely changes, slow to query every 500ms)
    private float? _cachedNvidiaMaxLimitW;

    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Base component offset added in Tier-4 fallback mode (Watts).
    /// Covers motherboard VRMs, PCH, NVMe SSDs, fans, and USB peripherals
    /// that are not accounted for by CPU RAPL or GPU NVML sensors.
    /// Tune per platform:
    ///   Laptop   → 5–8 W
    ///   Desktop  → 10–15 W
    ///   HEDT     → 20–25 W
    /// </summary>
    public float ComponentBaseOffsetWatts { get; set; } = 10f;

    // ── Constructor ───────────────────────────────────────────────────────────

    public HWInfoSensorEngine()
    {
        _visitor = new HardwareVisitor();

        _computer = new Computer
        {
            IsCpuEnabled                = true,   // RAPL / SMU power + temp + clocks
            IsGpuEnabled                = true,   // NVML / ADL board power + temps
            IsMemoryEnabled             = true,   // GlobalMemoryStatusEx RAM
            IsMotherboardEnabled        = true,   // VRM input shunt (Tier 3)
            IsControllerEnabled         = true,   // Embedded Controller (EC)
            IsBatteryEnabled            = true,   // Tier 1: laptop battery shunt
            IsPsuEnabled                = true,   // Tier 2: PMBus digital PSU
            IsStorageEnabled            = false,  // SMART — skip for poll speed
            IsNetworkEnabled            = false,  // not needed
        };

        _computer.Open();
        _computer.Accept(_visitor);

        // Fetch the absolute max GPU board power limit via nvidia-smi (150W + 25W Dynamic Boost = 175W)
        _ = Task.Run(() => FetchNvidiaPowerLimitAsync());
    }

    private void FetchNvidiaPowerLimitAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=power.max_limit --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            string output = p?.StandardOutput.ReadToEnd()?.Trim();
            p?.WaitForExit();

            if (float.TryParse(output, out float limit))
            {
                _cachedNvidiaMaxLimitW = limit;
            }
        }
        catch { /* ignored if nvidia-smi is missing */ }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Refresh all sensors and return a new <see cref="HardwareSnapshot"/>.
    /// Safe to call from any thread.
    /// </summary>
    public HardwareSnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            _computer.Accept(_visitor);   // refresh all sensor values

            // ── Per-category accumulators ─────────────────────────────────
            float? cpuPowerW        = null;
            bool   cpuPowerIsExact  = false;
            float? cpuTempC         = null;
            float? cpuClockMhz      = null;
            float? cpuLimitW        = null;
            string cpuName          = "Unknown CPU";

            float? gpuPowerW        = null;
            bool   gpuPowerIsExact  = false;
            float? gpuCoreTempC     = null;
            float? gpuHotspotTempC  = null;
            float? gpuLimitW        = null;
            string gpuName          = "Unknown GPU";

            float? memUsedGb        = null;
            float? memAvailGb       = null;

            // ── Tier 1–4 physical power accumulators ──────────────────────
            float? platformPowerW   = null;   // Tier 1 (Intel PSys / AMD Platform)
            float? batteryPowerW    = null;   // Tier 2
            float? psuPowerW        = null;   // Tier 3
            float? vrmPowerW        = null;   // Tier 4

            // ── Walk hardware tree ────────────────────────────────────────
            foreach (IHardware hw in _computer.Hardware)
            {
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        cpuName = hw.Name;
                        (cpuPowerW, cpuPowerIsExact, cpuTempC, cpuClockMhz, cpuLimitW, float? p1) =
                            ReadCpuSensors(hw);
                        platformPowerW ??= p1;
                        break;

                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        if (gpuName == "Unknown GPU")
                        {
                            gpuName = hw.Name;
                            (gpuPowerW, gpuPowerIsExact, gpuCoreTempC, gpuHotspotTempC, gpuLimitW) =
                                ReadGpuSensors(hw);
                            
                            // Override with the absolute max hardware limit if available
                            if (_cachedNvidiaMaxLimitW.HasValue && hw.HardwareType == HardwareType.GpuNvidia)
                            {
                                gpuLimitW = _cachedNvidiaMaxLimitW.Value;
                            }
                        }
                        break;

                    case HardwareType.Memory:
                        (memUsedGb, memAvailGb) = ReadMemorySensors(hw);
                        break;

                    case HardwareType.Battery:
                        batteryPowerW = ReadBatterySensors(hw);
                        break;

                    case HardwareType.Psu:
                        psuPowerW = ReadPsuSensors(hw);
                        break;

                    case HardwareType.Motherboard:
                    case HardwareType.EmbeddedController:
                        (vrmPowerW, float? p2) = ReadVrmSensors(hw);
                        platformPowerW ??= p2;
                        break;
                }
            }

            // ── Resolve total power via cascade ───────────────────────────
            var (totalW, source, sourceTag, isEstimated) =
                ResolveTotalPower(platformPowerW, batteryPowerW, psuPowerW, vrmPowerW, cpuPowerW, gpuPowerW);

            // ── Build final snapshot ──────────────────────────────────────
            return new HardwareSnapshot
            {
                CapturedAtUtc             = DateTime.UtcNow,

                CpuName                   = cpuName,
                CpuPackagePowerWatts      = cpuPowerW,
                CpuPowerTag               = cpuPowerW.HasValue
                                                ? (cpuPowerIsExact ? TagExact : TagEst)
                                                : TagNa,
                CpuTemperatureCelsius     = cpuTempC,
                CpuCoreClockMhz           = cpuClockMhz,
                CpuPowerLimitWatts        = cpuLimitW,

                GpuName                   = gpuName,
                GpuBoardPowerWatts        = gpuPowerW,
                GpuPowerTag               = gpuPowerW.HasValue
                                                ? (gpuPowerIsExact ? TagExact : TagEst)
                                                : TagNa,
                GpuCoreTempCelsius        = gpuCoreTempC,
                GpuHotspotTempCelsius     = gpuHotspotTempC,
                GpuPowerLimitWatts        = gpuLimitW,

                MemoryUsedGb              = memUsedGb,
                MemoryAvailableGb         = memAvailGb,

                TotalSystemPowerWatts     = totalW,
                PowerSource               = source,
                PowerSourceTag            = sourceTag,
                IsEstimated               = isEstimated,
                ComponentBaseOffsetWatts  = ComponentBaseOffsetWatts,
            };
        }
    }

    // ── Tier resolution ───────────────────────────────────────────────────────

    private (float watts, PowerSourceKind kind, string tag, bool isEst)
        ResolveTotalPower(
            float? platformW,
            float? batteryW,
            float? psuW,
            float? vrmW,
            float? cpuW,
            float? gpuW)
    {
        // Tier 1 — Intel/AMD Platform Telemetry (MSR PSys)
        if (platformW is float pw && pw > MinValidPowerWatts)
            return (pw, PowerSourceKind.PlatformTelemetry, TagPlatform, false);

        // Tier 2 — Battery EC shunt
        if (batteryW is float bw && bw > MinValidPowerWatts)
            return (bw, PowerSourceKind.BatteryShunt, TagPhysical, false);

        // Tier 3 — PMBus digital PSU
        if (psuW is float pw2 && pw2 > MinValidPowerWatts)
            return (pw2, PowerSourceKind.DigitalPsu, TagPhysical, false);

        // Tier 4 — Motherboard / EC VRM shunt
        if (vrmW is float vw && vw > MinValidPowerWatts)
            return (vw, PowerSourceKind.VrmInputShunt, TagPhysical, false);

        // Tier 5 — Component sum fallback
        float sum = (cpuW ?? 0f) + (gpuW ?? 0f) + ComponentBaseOffsetWatts;
        return (MathF.Max(sum, 0f), PowerSourceKind.CalculatedSum, TagEst, true);
    }

    // ── CPU reader ────────────────────────────────────────────────────────────

    private static (float? power, bool exact, float? temp, float? clock, float? limit, float? platformPower)
        ReadCpuSensors(IHardware hw)
    {
        float? power = null; bool exact = false;
        float? temp  = null;
        float? clock = null;
        float? limit = null;
        float? platform = null;

        ProcessCpuSensorArray(hw.Sensors, ref power, ref exact, ref temp, ref clock, ref limit, ref platform);

        foreach (IHardware sub in hw.SubHardware)
        {
            sub.Update();
            ProcessCpuSensorArray(sub.Sensors, ref power, ref exact, ref temp, ref clock, ref limit, ref platform);
        }

        return (power, exact, temp, clock, limit, platform);
    }

    /// <summary>
    /// Priority-ordered sensor matching for CPU.
    ///
    /// Power  — "CPU Package" > "Package" > "CPU Cores" > any Power
    ///   Sensors named "Package" come from the RAPL MSR domain and are EXACT.
    ///   Any other Power sensor is tagged (EST).
    ///
    /// Temp   — "CPU Package" > "Tctl/Tdie" > "CPU Die Average" > "Core #0" > any Temp
    /// Clock  — "CPU Core #0" > "CPU Core" > any Clock
    /// </summary>
    private static void ProcessCpuSensorArray(
        ISensor[]  sensors,
        ref float? power,
        ref bool   exact,
        ref float? temp,
        ref float? clock,
        ref float? limit,
        ref float? platform)
    {
        foreach (ISensor s in sensors)
        {
            if (!s.Value.HasValue) continue;
            float  v = s.Value.Value;
            string n = s.Name.ToUpperInvariant();

            // ── Power ─────────────────────────────────────────────────────
            if (s.SensorType == SensorType.Power)
            {
                if (n.Equals("TOTAL SYSTEM POWER") || n.Equals("SYSTEM POWER") || n.Equals("SYSTEM AGENT POWER"))
                {
                    platform ??= v;
                }

                if (n.Contains("LIMIT"))
                {
                    if (n.Contains("PL2") || n.Contains("PACKAGE POWER LIMIT 2") || n.Contains("PACKAGE POWER LIMIT"))
                    {
                        limit = v; // Prefer highest level limit
                    }
                    else
                    {
                        limit ??= v;
                    }
                }
                else if (power is null)
                {
                    if (n.Contains("CPU PACKAGE") || n.Contains("PACKAGE"))
                    {
                        power = v;
                        exact = true;   // RAPL MSR / AMD SMU — hardware counter
                    }
                    else if (n.Contains("CPU CORES") || n.Contains("CPU CORE"))
                    {
                        power ??= v;
                        // exact stays false — sub-domain sensors are derived
                    }
                    else
                    {
                        power ??= v;
                        // exact stays false
                    }
                }
            }

            // ── Temperature ───────────────────────────────────────────────
            if (s.SensorType == SensorType.Temperature && temp is null)
            {
                if      (n.Contains("CPU PACKAGE"))                        temp = v;
                else if (n.Contains("TCTL") || n.Contains("TDIE"))         temp ??= v;
                else if (n.Contains("CPU DIE") || n.Contains("CPU AVERAGE")) temp ??= v;
                else if (n.Contains("CORE #0") || n.Contains("CORE 0"))    temp ??= v;
                else                                                         temp ??= v;
            }

            // ── Clock ─────────────────────────────────────────────────────
            if (s.SensorType == SensorType.Clock && clock is null)
            {
                if      (n.Contains("CPU CORE #0") || n.Contains("CORE #0")) clock = v;
                else if (n.Contains("CPU CORE"))                               clock ??= v;
                else                                                            clock ??= v;
            }
        }
    }

    // ── GPU reader ────────────────────────────────────────────────────────────

    private static (float? power, bool exact, float? coreTemp, float? hotspot, float? limit)
        ReadGpuSensors(IHardware hw)
    {
        float? power    = null; bool exact = false;
        float? coreTemp = null;
        float? hotspot  = null;
        float? limit    = null;

        ProcessGpuSensorArray(hw.Sensors, ref power, ref exact, ref coreTemp, ref hotspot, ref limit);

        foreach (IHardware sub in hw.SubHardware)
        {
            sub.Update();
            ProcessGpuSensorArray(sub.Sensors, ref power, ref exact, ref coreTemp, ref hotspot, ref limit);
        }

        return (power, exact, coreTemp, hotspot, limit);
    }

    /// <summary>
    /// Priority-ordered sensor matching for GPU.
    ///
    /// Power  — "GPU Power" / "GPU Package" / "Total Board" → EXACT (NVML / ADL physical)
    ///          Any other Power → (EST)
    ///
    /// Core temp — avoid picking up hotspot, VRAM, VRM sensors.
    /// Hotspot   — explicitly match "Hot Spot" / "Hotspot" / "Junction".
    /// </summary>
    private static void ProcessGpuSensorArray(
        ISensor[]  sensors,
        ref float? power,
        ref bool   exact,
        ref float? coreTemp,
        ref float? hotspot,
        ref float? limit)
    {
        foreach (ISensor s in sensors)
        {
            if (!s.Value.HasValue) continue;
            float  v = s.Value.Value;
            string n = s.Name.ToUpperInvariant();

            // ── Power ─────────────────────────────────────────────────────
            if (s.SensorType == SensorType.Power)
            {
                if (n.Contains("LIMIT"))
                {
                    limit ??= v;
                }
                else if (power is null)
                {
                    if (n.Contains("GPU POWER")   || n.Contains("GPU PACKAGE")
                     || n.Contains("TOTAL BOARD"))
                    {
                        power = v;
                        exact = true;   // NVML nvmlDeviceGetPowerUsage / ADL PMLOG
                    }
                    else
                    {
                        power ??= v;
                        // exact stays false
                    }
                }
            }

            // ── Core temperature ──────────────────────────────────────────
            if (s.SensorType == SensorType.Temperature && coreTemp is null)
            {
                // Exclude hotspot / VRAM / VRM sensors from core-temp slot.
                if (n.Contains("HOT")    || n.Contains("SPOT")
                 || n.Contains("JUNCTION") || n.Contains("MEMORY")
                 || n.Contains("VRM"))
                    continue;

                if      (n.Contains("GPU CORE") || n.Contains("GPU TEMPERATURE") || n == "GPU")
                    coreTemp = v;
                else
                    coreTemp ??= v;
            }

            // ── Hotspot temperature ───────────────────────────────────────
            if (s.SensorType == SensorType.Temperature && hotspot is null)
            {
                if (n.Contains("HOT SPOT") || n.Contains("HOTSPOT") || n.Contains("JUNCTION"))
                    hotspot = v;
            }
        }
    }

    // ── Memory reader ─────────────────────────────────────────────────────────

    private static (float? used, float? avail) ReadMemorySensors(IHardware hw)
    {
        float? used = null, avail = null;

        foreach (ISensor s in hw.Sensors)
        {
            if (!s.Value.HasValue || s.SensorType != SensorType.Data) continue;
            string n = s.Name.ToUpperInvariant();

            if      (n == "MEMORY USED")      used  = s.Value.Value;
            else if (n == "MEMORY AVAILABLE") avail = s.Value.Value;
        }

        return (used, avail);
    }

    // ── Tier 1: Battery / ACPI shunt ─────────────────────────────────────────

    /// <summary>
    /// Reads battery discharge rate from the laptop Embedded Controller.
    /// Sensor names matched (case-insensitive):
    ///   "Discharge Rate"  — power drawn from battery (on-battery)
    ///   "Charge Rate"     — power entering battery (plugged-in, excluded from total)
    ///   "Total Power"     — some EC firmware reports net system power directly
    ///
    /// Returns null when the laptop is plugged in (discharge rate = 0 W)
    /// so the cascade falls through to Tier 2/3/4 correctly.
    /// </summary>
    private static float? ReadBatterySensors(IHardware hw)
    {
        hw.Update();
        foreach (IHardware sub in hw.SubHardware) sub.Update();

        float? discharge = null;
        float? total     = null;

        foreach (ISensor s in hw.Sensors)
        {
            if (!s.Value.HasValue || s.SensorType != SensorType.Power) continue;
            string n = s.Name.ToUpperInvariant();

            if (n.Contains("DISCHARGE RATE") || n.Contains("DISCHARGE")) discharge ??= s.Value.Value;
            if (n.Contains("TOTAL POWER"))                                 total     ??= s.Value.Value;
        }

        return discharge ?? total;
    }

    // ── Tier 2: PMBus digital PSU ─────────────────────────────────────────────

    /// <summary>
    /// Reads AC input power from a USB PMBus power supply.
    /// Supported hardware: Corsair RMi/AXi/HXi, Seasonic Focus PX, NZXT HALE.
    /// Prefers AC-input power over DC-output (AC includes PSU efficiency losses).
    /// </summary>
    private static float? ReadPsuSensors(IHardware hw)
    {
        hw.Update();
        foreach (IHardware sub in hw.SubHardware) sub.Update();

        float? acIn   = null;
        float? dcOut  = null;
        float? anyPow = null;

        foreach (ISensor s in hw.Sensors)
        {
            if (!s.Value.HasValue || s.SensorType != SensorType.Power) continue;
            string n = s.Name.ToUpperInvariant();

            if      (n.Contains("POWER IN")   || n.Contains("AC POWER")
                  || n.Contains("INPUT POWER"))                  acIn   ??= s.Value.Value;
            else if (n.Contains("OUTPUT POWER") || n.Contains("DC POWER"))
                                                                  dcOut  ??= s.Value.Value;
            else                                                   anyPow ??= s.Value.Value;
        }

        return acIn ?? dcOut ?? anyPow;
    }

    // ── Tier 4: Motherboard / EC VRM input shunt ──────────────────────────────

    /// <summary>
    /// Reads VRM input or system total power from high-end motherboard sensors.
    /// Present on: ASUS ProArt / ROG Maximus, MSI MEG, SuperMicro server boards.
    /// The sensor sits on the EPS 12V / ATX12V rail before voltage conversion.
    ///
    /// Also checks EmbeddedController hardware for laptop platforms that expose
    /// a system-level power sensor outside the battery ACPI interface.
    /// </summary>
    private static (float? vrm, float? platform) ReadVrmSensors(IHardware hw)
    {
        float? bestVrm = null;
        float? platform = null;

        // Check sub-hardware first (SuperIO chips live here on most boards).
        foreach (IHardware sub in hw.SubHardware)
        {
            sub.Update();
            foreach (ISensor s in sub.Sensors)
            {
                if (!s.Value.HasValue || s.SensorType != SensorType.Power) continue;
                string n = s.Name.ToUpperInvariant();

                if (n.Equals("TOTAL SYSTEM POWER") || n.Equals("SYSTEM POWER") || n.Equals("SYSTEM AGENT POWER"))
                {
                    platform ??= s.Value.Value;
                }
                else if (n.Contains("SYSTEM POWER") || n.Contains("INPUT POWER")
                 || n.Contains("VRM INPUT")    || n.Contains("VRM POWER")
                 || n.Contains("ATX12V POWER") || n.Contains("12V INPUT")
                 || n.Contains("CPU VRM")       || n.Contains("TOTAL POWER"))
                {
                    // Accumulate max in case multiple per-rail sensors are present.
                    bestVrm = bestVrm.HasValue
                        ? MathF.Max(bestVrm.Value, s.Value.Value)
                        : s.Value.Value;
                }
            }
        }

        // Top-level motherboard sensors (rarer, some SuperMicro boards).
        foreach (ISensor s in hw.Sensors)
        {
            if (!s.Value.HasValue || s.SensorType != SensorType.Power) continue;
            string n = s.Name.ToUpperInvariant();

            if (n.Equals("TOTAL SYSTEM POWER") || n.Equals("SYSTEM POWER") || n.Equals("SYSTEM AGENT POWER"))
            {
                platform ??= s.Value.Value;
            }
            else if (n.Contains("SYSTEM POWER") || n.Contains("INPUT POWER")
             || n.Contains("VRM INPUT")    || n.Contains("12V INPUT"))
            {
                bestVrm = bestVrm.HasValue
                    ? MathF.Max(bestVrm.Value, s.Value.Value)
                    : s.Value.Value;
            }
        }

        return (bestVrm, platform);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock) { _computer.Close(); }
    }
}
