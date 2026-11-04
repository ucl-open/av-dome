Push-Location $PSScriptRoot
if (!(Test-Path "./NuGet.config") -and (Test-Path "./NuGet.config.example")) {
    Copy-Item "./NuGet.config.example" "./NuGet.config"
    Write-Host ("Created .bonsai/NuGet.config from NuGet.config.example. The copy is git-ignored: " +
        "you can safely replace %GITHUB_PACKAGES_TOKEN% in it with your own PAT (read:packages).")
}
# The lab's OpenRigs.* packages come from ../local_packages/, which a NuGet.config copied before
# the example listed it lacks; add it there, keeping the rest of that copy as it is.
if (Test-Path "./NuGet.config") {
    $existing = Get-Content "./NuGet.config" -Raw
    if ($existing -notmatch 'local_packages') {
        $existing = $existing -replace '(<packageSources>\s*\r?\n)', ('$1    <add key="local packages" value="../local_packages/" />' + "`r`n")
        Set-Content "./NuGet.config" $existing -NoNewline -Encoding UTF8
        Write-Host "Added ../local_packages/ to .bonsai/NuGet.config."
    }
}
if (!(Test-Path "./Bonsai.exe")) {
    $release = "https://github.com/bonsai-rx/bonsai/releases/latest/download/Bonsai.zip"
    $configPath = "./Bonsai.config"
    if (Test-Path $configPath) {
        [xml]$config = Get-Content $configPath
        $bootstrapper = $config.PackageConfiguration.Packages.Package.where{$_.id -eq 'Bonsai'}
        if ($bootstrapper) {
            $version = $bootstrapper.version
            $release = "https://github.com/bonsai-rx/bonsai/releases/download/$version/Bonsai.zip"
        }
    }
    Invoke-WebRequest $release -OutFile "temp.zip"
    Move-Item -Path "NuGet.config" "temp.config" -ErrorAction SilentlyContinue
    Expand-Archive "temp.zip" -DestinationPath "." -Force
    Move-Item -Path "temp.config" "NuGet.config" -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "temp.zip"
}
$nugetConfig = Get-Content "./NuGet.config" -Raw -ErrorAction SilentlyContinue
if (-not $env:GITHUB_PACKAGES_TOKEN -and $nugetConfig -match '%GITHUB_PACKAGES_TOKEN%') {
    Write-Warning ("No GitHub Packages credentials: restoring OpenRigs.* packages will fail with " +
        "401 Unauthorized. Either edit .bonsai/NuGet.config (git-ignored) and replace " +
        "%GITHUB_PACKAGES_TOKEN% with a PAT that has read:packages, or set the " +
        "GITHUB_PACKAGES_TOKEN environment variable. See NuGet.config.example for details.")
}
# The workflow's layout (config\main.layout, tracked) into the editor's per-machine file, as
# coen-behaviour-box does. rig-setup runs this again whenever config\*.layout changes, so a
# restructure that moves window indices reaches every rig with its layout. A different one
# already here is kept beside it as main.saved.layout.
$layout = ".\Settings\src\main.layout"
$tracked = "..\config\main.layout"
if (Test-Path $tracked) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $layout) -Force | Out-Null
    if ((Test-Path $layout) -and ((Get-FileHash $layout).Hash -ne (Get-FileHash $tracked).Hash)) {
        Copy-Item $layout ".\Settings\src\main.saved.layout" -Force
    }
    Copy-Item $tracked $layout -Force
}
& .\Bonsai.exe --no-editor
Pop-Location