using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;

namespace SpotifyIsland
{
    public sealed class DayForecast
    {
        public DateTime Date;
        public double Min, Max;
        public int RainChance;
        public double RainMm;
        public string Code;
        public string Description;
    }

    public sealed class WeatherData
    {
        public string StationName;
        public double DistanceKm;
        public double Temperature;
        public double? FeelTemperature;
        public double? Humidity;
        public double? WindKmh;
        public int? WindBft;
        public string WindDirection;
        public string Description;
        public string IconCode;
        public bool IsNight;
        public List<DayForecast> Days = new List<DayForecast>();
        public DateTime Updated;
    }

    /// <summary>
    /// Talks to Buienradar (free, non-commercial use, attribution required):
    ///  - https://data.buienradar.nl/2.0/feed/json          current weather stations + 5-day forecast
    ///  - https://gpsgadget.buienradar.nl/data/raintext     rain forecast for the next 2 hours at an exact location
    /// </summary>
    public sealed class WeatherService
    {
        private const string FeedUrl = "https://data.buienradar.nl/2.0/feed/json";
        private static readonly HttpClient Http = CreateClient();

        private readonly double _manualLat, _manualLon;
        private double _lat = double.NaN, _lon = double.NaN;

        public WeatherData Data { get; private set; }
        public double[] RainMm { get; private set; } = Array.Empty<double>();
        public string RainText { get; private set; }
        public bool RainComing { get; private set; }

        public bool HasLocation => !double.IsNaN(_lat) && !double.IsNaN(_lon);

        public WeatherService(double manualLat, double manualLon)
        {
            _manualLat = manualLat;
            _manualLon = manualLon;
        }

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyIsland/1.0 (personal desktop widget)");
            return c;
        }

        // ================= Location =================

        public async Task ResolveLocationAsync()
        {
            // 1) Manual override from the settings at the top of MainWindow.xaml.cs
            if (!double.IsNaN(_manualLat) && !double.IsNaN(_manualLon))
            {
                _lat = _manualLat;
                _lon = _manualLon;
                return;
            }

            // 2) Windows location service (Settings > Privacy > Location)
            try
            {
                var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
                var pos = await locator.GetGeopositionAsync(TimeSpan.FromHours(1), TimeSpan.FromSeconds(8));
                var p = pos.Coordinate.Point.Position;
                _lat = p.Latitude;
                _lon = p.Longitude;
                return;
            }
            catch { /* location off or denied: fall through */ }

            // 3) Approximate location from your IP address
            try
            {
                var json = await Http.GetStringAsync("https://ipwho.is/");
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return;
                var la = Num(r, "latitude");
                var lo = Num(r, "longitude");
                if (la.HasValue && lo.HasValue)
                {
                    _lat = la.Value;
                    _lon = lo.Value;
                }
            }
            catch { }
        }

        // ================= Current weather + 5-day forecast =================

        public async Task<bool> RefreshFeedAsync()
        {
            try
            {
                if (!HasLocation) await ResolveLocationAsync();
                if (!HasLocation) return false;

                var json = await Http.GetStringAsync(FeedUrl);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var data = new WeatherData();

                DateTime? sunrise = null, sunset = null;
                bool foundStation = false;

                if (root.TryGetProperty("actual", out var actual))
                {
                    sunrise = Time(actual, "sunrise");
                    sunset = Time(actual, "sunset");

                    if (actual.TryGetProperty("stationmeasurements", out var stations) &&
                        stations.ValueKind == JsonValueKind.Array)
                    {
                        double best = double.MaxValue;
                        JsonElement? bestEl = null;

                        foreach (var st in stations.EnumerateArray())
                        {
                            var la = Num(st, "lat");
                            var lo = Num(st, "lon");
                            var t = Num(st, "temperature");
                            if (!la.HasValue || !lo.HasValue || !t.HasValue) continue;

                            double dist = DistanceKm(_lat, _lon, la.Value, lo.Value);
                            if (dist < best)
                            {
                                best = dist;
                                bestEl = st;
                            }
                        }

                        if (bestEl.HasValue)
                        {
                            var s = bestEl.Value;
                            foundStation = true;
                            data.StationName = (Str(s, "stationname") ?? "").Replace("Meetstation ", "");
                            data.DistanceKm = best;
                            data.Temperature = Num(s, "temperature") ?? 0;
                            data.FeelTemperature = Num(s, "feeltemperature");
                            data.Humidity = Num(s, "humidity");

                            var ws = Num(s, "windspeed");
                            data.WindKmh = ws.HasValue ? ws.Value * 3.6 : (double?)null;
                            var bft = Num(s, "windspeedBft");
                            data.WindBft = bft.HasValue ? (int)bft.Value : (int?)null;
                            data.WindDirection = Str(s, "winddirection");

                            data.Description = Str(s, "weatherdescription");
                            data.IconCode = CodeFromUrl(Str(s, "iconurl"));
                        }
                    }
                }

                if (!foundStation) return false;

                var now = DateTime.Now;
                bool sunNight = sunrise.HasValue && sunset.HasValue && (now < sunrise.Value || now > sunset.Value);
                data.IsNight = IsDoubled(data.IconCode) || sunNight;

                if (root.TryGetProperty("forecast", out var fc) &&
                    fc.TryGetProperty("fivedayforecast", out var days) &&
                    days.ValueKind == JsonValueKind.Array)
                {
                    foreach (var day in days.EnumerateArray())
                    {
                        var date = Time(day, "day");
                        if (!date.HasValue) continue;

                        data.Days.Add(new DayForecast
                        {
                            Date = date.Value.Date,
                            Min = Num(day, "mintemperature") ?? Num(day, "mintemperatureMax") ?? 0,
                            Max = Num(day, "maxtemperature") ?? Num(day, "maxtemperatureMax") ?? 0,
                            RainChance = (int)(Num(day, "rainChance") ?? 0),
                            RainMm = Num(day, "mmRainMax") ?? Num(day, "rain") ?? 0,
                            Code = CodeFromUrl(Str(day, "iconurl")),
                            Description = Str(day, "weatherdescription")
                        });
                    }
                }

                data.Updated = DateTime.Now;
                Data = data;
                return true;
            }
            catch
            {
                return false; // keep showing the previous data
            }
        }

