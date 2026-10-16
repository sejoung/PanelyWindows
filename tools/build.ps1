<#
.SYNOPSIS
  솔루션을 빌드하고 테스트한다.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build.ps1                    # Debug 빌드 + 전체 테스트
  powershell -ExecutionPolicy Bypass -File tools\build.ps1 -Configuration Release
  powershell -ExecutionPolicy Bypass -File tools\build.ps1 -SkipTests
  powershell -ExecutionPolicy Bypass -File tools\build.ps1 -Filter Translation  # 이름에 Translation이 들어간 테스트만
  powershell -ExecutionPolicy Bypass -File tools\build.ps1 -Clean
#>
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$SkipTests,
    [string]$Filter = '',
    [switch]$Clean
)

. "$PSScriptRoot\_common.ps1"
$started = Get-Date
$dotnet = Get-Dotnet
Write-Note "SDK: $dotnet ($(& $dotnet --version))"

if ($Clean) {
    Write-Step 'bin/obj 정리'
    Get-ChildItem -Path (Join-Path $RepoRoot 'src'), (Join-Path $RepoRoot 'tests') -Directory -Recurse -Include bin, obj -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Ok '정리됨'
}

Write-Step "빌드 ($Configuration)"
Invoke-Dotnet $dotnet @('build', $SolutionFile, '-c', $Configuration, '-nologo', '-v', 'minimal')
Write-Ok '빌드 성공'

if (-not $SkipTests) {
    Write-Step '테스트'
    $testArgs = @('test', $TestProject, '-c', $Configuration, '--no-build', '-nologo')
    if ($Filter) { $testArgs += @('--filter', "FullyQualifiedName~$Filter") }
    Invoke-Dotnet $dotnet $testArgs
    Write-Ok '테스트 통과'
}

$exe = Join-Path $RepoRoot "src\Panely.App\bin\$Configuration\net10.0-windows10.0.19041.0\Panely.exe"
Write-Step ("완료 ({0:n0}초)" -f ((Get-Date) - $started).TotalSeconds)
Write-Note "빌드된 실행 파일: $exe"
Write-Note '설치해서 바로 실행하려면: tools\install-local.ps1'
