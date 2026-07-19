# コマンド駆動 UI シミュレーション基盤 — 設計ハンドオフ資料

> **ステータス**: 設計フェーズ / MVP 未着手
> **作成日**: 2026-07-19
> **対象**: Pure C# + Unity（.NET Standard 2.1 想定）
> **仮称**: SignalRouter

---

## 0. この資料について

UI 操作をコマンドとしてシミュレートし、シーケンス実行・リプレイ・MCP 経由のエージェント操作（スクショ不要）を可能にする OSS の設計を、対話で詰めた記録。**「何を決めたか」だけでなく「なぜそう決めたか」と「その決定が何に波及したか」** を残すことを主眼とする。第7章の Gua（先行 OSS）評価は着手前に必ず読むこと。

---

## 1. プロジェクト目的

- UI 操作を **構造化コマンド** として表現し、シミュレート実行する
- コマンドのシーケンス実行と **操作リプレイ**（再現性のあるデバッグ）
- **MCP 経由で AI エージェントがスクショなしに UI を操作** できる
- 「その場面で可能な UI 操作を列挙する」MCP ツールを提供する
- 実装は Pure C# + Unity。バスは VitalRouter / MessagePipe を想定

---

## 2. 確立した中核コンセプト

### 2.1 セマンティック UI ツリー = スクショの代替
ブラウザエージェントが使う **アクセシビリティツリー（a11y ツリー）を Unity UI 向けに自前で持つ** のが核心。全アクティブなインタラクタブル要素を、`id / role / label / 現在値 / enabled / visible / 許可された操作+パラメータスキーマ` として観測可能にする。これがあるからスクショが要らない。

### 2.2 「列挙」には 2 種類ある（混同禁止）
- **(A) コマンドカタログ** — そもそも存在する操作動詞の一覧（`click` / `set_text` …）。静的・スキーマ。MCP ツール `list_commands`。
- **(B) 現在可能な操作** — いまこの画面で、この要素に許可されている操作。動的・文脈依存。MCP ツール `get_ui_tree`。
- ユーザーの言う「その場面で可能な UI 操作の列挙」は **(B)**。ここの品質がエージェントの目隠し操作の成否を決める。

---

## 3. アーキテクチャ全体像

```
                 ┌──────────── core（本基盤所有）────────────┐
 MCPエージェント ─┤  外殻 req/res：検証・全順序採番・記録点     ├─ UI要素 ─┐
 / テスト / リプレイ└───────────────────────────────────────┘         │ 内殻 pub/sub
                                                                        │（要素所有・fan-out）
                                                          ┌─────────────┴─────────────┐
                                                          ▼             ▼             ▼
                                                       処理handler   演出handler   soundhandler
```

| レイヤ | 役割 | 所有 |
|---|---|---|
| L0 Semantic UI Registry | 全要素を追跡、(B) の source of truth | core |
| L1 Command Model | `IUICommand`（record struct）、JSON シリアライズ可 | core |
| L2 Dispatch（外殻 req/res） | 検証→発行→全完了 await→結果集約 | core |
| L2' Dispatch（内殻 pub/sub） | 1操作→N副作用の fan-out | UI要素 |
| L3 Recorder / Replayer | 外殻を盗聴して記録、host completion 確認付き再生 | core |
| L4 MCP Bridge | 外部プロセスの MCP サーバ ⇄ ランタイムを WebSocket 接続 | 別asm |
| L5 MCP Tool Surface | `get_ui_tree` / `wait_for` / 実行系 / record・replay | 別asm |

---

## 4. 意思決定の記録（決定トレイル）

> ここが本資料の核心。各決定は **論点 / 結論 / 理由 / 波及** で記す。

### D1. req/res と pub/sub の関係
- **論点**: MCP ツール呼び出し（1req→1res が必須）と、UI 操作の N 副作用波及（fan-out）をどう両立するか。
- **結論**: **排他ではなく入れ子**にする。外殻 = req/res、内殻 = pub/sub。
- **理由**: MCP の `click` は本質的に RPC で「1呼び出し1結果」を必ず要求する。一方「クリックが起きた」事実は処理・演出・音・Recorder へ N 波及する。形が違うものを 1 本にしない。
- **波及**: 結果回収の設計（D6）と記録点の位置（D5）が、この入れ子構造に従属する。

