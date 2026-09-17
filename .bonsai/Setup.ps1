Push-Location $PSScriptRoot
if (!(Test-Path "./NuGet.config") -and (Test-Path "./NuGet.config.example")) {
    Copy-Item "./NuGet.config.example" "./NuGet.config"
    Write-Host ("Created .bonsai/NuGet.config from NuGet.config.example. The copy is git-ignored: " +
        "you can safely replace %GITHUB_PACKAGES_TOKEN% in it with your own PAT (read:packages).")
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
    Write-Warning ("No GitHub Packages credentials: restoring UclOpen.* packages will fail with " +
        "401 Unauthorized. Either edit .bonsai/NuGet.config (git-ignored) and replace " +
        "%GITHUB_PACKAGES_TOKEN% with a PAT that has read:packages, or set the " +
        "GITHUB_PACKAGES_TOKEN environment variable. See NuGet.config.example for details.")
}
& .\Bonsai.exe --no-editor
Pop-Location