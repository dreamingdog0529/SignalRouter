# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

This file is maintained automatically by [Release Please](https://github.com/googleapis/release-please)
from Conventional Commits. Add user-facing notes under `[Unreleased]` if you want to
call something out before the next release.

## [0.1.1](https://github.com/dreamingdog0529/SignalRouter/compare/v0.1.0...v0.1.1) (2026-07-24)


### Features

* **core:** add deterministic stage pipeline and progress tracker ([04d3446](https://github.com/dreamingdog0529/SignalRouter/commit/04d34464615deb36b30eb4e0b55a84949ab56efa))
* **core:** add deterministic stage pipeline and progress tracker ([028f3e3](https://github.com/dreamingdog0529/SignalRouter/commit/028f3e3af57e9eaca55637aadc67062415a77296))
* **core:** add FIFO dispatcher and VitalRouter integration ([15c527a](https://github.com/dreamingdog0529/SignalRouter/commit/15c527a4f10d5e9e5dd31e565a261f5f86882f5d))
* **core:** add FIFO interaction dispatcher over a private VitalRouter router ([9ac84b4](https://github.com/dreamingdog0529/SignalRouter/commit/9ac84b4adf5b5f28725ba4088024d6357ccf643a))
* **core:** add interaction domain contracts ([46cf52e](https://github.com/dreamingdog0529/SignalRouter/commit/46cf52ed12f28bff77dd77c4c6c3cac8b1eae349))
* **core:** add interaction execution scope and context continuations ([0e714ec](https://github.com/dreamingdog0529/SignalRouter/commit/0e714ecbefcf2e215df591f4ef96756755539c1b))
* **core:** add strict replayer for recorded interaction sessions ([#13](https://github.com/dreamingdog0529/SignalRouter/issues/13)) ([24518a0](https://github.com/dreamingdog0529/SignalRouter/commit/24518a09edb20632aec30c3b7a62ad75f39f2d63))
* **core:** capture real state observations via canonical-hashed probes ([ca913e8](https://github.com/dreamingdog0529/SignalRouter/commit/ca913e8773475000623d9c18e4659645501c983c))
* **core:** capture real state observations via probes ([c530496](https://github.com/dreamingdog0529/SignalRouter/commit/c530496b6f2c62593010e4cbfabf75808ec256cf))
* **core:** enumerate nested interaction/argument property changes ([#11](https://github.com/dreamingdog0529/SignalRouter/issues/11)) ([3a156ed](https://github.com/dreamingdog0529/SignalRouter/commit/3a156edd5865926802d134ac3185587abd75cdc7))
* **core:** enumerate semantic-ui property-level state changes ([7c8da92](https://github.com/dreamingdog0529/SignalRouter/commit/7c8da92eb4433c02a571af95cf6bdb2eb8744152))
* **core:** enumerate semantic-ui property-level state changes ([59f96bf](https://github.com/dreamingdog0529/SignalRouter/commit/59f96bf18141ad309feff31dee5d019aca1e2c15))
* **core:** enumerate semantic-ui target additions and removals ([#10](https://github.com/dreamingdog0529/SignalRouter/issues/10)) ([3f8f94c](https://github.com/dreamingdog0529/SignalRouter/commit/3f8f94c0d22af9821f2c6ca2999e517e7ec8ef07))
* **core:** implement command catalog and registry ([b518251](https://github.com/dreamingdog0529/SignalRouter/commit/b5182512292b36624cc5281302b143eef3e099fa))
* **core:** implement core interaction model ([efb3ba0](https://github.com/dreamingdog0529/SignalRouter/commit/efb3ba0d29b48c786c02d25ac89f21def8df1f99))
* **core:** maintenance lease for mid-session recorder attach/detach ([#20](https://github.com/dreamingdog0529/SignalRouter/issues/20)) ([15919b4](https://github.com/dreamingdog0529/SignalRouter/commit/15919b410aa2cf4361f21b1565e439c1209a52af))
* **core:** record interactions as JSON Lines sessions ([#12](https://github.com/dreamingdog0529/SignalRouter/issues/12)) ([788f4e3](https://github.com/dreamingdog0529/SignalRouter/commit/788f4e3ed39049e894bd018751a5af82a8c03381))
* **core:** split-phase submission API with external request identity ([#16](https://github.com/dreamingdog0529/SignalRouter/issues/16)) ([c1309bb](https://github.com/dreamingdog0529/SignalRouter/commit/c1309bb69e732ecb979385754a1074700d6f5eaa))
* **mcphost:** control-operation recovery — single-flight, query, resend ([#21](https://github.com/dreamingdog0529/SignalRouter/issues/21)) ([532b4c7](https://github.com/dreamingdog0529/SignalRouter/commit/532b4c75479dadf8385aed4438376afa06f03b40))
* **mcphost:** expose recording/replay tools and freeze protocol v1.0 ([#24](https://github.com/dreamingdog0529/SignalRouter/issues/24)) ([02aaeda](https://github.com/dreamingdog0529/SignalRouter/commit/02aaeda5a1492e0640b441f9516f71d65fac4502))
* **mcphost:** mcp host process and core tool surface ([#18](https://github.com/dreamingdog0529/SignalRouter/issues/18)) ([055fd60](https://github.com/dreamingdog0529/SignalRouter/commit/055fd60fc0d95b3d1be9187b978be521d86fa29e))
* **protocol:** recording and replay control messages and host tools ([#19](https://github.com/dreamingdog0529/SignalRouter/issues/19)) ([c325c9c](https://github.com/dreamingdog0529/SignalRouter/commit/c325c9c783f5f83d21e11fcce752dcf11f026e75))
* **protocol:** versioned runtime protocol envelope, handshake, and request ledger ([#15](https://github.com/dreamingdog0529/SignalRouter/issues/15)) ([d12b90d](https://github.com/dreamingdog0529/SignalRouter/commit/d12b90d2ff255c193a1ab2623302a5c7f8822da7))
* **protocol:** websocket transport channel and unity runtime bridge ([#17](https://github.com/dreamingdog0529/SignalRouter/issues/17)) ([5451963](https://github.com/dreamingdog0529/SignalRouter/commit/54519634b66dfa1d26647127f14b9fc08fd129d1))
* **unity:** recording/replay session supervisor with transport control callbacks ([#23](https://github.com/dreamingdog0529/SignalRouter/issues/23)) ([9122b71](https://github.com/dreamingdog0529/SignalRouter/commit/9122b71075ea823f86a82e928829821f5aeca908))
* **unity:** uGUI button/text adapters and sample scene ([#14](https://github.com/dreamingdog0529/SignalRouter/issues/14)) ([3bbe24d](https://github.com/dreamingdog0529/SignalRouter/commit/3bbe24dfab3adf5ad35d53eeeaed516c52c2b429))


### Bug Fixes

* **core:** harden semantic-ui property-diff per review ([28bfafa](https://github.com/dreamingdog0529/SignalRouter/commit/28bfafac0443f170a6582e06b9f756deae61f0a7))
* **core:** harden stage-pipeline cancellation and threading per review ([83c8290](https://github.com/dreamingdog0529/SignalRouter/commit/83c829069e3e69d6fb4112ec2a0b0a9c60b18b2a))
* **core:** harden state-probe capture per review ([d5c054c](https://github.com/dreamingdog0529/SignalRouter/commit/d5c054ced2c1b1f82c9f967e1f22270460351ed3))
* **core:** harden the FIFO dispatcher per review ([fd94eb4](https://github.com/dreamingdog0529/SignalRouter/commit/fd94eb4b5cd1bbe3aff6f231a52f46b75f8b89d1))
* maintenance-acquire admission barrier and no-epoch-change control model ([#22](https://github.com/dreamingdog0529/SignalRouter/issues/22)) ([0de9778](https://github.com/dreamingdog0529/SignalRouter/commit/0de97780f028cc0d6cabd393000e536d69ef80c5))
* **release:** sync assembly version with Release Please ([1b9b659](https://github.com/dreamingdog0529/SignalRouter/commit/1b9b6595b3dc4fc2378556ffbb98eb4cd698a53b))
* sync release version and harden CI scaffolding ([c33d379](https://github.com/dreamingdog0529/SignalRouter/commit/c33d379939ef6dab147e9c2b00fccad27acc7fa1))


### Documentation

* align result equality description ([e3d61f5](https://github.com/dreamingdog0529/SignalRouter/commit/e3d61f56640c1e0baa721bc14f8612633ed92fd0))
* describe .NET-first source and package layout ([17ec002](https://github.com/dreamingdog0529/SignalRouter/commit/17ec002acee1eaadc1ce69022f76311a3ffc7abc))
* note the versioned hash envelope in ADR 0001 ([325e820](https://github.com/dreamingdog0529/SignalRouter/commit/325e82070c63627b7e99963d179d68aec0a3fe80))
* record canonical state hashing decision (ADR 0001) ([d863376](https://github.com/dreamingdog0529/SignalRouter/commit/d863376ca20fd8123412775ea7105f82c0fb7dae))
* record core interaction model decisions ([0c6e311](https://github.com/dreamingdog0529/SignalRouter/commit/0c6e31182a4038a9436c4258bdf2c79e602004ff))
* record planned state-history and MCP inspection direction ([61fbc0d](https://github.com/dreamingdog0529/SignalRouter/commit/61fbc0d1dbc3d05f56b91fe37e70308abd3494b7))
* record semantic-ui property-diff decision (ADR 0002) ([1e7eba3](https://github.com/dreamingdog0529/SignalRouter/commit/1e7eba33a9755007ea50be76c5349b65cb74ec4d))
* update contributors ([328cae4](https://github.com/dreamingdog0529/SignalRouter/commit/328cae4ad8d83bd7cc0fa8dfa6ebab91d5a7a7c6))
* update contributors ([af68398](https://github.com/dreamingdog0529/SignalRouter/commit/af68398e69f053e33be94bef8bc67f56fc8c58b2))


### Miscellaneous

* import project template and initialize for SignalRouter ([260cfe7](https://github.com/dreamingdog0529/SignalRouter/commit/260cfe7d2bbecd19b741c2269b55bf31c6eec695))


### Code Refactoring

* adopt .NET-first package layout to match PlayerData ([df48897](https://github.com/dreamingdog0529/SignalRouter/commit/df48897c66ff21b8a603609a55370ff99749407a))
* align dotnet project layout ([9b17f30](https://github.com/dreamingdog0529/SignalRouter/commit/9b17f301d0fb40d93315b4ed41cf8eaa6d911100))
* align dotnet project layout ([185b700](https://github.com/dreamingdog0529/SignalRouter/commit/185b700e109a4a31f555619e24c432dd9d0c53bb))
* **core:** centralize collection contracts via EquatableList ([50590d5](https://github.com/dreamingdog0529/SignalRouter/commit/50590d589f2ac2347661be232edaf5ec7526fd15))
* **core:** centralize collection contracts via EquatableList ([beaeaf7](https://github.com/dreamingdog0529/SignalRouter/commit/beaeaf75967bdad5588140a47b50f93617f2e34e))
* **core:** simplify interaction result contracts ([4400cb5](https://github.com/dreamingdog0529/SignalRouter/commit/4400cb5c353fe73fabd9bbd3c07d54adcd4a89fa))
* **core:** tighten EquatableList surface per review ([0482659](https://github.com/dreamingdog0529/SignalRouter/commit/04826598440bc8e7a48a50e96a1085786d5b9763))


### Tests

* **core:** cover dispatcher FIFO order, cancellation, reentrancy, and continuations ([c09b414](https://github.com/dreamingdog0529/SignalRouter/commit/c09b414da03ec9a097f7f3368182cded01994868))
* **core:** cover dispatcher hardening fixes ([04537a7](https://github.com/dreamingdog0529/SignalRouter/commit/04537a74bdad7dcc5cd759e9f3727a6e65d54519))
* **core:** cover property-diff hardening fixes ([29e67d7](https://github.com/dreamingdog0529/SignalRouter/commit/29e67d73d6ae6c479b60dc9933ccf19c74f19197))
* **core:** cover semantic-ui property-level diffs ([fd5e179](https://github.com/dreamingdog0529/SignalRouter/commit/fd5e17906036bc05241b10e1efab475ea39be3d9))
* **core:** cover stage-pipeline cancellation and context hardening ([5629725](https://github.com/dreamingdog0529/SignalRouter/commit/5629725d2d6d43e0c1c75c37fd4ec46810b8634e))
* **core:** cover state canonicalization, probe registry, and capture ([62e35b1](https://github.com/dreamingdog0529/SignalRouter/commit/62e35b156b8ce5b18542ebb1a43c6d274d8bc920))
* **core:** cover state-probe hardening fixes ([79278f8](https://github.com/dreamingdog0529/SignalRouter/commit/79278f8d2c875dc387c9e81706755b64e8ae73a0))
* **core:** cover the stage pipeline and progress tracker ([350c4a2](https://github.com/dreamingdog0529/SignalRouter/commit/350c4a255eb0f202272825b21094c868d2cb2397))
* **unity:** add editmode dispatcher smoke test ([54ecf49](https://github.com/dreamingdog0529/SignalRouter/commit/54ecf49f28e40c2f3fe72444dcb79c25d0ebaf0a))


### Build System

* check branch commits against local main fallback ([9c41d6f](https://github.com/dreamingdog0529/SignalRouter/commit/9c41d6fe41670162f90cbf858dc1cb7efbbad4d2))
* scaffold SignalRouter projects ([287adcd](https://github.com/dreamingdog0529/SignalRouter/commit/287adcdd087bafd1a92dc2fddafd40768e0bcce1))
* scaffold SignalRouter projects ([5672a2d](https://github.com/dreamingdog0529/SignalRouter/commit/5672a2d74b143df13c7c61350ee24ece4fc6479a))


### CI/CD

* create local-feed directory before restore ([68ab288](https://github.com/dreamingdog0529/SignalRouter/commit/68ab288c169dc114460eacf616435e8d46bc03af))
* gate codeql for private repositories ([f647c5a](https://github.com/dreamingdog0529/SignalRouter/commit/f647c5ae6050badcad1a346edf03e38e4d9c9af3))
* provision pinned dotnet sdk ([859aeb9](https://github.com/dreamingdog0529/SignalRouter/commit/859aeb96961d3a26b699a2c18262523c157ec706))
* scan C# sources with CodeQL ([81c381e](https://github.com/dreamingdog0529/SignalRouter/commit/81c381e746cd300c638b38bb552d997e220c38c4))

## [1.0.5](https://github.com/dreamingdog0529/repo-template/compare/v1.0.4...v1.0.5) (2026-07-18)


### Bug Fixes

* **deps:** bump js-yaml to 4.2.0 for Dependabot advisories ([#18](https://github.com/dreamingdog0529/repo-template/issues/18)) ([689d51f](https://github.com/dreamingdog0529/repo-template/commit/689d51fb42b4bc200ecccad46979c61da06dfde5))

## [1.0.4](https://github.com/dreamingdog0529/repo-template/compare/v1.0.3...v1.0.4) (2026-07-18)


### CI/CD

* harden workflows for OpenSSF Scorecard alerts ([#16](https://github.com/dreamingdog0529/repo-template/issues/16)) ([552876a](https://github.com/dreamingdog0529/repo-template/commit/552876a61992a7da338f3f2e4d2855325b3e5320))

## [1.0.3](https://github.com/dreamingdog0529/repo-template/compare/v1.0.2...v1.0.3) (2026-07-18)


### Documentation

* fix DCO GitHub App slug from dco2 to dco-2 ([#14](https://github.com/dreamingdog0529/repo-template/issues/14)) ([d58ce93](https://github.com/dreamingdog0529/repo-template/commit/d58ce93185d66505be32d59ade711c54ae603e9a))

## [1.0.2](https://github.com/dreamingdog0529/repo-template/compare/v1.0.1...v1.0.2) (2026-07-18)


### Documentation

* declutter root and placeholder the acknowledgments ([#12](https://github.com/dreamingdog0529/repo-template/issues/12)) ([6daeaf0](https://github.com/dreamingdog0529/repo-template/commit/6daeaf09e031c1de36bf88126344dc4da2dacdeb))

## [1.0.1](https://github.com/dreamingdog0529/repo-template/compare/v1.0.0...v1.0.1) (2026-07-18)


### CI/CD

* codify default-branch protection in settings.yml ([#9](https://github.com/dreamingdog0529/repo-template/issues/9)) ([1f716f1](https://github.com/dreamingdog0529/repo-template/commit/1f716f1476d74190e7a71e75af5be4ad6f552193))
* pin github actions to shas and add supply-chain hardening guide ([#11](https://github.com/dreamingdog0529/repo-template/issues/11)) ([0d7033f](https://github.com/dreamingdog0529/repo-template/commit/0d7033fc669863f59b14d22bd5115cc4491de397))

## 1.0.0 (2026-07-18)


### Bug Fixes

* keep template workflows valid and green before init ([ded3b70](https://github.com/dreamingdog0529/repo-template/commit/ded3b7088938471378716e8b940e21295a0e51b5))


### Documentation

* update contributors ([1feb9a5](https://github.com/dreamingdog0529/repo-template/commit/1feb9a5f3662790b366afbe3560fa8817c29137f))


### Miscellaneous

* **deps:** bump actions/first-interaction from 1 to 3 ([#5](https://github.com/dreamingdog0529/repo-template/issues/5)) ([766a2b2](https://github.com/dreamingdog0529/repo-template/commit/766a2b254172ce32998d5b6b99da5dcbb748d7ce))
* **deps:** bump actions/github-script from 7 to 9 ([#3](https://github.com/dreamingdog0529/repo-template/issues/3)) ([b36c370](https://github.com/dreamingdog0529/repo-template/commit/b36c370e1980fbf98d2a7d453c5de742b021b6ad))
* **deps:** bump actions/labeler from 5 to 6 ([#1](https://github.com/dreamingdog0529/repo-template/issues/1)) ([08bbc0b](https://github.com/dreamingdog0529/repo-template/commit/08bbc0bad10b11647f17e09b6115265785129d2d))
* **deps:** bump github/codeql-action from 3 to 4 ([#4](https://github.com/dreamingdog0529/repo-template/issues/4)) ([5a501b4](https://github.com/dreamingdog0529/repo-template/commit/5a501b4329d522bc724de186e071f6833a717899))
* **deps:** bump ossf/scorecard-action from 2.4.0 to 2.4.3 ([#2](https://github.com/dreamingdog0529/repo-template/issues/2)) ([c199f5f](https://github.com/dreamingdog0529/repo-template/commit/c199f5fe39f9660659038eef4d0b07a2d46c2f5f))
* scaffold public repository template ([a091894](https://github.com/dreamingdog0529/repo-template/commit/a091894ef4e14c87f67d9c8691b8a4b5a18d8517))


### CI/CD

* fix deprecated deny-licenses and first-interaction v3 inputs ([#7](https://github.com/dreamingdog0529/repo-template/issues/7)) ([d3b61a6](https://github.com/dreamingdog0529/repo-template/commit/d3b61a6f4b73b0b6eade7d1607308d3481eff9bb))

## [Unreleased]
