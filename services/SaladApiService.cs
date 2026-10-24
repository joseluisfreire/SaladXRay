using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;

namespace SaladXRayPanel.Services
{
    /// <summary>
    /// Guarda o estado em tempo real dos ganhos e do Handshake da máquina na API v2 da Salad.
    /// </summary>
    public class SaladApiState
    {
        public bool IsHandshakeOk { get; set; } = false;
        public string HandshakeMarkup { get; set; } = "[yellow]○ waiting...[/]";
        public double Last5MinEarning { get; set; } = 0.0;
        public double EstimatedHourlyRate { get; set; } = 0.0;
        public double ProjectedDailyRate { get; set; } = 0.0;
        public double LifetimeBalance { get; set; } = 0.0;
        public double Last24HourEarning { get; set; } = 0.0;
        public double LastHourEarning { get; set; } = 0.0;
        public double Last7DayEarning { get; set; } = 0.0;
        public double Last30DayEarning { get; set; } = 0.0;
        public List<(DateTime Timestamp, double Value)> EarningHistoryPoints { get; set; } = new();
        public double CurrentBalance { get; set; } = 0.0;
        public string DetectedMachineId { get; set; } = "Not Detected";
        public DateTime LastApiUpdate { get; set; } = DateTime.MinValue;
        public DateTime LastMachineUpdateTime { get; set; } = DateTime.MinValue;
        public bool IsApiOnline { get; set; } = false;
        public DateTime LastPulseTimestamp { get; set; } = DateTime.Now;
        public List<(DateTime Timestamp, double Value)> Last5MinPulseBuffer { get; set; } = new();
    }

