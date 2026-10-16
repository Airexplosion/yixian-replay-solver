$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    dotnet publish Yx.SolverHost -c Release -r linux-x64 --self-contained true -o solver-publish/linux-x64
    if($LASTEXITCODE -ne 0){throw 'Solver publish failed'}
    foreach($taskDataName in @('battle_config.json','card_effects.json','catalog.json')){
        if(!(Test-Path -LiteralPath (Join-Path 'solver-data' $taskDataName))){throw ('Missing local deployment data: '+$taskDataName)}
    }
    Write-Output 'Self-contained Linux host: solver-publish/linux-x64'
    Write-Output 'Deploy the host and solver-data separately; never commit local game data.'
} finally { Pop-Location }
