#nullable disable
#pragma warning disable CA1416

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Spectre.Console;
using Spectre.Console.Rendering;

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;

namespace SaladXRayPanel
{
    class Program
    {
        // ==========================================
        // UI STATE VARIABLES
        // ==========================================

        // AMD GPU LOAD
        static List<PerformanceCounter> amdGpuCounters = null;
        static bool amdCountersInitFailed = false;

        static bool showHelpScreen = false; // Help/About screen control
        static FigletFont embeddedFont = null;

        // Fixed width for the Uptime panel (synchronized with the banner calculation)
        const int UPTIME_PANEL_WIDTH = 23;

        static string balance = "Computing...", projected = "Computing...", lastUpdateTimer = "Computing...";
        static string wslStatusStr = "Pending...", ramUsage = "Awaiting WSL...", wslDiskSize = "Computing...", vNetStats = "Tx: 0 KB/s | Rx: 0 KB/s";
        static string jobId = "Pending...", containerStatus = "Pending...", workTime = "Computing...";
        static string txtCpu = "Computing...", txtGpu = "Computing...", txtRam = "Computing...";
        static string txtDisk = "Computing...";
        static string minerStatus = "[grey]Idle[/]", bandwidthStatus = "[grey]Idle[/]";
        static string lastWarning = "No recent errors detected in the current session.";

        // Human-readable Matrix status (Salad Backend)
        static string matrixStatus = "[grey]Waiting for matrix data...[/]";

        // GPU Demand Tracking Variables
        static string gpuDemandStatus = "[grey]Initializing WMI/API...[/]";
        static string gpuEarning24h = "[grey]Waiting...[/]";
        static string gpuDemandTier = "[grey]Waiting...[/]";
        static string gpuNetworkUtil = "[grey]Waiting...[/]";
        static DateTime lastGpuDemandUpdate = DateTime.MinValue;
        static bool isFetchingDemand = false;

        // Download and Unpacking Tracking Variables
        static bool isPullingState = false;
        static double totalPullingMB = 0;
        static double initialPercentTracker = -1;
        static double initialMbTracker = 0;
        static double lastEstimatedMB = 0;
        static string activeLayer = "N/A";
        static double layerProgress = 0.0;
        static double currentVmDownKbps = 0;
        static double globalProgress = 0.0;
        static double wslRamMB = 0.0;
        static Queue<double> speedHistory = new Queue<double>();

        // Disk Identification Variables
        static string hostDiskName = "Storage Disk";
        static bool isDiskInfoLoaded = false;

        static DateTime appStartTime = DateTime.Now;
        static DateTime saladStartTime = DateTime.MinValue;
        static DateTime saladBowlStartTime = DateTime.MinValue;
        static string saladVersion = "Detecting...";
        static string saladBowlVersion = "Detecting...";
        static string xrayVersion = FormatVersionWithShortHash(
            Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0"
        );

        // VAR EXE names
        static string uiName = "Salad";
        static string svcName = "Salad.Bowl.Service";
        static string xrayName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;

        // Network Variables (WSL & Host SGS)
        static long lastVNetRx = 0, lastVNetTx = 0;
        static DateTime lastVNetTime = DateTime.MinValue;
        static double vNetTotalRxGB = 0, vNetTotalTxGB = 0;

        static long lastSgsRx = 0, lastSgsTx = 0;
        static DateTime lastSgsTime = DateTime.MinValue;
        static double sgsTotalRxMB = 0, sgsTotalTxMB = 0;
        static string sgsDetails = "[grey]Computing traffic...[/]";
        static string sgsTotalTraffic = "0 MB (IN) | 0 MB (OUT)";
        static string sgsNodeName = "Waiting for node...";

        static DateTime lastWalletUpdate = DateTime.MinValue;
        static DateTime jobStartTime = DateTime.MinValue;
        static DateTime lastLogHeartbeat = DateTime.MinValue;
        static Queue<string> recentLogs = new Queue<string>(new[] { "Awaiting logs...", "", "", "" });
        static long lastLogPosition = 0;

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.CursorVisible = false;

