# Project root = folder containing this script
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Paths
$VenvActivate = Join-Path $Root ".venv\Scripts\Activate.ps1"
$ExperimentPy = Join-Path $Root "src\ucl_open_av_dome\experiment.py"
$SchemaJson   = Join-Path $Root "AvDome.json"
$Extensions   = Join-Path $Root "src\Extensions"

& $VenvActivate

Write-Host "Running experiment.py..."
python $ExperimentPy

Write-Host "Running bonsai.sgen..."
dotnet bonsai.sgen $SchemaJson -o $Extensions --serializer yaml
