param (
    [string]$filename
)

# Public release version. Development builds inside this fork were numbered 3.x, but the
# released product is 1.0 - a separate line descended from upstream sw2urdf 1.6.1.
$BaseVersion = '1.0'

# git is optional: a source drop without .git (or without git installed) must still build.
$CommitVersion = $BaseVersion
try {
    $git = Get-Command git -CommandType Application -ErrorAction Stop
    $sha = & $git.Source rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $sha) {
        $dirty = & $git.Source status --porcelain 2>$null
        $suffix = if ($dirty) { '+{0}.dirty' -f $sha } else { '+{0}' -f $sha }
        $CommitVersion = $BaseVersion + $suffix
    }
}
catch {
    # git not installed - keep the plain release version
}

$FileContent = 'using System.Reflection;

[assembly: AssemblyInformationalVersion("{0}")]' -f $CommitVersion

$FileContent | Out-File -FilePath $filename -Encoding utf8
