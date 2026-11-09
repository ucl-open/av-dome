param(
    # Internal: the step this script runs in a fresh process of its own (assemblies stay loaded).
    [ValidateSet('', 'Build', 'RigDevice', 'Checks', 'Audio')][string]$Step = '',
    [string]$Workflow = 'main.bonsai'
)
# Checks a change to src\ without the rig and without opening Bonsai: no window, no device.
#   1. Compiles src\Extensions under C# 5, the language Bonsai compiles it with.
#   2. Builds every workflow that reads rig.yml (main, CalibrateValve, CameraFrameMonitor,
#      ZapitTrialTest) with Bonsai's own expression builders, as Bonsai does at Start before it
#      subscribes anything: every type, selector, include, subject and mapping. Each is built
#      against config\rig-template.yml filled in as a rig's, with every node that reads rig.yml
#      pointed at that copy, and for main.bonsai what RecordHarpDevices made from it, what
#      ExperimentManifest expects, and that each window in config\main.layout (once there is one)
#      sits on a node it fits are checked.
#   3. Runs RigDevice on the template: every name main.bonsai asks for is there, a wrong name is
#      refused, and every key in the template reaches the Settings_Rig JSON the experiment logs.
#   4. Each config\task*.yml reads as the workflow reads it at Go, TaskPath is exposed and no
#      override is applied (rig-control writes the task file; lib-open-rigs settings-files plan).
#      InitialiseTrial draws trials from task-localization.yml and task-discrimination.yml (both target
#      modalities): the answer, silent trials, auto-reward only at the left spout, stage 0's spouts.
#   5. LoadAuditoryStimulusSet on synthetic WAVs and filters: equalising matches the filter's
#      convolution, and asymmetric taps, a wrong rate or a missing file are refused naming it;
#      InitialiseTrial refuses a flickerFrequency other than the set's 8 Hz.
# Needs the environment set up (.bonsai\Setup.ps1: .bonsai\Packages). Exit 0 when all pass.
$ErrorActionPreference = 'Stop'
$self = $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent (Split-Path -Parent $self)
$bin = Join-Path ([IO.Path]::GetTempPath()) 'av-dome-check'
$bonsaiDir = Join-Path $repo '.bonsai'
$workflows = 'main.bonsai', 'CalibrateValve.bonsai', 'CameraFrameMonitor.bonsai', 'ZapitTrialTest.bonsai'

function Initialize-Assemblies {
    [xml]$cfg = Get-Content (Join-Path $bonsaiDir 'Bonsai.config') -Raw
    $map = @{}
    foreach ($a in $cfg.PackageConfiguration.AssemblyLocations.AssemblyLocation) {
        if ($a.processorArchitecture -in 'MSIL', 'Amd64') { $map[$a.assemblyName] = Join-Path $bonsaiDir $a.location }
    }
    $map['Extensions'] = Join-Path $bin 'Extensions.dll'
    foreach ($n in 'YamlDotNet', 'Newtonsoft.Json') { $map[$n] = Join-Path $bin "$n.dll" }
    # Native libraries (OpenCV, libgit2) from the folders Bonsai.config names for x64.
    foreach ($f in $cfg.PackageConfiguration.LibraryFolders.LibraryFolder) {
        if ($f.platform -eq 'x64') { $env:PATH = (Join-Path $bonsaiDir $f.path) + ';' + $env:PATH }
    }
    $script:resolveMap = $map; $script:resolvePkgs = Join-Path $bonsaiDir 'Packages'
    $script:resolver = [ResolveEventHandler]{
        param($s, $e)
        $n = (New-Object Reflection.AssemblyName $e.Name).Name
        $loaded = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq $n } | Select-Object -First 1
        if ($loaded) { return $loaded }
        $p = $script:resolveMap[$n]
        if (-not $p) {
            $p = Get-ChildItem $script:resolvePkgs -Recurse -Filter "$n.dll" -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\lib\\net4' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
        if ($p -and (Test-Path $p)) { return [Reflection.Assembly]::LoadFrom($p) }
        return $null
    }
    [AppDomain]::CurrentDomain.add_AssemblyResolve($script:resolver)
    foreach ($n in 'Bonsai.Core', 'Bonsai', 'Extensions') { $null = [Reflection.Assembly]::LoadFrom($map[$n]) }
}

