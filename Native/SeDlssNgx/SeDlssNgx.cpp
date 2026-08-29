#include "SeDlssNgx.h"
#include "ngx_min.h"

#include <d3d11.h>
#include <d3dcompiler.h>
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d3dcompiler.lib")

namespace
{
std::mutex g_mutex;
std::string g_lastError = "not initialized";
HMODULE g_ngx;
ID3D11Device* g_device;
NVSDK_NGX_Parameter* g_capabilityParams;
NVSDK_NGX_Parameter* g_evalParams;
NVSDK_NGX_Handle* g_dlss;
bool g_initialized;
bool g_supported;
int g_quality = -1;
int g_preset = -1;
uint32_t g_outW;
uint32_t g_outH;
std::wstring g_searchPath;
std::vector<std::wstring> g_ngxPaths;
std::vector<const wchar_t*> g_ngxPathPtrs;

ID3D11Texture2D* g_mvTex;
ID3D11RenderTargetView* g_mvRtv;
ID3D11ShaderResourceView* g_mvSrv;
ID3D11VertexShader* g_mvVs;
ID3D11PixelShader* g_mvPs;
ID3D11Buffer* g_mvCb;
ID3D11SamplerState* g_mvSampler;
uint32_t g_mvW;
uint32_t g_mvH;

using PFN_InitProject = NVSDK_NGX_Result(NVSDK_CONV*)(const char*, NVSDK_NGX_EngineType, const char*, const wchar_t*, ID3D11Device*, NVSDK_NGX_Version, const NVSDK_NGX_FeatureCommonInfo*);
using PFN_InitProjectSdk = NVSDK_NGX_Result(NVSDK_CONV*)(const char*, NVSDK_NGX_EngineType, const char*, const wchar_t*, ID3D11Device*, const NVSDK_NGX_FeatureCommonInfo*, NVSDK_NGX_Version);
using PFN_InitApp = NVSDK_NGX_Result(NVSDK_CONV*)(unsigned long long, const wchar_t*, ID3D11Device*, const NVSDK_NGX_FeatureCommonInfo*, NVSDK_NGX_Version);
using PFN_Shutdown1 = NVSDK_NGX_Result(NVSDK_CONV*)(ID3D11Device*);
using PFN_GetCaps = NVSDK_NGX_Result(NVSDK_CONV*)(NVSDK_NGX_Parameter**);
using PFN_AllocParams = NVSDK_NGX_Result(NVSDK_CONV*)(NVSDK_NGX_Parameter**);
using PFN_DestroyParams = NVSDK_NGX_Result(NVSDK_CONV*)(NVSDK_NGX_Parameter*);
using PFN_CreateFeature = NVSDK_NGX_Result(NVSDK_CONV*)(ID3D11DeviceContext*, NVSDK_NGX_Feature, NVSDK_NGX_Parameter*, NVSDK_NGX_Handle**);
using PFN_ReleaseFeature = NVSDK_NGX_Result(NVSDK_CONV*)(NVSDK_NGX_Handle*);
using PFN_Evaluate = NVSDK_NGX_Result(NVSDK_CONV*)(ID3D11DeviceContext*, const NVSDK_NGX_Handle*, const NVSDK_NGX_Parameter*, void*);

PFN_InitProject pInitProject;
PFN_InitProjectSdk pInitProjectSdk;
PFN_InitApp pInitApp;
PFN_Shutdown1 pShutdown1;
PFN_GetCaps pGetCaps;
PFN_AllocParams pAllocParams;
PFN_DestroyParams pDestroyParams;
PFN_CreateFeature pCreateFeature;
PFN_ReleaseFeature pReleaseFeature;
PFN_Evaluate pEvaluate;

void SetError(const char* text)
{
    g_lastError = text ? text : "unknown error";
}

template<typename T>
T LoadFn(const char* name)
{
    auto fn = reinterpret_cast<T>(GetProcAddress(g_ngx, name));
    return fn;
}

template<typename T>
T LoadFn2(const char* primary, const char* secondary)
{
    if (auto fn = LoadFn<T>(primary))
        return fn;
    return LoadFn<T>(secondary);
}

const char kMvShader[] = R"(
#pragma pack_matrix(row_major)
cbuffer Constants : register(b0)
{
    float4x4 InvViewProj;
    float4x4 UnjitteredViewProj;
    float4x4 PrevViewProj;
    float2 RenderSize;
    float2 InvRenderSize;
};
Texture2D DepthTex : register(t0);
SamplerState PointSamp : register(s0);

static const float2 kMvDilateOff[8] =
{
    float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1),
    float2(1, 1), float2(-1, 1), float2(1, -1), float2(-1, -1)
};

