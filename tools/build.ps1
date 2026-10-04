$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
dotnet build (Join-Path $taskRoot 'Yx.WebSim/Yx.WebSim.csproj') -c Release -v minimal
if($LASTEXITCODE -ne 0){throw 'WASM build failed'}
$taskBundle=Join-Path $taskRoot 'Yx.WebSim/bin/Release/net8.0/browser-wasm/AppBundle'
$taskDist=Join-Path $taskRoot 'dist'
New-Item -ItemType Directory -Path $taskDist -Force | Out-Null
Copy-Item (Join-Path $taskBundle '_framework') $taskDist -Recurse -Force
Copy-Item (Join-Path $taskRoot 'Yx.WebSim/wwwroot/*') $taskDist -Recurse -Force
$taskCacheFiles=[ordered]@{}
$taskCoreFiles=[System.Collections.Generic.List[string]]::new()
$taskResources=@(Get-ChildItem -LiteralPath (Join-Path $taskDist '_framework') -File | Where-Object {$_.Extension -in @('.js','.wasm','.json')})
$taskResources+=@(Get-ChildItem -LiteralPath (Join-Path $taskDist 'assets') -Recurse -File)
foreach($taskName in @('sim-worker.js','data/battle_config.json','data/card_effects.json')){$taskResources+=Get-Item -LiteralPath (Join-Path $taskDist $taskName)}
foreach($taskResource in ($taskResources | Sort-Object FullName)){
    $taskRelative=[System.IO.Path]::GetRelativePath($taskDist,$taskResource.FullName).Replace('\','/')
    $taskCacheFiles[$taskRelative]=(Get-FileHash -LiteralPath $taskResource.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if(!$taskRelative.StartsWith('assets/')){$taskCoreFiles.Add($taskRelative)}
}
$taskVersionText=($taskCacheFiles | ConvertTo-Json -Compress -Depth 5)+(Get-FileHash -LiteralPath (Join-Path $taskDist 'sw.js') -Algorithm SHA256).Hash+(Get-FileHash -LiteralPath (Join-Path $taskDist 'resource-cache.js') -Algorithm SHA256).Hash
$taskVersion=[Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($taskVersionText))).Substring(0,16).ToLowerInvariant()
$taskCardFiles=[ordered]@{}
foreach($taskRelative in $taskCacheFiles.Keys){if($taskRelative.StartsWith('assets/card-components/')){$taskCardFiles[$taskRelative]=$taskCacheFiles[$taskRelative]}}
$taskCardVersion=[Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($taskCardFiles | ConvertTo-Json -Compress)))).Substring(0,16).ToLowerInvariant()
@{version=$taskVersion;files=$taskCacheFiles;core=$taskCoreFiles.ToArray()} | ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath (Join-Path $taskDist 'asset-manifest.json') -Encoding utf8
foreach($taskCacheScript in @('sw.js','resource-cache.js','app.js')){
    $taskScriptPath=Join-Path $taskDist $taskCacheScript
    (Get-Content -LiteralPath $taskScriptPath -Raw).Replace('__CACHE_VERSION__',$taskVersion) | Set-Content -LiteralPath $taskScriptPath -Encoding utf8
}
foreach($taskHtmlName in @('index.html','ladder.html','characters.html','player.html')){
    $taskHtmlPath=Join-Path $taskDist $taskHtmlName
    $taskHtml=(Get-Content -LiteralPath $taskHtmlPath -Raw).Replace('__CARD_VERSION__',$taskCardVersion)
    foreach($taskAssetName in @('app.js','ladder.js','characters.js','player.js','style.css')){
        $taskHash=(Get-FileHash -LiteralPath (Join-Path $taskDist $taskAssetName) -Algorithm SHA256).Hash.Substring(0,12).ToLowerInvariant()
        $taskHtml=$taskHtml.Replace(('"'+$taskAssetName+'"'),('"'+$taskAssetName+'?v='+$taskHash+'"'))
    }
    Set-Content -LiteralPath $taskHtmlPath -Value $taskHtml -Encoding utf8
}
Set-Content -LiteralPath (Join-Path $taskDist '.nojekyll') -Value ''
Write-Output "Static website: $taskDist"
