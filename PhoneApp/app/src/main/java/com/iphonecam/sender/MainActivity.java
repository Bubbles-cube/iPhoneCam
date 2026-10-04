package com.iphonecam.sender;

import android.Manifest;
import android.app.Activity;
import android.content.pm.PackageManager;
import android.graphics.Matrix;
import android.graphics.SurfaceTexture;
import android.os.Bundle;
import android.view.TextureView;
import android.widget.Button;
import android.widget.SeekBar;
import android.widget.TextView;

/**
 * v2 主介面：只管顯示＋使用者輸入，轉交 StreamController。
 * cover 預覽：視圖 match_parent（尺寸永遠＝畫框）＋實測尺寸現算矩陣，
 * 不手設 LayoutParams（v1 壓扁／縮角教訓）。
 */
public class MainActivity extends Activity implements StreamController.Ui {

    private TextureView preview;
    private android.widget.FrameLayout frameBox;
    private android.widget.ImageView standbyView;
    private TextView status, liveBadge, liveFps, connDot, connTitle, connIp, connSig;
    private TextView resVal, fpsVal, camVal, bitrateVal;
    private android.view.View pageHome, pageSettings, pageHelp;
    private Button navHome, navSettings, navHelp, btnToggle;
    private SeekBar bitrateSeek;
    private int lastFw = 0, lastFh = 0;
    private String fitInfo = "-";

