# targetbox.ps1 - diagnostic harness: shows a topmost text box that stays
# focused and pumps messages while an external orchestrator drives the voice
# chain (mic switch + hotkey injection + cable playback). Writes the box's
# final content to <resultFile> when <doneFile> appears, then closes.
#   targetbox.ps1 <readyFile> <doneFile> <resultFile>
param($ready, $done, $result)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.Text = 'MiVoiceMic 诊断 - 语音识别文字会打到这里，请勿移动鼠标/键盘'
$form.TopMost = $true
$form.Size = New-Object System.Drawing.Size(560, 260)
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(1200, 400)
$form.ShowInTaskbar = $true
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true
$tb.Dock = 'Fill'
$tb.Font = New-Object System.Drawing.Font('Microsoft YaHei', 14)
$tb.ReadOnly = $false
$form.Controls.Add($tb)
$form.Add_Shown({ $form.Activate(); $tb.Focus() })
$form.Show()
$form.Activate()
$tb.Focus()
[System.Windows.Forms.Application]::DoEvents()
Set-Content -Path $ready -Value 'ok'
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path $done) -and $sw.Elapsed.TotalSeconds -lt 40) {
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 60
}
Start-Sleep -Milliseconds 400
[System.Windows.Forms.Application]::DoEvents()
Set-Content -Path $result -Value $tb.Text
$form.Close()
