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
  <a href="docs/README.md"><strong>アーキテクチャを読む »</strong></a>
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
エンジン非依存の Pure C# ランタイム（最初のターゲットエンジンは Unity）です。ピクセルやスクリーンショット経由で UI を操作
するのではなく、**セマンティック UI ツリー**（各インタラクタブル要素を `id` / `role` /
`label` / 現在値 / `enabled`・`visible` 状態 / いま許可された操作として観測できる形）を
公開します。これにより、コマンドのシーケンス実行・記録・決定論的なリプレイが可能に
なります。

これが可能にするのは 2 つです。**再現性のあるデバッグ**（失敗したセッションを記録し、
アプリ固有のハンドラごと同一コマンド列を再生する）と、**MCP 経由での AI エージェントに
よるスクリーンショット不要な操作**（エージェントがその画面で可能な操作を列挙し、直接
駆動する）です。UI をデータとして観測・操作したい Unity アプリ／ゲーム開発チームを対象と
しています。

> **ステータス:** エンジン非依存のコアは実装済みです — 単一オーナー kernel と typed
> contracts、completeness 付き観測層と content-addressed な canonical state、E1–E8
> evidence による durable recording、隔離双子環境での replay と typed 三値比較、seal
> evaluator、そしてリポジトリ内 reference adapter が end-to-end で合格する TCK。次の
> ロードマップ項目は protocol gateway(MCP 面)と Unity adapter です。保証の正典は
> [docs/spec/guarantees.md](docs/spec/guarantees.md) です。

### 使用技術

- **Pure C#**(.NET Standard 2.1、C# 9) — ランタイムはパッケージ依存ゼロ
- **[Unity 6](https://unity.com/)** — 最初のターゲットエンジン(adapter は今後。コアは Unity に非依存)
- **[Model Context Protocol](https://modelcontextprotocol.io/)**(MCP) — エージェント向け面(gateway は今後)

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="features"></a>

## 機能

kernel・観測・recording・replay の各層は実装済みで、protocol gateway と Unity adapter が残りのロードマップ項目です。

- **単一オーナー kernel** — 単一 mutation lane、split-phase adapter protocol、exactly-once completion、typed failure matrix。全保証は [docs/spec/](docs/spec/) の規範表。
- **セマンティック観測** — completeness map 付き projected view と content-addressed canonical state。snapshot の `ContentId` は portable な verify-before-use digest。
- **Durable recording** — 追記専用 evidence artifact（E1–E8 cut、delta 符号化 checkpoint、droppable な diagnostics timeline）。crash-honest で reader が分類する形式。
- **隔離 replay** — pre-scan の信頼境界、隔離双子環境、`Equal | Diverged | Incomparable(reason)` を答える typed 厳密比較。CI 検証ケース化の可否は seal evaluator が判定。
- **Adapter TCK** — あらゆるエンジン adapter が合格すべき conformance kit。リポジトリ内 reference adapter が end-to-end で合格。
- **規範化された性能** — 静止 pump は 0 バイト確保、仕事量は投入量に比例。CI の allocation gate が強制。

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="getting-started"></a>

## はじめに

まだリリースパッケージはありません。version 0.1.0 の実装基盤をローカルで build するには
リポジトリをクローンしてください。

<a id="prerequisites"></a>

### 前提条件

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

ランタイムは kernel と adapter SDK のアセンブリ群
(`SignalRouter.Contracts`、`SignalRouter.Kernel`、`SignalRouter.AdapterSdk`、codec
leaf 群、`SignalRouter.Recording`、`SignalRouter.Replay`、`SignalRouter.Tck`)として
利用します。エンジン adapter は adapter SDK のシームを実装し、TCK で適合を証明します —
`src/ReferenceAdapter` が実例です。リリースパッケージはまだありません。アーキテクチャは
[docs/README.md](docs/README.md)、保証の正典は
[docs/spec/guarantees.md](docs/spec/guarantees.md) を参照してください。

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="development"></a>

## 開発

repository root から次の wrapper を実行します。

```sh
task build
task test
task check
```

全ランタイムアセンブリは C# 9・`netstandard2.1`・パッケージ依存ゼロで compile し、
warning も build failure にします。正確な toolchain と互換性境界は
**[docs/development.md](docs/development.md)** を参照してください。

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
| [docs/README.md](docs/README.md) | 設計文書の入口 — philosophy・architecture・spec 一式 |
| [guarantees.md](docs/spec/guarantees.md) | 規範的な保証カタログ（evidence・outcome・failure matrix） |
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

- [Model Context Protocol](https://modelcontextprotocol.io/) — エージェント向けプロトコル
- [oss-project-template](https://github.com/container-registry/oss-project-template) — リポジトリ自動化とコミュニティ文書の基盤

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>
