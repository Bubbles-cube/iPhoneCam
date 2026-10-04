package com.iphonecam.sender;

import android.os.Build;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Collections;
import java.util.Enumeration;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * v2 網路端：TCP 伺服器（CAM1 控制＋TCP 備援影像）＋UDP 影像（CUM1）＋UDP 信標。
 * 不碰相機、不碰 UI；事件經 Listener 交給 StreamController。
 */
public class NetEndpoint {

    static final String TAG = "IPC-NET";

    public static final int CAM_PORT = 19300;
    public static final int BEACON_PORT = 19301;
    public static final int UDP_PORT = 19302;
    public static final byte T_VIDEO = 0x01;
    public static final byte T_CONTROL = 0x03;

    static final int UDP_MAGIC = 0x43554D31; // "CUM1"
    static final int UDP_MTU = 1400;

    public interface Listener {
        void onLog(String s);
        void onPcConnected(String ip);
        void onPcDisconnected(String ip);
        void onControl(String kind, JSONObject json);
    }

    private final Listener listener;
    private volatile boolean running;
    private Thread serverThread, beaconThread;
    private ServerSocket serverSocket;
    private Socket clientSocket;
    private OutputStream clientOut;
    private String clientIp = "-";
    private String lastPcIp = "";
    private String maxLabel = "720P30";

    // UDP 發送端狀態
    private DatagramSocket udpSock = null;
    private InetAddress udpAddr = null;
    private int udpPort = UDP_PORT;
    private volatile boolean useUdp = false;
    private int udpSeq = 0;
    private final LinkedHashMap<Integer, byte[]> udpCache =
            new LinkedHashMap<Integer, byte[]>(40, 0.75f, true) {
                protected boolean removeEldestEntry(Map.Entry<Integer, byte[]> e) {
                    return size() > 32;
                }
            };

    public NetEndpoint(Listener listener) {
        this.listener = listener;
    }

    // ---------- 生命週期 ----------

    public void start() {
        running = true;
        serverThread = new Thread(this::serverLoop, "cam-server");
        serverThread.start();
        beaconThread = new Thread(this::beaconLoop, "cam-beacon");
        beaconThread.start();
    }

    public void stop() {
        running = false;
        closeClient();
        useUdp = false;
        try { if (udpSock != null) udpSock.close(); } catch (Exception ignored) {}
        udpSock = null;
        try { if (serverSocket != null) serverSocket.close(); } catch (Exception ignored) {}
        serverSocket = null;
        joinQuiet(serverThread);
        joinQuiet(beaconThread);
        serverThread = beaconThread = null;
    }

    private static void joinQuiet(Thread t) {
        if (t != null) { try { t.join(500); } catch (Exception ignored) {} }
    }

    public boolean hasClient() {
        return clientOut != null;
    }

    public void setMaxLabel(String s) {
        maxLabel = s;
    }

    // ---------- TCP 伺服器 ----------

    private void serverLoop() {
        Log.i(TAG, "server 執行緒啟動，bind " + CAM_PORT);
        try (ServerSocket srv = new ServerSocket(CAM_PORT)) {
            serverSocket = srv;
            while (running) {
                Socket c;
                try {
                    c = srv.accept();
                } catch (IOException e) {
                    if (running) Log.i(TAG, "accept 失敗：" + e);
                    break;
                }
                try { c.setTcpNoDelay(true); } catch (Exception ignored) {}
                closeClient();
                clientSocket = c;
                clientIp = c.getInetAddress().getHostAddress();
                lastPcIp = clientIp;
                try {
                    clientOut = c.getOutputStream();
                } catch (IOException e) {
                    closeClient();
                    continue;
                }
                final String ip = clientIp;
                listener.onLog("PC 連入：" + ip);
                listener.onPcConnected(ip);
                readLoop(c);
                closeClient();
                listener.onLog("PC 斷線：" + ip);
                listener.onPcDisconnected(ip);
            }
        } catch (IOException e) {
            listener.onLog("監聽失敗：" + e.getMessage());
        } catch (Throwable t) {
            Log.i(TAG, "server 非 IO 死亡：" + t);
        }
        Log.i(TAG, "server 執行緒結束");
    }