float2 CameraVelocity(float2 uv, float depth)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 clip = float4(ndc, depth, 1.0);
    float4 world = mul(clip, InvViewProj);
    world /= max(world.w, 1e-6);
    float4 currClip = mul(world, UnjitteredViewProj);
    currClip /= max(currClip.w, 1e-6);
    float4 prevClip = mul(world, PrevViewProj);
    prevClip /= max(prevClip.w, 1e-6);
    float2 currUv = float2(currClip.x * 0.5 + 0.5, 0.5 - currClip.y * 0.5);
    float2 prevUv = float2(prevClip.x * 0.5 + 0.5, 0.5 - prevClip.y * 0.5);
    return (currUv - prevUv) * RenderSize;
}

void VSMain(uint id : SV_VertexID, out float4 pos : SV_Position, out float2 uv : TEXCOORD0)
{
    uv = float2((id << 1) & 2, id & 2);
    pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 PSMain(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float depth = DepthTex.SampleLevel(PointSamp, uv, 0).r;
    float2 velocity = CameraVelocity(uv, depth);
    float closest = depth;
    float2 dilated = velocity;
    [unroll]
    for (int i = 0; i < 8; i++)
    {
        float2 nuv = uv + kMvDilateOff[i] * InvRenderSize;
        float nd = DepthTex.SampleLevel(PointSamp, nuv, 0).r;
        if (nd > closest)
        {
            closest = nd;
            dilated = CameraVelocity(nuv, nd);
        }
    }
    return float4(dilated, 0, 1);
}
)";

void ReleaseMvPipeline()
{
    if (g_mvRtv) { g_mvRtv->Release(); g_mvRtv = nullptr; }
    if (g_mvSrv) { g_mvSrv->Release(); g_mvSrv = nullptr; }
    if (g_mvTex) { g_mvTex->Release(); g_mvTex = nullptr; }
    g_mvW = g_mvH = 0;
}

void ReleaseMvShaders()
{
    if (g_mvVs) { g_mvVs->Release(); g_mvVs = nullptr; }
    if (g_mvPs) { g_mvPs->Release(); g_mvPs = nullptr; }
    if (g_mvCb) { g_mvCb->Release(); g_mvCb = nullptr; }
    if (g_mvSampler) { g_mvSampler->Release(); g_mvSampler = nullptr; }
}

bool EnsureMvShaders(ID3D11Device* device)
{
    if (g_mvVs && g_mvPs && g_mvCb && g_mvSampler)
        return true;

    ID3DBlob* vsBlob = nullptr;
    ID3DBlob* psBlob = nullptr;
    ID3DBlob* err = nullptr;
    HRESULT hr = D3DCompile(kMvShader, sizeof(kMvShader) - 1, "SeDlssMv", nullptr, nullptr, "VSMain", "vs_5_0", 0, 0, &vsBlob, &err);
    if (FAILED(hr))
    {
        SetError("failed to compile motion-vector VS");
        if (err) err->Release();
        return false;
    }
    if (err) { err->Release(); err = nullptr; }
    hr = D3DCompile(kMvShader, sizeof(kMvShader) - 1, "SeDlssMv", nullptr, nullptr, "PSMain", "ps_5_0", 0, 0, &psBlob, &err);
    if (FAILED(hr))
    {
        SetError("failed to compile motion-vector PS");
        vsBlob->Release();
        if (err) err->Release();
        return false;
    }
    if (err) err->Release();

    hr = device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &g_mvVs);
    vsBlob->Release();
    if (FAILED(hr))
    {
        psBlob->Release();
        SetError("failed to create motion-vector VS");
        return false;
    }
    hr = device->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, &g_mvPs);
    psBlob->Release();
    if (FAILED(hr))
    {
        SetError("failed to create motion-vector PS");
        return false;
    }

    D3D11_BUFFER_DESC cb{};
    cb.ByteWidth = 208;
    cb.Usage = D3D11_USAGE_DYNAMIC;
    cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    cb.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    hr = device->CreateBuffer(&cb, nullptr, &g_mvCb);
    if (FAILED(hr))
    {
        SetError("failed to create motion-vector constant buffer");
        return false;
    }

    D3D11_SAMPLER_DESC samp{};
    samp.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
    samp.AddressU = samp.AddressV = samp.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    hr = device->CreateSamplerState(&samp, &g_mvSampler);
    if (FAILED(hr))
    {
        SetError("failed to create motion-vector sampler");
        return false;
    }
    return true;
}

