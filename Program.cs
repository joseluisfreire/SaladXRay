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
        static double currentGpuLoadPct = 0;
		static bool nvidiaSmiFailed = false;

		static DateTime wslStartTime = DateTime.MinValue;

		static bool showSupportTab = false; // toggle [S] entre About e Suporte
        static bool showHelpScreen = false; // Help/About screen control
        static FigletFont embeddedFont = null;

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

        // Fixed width for the Uptime panel (synchronized with the banner calculation)
        const int UPTIME_PANEL_WIDTH = 23;

        static string balance = "Computing...", projected = "Computing...", lastUpdateTimer = "Computing...";
        static string wslStatusStr = "Pending...", ramUsage = "Awaiting WSL...", wslDiskSize = "Computing...", vNetStats = "Tx: 0 KB/s | Rx: 0 KB/s";
        static string jobId = "Pending...", containerStatus = "Pending...", workTime = "Computing...";
        static string txtCpu = "Computing...", txtGpu = "Computing...", txtRam = "Computing...";
        static string txtDisk = "Computing...";
		static readonly Queue<(DateTime Timestamp, string Message)> errorHistory = new();
		const int MaxErrorHistory = 5;
		static bool showErrorHistory = false; // toggle [E]
		static DateTime lastErrorClearTime = DateTime.Now; // referência "sem incidentes há..."
		
	//windows detect
	static string osDisplayName = "Detecting...";
	static bool isLegacyEmojiMode = false;
	private static readonly Regex EmojiShortcodeRegex = new(@":[a-z0-9_]+:");
	// Fallback Unicode para emojis do Spectre em terminais legados (Windows 10)
	private static readonly Dictionary<string, string> _adaptIconCache = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Dictionary<string, string> EmojiFallbackMap = new(StringComparer.OrdinalIgnoreCase)
	{
		{ ":money_bag:", "💰" },
		{ ":desktop_computer:", "🖥" },
		{ ":stopwatch:", "⏱" },
		{ ":floppy_disk:", "💾" },
		{ ":optical_disk:", "💿" },
		{ ":satellite_antenna:", "📡" },
		{ ":satellite:", "🛰" },
		{ ":id_button:", "🆔" },
		{ ":package:", "📦" },
		{ ":gear:", "⚙" },
		{ ":fire:", "🔥" },
		{ ":bar_chart:", "📊" },
		{ ":gem_stone:", "💎" },
		{ ":chart_increasing:", "📈" },
		{ ":globe_with_meridians:", "🌐" },
		{ ":pick:", "⛏️" },
		{ ":warning:", "⚠" },
		{ ":broken_heart:", "💔" },
		{ ":red_heart:", "❤" },
		{ ":beating_heart:", "💓" },
	};	
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

	static string GenerateProgressBar(double percent, int barLength = 20)
	{
	    // Garante que a porcentagem fique entre 0 e 100
	    if (percent < 0) percent = 0;
	    if (percent > 100) percent = 100;

	    // Calcula quantos blocos "cheios" a barra vai ter
	    int filledBlocks = (int)Math.Round((percent / 100.0) * barLength);
	    int emptyBlocks = barLength - filledBlocks;

	    // Desenha a barra com caracteres Unicode
	    string filled = new string('█', filledBlocks);
	    string empty = new string('░', emptyBlocks);

	    // Retorna a barra colorida em verde e cinza usando o Markup do Spectre
	    return $"[green]{filled}[/][grey]{empty}[/]";
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

								if (loopCounter % 10 == 0)
								{
									UpdateHostHardware();
									UpdateWSLData();
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
									}
								}
								catch { /* ignora erro pontual de leitura de teclado */ }

								await Task.Delay(100);
							}
						}
					});
			}
		}

        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

        private static string NormalizeGpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            return WhitespaceRegex.Replace(name.Trim(), " ").ToUpperInvariant();
        }

        private static GpuDemandData FindMatchingGpu(string localGpuName, IEnumerable<GpuDemandData> apiItems)
        {
            if (string.IsNullOrWhiteSpace(localGpuName)) return null;
            var normalizedLocal = NormalizeGpuName(localGpuName);

            foreach (var item in apiItems)
            {
                if (NormalizeGpuName(item.Name) == normalizedLocal) return item;
                if (NormalizeGpuName(item.DisplayName) == normalizedLocal) return item;
                if (item.VariantNames != null)
                {
                    foreach (var variant in item.VariantNames)
                    {
                        if (!string.IsNullOrWhiteSpace(variant) && NormalizeGpuName(variant) == normalizedLocal)
                            return item;
                    }
                }
            }

            foreach (var item in apiItems)
            {
                var normName = NormalizeGpuName(item.Name);
                if (normName.Contains(normalizedLocal) || normalizedLocal.Contains(normName))
                    return item;
            }

            return null;
        }

        static NovatechGpuData FindNovatechMatch(string localGpuName, IEnumerable<NovatechGpuData> novaItems)
        {
            if (string.IsNullOrWhiteSpace(localGpuName)) return null;
            var normalizedLocal = NormalizeGpuName(localGpuName);

            foreach (var item in novaItems)
            {
                if (NormalizeGpuName(item.DisplayName) == normalizedLocal) return item;
            }

            foreach (var item in novaItems)
            {
                var normNova = NormalizeGpuName(item.DisplayName);
                if (normNova.Contains(normalizedLocal) || normalizedLocal.Contains(normNova))
                    return item;
            }

            return null;
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
                    try
                    {
                        string novaJson = await httpClient.GetStringAsync("https://salad-tools.novatech.gg/api/gpus");
                        var novaOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var novaResponse = JsonSerializer.Deserialize<NovatechResponse>(novaJson, novaOptions);

                        if (novaResponse?.Gpus != null)
                        {
                            var novaMatch = FindNovatechMatch(localGpuName, novaResponse.Gpus);
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
                        }
                        else isNovaOnline = false;
                    }
                    catch { isNovaOnline = false; }

                    // 2. SALAD usa o nome canônico da Novatech (fallback: nome local)
                    try
                    {
                        string json = await httpClient.GetStringAsync("https://app-api.salad.com/api/v2/demand-monitor/gpu");
                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var gpus = JsonSerializer.Deserialize<List<GpuDemandData>>(json, options);

                        var myGpu = FindMatchingGpu(canonicalName, gpus ?? new List<GpuDemandData>());

                        if (myGpu != null)
                        {
                            gpuDemandStatus = $"[bold green]{myGpu.DisplayName}[/]";
                            gpuDemandTier = $"[cyan]{myGpu.DemandTierName}[/] (Recommended Host RAM: {myGpu.RecommendedSpecs?.RamGb}GB)";

                            double realBusyPct = myGpu.UtilizationPct;
                            if (realBusyPct < 0) realBusyPct = 0;
                            if (realBusyPct > 100) realBusyPct = 100;

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
                    }
                    catch
                    {
                        gpuDemandStatus = "[red]Salad API Offline or Error[/]";
                        gpuDemandTier = "[grey]N/A[/]";
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

        static void UpdateWSLData()
        {
            try
            {
                var wslProcs = Process.GetProcesses();
                try
                {
                    long ramTotalBytes = wslProcs
                        .Where(p => p.ProcessName == "vmmemWSL" || p.ProcessName == "vmmem" || p.ProcessName == "wslhost")
                        .Sum(p => p.WorkingSet64);
                    wslRamMB = ramTotalBytes / 1048576.0;
                    ramUsage = wslRamMB > 0 ? $"{wslRamMB:N1} MB" : "Awaiting WSL...";

                    // --- UPTIME WSL ---
                    var oldestWsl = wslProcs.OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).FirstOrDefault();
                    if (oldestWsl != null)
                    {
                        DateTime? safeStartTime = null;
                        try { safeStartTime = oldestWsl.StartTime; }
                        catch
                        {
                            try
                            {
                                using (var searcher = new ManagementObjectSearcher($"SELECT CreationDate FROM Win32_Process WHERE ProcessId = {oldestWsl.Id}"))
                                {
                                    foreach (ManagementObject obj in searcher.Get())
                                    {
                                        string wmiDate = obj["CreationDate"]?.ToString();
                                        if (!string.IsNullOrEmpty(wmiDate)) safeStartTime = ManagementDateTimeConverter.ToDateTime(wmiDate);
                                    }
                                }
                            }
                            catch { }
                        }
                        wslStartTime = safeStartTime ?? DateTime.Now;
                    }
                    else
                    {
                        wslStartTime = DateTime.MinValue;
                    }
                }
                finally
                {
                    foreach (var p in wslProcs) p.Dispose();
                }

                ProcessStartInfo psi = new ProcessStartInfo { FileName = "wsl.exe", Arguments = "-l -v", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.Unicode };
                using (Process proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd().Replace("\0", "");
                    var matchWsl = Regex.Match(output, @"salad-enterprise-linux\s+([A-Za-z]+)");
                    if (matchWsl.Success) { string state = matchWsl.Groups[1].Value; wslStatusStr = state.Contains("Running") ? "Running (Active)" : state.Contains("Stopped") ? "STOPPED (Offline)" : state; }
                }
            }
            catch { wslStatusStr = "Error reading WSL"; wslRamMB = 0; }

            if (wslStatusStr.Contains("STOPPED") || wslStatusStr.Contains("Offline") || wslStatusStr.Contains("Error") || wslStatusStr.Contains("Pending"))
            {
                if (containerStatus.Contains("Running"))
                {
                    containerStatus = "[yellow]Waiting for WSL...[/]";
                }
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
								{
									foreach (ManagementObject obj in searcher.Get())
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
				// Início de um novo lote de workloads reportado pelo matrix.
				// Reseta o estado antes de reprocessar as linhas deste ciclo,
				// assim se o SGS/miner não aparecer mais nesse lote, ele
				// automaticamente deixa de ser considerado ativo, sem depender
				// de uma frase exata de "Stopping workload" que nem sempre é emitida.
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
					// É MINERADOR: o nome real é limpo (TeamRedMiner, T-Rex, XMRig)
					computeWorkloadType = $"miner:{wlName}";
					workloadHardwareType = BuildWorkloadTypeDisplay(computeWorkloadType, isBandwidthActive);
					jobId = wlShortId;
				}
				else // É CONTAINER
				{
					// Se ainda não descobrimos se é CPU ou GPU, coloca "GPU" provisório,
					// a menos que já tenha sido marcado como CPU
					if (computeWorkloadType != "CPU")
					{
						computeWorkloadType = "GPU";
					}

					workloadHardwareType = BuildWorkloadTypeDisplay(computeWorkloadType, isBandwidthActive);

					// Pega apenas os 8 primeiros caracteres como ID limpo
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

			// Se for minerador confirmado, NADA ABAIXO pode sobrescrever!
			if (!isMiningLocked)
			{
				if (line.Contains("CPU HardwareCompatibility", StringComparison.OrdinalIgnoreCase))
				{
					computeWorkloadType = "CPU";
					workloadHardwareType = BuildWorkloadTypeDisplay(computeWorkloadType, isBandwidthActive);
				}
				else if (line.Contains("GPU HardwareCompatibility", StringComparison.OrdinalIgnoreCase))
				{
					// Só crava GPU se não for minerador e não tiver sido confirmado como CPU
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
            // 1. CPU
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT LoadPercentage, Name FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get()) 
                    { 
                        txtCpu = $"{obj["Name"]?.ToString() ?? "CPU"} (Load: {obj["LoadPercentage"]?.ToString() ?? "0"}%)"; 
                        break; 
                    }
                }
            }
            catch { txtCpu = "CPU (Load: N/A)"; }

            // 2. RAM
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        double totalMb = Convert.ToDouble(obj["TotalVisibleMemorySize"]) / 1024; 
                        double freeMb = Convert.ToDouble(obj["FreePhysicalMemory"]) / 1024;
                        txtRam = $"{Math.Round((totalMb - freeMb) / 1024, 1)} GB / {Math.Round(totalMb / 1024, 1)} GB (Load: {Math.Round(((totalMb - freeMb) / totalMb) * 100, 0)}%)"; 
                        break;
                    }
                }
            }
            catch { txtRam = "RAM (Load: N/A)"; }

            // 3. GPU (NVIDIA via nvidia-smi / Fallback AMD)
            bool nvidiaSuccess = false;

            if (!nvidiaSmiFailed)
            {
                try
                {
                    txtGpu = "Searching GPU...";
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
                                    txtGpu = $"{smiName} (Load: {parts[1].Trim()}% | Pwr: {parts[2].Trim()}W | Temp: {parts[3].Trim()}\u00B0C)";

                                    if (double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double gpuLoad))
                                        currentGpuLoadPct = gpuLoad;

                                    nvidiaSuccess = true;
                                }
                            }
                        }
                    }

                    if (!nvidiaSuccess)
                        nvidiaSmiFailed = true;
                }
                catch
                {
                    nvidiaSmiFailed = true;
                }
            }

            // Fallback AMD / Sem NVIDIA
            if (!nvidiaSuccess)
            {
                string fallbackGpu = GetMiningGpuName() ?? "Unknown GPU";
                double amdLoad = GetAmdGpuUtilization();

                txtGpu = amdLoad >= 0
                    ? $"{fallbackGpu} (Load: {amdLoad}%)"
                    : $"{fallbackGpu} (SMI Sensores N/A)";

                currentGpuLoadPct = amdLoad >= 0 ? amdLoad : 0;
            }

            // 4. DISK
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

                        txtDisk = $"{hostDiskName} {GetDiskFreeSpaceStr()} (Load: {util:F0}% | R: {readStr} W: {writeStr})";
                        break;
                    }
                }
            }
            catch
            {
                txtDisk = $"{hostDiskName} | {GetDiskFreeSpaceStr()} (I/O Data N/A)";
            }
        }

		static void AddLogToScreen(string line)
		{
			if (string.IsNullOrWhiteSpace(line)) return;

			recentLogs.RemoveAll(x => x.IsError && (DateTime.Now - x.Timestamp).TotalSeconds > 60);

			// Normaliza tabs -> substitui por espaço único ANTES de qualquer contagem de tamanho,
			// eliminando a diferença entre Length (char count) e largura visual real no terminal.
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

        static IRenderable RenderPanel(string filePath)
        {
            // =========================================================================
            // HELP / ABOUT / TROUBLESHOOTING SCREEN (WITH [S] TOGGLE)
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
                    "[yellow][[CTRL]] [[+ / -]][/] : Zoom in/out on the terminal (Windows default).\n" +
                    "[yellow][[N]][/]            : Toggle Novatech demand overlay / view.\n" +
                    "[yellow][[E]][/]            : Toggle recent events / Error Log panel.\n" +
                    "[yellow][[C]][/]            : Clear error log history.\n" +
                    "[yellow][[S]][/]            : Toggle between [cyan]About[/] and [orange1]Troubleshooting[/] panels.\n" +
                    "[yellow][[H]][/]            : Toggle between Dashboard and this Help screen.\n" +
                    "[yellow][[ESC]][/]          : Safely exit SaladXRay."
                );

                var helpPanel = new Panel(helpText)
                    .Header("[white bold] HELP & CONTROLS [/]", Justify.Left)
                    .BorderColor(Color.Green)
                    .Padding(2, 1, 2, 1);

                helpGrid.AddRow(helpPanel);

                // =========================================================================
                // ABA 1: TROUBLESHOOTING & SUPPORT GUIDELINES (Ativada com [T])
                // =========================================================================
                if (showSupportTab)
                {
                    var supportText = new Markup(
                        "[bold yellow]:warning: Don't Panic Over Isolated Errors & Red Logs![/]\n" +
                        "Containers, distributed networks, and WSL constantly produce transient warnings. " +
                        "Seeing an [red][[ERR]][/] line in X-Ray does [italic]not[/] necessarily mean your node is broken " +
                        "or that you stopped earning.\n\n" +
                        "Before interrupting any process, [bold yellow]be patient[/] and allow workloads to stabilize before making hasty decisions. " +
                        "Always check your settings whenever possible: if a clean reinstall is performed (uninstall, reboot, and reinstall) " +
                        "instead of a direct in-place update, the Salad app resets back to its default settings and everything must be configured again. " +
                        "Keep these details in mind before seeking support.\n\n" +
                        "[bold cyan]1. Explore the Official Troubleshooting Hub First:[/]\n" +
                        "Only if you have already tried everything and still feel something is wrong, head to the official support website and check the [yellow]Troubleshooting[/] section:\n" +
                        "   [link=https://support.salad.com]https://support.salad.com[/]\n" +
                        "It is very likely you will solve the issue entirely on your own thanks to the excellent guides and tutorials available there.\n\n" +
                        "[bold cyan]2. Opening a Support Ticket (Last Resort):[/]\n" +
                        $"Only open a support ticket as a last resort after trying the troubleshooting guides. "
                    );

                    var supportPanel = new Panel(supportText)
                        .Header("[white bold] TROUBLESHOOTING & SUPPORT GUIDELINES [/]", Justify.Left)
                        .BorderColor(Color.DarkOrange)
                        .Padding(2, 1, 2, 1);

                    helpGrid.AddRow(supportPanel);

                    var navInstruction = new Markup("\n[yellow][[S]][/] View Project Philosophy & About  |  [blink red][[H]][/] Back to Dashboard");
                    helpGrid.AddRow(new Align(navInstruction, HorizontalAlignment.Center));
                }
                // =========================================================================
                // ABA 2: ABOUT SALAD XRAY (Padrão)
                // =========================================================================
                else
                {
                    var aboutText = new Markup(
                        "[bold cyan]Born for Peace of Mind[/]\n\n" +
                        "[green]SaladXRay[/] exists to streamline monitoring your Salad Node. " +
                        "Opening Task Manager, fighting with Resource Monitor (resmon), and juggling five " +
                        "windows just to see if WSL is alive or if a miner started is ridiculous. " +
                        "Even worse: refreshing web pages manually to check demand APIs until you catch " +
                        "a red flag on the metrics task.\n\n" +
                        "Triumphantly, the original spark behind this project was fulfilled even before v1.0 " +
                        "was released: the natural evolution of the official app, which finally introduced " +
                        "native container download progress bars with ETA. But SaladXRay didn't stop there " +
                        "— it evolved.\n\n" +
                        "Today, this tool delivers the [yellow]entire ecosystem at a single glance[/]: " +
                        "deep VM/WSL health and real uptime, VM network traffic, workload status, " +
                        "parallel processes (miners and SGS bandwidth nodes), real-time wallet balance, " +
                        "and safe, cached GPU demand telemetry (Novatech & Salad).\n\n" +
                        "All without heavy GUIs, using only [green]~26 MB of RAM[/] (half of Task Manager). " +
                        "It doesn't steal a single cycle from your compute hardware.\n\n" +
                        "[bold cyan][[-h]] Human-Readable Translation:[/] For your absolute peace of mind, SaladXRay is " +
                        "strictly a read-only tool. Zero hidden commands, zero system modifications. " +
                        "It only reads local logs, native Windows APIs, and public endpoints. " +
                        "Just like a real X-Ray: through it, you only observe.\n\n" +
                        "Built for anyone who wants absolute [bold]clarity[/] under the hood instead of " +
                        "guessing through noisy, generic system tools.\n\n" +
                        "My sincere gratitude to everyone in the community who tested, supported, and shared " +
                        "this journey. And of course: [bold]the hardest bug I ever fixed?[/] My wife. Everything " +
						$"[grey]Love you, babe.[/]\n\n" +
						$"[link=https://github.com/joseluisfreire]https://github.com/joseluisfreire[/] [grey]XRay Version:[/] {xrayVersion}\n" +
						"[grey]Built with patience, gratitude, and dedication for the community.[/]"
                    );

                    var aboutPanel = new Panel(aboutText)
                        .Header("[white bold] ABOUT SALAD XRAY [/]", Justify.Left)
                        .BorderColor(Color.Blue)
                        .Padding(2, 1, 2, 1);

                    helpGrid.AddRow(aboutPanel);

                    var navInstruction = new Markup("\n[yellow][[T]][/] View Support & Troubleshooting  |  [blink red][[H]][/] Back to Dashboard");
                    helpGrid.AddRow(new Align(navInstruction, HorizontalAlignment.Center));
                }

                return helpGrid;
            }

            // =========================================================================
            // MAIN SCREEN (DASHBOARD)
            // =========================================================================
            var grid = new Grid().Expand();
            grid.AddColumn(new GridColumn());

	    string fileName = Path.GetFileName(filePath) ?? "No log file found";

            // AQUI FOI ONDE A MÁGICA ACONTECEU COM O SEU HELPER
            TimeSpan appUptime = DateTime.Now - appStartTime;
            string formattedAppUptime = FormatUptime(appUptime);

            TimeSpan osUptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
	    string formattedOsUptime = FormatUptime(osUptime);

            string formattedSaladUptime = saladStartTime != DateTime.MinValue
                ? FormatUptime(DateTime.Now - saladStartTime)
                : "Offline";

            string formattedBowlUptime = saladBowlStartTime != DateTime.MinValue
                ? FormatUptime(DateTime.Now - saladBowlStartTime)
                : "Offline";

            string uiStr = saladVersion == "Unknown" || saladVersion == "Detecting..." ? uiName : $"{uiName} v{saladVersion}";
            string svcStr = saladBowlVersion == "Offline" || saladBowlVersion == "Unknown" || saladBowlVersion == "Detecting..." ? svcName : $"{svcName} v{saladBowlVersion}";

            string titleText = $"[yellow bold]{xrayName} v{xrayVersion} [[ESC]] Exit [[H]] Help[/]";

		var infoPanel = CreateBannerPanel(titleText, new Dictionary<string, string> {
		    { "APP VERSION", $"[green]{Markup.Escape(uiStr)}[/]" },
		    { "SVC VERSION", $"[blue]{Markup.Escape(svcStr)}[/]" },
		    { "OS VERSION", $"[magenta]{Markup.Escape(osDisplayName)}[/]" },
		    { "READING LOG", $"[cyan]{Markup.Escape(fileName)}[/]" }
		});

            // 1. CONTENT (Internal table, super clean)
            var uptimeContent = new Table().HideHeaders().Border(TableBorder.None);
            uptimeContent.AddColumn(new TableColumn("").NoWrap());
            uptimeContent.AddRow(new Markup($"[green]App:[/] {formattedSaladUptime}"));
            uptimeContent.AddRow(new Markup($"[blue]Svc:[/] {formattedBowlUptime}"));
            uptimeContent.AddRow(new Markup($"[magenta]OS :[/] {formattedOsUptime}"));
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
            
            // Se estiver Running, mostra o status + relógio + tempo. Se estiver Off/qualquer outra coisa, mostra apenas o status limpo!
            string wslDisplayStatus = wslStatusStr.Contains("Running")
                ? $"[{wslColor}]{Markup.Escape(wslStatusStr)}[/]  :alarm_clock: {workTime}"
                : $"[{wslColor}]{Markup.Escape(wslStatusStr)}[/]";

            grid.AddRow(CreateSection("LINUX WSL (VIRTUAL MACHINE)", new Dictionary<string, string> { 
                { ":desktop_computer: VM STATUS", wslDisplayStatus }, 
                { ":floppy_disk: VM RAM", ramUsage }, 
                { ":optical_disk: VM DISK", wslDiskSize }, 
                { ":satellite_antenna: VM LAN", vNetStats } 
            }));

            string heart = ":broken_heart:";
            if ((DateTime.Now - lastLogHeartbeat).TotalMinutes < 2)
            {
                heart = (DateTime.Now.Millisecond < 500) ? ":red_heart:" : ":beating_heart:";
            }

            string matrixStatusWithHeart = $"{matrixStatus} {heart}";

            string displayContainerStatus;
            if (containerStatus.Contains("Running"))
            {
                displayContainerStatus = "[bold green]Running (Active)[/]";
            }
            else if (isPullingState && globalProgress >= 98.0 && currentVmDownKbps < 1024 && wslRamMB > 800)
            {
                displayContainerStatus = $"[cyan]Unpacking / Extracting... (WSL RAM Spike: {wslRamMB:N0} MB | Low Net I/O)[/]";
            }
            else
            {
                displayContainerStatus = containerStatus;
            }

			grid.AddRow(CreateSection("SALAD CONTAINER WORKLOAD", new Dictionary<string, string> {
				{ ":satellite: MATRIX STATE", matrixStatusWithHeart },
				{ ":id_button: TYPE / ID", $"{workloadHardwareType} [grey]/[/] {Markup.Escape(jobId)}" },
				{ ":package: CONTAINER", displayContainerStatus }
			}));

            grid.AddRow(CreateSection("GLOBAL HARDWARE (HOST)", new Dictionary<string, string> {
                { ":gear: HOST CPU", txtCpu },
                { ":fire: HOST GPU", txtGpu },
                { ":bar_chart: HOST RAM", txtRam },
                { ":floppy_disk: HOST DISK", txtDisk }
            }));

            Dictionary<string, string> gpuDemandItems;
            string demandSourceLabel;

            if (useNovaDemandView && isNovaOnline)
            {
                demandSourceLabel = "NOVATECH";

                string novaUtilStr = novaUtil.HasValue
                    ? $"[yellow]{Math.Round(novaUtil.Value, 1)}%[/]"
                    : "[grey]N/A[/]";

                string novaEarnStr = (novaMinEarning.HasValue && novaMaxEarning.HasValue)
                    ? $"Min: [bold green]${novaMinEarning.Value:F3}[/] / Max: [bold green]${novaMaxEarning.Value:F3}[/]"
                    : "[grey]N/A[/]";

                gpuDemandItems = new Dictionary<string, string> {
                    { ":fire: GPU MATCH", novaMatchStatus },
                    { ":gem_stone: DEMAND", novaTier != null ? $"[cyan]{Markup.Escape(novaTier)}[/]" : "[grey]N/A[/]" },
                    { ":chart_increasing: NET UTIL", novaUtilStr },
                    { ":money_bag: HOURLY EST", novaEarnStr }
                };
            }
            else
            {
                demandSourceLabel = "SALAD";

                gpuDemandItems = new Dictionary<string, string> {
                    { ":fire: GPU MATCH", gpuDemandStatus },
                    { ":gem_stone: DEMAND", gpuDemandTier },
                    { ":chart_increasing: NET UTIL", gpuNetworkUtil },
                    { ":money_bag: 24H EST", gpuEarning24h }
                };
            }

            grid.AddRow(CreateSection($"GPU DEMAND [{demandSourceLabel}] (Press [N] to switch)", gpuDemandItems));

			string sgsUptimeStr = sgsStartTime != DateTime.MinValue ? FormatUptime(DateTime.Now - sgsStartTime) : "Offline";
			string sgsColor = sgsStatus == "Online" ? "green" : "grey";

			string sgsOneLine = sgsStatus == "Online"
				? $"[{sgsColor}]{sgsStatus}[/] | Up: {sgsUptimeStr} | CPU: {sgsCpuUsagePct:F1}% | RAM: {sgsRamMB2:F1} MB | Net: {sgsNetworkSpeed}"
				: "[grey]Offline[/]";

			var hostWorkloads = new Dictionary<string, string>
			{
				{ ":globe_with_meridians: SGS CLIENT", sgsOneLine }
			};

			grid.AddRow(CreateSection("WINDOWS HOST WORKLOADS", hostWorkloads));

            // Se houver algum erro velho na lista no momento do render, descarta também
            recentLogs.RemoveAll(x => x.IsError && (DateTime.Now - x.Timestamp).TotalSeconds > 60);

			var logsArray = recentLogs.Select(x => x.FormattedText).ToArray();
			int logMaxWidth = Math.Max(20, AnsiConsole.Profile.Width - 8);

			var logsBlock = new Markup(
				$"  {TruncateWithColors(logsArray.Length > 0 ? logsArray[0] : "", logMaxWidth)}\n" +
				$"  {TruncateWithColors(logsArray.Length > 1 ? logsArray[1] : "", logMaxWidth)}\n" +
				$"  {TruncateWithColors(logsArray.Length > 2 ? logsArray[2] : "", logMaxWidth)}\n" +
				$"  {TruncateWithColors(logsArray.Length > 3 ? logsArray[3] : "", logMaxWidth)}\n" +
				$"  {TruncateWithColors(logsArray.Length > 4 ? logsArray[4] : "", logMaxWidth)}"
			);

			int errValWidth = Math.Max(10, AnsiConsole.Profile.Width - 25);
			var errorLines = new List<IRenderable>();

			if (showErrorHistory)
			{
				var errors = errorHistory.Reverse().ToList();

				for (int i = 0; i < MaxErrorHistory; i++)
				{
					if (i < errors.Count)
					{
						var e = errors[i];
						string line = $"[grey]{e.Timestamp:HH:mm:ss}[/] [white]:[/] {TruncateWithColors($"[red]{Markup.Escape(e.Message)}[/]", errValWidth)}";
						errorLines.Add(new Markup(line));
					}
					else
					{
						errorLines.Add(new Markup(" "));
					}
				}
			}

			string eColor = errorHistory.Count > 0 ? "indianred1" : "cyan";

			string errorHeaderTitle = showErrorHistory
				? $"[cyan][[ ERROR HISTORY ([{eColor}][[E]][/] back | [[C]] clear) ]][/]"
				: $"[cyan][[ RECENT EVENTS ([{eColor}][[E]][/] history) ]][/]";

			grid.AddRow(
				new Panel(showErrorHistory ? (IRenderable)new Rows(errorLines) : logsBlock)
					.Header(errorHeaderTitle)
					.BorderColor(Color.Cyan)
					.Expand()
			);

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

	// adapt icons w10
	static string AdaptIcon(string label)
	{
		if (string.IsNullOrEmpty(label)) return label;

		if (_adaptIconCache.TryGetValue(label, out var cached))
			return cached;

		string result;

		if (isLegacyEmojiMode)
		{
			// Substitui cada :shortcode: pelo Unicode correspondente (fallback real)
			result = EmojiShortcodeRegex.Replace(label, match =>
			{
				return EmojiFallbackMap.TryGetValue(match.Value, out var unicodeChar)
					? unicodeChar + " "
					: ""; // shortcode desconhecido: remove silenciosamente
			}).TrimStart();
		}
		else
		{
			result = label;
		}

		_adaptIconCache[label] = result;
		return result;
	}
	static Panel CreateSection(string title, Dictionary<string, string> items)
	{
		var table = new Table().HideHeaders().Border(TableBorder.None).Expand();
		table.AddColumn(new TableColumn("Label").Width(15).NoWrap()); 
		table.AddColumn(new TableColumn("Value").NoWrap());

		foreach (var item in items)
		{
			string rawVal = item.Value ?? "";
			string truncatedVal = TruncateWithColors(rawVal, Math.Max(10, AnsiConsole.Profile.Width - 25));

			Markup valueMarkup;
			try
			{
				// Tenta renderizar normalmente (com cores legítimas)
				valueMarkup = new Markup($"[white]:[/] {truncatedVal}");
			}
			catch
			{
				// Se vier [N/A], [N/B], [Not Supported] ou qualquer colchete desconhecido,
				// escapa o valor e nunca mais quebra o dashboard!
				string safeVal = TruncateWithColors(Markup.Escape(rawVal), Math.Max(10, AnsiConsole.Profile.Width - 25));
				valueMarkup = new Markup($"[white]:[/] {safeVal}");
			}

			table.AddRow(
				new Markup($"[white]{Markup.Escape(AdaptIcon(item.Key ?? ""))}[/]"), 
				valueMarkup
			);
		}

		return new Panel(table)
			.Header($"[cyan][[ {Markup.Escape(title)} ]][/]")
			.BorderColor(Color.White)
			.Expand();
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

        // Padrões de iGPU AMD integrada (APU) que devem perder pra qualquer GPU discreta
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
	        {
	            foreach (ManagementObject obj in searcher.Get())
	            {
	                string gpuName = obj["Name"]?.ToString();
	                if (string.IsNullOrEmpty(gpuName)) continue;

	                bool isAmd = gpuName.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0
	                          || gpuName.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0;
	                bool isNvidia = gpuName.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0
	                             || gpuName.IndexOf("GeForce", StringComparison.OrdinalIgnoreCase) >= 0;

	                // Whitelist:eonly AMD and NVIDIA 
	                if (!isAmd && !isNvidia) continue;

	                candidates.Add(gpuName);
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

        // ==========================================
        // O SEU HELPER ADICIONADO AQUI
        // ==========================================
        static string FormatUptime(TimeSpan ts)
        {
            if (ts.TotalDays >= 1)
                return $"{(int)ts.TotalDays}d {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
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

        [JsonPropertyName("variantNames")]
        public List<string> VariantNames { get; set; }
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

    public class NovatechResponse
    {
        [JsonPropertyName("gpus")]
        public List<NovatechGpuData> Gpus { get; set; }
    }

    public class NovatechGpuData
    {
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("demandTier")]
        public string DemandTier { get; set; }

        [JsonPropertyName("utilization")]
        public double Utilization { get; set; }

        [JsonPropertyName("minEarningRate")]
        public double? MinEarningRate { get; set; }

        [JsonPropertyName("maxEarningRate")]
        public double? MaxEarningRate { get; set; }
    }
}