            // ==========================================
            // LOAD CUSTOM FONT
            // ==========================================
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("SaladXRayPanel.smslant.flf"))
                {
                    if (stream != null) embeddedFont = FigletFont.Load(stream);
                }
            }
            catch { /* IF ERROR */ }
            // ==========================================

            string logsFolder = @"C:\ProgramData\Salad\logs\";

            RestoreInitialState(logsFolder);
            UpdateSaladInfo();

            while (true)
            {
                string logFile = GetMostRecentLogFile(logsFolder);
                if (logFile == null) logFile = $"log-{DateTime.Now:yyyyMMdd}.txt (Not Found)";

                await AnsiConsole.Live(RenderPanel(logFile))
                    .Cropping(VerticalOverflowCropping.Bottom)
                    .StartAsync(async ctx =>
                    {
                        int loopCounter = 0;
                        while (true)
                        {
                            logFile = GetMostRecentLogFile(logsFolder);
                            if (logFile != null) ReadSaladLogs(logFile);

                            if (loopCounter % 2 == 0)
                            {
                                UpdateHostHardware();
                                UpdateWSLData();
                                UpdateNetwork();
                                UpdateSaladInfo();
                                _ = FetchGpuDemandDataAsync();
                            }

                            CalculateUptime();
                            ctx.UpdateTarget(RenderPanel(logFile));

                            loopCounter++;

                            // Check keyboard input
                            for (int i = 0; i < 10; i++)
                            {
                                if (!Console.IsInputRedirected && Console.KeyAvailable)
                                {
                                    var key = Console.ReadKey(true);
                                    if (key.Key == ConsoleKey.Escape)
                                    {
                                        Console.CursorVisible = true;
                                        Environment.Exit(0);
                                    }
                                    else if (key.Key == ConsoleKey.H)
                                    {
                                        showHelpScreen = !showHelpScreen;
                                        ctx.UpdateTarget(RenderPanel(logFile));
                                    }
                                }
                                await Task.Delay(100);
                            }
                        }
                    });
            }
        }

        static async Task FetchGpuDemandDataAsync()
        {
            if (isFetchingDemand) return;
            isFetchingDemand = true;

            try
            {
                if ((DateTime.Now - lastGpuDemandUpdate).TotalMinutes < 5)
                {
                    isFetchingDemand = false;
                    return;
                }

                await Task.Run(async () =>
                {
                    string localGpuName = GetMiningGpuName();

                    if (string.IsNullOrEmpty(localGpuName))
                    {
                        gpuDemandStatus = "[red]Host GPU could not be identified[/]";
                        return;
                    }

                    using (var client = new HttpClient())
                    {
                        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 SaladXRayPanel/1.0");
                        string json = await client.GetStringAsync("https://app-api.salad.com/api/v2/demand-monitor/gpu");

                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var gpus = JsonSerializer.Deserialize<List<GpuDemandData>>(json, options);

                        var myGpu = gpus?.FirstOrDefault(g =>
                            (g.Name != null && g.Name.Equals(localGpuName, StringComparison.OrdinalIgnoreCase)) ||
                            (g.DisplayName != null && g.DisplayName.Equals(localGpuName, StringComparison.OrdinalIgnoreCase)));

                        if (myGpu != null)
                        {
                                gpuDemandStatus = $"[bold green]{myGpu.DisplayName}[/]";
                                gpuDemandTier = $"[cyan]{myGpu.DemandTierName}[/] (Min RAM: {myGpu.RecommendedSpecs?.RamGb}GB)";

double realBusyPct = myGpu.UtilizationPct;
if (realBusyPct < 0) realBusyPct = 0;
if (realBusyPct > 100) realBusyPct = 100;

gpuNetworkUtil = $"[yellow]{Math.Round(realBusyPct, 1)}%[/] of active machines working";

                            if (myGpu.EarningRates != null)
                            {
                                double avg24h = myGpu.EarningRates.AvgEarningRate * 24;
                                double max24h = myGpu.EarningRates.MaxEarningRate * 24;
                                gpuEarning24h = $"Avg: [bold green]${avg24h:F2}[/] / Max Pico: [bold green]${max24h:F2}[/] (24h)";
                            }
                        }
                        else
                        {
                            gpuDemandStatus = $"[darkorange]Not Listed[/] [grey]({localGpuName})[/]";
                            gpuDemandTier = "[grey]Low/No Demand[/]";
                            gpuNetworkUtil = "[grey]N/A[/]";
                            gpuEarning24h = "[grey]N/A[/]";
                        }
                    }
                });

                lastGpuDemandUpdate = DateTime.Now;
            }
            catch
            {
                gpuDemandStatus = $"[red]API Offline or Error[/]";
                gpuDemandTier = "[grey]N/A[/]";
            }
            finally
            {
                isFetchingDemand = false;
            }
        }

        static void UpdateSaladInfo()
        {
            try
            {
                int myProcessId = Process.GetCurrentProcess().Id;

                var processes = Process.GetProcesses()
                    .Where(p => p.ProcessName.StartsWith("salad", StringComparison.OrdinalIgnoreCase)
                             && p.Id != myProcessId
                             && !p.ProcessName.Contains("XRay", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (processes.Count > 0)
                {
                    DateTime? mainAppStart = null;
                    DateTime? bowlServiceStart = null;
                    string mainAppPath = null;
                    string bowlServicePath = null;

                    foreach (var p in processes)
                    {
                        string pName = p.ProcessName.ToLower();

                        if (pName == "salad" || pName == "salad (amd edition)")
                        {
                            uiName = p.ProcessName;
                            try
                            {
                                if (mainAppStart == null || p.StartTime < mainAppStart) mainAppStart = p.StartTime;
                            }
                            catch { if (mainAppStart == null) mainAppStart = DateTime.Now; }

                            try { mainAppPath = p.MainModule?.FileName; } catch { }
                        }
                        else if (pName.Contains("bowl"))
                        {
                            svcName = p.ProcessName;

                            DateTime? safeStartTime = null;
                            try { safeStartTime = p.StartTime; }
                            catch
                            {
                                try
                                {
                                    using (var searcher = new ManagementObjectSearcher($"SELECT CreationDate FROM Win32_Process WHERE ProcessId = {p.Id}"))
                                    {
                                        foreach (ManagementObject obj in searcher.Get())
                                        {
                                            string wmiDate = obj["CreationDate"]?.ToString();
                                            if (!string.IsNullOrEmpty(wmiDate)) safeStartTime = ManagementDateTimeConverter.ToDateTime(wmiDate);
                                        }
                                    }
                                } catch { }
                            }

                            if (safeStartTime == null) safeStartTime = DateTime.Now;
                            if (bowlServiceStart == null || safeStartTime < bowlServiceStart) bowlServiceStart = safeStartTime;

                            try { bowlServicePath = p.MainModule?.FileName; } catch { }
                        }
                    }

                    if (bowlServiceStart.HasValue && string.IsNullOrEmpty(bowlServicePath))
                    {
                        try
                        {
                            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SaladBowl"))
                            {
                                string imagePath = key?.GetValue("ImagePath")?.ToString();
                                if (!string.IsNullOrEmpty(imagePath))
                                {
                                    int sbIndex = imagePath.IndexOf("--sb");
                                    if (sbIndex > -1) bowlServicePath = imagePath.Substring(sbIndex + 4).Replace("\"", "").Trim();
                                    else
                                    {
                                        bowlServicePath = imagePath.Replace("\"", "");
                                        int exeIndex = bowlServicePath.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                                        if (exeIndex > 0) bowlServicePath = bowlServicePath.Substring(0, exeIndex + 4);
                                    }
                                }
                            }
                        }
                        catch { }

                        if (string.IsNullOrEmpty(bowlServicePath))
                        {
                            string fallback = @"C:\Program Files\Salad\SaladBowl\Salad.Bowl.Service.exe";
                            if (File.Exists(fallback)) bowlServicePath = fallback;
                        }
                    }

                    if (mainAppStart.HasValue)
                    {
                        saladStartTime = mainAppStart.Value;
                        if (!string.IsNullOrEmpty(mainAppPath) && File.Exists(mainAppPath))
                            saladVersion = FileVersionInfo.GetVersionInfo(mainAppPath).FileVersion;
                    }
                    else saladStartTime = DateTime.MinValue;

                    if (bowlServiceStart.HasValue)
                    {
                        saladBowlStartTime = bowlServiceStart.Value;
                        if (!string.IsNullOrEmpty(bowlServicePath) && File.Exists(bowlServicePath))
                            saladBowlVersion = FormatVersionWithShortHash(FileVersionInfo.GetVersionInfo(bowlServicePath).ProductVersion);
                    }
                    else
                    {
                        saladBowlStartTime = DateTime.MinValue;
                        saladBowlVersion = "Offline";
                    }

                    if (saladVersion != "Unknown" && saladVersion != "Detecting...") return;
                }
                else
                {
                    saladStartTime = DateTime.MinValue;
                    saladBowlStartTime = DateTime.MinValue;
                    saladBowlVersion = "Offline";
                }
            }
            catch { }

            try
            {
                string[] possibleRegistryKeys = {
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Salad",
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Salad (AMD Edition)"
                };

                foreach (string regPath in possibleRegistryKeys)
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(regPath))
                    {
                        if (key != null) { saladVersion = key.GetValue("DisplayVersion")?.ToString() ?? "Unknown"; return; }
                    }
                }
            } catch { }

            if (saladVersion == "Detecting...") saladVersion = "Unknown";
        }

        static void RestoreInitialState(string logsFolder)
        {
            string currentFile = GetMostRecentLogFile(logsFolder);
            if (currentFile == null) return;
            try
            {
                using (var fs = new FileStream(currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, System.Text.Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        ProcessLogLine(line, isStartup: true);
                        if (!line.Contains("Progress(") && !line.Contains("Pull progress")) AddLogToScreen(line);
                    }
                    lastLogPosition = fs.Position;
                }

                if (jobId != "Pending...") jobStartTime = FindRealJobStartTime(jobId, logsFolder);
            } catch { }
        }

        static DateTime FindRealJobStartTime(string desiredJobId, string logsFolder)
        {
            try
            {
                var files = new DirectoryInfo(logsFolder).GetFiles("log-*.txt").OrderByDescending(f => f.LastWriteTime).Take(3).Reverse();
                foreach (var file in files)
                {
                    using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs, System.Text.Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Contains(desiredJobId))
                            {
                                var matchData = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})");
                                if (matchData.Success && DateTime.TryParse(matchData.Groups[1].Value, out DateTime ts)) return ts;
                            }
                        }
                    }
                }
            } catch { }
            return DateTime.Now;
        }

        static string GetMostRecentLogFile(string folder)
        {
            if (!Directory.Exists(folder)) return null;
            return new DirectoryInfo(folder).GetFiles("log-*.txt").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
        }

        static void ReadSaladLogs(string filePath)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < lastLogPosition) lastLogPosition = 0;
                    fs.Seek(lastLogPosition, SeekOrigin.Begin);
                    using (var reader = new StreamReader(fs, System.Text.Encoding.UTF8, true, 4096, leaveOpen: true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            ProcessLogLine(line, isStartup: false);
                            if (line.Contains("Progress(") || line.Contains("Pull progress")) continue;
                            AddLogToScreen(line);
                        }
                    }
                    lastLogPosition = fs.Position;
                }
            } catch { }
        }

        static void ProcessLogLine(string line, bool isStartup = false)
        {
            DateTime timestampLog = DateTime.Now;
            var matchData = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})");
            if (matchData.Success) DateTime.TryParse(matchData.Groups[1].Value, out timestampLog);

            var matchMatrix = Regex.Match(line, @"Received desired state from matrix - (\d+) workloads");
            if (matchMatrix.Success)
            {
                if (int.TryParse(matchMatrix.Groups[1].Value, out int wCount))
                {
                    if (wCount == 0)
                    {
                        matrixStatus = "[grey]Idle - Searching for jobs...[/]";
                        containerStatus = "[grey]Stopped / Waiting[/]";
                        isPullingState = false;
                        globalProgress = 0.0;
                        jobId = "Pending...";
                    }
                    else
                    {
                        matrixStatus = $"[bold green]Job Acquired! ({wCount} active workload)[/]";
                    }
                }
            }

            var matchWallet = Regex.Match(line, @"Wallet: Current\((.*?)\), Predicted\((.*?)\)");
            if (matchWallet.Success)
            {
                balance = matchWallet.Groups[1].Value; projected = matchWallet.Groups[2].Value; lastWalletUpdate = timestampLog;
            }

            var matchWorkload = Regex.Match(line, @"salad\.com/sce/([a-f0-9\-]+)");
            if (matchWorkload.Success)
            {
                string newJobId = matchWorkload.Groups[1].Value;
                if (jobId != newJobId)
                {
                    jobId = newJobId;
                    if (!isStartup) jobStartTime = FindRealJobStartTime(newJobId, @"C:\ProgramData\Salad\logs\");
                    containerStatus = "Starting..."; matrixStatus = "[yellow]Initializing new job...[/]";
                    initialPercentTracker = -1; initialMbTracker = 0; lastEstimatedMB = 0; totalPullingMB = 0; isPullingState = false;
                    globalProgress = 0.0;
                }
            }

            var matchLayer = Regex.Match(line, @"Pull progress event: .*?@sha256:([a-f0-9]{8})[a-f0-9]*\s([0-9.]+)");
            if (matchLayer.Success)
            {
                activeLayer = matchLayer.Groups[1].Value;
                if (double.TryParse(matchLayer.Groups[2].Value.Replace(",", "."), System.Globalization.CultureInfo.InvariantCulture, out double lProg))
                    layerProgress = Math.Round(lProg * 100, 1);
            }

            var matchProgress = Regex.Match(line, @"Progress\((0[,.]\d+|1[,.]0+)\)");
            if (matchProgress.Success)
            {
                isPullingState = true;
                if (double.TryParse(matchProgress.Groups[1].Value.Replace(",", "."), System.Globalization.CultureInfo.InvariantCulture, out double p))
                {
                    double percentage = Math.Round(p * 100, 1);

                    if (!isStartup)
                    {
                        if (initialPercentTracker == -1 || percentage < globalProgress)
                        {
                            initialPercentTracker = percentage;
                            initialMbTracker = totalPullingMB;
                        }

                        double deltaPercentage = percentage - initialPercentTracker;
                        double deltaMB = totalPullingMB - initialMbTracker;

                        if (deltaPercentage >= 1.0 && deltaMB >= 10)
                        {
                            double phaseEstimate = (deltaMB * 100.0) / deltaPercentage;
                            lastEstimatedMB = initialMbTracker + phaseEstimate;
                        }
                    }

                    globalProgress = percentage;

                    double absoluteDownloadedMB = totalPullingMB;

                    if (lastEstimatedMB > 0)
                    {
                        double calculatedDownload = (lastEstimatedMB * percentage) / 100.0;
                        absoluteDownloadedMB = Math.Max(calculatedDownload, totalPullingMB);
                    }

                    string etaStr = "";
                    if (lastEstimatedMB > 0)
                    {
                        double remainingMB = lastEstimatedMB - absoluteDownloadedMB;
                        if (remainingMB < 0) remainingMB = 0;

                        double avgSpeedKbps = speedHistory.Count > 0 ? speedHistory.Average() : currentVmDownKbps;

                        if (avgSpeedKbps > 0 && remainingMB > 0)
                        {
                            double speedMBps = avgSpeedKbps / 1024.0;
                            if (speedMBps > 0.1)
                            {
                                double remainingSecs = remainingMB / speedMBps;
                                if (remainingSecs > 86400) remainingSecs = 86400;
                                TimeSpan tSpan = TimeSpan.FromSeconds(remainingSecs);

                                if (tSpan.TotalHours >= 1)
                                    etaStr = $" | ETA: {tSpan.Hours}h {tSpan.Minutes}m";
                                else
                                    etaStr = $" | ETA: {tSpan.Minutes}m {tSpan.Seconds}s";
                            }
                        }
                    }

                    string physicalStr = absoluteDownloadedMB > 1024 ? $"{(absoluteDownloadedMB / 1024.0):F2} GB" : $"{Math.Round(absoluteDownloadedMB, 0)} MB";

                    if (lastEstimatedMB > absoluteDownloadedMB)
                    {
                        string totalStr = lastEstimatedMB > 1024 ? $"{(lastEstimatedMB / 1024.0):F2} GB" : $"{Math.Round(lastEstimatedMB, 0)} MB";
                        physicalStr = $"{physicalStr} / {totalStr}";
                    }
                    else if (initialPercentTracker != -1)
                    {
                        physicalStr = $"{physicalStr} (Syncing Size...)";
                    }

                    containerStatus = $"[yellow]Global: {percentage}% | ?Layer? [[{activeLayer}]] | DL: {physicalStr}{etaStr}[/]";
                }
            }

            if (line.Contains("Running(Ready") || line.Contains("already running") || line.Contains("already installed")) { containerStatus = "[green]Running (Stable)[/]"; isPullingState = false; globalProgress = 0.0; }
            else if (line.Contains("Killed") || line.Contains("Stopped") || line.Contains("failed"))
            {
                if (globalProgress > 0 && globalProgress < 100)
                {
                    containerStatus = $"[darkorange]Network Hiccup / Retrying... (Frozen at {globalProgress}%)[/]";
                }
                else
                {
                    isPullingState = false;
                    containerStatus = "[grey]Stopped / Waiting[/]";
                    globalProgress = 0.0;
                }
            }

            var matchBandwidthNode = Regex.Match(line, @"(Bandwidth-[a-zA-Z0-9\-]+)");
            if (matchBandwidthNode.Success) { bandwidthStatus = "[magenta]Active[/]"; sgsNodeName = matchBandwidthNode.Groups[1].Value; }

            if (line.Contains("Stopping workload") && line.Contains("Bandwidth")) { bandwidthStatus = "[grey]Idle[/]"; sgsNodeName = "Waiting for node..."; }

            if (line.Contains("[WRN]") || line.Contains("[ERR]") || line.Contains("failed"))
            {
                var matchError = Regex.Match(line, @"\]\s+(.*)");
                lastWarning = matchError.Success ? (matchError.Groups[1].Value.Length > 85 ? matchError.Groups[1].Value.Substring(0, 82) + "..." : matchError.Groups[1].Value) : line;
            }

            if (line.Contains("Heartbeat"))
            {
                lastLogHeartbeat = DateTime.Now;
            }

            var matchDisk = Regex.Match(line, @"DistroSize\s*=\s*([0-9.]+)");
            if (matchDisk.Success && double.TryParse(matchDisk.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double bytes))
                wslDiskSize = $"{(bytes / 1073741824.0):N2} GB";
        }

        static void UpdateNetwork()
        {
            long currentHostRx = 0, currentHostTx = 0;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
                    {
                        var stats = ni.GetIPStatistics(); currentHostRx += stats.BytesReceived; currentHostTx += stats.BytesSent;
                    }
                }
                DateTime now = DateTime.Now;
                if (lastVNetTime != DateTime.MinValue)
                {
                    double diff = (now - lastVNetTime).TotalSeconds;
                    if (diff > 0)
                    {
                        long vmRxDiff = currentHostTx > lastVNetTx ? currentHostTx - lastVNetTx : 0;
                        long vmTxDiff = currentHostRx > lastVNetRx ? currentHostRx - lastVNetRx : 0;
                        if (isPullingState) totalPullingMB += (vmRxDiff / 1048576.0);
                        currentVmDownKbps = (vmRxDiff / diff) / 1024.0; double txKbps = (vmTxDiff / diff) / 1024.0;
                        speedHistory.Enqueue(currentVmDownKbps);
                        if (speedHistory.Count > 10) speedHistory.Dequeue();
                        string rxStr = currentVmDownKbps >= 1024 ? $"[bold green]{currentVmDownKbps / 1024.0:F2} MB/s[/]" : $"{currentVmDownKbps:F1} KB/s";
                        string txStr = txKbps >= 1024 ? $"[bold fuchsia]{txKbps / 1024.0:F2} MB/s[/]" : $"{txKbps:F1} KB/s";
                        vNetTotalRxGB = currentHostTx / 1073741824.0; vNetTotalTxGB = currentHostRx / 1073741824.0;
                        vNetStats = $"Tx: {txStr} | Rx: {rxStr} (Total: {vNetTotalRxGB:F2} GB / {vNetTotalTxGB:F2} GB)";
                    }
                }
                lastVNetRx = currentHostRx; lastVNetTx = currentHostTx; lastVNetTime = now;
            } catch { }

            long currentSgsRx = 0, currentSgsTx = 0, currentSgsRam = 0; bool isProcessFound = false;
            try
            {
                var sgsProcs = Process.GetProcesses().Where(p => p.ProcessName.StartsWith("sgs", StringComparison.OrdinalIgnoreCase) || p.ProcessName.StartsWith("v2ray", StringComparison.OrdinalIgnoreCase) || p.ProcessName.StartsWith("ss-local", StringComparison.OrdinalIgnoreCase)).ToList();
                if (sgsProcs.Count > 0)
                {
                    isProcessFound = true; currentSgsRam = sgsProcs.Sum(p => p.WorkingSet64);
                    string wqlPids = string.Join(" OR ", sgsProcs.Select(p => $"ProcessId={p.Id}"));
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher($"SELECT ReadTransferCount, WriteTransferCount FROM Win32_Process WHERE {wqlPids}"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            currentSgsRx += Convert.ToInt64(obj["ReadTransferCount"] ?? 0); currentSgsTx += Convert.ToInt64(obj["WriteTransferCount"] ?? 0);
                        }
                    }
                }
                DateTime now = DateTime.Now;
                if (isProcessFound)
                {
                    if (lastSgsTime != DateTime.MinValue)
                    {
                        double diff = (now - lastSgsTime).TotalSeconds;
                        if (diff > 0)
                        {
                            long rxDiff = currentSgsRx > lastSgsRx ? currentSgsRx - lastSgsRx : 0;
                            long txDiff = currentSgsTx > lastSgsTx ? currentSgsTx - lastSgsTx : 0;
                            sgsTotalRxMB += rxDiff / 1048576.0; sgsTotalTxMB += txDiff / 1048576.0;
                            double rxKbps = (rxDiff / diff) / 1024.0; double txKbps = (txDiff / diff) / 1024.0;
                            string rxStr = rxKbps >= 1024 ? $"[bold green]{rxKbps / 1024.0:F2} MB/s[/]" : $"{rxKbps:F1} KB/s";
                            string txStr = txKbps >= 1024 ? $"[bold fuchsia]{txKbps / 1024.0:F2} MB/s[/]" : $"{txKbps:F1} KB/s";
                            sgsDetails = $"[[ IN: {rxStr} | OUT: {txStr} ]] (RAM Usage: {currentSgsRam / 1048576.0:F1} MB)";
                            sgsTotalTraffic = $"{sgsTotalRxMB:F2} MB (IN) | {sgsTotalTxMB:F2} MB (OUT)";
                        }
                    }
                    lastSgsRx = currentSgsRx; lastSgsTx = currentSgsTx; lastSgsTime = now;
                }
                else { sgsDetails = "[grey]Network process idle or waiting...[/]"; lastSgsTime = DateTime.MinValue; }
            } catch { }
        }

        static void CalculateUptime()
        {
            if (jobStartTime != DateTime.MinValue && jobId != "Pending..." && containerStatus.Contains("Running"))
            {
                var diff = DateTime.Now - jobStartTime;
                if (diff.TotalSeconds < 0) diff = TimeSpan.Zero;
                workTime = diff.TotalHours >= 1 ? $"{(int)diff.TotalHours}h {diff.Minutes}m" : $"{diff.Minutes}m {diff.Seconds}s";
            }
            else workTime = "Waiting...";

            if (lastWalletUpdate != DateTime.MinValue)
            {
                var diff = DateTime.Now - lastWalletUpdate;
                if (diff.TotalSeconds < 0) diff = TimeSpan.Zero;
                lastUpdateTimer = $"{(int)diff.TotalMinutes}m {diff.Seconds}s";
            }
        }

        static void LoadDiskInfo()
        {
            if (isDiskInfoLoaded) return;

            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT MediaType, BusType, Size FROM MSFT_PhysicalDisk"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        int mediaType = Convert.ToInt32(obj["MediaType"]);
                        int busType = Convert.ToInt32(obj["BusType"]);

                        ulong sizeBytes = Convert.ToUInt64(obj["Size"] ?? 0);
                        double sizeGb = sizeBytes / 1073741824.0;
                        string sizeStr = sizeGb >= 1000 ? $"{(sizeGb / 1024.0):F1} TB" : $"{Math.Round(sizeGb)} GB";

                        string mType = mediaType == 4 ? "SSD" : mediaType == 3 ? "HDD" : "DISK";
                        string bType = busType == 17 ? "NVMe" : busType == 11 ? "SATA" : busType == 7 ? "USB" : "";

                        hostDiskName = $"{bType} {mType} [[{sizeStr}]]".Trim();
                        isDiskInfoLoaded = true;
                        return;
                    }
                }
            }
            catch { }

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Model, Size FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        ulong sizeBytes = Convert.ToUInt64(obj["Size"] ?? 0);
                        double sizeGb = sizeBytes / 1073741824.0;
                        string sizeStr = sizeGb >= 1000 ? $"{(sizeGb / 1024.0):F1} TB" : $"{Math.Round(sizeGb)} GB";

                        string model = obj["Model"]?.ToString() ?? "";
                        string baseName = "Disk Drive";

                        if (model.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0) baseName = "NVMe SSD";
                        else if (model.IndexOf("SSD", StringComparison.OrdinalIgnoreCase) >= 0) baseName = "SATA SSD";

                        hostDiskName = $"{baseName} [[{sizeStr}]]";
                        isDiskInfoLoaded = true;
                        return;
                    }
                }
            }
            catch { isDiskInfoLoaded = true; }
        }


