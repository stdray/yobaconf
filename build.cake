#addin nuget:?package=Cake.Docker&version=1.5.0-beta.1&prerelease
#tool dotnet:?package=GitVersion.Tool&version=6.4.0

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var dockerImage = Argument("dockerImage", "yobaconf");
var dockerTagArgument = Argument("dockerTag", string.Empty);
var dockerPushEnabled = Argument("dockerPush", false);
var ghcrRepositoryArgument = Argument("ghcrRepository", string.Empty);
var dockerTagOutputArgument = Argument("dockerTagOutput", string.Empty);
// Buildx GHA layer cache — passed as CLI args from CI, empty locally so dev builds
// stay on the default docker daemon with no buildx cache backend dependency.
// Typical CI invocation: --dockerCacheFrom=type=gha --dockerCacheTo=type=gha,mode=max
var dockerCacheFrom = Argument("dockerCacheFrom", string.Empty);
var dockerCacheTo = Argument("dockerCacheTo", string.Empty);

var solution = "./YobaConf.slnx";
var webProject = "./src/YobaConf.Web/YobaConf.Web.csproj";
var unitTestProject = "./tests/YobaConf.Tests/YobaConf.Tests.csproj";
var e2eTestProject = "./tests/YobaConf.E2ETests/YobaConf.E2ETests.csproj";
var dockerFile = "./src/YobaConf.Web/Dockerfile";
var runnerDockerFile = "./src/YobaConf.Runner/Dockerfile";
var runnerImage = "yobaconf-runner";

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

// Fast lane: unit tests only. E2E sits in its own target because it needs a Playwright
// Chromium download (~200MB) + headless browser runtime that shouldn't block main builds.
Task("Test")
	.IsDependentOn("Build")
	.Does(() =>
{
	DotNetTest(unitTestProject, new DotNetTestSettings
	{
		Configuration = configuration,
		NoBuild = true
	});
});

