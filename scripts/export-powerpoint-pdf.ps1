<#
.SYNOPSIS
    Exports PowerPoint-native PDF sidecars for decks whose fonts or artwork differ in LibreOffice.
.EXAMPLE
    ./scripts/export-powerpoint-pdf.ps1 -Path '../Slides/talks/example/My talk.pptx'
.EXAMPLE
    ./scripts/export-powerpoint-pdf.ps1 -Path (Get-ChildItem '../Slides/talks/example' -Filter '*.pptx').FullName -Force
.NOTES
    Requires Windows and Microsoft PowerPoint. Podium already prefers a same-named PDF beside a PPTX.
    Sources are copied to scratch space and opened read-only. Exported PDFs contain visible slides, not notes or
    hidden slides. Re-run after editing a PPTX; use -Force to replace an existing PDF intentionally.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Path,
    [switch] $Force
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This exporter requires Windows and Microsoft PowerPoint.' }

$files = @($Path | ForEach-Object {
    $file = Get-Item -LiteralPath $_
    if ($file.PSIsContainer -or $file.Extension -ine '.pptx') { throw "Expected a .pptx file: $_" }
    $pdf = [IO.Path]::ChangeExtension($file.FullName, '.pdf')
    if (-not $Force -and [IO.File]::Exists($pdf)) { throw "PDF already exists: $pdf. Use -Force to regenerate it." }
    $file
})
# PowerPoint automation can reuse a running application. Never close someone's interactive session in our cleanup.
if (Get-Process POWERPNT -ErrorAction SilentlyContinue) { throw 'Close PowerPoint before running the native PDF exporter.' }
$work = Join-Path ([IO.Path]::GetTempPath()) "opencode/podium-pdf-$([guid]::NewGuid().ToString('N'))"
[void][IO.Directory]::CreateDirectory($work)
$app = $null
try {
    $app = New-Object -ComObject PowerPoint.Application
    $app.AutomationSecurity = 3 # msoAutomationSecurityForceDisable
    foreach ($file in $files) {
        $presentation = $null
        $range = $null
        try {
            $copy = Join-Path $work 'input.pptx'
            Copy-Item -LiteralPath $file.FullName -Destination $copy
            $presentation = $app.Presentations.Open($copy, -1, 0, 0) # read-only, no presentation window
            $range = $presentation.PrintOptions.Ranges.Add(1, $presentation.Slides.Count)
            $tempPdf = Join-Path $work 'export.pdf'
            if ([IO.File]::Exists($tempPdf)) { [IO.File]::Delete($tempPdf) }
            # PDF, print-quality, no frame, slide output, no hidden slides, all slides. Keep labels/IRM and
            # accessibility tags; bitmap a font if PowerPoint cannot embed it rather than substituting it later.
            $presentation.ExportAsFixedFormat($tempPdf, 2, 2, 0, 1, 1, 0, $range, 1, '', $false, $true, $true, $true, $false)
            $header = [Text.Encoding]::ASCII.GetString([byte[]](Get-Content -LiteralPath $tempPdf -AsByteStream -TotalCount 5))
            if ($header -ne '%PDF-') { throw "PowerPoint did not produce a PDF for $($file.Name)" }
            $pdf = [IO.Path]::ChangeExtension($file.FullName, '.pdf')
            [IO.File]::Copy($tempPdf, $pdf, $Force.IsPresent)
            [pscustomobject]@{ Source = $file.FullName; Pdf = $pdf; Bytes = (Get-Item -LiteralPath $pdf).Length }
        } finally {
            if ($range) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($range) }
            if ($presentation) { $presentation.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($presentation) }
        }
    }
} finally {
    try {
        if ($app) { $app.Quit(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($app) }
    } finally {
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
