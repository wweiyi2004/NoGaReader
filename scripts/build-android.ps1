param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("apk", "aab")]
    [string]$Format = "aab"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src\NoGaReader.Mobile\NoGaReader.Mobile.csproj"
$artifactDirectory = Join-Path $repositoryRoot "artifacts\android\$Configuration-$Format"

New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null

if ($Format -eq "apk")
{
    $targets = @(
        @{ Runtime = "android-arm64"; FileName = "NoGaReader-Android-arm64-Signed.apk" },
        @{ Runtime = "android-x64"; FileName = "NoGaReader-Android-x64-Signed.apk" }
    )

    foreach ($target in $targets)
    {
        $runtimeDirectory = Join-Path $artifactDirectory $target.Runtime
        New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null

        dotnet publish $projectPath `
            -c $Configuration `
            -f net9.0-android `
            -r $target.Runtime `
            -p:RuntimeIdentifiers=$($target.Runtime) `
            -p:AndroidPackageFormat=apk `
            -p:EmbedAssembliesIntoApk=true `
            -p:PublishDir="$runtimeDirectory\"

        if ($LASTEXITCODE -ne 0)
        {
            throw "Android publish for $($target.Runtime) failed with exit code $LASTEXITCODE."
        }

        $signedPackage = Get-ChildItem -LiteralPath $runtimeDirectory -Filter "*-Signed.apk" -File |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1

        if ($null -eq $signedPackage)
        {
            throw "Android publish for $($target.Runtime) produced no signed APK."
        }

        Copy-Item -LiteralPath $signedPackage.FullName `
            -Destination (Join-Path $artifactDirectory $target.FileName) `
            -Force
    }
}
else
{
    dotnet publish $projectPath `
        -c $Configuration `
        -f net9.0-android `
        -p:AndroidPackageFormat=$Format `
        -p:PublishDir="$artifactDirectory\"

    if ($LASTEXITCODE -ne 0)
    {
        throw "Android publish failed with exit code $LASTEXITCODE."
    }
}

$packages = Get-ChildItem -LiteralPath $artifactDirectory -Filter "*.$Format" -File
if ($packages.Count -eq 0)
{
    throw "Android publish completed but produced no .$Format package."
}

$packages | Select-Object FullName, Length
