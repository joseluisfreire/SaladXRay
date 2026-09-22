#nullable disable
#pragma warning disable CA1416

using System;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Microsoft.Win32;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Reflection;

namespace SaladXRayPanel
{
    partial class Program
    {
        // ==========================================
        // STATE VARIABLES
        // ==========================================

        // AMD GPU LOAD
        static List<PerformanceCounter> amdGpuCounters = null;
        static bool amdCountersInitFailed = false;
        static bool nvidiaSmiFailed = false;

        static DateTime wslStartTime = DateTime.MinValue;

        // SGS - portado 1:1 do EnvironmentService.cs
        static DateTime sgsStartTime = DateTime.MinValue;
        static string sgsStatus = "Offline";
        static double sgsCpuUsagePct = 0;
        static double sgsRamMB2 = 0; // MB via WorkingSetPrivate (WMI), não confundir com sgsTotalRxMB/Tx existentes
        static string sgsNetworkSpeed = "0 bps";

        static readonly Dictionary<string, (ulong PercentProcessorTime, ulong TimestampSys100NS)> _sgsCpuHistory = new();
        static PerformanceCounter _sgsNetCounter = null;
        static int _sgsNetCounterPid = -1;

        static string _categoryName = null;
        static string _counterName = null;
        static string _idCounterName = null;

        static string balance = "Computing...", projected = "Computing...", lastUpdateTimer = "Computing...";
        static string wslStatusStr = "Pending...", ramUsage = "Awaiting WSL...", wslDiskSize = "Computing...", vNetStats = "Tx: 0 KB/s | Rx: 0 KB/s";
        static string jobId = "Pending...", containerStatus = "Pending...", workTime = "Computing...";
        static string txtCpu = "Computing...", txtGpu = "Computing...", txtRam = "Computing...";
        static string txtDisk = "Computing...";
        static readonly Queue<(DateTime Timestamp, string Message)> errorHistory = new();
        const int MaxErrorHistory = 5;
        static bool showErrorHistory = false; // toggle [E]		
        static DateTime lastErrorClearTime = DateTime.Now; // referência "sem incidentes há..."
        //real time clock 
        static bool showLiveClockMode = false; // toggle [P]
        static double baseCpuClockGHz = -1;

        // CPU Telemetry (Fiel ao HardwareService + Task Manager)
        static ulong lastIdleTime = 0, lastKernelTime = 0, lastUserTime = 0;
        static bool cpuTimesInitialized = false;
        static string cachedCpuName = "CPU";
        static double cpuFrequencyGHz = 0.0;
        static string cpuVirtualizationStatus = "Detecting...";
        private const int PF_VIRT_FIRMWARE_ENABLED = 21;

        [DllImport("kernel32.dll")]
        private static extern bool IsProcessorFeaturePresent(int processorFeature);
        [StructLayout(LayoutKind.Sequential)]
        struct PROCESSOR_POWER_INFORMATION
        {
            public uint Number;
            public uint MaxMhz;
            public uint CurrentMhz;
            public uint MhzLimit;
            public uint MaxIdleState;
            public uint CurrentIdleState;
        }

        [DllImport("powrprof.dll")]
        private static extern uint CallNtPowerInformation(
            int informationLevel, IntPtr lpInputBuffer, uint nInputBufferSize,
            IntPtr lpOutputBuffer, uint nOutputBufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(
            out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

        private const int ProcessorInformation = 11;

        private static ulong FileTimeToUlong(System.Runtime.InteropServices.ComTypes.FILETIME ft)
            => ((ulong)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

        static double GetCurrentCpuFrequencyGHz()
        {
            try
            {
                int coreCount = Environment.ProcessorCount;
                int structSize = Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>();
                int totalSize = structSize * coreCount;
                IntPtr buffer = Marshal.AllocHGlobal(totalSize);
                try
                {
                    uint result = CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buffer, (uint)totalSize);
                    if (result != 0) return 0;

                    double sumMhz = 0;
                    double sumMaxMhz = 0;
                    for (int i = 0; i < coreCount; i++)
                    {
                        IntPtr ptr = IntPtr.Add(buffer, i * structSize);
                        var info = Marshal.PtrToStructure<PROCESSOR_POWER_INFORMATION>(ptr);
                        sumMhz += info.CurrentMhz;
                        sumMaxMhz += info.MaxMhz;
                    }

                    // Cacheia o clock nominal (base) só na primeira vez
                    if (baseCpuClockGHz <= 0)
                        baseCpuClockGHz = (sumMaxMhz / coreCount) / 1000.0;

                    return (sumMhz / coreCount) / 1000.0;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch { return 0; }
        }

        static double GetLiveClockViaWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
                using var collection = searcher.Get();

                foreach (ManagementObject obj in collection)
                {
                    using (obj)
                    {
                        double perfPercent = Convert.ToDouble(obj["PercentProcessorPerformance"] ?? 0);
                        if (baseCpuClockGHz <= 0) return 0; // ainda não populado, evita cálculo errado
                        return baseCpuClockGHz * (perfPercent / 100.0);
                    }
                }
            }
            catch { }
            return 0;
        }

        static double GetCurrentCpuLoadPercent()
        {
            try
            {
                if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
                    return 0;

                ulong idle = FileTimeToUlong(idleFt);
                ulong kernel = FileTimeToUlong(kernelFt);
                ulong user = FileTimeToUlong(userFt);

                if (!cpuTimesInitialized)
                {
                    lastIdleTime = idle; lastKernelTime = kernel; lastUserTime = user;
                    cpuTimesInitialized = true;
                    return 0;
                }

                ulong idleDiff = idle - lastIdleTime;
                ulong kernelDiff = kernel - lastKernelTime;
                ulong userDiff = user - lastUserTime;

                lastIdleTime = idle; lastKernelTime = kernel; lastUserTime = user;

                ulong totalDiff = kernelDiff + userDiff;
                if (totalDiff == 0) return 0;

                double load = (double)(totalDiff - idleDiff) / totalDiff * 100.0;
                return Math.Clamp(load, 0, 100);
            }
            catch { return 0; }
        }

        //percent
        static double currentCpuLoadPct = 0;
        static double currentGpuLoadPct = 0;
        static double currentRamLoadPct = 0;
        static double currentDiskLoadPct = 0;

        // --- VHDX Telemetry & LED (portado do VmService.cs) ---
        static readonly string VhdxPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Salad", "wsl", "ext4.vhdx");
        static long lastVhdxSize = -1;
        static DateTime lastVhdxCheckTime = DateTime.MinValue;
        static DateTime lastVhdxWriteTime = DateTime.MinValue;
        static DateTime lastDiskWriteAt = DateTime.MinValue;
        static bool isDiskLedActive = false;
        static double vhdxGrowthRateMBs = 0;
        static DateTime lastVhdxIoCheck = DateTime.MinValue;

        // windows detect
        static string osDisplayName = "Detecting...";

        // Human-readable Matrix status (Salad Backend)
        static string matrixStatus = "[grey]Waiting for matrix data...[/]";

        // GPU Demand Tracking Variables
        static string gpuDemandStatus = "[grey]Initializing WMI/API...[/]";
        static string gpuEarning24h = "[grey]Waiting...[/]";
        static string gpuDemandTier = "[grey]Waiting...[/]";
        static string gpuNetworkUtil = "[grey]Waiting...[/]";
        static DateTime lastGpuDemandUpdate = DateTime.MinValue;
        static bool isFetchingDemand = false;
        static readonly HttpClient httpClient = CreateHttpClient();
        static readonly DemandService demandService = new(httpClient);

        // Novatech (nova fonte de dados)
        static string novaTier = null;
        static double? novaUtil = null;
        static double? novaMinEarning = null;
        static double? novaMaxEarning = null;
        static string novaMatchStatus = "[grey]Waiting...[/]";
        static bool isNovaOnline = false;
        static bool useNovaDemandView = false; // toggle [N]

        static readonly string SaladLogDirectory = ResolveSaladLogDirectory();

        static string ResolveSaladLogDirectory()
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var candidate = Path.Combine(programData, "Salad", "logs");
            if (Directory.Exists(candidate))
                return candidate;

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Salad Technologies\Salad");
                var installPath = key?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(installPath))
                {
                    var regCandidate = Path.Combine(installPath, "logs");
                    if (Directory.Exists(regCandidate))
                        return regCandidate;
                }
            }
            catch { }

