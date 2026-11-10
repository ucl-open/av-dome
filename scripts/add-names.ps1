param([switch]$Apply, [string]$ListPath = 'K:\config\data-dictionary.json')
# Adds this workflow's trial columns (schemas\trial-columns.yml) and its own streams to the lab's
# names list (K:\config\data-dictionary.json), which rig-control checks before it launches the
# workflow, and tags each name this workflow uses with av-dome (usedBy; every other
# repo's tags are kept). Names it no longer writes ($retired) lose its tag, and leave the list when
# no other repo is tagged on them. Without -Apply it only says what it would change. -Apply saves through
# rig-setup's own Save-NamesDictionary, which refuses if the file changed since it was read, backs it
# up and logs the change. The list is production config: apply only on Pip's word.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\add-names.ps1 [-Apply]
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$me = 'av-dome'
$rigSetup = 'C:\Github\Coen-Lab\rig-setup'
. "$rigSetup\engine\detect.ps1"
. "$rigSetup\engine\device-names.ps1"
$path = $ListPath  # another copy only to try the script
$was = [IO.File]::ReadAllText($path)
$prev = $was | ConvertFrom-Json
$read = "import json,yaml;d=yaml.safe_load(open(r'$repo\schemas\trial-columns.yml',encoding='utf-8'));print(json.dumps({k:{'type':v['type'],'description':v['description']} for k,v in d.items()}))"
$cols = uv run --project $repo --with pyyaml python -c $read | ConvertFrom-Json
if (-not $cols) { throw "could not read $repo\schemas\trial-columns.yml" }
$tc = [ordered]@{}
foreach ($p in $prev.trialColumns.PSObject.Properties) { $tc[$p.Name] = $p.Value }
$added = @()
foreach ($p in $cols.PSObject.Properties) {
    if ($tc.Contains($p.Name)) {
        if ($tc[$p.Name].type -ne $p.Value.type) { throw "column $($p.Name) is $($tc[$p.Name].type) on the list, $($p.Value.type) here" }
        continue
    }
    $tc[$p.Name] = [ordered]@{ type = $p.Value.type; description = $p.Value.description }
    $added += "column $($p.Name)"
}
# The lab-wide meaning of the laser columns av-dome shares (audit, 10 Nov 2026): the list said "sham" and
# fitted coen-dome-navigation only. Its scripts\add-names.ps1 sets the same text: change both together.
$labDescriptions = [ordered]@{
    laserCondition = 'The Zapit condition (stimLocations number, from 1) sent to Zapit this trial, 0 when none was sent. Whether the laser was on is laserPowerMw > 0; each workflow''s trial-columns.yml says when a condition goes with the laser off.'
    laserPowerMw   = 'Laser power sent to Zapit for this trial (the task''s optogenetics.laserPowerMw), as commanded, not measured. 0 on laser-off trials and without optogenetics.'
}
$described = @()
foreach ($n in $labDescriptions.Keys) {
    if ($tc.Contains($n) -and $tc[$n].description -cne $labDescriptions[$n]) { $tc[$n].description = $labDescriptions[$n]; $described += $n }
}
$streams = @($prev.streams)
# This workflow's own stream: Zapit's replies.
$ownStreams = @('BonsaiData_ZapitReplies')
# Names this workflow wrote once and no longer does (Pip, 9 Nov 2026): optoStimulated and optoCondition became
# laserCondition (and laserPowerMw); the angle columns end in their unit; Zapit's replies moved into BonsaiData.
$retired = @{ trialColumns = @('optoStimulated', 'optoCondition', 'visualAzimuth', 'visualElevation', 'visualPhase', 'audioAzimuth', 'audioElevation'); streams = @('Zapit') }
foreach ($s in $ownStreams) { if ($streams -notcontains $s) { $streams += $s; $added += "stream $s" } }
$es = [ordered]@{}; foreach ($p in $prev.experimentSettings.PSObject.Properties) { $es[$p.Name] = $p.Value }
# usedBy as it was, with this repo on each of its names (the lab's Settings_* and calibration streams
# it already writes are tagged by whoever saves the list).
$usedBy = [ordered]@{}
foreach ($section in 'streams', 'experimentSettings', 'trialColumns') {
    $usedBy[$section] = [ordered]@{}
    $old = Get-NamesMember (Get-NamesMember $prev 'usedBy') $section
    if ($old) { foreach ($p in $old.PSObject.Properties) { $usedBy[$section][$p.Name] = @($p.Value) } }
}
$tagged = @()
function Add-Tag([string]$Section, [string]$Name) {
    $tags = @($usedBy[$Section][$Name] | Where-Object { $_ })
    if ($tags -notcontains $me) { $usedBy[$Section][$Name] = @(@($tags) + $me | Sort-Object); $script:tagged += $Name }
}
foreach ($name in $cols.PSObject.Properties.Name) { Add-Tag 'trialColumns' $name }
foreach ($s in $ownStreams) { Add-Tag 'streams' $s }
$removed = @()
foreach ($section in $retired.Keys) {
    foreach ($name in $retired[$section]) {
        if (-not $usedBy[$section].Contains($name)) { $others = @() } else { $others = @($usedBy[$section][$name] | Where-Object { $_ -and $_ -ne $me }) }
        if ($others) {
            if (@($usedBy[$section][$name]) -contains $me) { $usedBy[$section][$name] = $others; $removed += "$me tag on $name" }
            continue
        }
        if ($usedBy[$section].Contains($name)) { $usedBy[$section].Remove($name) }
        if ($section -eq 'trialColumns' -and $tc.Contains($name)) { $tc.Remove($name); $removed += "column $name" }
        if ($section -eq 'streams' -and $streams -contains $name) { $streams = @($streams | Where-Object { $_ -ne $name }); $removed += "stream $name" }
    }
}
$body = [ordered]@{ streams = $streams; experimentSettings = $es; trialColumns = $tc; usedBy = $usedBy }
"would add $($added.Count): $($added -join ', ')"
"would tag $($tagged.Count) with ${me}: $($tagged -join ', ')"
"would remove $($removed.Count): $($removed -join ', ')"
"would describe $($described.Count): $($described -join ', ')"
if ($Apply -and ($added -or $tagged -or $removed -or $described)) {
    $what = @($(if ($added) { "added $($added -join ', ')" }), $(if ($tagged) { "tagged $($tagged -join ', ') with $me" }),
              $(if ($removed) { "removed $($removed -join ', ')" }), $(if ($described) { "described $($described -join ', ')" })) -join '; '
    $r = Save-NamesDictionary -Path $path -Body $body -Previous $prev -Was $was -By 'PC' -What "$what (av-dome pip/main-switchover)"
    "saved: $($r.Ok) $($r.Detail)"
}
