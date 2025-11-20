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
        System.Timers.Timer timer;

        public static string usageString = " ";

        public static HardwareMonitor instance;

        Computer computer;
        float lastCpu = 0;
        string lastRam = "";

        // Use a lock to prevent reentrancy rather than a busy boolean
        private readonly object _lock = new object();

        public HardwareMonitor()
        {
            instance = this;

            timer = new System.Timers.Timer();
            timer.Interval = 1000;
            timer.Elapsed += Timer_Elapsed;
            timer.AutoReset = true;

            computer = new Computer()
            {
                IsMemoryEnabled = true,
                IsCpuEnabled = true // Enable CPU monitoring
            };

            // Open once and keep the computer object open during lifetime (much cheaper than Open/Close each tick)
            try
            {
                computer.Open();
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine("HardwareMonitor: error opening Computer: " + ex);
#endif
            }

            timer.Start();
        }

        private void Timer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            // Try to acquire lock quickly; if busy, skip this tick
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
                foreach (var hardware in computer.Hardware)
                {
                    if (hardware == null) continue;

                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        hardware.Update();
                        foreach (var sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Load && sensor.Name == "CPU Total")
                            {
                                lastCpu = Mathf.LimitDecimalPoints((float)sensor.Value.GetValueOrDefault(), 1);
                                break;
                            }
                        }
                    }
                    else if (hardware.HardwareType == HardwareType.Memory)
                    {
                        hardware.Update();

                        float memUsed = 0;
                        float memFree = 0;

                        foreach (var sensor in hardware.Sensors)
                        {
                            if (sensor.Name == "Memory Used")
                            {
                                memUsed = Mathf.LimitDecimalPoints((float)sensor.Value.GetValueOrDefault(), 1);
                            }
                            else if (sensor.Name == "Memory Available")
                            {
                                memFree = Mathf.LimitDecimalPoints((float)sensor.Value.GetValueOrDefault(), 1);
                            }
                        }

                        lastRam = memUsed + "GB / " + Mathf.LimitDecimalPoints(memFree + memUsed, 0) + "GB";
                    }
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
                if (instance?.computer != null)
                {
                    instance.computer.Close();
                }
            }
            catch (Exception) { }
        }
    }
}
