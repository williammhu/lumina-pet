# 构建与发布

## 只编译程序

在 Windows 10/11 x64 上安装 .NET Framework 4.8。将 Releases 的两个资源包解压到仓库的 `outputs/` 目录，得到 `outputs/Lumina-Desktop/assets/`，然后在 PowerShell 中运行：

```powershell
.\lumina-desktop\build.ps1
```

输出为 `outputs/Lumina-Desktop/Lumina.exe`。源码使用 Windows 自带的 .NET Framework C# 编译器，不依赖 NuGet。保留同目录的 `Lumina.exe.config` 和 `assets`。

## 从关键帧构建全部动画

需要 Windows x64、Python 3.12 和 .NET Framework 4.8，并预留约 12 GB 可用磁盘空间。Python 依赖仅用于制作动画。

```powershell
python -m pip install -r requirements-build.txt
python scripts/build_all.py
python scripts/package_github.py
```

构建会复制 `assets-src`，生成基础动作和 12 个新增动作的 720×1056 中间帧，再转换为统一的 1280×1536 XPRESS 高清帧，最后编译程序。所有步骤都在 `outputs/` 中进行。

打包工具将 23 个动画文件按大小均衡放进两个互补 ZIP，每个 ZIP 都小于 2 GiB。两份都解压到同一位置后，组合成完整的 `Lumina-Desktop` 文件夹。`SHA256SUMS.txt` 记录两个 ZIP 的 SHA-256。

## 验证

离线动画工具逐帧检查解码长度和 CRC，并记录文件 SHA-256。最终 C# 程序还可在有桌面会话的 Windows 中运行：

```powershell
Start-Process .\outputs\Lumina-Desktop\Lumina.exe -ArgumentList '--self-test' -Wait
```

结果位于 `outputs/Lumina-Desktop/qa-v4/tests.json`。完整测试会创建临时测试窗口；发布参考版本通过 50 项检查。性能与真实 Codex 联动应在本机桌面环境验证，不能用云端构建结果代替。

## GitHub 发布

`.github/workflows/release.yml` 在 `main` 的 `release-version.txt` 更新时，或手动运行时构建程序与全部动画，并发布对应的 `v版本号`。流程只为发布任务授予仓库内容写入权限，使用 GitHub 自动提供的短期令牌，无需保存个人访问令牌。

发布先建为草稿，两个 ZIP、说明与校验文件上传成功后才公开。已存在相同版本时流程会停止，避免覆盖发布附件。

## 上传范围

仓库包含源码、编译配置、离线制作工具、必要关键帧、动作源图、提示词和预览图。以下内容不进入 Git 历史：

- 数 GB 的 `.luma` 动画与可执行程序：通过 Releases 分发。
- 中间帧、旧版本、备份、构建依赖和临时目录。
- 本地 Codex 会话、登录信息、个人设置与运行日志。

素材提示词中已移除制作电脑的绝对路径。
