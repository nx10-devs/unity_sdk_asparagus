package com.asparagus.sdk;

import android.app.Activity;
import android.app.KeyguardManager;
import android.content.Context;
import android.hardware.Sensor;
import android.hardware.SensorEvent;
import android.hardware.SensorEventListener;
import android.hardware.SensorManager;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;
import android.provider.Settings;
import android.view.Window;
import android.view.WindowManager;

import java.lang.reflect.Field;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Off-thread native Android sensor capture and system-state plugin for the Asparagus Unity SDK.
 *
 * <p>Uses a single background {@link HandlerThread} and pre-allocated primitive circular ring
 * buffers per sensor so capture runs independently of Unity's frame rate and continues through
 * full-screen ads while Unity is paused. All entry points and callbacks catch {@link Throwable}
 * so native errors can never crash the host game.</p>
 */
public final class AsparagusSensorPlugin {
    public static final int SENSOR_ACCELEROMETER = 1;
    public static final int SENSOR_GYROSCOPE = 2;
    public static final int SENSOR_GRAVITY = 3;
    public static final int SENSOR_MAGNETOMETER = 4;

    private static final int SENSOR_COUNT = 4;
    private static final int MIN_CAPACITY = 64;
    private static final int MAX_CAPACITY = 32768;
    private static final int MAX_DRAIN_CHUNK = 4096;
    // Android 12+ caps unprivileged sensor delivery at 200 Hz (5000 us period) without HIGH_SAMPLING_RATE_SENSORS.
    private static final int MIN_SAMPLING_PERIOD_US = 5000;

    private static final Object LOCK = new Object();
    private static final SensorRing[] RINGS = new SensorRing[SENSOR_COUNT];
    private static final AtomicInteger ERROR_COUNT = new AtomicInteger(0);

    private static volatile Context s_appContext;
    private static volatile SensorManager s_sensorManager;
    private static volatile boolean s_unityPaused = false;
    private static volatile int s_pauseBufferSeconds = 120;

    private static HandlerThread s_sensorThread;
    private static Handler s_sensorHandler;

    static {
        for (int i = 0; i < SENSOR_COUNT; i++) {
            RINGS[i] = new SensorRing(i + 1);
        }
    }

    private AsparagusSensorPlugin() {
    }

    private static final class SensorRing {
        final int sensorId;
        long[] timestampsUs;
        float[] xs;
        float[] ys;
        float[] zs;
        int capacity;
        int head;
        int count;

        long overflowCount;
        long overflowStartUs;
        long overflowEndUs;

        boolean active;
        SensorEventListener listener;

        SensorRing(int sensorId) {
            this.sensorId = sensorId;
        }

        void ensureCapacity(int targetCapacity) {
            int clamped = Math.max(MIN_CAPACITY, Math.min(MAX_CAPACITY, targetCapacity));
            if (timestampsUs == null || timestampsUs.length < clamped) {
                timestampsUs = new long[clamped];
                xs = new float[clamped];
                ys = new float[clamped];
                zs = new float[clamped];
            }
            capacity = clamped;
            head = 0;
            count = 0;
            overflowCount = 0L;
            overflowStartUs = 0L;
            overflowEndUs = 0L;
        }

        void push(long timestampUs, float x, float y, float z) {
            if (capacity <= 0 || timestampsUs == null) {
                return;
            }

            if (count == capacity) {
                long droppedTs = timestampsUs[head];
                if (overflowCount == 0L) {
                    overflowStartUs = droppedTs;
                }
                overflowEndUs = timestampUs;
                overflowCount++;

                timestampsUs[head] = timestampUs;
                xs[head] = x;
                ys[head] = y;
                zs[head] = z;
                head = (head + 1) % capacity;
                return;
            }

            int tail = (head + count) % capacity;
            timestampsUs[tail] = timestampUs;
            xs[tail] = x;
            ys[tail] = y;
            zs[tail] = z;
            count++;
        }

        void clearBuffer() {
            head = 0;
            count = 0;
            overflowCount = 0L;
            overflowStartUs = 0L;
            overflowEndUs = 0L;
        }
    }

    private static final class RingSensorListener implements SensorEventListener {
        private final SensorRing ring;

        RingSensorListener(SensorRing ring) {
            this.ring = ring;
        }

