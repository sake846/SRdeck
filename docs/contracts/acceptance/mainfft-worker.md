# MainFftWorker／MainFftService 回帰試験仕様

この仕様は、回帰試験仕様書 4.2、4.3、4.9 の FFT ワーカー経路を、制御用ワーカーだけでなく実 `MainFftWorker` と実 `MainFftService` で検査するための契約である。

## MainFftWorker

- `Start` 前、処理中、停止後の `TrySubmit` は受理せず、ドロップ数へ反映する。
- 受理済み要求は 1 件だけ処理中とし、プロセッサ例外では `HasFrame=false` の結果を返した後も次要求を受理する。
- 完了コールバック例外をワーカーの詰まりやスレッド終了へ伝播させず、次要求を処理する。
- `Dispose` は停止を待ってプロセッサ、セマフォ、キャンセルソースを一度だけ解放する。二重 `Dispose` は安全で、破棄後 `Start` は拒否する。
- 保留メタデータは 64 件を上限とする。上限を超えた最古の要求はフレーム対応から除外し、上限内の要求は完了タグで対応付ける。

## MainFftService

- 実ワーカーから公開されたフレームは、フレーム ID、中心周波数、ウォーターフォール連番、全表示バッファを同じ世代で公開する。
- GPU集約による帯域電力サマリーも同じフレームで公開し、再利用・リセット時に前のサマリーを残さない。
- 同じフレームを複数リースした場合、各リースの解放を独立して数え、二重解放を無視する。未公開になったフレームは全リース解放後だけ次の書き込みバッファへ再利用する。
- 破棄中に完了したフレームは公開コールバック、診断更新、画面フレーム更新を発生させない。
- 公開フレームごとに完了コールバックと FFT 診断更新を一度だけ実行する。

## 自動試験

- `Main FFT worker failure stop and ownership`
- `Main FFT worker metadata pruning`
- `Main FFT service real worker lifecycle`
- `GPU spectrum aggregation preserves noise and power`、`Spectrum power summary tuning guard`
- 既存の `Main FFT coherent frame publication`、`Main FFT warm-up buffer reuse`、`Main FFT reset generation isolation`、`Main FFT async completion metadata pairing`
