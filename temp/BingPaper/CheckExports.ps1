$bytes = [System.IO.File]::ReadAllBytes('E:\Users\Administrator\source\repos\BingWPDLHelper\bin\x64\Debug\Microsoft.WindowsAppRuntime.Bootstrap.dll')
$peOffset = [BitConverter]::ToUInt32($bytes, 0x3C)
$exportOffset = [BitConverter]::ToUInt32($bytes, [int]($peOffset + 0x78))
$nameCount = [BitConverter]::ToUInt32($bytes, [int]($exportOffset + 0x18))
$namesOffset = [BitConverter]::ToUInt32($bytes, [int]($exportOffset + 0x20))

Write-Host "Exported functions:"
for ($i = 0; $i -lt $nameCount; $i++) {
    $nOffset = [BitConverter]::ToUInt32($bytes, [int]($namesOffset + $i * 4))
    $s = [int]$nOffset
    while ($bytes[$s] -ne 0) {
        $s++
    }
    Write-Host ([System.Text.Encoding]::ASCII.GetString($bytes, [int]$nOffset, $s - [int]$nOffset))
}