        // ================= Rain forecast for the next 2 hours =================

        public async Task<bool> RefreshRainAsync()
        {
            try
            {
                if (!HasLocation) await ResolveLocationAsync();
                if (!HasLocation) return false;

                // The radar only covers roughly the Benelux / north-west Europe.
                if (_lat < 50 || _lat > 54 || _lon < 1 || _lon > 9)
                {
                    RainMm = Array.Empty<double>();
                    RainText = null;
                    RainComing = false;
                    return false;
                }

                string url = string.Format(CultureInfo.InvariantCulture,
                    "https://gpsgadget.buienradar.nl/data/raintext?lat={0:0.00}&lon={1:0.00}", _lat, _lon);
                string text = await Http.GetStringAsync(url);

                var tokens = new List<string>();
                var raws = new List<double>();
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Trim().Split('|');   // "077|12:35"
                    if (parts.Length < 2) continue;
                    var tok = parts[0].Trim();
                    if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                    tokens.Add(tok);
                    raws.Add(v);
                }
                if (raws.Count == 0) return false;

                // Old format: 0-255 scale, mm/h = 10^((v-109)/32).  Newer responses may be plain mm/h with decimals.
                bool mmFormat = tokens.Any(t => t.Contains('.')) && raws.Max() <= 30;
                RainMm = raws.Select(v => mmFormat
                        ? Math.Max(0, v)
                        : (v <= 0 ? 0 : Math.Pow(10, (v - 109) / 32.0)))
                    .ToArray();

                BuildRainText();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void BuildRainText()
        {
            const double threshold = 0.1; // mm/h
            var r = RainMm;
            int n = r.Length;
            int first = Array.FindIndex(r, v => v >= threshold);
            RainComing = first >= 0;

            if (first < 0)
            {
                RainText = n >= 20 ? "Droog komende 2 uur" : $"Droog komende {n * 5} min";
                return;
            }

            if (first == 0)
            {
                int dry = -1;
                for (int i = 1; i < n; i++)
                {
                    if (r[i] < threshold) { dry = i; break; }
                }
                RainText = dry < 0 ? "Regen komende 2 uur" : $"Regen, droog over {Dur(dry * 5)}";
                return;
            }

            RainText = $"Regen over {Dur(first * 5)}";
        }

        private static string Dur(int minutes)
        {
            if (minutes < 60) return $"{minutes} min";
            return minutes % 60 == 0 ? $"{minutes / 60} uur" : $"{minutes / 60}u{minutes % 60:00}";
        }

        // ================= Helpers =================

        private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371;
            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        private static double? Num(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
            return null;
        }

        private static string Str(JsonElement e, string name)
        {
            return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static DateTime? Time(JsonElement e, string name)
        {
            var s = Str(e, name);
            if (s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return dt;
            return null;
        }

        // ".../icons/weather/30x30/cc.png" -> "cc"  (a doubled letter means the night version)
        private static string CodeFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            int q = url.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) url = url.Substring(0, q);
            return System.IO.Path.GetFileNameWithoutExtension(url);
        }

        private static bool IsDoubled(string code)
        {
            return code != null && code.Length == 2 && code[0] == code[1];
        }
    }
}
