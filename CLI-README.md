# WuwaAssetProbe

这是 WuwaAssetExplorer 的独立命令行支线，用于在 WinUI 3 前端继续开发期间快速完成鸣潮客户端资源定位、正向引用检查和反向引用追查。

## 当前能力

- 直接读取 `Wuthering Waves Game\Client\Content\Paks`。
- 自动获取并缓存鸣潮 AES Keys。
- 挂载一次后进入交互模式。
- 搜索资源路径、定位短名。
- 查看 Package ImportMap。
- 导出 CUE4Parse Properties JSON。
- 从已解析对象中提取正向路径引用。
- 建立 SQLite 持久化 ImportMap 反向引用索引。
- 索引建立后，多个 `backrefs` 查询直接复用同一份数据库。
- 索引支持中断后续建、失败包重试、客户端更新检测。
- 保留 `backrefs --scan` 作为逐包直接扫描的诊断手段。
- 保留 `grep/rawgrep` 作为字符串线索补充。

## 数据位置

程序继续采用便携式目录结构。设置、索引都跟随 EXE：

```text
WuwaAssetProbe.exe
Data\
  cli-settings.json
  reference-index-v1.sqlite
```

更换新版 EXE 时，可以手动把旧版 `Data` 文件夹一起移动到新版目录。

## 反向引用索引

旧版 `backrefs` 每次查询都会重新遍历全部 UE Package。完整客户端约 85 万个 Package 时，一次查询可能需要几十分钟。

新版把一次全局 ImportMap 扫描结果保存到：

```text
Data\reference-index-v1.sqlite
```

推荐第一次启动后先执行：

```text
index build
```

首次建立仍然需要扫描全部 UE Package。扫描完成后，同一客户端版本中的后续 `backrefs` 会直接查询 SQLite，不再重新扫 85 万个包。

索引按包分批提交。程序中途退出后，再次执行：

```text
index build
```

会继续处理尚未写入数据库的包。

客户端 Paks/IoStore 文件发生变化时，程序会通过容器指纹识别旧索引，并要求重建，防止旧版本引用关系混入新版本。

### 索引命令

```text
index status
index build [--jobs N]
index rebuild [--jobs N]
index retry [--jobs N]
index clear
```

- `status`：显示索引是否可用、已索引包数量、失败包数量和硬引用边数量。
- `build`：首次建立或继续未完成的索引。
- `rebuild`：删除现有索引后完整重建。
- `retry`：只重新处理此前解析失败的 Package。
- `clear`：删除索引数据库。

默认并发数根据 CPU 核心数自动选择，范围 4–12；也可用 `--jobs N` 手动指定。

## backrefs

```text
backrefs <资源名或路径> [--path 路径过滤] [--max N] [--jobs N]
```

默认使用 SQLite 索引。

首次查询时如果索引尚未建立，程序会自动建立；为了操作更明确，仍推荐先主动执行 `index build`。

示例：

```text
backrefs T_Aimisi_Sub_40001
backrefs T_Aimisi_Sub_40002
backrefs T_AMS_atlas_12001
backrefs T_Aimisi_Sub_40001 --path Aki/Effect
```

### 匹配规则

反向索引优先保存、比较完整标准化 Package 路径，例如：

```text
/Game/Aki/Effect/Texture/WenliNoMip/T_Aimisi_Sub_40001
```

`Client/Content/...`、`/Game/...`、对象路径中的 `.ObjectName` 会统一成同一种 Package 路径。

短资源名只用于安全补全：只有该短名在整个客户端中唯一对应一个 `.uasset/.umap` 时才会使用。存在多个同名 Package 时不会通过短名产生反向引用命中。

这可以避免旧版 `targetNames.Contains(name)` 一类逻辑造成的同名假阳性。

### 直接扫描诊断

```text
backrefs T_Aimisi_Sub_40001 --scan
```

`--scan` 绕过 SQLite，再执行一次逐包 ImportMap 语义扫描。它主要用于核对索引结果，速度仍然较慢。

## imports

```text
imports <资源名或路径>
imports <资源名或路径> --assets
```

普通模式保留完整 ImportMap 输出，因此 Texture2D 本身常会看到：

```text
/Script/Engine
/Script/Engine.Texture2D
/Script/OodleTextureStorageProvider
...
```

`--assets` 只显示标准化后的实际资源 Package 依赖，适合检查：

```text
MaterialInstance -> Texture2D
Niagara -> MaterialInstance
Prefab -> Niagara
```

例如：

```text
imports MI_Aimisi_Sub_40001_S --assets
imports MI_Trans_Sub_21004_S --assets
```

## 其他命令

```text
search <文本> [--max N]
where <资源名或路径>
refs <资源名或路径>
dump <资源名或路径>
grep <字符串> [--path 路径过滤] [--max N] [--jobs N]
exit
```

`where` 支持：

- 资源短名
- `Client/Content/...`
- `/Game/...`
- UE 对象路径

`dump` 输出到程序目录下的 `Output`。

`grep/rawgrep` 继续执行 UTF-8/UTF-16 原始字符串扫描，只作为软引用、名称或特殊序列化内容的补充线索。

## 当前引用层级

当前 SQLite 索引保存的是 CUE4Parse `ImportMap + ResolvePackageIndex` 得到的语义硬引用关系。

它适合当前爱弥斯资源链的主要追踪：

```text
Texture2D
<- MaterialInstance
<- Niagara
<- Prefab
<- DataAsset
<- Animation/Montage
```

软引用、Niagara 编译数据、RapidIteration、动态材质参数等仍可能需要结合：

```text
refs
dump
grep
```

继续验证。

## 爱弥斯当前推荐测试

第一次：

```text
index build
index status
```

索引完成后：

```text
backrefs T_Aimisi_Sub_40001
backrefs T_Aimisi_Sub_40002
backrefs T_AMS_atlas_12001
backrefs T_Aimisi_Sub_40003
backrefs T_Aimisi_Sub_40004
backrefs T_Aimisi_Sub_40006

imports MI_Aimisi_Sub_40001_S --assets
imports MI_Trans_Sub_21004_S --assets
imports MI_Trans_Sub_21007_S --assets
imports MI_Trans_Sub_21008_S --assets
imports MI_Trans_Sub_21009_S --assets
imports MI_Trans_Sub_21012_S --assets
```

## 分支

```text
cli/quick-asset-probe
```

CLI 仍是当前快速验证器。确认解析逻辑成熟后，再将引用索引和路径标准化能力下沉到 Core，供 WinUI 3 前端复用。
