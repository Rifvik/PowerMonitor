import sys
import os
import time
import ctypes
from ctypes import wintypes
import psutil
import pythoncom
import wmi

def is_admin():
    try:
        return ctypes.windll.shell32.IsUserAnAdmin()
    except:
        return False

if not is_admin():
    ctypes.windll.shell32.ShellExecuteW(None, "runas", sys.executable, " ".join(sys.argv), None, 1)
    sys.exit()

try:
    import pynvml
    HAS_NVML = True
except ImportError:
    HAS_NVML = False

from PySide6.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout, 
                               QHBoxLayout, QLabel, QSystemTrayIcon, QMenu, QStyle, QFrame, QSizePolicy)
from PySide6.QtGui import QIcon, QPainter, QColor, QFont, QPixmap, QAction, QPainterPath, QPen
from PySide6.QtCore import Qt, QThread, Signal, QTimer, QPointF

# Color Palette Definitions
COLOR_BG = "#E8F5E9"
COLOR_SECONDARY = "#A5D6A7"
COLOR_PRIMARY = "#66BB6A"
COLOR_ACTIVE = "#1B5E20"

def resource_path(relative_path):
    """ Get absolute path to resource, works for dev and for PyInstaller """
    try:
        base_path = sys._MEIPASS
    except Exception:
        base_path = os.path.abspath(".")
    return os.path.join(base_path, relative_path)

class SYSTEM_POWER_STATUS(ctypes.Structure):
    _fields_ = [
        ('ACLineStatus', wintypes.BYTE),
        ('BatteryFlag', wintypes.BYTE),
        ('BatteryLifePercent', wintypes.BYTE),
        ('SystemStatusFlag', wintypes.BYTE),
        ('BatteryLifeTime', wintypes.DWORD),
        ('BatteryFullLifeTime', wintypes.DWORD),
    ]

def get_battery_status_ctypes():
    status = SYSTEM_POWER_STATUS()
    if ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(status)):
        return status
    return None

