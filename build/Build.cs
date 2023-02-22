//using System;
//using System.Linq;
//using Nuke.Common;
//using Nuke.Common.CI;
//using Nuke.Common.Execution;
//using Nuke.Common.IO;
//using Nuke.Common.ProjectModel;
//using Nuke.Common.Tooling;
//using Nuke.Common.Utilities.Collections;
//using static Nuke.Common.EnvironmentInfo;
//using static Nuke.Common.IO.FileSystemTasks;
//using static Nuke.Common.IO.PathConstruction;

/*
 将 GitHubActions 属性添加到 Build 类。此属性用于生成 Github 操作的生成脚本。我们为该属性提供了以下参数：
name - 这是工作流的名称。它将用于生成工作流文件名。
Image  - 这是将用于运行生成的映像。在我们的例子中，我们使用的是UbuntuLatest。
AutoGenerate  - 这是一个布尔值，指示是否应生成生成脚本。在我们的例子中，我们将其设置为 true。
FetchDepth - 这是将从存储库获取的提交数。在我们的例子中，我们将其设置为 0，这意味着所有提交都将从所有分支和标签中获取。
OnPushBranch - 这是一个分支数组，将触发推送时构建。在我们的例子中，我们将其设置为 main、dev 和 releases/**。
OnPullRequestBranch - 这是一个分支数组，将触发拉取请求上的构建。在我们的例子中，我们将其设置为版本/**。
InvokedTargets  - 这是触发生成时将调用的目标数组。在本例中，我们将其设置为“干净”。
EnableGitHubToken - 这是一个布尔值，指示是否应启用GITHUB_TOKEN。在我们的例子中，我们将其设置为 true。
ImportSecrets  - 这是将从存储库导入的机密数组。在我们的例子中，我们将其设置为 MY_GET_API_KEY 和 NUGET_API_KEY。
 */
using System.Linq;

using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Git;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using Nuke.Common.CI.GitHubActions;

using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using System.IO;
using Octokit;
using System.Threading.Tasks;
using Nuke.Common.Tools.GitHub;
using Nuke.Common.ChangeLog;
using System;
using Octokit.Internal;
using ParameterAttribute = Nuke.Common.ParameterAttribute;

[GitHubActions("continuous",
    GitHubActionsImage.UbuntuLatest,
    AutoGenerate = false,
    FetchDepth = 0,
    OnPushBranches = new[] { "main", "dev", "releases/**" },
    OnPullRequestBranches = new[] { "releases/**" },
    InvokedTargets = new[] {
        //nameof(Clean)
        nameof(Pack)
    }, EnableGitHubToken = true,
    ImportSecrets = new[] { nameof(NuGetApiKey) }
    )]

