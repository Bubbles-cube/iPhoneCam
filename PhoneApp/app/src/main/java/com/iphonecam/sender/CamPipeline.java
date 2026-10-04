package com.iphonecam.sender;

import android.content.Context;
import android.graphics.ImageFormat;
import android.graphics.SurfaceTexture;
import android.hardware.SensorManager;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;
import android.util.Base64;
import android.util.Log;
import android.view.OrientationEventListener;
import android.view.Surface;

import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.List;

/**
 * v2 相機管線：Camera2 會話＋H.264 編碼＋drain＋陀螺儀方向＋SPS 解析。
 * v1 教訓內建：drain 認 encGen 代號（舊代直接退場）；SPS 三格式通吃；
 * 超採樣整組刪除（v2 不做）；只走 H.264。
 */
public class CamPipeline {

    static final String TAG = "IPC-CAM";

    public interface Listener {
        void onLog(String s);
        void onSessionReady();            // session 開起來
        void onSessionFailed(String why); // HAL 拒收／例外（controller 跑 fallback）
        void onSpsReady();                // sps/pps 到齊（controller 發 codec_config）
        void onFramesSent(long sent, long keys);
        void onEncoderDead();             // drain 莫名死亡（controller 排重啟，同代才執行）
        void onRotation(int rot, int flip);// 方向變（controller 發 video_params＋orientation）
        void onHeartbeat(int rot, int flip); // 每 2 秒重申（PC 漏跟隨自癒）
    }

    /** 幀出口：controller 決定走 UDP／TCP。avcc 已含 keyframe 的 SPS/PPS 前綴。 */
    public interface FrameSink {
        void sendFrame(byte[] avcc, boolean key);
    }

    private final Context appCtx;
    private final Listener listener;
    private final FrameSink sink;

    private final HandlerThread bgThread;
    private final Handler bg;
    private final CameraManager cameras;
    private String backId, frontId;
    private boolean useFront = false;
    private CameraDevice device;
    private CameraCaptureSession session;

    private volatile MediaCodec encoder;
    private Surface encoderSurface;
    private Thread drainThread;
    private volatile boolean streaming;
    private volatile int encGen = 0;

    // 目前編碼參數（controller 經 startEncoder 指定）
    private int vidW = 960, vidH = 720, vidFps = 30;
    private double vidMbps = 4.0;
    private int bufW = 0, bufH = 0; // SurfaceTexture 實際緩衝（cover 矩陣只認這個）

    private byte[] sps, pps;
    private boolean spsNotified = false;

    // 方向
    private OrientationEventListener orient;
    private int lastAngle = -1, curRot = -1, curFlip = -1, sensorOrient = 90;
    private int rotCandidate = -1;
    private long rotCandidateSince = 0;
    private long lastOrientMs = 0;

    private android.util.Range<Integer>[] fpsRanges = null;

