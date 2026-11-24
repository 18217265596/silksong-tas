[CmdletBinding()]
param(
    [string]$Stage = (Join-Path ([IO.Path]::GetTempPath()) 'SilksongRace'),
    [switch]$PrepareOnly,
    [Parameter(Mandatory)][string]$SteamGame,
    [Parameter(Mandatory)][string]$Slot2,
    [string]$ModProfile,
    [switch]$Visible
)

$ErrorActionPreference = 'Stop'
$base = Join-Path (Split-Path -Parent $PSScriptRoot) 'Silksong-TAS.ps1'
$options = @{ Stage = $Stage; SteamGame = $SteamGame; Slot2 = $Slot2; PrepareOnly = $true }
if ($ModProfile) { $options.ModProfile = $ModProfile }
& $base @options | Out-Null
Copy-Item -LiteralPath "$PSScriptRoot\routes\audit-reference.tsv","$PSScriptRoot\routes\choice-reference.tsv","$PSScriptRoot\routes\retime-opening.tsv" -Destination $Stage

$game=Join-Path $Stage 'Game'
$managed=Join-Path $game 'Hollow Knight Silksong_Data\Managed'
$core=Join-Path $game 'BepInEx\core'
$csc="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs=@("$core\BepInEx.dll","$core\0Harmony.dll")+@(
    'Assembly-CSharp.dll','UnityEngine.CoreModule.dll','UnityEngine.dll','UnityEngine.Physics2DModule.dll',
    'PlayMaker.dll','netstandard.dll','TeamCherry.SharedUtils.dll','Unity.Addressables.dll',
    'Unity.ResourceManager.dll','UnityEngine.ImageConversionModule.dll','UnityEngine.AudioModule.dll'
) | ForEach-Object { if([IO.Path]::IsPathRooted($_)){$_}else{Join-Path $managed $_} }

$moduleSource="$PSScriptRoot\SilkPracticeCloseOut.cs"
& $csc /nologo /target:library "/out:$game\BepInEx\plugins\SilkPractice.dll" ($refs|ForEach-Object{"/r:$_"}) $moduleSource
if($LASTEXITCODE -ne 0){throw 'Race module compile failed.'}
if($PrepareOnly){return $game}

$env:SILK_PRACTICE_DIR=$Stage
$log='"'+(Join-Path $Stage 'player.log')+'"'
Start-Process -FilePath "$game\Hollow Knight Silksong.exe" -WorkingDirectory $game -WindowStyle $(if ($Visible) { 'Normal' } else { 'Hidden' }) -PassThru -ArgumentList @(
    '-screen-width','960','-screen-height','540','-screen-fullscreen','0','-logFile',$log
) | Select-Object Id, ProcessName