    /// <summary>
    /// Serviço exclusivo para interagir com a API v2 do Salad (Ganhos, Máquinas e Handshake).
    /// </summary>
    public class SaladApiService
    {
        private readonly SaladApiState _state;
        private readonly HttpClient _httpClient;
        private static readonly string TokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Salad", "token.txt");
        private const string BaseUrl = "https://app-api.salad.com/api/v2";
		private const decimal MinPulse = 0.000001m;
        private decimal _lastLifetimeExact = -1m; // -1 = baseline pendente
        private string _cachedMachineId = null;
        private bool _isFetching = false;
        private string _localMachineId = null;
        private bool _machinesOk = false;
        private bool _pulseThisCycle = false;
        private DateTime _lastFull = DateTime.MinValue;
        private static readonly string SaladLogsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Salad", "logs");
        private static readonly System.Text.RegularExpressions.Regex MachineIdRegex = new(
            @"api/v2/machines/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
System.Text.RegularExpressions.RegexOptions.None);
		private DateTime _lastHistory = DateTime.MinValue;
		private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        public SaladApiService(SaladApiState state)
        {
            _state = state;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SaladXRayPanel-Handshake/2.0");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public SaladApiState State => _state;
        public bool LastCallOk { get; private set; } = true;
        public TimeSpan? RetryAfter { get; private set; }

        /// true = entrou pulso novo neste ciclo
        public async Task<bool> PollAsync()
        {
            _machinesOk = false;
            _pulseThisCycle = false;
            RetryAfter = null;
            await RefreshEarningsDataAsync();
            LastCallOk = _machinesOk;
            return _pulseThisCycle;
        }

public async Task RefreshEarningsDataAsync()
{
    if (_isFetching) return;
    _isFetching = true;

    try
    {
        if (!File.Exists(TokenPath))
        {
            LogMessage("ERRO: Arquivo de token não localizado em: " + TokenPath);
            _state.HandshakeMarkup = "[red]Offline (No Token)[/]";
            _state.IsApiOnline = false;
            return;
        }

        string token = (await File.ReadAllTextAsync(TokenPath)).Trim();
        if (string.IsNullOrEmpty(token))
        {
            _state.HandshakeMarkup = "[red]Offline (Empty Token)[/]";
            _state.IsApiOnline = false;
            return;
        }

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // /machines -> update_time + lifetime_balance (pulso)
        await DetectPulseAsync();

        if (!_machinesOk) { _state.IsApiOnline = false; return; }

        bool stale = DateTime.Now - _lastFull > TimeSpan.FromMinutes(5);
        if (_pulseThisCycle || stale)
        {
            await FetchProfileBalanceAsync();   // só exibição (saldo da conta)
            _lastFull = DateTime.Now;
        }

        if (string.IsNullOrEmpty(_cachedMachineId))
        {
            _state.HandshakeMarkup = "[yellow]○ Machine Not Found[/]";
            _state.IsApiOnline = false;
        }
        else
        {
            if (_pulseThisCycle || DateTime.Now - _lastHistory > TimeSpan.FromSeconds(60))
            {
                await FetchEarningHistoryAsync();
                _lastHistory = DateTime.Now;
            }
            _state.IsApiOnline = true;
        }

        _state.LastApiUpdate = DateTime.Now;
    }
    catch (Exception ex)
    {
        LogMessage($"GLOBAL API ERROR: {ex.Message}");
        _state.IsApiOnline = false;
    }
    finally
    {
        _isFetching = false;
    }
}

        /// <summary>
        /// /machines dá update_time e lifetime_balance. O delta do lifetime (f6) é o pulso.
        /// O /earnings/5-minutes só alimenta card, handshake e taxa/hora.
        /// A 1h trailing vem do buffer de pulsos.
        /// </summary>
        private async Task DetectPulseAsync()
        {
            try
            {
                _localMachineId ??= ReadLocalMachineId();
                if (_localMachineId == null)
                {
                    LogMessage("ERRO: Machine ID não encontrado em Salad\\logs\\main.log");
                    return;
                }

                var response = await _httpClient.GetAsync($"{BaseUrl}/machines");
                if (!response.IsSuccessStatusCode)
                {
                    LogMessage($"FALHA /machines: HTTP {response.StatusCode}");
                    if ((int)response.StatusCode == 429)
                        RetryAfter = response.Headers.RetryAfter?.Delta
                            ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.Now : null);
                    return;
                }
                _machinesOk = true;

                string json = await response.Content.ReadAsStringAsync();
var data = JsonSerializer.Deserialize<SaladMachinesResponse>(json, JsonOpts);

                var machine = data?.Items?.FirstOrDefault(i =>
                    string.Equals(i.MachineId, _localMachineId, StringComparison.OrdinalIgnoreCase));

                if (machine == null)
                {
                    LogMessage($"ERRO: máquina {_localMachineId} não veio em /machines");
                    return;
                }

                _cachedMachineId = machine.MachineId;
                _state.DetectedMachineId = _cachedMachineId;

                // UTC -> local
                var ts = machine.UpdateTime;
                if (ts.Kind == DateTimeKind.Utc) ts = ts.ToLocalTime();
                else if (ts.Kind == DateTimeKind.Unspecified) ts = DateTime.SpecifyKind(ts, DateTimeKind.Utc).ToLocalTime();

                // Card/handshake/taxa: só quando o update_time andou
                if (ts > _state.LastMachineUpdateTime && await Read5MinAsync() is decimal w)
                    Apply5Min(w);

                // Pulso: delta do lifetime em f6
                decimal life = Math.Round(machine.LifetimeBalance, 6);
                if (life > 0m)
                {
                    if (_lastLifetimeExact < 0m || life < _lastLifetimeExact)
                    {
                        _lastLifetimeExact = life; // baseline (ou saldo resetou)
                        LogMessage($"BASELINE life={life:F6}");
                    }
                    else
                    {
                        decimal amount = life - _lastLifetimeExact;
                        if (amount >= MinPulse)
                        {
                            _lastLifetimeExact = life; // só avança quando é pulso
                            _pulseThisCycle = true;
                            LogMessage($"PULSO REAL: ts={ts:HH:mm:ss} amount={amount:F6}");

                            lock (_state.Last5MinPulseBuffer)
                            {
                                _state.Last5MinPulseBuffer.Add((ts, (double)amount));
                                _state.Last5MinPulseBuffer.RemoveAll(p => p.Timestamp < DateTime.Now.AddMinutes(-60));
                            }
                            _state.LastPulseTimestamp = DateTime.Now;
                        }
                    }
                }

                // 1h trailing: pulsos reais do buffer (todo poll)
                lock (_state.Last5MinPulseBuffer)
                {
                    var cutoff1h = DateTime.Now.AddHours(-1);
                    _state.LastHourEarning = Math.Round(
                        _state.Last5MinPulseBuffer.Where(p => p.Timestamp >= cutoff1h).Sum(p => p.Value), 6);
                }

                _state.LastMachineUpdateTime = ts;
            }
            catch (Exception ex)
            {
                LogMessage($"ERRO DetectPulse: {ex.Message}");
            }
        }

        private async Task<decimal?> Read5MinAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{BaseUrl}/machines/{_cachedMachineId}/earnings/5-minutes");
                if (!response.IsSuccessStatusCode)
                {
                    LogMessage($"FALHA /earnings/5-minutes: HTTP {response.StatusCode}");
                    return null;
                }

