package com.iphonecam.sender;

import android.app.Activity;
import android.content.Context;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.SystemClock;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;
import java.io.FileWriter;

/**
 * v2 總控：擁有檔位狀態＋開播開關＋session 失敗 fallback 鏈＋保命機制。
 * 手機＝TCP 伺服器，PC 主動連入。UI 只顯示（經 Ui 介面），不直接碰相機／socket。
 */
public class StreamController implements NetEndpoint.Listener, CamPipeline.Listener, CamPipeline.FrameSink {

    static final String TAG = "IPC-CTL";

    // 輸出檔位：原生 4:3（CMOS 滿幅，上下不裁）／FPS 分開調
    static final int[][] TIERS = {{960, 720}, {1440, 1080}};
    static final String[] TIER_NAMES = {"720P", "1080P"};
    static final double[] TIER_BR = {4.0, 8.0};
    static final int[] FPS_TIERS = {30, 60};

    public interface Ui {
        void setStatus(String s);
        void setLive(boolean live, String fpsLabel);
        void setConn(boolean on, String ip, String localIp);
        void setStandby(boolean on);
        void refreshTiers(String res, String fps, String live, double mbps);
        void syncBitrate(double mbps);
        void setCamLabel(boolean front);
        void fitPreview();
        String coverInfo(); // 預覽矩陣實測（視/buf/縮放），成功日誌用
    }

    private final Activity activity;
    private final Ui ui;
    private final CamPipeline cam;
    private final NetEndpoint net;

    private int resIdx = 0, fpsIdx = 0;
    private int vidW = 960, vidH = 720, vidFps = 30;
    private double vidMbps = 4.0;
    private int lastRot = 0, lastFlip = 0;

    private volatile boolean broadcasting = false;
    private volatile boolean hasVideo = false;
    private volatile int sessFails = 0;
    private int encRestartTries = 0;
    private int encToken = 0;
    private WifiManager.WifiLock wifiLock;
    private Object thermalListener;

    public StreamController(Activity activity, Ui ui) {
        this.activity = activity;
        this.ui = ui;
        this.cam = new CamPipeline(activity.getApplicationContext(), this, this);
        this.net = new NetEndpoint(this);
        installCrashLog();
    }

    public void destroy() {
        stopBroadcast();
        cam.destroy();
    }

    // ---------- UI 呼叫入口 ----------

    public void toggle() {
        if (!broadcasting) startBroadcast();
        else stopBroadcast();
    }

    public boolean isBroadcasting() { return broadcasting; }
    public boolean hasVideo() { return hasVideo; }
    public int bufW() { return cam.getBufW(); }
    public int bufH() { return cam.getBufH(); }