        @Override
        public void onSensorChanged(SensorEvent event) {
            try {
                if (event == null || event.values == null || event.values.length < 3) {
                    return;
                }

                if (s_unityPaused && s_pauseBufferSeconds <= 0) {
                    return;
                }

                long tsUs = event.timestamp / 1000L;
                float x = event.values[0];
                float y = event.values[1];
                float z = event.values[2];

                synchronized (LOCK) {
                    if (ring.active) {
                        ring.push(tsUs, x, y, z);
                    }
                }
            } catch (Throwable t) {
                ERROR_COUNT.incrementAndGet();
            }
        }

        @Override
        public void onAccuracyChanged(Sensor sensor, int accuracy) {
            // Not required by contract.
        }
    }

    public static boolean isAvailable() {
        try {
            return resolveSensorManager() != null;
        } catch (Throwable t) {
            ERROR_COUNT.incrementAndGet();
            return false;
        }
    }

    public static boolean hasSensor(int sensorId) {
        try {
            int androidType = toAndroidSensorType(sensorId);
            if (androidType < 0) {
                return false;
            }

            SensorManager manager = resolveSensorManager();
            if (manager == null) {
                return false;
            }

            return manager.getDefaultSensor(androidType) != null;
        } catch (Throwable t) {
            ERROR_COUNT.incrementAndGet();
            return false;
        }
    }

    /**
     * Starts off-thread capture for the specified sensor.
     *
     * @return {@code 1} if started, {@code 0} if sensor is missing or refused, {@code -1} on error.
     */
    public static int startSensor(int sensorId, int requestedHz, int bufferSeconds) {
        try {
            int idx = sensorId - 1;
            int androidType = toAndroidSensorType(sensorId);
            if (idx < 0 || idx >= SENSOR_COUNT || androidType < 0 || requestedHz <= 0) {
                return 0;
            }

            SensorManager manager = resolveSensorManager();
            if (manager == null) {
                return -1;
            }

            Sensor sensor = manager.getDefaultSensor(androidType);
            if (sensor == null) {
                return 0;
            }

            int safeBufferSeconds = Math.max(2, bufferSeconds);
            long requestedCapacity = (long) requestedHz * (long) safeBufferSeconds;
            int targetCapacity = requestedCapacity > MAX_CAPACITY ? MAX_CAPACITY : (int) requestedCapacity;
            int samplingPeriodUs = Math.max(MIN_SAMPLING_PERIOD_US, Math.round(1_000_000.0f / (float) requestedHz));

            synchronized (LOCK) {
                SensorRing ring = RINGS[idx];
                if (ring.listener != null) {
                    try {
                        manager.unregisterListener(ring.listener);
                    } catch (Throwable ignored) {
                        ERROR_COUNT.incrementAndGet();
                    }
                    ring.listener = null;
                    ring.active = false;
                }

                ensureHandlerThreadLocked();
                if (s_sensorHandler == null) {
                    return -1;
                }

                ring.ensureCapacity(targetCapacity);
                SensorEventListener listener = new RingSensorListener(ring);
                ring.listener = listener;
                ring.active = true;

                boolean registered = manager.registerListener(listener, sensor, samplingPeriodUs, s_sensorHandler);
                if (!registered) {
                    ring.active = false;
                    ring.listener = null;
                    return 0;
                }

                return 1;
            }
        } catch (Throwable t) {
            ERROR_COUNT.incrementAndGet();
            return -1;
        }
    }

    public static void stopSensor(int sensorId) {
        try {
            int idx = sensorId - 1;
            if (idx < 0 || idx >= SENSOR_COUNT) {
                return;
            }

            SensorManager manager = s_sensorManager;
            synchronized (LOCK) {
                SensorRing ring = RINGS[idx];
                ring.active = false;
                if (ring.listener != null && manager != null) {
                    try {
                        manager.unregisterListener(ring.listener);
                    } catch (Throwable t) {
                        ERROR_COUNT.incrementAndGet();
                    }
                }
                ring.listener = null;
                ring.clearBuffer();
            }
        } catch (Throwable t) {
            ERROR_COUNT.incrementAndGet();
        }
    }

