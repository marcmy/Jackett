using System;
using System.Linq;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Jackett.Common.Indexers.Definitions;
using Jackett.Common.Models;
using Jackett.Common.Utils.Clients;
using Jackett.Test.TestHelpers;
using NLog;
using NUnit.Framework;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using WebRequest = Jackett.Common.Utils.Clients.WebRequest;

// todo: add all fields from the search block (poster, imdbid, ...)
// todo: add definition with post
// todo: test download block
// todo: test login block
// todo: test settings block
// todo: test other search modes
// todo: review coverage, too many things missing (headers, encoding, ...)
namespace Jackett.Test.Common.Indexers
{
    [TestFixture]
    public class CardigannIndexerHtmlTests
    {
        private readonly TestWebClient _webClient = new TestWebClient();
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly TestCacheService _cacheService = new TestCacheService();

        [Test]
        public async Task TestCardigannHtmlAsync()
        {
            _webClient.RegisterRequestCallback("https://www.testdefinition1.cc/search?query=ubuntu&sort=created", "html-response1.html");
            var definition = LoadTestDefinition("html-definition1.yml");
            var indexer = new CardigannIndexer(null, _webClient, _logger, null, _cacheService, definition);

            var query = new TorznabQuery
            {
                QueryType = "search",
                SearchTerm = "ubuntu",
            };

            var result = await indexer.ResultsForQuery(query, false);
            Assert.AreEqual(false, result.IsFromCache);

            var releases = result.Releases.ToList();
            Assert.AreEqual(25, releases.Count);

            var firstRelease = releases.First();
            Assert.AreEqual(1, firstRelease.Category.Count);
            Assert.AreEqual(8000, firstRelease.Category.First());
            Assert.AreEqual("ubuntu-19.04-desktop-amd64.iso", firstRelease.Title);
            Assert.AreEqual("https://www.testdefinition1.cc/torrent/d540fc48eb12f2833163eed6421d449dd8f1ce1f", firstRelease.Details.ToString());
            Assert.AreEqual("http://itorrents.org/torrent/d540fc48eb12f2833163eed6421d449dd8f1ce1f.torrent", firstRelease.Link.ToString());
            Assert.AreEqual("http://itorrents.org/torrent/d540fc48eb12f2833163eed6421d449dd8f1ce1f.torrent", firstRelease.Guid.ToString());
            Assert.AreEqual("magnet:?xt=urn:btih:d540fc48eb12f2833163eed6421d449dd8f1ce1f&dn=ubuntu-19.04-desktop-amd64.iso",
                             firstRelease.MagnetUri.ToString().Split(new[] { "&tr" }, StringSplitOptions.None).First());
            Assert.AreEqual("d540fc48eb12f2833163eed6421d449dd8f1ce1f", firstRelease.InfoHash);
            Assert.AreEqual(2024, firstRelease.PublishDate.Year);
            Assert.AreEqual(2097152000, firstRelease.Size);
            Assert.AreEqual(12, firstRelease.Seeders);
            Assert.AreEqual(13, firstRelease.Peers);
            Assert.AreEqual(1, firstRelease.DownloadVolumeFactor);
            Assert.AreEqual(2, firstRelease.UploadVolumeFactor);
            Assert.AreEqual(23.4375, firstRelease.Gain);
        }

        [TestCase("happyfappy.yml", false)]
        [TestCase("happyfappy.yml", true)]
        [TestCase("happyfappy2fa.yml", false)]
        [TestCase("happyfappy2fa.yml", true)]
        public async Task HappyFappyRowsSurviveMissingTagsAndRecognizeCurrentVerifiedIcon(string fileName, bool hasTags)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
            var definition = deserializer.Deserialize<IndexerDefinition>(
                File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Definitions", fileName)));
            var tags = hasTags ? "<div class='tags'>sample-tag</div>" : "";
            var html = @"<div id='nav_userinfo'></div><table id='torrent_table'><tbody>
                <tr class='torrent rowb'>
                  <td><a href='/torrents.php?filter_cat[1]=1'>category</a></td>
                  <td><i class='font_icon torrent_icons icon_torrent_okay'></i>
                    <a href='/torrents.php?action=download&amp;id=1'>download</a>
                    <a href='/torrents.php?id=1'>Sample release</a>" + tags + @"</td>
                  <td>1</td><td>0</td><td><span title='Oct 02 2026, 22:18'>date</span></td>
                  <td>537.35 MiB</td><td>105</td><td>38</td><td>0</td><td>uploader</td>
                </tr></tbody></table>";
            var indexer = new CardigannIndexer(null, new HtmlWebClient(html), _logger, null,
                new TestCacheService(), definition);

            var result = await indexer.ResultsForQuery(new TorznabQuery
            {
                QueryType = "search",
                SearchTerm = "sample"
            }, false);

            var releases = result.Releases.ToList();
            Assert.That(releases, Has.Count.EqualTo(1));
            Assert.That(releases[0].Title, Is.EqualTo("Sample release"));
            Assert.That(releases[0].Seeders, Is.EqualTo(38));
            Assert.That(releases[0].Description.Trim(), Is.EqualTo(hasTags ? "Verified: sample-tag" : "Verified:"));
        }

        private sealed class HtmlWebClient : TestWebClient
        {
            private readonly string _html;
            public HtmlWebClient(string html) => _html = html;
            public override Task<WebResult> GetResultAsync(WebRequest request) => Task.FromResult(new WebResult
            {
                Request = request,
                Status = HttpStatusCode.OK,
                ContentBytes = Encoding.UTF8.GetBytes(_html)
            });
        }

        private static IndexerDefinition LoadTestDefinition(string fileName)
        {
            var definitionString = TestUtil.LoadTestFile(fileName);
            var deserializer = new DeserializerBuilder()
                               .WithNamingConvention(CamelCaseNamingConvention.Instance)
                               .Build();
            return deserializer.Deserialize<IndexerDefinition>(definitionString);
        }
    }
}