### D2. 一級市民 か 内部配管 か
- **論点**: UI コマンドを「アプリの既存バスに乗る一級市民」にして、アプリ自前のハンドラ（`[Routes]` / `ISubscriber<T>`）を自動発火させるか。それともシミュフレーム内部の配管に留めるか。
- **結論**: **内部配管（＝一級市民は要らない, "no"）**。
- **理由**: ユーザー判断。アプリのバス設計に侵入しない疎結合を優先。
- **波及**: ただし「アプリ巻き込みは不要」でも「シミュフレーム内の 1→N fan-out（処理/演出/音）」は欲しい、という区別が D3 を生む。

### D3. ユーザーの最終モデル（本設計の骨格）
- **結論**: `request → UI要素 → pub → sub群（処理/演出/音）→ 全完了 → UI要素が response`。
- **要点**:
  - **UI 要素が単一の応答主体**。「自分が pub し、全 sub の完了を await し、まとめて res で返す」責任を持つ → 結果の所在が一意になり相関の曖昧さが消える。
  - **fan-out はアプリのバスに侵入せず、UI 要素の内側に閉じ込める**。「一級市民は不要（D2）だが fan-out は欲しい」を両立。
- **波及**: バス選択の境界が「core が選ぶ」から「要素が内殻で自由に使う」へ移動（D4）。

### D4. バス選択の自由化 → 抽象化
- **論点**: core が VitalRouter / MessagePipe のどちらを使うか（当初は排他選択制にする方針だった）。
- **結論（中間）**: **要素が何で内殻 pub/sub するかはユーザーの自由**。core は外殻 req/res しか知らない。
- **理由**: D3 で発行主体が UI 要素に降りたため、core は内殻機構を知る必要がない。
- **波及**: `ICommandBusAdapter` の 2 実装、version defines による排他コンパイル、`IUICommand` マーカー問題が**まるごと不要化**。core が触る契約は `IInteractable` 1 本だけになる（第5章）。
- **※後続 D7 でこの「自由化」方針は一部撤回される**（RequestAll を core 昇格させたため）。トレイルとして両方残す。

### D5. 記録点の固定
- **結論**: 記録は **外殻（core 所有の req/res 入口/出口）に固定**。内殻 pub/sub では記録しない。
- **理由**: 内殻はユーザーが何のバスを使うか不定なので盗聴器を仕込めない。全コマンドは必ず外殻の一点を通るので、そこで記録すれば **記録の均質性**（誰が発行しても同一形でログに残る）が保証され、むしろ強くなる。
- **波及**: エージェント発・人間発・リプレイ発のコマンドがログ上で完全同一形になり、「エージェントが踏んだバグ」をそのまま人手の再生に使える。

### D6. 結果回収と MessagePipe の罠
- **論点**: `PublishAsync` は全ハンドラ完了まで待てるが戻り値を持たない。どう結果を返すか。
- **調査結果（要注意）**: MessagePipe は **req/res と pub/sub が独立した 2 本の配管**。Filter は片方にしか刺さらない。req/res で送るとフィルタ（Recorder の盗聴）を迂回し **記録されない**。
- **結論**: 記録したいものは必ず Recorder が盗聴する経路を通す。
- **波及**: この罠が D7 の判断（RequestAll を使うか）の前提になる。VitalRouter は経路が実質 1 本なのでこの罠は起きず、MP 固有の注意。

### D7. RequestAll を core 外殻に昇格
- **論点**: MessagePipe の `IRequestAllHandler` / `IAsyncRequestAllHandler`（1req→N handler→全応答集約）を使えるか。
- **結論**: **使う。core 外殻シームそのものに昇格させる**。
- **理由**: 「req/res の素直さ（結果が返る）」と「pub/sub の fan-out（N 波及）」が 1 プリミティブで同時に手に入る。D3 の外殻をそのまま実現できる。
- **波及（重要な代償）**:
  - **core = MessagePipe 固定**になる。D4 の「バス自由 / VitalRouter 選択制」は正式に撤回。
  - fan-out・await 集約・ハンドラ登録を MP が肩代わりし、実装が最小化される。
  - VitalRouter には req/res 概念がないため、VR は選択肢から外れる（もし残すなら別途エミュレーションが要る）。