    private void startBroadcast() {
        broadcasting = true;
        sessFails = 0;
        encRestartTries = 0;
        try { activity.getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON); }
        catch (Exception ignored) {}
        try {
            WifiManager wm = (WifiManager) activity.getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "iPhoneCam:stream");
            wifiLock.acquire();
        } catch (Exception ignored) {}
        startThermalWatch();
        applyTier(vidW, vidH, vidFps, vidMbps);
        net.setMaxLabel(TIER_NAMES[resIdx] + FPS_TIERS[fpsIdx]);
        net.start();
        ui.setConn(false, "", NetEndpoint.localIp());
        onLog("🔴 LIVE 廣播中…");
        refreshLabels();
    }

    private void stopBroadcast() {
        broadcasting = false;
        hasVideo = false;
        streamingOff();
        net.stop();
        stopThermalWatch();
        try { activity.getWindow().clearFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON); }
        catch (Exception ignored) {}
        try { if (wifiLock != null && wifiLock.isHeld()) wifiLock.release(); } catch (Exception ignored) {}
        wifiLock = null;
        ui.setConn(false, "", NetEndpoint.localIp());
        ui.setLive(false, liveLabel());
        onLog("已停止");
    }

    private void streamingOff() {
        cam.stopEncoder();
        cam.closeSession();
    }

    public void cycleRes() {
        resIdx = (resIdx + 1) % TIERS.length;
        vidMbps = TIER_BR[resIdx];
        applyQuality();
    }

    public void cycleFps() {
        fpsIdx = (fpsIdx + 1) % FPS_TIERS.length;
        applyQuality();
    }

    private void applyQuality() {
        sessFails = 0; // 手動換檔＝新的嘗試
        int w = TIERS[resIdx][0], h = TIERS[resIdx][1], fps = FPS_TIERS[fpsIdx];
        if (broadcasting) {
            applyTier(w, h, fps, vidMbps);
        } else {
            vidW = w; vidH = h; vidFps = fps;
            onLog("輸出設為 " + TIER_NAMES[resIdx] + " " + fps + "FPS（開播生效）");
        }
        net.setMaxLabel(TIER_NAMES[resIdx] + FPS_TIERS[fpsIdx]);
        refreshLabels();
    }

    public void setBitrate(double mbps) {
        vidMbps = mbps;
        if (broadcasting) applyTier(vidW, vidH, vidFps, vidMbps);
        else onLog("碼率設為 " + (int) mbps + "M（開播生效）");
        refreshLabels();
    }

    public void switchCamera() {
        hasVideo = false;
        ui.setStandby(true);
        ui.setLive(false, liveLabel());
        boolean front = cam.switchCamera();
        ui.setCamLabel(front);
    }

    public boolean isFront() { return cam.isFront(); }

    public void previewSurface(android.graphics.SurfaceTexture st) {
        cam.setPreviewTexture(st);
        ui.fitPreview();
    }

    // ---------- 檔位套用 ----------

    private void applyTier(int w, int h, int fps, double mbps) {
        snapTier(w, h, fps);
        vidW = w; vidH = h; vidFps = fps; vidMbps = mbps;
        encToken++;
        cam.setEncodeParams(w, h, fps, mbps);
        if (cam.startEncoder()) {
            sendVideoParams();
            refreshLabels();
        } else {
            net.sendControl("video_rejected", "{\"reason\":\"encoder\",\"fallback\":\"720p30 h264\"}");
            onSessionFailed("編碼啟動失敗");
        }
    }

    private void snapTier(int w, int h, int fps) {
        for (int i = 0; i < TIERS.length; i++) {
            if (TIERS[i][0] == w && TIERS[i][1] == h) resIdx = i;
        }
        for (int i = 0; i < FPS_TIERS.length; i++) {
            if (FPS_TIERS[i] == fps) fpsIdx = i;
        }
    }

    /** session 開不起來（HAL 拒收／例外）：第一次同檔重試，再逐檔降
     * （1080P→720P→30fps），連敗 4 次停住。每一檔都走同一條鏈。 */
    public void onSessionFailed(String why) {
        hasVideo = false;
        ui.setLive(false, liveLabel());
        ui.setStandby(true);
        if (!broadcasting) { onLog("session 失敗（已關播，不重試）：" + why); return; }
        if (sessFails >= 4) { onLog("session 連敗多次，先停住（請手動降檔或重開）：" + why); return; }
        sessFails++;
        if (sessFails == 1) {
            onLog("session 失敗，同檔重試一次…（" + why + "）");
            cam.postDelayed(() -> cam.createSession(), 300);
            return;
        }
        if (resIdx > 0) {
            resIdx--;
            vidMbps = TIER_BR[resIdx];
            refreshLabels();
            onLog("此解析度開不起來，自動降檔→" + TIER_NAMES[resIdx] + "…（" + why + "）");
            applyTier(TIERS[resIdx][0], TIERS[resIdx][1], FPS_TIERS[fpsIdx], vidMbps);
            return;
        }
        if (FPS_TIERS[fpsIdx] > 30) {
            fpsIdx = 0;
            refreshLabels();
            onLog("降 FPS→30 重試…（" + why + "）");
            applyTier(vidW, vidH, 30, vidMbps);
            return;
        }
        onLog("最低檔也開不起來，請重開 App 或換手機：（" + why + "）");
    }

    // ---------- CamPipeline.Listener ----------

    public void onLog(String s) {
        Log.i(TAG, s);
        ui.setStatus(s);
    }

    public void onSessionReady() {
        sessFails = 0;
        hasVideo = true;
        ui.setStandby(false);
        ui.fitPreview();
        onLog("預覽就緒 " + vidW + "x" + vidH + " " + ui.coverInfo());
        ui.setLive(true, liveLabel());
    }

    public void onSpsReady() {
        String both = cam.seqHeadBase64();
        if (both == null) return;
        int sep = both.indexOf('|');
        net.sendControl("codec_config", "{\"sps\":\"" + both.substring(0, sep)
                + "\",\"pps\":\"" + both.substring(sep + 1) + "\"}");
        onLog("SPS 就緒 sps=" + cam.getSpsLen() + " pps=" + cam.getPpsLen());
    }

    public void onFramesSent(long sent, long keys) {
        onLog("已送 " + sent + " 幀 key=" + keys);
    }

    public void onEncoderDead() {
        if (!broadcasting) return;
        encRestartTries++;
        final int waitMs = Math.min(30000, 1000 * encRestartTries);
        final int tok = encToken;
        onLog("編碼重啟排程（" + waitMs + "ms 後）");
        cam.postDelayed(() -> {
            if (!broadcasting || tok != encToken) return; // 已有人重啟／換檔，作廢
            applyTier(vidW, vidH, vidFps, vidMbps);
            if (cam.isStreaming()) { encRestartTries = 0; onLog("編碼重啟成功"); }
        }, waitMs);
    }

    public void onRotation(int rot, int flip) {
        lastRot = rot; lastFlip = flip;
        sendVideoParams();
        sendOrientation();
    }

    public void onHeartbeat(int rot, int flip) {
        lastRot = rot; lastFlip = flip;
        sendOrientation();
    }

    // ---------- CamPipeline.FrameSink ----------

    public void sendFrame(byte[] avcc, boolean key) {
        if (!net.hasClient()) return; // 沒人連就不送（省電省熱），buffer 照 release 不噎編碼器
        if (net.usingUdp()) net.udpSendFrame(avcc, key);
        else net.sendVideo(avcc);
    }

    // ---------- NetEndpoint.Listener ----------

    public void onPcConnected(String ip) {
        pcIp = ip;
        ui.setConn(true, ip, "");
        net.sendControl("hello", "{\"app\":\"iPhoneCam-Android\",\"version\":\"v2\",\"device\":\"" + android.os.Build.MODEL + "\"}");
        sendVideoParams();
        String both = cam.seqHeadBase64();
        if (both != null) {
            int sep = both.indexOf('|');
            net.sendControl("codec_config", "{\"sps\":\"" + both.substring(0, sep)
                    + "\",\"pps\":\"" + both.substring(sep + 1) + "\"}");
        }
    }

    public void onPcDisconnected(String ip) {
        ui.setConn(false, "", NetEndpoint.localIp());
    }

    public void onControl(String kind, JSONObject o) {
        try {
            if ("set_video".equals(kind)) {
                int w = o.optInt("width", vidW), h = o.optInt("height", vidH);
                int fps = o.optInt("fps", vidFps);
                double br = o.optDouble("bitrate_mbps", vidMbps);
                sessFails = 0; // PC 指定＝新的嘗試
                applyTier(w, h, fps, br);
                net.setMaxLabel(TIER_NAMES[resIdx] + FPS_TIERS[fpsIdx]);
            } else if ("udp_ready".equals(kind)) {
                // host 由 PC 明確給（adb 轉發時 TCP 對端是假位址不可信）
                String rip = o.optString("host", "");
                if (rip == null || rip.length() < 7) rip = pcIp;
                int rport = o.optInt("port", NetEndpoint.UDP_PORT);
                if (rip != null && rip.length() >= 7) net.udpSetup(rip, rport);
            } else if ("udp_lost".equals(kind)) {
                net.udpDown();
            } else if ("nack".equals(kind)) {
                JSONArray arr = o.optJSONArray("seqs");
                if (arr != null) {
                    int[] seqs = new int[arr.length()];
                    for (int i = 0; i < arr.length(); i++) seqs[i] = arr.optInt(i);
                    net.udpResend(seqs);
                }
            } else if ("idr_request".equals(kind)) {
                onLog("收到 IDR 要求");
                cam.requestIdr();
            }
        } catch (Exception ignored) {}
    }

    private String pcIp = "";

    // ---------- 控制發送 ----------

    private void sendVideoParams() {
        net.sendControl("video_params", "{\"width\":" + vidW + ",\"height\":" + vidH
                + ",\"fps\":" + vidFps + ",\"codec\":\"h264\""
                + ",\"rot\":" + Math.max(lastRot, 0) + ",\"flip\":" + Math.max(lastFlip, 0) + "}");
    }

    private void sendOrientation() {
        net.sendControl("orientation", "{\"rot\":" + Math.max(lastRot, 0)
                + ",\"flip\":" + Math.max(lastFlip, 0) + "}");
    }

    // ---------- UI 同步 ----------

    private String liveLabel() {
        return " " + TIER_NAMES[resIdx] + " " + FPS_TIERS[fpsIdx] + "FPS";
    }

    private void refreshLabels() {
        ui.refreshTiers(TIER_NAMES[resIdx] + " ＞", FPS_TIERS[fpsIdx] + "FPS ＞", liveLabel(), vidMbps);
        net.setMaxLabel(TIER_NAMES[resIdx] + FPS_TIERS[fpsIdx]);
    }

    // ---------- 保命：過熱／閃退 ----------

    void startThermalWatch() {
        try {
            if (Build.VERSION.SDK_INT < 29) return;
            android.os.PowerManager pm = (android.os.PowerManager)
                    activity.getSystemService(Context.POWER_SERVICE);
            if (pm == null) return;
            stopThermalWatch();
            android.os.PowerManager.OnThermalStatusChangedListener l =
                    new android.os.PowerManager.OnThermalStatusChangedListener() {
                        public void onThermalStatusChanged(int status) {
                            if (status >= 4 && vidMbps > 2.0) {
                                vidMbps = 2.0;
                                onLog("過熱降碼率到 2Mbps（要全速請重開廣播）");
                                ui.syncBitrate(2.0);
                                if (broadcasting) applyTier(vidW, vidH, vidFps, vidMbps);
                            }
                        }
                    };
            thermalListener = l;
            pm.addThermalStatusListener(activity.getMainExecutor(), l);
        } catch (Exception ignored) {}
    }

    void stopThermalWatch() {
        try {
            if (Build.VERSION.SDK_INT < 29 || thermalListener == null) return;
            android.os.PowerManager pm = (android.os.PowerManager)
                    activity.getSystemService(Context.POWER_SERVICE);
            if (pm != null) pm.removeThermalStatusListener(
                    (android.os.PowerManager.OnThermalStatusChangedListener) thermalListener);
        } catch (Exception ignored) {}
        thermalListener = null;
    }

    void installCrashLog() {
        try {
            File dir = activity.getExternalFilesDir(null);
            if (dir == null) return;
            final File crash = new File(dir, "crash.log");
            if (crash.exists()) {
                String msg = "上次閃退：";
                try {
                    BufferedReader br = new BufferedReader(new FileReader(crash));
                    String l1 = br.readLine(), l2 = br.readLine();
                    br.close();
                    if (l1 != null) msg += l1;
                    if (l2 != null) msg += "｜" + l2;
                    if (msg.length() > 200) msg = msg.substring(0, 200);
                } catch (Exception ignored) {}
                ui.setStatus(msg);
                crash.renameTo(new File(dir, "crash-last.log"));
            }
            final Thread.UncaughtExceptionHandler prev = Thread.getDefaultUncaughtExceptionHandler();
            Thread.setDefaultUncaughtExceptionHandler((t, e) -> {
                try {
                    FileWriter fw = new FileWriter(crash);
                    fw.write(e.toString() + "\n");
                    for (StackTraceElement s : e.getStackTrace()) fw.write("  at " + s + "\n");
                    fw.close();
                } catch (Exception ignored) {}
                if (prev != null) prev.uncaughtException(t, e);
            });
        } catch (Exception ignored) {}
    }
}
