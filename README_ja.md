<a id="readme-top"></a>

<div align="center">

[English](./README.md) | 日本語

<!-- TODO: ロゴを追加してコメントを外してください
<img src="assets/logo.png" alt="SignalRouter logo" width="120" height="120">
-->

<h1>SignalRouter</h1>

<p><em>UI操作を構造化コマンドとしてシミュレート・リプレイし、再現性のあるデバッグとスクリーンショット不要なMCPエージェント操作を可能にする（Pure C# + Unity）。</em></p>

[![CI](https://github.com/dreamingdog0529/SignalRouter/actions/workflows/ci.yml/badge.svg)](https://github.com/dreamingdog0529/SignalRouter/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/dreamingdog0529/SignalRouter?include_prereleases&sort=semver)](https://github.com/dreamingdog0529/SignalRouter/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/dreamingdog0529/SignalRouter/badge)](https://securityscorecards.dev/viewer/?uri=github.com/dreamingdog0529/SignalRouter)

<p>
  <a href="docs/design.md"><strong>アーキテクチャを読む »</strong></a>
  <br /><br />
  <a href="https://github.com/dreamingdog0529/SignalRouter/issues/new?template=bug_report.yml">バグ報告</a>
  ·
  <a href="https://github.com/dreamingdog0529/SignalRouter/issues/new?template=feature_request.yml">機能リクエスト</a>
  ·
  <a href="https://github.com/dreamingdog0529/SignalRouter/discussions">ディスカッション</a>
</p>

</div>

<details>
  <summary>目次</summary>
  <ol>
    <li><a href="#about">概要</a></li>
    <li><a href="#features">機能</a></li>
    <li>
      <a href="#getting-started">はじめに</a>
      <ul>
        <li><a href="#prerequisites">前提条件</a></li>
        <li><a href="#installation">インストール</a></li>
      </ul>
    </li>
    <li><a href="#usage">使い方</a></li>
    <li><a href="#development">開発</a></li>
    <li><a href="#roadmap">ロードマップ</a></li>
    <li><a href="#contributing">コントリビュート</a></li>
    <li><a href="#project-docs">プロジェクト文書</a></li>
    <li><a href="#license">ライセンス</a></li>
    <li><a href="#acknowledgments">謝辞</a></li>
  </ol>
</details>

<a id="about"></a>

## 概要

<!-- TODO: スクリーンショットを追加してコメントを外してください
<img src="assets/screenshot.png" alt="SignalRouter screenshot">
-->

SignalRouter は、UI 操作を **構造化された、シリアライズ可能なコマンド** として表現する
Unity ランタイム（コアは Pure C#）です。ピクセルやスクリーンショット経由で UI を操作
するのではなく、**セマンティック UI ツリー**（各インタラクタブル要素を `id` / `role` /
`label` / 現在値 / `enabled`・`visible` 状態 / いま許可された操作として観測できる形）を
公開します。これにより、コマンドのシーケンス実行・記録・決定論的なリプレイが可能に
なります。

これが可能にするのは 2 つです。**再現性のあるデバッグ**（失敗したセッションを記録し、
アプリ固有のハンドラごと同一コマンド列を再生する）と、**MCP 経由での AI エージェントに
よるスクリーンショット不要な操作**（エージェントがその画面で可能な操作を列挙し、直接
駆動する）です。UI をデータとして観測・操作したい Unity アプリ／ゲーム開発チームを対象と
しています。

> **ステータス:** .NET-first の build/test 基盤に加え、command model、
> immutable command catalog と codec、structured result model、semantic registry を
> 実装済みです。FIFO dispatch、stage 実行と state probe、record/replay、Unity UI、
> WebSocket、MCP のプロダクション機能は未実装です。対応範囲と受け入れ基準は
> [アーキテクチャ資料](docs/design.md) に定義しています。

### 使用技術

- **[Unity 6](https://unity.com/)**（MVP は uGUI） — 観測・駆動対象の UI ランタイム
- **Pure C#**（.NET Standard 2.1） — コアは Unity に非依存
- **[VitalRouter](https://github.com/hadashiA/VitalRouter)** — 単一のプロセス内コマンドバス
- **[Model Context Protocol](https://modelcontextprotocol.io/)**（MCP） — エージェント向けツール面。WebSocket でランタイムにブリッジ

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="features"></a>

## 機能

core model は実装済みで、runtime 実行と transport 機能は計画中です。

- **セマンティック UI registry（core 実装済み）** — 登録済み要素（`id` / `role` / `label` / 値 / 状態）と catalog 検証済み操作を決定的な snapshot として観測。
- **構造化コマンド（core 実装済み）** — 操作を C# 9 互換の immutable value（`click` / `set_value`）と厳密な versioned JSON codec でモデル化。
- **記録 & リプレイ** — 全コマンドが必ず `IInteractionDispatcher` を通るため、terminal result 確認（キュー受理≠完了）付きで決定論的に再生。
- **決定論的な例外モデル** — Sequential 実行で `Rejected`（検証落ち・副作用ゼロ）と `Faulted`（stage *k* で失敗・*k−1* まで適用）を分離し、中断点を正確に再現。
- **MCP エージェント操作** — `get_ui_tree` / `wait_for` と実行系ツールでピクセルなしに UI を駆動。例外はシームで catch し、MCP 境界に漏らさない。

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="getting-started"></a>

## はじめに

まだリリースパッケージはありません。version 0.1.0 の実装基盤をローカルで build するには
リポジトリをクローンしてください。

<a id="prerequisites"></a>

### 前提条件

- 標準 Unity Hub path にインストールした Unity 6000.5.4f1
- .NET SDK 10.0.302
- PowerShell 7 と [Task](https://taskfile.dev/)
- `task check` 用の [typos](https://github.com/crate-ci/typos)

<a id="installation"></a>

### インストール

```sh
git clone https://github.com/dreamingdog0529/SignalRouter.git
cd SignalRouter
```

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="usage"></a>

## 使い方

`SignalRouter.Core` は `ClickCommand`、`SetValueCommand`、immutable
`InteractionCommandCatalog`、structured `InteractionResult`、
lifetime-scoped `InteractionRegistry` を公開します。`IInteractionDispatcher` と typed
pipeline contract は将来の実行境界を定義しますが、dispatcher と stage executor は未実装です。
現在の保証と後続 runtime の範囲は [アーキテクチャ資料](docs/design.md) を参照してください。

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="development"></a>

## 開発

repository root から次の wrapper を実行します。

```sh
task build
task test
task check
```

`SignalRouter.Core` と `SignalRouter.Protocol` は C# 9・`netstandard2.1` として compile し、
warning も build failure にします。Unity 開発 project では C# 11 language feature test のため
`-langversion:preview` を有効にしますが、配布される NuGet package の利用者に preview 設定は
要求しません。正確な toolchain と互換性境界は **[docs/development.md](docs/development.md)** を参照してください。

コントリビュート手順: **[CONTRIBUTING.md](.github/CONTRIBUTING.md)**

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="roadmap"></a>

## ロードマップ

計画中の機能や既知の課題は [Issues](https://github.com/dreamingdog0529/SignalRouter/issues) と
[ROADMAP.md](ROADMAP.md) を参照してください。

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="contributing"></a>

## コントリビュート

コントリビュートを歓迎します。ワークフロー（Conventional Commits・DCO サインオフ・PR 手順）は
**[CONTRIBUTING.md](.github/CONTRIBUTING.md)** を、コミュニティ標準は
[行動規範](.github/CODE_OF_CONDUCT.md) を参照してください。

貢献者一覧は英語 README の [Contributors](README.md#contributing) を参照してください（git 履歴から自動更新）。

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="project-docs"></a>

## プロジェクト文書

リポジトリの自動化とコミュニティ文書は
[container-registry/oss-project-template](https://github.com/container-registry/oss-project-template)
を基にしています。

| 文書 | 内容 |
|------|------|
| [design.md](docs/design.md) | アーキテクチャ・保証・互換性・MVP 受け入れ基準 |
| [development.md](docs/development.md) | 現在の開発状況とツール |
| [CONTRIBUTING.md](.github/CONTRIBUTING.md) | 開発・テスト・PR・DCO・CI/CD・リリース |
| [SUPPORT.md](.github/SUPPORT.md) | サポートの受け方 |
| [ROADMAP.md](ROADMAP.md) | 方向性と提案の仕方 |
| [CODE_OF_CONDUCT.md](.github/CODE_OF_CONDUCT.md) | 行動規範 |
| [SECURITY.md](.github/SECURITY.md) | 脆弱性の非公開報告 |
| [CODEOWNERS](CODEOWNERS) | デフォルトのレビュー担当 |
| [CHANGELOG.md](CHANGELOG.md) | 変更履歴 |
| [LICENSE](LICENSE) | MIT ライセンス本文 |

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="license"></a>

## ライセンス

MIT ライセンスで配布しています。詳細は [LICENSE](LICENSE) を参照してください。

MIT © 2026 dreamingdog0529

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="acknowledgments"></a>

## 謝辞

- [VitalRouter](https://github.com/hadashiA/VitalRouter) — プロセス内コマンドルーティング
- [Model Context Protocol](https://modelcontextprotocol.io/) — エージェント向けプロトコル
- [oss-project-template](https://github.com/container-registry/oss-project-template) — リポジトリ自動化とコミュニティ文書の基盤

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>
