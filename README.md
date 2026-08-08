<div align="center">
  <h1>⚡ PowerMonitor</h1>
  <p><b>A modern, Ring-0 hardware telemetry dashboard for exact CPU and GPU power tracking.</b></p>
  
  <img src="assets/screenshot.png" alt="PowerMonitor Dashboard" width="100%">

  <br />
</div>

## 📖 Overview

**PowerMonitor** is a standalone Windows hardware telemetry dashboard that bridges the gap between low-level kernel sensor readings and beautiful, modern web design. Built using a hybrid **C# and WebView2** architecture, it provides exact, real-time power tracking without the visual clutter of traditional monitoring tools.

## ✨ Key Features

- 🔋 **Real-Time Telemetry:** Polls hardware sensors exactly matching HWiNFO64 readings every 500ms using `LibreHardwareMonitorLib`.
- 🎯 **Dynamic TDP Detection:** Actively queries CPU PL1/PL2 limits and uses `nvidia-smi` to fetch the true maximum GPU board power limit (including Dynamic Boost), adapting automatically to your specific hardware.
- 🎨 **Modern Interface:** A pixel-perfect, responsive frontend dashboard built with HTML, CSS Flexbox, and Chart.js. The UI smoothly scales and reorganizes itself when you resize the window.
- 💸 **Live Cost Tracker:** Instantly calculates your exact electricity cost per hour, per day, and per session based on your real-time cumulative wattage and local energy rates.

## 🏗️ Architecture

PowerMonitor uses a split "Best of Both Worlds" hybrid architecture:

> **1. The Engine (C# / .NET 7):** A lightweight C# service runs with Administrator privileges to safely load Ring-0 kernel drivers (`WinRing0`). This allows it to read exact power states directly from Intel/AMD Model-Specific Registers (MSRs) and NVIDIA's NVML backend.<br><br>
> **2. The Glass (Edge WebView2):** The gorgeous user interface is rendered using standard web technologies injected seamlessly into a native Windows Form. Telemetry payloads are beamed from the C# kernel space to the JavaScript UI thread twice every second.

## 🚀 Getting Started

1. Download the latest self-contained `.zip` from the [Releases](../../releases/latest) page.
2. Extract the folder and run `HWMonitor.exe`.
3. **Note:** The application will prompt for **Administrator Privileges**. This is strictly required by the underlying driver to access secure hardware telemetry registers.

---
<div align="center">
  <i>Built for performance enthusiasts and hardware tinkerers.</i>
</div>
