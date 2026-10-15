$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $stage = Join-Path $root 'artifacts/PowderLab-0.1.0-windows-x64'
    $archive = Join-Path $root 'artifacts/PowderLab-0.1.0-windows-x64.zip'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    dotnet publish Powder.csproj -c Release -r win-x64 --self-contained true -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    $source = Join-Path $stage 'Source'
    New-Item $source -ItemType Directory -Force | Out-Null
    $items = @('Powder.csproj', 'Program.cs', 'global.json', 'packages.lock.json',
        'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', '.gitignore', '.vscode',
        'Simulation', 'UI', 'Tests', 'Assets', 'scripts', 'docs', 'licenses')
    foreach ($item in $items) { Copy-Item (Join-Path $root $item) $source -Recurse -Force }
    Copy-Item LICENSE, THIRD_PARTY_NOTICES.md, README.md $stage -Force
    Copy-Item licenses $stage -Recurse -Force
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
    Write-Host "Created $archive (Powder.exe + Source/Powder.csproj)"
}
finally { Pop-Location }
