using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Xml;
using CsvHelper;
using DynamicWin.Resources;
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
*   Last Modified:          02 January 2026
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
        // Shared default instance for global use
        public static readonly WeatherAPI Default = new WeatherAPI();

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

        // Internal loop control
        private CancellationTokenSource? _internalCts;
        private int _activeClients = 0;
        private readonly object _lock = new object();

        /// <summary>
        /// Start the background fetch loop. Multiple callers are supported via reference counting; the loop
        /// only runs while at least one caller has started it.
        /// </summary>
        /// <param name="countryIndex">Index of the country (used to build city list / lat-long lookup)</param>
        /// <param name="cityIndex">Index of the city (used for selecting a specific city when provided)</param>
        /// <param name="type">"default" for geo IP, "city" for user-selected</param>
        public void StartFetching(int countryIndex, int cityIndex, string? type = null)
        {
            lock (_lock)
            {
                _activeClients++;
                if (_activeClients == 1)
                {
                    _internalCts = new CancellationTokenSource();
                    // Start the loop without blocking the caller
                    _ = Task.Run(() => Fetch(countryIndex, cityIndex, type, _internalCts.Token, _internalCts));
#if DEBUG
                    Debug.WriteLine("[WEATHER API] StartFetching invoked; loop started.");
#endif
                }
                else
                {
#if DEBUG
                    Debug.WriteLine("[WEATHER API] StartFetching invoked; already running (refcount={0}).", _activeClients);
#endif
                }
            }
        }

        /// <summary>
        /// Stop the background fetch loop for the caller that previously started it. The loop will be cancelled
        /// only when the internal reference count reaches zero.
        /// </summary>
        public void StopFetching()
        {
            lock (_lock)
            {
                if (_activeClients > 0) _activeClients--;

                if (_activeClients <= 0)
                {
                    _internalCts?.Cancel();
                    _internalCts?.Dispose();
                    _internalCts = null;
                    _activeClients = 0;
#if DEBUG
                    Debug.WriteLine("[WEATHER API] StopFetching invoked; loop cancelled.");
#endif
                }
                else
                {
#if DEBUG
                    Debug.WriteLine("[WEATHER API] StopFetching invoked; remaining refcount={0}.", _activeClients);
#endif
                }
            }
        }

        /// <summary>
        /// Handles retrieval of forecast values from a specified city set by user.
        /// Internal use: prefer StartFetching/StopFetching for controlling the loop lifecycle.
        /// </summary>
        /// <param name="countryIndex">Country index (0 for Default)</param>
        /// <param name="cityIndex">City index (when selecting a city)</param>
        /// <param name="type">"default" for geo IP, "city" for user-selected</param>
        public async Task Fetch(int countryIndex, int cityIndex, string? type, CancellationToken token = default, CancellationTokenSource? cts = null)
        {
            // Load required values once; CSV parsing is cached now.
            string[] _c = (await LoadCountryNamesAsync()).ToArray();
            string[] _ct = Array.Empty<string>();
            if (countryIndex >= 0 && countryIndex < _c.Length)
            {
                _ct = await LoadCityNamesAsync(countryIndex);
            }

            // Use shared HttpClient
            // Loop fetch: cancellation-aware
            while (!token.IsCancellationRequested)
            {
                // If an explicit cancellation was requested via the provided CTS, respect it
                if (cts != null && cts.IsCancellationRequested)
                {
                    Debug.WriteLine("[WEATHER API] Task disposal requested via provided CTS.");
                    throw new OperationCanceledException(token);
                }

                string response = string.Empty;
                var lat = string.Empty; var lon = string.Empty;
                Location location = default;

                // If index is Default and requested type asks for default, fetch geo-location forecast instead
                if (_c.Length > 0 && countryIndex >= 0 && countryIndex < _c.Length && _c[countryIndex] == "Default" && type == "default")
                {
#if DEBUG
                    Debug.WriteLine("[WEATHER API] Forecast data request is default.");
#endif
                    try
                    {
                        response = await s_httpClient.GetStringAsync("https://ipinfo.io/geo").ConfigureAwait(false);
                        location = JsonConvert.DeserializeObject<Location>(response);
                    }
                    catch
                    {
                        location = new Location();
                    }

                    if (location.Equals(default(Location))) // Fallback if deserialization fails
                        location = new Location();

                    var locParts = (location.loc ?? "0,0").Split(',');
                    lat = locParts.Length > 0 ? locParts[0] : "0";
                    lon = locParts.Length > 1 ? locParts[1] : "0";
                }
                else // Read preference set by caller, then return requested values
                {
#if DEBUG
                    Debug.WriteLine("[WEATHER API] Forecast data request is user-defined.");
#endif
                    string city = ( _ct.Length > cityIndex && cityIndex >= 0) ? _ct[cityIndex] : string.Empty;
                    string country = (countryIndex >= 0 && countryIndex < _c.Length) ? _c[countryIndex] : string.Empty;

                    var loc = LoadLatLong(countryIndex, cityIndex);
                    var locParts = (loc ?? "0,0").Split(',');
                    lat = locParts.Length > 0 ? locParts[0] : "0";
                    lon = locParts.Length > 1 ? locParts[1] : "0";

                    location = new Location { city = city, region = country, loc = loc };

#if DEBUG
                    Debug.WriteLine("[WEATHER API] CITY = {0}, LATITUDE = {1}, LONGITUDE = {2}",
                        city, lat, lon);
#endif
                }

                // Use Open-Meteo instead of Microsoft's Tile Service Weather API as it's proven to be unreliable and inconsistent when fetching values
                string uri = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true&hourly=temperature_2m,relative_humidity_2m,wind_speed_10m";
#if DEBUG
                Debug.WriteLine(uri);
#endif

                OpenMeteoResponse? meteo = null;

                // Attempt to fetch values from defined API
                try
                {
                    string json = await s_httpClient.GetStringAsync(uri).ConfigureAwait(false);
                    meteo = JsonConvert.DeserializeObject<OpenMeteoResponse>(json);

#if DEBUG
                    // Display JSON contents on debug output for evaluation
                    Debug.WriteLine("========== OPEN-METEO PARAMETERS ==========");

                    if (meteo != null)
                    {
                        Debug.WriteLine($"Latitude: {meteo.latitude}");
                        Debug.WriteLine($"Longitude: {meteo.longitude}");
                        Debug.WriteLine($"Timezone: {meteo.timezone}");
                        Debug.WriteLine($"UTC Offset: {meteo.utc_offset_seconds}");
                        Debug.WriteLine($"Generation Time: {meteo.generationtime_ms} ms");

                        if (meteo.current_weather != null)
                        {
                            Debug.WriteLine("----- CURRENT WEATHER -----");
                            Debug.WriteLine($"Temp: {meteo.current_weather.temperature}°C");
                            Debug.WriteLine($"Wind: {meteo.current_weather.windspeed} km/h");
                            Debug.WriteLine($"Dir: {meteo.current_weather.winddirection}");
                            Debug.WriteLine($"WeatherCode: {meteo.current_weather.weathercode}");
                            Debug.WriteLine($"Time: {meteo.current_weather.time}");
                        }

                        if (meteo.hourly != null)
                        {
                            Debug.WriteLine("----- HOURLY WEATHER -----");
                            Debug.WriteLine($"Hours: {meteo.hourly.time?.Length ?? 0}");
                            Debug.WriteLine($"Temperature count: {meteo.hourly.temperature_2m?.Length ?? 0}");
                            Debug.WriteLine($"Humidity count: {meteo.hourly.relative_humidity_2m?.Length ?? 0}");
                            Debug.WriteLine($"Wind speed count: {meteo.hourly.wind_speed_10m?.Length ?? 0}");
                        }
                    }

                    Debug.WriteLine("===========================================");
#endif
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine("[OPEN-METEO] Error: " + ex);
#endif
                    meteo = null;
                }

                string finalCity = location.city;
                string finalRegion = location.region;
                string finalWeatherText = "Unknown";
                string celText = "0°C";
                string fahrText = "0F";

                // Format values accordingly for display if fetching is successful
                if (meteo?.current_weather != null)
                {
                    double cel = meteo.current_weather.temperature;
                    double fahr = cel * 9.0 / 5.0 + 32.0;

                    celText = cel.ToString("0.#") + "°C";
                    fahrText = fahr.ToString("0.#") + "F";

                    // Convert weather code to readable string
                    finalWeatherText = WeatherCodeToText(meteo.current_weather.weathercode);
                }

                // Send to widget
                _WeatherData = new WeatherData()
                {
                    city = finalCity,
                    region = finalRegion,
                    celsius = celText,
                    fahrenheit = fahrText,
                    weatherText = finalWeatherText
                };

                _OnWeatherDataReceived?.Invoke(_WeatherData);

#if DEBUG
                Debug.WriteLine("[WEATHER API] IDX = {0}, TYPE = {1}", countryIndex, type);
#endif

                // Wait for 2 minutes or until cancelled
                try
                {
                    int totalMs = 120000;
                    int waited = 0;
                    const int step = 1000; // check every second

                    while (waited < totalMs && !token.IsCancellationRequested)
                    {
                        int delay = Math.Min(step, totalMs - waited);
                        await Task.Delay(delay).ConfigureAwait(false);
                        waited += delay;
                    }

                    if (token.IsCancellationRequested) break;
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine("[WEATHER API] Delay loop exception: " + ex);
#endif
                    break;
                }
            }

            // Only throw if the provided cancellation token was signalled. Do not treat other UI flags as a cancellation.
            if (token.IsCancellationRequested)
            {
#if DEBUG
                Debug.WriteLine("[WEATHER API] Task disposal received outside while-loop.");
#endif
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

            var selectedCountry = countryNames[idx];

            var cities = countries
                .Where(c =>
                {
                    if (c.country != selectedCountry)
                        return false;

                    // Handle empty or malformed population
                    if (string.IsNullOrWhiteSpace(c.population))
                        return false;

                    // Special case for Sweden per user request
                    if (string.Equals(c.country, "Sweden", StringComparison.OrdinalIgnoreCase))
                    {
                        if (double.TryParse(c.population, out double pop))
                            return pop > 3000;
                        return false;
                    }

                    if (double.TryParse(c.population, out double popDefault))
                        return popDefault > 100000;

                    return false;
                })
                .Select(c => c.city)
                .Distinct()
                .Order()
                .ToArray();

            // Note: do not mutate external settings from here; caller should update totals if needed
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

            if (idx2 < 0 || idx2 >= cities.Length)
                return string.Empty;

            var city = cities[idx2];
#if DEBUG
            Debug.WriteLine("[WEATHER API] {0}, {1}, {2}, {3} + {4}", city.city, city.country, city.population, city.lat, city.lng);
#endif
            return $"{(int)city.lat},{(int)city.lng}";
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

        /// <summary>
        /// Converts a numeric weather code to its corresponding human-readable weather description.
        /// </summary>
        /// <remarks>This method is typically used to translate standardized weather codes from external
        /// data sources into descriptive text for display or logging purposes.</remarks>
        /// <param name="code">The weather condition code to convert. Valid codes correspond to specific weather types as defined by the
        /// data source.</param>
        /// <returns>A string containing the weather description that corresponds to the specified code. Returns "Unknown" if the
        /// code does not match a known weather type.</returns>
        public static string WeatherCodeToText(int code)
        {
            return code switch
            {
                0 => "Clear",
                1 => "Mainly Clear",
                2 => "Partly Cloudy",
                3 => "Cloudy",
                45 => "Foggy",
                48 => "Freezing Fog",
                51 => "Light Drizzle",
                53 => "Drizzle",
                55 => "Heavy Drizzle",
                56 => "Freezing Drizzle",
                57 => "Freezing Drizzle",
                61 => "Light Rain",
                63 => "Rain",
                65 => "Heavy Rain",
                66 => "Freezing Rain",
                67 => "Freezing Rain",
                71 => "Light Snow",
                73 => "Snow",
                75 => "Heavy Snow",
                77 => "Snow Grains",
                80 => "Light Rain Showers",
                81 => "Rain Showers",
                82 => "Heavy Rain Showers",
                85 => "Light Snow Showers",
                86 => "Snow Showers",
                95 => "Thunderstorm",
                96 => "Thunderstorm w/ Hail",
                99 => "Thunderstorm w/ Heavy Hail",
                _ => "Unknown"
            };
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
