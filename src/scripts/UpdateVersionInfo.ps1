param (
    [string]$filename
)

# git is optional: a source drop without .git (or without git installed) must still build.
$CommitVersion = 'unknown'
try {
    $git = Get-Command git -CommandType Application -ErrorAction Stop
    $described = & $git.Source describe --tags --long --dirty --always 2>$null
    if ($LASTEXITCODE -eq 0 -and $described) {
        $CommitVersion = ($described -join ' ').Trim()
    }
}
catch {
    # git not installed - keep the default
}

$FileContent = 'using System.Reflection;

[assembly: AssemblyInformationalVersion("{0}")]' -f $CommitVersion

$FileContent | Out-File -FilePath $filename -Encoding utf8