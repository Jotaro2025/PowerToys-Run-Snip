# winget 清单（1.5.0 就绪）

本目录为 [winget-pkgs](https://github.com/microsoft/winget-pkgs) 就绪的清单，包标识符：`Jotaro2025.PowerToysRunSnip`。

## 当前状态（1.5.0）

- `1.5.0/` 四个清单文件已填入 **真实 SHA256** 与 Inno `ProductCode`，可提交上游：

  | 架构 | SHA256 |
  |---|---|
  | x64-slim | `9038A3E0944EE6BA891E5A81C57A008E17BE7A0E27BAD9FCD614AA433E039C8F` |
  | arm64-slim | `F956BB8499BB826A1E634000660A12742D226A0140144936CFC9E73119BD152D` |

- `ProductCode` 取自 `installer/Snip.iss` 的 `AppId`（每架构一个 GUID + `_is1` 后缀）。
- `1.4.0/` 是历史草稿（当时 Release 无资产、SHA 为占位符），仅留档，勿提交。

## 发布新版本时

1. 确认 Release 含 `Snip-Setup-<ver>-x64-slim.exe` 与 `Snip-Setup-<ver>-arm64-slim.exe`。
2. 计算 SHA256（下载后本地算，或 `gh release view` + `curl`）：
3. 复制上一版目录为 `<新版本>/`，更新 `PackageVersion`、`InstallerUrl`、`InstallerSha256`、`ReleaseNotesUrl`。
4. 本地验证：

```powershell
winget validate winget/Jotaro2025.PowerToysRunSnip/1.5.0
winget install --manifest winget/Jotaro2025.PowerToysRunSnip/1.5.0
```

5. Fork `microsoft/winget-pkgs`，将 `1.5.0/` 拷到 `manifests/j/Jotaro2025/PowerToysRunSnip/1.5.0/` 并开 PR（首次提交需走人工审核）。

## English

1.5.0 manifests carry real SHA256 hashes and Inno ProductCodes — ready for an
upstream winget-pkgs PR. 1.4.0 remains as a historical draft with placeholders;
do not submit it. For each new release: copy the previous version directory,
bump PackageVersion / InstallerUrl / InstallerSha256 / ReleaseNotesUrl, then
`winget validate` before PR.
