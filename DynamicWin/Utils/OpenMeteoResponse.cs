using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DynamicWin.Utils
{
    internal class OpenMeteoResponse
    {
        public double latitude { get; set; }
        public double longitude { get; set; }
        public double generationtime_ms { get; set; }
        public int utc_offset_seconds { get; set; }
        public string timezone { get; set; }
        public string timezone_abbreviation { get; set; }
        public double elevation { get; set; }
        public CurrentWeather? current_weather { get; set; }
        public HourlyData? hourly { get; set; }
    }

    internal class CurrentWeather
    {
        public double temperature { get; set; }
        public double windspeed { get; set; }
        public double winddirection { get; set; }
        public int weathercode { get; set; }
        public string time { get; set; }
    }

    internal class HourlyData
    {
        public string[]? time { get; set; }
        public double[]? temperature_2m { get; set; }
        public double[]? relative_humidity_2m { get; set; }
        public double[]? wind_speed_10m { get; set; }
    }
}
