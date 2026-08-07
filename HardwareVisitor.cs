// HardwareVisitor.cs
// ───────────────────────────────────────────────────────────────────────────
// IVisitor implementation required by LibreHardwareMonitorLib's visitor
// pattern.  The library's Computer object traverses the hardware tree by
// calling Accept(visitor) on each IHardware node; the visitor must then
// call hardware.Update() and recursively visit sub-hardware (e.g. CPU
// dies, GPU sub-components) so that all sensor values are refreshed.
// ───────────────────────────────────────────────────────────────────────────

using LibreHardwareMonitor.Hardware;

namespace HWMonitor;

/// <summary>
/// Traverses the full LibreHardwareMonitor hardware tree on each poll cycle,
/// calling <see cref="IHardware.Update"/> on every node and sub-hardware node
/// to refresh all underlying sensor readings from the hardware drivers.
/// </summary>
/// <remarks>
/// LHM uses the Visitor pattern so that the library is not coupled to a
/// specific update strategy.  This implementation performs a depth-first
/// traversal: parent → sub-hardware children, matching what HWiNFO64 does
/// when it refreshes its sensor list.
/// </remarks>
public sealed class HardwareVisitor : IVisitor
{
    /// <summary>
    /// Called by LHM for each top-level <see cref="IComputer"/> node.
    /// We visit every hardware item it contains.
    /// </summary>
    public void VisitComputer(IComputer computer)
    {
        // Kick off the traversal for all top-level hardware items
        // (CPU, GPU, Memory controller, Motherboard, …).
        computer.Traverse(this);
    }

    /// <summary>
    /// Called by LHM for each <see cref="IHardware"/> node (CPU, GPU, …).
    /// We refresh the node then recurse into its sub-hardware components.
    /// </summary>
    public void VisitHardware(IHardware hardware)
    {
        // Pull fresh readings from the kernel driver / WMI / NVML / ADL.
        hardware.Update();

        // Recurse into sub-hardware (e.g. CPU dies, GPU memory sub-component).
        foreach (IHardware subHardware in hardware.SubHardware)
            subHardware.Accept(this);
    }

    /// <summary>
    /// Called by LHM for each <see cref="ISensor"/> node.
    /// No action needed here — sensor values are already updated by
    /// <see cref="VisitHardware"/> calling <see cref="IHardware.Update"/>.
    /// </summary>
    public void VisitSensor(ISensor sensor) { /* values refreshed by hardware.Update() */ }

    /// <summary>
    /// Called by LHM for each <see cref="IParameter"/> node.
    /// Parameters are static configuration values; we do not need to act on them.
    /// </summary>
    public void VisitParameter(IParameter parameter) { /* no-op */ }
}
