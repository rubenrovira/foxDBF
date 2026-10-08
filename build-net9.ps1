#Requires -Version 5.1
<#
.SYNOPSIS
  Compila temporalmente el repositorio en .NET 9 y lo restaura después, byte a byte, a .NET 10.

.DESCRIPTION
  1. Valida que los 6 archivos afectados estén EXACTAMENTE en el estado NET10 esperado
     (si un solo conteo no coincide, aborta sin escribir ningún archivo).
  2. Aplica en memoria los cambios NET9 (TFM net10.0 -> net9.0 en los 5 .csproj,
     System.Text.Encoding.CodePages 10.0.9 -> 9.0.9, y #if false sobre un test con
     ambigüedad de compilación en NET9).
  3. Escribe los archivos, ejecuta `dotnet restore` y `dotnet build`.
  4. En finally: restaura los bytes originales de TODOS los archivos escritos,
     tanto si el build falla como si no. Después verifica la restauración byte a byte.

  No usa Git (sin stash, commits ni branches). No deja ningún cambio en el working tree.

.OUTPUTS
  Exit codes: 0 = build NET9 correcto; N = código de salida real de `dotnet build`;
  1 = validación previa fallida o excepción (repo intacto);
  2 = la verificación de restauración detectó diferencias (no debería ocurrir).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
$tfmOld     = '<TargetFramework>net10.0</TargetFramework>'
$tfmNew     = '<TargetFramework>net9.0</TargetFramework>'
$cpOld      = '<PackageReference Include="System.Text.Encoding.CodePages" Version="10.0.9" />'
$cpNew      = '<PackageReference Include="System.Text.Encoding.CodePages" Version="9.0.9" />'
$markerIf   = '#if false // build-net9.ps1: temporal solo para compilar en NET9'
$markerEnd  = '#endif    // build-net9.ps1'
$foxCsproj  = 'src\CrossVault.FoxDbf\CrossVault.FoxDbf.csproj'
$testRel    = 'tests\CrossVault.FoxDbf.Tests.Public\CdxReadTruncatedPoolTests.cs'
$testSig    = '    public void CdxOpen_OneTagTruncatedMidPool_HealthyTagSurvives_TruncatedTagAbsent()'
$factLine   = '    [Fact]'
$finallyLn  = '        finally { Cleanup(dir); }'

$csprojs = @(
    'src\CrossVault.FoxDbf.MicroVfp\CrossVault.FoxDbf.MicroVfp.csproj'
    'src\CrossVault.FoxDbf.Data\CrossVault.FoxDbf.Data.csproj'
    'src\CrossVault.FoxDbf.Expressions\CrossVault.FoxDbf.Expressions.csproj'
    $foxCsproj
    'tests\CrossVault.FoxDbf.Tests.Public\CrossVault.FoxDbf.Tests.Public.csproj'
)

# ---------------------------------------------------------------- helpers -----------------
function Count-Ordinal([string]$text, [string]$needle) {
    if ([string]::IsNullOrEmpty($needle)) { return 0 }
    return [regex]::Matches($text, [regex]::Escape($needle)).Count
}

function Read-TrackedFile([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $enc = [System.Text.UTF8Encoding]::new($true); $off = 3
    } else {
        $enc = [System.Text.UTF8Encoding]::new($false); $off = 0
    }
    $text = $enc.GetString($bytes, $off, $bytes.Length - $off)
    if ($text -match "(?<!`r)`n") { throw "Fin de línea LF suelto (no soportado): $path" }
    [pscustomobject]@{ Path = $path; Bytes = $bytes; Enc = $enc; Text = $text }
}

function Write-TextWithEncoding([string]$path, [string]$text, [System.Text.Encoding]$enc) {
    $preamble = $enc.GetPreamble()
    $body     = $enc.GetBytes($text)
    $all      = New-Object byte[] ($preamble.Length + $body.Length)
    [Array]::Copy($preamble, 0, $all, 0, $preamble.Length)
    [Array]::Copy($body, 0, $all, $preamble.Length, $body.Length)
    [IO.File]::WriteAllBytes($path, $all)
}

function Test-BytesEqual([byte[]]$a, [byte[]]$b) {
    if ($null -eq $a -or $null -eq $b) { return $false }
    if ($a.Length -ne $b.Length) { return $false }
    for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { return $false } }
    return $true
}

# ------------------------------------------------ FASE 1: validar (cero escrituras) -------
$problems = @()
$plan     = @()

foreach ($rel in $csprojs) {
    $abs = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $abs)) { $problems += "$rel : el archivo no existe"; continue }
    $f = Read-TrackedFile $abs

    $n = Count-Ordinal $f.Text $tfmOld
    if ($n -ne 1) { $problems += "$rel : se esperaba exactamente 1 TFM 'net10.0', hay $n (¿NET9 ya aplicado?)" }
    $n = Count-Ordinal $f.Text 'net9.0'
    if ($n -ne 0) { $problems += "$rel : ya contiene 'net9.0' ($n ocurrencias)" }

    $newText = $f.Text.Replace($tfmOld, $tfmNew)

    if ($rel -eq $foxCsproj) {
        $n = Count-Ordinal $f.Text $cpOld
        if ($n -ne 1) { $problems += "$rel : se esperaba 1 referencia CodePages '10.0.9', hay $n" }
        $n = Count-Ordinal $f.Text 'Version="9.0.9"'
        if ($n -ne 0) { $problems += "$rel : ya contiene CodePages '9.0.9'" }
        $newText = $newText.Replace($cpOld, $cpNew)
    }

    $plan += [pscustomobject]@{ Rel = $rel; File = $f; NewText = $newText }
}

