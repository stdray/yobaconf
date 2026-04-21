# Launches the dev loop: bun watchers (TS + Tailwind via concurrently) and dotnet watch.
# Two new PowerShell windows open with named titles; close each with Ctrl+C or the window X.
#
# Debug MSBuild does NOT invoke `bun install`/`bun run build` — the frontend watcher owns
# wwwroot/ during dev and we don't want the two fighting over output (see Directory.Build.props
# + YobaConf.Web.csproj BuildFrontend target, Release-only).

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot

$frontCmd = '$Host.UI.RawUI.WindowTitle = ''yobaconf-frontend''; Set-Location ''{0}\src\YobaConf.Web''; bun run dev' -f $Root
$backCmd  = '$Host.UI.RawUI.WindowTitle = ''yobaconf-backend''; Set-Location ''{0}''; dotnet watch --project src/YobaConf.Web' -f $Root

Start-Process powershell -ArgumentList '-NoExit', '-Command', $frontCmd
Start-Process powershell -ArgumentList '-NoExit', '-Command', $backCmd

Write-Host ''
Write-Host 'Started two dev processes in separate windows:'
Write-Host '  yobaconf-frontend  - ts + tailwind watchers (via concurrently)'
Write-Host '  yobaconf-backend   - dotnet watch (hot-reload .cs/.cshtml)'
Write-Host ''
Write-Host 'Ctrl+C in each window to stop, or close the window.'
