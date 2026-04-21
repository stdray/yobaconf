#addin nuget:?package=Cake.Docker&version=1.3.0
#tool dotnet:?package=GitVersion.Tool&version=6.4.0

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var dockerImage = Argument("dockerImage", "yobaconf-web");
var dockerTagArgument = Argument("dockerTag", string.Empty);
var dockerPushEnabled = Argument("dockerPush", false);
var ghcrRepositoryArgument = Argument("ghcrRepository", string.Empty);
var dockerTagOutputArgument = Argument("dockerTagOutput", string.Empty);

var solution = "./YobaConf.slnx";
var webProject = "./src/YobaConf.Web/YobaConf.Web.csproj";
var dockerFile = "./src/YobaConf.Web/Dockerfile";

GitVersion gitVersion = null;
var computedDockerTag = "latest";

DotNetBuildSettings CreateVersionedBuildSettings(string buildVersion, string shortSha, string commitDate) =>
	new()
	{
		Configuration = configuration,
		NoRestore = true,
		MSBuildSettings = new DotNetMSBuildSettings()
			.WithProperty("Version", buildVersion)
			.WithProperty("InformationalVersion", $"{buildVersion} ({shortSha}, {commitDate})")
			.WithProperty("GitShortSha", shortSha)
			.WithProperty("GitCommitDate", commitDate)
	};

Task("Clean")
	.Does(() =>
{
	DotNetClean(solution, new DotNetCleanSettings { Configuration = configuration });
});

Task("Restore")
	.IsDependentOn("Clean")
	.Does(() =>
{
	DotNetRestore(solution);
});

Task("Version")
	.IsDependentOn("Restore")
	.Does(() =>
{
	gitVersion = GitVersion(new GitVersionSettings
	{
		OutputType = GitVersionOutput.Json,
		NoFetch = true
	});

	Information("GitVersion FullSemVer: {0}", gitVersion.FullSemVer);
	Information("GitVersion ShortSha: {0}", gitVersion.ShortSha);
	Information("GitVersion CommitDate: {0}", gitVersion.CommitDate);
});

Task("Build")
	.IsDependentOn("Version")
	.Does(() =>
{
	var buildVersion = gitVersion.FullSemVer;
	DotNetBuild(solution, CreateVersionedBuildSettings(buildVersion, gitVersion.ShortSha, gitVersion.CommitDate));
});

Task("Test")
	.IsDependentOn("Build")
	.Does(() =>
{
	DotNetTest(solution, new DotNetTestSettings
	{
		Configuration = configuration,
		NoBuild = true
	});
});

Task("Docker")
	.IsDependentOn("Test")
	.Does(() =>
{
	var gitVersionTag = gitVersion.FullSemVer.Replace('+', '-');
	var finalTag = string.IsNullOrWhiteSpace(dockerTagArgument) ? gitVersionTag : dockerTagArgument;
	computedDockerTag = finalTag;
	var imageWithTag = $"{dockerImage}:{finalTag}";

	if (!string.IsNullOrWhiteSpace(dockerTagOutputArgument))
	{
		var outputPath = MakeAbsolute(FilePath.FromString(dockerTagOutputArgument));
		EnsureDirectoryExists(outputPath.GetDirectory());
		System.IO.File.WriteAllText(outputPath.FullPath, finalTag);
	}

	Information("Building Docker image {0}", imageWithTag);

	var buildSettings = new DockerImageBuildSettings
	{
		File = dockerFile,
		Tag = new[] { imageWithTag },
		BuildArg = new[]
		{
			$"APP_VERSION={gitVersion.FullSemVer}",
			$"GIT_SHORT_SHA={gitVersion.ShortSha}",
			$"GIT_COMMIT_DATE={gitVersion.CommitDate}"
		}
	};

	DockerBuild(buildSettings, ".");
});

// Smoke-test the chiseled runtime: launch container, wait for HTTP 200 on /, tear down.
// Without this we can't distinguish "built clean" from "runs clean" -- chiseled has no shell
// for docker-exec debugging, so failures only surface at runtime. 30s total timeout.
Task("DockerSmoke")
	.IsDependentOn("Docker")
	.Does(() =>
{
	var imageWithTag = $"{dockerImage}:{computedDockerTag}";
	var containerName = $"yobaconf-smoke-{Guid.NewGuid():N}".Substring(0, 30);

	Information("Starting smoke-test container {0}", containerName);
	var runExit = StartProcess("docker", new ProcessSettings
	{
		Arguments = $"run -d --name {containerName} -p 8080:8080 {imageWithTag}"
	});
	if (runExit != 0)
		throw new CakeException($"docker run failed with exit code {runExit}");

	try
	{
		var healthy = false;
		for (var i = 1; i <= 30; i++)
		{
			System.Threading.Thread.Sleep(1000);
			var curlExit = StartProcess("curl", new ProcessSettings
			{
				Arguments = "-fsSL -o /dev/null http://localhost:8080/",
				RedirectStandardError = true,
				RedirectStandardOutput = true
			});
			if (curlExit == 0)
			{
				Information("Smoke test passed after {0}s", i);
				healthy = true;
				break;
			}
		}

		if (!healthy)
		{
			StartProcess("docker", $"logs {containerName}");
			throw new CakeException("Container did not respond with 200 on / within 30s");
		}
	}
	finally
	{
		StartProcess("docker", $"stop {containerName}");
		StartProcess("docker", $"rm {containerName}");
	}
});

Task("DockerPush")
	.IsDependentOn("DockerSmoke")
	.WithCriteria(() => dockerPushEnabled)
	.Does(() =>
{
	if (string.IsNullOrWhiteSpace(computedDockerTag))
		throw new CakeException("Docker tag was not computed. Ensure the Docker task ran successfully before pushing.");

	var sourceImage = $"{dockerImage}:{computedDockerTag}";

	var repository = ghcrRepositoryArgument;

	if (string.IsNullOrWhiteSpace(repository))
	{
		var githubRepositoryEnv = EnvironmentVariable("GITHUB_REPOSITORY");
		if (string.IsNullOrWhiteSpace(githubRepositoryEnv))
			throw new CakeException("dockerPush enabled but no ghcrRepository argument provided and GITHUB_REPOSITORY environment variable is missing. Provide --ghcrRepository or set GITHUB_REPOSITORY.");

		var imageName = dockerImage;
		if (imageName.Contains('/'))
			imageName = imageName.Substring(imageName.LastIndexOf('/') + 1);

		repository = $"ghcr.io/{githubRepositoryEnv.ToLowerInvariant()}/{imageName.ToLowerInvariant()}";
	}
	else if (!repository.StartsWith("ghcr.io/", StringComparison.OrdinalIgnoreCase))
	{
		repository = $"ghcr.io/{repository}";
	}

	var targetImage = $"{repository}:{computedDockerTag}";

	var ghcrUsername = EnvironmentVariable("GHCR_USERNAME");
	var ghcrToken = EnvironmentVariable("GHCR_TOKEN");

	if (!string.IsNullOrWhiteSpace(ghcrUsername) && !string.IsNullOrWhiteSpace(ghcrToken))
		DockerLogin("ghcr.io", ghcrUsername, ghcrToken);
	else
		Information("GHCR credentials not provided via GHCR_USERNAME/GHCR_TOKEN; assuming docker login already performed.");

	Information("Tagging {0} as {1}", sourceImage, targetImage);
	DockerTag(sourceImage, targetImage);

	Information("Pushing {0}", targetImage);
	DockerPush(targetImage);
});

Task("Default")
	.IsDependentOn("DockerPush");

RunTarget(target);
