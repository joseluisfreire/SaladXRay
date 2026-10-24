#nullable disable
#pragma warning disable CA1416

using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Spectre.Console;
using Spectre.Console.Rendering;
using SaladXRay.Rendering;
using SaladXRayPanel.Models;
using SaladXRayPanel.Services;

namespace SaladXRayPanel
{
    partial class Program
    {
        static void SyncXrayGraphData()
        {
            var s = saladApiService.State;
            xrayGraphData.AppStartTime = appStartTime;
            xrayGraphData.PulseElapsedSeconds = (int)(DateTime.Now - s.LastPulseTimestamp).TotalSeconds;
            xrayGraphData.Last5MinPulseBuffer = s.Last5MinPulseBuffer;
            xrayGraphData.EarningHistoryPoints = s.EarningHistoryPoints;
            xrayGraphData.CurrentBalance = s.CurrentBalance;
            xrayGraphData.ProjectedDailyRate = s.ProjectedDailyRate;
        }

		static IRenderable GetCurrentScreen(string logFile)
		{
			//SyncXrayGraphData();
			return RenderPanel(logFile);
		}

		static System.Diagnostics.PerformanceCounter? _wsPriv;

		static string MemInfo()
		{
			using var p = System.Diagnostics.Process.GetCurrentProcess();
			try
			{
				_wsPriv ??= new System.Diagnostics.PerformanceCounter(
					"Process", "Working Set - Private", p.ProcessName, true);
				return $"SaladXray ~{(long)_wsPriv.NextValue() / 1048576}mb";
			}
			catch
			{
				return $"Mem={p.PrivateMemorySize64 / 1048576}mb";
			}
		}

		static async Task ApiPollLoopAsync()
		{
			var sched = new PollScheduler();

			while (true)
			{
				bool changed = false;
				try
				{
					if (saladApiService != null)
					{
						changed = await saladApiService.PollAsync();
						if (saladApiService.LastCallOk) sched.OnOk();
						else sched.OnError(saladApiService.RetryAfter);
					}
				}
				catch { sched.OnError(null); }

					List<(DateTime Ts, double Value)> snap;
					var buf = saladApiService?.State.Last5MinPulseBuffer;
					if (buf == null) snap = new();
					else lock (buf) snap = new List<(DateTime Ts, double Value)>(buf);

				var d = sched.NextDelay(DateTime.Now, snap);

		#if DEBUG
				try
				{
					File.AppendAllText(@"C:\temp\salad_api.log",
						$"{DateTime.Now:HH:mm:ss} POLL changed={changed} ok={saladApiService?.LastCallOk} pulses={snap.Count} delay={d.TotalSeconds:F0}s\n");
				}
				catch { }
		#endif

				await Task.Delay(d);
			}
		}

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.CursorVisible = false;

			// LOAD CUSTOM FONTS
			var asm = typeof(Program).Assembly;
			foreach (var res in asm.GetManifestResourceNames()
						 .Where(n => n.EndsWith(".flf", StringComparison.OrdinalIgnoreCase)))
			{
				try
				{
					using var stream = asm.GetManifestResourceStream(res);
					using var ms = new System.IO.MemoryStream();
					stream.CopyTo(ms);
					ms.Position = 0;
					Fonts[System.IO.Path.GetFileNameWithoutExtension(res)] = FigletFont.Load(ms);
				}
				catch { }
			}
			
            string logsFolder = SaladLogDirectory + System.IO.Path.DirectorySeparatorChar;

            RestoreInitialState(logsFolder);
            UpdateSaladInfo();
            DetectWindowsVersion();
            InitializeCpuTelemetry();
            _ = Task.Run(ApiPollLoopAsync);

            while (true)
            {
                string logFile = GetMostRecentLogFile(logsFolder);
                if (logFile == null) logFile = $"log-{DateTime.Now:yyyyMMdd}.txt (Not Found)";

                Console.Clear();
                Console.SetCursorPosition(0, 0);

                // Um único Live para a vida toda do app - nunca recriado
                await AnsiConsole.Live(GetCurrentScreen(logFile))
                    .Cropping(VerticalOverflowCropping.Bottom)
                    .StartAsync(async ctx =>
                    {
                        int loopCounter = 0;
                        long lastMemTick = 0;

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
									VmTelemetryService.UpdateNetwork(isPullingState, ref totalPullingMB);
									UpdateSaladInfo();
									_ = FetchGpuDemandDataAsync();
								}

                                if (loopCounter % 2 == 1)
                                {
                                    UpdateHostHardware();
                                }

								if (loopCounter % 10 == 0)
								{
									VmTelemetryService.UpdateWSLData();
									VmTelemetryService.UpdateVhdxDiskUsage();
									UpdateSgsInfo();
						//			UpdateSgsCpuRam();
								}

                                CalculateUptime();
                                if (DetectConsoleResize())
                                {
                                    //Console.Clear();
                                    //Console.SetCursorPosition(0, 0);
                                }

                                ctx.UpdateTarget(GetCurrentScreen(logFile));
                            }
                            catch
                            {
                                // Ignora falhas pontuais (WMI travando, rede caindo, log rotacionando, etc.)
                                // sem nunca destruir o Live ou limpar a tela.
                            }

                            if (Environment.TickCount64 - lastMemTick >= 10000)
                            {
                                lastMemTick = Environment.TickCount64;
                                Console.Title = MemInfo();
                            }

                            loopCounter++;

                            // Check keyboard input
                            for (int i = 0; i < 20; i++)
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
                                            ctx.UpdateTarget(GetCurrentScreen(logFile));
                                        }
                                        else if (key.Key == ConsoleKey.N)
                                        {
                                            useNovaDemandView = !useNovaDemandView;
                                            ctx.UpdateTarget(GetCurrentScreen(logFile));
                                        }
                                        else if (key.Key == ConsoleKey.E)
                                        {
                                            showErrorHistory = !showErrorHistory;
                                            ctx.UpdateTarget(GetCurrentScreen(logFile));
                                        }
                                        else if (key.Key == ConsoleKey.W)
                                        {
                                            WalletScreenIndex = (WalletScreenIndex + 1) % 5;
                                            ctx.UpdateTarget(GetCurrentScreen(logFile));
                                        }
                                        else if (key.Key == ConsoleKey.C)
                                        {
                                            errorHistory.Clear();
                                            lastErrorClearTime = DateTime.Now;
                                            ctx.UpdateTarget(GetCurrentScreen(logFile));
                                        }
                                    }
                                }
								catch { /* ignora erro pontual de leitura de teclado */ }

								await Task.Delay(50);

								// re-renderiza rápido só enquanto o split-flap anima
								if (GraphBuilder.FlapActive)
								{
									GraphBuilder.FlapActive = false;
									try { ctx.UpdateTarget(GetCurrentScreen(logFile)); } catch { }
								}
							}
                        }
                    });
            }
        }
    }
}
