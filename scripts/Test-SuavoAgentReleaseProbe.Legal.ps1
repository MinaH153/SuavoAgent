function Test-ReleaseLegalBundle {
    param([string]$Directory)
    $legal = Join-Path $Directory 'legal'
    $required = @(
        'THIRD-PARTY-NOTICES.txt',
        'THIRD-PARTY-PROVENANCE.json',
        'external-assets.json',
        'license-texts\MIT.txt',
        'license-texts\Apache-2.0.txt',
        'license-texts\Leptonica-BSD-2-Clause.txt'
    )
    foreach ($relative in $required) {
        $path = Join-Path $legal $relative
        $ok = Test-Path -LiteralPath $path -PathType Leaf
        if ($ok) {
            $item = Get-Item -LiteralPath $path
            $ok = $item.Length -gt 0 -and $item.Length -le 8MB
        }
        Add-ProbeResult -Name "legal:$relative" -Ok $ok -Detail $(if ($ok) { 'present' } else { 'missing_or_invalid' })
    }

    $noticePath = Join-Path $legal 'THIRD-PARTY-NOTICES.txt'
    if (Test-Path -LiteralPath $noticePath -PathType Leaf) {
        $notice = Get-Content -LiteralPath $noticePath -Raw
        $markers = @(
            'Apache License',
            'MIT License',
            'Copyright (C) 2001-2020 Leptonica',
            'MICROSOFT .NET RUNTIME 8.0.28'
        )
        $complete = @($markers | Where-Object { -not $notice.Contains($_) }).Count -eq 0
        Add-ProbeResult -Name 'legal:required-license-texts' -Ok $complete -Detail $(if ($complete) { 'complete' } else { 'incomplete' })
    }

    $sbom = Join-Path $Directory 'suavoagent.spdx.json'
    try {
        $document = Get-Content -LiteralPath $sbom -Raw | ConvertFrom-Json
        $root = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd([char[]]"\/")
        $rootPackages = @($document.packages | Where-Object {
            [string]$_.SPDXID -ceq 'SPDXRef-Package-SuavoAgent'
        })
        $declaredExclusions = @()
        if ($rootPackages.Count -eq 1) {
            $declaredExclusions = @(
                $rootPackages[0].packageVerificationCode.packageVerificationCodeExcludedFiles |
                    ForEach-Object { [string]$_ }
            )
        }
        $declaredSet = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        $exclusionsUnique = $true
        foreach ($name in $declaredExclusions) {
            if (-not $declaredSet.Add($name)) { $exclusionsUnique = $false }
        }
        $preFinalExclusionsOk =
            $declaredExclusions.Count -eq 1 -and
            $declaredSet.Contains('./suavoagent.spdx.json')
        $manifestSignatures = @(
            Get-ChildItem -LiteralPath $Directory -File | Where-Object {
                $_.Name -cmatch '^update-manifest-v[0-9]+\.[0-9]+\.[0-9]+\.sig$'
            }
        )
        $finalExclusionsOk =
            $manifestSignatures.Count -eq 1 -and
            $declaredExclusions.Count -eq 4 -and
            $declaredSet.Contains('./suavoagent.spdx.json') -and
            $declaredSet.Contains('./checksums.sha256') -and
            $declaredSet.Contains('./checksums.sha256.sig') -and
            $declaredSet.Contains('./' + $manifestSignatures[0].Name) -and
            (Test-Path -LiteralPath (Join-Path $Directory 'checksums.sha256') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $Directory 'checksums.sha256.sig') -PathType Leaf)
        $actualFiles = @{}
        foreach ($file in @(Get-ChildItem -LiteralPath $Directory -Recurse -File)) {
            $relative = $file.FullName.Substring($root.Length).TrimStart([char[]]"\/").Replace('\', '/')
            $documentName = './' + $relative
            if ($declaredSet.Contains($documentName)) { continue }
            $actualFiles[$documentName] = $file.FullName
        }
        $documentFiles = @($document.files)
        $seen = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        $sbomOk =
            [string]$document.spdxVersion -eq 'SPDX-2.3' -and
            [string]$document.SPDXID -eq 'SPDXRef-DOCUMENT' -and
            $rootPackages.Count -eq 1 -and
            $exclusionsUnique -and
            ($preFinalExclusionsOk -or $finalExclusionsOk) -and
            $documentFiles.Count -eq $actualFiles.Count
        foreach ($entry in $documentFiles) {
            $name = [string]$entry.fileName
            if (-not $actualFiles.ContainsKey($name) -or -not $seen.Add($name)) {
                $sbomOk = $false
                continue
            }
            $checksums = @($entry.checksums | Where-Object { [string]$_.algorithm -eq 'SHA256' })
            if ($checksums.Count -ne 1) {
                $sbomOk = $false
                continue
            }
            $expected = [string]$checksums[0].checksumValue
            $actual = (Get-FileHash -LiteralPath $actualFiles[$name] -Algorithm SHA256).Hash
            if ($expected -notmatch '^[A-Fa-f0-9]{64}$' -or $actual -ine $expected) {
                $sbomOk = $false
            }
        }
        if ($seen.Count -ne $actualFiles.Count) { $sbomOk = $false }
        Add-ProbeResult `
            -Name 'sbom:spdx-signed-cohort' `
            -Ok $sbomOk `
            -Detail $(if ($sbomOk) { 'exact_files_and_hashes' } else { 'invalid_or_mismatch' }) `
            -Metadata @{ documentedFiles = $documentFiles.Count; actualFiles = $actualFiles.Count }
    } catch {
        Add-ProbeResult -Name 'sbom:spdx-signed-cohort' -Ok $false -Detail 'missing_or_unreadable'
    }
}