    private void readLoop(Socket c) {
        try {
            InputStream in = c.getInputStream();
            while (running && !c.isClosed()) {
                byte[] hdr = readN(in, 17);
                if (hdr[0] != 0x43 || hdr[1] != 0x41 || hdr[2] != 0x4D || hdr[3] != 0x31) {
                    Log.i(TAG, "壞訊框魔數，斷線");
                    break;
                }
                int len = ((hdr[13] & 0xFF) << 24) | ((hdr[14] & 0xFF) << 16)
                        | ((hdr[15] & 0xFF) << 8) | (hdr[16] & 0xFF);
                byte[] pl = len > 0 ? readN(in, len) : new byte[0];
                if (hdr[4] == T_CONTROL) {
                    try {
                        JSONObject o = new JSONObject(new String(pl, "UTF-8"));
                        listener.onControl(o.optString("kind"), o);
                    } catch (Exception e) {
                        Log.i(TAG, "控制解析失敗：" + e.getMessage());
                    }
                }
            }
        } catch (Exception ignored) {}
    }

    synchronized void closeClient() {
        try { if (clientSocket != null) clientSocket.close(); } catch (Exception ignored) {}
        clientSocket = null;
        clientOut = null;
        useUdp = false; // 斷線回 TCP 預設，下個客戶端重新 udp_ready
    }

    // ---------- 控制發送 ----------

    public synchronized void sendControl(String kind, String jsonBody) {
        if (clientOut == null) return;
        try {
            String json = jsonBody.trim();
            if (json.startsWith("{")) json = json.substring(0, json.length() - 1) + ",\"kind\":\"" + kind + "\"}";
            else json = "{\"kind\":\"" + kind + "\"}";
            writeFrame(clientOut, T_CONTROL, json.getBytes("UTF-8"));
        } catch (IOException e) {
            closeClient();
        }
    }

    /** TCP 影像（UDP 啟用前／斷流後的備援）。 */
    public void sendVideo(byte[] avcc) {
        OutputStream o = clientOut;
        if (o == null || avcc.length == 0) return;
        try {
            synchronized (this) {
                if (clientOut == null) return;
                writeFrame(clientOut, T_VIDEO, avcc);
            }
        } catch (IOException e) {
            closeClient();
        }
    }

    static void writeFrame(OutputStream o, byte type, byte[] payload) throws IOException {
        long ts = System.nanoTime() / 1000L;
        o.write(new byte[]{(byte) 0x43, (byte) 0x41, (byte) 0x4D, (byte) 0x31, type,
                (byte) (ts >>> 56), (byte) (ts >>> 48), (byte) (ts >>> 40), (byte) (ts >>> 32),
                (byte) (ts >>> 24), (byte) (ts >>> 16), (byte) (ts >>> 8), (byte) ts,
                (byte) (payload.length >>> 24), (byte) (payload.length >>> 16),
                (byte) (payload.length >>> 8), (byte) payload.length});
        o.write(payload);
        o.flush();
    }

    static byte[] readN(InputStream in, int n) throws IOException {
        byte[] b = new byte[n];
        int got = 0;
        while (got < n) {
            int r = in.read(b, got, n - got);
            if (r < 0) throw new IOException("closed");
            got += r;
        }
        return b;
    }

    // ---------- UDP 影像發送 ----------

    public synchronized void udpSetup(String ip, int port) {
        try {
            if (udpSock == null || udpSock.isClosed()) udpSock = new DatagramSocket();
            udpAddr = InetAddress.getByName(ip);
            udpPort = port > 0 ? port : UDP_PORT;
            synchronized (udpCache) { udpCache.clear(); }
            useUdp = true;
            listener.onLog("UDP 影像啟動：" + ip + ":" + udpPort);
        } catch (Exception e) {
            useUdp = false;
            listener.onLog("UDP 啟動失敗（續用 TCP）：" + e.getMessage());
        }
    }

