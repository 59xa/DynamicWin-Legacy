using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Xml;
using CsvHelper;
using DynamicWin.Resources;
using DynamicWin.UI.Widgets.Big;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;

/*
*   Overview:
*    - Implement new Weather API that allows the user to change their location and display its weather.
*    - Allows user to easily access a list of countries and cities in a comma-separated value format.
*    - Handles both IP address geo-location and user-configured weather forecast.
*    
*   Author:                 59xa
*   GitHub:                 https://github.com/59xa
*   Implementation Date:    16 May 2025
*   Last Modified:          20 November 2025
*   
*   TO MAINTAINERS:
*    - When fetching weather data, the API might hallucinate, and retrieve forecast data from a different city.
*    - This only occurs as the task doesn't get killed gracefully when doing a hot reload.
*    - This behaviour will not occur when it's fully compiled for end-user.
*/

namespace DynamicWin.Utils
{
    // Initialise weather API class
    public class WeatherAPI
    {
        // Reuse a single HttpClient for the app lifetime (recommended)
        private static readonly HttpClient s_httpClient = new HttpClient();

        // Cache CSV parsing to avoid reopening/parsing the file repeatedly
        private static Task<List<Country>>? s_cachedCsvTask;
        private static Task<List<Country>> GetCachedCsvAsync()
        {
            if (s_cachedCsvTask == null)
            {
                s_cachedCsvTask = LoadCsvAsync();
            }
            return s_cachedCsvTask;
        }

        // Initialise WeatherData struct
        private WeatherData _WeatherData = new WeatherData();
        public WeatherData _Weather { get => _WeatherData; }

        public Action<WeatherData>? _OnWeatherDataReceived;