bool EnsureMvTarget(ID3D11Device* device, uint32_t width, uint32_t height)
{
    if (g_mvTex && g_mvW == width && g_mvH == height)
        return true;
    ReleaseMvPipeline();

    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R16G16_FLOAT;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    HRESULT hr = device->CreateTexture2D(&desc, nullptr, &g_mvTex);
    if (FAILED(hr))
    {
        SetError("failed to create motion-vector texture");
        return false;
    }
    hr = device->CreateRenderTargetView(g_mvTex, nullptr, &g_mvRtv);
    if (FAILED(hr))
    {
        SetError("failed to create motion-vector RTV");
        return false;
    }
    hr = device->CreateShaderResourceView(g_mvTex, nullptr, &g_mvSrv);
    if (FAILED(hr))
    {
        SetError("failed to create motion-vector SRV");
        return false;
    }
    g_mvW = width;
    g_mvH = height;
    return true;
}

void DescribeTexture(ID3D11Resource* res, char* buf, size_t n)
{
    if (!res)
    {
        strcpy_s(buf, n, "null");
        return;
    }
    ID3D11Texture2D* tex = nullptr;
    if (FAILED(res->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&tex))) || !tex)
    {
        strcpy_s(buf, n, "not-tex2d");
        return;
    }
    D3D11_TEXTURE2D_DESC desc{};
    tex->GetDesc(&desc);
    tex->Release();
    sprintf_s(buf, n, "%ux%u fmt=%u bind=0x%x", desc.Width, desc.Height, (unsigned)desc.Format, desc.BindFlags);
}

void UnbindPipeline(ID3D11DeviceContext* ctx)
{
    ID3D11RenderTargetView* rtvs[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT]{};
    ctx->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtvs, nullptr);
    ID3D11ShaderResourceView* srvs[8]{};
    ctx->PSSetShaderResources(0, 8, srvs);
    ctx->VSSetShaderResources(0, 8, srvs);
    ctx->CSSetShaderResources(0, 8, srvs);
    ID3D11UnorderedAccessView* uavs[8]{};
    UINT initial[8];
    for (int i = 0; i < 8; ++i)
        initial[i] = 0xFFFFFFFFu;
    ctx->CSSetUnorderedAccessViews(0, 8, uavs, initial);
}

void ReleaseDlss()
{
    if (g_dlss && pReleaseFeature)
    {
        pReleaseFeature(g_dlss);
        g_dlss = nullptr;
    }
    g_quality = -1;
    g_preset = -1;
    g_outW = g_outH = 0;
}

std::wstring JoinPath(const std::wstring& dir, const wchar_t* file)
{
    if (dir.empty())
        return file ? file : L"";
    if (dir.back() == L'\\' || dir.back() == L'/')
        return dir + file;
    return dir + L'\\' + file;
}

std::string Narrow(const std::wstring& wide)
{
    if (wide.empty())
        return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), (int)wide.size(), nullptr, 0, nullptr, nullptr);
    if (n <= 0)
        return {};
    std::string out((size_t)n, 0);
    WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), (int)wide.size(), &out[0], n, nullptr, nullptr);
    return out;
}

std::wstring ReadNgxCoreDir()
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\NVIDIA Corporation\\Global\\NGXCore", 0,
            KEY_READ | KEY_WOW64_64KEY, &key) != ERROR_SUCCESS)
        return {};
    wchar_t buf[MAX_PATH]{};
    DWORD size = sizeof(buf);
    DWORD type = 0;
    LONG st = RegQueryValueExW(key, L"FullPath", nullptr, &type, reinterpret_cast<LPBYTE>(buf), &size);
    RegCloseKey(key);
    if (st != ERROR_SUCCESS || (type != REG_SZ && type != REG_EXPAND_SZ) || buf[0] == 0)
        return {};
    return buf;
}