    public CamPipeline(Context appCtx, Listener listener, FrameSink sink) {
        this.appCtx = appCtx;
        this.listener = listener;
        this.sink = sink;
        bgThread = new HandlerThread("cam");
        bgThread.start();
        bg = new Handler(bgThread.getLooper());
        cameras = (CameraManager) appCtx.getSystemService(Context.CAMERA_SERVICE);
        try {
            for (String id : cameras.getCameraIdList()) {
                Integer facing = cameras.getCameraCharacteristics(id)
                        .get(CameraCharacteristics.LENS_FACING);
                if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK) backId = id;
                if (facing != null && facing == CameraCharacteristics.LENS_FACING_FRONT) frontId = id;
            }
        } catch (CameraAccessException e) {
            listener.onLog("相機列舉失敗：" + e.getMessage());
        }
        orient = new OrientationEventListener(appCtx, SensorManager.SENSOR_DELAY_NORMAL) {
            public void onOrientationChanged(int o) {
                if (o == ORIENTATION_UNKNOWN) return;
                final int a = ((o + 45) / 90 * 90) % 360;
                bg.post(() -> debounceAngle(a));
            }
        };
        if (orient.canDetectOrientation()) orient.enable();
    }

    public void destroy() {
        try { if (orient != null) orient.disable(); } catch (Exception ignored) {}
        stopEncoder();
        closeSession();
        if (device != null) { try { device.close(); } catch (Exception ignored) {} device = null; }
        bgThread.quitSafely();
    }

    public void postDelayed(Runnable r, long ms) {
        try { bg.postDelayed(r, ms); } catch (Exception ignored) {}
    }

    // ---------- 相機 ----------

    public void openCamera() {
        if (device != null) return;
        String id = useFront && frontId != null ? frontId : backId;
        if (id == null) { listener.onLog("找不到鏡頭"); return; }
        try {
            CameraCharacteristics cc = cameras.getCameraCharacteristics(id);
            Integer so = cc.get(CameraCharacteristics.SENSOR_ORIENTATION);
            sensorOrient = so != null ? so : 90;
            fpsRanges = cc.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
            updateRotation(true);
            cameras.openCamera(id, new CameraDevice.StateCallback() {
                public void onOpened(CameraDevice d) { device = d; createSession(); }
                public void onDisconnected(CameraDevice d) { d.close(); device = null; }
                public void onError(CameraDevice d, int e) {
                    d.close(); device = null;
                    listener.onSessionFailed("相機錯誤 " + e);
                }
            }, bg);
        } catch (CameraAccessException | SecurityException e) {
            listener.onLog("開相機失敗：" + e.getMessage());
        }
    }

    public void restartCamera() {
        closeSession();
        if (device != null) { try { device.close(); } catch (Exception ignored) {} device = null; }
        openCamera();
    }

    public boolean switchCamera() {
        useFront = !useFront;
        restartCamera();
        return useFront;
    }

    public boolean isFront() {
        return useFront;
    }

    public void closeSession() {
        if (session != null) { try { session.close(); } catch (Exception ignored) {} session = null; }
    }

    public int getBufW() { return bufW; }
    public int getBufH() { return bufH; }

    // ---------- 方向 ----------

    void debounceAngle(int a) {
        long now = SystemClock.uptimeMillis();
        if (a != rotCandidate) { rotCandidate = a; rotCandidateSince = now; return; }
        if (a != lastAngle && now - rotCandidateSince >= 1000) {
            lastAngle = a;
            updateRotation(false);
        }
    }

    void updateRotation(boolean force) {
        try {
            if (lastAngle < 0) lastAngle = 0;
            int r, f;
            if (useFront) { r = (sensorOrient - lastAngle + 360) % 360; f = 1; }
            else { r = (sensorOrient + lastAngle) % 360; f = 0; }
            if (!force && r == curRot && f == curFlip) return;
            curRot = r; curFlip = f;
            listener.onRotation(Math.max(curRot, 0), Math.max(curFlip, 0));
        } catch (Exception ignored) {}
    }

    // ---------- 會話 ----------

    public void setEncodeParams(int w, int h, int fps, double mbps) {
        vidW = w; vidH = h; vidFps = fps; vidMbps = mbps;
    }

    void createSession() {
        if (device == null) return;
        SurfaceTexture st;
        try {
            st = previewTexture;
        } catch (Exception e) {
            return;
        }
        if (st == null) return;
        try {
            st.setDefaultBufferSize(vidW, vidH);
            bufW = vidW; bufH = vidH; // 記住實際緩衝（cover 矩陣只認這個）
            Surface previewSurface = new Surface(st);
            List<Surface> targets = new ArrayList<>();
            targets.add(previewSurface);
            if (encoderSurface != null) targets.add(encoderSurface);
            CaptureRequest.Builder req = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
            req.addTarget(previewSurface);
            if (encoderSurface != null) req.addTarget(encoderSurface);
            android.util.Range<Integer> fpsRange = pickFpsRange(vidFps);
            if (fpsRange != null) {
                req.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, fpsRange);
            }
            device.createCaptureSession(targets, new CameraCaptureSession.StateCallback() {
                public void onConfigured(CameraCaptureSession s) {
                    session = s;
                    try {
                        s.setRepeatingRequest(req.build(), null, bg);
                        listener.onSessionReady();
                    } catch (CameraAccessException e) {
                        listener.onSessionFailed("repeating 失敗：" + e.getMessage());
                    }
                }
                public void onConfigureFailed(CameraCaptureSession s) {
                    try { s.close(); } catch (Exception ignored) {}
                    session = null;
                    listener.onSessionFailed("HAL 拒收此組合");
                }
            }, bg);
        } catch (CameraAccessException e) {
            listener.onSessionFailed("createSession 例外：" + e.getMessage());
        }
    }

    private SurfaceTexture previewTexture;
    private final Object previewLock = new Object();

    /** Activity 的 SurfaceTexture 就緒／銷毀時呼叫（UI 執行緒亦可）。 */
    public void setPreviewTexture(SurfaceTexture st) {
        synchronized (previewLock) { previewTexture = st; }
        if (st != null && device != null && session == null) createSession();
    }

    android.util.Range<Integer> pickFpsRange(int fps) {
        try {
            if (fpsRanges != null) {
                for (android.util.Range<Integer> r : fpsRanges) {
                    if (r.getLower() == fps && r.getUpper() == fps) return r;
                }
                android.util.Range<Integer> best = null;
                for (android.util.Range<Integer> r : fpsRanges) {
                    if (r.getLower() <= fps && fps <= r.getUpper()) {
                        if (best == null || (r.getUpper() - r.getLower()) < (best.getUpper() - best.getLower()))
                            best = r;
                    }
                }
                if (best != null) return best;
            }
        } catch (Exception ignored) {}
        return null;
    }

    // ---------- 編碼 ----------

    public synchronized boolean startEncoder() {
        stopEncoder();
        try {
            MediaFormat fmt = MediaFormat.createVideoFormat("video/avc", vidW, vidH);
            fmt.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                    MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
            fmt.setInteger(MediaFormat.KEY_BIT_RATE, (int) (vidMbps * 1_000_000));
            fmt.setInteger(MediaFormat.KEY_FRAME_RATE, vidFps);
            fmt.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, vidFps >= 60 ? 1 : 2);
            try { fmt.setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR); }
            catch (Exception ignored) {}
            if (Build.VERSION.SDK_INT >= 30) {
                try { fmt.setInteger("low-latency", 1); } catch (Exception ignored) {}
            }
            encoder = MediaCodec.createEncoderByType("video/avc");
            encoder.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            encoderSurface = encoder.createInputSurface();
            encoder.start();
            suckCsd();
            createSession();
            streaming = true;
            encGen++;
            drainThread = new Thread(this::drainLoop, "cam-drain");
            drainThread.start();
            return true;
        } catch (Exception e) {
            listener.onLog("編碼啟動失敗：" + e.getMessage());
            stopEncoder();
            return false;
        }
    }

    public synchronized void stopEncoder() {
        streaming = false;
        if (drainThread != null) { try { drainThread.join(500); } catch (Exception ignored) {} drainThread = null; }
        if (encoder != null) { try { encoder.stop(); } catch (Exception ignored) {} try { encoder.release(); } catch (Exception ignored) {} encoder = null; }
        if (encoderSurface != null) { try { encoderSurface.release(); } catch (Exception ignored) {} encoderSurface = null; }
        sps = pps = null;
        spsNotified = false;
    }

    public boolean isStreaming() {
        return streaming;
    }

    public void requestIdr() {
        try {
            if (encoder == null) return;
            android.os.Bundle b = new android.os.Bundle();
            b.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
            encoder.setParameters(b);
        } catch (Exception ignored) {}
    }

    public void suckCsd() {
        try {
            if (encoder == null) return;
            MediaFormat of = encoder.getOutputFormat();
            for (String k : new String[]{"csd-0", "csd-1", "csd-2"}) {
                if (!of.containsKey(k)) continue;
                ByteBuffer bb = of.getByteBuffer(k);
                if (bb == null || bb.remaining() < 2) continue;
                byte[] b = new byte[bb.remaining()];
                bb.get(b);
                parseCsd(b);
            }
        } catch (Exception ignored) {}
    }

    void drainLoop() {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        final int myGen = encGen;
        long sent = 0, keys = 0;
        // encGen == myGen：換檔時舊 drain 還卡在 dequeue 裡，睡醒直接退場，不碰新編碼器（v1 閃退教訓）
        while (streaming && encoder != null && encGen == myGen) {
            try {
                int idx = encoder.dequeueOutputBuffer(info, 2000);
                if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) { suckCsd(); continue; }
                if (idx < 0) continue;
                ByteBuffer buf = encoder.getOutputBuffer(idx);
                byte[] data = new byte[info.size];
                buf.get(data);
                boolean key = (info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0;
                boolean cfg = (info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                if (cfg) parseCsd(data);
                if (!cfg || key) {
                    byte[] avcc = data;
                    if (key && sps != null && pps != null) avcc = concat(getSeqHead(), data);
                    sink.sendFrame(avcc, key);
                    sent++;
                    if (key) keys++;
                    if (sent % 300 == 0) listener.onFramesSent(sent, keys);
                }
                long nowMs = SystemClock.uptimeMillis();
                if (nowMs - lastOrientMs >= 2000) { lastOrientMs = nowMs; listener.onHeartbeat(Math.max(curRot, 0), Math.max(curFlip, 0)); }
                encoder.releaseOutputBuffer(idx, false);
            } catch (Exception e) {
                listener.onLog("drain 死：" + e);
                break;
            }
        }
        if (encGen == myGen) listener.onEncoderDead(); // 舊代直接退場，不排重啟
    }

    // ---------- SPS（H.264 三格式通吃） ----------

    void parseCsd(byte[] csd) {
        try {
            if (csd == null || csd.length < 2) return;
            if (startsWith(csd, 0, 0, 0, 1) || startsWith(csd, 0, 0, 1)) {
                for (byte[] u : splitAnnexB(csd)) classifyNal(u);
            } else if ((csd[0] & 0xFF) == 1 && csd.length > 8) {
                parseAvcC(csd);
            } else {
                classifyNal(csd); // 單顆裸 NAL（如 67 64 00 1F… 開頭的 SPS）
            }
            if (sps != null && pps != null && !spsNotified) {
                spsNotified = true;
                listener.onSpsReady();
            }
        } catch (Exception e) {
            listener.onLog("csd 解析失敗：" + e.getMessage());
        }
    }

    static boolean startsWith(byte[] b, int... pre) {
        if (b.length < pre.length) return false;
        for (int i = 0; i < pre.length; i++) if ((b[i] & 0xFF) != pre[i]) return false;
        return true;
    }

    static List<byte[]> splitAnnexB(byte[] b) {
        List<byte[]> out = new ArrayList<>();
        int start = -1;
        for (int i = 0; i + 3 < b.length; i++) {
            int sc = (b[i] == 0 && b[i + 1] == 0)
                    ? (b[i + 2] == 1 ? 3 : (b[i + 2] == 0 && b[i + 3] == 1 ? 4 : 0)) : 0;
            if (sc > 0) {
                if (start >= 0 && i > start) out.add(java.util.Arrays.copyOfRange(b, start, i));
                start = i + sc;
                i += sc - 1;
            }
        }
        if (start >= 0 && start < b.length) out.add(java.util.Arrays.copyOfRange(b, start, b.length));
        return out;
    }

    void classifyNal(byte[] u) {
        if (u == null || u.length < 2) return;
        int t = u[0] & 0x1F; // H.264: 7=SPS 8=PPS
        if (t == 7 && sps == null) sps = u;
        else if (t == 8 && pps == null) pps = u;
    }

    void parseAvcC(byte[] csd) {
        int nSps = csd[5] & 0x1F, p = 6;
        for (int k = 0; k < nSps && p + 2 <= csd.length; k++) {
            int len = ((csd[p] & 0xFF) << 8) | (csd[p + 1] & 0xFF);
            if (len <= 0 || p + 2 + len > csd.length) break;
            if (sps == null) { sps = new byte[len]; System.arraycopy(csd, p + 2, sps, 0, len); }
            p += 2 + len;
        }
        if (p >= csd.length) return;
        int nPps = csd[p++] & 0xFF;
        for (int k = 0; k < nPps && p + 2 <= csd.length; k++) {
            int len = ((csd[p] & 0xFF) << 8) | (csd[p + 1] & 0xFF);
            if (len <= 0 || p + 2 + len > csd.length) break;
            if (pps == null) { pps = new byte[len]; System.arraycopy(csd, p + 2, pps, 0, len); }
            p += 2 + len;
        }
    }

    public byte[] getSeqHead() {
        if (sps == null || pps == null) return null;
        return concat(annex(sps), annex(pps));
    }

    static byte[] annex(byte[] nal) {
        byte[] out = new byte[nal.length + 4];
        out[0] = 0; out[1] = 0; out[2] = 0; out[3] = 1;
        System.arraycopy(nal, 0, out, 4, nal.length);
        return out;
    }

    static byte[] concat(byte[]... parts) {
        int n = 0;
        for (byte[] p : parts) n += p.length;
        byte[] out = new byte[n];
        int o = 0;
        for (byte[] p : parts) { System.arraycopy(p, 0, out, o, p.length); o += p.length; }
        return out;
    }

    public String seqHeadBase64() {
        byte[] head = getSeqHead();
        if (head == null || pps == null) return null;
        return Base64.encodeToString(head, Base64.NO_WRAP) + "|" + Base64.encodeToString(pps, Base64.NO_WRAP);
    }

    public int getSpsLen() { return sps != null ? sps.length : 0; }
    public int getPpsLen() { return pps != null ? pps.length : 0; }
}
