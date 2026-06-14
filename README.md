# Finisher

敵を**ダウンではなくキルした瞬間**に、某ゲーム風のフィニッシャーエフェクトをキル地点へ表示する Rust (Oxide/uMod) プラグイン。

魔法陣リング・衝撃波・光の柱・火花・キル文字が一斉に立ち上がり、任意で3Dエンブレム（OBJモデル）も表示できる。描画とモデル変換ツールは [jerkypaisen/rust-custom-3d-display](https://github.com/jerkypaisen/rust-custom-3d-display) の `MeshSurfaceDraw` の手法（admin昇格 → `ddraw.*` 送信 → 復帰、三角形のスキャンライン充填）を流用している。

---

## 特長

- **「ダウン」と「キル」を正確に区別** — Rust の `OnEntityDeath` フックは実際の死亡（`Die()`）でのみ発火し、瀕死で倒れる「ダウン（wounded）」では発火しない。これだけで確実にキルだけを拾える。
- **某ゲーム風の手続き的エフェクト**（個別にON/OFF・色変更可）
  - 地面の魔法陣リング（逆回転する二重リング＋六芒星）
  - 外へ広がって消える衝撃波リング
  - 立ち上がる光の柱
  - 上空へ飛び散り重力で落ちる火花
  - キル文字バナー（キラー名・ヘッドショット表示つき）
- **任意の3Dエンブレム** — OBJモデルをスキャンライン充填でキル地点に表示（遺体から立ち上がる演出）。面色／テクスチャ両対応。
- **クライアント改造不要** — バニラクライアントにそのまま表示される。

## 動作要件

- Rust 専用サーバー + [Oxide/uMod](https://umod.org/)
- プレイヤーの権限は不要（既定）。`ddraw` の都合上、エフェクトが見える範囲のプレイヤーを一時的に admin 昇格させ、終了時に元へ戻す（参照カウントで複数エフェクトを安全に共有）。

## インストール

1. `Finisher.cs` を `oxide/plugins/` に置く
2. サーバーが自動でコンパイル・ロードする
3. 設定ファイル `oxide/config/Finisher.json` が生成される

## 権限

| 権限 | 用途 |
|---|---|
| `finisher.see` | （`Require Permission To See` を true にした場合）エフェクトが見える |
| `finisher.trigger` | （`Require Permission To Trigger` を true にした場合）キル時にエフェクトを発生させられる |

既定ではどちらも不要（全員に発生・全員に表示）。

## 設定（`oxide/config/Finisher.json`）

| キー | 既定 | 説明 |
|---|---|---|
| `Effect Duration (seconds)` | `2.6` | エフェクトの長さ |
| `Update Interval (seconds, smaller = smoother/heavier)` | `0.08` | 更新間隔。小さいほど滑らかだが重い |
| `View Distance (m)` | `60` | エフェクトが見える距離 |
| `Occluded By Terrain/Buildings (zTest)` | `true` | 地形・建物に隠れる |
| `Require Permission To See (finisher.see)` | `false` | 見るのに権限を必須にする |
| `Require Permission To Trigger (finisher.trigger)` | `false` | 発生させるのに権限を必須にする |
| `Trigger On NPC Kills (animals/scientists/etc.)` | `true` | NPCを倒したときも出す |
| `Players And NPCs Only (no buildings/etc.)` | `true` | プレイヤー/NPC以外の被害者では出さない |
| `Skip Suicide And Team Kills` | `true` | 自殺・同士討ちでは出さない |
| `Headshot Kills Only` | `false` | ヘッドショットキルのときだけ出す |
| `Primary Color RGBA` | `[1, 0.12, 0.16, 1]` | メインカラー（既定=赤） |
| `Accent Color RGBA` | `[1, 0.35, 0.30, 1]` | アクセントカラー（既定=赤） |
| `Show Ground Rune Rings` | `true` | 地面の魔法陣リング |
| `Show Shockwave Ring` | `true` | 衝撃波リング |
| `Show Rising Light Pillars` | `true` | 上昇する光の柱 |
| `Show Sparks` | `true` | 火花 |
| `Spark Count` | `26` | 火花の本数 |
| `Show Text` | `true` | 文字バナー |
| `Banner Text` | `"ELIMINATED"` | 表示する文字 |
| `Ring Max Radius (m)` | `1.6` | リングの最大半径 |
| `Effect Height (m)` | `2.4` | エフェクトの高さ |
| `Emblem OBJ Name (empty = off)` | `""` | 3DエンブレムのOBJ名（空でOFF） |
| `Tint Emblem With Primary Color (ignore texture)` | `true` | エンブレムをメインカラーの単色にする（false で元のテクスチャ色） |
| `Emblem Scale` | `1` | エンブレムのスケール（1.0=高さ1m） |
| `Emblem Max Lines` | `8000` | エンブレムの線数上限 |
| `Emblem Line Spacing (m, smaller = denser)` | `0.02` | エンブレムの線間隔。小さいほど濃い |

> 既定では全エフェクトが**赤**で統一されている。色を変えたいときは `Primary Color RGBA` / `Accent Color RGBA` を編集する。

## 3Dエンブレム（任意）

`Emblem OBJ Name` にモデル名を設定すると、`oxide/data/Finisher/<名前>.obj`（無ければ `oxide/data/MeshDraw/<名前>.obj`）を読み込み、キル地点に正面固定で浮かべる。

MeshSurfaceDraw と同じ拡張OBJフォーマットに対応（描画もスキャンライン充填を流用）:

- **面色モード**: `v` / `c R G B`（面色） / `f`
- **テクスチャモード**: `v` / `vt` / `tex W H` + `tx <hex>` / `f v/vt`（塗り線を色の変わり目で分割してテクスチャの描き込みを再現）

重ね描き（passes）やインナーコアは使わず **1層**。スキン/アニメは非対応（短時間の静止表示のため）。大量の `ddraw` を一度に投げてスパイクしないよう、エンブレムの線は複数フレームに分割して送る。

### 利用できるモデルのスペック

エンブレムには、自分で用意した任意のモデルを使える。変換ツール `model2mdraw.py` が次の形式を読み込めるので、それを拡張OBJ（`.obj`）に変換して使う。

| 項目 | 推奨／対応範囲 | 補足 |
|---|---|---|
| 入力形式 | `OBJ` / `GLB` / `GLTF` / `STL` / `PLY` / `3MF` / `PMX` | **FBXは非対応**（Blender等で一度GLBに書き出す） |
| 面数（ポリゴン） | **4,000〜8,000 面**（三角形）が目安 | 多いほど綺麗だが線が増えて重い。`-f <面数>` で削減。`Emblem Max Lines`（既定8000）で線数も頭打ちになる |
| テクスチャ | あり（推奨）／なし どちらも可 | あり=`map_Kd` のPNG等。変換時に最大辺 `--texsize`（既定512px）のアトラスへ縮小して埋め込む。**透過(アルファ)は表現できない**（不透明として扱う） |
| UV | テクスチャを使うなら必須 | UVが無い／色だけのモデルは「面色モード」で各面の代表色になる |
| 色（テクスチャなし時） | 頂点色・面色・単色マテリアル | 面ごとの代表色 `c R G B` として焼き込まれる |
| 形状 | 中身が詰まっていなくてもよい（薄板・開いたメッシュも可） | 全方向から塗るので裏面も見える |
| サイズ | 任意（変換時に `-H <m>` で高さ正規化） | 既定は高さ1m。ゲーム内では `Emblem Scale` でさらに調整 |
| ボーン/アニメ | 不可（無視される） | エンブレムは静止表示のみ |

**ポイント**

- **低〜中ポリ・単一マテリアル・テクスチャ付き**が最も扱いやすい（生成AIモデルやゲーム向けアセットが好相性）。
- 高ポリ（数万面）でも `-f` で削減すれば使えるが、ディテールは線の密度なりになる。
- 細い棒・髪の毛のような薄い部位や、透過で抜いた形状は線描画と相性が悪い（`model2mdraw.py` は透明面を自動削除する）。
- アイコン／ロゴのような単純な形状は面数が少なく軽くて綺麗に出る。

### サンプルモデルの変換

`sample/` のモデルを、[jerkypaisen/rust-custom-3d-display](https://github.com/jerkypaisen/rust-custom-3d-display) の変換ツール `tools/model2mdraw.py` で変換済み（`sample/emblem.obj`）。生成し直す場合は、リポジトリを clone して:

```bash
git clone https://github.com/jerkypaisen/rust-custom-3d-display
python rust-custom-3d-display/tools/model2mdraw.py \
       sample/model.obj -f 6000 -H 1.0 -o sample/emblem.obj
```

- テクスチャ付きモデルは自動で「テクスチャ埋め込みモード」になり、`emblem.obj` にテクスチャ（512²アトラス）も埋め込まれる
- `-f 6000`: 10343面 → 6000面に削減（ライン描画は 4000〜8000面が目安）
- `-H 1.0`: 高さ1mに正規化
- 仕上がりは `sample/preview_lines.png` で確認できる

### 使い方

1. `sample/emblem.obj` をサーバーの `oxide/data/Finisher/emblem.obj` にコピー
2. `Emblem OBJ Name` を `emblem` に設定
3. （任意）`Emblem Scale` で大きさを調整

既定では `Tint Emblem With Primary Color = true` なので、エンブレムも赤一色で描かれる。元のテクスチャ色（ピンク/緑）で出したい場合は `false` にする。

## 仕組み（技術メモ）

`ddraw.*` はサーバーから admin フラグの立ったクライアントにしか描画コマンドを送れない。本プラグインはエフェクトが見える範囲のプレイヤーを一時昇格させて線を送り、終了時に元へ戻す。エフェクトは線が疎な手続き的プリミティブが主役なので、MeshSurfaceDraw が突き当たった「全身スキンの滑らかアニメ＝スループットの壁」には当たらない。

## ライセンス

MIT
