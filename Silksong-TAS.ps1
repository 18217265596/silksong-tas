[CmdletBinding(DefaultParameterSetName = 'Launch')]
param(
    [Parameter(ParameterSetName = 'Launch')][switch]$PrepareOnly,
    [Parameter(ParameterSetName = 'Launch')][switch]$Visible,
    [Parameter(ParameterSetName = 'Control', Mandatory)][string]$Command,
    [Parameter(ParameterSetName = 'Control')][string]$InputFile,
    [Parameter(ParameterSetName = 'Launch', Mandatory)][string]$SteamGame,
    [Parameter(ParameterSetName = 'Launch', Mandatory)][string]$Slot2,
    [Parameter(ParameterSetName = 'Launch')][string]$ModProfile = (Join-Path $env:APPDATA 'r2modmanPlus-local\HollowKnightSilksong\profiles\Default'),
    [string]$Stage = (Join-Path ([IO.Path]::GetTempPath()) 'SilksongTAS')
)

$ErrorActionPreference = 'Stop'
$game = Join-Path $Stage 'Game'
$exe = Join-Path $game 'Hollow Knight Silksong.exe'
$managed = Join-Path $game 'Hollow Knight Silksong_Data\Managed'
$core = Join-Path $game 'BepInEx\core'
$practice = Join-Path $Stage 'Practice'

if ($PSCmdlet.ParameterSetName -eq 'Control') {
    $commandPath = Join-Path $Stage 'command.txt'
    if (Test-Path -LiteralPath $commandPath) { throw 'The practice process has an unread command.' }
    if ($InputFile) { Copy-Item -LiteralPath $InputFile -Destination (Join-Path $Stage 'inputs.txt') }
    $pending = Join-Path $Stage 'command.pending'
    $Command | Set-Content -LiteralPath $pending -Encoding ascii
    Move-Item -LiteralPath $pending -Destination $commandPath
    return
}

$steamGame = (Resolve-Path -LiteralPath $SteamGame).Path
$Slot2 = (Resolve-Path -LiteralPath $Slot2).Path
$sourceDll = Join-Path $steamGame 'Hollow Knight Silksong_Data\Managed\Assembly-CSharp.dll'
$exePath = [IO.Path]::GetFullPath($exe)
if (Get-Process -Name 'Hollow Knight Silksong' -ErrorAction SilentlyContinue | Where-Object Path -eq $exePath) {
    throw 'This TAS instance is running. Send the quit command before rebuilding.'
}

New-Item -ItemType Directory -Path "$game\BepInEx\plugins","$practice\default" -Force | Out-Null
Copy-Item -LiteralPath $Slot2 -Destination "$practice\default\user2.dat" -Force
$shared = Join-Path (Split-Path -Parent $Slot2) 'shared.dat'
if (Test-Path -LiteralPath $shared) { Copy-Item -LiteralPath $shared -Destination "$practice\default" -Force }

Get-ChildItem -LiteralPath $steamGame -File -Recurse | ForEach-Object {
    $relative = $_.FullName.Substring($steamGame.Length + 1)
    if ($relative -match '^RandomizerModData\\' -or $_.Extension -in '.log', '.dmp' -or
        $_.Name -in 'Assembly-CSharp.dll','winhttp.dll','doorstop_config.ini','steam_appid.txt',
        '.doorstop_version','libdoorstop.so','libdoorstop.dylib','steam_api64.dll') { return }
    $dest = Join-Path $game $relative
    if (!(Test-Path -LiteralPath $dest)) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest)) | Out-Null
        if ([IO.Path]::GetPathRoot($dest) -eq [IO.Path]::GetPathRoot($_.FullName)) {
            New-Item -ItemType HardLink -Path $dest -Target $_.FullName | Out-Null
        } else {
            Copy-Item -LiteralPath $_.FullName -Destination $dest
        }
    }
}
Copy-Item -Path "$modProfile\BepInEx\core\*" -Destination ([IO.Directory]::CreateDirectory($core).FullName) -Recurse -Force
Copy-Item -LiteralPath "$modProfile\winhttp.dll" -Destination $game -Force
Copy-Item -LiteralPath "$steamGame\doorstop_config.ini" -Destination $game -Force

# Redirect game-level reads and saves before loading the Steam slot copy.
[void][Reflection.Assembly]::LoadFrom("$core\Mono.Cecil.dll")
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($managed)
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $resolver
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($sourceDll, $reader)
$redirects = 0
try {
    foreach ($type in $assembly.MainModule.GetTypes()) {
        foreach ($method in $type.Methods) {
            if (!$method.HasBody) { continue }
            foreach ($il in $method.Body.Instructions) {
                if ($il.OpCode.Code -ne 'Call' -or $il.Operand.Name -ne 'get_persistentDataPath' -or
                    $il.Operand.DeclaringType.FullName -ne 'UnityEngine.Application') { continue }
                $il.OpCode = [Mono.Cecil.Cil.OpCodes]::Ldstr
                $il.Operand = [IO.Path]::GetFullPath($practice)
                $redirects++
            }
        }
    }
    if ($redirects -ne 5) { throw "This Steam build has $redirects save-path call sites. Expected 5 for 1.0.30000." }
    $assembly.Write("$managed\Assembly-CSharp.practice")
} finally {
    $assembly.Dispose()
    $resolver.Dispose()
}
Move-Item -LiteralPath "$managed\Assembly-CSharp.practice" -Destination "$managed\Assembly-CSharp.dll" -Force

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = @("$core\BepInEx.dll", "$core\0Harmony.dll") + @(
    'Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.dll', 'UnityEngine.Physics2DModule.dll',
    'PlayMaker.dll', 'netstandard.dll', 'TeamCherry.SharedUtils.dll', 'Unity.Addressables.dll', 'Unity.ResourceManager.dll'
) | ForEach-Object { if ([IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $managed $_ } }
& $csc /nologo /target:library "/out:$game\BepInEx\plugins\SilkPractice.dll" ($refs | ForEach-Object { "/r:$_" }) "$PSScriptRoot\SilkPractice.cs"
if ($LASTEXITCODE -ne 0) { throw 'The TAS module did not compile.' }

if ($PrepareOnly) { return $exe }
$env:SILK_PRACTICE_DIR = $Stage
$window = if ($Visible) { 'Normal' } else { 'Hidden' }
$logArg = '"' + (Join-Path $Stage 'player.log') + '"'
Start-Process -FilePath $exe -WorkingDirectory $game -WindowStyle $window -PassThru -ArgumentList @(
    '-screen-width','960','-screen-height','540','-screen-fullscreen','0','-logFile',$logArg
) | Select-Object Id, ProcessName
