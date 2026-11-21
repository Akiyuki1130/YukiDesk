param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Clean', 'RuntimeCleanup', 'Checksums')]
    [string] $Action,
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [ValidateSet('x64', 'arm64')]
    [string[]] $Architectures
)

$ErrorActionPreference = 'Stop'
$publish = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'publish'))
$installers = Join-Path $PSScriptRoot 'installers'

if ($Action -eq 'Clean') {
    foreach ($arch in $Architectures) {
        foreach ($name in @("win-$arch", "win-$arch-slim")) {
            $target = Join-Path $publish $name
            if (!(Test-Path -LiteralPath $target)) { continue }
            $resolved = (Resolve-Path -LiteralPath $target).ProviderPath
            if ([IO.Path]::GetDirectoryName($resolved) -ne $publish -or $resolved -ne $target) {
                throw "Unexpected publish directory $resolved"
            }
            $item = Get-Item -LiteralPath $resolved -Force
            $publishItem = Get-Item -LiteralPath $publish -Force
            if (($item.Attributes -bor $publishItem.Attributes) -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to clean a redirected publish directory: $resolved"
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    exit 0
}

if ($Action -eq 'RuntimeCleanup') {
    foreach ($arch in $Architectures) {
        $full = Join-Path $publish "win-$arch"
        $slim = Join-Path $publish "win-$arch-slim"
        $entries = foreach ($file in Get-ChildItem -LiteralPath $full -Recurse -File) {
            $relative = $file.FullName.Substring($full.Length + 1)
            if (!(Test-Path -LiteralPath (Join-Path $slim $relative))) {
                'Type: files; Name: "{{app}}\{0}"' -f $relative
            }
        }
        if (!$entries) { throw "Missing full-only runtime files for $arch" }
        Set-Content -LiteralPath (Join-Path $publish "full-cleanup-$arch.iss") -Encoding Ascii -Value $entries
    }
    exit 0
}

$versions = @(foreach ($arch in $Architectures) {
    foreach ($name in @("win-$arch", "win-$arch-slim")) {
        (Get-Item -LiteralPath (Join-Path $publish "$name\YukiDesk.exe")).VersionInfo.FileVersion
    }
}) | Select-Object -Unique
if (@($versions).Count -ne 1 -or $versions -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'Published executables have inconsistent version numbers.'
}
$version = [string] $versions
$tagVersion = ([Version] $version).ToString(3)
$hashes = foreach ($arch in $Architectures) {
    foreach ($flavor in @('full', 'slim')) {
        $name = "YukiDesk-Setup-$arch-$flavor-$version.exe"
        $file = Get-Item -LiteralPath (Join-Path $installers $name)
        $stream = $file.OpenRead()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose(); $stream.Dispose() }
        "$digest  $name"
    }
}

# A machine-specific run uses a distinct manifest so it cannot replace a release manifest.
$suffix = if ($Architectures.Count -eq 1) { '-' + $Architectures[0] } else { '' }
$manifest = Join-Path $installers "SHA256SUMS-$tagVersion$suffix.txt"
Set-Content -LiteralPath $manifest -Encoding Ascii -Value $hashes
if ($Architectures.Count -eq 2) {
    Get-ChildItem -LiteralPath $installers -Filter 'YukiDesk-Setup-*.exe' -File |
        Where-Object Name -NotLike "*-$version.exe" |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
}
Write-Output $hashes
Write-Output "Manifest: $manifest"
