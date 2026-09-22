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
        static FigletFont embeddedFont = null;

        // Fixed width for the Uptime panel (synchronized with the banner calculation)
        const int UPTIME_PANEL_WIDTH = 23;

        static bool isLegacyEmojiMode = false;

        // Regex que captura emojis Unicode e símbolos gráficos (com suporte a variation selectors como \uFE0F)
		private static readonly Regex EmojiRegex = new(@"([\uD83C-\uD83F][\uD800-\uDFFF]|[\u2300-\u27BF])\uFE0F?", RegexOptions.Compiled);
        private static readonly Dictionary<string, string> _adaptIconCache = new(StringComparer.OrdinalIgnoreCase);

        static string GenerateProgressBar(double percent, int barLength = 20)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            int filledBlocks = (int)Math.Round((percent / 100.0) * barLength);
            int emptyBlocks = barLength - filledBlocks;

            string filled = new string('█', filledBlocks);
            string empty = new string('░', emptyBlocks);

            return $"[green]{filled}[/][grey]{empty}[/]";
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
                var helpGrid = new Grid().Expand();
                helpGrid.AddColumn(new GridColumn());

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
                    "[yellow][[P]][/]            : Toggle Live CPU Clock (real-time, uses extra ~5-7mb RAM while active).\n" +
                    "[yellow][[S]][/]            : Toggle between [cyan]About[/] and [orange1]Troubleshooting[/] panels.\n" +
                    "[yellow][[H]][/]            : Toggle between Dashboard and this Help screen.\n" +
                    "[yellow][[ESC]][/]          : Safely exit SaladXRay."
                );

                var helpPanel = new Panel(helpText)
                    .Header("[white bold] HELP & CONTROLS [/]", Justify.Left)
                    .BorderColor(Color.Green)
                    .Padding(2, 1, 2, 1);

                helpGrid.AddRow(helpPanel);

                if (showSupportTab)
                {
                    string warningIcon = AdaptIcon("⚠️");
                    var supportText = new Markup(
                        $"[bold yellow]{warningIcon} Don't Panic Over Isolated Errors & Red Logs![/]\n" +
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

                    var navInstruction = new Markup("\n[yellow][[S]][/] View Support & Troubleshooting  |  [blink red][[H]][/] Back to Dashboard");
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

            grid.AddRow(topHeaderGrid);

            grid.AddRow(CreateSection("EARNINGS", new Dictionary<string, string> { 
                { "💰 WALLET", $"[bold green]${balance}[/] | 24H EST: [bold yellow]${projected}[/] | UPDATED: {lastUpdateTimer}" } 
            }));

            string wslColor = wslStatusStr.Contains("Running") ? "green" : wslStatusStr.Contains("STOPPED") ? "red" : "yellow";

            string wslDisplayStatus = wslStatusStr.Contains("Running")
                ? $"[{wslColor}]{Markup.Escape(wslStatusStr)}[/]  {AdaptIcon("⏰")} {workTime}"
                : $"[{wslColor}]{Markup.Escape(wslStatusStr)}[/]";

            string diskLed = isDiskLedActive ? "[bold green]●[/]" : "[grey]○[/]";
            string diskGrowth = vhdxGrowthRateMBs > 0.05 ? $" [yellow]↑ {vhdxGrowthRateMBs:F1} MB/s[/]" : "";
            string vhdxDisplay = $"{diskLed} {wslDiskSize}{diskGrowth}";

            grid.AddRow(CreateSection("LINUX WSL (VIRTUAL MACHINE)", new Dictionary<string, string> {
                { "🖥️ STATUS", wslDisplayStatus },
                { "💾 RAM",    ramUsage },
                { "💿 DISK",   vhdxDisplay },
                { "📡 LAN",    vNetStats }
            }));

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
            else if (isPullingState && globalProgress >= 98.0 && currentVmDownKbps < 1024 && wslRamMB > 800)
            {
                displayContainerStatus = $"[cyan]Unpacking / Extracting... (WSL RAM Spike: {wslRamMB:N0} MB | Low Net I/O)[/]";
            }
            else
            {
                displayContainerStatus = containerStatus;
            }

            grid.AddRow(CreateSection("SALAD CONTAINER WORKLOAD", new Dictionary<string, string> {
                { "🛰️ MATRIX",    matrixStatusWithHeart },
                { "🪪 TYPE",      $"{workloadHardwareType} [grey]/[/] {Markup.Escape(jobId)}" },
                { "📦 CONTAINER", displayContainerStatus }
            }));

            // PAINEL HARDWARE (HOST)
            int terminalWidth = AnsiConsole.Profile.Width;
            int innerWidth = Math.Max(30, terminalWidth - 3);

            const int col1Width = 14;
            const int colSepWidth = 2;
            const int col2Width = 12;
            int col3Width = Math.Max(15, innerWidth - col1Width - colSepWidth - col2Width);

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

            var hwPanel = new Panel(hwTable)
                .Header("[cyan bold][[ GLOBAL HARDWARE (HOST) ]][/]")
                .BorderColor(Color.White)
                .SquareBorder()
                .Padding(0, 0, 0, 0)
                .Expand();

            grid.AddRow(hwPanel);

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

            string sgsUptimeStr = sgsStartTime != DateTime.MinValue ? FormatUptime(DateTime.Now - sgsStartTime) : "Offline";
            string sgsColor = sgsStatus == "Online" ? "green" : "grey";

            string sgsOneLine = sgsStatus == "Online"
                ? $"[{sgsColor}]{sgsStatus}[/] | Up: {sgsUptimeStr} | CPU: {sgsCpuUsagePct:F1}% | RAM: {sgsRamMB2:F1} MB | Net: {sgsNetworkSpeed}"
                : "[grey]Offline[/]";

            var hostWorkloads = new Dictionary<string, string>
            {
                { "🌐 SGS NODE", sgsOneLine }
            };

            grid.AddRow(CreateSection("WINDOWS HOST WORKLOADS", hostWorkloads));

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

        // =========================================================================
        // MOTOR DE TRUNCAMENTO SEGURO
        // =========================================================================
        private static string TruncateWithColors(string text, int maxLength)
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

            sb.Append("...");

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

            foreach (var item in items)
            {
                string rawVal = item.Value ?? "";
                string truncatedVal = TruncateWithColors(rawVal, Math.Max(10, AnsiConsole.Profile.Width - 22));

                Markup valueMarkup;
                try
                {
                    valueMarkup = new Markup($"[white]│[/] {truncatedVal}");
                }
                catch
                {
                    string safeVal = TruncateWithColors(Markup.Escape(rawVal), Math.Max(10, AnsiConsole.Profile.Width - 22));
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
    }
}
