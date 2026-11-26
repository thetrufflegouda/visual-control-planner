[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'VisualControlsBinder.csproj'
$destination = [IO.Path]::GetFullPath($Destination)
$zip = "$destination.zip"
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $zip)) {
    throw 'Choose a new portable output directory and archive name.'
}

$staging = Join-Path $PSScriptRoot ('obj/portable/' + [Guid]::NewGuid().ToString('N'))
dotnet publish $project -c Release -r win-x64 --self-contained true -o $staging `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'The Windows x64 publish failed.' }

$looseLibraries = Get-ChildItem -LiteralPath $staging -File -Filter '*.dll' -Recurse
if ($looseLibraries) { throw "Unbundled libraries in the single-file publish: $($looseLibraries.Name -join ', ')." }
[IO.Directory]::CreateDirectory($destination) | Out-Null
Copy-Item -LiteralPath (Join-Path $staging 'VisualControlPlanner.exe') -Destination $destination
$support = Join-Path $destination 'Support'

# Ship only the standard catalog and the photo files its layouts reference.
$catalogs = @((Get-Item -LiteralPath (Join-Path $staging 'Hardware/layouts.json'))) +
    @(Get-ChildItem -LiteralPath (Join-Path $staging 'Hardware/Library') -Filter '*.json' -File)
$photoPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($catalog in $catalogs) {
    $relative = [IO.Path]::GetRelativePath($staging, $catalog.FullName)
    $target = Join-Path $support $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $catalog.FullName -Destination $target
    $data = Get-Content -LiteralPath $catalog.FullName -Raw | ConvertFrom-Json
    foreach ($layout in $data.layouts) {
        foreach ($view in $layout.views) {
            [void] $photoPaths.Add($view.image)
        }
    }
}
$imagesRoot = (Join-Path $staging 'Hardware/Images') + [IO.Path]::DirectorySeparatorChar
foreach ($relative in $photoPaths) {
    $source = [IO.Path]::GetFullPath((Join-Path $staging $relative))
    if (!$source.StartsWith($imagesRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetExtension($source) -notin '.jpg', '.jpeg', '.png', '.bmp') {
        throw "Unexpected catalog photo location or format: $relative"
    }
    $target = Join-Path $support ([IO.Path]::GetRelativePath($staging, $source))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/usage.md') -Destination (Join-Path $support 'README.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Packaging/Third Party Notices') -Destination $support -Recurse
$license = Join-Path $PSScriptRoot 'LICENSE'
if (Test-Path -LiteralPath $license -PathType Leaf) {
    Copy-Item -LiteralPath $license -Destination $support
}

[IO.Compression.ZipFile]::CreateFromDirectory($destination, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Get-Item -LiteralPath $zip
