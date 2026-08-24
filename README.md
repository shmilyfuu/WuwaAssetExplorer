# WuwaAssetExplorer

A Windows 11 native WinUI 3 asset explorer for Wuthering Waves.

The project is intended to provide FModel-like fast package-name/path search plus persistent forward/reverse asset-reference queries, while reading the game archives directly through CUE4Parse.

## Initial goals

- Native Windows App SDK / WinUI 3 UI.
- Unpackaged, self-contained x64 distribution for portable use.
- Direct Wuthering Waves archive loading with `GAME_WutheringWaves`.
- AES endpoint support compatible with the maintained Wuthering Waves key endpoint.
- Fast in-memory asset catalog search.
- Persistent reference index for upstream/downstream dependency queries.
- Incremental index rebuilds when game containers change.

Development is in the bootstrap phase.
