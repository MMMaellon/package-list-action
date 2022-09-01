using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.IO;
using Octokit;

[GitHubActions(
    "GHTest",
    GitHubActionsImage.UbuntuLatest,
    On = new[] { GitHubActionsTrigger.WorkflowDispatch },
    EnableGitHubToken = true,
    InvokedTargets = new[] { nameof(ConfigurePackageVersion) })]
class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.BuildRepoListing);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    
    GitHubActions GitHubActions => GitHubActions.Instance;

    private const string PackageManifestFilename = "package.json";
    private const string PackageVersionProperty = "version";
    private string CurrentPackageVersion;
    private const string VRCAgent = "VCCBootstrap 1.0";
    
    [Parameter("PackageName")]
    private string CurrentPackageName;

    [Parameter("Path to Target Package")] private string TargetPackagePath;
    
    Target ConfigurePackageVersion => _ => _
        .Executes(() =>
        {
            Serilog.Log.Information($"TargetPackagePath is {TargetPackagePath}");
            var manifestFile = (AbsolutePath)TargetPackagePath / PackageManifestFilename;
            var jManifest = JObject.Parse(File.ReadAllText(manifestFile));
            CurrentPackageVersion = jManifest.Value<string>(PackageVersionProperty);
            if (string.IsNullOrWhiteSpace(CurrentPackageVersion))
            {
                throw new Exception($"Could not find Package Version in {manifestFile.Name}");
            }
            Serilog.Log.Information($"Found version {CurrentPackageVersion} for {manifestFile}");
        });
    
    protected GitHubClient Client
    {
        get
        {
            if (_client == null)
            {
                _client = new GitHubClient(new ProductHeaderValue("VCC-Nuke"),
                    new Octokit.Internal.InMemoryCredentialStore(new Credentials(GitHubActions.Token)));
            }
            return _client;
        }
    }
    private GitHubClient _client;

    // Assumes single package in this type of listing, make a different one for multi-package sets
    Target BuildRepoListing => _ => _
        .Executes( async () =>
        {
            JObject repoList = new JObject()
            {
                {"name", "MyRepoName"},
                {"author", "developer@vrchat.com"},
                {"url", "https://urlParameter"},
                {"packages", new JObject()
                    {
                        CurrentPackageName, new JObject()
                        {
                            "versions", new JObject()
                        }
                    }
                }
            };

            var repoName = GitHubActions.Repository.Replace($"{GitHubActions.RepositoryOwner}/", "");
            var releases = await Client.Repository.Release.GetAll(GitHubActions.RepositoryOwner, repoName);
            foreach (var release in releases)
            {
                var manifestUrl = release.Assets.First(asset => asset.Name.CompareTo(PackageManifestFilename) == 0)
                    .BrowserDownloadUrl;
                
                // Add latest package version
                ((JObject)repoList["versions"])?.Add(release.TagName, await GetRemoteString(manifestUrl));
            }
            
            Serilog.Log.Information($"Made RepoList:\n {0}", repoList.ToString(Formatting.Indented));
        });
    
    public static async Task<string> GetRemoteString(string url)
    {
        using (var client = new WebClient())
        {
            // Add User Agent or else CloudFlare will return 1020
            client.Headers.Add(HttpRequestHeader.UserAgent, VRCAgent);
            return await client.DownloadStringTaskAsync(url);
        }
    }
}
