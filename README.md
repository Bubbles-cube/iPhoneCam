# 泡泡直播助手 v2

手機鏡頭 → Wi-Fi → PC 虛擬攝像頭 → OBS／Discord，低延遲、切檔不死、狀態誠實。

v1（`iPhoneCam/`，本機留檔）功能驗證通過但補丁疊補丁，v2 推倒重寫，只帶走「已驗證的知識」。詳細產品設計見 `docs/product-v2.md`。

## 架構

```
iPhoneCamV2/
├── PhoneApp/    # Android 端：相機＋H.264 編碼＋傳輸＋UI
├── PcNet/       # PC 共用庫（net10.0，無 UI 相依）：協議／收流／解包
├── PcApp/       # PC 儀表板（WPF）：預覽＋控制＋統計
├── VirtualCam/  # 虛擬攝像頭（MF）：給 OBS／Discord 用的那顆相機
└── docs/        # 產品設計文件
```

## 功能簡介

### 手機端（Android）

- 預覽：前／後鏡切換，直向鎖定，cover 填滿置中；無畫面時顯示待機圖
- 編碼：H.264 only，CBR；檔位 720P（960x720）／1080P（1440x1080），FPS 30／60，碼率 4M／8M（設定頁滑桿 1～40M 可調）
- 傳輸：TCP 走控制（hello／video_params／codec_config／idr_request／set_video），UDP 走影像（分片＋NACK＋3 秒看門狗，斷線自動回 TCP）
- 方向：陀螺儀 rot＋鏡像＋2 秒心跳，PC 跟著轉；本機預覽不轉
- 保命機制：drain 防重入、session 失敗自動降檔、亮屏＋WiFi 鎖、過熱降碼率、閃退記錄、快門鎖定
- 誠實狀態：LIVE＝真有畫面才亮

### PC 端（WPF）

- 儀表板：即時預覽、裝置搜尋＋手動 IP、解析度／FPS 下拉、IDR 按鈕、截圖、存檔開關
- 解碼：ffmpeg 子程序，D3D11VA 硬解優先、失敗自動退軟解；幀計數＋CPU 顯示
- 虛擬攝像頭：NV12 直通、跟著串流換畫布、斷訊凍結上一幀不斷彩條

### 第一版明確不做

美顏 UI、影像濾鏡、超採樣、ABR 碼率階梯、2K／4K 檔、HEVC、iOS 端、USB 傳輸、麥克風（純視訊產品）。

## 建置

```powershell
# PC 端（需 .NET 10 SDK）
dotnet build "iPhoneCamV2.slnx" -c Release

# 手機端（需 JDK 21 給 Gradle 跑）
gradle assembleDebug   # 在 PhoneApp/ 下，產出 app-debug.apk
```

虛擬攝像頭啟用（系統管理員一次即可，同 CLSID 透明替換舊版）：

```powershell
regsvr32 VirtualCam.comhost.dll
```

## 做到哪裡了（2026-10-09）

- 手機端、PC 端（庫＋儀表板＋虛擬攝像頭）都完成，方案建置 0 警告 0 錯誤
- 迴路驗收全過：測試流＋假手機，PC 預覽 `960x720 後鏡 rot=0`＋29fps＋硬解
- v2 虛擬攝像頭已註冊，FrameServer 已重啟，裝置清單列出 `iPhoneCam`

## 還沒解決／待驗收

1. **真機 5 條驗收還沒跑**（需在手機裝 `泡泡直播助手-v2.apk`）：
   - 開播中切檔／切幀率：不死、不凍超過 2 秒
   - PC 預覽與 OBS 同畫面，連續 5 分鐘不斷流
   - 轉向 2 秒內跟正；斷 Wi-Fi 回來 3 秒內自動回 TCP
   - 本機預覽各檔填滿無變形
2. **OBS 要刪掉舊視訊源重加**（換檔＝裝置重建，眼珠開關不一定夠）
3. iOS 端、USB 傳輸不在本版範圍，要等下一版