    public void udpSendFrame(byte[] avcc, boolean key) {
        DatagramSocket ds = udpSock;
        InetAddress addr = udpAddr;
        if (ds == null || ds.isClosed() || addr == null) return;
        try {
            int seq;
            synchronized (this) { seq = udpSeq++; }
            synchronized (udpCache) { udpCache.put(seq, avcc); }
            sendUdpPackets(ds, addr, seq, avcc, key ? 1 : 0);
        } catch (Exception ignored) {}
    }

    private void sendUdpPackets(DatagramSocket ds, InetAddress addr, int seq, byte[] avcc, int flags)
            throws IOException {
        int total = (avcc.length + UDP_MTU - 1) / UDP_MTU;
        if (total < 1) total = 1;
        if (total > 600) return; // 單幀超過 ~840KB 不合理，丟（防爆）
        long ts = System.nanoTime() / 1000L;
        for (int i = 0; i < total; i++) {
            int off = i * UDP_MTU;
            int len = Math.min(UDP_MTU, avcc.length - off);
            ByteBuffer b = ByteBuffer.allocate(21 + len);
            b.order(ByteOrder.BIG_ENDIAN);
            b.putInt(UDP_MAGIC);
            b.putInt(seq);
            b.putLong(ts);
            b.put((byte) flags);
            b.putShort((short) i);
            b.putShort((short) total);
            b.put(avcc, off, len);
            byte[] pkt = b.array();
            ds.send(new DatagramPacket(pkt, pkt.length, addr, udpPort));
        }
    }

    public void udpResend(int[] seqs) {
        if (udpSock == null || udpAddr == null) return;
        try {
            for (int seq : seqs) {
                byte[] avcc;
                synchronized (udpCache) { avcc = udpCache.get(seq); }
                if (avcc == null) continue;
                sendUdpPackets(udpSock, udpAddr, seq, avcc, 0); // 重傳不佔新 seq
            }
        } catch (Exception ignored) {}
    }

    public void udpDown() {
        useUdp = false;
        listener.onLog("UDP 斷流，切回 TCP（不斷線）");
    }

    public boolean usingUdp() {
        return useUdp;
    }

    // ---------- UDP 信標 ----------

    private void beaconLoop() {
        try {
            DatagramSocket ds = new DatagramSocket();
            ds.setBroadcast(true);
            String model = Build.MODEL;
            while (running) {
                try {
                    JSONObject o = new JSONObject();
                    o.put("kind", "beacon");
                    o.put("name", model);
                    o.put("ip", localIp());
                    o.put("port", CAM_PORT);
                    JSONArray arr = new JSONArray();
                    arr.put("h264");
                    o.put("codec", arr);
                    o.put("max", maxLabel);
                    byte[] b = o.toString().getBytes("UTF-8");
                    ds.send(new DatagramPacket(b, b.length,
                            InetAddress.getByName("255.255.255.255"), BEACON_PORT));
                    // 家用 AP 廣播常掉包：已知 PC 補一發單播
                    try {
                        String pc = lastPcIp;
                        if (pc != null && pc.length() > 7 && pc.indexOf(':') < 0)
                            ds.send(new DatagramPacket(b, b.length,
                                    InetAddress.getByName(pc), BEACON_PORT));
                    } catch (Exception ignored) {}
                } catch (Exception ignored) {}
                Thread.sleep(2000);
            }
            ds.close();
        } catch (Exception ignored) {}
    }

    public static String localIp() {
        try {
            Enumeration<NetworkInterface> nis = NetworkInterface.getNetworkInterfaces();
            for (NetworkInterface ni : Collections.list(nis)) {
                if (!ni.isUp() || ni.isLoopback()) continue;
                for (java.net.InetAddress a : Collections.list(ni.getInetAddresses())) {
                    if (a.isSiteLocalAddress() && a.getHostAddress() != null
                            && a.getHostAddress().indexOf(':') < 0) return a.getHostAddress();
                }
            }
        } catch (Exception ignored) {}
        return "0.0.0.0";
    }
}