            return candidate; // fallback, mesmo sem existir ainda
        }

        static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 SaladXRayPanel/1.0");
            return client;
        }

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
        //ram
        static string ramExtraInfo = "";
        static bool ramInfoLoaded = false;
        static bool _ramCounterInitFailed = false;

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

        static string workloadHardwareType = "[grey]N/A[/]";
        static string computeWorkloadType = null;
        static bool isBandwidthActive = false;

        static DateTime lastWalletUpdate = DateTime.MinValue;
        static DateTime jobStartTime = DateTime.MinValue;
        static DateTime lastLogHeartbeat = DateTime.MinValue;

        class LogItem
        {
            public string FormattedText;
            public DateTime Timestamp;
            public bool IsError;
        }

        static List<LogItem> recentLogs = new List<LogItem>
        {
            new LogItem { FormattedText = "[grey]Awaiting logs...[/]", Timestamp = DateTime.Now, IsError = false }
        };
        static long lastLogPosition = 0;

        static int lastKnownWidth = -1;
        static int lastKnownHeight = -1;

        static bool DetectConsoleResize()
        {
            int w = Console.WindowWidth;
            int h = Console.WindowHeight;

            if (lastKnownWidth == -1) { lastKnownWidth = w; lastKnownHeight = h; return false; }

            if (w != lastKnownWidth || h != lastKnownHeight)
            {
                lastKnownWidth = w;
                lastKnownHeight = h;
                return true;
            }
            return false;
        }

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

            string logsFolder = SaladLogDirectory + Path.DirectorySeparatorChar;

            RestoreInitialState(logsFolder);
            UpdateSaladInfo();
            DetectWindowsVersion();
            InitializeCpuTelemetry();

            while (true)
            {
                string logFile = GetMostRecentLogFile(logsFolder);
                if (logFile == null) logFile = $"log-{DateTime.Now:yyyyMMdd}.txt (Not Found)";

                Console.Clear();
                Console.SetCursorPosition(0, 0);

                // Um único Live para a vida toda do app - nunca recriado
                await AnsiConsole.Live(RenderPanel(logFile))
                    .Cropping(VerticalOverflowCropping.Bottom)
                    .StartAsync(async ctx =>
                    {
                        int loopCounter = 0;

                        while (true)
                        {
                            try
                            {
                                string foundLog = GetMostRecentLogFile(logsFolder);
                                if (foundLog != null)
                                {
                                    logFile = foundLog;
                                    ReadSaladLogs(logFile);
                                }

                                if (loopCounter % 2 == 0)
                                {
                                    UpdateNetwork();
                                    UpdateSaladInfo();
                                    _ = FetchGpuDemandDataAsync();
                                }

                                if (loopCounter % 2 == 1)
                                {
                                    UpdateHostHardware();
                                }

                                if (loopCounter % 10 == 0)
                                {
                                    UpdateWSLData();
                                    UpdateVhdxDiskUsage();
                                    UpdateSgsInfo();
                                    UpdateSgsCpuRam();
                                }

                                CalculateUptime();
                                if (DetectConsoleResize())
                                {
                                    //Console.Clear();
                                    //Console.SetCursorPosition(0, 0);
                                }

                                var panel = RenderPanel(logFile);
                                ctx.UpdateTarget(panel);
                            }
                            catch
                            {
                                // Ignora falhas pontuais (WMI travando, rede caindo, log rotacionando, etc.)
                                // sem nunca destruir o Live ou limpar a tela.
                            }

                            loopCounter++;

                            // Check keyboard input
                            for (int i = 0; i < 10; i++)
                            {
                                try
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
                                        else if (key.Key == ConsoleKey.S)
                                        {
                                            showSupportTab = !showSupportTab;
                                            ctx.UpdateTarget(RenderPanel(logFile));
                                        }
                                        else if (key.Key == ConsoleKey.N)
                                        {
                                            useNovaDemandView = !useNovaDemandView;
                                            ctx.UpdateTarget(RenderPanel(logFile));
                                        }
                                        else if (key.Key == ConsoleKey.E)
                                        {
                                            showErrorHistory = !showErrorHistory;
                                            ctx.UpdateTarget(RenderPanel(logFile));
                                        }
                                        else if (key.Key == ConsoleKey.C)
                                        {
                                            errorHistory.Clear();
                                            lastErrorClearTime = DateTime.Now;
                                            ctx.UpdateTarget(RenderPanel(logFile));
                                        }
                                        else if (key.Key == ConsoleKey.P)
                                        {
                                            showLiveClockMode = !showLiveClockMode;
                                            ctx.UpdateTarget(RenderPanel(logFile));
                                        }
                                    }
                                }
                                catch { /* ignora erro pontual de leitura de teclado */ }

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

                    // 1. NOVATECH primeiro (base maior) - resolve nome canônico
                    string canonicalName = localGpuName;
                    var novaMatch = await demandService.FetchNovatechAsync(localGpuName);

                    if (novaMatch != null)
                    {
                        canonicalName = novaMatch.DisplayName;
                        novaTier = novaMatch.DemandTier;
                        novaUtil = novaMatch.Utilization;
                        novaMinEarning = novaMatch.MinEarningRate;
                        novaMaxEarning = novaMatch.MaxEarningRate;
                        novaMatchStatus = $"[bold green]{novaMatch.DisplayName}[/]";
                        isNovaOnline = true;
                    }
                    else
                    {
                        novaMatchStatus = $"[darkorange]Not Listed on Novatech[/] [grey]({localGpuName})[/]";
                        isNovaOnline = false;
                    }

                    // 2. SALAD usa o nome canônico da Novatech (fallback: nome local)
                    var myGpu = await demandService.FetchSaladGpuAsync(canonicalName);

                    if (myGpu != null)
                    {
                        gpuDemandStatus = $"[bold green]{myGpu.DisplayName}[/]";
                        gpuDemandTier = $"[cyan]{myGpu.DemandTierName}[/] (Recommended Host RAM: {myGpu.RecommendedSpecs?.RamGb}GB)";

                        double realBusyPct = Math.Clamp(myGpu.UtilizationPct, 0, 100);
                        gpuNetworkUtil = $"[yellow]{Math.Round(realBusyPct, 1)}%[/] of active machines working";

                        if (myGpu.EarningRates != null)
                        {
                            double avg24h = myGpu.EarningRates.AvgEarningRate * 24;
                            double max24h = myGpu.EarningRates.MaxEarningRate * 24;
                            gpuEarning24h = $"Avg: [bold green]${avg24h:F2}[/] / Max Pico: [bold green]${max24h:F2}[/]";
                        }
                    }
                    else
                    {
                        gpuDemandStatus = $"[darkorange]Not Listed[/] [grey]({localGpuName})[/]";
                        gpuDemandTier = "[grey]Low/No Demand[/]";
                        gpuNetworkUtil = "[grey]N/A[/]";
                        gpuEarning24h = "[grey]N/A[/]";
                    }
                });

                lastGpuDemandUpdate = DateTime.Now;
            }
            catch
            {
                gpuDemandStatus = "[red]API Offline or Error[/]";
                gpuDemandTier = "[grey]N/A[/]";
            }
            finally
            {
                isFetchingDemand = false;
            }
        }

        static void DetectWindowsVersion()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                string productName = key?.GetValue("ProductName")?.ToString() ?? "Windows";
                string buildStr = key?.GetValue("CurrentBuildNumber")?.ToString() ?? "0";
                int build = int.TryParse(buildStr, out int b) ? b : 0;

                // Windows 11 reporta build >= 22000, mas o registro ainda chama de "Windows 10"
                if (build >= 22000)
                {
                    productName = productName.Replace("Windows 10", "Windows 11");
                    isLegacyEmojiMode = false;
                }
                else
                {
                    isLegacyEmojiMode = true;
                }

                string displayVersion = key?.GetValue("DisplayVersion")?.ToString() ?? "";
                osDisplayName = string.IsNullOrEmpty(displayVersion)
                    ? $"{productName} (Build {build})"
                    : $"{productName} {displayVersion} (Build {build})";
            }
            catch
            {
                osDisplayName = "Unknown Windows";
                isLegacyEmojiMode = false;
            }
        }

        static void UpdateSaladInfo()
        {
            Process[] allProcesses = null;
            try
            {
                using var currentProc = Process.GetCurrentProcess();
                int myProcessId = currentProc.Id;

                allProcesses = Process.GetProcesses();
                var processes = allProcesses
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
                                    using (var results = searcher.Get())
                                    {
                                        foreach (ManagementObject obj in results)
                                        {
                                            using (obj)
                                            {
                                                string wmiDate = obj["CreationDate"]?.ToString();
                                                if (!string.IsNullOrEmpty(wmiDate)) safeStartTime = ManagementDateTimeConverter.ToDateTime(wmiDate);
                                            }
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
            finally
            {
                if (allProcesses != null)
                    foreach (var p in allProcesses) p.Dispose();
            }

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

        static bool DetectVirtualizationEnabled()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem");
                using var collection = searcher.Get();
                foreach (ManagementObject obj in collection)
                {
                    using (obj)
                    {
                        if (obj["HypervisorPresent"] is bool hp)
                            return hp;
                    }
                }
            }
            catch { }

            return false; // fallback conservador se a query falhar
        }

        static void InitializeCpuTelemetry()
        {
            GetCurrentCpuLoadPercent();
            GetCurrentCpuFrequencyGHz();

            // 2. Cache do Nome da CPU
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                using var collection = searcher.Get();
                foreach (ManagementObject obj in collection)
                {
                    using (obj)
                    {
                        cachedCpuName = obj["Name"]?.ToString()?.TrimEnd() ?? "CPU";
                        break;
                    }
                }
            }
            catch { cachedCpuName = "CPU"; }

            // 3. Detecção de Virtualização
            try
            {
                bool isEnabled = DetectVirtualizationEnabled();
                cpuVirtualizationStatus = isEnabled ? "[green]VT: ON[/]" : "[red]VT: OFF[/]";
            }
            catch
            {
                cpuVirtualizationStatus = "[grey]VT: N/A[/]";
            }
        }

        static void UpdateWSLData()
        {
            try
            {
                // 1. CAPTURA DO UPTIME REAL DA VM (vmmem) DIRETO DO KERNEL VIA WMI
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
                                {
                                    oldestVmTime = dt;
                                }
                            }
                        }
                    }

                    wslStartTime = oldestVmTime ?? DateTime.MinValue;
                }
                catch
                {
                    wslStartTime = DateTime.MinValue;
                }

                // 2. RAM DA VM (WorkingSet apenas dos processos do WSL)
                var wslProcs = Process.GetProcesses();
                try
                {
                    long ramTotalBytes = wslProcs
                        .Where(p => p.ProcessName.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase) 
                                 || p.ProcessName.StartsWith("wsl", StringComparison.OrdinalIgnoreCase))
                        .Sum(p => p.WorkingSet64);

                    wslRamMB = ramTotalBytes / 1048576.0;
                    ramUsage = wslRamMB > 0 ? $"{wslRamMB:N1} MB" : "Awaiting WSL...";
                }
                finally
                {
                    foreach (var p in wslProcs) p.Dispose();
                }

                // 3. STATUS DA DISTRO VIA WSL CLI
                ProcessStartInfo psi = new ProcessStartInfo 
                { 
                    FileName = "wsl.exe", 
                    Arguments = "-l -v", 
                    RedirectStandardOutput = true, 
                    UseShellExecute = false, 
                    CreateNoWindow = true, 
                    StandardOutputEncoding = System.Text.Encoding.Unicode 
                };

                using (Process proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd().Replace("\0", "");
                    var matchWsl = Regex.Match(output, @"salad-enterprise-linux\s+([A-Za-z]+)");
                    if (matchWsl.Success) 
                    { 
                        string state = matchWsl.Groups[1].Value; 
                        wslStatusStr = state.Contains("Running") ? "Running (Active)" : state.Contains("Stopped") ? "STOPPED (Offline)" : state; 
                    }
                }
            }
            catch 
            { 
                wslStatusStr = "Error reading WSL"; 
                wslRamMB = 0; 
            }

            if (wslStatusStr.Contains("STOPPED") || wslStatusStr.Contains("Offline") || wslStatusStr.Contains("Error") || wslStatusStr.Contains("Pending"))
            {
                if (containerStatus.Contains("Running"))
                {
                    containerStatus = "[yellow]Waiting for WSL...[/]";
                }
            }
        }

        static void UpdateVhdxDiskUsage()
        {
            // TRAVA 1: Se o WSL não está rodando, zero I/O no disco!
            if (!wslStatusStr.Contains("Running"))
            {
                isDiskLedActive = false;
                vhdxGrowthRateMBs = 0;
                return;
            }

            // TRAVA 2: Só checa disco a cada 4 segundos (elimina o 0,1 MB/s no Task Manager)
            if ((DateTime.Now - lastVhdxIoCheck).TotalSeconds < 4)
            {
                // Apaga o LED suavemente quando expira o pulso, sem encostar no arquivo
                if (isDiskLedActive && (DateTime.Now - lastDiskWriteAt).TotalMilliseconds > 600)
                    isDiskLedActive = false;

                return;
            }

            lastVhdxIoCheck = DateTime.Now;

            try
            {
                var info = new FileInfo(VhdxPath);
                if (!info.Exists) return;

                info.Refresh();
                long currentSize = info.Length;
                DateTime writeTime = info.LastWriteTimeUtc;
                DateTime now = DateTime.Now;

                if (lastVhdxSize >= 0 && lastVhdxCheckTime != DateTime.MinValue)
                {
                    double elapsedSec = (now - lastVhdxCheckTime).TotalSeconds;
                    if (elapsedSec > 0)
                    {
                        long deltaBytes = currentSize - lastVhdxSize;
                        vhdxGrowthRateMBs = Math.Max(0, (deltaBytes / elapsedSec) / (1024.0 * 1024.0));
                    }
                }

                lastVhdxSize = currentSize;
                lastVhdxCheckTime = now;

                if (lastVhdxWriteTime != DateTime.MinValue && writeTime > lastVhdxWriteTime)
                {
                    isDiskLedActive = true;
                    lastDiskWriteAt = now;
                }
                lastVhdxWriteTime = writeTime;

                // Só usa o arquivo como fallback se o log ainda não tiver entregue o DistroSize
                if (wslDiskSize == "Computing...")
                {
                    wslDiskSize = $"{(currentSize / 1073741824.0):N2} GB";
                }
            }
            catch
            {
                vhdxGrowthRateMBs = 0;
            }
        }

        static void InitializeCounterNames()
        {
            if (_categoryName != null) return;
            if (PerformanceCounterCategory.Exists("Processo"))
            {
                _categoryName = "Processo";
                _counterName = "Outros bytes de E/S/s";
                _idCounterName = "ID do processo";
            }
            else
            {
                _categoryName = "Process";
                _counterName = "IO Other Bytes/sec";
                _idCounterName = "ID Process";
            }
        }

        static string GetProcessInstanceNameUnified(int pid, string processName)
        {
            try
            {
                var cat = new PerformanceCounterCategory(_categoryName);
                string[] instances = cat.GetInstanceNames();

                foreach (var instance in instances)
                {
                    if (instance.StartsWith(processName, StringComparison.OrdinalIgnoreCase))
                    {
                        using var cnt = new PerformanceCounter(_categoryName, _idCounterName, instance, true);
                        if ((int)cnt.NextValue() == pid) return instance;
                    }
                }
            }
            catch { }
            return processName;
        }

        static string FormatNetworkSpeed(double bps)
        {
            if (bps < 0) bps = 0;
            if (bps < 1000) return $"{bps:F0} bps";
            if (bps < 1000000) return $"{bps / 1000.0:F1} Kbps";
            return $"{bps / 1000000.0:F1} Mbps";
        }

        static string CalculateSgsNetworkSpeed(Process sgsProcess)
        {
            if (sgsProcess == null)
            {
                _sgsNetCounter?.Dispose();
                _sgsNetCounter = null;
                _sgsNetCounterPid = -1;
                return "0 bps";
            }

            try
            {
                int pid = sgsProcess.Id;
                string processName = sgsProcess.ProcessName;

                if (_sgsNetCounter == null || _sgsNetCounterPid != pid)
                {
                    _sgsNetCounter?.Dispose();
                    string instance = GetProcessInstanceNameUnified(pid, processName);
                    _sgsNetCounter = new PerformanceCounter(_categoryName, _counterName, instance, true);
                    _sgsNetCounter.NextValue(); // aquecimento
                    _sgsNetCounterPid = pid;
                    return "0 bps";
                }

                float bytesPerSec = _sgsNetCounter.NextValue();
                double bps = bytesPerSec * 8.0;
                return FormatNetworkSpeed(bps);
            }
            catch
            {
                _sgsNetCounter?.Dispose();
                _sgsNetCounter = null;
                _sgsNetCounterPid = -1;
                return "0 bps";
            }
        }

        static void UpdateSgsInfo()
        {
            Process[] allProcesses = null;
            try
            {
                allProcesses = Process.GetProcesses();
                var sgsProcesses = allProcesses
                    .Where(p => {
                        try { return p.ProcessName.StartsWith("sgs", StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    })
                    .ToList();

                if (sgsProcesses.Count > 0)
                {
                    var oldestSgs = sgsProcesses.OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).FirstOrDefault();
                    if (oldestSgs != null)
                    {
                        DateTime? safeStartTime = null;
                        try { safeStartTime = oldestSgs.StartTime; }
                        catch
                        {
                            try
                            {
                                using (var searcher = new ManagementObjectSearcher($"SELECT CreationDate FROM Win32_Process WHERE ProcessId = {oldestSgs.Id}"))
                                using (var results = searcher.Get())
                                {
                                    foreach (ManagementObject obj in results)
                                        using (obj)
                                        {
                                            string wmiDate = obj["CreationDate"]?.ToString();
                                            if (!string.IsNullOrEmpty(wmiDate)) safeStartTime = ManagementDateTimeConverter.ToDateTime(wmiDate);
                                        }
                                }
                            }
                            catch { }
                        }
                        sgsStartTime = safeStartTime ?? DateTime.Now;
                        sgsStatus = "Online";
                    }

                    InitializeCounterNames();
                    sgsNetworkSpeed = CalculateSgsNetworkSpeed(oldestSgs);
                }
                else
                {
                    sgsStartTime = DateTime.MinValue;
                    sgsStatus = "Offline";
                    sgsNetworkSpeed = CalculateSgsNetworkSpeed(null);
                }
            }
            catch { }
            finally { if (allProcesses != null) foreach (var p in allProcesses) p.Dispose(); }
        }

        static void UpdateSgsCpuRam()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PercentProcessorTime, Timestamp_Sys100NS, WorkingSetPrivate FROM Win32_PerfRawData_PerfProc_Process WHERE Name LIKE 'sgs%'");

                double sgsCpuAccumulated = 0;
                ulong sgsRamBytesAccumulated = 0;
                int cores = Environment.ProcessorCount;
                var activeNames = new HashSet<string>();

                using var collection = searcher.Get();
                foreach (ManagementObject obj in collection)
                {
                    using (obj)
                    {
                        string name = obj["Name"]?.ToString();
                        if (string.IsNullOrEmpty(name)) continue;
                        if (name.Contains("XRay", StringComparison.OrdinalIgnoreCase)) continue;

                        activeNames.Add(name);

                        ulong currentRawCpu = Convert.ToUInt64(obj["PercentProcessorTime"] ?? 0);
                        ulong currentTimestamp = Convert.ToUInt64(obj["Timestamp_Sys100NS"] ?? 0);
                        ulong workingSetPrivate = Convert.ToUInt64(obj["WorkingSetPrivate"] ?? 0);

                        sgsRamBytesAccumulated += workingSetPrivate;

                        if (_sgsCpuHistory.TryGetValue(name, out var prev))
                        {
                            long deltaCpu = (long)currentRawCpu - (long)prev.PercentProcessorTime;
                            long deltaTicks = (long)currentTimestamp - (long)prev.TimestampSys100NS;

                            if (deltaTicks > 0 && deltaCpu >= 0)
                            {
                                double instancePct = ((double)deltaCpu / deltaTicks) * 100.0;
                                sgsCpuAccumulated += instancePct;
                            }
                        }

                        _sgsCpuHistory[name] = (currentRawCpu, currentTimestamp);
                    }
                }

                var staleKeys = _sgsCpuHistory.Keys.Where(k => !activeNames.Contains(k)).ToList();
                foreach (var key in staleKeys) _sgsCpuHistory.Remove(key);

                sgsCpuUsagePct = cores > 0 ? Math.Clamp(sgsCpuAccumulated / cores, 0, 100) : 0;
                sgsRamMB2 = sgsRamBytesAccumulated / (1024.0 * 1024.0);
            }
            catch
            {
                sgsCpuUsagePct = 0;
                sgsRamMB2 = 0;
            }
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

        static string BuildGenericWorkloadTag(string typedName)
        {
            var parts = typedName.Split(':', 2);
            string kind = parts[0].ToLowerInvariant();
            string name = parts.Length > 1 ? parts[1] : parts[0];

            string icon = kind switch
            {
                "miner" => ":pick:",
                "container" => ":package:",
                "compute" => ":gear:",
                _ => ":question_mark:"
            };

            return $"[yellow]{icon} {Markup.Escape(name)}[/]";
        }

        static string BuildWorkloadTypeDisplay(string compute, bool bandwidth)
        {
            string computePart = compute switch
            {
                "GPU" => "[bold orange1]:fire: GPU[/]",
                "CPU" => "[cyan]:gear: CPU[/]",
                string s when s.Contains(":") => BuildGenericWorkloadTag(s),
                _ => null
            };

            string bandwidthPart = bandwidth ? "[blue]:globe_with_meridians: Bandwidth[/]" : null;

            if (computePart != null && bandwidthPart != null)
                return $"{computePart} + {bandwidthPart}";
            if (computePart != null)
                return computePart;
            if (bandwidthPart != null)
                return bandwidthPart;

            return "[grey]N/A[/]";
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
                        const double SignificantDropThreshold = 5.0; // só reseta se cair mais que 5%

                        if (initialPercentTracker == -1 || percentage < (globalProgress - SignificantDropThreshold))
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

                    string visualBar = GenerateProgressBar(percentage);
                    containerStatus = $"{visualBar} [yellow]{percentage}% | {physicalStr}{etaStr}[/]";
                }
            }

            // Captura qualquer variação de container ativo/rodando do Salad e do Matrix
            if (line.Contains("Running(Ready", StringComparison.OrdinalIgnoreCase) || 
                line.Contains("[Running]", StringComparison.OrdinalIgnoreCase) || 
                line.Contains(":Running", StringComparison.OrdinalIgnoreCase) || 
                line.Contains("already running", StringComparison.OrdinalIgnoreCase) || 
                line.Contains("already installed", StringComparison.OrdinalIgnoreCase))
            {
                containerStatus = "[green]Running (Active)[/]";
                isPullingState = false;
                globalProgress = 100.0;
            }
            else if (line.Contains("Killed", StringComparison.OrdinalIgnoreCase) || 
                     line.Contains("Stopped workload", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Stopping workload", StringComparison.OrdinalIgnoreCase))
            {
                isPullingState = false;
                containerStatus = "[grey]Stopped / Waiting[/]";
                matrixStatus = "[grey]Idle - Searching for jobs...[/]";
                globalProgress = 0.0;
                computeWorkloadType = null;
                workloadHardwareType = BuildWorkloadTypeDisplay(computeWorkloadType, isBandwidthActive);
            }

            if (line.Contains("OrchestrationEvent: Workloads"))
            {
                isBandwidthActive = false;
                computeWorkloadType = null;
            }

            var matchBandwidthNode = Regex.Match(line, @"(Bandwidth-[a-zA-Z0-9\-]+)");
            if (matchBandwidthNode.Success)
            {
                isBandwidthActive = true;
                workloadHardwareType = BuildWorkloadTypeDisplay(computeWorkloadType, isBandwidthActive);
            }

            var matchWorkloadReceived = Regex.Match(line, @"Workload Received: \(([a-zA-Z0-9]+) -> (.+?)\s*,\s*(\w+)\)");
            if (matchWorkloadReceived.Success)
            {
                string wlShortId = matchWorkloadReceived.Groups[1].Value.Trim();
                string wlName    = matchWorkloadReceived.Groups[2].Value.Trim();
                string wlKind    = matchWorkloadReceived.Groups[3].Value.Trim().ToLowerInvariant();

                if (wlName.Contains("Bandwidth", StringComparison.OrdinalIgnoreCase))
                {
                    // SGS / Bandwidth - mantido isolado
                }
                else if (wlKind == "miner")
                {
                    computeWorkloadType = $"miner:{wlName}";
                    workloadHardwareType = BuildWorkloadTypeDisplay(computeWorkloadType, isBandwidthActive);
                    jobId = wlShortId;
                }
                else
                {
                    if (computeWorkloadType != "CPU")
                    {
                        computeWorkloadType = "GPU";
                    }

                    workloadHardwareType = BuildWorkloadTypeDisplay(computeWorkloadType, isBandwidthActive);
                    string cleanId = wlShortId.Length > 8 ? wlShortId.Substring(0, 8) : wlShortId;

                    if (jobId != cleanId)
                    {
                        jobId = cleanId;
                        if (!string.IsNullOrEmpty(jobId))
                            jobStartTime = FindRealJobStartTime(jobId, SaladLogDirectory + Path.DirectorySeparatorChar);

                        containerStatus = "Starting...";
                        initialPercentTracker = -1; 
                        initialMbTracker = 0; 
                        lastEstimatedMB = 0; 
                        totalPullingMB = 0; 
                        isPullingState = false;
                        globalProgress = 0.0;
                    }
                }
            }

            // =========================================================================
            // IDENTIFICAÇÃO DEFINITIVA DO HARDWARE (BLINDADA CONTRA MINERADOR)
            // =========================================================================
            bool isMiningLocked = computeWorkloadType != null && computeWorkloadType.StartsWith("miner:", StringComparison.OrdinalIgnoreCase);

            if (!isMiningLocked)
            {
                if (line.Contains("CPU HardwareCompatibility", StringComparison.OrdinalIgnoreCase))
                {
                    computeWorkloadType = "CPU";
                    workloadHardwareType = BuildWorkloadTypeDisplay(computeWorkloadType, isBandwidthActive);
                }
                else if (line.Contains("GPU HardwareCompatibility", StringComparison.OrdinalIgnoreCase))
                {
                    if (computeWorkloadType != "CPU")
                    {
                        computeWorkloadType = "GPU";
                        workloadHardwareType = BuildWorkloadTypeDisplay(computeWorkloadType, isBandwidthActive);
                    }
                }
                else if (string.IsNullOrEmpty(computeWorkloadType))
                {
                    if (line.Contains("miner", StringComparison.OrdinalIgnoreCase) && !line.Contains("sgs", StringComparison.OrdinalIgnoreCase))
                    {
                        computeWorkloadType = "MINER";
                        workloadHardwareType = BuildWorkloadTypeDisplay(computeWorkloadType, isBandwidthActive);
                    }
                }
            }

            if (line.Contains("[ERR]"))
            {
                var matchError = Regex.Match(line, @"\[ERR\]\s+(.*)");
                string rawMsg = matchError.Success ? matchError.Groups[1].Value : line;
                string trimmed = rawMsg.Length > 105 ? rawMsg.Substring(0, 102) + "..." : rawMsg;

                errorHistory.Enqueue((DateTime.Now, trimmed));
                if (errorHistory.Count > MaxErrorHistory)
                    errorHistory.Dequeue();
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
        }

        static void CalculateUptime()
        {
            if (wslStartTime != DateTime.MinValue && wslStatusStr.Contains("Running"))
            {
                workTime = FormatUptime(DateTime.Now - wslStartTime);
            }
            else
            {
                workTime = "[grey]Offline[/]";
            }

            if (lastWalletUpdate != DateTime.MinValue)
            {
                var diff = DateTime.Now - lastWalletUpdate;
                if (diff.TotalSeconds < 0) diff = TimeSpan.Zero;
                lastUpdateTimer = $"{(int)diff.TotalMinutes}m {diff.Seconds}s";
            }
        }

        static DriveInfo cachedDrive = null;
        static string cachedDriveRoot = null;
        static string cachedDiskFreeStr = null;
        static DateTime lastDiskFreeCheck = DateTime.MinValue;

        static string GetDiskFreeSpaceStr()
        {
            if (cachedDiskFreeStr != null && (DateTime.Now - lastDiskFreeCheck).TotalSeconds < 5)
                return cachedDiskFreeStr;

            try
            {
                string rootPath = Path.GetPathRoot(SaladLogDirectory) ?? "C:\\";
                if (cachedDrive == null || cachedDriveRoot != rootPath)
                {
                    cachedDrive = new DriveInfo(rootPath);
                    cachedDriveRoot = rootPath;
                }

                double freeGb = cachedDrive.AvailableFreeSpace / 1073741824.0;
                double freeRounded = Math.Round(freeGb, 1);

                string color = freeGb < 10 ? "red bold" : "white";
                cachedDiskFreeStr = $"[{color}]Free: {freeRounded} GB[/]";
            }
            catch
            {
                cachedDrive = null;
                cachedDiskFreeStr = "[grey]Free: N/A[/]";
            }

            lastDiskFreeCheck = DateTime.Now;
            return cachedDiskFreeStr;
        }

        static void LoadRamModuleInfo()
        {
            if (ramInfoLoaded) return;

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT SMBIOSMemoryType, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
                using var collection = searcher.Get();

                int ddrType = 0;
                uint configuredSpeed = 0;

                foreach (ManagementObject obj in collection)
                {
                    using (obj)
                    {
                        ddrType = Convert.ToInt32(obj["SMBIOSMemoryType"] ?? 0);
                        configuredSpeed = Convert.ToUInt32(obj["ConfiguredClockSpeed"] ?? 0);
                        break;
                    }
                }

                string ddrLabel = ddrType switch
                {
                    26 => "DDR4",
                    34 => "DDR5",
                    24 => "DDR3",
                    _  => "DDR?"
                };

                int jedecBase = ddrLabel switch
                {
                    "DDR3" => 2133,
                    "DDR4" => 2133,
                    "DDR5" => 4800,
                    _ => 0
                };

                string xmpLabel = (configuredSpeed > 0 && jedecBase > 0)
                    ? (configuredSpeed > jedecBase ? "[green]XMP ON[/]" : "[red]XMP OFF[/]")
                    : "[grey]XMP N/A[/]";

                ramExtraInfo = $"{ddrLabel} | {xmpLabel} | {configuredSpeed} MT/s";
            }
            catch
            {
                ramExtraInfo = "DDR? | XMP N/A";
            }

            ramInfoLoaded = true;
        }

        static void LoadDiskInfo()
        {
            if (isDiskInfoLoaded) return;

            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT MediaType, BusType, Size FROM MSFT_PhysicalDisk"))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementObject obj in collection)
                    {
                        using (obj)
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
            }
            catch { }

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Model, Size FROM Win32_DiskDrive"))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementObject obj in collection)
                    {
                        using (obj)
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
            }
            catch { isDiskInfoLoaded = true; }
        }

        static double GetAmdGpuUtilization()
        {
            try
            {
                if (amdGpuCounters == null)
                {
                    if (!PerformanceCounterCategory.Exists("GPU Engine"))
                        return -1;

                    var category = new PerformanceCounterCategory("GPU Engine");
                    var instanceNames = category.GetInstanceNames();

                    var validCounters = new List<PerformanceCounter>();
                    foreach (var name in instanceNames)
                    {
                        if (name.Contains("engtype_3D") || name.Contains("engtype_Compute"))
                        {
                            try
                            {
                                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                                counter.NextValue();
                                validCounters.Add(counter);
                            }
                            catch { }
                        }
                    }

                    if (validCounters.Count == 0)
                        return -1;

                    amdGpuCounters = validCounters;
                    return -1;
                }

                double total = 0;
                var aliveCounters = new List<PerformanceCounter>();

                foreach (var c in amdGpuCounters)
                {
                    try
                    {
                        total += c.NextValue();
                        aliveCounters.Add(c);
                    }
                    catch
                    {
                        c.Dispose();
                    }
                }

                if (aliveCounters.Count == 0 || aliveCounters.Count < amdGpuCounters.Count / 2)
                {
                    amdGpuCounters = null;
                }
                else
                {
                    amdGpuCounters = aliveCounters;
                }

                if (total > 100) total = 100;
                if (total < 0) total = 0;
                return Math.Round(total, 1);
            }
            catch
            {
                amdGpuCounters = null;
                return -1;
            }
        }

        static void UpdateHostHardware()
        {
            try
            {
                double cpuLoad = GetCurrentCpuLoadPercent();
                currentCpuLoadPct = Math.Round(cpuLoad, 0);

                cpuFrequencyGHz = showLiveClockMode
                    ? GetLiveClockViaWmi()
                    : GetCurrentCpuFrequencyGHz();

                string freqStr = cpuFrequencyGHz > 0 ? $"{cpuFrequencyGHz:F2} GHz" : "N/A";
                txtCpu = $"{cachedCpuName} | {freqStr} | {cpuVirtualizationStatus}";
            }
            catch
            {
                txtCpu = $"{cachedCpuName} | {cpuVirtualizationStatus}";
                currentCpuLoadPct = 0;
            }

            LoadRamModuleInfo();

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                using var collection = searcher.Get();

                foreach (ManagementObject obj in collection)
                {
                    using (obj)
                    {
                        double totalMb = Convert.ToDouble(obj["TotalVisibleMemorySize"] ?? 0) / 1024.0;
                        double freeMb = Convert.ToDouble(obj["FreePhysicalMemory"] ?? 0) / 1024.0;
                        double usedMb = totalMb - freeMb;

                        currentRamLoadPct = totalMb > 0 ? Math.Round((usedMb / totalMb) * 100.0, 0) : 0;
                        txtRam = $"{(usedMb / 1024.0):F1}G / {(totalMb / 1024.0):F1}G | {ramExtraInfo}";
                        break;
                    }
                }
            }
            catch
            {
                txtRam = ramExtraInfo;
                currentRamLoadPct = 0;
            }

            bool nvidiaSuccess = false;

            if (!nvidiaSmiFailed)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo 
                    { 
                        FileName = "nvidia-smi", 
                        Arguments = "--query-gpu=name,utilization.gpu,power.draw,temperature.gpu --format=csv,noheader,nounits", 
                        RedirectStandardOutput = true, 
                        UseShellExecute = false, 
                        CreateNoWindow = true 
                    };

                    using (Process proc = Process.Start(psi))
                    {
                        if (proc != null)
                        {
                            string output = proc.StandardOutput.ReadToEnd().Trim();
                            proc.WaitForExit();

                            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                            {
                                var parts = output.Split(',');
                                if (parts.Length >= 4)
                                {
                                    string smiName = parts[0].Trim().Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "");
                                    double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out currentGpuLoadPct);
                                    txtGpu = $"{smiName} | {parts[2].Trim()}W | {parts[3].Trim()}°C";
                                    nvidiaSuccess = true;
                                }
                            }
                        }
                    }

                    if (!nvidiaSuccess) nvidiaSmiFailed = true;
                }
                catch { nvidiaSmiFailed = true; }
            }

            if (!nvidiaSuccess)
            {
                string fallbackGpu = GetMiningGpuName() ?? "Unknown GPU";
                double amdLoad = GetAmdGpuUtilization();

                currentGpuLoadPct = amdLoad >= 0 ? amdLoad : 0;
                txtGpu = fallbackGpu;
            }

            LoadDiskInfo();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT PercentIdleTime, DiskReadBytesPersec, DiskWriteBytesPersec FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'"))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementObject obj in collection)
                    {
                        using (obj)
                        {
                            float idle = obj["PercentIdleTime"] != null ? Convert.ToSingle(obj["PercentIdleTime"]) : 100f;
                            float util = Math.Clamp(100f - idle, 0f, 100f);
                            currentDiskLoadPct = util;

                            float readB = obj["DiskReadBytesPersec"] != null ? Convert.ToSingle(obj["DiskReadBytesPersec"]) : 0;
                            float writeB = obj["DiskWriteBytesPersec"] != null ? Convert.ToSingle(obj["DiskWriteBytesPersec"]) : 0;

                            string readStr = readB >= 1048576 ? $"{(readB / 1048576):F1} MB/s" : $"{(readB / 1024):F1} KB/s";
                            string writeStr = writeB >= 1048576 ? $"{(writeB / 1048576):F1} MB/s" : $"{(writeB / 1024):F1} KB/s";

                            txtDisk = $"{hostDiskName} {GetDiskFreeSpaceStr()} | R: {readStr} W: {writeStr}";
                            break;
                        }
                    }
                }
            }
            catch
            {
                txtDisk = $"{hostDiskName} | {GetDiskFreeSpaceStr()}";
                currentDiskLoadPct = 0;
            }
        }

        static void AddLogToScreen(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            recentLogs.RemoveAll(x => x.IsError && (DateTime.Now - x.Timestamp).TotalSeconds > 60);

            string normalizedLine = line.Replace("\t", " ").TrimStart();

            const int MaxLineChars = 105;
            const int CutAt = 102;

            string shortLine = Regex.Replace(normalizedLine, @"^\d{4}-\d{2}-\d{2}\s+(\d{2}:\d{2}:\d{2})\.\d+\s+[-+]\d{2}:\d{2}\s+\[\w{3}\]\s+", "[$1] ");
            if (shortLine.Length > MaxLineChars) shortLine = shortLine.Substring(0, CutAt) + "...";

            bool isErr = line.Contains("[ERR]");
            string colorTag = isErr ? "red" : "grey";

            while (recentLogs.Count >= 5)
                recentLogs.RemoveAt(0);

            recentLogs.Add(new LogItem
            {
                FormattedText = $"[{colorTag}]{Markup.Escape(shortLine)}[/]",
                Timestamp = DateTime.Now,
                IsError = isErr
            });
        }

        private static readonly string[] AmdIntegratedPatterns = {
            "Radeon(TM) Graphics",
            "Radeon Graphics",
            "Radeon(TM) Vega",
            "Radeon Vega",
            "Radeon(TM) HD Graphics",
            "Radeon HD Graphics"
        };

        static bool IsAmdIntegrated(string gpuName)
        {
            foreach (var pattern in AmdIntegratedPatterns)
            {
                if (gpuName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        static string GetMiningGpuName()
        {
            try
            {
                var candidates = new List<string>();
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementObject obj in collection)
                    {
                        using (obj)
                        {
                            string gpuName = obj["Name"]?.ToString();
                            if (string.IsNullOrEmpty(gpuName)) continue;

                            bool isAmd = gpuName.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0
                                      || gpuName.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isNvidia = gpuName.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0
                                         || gpuName.IndexOf("GeForce", StringComparison.OrdinalIgnoreCase) >= 0;

                            if (!isAmd && !isNvidia) continue;

                            candidates.Add(gpuName);
                        }
                    }
                }

                if (candidates.Count == 0) return null;

                foreach (var gpuName in candidates)
                {
                    bool isAmd = gpuName.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isNvidia = gpuName.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (isAmd && !IsAmdIntegrated(gpuName))
                        return gpuName.Replace("Radeon ", "").Replace("radeon ", "").Trim();

                    if (isNvidia)
                        return gpuName.Replace("GeForce ", "").Replace("geforce ", "").Trim();
                }

                foreach (var gpuName in candidates)
                {
                    if (gpuName.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0)
                        return gpuName.Replace("Radeon ", "").Replace("radeon ", "").Trim();
                }

                return candidates[0];
            }
            catch { }
            return null;
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

        static string FormatUptime(TimeSpan ts)
        {
            if (ts.TotalDays >= 1)
                return $"{(int)ts.TotalDays}d {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }
}
