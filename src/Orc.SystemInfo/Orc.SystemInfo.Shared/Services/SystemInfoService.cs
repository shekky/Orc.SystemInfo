﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="SystemInfoService.cs" company="Wild Gums">
//   Copyright (c) 2008 - 2015 Wild Gums. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------


namespace Orc.SystemInfo
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Management;
    using System.Text.RegularExpressions;
    using Catel;
    using Catel.Logging;
    using MethodTimer;
    using Microsoft.Win32;
    using Win32;

    public class SystemInfoService : ISystemInfoService
    {
        private static readonly ILog Log = LogManager.GetCurrentClassLogger();

        private readonly IWindowsManagementInformationService _windowsManagementInformationService;

        public SystemInfoService(IWindowsManagementInformationService windowsManagementInformationService)
        {
            Argument.IsNotNull(() => windowsManagementInformationService);

            _windowsManagementInformationService = windowsManagementInformationService;
        }

        #region ISystemInfoService Members
        [Time]
        public IEnumerable<SystemInfoElement> GetSystemInfo()
        {
            Log.Debug("Retrieving system info");

            var items = new List<SystemInfoElement>();

            items.Add(new SystemInfoElement("User name", Environment.UserName));
            items.Add(new SystemInfoElement("User domain name", Environment.UserDomainName));
            items.Add(new SystemInfoElement("Machine name", Environment.MachineName));
            items.Add(new SystemInfoElement("OS version", Environment.OSVersion.ToString()));
            items.Add(new SystemInfoElement("Version", Environment.Version.ToString()));

            try
            {
                var wmi = new ManagementObjectSearcher("select * from Win32_OperatingSystem")
                    .Get()
                    .Cast<ManagementObject>()
                    .First();

                items.Add(new SystemInfoElement("OS name", wmi.GetValue("Caption")));
                items.Add(new SystemInfoElement("Architecture", wmi.GetValue("OSArchitecture")));
                items.Add(new SystemInfoElement("ProcessorId", wmi.GetValue("ProcessorId")));
                items.Add(new SystemInfoElement("Build", wmi.GetValue("BuildNumber")));
                items.Add(new SystemInfoElement("MaxProcessRAM", (wmi.GetLongValue("MaxProcessMemorySize")).ToReadableSize()));
            }
            catch (Exception ex)
            {
                items.Add(new SystemInfoElement("OS info", "n/a, please contact support"));
                Log.Warning(ex, "Failed to retrieve OS information");
            }

            var memStatus = new Kernel32.MEMORYSTATUSEX();
            if (Kernel32.GlobalMemoryStatusEx(memStatus))
            {
                items.Add(new SystemInfoElement("Total memory", memStatus.ullTotalPhys.ToReadableSize()));
                items.Add(new SystemInfoElement("Available memory", memStatus.ullAvailPhys.ToReadableSize()));
            }

            try
            {
                var cpu = new ManagementObjectSearcher("select * from Win32_Processor")
                    .Get()
                    .Cast<ManagementObject>()
                    .First();

                items.Add(new SystemInfoElement("CPU name", cpu.GetValue("Name")));
                items.Add(new SystemInfoElement("Description", cpu.GetValue("Caption")));
                items.Add(new SystemInfoElement("Address width", cpu.GetValue("AddressWidth")));
                items.Add(new SystemInfoElement("Data width", cpu.GetValue("DataWidth")));
                items.Add(new SystemInfoElement("SpeedMHz", cpu.GetValue("MaxClockSpeed")));
                items.Add(new SystemInfoElement("BusSpeedMHz", cpu.GetValue("ExtClock")));
                items.Add(new SystemInfoElement("Number of cores", cpu.GetValue("NumberOfCores")));
                items.Add(new SystemInfoElement("Number of logical processors", cpu.GetValue("NumberOfLogicalProcessors")));
            }
            catch (Exception ex)
            {
                items.Add(new SystemInfoElement("CPU info", "n/a, please contact support"));
                Log.Warning(ex, "Failed to retrieve CPU information");
            }

            items.Add(new SystemInfoElement("System up time", GetSystemUpTime()));
            items.Add(new SystemInfoElement("Application up time", (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString()));

            items.Add(new SystemInfoElement("Current culture", CultureInfo.CurrentCulture.ToString()));

            items.Add(new SystemInfoElement(".Net Framework versions", string.Empty));
            foreach (var pair in GetNetFrameworkVersions())
            {
                items.Add(new SystemInfoElement(string.Empty, pair));
            }

            Log.Debug("Retrieved system info");

            return items;
        }
        #endregion

        #region Methods
        private static string GetSystemUpTime()
        {
            try
            {
                var upTime = new PerformanceCounter("System", "System Up Time");
                upTime.NextValue();
                return TimeSpan.FromSeconds(upTime.NextValue()).ToString();
            }
            catch (Exception)
            {
                return "n/a";
            }
        }

        private static IEnumerable<string> GetNetFrameworkVersions()
        {
            var versions = new List<string>();

            try
            {
                using (var ndpKey = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, string.Empty)
                    .OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\"))
                {
                    foreach (var versionKeyName in ndpKey.GetSubKeyNames().Where(x => x.StartsWith("v")))
                    {
                        using (var versionKey = ndpKey.OpenSubKey(versionKeyName))
                        {
                            foreach (var fullName in BuildFrameworkNamesRecursively(versionKey, versionKeyName, topLevel: true))
                            {
                                if (!string.IsNullOrWhiteSpace(fullName))
                                {
                                    versions.Add(fullName);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to get .net framework versions");
            }

            return versions;
        }

        private static IEnumerable<string> BuildFrameworkNamesRecursively(RegistryKey registryKey, string name, string topLevelSp = "0", bool topLevel = false)
        {
            Argument.IsNotNull(() => registryKey);
            Argument.IsNotNullOrEmpty(() => name);
            Argument.IsNotNullOrEmpty(() => topLevelSp);

            if (registryKey == null)
            {
                yield break;
            }

            var fullVersion = string.Empty;

            var version = (string)registryKey.GetValue("Version", string.Empty);
            var sp = registryKey.GetValue("SP", "0").ToString();
            var install = registryKey.GetValue("Install", string.Empty).ToString();

            if (string.Equals(sp, "0"))
            {
                sp = topLevelSp;
            }

            if (!string.Equals(sp, "0") && string.Equals(install, "1"))
            {
                fullVersion = string.Format("{0} {1} SP{2}", name, version, sp);
            }
            else if (string.Equals(install, "1"))
            {
                fullVersion = string.Format("{0} {1}", name, version);
            }

            var topLevelInitialized = !topLevel || !string.IsNullOrEmpty(fullVersion);

            var subnamesCount = 0;
            foreach (var subKeyName in registryKey.GetSubKeyNames().Where(x => Regex.IsMatch(x, @"^\d{4}$|^Client$|^Full$")))
            {
                using (var subKey = registryKey.OpenSubKey(subKeyName))
                {
                    foreach (var subName in BuildFrameworkNamesRecursively(subKey, string.Format("{0} {1}", name, subKeyName), sp, !topLevelInitialized))
                    {
                        yield return subName;
                        subnamesCount++;
                    }
                }
            }

            if (subnamesCount == 0)
            {
                yield return fullVersion;
            }
        }
        #endregion
    }
}