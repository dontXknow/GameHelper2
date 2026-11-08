param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../Verification/Artifacts/release'),
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$Revision
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$build = (Resolve-Path (Join-Path $repo 'GameHelper/bin/Release/net10.0-windows/win-x64')).Path
$output = New-Item -ItemType Directory -Path $OutputDirectory -Force
$staging = Join-Path $output.FullName ('staging-' + [Guid]::NewGuid().ToString('N'))
$package = New-Item -ItemType Directory -Path (Join-Path $staging 'GameHelper') -Force

$required = @(
    'GameHelper.exe', 'GameHelper.dll', 'GameHelper.deps.json', 'GameHelper.runtimeconfig.json',
    'Launcher.exe', 'Launcher.dll', 'Launcher.deps.json', 'Launcher.runtimeconfig.json',
    'GameOffsets.dll', 'cimgui.dll'
)
foreach ($file in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $build $file) -PathType Leaf)) {
        throw "Missing build output: $file. Build GameOverlay.sln in Release first."
    }
}
Get-ChildItem -LiteralPath $build -File | Where-Object {
    $_.Name -in $required -or $_.Extension -in '.dll', '.pdb'
} | Copy-Item -Destination $package.FullName

foreach ($directory in 'fonts', 'Localization') {
    Copy-Item -LiteralPath (Join-Path $build $directory) -Destination $package.FullName -Recurse
}

# Package only plugins built by the solution and their shipped assets.
$solution = Get-Content -LiteralPath (Join-Path $repo 'GameOverlay.sln') -Raw
$plugins = [regex]::Matches($solution, '"Plugins\\([^"\\]+)\\[^"\\]+\.csproj"')
if (!$plugins.Count) { throw 'No plugin projects found in GameOverlay.sln.' }
foreach ($project in $plugins) {
    $plugin = $project.Groups[1].Value
    $source = Join-Path $build "Plugins/$plugin"
    if (!(Test-Path -LiteralPath (Join-Path $source "$plugin.dll") -PathType Leaf)) {
        throw "Missing plugin build: $plugin.dll"
    }

    $destination = New-Item -ItemType Directory -Path (Join-Path $package.FullName "Plugins/$plugin") -Force
    Get-ChildItem -LiteralPath $source -File | Where-Object {
        $_.Extension -in '.dll', '.pdb' -or
        $_.Name -in 'StatusEffectGroup.json', 'boss_arena_tgt_files.txt', 'important_tgt_files.txt', 'stairs_tgt_files.txt'
    } | Copy-Item -Destination $destination.FullName
    foreach ($directory in 'icons', 'json', 'Localization', 'Textures') {
        $assets = Join-Path $source $directory
        if (Test-Path -LiteralPath $assets -PathType Container) {
            Copy-Item -LiteralPath $assets -Destination $destination.FullName -Recurse
        }
    }

    $license = Join-Path $repo "Plugins/$plugin/LICENSE"
    if (Test-Path -LiteralPath $license -PathType Leaf) {
        Copy-Item -LiteralPath $license -Destination $destination.FullName
    }
}

Copy-Item -LiteralPath (Join-Path $repo 'release/README.md'), (Join-Path $repo 'CHANGELOG.md') -Destination $package.FullName
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $build 'GameHelper.dll')).ProductVersion
$label = if ($Revision) { $Revision.Substring(0, 7) } else { 'local' }
$zip = Join-Path $output.FullName "GameHelper2_v${version}_${label}_win-x64.zip"
Compress-Archive -LiteralPath $package.FullName -DestinationPath $zip -Force
[pscustomobject]@{
    Path = $zip
    SHA256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
}