void AddDriverStoreCandidates(std::vector<std::wstring>& candidates)
{
    wchar_t sys[MAX_PATH]{};
    if (!GetSystemDirectoryW(sys, MAX_PATH))
        return;
    std::wstring repo = JoinPath(sys, L"DriverStore\\FileRepository");
    WIN32_FIND_DATAW fd{};
    HANDLE find = FindFirstFileW(JoinPath(repo, L"nv*").c_str(), &fd);
    if (find == INVALID_HANDLE_VALUE)
        return;
    do
    {
        if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
            continue;
        candidates.push_back(JoinPath(JoinPath(repo, fd.cFileName), L"_nvngx.dll"));
    } while (FindNextFileW(find, &fd));
    FindClose(find);
}

HMODULE TryLoadNgx(const std::wstring& path)
{
    if (path.empty())
        return nullptr;
    DWORD attrib = GetFileAttributesW(path.c_str());
    if (attrib == INVALID_FILE_ATTRIBUTES)
        return nullptr;
    HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!module)
        module = LoadLibraryW(path.c_str());
    return module;
}

HMODULE LoadNgxModule(std::wstring& loadedFrom, DWORD& lastError)
{
    lastError = 0;
    loadedFrom.clear();
    std::vector<std::wstring> candidates;

    wchar_t sys[MAX_PATH]{};
    if (GetSystemDirectoryW(sys, MAX_PATH))
    {
        candidates.push_back(JoinPath(sys, L"_nvngx.dll"));
        candidates.push_back(JoinPath(sys, L"nvngx.dll"));
    }

    std::wstring ngxCore = ReadNgxCoreDir();
    if (!ngxCore.empty())
    {
        candidates.push_back(JoinPath(ngxCore, L"_nvngx.dll"));
        candidates.push_back(JoinPath(ngxCore, L"nvngx.dll"));
    }

    AddDriverStoreCandidates(candidates);
    candidates.push_back(L"_nvngx.dll");
    candidates.push_back(L"nvngx.dll");

    DWORD err = 0;
    for (const auto& path : candidates)
    {
        HMODULE module = TryLoadNgx(path);
        if (module)
        {
            loadedFrom = path;
            return module;
        }
        err = GetLastError();
        if (err != 0)
            lastError = err;
    }
    return nullptr;
}

void BuildFeatureSearchPaths(const wchar_t* pluginPath)
{
    g_ngxPaths.clear();
    g_ngxPathPtrs.clear();
    auto addUnique = [](const std::wstring& path)
    {
        if (path.empty())
            return;
        for (const auto& existing : g_ngxPaths)
        {
            if (_wcsicmp(existing.c_str(), path.c_str()) == 0)
                return;
        }
        g_ngxPaths.push_back(path);
    };

    if (pluginPath && pluginPath[0])
        addUnique(pluginPath);
    addUnique(ReadNgxCoreDir());
    for (auto& path : g_ngxPaths)
        g_ngxPathPtrs.push_back(path.c_str());
}

void ApplyHintPresets(NVSDK_NGX_Parameter* params, int preset)
{
    unsigned int k = (unsigned int)NVSDK_NGX_DLSS_Hint_Render_Preset_K;
    unsigned int l = (unsigned int)NVSDK_NGX_DLSS_Hint_Render_Preset_L;
    unsigned int m = (unsigned int)NVSDK_NGX_DLSS_Hint_Render_Preset_M;
    unsigned int dlaa = k, quality = k, balanced = k, perf = m, ultra = l, ultraQ = k;
    if (preset == (int)NVSDK_NGX_DLSS_Hint_Render_Preset_J ||
        preset == (int)NVSDK_NGX_DLSS_Hint_Render_Preset_K ||
        preset == (int)NVSDK_NGX_DLSS_Hint_Render_Preset_L ||
        preset == (int)NVSDK_NGX_DLSS_Hint_Render_Preset_M)
    {
        unsigned int forced = (unsigned int)preset;
        dlaa = quality = balanced = perf = ultra = ultraQ = forced;
    }
    params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA, dlaa);
    params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality, quality);
    params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Balanced, balanced);
    params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Performance, perf);
    params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraPerformance, ultra);
    params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraQuality, ultraQ);
}
}