        /// <summary>
        /// Handles retrieval of forecast values from a specified city set by user.
        /// </summary>
        /// <param name="idx">The index that the function should use.</param>
        /// <param name="type">Optional parameter that defines whether index value is "default" or "city"</param>
        /// <returns>void</returns>
        public async Task Fetch(int idx, string? type, CancellationToken token = default, CancellationTokenSource? cts = null)
        {
            // Load required values once; CSV parsing is cached now.
            string[] _c = (await LoadCountryNamesAsync()).ToArray();
            string[] _ct = (await LoadCityNamesAsync(RegisterWeatherWidgetSettings.saveData.countryIndex));

            // Use shared HttpClient
            // Loop fetch: cancellation-aware
            while (!token.IsCancellationRequested && RegisterWeatherWidgetSettings.saveData.isSettingsMenuOpen == false)
            {
                if (token.IsCancellationRequested || RegisterWeatherWidgetSettings.saveData.isSettingsMenuOpen)
                {
                    Debug.WriteLine("[WEATHER API] Task disposal received inside while-loop.");
                    throw new OperationCanceledException(token);
                }
                else if (cts != null && cts.IsCancellationRequested)
                {
                    Debug.WriteLine("[WEATHER API] Task disposal received inside while-loop.");
                    cts.Cancel();
                    throw new OperationCanceledException(token);
                }

                string response = string.Empty;
                var lat = string.Empty; var lon = string.Empty;
                Location location = default;

                // If index is Default, fetch geo-location forecast instead
                if (_c[RegisterWeatherWidgetSettings.saveData.countryIndex] == "Default" && type == "default")
                {
#if DEBUG
                    Debug.WriteLine("[WEATHER API] Forecast data request is default.");
#endif
                    response = await s_httpClient.GetStringAsync("https://ipinfo.io/geo").ConfigureAwait(false);
                    location = JsonConvert.DeserializeObject<Location>(response);

                    if (location.Equals(default(Location))) // Fallback if deserialization fails
                        location = new Location();

                    var locParts = (location.loc ?? "0,0").Split(',');
                    lat = locParts.Length > 0 ? locParts[0] : "0";
                    lon = locParts.Length > 1 ? locParts[1] : "0";
                }
                else // Read preference set by user, then return requested values
                {
#if DEBUG
                    Debug.WriteLine("[WEATHER API] Forecast data request is user-defined.");
#endif
                    string city = (_ct.Length > RegisterWeatherWidgetSettings.saveData.cityIndex && RegisterWeatherWidgetSettings.saveData.cityIndex >= 0) ? _ct[RegisterWeatherWidgetSettings.saveData.cityIndex] : string.Empty;
                    string country = _c[RegisterWeatherWidgetSettings.saveData.countryIndex];

                    var loc = LoadLatLong(RegisterWeatherWidgetSettings.saveData.countryIndex, RegisterWeatherWidgetSettings.saveData.cityIndex);
                    var locParts = (loc ?? "0,0").Split(',');
                    lat = locParts.Length > 0 ? locParts[0] : "0";
                    lon = locParts.Length > 1 ? locParts[1] : "0";

                    location = new Location { city = city, region = country, loc = loc };
                }

                string _t = null;
                string _w = null;

                // Read XML from weather service using HttpClient stream + XmlReader (async) to avoid blocking
                string uri = string.Format("https://tile-service.weather.microsoft.com/livetile/front/{0},{1}", lat, lon);
#if DEBUG
                Debug.WriteLine(uri);
#endif
                try
                {
                    using var stream = await s_httpClient.GetStreamAsync(uri).ConfigureAwait(false);
                    var settings = new XmlReaderSettings { IgnoreWhitespace = true, Async = true };
                    using var reader = XmlReader.Create(stream, settings);
                    int _n = 0;
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        if (reader.NodeType == XmlNodeType.Text)
                        {
                            if (_n == 1) _t = reader.Value;
                            else if (_n == 2) _w = reader.Value;
                            _n++;
                        }
                    }
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine("[WEATHER API] Error fetching/parsing weather XML: " + ex);
#endif
                    _t = "0";
                    _w = string.Empty;
                }

                // Ensure _t is not null or empty
                string _fahrRaw = string.IsNullOrWhiteSpace(_t) ? "0" : _t.Replace("°", "");

                // Try parsing the Fahrenheit temperature
                if (!double.TryParse(_fahrRaw, out double fahrValue))
                    fahrValue = 0; // Fallback if parsing fails

                // Convert to Celsius
                double celcValue = (fahrValue - 32.0) * 5.0 / 9.0;

                // Format the output with 1 decimal place
                string _fahrText = fahrValue.ToString("0.#");
                string _celcText = celcValue.ToString("0.#");

#if DEBUG
                Debug.WriteLine(string.Format("[WEATHER API] {0}, {1}F({2}°C), {3}", location.city, _t, _celcText, _w));
#endif

                _WeatherData = new WeatherData() { city = location.city, region = location.region, celsius = _celcText + "°C", fahrenheit = _fahrText + "F", weatherText = _w };
                _OnWeatherDataReceived?.Invoke(_WeatherData);

#if DEBUG
                Debug.WriteLine("[WEATHER API] IDX = {0}, TYPE = {1}", idx, type);
#endif

                // Wait for 2 minutes or until cancelled
                try
                {
                    await Task.Delay(120000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }

            if (token.IsCancellationRequested || RegisterWeatherWidgetSettings.saveData.isSettingsMenuOpen)
            {
                Debug.WriteLine("[WEATHER API] Task disposal received outside while-loop.");
                throw new OperationCanceledException(token);
            }
        }

        // Initialise Country constructor
        public class Country
        {
            public string country { get; set; }
            public string city { get; set; }
            public double lat { get; set; }
            public double lng { get; set; }
            public string population { get; set; }
        }

        // Logic to load provided comma-separated value file
        static async Task<List<Country>> LoadCsvAsync()
        {
            var defaultVal = new Country { country = "Default" };
            using var stream = new FileStream(Res.WeatherLocations, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream);

            string csvText = await reader.ReadToEndAsync().ConfigureAwait(false);

            using var stringReader = new StringReader(csvText);
            using var csv = new CsvReader(stringReader, System.Globalization.CultureInfo.InvariantCulture);

            List<Country> records = csv.GetRecords<Country>()
                .Where(r => !string.IsNullOrWhiteSpace(r.country)) // Safety check
                .OrderBy(r => r.country)
                .ToList();

            records.Insert(0, defaultVal); // Add "Default" at the start
            return records;
        }

        /// <summary>
        /// Retrieves a list of countries from the given comma-separated value file.
        /// </summary>
        /// <returns>A list of country names.</returns>
        public static async Task<string[]> LoadCountryNamesAsync()
        {
            var countries = await GetCachedCsvAsync().ConfigureAwait(false);
            var countryNames = countries
                .Select(c => c.country)
                .Distinct()
                .ToArray(); // Ensure no duplicates when returning list data

            return countryNames;
        }

        /// <summary>
        /// Retrieves a list of cities from a country inside the comma-separated value file.
        /// </summary>
        /// <param name="idx">The index value of a specific country.</param>
        /// <returns>A list of city names for a specific country.</returns>
        public static async Task<string[]> LoadCityNamesAsync(int idx)
        {
            var countries = await GetCachedCsvAsync().ConfigureAwait(false);
            var countryNames = countries.Select(c => c.country).Distinct().ToArray();

            var cities = countries
                .Where(c =>
                {
                    if (c.country != countryNames[idx])
                        return false;

                    // Handle empty or malformed population
                    if (string.IsNullOrWhiteSpace(c.population))
                        return false;

                    if (double.TryParse(c.population, out double pop))
                        return pop > 100000;

                    return false;
                })
                .Select(c => c.city)
                .Distinct()
                .Order()
                .ToArray();

            RegisterWeatherWidgetSettings.saveData.totalCities = Math.Max(0, cities.Length - 1);

            return cities;
        }

        /// <summary>
        /// Retrieves the latitude and longitude values of a city's location in an asynchronous manner.
        /// </summary>
        /// <param name="idx">The index value of a specific country.</param>
        /// <param name="idx2">The index value of a specific city.</param>
        /// <returns>A string that contains both the latitude and longitude value.</returns>
        public static async Task<string> LoadLatLongAsync(int idx, int idx2)
        {
            var countries = await GetCachedCsvAsync().ConfigureAwait(false);
            var countryNames = countries.Select(c => c.country).Distinct().ToArray();
            var selectedCountry = countryNames[idx];

            var cities = countries
                .Where(c => c.country == selectedCountry)
                .OrderBy(c => c.city)
                .ToArray();

            if (RegisterWeatherWidgetSettings.saveData.cityIndex < 0 || RegisterWeatherWidgetSettings.saveData.cityIndex >= cities.Length)
                return string.Empty;

            var city = cities[idx2];
            return $"{city.lat},{city.lng}";
        }

        /// <summary>
        /// Retrieves the latitude and longitude values of a city's location in a synchronous manner.
        /// </summary>
        /// <param name="idx">The index value of a specific country.</param>
        /// <param name="idx2">The index value of a specific city.</param>
        /// <returns>A string that contains both the latitude and longitude value.</returns>
        public static string LoadLatLong(int idx, int idx2)
        {
            return LoadLatLongAsync(idx, idx2).GetAwaiter().GetResult();
        }
    }

    // Initialise Location structure
    struct Location
    {
        public string city;
        public string region;
        public string country;
        public string loc;
    }

    // Initialise WeatherData structure
    public struct WeatherData
    {
        public string city;
        public string region;
        public string weatherText;
        public string celsius;
        public string fahrenheit;
    }
}
