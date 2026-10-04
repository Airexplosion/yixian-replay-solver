$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
dotnet build (Join-Path $taskRoot 'Yx.WebSim/Yx.WebSim.csproj') -c Release -v minimal
if($LASTEXITCODE -ne 0){throw 'WASM build failed'}
$taskBundle=Join-Path $taskRoot 'Yx.WebSim/bin/Release/net8.0/browser-wasm/AppBundle'
$taskDist=Join-Path $taskRoot 'dist'
New-Item -ItemType Directory -Path $taskDist -Force | Out-Null
Copy-Item (Join-Path $taskBundle '_framework') $taskDist -Recurse -Force
Copy-Item (Join-Path $taskRoot 'Yx.WebSim/wwwroot/*') $taskDist -Recurse -Force
foreach($taskHtmlName in @('index.html','ladder.html','characters.html','player.html')){
    $taskHtmlPath=Join-Path $taskDist $taskHtmlName
    $taskHtml=Get-Content -LiteralPath $taskHtmlPath -Raw
    foreach($taskAssetName in @('app.js','ladder.js','characters.js','player.js','style.css')){
        $taskHash=(Get-FileHash -LiteralPath (Join-Path $taskDist $taskAssetName) -Algorithm SHA256).Hash.Substring(0,12).ToLowerInvariant()
        $taskHtml=$taskHtml.Replace(('"'+$taskAssetName+'"'),('"'+$taskAssetName+'?v='+$taskHash+'"'))
    }
    Set-Content -LiteralPath $taskHtmlPath -Value $taskHtml -Encoding utf8
}
Set-Content -LiteralPath (Join-Path $taskDist '.nojekyll') -Value ''
Write-Output "Static website: $taskDist"
