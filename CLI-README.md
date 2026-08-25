# WuwaAssetProbe

这是 WuwaAssetExplorer 的独立命令行支线，用于在 WinUI 3 前端继续开发期间先快速完成鸣潮客户端资源定位与引用追查。

## 目标

- 直接读取 `Wuthering Waves Game\Client\Content\Paks`，不依赖 FModel 预导出 JSON。
- 自动从 `https://yarik0chka.github.io/wuwa-keys/keys.json` 获取 `mainKey + dynamicKeys`，并沿用 Core 的 AES 缓存。
- 挂载一次后进入交互模式，避免每条查询都重新扫描包体。
- 快速搜索资源名/路径。
- 对单个资源导出 CUE4Parse Properties JSON。
- 从单个资源 JSON 中提取正向资源路径引用。
- 对所有 UE 资源包做字符串扫描，快速得到“谁可能引用了这个资源名”的候选列表。

`backrefs` 是快速候选扫描，不是最终语义引用图。它扫描包数据中的资源名/字符串，适合当前逐层追踪：Texture2D → MaterialInstance → Niagara → Prefab → DataAsset → 动画。命中后再使用 `refs` 或 `dump` 验证。

## 使用

首次启动：

```text
WuwaAssetProbe.exe
```

按提示输入鸣潮游戏目录或 `Client\Content\Paks`。成功后路径会保存到程序目录的 `Data\cli-settings.json`。

也可以显式指定：

```text
WuwaAssetProbe.exe --paks "D:\Wuthering Waves\Wuthering Waves Game\Client\Content\Paks"
```

进入 `wuwa>` 后：

```text
search Aimisi
where T_Aimisi_Sub_40001
backrefs T_Aimisi_Sub_40001
refs MI_Trans_Sub_21004_S
dump MI_Trans_Sub_21004_S
backrefs MI_Trans_Sub_21004_S
refs NS_Fx_Aimisi_R1a_Screen_01
```

常用命令：

```text
search <文本> [--max N]
where <资源名或路径>
refs <资源名或路径>
dump <资源名或路径>
backrefs <资源名/字符串> [--path 路径过滤] [--max N] [--jobs N]
exit
```

`dump` 输出到程序目录的 `Output`。

## 当前限制

- `backrefs` 通过原始包数据/名称字符串命中寻找候选，可能有自引用或假阳性，也可能漏掉完全编译化且名称不在可读包数据中的特殊引用。
- `refs` 依赖 CUE4Parse 能正常解析目标包，并从序列化 JSON 里提取 `/Game/`、`Client/Content/`、`Engine/Content/` 路径。
- Niagara 编译 VM、RapidIteration 等深层语义还需要后续 WinUI/索引线的专门解析。
- 当前先服务于快速调查，不建立 SQLite 持久化反向引用数据库。

## 分支

此工具独立维护在：

```text
cli/quick-asset-probe
```

WinUI 3 主线可以在其他 feature/fix 分支继续进行，两者互不影响。后续确认 CLI 中某些解析逻辑成熟后，可以再提取到 Core 或 cherry-pick 到主线。
