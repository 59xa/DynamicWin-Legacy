using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace DynamicWin.Utils
{
    internal class HardwareMonitor
    {
        private Timer timer;

        public static string usageString = " ";

        public static HardwareMonitor instance;

        private Computer computer;
        private float lastCpu = 0;
        private string lastRam = "";

        private readonly object _lock = new object();

        private IHardware? cpuHardware;
        private IHardware? memoryHardware;
        private ISensor? cpuLoadSensor;
        private ISensor? memoryUsedSensor;
        private ISensor? memoryAvailableSensor;

        private const int IntervalMs = 1500;

        public HardwareMonitor()
        {
            instance = this;

            computer = new Computer()
            {
                IsMemoryEnabled = true,
                IsCpuEnabled = true
            };

            try
            {
                computer.Open();
                InitializeSensors();
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine("HardwareMonitor: error opening Computer: " + ex);
#endif
            }

            timer = new Timer(TimerCallback, null, IntervalMs, IntervalMs);
        }

        private void InitializeSensors()
        {
            try
            {
                foreach (var hw in computer.Hardware)
                {
                    if (hw == null) continue;

                    if (hw.HardwareType == HardwareType.Cpu && cpuHardware == null)
                    {
                        cpuHardware = hw;

                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType == SensorType.Load && s.Name == "CPU Total")
                            {
                                cpuLoadSensor = s;
                                break;
                            }
                        }
                    }
                    else if (hw.HardwareType == HardwareType.Memory && memoryHardware == null)
                    {
                        memoryHardware = hw;
                        foreach (var s in hw.Sensors)
                        {
                            if (s.Name == "Memory Used")
                                memoryUsedSensor = s;
                            else if (s.Name == "Memory Available")
                                memoryAvailableSensor = s;
                        }
                    }

                    if (hw.SubHardware != null && hw.SubHardware.Length > 0)
                    {
                        foreach (var sub in hw.SubHardware)
                        {
                            if (sub == null) continue;

                            if (sub.HardwareType == HardwareType.Cpu && cpuHardware == null)
                            {
                                cpuHardware = sub;
                                foreach (var s in sub.Sensors)
                                {
                                    if (s.SensorType == SensorType.Load && s.Name == "CPU Total")
                                    {
                                        cpuLoadSensor = s;
                                        break;
                                    }
                                }
                            }
                            else if (sub.HardwareType == HardwareType.Memory && memoryHardware == null)
                            {
                                memoryHardware = sub;
                                foreach (var s in sub.Sensors)
                                {
                                    if (s.Name == "Memory Used")
                                        memoryUsedSensor = s;
                                    else if (s.Name == "Memory Available")
                                        memoryAvailableSensor = s;
                                }
                            }
                        }
                    }

                    if (cpuHardware != null && memoryHardware != null && cpuLoadSensor != null && memoryUsedSensor != null && memoryAvailableSensor != null)
                        break;
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine("HardwareMonitor: InitializeSensors exception: " + ex);
#endif
            }
        }

        private void TimerCallback(object? state)
        {
            if (!Monitor.TryEnter(_lock))
            {
#if DEBUG
                Debug.WriteLine("HardwareMonitor SKIPPED due to reentrant call");
#endif
                return;
            }

            try
            {
#if DEBUG
                Debug.WriteLine($"HardwareMonitor BEGIN");
#endif
                if (cpuHardware == null || memoryHardware == null || cpuLoadSensor == null || memoryUsedSensor == null || memoryAvailableSensor == null)
                {
                    InitializeSensors();
                }

                if (cpuHardware != null)
                {
                    try
                    {
                        cpuHardware.Update();
                        if (cpuLoadSensor != null && cpuLoadSensor.Value.HasValue)
                        {
                            lastCpu = Mathf.LimitDecimalPoints((float)cpuLoadSensor.Value.GetValueOrDefault(), 1);
                        }
                    }
                    catch { /* tolerate sensor update failures */ }
                }

                if (memoryHardware != null)
                {
                    try
                    {
                        memoryHardware.Update();

                        float memUsed = 0;
                        float memFree = 0;

                        if (memoryUsedSensor != null && memoryUsedSensor.Value.HasValue)
                            memUsed = Mathf.LimitDecimalPoints((float)memoryUsedSensor.Value.GetValueOrDefault(), 1);

                        if (memoryAvailableSensor != null && memoryAvailableSensor.Value.HasValue)
                            memFree = Mathf.LimitDecimalPoints((float)memoryAvailableSensor.Value.GetValueOrDefault(), 1);

                        lastRam = memUsed + "GB / " + Mathf.LimitDecimalPoints(memFree + memUsed, 0) + "GB";
                    }
                    catch { }
                }

                usageString = $"CPU: {lastCpu}%    RAM: {lastRam}";

#if DEBUG
                Debug.WriteLine($"HardwareMonitor END");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine(ex.ToString());
                Debug.WriteLine($"HardwareMonitor EXCEPTION");
#endif
            }
            finally
            {
                Monitor.Exit(_lock);
            }
        }

        public static void Stop()
        {
            try
            {
                if (instance != null)
                {
                    // stop and dispose timer
                    try
                    {
                        instance.timer?.Change(Timeout.Infinite, Timeout.Infinite);
                        instance.timer?.Dispose();
                    }
                    catch { }

                    if (instance.computer != null)
                    {
                        try
                        {
                            instance.computer.Close();
                        }
                        catch { }
                    }
                }
            }
            catch (Exception) { }
        }
    }
}
