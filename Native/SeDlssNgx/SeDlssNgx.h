#pragma once

#include <stdint.h>

#ifdef SEDLSS_EXPORTS
#define SEDLSS_API __declspec(dllexport)
#else
#define SEDLSS_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum SeDlssQuality
{
    SeDlssQuality_MaxPerf = 0,
    SeDlssQuality_Balanced = 1,
    SeDlssQuality_MaxQuality = 2,
    SeDlssQuality_UltraPerformance = 3,
    SeDlssQuality_UltraQuality = 4,
    SeDlssQuality_DLAA = 5
};

typedef struct SeDlssInitArgs
{
    void* Device;
    const wchar_t* DllSearchPath;
    const wchar_t* LogPath;
    const wchar_t* DebugLogPath;
} SeDlssInitArgs;

typedef struct SeDlssEvalArgs
{
    void* DeviceContext;
    void* Color;
    void* Depth;
    void* MotionVectors;
    void* Output;
    void* Exposure;
    float JitterX;
    float JitterY;
    float MvScaleX;
    float MvScaleY;
    int Reset;
    float Sharpness;
    uint32_t RenderWidth;
    uint32_t RenderHeight;
} SeDlssEvalArgs;

typedef struct SeDlssMvArgs
{
    void* Device;
    void* DeviceContext;
    void* Depth;
    uint32_t Width;
    uint32_t Height;
    float InvViewProj[16];
    float UnjitteredViewProj[16];
    float PrevViewProj[16];
} SeDlssMvArgs;

SEDLSS_API int SeDlss_Init(const SeDlssInitArgs* args);
SEDLSS_API int SeDlss_IsSupported(void);
SEDLSS_API int SeDlss_SetMode(int quality, uint32_t outWidth, uint32_t outHeight,
    uint32_t* outRenderWidth, uint32_t* outRenderHeight, float* outSharpness, int preset);
SEDLSS_API int SeDlss_Evaluate(const SeDlssEvalArgs* args);
SEDLSS_API void* SeDlss_GenerateCameraMotionVectors(const SeDlssMvArgs* args);
SEDLSS_API int SeDlss_UpsampleDepth(void* device, void* context, void* srcDepth, void* destDepth);
SEDLSS_API void SeDlss_Shutdown(void);
SEDLSS_API const char* SeDlss_LastError(void);

#ifdef __cplusplus
}
#endif
