using System;
using System.Collections.Generic;

namespace SaladXRayPanel.Models
{
    public class XRayData
    {
        public DateTime AppStartTime { get; set; } = DateTime.Now;
        public int PulseElapsedSeconds { get; set; } = 0;
		public List<(DateTime Timestamp, double Value)> Last5MinPulseBuffer { get; set; } = new();
        public List<(DateTime Timestamp, double Value)> EarningHistoryPoints { get; set; } = new();
		
		public double CurrentBalance { get; set; } = 0.0;
		public double ProjectedDailyRate { get; set; } = 0.0;
		
        // Teto/piso de ganho por hora, vindo das duas APIs (oficial Salad + paralela Novatech)
        public double OfficialAvgRateHour { get; set; } = 0.0;
        public double OfficialMaxRateHour { get; set; } = 0.0;
        public double NovaMinRateHour { get; set; } = 0.0;
        public double NovaMaxRateHour { get; set; } = 0.0;
        public bool HasOfficialRate { get; set; } = false;
        public bool HasNovaRate { get; set; } = false;		
    }
}
