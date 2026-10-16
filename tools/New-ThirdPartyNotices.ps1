<#
.SYNOPSIS
  Writes THIRD-PARTY-NOTICES.md and the license texts of the redistributed packages into a release folder.

.DESCRIPTION
  The list is built from the packages that the app actually restored (obj/project.assets.json), so it cannot go
  stale. For every package it records the license from the package's .nuspec and copies the license file that
  the package ships. The license texts that FFmpegInteropX bundles for FFmpeg and its libraries are copied too,
  and the FFmpeg DLLs found in the release folder are listed, as the LGPL asks.

  Run it after "dotnet build" / "dotnet publish" and before the release folder is zipped.

.PARAMETER OutputDirectory
  The release folder (the one that contains EmbeddingSearchViewer.App.exe).
#>
param(
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $ProjectAssets = (Join-Path $PSScriptRoot '..\src\EmbeddingSearchViewer.App\obj\project.assets.json')
)

$ErrorActionPreference = 'Stop'

# Packages that are only used to build the app and are not part of what is shipped.
$buildOnly = @('Microsoft.Windows.SDK.BuildTools', 'Microsoft.Windows.SDK.BuildTools.MSIX', 'Microsoft.Windows.SDK.BuildTools.WinApp')

if (-not (Test-Path -LiteralPath $ProjectAssets)) {
    throw "project.assets.json not found at '$ProjectAssets'. Build the app first."
}
$assets = Get-Content -Raw -LiteralPath $ProjectAssets | ConvertFrom-Json
$packageFolders = @($assets.packageFolders.PSObject.Properties.Name)

function Find-PackageDirectory([string] $id, [string] $version) {
    foreach ($folder in $packageFolders) {
        $dir = Join-Path $folder (Join-Path $id.ToLowerInvariant() $version)
        if (Test-Path -LiteralPath $dir) { return $dir }
    }
    return $null
}

$licenseRoot = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Force -Path $licenseRoot | Out-Null

$rows = @()
foreach ($entry in $assets.libraries.PSObject.Properties) {
    if ($entry.Value.type -ne 'package') { continue }
    $id, $version = $entry.Name -split '/', 2
    if ($buildOnly -contains $id) { continue }

    $dir = Find-PackageDirectory $id $version
    if (-not $dir) { throw "Package $id $version is not in the NuGet cache." }

    [xml] $nuspec = Get-Content -Raw -LiteralPath (Get-ChildItem -LiteralPath $dir -Filter '*.nuspec' | Select-Object -First 1).FullName
    $meta = $nuspec.package.metadata
    $license = $meta.license
    $url = "$($meta.projectUrl)".Trim()

    $licenseText = '(see the package)'
    $licensePath = ''
    if ($license -and $license.type -eq 'expression') {
        $licenseText = "$($license.'#text')".Trim()
    }
    elseif ($license -and $license.type -eq 'file') {
        $licenseText = 'see license file'
        $source = Join-Path $dir "$($license.'#text')".Trim()
        if (Test-Path -LiteralPath $source) {
            $target = Join-Path $licenseRoot $id
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Force
            $licensePath = "licenses/$id/$(Split-Path -Leaf $source)"
        }
    }
    elseif ("$($meta.licenseUrl)".Trim()) {
        $licenseText = "$($meta.licenseUrl)".Trim()
    }

    # Packages that only name their license (MIT, Apache-2.0, ...) often still carry the text or a NOTICE at their root
    if (-not $licensePath) {
        $files = @(Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Name -match '^(LICEN[CS]E|NOTICE|COPYING|THIRD[-_ ]?PARTY)' })
        if ($files.Count -gt 0) {
            $target = Join-Path $licenseRoot $id
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            $files | Copy-Item -Destination $target -Force
            $licensePath = "licenses/$id/"
        }
    }

    # Still nothing: use the standard text of the named license (official SPDX text; the copyright holders are on the project page)
    if (-not $licensePath -and $licenseText -match '^(MIT|Apache-2\.0|BSD-3-Clause)$') {
        $standard = Join-Path $PSScriptRoot "license-texts/$($Matches[1]).txt"
        if (Test-Path -LiteralPath $standard) {
            $spdxDir = Join-Path $licenseRoot 'spdx'
            New-Item -ItemType Directory -Force -Path $spdxDir | Out-Null
            Copy-Item -LiteralPath $standard -Destination $spdxDir -Force
            $licensePath = "licenses/spdx/$($Matches[1]).txt"
        }
    }

    # FFmpegInteropX ships the license texts of FFmpeg and of the libraries built into it
    $bundled = Join-Path $dir 'licenses'
    if ($id -eq 'FFmpegInteropX.Desktop.FFmpeg' -and (Test-Path -LiteralPath $bundled)) {
        $target = Join-Path $licenseRoot $id
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item -Path (Join-Path $bundled '*') -Destination $target -Recurse -Force
        $licensePath = "licenses/$id/"
    }

    $rows += [pscustomobject]@{ Id = $id; Version = $version; License = $licenseText; Url = $url; Path = $licensePath }
}
$rows = $rows | Sort-Object { $_.Id.ToLowerInvariant() }