extern "C" int SeDlss_Init(const SeDlssInitArgs* args)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_initialized)
        return 1;
    if (!args || !args->Device)
    {
        SetError("Init requires a D3D11 device");
        return 0;
    }

    if (args->DllSearchPath && args->DllSearchPath[0])
        g_searchPath = args->DllSearchPath;

    // Current NVIDIA drivers keep _nvngx.dll in the DriverStore, not System32.
    // Load it by absolute path so a process-wide SetDllDirectory cannot hide it.
    std::wstring loadedFrom;
    DWORD loadError = 0;
    g_ngx = LoadNgxModule(loadedFrom, loadError);
    if (!g_ngx)
    {
        std::string ngxCore = Narrow(ReadNgxCoreDir());
        char buf[512];
        sprintf_s(buf, "failed to load _nvngx.dll (Win32 %lu). NGXCore=%s",
            loadError, ngxCore.empty() ? "(registry missing)" : ngxCore.c_str());
        SetError(buf);
        return 0;
    }

    // Driver _nvngx.dll exports Init_ProjectID / EvaluateFeature.
    // The NGX SDK static lib uses Init_with_ProjectID / EvaluateFeature_C.
    pInitProject = LoadFn<PFN_InitProject>("NVSDK_NGX_D3D11_Init_ProjectID");
    pInitProjectSdk = LoadFn<PFN_InitProjectSdk>("NVSDK_NGX_D3D11_Init_with_ProjectID");
    pInitApp = LoadFn<PFN_InitApp>("NVSDK_NGX_D3D11_Init_Ext");
    if (!pInitApp)
        pInitApp = LoadFn<PFN_InitApp>("NVSDK_NGX_D3D11_Init");
    pShutdown1 = LoadFn<PFN_Shutdown1>("NVSDK_NGX_D3D11_Shutdown1");
    pGetCaps = LoadFn<PFN_GetCaps>("NVSDK_NGX_D3D11_GetCapabilityParameters");
    pAllocParams = LoadFn<PFN_AllocParams>("NVSDK_NGX_D3D11_AllocateParameters");
    pDestroyParams = LoadFn<PFN_DestroyParams>("NVSDK_NGX_D3D11_DestroyParameters");
    pCreateFeature = LoadFn<PFN_CreateFeature>("NVSDK_NGX_D3D11_CreateFeature");
    pReleaseFeature = LoadFn<PFN_ReleaseFeature>("NVSDK_NGX_D3D11_ReleaseFeature");
    pEvaluate = LoadFn2<PFN_Evaluate>("NVSDK_NGX_D3D11_EvaluateFeature", "NVSDK_NGX_D3D11_EvaluateFeature_C");
    if ((!pInitProject && !pInitProjectSdk && !pInitApp) || !pShutdown1 || !pGetCaps || !pCreateFeature || !pReleaseFeature || !pEvaluate)
    {
        char buf[384];
        sprintf_s(buf, "NGX driver exports are missing (init=%d/%d/%d shut=%d caps=%d create=%d release=%d eval=%d) from %s",
            pInitProject ? 1 : 0, pInitProjectSdk ? 1 : 0, pInitApp ? 1 : 0,
            pShutdown1 ? 1 : 0, pGetCaps ? 1 : 0, pCreateFeature ? 1 : 0,
            pReleaseFeature ? 1 : 0, pEvaluate ? 1 : 0,
            Narrow(loadedFrom).c_str());
        SetError(buf);
        return 0;
    }

    NVSDK_NGX_FeatureCommonInfo info{};
    BuildFeatureSearchPaths(g_searchPath.empty() ? nullptr : g_searchPath.c_str());
    if (!g_ngxPathPtrs.empty())
    {
        info.PathListInfo.Path = g_ngxPathPtrs.data();
        info.PathListInfo.Length = (unsigned int)g_ngxPathPtrs.size();
    }

    g_device = static_cast<ID3D11Device*>(args->Device);
    const wchar_t* logPath = args->LogPath && args->LogPath[0] ? args->LogPath : L".";
    // Driver export uses (version, featureInfo). SDK wrapper uses (featureInfo, version).
    const char* projectId = "8e4c2a71-6b9d-4f13-9c1a-7f2e5b90d4c3";
    NVSDK_NGX_Result result = NVSDK_NGX_Result_Fail;
    if (pInitProject)
        result = pInitProject(projectId, NVSDK_NGX_ENGINE_TYPE_CUSTOM, "1.0.0", logPath, g_device, NVSDK_NGX_Version_API, &info);
    else if (pInitProjectSdk)
        result = pInitProjectSdk(projectId, NVSDK_NGX_ENGINE_TYPE_CUSTOM, "1.0.0", logPath, g_device, &info, NVSDK_NGX_Version_API);
    else
        result = pInitApp(0x244850, logPath, g_device, &info, NVSDK_NGX_Version_API);
    if (NVSDK_NGX_FAILED(result))
    {
        char buf[96];
        sprintf_s(buf, "NVSDK_NGX_D3D11_Init_with_ProjectID failed (0x%08X)", (unsigned)result);
        SetError(buf);
        return 0;
    }

    result = pGetCaps(&g_capabilityParams);
    if (NVSDK_NGX_FAILED(result) || !g_capabilityParams)
    {
        SetError("GetCapabilityParameters failed");
        return 0;
    }
    if (pAllocParams)
        pAllocParams(&g_evalParams);

    int available = 0;
    g_capabilityParams->Get(NVSDK_NGX_Parameter_SuperSampling_Available, &available);
    g_supported = available != 0;
    g_initialized = true;
    if (g_supported)
    {
        std::string msg = "initialized from " + Narrow(loadedFrom);
        SetError(msg.c_str());
    }
    else
        SetError("NGX initialized but Super Sampling is not available");
    return 1;
}

