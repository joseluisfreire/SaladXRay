#nullable disable
#pragma warning disable CA1416

using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Management;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace SaladXRayPanel
{
    /// <summary>
    /// Módulo isolado: TUDO relacionado à VM (WSL/vmmem) do Salad.
    /// Status, uptime (via WMI CreationDate, persiste entre aberturas do app),
    /// RAM, CPU, disco (VHDX) e rede virtual (vEthernet).
    /// </summary>
    public static class VmTelemetryService
    {
        // ======================= STATE =======================

        public static DateTime WslStartTime = DateTime.MinValue;
        public static string WslStatusStr = "Pending...";
        public static string DistroName = "salad-enterprise-linux";
        public static string RamUsage = "Awaiting WSL...";
        public static string WslDiskSize = "Computing...";
        public static string VNetStats = "Tx: 0 KB/s | Rx: 0 KB/s";
        public static string VNetTxStr = "Tx: 0 KB/s";
        public static string VNetRxStr = "Rx: 0 KB/s";        public static double WslRamMB = 0.0;
        public static double WslCpuUsage = 0.0; // soma normalizada (vmmem% + wsl%) / núcleos
        public static string WorkTime = "Computing...";

        // Trava central: só true depois que "wsl -l -v" confirma a distro certa Running.
        private static bool _saladDistroConfirmedRunning = false;

        // Histórico de CPU por processo (p/ cálculo de delta)
        private static readonly Dictionary<string, (ulong PercentProcessorTime, ulong TimestampSys100NS)> _cpuHistory = new();

        // VHDX (disco virtual)
        public static readonly string VhdxPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Salad", "wsl", "ext4.vhdx");

        public static long LastVhdxSize = -1;
        public static DateTime LastVhdxCheckTime = DateTime.MinValue;
        public static DateTime LastVhdxWriteTime = DateTime.MinValue;
        public static DateTime LastDiskWriteAt = DateTime.MinValue;
        public static bool IsDiskLedActive = false;
        public static double VhdxGrowthRateMBs = 0;
        public static DateTime LastVhdxIoCheck = DateTime.MinValue;

        // Rede virtual (vEthernet)
        public static long LastVNetRx = 0, LastVNetTx = 0;
        public static DateTime LastVNetTime = DateTime.MinValue;
        public static double VNetTotalRxGB = 0, VNetTotalTxGB = 0;
        public static double CurrentVmDownKbps = 0;

        // LEDs de atividade (seta pra cima = upload/Tx, pra baixo = download/Rx)
        public static DateTime LastRxActiveAt = DateTime.MinValue;
        public static DateTime LastTxActiveAt = DateTime.MinValue;
        public static bool IsRxLedActive => (DateTime.Now - LastRxActiveAt).TotalMilliseconds < 700;
        public static bool IsTxLedActive => (DateTime.Now - LastTxActiveAt).TotalMilliseconds < 700;

        // ======================= STATUS (CONFIRMA DISTRO) =======================

        public static void UpdateWslDistroStatus()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    Arguments = "-l -v",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.Unicode
                };

                using var proc = Process.Start(psi);
                if (proc == null) return;

                string output = proc.StandardOutput.ReadToEnd().Replace("\0", "");
                proc.WaitForExit(3000);

                var match = Regex.Match(output, @"salad-enterprise-linux\s+([A-Za-z]+)", RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    DistroName = "salad-enterprise-linux";
                    string state = match.Groups[1].Value;

                    if (state.Contains("Running"))
                    {
                        WslStatusStr = "Running (Active)";
                        _saladDistroConfirmedRunning = true;
                    }
                    else
                    {
                        WslStatusStr = "STOPPED (Offline)";
                        _saladDistroConfirmedRunning = false;
                    }
                }
                else
                {
                    // Distro não listada -> não instalada ou removida
                    WslStatusStr = "STOPPED (Offline)";
                    _saladDistroConfirmedRunning = false;
                }
            }
            catch
            {
                WslStatusStr = "Error reading WSL";
                _saladDistroConfirmedRunning = false;
            }
        }

        // ======================= UPTIME + RAM + CPU (SÓ SE DISTRO CONFIRMADA) =======================

        public static void UpdateWSLData()
        {
            // 1. Sempre confirma a distro primeiro (chamada única "wsl -l -v")
            UpdateWslDistroStatus();

            // 2. Sem a distro Salad confirmada Running, zera tudo e sai.
            //    Evita contar vmmem/CPU/RAM de outra distro WSL (Ubuntu, Debian etc).
            if (!_saladDistroConfirmedRunning)
            {
                WslStartTime = DateTime.MinValue;
                WslRamMB = 0;
                WslCpuUsage = 0;
                RamUsage = "Offline";
                _cpuHistory.Clear();
                return;
            }

            // 3. Uptime REAL via WMI CreationDate do processo vmmem.
            //    Isso é dado persistente do Windows: se o app abrir com a distro
            //    já rodando há 3h, o uptime já mostra 3h (não zera por sessão).
            try
            {
                using var searcherVm = new ManagementObjectSearcher(
                    "SELECT CreationDate FROM Win32_Process WHERE Name LIKE 'vmmem%'");

                DateTime? oldestVmTime = null;
                using var collectionVm = searcherVm.Get();
                foreach (ManagementObject obj in collectionVm)
                {
                    using (obj)
                    {
                        string wmiDate = obj["CreationDate"]?.ToString();
                        if (!string.IsNullOrEmpty(wmiDate))
                        {
                            DateTime dt = ManagementDateTimeConverter.ToDateTime(wmiDate);
                            if (oldestVmTime == null || dt < oldestVmTime.Value)
                                oldestVmTime = dt;
                        }
                    }
                }

                WslStartTime = oldestVmTime ?? DateTime.MinValue;
            }
            catch
            {
                WslStartTime = DateTime.MinValue;
            }

            // 4. RAM + CPU via Win32_PerfRawData_PerfProc_Process (vmmem% + wsl%)
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PercentProcessorTime, Timestamp_Sys100NS, WorkingSet FROM Win32_PerfRawData_PerfProc_Process WHERE Name LIKE 'vmmem%' OR Name LIKE 'wsl%'");

                double totalCpuAccumulated = 0;
                ulong totalWorkingSetBytes = 0;
                int cores = Environment.ProcessorCount;
                var activeNames = new HashSet<string>();
                bool anyFound = false;

                using var collection = searcher.Get();
                foreach (ManagementObject obj in collection)
                {
                    string name = obj["Name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    anyFound = true;
                    activeNames.Add(name);

                    ulong currentRawCpu = Convert.ToUInt64(obj["PercentProcessorTime"] ?? 0);
                    ulong currentTimestamp = Convert.ToUInt64(obj["Timestamp_Sys100NS"] ?? 0);
                    ulong workingSet = Convert.ToUInt64(obj["WorkingSet"] ?? 0);

                    totalWorkingSetBytes += workingSet;

                    if (_cpuHistory.TryGetValue(name, out var prev))
                    {
                        long deltaCpu = (long)currentRawCpu - (long)prev.PercentProcessorTime;
                        long deltaTicks = (long)currentTimestamp - (long)prev.TimestampSys100NS;

                        if (deltaTicks > 0 && deltaCpu >= 0)
                        {
                            double instancePct = ((double)deltaCpu / deltaTicks) * 100.0;
                            totalCpuAccumulated += instancePct;
                        }
                    }

                    _cpuHistory[name] = (currentRawCpu, currentTimestamp);
                }

                if (!anyFound)
                {
                    WslRamMB = 0;
                    WslCpuUsage = 0;
                    RamUsage = "Awaiting WSL...";
                    _cpuHistory.Clear();
                    return;
                }

                var staleKeys = _cpuHistory.Keys.Where(k => !activeNames.Contains(k)).ToList();
                foreach (var key in staleKeys) _cpuHistory.Remove(key);

                WslRamMB = totalWorkingSetBytes / 1048576.0;
                RamUsage = WslRamMB > 0 ? $"{WslRamMB:N1} MB" : "Awaiting WSL...";
                WslCpuUsage = cores > 0 ? Math.Clamp(totalCpuAccumulated / cores, 0, 100) : 0;
            }
            catch
            {
                WslRamMB = 0;
                WslCpuUsage = 0;
                RamUsage = "Error reading CPU/RAM";
            }
        }

        public static void CalculateUptime()
        {
            WorkTime = (WslStartTime != DateTime.MinValue && WslStatusStr.Contains("Running"))
                ? FormatUptime(DateTime.Now - WslStartTime)
                : "[grey]Offline[/]";
        }

        public static bool IsRunning => WslStatusStr.Contains("Running");

        // ======================= DISCO VIRTUAL (VHDX) =======================

        public static void UpdateVhdxDiskUsage()
        {
            if (!WslStatusStr.Contains("Running"))
            {
                IsDiskLedActive = false;
                VhdxGrowthRateMBs = 0;
                return;
            }

            if ((DateTime.Now - LastVhdxIoCheck).TotalSeconds < 4)
            {
                if (IsDiskLedActive && (DateTime.Now - LastDiskWriteAt).TotalMilliseconds > 600)
                    IsDiskLedActive = false;
                return;
            }

            LastVhdxIoCheck = DateTime.Now;

            try
            {
                var info = new FileInfo(VhdxPath);
                if (!info.Exists) return;

                info.Refresh();
                long currentSize = info.Length;
                DateTime writeTime = info.LastWriteTimeUtc;
                DateTime now = DateTime.Now;

                if (LastVhdxSize >= 0 && LastVhdxCheckTime != DateTime.MinValue)
                {
                    double elapsedSec = (now - LastVhdxCheckTime).TotalSeconds;
                    if (elapsedSec > 0)
                    {
                        long deltaBytes = currentSize - LastVhdxSize;
                        VhdxGrowthRateMBs = Math.Max(0, (deltaBytes / elapsedSec) / (1024.0 * 1024.0));
                    }
                }

                LastVhdxSize = currentSize;
                LastVhdxCheckTime = now;

                if (LastVhdxWriteTime != DateTime.MinValue && writeTime > LastVhdxWriteTime)
                {
                    IsDiskLedActive = true;
                    LastDiskWriteAt = now;
                }
                LastVhdxWriteTime = writeTime;

                if (WslDiskSize == "Computing...")
                    WslDiskSize = $"{(currentSize / 1073741824.0):N2} GB";
            }
            catch
            {
                VhdxGrowthRateMBs = 0;
            }
        }

        public static void SetDistroSizeFromLog(double bytes)
        {
            WslDiskSize = $"{(bytes / 1073741824.0):N2} GB";
        }

        // ======================= REDE VIRTUAL (vEthernet) =======================

        public static void UpdateNetwork(bool isPullingState, ref double totalPullingMB)
        {
            long currentHostRx = 0, currentHostTx = 0;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
                    {
                        var stats = ni.GetIPStatistics();
                        currentHostRx += stats.BytesReceived;
                        currentHostTx += stats.BytesSent;
                    }
                }

                DateTime now = DateTime.Now;
                if (LastVNetTime != DateTime.MinValue)
                {
                    double diff = (now - LastVNetTime).TotalSeconds;
                    if (diff > 0)
                    {
                        long vmRxDiff = currentHostTx > LastVNetTx ? currentHostTx - LastVNetTx : 0;
                        long vmTxDiff = currentHostRx > LastVNetRx ? currentHostRx - LastVNetRx : 0;

                        if (vmRxDiff > 1024) LastRxActiveAt = now;
                        if (vmTxDiff > 1024) LastTxActiveAt = now;

                        if (isPullingState) totalPullingMB += (vmRxDiff / 1048576.0);
						
                        CurrentVmDownKbps = (vmRxDiff / diff) / 1024.0;
                        double txKbps = (vmTxDiff / diff) / 1024.0;

                        string rxStr = CurrentVmDownKbps >= 1024
                            ? $"[bold green]{CurrentVmDownKbps / 1024.0:F2} MB/s[/]"
                            : $"{CurrentVmDownKbps:F1} KB/s";
                        string txStr = txKbps >= 1024
                            ? $"[bold fuchsia]{txKbps / 1024.0:F2} MB/s[/]"
                            : $"{txKbps:F1} KB/s";

                        VNetTotalRxGB = currentHostTx / 1073741824.0;
                        VNetTotalTxGB = currentHostRx / 1073741824.0;

                        VNetStats = $"Tx: {txStr} | Rx: {rxStr}";
                        VNetTxStr = $"Tx {txStr}";
                        VNetRxStr = $"| Rx {rxStr}";
					}
                }

                LastVNetRx = currentHostRx;
                LastVNetTx = currentHostTx;
                LastVNetTime = now;
            }
            catch { }
        }

        // ======================= HELPERS =======================

        static string FormatUptime(TimeSpan ts)
        {
            if (ts.TotalDays >= 1)
                return $"{(int)ts.TotalDays}d {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }
}
