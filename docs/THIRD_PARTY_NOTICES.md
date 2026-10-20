# 第三方组件与来源

本工具不修改或重新分发游戏程序集与游戏资源。下列组件按各自许可使用；第三方许可文本及上游来源随发行包保留。

| 组件 | 版本 / 用途 | 上游 |
|---|---|---|
| .NET / WPF / ASP.NET Core | 10；桌面与本机只读展示 | https://github.com/dotnet |
| Microsoft WebView2 SDK | 1.0.3856.49；内嵌网页 | https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3856.49 |
| WebView2 Fixed Version Runtime | 155.0.4283.62 x64；便携浏览器运行时 | https://developer.microsoft.com/microsoft-edge/webview2/ |
| Newtonsoft.Json | 13.0.3；本机 JSON 数据 | https://github.com/JamesNK/Newtonsoft.Json |
| Microsoft.Data.Sqlite | 10.0.12；持久接收账本与队列；MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw / e_sqlite3 | 2.1.12；SQLite 原生绑定；Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.12 |
| Google.Protobuf | 3.32.1；有界协议解码 | https://github.com/protocolbuffers/protobuf |
| BepInExPack_Silksong | 1.0.3；未修改的 Windows 加载器组件 | https://github.com/silksong-modding/BepInExPack-Silksong |
| BepInEx | 5.4.23.4；游戏 Mod 加载 | https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.4 |
| HarmonyX / MonoMod / Mono.Cecil / Doorstop | 随 BepInEx 包提供的未修改依赖 | 参见 BepInEx 发布包及各组件许可 |

BepInEx 与各独立组件的许可文本及对应源码入口随包保留；本工具不会阻止替换这些独立组件。WebView2 Runtime 适用微软的分发条款。

抖音消息字段研究参考公开的 DouyinLiveWebFetcher 协议定义；本工具未调用其采集脚本、签名服务或复制其采集实现。实际输入来自本人操作的独立 WebView2 页面，解码器使用 Google.Protobuf。

Windows 图标和界面图形为本项目自绘。

SQLitePCLRaw copyright 2014-2024 SourceGear, LLC；Microsoft.Data.Sqlite copyright Microsoft Corporation / .NET Foundation and Contributors。发行包保留对应许可。