static double GetAmdGpuUtilization()
{
    if (amdCountersInitFailed) return -1;

    try
    {
        if (amdGpuCounters == null)
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instances = category.GetInstanceNames()
                .Where(i => i.Contains("engtype_3D") || i.Contains("engtype_Compute"))
                .ToArray();

            amdGpuCounters = new List<PerformanceCounter>();
            foreach (var instance in instances)
            {
                foreach (var c in category.GetCounters(instance))
                {
                    if (c.CounterName == "Utilization Percentage")
                        amdGpuCounters.Add(c);
                }
            }

            if (amdGpuCounters.Count == 0)
            {
                amdCountersInitFailed = true;
                return -1;
            }

            // First reading is always 0, so discard it and return -1 this time.
            foreach (var c in amdGpuCounters) c.NextValue();
            return -1;
        }

        double total = amdGpuCounters.Sum(c => c.NextValue());
        if (total > 100) total = 100;
        if (total < 0) total = 0;
        return Math.Round(total, 1);
    }
    catch
    {
        amdCountersInitFailed = true;
        return -1;
    }
}

        static void UpdateHostHardware()
        {
            try
            {
            string[] minerNames = { "t-rex", "trex", "gminer", "srbminer", "xmrig", "nbminer", "lolminer", "excavator", "rigel", "bzminer", "phoenixminer", "miner" };
                bool minerFound = false;

                foreach (string mName in minerNames)
                {
                    Process[] procs = Process.GetProcessesByName(mName);
                    if (procs.Length > 0)
                    {
                        minerStatus = $"[yellow][[ ACTIVE - {procs[0].ProcessName.ToUpper()} ]][/]";
                        minerFound = true;
                        break;
                    }
                }

                if (!minerFound) { minerStatus = "[grey]Idle[/]"; }
            }
            catch
            {
                minerStatus = "[red]Error Checking[/]";
            }

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT LoadPercentage, Name FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get()) { txtCpu = $"{obj["Name"]?.ToString() ?? "CPU"} (Load: {obj["LoadPercentage"]?.ToString() ?? "0"}%)"; break; }
                }
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        double totalMb = Convert.ToDouble(obj["TotalVisibleMemorySize"]) / 1024; double freeMb = Convert.ToDouble(obj["FreePhysicalMemory"]) / 1024;
                        txtRam = $"{Math.Round((totalMb - freeMb) / 1024, 1)} GB / {Math.Round(totalMb / 1024, 1)} GB (Load: {Math.Round(((totalMb - freeMb) / totalMb) * 100, 0)}%)"; break;
                    }
                }
                txtGpu = "Searching GPU...";
                ProcessStartInfo psi = new ProcessStartInfo { FileName = "nvidia-smi", Arguments = "--query-gpu=name,utilization.gpu,power.draw,temperature.gpu --format=csv,noheader,nounits", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using (Process proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd().Trim();
                    if (!string.IsNullOrEmpty(output))
                    {
                        var parts = output.Split(',');
                        if (parts.Length >= 4)
                        {
                             string smiName = parts[0].Trim().Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "");
                             txtGpu = $"{smiName} (Load: {parts[1].Trim()}% | Pwr: {parts[2].Trim()}W | Temp: {parts[3].Trim()}›C)";
                        }
                    }
                }
            }
