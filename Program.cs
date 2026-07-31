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

namespace SaladXRayPanel
{
    class Program
    {
        // ==========================================
        // UI STATE VARIABLES
        // ==========================================
        static string balance = "Computing...", projected = "Computing...", lastUpdateTimer = "Computing...";
        static string wslStatusStr = "Pending...", ramUsage = "Awaiting WSL...", wslDiskSize = "Computing...", vNetStats = "Tx: 0 KB/s | Rx: 0 KB/s";
        static string jobId = "Pending...", containerStatus = "Pending...", workTime = "Computing...";
        static string txtCpu = "Computing...", txtGpu = "Computing...", txtRam = "Computing...";
        static string minerStatus = "[grey]Idle[/]", bandwidthStatus = "[grey]Idle[/]";
        static string lastWarning = "No recent errors detected in the current session.";

        // Human-readable Matrix status (Salad Backend)
        static string matrixStatus = "[grey]Waiting for matrix data...[/]";

        // Download and Unpacking Tracking Variables
        static bool isPullingState = false;
        static double totalPullingMB = 0;
        static double initialPercentTracker = -1;
        static double initialMbTracker = 0;
        static double lastEstimatedMB = 0;
        static string activeLayer = "N/A";
        static double layerProgress = 0.0;
        static double currentVmDownKbps = 0;
        static double globalProgress = 0.0; // <-- Added for smart tracking
        static double wslRamMB = 0.0;       // <-- Added to track RAM peak
        static Queue<double> speedHistory = new Queue<double>();