class TelemetryWorker(QThread):
    metrics_updated = Signal(dict)

    def __init__(self):
        super().__init__()
        self.running = True
        self.has_lhm = False
        try:
            # Change working directory so .NET Fusion can find System.Memory.dll etc.
            orig_dir = os.path.abspath(".")
            base_path = sys._MEIPASS if hasattr(sys, '_MEIPASS') else orig_dir
            os.chdir(base_path)
            
            import clr
            clr.AddReference(os.path.join(base_path, "LibreHardwareMonitorLib.dll"))
            from LibreHardwareMonitor.Hardware import Computer
            self.computer = Computer()
            self.computer.IsCpuEnabled = True
            self.computer.IsBatteryEnabled = True
            self.computer.Open()
            self.has_lhm = True
            
            os.chdir(orig_dir)
        except Exception as e:
            print("Failed to init LHM:", e)
            try:
                os.chdir(orig_dir)
            except:
                pass

    def run(self):
        # Initialize COM for WMI in this thread
        pythoncom.CoInitialize()
        w = wmi.WMI()
        
        if HAS_NVML:
            try:
                pynvml.nvmlInit()
                self.gpu_handle = pynvml.nvmlDeviceGetHandleByIndex(0)
                name = pynvml.nvmlDeviceGetName(self.gpu_handle)
                self.gpu_name = name.decode('utf-8') if isinstance(name, bytes) else name
            except pynvml.NVMLError:
                self.gpu_handle = None
                self.gpu_name = "Unknown GPU"
        else:
            self.gpu_handle = None
            self.gpu_name = "Unknown GPU"

        self.cpu_name = "Unknown CPU"
        try:
            for processor in w.Win32_Processor():
                self.cpu_name = processor.Name.strip()
                break
        except Exception:
            pass

        while self.running:
            data = {
                "cpu_name": self.cpu_name,
                "gpu_name": self.gpu_name,
                "cpu_power": 0.0,
                "gpu_power": 0.0,
                "cpu_temp": 0.0,
                "gpu_temp": 0.0,
                "cpu_tdp": 0.0,
                "gpu_tdp": 0.0,
                "total_power": 0.0,
                "bat_charge_rate": 0.0,
                "bat_capacity": 0,
                "bat_design": 0,
                "bat_health": 0.0,
                "bat_percent": 0.0,
                "bat_status": "Unknown",
                "bat_time_left": "Unknown"
            }

            # 1. GPU Power
            if self.gpu_handle:
                try:
                    power_mw = pynvml.nvmlDeviceGetPowerUsage(self.gpu_handle)
                    data["gpu_power"] = power_mw / 1000.0
                    
                    data["gpu_temp"] = pynvml.nvmlDeviceGetTemperature(self.gpu_handle, pynvml.NVML_TEMPERATURE_GPU)
                    try:
                        tdp_mw = pynvml.nvmlDeviceGetEnforcedPowerLimit(self.gpu_handle)
                        data["gpu_tdp"] = tdp_mw / 1000.0
                    except pynvml.NVMLError:
                        try:
                            tdp_mw = pynvml.nvmlDeviceGetPowerManagementLimit(self.gpu_handle)
                            data["gpu_tdp"] = tdp_mw / 1000.0
                        except pynvml.NVMLError:
                            pass
                except pynvml.NVMLError:
                    pass

            # 2. CPU Power (Heuristic fallback, as WMI doesn't easily expose CPU power without LibreHardwareMonitor)
            cpu_percent = psutil.cpu_percent(interval=None)
            # Rough estimation: 10W idle + 45W max load (Example for a typical laptop CPU)
            data["cpu_power"] = 10.0 + (cpu_percent / 100.0) * 45.0 

            # 3. Battery via WMI & ctypes
            bat_status_ctypes = get_battery_status_ctypes()
            if bat_status_ctypes:
                data["bat_percent"] = float(bat_status_ctypes.BatteryLifePercent)
                if bat_status_ctypes.ACLineStatus == 1:
                    data["bat_status"] = "Plugged In"
                else:
                    data["bat_status"] = "Discharging"
                
                life_time = bat_status_ctypes.BatteryLifeTime
                if life_time != 0xFFFFFFFF: # max DWORD
                    hours = life_time // 3600
                    minutes = (life_time % 3600) // 60
                    data["bat_time_left"] = f"{hours}h {minutes}m"
                else:
                    data["bat_time_left"] = "Calculating..."

            try:
                batteries = w.Win32_Battery()
                if batteries:
                    b = batteries[0]
                    # Sometimes these fields might be None or missing depending on the driver
                    design_cap = getattr(b, 'DesignCapacity', None)
                    full_cap = getattr(b, 'FullChargeCapacity', None)
                    charge_rate = getattr(b, 'ChargeRate', 0) or 0
                    discharge_rate = getattr(b, 'DischargeRate', 0) or 0
                    
                    if design_cap:
                        data["bat_design"] = int(design_cap)
                    if full_cap:
                        data["bat_capacity"] = int(full_cap)
                    
                    if data["bat_status"] == "Discharging" and discharge_rate > 0:
                        data["bat_charge_rate"] = float(discharge_rate) / 1000.0 # W
                    elif charge_rate > 0:
                        data["bat_charge_rate"] = float(charge_rate) / 1000.0 # W

            except Exception as e:
                print(f"WMI Battery error: {e}")
            
            # LHM Overrides
            if self.has_lhm:
                try:
                    for hw in self.computer.Hardware:
                        hw.Update()
                        hw_type = hw.HardwareType.ToString()
                        if hw_type == "Cpu":
                            for sensor in hw.Sensors:
                                s_type = sensor.SensorType.ToString()
                                name = sensor.Name
                                if sensor.Value is not None and sensor.Value > 0:
                                    if s_type == "Temperature" and ("Package" in name or "Core Average" in name or "Core Max" in name):
                                        data["cpu_temp"] = sensor.Value
                                    elif s_type == "Power" and "Package" in name:
                                        data["cpu_tdp"] = sensor.Value
                                        data["cpu_power"] = sensor.Value
                        elif hw_type == "Battery":
                            for sensor in hw.Sensors:
                                s_type = sensor.SensorType.ToString()
                                name = sensor.Name
                                if sensor.Value is not None and sensor.Value > 0:
                                    if s_type == "Power" and "Charge" in name:
                                        data["bat_charge_rate"] = sensor.Value
                                    elif s_type == "Power" and "Discharge" in name:
                                        data["bat_charge_rate"] = sensor.Value
                                    elif s_type == "Energy" and "Designed" in name:
                                        data["bat_design"] = int(sensor.Value)
                                    elif s_type == "Energy" and "Full" in name:
                                        data["bat_capacity"] = int(sensor.Value)
                                    elif s_type == "Level" and "Charge" in name:
                                        data["bat_percent"] = sensor.Value
                except Exception as e:
                    print("LHM update error:", e)

            # Recalculate battery health just in case LHM updated the capacities
            if data["bat_design"] > 0 and data["bat_capacity"] > 0:
                data["bat_health"] = (data["bat_capacity"] / data["bat_design"]) * 100.0

            # Total Power Estimation
            # If we have discharge rate in Watts (some systems report mW, some W), we can use it.
            # But let's sum up our knowns.
            data["total_power"] = data["cpu_power"] + data["gpu_power"] + 5.0 # +5W for rest of system
            
            # If on battery and we have a valid discharge rate that looks like system power, use it
            if data["bat_status"] == "Discharging" and data["bat_charge_rate"] > 0:
                # Some batteries report discharge rate in mW
                data["total_power"] = data["bat_charge_rate"]

            self.metrics_updated.emit(data)
            time.sleep(1.0)
            
        pythoncom.CoUninitialize()

    def stop(self):
        self.running = False
        self.wait()