catch
{
    string fallbackGpu = GetMiningGpuName() ?? "Unknown GPU";
    double amdLoad = GetAmdGpuUtilization();

    txtGpu = amdLoad >= 0
        ? $"{fallbackGpu} (Load: {amdLoad}%)"
        : $"{fallbackGpu} (SMI Sensores N/A)";
}
            LoadDiskInfo();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT PercentIdleTime, DiskReadBytesPersec, DiskWriteBytesPersec FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        float idle = obj["PercentIdleTime"] != null ? Convert.ToSingle(obj["PercentIdleTime"]) : 100f;
                        float util = 100f - idle;
                        if (util < 0) util = 0; if (util > 100) util = 100;

                        float readB = obj["DiskReadBytesPersec"] != null ? Convert.ToSingle(obj["DiskReadBytesPersec"]) : 0;
                        float writeB = obj["DiskWriteBytesPersec"] != null ? Convert.ToSingle(obj["DiskWriteBytesPersec"]) : 0;

                        string readStr = readB >= 1048576 ? $"{(readB / 1048576):F1} MB/s" : $"{(readB / 1024):F1} KB/s";
                        string writeStr = writeB >= 1048576 ? $"{(writeB / 1048576):F1} MB/s" : $"{(writeB / 1024):F1} KB/s";

                        txtDisk = $"{hostDiskName} (Load: {util:F0}% | R: {readStr} | W: {writeStr})";
                        break;
                    }
                }
            }
            catch
            {
                txtDisk = $"{hostDiskName} (I/O Data N/A)";
            }
        }

        static void UpdateWSLData()
        {
            try
            {
                long ramTotalBytes = Process.GetProcesses().Where(p => p.ProcessName == "vmmemWSL" || p.ProcessName == "vmmem" || p.ProcessName == "wslhost").Sum(p => p.WorkingSet64);
                wslRamMB = ramTotalBytes / 1048576.0;
                ramUsage = wslRamMB > 0 ? $"{wslRamMB:N1} MB" : "Awaiting WSL...";

                ProcessStartInfo psi = new ProcessStartInfo { FileName = "wsl.exe", Arguments = "-l -v", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.Unicode };
                using (Process proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd().Replace("\0", "");
                    var matchWsl = Regex.Match(output, @"salad-enterprise-linux\s+([A-Za-z]+)");
                    if (matchWsl.Success) { string state = matchWsl.Groups[1].Value; wslStatusStr = state.Contains("Running") ? "Running (Active)" : state.Contains("Stopped") ? "STOPPED (Offline)" : state; }
                }
            } catch { wslStatusStr = "Error reading WSL"; wslRamMB = 0; }

            if (wslStatusStr.Contains("STOPPED") || wslStatusStr.Contains("Offline") || wslStatusStr.Contains("Error") || wslStatusStr.Contains("Pending"))
            {
                if (containerStatus.Contains("Running"))
                {
                    containerStatus = "[yellow]Waiting for WSL...[/]";
                }
            }
        }

        static void AddLogToScreen(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            string shortLine = Regex.Replace(line, @"^\d{4}-\d{2}-\d{2}\s+(\d{2}:\d{2}:\d{2})\.\d+\s+[-+]\d{2}:\d{2}\s+\[\w{3}\]\s+", "[$1] ");
            if (shortLine.Length > 85) shortLine = shortLine.Substring(0, 82) + "...";
            if (recentLogs.Count >= 4) recentLogs.Dequeue(); recentLogs.Enqueue($"[grey]{Markup.Escape(shortLine)}[/]");
        }

        static IRenderable RenderPanel(string filePath)
        {
            // =========================================================================
            // HELP / ABOUT SCREEN (NEW)
            // =========================================================================
            if (showHelpScreen)
            {
                var helpGrid = new Grid().Expand();
                helpGrid.AddColumn(new GridColumn());

                // USES THE EMBEDDED FONT (OR THE DEFAULT AS BACKUP IF IT FAILS)
                var fontToUse = embeddedFont ?? FigletFont.Default;
                var logoSalad = new FigletText(fontToUse, "Salad").Color(Color.SpringGreen3);
                var logoXRay = new FigletText(fontToUse, "XRay").Color(Color.DeepSkyBlue1);

                var logoTable = new Table().HideHeaders().Border(TableBorder.None);
                logoTable.AddColumn(new TableColumn("").LeftAligned().Padding(0, 0, 0, 0).NoWrap());
                logoTable.AddColumn(new TableColumn("").LeftAligned().Padding(0, 0, 0, 0).NoWrap());
                logoTable.AddRow(logoSalad, logoXRay);

                helpGrid.AddRow(new Align(logoTable, HorizontalAlignment.Center));
                helpGrid.AddRow(new Text(""));

var helpText = new Markup(
    "[green bold]Controls & Shortcuts:[/]\n\n" +
    "[yellow][[CTRL]] [[+ / -]][/] : Zoom in/out on the terminal (Windows default).\n" +
    "[yellow][[H]][/]            : Toggle between the Dashboard and this Help screen.\n" +
    "[yellow][[ESC]][/]          : Safely exit SaladXRay."
);

                var helpPanel = new Panel(helpText)
                    .Header("[white bold] HELP & CONTROLS [/]", Justify.Left)
                    .BorderColor(Color.Green)
                    .Padding(2, 1, 2, 1);

                helpGrid.AddRow(helpPanel);

var aboutText = new Markup(
    "[bold cyan]Born from Agony, Built for Peace of Mind[/]\n\n" +
    "[green]SaladXRay[/] exists because of pure agony. Watching a container download " +
    "with no idea when it would finish, no transfer rate, no job size, no ETA - just " +
    "refreshing the raw logs like a maniac, hoping for a clue. That anxiety is gone now.\n\n" +
    "This tool was built to answer the questions Salad itself doesn't show you: " +
    "[yellow]how much is downloaded, how fast, and how long until it's done[/]. " +
    "Real-time visibility into your hardware, your WSL virtual machine, your container " +
    "workload, your wallet, and real-time network demand fetched directly from Salad's public API " +
    "- all in one glance.\n\n" +
    "[bold cyan][[-h]] Human-Readable Translation:[/] For your absolute peace of mind, SaladXRay is " +
    "strictly a read-only tool. No spooky background commands, no system tweaks. It safely builds " +
    "this dashboard by simply parsing the Salad log file, tapping into standard Windows APIs, and " +
    "reading public data. Just like a real X-Ray, it only observes.\n\n" +
    "True story: before development even started, I picked up a container, watched the download " +
    "crawl through Task Manager, and right in the middle of it... the power went out. I never " +
    "knew how much had downloaded, or how much was left. SaladXRay was born out of exactly " +
    "that kind of moment.\n\n" +
    "Built in about 15 days total - 5 of them after the first beta - for anyone running " +
    "Salad who's ever wanted to actually [bold]understand[/] what's happening under the hood, " +
    "instead of just hoping for the best.\n\n" +
    "[bold]The hardest bug I ever fixed?[/] My wife. Everything else - WSL quirks, log parsing, " +
    "GPU demand APIs - was easy compared to that. [grey](Love you, babe.)[/]\n\n" +
    $"[grey]XRay Version:[/] {xrayVersion}\n" +
    "[grey]Built with patience (and a very understanding wife) for the community.[/]"
);
                var aboutPanel = new Panel(aboutText)
                    .Header("[white bold] ABOUT SALAD XRAY [/]", Justify.Left)
                    .BorderColor(Color.Blue)
                    .Padding(2, 1, 2, 1);

                helpGrid.AddRow(aboutPanel);

                var backInstruction = new Markup("\n[blink red]Press [[H]] to go back...[/]");
                helpGrid.AddRow(new Align(backInstruction, HorizontalAlignment.Center));

                return helpGrid;
            }

            // =========================================================================
            // MAIN SCREEN (DASHBOARD)
            // =========================================================================
            var grid = new Grid().Expand();
            grid.AddColumn(new GridColumn());

            string fileName = Path.GetFileName(filePath);

            TimeSpan appUptime = DateTime.Now - appStartTime;
            string formattedAppUptime = $"{(int)appUptime.TotalHours:D2}:{appUptime.Minutes:D2}:{appUptime.Seconds:D2}";

            string formattedSaladUptime = saladStartTime != DateTime.MinValue
                ? $"{(int)(DateTime.Now - saladStartTime).TotalHours:D2}:{(DateTime.Now - saladStartTime).Minutes:D2}:{(DateTime.Now - saladStartTime).Seconds:D2}"
                : "Offline";

            string formattedBowlUptime = saladBowlStartTime != DateTime.MinValue
                ? $"{(int)(DateTime.Now - saladBowlStartTime).TotalHours:D2}:{(DateTime.Now - saladBowlStartTime).Minutes:D2}:{(DateTime.Now - saladBowlStartTime).Seconds:D2}"
                : "Offline";

            string uiStr = saladVersion == "Unknown" || saladVersion == "Detecting..." ? uiName : $"{uiName} v{saladVersion}";
            string svcStr = saladBowlVersion == "Offline" || saladBowlVersion == "Unknown" || saladBowlVersion == "Detecting..." ? svcName : $"{svcName} v{saladBowlVersion}";

            string titleText = $"[yellow bold]{xrayName} v{xrayVersion} [[ESC]] Exit [[H]] Help/About[/]";

            var infoPanel = CreateBannerPanel(titleText, new Dictionary<string, string> {
                { "APP VERSION", $"[green]{Markup.Escape(uiStr)}[/]" },
                { "SVC VERSION", $"[blue]{Markup.Escape(svcStr)}[/]" },
                { "READING LOG", $"[cyan]{Markup.Escape(fileName)}[/]" }
            });

            // 1. CONTENT (Internal table, super clean)
            var uptimeContent = new Table().HideHeaders().Border(TableBorder.None);
            uptimeContent.AddColumn(new TableColumn("").NoWrap());
            uptimeContent.AddRow(new Markup($"[green]App:[/] {formattedSaladUptime}"));
            uptimeContent.AddRow(new Markup($"[blue]Svc:[/] {formattedBowlUptime}"));
            uptimeContent.AddRow(new Markup($"[yellow bold]Xry:[/] {formattedAppUptime}"));

            string uptimeTitle = "[yellow bold]Uptime[/]";

            // 2. THE BOX (Compact style, straightforward)
            var uptimePanel = new Panel(uptimeContent)
                .Header(uptimeTitle)
                .BorderColor(Color.White)
                .SquareBorder();

            // 3. Safe right alignment
            var rightAlignedUptime = new Align(uptimePanel, HorizontalAlignment.Right);

// 4. TOP GRID
var topHeaderGrid = new Grid();
topHeaderGrid.AddColumn(new GridColumn());
topHeaderGrid.AddColumn(new GridColumn().Width(UPTIME_PANEL_WIDTH));
topHeaderGrid.AddRow(infoPanel, new Align(uptimePanel, HorizontalAlignment.Right));
topHeaderGrid.Expand();

grid.AddRow(topHeaderGrid); // without wrapping Panel

            // =============================================

            grid.AddRow(CreateSection("EARNINGS", new Dictionary<string, string> { { ":money_bag: WALLET", $"[bold green]${balance}[/] | 24H EST: [bold yellow]${projected}[/] | UPDATED: {lastUpdateTimer}" } }));

            string wslColor = wslStatusStr.Contains("Running") ? "green" : wslStatusStr.Contains("STOPPED") ? "red" : "yellow";
            grid.AddRow(CreateSection("LINUX WSL (VIRTUAL MACHINE)", new Dictionary<string, string> { { ":desktop_computer: VM STATUS", $"[{wslColor}]{wslStatusStr}[/]" }, { ":floppy_disk: VM RAM", ramUsage }, { ":optical_disk: VM DISK", wslDiskSize }, { ":satellite_antenna: VM LAN", vNetStats } }));

            string heart = ":broken_heart:";
            if ((DateTime.Now - lastLogHeartbeat).TotalMinutes < 2)
            {
                heart = (DateTime.Now.Millisecond < 500) ? ":red_heart:" : ":beating_heart:";
            }

            string matrixStatusWithHeart = $"{matrixStatus} {heart}";

            string displayContainerStatus = containerStatus;
            if (isPullingState && globalProgress >= 98.0 && currentVmDownKbps < 1024 && wslRamMB > 800)
            {
                displayContainerStatus = $"[cyan]Unpacking / Extracting... (WSL RAM Spike: {wslRamMB:N0} MB | Low Net I/O)[/]";
            }

            grid.AddRow(CreateSection("SALAD CONTAINER WORKLOAD", new Dictionary<string, string> {
                { ":satellite: MATRIX STATE", matrixStatusWithHeart },
                { ":id_button: WORKLOAD ID", jobId },
                { ":package: CONTAINER", displayContainerStatus },
                { ":stopwatch: UPTIME", workTime }
            }));

            grid.AddRow(CreateSection("GLOBAL HARDWARE (HOST)", new Dictionary<string, string> {
                { ":gear: HOST CPU", txtCpu },
                { ":fire: HOST GPU", txtGpu },
                { ":bar_chart: HOST RAM", txtRam },
                { ":floppy_disk: HOST DISK", txtDisk }
            }));

            grid.AddRow(CreateSection("SALAD GPU DEMAND", new Dictionary<string, string> {
                { ":fire: GPU MATCH", gpuDemandStatus },
                { ":gem_stone: DEMAND", gpuDemandTier },
                { ":chart_increasing: NET UTIL", gpuNetworkUtil },
                { ":money_bag: 24H EST", gpuEarning24h }
            }));

            var hostWorkloads = new Dictionary<string, string> { { ":pick: GPU MINER", minerStatus }, { ":globe_with_meridians: SGS NODE", bandwidthStatus.Contains("Idle") ? "[grey]Idle[/]" : $"[magenta]{sgsNodeName}[/]" } };
            if (!bandwidthStatus.Contains("Idle")) { hostWorkloads.Add(":satellite_antenna: SGS I/O", sgsDetails); hostWorkloads.Add(":chart_increasing: SGS TRAFFIC", $"[magenta]{sgsTotalTraffic}[/]"); }
            grid.AddRow(CreateSection("WINDOWS HOST WORKLOADS", hostWorkloads));

            var logsArray = recentLogs.ToArray();
            var errorTable = new Table().HideHeaders().Border(TableBorder.None).Expand();
            errorTable.AddColumn(new TableColumn("Label").Width(15).NoWrap()); errorTable.AddColumn(new TableColumn("Value").NoWrap());
            errorTable.AddRow(new Markup("[white]:warning: Last Error[/]"), new Markup($"[white]:[/] {TruncateWithColors($"[red]{Markup.Escape(lastWarning)}[/]", Math.Max(10, AnsiConsole.Profile.Width - 25))}"));

            int logMaxWidth = Math.Max(20, AnsiConsole.Profile.Width - 8);
            var logsBlock = new Markup($"  {TruncateWithColors(logsArray.Length > 0 ? logsArray[0] : "", logMaxWidth)}\n  {TruncateWithColors(logsArray.Length > 1 ? logsArray[1] : "", logMaxWidth)}\n  {TruncateWithColors(logsArray.Length > 2 ? logsArray[2] : "", logMaxWidth)}\n  {TruncateWithColors(logsArray.Length > 3 ? logsArray[3] : "", logMaxWidth)}");

            grid.AddRow(new Panel(new Rows(errorTable, logsBlock)).Header("[cyan][[ RECENT EVENTS ]][/]").BorderColor(Color.Cyan).Expand());

            return grid;
        }

        static string TruncateWithColors(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var result = new System.Text.StringBuilder(); int visibleLength = 0; bool insideTag = false; int openTags = 0; bool isCut = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '[' && i + 1 < text.Length && text[i + 1] == '[') { if (visibleLength < maxLength - 3) { result.Append("[["); visibleLength++; i++; continue; } else { isCut = true; break; } }
                if (c == ']' && i + 1 < text.Length && text[i + 1] == ']') { if (visibleLength < maxLength - 3) { result.Append("]]"); visibleLength++; i++; continue; } else { isCut = true; break; } }
                if (c == '[') { insideTag = true; result.Append('['); if (i + 1 < text.Length && text[i + 1] == '/') openTags--; else openTags++; continue; }
                if (insideTag) { result.Append(c); if (c == ']') insideTag = false; continue; }
                if (visibleLength < maxLength - 3) { result.Append(c); visibleLength++; } else { isCut = true; break; }
            }
            if (isCut) { result.Append("..."); for (int j = 0; j < Math.Max(0, openTags); j++) result.Append("[/]"); }
            return result.ToString();
        }

        static Panel CreateSection(string title, Dictionary<string, string> items)
        {
            var table = new Table().HideHeaders().Border(TableBorder.None).Expand();
            table.AddColumn(new TableColumn("Label").Width(15).NoWrap()); table.AddColumn(new TableColumn("Value").NoWrap());
            foreach (var item in items) table.AddRow(new Markup($"[white]{Markup.Escape(item.Key ?? "")}[/]"), new Markup($"[white]:[/] {TruncateWithColors(item.Value ?? "", Math.Max(10, AnsiConsole.Profile.Width - 25))}"));
            return new Panel(table).Header($"[cyan][[ {Markup.Escape(title)} ]][/]").BorderColor(Color.Cyan).Expand();
        }