# The template filled in as a rig's, at <bin>\config\<MACHINE>\rig.yml, so the nodes that read
# rig.yml when the workflow is built make what they make on a rig; this machine need not have one.
function Write-FilledTemplate {
    $rigDir = Join-Path $bin "config\$([Environment]::MachineName)"
    $null = New-Item -ItemType Directory -Path $rigDir -Force
    $rigYml = Join-Path $rigDir 'rig.yml'
    $template = [IO.File]::ReadAllText((Join-Path $repo 'config\rig-template.yml'))
    $serials = 'BG01WA0E', 'AQ03WH45', 'AQ03WRBG', 'AQ03WW0D', 'E664C856C360B937', 'E664C856C31F4637'
    foreach ($s in $serials) { $template = ([regex]'usbSerial: CHANGE-ME').Replace($template, "usbSerial: $s", 1) }
    $template = $template.Replace('rigId: XX', 'rigId: D1').Replace('serialNumber: "00000000"', "serialNumber: '1'").Replace("rootPath: C:\Coen-Lab-Local\Acquired-Data", "rootPath: $bin").Replace('RigConfigs\CHANGE-ME\', "RigConfigs\$([Environment]::MachineName)\")
    foreach ($left in 'CHANGE-ME', 'rigId: XX', 'serialNumber: "00000000"', "rootPath: C:\Coen-Lab-Local\Acquired-Data") {
        if ($template.Contains($left)) { throw "check-workflow: the template still holds '$left' after filling; update Write-FilledTemplate." }
    }
    if (-not $template.Contains("rootPath: $bin")) { throw "check-workflow: config\rig-template.yml's rootPath is not C:\Coen-Lab-Local\Acquired-Data; update the Replace above." }
    [IO.File]::WriteAllText($rigYml, $template)
    return $rigYml
}

if ($Step -eq 'Build') {
    Initialize-Assemblies
    $workflowPath = Join-Path $repo "src\$Workflow"
    foreach ($m in [regex]::Matches((Get-Content $workflowPath -Raw), 'assembly=([A-Za-z0-9\.]+)"')) {
        $null = [Reflection.Assembly]::Load($m.Groups[1].Value)
    }
    # Bonsai opens a workflow from its own folder, and relative include paths are found from there.
    Set-Location (Split-Path $workflowPath); [Environment]::CurrentDirectory = (Split-Path $workflowPath)
    $reader = [Xml.XmlReader]::Create($workflowPath)
    try { $wb = [Bonsai.WorkflowBuilder]::Serializer.Deserialize($reader) } finally { $reader.Close() }
    $rigYml = Write-FilledTemplate
    $configRoot = Join-Path $bin 'config'
    # Every node's own copy of where rig.yml is (ResolveRigConfigDirectory, RecordHarpDevices,
    # StreamDevices; ExperimentManifest's ConfigFiles) is typed by hand into the workflow: they must
    # all be the same root, and each is pointed at the filled template for this build.
    $script:roots = New-Object System.Collections.Generic.List[string]
    function Redirect-ConfigRoots($graph) {
        foreach ($n in $graph) {
            $b = [Bonsai.Expressions.ExpressionBuilder]::Unwrap($n.Value)
            $owner = if ($b -is [Bonsai.Expressions.CombinatorBuilder]) { $b.Combinator } else { $b }
            # A node's inner Settings node mirrors its ConfigRoot and is already redirected with it.
            if ($owner -and $owner.GetType().Namespace -like 'OpenRigs.*' -and $owner.GetType().Name -notlike '*Settings' -and $owner.PSObject.Properties['ConfigRoot']) {
                $script:roots.Add("$($owner.GetType().Name)=$($owner.ConfigRoot)"); $owner.ConfigRoot = $configRoot
            }
            if ($b.GetType().Name -eq 'ExperimentManifest') {
                foreach ($file in $b.ConfigFiles -split ';') { if ($file -match '^(.*)\\%COMPUTERNAME%\\rig\.yml$') { $script:roots.Add("ExperimentManifest.ConfigFiles=$($Matches[1])") } }
                $b.ConfigFiles = $rigYml
            }
            if ($b -is [Bonsai.Expressions.IWorkflowExpressionBuilder] -and $b.Workflow) { Redirect-ConfigRoots $b.Workflow }
        }
    }
    Redirect-ConfigRoots $wb.Workflow
    $failed = 0
    $distinct = @($script:roots | ForEach-Object { ($_ -split '=', 2)[1].TrimEnd('\') } | Sort-Object -Unique)
    if ($script:roots.Count -ge 1 -and $distinct.Count -eq 1) { "  ok    every node reads rig.yml under the same root, $($distinct[0]) ($($script:roots.Count) nodes)" }
    else { "  FAIL  the nodes' rig.yml roots differ, or none was found: $($script:roots -join '; ')"; $failed++ }
    try {
        $built = [Bonsai.Expressions.ExpressionBuilderGraphExtensions]::Build($wb.Workflow)
    } catch {
        $x = $_.Exception
        while ($x) { $b = $null; try { $b = $x.Builder } catch {}; $nm = if ($b) { [Bonsai.Expressions.ExpressionBuilder]::GetElementDisplayName($b) } else { '' }; "  $($x.GetType().Name): $nm :: $($x.Message)"; $x = $x.InnerException }
        exit 1
    }
    "  ok    $Workflow builds"
    if ($Workflow -ne 'main.bonsai') { exit $(if ($failed) { 1 } else { 0 }) }
    # A Harp Device opens its port as it is subscribed, with whatever PortName it holds then, so each
    # device's settings must be complete before HardwareDevices subscribes. Bonsai connects a node
    # with more than one successor only after the whole workflow has subscribed, so a branch anywhere
    # upstream of a RigDevice in RigSetup reads rig.yml too late, and the board opens its design-time
    # port (4 Nov 2026 on GREEN-DOME-1: "The port 'COM16' does not exist").
    $rigSetup = $wb.Workflow | Where-Object { $b = [Bonsai.Expressions.ExpressionBuilder]::Unwrap($_.Value); $b -is [Bonsai.Expressions.GroupWorkflowBuilder] -and $b.Name -eq 'RigSetup' } | Select-Object -First 1
    if (-not $rigSetup) { "  FAIL  main.bonsai has no RigSetup group"; $failed++ }
    else {
        $g = ([Bonsai.Expressions.ExpressionBuilder]::Unwrap($rigSetup.Value)).Workflow
        $preds = @{}
        foreach ($n in $g) { foreach ($e in $n.Successors) { if (-not $preds.ContainsKey($e.Target)) { $preds[$e.Target] = @() }; $preds[$e.Target] += $n } }
        $branching = New-Object System.Collections.Generic.List[string]; $devices = 0
        foreach ($n in $g) {
            $b = [Bonsai.Expressions.ExpressionBuilder]::Unwrap($n.Value)
            if (-not ($b -is [Bonsai.Expressions.CombinatorBuilder] -and $b.Combinator.GetType().Name -eq 'RigDevice')) { continue }
            $devices++
            $todo = New-Object System.Collections.Generic.Queue[object]; $todo.Enqueue($n); $seen = @{}
            while ($todo.Count) {
                $c = $todo.Dequeue()
                foreach ($p in @($preds[$c])) {
                    if (-not $p -or $seen.ContainsKey($p)) { continue }; $seen[$p] = 1; $todo.Enqueue($p)
                    if ($p.Successors.Count -gt 1) { $branching.Add("$([Bonsai.Expressions.ExpressionBuilder]::GetElementDisplayName($p.Value)) (before $($b.Combinator.Name))") }
                }
            }
        }
        $branching = @($branching | Sort-Object -Unique)
        if ($devices -eq 9 -and $branching.Count -eq 0) { "  ok    RigSetup: none of the $devices RigDevice chains branches, so rig.yml is read before any board opens" }
        else { "  FAIL  RigSetup: $devices RigDevice node(s); branching upstream of one: $($branching -join '; ')"; $failed++ }
    }
    # What the build made, read from the objects in the built expression.
    Add-Type -ReferencedAssemblies System.Core -TypeDefinition @'
public class BuiltObjects : System.Linq.Expressions.ExpressionVisitor {
    public System.Collections.Generic.List<object> Found = new System.Collections.Generic.List<object>();
    protected override System.Linq.Expressions.Expression VisitConstant(System.Linq.Expressions.ConstantExpression node) {
        if (node.Value != null) Found.Add(node.Value);
        return base.VisitConstant(node);
    }
}
'@
    $objects = New-Object BuiltObjects
    $null = $objects.Visit($built)
    $records = @($objects.Found | Where-Object { $_.GetType().FullName -eq 'OpenRigs.HarpDevices.RecordHarpDevice' })
    $got = @($records | ForEach-Object { "$($_.Name) $($_.PortName) $($_.UsbSerial) $($_.ExpectedWhoAmI)" } | Sort-Object)
    # A Lickety Split reports WhoAmI 1400 (known since OpenRigs.HarpDevices 0.1.0-local.7), so it is checked.
    $want = @('BehaviorBoard-Main COM1 BG01WA0E 1216', 'LicketySplit-Left COM5 E664C856C360B937 1400', 'LicketySplit-Right COM6 E664C856C31F4637 1400', 'StepperDriver-A COM3 AQ03WRBG 1130', 'StepperDriver-B COM4 AQ03WW0D 1130', 'TimestampGeneratorGen3-Main COM2 AQ03WH45 1158')
    if (($got -join '; ') -eq ($want -join '; ')) { "  ok    RecordHarpDevices records the Behavior board, the timestamp generator, both stepper drivers and both Lickety Splits from rig.yml" }
    else { "  FAIL  RecordHarpDevices made: $($got -join '; ')"; $failed++ }
    $dumps = @($objects.Found | Where-Object { $_.GetType().FullName -eq 'OpenRigs.HarpDevices.RequestRegisterDump' }).Count
    if ($dumps -eq 6) { "  ok    ... and asks each for its dump on <Name>.Commands" } else { "  FAIL  $dumps dump request(s), not 6: is every board's command subject <Name>.Commands?"; $failed++ }
    $recorder = @($objects.Found | Where-Object { $_.GetType().Name -eq 'ManifestRecorder' })
    $planField = if ($recorder.Count) { $recorder[0].GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic,Public') | Where-Object { $_.FieldType.Name -eq 'ExpectedPlan' } | Select-Object -First 1 }
    $expected = if ($planField) { @($planField.GetValue($recorder[0]).Entries | ForEach-Object { $_.Path }) } else { @() }
    # A register map (<Name>.yml) for every board here: a loaded Harp package decodes each (the timestamp
    # generator's map is the lab's copy since OpenRigs.Manifest 0.1.0-local.16, as its package carries none).
    $wantFiles = 'BehaviorBoard-Main/', 'TimestampGeneratorGen3-Main/', 'StepperDriver-A/', 'StepperDriver-B/', 'LicketySplit-Left/', 'LicketySplit-Right/', 'Camera-Face/', 'Camera-Pupil/', 'Camera-Body/',
        'BehaviorBoard-Main/BehaviorBoard-Main_device.json', 'BehaviorBoard-Main/BehaviorBoard-Main.yml',
        'TimestampGeneratorGen3-Main/TimestampGeneratorGen3-Main_device.json', 'TimestampGeneratorGen3-Main/TimestampGeneratorGen3-Main.yml',
        'StepperDriver-A/StepperDriver-A_device.json', 'StepperDriver-A/StepperDriver-A.yml',
        'StepperDriver-B/StepperDriver-B_device.json', 'StepperDriver-B/StepperDriver-B.yml',
        'LicketySplit-Left/LicketySplit-Left_device.json', 'LicketySplit-Right/LicketySplit-Right_device.json',
        'LicketySplit-Left/LicketySplit-Left.yml', 'LicketySplit-Right/LicketySplit-Right.yml',
        'Settings/Settings_Rig_', 'Settings/Settings_Task_', 'Settings/Settings_Experiment_', 'BonsaiData/'
    $missing = @($wantFiles | Where-Object { $expected -notcontains $_ })
    $extra = @($expected | Where-Object { $wantFiles -notcontains $_ })
    if ($missing.Count -eq 0 -and $extra.Count -eq 0) { "  ok    ExperimentManifest expects the $($expected.Count) folders and files of an experiment" }
    else {
        "  FAIL  ExperimentManifest expects '$($expected -join ', ')'; missing: $($missing -join ', '); extra: $($extra -join ', ')"; $failed++
        if ($planField) { foreach ($note in $planField.GetValue($recorder[0]).Notes) { "        its note: $note" } }
    }
    # config\main.layout's windows, on the nodes as the editor has them (coen-behaviour-box's check):
    # Bonsai loads each group in topological order, not file order, so a node fed from later in the
    # file moves up and shifts the indices after it. A window on the wrong node makes the editor
    # offer to delete the layout, and --start waits. A visualizer type not in $wanted fails until it
    # is added with the output type it needs ('' for any).
    $layoutPath = Join-Path $repo 'config\main.layout'
    if (Test-Path $layoutPath) {
        $inspectable = [Bonsai.Expressions.ExpressionBuilderGraphExtensions]::ToInspectableGraph($wb.Workflow)
        $null = [Bonsai.Expressions.ExpressionBuilderGraphExtensions]::Build($inspectable)
        $wanted = @{ 'Bonsai.Vision.Design.IplImageVisualizer' = 'OpenCV.Net.IplImage'; 'Bonsai.Design.ObjectTextVisualizer' = '' }
        function Test-Layout($settings, $graph, $where) {
            $nodes = @($graph)
            foreach ($window in $settings.WindowSettings) {
                $i = [int]$window.Index
                if ($i -ge $nodes.Count) { "  FAIL  layout $where$i`: the workflow has $($nodes.Count) nodes there"; continue }
                $node = $nodes[$i].Value
                $at = "$where$i ($($node.Builder.GetType().Name))"
                if ($window.NestedLayout) {
                    if ($node.Builder -is [Bonsai.Expressions.IWorkflowExpressionBuilder]) { Test-Layout $window.NestedLayout $node.Builder.Workflow "$at > " }
                    else { "  FAIL  layout $at holds windows but is not a group" }
                }
                if ($window.VisualizerTypeName) {
                    $type = $wanted[$window.VisualizerTypeName]
                    if ($null -eq $type) { "  FAIL  layout $at`: $($window.VisualizerTypeName) is not one this check knows" }
                    elseif ($type -and "$($node.ObservableType)" -ne $type) { "  FAIL  layout $at`: $($window.VisualizerTypeName) on a node of $($node.ObservableType), not $type" }
                    else { "  ok    layout window $($window.VisualizerTypeName) on $at" }
                }
            }
        }
        [xml]$layout = Get-Content $layoutPath -Raw
        $layoutLines = @(Test-Layout $layout.VisualizerLayout $inspectable '')
        $layoutLines
        $failed += @($layoutLines | Where-Object { $_ -like '  FAIL*' }).Count
    } else { "  ok    no config\main.layout yet (BACKLOG.md); nothing to check" }
    exit $(if ($failed) { 1 } else { 0 })
}

if ($Step -eq 'RigDevice') {
    Initialize-Assemblies
    # A scriptblock resolver called from an Rx worker thread while this one waits deadlocks, so
    # everything is loaded now and the resolver removed before anything runs.
    foreach ($p in $script:resolveMap.Values) { if (Test-Path $p) { try { $null = [Reflection.Assembly]::LoadFrom($p) } catch {} } }
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($script:resolver)
    $yaml = [IO.File]::ReadAllText((Write-FilledTemplate))
    $E = [Linq.Expressions.Expression]
    $wait = [System.Reactive.Linq.Observable].GetMethods() | Where-Object { $_.Name -eq 'Wait' -and $_.GetParameters().Count -eq 1 } | Select-Object -First 1
    function New-RigYamlExpression {
        $x = $E::Constant([System.Reactive.Linq.Observable]::Return($yaml), [IObservable[string]])
        $des = New-Object UclOpenAvDome.DeserializeFromYaml
        $des.Type = [Activator]::CreateInstance([Bonsai.Expressions.TypeMapping`1].MakeGenericType([UclOpenAvDome.UclOpenAvDomeRig]))
        return $des.Build([Linq.Expressions.Expression[]]@($x))
    }
    # rig.yml text -> DeserializeFromYaml -> MemberSelector -> RigDevice, as RigSetup chains them.
    function Invoke-RigDevice([string]$Member, [string]$Name) {
        $x = New-RigYamlExpression
        $sel = New-Object Bonsai.Expressions.MemberSelectorBuilder; $sel.Selector = $Member
        $x = $sel.Build([Linq.Expressions.Expression[]]@($x))
        $cb = New-Object Bonsai.Expressions.CombinatorBuilder
        $rd = New-Object UclOpenAvDome.RigDevice; $rd.Name = $Name; $cb.Combinator = $rd
        $x = $cb.Build([Linq.Expressions.Expression[]]@($x))
        $call = $E::Call($wait.MakeGenericMethod($x.Type.GetGenericArguments()[0]), $x)
        return $E::Lambda($E::Convert($call, [object])).Compile().DynamicInvoke()
    }
    $failed = 0
    # The names main.bonsai asks for; each reads the rig.yml group of its kind (Camera-Face reads Camera).
    $asked = [regex]::Matches((Get-Content (Join-Path $repo 'src\main.bonsai') -Raw), '<Combinator xsi:type="p2:RigDevice">\s*<p2:Name>([^<]*)</p2:Name>')
    if ($asked.Count -ne 9) { "  FAIL  found $($asked.Count) RigDevice nodes in main.bonsai, not 9"; $failed++ }
    foreach ($m in $asked) {
        $name = $m.Groups[1].Value; $member = ($name -split '-')[0]
        try {
            $r = Invoke-RigDevice $member $name
            if ($r.Name -ne $name) { throw "came back named '$($r.Name)'" }
            "  ok    $name is in the template"
        } catch { $x = $_.Exception; while ($x.InnerException) { $x = $x.InnerException }; "  FAIL  ${name}: $($x.Message)"; $failed++ }
    }
    foreach ($c in @(@('Camera', 'Camera-Box', 'KeyNotFoundException', 'Its camera entries: body, face, pupil.'), @('Camera', 'CameraFace', 'ArgumentException', 'Kind-Instance'))) {
        try { $null = Invoke-RigDevice $c[0] $c[1]; "  FAIL  $($c[1]) was accepted"; $failed++ }
        catch {
            $x = $_.Exception; while ($x.InnerException) { $x = $x.InnerException }
            if ($x.GetType().Name -eq $c[2] -and $x.Message.Contains($c[3])) { "  ok    $($c[1]) refused: $($x.Message)" }
            else { "  FAIL  $($c[1]): $($x.GetType().Name): $($x.Message)"; $failed++ }
        }
    }
    # Each side's reward line is the one rig.yml's rewardValves names (LoadSideRewardLine), never
    # assumed: swapped in rig.yml, swapped in the workflow; one line for both, the Zapit TTL's line,
    # or no rewardValves at all is refused at startup.
    $x = New-RigYamlExpression
    $readRig = { $E::Lambda($E::Convert($E::Call($wait.MakeGenericMethod([UclOpenAvDome.UclOpenAvDomeRig]), $x), [object])).Compile().DynamicInvoke() }
    $sides = { param($r) (@([RewardSide]::Left, [RewardSide]::Right) | ForEach-Object { try { 'DO' + [LoadSideRewardLine]::LineOf($r, $_) } catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; 'refused: ' + $e.Message } }) -join ' / ' }
    $rig = & $readRig
    $got = & $sides $rig
    if ($got -eq 'DO1 / DO2') { "  ok    rewardValves: left DO1, right DO2 as the template says" } else { "  FAIL  rewardValves read as $got"; $failed++ }
    $rig.RewardValves.Left = [UclOpenAvDome.ValveLine]::DO2; $rig.RewardValves.Right = [UclOpenAvDome.ValveLine]::DO1
    $got = & $sides $rig
    if ($got -eq 'DO2 / DO1') { "  ok    rewardValves swapped: left DO2, right DO1" } else { "  FAIL  swapped rewardValves read as $got"; $failed++ }
    foreach ($c in @(@('both DO1', { $rig.RewardValves.Right = [UclOpenAvDome.ValveLine]::DO1; $rig.RewardValves.Left = [UclOpenAvDome.ValveLine]::DO1 }, 'to both sides'),
                     @('left DO3, the Zapit line', { $rig.RewardValves.Left = [UclOpenAvDome.ValveLine]::DO3; $rig.RewardValves.Right = [UclOpenAvDome.ValveLine]::DO2 }, 'zapit.triggerLine'),
                     @('no rewardValves', { $rig.RewardValves = $null }, 'rig.yml has no rewardValves'))) {
        & $c[1]
        $got = & $sides $rig
        if ($got -like "refused: *$($c[2])*") { "  ok    rewardValves $($c[0]) refused: $(($got -split ' / ')[0].Substring(9))" } else { "  FAIL  rewardValves $($c[0]) read as $got"; $failed++ }
    }
    # Each reward's RewardDelivery row names its side and the line it fired on, here swapped.
    $d = [PlanReward]::Plan(1, 4, [RewardLine]::new(2, 40), [RewardLine]::new(1, 40), 50)
    if ("$($d.Side) DO$($d.Line)" -eq 'Left DO2') { "  ok    a left reward on swapped lines logs Side Left, Line 2" } else { "  FAIL  a left reward on swapped lines logged $($d.Side) DO$($d.Line)"; $failed++ }
    # Each valve calibration snapshot is named by its line and takes that line's curve, whichever side has it.
    $snaps = [regex]::Matches((Get-Content (Join-Path $repo 'src\main.bonsai') -Raw), 'Calibrated &amp;&amp; Line == (\d)</scr:Expression>(?:(?!<Expression xsi:type="rx:Condition">)[\s\S])*?<Name>ValveCalibration(\w+)</Name>')
    $pairs = ($snaps | ForEach-Object { "DO$($_.Groups[1].Value)=$($_.Groups[2].Value)" }) -join ' '
    if ($pairs -eq 'DO1=DO1 DO2=DO2') { "  ok    ValveCalibrationDO1/DO2 each take their own line's curve" } else { "  FAIL  valve calibration snapshots: '$pairs', not 'DO1=DO1 DO2=DO2'"; $failed++ }
    # The experiment's Settings_Rig JSON is rig.yml as parsed, and the manifest keeps only rig.yml's hash,
    # so every key in rig.yml must reach the JSON.
    try {
        $x = New-RigYamlExpression
        $cb = New-Object Bonsai.Expressions.CombinatorBuilder
        $ser = New-Object UclOpenAvDome.SerializeToJson; $ser.Formatting = 'Indented'; $cb.Combinator = $ser
        $x = $cb.Build([Linq.Expressions.Expression[]]@($x))
        $json = $E::Lambda($E::Call($wait.MakeGenericMethod([string]), $x)).Compile().DynamicInvoke()
        $jsonKeys = New-Object System.Collections.Generic.List[string]
        foreach ($t in [Newtonsoft.Json.Linq.JToken]::Parse($json).Descendants()) {
            if ($t -is [Newtonsoft.Json.Linq.JProperty]) { $jsonKeys.Add(($t.Path -replace '\[\d+\]', '[]')) }
        }
        $yamlKeys = New-Object System.Collections.Generic.List[string]
        function Add-YamlKeys($node, [string]$at) {
            if ($node -is [System.Collections.IDictionary]) { foreach ($k in $node.Keys) { $p = if ($at) { "$at.$k" } else { "$k" }; $yamlKeys.Add($p); Add-YamlKeys $node[$k] $p } }
            elseif ($node -is [System.Collections.IList]) { foreach ($i in $node) { Add-YamlKeys $i "$at[]" } }
        }
        Add-YamlKeys ((New-Object YamlDotNet.Serialization.Deserializer).Deserialize((New-Object IO.StringReader $yaml))) ''
        $lost = @($yamlKeys | Where-Object { $jsonKeys -notcontains $_ })
        if ($yamlKeys.Count -gt 0 -and $lost.Count -eq 0) { "  ok    Settings_Rig JSON keeps all $($yamlKeys.Count) keys of rig-template.yml" }
        else { "  FAIL  Settings_Rig JSON lost $($lost.Count) of $($yamlKeys.Count) keys of rig-template.yml: $($lost -join ', ')"; $failed++ }
    } catch { $x = $_.Exception; while ($x.InnerException) { $x = $x.InnerException }; "  FAIL  Settings_Rig JSON: $($x.Message)"; $failed++ }
    exit $(if ($failed) { 1 } else { 0 })
}

if ($Step -eq 'Checks') {
    Initialize-Assemblies
    $failed = 0
    # Each task file as the workflow reads it at Go (UclOpenAvDomeTask's DeserializeFromYaml: no
    # unknown key), rig-control's copy of it being the same file with values changed. And TaskPath
    # is the launch property rig-control sets, the task file no parameter.
    $null = [Reflection.Assembly]::LoadFrom($script:resolveMap['YamlDotNet'])
    foreach ($file in Get-ChildItem (Join-Path $repo 'config') -Filter 'task*.yml') {
        try {
            # The workflow's builder, but for its date converter, which PowerShell cannot construct: a task file holds no dates.
            $d = (New-Object YamlDotNet.Serialization.DeserializerBuilder).Build()
            $c = $d.Deserialize((New-Object IO.StringReader ([IO.File]::ReadAllText($file.FullName))), [UclOpenAvDomeTask.UclOpenAvDomeTaskLogic])
            if ($c.TaskParameters.TrainingStages.Count -gt 0) { "  ok    config\$($file.Name) reads as the workflow reads it at Go" }
            else { "  FAIL  config\$($file.Name) has no trainingStages"; $failed++ }
        } catch { $x = $_.Exception; while ($x.InnerException) { $x = $x.InnerException }; "  FAIL  config\$($file.Name): $($x.Message)"; $failed++ }
    }
    # Trials drawn by InitialiseTrial from the committed task files: what each task answers,
    # which trials are silent, where auto-reward may go and whether the spouts move.
    $rx = 'System.Reactive.Interfaces', 'System.Reactive.Core', 'System.Reactive.Linq' | ForEach-Object { $script:resolveMap[$_] }
    Add-Type -ReferencedAssemblies (@($script:resolveMap['Extensions'], 'System.Core') + $rx) -TypeDefinition @'
using System; using System.Collections.Generic; using System.Linq; using System.Reactive.Linq;
public static class DrawTrials {
    public static List<Trial> Draw(UclOpenAvDomeTask.UclOpenAvDomeTaskLogic task, int stage, int count) {
        var draw = new InitialiseTrial { trainingStageId = stage, TrialType = -1 };
        return draw.Process(Enumerable.Repeat(Tuple.Create("Correct", task), count).ToObservable()).ToEnumerable().ToList();
    }
}
'@
    function Read-Task([string]$Name) {
        $d = (New-Object YamlDotNet.Serialization.DeserializerBuilder).Build()
        $t = $d.Deserialize((New-Object IO.StringReader ([IO.File]::ReadAllText((Join-Path $repo "config\$Name")))), [UclOpenAvDomeTask.UclOpenAvDomeTaskLogic])
        $t.TaskParameters.Optogenetics.Enabled = $false  # no Zapit config on this machine
        $t
    }
    function Test-Trials([string]$What, $Trials, [scriptblock]$Rule) {
        $bad = @($Trials | Where-Object { -not (& $Rule $_) })
        if ($bad.Count -eq 0) { "  ok    $What" } else { "  FAIL  $What ($($bad.Count) of $($Trials.Count) trials, first: type $($bad[0].trialType) target $($bad[0].targetSide))"; $script:failed++ }
    }
    try {
        $loc = [DrawTrials]::Draw((Read-Task 'task-localization.yml'), 4, 300)
        Test-Trials 'localization: every trial plays sound, moves the spouts and answers the stimulus side' $loc {
            param($t) $t.taskType -eq 'localization' -and $t.audioEnabled -and $t.spoutMovements -and
                ($t.trialType -ne 0 -or $t.visualAzimuth -eq 0 -or $t.targetSide -eq $(if ($t.visualAzimuth -gt 0) { 'Right' } else { 'Left' })) }
        foreach ($modality in 'Visual', 'Auditory') {
            $task = Read-Task 'task-discrimination.yml'
            $task.TaskParameters.TargetModality = [UclOpenAvDomeTask.Modality]$modality
            $visualTarget = $modality -eq 'Visual'
            $all = [DrawTrials]::Draw($task, 3, 300)
            Test-Trials "discrimination, $($modality.ToLower()) target: target trials answer left, distractors right, by modality" $all {
                param($t) ($t.trialType -ne 0 -or ($t.targetSide -eq 'Left' -and $t.audioEnabled -ne $visualTarget -and ($t.visualContrast -gt 0) -eq $visualTarget)) -and
                    ($t.trialType -ne 1 -or ($t.targetSide -eq 'Right' -and $t.audioEnabled -eq $visualTarget -and ($t.visualContrast -gt 0) -ne $visualTarget)) }
            Test-Trials "discrimination, $($modality.ToLower()) target: neutral trials are silent" $all { param($t) $t.trialType -ne 4 -or -not $t.audioEnabled }
            $habituation = [DrawTrials]::Draw($task, 0, 50)
            Test-Trials "discrimination, $($modality.ToLower()) target: stage 0 auto-rewards every trial at the left spout and keeps the spouts in" $habituation {
                param($t) $t.autoReward -and $t.targetSide -eq 'Left' -and -not $t.spoutMovements }
            $task.TaskParameters.TrainingStages[2].AutoRewardProbability = 1.0
            Test-Trials "discrimination, $($modality.ToLower()) target: auto-reward never goes to the right spout" ([DrawTrials]::Draw($task, 2, 300)) {
                param($t) $t.autoReward -eq ($t.targetSide -eq 'Left') }
        }
    } catch { $x = $_.Exception; while ($x.InnerException) { $x = $x.InnerException }; "  FAIL  drawing trials: $($x.Message)"; $failed++ }
    $wf = Get-Content (Join-Path $repo 'src\main.bonsai') -Raw
    if ($wf -match '<Property Name="TaskPath"' -and $wf -notmatch 'ApplyTaskOverrides|<Name>TaskFile</Name>') { "  ok    main.bonsai takes its task file from TaskPath, with no overrides" }
    else { "  FAIL  main.bonsai does not expose TaskPath, or still applies overrides or names a TaskFile parameter"; $failed++ }
    $wf = Get-Content (Join-Path $repo 'src\main.bonsai') -Raw
    if ($wf -match 'C:\\RigConfigs\\LickSpoutPositions') { "  FAIL  main.bonsai still names production's C:\RigConfigs\LickSpoutPositions"; $failed++ }
    else { "  ok    main.bonsai keeps spout positions under the rig's own config directory" }
    exit $(if ($failed) { 1 } else { 0 })
}

if ($Step -eq 'Audio') {
    Initialize-Assemblies
    $failed = 0
    $rig = Join-Path $bin 'audio-check'
    Remove-Item $rig -Recurse -Force -ErrorAction SilentlyContinue
    $stimDir = Join-Path $rig 'calibration\AuditoryStimuli'
    $null = New-Item -ItemType Directory -Path $stimDir -Force
    function Write-Wave([string]$Path, [int16[]]$Samples, [int]$Rate) {
        $w = New-Object IO.BinaryWriter ([IO.File]::Create($Path))
        $w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + 2 * $Samples.Length)); $w.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
        $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]$Rate); $w.Write([int]($Rate * 2)); $w.Write([int16]2); $w.Write([int16]16)
        $w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int](2 * $Samples.Length)); foreach ($s in $Samples) { $w.Write($s) }
        $w.Close()
    }
    function Write-Filter([string]$Name, [double[]]$Taps, [double]$Rate) {
        $json = '{"calibratedAt":"2026-11-04T00:00:00Z","calibratedBy":"check-workflow","machineName":"","notes":"","sampleRate":' + $Rate + ',"taps":[' + (($Taps | ForEach-Object { $_.ToString('R', [Globalization.CultureInfo]::InvariantCulture) }) -join ',') + ']}'
        [IO.File]::WriteAllText((Join-Path $rig "calibration\$Name.json"), $json)
    }
    $rand = New-Object Random 1
    $waves = @{}
    foreach ($i in 0..99) {
        $s = [int16[]](1..480 | ForEach-Object { $rand.Next(-32768, 32768) }); $waves[$i] = $s
        Write-Wave (Join-Path $stimDir "filtered_pink_pulse_train_$i.wav") $s 48000
    }
    $sym = [double[]](0.01, -0.05, 0.2, 1.1, 0.2, -0.05, 0.01)
    Write-Filter 'sym' $sym 48000
    Write-Filter 'asym' ([double[]](1, 0.5, 0.25)) 48000
    Write-Filter 'rate' $sym 44100
    function New-Speakers([string]$Filter) {
        $a = New-Object UclOpenAvDome.SpeakerArray; $a.Device.SampleRate = 48000
        $a.Speakers = New-Object 'System.Collections.Generic.Dictionary[string,UclOpenAvDome.Speaker]'
        $l = New-Object UclOpenAvDome.Speaker; $l.Channel = 0; $l.Filter = $Filter; $a.Speakers.Add('left', $l)
        $c = New-Object UclOpenAvDome.Speaker; $c.Channel = 2; $c.Filter = 'calibration/sym.json'; $a.Speakers.Add('centre', $c)
        return $a
    }
    function Get-Refusal([scriptblock]$Run) {
        try { & $Run; return $null } catch { $x = $_.Exception; while ($x.InnerException) { $x = $x.InnerException }; return $x }
    }
    function Invoke-Load($Speakers) { [AuditoryStimulusSet]::Load($Speakers, $rig, 'calibration\AuditoryStimuli', 'filtered_pink_pulse_train_') }
    [Console]::SetOut([IO.TextWriter]::Null)  # Load's warnings
    try {
        $set = Invoke-Load (New-Speakers $null)
        # Channel 2's waveform 7 against the filter by hand: centred, zero outside, scaled as production's Rescale.
        $x = $waves[7] | ForEach-Object { ($_ + 32768.0) * (2.0 / 65535.0) - 1.0 }
        $m = $set.Stimulus(7, 2); $got = New-Object float[] $m.Cols; [Runtime.InteropServices.Marshal]::Copy($m.Data, $got, 0, $m.Cols)
        $worst = 0.0
        for ($n = 0; $n -lt $x.Count; $n++) {
            $want = 0.0; for ($j = 0; $j -lt $sym.Length; $j++) { $k = $n + $j - 3; if ($k -ge 0 -and $k -lt $x.Count) { $want += $sym[$j] * $x[$k] } }
            $worst = [Math]::Max($worst, [Math]::Abs($want - $got[$n]))
        }
        $raw = $set.Stimulus(7, 0); $rawGot = New-Object float[] $raw.Cols; [Runtime.InteropServices.Marshal]::Copy($raw.Data, $rawGot, 0, $raw.Cols)
        $rawWorst = 0.0; for ($n = 0; $n -lt $x.Count; $n++) { $rawWorst = [Math]::Max($rawWorst, [Math]::Abs($x[$n] - $rawGot[$n])) }
        if ($m.Cols -eq 480 -and $worst -lt 1e-5 -and $rawWorst -lt 1e-6 -and $set.WaveformLength -eq 480) { $r = "  ok    LoadAuditoryStimulusSet equalises as the filter's convolution, centred, and leaves an unfiltered speaker as read" }
        else { $r = "  FAIL  equalised error $worst, unfiltered error $rawWorst, length $($m.Cols)"; $failed++ }
    } catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; $r = "  FAIL  LoadAuditoryStimulusSet: $($e.Message)"; $failed++ }
    $cases = @(
        @('asymmetric taps', { Invoke-Load (New-Speakers 'calibration/asym.json') }, 'not symmetric'),
        @('a filter for 44100 Hz', { Invoke-Load (New-Speakers 'calibration/rate.json') }, 'designed for 44100 Hz'),
        @('a missing filter', { Invoke-Load (New-Speakers 'calibration/none.json') }, 'filter not found'),
        @('a stimulus at 44100 Hz', { Write-Wave (Join-Path $stimDir 'filtered_pink_pulse_train_5.wav') $waves[5] 44100; Invoke-Load (New-Speakers $null) }, 'filtered_pink_pulse_train_5.wav'),
        @('a missing stimulus', { Remove-Item (Join-Path $stimDir 'filtered_pink_pulse_train_42.wav'); Invoke-Load (New-Speakers $null) }, 'filtered_pink_pulse_train_42.wav'))
    $lines = @($r)
    foreach ($case in $cases) {
        $e = Get-Refusal $case[1]
        if ($e -and $e.Message.Contains($case[2])) { $lines += "  ok    LoadAuditoryStimulusSet refuses $($case[0])" }
        else { $lines += "  FAIL  LoadAuditoryStimulusSet on $($case[0]): $(if ($e) { $e.Message } else { 'accepted' })"; $failed++ }
    }
    # InitialiseTrial's first-trial check: flickerFrequency must be the set's pulse rate.
    $p = New-Object UclOpenAvDomeTask.UclOpenAvDomeTaskParameters
    $p.VisualStimulus = New-Object UclOpenAvDomeTask.VisualStimulus
    $p.AudioStimulus = New-Object UclOpenAvDomeTask.AudioStimulus
    $p.AudioStimulus.SpeakerLocations = New-Object 'System.Collections.Generic.List[UclOpenAvDomeTask.SphericalPosition]'
    $check = [InitialiseTrial].GetMethod('EnsureConfigChecked', [Reflection.BindingFlags]'Instance,NonPublic')
    $p.FlickerFrequency = 4
    $e = Get-Refusal { $null = $check.Invoke((New-Object InitialiseTrial), [object[]]@($p.PSObject.BaseObject)) }
    $p.FlickerFrequency = 8
    $e8 = Get-Refusal { $null = $check.Invoke((New-Object InitialiseTrial), [object[]]@($p.PSObject.BaseObject)) }
    if ($e -and $e.Message.Contains('flickerFrequency is 4') -and -not $e8) { $lines += "  ok    InitialiseTrial refuses a flickerFrequency other than the stimulus set's 8 Hz" }
    else { $lines += "  FAIL  flickerFrequency check: 4 Hz $(if ($e) { $e.Message } else { 'accepted' }); 8 Hz $(if ($e8) { $e8.Message } else { 'accepted' })"; $failed++ }
    [Console]::SetOut((New-Object IO.StreamWriter ([Console]::OpenStandardOutput()) -Property @{ AutoFlush = $true }))
    $lines
    exit $(if ($failed) { 1 } else { 0 })
}

if (-not (Test-Path (Join-Path $bonsaiDir 'Packages'))) { throw "No .bonsai\Packages in $repo. Run .bonsai\Setup.ps1 first." }
$bad = 0
"1. Compiling src\Extensions as C# 5..."
Remove-Item $bin -Recurse -Force -ErrorAction SilentlyContinue
# Bonsai compiles the Extensions folder beside each workflow with that folder's own project, so a
# folder under src with a project of its own is not part of this build (GammaCalibrationFromBonvision
# needs MathNet, which src's project does not list). Quoted as \"...\" for Windows PowerShell 5.1.
$src = Join-Path $repo 'src'
$nested = @(Get-ChildItem $src -Recurse -Filter *.csproj | Where-Object { $_.DirectoryName -ne $src } |
    ForEach-Object { ($_.DirectoryName.Substring($src.Length + 1) -replace '\\', '/') + '/**' } | Select-Object -Unique)
$exclude = '-p:DefaultItemExcludesInProjectFolder=\"' + ((@('**/.*/**') + $nested) -join ';') + '\"'
$log = & { $ErrorActionPreference = 'Continue'; & dotnet build (Join-Path $repo 'src\Extensions.csproj') -o $bin -nologo -v q -p:LangVersion=5 $exclude `
    --source https://api.nuget.org/v3/index.json --source (Join-Path $repo 'local_packages') 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { $log | Where-Object { $_ -match 'error' } | Select-Object -Unique | ForEach-Object { "  $_" }; "  FAILED"; exit 1 }
"  ok"
"2. Building each workflow that reads rig.yml with Bonsai's builders, against the filled template..."
foreach ($w in $workflows) {
    & { $ErrorActionPreference = 'Continue'; & powershell -NoProfile -ExecutionPolicy Bypass -File $self -Step Build -Workflow $w 2>&1 | ForEach-Object { "$_" } }
    if ($LASTEXITCODE -ne 0) { "  FAILED  $w"; $bad++ }
}
"3. RigDevice and Settings_Rig on config\rig-template.yml..."
& { $ErrorActionPreference = 'Continue'; & powershell -NoProfile -ExecutionPolicy Bypass -File $self -Step RigDevice 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { "  FAILED"; $bad++ }
"4. The task files, TaskPath and the spout positions path..."
& { $ErrorActionPreference = 'Continue'; & powershell -NoProfile -ExecutionPolicy Bypass -File $self -Step Checks 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { "  FAILED"; $bad++ }
"5. The auditory stimulus set and the flickerFrequency check, on synthetic files..."
& { $ErrorActionPreference = 'Continue'; & powershell -NoProfile -ExecutionPolicy Bypass -File $self -Step Audio 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { "  FAILED"; $bad++ }
if ($bad) { "check-workflow: $bad step(s) failed."; exit 1 }
"check-workflow: all 5 steps passed."