class SparklineWidget(QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.history = [0.0] * 60
        self.max_val = 100.0
        self.setMinimumHeight(60)

    def add_value(self, val):
        self.history.pop(0)
        self.history.append(val)
        if val > self.max_val:
            self.max_val = val
        self.update()

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        
        rect = self.rect()
        width = rect.width()
        height = rect.height()

        # Background
        painter.fillRect(rect, QColor(COLOR_BG))
        
        # Draw line
        if not self.history:
            return
            
        path = QPainterPath()
        step_x = width / (len(self.history) - 1)
        
        # Calculate Y scaling
        eff_max = max(self.max_val, 10.0) # avoid division by zero
        
        for i, val in enumerate(self.history):
            x = i * step_x
            y = height - (val / eff_max) * height
            if i == 0:
                path.moveTo(x, y)
            else:
                path.lineTo(x, y)
                
        pen = QPen(QColor(COLOR_ACTIVE))
        pen.setWidth(2)
        painter.setPen(pen)
        painter.drawPath(path)
        
        # Draw fill
        path.lineTo(width, height)
        path.lineTo(0, height)
        path.closeSubpath()
        fill_color = QColor(COLOR_ACTIVE)
        fill_color.setAlpha(40)
        painter.fillPath(path, fill_color)

class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Power Monitor")
        self.setWindowIcon(QIcon(resource_path("icon.png")))
        self.resize(350, 450)
        self.setStyleSheet(f"QMainWindow {{ background-color: {COLOR_BG}; }}")
        
        self.tray_icon = None
        self.setup_ui()
        self.setup_tray()
        
        self.worker = TelemetryWorker()
        self.worker.metrics_updated.connect(self.update_ui)
        self.worker.start()

    def setup_ui(self):
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        
        layout = QVBoxLayout(central_widget)
        layout.setContentsMargins(20, 20, 20, 20)
        layout.setSpacing(15)
        
        # Title
        title = QLabel("System Power Monitor")
        title.setFont(QFont("Segoe UI", 16, QFont.Bold))
        title.setStyleSheet(f"color: {COLOR_ACTIVE};")
        title.setAlignment(Qt.AlignCenter)
        layout.addWidget(title)
        
        # Main Total Power
        self.lbl_total_power = QLabel("0 W")
        self.lbl_total_power.setFont(QFont("Segoe UI", 32, QFont.Bold))
        self.lbl_total_power.setStyleSheet(f"color: {COLOR_ACTIVE};")
        self.lbl_total_power.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.lbl_total_power)
        
        # Sparkline Graph
        self.sparkline = SparklineWidget()
        layout.addWidget(self.sparkline)
        
        # Metrics Group
        metrics_frame = QFrame()
        metrics_frame.setStyleSheet(f"QFrame {{ background-color: {COLOR_SECONDARY}; border-radius: 8px; }}")
        metrics_layout = QVBoxLayout(metrics_frame)
        
        self.lbl_cpu = self.create_metric_label("CPU Power:")
        self.lbl_cpu.setWordWrap(True)
        self.lbl_gpu = self.create_metric_label("GPU Power:")
        self.lbl_gpu.setWordWrap(True)
        metrics_layout.addWidget(self.lbl_cpu)
        metrics_layout.addWidget(self.lbl_gpu)
        layout.addWidget(metrics_frame)
        
        # Battery Group
        bat_frame = QFrame()
        bat_frame.setStyleSheet(f"QFrame {{ background-color: {COLOR_SECONDARY}; border-radius: 8px; }}")
        bat_layout = QVBoxLayout(bat_frame)
        
        self.lbl_bat_status = self.create_metric_label("Battery Status:")
        self.lbl_bat_percent = self.create_metric_label("Remaining:")
        self.lbl_bat_health = self.create_metric_label("Health:")
        self.lbl_bat_rate = self.create_metric_label("Rate:")
        
        bat_layout.addWidget(self.lbl_bat_status)
        bat_layout.addWidget(self.lbl_bat_percent)
        bat_layout.addWidget(self.lbl_bat_health)
        bat_layout.addWidget(self.lbl_bat_rate)
        
        layout.addWidget(bat_frame)
        layout.addStretch()

    def create_metric_label(self, text):
        lbl = QLabel(f"{text} --")
        lbl.setFont(QFont("Segoe UI", 10, QFont.Bold))
        lbl.setStyleSheet(f"color: {COLOR_ACTIVE};")
        return lbl

    def setup_tray(self):
        self.tray_icon = QSystemTrayIcon(self)
        dummy_data = {"total_power": 0.0, "cpu_name": "CPU", "gpu_name": "GPU", "cpu_power": 0.0, "gpu_power": 0.0}
        self.update_tray_icon(dummy_data)
        
        menu = QMenu()
        menu.setStyleSheet(f"QMenu {{ background-color: {COLOR_BG}; color: {COLOR_ACTIVE}; }}")
        
        show_action = QAction("Show Dashboard", self)
        show_action.triggered.connect(self.showNormal)
        menu.addAction(show_action)
        
        settings_action = QAction("Settings", self)
        # Placeholder for settings
        menu.addAction(settings_action)
        
        exit_action = QAction("Exit", self)
        exit_action.triggered.connect(self.close_app)
        menu.addAction(exit_action)
        
        self.tray_icon.setContextMenu(menu)
        self.tray_icon.activated.connect(self.tray_icon_activated)
        self.tray_icon.show()

    def update_tray_icon(self, data):
        total_p = data.get("total_power", 0.0)
        
        pixmap = QPixmap(32, 32)
        pixmap.fill(Qt.transparent)
        
        painter = QPainter(pixmap)
        painter.setRenderHint(QPainter.TextAntialiasing)
        
        painter.setPen(Qt.white)
        text_val = f"{total_p:.1f}"
        
        # Dynamically adjust font size to maximize visibility
        point_size = 12 if len(text_val) <= 4 else 10
        font = QFont("Segoe UI", point_size, QFont.Bold)
        
        painter.setFont(font)
        painter.drawText(pixmap.rect(), Qt.AlignCenter, text_val)
        painter.end()
        
        self.tray_icon.setIcon(QIcon(pixmap))
        
        cpu_str = f"{data.get('cpu_name', 'CPU')}: {data.get('cpu_power', 0.0):.1f}W"
        if HAS_NVML and data.get('gpu_power', 0.0) > 0:
            gpu_str = f"{data.get('gpu_name', 'GPU')}: {data.get('gpu_power', 0.0):.1f}W | {data.get('gpu_temp', 0)}°C"
        else:
            gpu_str = f"{data.get('gpu_name', 'GPU')}: N/A"
            
        self.tray_icon.setToolTip(f"System Power: {total_p:.1f}W\n{cpu_str}\n{gpu_str}")

    def tray_icon_activated(self, reason):
        if reason == QSystemTrayIcon.DoubleClick:
            if self.isVisible():
                self.hide()
            else:
                self.showNormal()
                self.activateWindow()

    def update_ui(self, data):
        total_p = data["total_power"]
        
        self.lbl_total_power.setText(f"{total_p:.1f} W")
        self.sparkline.add_value(total_p)
        
        cpu_temp_str = f"{data['cpu_temp']:.0f} °C" if data['cpu_temp'] > 0 else "N/A"
        cpu_tdp_str = f"{data['cpu_tdp']:.1f} W" if data['cpu_tdp'] > 0 else "N/A"
        self.lbl_cpu.setText(f"CPU ({data['cpu_name']}):\nPower: {data['cpu_power']:.1f} W\nTemp: {cpu_temp_str}\nTDP: {cpu_tdp_str}")
        
        if HAS_NVML and data['gpu_power'] > 0:
            gpu_tdp_str = f"{data['gpu_tdp']:.1f} W" if data['gpu_tdp'] > 0 else "N/A"
            self.lbl_gpu.setText(f"GPU ({data['gpu_name']}):\nPower: {data['gpu_power']:.1f} W\nTemp: {data['gpu_temp']} °C\nTDP: {gpu_tdp_str}")
        else:
            self.lbl_gpu.setText(f"GPU ({data['gpu_name']}):\nN/A")
            
        self.lbl_bat_status.setText(f"Battery Status: {data['bat_status']}")
        self.lbl_bat_percent.setText(f"Remaining: {data['bat_percent']:.0f}% ({data['bat_time_left']})")
        if data['bat_health'] > 0:
            self.lbl_bat_health.setText(f"Health: {data['bat_health']:.1f}% ({data['bat_capacity']} / {data['bat_design']} mWh)")
        else:
            self.lbl_bat_health.setText("Health: N/A")
            
        self.lbl_bat_rate.setText(f"Rate: {data['bat_charge_rate']:.1f} W")
        
        self.update_tray_icon(data)

    def closeEvent(self, event):
        # Override close to minimize to tray
        event.ignore()
        self.hide()
        self.tray_icon.showMessage("Power Monitor", "Application minimized to tray.", QSystemTrayIcon.Information, 2000)

    def close_app(self):
        self.worker.stop()
        QApplication.quit()

if __name__ == "__main__":
    app = QApplication(sys.argv)
    
    # Don't quit when the last window is closed (since we run in tray)
    app.setQuitOnLastWindowClosed(False)
    
    window = MainWindow()
    window.show()
    sys.exit(app.exec())