### D8. 応答側で例外が出たときの挙動
- **調査結果（MessagePipe 公式）**: エラーは呼び出し元へ伝播し、**後続 subscriber は停止**。RequestAll が部分成功を集約して返すことはない（オールオアナッシング寄り）。この挙動は「エラーを無視するフィルタ」で変更可能。
- **選択肢**: A=fail-fast / B=error-ignoring フィルタで全走 / C=状態系 fail-fast・演出系 ignore のステージ分離。
- **結論**: **A: fail-fast** を採用（ユーザー判断）。
- **A の設計**:
  - 最初の例外で止め、**シームで必ず catch**して構造化。**throw を MCP 境界に漏らさない**（死守ライン）。catch は握り潰しではなく、例外情報は完全保存して返す。
  - **`Rejected` と `Faulted` を分ける**（D5・検証前置と整合）:
    - `Rejected` = Publish 前の検証落ち（要素不在/操作不許可/非活性）。副作用ゼロ。リプレイで再 Publish しない。
    - `Faulted` = Publish 後、stage k で例外。k-1 まで副作用発生。リプレイで同地点まで再実行し同一 Faulted を再現。
  - **`AsyncPublishStrategy` は `Sequential` 固定**（既定は Parallel なので明示変更）。これにより「stage k で確実に止まり k+1 以降未実行」が保証され、部分実行が **決定論的**になる（同地点で同一中断を再現できる）。
- **A を選んだことで受け入れる帰結**:
  - 演出・音の例外でも操作全体が Faulted になる（潔癖だが脆い、表裏）。
  - 部分実行の穴（k-1 まで適用済み）は解決しない。代わりに「どこで落ちたか」を正確に記録して中断状態を決定論再現する、が A の思想。

### D9. StageTracker の実装方針
- **論点**: 「どの stage まで走ったか（落ちた index k）」をどう採るか。
- **結論**: **RequestAll のフィルタでハンドラ呼び出し前後をフックしてカウント**（ハンドラ無改変）。Recorder を「フィルタで盗聴」する発想の再利用。
- **状態**: 未実装。次のアクション候補（第8章）。

---

## 5. 確定した主要インターフェース（コードスケッチ）

> D3〜D8 を反映した現時点の骨格。シグネチャは確定前のドラフト。

```csharp
// core が要求する唯一の契約
public interface IInteractable {
    ElementDescriptor Describe();                 // get_ui_tree 用
    bool CanAccept(in UiCommand cmd);             // 検証（Publish 前）
    UniTask<ExecuteResult> ExecuteAsync(          // pub→全sub完了await→res
        UiCommand cmd, CancellationToken ct);
}

public enum ExecuteStatus { Ok, Faulted, Rejected }

public readonly struct ExecuteResult {
    public ExecuteStatus Status { get; }
    public string ElementId { get; }
    public long Sequence { get; }                 // 外殻で採番した全順序番号

    // Faulted 時のみ
    public int? FailedStageIndex { get; }         // Sequential で確定する k
    public int? CompletedStages { get; }          // k-1 まで適用済み
    public FaultInfo? Fault { get; }              // 型名/メッセージ/stacktrace を正規化

    public StateDiff Diff { get; }                // before→after 差分（部分適用が写る）
    public IReadOnlyList<AvailableOp> AvailableOps { get; } // 次に可能な操作
}
```

