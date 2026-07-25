<#
.SYNOPSIS
    Adım 1 — gerçek ortamda ölçüm ve kapsam sondası.

.DESCRIPTION
    Her katman için iki şey yapar:
      1) KAPSAM SONDASI  — bu veritabanında hangi obje sınıfları var ve hangileri
                           karşılaştırma kapsamı dışında (sayılarıyla).
      2) KARŞILAŞTIRMA   — gerçek süre ve gerçek fark sayısı.

    TAMAMEN SALT OKUNURDUR. Yalnızca sistem katalog view'larına SELECT atar
    (sys.objects, sys.columns, sys.sql_modules, sys.dm_db_partition_stats...).
    Hiçbir veri okumaz, hiçbir şey yazmaz, hiçbir şey değiştirmez.

    Çıktının tamamı tek bir dosyaya yazılır; o dosyayı geri gönderin.

.EXAMPLE
    .\adim1-olcum.ps1
    .\adim1-olcum.ps1 -Databases EDWSTG
    .\adim1-olcum.ps1 -SourceServer "SRVDEV\PASIFIK" -TargetServer "PASIFIK"
#>
[CmdletBinding()]
param(
    [string]   $SourceServer = 'SRVDEV\PASIFIK',
    [string]   $TargetServer = 'PASIFIK',
    [string[]] $Databases    = @('EDWSTG','EDWSKEY','EDWLRY','EDW','EDWDM','EDWArchive','EDWBridge'),
    [string]   $OutputPath   = "adim1-sonuc-$(Get-Date -Format 'yyyyMMdd-HHmm').txt"
)

$ErrorActionPreference = 'Continue'
$exe = Join-Path $PSScriptRoot 'SchemaDiff.Cli.exe'
if (-not (Test-Path $exe)) { throw "SchemaDiff.Cli.exe bulunamadı: $exe" }

function ConnectionString([string]$server, [string]$database) {
    "Server=$server;Database=$database;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30"
}

$lines = @()
$lines += "SchemaDiff — Adım 1 ölçümü"
$lines += "Tarih   : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$lines += "Makine  : $env:COMPUTERNAME   Kullanıcı: $env:USERDOMAIN\$env:USERNAME"
$lines += "Kaynak  : $SourceServer"
$lines += "Hedef   : $TargetServer"
$lines += "Katman  : $($Databases -join ', ')"
$lines += ("=" * 92)

foreach ($db in $Databases) {
    Write-Host "[$db] kapsam sondası..." -ForegroundColor Cyan
    $lines += ""
    $lines += ("#" * 92)
    $lines += "# KATMAN: $db"
    $lines += ("#" * 92)

    foreach ($side in @(@{ Ad = 'KAYNAK'; Sunucu = $SourceServer }, @{ Ad = 'HEDEF'; Sunucu = $TargetServer })) {
        $lines += ""
        $lines += ">>> $($side.Ad) — $($side.Sunucu).$db"
        $lines += (& $exe --coverage -s (ConnectionString $side.Sunucu $db) 2>&1 | Out-String).TrimEnd()
    }

    Write-Host "[$db] karşılaştırma..." -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $compare = & $exe `
        -s (ConnectionString $SourceServer $db) `
        -t (ConnectionString $TargetServer $db) `
        --risk --triggers --timings -d 40 2>&1 | Out-String
    $sw.Stop()

    $lines += ""
    $lines += ">>> KARŞILAŞTIRMA — süre (uçtan uca, süreç başlatma dahil): $([math]::Round($sw.Elapsed.TotalSeconds,1)) sn"
    $lines += $compare.TrimEnd()
}

$lines | Out-File -FilePath $OutputPath -Encoding utf8
Write-Host ""
Write-Host "Bitti. Sonuç dosyası:" -ForegroundColor Green
Write-Host (Resolve-Path $OutputPath)
Write-Host ""
Write-Host "Bu dosyayı geri gönderin. İçinde kimlik bilgisi ya da veri YOKTUR —" -ForegroundColor Yellow
Write-Host "yalnızca obje adları, sayılar ve süreler bulunur." -ForegroundColor Yellow