class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.Pack);

    [Solution(GenerateProjects = true)]
    readonly Solution Solution;

    [Parameter("NuGet Api Key"), Secret]
    readonly string NuGetApiKey;

    [GitVersion]
    readonly GitVersion GitVersion;

    [GitRepository]
    readonly GitRepository GitRepository;

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;


    [Parameter("Artifacts Type")]
    readonly string ArtifactsType;

    [Parameter("Copyright Details")]
    readonly string Copyright;

    [Parameter("Excluded Artifacts Type")]
    readonly string ExcludedArtifactsType;

    [Parameter("Nuget Feed Url for Public Access of Pre Releases")]
    readonly string NugetFeed;

    static Nuke.Common.CI.GitHubActions.GitHubActions GitHubActions => Nuke.Common.CI.GitHubActions.GitHubActions.Instance;

    static AbsolutePath ArtifactsDirectory => RootDirectory / ".artifacts";

    static readonly string PackageContentType = "application/octet-stream";
    static string ChangeLogFile => RootDirectory / "CHANGELOG.md";

    string GithubNugetFeed => GitHubActions != null ? $"https://nuget.pkg.github.com/{GitHubActions.RepositoryOwner}/index.json"
        : null;

    //Target Clean => _ => _
    //    .Before(Restore)
    //    .Executes(() =>
    //    {
    //    });

    //Target Restore => _ => _
    //    .Executes(() =>
    //    {
    //    });

    //Target Compile => _ => _
    //    .DependsOn(Restore)
    //    .Executes(() =>
    //    {
    //    });

    Target Clean => _ => _
        .Description("清理项目")
        .Before(Restore)
        .Executes(() =>
        {
            DotNetClean(c => c.SetProject(Solution.src.Nuke_HelloWorld));
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
        .Description("还原项目")
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetRestore(r => r.SetProjectFile(Solution.src.Nuke_HelloWorld));
        });

    Target Compile => _ => _
        .Description("编译项目")
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(b => b
            .SetProjectFile(Solution.src.Nuke_HelloWorld)
            .SetConfiguration(Configuration)
            .SetVersion(GitVersion.NuGetVersionV2)
            .SetAssemblyVersion(GitVersion.AssemblySemVer)
            .SetInformationalVersion(GitVersion.InformationalVersion)
            .SetFileVersion(GitVersion.AssemblySemFileVer)
            .EnableNoRestore()
            );
        });

    Target Pack => _ => _
        .Description("打包项目")
        .Requires(() => Configuration.Equals(Configuration.Release))
        .Produces(ArtifactsDirectory / ArtifactsType)
        .DependsOn(Compile)
        .Triggers(PublishToGitHub, PublishToNuGet)
        .Executes(() =>
        {
            DotNetPack(p =>
                p.SetProject(Solution.src.Nuke_HelloWorld)
                .SetConfiguration(Configuration)
                .SetOutputDirectory(ArtifactsDirectory)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetCopyright(Copyright)
                .SetVersion(GitVersion.NuGetVersionV2)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
            );
        });

    Target PublishToGitHub => _ => _
        .Description("发布到Github仅用于开发")
        .Triggers(CreateRelease)
        .Requires(() => Configuration.Equals(Configuration.Release))
        .OnlyWhenStatic(() => GitRepository.IsOnDevelopBranch() || GitHubActions.IsPullRequest)
        .Executes(() =>
        {
            Console.WriteLine($"{GitRepository == null} {GitHubActions == null}");
            Console.WriteLine($"Run GitHub {GitRepository?.IsOnDevelopBranch()} && {GitHubActions?.IsPullRequest}");            

            GlobFiles(ArtifactsDirectory, ArtifactsType)
                .Where(x => !x.EndsWith(ExcludedArtifactsType))
                .ForEach(x =>
                {
                    DotNetNuGetPush(s => s
                        .SetTargetPath(x)
                        .SetSource(GithubNugetFeed)
                        .SetApiKey(GitHubActions.Token)
                        .EnableSkipDuplicate()
                    );
                });
        });


    Target PublishToNuGet => _ => _
    .Description($"将这个版本发布到NuGet")
    .Requires(() => Configuration.Equals(Configuration.Release))
    .Triggers(CreateRelease)
    .OnlyWhenStatic(() => GitRepository.IsOnReleaseBranch())
    .Executes(() =>
    {
        GlobFiles(ArtifactsDirectory, ArtifactsType)
            .Where(x => !x.EndsWith(ExcludedArtifactsType))
            .ForEach(x =>
            {
                DotNetNuGetPush(s => s
                    .SetTargetPath(x)
                    .SetSource(NugetFeed)
                    .SetApiKey(NuGetApiKey)
                    .EnableSkipDuplicate()
                );
            });
    });


    Target CreateRelease => _ => _
        .Description("创建发布Release版本")
        .Requires(() => Configuration.Equals(Configuration.Release))
        .OnlyWhenDynamic(() => GitRepository.IsOnMainOrMasterBranch() || GitRepository.IsOnReleaseBranch())
        .Executes(async () =>
        {
            var credentials = new Credentials(GitHubActions.Token);
            GitHubTasks.GitHubClient = new GitHubClient(new ProductHeaderValue(nameof(NukeBuild)),
               new InMemoryCredentialStore(credentials));

            var (owner, name) = (GitRepository.GetGitHubOwner(), GitRepository.GetGitHubName());

            var releaseTag = GitVersion.NuGetVersionV2;
            var changeLogSectionEntries = ChangelogTasks.ExtractChangelogSectionNotes(ChangeLogFile);
            var latestChangeLog = changeLogSectionEntries.Aggregate((c, n) => c + Environment.NewLine + n);

            var newRelease = new NewRelease(releaseTag)
            {
                TargetCommitish = GitVersion.Sha,
                Draft = true,
                Name = $"v{releaseTag}",
                Prerelease = !string.IsNullOrEmpty(GitVersion.PreReleaseTag),
                Body = latestChangeLog
            };
            

            var createdRelease = await GitHubTasks.GitHubClient.Repository.Release.Create(owner, name, newRelease);

            //GlobFiles(ArtifactsDirectory, ArtifactsType)
            //    .Where(x=> !x.EndsWith(ExcludedArtifactsType))
            //    .ForEach(async x => await UploadReleaseAssetToGitHub(createdRelease, x));

            GlobFiles(ArtifactsDirectory, ArtifactsType)
             .Where(x => !x.EndsWith(ExcludedArtifactsType))
             .ForEach(async x => await UploadReleaseAssetToGithub(createdRelease, x));

            await GitHubTasks
                  .GitHubClient
                  .Repository
                  .Release
                  .Edit(owner, name, createdRelease.Id, new ReleaseUpdate { Draft = false });
        });

    private static async Task UploadReleaseAssetToGithub(Release release, string asset)
    {
        await using var artifactStream = File.OpenRead(asset);
        var fileName = Path.GetFileName(asset);
        var assetUpload = new ReleaseAssetUpload
        {
            FileName = fileName,
            ContentType = PackageContentType,
            RawData = artifactStream,
        };
        await GitHubTasks.GitHubClient.Repository.Release.UploadAsset(release, assetUpload);
    }


    //private static async Task UploadReleaseAssetToGitHub1(Release release,string asset)
    //{
    //    await using var artifactStream = File.OpenRead(asset);
    //    var fileName = Path.GetFileName(asset);
    //    var assetUPload = new ReleaseAssetUpload
    //    {
    //        FileName = fileName,
    //        ContentType = PackageContentType,
    //        RawData = artifactStream,
    //    };

    //    await GitHubTasks.GitHubClient.Repository.Release
    //        .UploadAsset(release, assetUPload);
    //}

    //private static async Task UploadReleaseAssetToGithub(Release release, string asset)
    //{
    //    await using var artifactStream = File.OpenRead(asset);
    //    var fileName = Path.GetFileName(asset);
    //    var assetUpload = new ReleaseAssetUpload
    //    {
    //        FileName = fileName,
    //        ContentType = PackageContentType,
    //        RawData = artifactStream,
    //    };
    //    await GitHubTasks.GitHubClient.Repository.Release.UploadAsset(release, assetUpload);        
    //}



    // 异步执行

    //Target MyTarget => _ => _
    //    .Executes(async () =>
    //    {
    //        await Console.Out.WriteLineAsync("Hello Nuke Async");
    //    });

}