// E2E: auto-installs Playwright browsers on first run (fast-path on re-runs — cached in
// ~/.cache/ms-playwright). `--with-deps` pulls system libs on Linux; no-op on Win/macOS.
// Depends on Build, not Test, so CI runs the two test targets in parallel without
// duplicating the compile step.
Task("E2ETest")
	.IsDependentOn("Build")
	.Does(() =>
{
	var installer = GetFiles("tests/YobaConf.E2ETests/bin/**/playwright.ps1").FirstOrDefault();
	if (installer is null)
		throw new CakeException("playwright.ps1 not found — Build target did not produce it");
	var withDeps = IsRunningOnUnix() ? "--with-deps" : "";
	var installExit = StartProcess("pwsh", new ProcessSettings
	{
		Arguments = $"{installer.FullPath} install chromium {withDeps}".Trim(),
	});
	if (installExit != 0)
		throw new CakeException($"playwright install failed with exit code {installExit}");

	DotNetTest(e2eTestProject, new DotNetTestSettings
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

	// buildx + GHA layer cache when --dockerCacheFrom/--dockerCacheTo are provided
	// (CI). Load=true brings the final image into the local docker daemon so
	// DockerSmoke and DockerPush can tag/run it. Without CacheFrom/CacheTo, buildx
	// still works (local dev) — it just doesn't read/write the gha backend.
	var cacheFrom = string.IsNullOrWhiteSpace(dockerCacheFrom) ? Array.Empty<string>() : new[] { dockerCacheFrom };
	var cacheTo = string.IsNullOrWhiteSpace(dockerCacheTo) ? Array.Empty<string>() : new[] { dockerCacheTo };

	var buildSettings = new DockerBuildXBuildSettings
	{
		File = dockerFile,
		Tag = new[] { imageWithTag },
		BuildArg = new[]
		{
			$"APP_VERSION={gitVersion.FullSemVer}",
			$"GIT_SHORT_SHA={gitVersion.ShortSha}",
			$"GIT_COMMIT_DATE={gitVersion.CommitDate}"
		},
		CacheFrom = cacheFrom,
		CacheTo = cacheTo,
		Load = true,
	};

	DockerBuildXBuild(buildSettings, ".");
});

// Runner image — same build-cache story as the web image, but the output image's job is
// to expose `/yobaconf-run` for downstream COPY --from. No ENTRYPOINT smoke needed; we
// confirm the image built by doing a `docker run --rm <img> --help` expectation that the
// binary runs and prints the usage (exit 5 because no child args → InvalidArgs).
Task("DockerRunner")
	.IsDependentOn("Test")
	.Does(() =>
{
	var tag = string.IsNullOrWhiteSpace(dockerTagArgument)
		? gitVersion.FullSemVer.Replace('+', '-')
		: dockerTagArgument;
	var imageWithTag = $"{runnerImage}:{tag}";

	Information("Building runner image {0}", imageWithTag);

	var cacheFrom = string.IsNullOrWhiteSpace(dockerCacheFrom) ? Array.Empty<string>() : new[] { dockerCacheFrom };
	var cacheTo = string.IsNullOrWhiteSpace(dockerCacheTo) ? Array.Empty<string>() : new[] { dockerCacheTo };

	DockerBuildXBuild(new DockerBuildXBuildSettings
	{
		File = runnerDockerFile,
		Tag = new[] { imageWithTag },
		BuildArg = new[]
		{
			$"APP_VERSION={gitVersion.FullSemVer}",
			$"GIT_SHORT_SHA={gitVersion.ShortSha}",
		},
		CacheFrom = cacheFrom,
		CacheTo = cacheTo,
		Load = true,
	}, ".");
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
				// `-f` drops on any 4xx/5xx; `-s` silent; `-S` shows errors; `--max-time 2` bounds each
				// probe so a mid-boot hang doesn't eat the whole loop. `-o NUL` on Windows because
				// `curl.exe` doesn't understand `/dev/null`.
				Arguments = IsRunningOnWindows()
					? "-fsS --max-time 2 -o NUL http://127.0.0.1:8080/health"
					: "-fsS --max-time 2 -o /dev/null http://127.0.0.1:8080/health",
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

// Publish-gate invariant: DockerPush depends on every test target — explicitly,
// not just transitively. Test is already in the chain via DockerSmoke → Docker →
// Test, but listing it here keeps the top-level dependencies visible without
// walking the tree. When a new test project lands (E2E in Phase B, perf, etc.),
// append `.IsDependentOn("NewTestTarget")` so publish can never ship ahead of
// any test suite. Mirrors the same rule in yobalog `build.cake`.
Task("DockerPush")
	.IsDependentOn("Test")
	.IsDependentOn("E2ETest")
	.IsDependentOn("DockerSmoke")
	.IsDependentOn("DockerRunner")
	.WithCriteria(() => dockerPushEnabled)
	.Does(() =>
{
	if (string.IsNullOrWhiteSpace(computedDockerTag))
		throw new CakeException("Docker tag was not computed. Ensure the Docker task ran successfully before pushing.");

	var sourceImage = $"{dockerImage}:{computedDockerTag}";
	var runnerSourceImage = $"{runnerImage}:{computedDockerTag}";

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
	// Runner image shares the GHCR org but sits under its own image name: …/yobaconf-runner.
	var runnerRepository = repository.EndsWith("/yobaconf", StringComparison.OrdinalIgnoreCase)
		? repository.Substring(0, repository.Length - "yobaconf".Length) + "yobaconf-runner"
		: repository + "-runner";
	var runnerTargetImage = $"{runnerRepository}:{computedDockerTag}";

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

	Information("Tagging {0} as {1}", runnerSourceImage, runnerTargetImage);
	DockerTag(runnerSourceImage, runnerTargetImage);
	Information("Pushing {0}", runnerTargetImage);
	DockerPush(runnerTargetImage);
});

// Single-window dev loop: bun watchers (ts + css via concurrently) and dotnet watch stream to
// the same terminal. Ctrl+C kills both process trees. No two-window ps1 wrapper needed.
// Uses System.Diagnostics.Process directly (Cake's IProcess lacks HasExited / tree-kill).
Task("Dev")
	.Does(() =>
{
	var webDir = MakeAbsolute(Directory("./src/YobaConf.Web")).FullPath;

	var frontend = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bun", "run dev")
	{
		WorkingDirectory = webDir,
		UseShellExecute = false,
	});
	var backend = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "watch --project src/YobaConf.Web")
	{
		UseShellExecute = false,
	});

	if (frontend == null || backend == null)
		throw new CakeException("Failed to start dev processes (bun / dotnet).");

	void KillAll()
	{
		try { if (!frontend.HasExited) frontend.Kill(entireProcessTree: true); } catch { }
		try { if (!backend.HasExited) backend.Kill(entireProcessTree: true); } catch { }
	}

	Console.CancelKeyPress += (_, e) =>
	{
		e.Cancel = true;
		KillAll();
	};

	Information("dev loop started (bun watchers + dotnet watch). Ctrl+C to stop.");

	// Poll until either child exits, then tear down the other. entireProcessTree=true covers
	// concurrently's ts/css sub-bun-s and dotnet watch's app child.
	while (!frontend.HasExited && !backend.HasExited)
	{
		System.Threading.Thread.Sleep(500);
	}

	KillAll();
});

// Single-target entry point for the PR-only `ci` job — Test + E2ETest share the Build
// task (Cake DAG). Keeps the CI yml simple (one `./build.sh --target=CI` line) and
// ensures E2E doesn't run if unit tests fail.
Task("CI")
	.IsDependentOn("Test")
	.IsDependentOn("E2ETest");

Task("Default")
	.IsDependentOn("DockerPush");

RunTarget(target);
