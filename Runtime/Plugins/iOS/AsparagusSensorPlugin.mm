#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <CoreMotion/CoreMotion.h>

#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <vector>

namespace {

constexpr int32_t kSensorAccelerometer = 1;
constexpr int32_t kSensorGyroscope = 2;
constexpr int32_t kSensorGravity = 3;
constexpr int32_t kSensorMagnetometer = 4;
constexpr int32_t kSensorCount = 4;
constexpr int32_t kMinCapacity = 64;
constexpr int32_t kMaxCapacity = 32768;

struct SensorRing {
    int32_t sensorId = 0;
    std::vector<int64_t> timestampsUs;
    std::vector<float> xs;
    std::vector<float> ys;
    std::vector<float> zs;
    int32_t capacity = 0;
    int32_t head = 0;
    int32_t count = 0;

    int64_t overflowCount = 0;
    int64_t overflowStartUs = 0;
    int64_t overflowEndUs = 0;

    bool active = false;

    void EnsureCapacity(int32_t targetCapacity) {
        int32_t clamped = targetCapacity;
        if (clamped < kMinCapacity) clamped = kMinCapacity;
        if (clamped > kMaxCapacity) clamped = kMaxCapacity;

        if (static_cast<int32_t>(timestampsUs.size()) < clamped) {
            timestampsUs.resize(static_cast<size_t>(clamped), 0);
            xs.resize(static_cast<size_t>(clamped), 0.0f);
            ys.resize(static_cast<size_t>(clamped), 0.0f);
            zs.resize(static_cast<size_t>(clamped), 0.0f);
        }

        capacity = clamped;
        head = 0;
        count = 0;
        overflowCount = 0;
        overflowStartUs = 0;
        overflowEndUs = 0;
    }

    void Push(int64_t tsUs, float x, float y, float z) {
        if (capacity <= 0) {
            return;
        }

        if (count == capacity) {
            int64_t droppedTs = timestampsUs[static_cast<size_t>(head)];
            if (overflowCount == 0) {
                overflowStartUs = droppedTs;
            }
            overflowEndUs = tsUs;
            overflowCount++;

            timestampsUs[static_cast<size_t>(head)] = tsUs;
            xs[static_cast<size_t>(head)] = x;
            ys[static_cast<size_t>(head)] = y;
            zs[static_cast<size_t>(head)] = z;
            head = (head + 1) % capacity;
            return;
        }

        int32_t tail = (head + count) % capacity;
        timestampsUs[static_cast<size_t>(tail)] = tsUs;
        xs[static_cast<size_t>(tail)] = x;
        ys[static_cast<size_t>(tail)] = y;
        zs[static_cast<size_t>(tail)] = z;
        count++;
    }

    void ClearBuffer() {
        head = 0;
        count = 0;
        overflowCount = 0;
        overflowStartUs = 0;
        overflowEndUs = 0;
    }
};

std::mutex g_mutex;
SensorRing g_rings[kSensorCount];
std::atomic<int32_t> g_errorCount{0};
std::atomic<bool> g_unityPaused{false};
std::atomic<int32_t> g_pauseBufferSeconds{120};
std::atomic<int32_t> g_screenLockedState{-1};

CMMotionManager* g_motionManager = nil;
NSOperationQueue* g_motionQueue = nil;
bool g_initialized = false;
id g_lockObserver = nil;
id g_unlockObserver = nil;

inline uint32_t FloatToRawBits(float value) {
    uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

void EnsureInitializedLocked() {
    if (g_initialized) {
        return;
    }

    for (int32_t i = 0; i < kSensorCount; i++) {
        g_rings[i].sensorId = i + 1;
    }

    g_motionManager = [[CMMotionManager alloc] init];
    g_motionQueue = [[NSOperationQueue alloc] init];
    g_motionQueue.name = @"com.asparagus.sdk.motion";
    g_motionQueue.maxConcurrentOperationCount = 1;
    g_motionQueue.qualityOfService = NSQualityOfServiceUserInteractive;

    NSNotificationCenter* nc = [NSNotificationCenter defaultCenter];
    g_lockObserver = [nc addObserverForName:UIApplicationProtectedDataWillBecomeUnavailable
                                     object:nil
                                      queue:nil
                                 usingBlock:^(__unused NSNotification* note) {
                                     g_screenLockedState.store(1);
                                 }];
    g_unlockObserver = [nc addObserverForName:UIApplicationProtectedDataDidBecomeAvailable
                                       object:nil
                                        queue:nil
                                   usingBlock:^(__unused NSNotification* note) {
                                       g_screenLockedState.store(0);
                                   }];

    if ([NSThread isMainThread]) {
        g_screenLockedState.store([UIApplication sharedApplication].isProtectedDataAvailable ? 0 : 1);
    }

    g_initialized = true;
}

void PushSampleFromCallback(int32_t sensorIndex, double timestampSec, double x, double y, double z) {
    if (g_unityPaused.load() && g_pauseBufferSeconds.load() <= 0) {
        return;
    }

    int64_t tsUs = static_cast<int64_t>(std::llround(timestampSec * 1000000.0));
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_rings[sensorIndex].active) {
        g_rings[sensorIndex].Push(tsUs, static_cast<float>(x), static_cast<float>(y), static_cast<float>(z));
    }
}

void StopSensorLocked(int32_t sensorId) {
    int32_t idx = sensorId - 1;
    if (idx < 0 || idx >= kSensorCount) {
        return;
    }

    g_rings[idx].active = false;
    g_rings[idx].ClearBuffer();

    if (g_motionManager == nil) {
        return;
    }

    switch (sensorId) {
        case kSensorAccelerometer:
            if (g_motionManager.isAccelerometerActive) {
                [g_motionManager stopAccelerometerUpdates];
            }
            break;
        case kSensorGyroscope:
            if (g_motionManager.isGyroActive) {
                [g_motionManager stopGyroUpdates];
            }
            break;
        case kSensorGravity:
            if (g_motionManager.isDeviceMotionActive) {
                [g_motionManager stopDeviceMotionUpdates];
            }
            break;
        case kSensorMagnetometer:
            if (g_motionManager.isMagnetometerActive) {
                [g_motionManager stopMagnetometerUpdates];
            }
            break;
        default:
            break;
    }
}

} // namespace

