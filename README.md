# 屏译 SnapTrans

[项目主页](https://cenyangtze.github.io/eztrans/) · [下载](https://github.com/CenYangtze/eztrans/releases/latest)

Windows 截图翻译工具。框选屏幕，即刻看到译文，或生成与原图等大的译图；按一下快捷键还能查光标旁的英文单词。

支持 OpenAI 兼容接口、Anthropic 接口，以及 Ollama、LM Studio 等本地模型。

## 安装

系统要求：Windows 10 22H2 或 Windows 11。

到 [Releases](https://github.com/CenYangtze/eztrans/releases) 下载与系统架构一致的 `scd-setup.exe` 并安装。

安装包没有代码签名，首次运行时 Windows 可能提示“已保护你的电脑”，点「更多信息 → 仍要运行」即可。

## 使用

1. 启动后屏译在系统托盘运行。
2. 打开「设置 → 模型」，选择服务商，填写模型名和 API Key，测试连接。
3. 按 `Alt+Q` 框选翻译，按 `Alt+Shift+Q` 生成译图。
4. 把鼠标移到英文单词上，按 `Alt+W` 查词（无需配置模型）。

| 操作 | 快捷键 |
| --- | --- |
| 框选翻译 | `Alt+Q` |
| 框选译图 | `Alt+Shift+Q` |
| 查词 | `Alt+W` |
| 钉住悬浮窗 | `Ctrl+P` |
| 重新翻译 | `Ctrl+R` |
| 调整字号 | `Ctrl+滚轮` |
| 查看原图 | 按住空格 |
| 关闭 | `Esc` |

快捷键可在「设置 → 快捷键」中修改。

## 隐私

截图只发送到你配置的模型服务，默认不保存，没有遥测。API Key 加密保存在本机。

查词时，只有被查的单词会发送到所选的词典服务（有道词典或 Google 翻译）。
