using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace DynamicWin.Utils
{

    public class DeviceUsageChecker
    {
        private static readonly string MicrophoneSubkey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        private static readonly string WebcamSubkey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
        private static readonly string TimestampValueName = "LastUsedTimeStop";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMilliseconds(750);
        private static readonly object cacheLock = new object();
        private static DateTime lastMicrophoneCheck = DateTime.MinValue;
        private static DateTime lastWebcamCheck = DateTime.MinValue;
        private static bool cachedMicrophoneInUse;
        private static bool cachedWebcamInUse;

        public static bool IsMicrophoneInUse()
        {
            return IsDeviceInUseCached(MicrophoneSubkey, ref lastMicrophoneCheck, ref cachedMicrophoneInUse);
        }

        public static bool IsWebcamInUse()
        {
            return IsDeviceInUseCached(WebcamSubkey, ref lastWebcamCheck, ref cachedWebcamInUse);
        }

        private static bool IsDeviceInUseCached(string subkey, ref DateTime lastCheck, ref bool cachedValue)
        {
            var now = DateTime.UtcNow;
            lock (cacheLock)
            {
                if ((now - lastCheck) < CacheDuration)
                    return cachedValue;

                cachedValue = IsDeviceInUse(subkey);
                lastCheck = now;
                return cachedValue;
            }
        }

        private static bool IsDeviceInUse(string subkey)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(subkey))
                {
                    if (key == null) return false;

                    foreach (string subkeyName in key.GetSubKeyNames())
                    {
                        string subkeyPath = $@"{subkey}\{subkeyName}";
                        if (subkeyName.Equals("NonPackaged"))
                        {
                            using (RegistryKey nonPackagedKey = Registry.CurrentUser.OpenSubKey(subkeyPath))
                            {
                                if (nonPackagedKey == null) continue;

                                foreach (string npSubkeyName in nonPackagedKey.GetSubKeyNames())
                                {
                                    string npSubkeyPath = $@"{subkeyPath}\{npSubkeyName}";
                                    if (GetSubkeyTimestamp(npSubkeyPath) == 0)
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (GetSubkeyTimestamp(subkeyPath) == 0)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
            return false;
        }

        private static long GetSubkeyTimestamp(string subkeyPath)
        {
            try
            {
                using (RegistryKey subkey = Registry.CurrentUser.OpenSubKey(subkeyPath))
                {
                    if (subkey == null) return -1;
                    object value = subkey.GetValue(TimestampValueName);
                    if (value != null && long.TryParse(value.ToString(), out long timestamp))
                    {
                        return timestamp;
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
            return -1;
        }
    }

}
