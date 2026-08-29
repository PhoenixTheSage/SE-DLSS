#pragma once

// Minimal NGX surface used by SeDlssNgx. Recreated from the public NVIDIA DLSS SDK
// headers so this wrapper can compile without vendoring NVIDIA source.

#include <stdint.h>

#ifndef NVSDK_CONV
#define NVSDK_CONV __cdecl
#endif

#define NVSDK_NGX_VERSION_API_MACRO 0x0000015

typedef enum NVSDK_NGX_Version { NVSDK_NGX_Version_API = NVSDK_NGX_VERSION_API_MACRO } NVSDK_NGX_Version;
typedef int NVSDK_NGX_Result;
#define NVSDK_NGX_Result_Success ((NVSDK_NGX_Result)0x1)
#define NVSDK_NGX_Result_Fail ((NVSDK_NGX_Result)0xBAD00000)
#define NVSDK_NGX_FAILED(r) (((uint32_t)(r) & 0xFFF00000) == (uint32_t)NVSDK_NGX_Result_Fail)
typedef enum NVSDK_NGX_Feature { NVSDK_NGX_Feature_SuperSampling = 1 } NVSDK_NGX_Feature;
typedef enum NVSDK_NGX_EngineType { NVSDK_NGX_ENGINE_TYPE_CUSTOM = 0 } NVSDK_NGX_EngineType;
typedef enum NVSDK_NGX_PerfQuality_Value
{
    NVSDK_NGX_PerfQuality_Value_MaxPerf = 0,
    NVSDK_NGX_PerfQuality_Value_Balanced = 1,
    NVSDK_NGX_PerfQuality_Value_MaxQuality = 2,
    NVSDK_NGX_PerfQuality_Value_UltraPerformance = 3,
    NVSDK_NGX_PerfQuality_Value_UltraQuality = 4,
    NVSDK_NGX_PerfQuality_Value_DLAA = 5
} NVSDK_NGX_PerfQuality_Value;

typedef enum NVSDK_NGX_DLSS_Feature_Flags
{
    NVSDK_NGX_DLSS_Feature_Flags_None = 0,
    NVSDK_NGX_DLSS_Feature_Flags_IsHDR = 1 << 0,
    NVSDK_NGX_DLSS_Feature_Flags_MVLowRes = 1 << 1,
    NVSDK_NGX_DLSS_Feature_Flags_MVJittered = 1 << 2,
    NVSDK_NGX_DLSS_Feature_Flags_DepthInverted = 1 << 3,
    NVSDK_NGX_DLSS_Feature_Flags_AutoExposure = 1 << 6
} NVSDK_NGX_DLSS_Feature_Flags;

typedef enum NVSDK_NGX_DLSS_Hint_Render_Preset
{
    NVSDK_NGX_DLSS_Hint_Render_Preset_Default = 0,
    NVSDK_NGX_DLSS_Hint_Render_Preset_J = 10,
    NVSDK_NGX_DLSS_Hint_Render_Preset_K = 11,
    NVSDK_NGX_DLSS_Hint_Render_Preset_L = 12,
    NVSDK_NGX_DLSS_Hint_Render_Preset_M = 13
} NVSDK_NGX_DLSS_Hint_Render_Preset;

typedef enum NVSDK_NGX_Logging_Level
{
    NVSDK_NGX_LOGGING_LEVEL_OFF = 0,
    NVSDK_NGX_LOGGING_LEVEL_ON,
    NVSDK_NGX_LOGGING_LEVEL_VERBOSE
} NVSDK_NGX_Logging_Level;

struct ID3D11Device;
struct ID3D11DeviceContext;
struct ID3D11Resource;
struct ID3D12Resource;

typedef struct NVSDK_NGX_Handle NVSDK_NGX_Handle;
typedef struct NVSDK_NGX_FeatureCommonInfo_Internal NVSDK_NGX_FeatureCommonInfo_Internal;

#ifdef __cplusplus
struct NVSDK_NGX_Parameter
{
    virtual void Set(const char* InName, unsigned long long InValue) = 0;
    virtual void Set(const char* InName, float InValue) = 0;
    virtual void Set(const char* InName, double InValue) = 0;
    virtual void Set(const char* InName, unsigned int InValue) = 0;
    virtual void Set(const char* InName, int InValue) = 0;
    virtual void Set(const char* InName, ID3D11Resource* InValue) = 0;
    virtual void Set(const char* InName, ID3D12Resource* InValue) = 0;
    virtual void Set(const char* InName, void* InValue) = 0;

    virtual NVSDK_NGX_Result Get(const char* InName, unsigned long long* OutValue) const = 0;
    virtual NVSDK_NGX_Result Get(const char* InName, float* OutValue) const = 0;
    virtual NVSDK_NGX_Result Get(const char* InName, double* OutValue) const = 0;
    virtual NVSDK_NGX_Result Get(const char* InName, unsigned int* OutValue) const = 0;
    virtual NVSDK_NGX_Result Get(const char* InName, int* OutValue) const = 0;
    virtual NVSDK_NGX_Result Get(const char* InName, ID3D11Resource** OutValue) const = 0;
    virtual NVSDK_NGX_Result Get(const char* InName, ID3D12Resource** OutValue) const = 0;
    virtual NVSDK_NGX_Result Get(const char* InName, void** OutValue) const = 0;

