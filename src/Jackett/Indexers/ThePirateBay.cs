﻿using CsQuery;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Jackett.Indexers
{
    public class ThePirateBay : IndexerInterface
    {

        class ThePirateBayConfig : ConfigurationData
        {
            public StringItem Url { get; private set; }

            public ThePirateBayConfig()
            {
                Url = new StringItem { Name = "Url", Value = DefaultUrl };
            }

            public override Item[] GetItems()
            {
                return new Item[] { Url };
            }
        }

        public event Action<IndexerInterface, Newtonsoft.Json.Linq.JToken> OnSaveConfigurationRequested;

        public string DisplayName { get { return "The Pirate Bay"; } }

        public string DisplayDescription { get { return "The worlds largest bittorrent indexer"; } }

        public Uri SiteLink { get { return new Uri(DefaultUrl); } }

        public bool IsConfigured { get; private set; }

        const string DefaultUrl = "https://thepiratebay.se";
        const string SearchUrl = "/s/?q=\"{0}\"&category=205&page=0&orderby=99";
        const string SwitchSingleViewUrl = "/switchview.php?view=s";

        string BaseUrl;

        CookieContainer cookies;
        HttpClientHandler handler;
        HttpClient client;


        public ThePirateBay()
        {
            IsConfigured = false;
            cookies = new CookieContainer();
            handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                AllowAutoRedirect = true,
                UseCookies = true,
            };
            client = new HttpClient(handler);
        }

        public Task<ConfigurationData> GetConfigurationForSetup()
        {
            var config = new ThePirateBayConfig();
            return Task.FromResult<ConfigurationData>(config);
        }

        public async Task ApplyConfiguration(JToken configJson)
        {
            var config = new ThePirateBayConfig();
            config.LoadValuesFromJson(configJson);

            var uri = new Uri(config.Url.Value);
            var formattedUrl = string.Format("{0}://{1}", uri.Scheme, uri.Host);
            var releases = await PerformQuery(new TorznabQuery(), formattedUrl);
            if (releases.Length == 0)
                throw new Exception("Could not find releases from this URL");

            BaseUrl = formattedUrl;

            var configSaveData = new JObject();
            configSaveData["base_url"] = BaseUrl;

            if (OnSaveConfigurationRequested != null)
                OnSaveConfigurationRequested(this, configSaveData);

            IsConfigured = true;

        }

        public void LoadFromSavedConfiguration(JToken jsonConfig)
        {
            BaseUrl = (string)jsonConfig["base_url"];
            IsConfigured = true;
        }

        public async Task<ReleaseInfo[]> PerformQuery(TorznabQuery query)
        {
            return await PerformQuery(query, BaseUrl);
        }

        async Task<ReleaseInfo[]> PerformQuery(TorznabQuery query, string baseUrl)
        {
            List<ReleaseInfo> releases = new List<ReleaseInfo>();

            foreach (var title in query.ShowTitles ?? new string[] { string.Empty })
            {
                var searchString = title + " " + query.GetEpisodeSearchString();
                var episodeSearchUrl = baseUrl + string.Format(SearchUrl, HttpUtility.UrlEncode(searchString));

                var message = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri(baseUrl + SwitchSingleViewUrl)
                };
                message.Headers.Referrer = new Uri(episodeSearchUrl);

                string results;

                if (Program.IsWindows)
                {
                    var response = await client.SendAsync(message);
                    results = await response.Content.ReadAsStringAsync();
                }
                else
                {
                    var response = await CurlHelper.GetAsync(baseUrl + SwitchSingleViewUrl, null, episodeSearchUrl);
                    results = Encoding.UTF8.GetString(response.Content);
                }

                CQ dom = results;

                var rows = dom["#searchResult > tbody > tr"];
                foreach (var row in rows)
                {
                    var release = new ReleaseInfo();

                    CQ qLink = row.ChildElements.ElementAt(1).Cq().Children("a").First();

                    release.MinimumRatio = 1;
                    release.MinimumSeedTime = 172800;
                    release.Title = qLink.Text().Trim();
                    release.Description = release.Title;
                    release.Comments = new Uri(baseUrl + qLink.Attr("href").TrimStart('/'));
                    release.Guid = release.Comments;

                    var timeString = row.ChildElements.ElementAt(2).Cq().Text();
                    if (timeString.Contains("mins ago"))
                        release.PublishDate = (DateTime.Now - TimeSpan.FromMinutes(int.Parse(timeString.Split(' ')[0])));
                    else if (timeString.Contains("Today"))
                        release.PublishDate = (DateTime.UtcNow - TimeSpan.FromHours(2) - TimeSpan.Parse(timeString.Split(' ')[1])).ToLocalTime();
                    else if (timeString.Contains("Y-day"))
                        release.PublishDate = (DateTime.UtcNow - TimeSpan.FromHours(26) - TimeSpan.Parse(timeString.Split(' ')[1])).ToLocalTime();
                    else if (timeString.Contains(':'))
                    {
                        var utc = DateTime.ParseExact(timeString, "MM-dd HH:mm", CultureInfo.InvariantCulture) - TimeSpan.FromHours(2);
                        release.PublishDate = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
                    }
                    else
                    {
                        var utc = DateTime.ParseExact(timeString, "MM-dd yyyy", CultureInfo.InvariantCulture) - TimeSpan.FromHours(2);
                        release.PublishDate = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
                    }

                    var downloadCol = row.ChildElements.ElementAt(3).Cq().Find("a");
                    release.MagnetUri = new Uri(downloadCol.Attr("href"));
                    release.InfoHash = release.MagnetUri.ToString().Split(':')[3].Split('&')[0];

                    var sizeString = row.ChildElements.ElementAt(4).Cq().Text().Split(' ');
                    var sizeVal = float.Parse(sizeString[0]);
                    var sizeUnit = sizeString[1];
                    release.Size = ReleaseInfo.GetBytes(sizeUnit, sizeVal);

                    release.Seeders = int.Parse(row.ChildElements.ElementAt(5).Cq().Text());
                    release.Peers = int.Parse(row.ChildElements.ElementAt(6).Cq().Text()) + release.Seeders;

                    releases.Add(release);
                }
            }
            return releases.ToArray();

        }


        public Task<byte[]> Download(Uri link)
        {
            throw new NotImplementedException();
        }
    }
}
