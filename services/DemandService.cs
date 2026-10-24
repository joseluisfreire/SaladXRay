#nullable disable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SaladXRayPanel
{
    public class DemandService
    {
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
        private readonly HttpClient _httpClient;

        public DemandService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public static string NormalizeGpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            return WhitespaceRegex.Replace(name.Trim(), " ").ToUpperInvariant();
        }

        public static GpuDemandData FindMatchingGpu(string localGpuName, IEnumerable<GpuDemandData> apiItems)
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

        public static NovatechGpuData FindNovatechMatch(string localGpuName, IEnumerable<NovatechGpuData> novaItems)
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

        public async Task<NovatechGpuData> FetchNovatechAsync(string localGpuName)
        {
            try
            {
                string novaJson = await _httpClient.GetStringAsync("https://salad-tools.novatech.gg/api/gpus");
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var response = JsonSerializer.Deserialize<NovatechResponse>(novaJson, options);

                if (response?.Gpus != null)
                    return FindNovatechMatch(localGpuName, response.Gpus);
            }
            catch { }

            return null;
        }

        public async Task<GpuDemandData> FetchSaladGpuAsync(string canonicalName)
        {
            try
            {
                string json = await _httpClient.GetStringAsync("https://app-api.salad.com/api/v2/demand-monitor/gpu");
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var gpus = JsonSerializer.Deserialize<List<GpuDemandData>>(json, options);

                return FindMatchingGpu(canonicalName, gpus ?? new List<GpuDemandData>());
            }
            catch { }

            return null;
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
