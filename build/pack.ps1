<#
    build\pack.ps1 —— 一键把 Re0Agent 打包成 dist\install.exe。
    步骤：icon.png → build\icon.ico + src\...\Resources\icon.ico → dotnet publish (self-contained win-x64) → ISCC 编译安装器。

    用法（从仓库根或任意目录）：
        pwsh -File build\pack.ps1
    可选参数：
        -SkipPublish   仅重编安装器（复用已有 dist\app）
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repo    = Split-Path -Parent $PSScriptRoot          # 仓库根
$build   = $PSScriptRoot
$appProj = Join-Path $repo 'src\Re0Agent.App\Re0Agent.App.csproj'
$pngPath = Join-Path $repo 'icon.png'
$icoBuild = Join-Path $build 'icon.ico'
$icoRes   = Join-Path $repo 'src\Re0Agent.App\Resources\icon.ico'
$distApp  = Join-Path $repo 'dist\app'
$tfm      = 'net8.0-windows10.0.19041.0'

if (-not (Test-Path $pngPath)) { throw "找不到图标源 $pngPath" }

# ---------------------------------------------------------------------------
# 1. PNG -> 多尺寸 ICO。优先 ImageMagick；无则用 .NET System.Drawing 回退（单 256 尺寸）。
# ---------------------------------------------------------------------------
function Convert-PngToIco {
    param([string]$Png, [string]$Ico)

    $magick = Get-Command magick -ErrorAction SilentlyContinue
    if ($magick) {
        Write-Host "[ico] 用 ImageMagick 生成多尺寸 ICO..." -ForegroundColor Cyan
        & $magick.Source $Png -background none -define 'icon:auto-resize=256,128,64,48,32,16' $Ico
        if ($LASTEXITCODE -ne 0) { throw "magick 转 ICO 失败 (exit $LASTEXITCODE)" }
        return
    }

    Write-Host "[ico] 未装 ImageMagick，改用 .NET 生成多尺寸 ICO（16/32/48/64/128/256，PNG 内嵌）..." -ForegroundColor Yellow
    Add-Type -AssemblyName System.Drawing
    $src = [System.Drawing.Image]::FromFile($Png)
    try {
        # 关键修复：必须内嵌多种尺寸。任务栏/快捷方式取 16、32px，若 ICO 只含 256px
        # 单尺寸，Windows 在小图位置会渲染成空白 —— 这正是「图标显示不对/空白」的根因。
        $sizes = 16, 32, 48, 64, 128, 256
        $pngs = foreach ($sz in $sizes) {
            $bmp = New-Object System.Drawing.Bitmap $sz, $sz
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.DrawImage($src, 0, 0, $sz, $sz)
            $g.Dispose()

            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
            ,$ms.ToArray()   # 逗号避免 PS 展开字节数组
        }

        $fs = [System.IO.File]::Create($Ico)
        $bw = New-Object System.IO.BinaryWriter($fs)
        # ICONDIR：6 字节头 + 每尺寸 16 字节目录项。
        $bw.Write([UInt16]0)               # reserved
        $bw.Write([UInt16]1)               # type = icon
        $bw.Write([UInt16]$sizes.Count)    # count

        $offset = 6 + 16 * $sizes.Count    # 首个图像数据偏移
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $sz = $sizes[$i]
            $dim = if ($sz -ge 256) { [Byte]0 } else { [Byte]$sz }  # 0 => 256
            $bw.Write($dim)                 # width
            $bw.Write($dim)                 # height
            $bw.Write([Byte]0)              # colors (truecolor)
            $bw.Write([Byte]0)              # reserved
            $bw.Write([UInt16]1)            # planes
            $bw.Write([UInt16]32)           # bpp
            $bw.Write([UInt32]$pngs[$i].Length)  # bytes
            $bw.Write([UInt32]$offset)      # offset
            $offset += $pngs[$i].Length
        }
        foreach ($p in $pngs) { $bw.Write($p) }
        $bw.Flush(); $bw.Dispose(); $fs.Dispose()
    }
    finally { $src.Dispose() }
}

Convert-PngToIco -Png $pngPath -Ico $icoBuild
New-Item -ItemType Directory -Force -Path (Split-Path $icoRes) | Out-Null
Copy-Item $icoBuild $icoRes -Force
Write-Host "[ico] 已生成 $icoBuild 并复制到 $icoRes" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 2. dotnet publish (self-contained win-x64)
# ---------------------------------------------------------------------------
if (-not $SkipPublish) {
    if (Test-Path $distApp) { Remove-Item $distApp -Recurse -Force }
    Write-Host "[publish] dotnet publish self-contained win-x64 ..." -ForegroundColor Cyan
    dotnet publish $appProj -c Release -f $tfm `
        -p:RuntimeIdentifier=win-x64 -p:WindowsPackageType=None `
        --self-contained true `
        -o $distApp
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 (exit $LASTEXITCODE)。若报文件锁，请先关闭正在运行的 App。" }
    Write-Host "[publish] 完成 -> $distApp" -ForegroundColor Green
}

$exe = Join-Path $distApp 'Re0Agent.App.exe'
if (-not (Test-Path $exe)) { throw "发布产物缺少 $exe，安装器无法打包。" }

# ---------------------------------------------------------------------------
# 3. ISCC 编译安装器 -> dist\install.exe
# ---------------------------------------------------------------------------
$iscc = @(
    "C:\Users\Xzhon_\AppData\Local\Programs\Inno Setup 6\ISCC.exe",
    "C:\Users\Xzhon_\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $g = Get-Command iscc -ErrorAction SilentlyContinue
    if ($g) { $iscc = $g.Source }
}
if (-not $iscc) {
    throw "未找到 Inno Setup 编译器 ISCC.exe。请先安装：winget install JRSoftware.InnoSetup  然后重跑本脚本。"
}

Write-Host "[iscc] 编译安装器 ..." -ForegroundColor Cyan
& $iscc (Join-Path $build 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败 (exit $LASTEXITCODE)" }

$installExe = Join-Path $repo 'dist\install.exe'
if (Test-Path $installExe) {
    $sz = [Math]::Round((Get-Item $installExe).Length / 1MB, 1)
    Write-Host "`n✅ 打包完成：$installExe（$sz MB）" -ForegroundColor Green
} else {
    throw "ISCC 结束但未见 dist\install.exe"
}
