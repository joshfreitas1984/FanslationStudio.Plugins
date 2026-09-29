using System.Net;
using FanslationStudio.Plugins.Update;

namespace FanslationStudio.Plugins.Tests
{
    public class UpdateTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "update-tests-" + Guid.NewGuid().ToString("N"));

        private const string Manifest = """
            {
              "schemaVersion": 1,
              "version": "2026.09.28.16.01",
              "gitHubRepo": "owner/repo",
              "steamAppId": 3202030,
              "patchZipPrefix": "EnglishPatch",
              "files": [ { "path": "BepInEx/a.yaml", "sha256": "abc", "size": 1 } ],
              "seedOnly": []
            }
            """;

        [Theory]
        [InlineData("2026.09.28.16.01", "2026.09.29.18.07", true)]
        [InlineData("2026.9.9.1.1", "2026.09.10.0.0", true)]
        [InlineData("2026.09.29.18.07", "2026.09.29.18.07", false)]
        [InlineData("2026.09.29.18.07", "2026.09.28.16.01", false)]
        [InlineData("2026.09.29.18.07", "v2026.09.29.18.08", true)]
        [InlineData("", "2026.09.29.18.07", true)]
        [InlineData("manual", "2026.09.29.18.07", true)]
        public void IsNewer_ComparesDottedVersionsNumerically(string installed, string latest, bool expected)
        {
            Assert.Equal(expected, ReleaseVersion.IsNewer(installed, latest));
        }

        [Fact]
        public void InstalledRelease_ReadsVersionAndUpdaterMetadataFromManifest()
        {
            var release = InstalledRelease.Parse(Manifest)!;

            Assert.Equal("2026.09.28.16.01", release.Version);
            Assert.Equal("owner/repo", release.GitHubRepo);
            Assert.Equal(3202030, release.SteamAppId);
            Assert.Equal("EnglishPatch", release.PatchZipPrefix);
        }

        [Fact]
        public void InstalledRelease_ToleratesOlderManifestsWithoutMetadata()
        {
            var release = InstalledRelease.Parse("""{ "schemaVersion": 1, "version": "1.2.3", "files": [] }""")!;

            Assert.Equal("1.2.3", release.Version);
            Assert.Null(release.GitHubRepo);
            Assert.Equal(0, release.SteamAppId);
            Assert.Null(InstalledRelease.Parse("{}"));
        }

        [Fact]
        public void InstalledRelease_Read_ReturnsNullWhenNoManifestIsInstalled()
        {
            Directory.CreateDirectory(_root);
            Assert.Null(InstalledRelease.Read(_root));

            File.WriteAllText(Path.Combine(_root, InstalledRelease.ManifestFileName), Manifest);
            Assert.Equal("owner/repo", InstalledRelease.Read(_root)!.GitHubRepo);
        }

        [Fact]
        public void Urls_UseTheReleaseTagAndTheStableInstallerRelease()
        {
            Assert.Equal("https://api.github.com/repos/o/r/releases/latest", UpdateService.BuildLatestReleaseApiUrl("o/r"));
            Assert.Equal("https://github.com/o/r/releases/download/2026.09.29.18.07/EnglishPatch-2026.09.29.18.07.zip",
                UpdateService.BuildPatchZipUrl("o/r", "EnglishPatch", "2026.09.29.18.07"));
            Assert.Equal("https://github.com/o/r/releases/download/v1/Patch-1.zip", UpdateService.BuildPatchZipUrl("o/r", "Patch", "v1"));
            Assert.Equal("https://github.com/o/r/releases/download/installer/Installer-win-x64.exe", UpdateService.BuildInstallerUrl("o/r"));
            Assert.Equal("2026.09.29.18.07", UpdateService.ParseTagName("""{"id":1,"tag_name":"2026.09.29.18.07","name":"x"}"""));
            Assert.Null(UpdateService.ParseTagName("{}"));
        }

        [Fact]
        public async Task Check_FindsNewerRelease_ThenDownloadsPatchAndInstaller()
        {
            var handler = new StubHandler(request => request.RequestUri!.AbsoluteUri switch
            {
                "https://api.github.com/repos/owner/repo/releases/latest" => Ok("""{"tag_name":"2026.09.29.18.07"}"""),
                "https://github.com/owner/repo/releases/download/2026.09.29.18.07/EnglishPatch-2026.09.29.18.07.zip" => Ok("zip-bytes"),
                "https://github.com/owner/repo/releases/download/installer/Installer-win-x64.exe" => Ok("exe-bytes"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
            var service = new UpdateService(new NoOpLogger(), new HttpClient(handler), Path.Combine(_root, "staging"));

            service.StartCheck(InstalledRelease.Parse(Manifest)!);
            await WaitFor(service, UpdateStatus.Available);
            Assert.Equal("2026.09.29.18.07", service.LatestVersion);

            service.StartDownload();
            await WaitFor(service, UpdateStatus.ReadyToApply);

            Assert.Equal(100, service.Progress);
            Assert.Equal("zip-bytes", File.ReadAllText(Path.Combine(_root, "staging", "EnglishPatch-2026.09.29.18.07.zip")));
            Assert.Equal("exe-bytes", File.ReadAllText(Path.Combine(_root, "staging", "Installer-win-x64.exe")));
        }

        [Fact]
        public async Task Check_ReportsUpToDate_WhenLatestIsNotNewer_OrNoReleaseExists()
        {
            var same = new UpdateService(new NoOpLogger(),
                new HttpClient(new StubHandler(_ => Ok("""{"tag_name":"2026.09.28.16.01"}"""))), _root);
            same.StartCheck(InstalledRelease.Parse(Manifest)!);
            await WaitFor(same, UpdateStatus.UpToDate);

            var none = new UpdateService(new NoOpLogger(),
                new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), _root);
            none.StartCheck(InstalledRelease.Parse(Manifest)!);
            await WaitFor(none, UpdateStatus.UpToDate);
        }

        [Fact]
        public async Task Failures_NeverThrow_AndLeaveTheInstallAlone()
        {
            var check = new UpdateService(new NoOpLogger(),
                new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))), _root);
            check.StartCheck(InstalledRelease.Parse(Manifest)!);
            await WaitFor(check, UpdateStatus.Failed);
            Assert.Contains("Update check failed", check.Error);

            var download = new UpdateService(new NoOpLogger(), new HttpClient(new StubHandler(request =>
                request.RequestUri!.AbsoluteUri.Contains("api.github.com")
                    ? Ok("""{"tag_name":"2026.09.29.18.07"}""")
                    : new HttpResponseMessage(HttpStatusCode.NotFound))), Path.Combine(_root, "staging2"));
            download.StartCheck(InstalledRelease.Parse(Manifest)!);
            await WaitFor(download, UpdateStatus.Available);
            download.StartDownload();
            await WaitFor(download, UpdateStatus.Failed);
            Assert.Contains("unchanged", download.Error);

            // Not ready, so it must refuse rather than launch anything.
            Assert.False(download.TryLaunchUpdater(1, _root));
        }

        private static HttpResponseMessage Ok(string content) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };

        private static async Task WaitFor(UpdateService service, UpdateStatus expected)
        {
            for (var i = 0; i < 200 && service.Status != expected; i++)
                await Task.Delay(25);

            Assert.Equal(expected, service.Status);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(respond(request));
        }
    }
}