extern "C" int SeDlss_IsSupported(void)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return g_supported ? 1 : 0;
}

extern "C" int SeDlss_SetMode(int quality, uint32_t outWidth, uint32_t outHeight,
    uint32_t* outRenderWidth, uint32_t* outRenderHeight, float* outSharpness, int preset)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_initialized || !g_supported || !g_capabilityParams || !g_device)
    {
        SetError("NGX is not initialized");
        return 0;
    }
    if (outWidth == 0 || outHeight == 0)
    {
        SetError("invalid output size");
        return 0;
    }

    NVSDK_NGX_Parameter* params = g_capabilityParams;
    params->Set(NVSDK_NGX_Parameter_Width, outWidth);
    params->Set(NVSDK_NGX_Parameter_Height, outHeight);
    params->Set(NVSDK_NGX_Parameter_PerfQualityValue, quality);
    params->Set(NVSDK_NGX_Parameter_RTXValue, 0);
    ApplyHintPresets(params, preset);

    uint32_t renderW = outWidth;
    uint32_t renderH = outHeight;
    float sharpness = 0.0f;
    void* callback = nullptr;
    params->Get(NVSDK_NGX_Parameter_DLSSOptimalSettingsCallback, &callback);
    if (callback)
    {
        auto pfn = reinterpret_cast<PFN_NVSDK_NGX_DLSS_GetOptimalSettingsCallback>(callback);
        NVSDK_NGX_Result opt = pfn(params);
        if (!NVSDK_NGX_FAILED(opt))
        {
            params->Get(NVSDK_NGX_Parameter_OutWidth, &renderW);
            params->Get(NVSDK_NGX_Parameter_OutHeight, &renderH);
            params->Get(NVSDK_NGX_Parameter_Sharpness, &sharpness);
        }
    }

    if (renderW == 0 || renderH == 0)
    {
        SetError("DLSS optimal settings returned a zero size");
        return 0;
    }

    if (g_dlss && g_quality == quality && g_preset == preset && g_outW == outWidth && g_outH == outHeight)
    {
        if (outRenderWidth) *outRenderWidth = renderW;
        if (outRenderHeight) *outRenderHeight = renderH;
        if (outSharpness) *outSharpness = sharpness;
        return 1;
    }

    ReleaseDlss();
    params->Set(NVSDK_NGX_Parameter_Width, renderW);
    params->Set(NVSDK_NGX_Parameter_Height, renderH);
    params->Set(NVSDK_NGX_Parameter_OutWidth, outWidth);
    params->Set(NVSDK_NGX_Parameter_OutHeight, outHeight);
    params->Set(NVSDK_NGX_Parameter_PerfQualityValue, quality);
    ApplyHintPresets(params, preset);
    int flags = NVSDK_NGX_DLSS_Feature_Flags_IsHDR | NVSDK_NGX_DLSS_Feature_Flags_DepthInverted | NVSDK_NGX_DLSS_Feature_Flags_AutoExposure | NVSDK_NGX_DLSS_Feature_Flags_MVLowRes;
    params->Set(NVSDK_NGX_Parameter_DLSS_Feature_Create_Flags, flags);
    params->Set(NVSDK_NGX_Parameter_DLSS_Enable_Output_Subrects, 0);

    ID3D11DeviceContext* ctx = nullptr;
    g_device->GetImmediateContext(&ctx);
    if (!ctx)
    {
        SetError("failed to get immediate context");
        return 0;
    }
    NVSDK_NGX_Result result = pCreateFeature(ctx, NVSDK_NGX_Feature_SuperSampling, params, &g_dlss);
    ctx->Release();
    if (NVSDK_NGX_FAILED(result) || !g_dlss)
    {
        char buf[96];
        sprintf_s(buf, "CreateFeature SuperSampling failed (0x%08X)", (unsigned)result);
        SetError(buf);
        return 0;
    }

    g_quality = quality;
    g_preset = preset;
    g_outW = outWidth;
    g_outH = outHeight;
    if (outRenderWidth) *outRenderWidth = renderW;
    if (outRenderHeight) *outRenderHeight = renderH;
    if (outSharpness) *outSharpness = sharpness;
    SetError("DLSS feature created");
    return 1;
}

