# Architecture

## Product constraints

- Windows 11-first desktop app.
- UI implemented with Windows App SDK / WinUI 3 only.
- Portable unpackaged x64 distribution; no installer required.
- Reads Wuthering Waves archives directly instead of automating FModel.
- Current keys are retrieved from a configurable AES Endpoint and cached locally.
- Normal asset-name/path search must remain usable independently from the heavier reference index.

## Layers

### App

WinUI 3 shell and interaction only. The initial shell uses system Mica, `NavigationView`, `AutoSuggestBox`, `ListView`, `InfoBar`, `ProgressRing` and standard Windows theme resources.

### Core / archive provider

`WuwaArchiveService` owns the CUE4Parse `DefaultFileProvider` configured with `GAME_WutheringWaves`. It initializes the VFS, submits main + dynamic AES keys, mounts archives, then snapshots `Provider.Files.Keys` into the resource catalog.

### Core / fast asset catalog

`AssetCatalog` stores a compact sorted path array plus a case-insensitive exact-name dictionary. It deliberately does not parse UObject properties. Search runs against memory and returns at most a bounded result count, so normal typing does not trigger archive reads.

### Core / AES

`AesEndpointService` reads `mainKey` and `dynamicKeys` from a configurable JSON endpoint. A successful response is cached under `Data`. If the endpoint is temporarily unavailable, the cache can be reused.

### Future reference index

The next phase will parse package/object references independently of the fast catalog and persist a graph locally. Planned queries:

- direct dependencies (forward references)
- direct referrers (reverse references)
- recursive dependency/referrer tree
- path between two assets
- incremental container re-indexing after game updates

The reference build must be cancellable and must not block ordinary asset-name/path search.

## Search performance policy

The UI never searches `.pak`/`.ucas` bytes per keystroke. Archive/container indices are loaded once, and search is performed on the resulting in-memory catalog. Query work is cancellable, bounded to 500 displayed results, and uses a short input debounce to avoid redundant work while typing.

After the reference database is introduced, ordinary asset search will continue to use the lightweight catalog; reference queries will use indexed database lookups.