```csharp
// 外殻シーム（A: fail-fast 実装の骨）
async ValueTask<ExecuteResult> ExecuteAsync(UiCommand cmd, CancellationToken ct) {
    var seq = _sequencer.Next();                     // 外殻で全順序採番
    _recorder.OnRequested(seq, cmd);                 // 記録点①（必ず通る一点）

    if (!_registry.TryResolve(cmd.Target, out var el)) return Reject(seq, cmd, "NotFound");
    if (!el.CanAccept(cmd))                           return Reject(seq, cmd, "OpNotAllowed");

    var before = _registry.Snapshot(el.Scope);
    try {
        await _requestAll.InvokeAllAsync(cmd.WithSeq(seq), ct);   // Sequential / A: 最初の例外で伝播
        var after = _registry.Snapshot(el.Scope);
        var r = ExecuteResult.Ok(seq, el.Id, Diff(before, after), el.AvailableOps());
        _recorder.OnCompleted(seq, r);               // 記録点②
        return r;
    }
    catch (Exception ex) {
        var after = _registry.Snapshot(el.Scope);    // 部分適用ぶんを取る
        var r = ExecuteResult.Faulted(
            seq, el.Id,
            failedStage: StageTracker.Current(seq),  // Sequential だから確定できる
            diff: Diff(before, after),
            fault: FaultInfo.Normalize(ex));
        _recorder.OnFaulted(seq, r);                 // 記録点③（中断点をログへ）
        return r;                                     // throw せず return（MCP へ漏らさない）
    }
}
```

---

## 6. 依存ライブラリの制約・落とし穴（調査で判明）

- **MCP C# SDK（ModelContextProtocol, 現行 1.4.1）**: Core / hosting・DI / AspNetCore に分かれ、**基本 .NET ホスト前提**。Unity ランタイム（IL2CPP / .NET Standard 2.1）で直接ホストは相性が悪い → **外部プロセスの MCP サーバ ⇄ Unity をブリッジ**する構成が定石（既存 Unity MCP 実装も同様）。
- **ドメインリロード**: Play mode 突入でドメインリロードが走りブリッジ接続が切れる（既知問題）。**Enter Play Mode Settings で Reload Domain をオフ**、または再接続耐性を持たせる。
- **MessagePipe**:
  - エラーは呼び出し元へ伝播し後続 subscriber は停止（D8）。フィルタで変更可能。
  - **req/res と pub/sub は別配管**。フィルタは片方にしか刺さらない（D6）。
  - `AsyncPublishStrategy` 既定は **Parallel**。決定論のため **Sequential に明示変更**（D8）。
  - `RequestAll` で多ハンドラ集約可能。`PublishAsync` で全 subscriber 完了を await できる。
- **VitalRouter（現行 2.2.0）**: Publisher→Router→Interceptor→Handler。`ICommandInterceptor` が全コマンドを捕捉（Recorder に好適）。`CommandOrdering.Sequential` で逐次直列化。純データは `record struct` 推奨・ゼロアロケーション。Unity/.NET 両対応。**req/res 概念は無い**（D7 で選外）。
- **メインスレッド境界**: WebSocket エンドポイントは別スレッド。Registry / バスは Unity メインスレッドでしか触れない。await 前に main thread へマーシャリング（UniTask 等）。
- **「完了」の定義**: `PublishAsync` 完了 = 同期的副作用が落ち着いた時点。アニメ・遷移など複数フレーム跨ぎは別。**execute は即時結果のみ、settle 待機は `wait_for` に分離**。

---

## 7. 先行 OSS「Gua」の発見と評価 ★着手前必読★

**https://gua.orizika.com/ / github.com/link1345/gua（MIT, Godot 4.7 + Unity 6 preview）**

構想と **目的・構造がほぼ完全に一致する既存実装**。しかも実装済み・ドキュメント完備・CI 連携済み。

### 既に実装済み（＝本基盤の独自性ではない部分）
- セマンティック UI ツリーでスクショなしエージェント操作（`get_ui_tree`、id/role/text/state）
- WebSocket ブリッジで外部プロセス ⇄ ランタイム
- 記録・再生（`GuaRecorder` / `GuaReplayer`、`recording.schema.json`）、**timing・前後 revision・待機条件を記録**
- **ホスト完了確認付き Replay**（＝キュー受理≠成功、completion event 確認：本基盤の 5 ターン分の議論に対応）
- request correlation で action↔host result を ID 対応（＝外殻 req/res・全順序採番）
- state-based waits（sleep 無し）＝ `wait_for` 分離
- uGUI + UI Toolkit + TextMeshPro 自動反映（MVP で迷った両対応が済み）
- 失敗時の証拠保全（UI Tree・差分・pending request・event 履歴・ログ・env・スクショを 1 artifact に集約）
- 機密値を secret key のみ保存し replay 時に memory 内解決
- gui-mcp で全操作・record・replay・visual 比較を AI ツール公開
- Godot 対応・Visual regression・GitHub Actions

