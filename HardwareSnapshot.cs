// HardwareSnapshot.cs
// ─────────────────────────────────────────────────────────────────────────────
// Immutable point-in-time snapshot with explicit EXACT / (EST) tagging on every
// power field, matching HWiNFO64's sensor-validity model.
// ─────────────────────────────────────────────────────────────────────────────

namespace HWMonitor;

/// <summary>
/// Enumerates every possible source that can back
/// <see cref="HardwareSnapshot.TotalSystemPowerWatts"/>.
/// </summary>
public enum PowerSourceKind
{
    /// <summary>Intel/AMD Enhanced Platform Telemetry (MSR PSys / Total System Power).</summary>
    PlatformTelemetry,
    /// <summary>Laptop EC battery discharge-rate shunt (most accurate).</summary>
    BatteryShunt,
    /// <summary>PMBus digital PSU reporting AC-input power via USB HID.</summary>
    DigitalPsu,
    /// <summary>Motherboard VRM-input or EPS-12V shunt resistor.</summary>
    VrmInputShunt,
    /// <summary>CPU + GPU component sum + fixed base offset (no physical shunt).</summary>
    CalculatedSum,
}

/// <summary>
/// Full hardware telemetry snapshot.  Every power field that could not be read
/// from a physical sensor carries a tag string of <c>"(EST)"</c>; fields that
/// came from real hardware carry <c>"[EXACT]"</c>.
/// </summary>
public sealed record HardwareSnapshot
{
    // ── Timestamp ─────────────────────────────────────────────────────────────
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;

    // ── CPU ───────────────────────────────────────────────────────────────────

    /// <summary>CPU package name (e.g. "Intel Core Ultra 9 185H").</summary>
    public string CpuName { get; init; } = "Unknown CPU";

    /// <summary>
    /// CPU Package Power in Watts from Intel RAPL MSR 0x611 or AMD SMU PPT.
    /// Null when the Ring-0 driver is unavailable (no admin) or sensor absent.
    /// </summary>
    public float? CpuPackagePowerWatts { get; init; }

    /// <summary>
    /// <c>"[EXACT]"</c> when read from RAPL/SMU; <c>"(EST)"</c> when estimated
    /// from utilisation; <c>"N/A"</c> when completely unavailable.
    /// </summary>
    public string CpuPowerTag { get; init; } = "N/A";

    /// <summary>CPU package / die temperature in °C.</summary>
    public float? CpuTemperatureCelsius { get; init; }

    /// <summary>Representative core clock in MHz (Core #0 / first P-core).</summary>
    public float? CpuCoreClockMhz { get; init; }

    /// <summary>Dynamic PL1/PL2 power limit if read from hardware.</summary>
    public float? CpuPowerLimitWatts { get; init; }

    // ── GPU ───────────────────────────────────────────────────────────────────

    /// <summary>GPU name (e.g. "NVIDIA GeForce RTX 4090 Laptop GPU").</summary>
    public string GpuName { get; init; } = "Unknown GPU";

    /// <summary>
    /// GPU total board power in Watts from NVML / ADL board-power sensor.
    /// Null when no discrete GPU is detected or NVML is unavailable.
    /// </summary>
    public float? GpuBoardPowerWatts { get; init; }

    /// <summary>
    /// <c>"[EXACT]"</c> when read from NVML/ADL; <c>"(EST)"</c> when derived
    /// from utilisation; <c>"N/A"</c> when completely unavailable.
    /// </summary>
    public string GpuPowerTag { get; init; } = "N/A";

    /// <summary>GPU core temperature in °C.</summary>
    public float? GpuCoreTempCelsius { get; init; }

    /// <summary>GPU hotspot / junction temperature in °C.</summary>
    public float? GpuHotspotTempCelsius { get; init; }

    /// <summary>Dynamic Board Power Limit if read from hardware.</summary>
    public float? GpuPowerLimitWatts { get; init; }

    // ── Memory ────────────────────────────────────────────────────────────────

    public float? MemoryUsedGb      { get; init; }
    public float? MemoryAvailableGb { get; init; }
    public float? MemoryTotalGb =>
        MemoryUsedGb.HasValue && MemoryAvailableGb.HasValue
            ? MemoryUsedGb.Value + MemoryAvailableGb.Value
            : null;

    // ── Total System Power ────────────────────────────────────────────────────

    /// <summary>
    /// Total system power draw in Watts, resolved through the 4-tier cascade.
    /// Always has a value (Tier-4 fallback guarantees a number even with no sensors).
    /// </summary>
    public float TotalSystemPowerWatts { get; init; }

    /// <summary>
    /// Which hardware path produced <see cref="TotalSystemPowerWatts"/>.
    /// </summary>
    public PowerSourceKind PowerSource { get; init; } = PowerSourceKind.CalculatedSum;

    /// <summary>
    /// Short display tag printed after the wattage value.
    /// Physical readings: <c>"[PHYSICAL SHUNT]"</c>
    /// Fallback estimate: <c>"(EST)"</c>
    /// </summary>
    public string PowerSourceTag { get; init; } = "(EST)";

    /// <summary>
    /// <c>false</c> for Tiers 1–3 (hardware shunt); <c>true</c> for Tier 4.
    /// Drives colour-coding in the console output.
    /// </summary>
    public bool IsEstimated { get; init; } = true;

    /// <summary>
    /// Base wattage added in Tier-4 component-sum mode to account for board
    /// VRMs, PCH, NVMe, fans, and peripherals not covered by RAPL or NVML.
    /// Default: 10 W.
    /// </summary>
    public float ComponentBaseOffsetWatts { get; init; } = 10f;

    // ── Convenience helpers ───────────────────────────────────────────────────

    /// <summary>Human-readable one-line summary of the total power + source.</summary>
    public string TotalPowerSummary =>
        IsEstimated
            ? $"{TotalSystemPowerWatts,7:F1} W (EST - Component Sum)"
            : $"{TotalSystemPowerWatts,7:F1} W {PowerSourceTag}";

    /// <summary>Formatted CPU power string with tag.</summary>
    public string CpuPowerDisplay =>
        CpuPackagePowerWatts.HasValue
            ? $"{CpuPackagePowerWatts.Value,6:F1} W [{CpuPowerTag}]"
            : $"   N/A [{CpuPowerTag}]";

    /// <summary>Formatted GPU power string with tag.</summary>
    public string GpuPowerDisplay =>
        GpuBoardPowerWatts.HasValue
            ? $"{GpuBoardPowerWatts.Value,6:F1} W [{GpuPowerTag}]"
            : $"   N/A [{GpuPowerTag}]";
}
