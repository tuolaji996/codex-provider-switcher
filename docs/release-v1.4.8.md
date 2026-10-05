# Codex Provider Switcher v1.4.8

## 中文

- 修复 #17：供应商页新增“编辑账号”和“删除账号”，可修改名称、Base URL、模型及新 Key，不再把编辑变成重复添加。
- 编辑只保存账号资料，不会立即切换或重启 Codex；需要使用新配置时再点击“切换到第三方”。更换 Base URL 需要新的 Key。
- 账号保存、凭据写入或删除失败时回滚；兼容性、工具和图片测试不再创建账号或保存草稿 Key。
- 保留未应用的修改；保护当前使用的账号和共用凭据，避免关闭重开后恢复已删除账号或覆盖其他账号。
- GPT-6.1 Sol 无自定义设置时自动配置 1M/900K 预设，并补上 Max / Ultra 菜单配置。现有推理档位、自定义上下文、官方登录及聊天历史保持不变。
- 新增 60 个账号管理回归用例、951 项检查及不截图的中英 WPF 窗口检查。

注意：1M 是配置预设，实际可用输入和压缩阈值仍受 Codex 模型目录及供应商限制；这次没有进行满 1M 输入实测。DeepSeek 等第三方仍需要支持 Codex 所需的 Responses 协议，账号管理修复不等于新增协议适配。

安装：下载 Windows x64 ZIP，解压后运行 `install.ps1`，从桌面快捷方式打开。首次运行新版不会自动清理重复账号；请自行选择要保留或删除的账号。

## English

- Fixes #17 with explicit **Edit account** and **Delete account** controls. Change a saved account's name, URL, model, or key in place without creating a duplicate.
- Editing only saves the account; it does not immediately switch or restart Codex. Apply with **Switch to third-party**. Changing the Base URL requires a new key.
- Failed account/credential operations roll back. Compatibility, tool, and image tests now use temporary drafts without creating accounts or saving draft keys.
- Preserve unapplied edits and shared credentials, protect the active account, and prevent deleted accounts from reappearing or another account from being overwritten on restart.
- Configure the GPT-6.1 Sol 1M/900K preset when no custom context or opt-out exists, and enable Max / Ultra menu permissions without forcing a reasoning setting.
- Adds 60 account-management regression cases, 951 checks, and non-visual bilingual WPF smoke tests. Official sign-in and chat history stay intact.

The 1M setting is a preset, not a guarantee of one million usable input tokens. Runtime model limits, compaction, and provider support still apply; a full-window live request was not tested. This release does not add a DeepSeek protocol adapter: providers must still support Codex's Responses protocol.

Download and extract the Windows x64 ZIP, run `install.ps1`, and open the desktop shortcut. Duplicate accounts are never merged or deleted automatically.