### 唯一の差別化候補軸
**Gua は「外部から入力経路を叩く観測者」（`DRAW→REFLECT→CONNECT→ACT`）。本設計は「VitalRouter/MessagePipe のコマンドバスに一級市民として乗り、UI 操作＝アプリ内部コマンドとして流す」。**

実利になる場面（Gua が構造的に取りにいっていない領域）:
- リプレイが「入力の再生」でなく **アプリコマンド列の再生**で、アプリ固有ハンドラごと決定論再現したい
- 演出・音の完了を汎用 completion でなく **そのアプリの pub/sub の意味論**で待ちたい
- コマンドがアプリの一級市民で、エージェント操作/テスト/実プレイが同一コマンド型で統一される

ただしニッチ（VitalRouter/MessagePipe 採用 かつ エージェント操作/リプレイ が欲しい層）。

### 未確認の一点（進路を決める鍵）
本基盤で深く踏んだ **D8 の例外設計（A / Rejected・Faulted 分離 / 部分実行の決定論再現）** が、Gua の `recording.schema.json` と `GuaReplayer` に既にあるか。**着手前にこの 2 ファイルを読むこと。**
- 既にある → 車輪の再発明。**Gua に乗り、バス統合を寄与（PR）** が最短。
- 無い/浅い → 本基盤の例外・部分実行設計は本物の付加価値。**Gua 互換を保ちつつ「VitalRouter/MessagePipe ネイティブなコマンドバス統合層」として別レイヤーで出す**（競合せず補完）。

### 3 つの現実的選択肢
1. **Gua を使う/貢献する**（最も合理的）
2. **バス統合に全振りして別物として作る**（Gua と非競合、ただし市場は狭い）
3. **やめる**（バス統合が自社で不要なら Gua で足りる）

**ゼロから全部作るのは最も損**（Gua が 8 割方実装済みのため）。

---

## 8. 未決事項と次のアクション

### 最優先（進路確定に必須）
- [ ] **Gua の `recording.schema.json` と `GuaReplayer` を読む** → D8 の解が既存か判定 → 「寄与 or 別レイヤー or 作らない」を決定

### 進路が「作る/寄与する」に決まった後
- [ ] `StageTracker` を「フィルタでカウント」方式で実装（D9）
- [ ] `Faulted` をリプレイ側がどう読むか＝「同一中断を再現するループ」の設計
- [ ] `ExecuteResult` 最終スキーマ確定（特に `Faulted` の stage 表現と `StateDiff` の差分表現）
- [ ] `UiCommand` / `ElementDescriptor` / `AvailableOp` の具体シグネチャ
- [ ] MVP スコープ: uGUI 一本か両対応か（Gua は両対応済み → 差別化にならない）
- [ ] 決定論フック（`IClock` / `IRandom` 注入・記録）。A×一級市民でアプリ側ハンドラが同経路で走るため重要度が上がる
- [ ] セキュリティ（外部からアプリ駆動＝RCE 面：localhost 限定・トークン・リリース既定オフ）
- [ ] 仮称の決定

### 積み残しの論点
- ペイロード肥大対策（UI ツリーの screen/role スコープ絞り・ページング）
- 要素の識別方式（明示 ID / 階層パス / role+label の解決優先順位。ログには常に stableID を焼く方針は合意済み）

---

## 付録: 用語

| 用語 | 意味 |
|---|---|
| 外殻 / 内殻 | 外殻=core 所有の req/res（検証・採番・記録）。内殻=要素所有の pub/sub（fan-out） |
| シーム（seam） | 外殻の req/res 境界。ここで検証・記録・例外 catch を行う |
| stage | 1 コマンドに反応する各 sub ハンドラ（処理/演出/音…）の実行単位 |
| Rejected | Publish 前の検証落ち。副作用ゼロ |
| Faulted | Publish 後 stage k で例外。k-1 まで副作用発生 |
| host completion 確認 | キュー受理でなく副作用完了イベントで成功判定すること |
| 一級市民 / 内部配管 | コマンドをアプリ既存バスに乗せるか（D2 で内部配管を選択） |
