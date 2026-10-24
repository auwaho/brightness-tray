[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($taskAsset in @(@('StoreLogo', 50), @('Square44x44Logo', 44), @('Square150x150Logo', 150))) {
    $taskSize = [int]$taskAsset[1]
    $taskBitmap = [System.Drawing.Bitmap]::new($taskSize, $taskSize)
    $taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
    $taskPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 2.3)
    try {
        $taskGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        # Reuse the tray sun geometry, leaving space for the Start menu's icon plate.
        $taskScale = [single]($taskSize / 48)
        $taskGraphics.TranslateTransform([single]($taskSize / 6), [single]($taskSize / 6))
        $taskGraphics.ScaleTransform($taskScale, $taskScale)
        $taskPen.StartCap = $taskPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $taskGraphics.DrawEllipse($taskPen, [single]9.5, [single]9.5, [single]13, [single]13)
        for ($taskIndex = 0; $taskIndex -lt 8; $taskIndex++) {
            $taskAngle = $taskIndex * [Math]::PI / 4
            $taskGraphics.DrawLine($taskPen,
                [single](16 + [Math]::Cos($taskAngle) * 11), [single](16 + [Math]::Sin($taskAngle) * 11),
                [single](16 + [Math]::Cos($taskAngle) * 14.5), [single](16 + [Math]::Sin($taskAngle) * 14.5))
        }
        $taskBitmap.Save((Join-Path $OutputDirectory ($taskAsset[0] + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $taskPen.Dispose()
        $taskGraphics.Dispose()
        $taskBitmap.Dispose()
    }
}
