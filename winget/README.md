# winget 清单（提交前须填 SHA256）

本目录为 [winget-pkgs](https://github.com/microsoft/winget-pkgs) 就绪的清单草稿，包标识符：`Jotaro2025.PowerToysRunSnip`。

## 当前状态（1.4.0）

- GitHub Release [`v1.4.0`](https://github.com/Jotaro2025/PowerToys-Run-Snip/releases/tag/v1.4.0) 已创建，**安装包资源尚未上传**（`installer.yml` 依赖 GitHub Actions；若账单/CI 未恢复则需本机 `build/build-installer.ps1` 后手动附加到 Release）。
- `InstallerSha256` / `ProductCode` 为占位符，**不可直接 PR 到 winget-pkgs**，否则验证失败。

## 资源就绪后

1. 确认 Release 含 `Snip-Setup-1.4.0-x64-slim.exe` 与 `Snip-Setup-1.4.0-arm64-slim.exe`（命名与 README 一致）。
2. 计算 SHA256：

```powershell
Get-FileHash .\Snip-Setup-1.4.0-x64-slim.exe -Algorithm SHA256
Get-FileHash .\Snip-Setup-1.4.0-arm64-slim.exe -Algorithm SHA256
```

3. 写入 `Jotaro2025.PowerToysRunSnip.installer.yaml` 的 `InstallerSha256`；`ProductCode` 取自 Inno `AppId`（见 `installer/Snip.iss`）。
4. 本地验证：

```powershell
winget validate winget/Jotaro2025.PowerToysRunSnip/1.4.0
winget install --manifest winget/Jotaro2025.PowerToysRunSnip/1.4.0
```

5. Fork `microsoft/winget-pkgs`，将本目录拷到 `manifests/j/Jotaro2025/PowerToysRunSnip/1.4.0/` 并开 PR。

## English

Draft manifests for winget-pkgs. Fill real `InstallerSha256` (and Inno `ProductCode`) after Release assets exist; placeholders must not be submitted upstream.