extern "C" int SeDlss_Evaluate(const SeDlssEvalArgs* args)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_initialized || !g_dlss || !args || !args->DeviceContext || !args->Color || !args->Output || !args->Depth)
    {
        SetError("Evaluate missing device, color, depth, or output");
        return 0;
    }

    NVSDK_NGX_Parameter* params = g_evalParams ? g_evalParams : g_capabilityParams;
    if (!params)
    {
        SetError("no NGX parameter map");
        return 0;
    }

    auto* ctx = static_cast<ID3D11DeviceContext*>(args->DeviceContext);
    auto* color = static_cast<ID3D11Resource*>(args->Color);
    auto* depth = static_cast<ID3D11Resource*>(args->Depth);
    auto* output = static_cast<ID3D11Resource*>(args->Output);
    auto* motion = static_cast<ID3D11Resource*>(args->MotionVectors);
    if (!motion && g_device && EnsureMvTarget(g_device, args->RenderWidth, args->RenderHeight))
    {
        const float clear[4] = { 0, 0, 0, 0 };
        ctx->ClearRenderTargetView(g_mvRtv, clear);
        motion = g_mvTex;
    }

    params->Reset();
    params->Set(NVSDK_NGX_Parameter_Color, color);
    params->Set(NVSDK_NGX_Parameter_Output, output);
    params->Set(NVSDK_NGX_Parameter_Depth, depth);
    if (motion)
        params->Set(NVSDK_NGX_Parameter_MotionVectors, motion);
    params->Set(NVSDK_NGX_Parameter_Jitter_Offset_X, args->JitterX);
    params->Set(NVSDK_NGX_Parameter_Jitter_Offset_Y, args->JitterY);
    params->Set(NVSDK_NGX_Parameter_Sharpness, args->Sharpness);
    params->Set(NVSDK_NGX_Parameter_Reset, args->Reset);
    params->Set(NVSDK_NGX_Parameter_MV_Scale_X, args->MvScaleX == 0.0f ? 1.0f : args->MvScaleX);
    params->Set(NVSDK_NGX_Parameter_MV_Scale_Y, args->MvScaleY == 0.0f ? 1.0f : args->MvScaleY);
    params->Set(NVSDK_NGX_Parameter_DLSS_Render_Subrect_Dimensions_Width, args->RenderWidth);
    params->Set(NVSDK_NGX_Parameter_DLSS_Render_Subrect_Dimensions_Height, args->RenderHeight);

    // Color/output/depth are often still bound as RTV/DSV from Keen's last pass.
    UnbindPipeline(ctx);

    NVSDK_NGX_Result result = pEvaluate(ctx, g_dlss, params, nullptr);
    if (NVSDK_NGX_FAILED(result))
    {
        char colorDesc[64], depthDesc[64], outputDesc[64], motionDesc[64];
        DescribeTexture(color, colorDesc, sizeof(colorDesc));
        DescribeTexture(depth, depthDesc, sizeof(depthDesc));
        DescribeTexture(output, outputDesc, sizeof(outputDesc));
        DescribeTexture(motion, motionDesc, sizeof(motionDesc));
        char buf[512];
        sprintf_s(buf,
            "EvaluateFeature failed (0x%08X) render=%ux%u color=%s depth=%s mv=%s output=%s",
            (unsigned)result, args->RenderWidth, args->RenderHeight,
            colorDesc, depthDesc, motionDesc, outputDesc);
        SetError(buf);
        return 0;
    }
    SetError("ok");
    return 1;
}

