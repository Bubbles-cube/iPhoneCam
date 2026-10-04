# 泡泡直播助手 v2 技術方案

> 對應產品文件 `product-v2.md`。本方案只收「v1 已驗證」的結論，推論過程見舊 `AGENTS.md`，此處只寫怎麼做。

## 架構

```text
手機(Sender)                        PC(Receiver)
┌─────────────┐  TCP 19300 控制      ┌──────────────┐
│ NetEndpoint │◄────CAM1───────────►│ CamClient    │
│  UdpSender  │  UDP 19302 影像      │ UdpReceiver  │
└──────┬──────┘ ────CUM1───────────►└──────┬───────┘
       │ CamPipeline                         │ AnnexB→ffmpeg→NV12
       │ (Camera2+MediaCodec+drain)          ▼
       │                              VirtualCam (MMF) → OBS/Discord
Beacon UDP 19301 ──►  BeaconListener（PC 搜尋用）
```

## 協議（沿用 v1，不改：兩端對得上最重要）

- CAM1（TCP）：`[CAM1][type 1B][tsUs 8B][len 4B][payload]`，`type 0x01=影像(AVCC)`／`0x03=控制(JSON含kind)`。
- 控制 kinds：`hello`／`video_params{w,h,fps,codec,rot,flip}`／`codec_config{sps,pps}`／`set_video`／`idr_request`／`udp_ready{port,host}`／`udp_ok`／`udp_lost`／`nack{seqs}`／`orientation{rot,flip}`／`video_rejected`。**砍掉**：`net_state`（ABR 用，MVP 無 ABR）、`set_image`（美顏用）。
- CUM1（UDP）：`[0x43554D31][seq u32][tsUs u64][flags 1B][fragIdx u16][fragTotal u16][payload≤1400B]`，payload＝AVCC。手機快取 32 幀供重傳；PC 重組＋60ms 抖動＋120ms NACK＋150ms 跳洞＋500ms 丟棄；首包到切 UDP，3 秒無包回 TCP。
- 信標（UDP 19301，每 2 秒＋已知 PC 補單播）：`{kind,name,ip,port,codec:[h264],max:"720P30"}`。
- 埠號、CLSID（`8136b935…`）、NV12 直通，全部沿用。

## 手機端（4 個檔，各管一攤）

- `MainActivity`：版面接線＋生命週期＋cover 預覽＋狀態列。不碰相機、不碰 socket。
- `CamPipeline`：Camera2 會話＋MediaCodec（H.264/CBR/GOP 分檔）＋drain＋陀螺儀 rot＋AE 快門鎖＋SPS 解析（Annex-B／avcC／裸 NAL 三吃）。drain 認 `encGen` 代號（v1 閃退教訓）。
- `NetEndpoint`：TCP 伺服器＋控制收發＋UDP 分片發送＋NACK 重傳＋信標。斷線預設回 TCP。
- `StreamController`：開播／關播＋session 失敗 fallback 鏈（關超採樣→逐檔降→60轉30→停住）＋LIVE 誠實化＋亮屏／WiFi 鎖／過熱／crash 記錄。
- 執行緒鐵律（v1 卡死教訓）：UI 只顯示；相機回呼走專用 HandlerThread；網路迴圈 `Task`／Thread 跑，不沾 UI context；寫 stdin 用有界佇列＋專屬執行緒（滿了丟最新）。

## PC 端（3 專案）

- `PcNet`（net10.0 純函式庫）：`CamProto`（埠＋kinds）／`CamClient`（TCP＋`NoDelay`＋fps/Mbps 統計）／`UdpReceiver`（重組＋NACK＋jitter）／`BeaconListener`（去重＋檔位變化刷新）／`AnnexB`（AVCC→AnnexB，壞幀回 null）。
- `VirtualCam`（v1 Source 原樣搬過來：已乾淨，只 NV12＋動態畫布＋MMF；CLSID 不變，透明替換）。
- `PcApp`（WPF 霓虹儀表板精簡版）：預覽（隔幀半解析監看）＋連線＋兩檔下拉＋IDR＋截圖＋存檔＋硬解／軟解顯示＋幀計數＋OBS 按鈕＋診斷複製。ffmpeg 子程序（D3D11VA 優先，8 秒早死＋關鍵字退軟解；**禁 `-fflags nobuffer`**，v1 血淚）。解碼器只認 `codec:WxH:fps:hw`＋eq key，方向／參數單獨變立即跟隨。
- UI 鐵律（v1 卡頓教訓）：採集放背景＋`BeginInvoke` 回 UI；同文字不重設；斷線清殘留（畫面／文字／統計／存檔開關）。

## 測試（AGENTS.md 第二條：交付前全過）

1. 迴路：假手機（無限會動 testsrc）→ PC 收→解→顯，動量驗證（PIL 差分＞10％）。
2. 單元：cover 數學（五組尺寸）、NACK 重組、Exp-Golomb 不測（ffprobe 為準）。
3. 電腦操作驗收：computer-use 無障礙走一遍主流程＋截圖。
4. 真機跑 `product-v2.md` 的 5 條驗收標準。

## 目錄

```text
iPhoneCamV2/
  docs/product-v2.md  docs/tech-v2.md
  PhoneApp/  (Android Gradle, 包名沿用 com.iphonecam.sender)
  PcNet/ PcApp/ VirtualCam/  (.NET 10, WPF + DirectN)
```
