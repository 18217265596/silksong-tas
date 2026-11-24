# Silksong Lost Lace TAS

《空洞骑士：丝之歌》Windows TAS 工具。实测漫游者、火刀与丝刃冲刺 **48.283 秒无伤**（2897 游戏帧，终点 −44 HP）。计时从开场战吼后首次可操作到蕾丝 HP 首次低于零。

## 环境

使用正版 Steam 版 **1.0.30000**、BepInEx **5.4.23.4** 和 Windows PowerShell。需要自己提供游戏目录、BepInEx 安装以及可进入失心蕾丝战斗的第二槽。测试使用隔离目录中的存档副本。

```powershell
$stage = Join-Path ([IO.Path]::GetTempPath()) 'SilksongRace'
.\Race\Start-LostLaceRace.ps1 -Stage $stage -SteamGame '<Steam 游戏目录>' -Slot2 '<user2.dat 路径>' -Visible
.\Silksong-TAS.ps1 -Stage $stage -Command 'load'
```

等长椅加载完成后发送 `step 65 RJ`，再发送 `inspect` 确认离椅。纪录局离椅后另运行 `step 6`，随后提交保存的试跑命令。

```powershell
.\Silksong-TAS.ps1 -Stage $stage -Command 'step 65 RJ'
.\Silksong-TAS.ps1 -Stage $stage -Command 'inspect'
.\Silksong-TAS.ps1 -Stage $stage -Command 'step 6'
$route = (Get-Content .\Race\routes\record-command.txt -Raw).Trim()
.\Silksong-TAS.ps1 -Stage $stage -Command $route
```

每条命令处理完成后再发送下一条。可从 `$stage\telemetry.txt` 查看状态。实测使用漫游者与存档自带的火刀、丝刃冲刺配装。启动时序和存档状态会影响结果，48.283 秒是实机记录。对应输入保存在 `Race/routes/crossfollow-0003.race-inputs.txt`，每行记录持续帧数和按键。退出使用 `quit` 命令。
