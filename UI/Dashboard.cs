#nullable disable
#pragma warning disable CA1416

using System;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace SaladXRayPanel
{
    partial class Program
    {
        // ==========================================
        // DASHBOARD / UI STATE
        // ==========================================
        static bool showSupportTab = false; // toggle [S] entre About e Suporte
        static bool showHelpScreen = false; // Help/About screen control
		internal static readonly Dictionary<string, FigletFont> Fonts = new(StringComparer.OrdinalIgnoreCase);
		internal static FigletFont GetFont(string name) => Fonts.TryGetValue(name, out var f) ? f : null;

        // Fixed width for the Uptime panel (synchronized with the banner calculation)
		const int UPTIME_PANEL_WIDTH = 23;
		const int PULSE_SIDE_TOTAL_OFFSET = 28; // 30 (painel PULSES) + 3 (gutter do Grid externo)

        static bool isLegacyEmojiMode = false;

        // Regex que captura emojis Unicode e símbolos gráficos (com suporte a variation selectors como \uFE0F)
		private static readonly Regex EmojiRegex = new(@"([\uD83C-\uD83F][\uD800-\uDFFF]|[\u2300-\u27BF])\uFE0F?", RegexOptions.Compiled);
        private static readonly Dictionary<string, string> _adaptIconCache = new(StringComparer.OrdinalIgnoreCase);
		
		//last payout
		private static DateTime? _lastPulseTs = null;

		private static string FormatSince(TimeSpan t) =>
			t.TotalSeconds < 60 ? $"{t.Seconds}s" :
			t.TotalHours   < 1  ? $"{t.Minutes}m {t.Seconds:00}s" :
			t.TotalDays    < 1  ? $"{(int)t.TotalHours}h {t.Minutes:00}m" :
								  $"{(int)t.TotalDays}d {t.Hours}h";

        /*static string GenerateProgressBar(double percent, int barLength = 20)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            int filledBlocks = (int)Math.Round((percent / 100.0) * barLength);
            int emptyBlocks = barLength - filledBlocks;

            string filled = new string('█', filledBlocks);
            string empty = new string('░', emptyBlocks);

            return $"[green]{filled}[/][grey]{empty}[/]";
        }*/
static string GenerateProgressBar(double percent, int barLength = 11)
{
    percent = Math.Clamp(percent, 0, 100);

    int filled = (int)Math.Round((percent / 100.0) * barLength);
    int empty = barLength - filled;

    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < filled; i++)
    {
        if (i % 2 == 0) sb.Append("[#39ff14]▰[/]");
        else            sb.Append("[grey]▰[/]");
    }

    return $"{sb}[grey27]{new string('▱', empty)}[/]";
}

        static string FormatPercentText(double percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            string percentColor = percent >= 88 ? "#ff3333" : (percent >= 60 ? "yellow1" : "white");
            return $"[{percentColor}]{percent,4:0}%[/]";
        }

        static Markup FormatOnlyProgressBar(double percent)
        {
            percent = Math.Clamp(percent, 0, 100);

            const int totalBars = 11;
            int filled = (int)Math.Round((percent / 100.0) * totalBars);
            int empty = totalBars - filled;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < filled; i++)
            {
                double ratio = totalBars > 1 ? (double)i / (totalBars - 1) : 1.0;

                if (ratio < 0.40)      sb.Append("[cyan1]▰[/]");
                else if (ratio < 0.70) sb.Append("[yellow1]▰[/]");
                else if (ratio < 0.88) sb.Append("[orange1]▰[/]");
                else                   sb.Append("[#ff3333]▰[/]");
            }

            string emptyBar = new string('▱', empty);
            return new Markup($"{sb}[grey27]{emptyBar}[/]");
        }

        static IRenderable RenderPanel(string filePath)
        {
			if (showHelpScreen)
			{
				return RenderHelpScreen();
			}

            // =========================================================================
            // MAIN SCREEN (DASHBOARD)
            // =========================================================================
			if (WalletScreenIndex == 4)
			{
				SyncXrayGraphData();
				var st = saladApiService?.State;
				return SaladXRay.Rendering.GraphBuilder.BuildW4Screen(
					xrayGraphData, BuildPulseSidePanel(),
					st?.Last24HourEarning ?? 0.0,
					st?.Last7DayEarning ?? 0.0,
					st?.Last30DayEarning ?? 0.0);
			}
			
			var grid = new Grid().Expand();
            grid.AddColumn(new GridColumn());

            string fileName = Path.GetFileName(filePath) ?? "No log file found";

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

            string titleText = $"[yellow bold]{xrayName} v{xrayVersion}[/]  " +
                $"[yellow bold][[ESC]] Exit [[H]] Help[/]";

            var infoPanel = CreateBannerPanel(titleText, new Dictionary<string, string> {
                { $"{AdaptIcon("🥗")} APP VER.", $"[green]{Markup.Escape(uiStr)}[/]" },
                { $"{AdaptIcon("⚙️")} SERVICE",  $"[blue]{Markup.Escape(svcStr)}[/]" },
                { $"{AdaptIcon("🪟")} WINVER",   $"[magenta]{Markup.Escape(osDisplayName)}[/]" },
                { $"{AdaptIcon("📋")} LOG FILE", $"[cyan]{Markup.Escape(fileName)}[/]" }
            });

            var uptimeContent = new Table().HideHeaders().Border(TableBorder.None);
            uptimeContent.AddColumn(new TableColumn("").NoWrap());
            uptimeContent.AddRow(new Markup($"[green]App:[/] {formattedSaladUptime}"));
            uptimeContent.AddRow(new Markup($"[blue]Svc:[/] {formattedBowlUptime}"));
            uptimeContent.AddRow(new Markup($"[magenta]OS :[/] {formattedOsUptime}"));
            uptimeContent.AddRow(new Markup($"[yellow bold]Xry:[/] {formattedAppUptime}"));

            string uptimeTitle = "[yellow bold]Uptime[/]";

            var uptimePanel = new Panel(uptimeContent)
                .Header(uptimeTitle)
                .BorderColor(Color.White)
                .Padding(1, 0, 0, 0)
                .SquareBorder();

            var topHeaderGrid = new Grid();
            topHeaderGrid.AddColumn(new GridColumn());
            topHeaderGrid.AddColumn(new GridColumn().Width(UPTIME_PANEL_WIDTH));
            topHeaderGrid.AddRow(infoPanel, new Align(uptimePanel, HorizontalAlignment.Right));
            topHeaderGrid.Expand();

            if (WalletScreenIndex == 3)
            {
                SyncXrayGraphData();
				grid.AddRow(SaladXRay.Rendering.GraphBuilder.BuildCompactGraph1hRow(xrayGraphData));
            }
            else
            {
                grid.AddRow(topHeaderGrid);

                string screenTag = WalletScreenIndex switch { 1 => "2/5", 2 => "3/5", _ => "1/5" };

                grid.AddRow(CreateSection($"EARNINGS ({screenTag})", new Dictionary<string, string> {
                    { "💰 WALLET", BuildSingleLineWallet() }
                }));
            }

			int vmAvailableWidth = AnsiConsole.Profile.Width - (WalletScreenIndex == 3 ? PULSE_SIDE_TOTAL_OFFSET : 0);
			grid.AddRow(CreateVmHardwarePanel(vmAvailableWidth));

            string heart = AdaptIcon("💔");
            if ((DateTime.Now - lastLogHeartbeat).TotalMinutes < 2)
            {
                heart = AdaptIcon((DateTime.Now.Millisecond < 500) ? "❤️" : "💓");
            }

            string matrixStatusWithHeart = $"{matrixStatus} {heart}";

            string displayContainerStatus;
            if (containerStatus.Contains("Running"))
            {
                displayContainerStatus = "[bold green]Running (Active)[/]";
            }
			else if (isPullingState && globalProgress >= 98.0 && VmTelemetryService.CurrentVmDownKbps < 1024 && VmTelemetryService.WslRamMB > 800)
			{
				displayContainerStatus = $"[cyan]Unpacking / Extracting... (WSL RAM Spike: {VmTelemetryService.WslRamMB:N0} MB | Low Net I/O)[/]";
			}
            else
            {
                displayContainerStatus = containerStatus;
            }
			//sgs
			string sgsUptimeStr = sgsStartTime != DateTime.MinValue ? FormatUptime(DateTime.Now - sgsStartTime) : "Offline";
            string sgsColor = sgsStatus == "Online" ? "green" : "grey";

            string sgsOneLine = sgsStatus == "Online"
                ? $"Up: {sgsUptimeStr} | Net: {sgsNetworkSpeed}"
                : "[grey]Offline[/]";
			//sgs
            grid.AddRow(CreateSection("SALAD WORKLOADS", new Dictionary<string, string> {
                { "🛰️ MATRIX",    matrixStatusWithHeart },
                { "🪪 TYPE",      $"{workloadHardwareType}" },
                { "📦 CONTAINER", displayContainerStatus },
				{ "🌐 SGS NODE", sgsOneLine }
            }));

            // PAINEL HARDWARE (HOST)
			int hwAvailableWidth = AnsiConsole.Profile.Width - (WalletScreenIndex == 3 ? PULSE_SIDE_TOTAL_OFFSET : 0);

            grid.AddRow(CreateHardwarePanel(hwAvailableWidth));

            Dictionary<string, string> gpuDemandItems;
            string demandSourceLabel;

            if (useNovaDemandView && isNovaOnline)
            {
                demandSourceLabel = "NOVATECH";

                string novaUtilStr = novaUtil.HasValue
                    ? $"[yellow]{Math.Round(novaUtil.Value, 1)}%[/]"
                    : "[grey]N/A[/]";

                string novaEarnStr = (novaMinEarning.HasValue && novaMaxEarning.HasValue)
                    ? $"Min: [bold green]${novaMinEarning.Value:F2}[/] / Max: [bold green]${novaMaxEarning.Value:F2}[/]"
                    : "[grey]N/A[/]";

                gpuDemandItems = new Dictionary<string, string> {
                    { "🔥 GPU MATCH", novaMatchStatus },
                    { "💎 DEMAND",    novaTier != null ? $"[cyan]{Markup.Escape(novaTier)}[/]" : "[grey]N/A[/]" },
                    { "📈 NET UTIL",  novaUtilStr },
                    { "💰 $/H EST",   novaEarnStr }
                };
            }
            else
            {
                demandSourceLabel = "SALAD";

                gpuDemandItems = new Dictionary<string, string> {
                    { "🔥 GPU MATCH", gpuDemandStatus },
                    { "💎 DEMAND",    gpuDemandTier },
                    { "📈 NET UTIL",  gpuNetworkUtil },
                    { "💰 24H EST",   gpuEarning24h }
                };
            }

            grid.AddRow(CreateSection($"GPU DEMAND [{demandSourceLabel}] (Press [N] to switch)", gpuDemandItems));

            /*string sgsUptimeStr = sgsStartTime != DateTime.MinValue ? FormatUptime(DateTime.Now - sgsStartTime) : "Offline";
            string sgsColor = sgsStatus == "Online" ? "green" : "grey";

            string sgsOneLine = sgsStatus == "Online"
                ? $"[{sgsColor}]{sgsStatus}[/] | Up: {sgsUptimeStr} | CPU: {sgsCpuUsagePct:F1}% | RAM: {sgsRamMB2:F1} MB | Net: {sgsNetworkSpeed}"
                : "[grey]Offline[/]";

            var hostWorkloads = new Dictionary<string, string>
            {
                { "🌐 SGS NODE", sgsOneLine }
            };

            grid.AddRow(CreateSection("WINDOWS HOST WORKLOADS", hostWorkloads));*/

            recentLogs.RemoveAll(x => x.IsError && (DateTime.Now - x.Timestamp).TotalSeconds > 60);

            var logsArray = recentLogs.Select(x => x.FormattedText).ToArray();
            int sideOffset = (WalletScreenIndex == 3 ? 25 : 0);
            int logMaxWidth = Math.Max(20, AnsiConsole.Profile.Width - sideOffset - 8);

            var logsBlock = new Markup(
                $"  {TruncateWithColors(logsArray.Length > 0 ? logsArray[0] : "", logMaxWidth)}\n" +
                $"  {TruncateWithColors(logsArray.Length > 1 ? logsArray[1] : "", logMaxWidth)}\n" +
                $"  {TruncateWithColors(logsArray.Length > 2 ? logsArray[2] : "", logMaxWidth)}\n" +
                $"  {TruncateWithColors(logsArray.Length > 3 ? logsArray[3] : "", logMaxWidth)}\n" +
                $"  {TruncateWithColors(logsArray.Length > 4 ? logsArray[4] : "", logMaxWidth)}"
            );

            int errValWidth = Math.Max(10, AnsiConsole.Profile.Width - sideOffset - 25);
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

            if (WalletScreenIndex == 3)
            {
                const int PULSE_SIDE_PANEL_WIDTH = 25;

                var sideColumn = new Rows(BuildPulseSidePanel());

                var outerGrid = new Grid();
                outerGrid.AddColumn(new GridColumn().Padding(0, 0, 0, 0));
                outerGrid.AddColumn(new GridColumn().Width(PULSE_SIDE_PANEL_WIDTH).Padding(0, 0, 0, 0));
                outerGrid.AddRow(grid, sideColumn);
                outerGrid.Expand();

                return outerGrid;
            }
            return grid;
        }

		internal static IRenderable BuildPulseSidePanel()
		{
			var pulseBuffer = xrayGraphData?.Last5MinPulseBuffer ?? new List<(DateTime Timestamp, double Value)>();
			List<(DateTime Timestamp, double Value)> pulseSnapshot;
			lock (pulseBuffer)
			{
				pulseSnapshot = pulseBuffer.OrderByDescending(p => p.Timestamp).ToList();
			}

			var pulseWindowEnd = DateTime.Now;
			var pulseWindowStart = pulseWindowEnd.AddHours(-1);
			if (pulseWindowStart < appStartTime) pulseWindowStart = appStartTime;

			var pulseWindowed = pulseSnapshot.Where(p => p.Timestamp >= pulseWindowStart).ToList();
			DateTime? oldestPulseTs = pulseWindowed.Count > 0 ? pulseWindowed.Min(p => p.Timestamp) : (DateTime?)null;

			var pulseLines = pulseWindowed.Select((p, idx) =>
			{
				bool isOldest = oldestPulseTs.HasValue && p.Timestamp == oldestPulseTs.Value;
				double secsLeft = isOldest ? 3600 - (DateTime.Now - p.Timestamp).TotalSeconds : double.MaxValue;
				string tsColor = isOldest && secsLeft <= 5 ? "orange1 blink" : (isOldest ? "orange1" : "cyan");

				return $"[silver]│[/][grey]{(idx + 1),2}[/][silver]│[/][{tsColor}]{p.Timestamp:HH:mm:ss}[/][silver]│[/][bold #00ff88]${p.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}[/][silver]│[/]";
			});
			IRenderable pulseContent = Align.Center(new Markup(string.Join("\n", pulseLines)));

			// Rolling window
			bool clockShouldSpin = oldestPulseTs.HasValue && (3600 - (DateTime.Now - oldestPulseTs.Value).TotalSeconds) is > 0 and <= 30;
			string[] spinFrames = { "◴", "◷", "◶", "◵" };
			string spin = clockShouldSpin
				? $"[#e2e8f0]{spinFrames[(int)((Environment.TickCount64 / 120) % spinFrames.Length)]}[/]"
				: "[grey42]◷[/]";
			string pulseClockLineStr = $"[grey58]{pulseWindowStart:HH:mm:ss}[/] {spin} [springgreen2]{pulseWindowEnd:HH:mm:ss}[/]";

			var clockInnerPanel = new Panel(new Markup(pulseClockLineStr).Centered())
				.Header(new PanelHeader("[bold paleTurquoise1] Rolling Window [/]", Justify.Center))
				.Border(BoxBorder.Rounded)
				.BorderStyle(new Style(clockShouldSpin ? Color.DeepPink1 : Color.Silver))
				.Padding(0, 0, 0, 0)
				.Expand();

			// Latest payout
			DateTime? newestPulseTs = pulseWindowed.Count > 0 ? pulseWindowed.Max(p => p.Timestamp) : (DateTime?)null;
			if (newestPulseTs.HasValue && (!_lastPulseTs.HasValue || newestPulseTs.Value > _lastPulseTs.Value))
				_lastPulseTs = newestPulseTs;

			string payoutClockStr = _lastPulseTs.HasValue
				? $"[grey42]⏱ [/] [springgreen2]{FormatSince(DateTime.Now - _lastPulseTs.Value)}[/] [paleturquoise1]ago[/]"
				: "[grey42]⏱  00:00[/]";

			var latestPayoutPanel = new Panel(new Markup(payoutClockStr).Centered())
				.Header(new PanelHeader("[bold paleTurquoise1] Latest Payout [/]", Justify.Center))
				.Border(BoxBorder.Rounded)
				.BorderStyle(new Style(Color.Silver))
				.Padding(0, 0, 0, 0)
				.Expand();

			var fixedWidthPanel = new Table()
				.Border(TableBorder.None)
				.HideHeaders()
				.AddColumn(new TableColumn("").Width(19))
				.AddRow(latestPayoutPanel.Expand());

			return new Panel(new Rows(
					Align.Center(fixedWidthPanel),
					new Markup("[silver]╭──┬────────┬─────────╮[/]"),
					new Markup("[paleTurquoise1]│id│🕗 Time │💲Amount │[/]"),
					new Markup("[silver]├──┼────────┼─────────┤[/]"),
					pulseContent,
					new Markup("[silver]╰──┴────────┴─────────╯[/]"),
					new Markup(" "),
					clockInnerPanel))
				.Header($"[bold paleTurquoise1] MINING REWARDS ({pulseWindowed.Count})[/]", Justify.Center)
				.BorderColor(Color.Gold1)
				.SquareBorder()
				.Padding(0, 1, 0, 0)
				.Expand();
		}

        // =========================================================================
        // MOTOR DE TRUNCAMENTO SEGURO
        // =========================================================================
        public static string TruncateWithColors(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (maxLength <= 0) return string.Empty;

            string plainText = Regex.Replace(text, @"\[\[|\]\]|\[/?[a-zA-Z0-9_# ]+\]", m => m.Value switch {
                "[[" => "[",
                "]]" => "]",
                _ => ""
            });

            int visibleLength = plainText.EnumerateRunes().Count();
            if (visibleLength <= maxLength)
                return text;

            if (maxLength <= 3)
                return new string('.', maxLength);

            int targetVisible = maxLength - 3;
            var sb = new StringBuilder();
            int currentVisible = 0;
            var openTags = new Stack<string>();

            for (int i = 0; i < text.Length; i++)
            {
                if (i + 1 < text.Length && ((text[i] == '[' && text[i + 1] == '[') || (text[i] == ']' && text[i + 1] == ']')))
                {
                    if (currentVisible < targetVisible)
                    {
                        sb.Append(text[i]).Append(text[i + 1]);
                        currentVisible++;
                    }
                    i++;
                    continue;
                }

                if (text[i] == '[')
                {
                    int closeIdx = text.IndexOf(']', i);
                    if (closeIdx != -1)
                    {
                        string tag = text.Substring(i, closeIdx - i + 1);
                        sb.Append(tag);

                        if (tag.StartsWith("[/"))
                        {
                            if (openTags.Count > 0) openTags.Pop();
                        }
                        else
                        {
                            openTags.Push(tag);
                        }

                        i = closeIdx;
                        continue;
                    }
                }

                if (currentVisible < targetVisible)
                {
                    var rune = Rune.GetRuneAt(text, i);
                    sb.Append(rune.ToString());
                    currentVisible++;
                    i += rune.Utf16SequenceLength - 1;
                }
                else
                {
                    break;
                }
            }

            sb.Append("…");

            while (openTags.Count > 0)
            {
                openTags.Pop();
                sb.Append("[/]");
            }

            return sb.ToString();
        }

        static string AdaptIcon(string label)
        {
            if (string.IsNullOrEmpty(label)) return label;

            if (_adaptIconCache.TryGetValue(label, out var cached))
                return cached;

            string result = isLegacyEmojiMode
                ? EmojiRegex.Replace(label, "-")
                : label;

            _adaptIconCache[label] = result;
            return result;
        }

        static Panel CreateSection(string title, Dictionary<string, string> items)
        {
            var table = new Table().HideHeaders().Border(TableBorder.None).Expand();
            table.AddColumn(new TableColumn("Label").Width(12).NoWrap());
            table.AddColumn(new TableColumn("Value").NoWrap());

            int availWidth = AnsiConsole.Profile.Width - (WalletScreenIndex == 3 ? 22 : 0);
            int maxValWidth = Math.Max(10, availWidth - 26);

            foreach (var item in items)
            {
                string rawVal = item.Value ?? "";
                string truncatedVal = TruncateWithColors(rawVal, maxValWidth);

                Markup valueMarkup;
                try
                {
                    valueMarkup = new Markup($"[white]│[/] {truncatedVal}");
                }
                catch
                {
                    string safeVal = TruncateWithColors(Markup.Escape(rawVal), maxValWidth);
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
                .SquareBorder()
                .Expand();
        }
		// ====================================================================
		// LIMITES DO .WSLCONFIG (lido uma única vez por sessão do app)
		// ====================================================================
		private static double _vmMemCeilingMB = 0;
		private static int _vmProcessors = 0;
		private static bool _vmLimitsLoaded = false;

		private static (double MemoryMB, int Processors) GetWslConfigLimits()
		{
			if (_vmLimitsLoaded)
				return (_vmMemCeilingMB, _vmProcessors);

			_vmLimitsLoaded = true;

			double memoryMB = 0;
			int processors = 0;

			try
			{
				string path = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
					".wslconfig");

				if (File.Exists(path))
				{
					foreach (var line in File.ReadAllLines(path))
					{
						string trimmed = line.Trim();
						if (trimmed.StartsWith("#") || !trimmed.Contains('=')) continue;

						var parts = trimmed.Split('=', 2);
						string key = parts[0].Trim().ToLowerInvariant();
						string val = parts[1].Trim();

						if (key == "memory")
						{
							var m = Regex.Match(val, @"(\d+(\.\d+)?)\s*(MB|GB|KB)?", RegexOptions.IgnoreCase);
							if (m.Success)
							{
								double num = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
								string unit = m.Groups[3].Value.ToUpperInvariant();
								memoryMB = unit switch
								{
									"GB" => num * 1024,
									"KB" => num / 1024,
									_ => num
								};
							}
						}
						else if (key == "processors")
						{
							if (int.TryParse(val, out int p) && p > 0)
								processors = p;
						}
					}
				}
			}
			catch { /* silencioso, cai no default */ }

			if (memoryMB <= 0)
			{
				try
				{
					using var searcher = new System.Management.ManagementObjectSearcher(
						"SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
					foreach (System.Management.ManagementObject obj in searcher.Get())
					{
						ulong bytes = (ulong)obj["TotalPhysicalMemory"];
						memoryMB = (bytes / 1024.0 / 1024.0) / 2.0; // default = 50% da RAM física
						break;
					}
				}
				catch { memoryMB = 16384; } // fallback de segurança
			}

			if (processors <= 0)
				processors = Environment.ProcessorCount;

			_vmMemCeilingMB = memoryMB;
			_vmProcessors = processors;

			return (_vmMemCeilingMB, _vmProcessors);
		}


		static Panel CreateVmHardwarePanel(int availableWidth)
		{
			int innerWidth = availableWidth - 3;

			const int col1Width = 14;
			const int colSepWidth = 2;
			const int col2Width = 12;
			int col3Width = Math.Max(0, innerWidth - col1Width - colSepWidth - col2Width);

			var vmTable = new Table()
				.Expand()
				.Border(TableBorder.None)
				.HideHeaders();

			vmTable.AddColumn(new TableColumn("").Width(col1Width).NoWrap().Padding(0, 0, 0, 0));
			vmTable.AddColumn(new TableColumn("").Width(colSepWidth).NoWrap().Padding(0, 0, 0, 0));
			vmTable.AddColumn(new TableColumn("").Width(col2Width).NoWrap().Padding(0, 0, 0, 0));
			vmTable.AddColumn(new TableColumn("").Width(col3Width).NoWrap().Padding(0, 0, 0, 0));

			var sep = new Markup("[white]│ [/]");

			// CPU da VM (já é % direto)
			double vmCpuPct = Math.Clamp(VmTelemetryService.WslCpuUsage, 0, 100);

			// RAM da VM: % relativa a um teto de referência (ajustável se você tiver .wslconfig)
			var (vmMemCeilingMB, vmProcessors) = GetWslConfigLimits();
			double vmRamPct = Math.Clamp((VmTelemetryService.WslRamMB / vmMemCeilingMB) * 100.0, 0, 100);

			string diskLed = VmTelemetryService.IsDiskLedActive ? "[bold green]●[/]" : "[grey]○[/]";
			string diskGrowth = VmTelemetryService.VhdxGrowthRateMBs > 0.05
				? $" [yellow]↑ {VmTelemetryService.VhdxGrowthRateMBs:F1} MB/s[/]"
				: "";
			string vhdxFileName = Path.GetFileName(VmTelemetryService.VhdxPath);
			string vhdxSizeDisplay = $"{VmTelemetryService.WslDiskSize}{diskGrowth}";

			bool wslIsRunning = VmTelemetryService.WslStatusStr.Contains("Running");
			string wslArrow = wslIsRunning ? "[green1]▲[/]" : "[grey70]▼[/]";
			
			string vCoresDisplay = $"[white]{vmProcessors}[/] vCores";

			double vmRamUsedGB = VmTelemetryService.WslRamMB / 1024.0;
			double vmRamTotalGB = vmMemCeilingMB / 1024.0;
			string vmRamDisplay = $"[white]{vmRamUsedGB:N1} GB[/] / [grey]{vmRamTotalGB:N1} GB[/]";

			vmTable.AddRow(
				new Markup($"{AdaptIcon(" ⚙️")} [white]vCPU[/]{FormatPercentText(vmCpuPct)}"),
				sep,
				FormatOnlyProgressBar(vmCpuPct),
				new Markup(TruncateWithColors(vCoresDisplay, col3Width))
			);

			vmTable.AddRow(
				new Markup($"{AdaptIcon(" 💾")} [white]vRAM[/]{FormatPercentText(vmRamPct)}"),
				sep,
				FormatOnlyProgressBar(vmRamPct),
				new Markup(TruncateWithColors(vmRamDisplay, col3Width))
			);

			vmTable.AddRow(
				new Markup($"{AdaptIcon(" 💿")} [white]vDSK[/]    {diskLed}"),
				sep,
				Align.Center(new Markup(TruncateWithColors($"[grey]{vhdxFileName}[/]", col2Width))),
				new Markup(TruncateWithColors(vhdxSizeDisplay, col3Width))
			);

			string rxArrow = VmTelemetryService.IsRxLedActive ? "[bold green]▼[/]" : "[grey]▼[/]";
			string txArrow = VmTelemetryService.IsTxLedActive ? "[bold fuchsia]▲[/]" : "[grey]▲[/]";

			vmTable.AddRow(
				new Markup($"{AdaptIcon(" 📡")} [white]vETH[/]   {txArrow}{rxArrow}"),
				sep,
				new Markup(TruncateWithColors(VmTelemetryService.VNetTxStr ?? "", col2Width)),
				new Markup(TruncateWithColors(VmTelemetryService.VNetRxStr ?? "", col3Width))
			);
			
			string headerTitle = $"[green1 bold][[ [bold green1]{Markup.Escape(VmTelemetryService.DistroName.ToUpperInvariant())}[/] {AdaptIcon("🐧")} {wslArrow} [grey70]{workTime}[/] ]][/]";
			return new Panel(vmTable)
				.Header(headerTitle)
				.BorderColor(Color.Green1)
				.SquareBorder()
				.Padding(0, 0, 0, 0)
				.Expand();
		}

        static Panel CreateHardwarePanel(int availableWidth)
        {
            int innerWidth = availableWidth - 3;

            const int col1Width = 14;
            const int colSepWidth = 2;
            const int col2Width = 12;
            // livre: encolhe/cresce exatamente conforme sobra, sem piso artificial
            int col3Width = Math.Max(0, innerWidth - col1Width - colSepWidth - col2Width);

            var hwTable = new Table()
                .Expand()
                .Border(TableBorder.None)
                .HideHeaders();

            hwTable.AddColumn(new TableColumn("").Width(col1Width).NoWrap().Padding(0, 0, 0, 0));
            hwTable.AddColumn(new TableColumn("").Width(colSepWidth).NoWrap().Padding(0, 0, 0, 0));
            hwTable.AddColumn(new TableColumn("").Width(col2Width).NoWrap().Padding(0, 0, 0, 0));
			hwTable.AddColumn(new TableColumn("").Width(col3Width).NoWrap().Padding(0, 0, 0, 0));

            var sep = new Markup("[white]│ [/]");

            hwTable.AddRow(
                new Markup($"{AdaptIcon(" ⚙️")} [white]CPU [/]{FormatPercentText(currentCpuLoadPct)}"),
                sep,
                FormatOnlyProgressBar(currentCpuLoadPct),
                new Markup(TruncateWithColors(txtCpu ?? "", col3Width))
            );

            hwTable.AddRow(
                new Markup($"{AdaptIcon(" 🔥")} [white]GPU [/]{FormatPercentText(currentGpuLoadPct)}"),
                sep,
                FormatOnlyProgressBar(currentGpuLoadPct),
                new Markup(TruncateWithColors(txtGpu ?? "", col3Width))
            );

            hwTable.AddRow(
                new Markup($"{AdaptIcon(" 📊")} [white]RAM [/]{FormatPercentText(currentRamLoadPct)}"),
                sep,
                FormatOnlyProgressBar(currentRamLoadPct),
                new Markup(TruncateWithColors(txtRam ?? "", col3Width))
            );

            hwTable.AddRow(
                new Markup($"{AdaptIcon(" 💾")} [white]DSK [/]{FormatPercentText(currentDiskLoadPct)}"),
                sep,
                FormatOnlyProgressBar(currentDiskLoadPct),
                new Markup(TruncateWithColors(txtDisk ?? "", col3Width))
            );

            return new Panel(hwTable)
                .Header("[cyan bold][[ GLOBAL HARDWARE (HOST) ]][/]")
                .BorderColor(Color.White)
                .SquareBorder()
                .Padding(0, 0, 0, 0)
                .Expand();
        }

        static Panel CreateBannerPanel(string title, Dictionary<string, string> items)
        {
            int panelWidth = Math.Max(20, AnsiConsole.Profile.Width - (UPTIME_PANEL_WIDTH + 5));
            int labelWidth = 12;

            var lines = new List<Markup>();

            foreach (var item in items)
            {
                string rawKey = AdaptIcon(item.Key ?? "");
                string key = rawKey.Length > labelWidth
                    ? rawKey.Substring(0, labelWidth)
                    : rawKey.PadRight(labelWidth);

                string safeKey = Markup.Escape(key);
                string rawLine = $"[white]{safeKey}[/] [white]│[/] {item.Value ?? ""}";
                string finalLine = TruncateWithColors(rawLine, panelWidth - 4);

                lines.Add(new Markup(finalLine));
            }

            var rows = new Rows(lines);

            return new Panel(rows)
                .Header(TruncateWithColors(title, Math.Max(15, AnsiConsole.Profile.Width - (UPTIME_PANEL_WIDTH + 7))))
                .BorderColor(Color.White)
                .SquareBorder();
        }
		// ====================================================================
		// CARTEIRA EM UMA LINHA COM ALINHAMENTO ESTRUTURAL E DECIMAIS ÍNTEGROS
		// ====================================================================
		public static int WalletScreenIndex = 0; // 0 = 1/3, 1 = 2/3, 2 = 3/3

		private static string GetWalletCountdown()
		{
			var state = saladApiService?.State;
			if (state == null) return "5m";

			int elapsed = (int)(DateTime.Now - state.LastPulseTimestamp).TotalSeconds;
			int remaining = Math.Max(0, 300 - elapsed);
			int m = remaining / 60;
			int s = remaining % 60;

			string timeColor = remaining <= 30 ? "red" : (remaining <= 60 ? "yellow" : "grey");
			return $"[{timeColor}]{m}:{s:D2}m[/]";
		}

		static bool IsAnyWorkloadActive()
		{
			// 1. SGS (Bandwidth)
			bool isSgs = sgsStatus == "Online" || isBandwidthActive;

			// 2. Miner, CPU ou GPU
			bool isCompute = !string.IsNullOrEmpty(computeWorkloadType);

			// 3. Container WSL
			bool isContainer = containerStatus.Contains("Running", StringComparison.OrdinalIgnoreCase) || isPullingState;

			return isSgs || isCompute || isContainer;
		}

		private static string BuildSingleLineWallet()
		{
			var s = saladApiService?.State;
			var inv = System.Globalization.CultureInfo.InvariantCulture;

			string dollar = "[bold #00ff88]$[/]";
			string timer = $"{AdaptIcon("⏱️")} {GetWalletCountdown()}";

			double bal  = s?.CurrentBalance ?? 0.0;
			double cer  = s?.EstimatedHourlyRate ?? 0.0;
			double p24  = s?.ProjectedDailyRate ?? 0.0;

			double p5m  = s?.Last5MinEarning ?? 0.0;
			double p1h  = s?.LastHourEarning ?? 0.0;
			double r24  = s?.Last24HourEarning ?? 0.0;

			double p7d  = s?.Last7DayEarning ?? 0.0;
			double p30d = s?.Last30DayEarning ?? 0.0;
			double ltb  = s?.LifetimeBalance ?? 0.0;

			// Snapshot direto do log com tratamento de anomalias
			string est24Display;
			string rawVal = (projected ?? "").Replace("$", "").Trim();

			if (string.IsNullOrWhiteSpace(rawVal) || rawVal.Equals("Computing...", StringComparison.OrdinalIgnoreCase))
			{
				double fb = cer > 0 ? (cer * 24.0) : p24;
				est24Display = $"[bold chartreuse1]${fb.ToString("0.00", inv)}[/]";
			}
			else if (rawVal.StartsWith("-"))
			{
				if (IsAnyWorkloadActive())
				{
					// Bug do Salad (-1 com workload rodando) -> assume a taxa horária x 24
					double fb = cer > 0 ? (cer * 24.0) : p24;
					est24Display = $"[bold chartreuse1]${fb.ToString("0.00", inv)}[/]";
				}
				else
				{
					// Negativo real (sem workload) -> exibe negativo em vermelho
					est24Display = $"[bold red]-${rawVal.TrimStart('-')}[/]";
				}
			}
			else
			{
				// Snapshot puro do log (ex: 0,98) sem alteração de formatação
				est24Display = $"[bold chartreuse1]${rawVal}[/]";
			}

			return WalletScreenIndex switch
			{
				1 => $"5M: {dollar}[bold #00ff88]{p5m.ToString("0.0000", inv)}[/]  [grey30]│[/]  " +
					 $"1H: {dollar}[bold #00ff88]{p1h.ToString("0.0000", inv)}[/]  [grey30]│[/]  " +
					 $"LAST 24H: {dollar}[bold #00ff88]{r24.ToString("0.00", inv)}[/]  [grey30]│[/]  {timer}",

				2 => $"7D: {dollar}[bold #00ff88]{p7d.ToString("0.00", inv)}[/]  [grey30]│[/]  " +
					 $"30D: {dollar}[bold #00ff88]{p30d.ToString("0.00", inv)}[/]  [grey30]│[/]  {timer}",

				_ => $"[bold gold3]${bal.ToString("0.0000", inv)}[/]  [grey30]│[/]  " +
					 $"24H EST: {est24Display}  [grey30]│[/]  " +
					 $"CER: {dollar}[bold #00ff88]{cer.ToString("0.0000", inv)}/h[/]  [grey30]│[/]  {timer}"
			};
		}
    }
}