    virtual void Reset() = 0;
};
#else
typedef struct NVSDK_NGX_Parameter NVSDK_NGX_Parameter;
#endif

typedef struct NVSDK_NGX_PathListInfo
{
    wchar_t const* const* Path;
    unsigned int Length;
} NVSDK_NGX_PathListInfo;

typedef void(NVSDK_CONV* NVSDK_NGX_AppLogCallback)(const char* message, NVSDK_NGX_Logging_Level loggingLevel, NVSDK_NGX_Feature sourceComponent);

typedef struct NVSDK_NGX_LoggingInfo
{
    NVSDK_NGX_AppLogCallback LoggingCallback;
    NVSDK_NGX_Logging_Level MinimumLoggingLevel;
    bool DisableOtherLoggingSinks;
} NVSDK_NGX_LoggingInfo;

typedef struct NVSDK_NGX_FeatureCommonInfo
{
    NVSDK_NGX_PathListInfo PathListInfo;
    NVSDK_NGX_FeatureCommonInfo_Internal* InternalData;
    NVSDK_NGX_LoggingInfo LoggingInfo;
} NVSDK_NGX_FeatureCommonInfo;

typedef NVSDK_NGX_Result(NVSDK_CONV* PFN_NVSDK_NGX_DLSS_GetOptimalSettingsCallback)(NVSDK_NGX_Parameter* InParams);
typedef void(NVSDK_CONV* PFN_NVSDK_NGX_ProgressCallback_C)(float InCurrentProgress, bool* OutShouldCancel);

#define NVSDK_NGX_Parameter_Width "Width"
#define NVSDK_NGX_Parameter_Height "Height"
#define NVSDK_NGX_Parameter_OutWidth "OutWidth"
#define NVSDK_NGX_Parameter_OutHeight "OutHeight"
#define NVSDK_NGX_Parameter_Sharpness "Sharpness"
#define NVSDK_NGX_Parameter_Reset "Reset"
#define NVSDK_NGX_Parameter_Color "Color"
#define NVSDK_NGX_Parameter_Output "Output"
#define NVSDK_NGX_Parameter_Depth "Depth"
#define NVSDK_NGX_Parameter_MotionVectors "MotionVectors"
#define NVSDK_NGX_Parameter_Jitter_Offset_X "Jitter.Offset.X"
#define NVSDK_NGX_Parameter_Jitter_Offset_Y "Jitter.Offset.Y"
#define NVSDK_NGX_Parameter_MV_Scale_X "MV.Scale.X"
#define NVSDK_NGX_Parameter_MV_Scale_Y "MV.Scale.Y"
#define NVSDK_NGX_Parameter_PerfQualityValue "PerfQualityValue"
#define NVSDK_NGX_Parameter_RTXValue "RTXValue"
#define NVSDK_NGX_Parameter_DLSSOptimalSettingsCallback "DLSSOptimalSettingsCallback"
#define NVSDK_NGX_Parameter_DLSS_Feature_Create_Flags "DLSS.Feature.Create.Flags"
#define NVSDK_NGX_Parameter_DLSS_Enable_Output_Subrects "DLSS.Enable.Output.Subrects"
#define NVSDK_NGX_Parameter_DLSS_Get_Dynamic_Max_Render_Width "DLSS.Get.Dynamic.Max.Render.Width"
#define NVSDK_NGX_Parameter_DLSS_Get_Dynamic_Max_Render_Height "DLSS.Get.Dynamic.Max.Render.Height"
#define NVSDK_NGX_Parameter_DLSS_Get_Dynamic_Min_Render_Width "DLSS.Get.Dynamic.Min.Render.Width"
#define NVSDK_NGX_Parameter_DLSS_Get_Dynamic_Min_Render_Height "DLSS.Get.Dynamic.Min.Render.Height"
#define NVSDK_NGX_Parameter_DLSS_Render_Subrect_Dimensions_Width "DLSS.Render.Subrect.Dimensions.Width"
#define NVSDK_NGX_Parameter_DLSS_Render_Subrect_Dimensions_Height "DLSS.Render.Subrect.Dimensions.Height"
#define NVSDK_NGX_Parameter_SuperSampling_Available "SuperSampling.Available"
#define NVSDK_NGX_Parameter_SuperSampling_FeatureInitResult "SuperSampling.FeatureInitResult"
#define NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA "DLSS.Hint.Render.Preset.DLAA"
#define NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality "DLSS.Hint.Render.Preset.Quality"
#define NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Balanced "DLSS.Hint.Render.Preset.Balanced"
#define NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Performance "DLSS.Hint.Render.Preset.Performance"
#define NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraPerformance "DLSS.Hint.Render.Preset.UltraPerformance"
#define NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraQuality "DLSS.Hint.Render.Preset.UltraQuality"