                string raw = (await response.Content.ReadAsStringAsync()).Trim().Trim('"');
                return decimal.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
            }
            catch (Exception ex)
            {
                LogMessage($"ERRO Read5Min: {ex.Message}");
                return null;
            }
        }

        private void Apply5Min(decimal v)
        {
            double d = (double)Math.Round(v, 6);
            _state.Last5MinEarning = d;

            if (d > 0)
            {
                _state.IsHandshakeOk = true;
                _state.HandshakeMarkup = "[bold green]● Connected (ON)[/]";
            }
            else
            {
                _state.IsHandshakeOk = false;
                _state.HandshakeMarkup = "[bold yellow]○ Pending[/]";
            }

            _state.EstimatedHourlyRate = d * 12;
            _state.ProjectedDailyRate = _state.EstimatedHourlyRate * 24;
        }

        private async Task FetchProfileBalanceAsync()
        {
            try
            {
                string url = "https://app-api.salad.com/api/v1/profile/balance";
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    LogMessage($"FALHA /profile/balance: HTTP {response.StatusCode}");
                    return;
                }

string json = await response.Content.ReadAsStringAsync();
var data = JsonSerializer.Deserialize<SaladBalanceResponse>(json, JsonOpts);

                if (data != null)
                {
                    _state.CurrentBalance = (double)Math.Round(data.CurrentBalance, 6);
                    _state.LifetimeBalance = (double)Math.Round(data.LifetimeBalance, 6);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"ERRO FetchProfileBalance: {ex.Message}");
            }
        }

        /// <summary>
		/// Busca earning-history (30d, buckets horários), calcula as somas 24h/7d/30d
		/// e guarda os buckets em EarningHistoryPoints para o gráfico.
        /// </summary>
        private async Task FetchEarningHistoryAsync()
        {
            try
            {
                string url = $"{BaseUrl}/machines/{_cachedMachineId}/earning-history?timeframe=30d";
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    LogMessage($"FALHA /earning-history: HTTP {response.StatusCode}");
                    return;
                }

                string json = await response.Content.ReadAsStringAsync();
var wrapper = JsonSerializer.Deserialize<SaladMachineEarningHistoryResponse>(json, JsonOpts);
                var data = wrapper?.Earnings;

                if (data == null || data.Count == 0) return;

				DateTime now = DateTime.UtcNow;
				var points = new List<(DateTime Timestamp, double Value)>();

				foreach (var kvp in data)
				{
					if (!DateTime.TryParse(kvp.Key, null,
							System.Globalization.DateTimeStyles.AdjustToUniversal |
							System.Globalization.DateTimeStyles.AssumeUniversal,
							out DateTime ts))
						continue;

					points.Add((ts, Math.Round(kvp.Value, 6)));
				}

				var ordered = points.OrderBy(p => p.Timestamp).ToList();

				// Soma os N buckets horários que terminam na hora atual (inclui o bucket de hourStart)
				double SumRolling(int n)
				{
					var from = now.AddHours(-n);
					var edge = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc);
					double keep = 1 - (from - edge).TotalMinutes / 60;
					double s = 0;
					foreach (var p in ordered)
					{
						if (p.Timestamp > edge && p.Timestamp <= now) s += p.Value;
						else if (p.Timestamp == edge) s += p.Value * keep;
					}
					return s;
				}

				double sum24h = SumRolling(24);
				double sum7d  = SumRolling(168);
				double sum30d = SumRolling(720);

				LogMessage($"SUMS 24h={sum24h:F6} 7d={sum7d:F6} 30d={sum30d:F6}");

                _state.Last24HourEarning = sum24h;
                _state.Last7DayEarning = sum7d;
                _state.Last30DayEarning = sum30d;
                _state.EarningHistoryPoints = points.OrderBy(p => p.Timestamp).ToList();

            }
            catch (Exception ex)
            {
            }
        }

        private string ReadLocalMachineId()
        {
            foreach (var name in new[] { "main.log", "main.old.log" })
            {
                try
                {
                    var path = Path.Combine(SaladLogsDir, name);
                    if (!File.Exists(path)) continue;

                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete);
                    long len = Math.Min(fs.Length, 1_000_000);
                    fs.Seek(-len, SeekOrigin.End);
                    using var sr = new StreamReader(fs);

                    var matches = MachineIdRegex.Matches(sr.ReadToEnd());
                    if (matches.Count > 0)
                        return matches[matches.Count - 1].Groups[1].Value;
                }
                catch (Exception ex) { LogMessage($"ERRO ReadLocalMachineId({name}): {ex.Message}"); }
            }
            return null;
        }
		
		[Conditional("DEBUG")]
        private void LogMessage(string message)
        {
            try
            {
                string logPath = @"C:\temp\salad_api.log";
                string dir = Path.GetDirectoryName(logPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch { }
        }
    }

    // ==========================================
    // CLASSES DTO PARA DESERIALIZAÇÃO JSON
    // ==========================================
    public class SaladMachineEarningHistoryResponse
    {
        [JsonPropertyName("machine_id")]
        public string MachineId { get; set; }

        [JsonPropertyName("earnings")]
        public Dictionary<string, double> Earnings { get; set; }
    }

    public class SaladMachinesResponse
    {
        [JsonPropertyName("items")]
        public List<SaladMachineItem> Items { get; set; }
    }

    public class SaladBalanceResponse
    {
        [JsonPropertyName("currentBalance")]
        public decimal CurrentBalance { get; set; }

        [JsonPropertyName("lifetimeBalance")]
        public decimal LifetimeBalance { get; set; }
    }

    public class SaladMachineItem
    {
        [JsonPropertyName("machine_id")]
        public string MachineId { get; set; }

        [JsonPropertyName("lifetime_balance")]
        public decimal LifetimeBalance { get; set; }

        [JsonPropertyName("update_time")]
        public DateTime UpdateTime { get; set; }
    }
}