# FFmpeg libraries that are actually in this release (named as the LGPL asks: they are separate, replaceable DLLs)
$ffmpegDlls = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.dll' -File |
    Where-Object { $_.Name -match '^(avcodec|avformat|avutil|avfilter|avdevice|swresample|swscale|postproc)-\d+\.dll$' } |
    Sort-Object Name | ForEach-Object Name)
$ffmpeg = $rows | Where-Object Id -eq 'FFmpegInteropX.Desktop.FFmpeg' | Select-Object -First 1

$md = New-Object System.Text.StringBuilder
[void] $md.AppendLine('# サードパーティのライセンス (Third-Party Notices)')
[void] $md.AppendLine()
[void] $md.AppendLine('このアプリは、次のオープンソースのコンポーネントを利用しています。一覧は、ビルド時に、実際に使われているパッケージから自動で作成しています。')
[void] $md.AppendLine('各ライセンスの全文は、`licenses/` フォルダにあります。パッケージ自身が文書を同梱していないものには、標準のライセンス全文（`licenses/spdx/`）を添えています（著作権者の表記は、各プロジェクトのページを参照してください）。')
[void] $md.AppendLine()
[void] $md.AppendLine('## AI モデル')
[void] $md.AppendLine()
[void] $md.AppendLine('モデルはこの zip に含まれていません。初回起動時に、アプリが Hugging Face からダウンロードします。')
[void] $md.AppendLine('ライセンスは、配布元のページを参照してください: https://huggingface.co/onnx-community/embeddinggemma-2-ONNX')
[void] $md.AppendLine()
if ($ffmpeg) {
    [void] $md.AppendLine('## FFmpeg (LGPL)')
    [void] $md.AppendLine()
    [void] $md.AppendLine("動画のフレームの取得に、FFmpeg のライブラリを使っています（パッケージ $($ffmpeg.Id) $($ffmpeg.Version) 経由。ライセンスは **$($ffmpeg.License)**）。")
    [void] $md.AppendLine('FFmpeg は、アプリ本体とは別の DLL として動的にリンクされており、同じ名前・互換のバージョンの DLL に置き換えることができます。')
    if ($ffmpegDlls.Count -gt 0) {
        [void] $md.AppendLine()
        [void] $md.AppendLine('この配布物に含まれる FFmpeg のライブラリ: ' + (($ffmpegDlls | ForEach-Object { "``$_``" }) -join ', '))
    }
    [void] $md.AppendLine()
    [void] $md.AppendLine('- FFmpeg のソースコード: https://ffmpeg.org/download.html （FFmpeg 7.0 系）')
    [void] $md.AppendLine('- ビルドの提供元: https://github.com/FFmpegInteropX/FFmpegInteropX')
    [void] $md.AppendLine('- FFmpeg と、内部で使われているライブラリのライセンス文: `licenses/FFmpegInteropX.Desktop.FFmpeg/`')
    [void] $md.AppendLine()
}
[void] $md.AppendLine('## パッケージ一覧')
[void] $md.AppendLine()
[void] $md.AppendLine('| パッケージ | バージョン | ライセンス | プロジェクト | ライセンス文 |')
[void] $md.AppendLine('|---|---|---|---|---|')
foreach ($r in $rows) {
    $link = if ($r.Path) { "``$($r.Path)``" } else { '' }
    [void] $md.AppendLine("| $($r.Id) | $($r.Version) | $($r.License) | $($r.Url) | $link |")
}

$noticePath = Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.md'
[System.IO.File]::WriteAllText($noticePath, $md.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("Third-party notices: {0} packages, {1} FFmpeg DLLs, licenses folder {2} files" -f $rows.Count, $ffmpegDlls.Count, (Get-ChildItem -Recurse -File $licenseRoot).Count)