    public static void stopAll() {
        try {
            s_unityPaused = false;
            SensorManager manager = s_sensorManager;
            synchronized (LOCK) {
                for (int i = 0; i < SENSOR_COUNT; i++) {
                    SensorRing ring = RINGS[i];
                    ring.active = false;
                    if (ring.listener != null && manager != null) {
                        try {
                            manager.unregisterListener(ring.listener);
                        } catch (Throwable t) {
                            ERROR_COUNT.incrementAndGet();
                        }
                    }
                    ring.listener = null;
                    ring.clearBuffer();
                }

                if (s_sensorThread != null) {
                    try {
                        s_sensorThread.quitSafely();
                    } catch (Throwable t) {
                        ERROR_COUNT.incrementAndGet();
                    }
                    s_sensorThread = null;
                    s_sensorHandler = null;
                }
            }
        } catch (Throwable t) {
            ERROR_COUNT.incrementAndGet();
        }
    }

    public static void setUnityPaused(boolean paused, int bufferSeconds) {
        try {
            s_pauseBufferSeconds = Math.max(0, bufferSeconds);
            s_unityPaused = paused;
        } catch (Throwable t) {
            ERROR_COUNT.incrementAndGet();
        }
    }

    public static long getSensorClockUs() {
        try {
            return SystemClock.elapsedRealtimeNanos() / 1000L;
        } catch (Throwable t) {
            ERROR_COUNT.incrementAndGet();
            return 0L;
        }
    }

    public static int getPendingSampleCount() {
        try {
            synchronized (LOCK) {
                int total = 0;
                for (int i = 0; i < SENSOR_COUNT; i++) {
                    total += RINGS[i].count;
                }
                return total;
            }
        } catch (Throwable t) {
            ERROR_COUNT.incrementAndGet();
            return 0;
        }
    }

    /**
     * Drains up to {@code maxSamples} queued samples across all sensor rings into a packed
     * {@code long[]} containing 3 longs per sample: {@code [timestampUs, (xBits<<32)|yBits, (sensorId<<32)|zBits]}.
     */
    public static long[] drainPackedSamples(int maxSamples) {
        try {
            int limit = Math.max(1, Math.min(MAX_DRAIN_CHUNK, maxSamples));
            synchronized (LOCK) {
                int totalAvailable = 0;
                for (int i = 0; i < SENSOR_COUNT; i++) {
                    totalAvailable += RINGS[i].count;
                }

                if (totalAvailable <= 0) {
                    return new long[0];
                }

                int toDrain = Math.min(limit, totalAvailable);
                long[] packed = new long[toDrain * 3];
                int outIndex = 0;
                int remaining = toDrain;

                for (int i = 0; i < SENSOR_COUNT && remaining > 0; i++) {
                    SensorRing ring = RINGS[i];
                    int take = Math.min(ring.count, remaining);
                    if (take <= 0) {
                        continue;
                    }

                    long sensorHeader = ((long) ring.sensorId) << 32;
                    for (int k = 0; k < take; k++) {
                        int slot = ring.head;
                        long tsUs = ring.timestampsUs[slot];
                        int xBits = Float.floatToRawIntBits(ring.xs[slot]);
                        int yBits = Float.floatToRawIntBits(ring.ys[slot]);
                        int zBits = Float.floatToRawIntBits(ring.zs[slot]);

                        packed[outIndex++] = tsUs;
                        packed[outIndex++] = (((long) xBits) << 32) | (yBits & 0xFFFFFFFFL);
                        packed[outIndex++] = sensorHeader | (zBits & 0xFFFFFFFFL);

                        ring.head = (slot + 1) % ring.capacity;
                        ring.count--;
                    }

                    remaining -= take;
                }

                return packed;
            }
        } catch (Throwable t) {
            ERROR_COUNT.incrementAndGet();
            return new long[0];
        }
    }

    public static boolean hasOverflow(int sensorId) {
        try {
            int idx = sensorId - 1;
            if (idx < 0 || idx >= SENSOR_COUNT) {
                return false;
            }

            synchronized (LOCK) {
                return RINGS[idx].overflowCount > 0L;
            }
        } catch (Throwable t) {
            ERROR_COUNT.incrementAndGet();
            return false;
        }
    }

    public static long[] consumeOverflowRange(int sensorId) {
        try {
            int idx = sensorId - 1;
            if (idx < 0 || idx >= SENSOR_COUNT) {
                return null;
            }

            synchronized (LOCK) {
                SensorRing ring = RINGS[idx];
                if (ring.overflowCount <= 0L) {
                    return null;
                }

                long[] result = new long[] {
                    ring.overflowCount,
                    ring.overflowStartUs,
                    ring.overflowEndUs
                };
                ring.overflowCount = 0L;
                ring.overflowStartUs = 0L;
                ring.overflowEndUs = 0L;
                return result;
            }
        } catch (Throwable t) {
            ERROR_COUNT.incrementAndGet();
            return null;
        }
    }

