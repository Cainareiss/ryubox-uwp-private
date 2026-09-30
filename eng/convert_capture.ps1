Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile('E:\ports\ryujinx uwp\artifacts\horizon_test.bmp')
$bmp.Save('E:\ports\ryujinx uwp\artifacts\horizon_test.png', [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "Saved horizon_current_capture.png"