        static DateTime appStartTime = DateTime.Now;
        static DateTime saladStartTime = DateTime.MinValue;
        static string saladVersion = "Detecting...";

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
                                }
                                await Task.Delay(100);
                            }
                        }
                    });
            }
        }

        static void UpdateSaladInfo()
        {
            try
            {
                var processes = Process.GetProcessesByName("Salad");
                if (processes.Length > 0)
                {
                    DateTime oldestStart = DateTime.Now;
                    string foundPath = null;
                    foreach (var p in processes)
                    {
                        try { if (p.StartTime < oldestStart) { oldestStart = p.StartTime; foundPath = p.MainModule?.FileName; } }
                        catch { }
                    }
                    saladStartTime = oldestStart;
                    if (foundPath != null && File.Exists(foundPath))
                        saladVersion = FileVersionInfo.GetVersionInfo(foundPath).FileVersion;

                    if (saladVersion != "Unknown" && saladVersion != "Detecting...") return;
                }
                else saladStartTime = DateTime.MinValue;
            } catch { }

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Salad"))
                {
                    if (key != null) { saladVersion = key.GetValue("DisplayVersion")?.ToString() ?? "Unknown"; return; }
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

            // 1. Matrix State
            var matchMatrix = Regex.Match(line, @"Received desired state from matrix - (\d+) workloads");
            if (matchMatrix.Success)
            {
                if (int.TryParse(matchMatrix.Groups[1].Value, out int wCount))
                {
                    if (wCount == 0)
                    {
                        matrixStatus = "[grey]Idle - Searching for jobs...[/]";
                        if (containerStatus.Contains("Running"))
                        {
                            containerStatus = "[grey]Stopped / Waiting[/]";
                            isPullingState = false;
                        }
                    }
                    else
                    {
                        matrixStatus = $"[bold green]Job Acquired! ({wCount} active workload)[/]";
                    }
                }
            }

            // 2. Wallet
            var matchWallet = Regex.Match(line, @"Wallet: Current\((.*?)\), Predicted\((.*?)\)");
            if (matchWallet.Success)
            {
                balance = matchWallet.Groups[1].Value; projected = matchWallet.Groups[2].Value; lastWalletUpdate = timestampLog;
            }

            // 3. Container and ID
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

            // 4. Download Progress
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
                        // 1. DROP DETECTION (Layer Change / Phase Reset)
                        if (initialPercentTracker == -1 || percentage < globalProgress)
                        {
                            initialPercentTracker = percentage;
                            initialMbTracker = totalPullingMB; 
                        }

                        double deltaPercentage = percentage - initialPercentTracker;
                        double deltaMB = totalPullingMB - initialMbTracker;

                        // 2. MATHEMATICAL SAFETY MARGIN
                        if (deltaPercentage >= 1.0 && deltaMB >= 10)
                        {
                            double phaseEstimate = (deltaMB * 100.0) / deltaPercentage;
                            lastEstimatedMB = initialMbTracker + phaseEstimate;
                        }
                    }

                    globalProgress = percentage;

                    // ====================================================================
                    // HERE LIES YOUR BRILLIANT INSIGHT:
                    // ====================================================================
                    double absoluteDownloadedMB = totalPullingMB; // Starts with the physical value read from the network interface (fallback)

                    if (lastEstimatedMB > 0)
                    {
                        // If we already have the total estimate, we calculate the real value based on the current %!
                        // Example: Total is 13.26 GB * 24% = 3.18 GB actually downloaded.
                        double calculatedDownload = (lastEstimatedMB * percentage) / 100.0;
                        
                        // We use the MAX value between the calculated one and the one read from the network interface.
                        // This ensures that if the app opened from scratch with Salad, we show the exact traffic.
                        // If the app opened later, "calculatedDownload" will be greater and take over.
                        absoluteDownloadedMB = Math.Max(calculatedDownload, totalPullingMB);
                    }
                    // ====================================================================

                    string etaStr = "";
                    if (lastEstimatedMB > 0)
                    {
                        double remainingMB = lastEstimatedMB - absoluteDownloadedMB;
                        if (remainingMB < 0) remainingMB = 0; 

                        // Takes the average of the last 10 readings instead of a 1-second spike
                        double avgSpeedKbps = speedHistory.Count > 0 ? speedHistory.Average() : currentVmDownKbps;

                        if (avgSpeedKbps > 0 && remainingMB > 0)
                        {
                            double speedMBps = avgSpeedKbps / 1024.0;
                            if (speedMBps > 0.1)
                            {
                                double remainingSecs = remainingMB / speedMBps;
                                if (remainingSecs > 86400) remainingSecs = 86400; // 24h limit
                                TimeSpan tSpan = TimeSpan.FromSeconds(remainingSecs);
                                
                                // Cleaner ETA (hides seconds if more than 1 hour remains)
                                if (tSpan.TotalHours >= 1)
                                    etaStr = $" | ETA: {tSpan.Hours}h {tSpan.Minutes}m";
                                else
                                    etaStr = $" | ETA: {tSpan.Minutes}m {tSpan.Seconds}s";
                            }
                        }
                    }

                    string physicalStr = absoluteDownloadedMB > 1024 ? $"{(absoluteDownloadedMB / 1024.0):F2} GB" : $"{Math.Round(absoluteDownloadedMB, 0)} MB";

                    // Only show total on screen if it makes mathematical sense
                    if (lastEstimatedMB > absoluteDownloadedMB)
                    {
                        string totalStr = lastEstimatedMB > 1024 ? $"{(lastEstimatedMB / 1024.0):F2} GB" : $"{Math.Round(lastEstimatedMB, 0)} MB";
                        physicalStr = $"{physicalStr} / {totalStr}";
                    }
                    else if (initialPercentTracker != -1)
                    {
                        physicalStr = $"{physicalStr} (Syncing Size...)";
                    }

                    containerStatus = $"[yellow]Global: {percentage}% | ¨Layer? [[{activeLayer}]] | DL: {physicalStr}{etaStr}[/]";
                }
            }

            if (line.Contains("Running(Ready") || line.Contains("already running") || line.Contains("already installed")) { containerStatus = "[green]Running (Stable)[/]"; isPullingState = false; globalProgress = 0.0; }
            else if (line.Contains("Killed") || line.Contains("Stopped") || line.Contains("failed")) 
            { 
                // If it errored out but we were already downloading (progress > 0), DO NOT RESET!
                if (globalProgress > 0 && globalProgress < 100)
                {
                    containerStatus = $"[darkorange]Network Hiccup / Retrying... (Frozen at {globalProgress}%)[/]";
                    // Keep isPullingState = true to avoid losing the math
                }
                else
                {
                    // Only reset if it truly wasn't downloading anything
                    isPullingState = false; 
                    containerStatus = "[grey]Stopped / Waiting[/]"; 
                    globalProgress = 0.0; 
                }
            }

            // 5. SGS Network
            var matchBandwidthNode = Regex.Match(line, @"(Bandwidth-[a-zA-Z0-9\-]+)");
            if (matchBandwidthNode.Success) { bandwidthStatus = "[magenta]Active[/]"; sgsNodeName = matchBandwidthNode.Groups[1].Value; }

            if (line.Contains("Stopping workload") && line.Contains("Bandwidth")) { bandwidthStatus = "[grey]Idle[/]"; sgsNodeName = "Waiting for node..."; }

            // 6. Errors
            if (line.Contains("[WRN]") || line.Contains("[ERR]") || line.Contains("failed"))
            {
                var matchError = Regex.Match(line, @"\]\s+(.*)");
                lastWarning = matchError.Success ? (matchError.Groups[1].Value.Length > 85 ? matchError.Groups[1].Value.Substring(0, 82) + "..." : matchError.Groups[1].Value) : line;
            }

            if (line.Contains("Heartbeat"))
            {
                lastLogHeartbeat = DateTime.Now;
            }

            // 7. WSL Disk
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

        static void UpdateHostHardware()
        {
            try
            {
                string[] minerNames = { "t-rex", "gminer", "srbminer-multi", "xmrig", "nbminer", "lolminer", "miner" };
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
            catch (Exception)
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
                    if (!string.IsNullOrEmpty(output)) { var parts = output.Split(','); if (parts.Length >= 4) txtGpu = $"{parts[0].Trim().Replace("NVIDIA GeForce ", "")} (Load: {parts[1].Trim()}% | Pwr: {parts[2].Trim()}W | Temp: {parts[3].Trim()}>C)"; }                
                }
            }
            catch { txtGpu = "GPU WMI/NVIDIA-SMI Not Available"; }
        }

        static void UpdateWSLData()
        {
            try
            {
                long ramTotalBytes = Process.GetProcesses().Where(p => p.ProcessName == "vmmemWSL" || p.ProcessName == "vmmem" || p.ProcessName == "wslhost").Sum(p => p.WorkingSet64);
                wslRamMB = ramTotalBytes / 1048576.0; // <-- Saves the RAM value in MB to the global variable
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
            var grid = new Grid(); grid.AddColumn(new GridColumn().NoWrap());

            string fileName = Path.GetFileName(filePath); 
            TimeSpan appUptime = DateTime.Now - appStartTime; string formattedAppUptime = $"{(int)appUptime.TotalHours:D2}:{appUptime.Minutes:D2}:{appUptime.Seconds:D2}";
            string formattedSaladUptime = saladStartTime != DateTime.MinValue ? $"{(int)(DateTime.Now - saladStartTime).TotalHours:D2}:{(DateTime.Now - saladStartTime).Minutes:D2}:{(DateTime.Now - saladStartTime).Seconds:D2}" : "Offline";

            string asciiArt = $"[bold #BCE70C]  ____        _           _   __  _  __                 \n / ___|  __ _| | __ _  __| |  \\ \\/ /|  _ \\ __ _ _   _   \n \\___ \\ / _` | |/ _` |/ _` |   \\  / | |_) / _` | | | |  \n  ___) | (_| | | (_| | (_| |   /  \\ |  _ < (_| | |_| |  \n |____/ \\__,_|_|\\__,_|\\__,_|  /_/\\_\\|_| \\_\\__,_|\\__, |  \n                                                |___/   \n[/]";
            string rawStatusLine = $"[gray]Salad [white]{saladVersion}[/] (UP: [white]{formattedSaladUptime}[/]) | Log: {fileName} | Xray: [white]{formattedAppUptime}[/][/]";
            string truncatedStatusLine = TruncateWithColors(rawStatusLine, Math.Max(10, AnsiConsole.Profile.Width - 6));
            var markupHeader = new Markup(asciiArt + truncatedStatusLine + "\n[bold #006400][[ESC]] Exit[/]").Overflow(Overflow.Crop);

            grid.AddRow(new Panel(new Align(markupHeader, HorizontalAlignment.Center)).BorderColor(Color.FromHex("#006400")));
            grid.AddRow(CreateSection("GLOBAL SYSTEM & WALLET", new Dictionary<string, string> { { ":money_bag: WALLET", $"[bold green]${balance}[/] | 24H EST: [bold yellow]${projected}[/] | UPDATED: {lastUpdateTimer}" } }));

            string wslColor = wslStatusStr.Contains("Running") ? "green" : wslStatusStr.Contains("STOPPED") ? "red" : "yellow";
            grid.AddRow(CreateSection("LINUX WSL (VIRTUAL MACHINE)", new Dictionary<string, string> { { ":desktop_computer: VM STATUS", $"[{wslColor}]{wslStatusStr}[/]" }, { ":floppy_disk: VM RAM", ramUsage }, { ":optical_disk: VM DISK", wslDiskSize }, { ":satellite_antenna: VM LAN", vNetStats } }));

            string heart = "❤️"; 
            if ((DateTime.Now - lastLogHeartbeat).TotalMinutes < 2)
            {
                heart = (DateTime.Now.Millisecond < 500) ? "❤️" : "";
            }

            string matrixStatusWithHeart = $"{matrixStatus} {heart}";

            // === THE DISPLAY MAGIC HAPPENS HERE ===
            string displayContainerStatus = containerStatus;
            if (isPullingState && globalProgress >= 98.0 && currentVmDownKbps < 1024 && wslRamMB > 800)
            {
                displayContainerStatus = $"[cyan]Unpacking / Extracting... (WSL RAM Spike: {wslRamMB:N0} MB | Low Net I/O)[/]";
            }

            grid.AddRow(CreateSection("SALAD CONTAINER WORKLOAD", new Dictionary<string, string> {
                { ":satellite: MATRIX STATE", matrixStatusWithHeart },
                { ":id_button: WORKLOAD ID", jobId },
                { ":package: CONTAINER", displayContainerStatus }, // <-- Uses the modified string here
                { ":stopwatch: UPTIME", workTime }
            }));

            grid.AddRow(CreateSection("GLOBAL HARDWARE (HOST)", new Dictionary<string, string> { { ":gear: HOST CPU", txtCpu }, { ":fire: HOST GPU", txtGpu }, { ":bar_chart: HOST RAM", txtRam } }));

            var hostWorkloads = new Dictionary<string, string> { { ":pick: GPU MINER", minerStatus }, { ":globe_with_meridians: SGS NODE", bandwidthStatus.Contains("Idle") ? "[grey]Idle[/]" : $"[magenta]{sgsNodeName}[/]" } };
            if (!bandwidthStatus.Contains("Idle")) { hostWorkloads.Add(":satellite_antenna: SGS I/O", sgsDetails); hostWorkloads.Add(":chart_increasing: SGS TRAFFIC", $"[magenta]{sgsTotalTraffic}[/]"); }
            grid.AddRow(CreateSection("WINDOWS HOST WORKLOADS", hostWorkloads));

            var logsArray = recentLogs.ToArray();
            var errorTable = new Table().HideHeaders().Border(TableBorder.None).Expand();
            errorTable.AddColumn(new TableColumn("Label").Width(15).NoWrap()); errorTable.AddColumn(new TableColumn("Value").NoWrap());
            errorTable.AddRow(new Markup("[white]:warning: Last Error[/]"), new Markup($"[white]:[/] {TruncateWithColors($"[red]{Markup.Escape(lastWarning)}[/]", Math.Max(10, AnsiConsole.Profile.Width - 32))}"));

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
            foreach (var item in items) table.AddRow(new Markup($"[white]{Markup.Escape(item.Key ?? "")}[/]"), new Markup($"[white]:[/] {TruncateWithColors(item.Value ?? "", Math.Max(10, AnsiConsole.Profile.Width - 32))}"));
            return new Panel(table).Header($"[cyan][[ {Markup.Escape(title)} ]][/]").BorderColor(Color.Cyan).Expand();
        }
    }
}