    public static int consumeErrorCount() {
        try {
            return ERROR_COUNT.getAndSet(0);
        } catch (Throwable t) {
            return 1;
        }
    }

    public static float getScreenBrightness() {
        try {
            Activity activity = resolveCurrentActivity();
            if (activity != null) {
                Window window = activity.getWindow();
                if (window != null) {
                    WindowManager.LayoutParams lp = window.getAttributes();
                    if (lp != null && lp.screenBrightness >= 0.0f && lp.screenBrightness <= 1.0f) {
                        return lp.screenBrightness;
                    }
                }
            }

            Context ctx = resolveAppContext();
            if (ctx == null) {
                return -1.0f;
            }

            int raw = Settings.System.getInt(ctx.getContentResolver(), Settings.System.SCREEN_BRIGHTNESS, -1);
            if (raw < 0) {
                return -1.0f;
            }

            float normalized = raw / 255.0f;
            if (normalized < 0.0f) return 0.0f;
            if (normalized > 1.0f) return 1.0f;
            return normalized;
        } catch (Throwable ignored) {
            // W15-04: omit brightness silently when platform/ROM denies access; never fault motion capture.
            return -1.0f;
        }
    }

    public static int getScreenLockedState() {
        try {
            Context ctx = resolveAppContext();
            if (ctx == null) {
                return -1;
            }

            Object service = ctx.getSystemService(Context.KEYGUARD_SERVICE);
            if (service instanceof KeyguardManager) {
                return ((KeyguardManager) service).isKeyguardLocked() ? 1 : 0;
            }

            return -1;
        } catch (Throwable ignored) {
            // W15-04: omit screen lock state silently when unavailable; never fault motion capture.
            return -1;
        }
    }

    private static int toAndroidSensorType(int sensorId) {
        switch (sensorId) {
            case SENSOR_ACCELEROMETER:
                return Sensor.TYPE_ACCELEROMETER;
            case SENSOR_GYROSCOPE:
                return Sensor.TYPE_GYROSCOPE;
            case SENSOR_GRAVITY:
                return Sensor.TYPE_GRAVITY;
            case SENSOR_MAGNETOMETER:
                return Sensor.TYPE_MAGNETIC_FIELD;
            default:
                return -1;
        }
    }

    private static void ensureHandlerThreadLocked() {
        if (s_sensorThread != null && s_sensorThread.isAlive() && s_sensorHandler != null) {
            return;
        }

        HandlerThread thread = new HandlerThread("AsparagusSensorThread");
        thread.setDaemon(true);
        thread.start();
        s_sensorThread = thread;
        s_sensorHandler = new Handler(thread.getLooper());
    }

    private static SensorManager resolveSensorManager() {
        SensorManager cached = s_sensorManager;
        if (cached != null) {
            return cached;
        }

        Context ctx = resolveAppContext();
        if (ctx == null) {
            return null;
        }

        Object service = ctx.getSystemService(Context.SENSOR_SERVICE);
        if (service instanceof SensorManager) {
            s_sensorManager = (SensorManager) service;
            return s_sensorManager;
        }

        return null;
    }

    private static Context resolveAppContext() {
        Context cached = s_appContext;
        if (cached != null) {
            return cached;
        }

        Activity activity = resolveCurrentActivity();
        if (activity != null) {
            Context appCtx = activity.getApplicationContext();
            s_appContext = appCtx != null ? appCtx : activity;
            return s_appContext;
        }

        return null;
    }

    private static Activity resolveCurrentActivity() {
        try {
            Class<?> unityPlayerClass = Class.forName("com.unity3d.player.UnityPlayer");
            Field activityField;
            try {
                activityField = unityPlayerClass.getField("currentActivity");
            } catch (NoSuchFieldException ignored) {
                activityField = unityPlayerClass.getDeclaredField("currentActivity");
                activityField.setAccessible(true);
            }

            Object value = activityField.get(null);
            if (value instanceof Activity) {
                return (Activity) value;
            }
        } catch (Throwable ignored) {
            // Running outside Unity Android player or activity not ready yet.
        }

        return null;
    }
}