    private StreamController ctl;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        setContentView(R.layout.activity_main);
        bindViews();
        ctl = new StreamController(this, this);
        wireButtons();
        if (frameBox != null) frameBox.addOnLayoutChangeListener(
                (v, l, t, r, bb, ol, ot, or, ob) -> {
                    int fw = frameBox.getWidth(), fh = frameBox.getHeight();
                    if (Math.abs(fw - lastFw) <= 2 && Math.abs(fh - lastFh) <= 2) return;
                    lastFw = fw;
                    lastFh = fh;
                    fitPreview();
                });
        preview.setSurfaceTextureListener(new TextureView.SurfaceTextureListener() {
            public void onSurfaceTextureAvailable(SurfaceTexture s, int w, int h) {
                ctl.previewSurface(s);
            }
            public void onSurfaceTextureSizeChanged(SurfaceTexture s, int w, int h) {
                fitPreview();
            }
            public boolean onSurfaceTextureDestroyed(SurfaceTexture s) {
                ctl.previewSurface(null);
                return true;
            }
            public void onSurfaceTextureUpdated(SurfaceTexture s) {}
        });
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.CAMERA}, 1);
        } else {
            ctl.previewSurface(preview.isAvailable() ? preview.getSurfaceTexture() : null);
        }
        setCamLabel(false);
        refreshTiers("720P ＞", "30FPS ＞", " 720P 30FPS", 4.0);
        setConn(false, "", NetEndpoint.localIp());
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 1 && grantResults.length > 0
                && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
            ctl.previewSurface(preview.isAvailable() ? preview.getSurfaceTexture() : null);
        } else if (requestCode == 1) {
            setStatus("需要相機權限才能預覽");
        }
    }

    @Override
    protected void onDestroy() {
        if (ctl != null) ctl.destroy();
        super.onDestroy();
    }

    // ---------- 版面接線 ----------

    private void bindViews() {
        preview = findViewById(R.id.preview);
        frameBox = findViewById(R.id.previewFrame);
        standbyView = findViewById(R.id.standbyView);
        status = findViewById(R.id.status);
        liveBadge = findViewById(R.id.liveBadge);
        liveFps = findViewById(R.id.liveFps);
        connDot = findViewById(R.id.connDot);
        connTitle = findViewById(R.id.connTitle);
        connIp = findViewById(R.id.connIp);
        connSig = findViewById(R.id.connSig);
        resVal = findViewById(R.id.resVal);
        fpsVal = findViewById(R.id.fpsVal);
        camVal = findViewById(R.id.camVal);
        bitrateVal = findViewById(R.id.bitrateVal);
        pageHome = findViewById(R.id.pageHome);
        pageSettings = findViewById(R.id.pageSettings);
        pageHelp = findViewById(R.id.pageHelp);
        navHome = findViewById(R.id.navHome);
        navSettings = findViewById(R.id.navSettings);
        navHelp = findViewById(R.id.navHelp);
        btnToggle = findViewById(R.id.btnToggle);
        bitrateSeek = findViewById(R.id.bitrateSeek);
    }

    private void wireButtons() {
        if (btnToggle != null) btnToggle.setOnClickListener(v -> ctl.toggle());
        if (navHome != null) navHome.setOnClickListener(v -> showPage("home"));
        if (navSettings != null) navSettings.setOnClickListener(v -> showPage("settings"));
        if (navHelp != null) navHelp.setOnClickListener(v -> showPage("help"));
        android.view.View rr = findViewById(R.id.resRow);
        android.view.View fr = findViewById(R.id.fpsRow);
        android.view.View cr = findViewById(R.id.camRow);
        if (rr != null) rr.setOnClickListener(v -> ctl.cycleRes());
        if (fr != null) fr.setOnClickListener(v -> ctl.cycleFps());
        if (cr != null) cr.setOnClickListener(v -> ctl.switchCamera());
        if (bitrateSeek != null) {
            bitrateSeek.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
                public void onProgressChanged(SeekBar s, int p, boolean fromUser) {
                    if (bitrateVal != null) bitrateVal.setText((p + 1) + "M");
                }
                public void onStartTrackingTouch(SeekBar s) {}
                public void onStopTrackingTouch(SeekBar s) {
                    ctl.setBitrate(s.getProgress() + 1);
                }
            });
        }
    }

    private void showPage(String which) {
        try {
            boolean home = "home".equals(which);
            boolean set = "settings".equals(which);
            if (pageHome != null) pageHome.setVisibility(home ? android.view.View.VISIBLE : android.view.View.GONE);
            if (pageSettings != null) pageSettings.setVisibility(set ? android.view.View.VISIBLE : android.view.View.GONE);
            if (pageHelp != null) pageHelp.setVisibility((!home && !set) ? android.view.View.VISIBLE : android.view.View.GONE);
            int pink = 0xFFFF5FD2, gray = 0xFF9AA3C0;
            if (navHome != null) navHome.setTextColor(home ? pink : gray);
            if (navSettings != null) navSettings.setTextColor(set ? pink : gray);
            if (navHelp != null) navHelp.setTextColor((!home && !set) ? pink : gray);
        } catch (Exception ignored) {}
    }

    // ---------- cover 預覽 ----------

    /** 單一等比 cover：s 取大填滿＋置中。尺寸全實測（視圖自身＋管線緩衝），無一處手設。 */
    public void fitPreview() {
        if (preview == null || ctl == null) return;
        try {
            int vw = preview.getWidth(), vh = preview.getHeight();
            if (vw <= 0 || vh <= 0) return;
            int bw = ctl.bufW(), bh = ctl.bufH();
            if (bw <= 0 || bh <= 0) return;
            float s = Math.max((float) vw / bw, (float) vh / bh);
            if (!(s > 0)) return;
            Matrix m = new Matrix();
            m.setScale(s, s);
            m.postTranslate((vw - bw * s) / 2f, (vh - bh * s) / 2f);
            preview.setTransform(m);
            fitInfo = "視" + vw + "x" + vh + "/buf" + bw + "x" + bh + "/x" + String.format("%.2f", s);
        } catch (Exception ignored) {}
    }

    public String coverInfo() {
        return fitInfo;
    }

    // ---------- StreamController.Ui ----------

    public void setStatus(final String s) {
        runOnUiThread(() -> { if (status != null) status.setText(s); });
    }

    public void setLive(final boolean live, final String fpsLabel) {
        runOnUiThread(() -> {
            if (liveBadge != null)
                liveBadge.setVisibility(live ? android.view.View.VISIBLE : android.view.View.INVISIBLE);
            if (liveFps != null) liveFps.setText(fpsLabel);
            if (btnToggle != null) btnToggle.setText(ctl.isBroadcasting() ? "■ 停止直播" : "（（•）） 開始直播");
            // 幀流穩定後矩陣再對一次（防過渡期量到舊緩衝）
            if (live && preview != null) {
                try { preview.postDelayed(() -> fitPreview(), 500); } catch (Exception ignored) {}
            }
        });
    }

    public void setConn(final boolean on, final String ip, final String local) {
        runOnUiThread(() -> {
            if (connTitle != null) connTitle.setText(on ? "已連接 PC" : "等待 PC 連接…");
            if (connIp != null) connIp.setText(on ? ("IP: " + ip) : ("本機 IP：" + NetEndpoint.localIp()));
            if (connDot != null) connDot.setTextColor(on ? 0xFF3DFF88 : 0xFF555577);
            if (connSig != null) {
                connSig.setText(on ? "訊號良好" : "待機");
                connSig.setTextColor(on ? 0xFF3DFF88 : 0xFF555577);
            }
        });
    }

    public void setStandby(final boolean on) {
        runOnUiThread(() -> {
            if (standbyView != null)
                standbyView.setVisibility(on ? android.view.View.VISIBLE : android.view.View.GONE);
        });
    }

    public void refreshTiers(final String res, final String fps, final String live, final double mbps) {
        runOnUiThread(() -> {
            if (resVal != null) resVal.setText(res);
            if (fpsVal != null) fpsVal.setText(fps);
            if (liveFps != null) liveFps.setText(live);
            syncBitrate(mbps);
            fitPreview();
        });
    }

    public void syncBitrate(final double mbps) {
        runOnUiThread(() -> {
            int p = Math.max(0, Math.min(39, (int) mbps - 1));
            if (bitrateSeek != null) bitrateSeek.setProgress(p);
            if (bitrateVal != null) bitrateVal.setText(((int) mbps) + "M");
        });
    }

    public void setCamLabel(final boolean front) {
        runOnUiThread(() -> {
            if (camVal != null) camVal.setText(front ? "前鏡頭 ＞" : "後鏡頭 ＞");
        });
    }
}
