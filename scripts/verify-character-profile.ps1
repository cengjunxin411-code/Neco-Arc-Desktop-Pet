[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path -Parent $PSScriptRoot
$run=Join-Path $root ('.local/profile-admission-'+[guid]::NewGuid().ToString('N'))
$source=Join-Path $run 'source'
[void](New-Item -ItemType Directory -Path $source -Force)
$rows=[System.Collections.Generic.List[object]]::new()
$savedProfile=$env:NECO_CHARACTER_PROFILE
$savedSettings=$env:NECO_SOURCE_ONLY_SETTINGS_DIR
function Invoke-Check([string]$Name,[string[]]$Arguments) {
    $previous=$ErrorActionPreference;$ErrorActionPreference='Continue'
    try {$lines=& dotnet @Arguments 2>&1;$code=$LASTEXITCODE} finally {$ErrorActionPreference=$previous}
    $lines | Set-Content -LiteralPath (Join-Path $run ($Name+'.log')) -Encoding utf8
    $rows.Add([pscustomobject]@{name=$Name;arguments=$Arguments;exitCode=$code})
    $lines | ForEach-Object {Write-Host $_}
    if($code -ne 0){throw "$Name failed with exit $code"}
}
try {
    $env:NECO_CHARACTER_PROFILE=$null
    Invoke-Check 'generate-original-block' @('run','--project',(Join-Path $root 'tests/SourceOnlyFallbackTests'),'-c','Release','--',$run,'--make-profile-fixture')
    # Copy only the positive public whitelist, never local evidence/private media.
    foreach($relative in Get-Content -LiteralPath (Join-Path $root 'docs/PUBLIC-FILES.txt')) {
        if(!$relative -or $relative.StartsWith('#')){continue}
        if([IO.Path]::IsPathRooted($relative) -or $relative.Split('/') -contains '..'){throw 'Invalid public whitelist path'}
        $destination=Join-Path $source $relative
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force)
        Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $destination
    }
    $profile=Get-Content -Raw -LiteralPath (Join-Path $root 'examples/character-profile.neutral.json') | ConvertFrom-Json
    foreach($entry in $profile.animations.PSObject.Properties) {
        $destination=Join-Path $source $entry.Name
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force)
        Copy-Item -LiteralPath (Join-Path $run 'synthetic-block.gif') -Destination $destination
        $entry.Value.PSObject.Properties.Remove('contacts')
        $entry.Value.PSObject.Properties.Remove('sideOverlay')
    }
    $noContact=Join-Path $run 'without-contacts.json'
    $profile | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $noContact -Encoding utf8
    foreach($entry in $profile.animations.PSObject.Properties){$entry.Value.canvas.width=81}
    $mismatch=Join-Path $run 'canvas-mismatch.json'
    $profile | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $mismatch -Encoding utf8
    $invalid=Join-Path $run 'invalid.json'
    '{' | Set-Content -LiteralPath $invalid -Encoding utf8
    Push-Location $source
    try {
        foreach($mode in @('ordinary','pilot','product')) {
            $flags=if($mode -eq 'pilot'){@('-p:DesktopPetPmv2Pilot=true')}elseif($mode -eq 'product'){@('-p:DesktopPetProduct=true')}else{@()}
            foreach($scenario in @('without-contacts','absent','canvas-mismatch','invalid')) {
                $testRoot=Join-Path $run ($mode+'-'+$scenario)
                [void](New-Item -ItemType Directory -Path $testRoot -Force)
                $env:NECO_SOURCE_ONLY_SETTINGS_DIR=$testRoot
                $env:NECO_CHARACTER_PROFILE=switch($scenario){'without-contacts'{$noContact};'canvas-mismatch'{$mismatch};'invalid'{$invalid};default{$null}}
                $extra=if($scenario -eq 'without-contacts'){@('--profile-admission')}elseif($mode -ne 'ordinary'){@('--pmv2')}else{@()}
                Invoke-Check ($mode+'-'+$scenario) (@('run','--project','tests/SourceOnlyFallbackTests','-c','Release')+$flags+@('--',$testRoot)+$extra)
            }
        }
    } finally {Pop-Location}
    Write-Host 'PASS synthetic profile integration; real character/frame conventions, hardware gestures and mixed-DPI remain separate acceptance.'
} finally {
    $env:NECO_CHARACTER_PROFILE=$savedProfile
    $env:NECO_SOURCE_ONLY_SETTINGS_DIR=$savedSettings
    $rows | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'commands.json') -Encoding utf8
    Write-Host "Synthetic evidence (ignored; do not export): $run"
}