$abs = Join-Path $root $testRel
if (-not (Test-Path -LiteralPath $abs)) {
    $problems += "$testRel : el archivo no existe"
} else {
    $f = Read-TrackedFile $abs

    $n = Count-Ordinal $f.Text 'build-net9.ps1'
    if ($n -ne 0) { $problems += "$testRel : marcadores 'build-net9.ps1' residuales ($n)" }

    $lines = $f.Text -split "`r`n"
    $sigIdx = @()
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -ceq $testSig) { $sigIdx += $i } }

    if ($sigIdx.Count -ne 1) {
        $problems += "$testRel : la firma del test aparece $($sigIdx.Count) veces (se espera 1)"
    } else {
        $s = $sigIdx[0]
        if ($s -lt 1 -or $lines[$s - 1] -cne $factLine) {
            $problems += "$testRel : la línea anterior a la firma no es '$factLine'"
        }
        if (($s + 1) -ge $lines.Count -or $lines[$s + 1] -cne '    {') {
            $problems += "$testRel : la firma no va seguida de '    {'"
        }

        $k = $s - 2
        while ($k -ge 0 -and $lines[$k].TrimStart().StartsWith('///', [StringComparison]::Ordinal)) { $k-- }
        $docStart = $k + 1
        if ($k -lt 0 -or $lines[$k].Trim().Length -ne 0) {
            $problems += "$testRel : el doc-comment no está precedido de una línea en blanco"
        } elseif ($lines[$docStart] -cne '    /// <summary>') {
            $problems += "$testRel : el bloque no empieza con '    /// <summary>'"
        }

        $after = @()
        for ($i = $s; $i -lt $lines.Count; $i++) { if ($lines[$i] -ceq $finallyLn) { $after += $i } }
        if ($after.Count -ne 1) {
            $problems += "$testRel : se espera 1 '$finallyLn' después de la firma, hay $($after.Count)"
        } else {
            $finIdx = $after[0]
            if (($finIdx + 2) -ge $lines.Count -or $lines[$finIdx + 1] -cne '    }' -or $lines[$finIdx + 2] -cne '}') {
                $problems += "$testRel : cierre del método/clase inesperado tras 'finally'"
            } else {
                $ls = [System.Collections.Generic.List[string]]::new()
                foreach ($l in $lines) { [void]$ls.Add($l) }
                $ls.Insert($finIdx + 2, $markerEnd)
                $ls.Insert($docStart, $markerIf)
                $newText = [string]::Join("`r`n", $ls)
                $plan += [pscustomobject]@{ Rel = $testRel; File = $f; NewText = $newText }
            }
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host ''
    Write-Host 'VALIDACION FALLIDA — estado NET10 no coincide. NO se modifico ningun archivo:' -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
    exit 1
}

Write-Host "Validacion NET10 OK: $($plan.Count) archivos listos para el parche temporal."
foreach ($p in $plan) { Write-Host "  ~ $($p.Rel)" }

# ------------------------- FASE 2+3: escribir, build; finally => restaurar bytes ----------
$touched   = [System.Collections.Generic.List[object]]::new()
$buildExit = 1

try {
    foreach ($p in $plan) {
        $touched.Add($p.File)                                  # ANTES de escribir
        Write-TextWithEncoding $p.File.Path $p.NewText $p.File.Enc
    }

    Write-Host ''
    Write-Host 'Parche NET9 aplicado. Ejecutando dotnet restore + dotnet build ...'

    Push-Location -LiteralPath $root
    try {
        & dotnet restore
        $restoreExit = $LASTEXITCODE
        if ($restoreExit -ne 0) {
            Write-Warning "dotnet restore devolvio $restoreExit; se continua igualmente con dotnet build"
        }
        & dotnet build
        $buildExit = $LASTEXITCODE
    }
    finally { Pop-Location }
}
catch {
    Write-Host "EXCEPCION: $($_.Exception.Message)" -ForegroundColor Red
    $buildExit = 1
}
finally {
    foreach ($f in $touched) {
        [IO.File]::WriteAllBytes($f.Path, $f.Bytes)             # byte-exacto, siempre
    }
}

# ------------------------------------------------- FASE 4: verificar + exit code ----------
$restoreOk = $true
foreach ($p in $plan) {
    $now = [IO.File]::ReadAllBytes($p.File.Path)
    if (-not (Test-BytesEqual $p.File.Bytes $now)) {
        $restoreOk = $false
        Write-Host "RESTAURACION DIFERENTE: $($p.Rel)" -ForegroundColor Red
    }
}
if (-not $restoreOk) {
    Write-Host 'El working tree NO quedo identico al original. Revisar manualmente.' -ForegroundColor Red
    exit 2
}

Write-Host ''
Write-Host "Repositorio restaurado a NET10 (byte-exacto, $($plan.Count) archivos). build exit = $buildExit"
exit $buildExit
