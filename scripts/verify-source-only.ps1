[CmdletBinding()]
param([switch]$DesktopSmoke)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$run = Join-Path $root ('.local/verification-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $run)
$rows = [System.Collections.Generic.List[object]]::new()
$saved = @{}
foreach ($item in Get-ChildItem Env:NECO_*) { $saved[$item.Name] = $item.Value; Remove-Item -LiteralPath ('Env:' + $item.Name) }
$env:NECO_TEST_OUTPUT_ROOT = Join-Path $run 'fixtures'
$env:NECO_SOURCE_ONLY_SETTINGS_DIR = Join-Path $run 'settings'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
function Invoke-Dotnet([string]$Name, [string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $lines = & dotnet @Arguments 2>&1; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previousPreference }
    $lines | Set-Content -LiteralPath (Join-Path $run ($Name + '.log')) -Encoding utf8
    $rows.Add([pscustomobject]@{ name=$Name; arguments=$Arguments; exitCode=$code })
    $lines | ForEach-Object { Write-Host $_ }
    Write-Host "$Name exit=$code"
    if ($code -ne 0) { throw "$Name failed with exit $code" }
}
Push-Location $root
try {
    Invoke-Dotnet 'sdk' @('--info')
    $modes = @(
        @{name='ordinary'; flags=@(); prefix=''; assembly='NecoArcDesktopPet'},
        @{name='product'; flags=@('-p:DesktopPetProduct=true'); prefix='product/'; assembly='NecoArcDesktopPet.Product'},
        @{name='pilot'; flags=@('-p:DesktopPetPmv2Pilot=true'); prefix='pmv2-pilot/'; assembly='NecoArcDesktopPet.PMv2Pilot'}
    )
    foreach ($mode in $modes) {
        Invoke-Dotnet ($mode.name + '-restore') (@('restore','NecoArcDesktopPet.csproj','--configfile','NuGet.Config') + $mode.flags)
        Invoke-Dotnet ($mode.name + '-rebuild') (@('build','NecoArcDesktopPet.csproj','-c','Release','--no-restore','-t:Rebuild') + $mode.flags)
    }
    $suites = @('ClickReactionTests','CoordinateGeometryTests','MonitorTopologyTests','PhysicsModelTests','GravityTests','PlanarInertiaTests','PhysicsGatingTests','DpiHwndVerificationTests','SourceOnlyIsolationTests','CharacterProfileTests')
    foreach ($suite in $suites) {
        $project = "tests/$suite/$suite.csproj"
        Invoke-Dotnet ($suite + '-restore') @('restore',$project,'--configfile','NuGet.Config')
        $arguments = @('run','--project',$project,'-c','Release','--no-restore')
        if ($suite -eq 'SourceOnlyIsolationTests') { $arguments += @('--',(Join-Path $run 'isolation')) }
        if ($suite -eq 'CharacterProfileTests') { $arguments += @('--',(Join-Path $run 'character-profile')) }
        Invoke-Dotnet $suite $arguments
    }
    if ($DesktopSmoke) {
        Invoke-Dotnet 'fallback-restore' @('restore','tests/SourceOnlyFallbackTests','--configfile','NuGet.Config')
        Invoke-Dotnet 'fallback-workbench' @('run','--project','tests/SourceOnlyFallbackTests','-c','Release','--no-restore','--',(Join-Path $run 'fallback'))
        try {
            & (Join-Path $PSScriptRoot 'verify-character-profile.ps1')
            $rows.Add([pscustomobject]@{ name='character-profile-desktop'; arguments=@('scripts/verify-character-profile.ps1'); exitCode=0 })
        } catch {
            $rows.Add([pscustomobject]@{ name='character-profile-desktop'; arguments=@('scripts/verify-character-profile.ps1'); exitCode=1 })
            throw
        }
    }
    foreach ($pmv2Mode in @('pilot','product')) {
        $flag = if ($pmv2Mode -eq 'pilot') { '-p:DesktopPetPmv2Pilot=true' } else { '-p:DesktopPetProduct=true' }
        Invoke-Dotnet ($pmv2Mode + '-regression-restore') @('restore','tests/SourceOnlyFallbackTests','--configfile','NuGet.Config',$flag)
        Invoke-Dotnet ($pmv2Mode + '-layout-inputs') @('run','--project','tests/SourceOnlyFallbackTests','-c','Release','--no-restore',$flag,'--',(Join-Path $run ($pmv2Mode + '-sizes')),'--pmv2-sizes')
        if ($DesktopSmoke) {
            Invoke-Dotnet ($pmv2Mode + '-missing-idle-layout') @('run','--project','tests/SourceOnlyFallbackTests','-c','Release','--no-restore','--no-build',$flag,'--',(Join-Path $run ($pmv2Mode + '-missing')),'--pmv2')
        }
    }
    Invoke-Dotnet 'verifier-restore'  @('restore','tools/SourceOnlyVerifier','--configfile','NuGet.Config')
    Invoke-Dotnet 'verifier-build' @('build','tools/SourceOnlyVerifier','-c','Release','--no-restore')
    $verifier = 'tools/SourceOnlyVerifier/bin/Release/net10.0/SourceOnlyVerifier.dll'
    Invoke-Dotnet 'source-isolation' @($verifier,'source',$root)
    $dotnetPath = (Get-Command dotnet).Source
    $packs = Join-Path (Split-Path -Parent $dotnetPath) 'packs/Microsoft.NETCore.App.Host.win-x64'
    $hostPack = Get-ChildItem -LiteralPath $packs -Directory | Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if ($null -eq $hostPack) { throw 'Installed .NET 10 Windows x64 apphost reference pack is required for icon verification.' }
    $sdkHost = Join-Path $hostPack.FullName 'runtimes/win-x64/native/apphost.exe'
    foreach ($mode in $modes) {
        $output = 'bin/' + $mode.prefix + 'Release/net10.0-windows/' + $mode.assembly
        Invoke-Dotnet ($mode.name + '-resources') @($verifier,'output',($output + '.dll'),($output + '.exe'),$sdkHost)
    }
    Write-Host 'PASS local source-only checks. Product/Pilot animation runtime and GitHub-hosted CI are separate gates.'
}
finally {
    $rows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'commands.json') -Encoding utf8
    Pop-Location
    foreach ($item in Get-ChildItem Env:NECO_*) { Remove-Item -LiteralPath ('Env:' + $item.Name) }
    foreach ($key in $saved.Keys) { Set-Item -LiteralPath ('Env:' + $key) -Value $saved[$key] }
    Write-Host "Local evidence: $run (ignored; do not publish)"
}
