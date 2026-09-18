# X 岛 Fluent

面向 Windows 11 的原生 X 岛桌面客户端。主界面与论坛功能基于 WPF 和 Windows DWM 构建，不使用 Electron 或网页套壳；WebView2 仅用于用户主动打开的饼干管理与搜索辅助窗口。

## 已实现功能

- Fluent Design 视觉：Mica 系统背景、圆角卡片、Windows 11 字体与图标、浅色/深色/跟随系统主题
- 板块分类与板块说明
- 时间线选择、串列表与分页
- 串详情、回复分页、只看 PO、查看引用、快速串号跳转
- 订阅列表、添加订阅、取消订阅
- 发新串、回复、引用回复、图片上传与可选水印
- 公告显示、CDN 自动选择、原图与缩略图加载
- 串内图片保存到本机
- 串与图片离线缓存，并支持 `.xthread` 缓存包导入、导出
- 左上角全岛搜索入口；点击搜索结果后由客户端原生串页面打开
- 饼干/Cookie 与订阅标识本机保存
- 内置受限 WebView2 用户系统窗口；在网页中点击“应用”饼干后自动读取 `userhash`、保存并刷新客户端
- 网络异常和 API 业务错误提示

## 运行

在 `release` 文件夹中双击 `XIsland.exe`。这是自包含的 Windows x64 单文件程序，无需预装 .NET；饼干导入窗口使用 Windows 11 自带的 Microsoft Edge WebView2 Runtime。

部分板块以及发串、回复需要有效的 X 岛饼干。推荐在“设置”中点击“从用户系统获取饼干”，登录后在网页用户系统中应用饼干；客户端检测到 `userhash` 变化后会自动保存并刷新。也可以手动粘贴完整 Cookie 字符串，例如 `userhash=...`。该信息仅写入当前用户的 `%LOCALAPPDATA%\XIslandFluent\settings.json`，不会发送到 X 岛以外的地址。

## API 对接

接口字段、请求方法和 multipart 表单严格依据 [Apifox X岛 API 规范](https://s.apifox.cn/aedf55de-a60a-4d71-aa9a-d37458239a70/llms.txt) 实现，原始规范快照保存在 `docs/api`。

Apifox 文档给出的主站根路径 `/getForumList` 当前返回 404，而 JSON API 的线上部署地址为 `https://api.nmb.best/api`。客户端对读取、引用与订阅接口使用该实际 JSON API 前缀；发串与回复仍严格使用文档指定的主站接口：

- `POST https://www.nmbxd1.com/Home/Forum/doPostThread.html`
- `POST https://www.nmbxd1.com/Home/Forum/doReplyThread.html`

规范中的公告路径当前由其独立静态源 `https://nmb.ovear.info/nmb-notice.json` 提供，主站同名路径已返回 404，因此客户端使用该静态源获取公告。

## 从源代码构建

需要 .NET 10 SDK 与 Windows 10/11：

```powershell
dotnet build -c Release
dotnet publish XIslandFluent.csproj -c Release -r win-x64 --self-contained true -o release
```

只读 API 冒烟测试：

```powershell
dotnet run --project tests/Smoke/XIslandFluent.Smoke.csproj -c Release
```

## 隐私与安全

- Cookie、订阅标识和主题设置只保存在本机当前用户目录。
- 程序只连接 `www.nmbxd1.com`、`api.nmb.best`、`nmb.ovear.info`、`nmb-search.166666666.xyz` 和 API 返回的图片 CDN。
- HTML 内容会转换为纯文本显示，不执行脚本，也不嵌入网页。
- 运行诊断日志保存在 `%LOCALAPPDATA%\XIslandFluent\logs`，仅记录操作阶段、状态码和错误，不记录饼干值或回复正文。

## 1.1.1 修复

- 修复发送回复、添加订阅等返回值操作触发泛型异步重载递归，最终导致 `0xC00000FD` 栈溢出闪退的问题。
- 回复按钮增加防重入保护，避免网络延迟期间重复提交。
- 串内图片准备改为带循环检测的迭代处理，避免异常数据形成递归风险。

## 1.2.0 新功能

- 支持保存串内图片。
- 支持包含正文与图片的离线串缓存，以及 `.xthread` 文件导入、导出。
- 新增全岛搜索窗口，搜索结果中的串链接会交回原生客户端打开。

## 1.2.1 原生窗口体验

- 主窗口改用 Windows 系统标题栏与系统窗口按钮，不再自行绘制标题栏。
- 恢复系统原生的最小化、最大化、还原动画、贴靠布局和系统菜单行为。
- 串号输入框增加常驻占位提示。

## 1.3.0 界面与浏览体验

- 删除内容区重复的大标题和时间线选择器，将时间线入口整合进左栏。
- 左栏空余区域改为常驻板块树，选择板块后直接打开。
- 单击串内图片或列表缩略图时，交由 Windows 默认图片查看器打开。
- 管理员发布的公告串增加醒目的“公告”标记。
