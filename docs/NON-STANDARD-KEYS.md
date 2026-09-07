# 非标准按键：为什么 返回/主页/菜单/直播/电源/音量± 不能直接用

本文解释小米蓝牙遥控器 2 Pro（RC003）上 7 个按键在 Windows 下"消失"的完整技术原因，
以及每种可能的解决路线为什么可行/不可行。结论先行：

> **无驱动 + Secure Boot 开启**的默认组合下，这 7 个键无法用任何纯软件手段拯救。
> 程序内置了 RemoteMapper 的测试签名内核驱动，关闭 Secure Boot 后可一键安装解锁；
> 不动 Secure Boot 的替代路线见文末。

## 1. 这些键的信号死在哪一环

信号链路：`遥控器 HID 报告 → 蓝牙栈 (BTHLE) → kbdhid.sys → 键盘事件 (VK/扫描码) → 用户态程序`

遥控器把每个按键组织进 **HID 顶级集合（Top-Level Collection, TLC）**。实测其 HID 报告映射：

| 键 | HID 用法 | 结果 |
|---|---|---|
| 语音键 | Keyboard Page → F5 | ✓ 映射为 VK_F5 |
| 方向 ×4 | Keyboard Page → 箭头键 | ✓ 正常方向键 |
| 确定键 | Keyboard Page → Enter | ✓ 正常 Enter |
| 返回 / 主页 / 菜单 / 直播 / 电源 / 音量± | **非标准 Keyboard Page 用法** | ✗ `kbdhid.sys` 找不到对应扫描码/VK，**在内核直接丢弃** |

丢弃点位于内核（`kbdhid.sys`），也就是说事件**根本不会到达**用户态——所以：

- 全局键盘钩子（`WH_KEYBOARD_LL`）：收不到
- Raw Input：收不到（键盘 TLC 被系统独占，且事件在上游已被丢）
- `WM_APPCOMMAND`：无事件
- PowerToys Keyboard Manager / AutoHotkey / 注册表 Scancode Map：这些是"重映射已存在按键"的工具，无键可映射

RemoteMapper 作者用 Android `getevent` 做对照确认：遥控器确实发出了这些键的 HID 报告（Android 能看到），是 Windows 的键盘类驱动选择不处理它们。

## 2. 逐条评估过的替代路线

以下路线均经过 RemoteMapper 作者与本项目的实测/调研（详见
[RemoteMapper NOTES.md](https://github.com/QL-4/RemoteMapper)）：

| 路线 | 可行性 | 原因 |
|---|---|---|
| **KMDF 内核过滤驱动**（本项目内置） | ✓（需测试签名） | 在报告到达 `kbdhid.sys` 前拦截，把 7 个非标准键改写成 F13–F19，并把语音键 F5 改写为 F20（与物理 F5 彻底隔离，本程序视同 F5）；精确绑定 `VID 2717 / PID 32B8` 的 HID TLC，ATVV 语音报告不受影响 |
| 注册表 Scancode Map / PowerToys / AutoHotkey | ✗ | 只能重映射"已存在"的按键；这些键没有事件 |
| Raw Input / `WM_APPCOMMAND` | ✗ | 上游已丢弃 |
| WinRT `Windows.Devices.Hid` / Chrome WebHID 直接读 HID | ✗ | Keyboard Page 属于受保护用法；且键盘 TLC 被系统独占打开 |
| GATT 直接订阅 HID 服务报告特征（绕过 HID 栈） | ✗ | HID 服务 (0x1812) 被 Windows HID 栈独占，特征读写返回 `AccessDenied`——本程序作为已配对的 GATT 客户端（走 ATVV 语音）也拿不到 |
| ETW / HCI 抓包后注入 | 实验性 | 高权限、脆弱、延迟差，是诊断手段不是输入方案 |
| 用户态驱动（UMDF） | ✗ | HID 键盘栈过滤必须在内核（KMDF）；UMDF 够不到独占的键盘 TLC |
| 正式签名内核驱动 | ✓（需要 EV 证书 + 微软硬件开发者计划） | 同一份驱动提交微软做"证明签名"（attestation signing）后，Secure Boot 开启也能装。个人项目难做到，适合 RemoteMapper 作者或公司去做 |
| BLE→USB 硬件代理（ESP32 等） | ✓（需自制固件） | 把遥控器转发成 USB 键盘；但语音通道（ATVV）需另行打通，总体更重 |
| 换遥控器 | ✓ | 选购 Windows 原生识别这些键的媒体遥控器 |
| 修改遥控器固件 | 理论上 | 消费设备无公开刷写入口，风险最高 |

## 3. 本项目的方案：内置测试签名驱动

按键映射页的 **「安装内核驱动（高级）」** 按钮会调起 `driver\MiRemoteHidFilter\` 里
RemoteMapper 项目的官方安装脚本（自动弹 UAC 提权）：

1. **第 1 步 `prepare-test-mode.bat`**：开启 Windows 测试签名（`bcdedit /set testsigning on`），
   并把驱动测试证书装入受信任存储 → **重启**
2. **第 2 步 `install-driver.bat`**：`pnputil /add-driver` 安装扩展 INF，把过滤驱动挂到
   遥控器的 HID TLC 上 → **再重启**
3. 重启后这些键以 **F13–F19** 到达系统——按键映射页里已保存的配置自动生效；语音键同时被驱动改写为 **F20**（与物理 F5 隔离），本程序自动把它视同 F5 处理，F5 拦截与按键映射行为不变

### 代价与限制（务必阅读）

- **Secure Boot 必须关闭**：测试签名与 Secure Boot 互斥。安装脚本会自动检测并在
  Secure Boot 开启时拒绝执行（不做任何系统更改）。关闭 Secure Boot 需要进 UEFI/BIOS 设置，
  且注意：
  - 若系统盘启用了 **BitLocker**，改 Secure Boot 可能触发恢复密钥输入——动手前先备份 48 位恢复密钥；
  - 部分要求 Secure Boot 的**反作弊游戏**会无法运行；
  - 桌面右下角会出现“测试模式”水印（测试签名的正常现象）。
- 需要两次重启（第 1 步后、第 2 步后各一次）。
- 遥控器需已配对。
- 不想用了：对话框里的“卸载驱动”按钮 + 重新打开 Secure Boot 即可回到默认状态。

### 为什么这样设计是安全的

- 驱动**精确绑定**到 `HID\VID&012717_PID&32b8` 这一个设备的键盘 TLC，不触碰其他任何设备；
- 只在该 TLC 的报告里做等长 usage 替换（改写键码），ATVV 语音报告走厂商私有服务，不经过该 TLC；
- 驱动来源与许可证：RemoteMapper 项目，GPL-3.0（与本程序一致），见下方致谢；
- 全部可逆：卸载驱动 + 关闭测试签名 + 重新打开 Secure Boot = 回到系统默认状态。

## 4. 不装驱动的日常影响

- 可用键：语音键（语音输入/或映射）、方向 ×4、确定键——**语音输入完全不受影响**；
- 返回/主页/菜单/直播/电源/音量± 按了没反应（音量请用键盘或系统托盘）；
- 映射页底部网格的配置可以随时填写保存，装驱动后自动生效。
