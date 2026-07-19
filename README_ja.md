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
  <a href="docs/development.md"><strong>ドキュメントを見る »</strong></a>
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

> **ステータス:** 設計フェーズ — MVP は未着手です。以下のアーキテクチャとインター
> フェースはドラフトです。意思決定の経緯と未決事項は [設計資料](docs/design.md) を参照してください。

### 使用技術

- **[Unity](https://unity.com/)**（uGUI / UI Toolkit） — 観測・駆動対象の UI ランタイム
- **Pure C#**（.NET Standard 2.1） — コアは Unity に非依存
- **[MessagePipe](https://github.com/Cysharp/MessagePipe)** — コアの req/res シームでの `RequestAll` fan-out
- **[VitalRouter](https://github.com/hadashiA/VitalRouter)** — 要素内の pub/sub 用コマンドバスの選択肢
- **[Model Context Protocol](https://modelcontextprotocol.io/)**（MCP） — エージェント向けツール面。WebSocket でランタイムにブリッジ

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="features"></a>

## 機能

- **セマンティック UI ツリー** — 各インタラクタブル要素（`id` / `role` / `label` / 値 / 状態）を、いま何ができるかのスクショ不要な source of truth として観測。
- **構造化コマンド** — UI 操作をシリアライズ可能な `record struct` コマンド（`click` / `set_text` …）としてモデル化。エージェント・テスト・実プレイの入力を単一のコマンド型で統一。
- **記録 & リプレイ** — 全コマンドが必ずコアの単一シームを通り記録されるため、host completion 確認（キュー受理≠完了）付きで決定論的に再生。
- **決定論的な例外モデル** — Sequential 実行で `Rejected`（検証落ち・副作用ゼロ）と `Faulted`（stage *k* で失敗・*k−1* まで適用）を分離し、中断点を正確に再現。
- **MCP エージェント操作** — `get_ui_tree` / `wait_for` と実行系ツールでピクセルなしに UI を駆動。例外はシームで catch し、MCP 境界に漏らさない。

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="getting-started"></a>

## はじめに

本プロジェクトは設計フェーズのため、まだリリースパッケージはありません。アーキテク
チャを追ったり試したりするにはリポジトリをクローンしてください。

<a id="prerequisites"></a>

### 前提条件

- Unity 2022 LTS 以降（uGUI および／または UI Toolkit）
- Pure C# コア用の .NET Standard 2.1 ツールチェーン
- req/res シーム用の [MessagePipe](https://github.com/Cysharp/MessagePipe)
- エージェントから UI を駆動する場合は MCP 対応クライアント

<a id="installation"></a>

### インストール

```sh
git clone https://github.com/dreamingdog0529/SignalRouter.git
cd SignalRouter
```

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="usage"></a>

## 使い方

公開 API は設計中です。想定している形は、各インタラクタブル要素が実装する単一のコア
契約で、検証・順序採番・記録・例外処理は req/res シームでコアが担います。

```csharp
// コアが UI 要素に要求する唯一の契約。
public interface IInteractable {
    ElementDescriptor Describe();                 // get_ui_tree 用
    bool CanAccept(in UiCommand cmd);             // 検証（Publish 前）
    UniTask<ExecuteResult> ExecuteAsync(          // pub → 全 sub 完了 await → res
        UiCommand cmd, CancellationToken ct);
}
```

MCP エージェントは `get_ui_tree` で可能な操作を確認し、コマンドを発行し、複数フレーム
にまたがる settle 待機には `wait_for` を使います（スクショ不要）。具体的なシグネチャは
MVP で確定します。

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>

<a id="development"></a>

## 開発

```sh
dotnet build
dotnet test
```

詳細な開発・ビルド手順: **[docs/development.md](docs/development.md)**
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

<!-- TODO: プロジェクトが依拠するリソース・ライブラリ・人物を列挙してください。下の例は置き換えてください。 -->

- [リソース名](https://example.com) — 提供内容

<p align="right">(<a href="#readme-top">トップへ戻る</a>)</p>