extern "C" void* SeDlss_GenerateCameraMotionVectors(const SeDlssMvArgs* args)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!args || !args->Device || !args->DeviceContext || !args->Depth || args->Width == 0 || args->Height == 0)
    {
        SetError("motion-vector args are incomplete");
        return nullptr;
    }

    auto* device = static_cast<ID3D11Device*>(args->Device);
    auto* ctx = static_cast<ID3D11DeviceContext*>(args->DeviceContext);
    if (!EnsureMvShaders(device) || !EnsureMvTarget(device, args->Width, args->Height))
        return nullptr;

    struct Cb
    {
        float InvViewProj[16];
        float UnjitteredViewProj[16];
        float PrevViewProj[16];
        float RenderSize[2];
        float InvRenderSize[2];
    } cb{};
    memcpy(cb.InvViewProj, args->InvViewProj, sizeof(cb.InvViewProj));
    memcpy(cb.UnjitteredViewProj, args->UnjitteredViewProj, sizeof(cb.UnjitteredViewProj));
    memcpy(cb.PrevViewProj, args->PrevViewProj, sizeof(cb.PrevViewProj));
    cb.RenderSize[0] = (float)args->Width;
    cb.RenderSize[1] = (float)args->Height;
    cb.InvRenderSize[0] = 1.0f / (float)args->Width;
    cb.InvRenderSize[1] = 1.0f / (float)args->Height;

    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(ctx->Map(g_mvCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        return nullptr;
    memcpy(mapped.pData, &cb, sizeof(cb));
    ctx->Unmap(g_mvCb, 0);

    // Unbind Keen's depth DSV before creating an SRV on the same texture.
    ID3D11RenderTargetView* nullRtvs[1] = { nullptr };
    ctx->OMSetRenderTargets(1, nullRtvs, nullptr);

    ID3D11ShaderResourceView* depthSrv = nullptr;
    auto* depthRes = static_cast<ID3D11Resource*>(args->Depth);
    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc{};
    srvDesc.Format = DXGI_FORMAT_R32_FLOAT;
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Texture2D.MipLevels = 1;
    if (FAILED(device->CreateShaderResourceView(depthRes, &srvDesc, &depthSrv)))
    {
        srvDesc.Format = DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
        if (FAILED(device->CreateShaderResourceView(depthRes, &srvDesc, &depthSrv)))
        {
            SetError("failed to create depth SRV for motion vectors");
            return nullptr;
        }
    }

    D3D11_VIEWPORT vp{};
    vp.Width = (float)args->Width;
    vp.Height = (float)args->Height;
    vp.MaxDepth = 1.0f;
    ctx->OMSetRenderTargets(1, &g_mvRtv, nullptr);
    ctx->RSSetViewports(1, &vp);
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(g_mvVs, nullptr, 0);
    ctx->PSSetShader(g_mvPs, nullptr, 0);
    ctx->VSSetConstantBuffers(0, 1, &g_mvCb);
    ctx->PSSetConstantBuffers(0, 1, &g_mvCb);
    ctx->PSSetShaderResources(0, 1, &depthSrv);
    ctx->PSSetSamplers(0, 1, &g_mvSampler);
    ctx->Draw(3, 0);

    ID3D11ShaderResourceView* nullSrv = nullptr;
    ctx->PSSetShaderResources(0, 1, &nullSrv);
    ctx->OMSetRenderTargets(0, nullptr, nullptr);
    depthSrv->Release();
    return g_mvTex;
}

extern "C" void SeDlss_Shutdown(void)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    ReleaseDlss();
    ReleaseMvPipeline();
    ReleaseMvShaders();
    if (g_evalParams && pDestroyParams)
    {
        pDestroyParams(g_evalParams);
        g_evalParams = nullptr;
    }
    g_capabilityParams = nullptr;
    if (g_initialized && pShutdown1)
        pShutdown1(g_device);
    g_initialized = false;
    g_supported = false;
    g_device = nullptr;
    if (g_ngx)
    {
        FreeLibrary(g_ngx);
        g_ngx = nullptr;
    }
    SetError("shutdown");
}

extern "C" const char* SeDlss_LastError(void)
{
    return g_lastError.c_str();
}
