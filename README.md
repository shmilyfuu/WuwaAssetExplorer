# WuwaAssetExplorer

Windows 11 原生 WinUI 3 鸣潮资源浏览与引用查询工具。

## 当前阶段

第一阶段目标是先验证三个基础能力：

1. 直接读取 `Wuthering Waves Game\Client\Content\Paks`。
2. 使用 `GAME_WutheringWaves` 与动态 AES Endpoint 挂载当前客户端资源。
3. 建立轻量内存资源目录，实现接近 FModel `Ctrl+Shift+F` 体验的即时名称/路径搜索。

引用关系数据库、递归依赖树、资源间最短引用路径与资源预览会在基础读取/搜索验证后继续加入。

## 技术栈

- .NET 10
- Windows App SDK 2.4.0 / WinUI 3
- CUE4Parse 1.2.2.202608
- Windows 11 x64
- Unpackaged + Windows App SDK self-contained + .NET self-contained

UI 只使用 Windows App SDK / WinUI 3 官方控件与系统主题资源，不引入 WPF、WinForms、Avalonia、Electron 或第三方 UI 框架。

## AES

默认 Endpoint：

```text
https://yarik0chka.github.io/wuwa-keys/keys.json
```

读取 `mainKey` 与 `dynamicKeys`。成功结果缓存到程序目录的 `Data\aes-cache.json`；网络不可用时会尝试使用最近缓存。

## 便携数据

程序配置与后续索引均放在程序目录：

```text
WuwaAssetExplorer\
├─ WuwaAssetExplorer.exe
├─ ...
└─ Data\
   ├─ settings.json
   └─ aes-cache.json
```

`Data` 已加入 `.gitignore`。

## 构建

需要 Windows 11 / Windows 构建环境与 .NET 10 SDK：

```powershell
dotnet restore .\src\WuwaAssetExplorer.App\WuwaAssetExplorer.App.csproj -r win-x64
dotnet build .\src\WuwaAssetExplorer.App\WuwaAssetExplorer.App.csproj -c Release -r win-x64 -p:Platform=x64
```

发布绿色版目录：

```powershell
dotnet publish .\src\WuwaAssetExplorer.App\WuwaAssetExplorer.App.csproj -c Release -r win-x64 -p:Platform=x64 -p:PublishProfile=win-x64
```

GitHub Actions 会在 Windows runner 上构建并生成 `WuwaAssetExplorer-win-x64` artifact。

## 搜索设计

普通资源搜索不解析每个 UObject。游戏加载后只从 CUE4Parse 已挂载的 `Provider.Files` 建立轻量目录和精确名称索引；输入搜索时在内存目录上执行大小写不敏感的名称/路径匹配，并限制 UI 返回数量。因此搜索成本与游戏磁盘体积解耦。

后续引用索引属于另一层：它会解析包内部引用并持久化到本地数据库，不会阻塞普通名称搜索。
