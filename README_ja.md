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

> **ステータス:** version 0.1.0 では UPM package、共有ソースを使う .NET project、
> 自動 build/test 基盤まで構築済みです。dispatcher、command、result、registry、Unity UI、
> MCP のプロダクション機能は未実装です。対応範囲と受け入れ基準は
> [アーキテクチャ資料](docs/design.md) に定義しています。

### 使用技術

- **[Unity 6](https://unity.com/)**（MVP は uGUI） — 観測・駆動対象の UI ランタイム
- **Pure C#**（.NET Standard 2.1） — コアは Unity に非依存
- **[VitalRouter](https://github.com/hadashiA/VitalRouter)** — 単一のプロセス内コマンドバス
- **[Model Context Protocol](https://modelcontextprotocol.io/)**（MCP） — エージェント向けツール面。WebSocket でランタイムにブリッジ

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="features"></a>

## 機能

以下は version 0.1.0 時点では未実装の計画機能です。

- **セマンティック UI ツリー** — 各インタラクタブル要素（`id` / `role` / `label` / 値 / 状態）を、いま何ができるかのスクショ不要な source of truth として観測。
- **構造化コマンド** — 操作を C# 9 互換のシリアライズ可能な immutable value type（`click` / `set_value` …）としてモデル化。エージェント・テスト・リプレイ・人間の入力を単一経路に統一。
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

version 0.1.0 は dispatcher、command、result、registry の公開 API を意図的に実装して
いません。UPM package は現時点で `SignalRouter.Core` と `SignalRouter.Protocol` の assembly
境界のみを提供します。計画中の API と動作は
[アーキテクチャ資料](docs/design.md) を参照してください。

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="development"></a>

## 開発

repository root から次の wrapper を実行します。

```sh
task build
task test
task check
```

共有 Runtime source は C# 9・`netstandard2.1` として compile し、warning も build failure
にします。Unity 開発 project では C# 11 language feature test のため
`-langversion:preview` を有効にしますが、UPM package 利用者に preview 設定は要求しません。
正確な toolchain と互換性境界は **[docs/development.md](docs/development.md)** を参照してください。

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
