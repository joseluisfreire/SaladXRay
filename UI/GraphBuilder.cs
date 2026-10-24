using Spectre.Console;
using Spectre.Console.Rendering;
using SaladXRayPanel.Models;
using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace SaladXRay.Rendering
{
    public static class GraphBuilder
    {
        private static readonly char[] SubBlocks = { '▁', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };
        private const string GridColor = "silver";
        private const string hmColor = "cyan";
		private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        // =====================================================================
        // PÚBLICO — usado pelo Dashboard (WalletScreenIndex == 3)
        // =====================================================================

		// Larguras do layout
		public const int LEFT_COL_W   = 16;  // YOUR BALANCE / EST 24H / StatRow (SUM e AVG)
		public const int PULSE_COL_W  = 22;  // LAST IN / 1H EARNINGS SUM (dentro da linha 1H)
		public const int PULSE_SIDE_W = 25;  // MINING REWARDS (coluna da direita do W4)

		public static Panel BuildCompactGraph1hRow(XRayData data)
		{
			var leftCol = new Layout("Left")
				.SplitRows(
					new Layout("Balance").Ratio(1),
					new Layout("Est24h").Ratio(1)
				);

			leftCol["Balance"].Update(BuildMiniFigletPanel("YOUR BALANCE", data.CurrentBalance, showExtraDigits: true, justify: Justify.Right));
			leftCol["Est24h"].Update(BuildMiniFigletPanel("EST 24H", data.ProjectedDailyRate, showExtraDigits: false, justify: Justify.Center));

			var pulseCol = new Layout("Pulse")
				.SplitRows(
					new Layout("Latest").Ratio(1),
					new Layout("Sum1h").Ratio(1)
				);

			pulseCol["Latest"].Update(BuildLatestPulsePanel(data));
			pulseCol["Sum1h"].Update(BuildLast1HPulseSumPanel(data));

			var root = new Layout("Row")
				.SplitColumns(
					new Layout("LeftCol").Size(LEFT_COL_W),
					new Layout("RightCol"),
					new Layout("PulseCol").Size(PULSE_COL_W)
				);

			root["LeftCol"].Update(leftCol);
			root["RightCol"].Update(BuildCompactGraph1hPanel(data));
			root["PulseCol"].Update(pulseCol);

			return new Panel(root)
			{
				Border = BoxBorder.None,
				Height = 12,
				Padding = new Padding(0, 0, 0, 0),
				Expand = true
			};
		}

		public static Panel BuildCompactGraph1hPanel(XRayData data)
		{
			var pulseData = BuildRawPulseGraphData(data);
			int totalWidth = Math.Max(40, AnsiConsole.Profile.Width);
			DateTime? oldestTs = pulseData.Count > 0 ? pulseData[0].Bucket : (DateTime?)null;

			return BuildRangePanel("TRAILING 1H EARNINGS CHART", pulseData, height: 4, maxAvailableWidth: totalWidth, slotWidth: 3, showXAxisRuler: true, oldestTs: oldestTs);
		}

		public static IRenderable BuildW4Screen(XRayData d, IRenderable pulsePanel,
			double sum24h, double sum7d, double sum30d)
		{
			var left = new Rows(
				BuildCompactGraph1hRow(d),   // era BuildCompactGraph1hRowW4
				StatRow("24H", "/H", sum24h, 24, BuildGraph24hPanel(d)),
				StatRow("7D",  "/D", sum7d,   7, BuildGraph7dPanel(d)),
				StatRow("30D", "/D", sum30d, 30, BuildGraph30dPanel(d)));

			var body = new Grid().Expand();
			body.AddColumn(new GridColumn().Padding(0, 0, 0, 0));
			body.AddColumn(new GridColumn().NoWrap().Width(PULSE_SIDE_W).Padding(0, 0, 0, 0));
			body.AddRow(left, pulsePanel);

			return body;
		}

		// SUM e AVG vêm da API (janela móvel). AVG = SUM / nº de períodos.  (cálculo intacto)
		private static IRenderable StatRow(string tag, string unit,
			double sum, int periods, Panel graph)
		{
			double avg = periods > 0 ? sum / periods : 0.0;

			var leftCol = new Layout("Left")
				.SplitRows(
					new Layout("Sum").Ratio(1),
					new Layout("Avg").Ratio(1)
				);

			leftCol["Sum"].Update(BuildMiniFigletPanel($"{tag} SUM", sum, justify: Justify.Center));
			leftCol["Avg"].Update(BuildMiniFigletPanel($"{tag} AVG {unit}", avg, justify: Justify.Center));

			var root = new Layout("Row")
				.SplitColumns(
					new Layout("LeftCol").Size(LEFT_COL_W),
					new Layout("RightCol")
				);

			root["LeftCol"].Update(leftCol);
			root["RightCol"].Update(graph);

			return new Panel(root)
			{
				Border = BoxBorder.None,
				Height = 12,
				Padding = new Padding(0, 0, 0, 0),
				Expand = true
			};
		}
//
		public static Panel BuildGraph24hPanel(XRayData d) =>
			LongPanel("24H", GroupByHour(d, 24), 2, "HH", "HH", 2);

		public static Panel BuildGraph7dPanel(XRayData d) =>
			LongPanel("7D", GroupByDay(d, 7), 4, "ddd", "dd/MM", 3);

		public static Panel BuildGraph30dPanel(XRayData d) =>
			LongPanel("30D", GroupByDay(d, 30), 2, "dd", "dd/MM", 3);

		private static Panel LongPanel(string title, List<(DateTime Bucket, double Value)> data,
			int slot, string topFmt, string botFmt, int botStep) =>
			BuildRangePanel(title, data, 4, 0, slotWidth: slot, showXAxisRuler: true,
				oldestTs: null, topFmt: topFmt, botFmt: botFmt, botStep: botStep);

        public static Panel BuildLatestPulsePanel(XRayData data)
        {
            var sourceBuffer = data?.Last5MinPulseBuffer ?? new List<(DateTime Timestamp, double Value)>();
            (DateTime Timestamp, double Value) lastPulse = default;
            bool hasPulse = false;

            lock (sourceBuffer)
            {
                if (sourceBuffer.Count > 0)
                {
                    lastPulse = sourceBuffer.OrderByDescending(p => p.Timestamp).First();
                    hasPulse = true;
                }
            }

            double pulseVal = hasPulse ? lastPulse.Value : 0.0;
            int totalSeconds = data.PulseElapsedSeconds;
            bool isOverdue = totalSeconds > 300;
            double progress = Math.Clamp(totalSeconds / 300.0, 0.0, 1.0);

            const int totalBlocks = 8;
            int activeBlocks = isOverdue
                ? totalBlocks
                : (int)Math.Clamp(Math.Round(progress * totalBlocks), 0, totalBlocks);
            if (!isOverdue && progress < 1.0 && activeBlocks == totalBlocks) activeBlocks = totalBlocks - 1;

            string stageColor = isOverdue ? "deeppink1"
                : (progress >= 0.90 ? "deeppink1"
                : (progress >= 0.60 ? "gold1"
                : "springgreen1"));

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < totalBlocks; i++)
            {
                if (i < activeBlocks)
                {
                    string blockColor = (i % 2 == 0) ? stageColor : "gray0";
                    sb.Append($"[{blockColor}]▰[/]");
                }
                else
                {
                    sb.Append("[gray23]▱[/]");
                }
            }
            string spinnerMarkup = sb.ToString();

			var panel = BuildMiniFigletPanel("LATEST RETURN", pulseVal, showExtraDigits: false, fullDigits: true,
				justify: Justify.Center, decimals: 6, flap: true,
				flapStamp: hasPulse ? lastPulse.Timestamp.Ticks : 0);
            panel.BorderStyle(new Style(Color.Gold1, decoration: Decoration.Bold));
            panel.Header = new PanelHeader($"[paleturquoise1] LAST IN [/]{spinnerMarkup}", Justify.Left);

            return panel;
        }

        public static Panel BuildLast1HPulseSumPanel(XRayData data)
        {
            var sourceBuffer = data?.Last5MinPulseBuffer ?? new List<(DateTime Timestamp, double Value)>();
            List<(DateTime Timestamp, double Value)> buffer;

            lock (sourceBuffer)
            {
                buffer = sourceBuffer.OrderByDescending(p => p.Timestamp).ToList();
            }

            var windowEnd = DateTime.Now;
            var windowStart = windowEnd.AddHours(-1);
            if (windowStart < data.AppStartTime) windowStart = data.AppStartTime;

			var inWindow = buffer.Where(p => p.Timestamp >= windowStart).ToList(); // já em ordem decrescente
			double sum = inWindow.Sum(p => p.Value);

			long stamp = inWindow.Count > 0
				? HashCode.Combine(inWindow.Count, inWindow[0].Timestamp.Ticks, inWindow[^1].Timestamp.Ticks)
				: 0;

			var panel = BuildMiniFigletPanel("PULSE SUM 1H", sum, showExtraDigits: false, fullDigits: true,
				justify: Justify.Center, decimals: 6, flap: true, flapStamp: stamp);
						panel.Header = new PanelHeader("[paleturquoise1] 1H EARNINGS SUM [/]", Justify.Center);
            return panel;
        }

        // =====================================================================
        // DADOS
        // =====================================================================

        private static List<(DateTime Bucket, double Value)> BuildRawPulseGraphData(XRayData data)
        {
            var sourceBuffer = data?.Last5MinPulseBuffer ?? new List<(DateTime Timestamp, double Value)>();
            List<(DateTime Timestamp, double Value)> buffer;

            lock (sourceBuffer)
            {
                buffer = sourceBuffer.OrderBy(p => p.Timestamp).ToList();
            }

            var oneHourAgo = DateTime.Now.AddHours(-1);
            return buffer
                .Where(p => p.Timestamp >= oneHourAgo && p.Timestamp >= data.AppStartTime)
                .Select(p => (p.Timestamp, p.Value))
                .ToList();
        }

		private static IEnumerable<(DateTime Timestamp, double Value)> LocalPoints(XRayData d) =>
			(d?.EarningHistoryPoints ?? new List<(DateTime Timestamp, double Value)>())
			.Select(p => (p.Timestamp.ToLocalTime(), p.Value));

		private static List<(DateTime Bucket, double Value)> GroupByHour(XRayData data, int hours)
		{
			var result = new List<(DateTime, double)>();
			var now = DateTime.Now;
			var lookup = LocalPoints(data)
				.GroupBy(p => new DateTime(p.Timestamp.Year, p.Timestamp.Month, p.Timestamp.Day, p.Timestamp.Hour, 0, 0))
				.ToDictionary(g => g.Key, g => g.Sum(p => p.Value));

			var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
			for (int i = hours - 1; i >= 0; i--)
			{
				var bucket = currentHour.AddHours(-i);
				result.Add((bucket, lookup.TryGetValue(bucket, out var v) ? v : 0.0));
			}
			return result;
		}

		private static List<(DateTime Bucket, double Value)> GroupByDay(XRayData data, int days)
		{
			var result = new List<(DateTime, double)>();
			var today = DateTime.Now.Date;
			var lookup = LocalPoints(data)
				.GroupBy(p => p.Timestamp.Date)
				.ToDictionary(g => g.Key, g => g.Sum(p => p.Value));

			for (int i = days - 1; i >= 0; i--)
			{
				var bucket = today.AddDays(-i);
				result.Add((bucket, lookup.TryGetValue(bucket, out var v) ? v : 0.0));
			}
			return result;
		}

        // =====================================================================
        // MOTOR DO GRÁFICO + RÉGUAS
        // =====================================================================

		private sealed class GraphRenderable : IRenderable
		{
			private readonly List<(DateTime Bucket, double Value)> _data;
			private readonly int _height, _slotWidth, _botStep;
			private readonly bool _showXAxisRuler;
			private readonly DateTime? _oldestTs;
			private readonly string _topFmt, _botFmt;
			
			public GraphRenderable(List<(DateTime Bucket, double Value)> data, int height, int slotWidth,
				bool showXAxisRuler, DateTime? oldestTs = null, string topFmt = null, string botFmt = null, int botStep = 3)
			{
				_data = data; _height = height; _slotWidth = slotWidth;
				_showXAxisRuler = showXAxisRuler; _oldestTs = oldestTs;
				_topFmt = topFmt; _botFmt = botFmt; _botStep = botStep;
			}

			public Measurement Measure(RenderOptions options, int maxWidth)
			{
				int yAxisWidth = maxWidth >= 24 ? 11 : 0;
				int barArea = _data.Count * _slotWidth;
				int idealWidth = Math.Min(maxWidth, Math.Max(24, yAxisWidth + barArea));
				return new Measurement(idealWidth, idealWidth);
			}

			public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
			{
				string s = BuildMultiLineGraph(_data, _height, maxWidth, _slotWidth, _data.Count,
					_showXAxisRuler, _oldestTs, _topFmt, _botFmt, _botStep);
				IRenderable markup = new Markup(s);
				return markup.Render(options, maxWidth);
			}
		}

		private static Panel BuildRangePanel(string title, List<(DateTime Bucket, double Value)> data, int height,
			int maxAvailableWidth, int slotWidth, bool showXAxisRuler = false, DateTime? oldestTs = null,
			string topFmt = null, string botFmt = null, int botStep = 3)
		{
			var content = new GraphRenderable(data, height, slotWidth, showXAxisRuler, oldestTs, topFmt, botFmt, botStep);
			return new Panel(content)
			{
				Header = new PanelHeader($"[paleturquoise1]{title}[/]", Justify.Center),
				Border = BoxBorder.Square,
				BorderStyle = new Style(Color.Gold1),
				Height = showXAxisRuler ? 10 : 8,
				Padding = new Padding(0, 0, 4, 0),
			};
		}

        private static string ColorizeRuler(char[] chars, int hlStart, int hlLen, string baseColor, string hlColor)
        {
            if (hlStart < 0 || hlStart >= chars.Length)
                return $"[{baseColor}]{Markup.Escape(new string(chars))}[/]";

            hlLen = Math.Min(hlLen, chars.Length - hlStart);
            string before = new string(chars, 0, hlStart);
            string mid = new string(chars, hlStart, hlLen);
            string after = new string(chars, hlStart + hlLen, chars.Length - hlStart - hlLen);

            return $"[{baseColor}]{Markup.Escape(before)}[/][{hlColor}]{Markup.Escape(mid)}[/][{baseColor}]{Markup.Escape(after)}[/]";
        }
		
		private static string ColorizeBaseline(string rawLine)
			=> Markup.Escape(rawLine).Replace("_", "[grey50]▁[/]");

		private static string BuildMultiLineGraph(List<(DateTime Bucket, double Value)> data, int height, int width, int slotWidth, int? fixedBarCount = null, bool showXAxisRuler = false, DateTime? oldestTs = null, string topFmt = null, string botFmt = null, int botStep = 3)
		{
			if (width <= 0 || height <= 0) return string.Empty;

			int yAxisWidth = width >= 24 ? 11 : 0;
            int graphWidth = yAxisWidth > 0 ? width - 11 : width;
            if (graphWidth <= 0) graphWidth = 1;

            int usableGraphWidth = yAxisWidth > 0 ? graphWidth - 1 : graphWidth;
            if (usableGraphWidth <= 0) usableGraphWidth = 1;

            // true  = corta à ESQUERDA (descarta antigos, "agora" sempre visível) -> +N… aparece à esquerda
            // false = corta à DIREITA  (descarta recentes)
            const bool cutOldest = true;

            int maxPossibleBars = Math.Max(1, (usableGraphWidth + 1) / slotWidth);

            int hidden = 0;
            if (showXAxisRuler && data.Count > maxPossibleBars)
            {
                hidden = data.Count - maxPossibleBars;
                data = cutOldest
                    ? data.Skip(hidden).ToList()
                    : data.Take(maxPossibleBars).ToList();
            }

            int targetBars = showXAxisRuler
                ? Math.Clamp(data.Count, 1, maxPossibleBars)
                : Math.Clamp(fixedBarCount ?? maxPossibleBars, 1, maxPossibleBars);

            var values = data.Select(d => d.Value).ToList();
            var resampled = showXAxisRuler ? values : Resample(values, targetBars);
            double max = resampled.Count > 0 ? resampled.Max() : 0;
            int oldestIdx = oldestTs.HasValue ? data.FindIndex(d => d.Bucket == oldestTs.Value) : -1;
            double secsLeft = oldestTs.HasValue ? 3600 - (DateTime.Now - oldestTs.Value).TotalSeconds : double.MaxValue;
            string hlColor = secsLeft <= 5 ? "orange1 blink" : "orange1";
            int hlCol = oldestIdx >= 0 ? oldestIdx * slotWidth : -1;
            if (max <= 0) max = 0.0001;

            char[,] grid = new char[height, usableGraphWidth];
            for (int r = 0; r < height; r++)
                for (int c = 0; c < usableGraphWidth; c++)
                    grid[r, c] = ' ';

            int barWidth = Math.Max(1, slotWidth - 1);

            for (int barIdx = 0; barIdx < Math.Min(targetBars, resampled.Count); barIdx++)
            {
                double val = resampled[barIdx];
                double normalizedVal = val / max;
                double rawHeight = normalizedVal * height;

                int colStart = barIdx * slotWidth;

                for (int w = 0; w < barWidth; w++)
                {
                    int colIdx = colStart + w;
                    if (colIdx >= usableGraphWidth) break;

                    const double epsilon = 0.001; // margem contra erro de arredondamento double
                    for (int row = 0; row < height; row++)
                    {
                        double rowBottom = row;
                        double rowTop = row + 1;

                        if (rawHeight >= rowTop - epsilon)
                            grid[row, colIdx] = '█';
                        else if (rawHeight > rowBottom + epsilon)
                        {
                            double fraction = rawHeight - rowBottom;
                            int blockIndex = (int)Math.Round(fraction * (SubBlocks.Length - 1));
                            blockIndex = Math.Clamp(blockIndex, 0, SubBlocks.Length - 1);
                            grid[row, colIdx] = SubBlocks[blockIndex];
                        }
                    }

                    if (val > 0 && grid[0, colIdx] == ' ')
                        grid[0, colIdx] = '_';
                }
            }

            var lines = new List<string>();

            string maxRawValueStr = $"${max.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}";
            string maxPaddedValue = maxRawValueStr.PadLeft(8);
            int spacesBeforeCorner = maxPaddedValue.Length + 1;

            string topPrefix = yAxisWidth > 0 ? new string(' ', spacesBeforeCorner) + "┌" : string.Empty;
            string bottomPrefix = yAxisWidth > 0 ? new string(' ', spacesBeforeCorner) + "└" : string.Empty;

            // ---- Régua X superior (minutos) ----
            if (showXAxisRuler && yAxisWidth > 0)
            {
				var topLabelChars = new char[usableGraphWidth + 1];
				for (int i = 0; i < topLabelChars.Length; i++) topLabelChars[i] = ' ';

				int topStep = topFmt == null ? 1 : Math.Max(1, (topFmt.Length + 1 + slotWidth - 1) / slotWidth);
				List<int> topIdx = topFmt == "HH" ? LabelIdx(data, targetBars, 0, true, 2)      // 24H: horas pares
								 : topFmt == "dd" ? LabelIdx(data, targetBars, 1, false, 2)     // 30D: dias ímpares
								 : Enumerable.Range(0, targetBars).Where(i => i % topStep == 0).ToList();
				foreach (int barIdx in topIdx)
				{
					int colStart = barIdx * slotWidth;
					if (colStart >= usableGraphWidth || barIdx >= data.Count) break;

					string minuteLabel = topFmt == null ? data[barIdx].Bucket.Minute.ToString("D2")
														: data[barIdx].Bucket.ToString(topFmt, Inv);
                    int labelStart = Math.Max(0, colStart);
					for (int c = 0; c < minuteLabel.Length && (labelStart + c) < topLabelChars.Length; c++)
                        topLabelChars[labelStart + c] = minuteLabel[c];
                }

                if (hidden > 0 && !cutOldest)
                {
                    string tag = $"+{hidden}…";
                    int start = Math.Max(0, usableGraphWidth - tag.Length);
                    for (int i = 0; i < tag.Length && (start + i) < usableGraphWidth; i++)
                        topLabelChars[start + i] = tag[i];
                }

				int topShift = (topFmt != null && topFmt.Length <= 2) ? 1 : 0;
				string topAxisPad = (hidden > 0 && cutOldest)
					? $"[yellow]{$"+{hidden}…".PadLeft(spacesBeforeCorner + 1 - topShift)}[/] "
					: new string(' ', spacesBeforeCorner + 2 - topShift);

                lines.Add($"{topAxisPad}{ColorizeRuler(topLabelChars, hlCol, 2, hmColor, hlColor)}");
            }

            // ---- Borda superior ----
            if (yAxisWidth > 0)
            {
                char[] topChars = new string('─', usableGraphWidth + 1).ToCharArray();

                if (showXAxisRuler)
                {
                    for (int barIdx = 0; barIdx < targetBars; barIdx++)
                    {
                        int colIdx = barIdx * slotWidth;
                        if (colIdx >= 0 && colIdx < topChars.Length) topChars[colIdx] = '┬';
                    }
                }

                lines.Add($"[{GridColor}]{topPrefix}{new string(topChars)}┐[/]");
            }
            else
            {
                lines.Add($"[{GridColor}]{new string('─', usableGraphWidth)}[/]");
            }

            // ---- Corpo + régua Y ----
            for (int row = height - 1; row >= 0; row--)
            {
                var graphLineChars = new char[usableGraphWidth];
                for (int col = 0; col < usableGraphWidth; col++)
                    graphLineChars[col] = grid[row, col];
                string rawGraphLine = new string(graphLineChars);

                if (yAxisWidth > 0)
                {
                    // Ancorado de cima pra baixo, com recuo fino de 1/4 de casa (ajuste visual)
                    double rowVal = height > 0 ? max * ((double)row + 0.22) / height : max;
                    string rawValueStr = $"${rowVal.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}";
                    string paddedValue = rawValueStr.PadLeft(8);
                    string yAxisMarkup = $"[{GridColor}]{paddedValue}[/] [{GridColor}]┤[/] ";

                    lines.Add($"{yAxisMarkup}[springgreen2]{ColorizeBaseline(rawGraphLine)}[/][{GridColor}]│[/]");

                }
                else
                {
                    lines.Add($"[springgreen2]{ColorizeBaseline(rawGraphLine)}[/]");
                }
            }
			
			List<int> botIdx = botFmt == "HH" ? LabelIdx(data, targetBars, 1, true, 2)                      // 24H: horas ímpares
							 : (botFmt == "dd/MM" && topFmt == "dd") ? LabelIdx(data, targetBars, 0, false, 4) // 30D: dias pares, a cada 4
							 : Enumerable.Range(0, targetBars).Where(i => i % botStep == 0).ToList();
							 
            // ---- Borda inferior ----
            if (yAxisWidth > 0)
            {
                char[] bottomChars = new string('─', usableGraphWidth + 1).ToCharArray();

                if (showXAxisRuler)
                {
				foreach (int barIdx in botIdx)
				{
					int colStart = barIdx * slotWidth;
					int colEnd = colStart + barWidth + 1;

                        if (colStart >= 0 && colStart < bottomChars.Length) bottomChars[colStart] = '┴';
                        if (colEnd >= 0 && colEnd < bottomChars.Length) bottomChars[colEnd] = '┴';
                    }
                }

                lines.Add($"[{GridColor}]{bottomPrefix}{new string(bottomChars)}┘[/]");

            }
            else
            {
                lines.Add($"[grey30]{new string('─', usableGraphWidth)}[/]");
            }

            // ---- Régua X inferior (HH:mm) ----
            if (showXAxisRuler && yAxisWidth > 0)
            {
                // +6 de folga: label "HH:mm" tem 5 chars
                var labelChars = new char[usableGraphWidth + 6];
                for (int i = 0; i < labelChars.Length; i++) labelChars[i] = ' ';

					foreach (int barIdx in botIdx)
					{
						int colStart = barIdx * slotWidth;
						if (colStart >= usableGraphWidth || barIdx >= data.Count) break;

                    string label = data[barIdx].Bucket.ToString(botFmt ?? "HH:mm", Inv);
                    for (int c = 0; c < label.Length && (colStart + c) < labelChars.Length; c++)
                        labelChars[colStart + c] = label[c];
                }

				int botPad = (botFmt != null && botFmt.Length <= 2) ? spacesBeforeCorner + 1 : spacesBeforeCorner - 1;
				string axisPad = new string(' ', Math.Max(0, botPad));
                lines.Add($"{axisPad}{ColorizeRuler(labelChars, (oldestIdx >= 0 && oldestIdx % botStep == 0) ? hlCol : -1, 5, hmColor, hlColor)}");
            }

            return string.Join("\n", lines);
        }
		
		private static List<int> LabelIdx(List<(DateTime Bucket, double Value)> d, int count, int parity, bool byHour, int gap)
		{
			var r = new List<int>();
			int last = -999;
			for (int i = 0; i < Math.Min(count, d.Count); i++)
			{
				int k = byHour ? d[i].Bucket.Hour : d[i].Bucket.Day;
				if (k % 2 == parity && i - last >= gap) { r.Add(i); last = i; }
			}
			return r;
		}
		
        private static List<double> Resample(List<double> source, int targetCount)
        {
            var output = new List<double>();
            if (source == null || source.Count == 0)
            {
                for (int i = 0; i < targetCount; i++) output.Add(0);
                return output;
            }

            if (source.Count == 1)
            {
                double val = source[0];
                for (int i = 0; i < targetCount; i++) output.Add(val);
                return output;
            }

            for (int i = 0; i < targetCount; i++)
            {
                double sourceIndex = (double)i / (targetCount - 1) * (source.Count - 1);
                int low = (int)Math.Floor(sourceIndex);
                int high = (int)Math.Ceiling(sourceIndex);
                double t = sourceIndex - low;

                output.Add((1 - t) * source[low] + t * source[high]);
            }

            return output;
        }
		
		//barrinha de demanda
		private static double ComputeCurrentRate(XRayData d)
		{
			if (d == null) return 0.0;

			double sum = 0.0;
			bool any = false;
			DateTime from = DateTime.Now.AddHours(-1);

			var src = d.Last5MinPulseBuffer;
			if (src != null)
			{
				lock (src)
				{
					foreach (var p in src)
					{
						if (p.Timestamp < from) continue;
						sum += p.Value;
						any = true;
					}
				}
			}

			// soma dos pulsos da última hora = $/hora
			return any ? sum : d.ProjectedDailyRate / 24.0; // fallback só sem pulsos
		}

		public static IRenderable BuildRateBar(XRayData d)
		{
			return new Markup(string.Join("\n", BuildSideBar(d, 16)));
		}

		private static string[] BuildSideBar(XRayData d, int w)
		{
			int inner = w - 2;
			double cer = ComputeCurrentRate(d);

			// ===== Régua da Salad (em cima): avg -> max, sem zero =====
			double sAvg = d.HasOfficialRate ? d.OfficialAvgRateHour * 24.0 : 0.0;
			double sMax = d.HasOfficialRate ? d.OfficialMaxRateHour * 24.0 : 0.0;

			string arrowRow = "";
			string rulerRow = "";
			if (sMax > sAvg)
			{
				string loT = "$" + sAvg.ToString("F2", Inv);
				string hiT = "$" + sMax.ToString("F2", Inv);
				int lineW = Math.Max(4, w - loT.Length - hiT.Length);
				rulerRow = $"[cyan1]{loT}[/][grey37]├{new string('─', lineW - 2)}┤[/][deeppink1]{hiT}[/]";
			}

			// ===== Barra horária (Novatech min -> max) =====
			double nMn = d.HasNovaRate ? d.NovaMinRateHour : 0.0;
			double nMx = d.HasNovaRate ? d.NovaMaxRateHour : 0.0;
			bool nova = nMx > nMn && nMx > 0;

			double lo = nova ? nMn : 0.0;
			double hi = nova ? nMx : Math.Max(cer, 0.0001);

			double frac = Math.Clamp((cer - lo) / (hi - lo), 0.0, 1.0);
			int pos = (int)Math.Round(frac * (inner - 1));
			int filled = cer > lo ? pos + 1 : 0;

			var barRow = new System.Text.StringBuilder($"[{GridColor}]│[/]");
			var botRow = new System.Text.StringBuilder($"[{GridColor}]└[/]");
			for (int i = 0; i < inner; i++)
			{
				barRow.Append(i < filled ? "[springgreen2]█[/]" : "[grey23]░[/]");
				botRow.Append(cer > 0 && i == pos ? "[gold1]▲[/]" : $"[{GridColor}]┬[/]");
			}
			barRow.Append($"[{GridColor}]│[/]");
			botRow.Append($"[{GridColor}]┘[/]");

			string loTxt = "$" + lo.ToString(nova ? "F2" : "F0", Inv);
			string hiTxt = "$" + hi.ToString(nova ? "F2" : "F4", Inv);
			string labels = loTxt + new string(' ', Math.Max(1, w - loTxt.Length - hiTxt.Length)) + hiTxt;

			// 6 linhas: [0]=borda sup do gráfico ... [5]=borda inf do gráfico
			return new[]
			{
				arrowRow,
				rulerRow,
				$"[{GridColor}]┌{new string('┴', inner)}┐[/]",
				barRow.ToString(),
				botRow.ToString(),
				$"[grey]{labels}[/]"
			};
		}

		//Helper split-flap
		private sealed class FlapState { public long LastStamp; public long StartTick = -1; }
		private static readonly Dictionary<string, FlapState> _flap = new();
		private static readonly char[] FlapChars = "0123456789".ToCharArray();
		public static bool FlapActive;

		private static string Flap(string key, string target, long stamp)
		{
			long now = Environment.TickCount64;
			if (!_flap.TryGetValue(key, out var st))
				_flap[key] = st = new FlapState { LastStamp = stamp };  // 1ª chamada: não anima
			else if (stamp != st.LastStamp)
			{
				st.LastStamp = stamp;                                   // 0 -> 1º pulso também dispara
				st.StartTick = now;
			}

			if (st.StartTick < 0) return target;                        // sem gatilho: parado

			var sb = new StringBuilder(target.Length);
			bool allLocked = true;
			for (int i = 0; i < target.Length; i++)
			{
				char c = target[i];
				/*int pos = target.Length - 1 - i;                        // trava da direita p/ esquerda
				bool locked = now >= st.StartTick + 300 + pos * 230;*/
				//trava 1 por 1 , pra travar todos de uma vez tira o + 300
				bool locked = now >= st.StartTick + 300 + i * 230;
				if (locked || !char.IsDigit(c)) sb.Append(c);
				else { sb.Append(FlapChars[Random.Shared.Next(FlapChars.Length)]); allLocked = false; }
			}

			if (allLocked) st.StartTick = -1;
			else FlapActive = true;
			return sb.ToString();
		}

        // =====================================================================
        // FIGLET MINI
        // =====================================================================

		private static Panel BuildMiniFigletPanel(string label, double value, bool showExtraDigits = false,
			Justify justify = Justify.Right, int decimals = 4, bool flap = false, long flapStamp = 0,
			bool fullDigits = false)
		{
			int mainDecimals = fullDigits ? decimals : Math.Max(decimals - 2, 0);
			int extraDecimals = showExtraDigits ? 2 : 0;
            string fullValue = value.ToString("F" + (mainDecimals + extraDecimals), System.Globalization.CultureInfo.InvariantCulture);
            int dotIndex = fullValue.IndexOf('.');
            string rawMainPart = dotIndex >= 0 ? fullValue.Substring(0, Math.Min(fullValue.Length, dotIndex + 1 + mainDecimals)) : fullValue;
            string extraPart = dotIndex >= 0 && fullValue.Length > dotIndex + 1 + mainDecimals ? fullValue.Substring(dotIndex + 1 + mainDecimals) : "00";
            int intDigits = dotIndex >= 0 ? dotIndex : rawMainPart.Length;
            string mainPart = intDigits >= 3 ? rawMainPart : $"${rawMainPart}";
			if (flap) mainPart = Flap(label, mainPart, flapStamp);

			IRenderable figletComponent;
			var font = SaladXRayPanel.Program.GetFont("miniwi");

			figletComponent = font != null
				? new FigletText(font, mainPart) { Justification = justify, Color = Color.SpringGreen2 }
				: new FigletText(mainPart) { Justification = justify, Color = Color.SpringGreen2 };
						IRenderable panelContent = figletComponent;

            if (showExtraDigits)
            {
				if (flap) extraPart = Flap(label + "_x", extraPart, flapStamp);
                string d1 = extraPart.Length > 0 ? extraPart[0].ToString() : "0";
                string d2 = extraPart.Length > 1 ? extraPart[1].ToString() : "0";

                var stackedDigits = new Rows(
                    new Markup($"[springgreen2]{Markup.Escape(d1)}[/]"),
                    new Markup($"[springgreen2]{Markup.Escape(d2)}[/]")
                );

                var bottomAligned = new Align(stackedDigits, HorizontalAlignment.Left, VerticalAlignment.Bottom)
                {
                    Height = 3
                };

                int mainColWidth = Math.Max(14, mainPart.Length * 2);
                var grid = new Grid();
                grid.AddColumn(new GridColumn().NoWrap().Width(mainColWidth).Padding(0, 0, 0, 0));
                grid.AddColumn(new GridColumn().NoWrap().Width(2).Padding(0, 0, 0, 0));

                grid.AddRow(figletComponent, bottomAligned);

                panelContent = new Align(grid, HorizontalAlignment.Right, VerticalAlignment.Bottom);
            }

            return new Panel(panelContent)
            {
                Header = new PanelHeader($"[paleturquoise1]{label}[/]", Justify.Center),
                Border = BoxBorder.Square,
                BorderStyle = new Style(Color.Gold1),
                Height = 5,
                Padding = new Padding(0, 0, 0, 0),
                Expand = true
            };
        }
    }
}