static Panel CreateBannerPanel(string title, Dictionary<string, string> items)
{
    int panelWidth = Math.Max(20, AnsiConsole.Profile.Width - (UPTIME_PANEL_WIDTH + 5));
    int labelWidth = 15;

    var lines = new List<Markup>();

    foreach (var item in items)
    {
        string key = (item.Key ?? "").Length > labelWidth
            ? (item.Key ?? "").Substring(0, labelWidth)
            : (item.Key ?? "").PadRight(labelWidth);

        string safeKey = Markup.Escape(key);
        string rawLine = $"[white]{safeKey}[/] [white]:[/] {item.Value ?? ""}";

        // truncates the COMPLETE LINE already assembled, ensuring it never exceeds the panel
        string finalLine = TruncateWithColors(rawLine, panelWidth - 4); // -4 = panel borders/padding

        lines.Add(new Markup(finalLine));
    }

    var rows = new Rows(lines);

    return new Panel(rows)
        .Header(TruncateWithColors(title, Math.Max(15, AnsiConsole.Profile.Width - (UPTIME_PANEL_WIDTH + 7))))
        .BorderColor(Color.White)
        .SquareBorder();
}

        static string GetMiningGpuName()
        {
            string nomeFinal = null;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string gpuName = obj["Name"]?.ToString();
                        if (string.IsNullOrEmpty(gpuName)) continue;

                        if (gpuName.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            gpuName.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            gpuName.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            continue;
                        }

                        if (gpuName.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            nomeFinal = gpuName.Replace("Radeon ", "").Replace("radeon ", "").Trim();
                            break;
                        }

                        if (gpuName.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            nomeFinal = gpuName.Replace("GeForce ", "").Replace("geforce ", "").Trim();
                            break;
                        }

                        nomeFinal = gpuName;
                        break;
                    }
                }
            }
            catch { }
            return nomeFinal;
        }

        static string FormatVersionWithShortHash(string fullVersion)
        {
            if (string.IsNullOrEmpty(fullVersion)) return "Unknown";

            var parts = fullVersion.Split('+');
            if (parts.Length > 1)
            {
                string hash = parts[1];
                string shortHash = hash.Length > 7 ? hash.Substring(0, 7) : hash;
                return $"{parts[0]}+{shortHash}";
            }

            return parts[0];
        }
    }

    public class GpuDemandData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("demandTierName")]
        public string DemandTierName { get; set; }

        [JsonPropertyName("utilizationPct")]
        public double UtilizationPct { get; set; }

        [JsonPropertyName("earningRates")]
        public EarningRatesData EarningRates { get; set; }

        [JsonPropertyName("recommendedSpecs")]
        public RecommendedSpecsData RecommendedSpecs { get; set; }
    }

    public class EarningRatesData
    {
        [JsonPropertyName("avgEarningRate")]
        public double AvgEarningRate { get; set; }

        [JsonPropertyName("maxEarningRate")]
        public double MaxEarningRate { get; set; }
    }

    public class RecommendedSpecsData
    {
        [JsonPropertyName("ramGb")]
        public int RamGb { get; set; }
    }
}

