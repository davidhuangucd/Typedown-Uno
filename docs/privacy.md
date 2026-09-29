# 隐私说明 / Privacy

Typedown Uno 不收集遥测、使用统计或崩溃报告，也不把打开的文档上传给 Typedown 项目。文档、设置、会话、
光标位置和崩溃恢复副本保存在本机应用数据目录中。

除文档自身引用的远程资源外，只有用户主动使用“分享到 HedgeDoc”或“测试连接”时，应用才会连接用户填写
的 HedgeDoc 服务器。分享会把
当前 Markdown 文档发送给该服务器；如果填写了邮箱和密码，登录请求也会发送给同一服务器。密码不会写入
`settings.json`：Windows 使用当前用户 DPAPI，macOS 使用 Keychain，Linux 使用 Secret Service。Linux
桌面没有可用的 Secret Service 工具时，密码只保留在当前进程内。

Markdown 中的远程图片、链接和其他外部资源可能由内嵌网页引擎或用户的默认浏览器访问，其服务器会看到正常
网络请求所包含的 IP 地址和请求信息。是否访问这类地址取决于文档内容和用户操作。

删除应用数据目录即可删除 Typedown Uno 保存的本地数据：

- Windows：`%LOCALAPPDATA%\Typedown.Uno\`
- Linux：`~/.local/share/Typedown.Uno/`
- macOS：`~/Library/Application Support/Typedown.Uno/`

---

Typedown Uno does not collect telemetry, usage analytics, or crash reports, and it does not upload opened
documents to the Typedown project. Documents, settings, session state, caret positions, and crash-recovery
copies remain in the local application-data directory.

Apart from remote resources referenced by a document, the app connects to a network service only when the user
invokes **Share to HedgeDoc** or **Test connection**. The current Markdown document, and optional login
credentials, are sent to the HedgeDoc server configured by the user. The password is excluded from
`settings.json`: Windows uses per-user DPAPI, macOS uses Keychain, and Linux uses Secret Service. If no Secret
Service tool is available on Linux, the password remains in memory for the current process only.

Remote images, links, and other external resources referenced by a Markdown document may be opened by the
embedded web engine or the default browser. Those servers receive ordinary network request information.

Deleting the application-data directory listed above removes local data saved by Typedown Uno.