extern "C" {

bool AsparagusIos_IsAvailable(void) {
    @try {
        try {
            std::lock_guard<std::mutex> lock(g_mutex);
            EnsureInitializedLocked();
            return g_motionManager != nil;
        } catch (...) {
            g_errorCount.fetch_add(1);
            return false;
        }
    } @catch (__unused NSException* ex) {
        g_errorCount.fetch_add(1);
        return false;
    }
}

bool AsparagusIos_HasSensor(int32_t sensorId) {
    @try {
        try {
            std::lock_guard<std::mutex> lock(g_mutex);
            EnsureInitializedLocked();
            if (g_motionManager == nil) {
                return false;
            }

            switch (sensorId) {
                case kSensorAccelerometer:
                    return g_motionManager.isAccelerometerAvailable;
                case kSensorGyroscope:
                    return g_motionManager.isGyroAvailable;
                case kSensorGravity:
                    return g_motionManager.isDeviceMotionAvailable;
                case kSensorMagnetometer:
                    return g_motionManager.isMagnetometerAvailable;
                default:
                    return false;
            }
        } catch (...) {
            g_errorCount.fetch_add(1);
            return false;
        }
    } @catch (__unused NSException* ex) {
        g_errorCount.fetch_add(1);
        return false;
    }
}

int32_t AsparagusIos_StartSensor(int32_t sensorId, int32_t requestedHz, int32_t bufferSeconds) {
    @try {
        try {
            int32_t idx = sensorId - 1;
            if (idx < 0 || idx >= kSensorCount || requestedHz <= 0) {
                return 0;
            }

            std::lock_guard<std::mutex> lock(g_mutex);
            EnsureInitializedLocked();
            if (g_motionManager == nil || g_motionQueue == nil) {
                return -1;
            }

            StopSensorLocked(sensorId);

            int32_t safeBufferSeconds = bufferSeconds < 2 ? 2 : bufferSeconds;
            int64_t requestedCap = static_cast<int64_t>(requestedHz) * static_cast<int64_t>(safeBufferSeconds);
            int32_t targetCap = requestedCap > kMaxCapacity ? kMaxCapacity : static_cast<int32_t>(requestedCap);
            double intervalSec = 1.0 / static_cast<double>(requestedHz);

            switch (sensorId) {
                case kSensorAccelerometer: {
                    if (!g_motionManager.isAccelerometerAvailable) {
                        return 0;
                    }
                    g_rings[idx].EnsureCapacity(targetCap);
                    g_rings[idx].active = true;
                    g_motionManager.accelerometerUpdateInterval = intervalSec;
                    [g_motionManager startAccelerometerUpdatesToQueue:g_motionQueue
                                                          withHandler:^(CMAccelerometerData* data, NSError* error) {
                        @try {
                            try {
                                if (error != nil || data == nil) {
                                    if (error != nil) g_errorCount.fetch_add(1);
                                    return;
                                }
                                PushSampleFromCallback(0, data.timestamp, data.acceleration.x, data.acceleration.y, data.acceleration.z);
                            } catch (...) {
                                g_errorCount.fetch_add(1);
                            }
                        } @catch (__unused NSException* ex) {
                            g_errorCount.fetch_add(1);
                        }
                    }];
                    return 1;
                }

                case kSensorGyroscope: {
                    if (!g_motionManager.isGyroAvailable) {
                        return 0;
                    }
                    g_rings[idx].EnsureCapacity(targetCap);
                    g_rings[idx].active = true;
                    g_motionManager.gyroUpdateInterval = intervalSec;
                    [g_motionManager startGyroUpdatesToQueue:g_motionQueue
                                                 withHandler:^(CMGyroData* data, NSError* error) {
                        @try {
                            try {
                                if (error != nil || data == nil) {
                                    if (error != nil) g_errorCount.fetch_add(1);
                                    return;
                                }
                                PushSampleFromCallback(1, data.timestamp, data.rotationRate.x, data.rotationRate.y, data.rotationRate.z);
                            } catch (...) {
                                g_errorCount.fetch_add(1);
                            }
                        } @catch (__unused NSException* ex) {
                            g_errorCount.fetch_add(1);
                        }
                    }];
                    return 1;
                }

                case kSensorGravity: {
                    if (!g_motionManager.isDeviceMotionAvailable) {
                        return 0;
                    }
                    g_rings[idx].EnsureCapacity(targetCap);
                    g_rings[idx].active = true;
                    g_motionManager.deviceMotionUpdateInterval = intervalSec;
                    [g_motionManager startDeviceMotionUpdatesToQueue:g_motionQueue
                                                         withHandler:^(CMDeviceMotion* motion, NSError* error) {
                        @try {
                            try {
                                if (error != nil || motion == nil) {
                                    if (error != nil) g_errorCount.fetch_add(1);
                                    return;
                                }
                                PushSampleFromCallback(2, motion.timestamp, motion.gravity.x, motion.gravity.y, motion.gravity.z);
                            } catch (...) {
                                g_errorCount.fetch_add(1);
                            }
                        } @catch (__unused NSException* ex) {
                            g_errorCount.fetch_add(1);
                        }
                    }];
                    return 1;
                }

                case kSensorMagnetometer: {
                    if (!g_motionManager.isMagnetometerAvailable) {
                        return 0;
                    }
                    g_rings[idx].EnsureCapacity(targetCap);
                    g_rings[idx].active = true;
                    g_motionManager.magnetometerUpdateInterval = intervalSec;
                    [g_motionManager startMagnetometerUpdatesToQueue:g_motionQueue
                                                         withHandler:^(CMMagnetometerData* data, NSError* error) {
                        @try {
                            try {
                                if (error != nil || data == nil) {
                                    if (error != nil) g_errorCount.fetch_add(1);
                                    return;
                                }
                                PushSampleFromCallback(3, data.timestamp, data.magneticField.x, data.magneticField.y, data.magneticField.z);
                            } catch (...) {
                                g_errorCount.fetch_add(1);
                            }
                        } @catch (__unused NSException* ex) {
                            g_errorCount.fetch_add(1);
                        }
                    }];
                    return 1;
                }

                default:
                    return 0;
            }
        } catch (...) {
            g_errorCount.fetch_add(1);
            return -1;
        }
    } @catch (__unused NSException* ex) {
        g_errorCount.fetch_add(1);
        return -1;
    }
}

void AsparagusIos_StopSensor(int32_t sensorId) {
    @try {
        try {
            std::lock_guard<std::mutex> lock(g_mutex);
            StopSensorLocked(sensorId);
        } catch (...) {
            g_errorCount.fetch_add(1);
        }
    } @catch (__unused NSException* ex) {
        g_errorCount.fetch_add(1);
    }
}

void AsparagusIos_StopAll(void) {
    @try {
        try {
            g_unityPaused.store(false, std::memory_order_relaxed);
            std::lock_guard<std::mutex> lock(g_mutex);
            for (int32_t id = 1; id <= kSensorCount; id++) {
                StopSensorLocked(id);
            }
        } catch (...) {
            g_errorCount.fetch_add(1);
        }
    } @catch (__unused NSException* ex) {
        g_errorCount.fetch_add(1);
    }
}

void AsparagusIos_SetUnityPaused(bool paused, int32_t bufferSeconds) {
    @try {
        try {
            g_pauseBufferSeconds.store(bufferSeconds < 0 ? 0 : bufferSeconds);
            g_unityPaused.store(paused);
        } catch (...) {
            g_errorCount.fetch_add(1);
        }
    } @catch (__unused NSException* ex) {
        g_errorCount.fetch_add(1);
    }
}

int64_t AsparagusIos_GetSensorClockUs(void) {
    @try {
        try {
            NSTimeInterval uptimeSec = [[NSProcessInfo processInfo] systemUptime];
            return static_cast<int64_t>(std::llround(uptimeSec * 1000000.0));
        } catch (...) {
            g_errorCount.fetch_add(1);
            return 0;
        }
    } @catch (__unused NSException* ex) {
        g_errorCount.fetch_add(1);
        return 0;
    }
}

int32_t AsparagusIos_DrainPackedSamples(int64_t* outPackedBuffer, int32_t maxSamples) {
    @try {
        try {
            if (outPackedBuffer == nullptr || maxSamples <= 0) {
                return 0;
            }

            std::lock_guard<std::mutex> lock(g_mutex);
            int32_t drained = 0;
            int32_t outIndex = 0;

            for (int32_t i = 0; i < kSensorCount && drained < maxSamples; i++) {
                SensorRing& ring = g_rings[i];
                int32_t take = ring.count;
                int32_t remaining = maxSamples - drained;
                if (take > remaining) {
                    take = remaining;
                }

                if (take <= 0) {
                    continue;
                }

                uint64_t sensorHeader = static_cast<uint64_t>(static_cast<uint32_t>(ring.sensorId)) << 32;
                for (int32_t k = 0; k < take; k++) {
                    size_t slot = static_cast<size_t>(ring.head);
                    uint32_t xBits = FloatToRawBits(ring.xs[slot]);
                    uint32_t yBits = FloatToRawBits(ring.ys[slot]);
                    uint32_t zBits = FloatToRawBits(ring.zs[slot]);

                    outPackedBuffer[outIndex++] = ring.timestampsUs[slot];
                    outPackedBuffer[outIndex++] = static_cast<int64_t>((static_cast<uint64_t>(xBits) << 32) | static_cast<uint64_t>(yBits));
                    outPackedBuffer[outIndex++] = static_cast<int64_t>(sensorHeader | static_cast<uint64_t>(zBits));

                    ring.head = (ring.head + 1) % ring.capacity;
                    ring.count--;
                }

                drained += take;
            }

            return drained;
        } catch (...) {
            g_errorCount.fetch_add(1);
            return -1;
        }
    } @catch (__unused NSException* ex) {
        g_errorCount.fetch_add(1);
        return -1;
    }
}

bool AsparagusIos_ConsumeOverflow(int32_t sensorId, int64_t* outStartUs, int64_t* outEndUs) {
    @try {
        try {
            int32_t idx = sensorId - 1;
            if (idx < 0 || idx >= kSensorCount) {
                return false;
            }

            std::lock_guard<std::mutex> lock(g_mutex);
            SensorRing& ring = g_rings[idx];
            if (ring.overflowCount <= 0) {
                return false;
            }

            if (outStartUs != nullptr) *outStartUs = ring.overflowStartUs;
            if (outEndUs != nullptr) *outEndUs = ring.overflowEndUs;
            ring.overflowCount = 0;
            ring.overflowStartUs = 0;
            ring.overflowEndUs = 0;
            return true;
        } catch (...) {
            g_errorCount.fetch_add(1);
            return false;
        }
    } @catch (__unused NSException* ex) {
        g_errorCount.fetch_add(1);
        return false;
    }
}

int32_t AsparagusIos_ConsumeErrorCount(void) {
    @try {
        try {
            return g_errorCount.exchange(0);
        } catch (...) {
            return 1;
        }
    } @catch (__unused NSException* ex) {
        return 1;
    }
}

float AsparagusIos_GetScreenBrightness(void) {
    @try {
        try {
            if (![NSThread isMainThread]) {
                return -1.0f;
            }
            CGFloat b = [UIScreen mainScreen].brightness;
            if (std::isnan(b) || b < 0.0) {
                return -1.0f;
            }
            if (b > 1.0) {
                return 1.0f;
            }
            return static_cast<float>(b);
        } catch (...) {
            // W15-04: omit brightness silently when unavailable; never fault motion capture.
            return -1.0f;
        }
    } @catch (__unused NSException* ex) {
        return -1.0f;
    }
}

int32_t AsparagusIos_GetScreenLockedState(void) {
    @try {
        try {
            int32_t cached = g_screenLockedState.load();
            if (cached >= 0) {
                return cached;
            }

            if ([NSThread isMainThread]) {
                int32_t state = [UIApplication sharedApplication].isProtectedDataAvailable ? 0 : 1;
                g_screenLockedState.store(state);
                return state;
            }

            return -1;
        } catch (...) {
            // W15-04: omit screen lock state silently when unavailable; never fault motion capture.
            return -1;
        }
    } @catch (__unused NSException* ex) {
        return -1;
    }
}

} // extern "C"
