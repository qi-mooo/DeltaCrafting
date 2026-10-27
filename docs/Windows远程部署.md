# Windows SSH 远程启动

通过 OpenSSH 连接 Windows 后，直接在 SSH 会话中启动 WinUI GUI 进程可能在会话结束时被回收，或者拿不到交互式桌面。部署和验证时使用当前登录用户的**一次性计划任务**，由任务在交互式令牌中启动程序，再删除任务。

在 PowerShell 中执行下面的模板，把 `$exe` 换成实际的 `DeltaCrafter.exe` 路径：

```powershell
$exe = 'C:\Users\i\AppData\Local\DeltaCrafter\app\<版本-内容哈希>\DeltaCrafter.exe'
$work = Split-Path -Parent $exe
$task = 'DeltaCrafter-RemoteOnce-' + [guid]::NewGuid().ToString('N')
$user = "$env:USERDOMAIN\$env:USERNAME"
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $work
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType InteractiveToken -RunLevel Limited
Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $task
Start-Sleep -Seconds 3
Get-Process -Name DeltaCrafter -ErrorAction SilentlyContinue | Select-Object Id,Path,StartTime
Unregister-ScheduledTask -TaskName $task -Confirm:$false
```

任务使用 `InteractiveToken`，所以目标用户必须已经登录桌面；`Limited` 保持普通用户权限，适合应用本身需要管理员权限提示的既有行为。若程序启动失败，先保留任务并查看任务历史，再执行删除命令：

```powershell
Get-ScheduledTaskInfo -TaskName $task
Unregister-ScheduledTask -TaskName $task -Confirm:$false
```

不要把 SSH 会话中的 `Start-Process` 当作远程 GUI 启动方式，也不要使用 `/RU SYSTEM`，否则程序无法访问用户桌面和游戏窗口。
